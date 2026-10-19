using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using iText.Commons.Bouncycastle.Cert;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Element;
using iText.Signatures;
using LabSign.Core.Signing;
using LabSign.Core.Token;

namespace LabSign.Tests;

/// <summary>A software RSA key standing in for the token's private key.</summary>
internal sealed class SoftwareSignature(RSA rsa) : IExternalSignature
{
    public string GetDigestAlgorithmName() => "SHA-256";
    public string GetSignatureAlgorithmName() => "RSA";
    public ISignatureMechanismParams? GetSignatureMechanismParameters() => null;
    public byte[] Sign(byte[] message) => rsa.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
}

internal sealed class FakeTokenSession : ITokenSession
{
    public FakeTokenSession(TestCertificate certificate)
    {
        Certificate = certificate.Certificate;
        Chain = [certificate.ITextCertificate];
        Signature = new SoftwareSignature(certificate.Key);
    }

    public string TokenLabel => "fake-token";
    public X509Certificate2 Certificate { get; }
    public IX509Certificate[] Chain { get; }
    public IExternalSignature Signature { get; }
    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
}

internal sealed class FakeTokenProvider(string correctPin = "1234", bool present = true) : ITokenProvider
{
    public TestCertificate Certificate { get; } = TestCertificate.Create();
    public List<string> PinsTried { get; } = [];
    public FakeTokenSession? LastSession { get; private set; }

    public TokenStatus GetStatus() => present
        ? new TokenStatus(true, true, "fake-token", Certificate.Certificate.Subject, Certificate.Certificate.NotAfter, null)
        : new TokenStatus(true, false, null, null, null, "No SafeNet token found. Plug in your token.");

    public ITokenSession Login(string pin)
    {
        PinsTried.Add(pin);
        if (pin != correctPin) throw new TokenException(TokenErrorKind.WrongPin, "Wrong PIN.");
        return LastSession = new FakeTokenSession(Certificate);
    }
}

/// <summary>Answers PIN prompts from a queue; an empty queue means the user pressed Cancel.</summary>
internal sealed class ScriptedInteraction(params string?[] pins) : ISignerInteraction
{
    private readonly Queue<string?> _pins = new(pins);
    public List<PinRequest> Requests { get; } = [];

    public Task<string?> RequestPinAsync(PinRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_pins.Count > 0 ? _pins.Dequeue() : null);
    }

    public byte[]? RenderStamp(string signerName, DateTime signedAt) => null;
}

internal sealed record TestCertificate(RSA Key, X509Certificate2 Certificate)
{
    public IX509Certificate ITextCertificate =>
        new iText.Bouncycastle.X509.X509CertificateBC(
            new Org.BouncyCastle.X509.X509CertificateParser().ReadCertificate(Certificate.RawData));

    public static TestCertificate Create(string subject = "CN=Test Signatory, O=Labserve Test")
    {
        var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return new TestCertificate(key, certificate);
    }
}

/// <summary>A throwaway <c>…\Reports\Leaf\{Pending,Signed}</c> tree.</summary>
internal sealed class ReportTree : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "labsign-test-" + Guid.NewGuid().ToString("N"));
    public string Pending => Path.Combine(Root, "Reports", "Leaf", "Pending");
    public string Signed => Path.Combine(Root, "Reports", "Leaf", "Signed");

    public ReportTree()
    {
        Directory.CreateDirectory(Pending);
        Directory.CreateDirectory(Signed);
    }

    public ReportFile File(string name) => new()
    {
        Filename = name,
        PendingPath = Path.Combine(Pending, name),
        SignedPath = Path.Combine(Signed, name),
    };

    public ReportFile AddPdf(string name, bool withMarker = true)
    {
        var file = File(name);
        TestPdfs.Write(file.PendingPath, withMarker);
        return file;
    }

    public ReportFile AddBytes(string name, string folder = "Pending")
    {
        var file = File(name);
        System.IO.File.WriteAllText(folder == "Pending" ? file.PendingPath : file.SignedPath, "not a pdf");
        return file;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

internal static class TestPdfs
{
    public static void Write(string path, bool withMarker = true)
    {
        using var document = new Document(new PdfDocument(new PdfWriter(path)));
        document.Add(new Paragraph("Leaf analysis report"));
        document.Add(new Paragraph("Results ..."));
        if (withMarker) document.Add(new Paragraph("SignHere!"));
    }

    /// <summary>Checks the signature like a PDF reader would: covers the file, intact, by this cert.</summary>
    public static (bool CoversWholeDocument, bool Intact, string Subject) Verify(string path)
    {
        using var document = new PdfDocument(new PdfReader(path));
        var util = new SignatureUtil(document);
        var name = Assert.Single(util.GetSignatureNames());
        var pkcs7 = util.ReadSignatureData(name);
        return (util.SignatureCoversWholeDocument(name), pkcs7.VerifySignatureIntegrityAndAuthenticity(),
            pkcs7.GetSigningCertificate().GetSubjectDN().ToString());
    }
}
