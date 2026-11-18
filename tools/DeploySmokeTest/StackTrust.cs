using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Mmorpg.Tools.DeploySmokeTest;

// TLS toward the stack's proxy with its own certificate authority trusted explicitly. The smoke
// test never turns verification off: the server certificate must chain to the root certificate
// that the proxy made, must be valid for the host name, and anything else is refused, including a
// certificate that the operating system would trust.
public static class StackTrust
{
    public static X509Certificate2 LoadRoot(string path)
    {
        return X509Certificate2.CreateFromPem(File.ReadAllText(path));
    }

    public static HttpClient CreateClient(Uri baseAddress, X509Certificate2 root)
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                    IsTrusted(certificate, ExtraCertificates(chain), errors, root)
            },
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    // True when the certificate chains to the root through the intermediates that the server sent,
    // and the host name matched. The operating system's trust store plays no part.
    public static bool IsTrusted(
        X509Certificate? certificate,
        IEnumerable<X509Certificate2> intermediates,
        SslPolicyErrors errors,
        X509Certificate2 root)
    {
        if (certificate is null
            || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)
            || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            return false;
        }

        using var leaf = new X509Certificate2(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        // The local authority publishes no revocation list.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        foreach (var intermediate in intermediates)
        {
            chain.ChainPolicy.ExtraStore.Add(intermediate);
        }

        return chain.Build(leaf)
            && chain.ChainElements.Count > 0
            && chain.ChainElements[^1].Certificate.Thumbprint == root.Thumbprint;
    }

    private static IEnumerable<X509Certificate2> ExtraCertificates(X509Chain? chain)
    {
        if (chain is null)
        {
            return [];
        }

        // The first element is the server certificate itself.
        return chain.ChainElements.Cast<X509ChainElement>().Skip(1).Select(element => element.Certificate).ToArray();
    }
}
