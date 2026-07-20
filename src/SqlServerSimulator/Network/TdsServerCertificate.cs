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
    public static X509Certificate2 Create() =>
        X509CertificateLoader.LoadPkcs12(CreatePkcs12(), password: null);

    /// <summary>
    /// PKCS#12 bytes (no password) for a fresh certificate. Exposed
    /// separately from <see cref="Create"/> because the bytes must be
    /// captured before <see cref="X509CertificateLoader"/> touches
    /// a platform key store: Windows marks a loaded private key
    /// non-exportable, so a store-loaded certificate cannot be re-exported
    /// as PKCS#12 ("Key not valid for use in specified state") — callers
    /// persisting the certificate write these bytes instead.
    /// </summary>
    public static byte[] CreatePkcs12()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlServerSimulator", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));

        // SslStream requires a certificate whose private key is loadable by
        // the platform TLS stack; round-tripping through PKCS#12 guarantees
        // that on every OS, where the CreateSelfSigned result alone does not.
        return selfSigned.Export(X509ContentType.Pkcs12);
    }
}
