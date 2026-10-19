using System.Security.Cryptography.X509Certificates;
using iText.Commons.Bouncycastle.Cert;
using iText.Signatures;

namespace LabSign.Core.Token;

/// <summary>What /health reports about the token. Readable without the PIN.</summary>
public sealed record TokenStatus(
    bool DriverPresent,
    bool Present,
    string? Label,
    string? Subject,
    DateTime? NotAfter,
    string? Error);

public enum TokenErrorKind
{
    DriverMissing,
    NoToken,
    NoKey,
    NoCertificate,
    WrongPin,
    PinLocked,
    Other,
}

public sealed class TokenException(TokenErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public TokenErrorKind Kind { get; } = kind;
}

/// <summary>A logged-in token: one per batch, logged out on dispose.</summary>
public interface ITokenSession : IDisposable
{
    string TokenLabel { get; }
    X509Certificate2 Certificate { get; }
    IX509Certificate[] Chain { get; }
    IExternalSignature Signature { get; }
}

public interface ITokenProvider
{
    TokenStatus GetStatus();

    /// <summary>Logs in with the PIN. Throws <see cref="TokenException"/>.</summary>
    ITokenSession Login(string pin);
}
