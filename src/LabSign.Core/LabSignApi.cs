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
/// The loopback HTTP API the LIMS page talks to:
/// <c>GET /health</c>, <c>POST /sign-batch</c> (NDJSON stream), <c>POST /finalize</c>.
/// </summary>
public static class LabSignApi
{
    public const string ServiceIdentity = "labsign";

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
        builder.Services.AddSingleton(new BatchSigner(config, token, interaction));

        var app = builder.Build();
        app.Use((context, next) => Guard(context, next, config));

        app.MapGet("/health", (BatchSigner signer) => Results.Json(new
        {
            service = ServiceIdentity,
            version,
            busy = signer.IsBusy,
            token = token.GetStatus(),
        }, Json));

        app.MapPost("/sign-batch", SignBatch);
        app.MapPost("/finalize", async (HttpContext context) =>
        {
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
            context.Response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
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

    private static Task WriteJson(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, Json));
    }
}
