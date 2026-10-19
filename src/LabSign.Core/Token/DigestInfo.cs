using System.Security.Cryptography;

namespace LabSign.Core.Token;

/// <summary>
/// CKM_RSA_PKCS signs whatever bytes it is given, so the PKCS#1 v1.5 DigestInfo (algorithm OID +
/// hash) has to be built here. Ported from SafeNetExternalSignature.CreateDigestInfo.
/// </summary>
public static class DigestInfo
{
    private static readonly byte[] Sha256Prefix =
        [0x30, 0x31, 0x30, 0x0D, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x01, 0x05, 0x00, 0x04, 0x20];
    private static readonly byte[] Sha384Prefix =
        [0x30, 0x41, 0x30, 0x0D, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x02, 0x05, 0x00, 0x04, 0x30];
    private static readonly byte[] Sha512Prefix =
        [0x30, 0x51, 0x30, 0x0D, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x03, 0x05, 0x00, 0x04, 0x40];

    public static string Normalise(string algorithm) => algorithm.ToUpperInvariant().Replace("-", "");

    public static byte[] Hash(string algorithm, byte[] message) => Normalise(algorithm) switch
    {
        "SHA256" => SHA256.HashData(message),
        "SHA384" => SHA384.HashData(message),
        "SHA512" => SHA512.HashData(message),
        _ => throw new ArgumentException($"Unsupported hash algorithm: {algorithm}", nameof(algorithm)),
    };

    public static byte[] Create(string algorithm, byte[] hash)
    {
        var prefix = Normalise(algorithm) switch
        {
            "SHA256" => Sha256Prefix,
            "SHA384" => Sha384Prefix,
            "SHA512" => Sha512Prefix,
            _ => throw new ArgumentException($"Unsupported hash algorithm: {algorithm}", nameof(algorithm)),
        };
        return [.. prefix, .. hash];
    }
}
