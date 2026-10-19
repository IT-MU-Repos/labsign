using iText.Signatures;
using Net.Pkcs11Interop.Common;
using Net.Pkcs11Interop.HighLevelAPI;

namespace LabSign.Core.Token;

/// <summary>
/// iText calls Sign with the CMS signed attributes; the token does the RSA private-key operation.
/// Ported from LIMS_SOURCE SafeNetExternalSignature.
/// </summary>
internal sealed class Pkcs11ExternalSignature(
    ISession session,
    IObjectHandle privateKey,
    Pkcs11InteropFactories factories,
    string hashAlgorithm = "SHA-256") : IExternalSignature
{
    public string GetDigestAlgorithmName() => hashAlgorithm;

    public string GetSignatureAlgorithmName() => "RSA";

    public ISignatureMechanismParams? GetSignatureMechanismParameters() => null;

    public byte[] Sign(byte[] message)
    {
        var digestInfo = DigestInfo.Create(hashAlgorithm, DigestInfo.Hash(hashAlgorithm, message));
        var mechanism = factories.MechanismFactory.Create(CKM.CKM_RSA_PKCS);
        return session.Sign(mechanism, privateKey, digestInfo);
    }
}
