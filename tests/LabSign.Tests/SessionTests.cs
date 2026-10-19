using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LabSign.Core;
using LabSign.Core.Signing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace LabSign.Tests;

public class SigningSessionTests
{
    private static byte[] Pdf(bool withMarker = true)
    {
        var path = Path.Combine(Path.GetTempPath(), $"labsign-{Guid.NewGuid():N}.pdf");
        try
        {
            TestPdfs.Write(path, withMarker);
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static (bool Covers, bool Intact, string Subject) Verify(byte[] signed)
    {
        var path = Path.Combine(Path.GetTempPath(), $"labsign-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, signed);
            return TestPdfs.Verify(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task One_pin_signs_many_documents_and_end_logs_out()
    {
        var token = new FakeTokenProvider();
        var interaction = new ScriptedInteraction("1234");
        var gate = new SigningGate();
        using var sessions = new SigningSessions(new LabSignConfig(), token, interaction, gate);

        var (started, failure) = await sessions.StartAsync("Test Signatory", 2, null, CancellationToken.None);
        Assert.Null(failure);
        Assert.Contains("Test Signatory", started!.CertificateSubject);
        Assert.True(gate.IsBusy);

        for (var i = 0; i < 2; i++)
        {
            var (signed, error) = sessions.Sign(started.SessionId, Pdf(), $"R{i}.pdf");
            Assert.Null(error);
            Assert.True(Verify(signed!).Intact);
        }
        Assert.Single(interaction.Requests);

        sessions.End(started.SessionId);
        Assert.True(token.LastSession!.Disposed);
        Assert.False(gate.IsBusy);
        Assert.Throws<KeyNotFoundException>(() => sessions.Sign(started.SessionId, Pdf(), "late.pdf"));
    }

    [Fact]
    public async Task A_pdf_without_the_marker_is_refused_but_the_session_lives_on()
    {
        using var sessions = new SigningSessions(new LabSignConfig(), new FakeTokenProvider(), new ScriptedInteraction("1234"), new SigningGate());
        var (started, _) = await sessions.StartAsync("x", 1, null, CancellationToken.None);

        var (signed, error) = sessions.Sign(started!.SessionId, Pdf(withMarker: false), "bad.pdf");
        Assert.Null(signed);
        Assert.Contains("SignHere!", error);
        Assert.Null(sessions.Sign(started.SessionId, Pdf(), "good.pdf").Error);
    }

    [Fact]
    public async Task An_idle_session_expires_and_frees_the_token()
    {
        var now = DateTimeOffset.UtcNow;
        var token = new FakeTokenProvider();
        var gate = new SigningGate();
        using var sessions = new SigningSessions(new LabSignConfig(), token, new ScriptedInteraction("1234"), gate, () => now);
        var (started, _) = await sessions.StartAsync("x", 1, null, CancellationToken.None);

        now += SigningSessions.IdleTimeout + TimeSpan.FromSeconds(1);
        sessions.ExpireIdle();

        Assert.True(token.LastSession!.Disposed);
        Assert.False(gate.IsBusy);
        Assert.Throws<KeyNotFoundException>(() => sessions.Sign(started!.SessionId, Pdf(), "R.pdf"));
    }

    [Fact]
    public async Task A_cancelled_pin_starts_nothing_and_frees_the_gate()
    {
        var gate = new SigningGate();
        using var sessions = new SigningSessions(new LabSignConfig(), new FakeTokenProvider(), new ScriptedInteraction(), gate);
        var (started, failure) = await sessions.StartAsync("x", 1, null, CancellationToken.None);

        Assert.Null(started);
        Assert.Contains("cancelled", failure!.Error);
        Assert.False(gate.IsBusy);
    }

    [Fact]
    public async Task A_session_and_a_share_batch_never_run_together()
    {
        var gate = new SigningGate();
        var token = new FakeTokenProvider();
        using var sessions = new SigningSessions(new LabSignConfig(), token, new ScriptedInteraction("1234"), gate);
        var batch = new BatchSigner(new LabSignConfig(), token, new ScriptedInteraction("1234"), gate);

        var (started, _) = await sessions.StartAsync("x", 1, null, CancellationToken.None);
        Assert.False(batch.TryBegin());
        Assert.Equal(409, (await sessions.StartAsync("y", 1, null, CancellationToken.None)).Failure!.StatusCode);

        sessions.End(started!.SessionId);
        Assert.True(batch.TryBegin());
    }
}

public sealed class SessionApiTests : IAsyncLifetime
{
    private const string Lims = "https://labserve.net";
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _app = LabSignApi.Build(new LabSignConfig(), new FakeTokenProvider(), new ScriptedInteraction("1234"), "9.9.9",
            builder => builder.WebHost.UseTestServer());
        await _app.StartAsync();
        _client = _app.GetTestClient();
        _client.DefaultRequestHeaders.Add("Origin", Lims);
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Start_sign_end_over_http()
    {
        var start = await _client.PostAsync("/sessions", new StringContent(
            """{"signer_name": "Test Signatory", "document_count": 1}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var body = await start.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("session_id").GetString();
        Assert.Contains("Test Signatory", body.GetProperty("certificate_subject").GetString());

        var health = await _client.GetFromJsonAsync<JsonElement>("/health");
        Assert.True(health.GetProperty("session_active").GetBoolean());

        var path = Path.Combine(Path.GetTempPath(), $"labsign-{Guid.NewGuid():N}.pdf");
        TestPdfs.Write(path);
        var pdf = new ByteArrayContent(await File.ReadAllBytesAsync(path));
        File.Delete(path);
        pdf.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        var signed = await _client.PostAsync($"/sessions/{id}/sign?filename=R1.pdf", pdf);
        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        Assert.Equal("application/pdf", signed.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString((await signed.Content.ReadAsByteArrayAsync())[..4]));

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/sessions/{id}")).StatusCode);
        var gone = await _client.PostAsync($"/sessions/{id}/sign?filename=R1.pdf", new ByteArrayContent([1]));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Preflight_allows_delete()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/sessions/x");
        request.Headers.Add("Access-Control-Request-Method", "DELETE");
        var response = await _client.SendAsync(request);
        Assert.Contains("DELETE", response.Headers.GetValues("Access-Control-Allow-Methods").Single());
    }

    [Fact]
    public async Task A_disallowed_origin_cannot_open_a_session()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/sessions")
        {
            Content = new StringContent("""{"signer_name": "x", "document_count": 1}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Remove("Origin");
        request.Headers.Add("Origin", "https://evil.example");
        var client = _app.GetTestClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }
}

public class LinuxIntegrationTests
{
    [Fact]
    public void Registers_the_link_handler_and_autostart_under_the_given_homes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"labsign-home-{Guid.NewGuid():N}");
        try
        {
            var xdg = new LinuxIntegration(Path.Combine(root, "data"), Path.Combine(root, "config"));
            xdg.Register("/opt/x/LabSign", "/opt/x/icon.png", runCommands: false);

            var handler = File.ReadAllText(xdg.ApplicationEntry);
            Assert.Contains("MimeType=x-scheme-handler/labsign;", handler);
            Assert.Contains("Exec=\"/opt/x/LabSign\" %u", handler);
            Assert.Contains("Icon=/opt/x/icon.png", handler);
            Assert.True(xdg.StartsAtLogin);
            Assert.DoesNotContain("%u", File.ReadAllText(xdg.AutostartEntry));

            xdg.SetStartsAtLogin(false, "/opt/x/LabSign", null);
            Assert.False(xdg.StartsAtLogin);
            xdg.Register("/opt/x/LabSign", null, runCommands: false); // a restart must not undo it
            Assert.False(xdg.StartsAtLogin);

            xdg.SetStartsAtLogin(true, "/opt/x/LabSign", null);
            Assert.True(xdg.StartsAtLogin);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Linux_defaults_probe_for_the_safenet_module()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal("/usr/lib64/libeToken.so", LabSignConfig.DefaultPkcs11LibraryPath(p => p == "/usr/lib64/libeToken.so"));
        Assert.Equal("/usr/lib/libeToken.so", LabSignConfig.DefaultPkcs11LibraryPath(_ => false));
        Assert.EndsWith(Path.Combine("labsign", "signature.png"), new LabSignConfig().StampImagePath);
    }
}
