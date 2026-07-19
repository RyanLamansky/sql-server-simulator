using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SqlServerSimulator.Network;

/// <summary>
/// Generates the ephemeral self-signed certificate a listener presents during
/// the TLS handshake. Clients connect with <c>TrustServerCertificate=true</c>;
/// the certificate exists only in memory and dies with the listener.
/// </summary>
internal static class TdsServerCertificate
{
    public static X509Certificate2 Create()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlServerSimulator", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));

        // SslStream requires a certificate whose private key is loadable by
        // the platform TLS stack; round-tripping through PKCS#12 guarantees
        // that on every OS, where the CreateSelfSigned result alone does not.
        return X509CertificateLoader.LoadPkcs12(selfSigned.Export(X509ContentType.Pkcs12), password: null);
    }
}
