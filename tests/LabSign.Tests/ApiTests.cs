using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LabSign.Core;
using LabSign.Core.Signing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace LabSign.Tests;

public sealed class ApiTests : IAsyncLifetime
{
    private const string Lims = "https://labserve.net";
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private readonly ReportTree _tree = new();
    private readonly FakeTokenProvider _token = new();

    public async Task InitializeAsync()
    {
        _app = LabSignApi.Build(new LabSignConfig(), _token, new ScriptedInteraction("1234"), "9.9.9",
            builder => builder.WebHost.UseTestServer());
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        _tree.Dispose();
    }

    private static HttpRequestMessage Post(string path, object body, string? origin = Lims)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, LabSignApi.Json), Encoding.UTF8, "application/json"),
        };
        if (origin is not null) request.Headers.Add("Origin", origin);
        return request;
    }

    [Fact]
    public async Task Health_reports_identity_version_and_token()
    {
        var health = await _client.GetFromJsonAsync<JsonElement>("/health");
        Assert.Equal("labsign", health.GetProperty("service").GetString());
        Assert.Equal("9.9.9", health.GetProperty("version").GetString());
        Assert.True(health.GetProperty("token").GetProperty("present").GetBoolean());
        Assert.Contains("Test Signatory", health.GetProperty("token").GetProperty("subject").GetString());
    }

    [Fact]
    public async Task A_disallowed_origin_is_refused_before_anything_is_signed()
    {
        var pdf = _tree.AddPdf("L1.pdf");
        var response = await _client.SendAsync(Post("/sign-batch",
            new SignBatchRequest { SignerName = "x", Files = [pdf] }, origin: "https://evil.example"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(File.Exists(pdf.SignedPath));
        Assert.Empty(_token.PinsTried);
    }

    [Fact]
    public async Task A_non_loopback_host_is_refused()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Host = "rebound.evil.example";
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Preflight_allows_the_lims_origin_and_private_network_access()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/sign-batch");
        request.Headers.Add("Origin", Lims);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Private-Network", "true");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(Lims, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Private-Network").Single());
    }

    [Fact]
    public async Task Sign_batch_streams_one_json_line_per_event_then_finalize_clears_pending()
    {
        var pdf = _tree.AddPdf("L1.pdf");
        var response = await _client.SendAsync(Post("/sign-batch",
            new SignBatchRequest { SignerName = "Test Signatory", Files = [pdf] }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var events = lines.Select(l => JsonSerializer.Deserialize<BatchEvent>(l, LabSignApi.Json)!).ToList();
        Assert.Equal(["start", "pin_required", "file", "done"], events.Select(e => e.Type));
        Assert.Equal("signed", events[2].Status);
        Assert.Contains("\"full_path\"", lines[2]);

        var finalize = await _client.SendAsync(Post("/finalize", new FinalizeRequest { Files = [pdf] }));
        Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);
        Assert.False(File.Exists(pdf.PendingPath));
        Assert.True(File.Exists(pdf.SignedPath));
    }

    [Fact]
    public async Task A_bad_path_is_a_400_and_signs_nothing()
    {
        var response = await _client.SendAsync(Post("/sign-batch", new SignBatchRequest
        {
            SignerName = "x",
            Files = [new ReportFile { Filename = "a.pdf", PendingPath = "/tmp/a.pdf", SignedPath = "/tmp/b.pdf" }],
        }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_token.PinsTried);
    }
}
