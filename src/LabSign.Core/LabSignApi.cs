using System.Text.Json;
using System.Text.Json.Serialization;
using LabSign.Core.Signing;
using LabSign.Core.Token;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabSign.Core;

/// <summary>
/// The loopback HTTP API the web apps talk to:
/// <c>GET /health</c>; the LIMS share flow <c>POST /sign-batch</c> (NDJSON stream) and
/// <c>POST /finalize</c>; and the Mule byte flow <c>POST /sessions</c>,
/// <c>POST /sessions/{id}/sign</c> (PDF in, signed PDF out), <c>DELETE /sessions/{id}</c>.
/// </summary>
public static class LabSignApi
{
    public const string ServiceIdentity = "labsign";

    public const string ShareSigningOff =
        "Signing files on a network share is switched off in this LabSign (share_signing_enabled).";

    /// <summary>A report PDF is a few MB; this is generous and still bounded.</summary>
    public const long MaxPdfBytes = 64L * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <param name="configureHost">Kestrel on loopback in the app; TestServer in tests.</param>
    public static WebApplication Build(
        LabSignConfig config,
        ITokenProvider token,
        ISignerInteraction interaction,
        string version,
        Action<WebApplicationBuilder> configureHost)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        configureHost(builder);
        var gate = new SigningGate();
        var sessions = new SigningSessions(config, token, interaction, gate);
        builder.Services.AddSingleton(new BatchSigner(config, token, interaction, gate));
        builder.Services.AddSingleton(sessions);

        var app = builder.Build();
        app.Use((context, next) => Guard(context, next, config));

        // A forgotten tab must not leave the token logged in.
        var idleTimer = new Timer(_ => sessions.ExpireIdle(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            idleTimer.Dispose();
            sessions.Dispose();
        });

        app.MapGet("/health", (BatchSigner signer) => Results.Json(new
        {
            service = ServiceIdentity,
            version,
            busy = signer.IsBusy,
            session_active = sessions.IsActive,
            token = token.GetStatus(),
        }, Json));

        app.MapPost("/sessions", async (HttpContext context) =>
        {
            var request = await ReadJson<StartSessionRequest>(context);
            if (request is null)
                return Results.Json(new { error = "Body must be JSON: {\"signer_name\": ..., \"document_count\": n}" }, Json, statusCode: 400);
            var (started, failure) = await sessions.StartAsync(
                request.SignerName, request.DocumentCount, request.Reason, context.RequestAborted);
            return started is not null
                ? Results.Json(started, Json)
                : Results.Json(new { error = failure!.Error }, Json, statusCode: failure.StatusCode);
        });

        app.MapPost("/sessions/{id}/sign", async (HttpContext context, string id) =>
        {
            var filename = context.Request.Query["filename"].ToString();
            if (string.IsNullOrWhiteSpace(filename)) filename = "document.pdf";
            var pdf = await ReadBody(context, MaxPdfBytes);
            if (pdf is null)
                return Results.Json(new { error = $"The PDF is larger than {MaxPdfBytes / (1024 * 1024)} MB." }, Json, statusCode: 413);
            if (pdf.Length == 0)
                return Results.Json(new { error = "No PDF in the request body." }, Json, statusCode: 400);
            try
            {
                var (signed, error) = sessions.Sign(
                    id, pdf, filename,
                    requireMarker: !string.Equals(context.Request.Query["placement"], "auto", StringComparison.OrdinalIgnoreCase),
                    page: context.Request.Query["page"],
                    corner: context.Request.Query["corner"]);
                return signed is not null
                    ? Results.File(signed, "application/pdf", filename)
                    : Results.Json(new { error }, Json, statusCode: 422);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.Json(new { error = ex.Message }, Json, statusCode: 404);
            }
        });

        app.MapDelete("/sessions/{id}", (string id) =>
        {
            sessions.End(id);
            return Results.NoContent();
        });

        // The share flow reads and writes \\server\...\Reports\<Type>\Pending|Signed. Off unless
        // a PC's config switches it on, so no page can reach a lab file server by default.
        app.MapPost("/sign-batch", (HttpContext context, BatchSigner signer) =>
            config.ShareSigningEnabled ? SignBatch(context, signer) : WriteJson(context, 403, new { error = ShareSigningOff }));
        app.MapPost("/finalize", async (HttpContext context) =>
        {
            if (!config.ShareSigningEnabled)
                return Results.Json(new { error = ShareSigningOff }, Json, statusCode: 403);
            var request = await ReadJson<FinalizeRequest>(context);
            return request is null
                ? Results.Json(new { error = "Body must be JSON: {\"files\": [...]}" }, Json, statusCode: 400)
                : Results.Json(new { results = BatchSigner.Finalize(request) }, Json);
        });

        return app;
    }

    private static async Task SignBatch(HttpContext context, BatchSigner signer)
    {
        var request = await ReadJson<SignBatchRequest>(context);
        var problem = request is null ? "Body must be JSON." : BatchSigner.Validate(request);
        if (problem is not null)
        {
            await WriteJson(context, 400, new { error = problem });
            return;
        }
        if (!signer.TryBegin())
        {
            await WriteJson(context, 409, new { error = "LabSign is already signing a batch. Wait for it to finish." });
            return;
        }

        context.Response.ContentType = "application/x-ndjson";
        context.Response.Headers.CacheControl = "no-store";
        await foreach (var batchEvent in signer.RunAsync(request!, context.RequestAborted))
        {
            try
            {
                await context.Response.WriteAsync(JsonSerializer.Serialize(batchEvent, Json) + "\n");
                await context.Response.Body.FlushAsync();
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // The page went away. RunAsync notices RequestAborted and stops after this file.
            }
        }
    }

    /// <summary>
    /// Host and Origin checks before anything else, then CORS for the allowed origin, including the
    /// Private Network Access preflight Chrome sends before a public page may reach loopback.
    /// </summary>
    private static async Task Guard(HttpContext context, Func<Task> next, LabSignConfig config)
    {
        if (!RequestGuards.IsHostAllowed(context.Request.Host.Value))
        {
            await WriteJson(context, 403, new { error = "LabSign only answers on localhost." });
            return;
        }

        var origin = context.Request.Headers.Origin.ToString();
        if (!RequestGuards.IsOriginAllowed(origin, config.AllowedOrigins))
        {
            Log.Write($"Rejected {context.Request.Path} from disallowed origin {origin}");
            await WriteJson(context, 403, new { error = $"Origin {origin} is not allowed to use LabSign." });
            return;
        }

        if (!string.IsNullOrEmpty(origin))
        {
            context.Response.Headers.AccessControlAllowOrigin = origin;
            context.Response.Headers.Vary = "Origin";
        }

        if (HttpMethods.IsOptions(context.Request.Method))
        {
            context.Response.Headers.AccessControlAllowMethods = "GET, POST, DELETE, OPTIONS";
            context.Response.Headers.AccessControlAllowHeaders = "Content-Type";
            context.Response.Headers.AccessControlMaxAge = "600";
            if (context.Request.Headers.ContainsKey("Access-Control-Request-Private-Network"))
                context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
            context.Response.StatusCode = 204;
            return;
        }

        await next();
    }

    private static async Task<T?> ReadJson<T>(HttpContext context) where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(context.Request.Body, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The whole request body, or null when it is larger than <paramref name="limit"/>.</summary>
    private static async Task<byte[]?> ReadBody(HttpContext context, long limit)
    {
        var sizeFeature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = limit + 1;
        if (context.Request.ContentLength > limit) return null;

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit) return null;
        }
        return buffer.ToArray();
    }

    private static Task WriteJson(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, Json));
    }
}
