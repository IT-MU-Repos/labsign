using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LabSign.Core;
using LabSign.Core.Signing;
using LabSign.Core.Token;
using Net.Pkcs11Interop.Common;
using Net.Pkcs11Interop.HighLevelAPI;

namespace LabSign.Tests;

/// <summary>
/// The real PKCS#11 path against SoftHSM2 standing in for the SafeNet eToken: slot discovery,
/// certificate-by-CKA_ID lookup, PIN errors, and a PDF signed by the token's key.
/// Runs when <c>LABSIGN_SOFTHSM_LIB</c> points at libsofthsm2.so and the token
/// <c>labsign-test</c> (user PIN 1234) is initialised; see build/test.sh.
/// </summary>
[Collection("softhsm")]
public class SoftHsmTests
{
    private const string Pin = "1234";
    private static readonly byte[] KeyId = [0x4C, 0x53, 0x01];
    private static readonly object Setup = new();
    private static TestCertificate? _imported;

    private static string Library()
    {
        var path = Environment.GetEnvironmentVariable("LABSIGN_SOFTHSM_LIB");
        Skip.If(string.IsNullOrEmpty(path) || !File.Exists(path), "SoftHSM2 not configured (LABSIGN_SOFTHSM_LIB).");
        return path!;
    }

    /// <summary>Imports a software-generated key and its certificate onto the SoftHSM token once.</summary>
    private static TestCertificate EnsureImported(string library)
    {
        lock (Setup)
        {
            if (_imported is not null) return _imported;
            var certificate = TestCertificate.Create("CN=SoftHSM Signatory, O=Labserve Test");
            var p = certificate.Key.ExportParameters(includePrivateParameters: true);
            var factories = new Pkcs11InteropFactories();
            using var pkcs11 = factories.Pkcs11LibraryFactory.LoadPkcs11Library(factories, library, AppType.MultiThreaded);
            var slot = pkcs11.GetSlotList(SlotsType.WithTokenPresent).Single(s => s.GetTokenInfo().Label.Trim() == "labsign-test");
            using var session = slot.OpenSession(SessionType.ReadWrite);
            session.Login(CKU.CKU_USER, Pin);
            IObjectAttribute A(CKA type, object value) => value switch
            {
                byte[] bytes => factories.ObjectAttributeFactory.Create(type, bytes),
                bool flag => factories.ObjectAttributeFactory.Create(type, flag),
                CKO cko => factories.ObjectAttributeFactory.Create(type, cko),
                CKK ckk => factories.ObjectAttributeFactory.Create(type, ckk),
                CKC ckc => factories.ObjectAttributeFactory.Create(type, ckc),
                _ => throw new ArgumentException(type.ToString()),
            };
            session.CreateObject([
                A(CKA.CKA_CLASS, CKO.CKO_PRIVATE_KEY), A(CKA.CKA_KEY_TYPE, CKK.CKK_RSA), A(CKA.CKA_TOKEN, true),
                A(CKA.CKA_PRIVATE, true), A(CKA.CKA_SIGN, true), A(CKA.CKA_ID, KeyId),
                A(CKA.CKA_MODULUS, p.Modulus!), A(CKA.CKA_PUBLIC_EXPONENT, p.Exponent!), A(CKA.CKA_PRIVATE_EXPONENT, p.D!),
                A(CKA.CKA_PRIME_1, p.P!), A(CKA.CKA_PRIME_2, p.Q!), A(CKA.CKA_EXPONENT_1, p.DP!),
                A(CKA.CKA_EXPONENT_2, p.DQ!), A(CKA.CKA_COEFFICIENT, p.InverseQ!),
            ]);
            // SafeNet keeps the public half beside the key; it is the only CKA_ID visible before login.
            session.CreateObject([
                A(CKA.CKA_CLASS, CKO.CKO_PUBLIC_KEY), A(CKA.CKA_KEY_TYPE, CKK.CKK_RSA), A(CKA.CKA_TOKEN, true),
                A(CKA.CKA_VERIFY, true), A(CKA.CKA_ID, KeyId),
                A(CKA.CKA_MODULUS, p.Modulus!), A(CKA.CKA_PUBLIC_EXPONENT, p.Exponent!),
            ]);
            // A decoy first, so the CKA_ID match (not "first certificate") is what picks the right one.
            var decoy = TestCertificate.Create("CN=Decoy, O=Elsewhere");
            foreach (var (cert, id) in new[] { (decoy, new byte[] { 0x99 }), (certificate, KeyId) })
            {
                session.CreateObject([
                    A(CKA.CKA_CLASS, CKO.CKO_CERTIFICATE), A(CKA.CKA_CERTIFICATE_TYPE, CKC.CKC_X_509),
                    A(CKA.CKA_TOKEN, true), A(CKA.CKA_ID, id), A(CKA.CKA_VALUE, cert.Certificate.RawData),
                    A(CKA.CKA_SUBJECT, cert.Certificate.SubjectName.RawData),
                ]);
            }
            session.Logout();
            return _imported = certificate;
        }
    }

    [SkippableFact]
    public void Status_reads_the_matching_certificate_without_a_pin()
    {
        var library = Library();
        var expected = EnsureImported(library);
        using var provider = new Pkcs11TokenProvider(library, fallbackCertificatePath: null);

        var status = provider.GetStatus();

        Assert.True(status.Present, status.Error);
        Assert.Equal("labsign-test", status.Label);
        Assert.Contains("SoftHSM Signatory", status.Subject);
        Assert.Equal(expected.Certificate.NotAfter.ToUniversalTime(), status.NotAfter);
    }

    [SkippableFact]
    public void A_wrong_pin_is_reported_as_such()
    {
        var library = Library();
        EnsureImported(library);
        using var provider = new Pkcs11TokenProvider(library, fallbackCertificatePath: null);

        var error = Assert.Throws<TokenException>(() => provider.Login("0000"));
        Assert.Equal(TokenErrorKind.WrongPin, error.Kind);
    }

    [SkippableFact]
    public void The_token_key_signs_a_pdf_that_verifies()
    {
        var library = Library();
        EnsureImported(library);
        using var provider = new Pkcs11TokenProvider(library, fallbackCertificatePath: null);
        using var tree = new ReportTree();
        var file = tree.AddPdf("L1.pdf");

        using (var session = provider.Login(Pin))
        {
            // DigestInfo + CKM_RSA_PKCS from the token must equal a standard PKCS#1 v1.5 signature.
            var data = "hello"u8.ToArray();
            using var rsa = session.Certificate.GetRSAPublicKey()!;
            Assert.True(rsa.VerifyData(data, session.Signature.Sign(data), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

            PdfReportSigner.Sign(file.PendingPath, file.SignedPath, SignHereFinder.Find(file.PendingPath)!,
                new StampContent(null, "Digitally signed by: SoftHSM"), "Report approved", "",
                session.Signature, session.Chain, tsa: null);
        }

        var (covers, intact, subject) = TestPdfs.Verify(file.SignedPath);
        Assert.True(covers);
        Assert.True(intact);
        Assert.Contains("SoftHSM Signatory", subject);
    }
}
