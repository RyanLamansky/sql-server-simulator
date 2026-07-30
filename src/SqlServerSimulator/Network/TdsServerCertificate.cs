using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SqlServerSimulator.Network;

/// <summary>
/// The self-signed certificate a listener presents during the TLS handshake
/// when the host supplied none of its own. It authenticates nothing: clients
/// connect with <c>TrustServerCertificate=true</c>, or pin the public part the
/// listener exports.
/// </summary>
internal static class TdsServerCertificate
{
    /// <summary>
    /// The certificate every listener without a supplied one presents,
    /// created on first use and reused for the life of the process. Creating
    /// one means generating an RSA-2048 key pair, which costs far more than
    /// standing up the listener around it — so a host that creates many
    /// listeners, such as a test suite with one per case, would otherwise pay
    /// that price over and over. Sharing also matches a real server, which
    /// presents one identity rather than a fresh one per endpoint. Never
    /// disposed: it lives as long as the process, and a listener disposing it
    /// would break every other listener.
    /// </summary>
    public static X509Certificate2 Shared => shared.Value;

    private static readonly Lazy<X509Certificate2> shared = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Generates a fresh certificate, paying for a new RSA key pair on every
    /// call. Anything that just needs the endpoint's default certificate wants
    /// <see cref="Shared"/> instead.
    /// </summary>
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
