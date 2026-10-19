using System.Security.Cryptography.X509Certificates;
using iText.Bouncycastle.X509;
using iText.Commons.Bouncycastle.Cert;
using iText.Signatures;
using Net.Pkcs11Interop.Common;
using Net.Pkcs11Interop.HighLevelAPI;
using Org.BouncyCastle.X509;

namespace LabSign.Core.Token;

/// <summary>
/// The SafeNet eToken through its PKCS#11 module (eTPKCS11.dll). Uses the first slot with a token
/// and the first RSA private key on it, as Signing.vb does. Unlike Signing.vb the certificate is
/// read from the token (the one whose CKA_ID matches the key), falling back to the .cer file the
/// legacy setup keeps beside it.
/// </summary>
public sealed class Pkcs11TokenProvider(string libraryPath, string? fallbackCertificatePath) : ITokenProvider, IDisposable
{
    private readonly Pkcs11InteropFactories _factories = new();
    private readonly object _gate = new();
    private IPkcs11Library? _library;

    public TokenStatus GetStatus()
    {
        lock (_gate)
        {
            try
            {
                var slot = FirstSlot();
                var label = slot.GetTokenInfo().Label.Trim();
                using var session = slot.OpenSession(SessionType.ReadOnly);
                // The private key is hidden until login; its public half carries the same CKA_ID.
                var publicKey = FindKey(session, CKO.CKO_PUBLIC_KEY);
                var keyId = publicKey is null ? null : ReadBytes(session, publicKey, CKA.CKA_ID);
                var certificate = FindCertificate(session, keyId) ?? LoadFallbackCertificate();
                return new TokenStatus(true, true, label, certificate?.Subject, certificate?.NotAfter.ToUniversalTime(),
                    certificate is null ? "No certificate found on the token." : null);
            }
            catch (TokenException ex)
            {
                return new TokenStatus(ex.Kind != TokenErrorKind.DriverMissing, false, null, null, null, ex.Message);
            }
            catch (Exception ex)
            {
                return new TokenStatus(true, false, null, null, null, $"Could not read the token: {ex.Message}");
            }
        }
    }

    public ITokenSession Login(string pin)
    {
        lock (_gate)
        {
            var slot = FirstSlot();
            var label = slot.GetTokenInfo().Label.Trim();
            var session = slot.OpenSession(SessionType.ReadOnly);
            try
            {
                try
                {
                    session.Login(CKU.CKU_USER, pin);
                }
                catch (Pkcs11Exception ex) when (ex.RV == CKR.CKR_USER_ALREADY_LOGGED_IN)
                {
                }
                catch (Pkcs11Exception ex) when (ex.RV == CKR.CKR_PIN_INCORRECT)
                {
                    throw new TokenException(TokenErrorKind.WrongPin, "Wrong PIN.", ex);
                }
                catch (Pkcs11Exception ex) when (ex.RV is CKR.CKR_PIN_LOCKED or CKR.CKR_PIN_EXPIRED)
                {
                    throw new TokenException(TokenErrorKind.PinLocked,
                        "The token PIN is locked. Unlock it with SafeNet Authentication Client.", ex);
                }

                var key = FindKey(session, CKO.CKO_PRIVATE_KEY)
                    ?? throw new TokenException(TokenErrorKind.NoKey, "No RSA private key found on the token.");
                var keyId = ReadBytes(session, key, CKA.CKA_ID);
                var certificate = FindCertificate(session, keyId) ?? LoadFallbackCertificate()
                    ?? throw new TokenException(TokenErrorKind.NoCertificate,
                        $"No certificate on the token and none at {fallbackCertificatePath}.");

                return new Pkcs11TokenSession(session, label, certificate,
                    new Pkcs11ExternalSignature(session, key, _factories));
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _library?.Dispose();
            _library = null;
        }
    }

    private ISlot FirstSlot()
    {
        if (_library is null)
        {
            if (!File.Exists(libraryPath))
                throw new TokenException(TokenErrorKind.DriverMissing,
                    $"SafeNet driver not found at {libraryPath}. Install SafeNet Authentication Client.");
            _library = _factories.Pkcs11LibraryFactory.LoadPkcs11Library(_factories, libraryPath, AppType.MultiThreaded);
        }
        var slots = _library.GetSlotList(SlotsType.WithTokenPresent);
        return slots.Count > 0
            ? slots[0]
            : throw new TokenException(TokenErrorKind.NoToken, "No SafeNet token found. Plug in your token.");
    }

    private IObjectHandle? FindKey(ISession session, CKO keyClass)
    {
        var template = new List<IObjectAttribute>
        {
            _factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, keyClass),
            _factories.ObjectAttributeFactory.Create(CKA.CKA_KEY_TYPE, CKK.CKK_RSA),
        };
        return session.FindAllObjects(template).FirstOrDefault();
    }

    /// <summary>The certificate whose CKA_ID matches the key; else the first non-CA one; else any.</summary>
    private X509Certificate2? FindCertificate(ISession session, byte[]? keyId)
    {
        var template = new List<IObjectAttribute>
        {
            _factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_CERTIFICATE),
            _factories.ObjectAttributeFactory.Create(CKA.CKA_CERTIFICATE_TYPE, CKC.CKC_X_509),
        };
        var candidates = new List<(X509Certificate2 Cert, byte[]? Id)>();
        foreach (var handle in session.FindAllObjects(template))
        {
            var value = ReadBytes(session, handle, CKA.CKA_VALUE);
            if (value is null || value.Length == 0) continue;
            try
            {
                candidates.Add((new X509Certificate2(value), ReadBytes(session, handle, CKA.CKA_ID)));
            }
            catch (Exception)
            {
                // Not a certificate we can parse; ignore it.
            }
        }
        if (keyId is { Length: > 0 })
        {
            var match = candidates.FirstOrDefault(c => c.Id is not null && c.Id.AsSpan().SequenceEqual(keyId));
            if (match.Cert is not null) return match.Cert;
        }
        return candidates.Select(c => c.Cert).FirstOrDefault(c => !IsCa(c)) ?? candidates.Select(c => c.Cert).FirstOrDefault();
    }

    private X509Certificate2? LoadFallbackCertificate() =>
        !string.IsNullOrEmpty(fallbackCertificatePath) && File.Exists(fallbackCertificatePath)
            ? new X509Certificate2(File.ReadAllBytes(fallbackCertificatePath))
            : null;

    private static bool IsCa(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(e => e.CertificateAuthority);

    private static byte[]? ReadBytes(ISession session, IObjectHandle handle, CKA attribute)
    {
        var values = session.GetAttributeValue(handle, [attribute]);
        return values.Count == 1 && !values[0].CannotBeRead ? values[0].GetValueAsByteArray() : null;
    }

    internal static IX509Certificate ToITextCertificate(X509Certificate2 certificate) =>
        new X509CertificateBC(new X509CertificateParser().ReadCertificate(certificate.RawData));

    private sealed class Pkcs11TokenSession(
        ISession session,
        string tokenLabel,
        X509Certificate2 certificate,
        IExternalSignature signature) : ITokenSession
    {
        public string TokenLabel { get; } = tokenLabel;
        public X509Certificate2 Certificate { get; } = certificate;
        public IX509Certificate[] Chain { get; } = [ToITextCertificate(certificate)];
        public IExternalSignature Signature { get; } = signature;

        public void Dispose()
        {
            try
            {
                session.Logout();
            }
            catch (Pkcs11Exception)
            {
                // Token pulled mid-batch, or already logged out.
            }
            session.Dispose();
        }
    }
}
