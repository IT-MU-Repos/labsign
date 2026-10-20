using iText.Signatures;
using LabSign.Core.Token;

namespace LabSign.Core.Signing;

public sealed record SessionStarted(string SessionId, string TokenLabel, string CertificateSubject);

public sealed record SessionStartFailure(int StatusCode, string Error);

/// <summary>
/// Byte signing for Mule: the page fetches each report PDF from the API, hands the bytes here,
/// gets the signed bytes back and uploads them. Nothing touches the file system.
///
/// A session is one PIN entry for many documents, like a share batch. Only one exists at a time
/// (it holds the <see cref="SigningGate"/> from start to end), and it logs the token out when
/// ended or after <see cref="IdleTimeout"/> without a request, so a forgotten browser tab does
/// not leave the token logged in.
/// </summary>
public sealed class SigningSessions(
    LabSignConfig config,
    ITokenProvider token,
    ISignerInteraction interaction,
    SigningGate gate,
    Func<DateTimeOffset>? clock = null) : IDisposable
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    private readonly object _lock = new();
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private Session? _current;

    private sealed class Session(string id, ITokenSession tokenSession, StampContent stamp, string reason)
    {
        public string Id { get; } = id;
        public ITokenSession Token { get; } = tokenSession;
        public StampContent Stamp { get; } = stamp;
        public string Reason { get; } = reason;
        public DateTimeOffset LastUsed { get; set; }
    }

    public bool IsActive
    {
        get
        {
            ExpireIdle();
            lock (_lock) return _current is not null;
        }
    }

    public async Task<(SessionStarted? Started, SessionStartFailure? Failure)> StartAsync(
        string signerName, int documentCount, string? reason, CancellationToken cancellationToken)
    {
        ExpireIdle();
        if (string.IsNullOrWhiteSpace(signerName))
            return (null, new SessionStartFailure(400, "signer_name is required."));
        if (!gate.TryEnter())
            return (null, new SessionStartFailure(409, "LabSign is already signing. Finish or close that first."));

        try
        {
            var status = token.GetStatus();
            if (!status.Present)
                return Fail(409, status.Error ?? "No SafeNet token found.");

            var (tokenSession, error) = await PinLogin.LoginAsync(
                token, interaction, status, Math.Max(documentCount, 1), cancellationToken);
            if (tokenSession is null)
                return Fail(409, error ?? "Could not log in to the token.");

            var signedAt = DateTime.Now;
            var stamp = new StampContent(
                interaction.RenderStamp(signerName, signedAt),
                $"Digitally signed by: {signerName}\nDate: {signedAt:yyyy-MM-dd HH:mm}");
            var session = new Session(
                Guid.NewGuid().ToString("N"),
                tokenSession,
                stamp,
                string.IsNullOrWhiteSpace(reason) ? config.DefaultReason : reason!)
            {
                LastUsed = _now(),
            };
            lock (_lock) _current = session;
            Log.Write($"Signing session by {signerName} with {tokenSession.Certificate.Subject}");
            return (new SessionStarted(session.Id, tokenSession.TokenLabel, tokenSession.Certificate.Subject), null);
        }
        catch
        {
            gate.Exit();
            throw;
        }

        (SessionStarted?, SessionStartFailure?) Fail(int code, string message)
        {
            gate.Exit();
            return (null, new SessionStartFailure(code, message));
        }
    }

    /// <summary>
    /// The signed PDF, or an error a person can act on ("SignHere! not found"). Throws
    /// <see cref="KeyNotFoundException"/> for an unknown or expired session.
    /// </summary>
    public (byte[]? Signed, string? Error) Sign(string sessionId, byte[] pdf, string filename) =>
        Sign(sessionId, pdf, filename, requireMarker: true, page: null, corner: null);

    /// <summary>
    /// As above; without <paramref name="requireMarker"/> a document with no SignHere! marker is
    /// signed on <paramref name="page"/> at <paramref name="corner"/> (see <see cref="SignaturePlacement"/>).
    /// </summary>
    public (byte[]? Signed, string? Error) Sign(
        string sessionId, byte[] pdf, string filename, bool requireMarker, string? page, string? corner)
    {
        ExpireIdle();
        Session session;
        lock (_lock)
        {
            if (_current is null || _current.Id != sessionId)
                throw new KeyNotFoundException("This signing session has ended. Start signing again.");
            session = _current;
            session.LastUsed = _now();
        }

        try
        {
            var (spot, placementError) = SignaturePlacement.Resolve(pdf, requireMarker, page, corner, filename);
            if (spot is null) return (null, placementError);
            var tsa = string.IsNullOrWhiteSpace(config.TsaUrl) ? null : new TSAClientBouncyCastle(config.TsaUrl);
            lock (session)
            {
                var signed = PdfReportSigner.SignBytes(
                    pdf, spot, session.Stamp, session.Reason, config.Location,
                    session.Token.Signature, session.Token.Chain, tsa);
                Log.Write($"{filename}: signed (session)");
                lock (_lock) session.LastUsed = _now();
                return (signed, null);
            }
        }
        catch (Exception ex) when (ex is not KeyNotFoundException)
        {
            Log.Write($"{filename}: failed (session) ({ex.Message})");
            return (null, $"{filename} could not be signed: {ex.Message}");
        }
    }

    /// <summary>Logs out and frees the gate. Unknown ids are ignored: ending twice is harmless.</summary>
    public void End(string sessionId)
    {
        Session? ended = null;
        lock (_lock)
        {
            if (_current is not null && _current.Id == sessionId)
            {
                ended = _current;
                _current = null;
            }
        }
        Close(ended);
    }

    /// <summary>Ends the session if it has been idle too long. Called on every request and by a timer.</summary>
    public void ExpireIdle()
    {
        Session? expired = null;
        lock (_lock)
        {
            if (_current is not null && _now() - _current.LastUsed > IdleTimeout)
            {
                expired = _current;
                _current = null;
            }
        }
        if (expired is not null) Log.Write("Signing session ended after being idle");
        Close(expired);
    }

    private void Close(Session? session)
    {
        if (session is null) return;
        try
        {
            session.Token.Dispose();
        }
        finally
        {
            gate.Exit();
        }
    }

    public void Dispose()
    {
        Session? current;
        lock (_lock)
        {
            current = _current;
            _current = null;
        }
        Close(current);
    }
}
