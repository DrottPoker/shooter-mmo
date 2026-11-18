using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Mmorpg.Tools.DeploySmokeTest;

namespace Mmorpg.Tools.Tests;

// The smoke test reaches the proxy over TLS with the proxy's own certificate authority trusted
// explicitly. It never turns verification off: a certificate that does not chain to that root, or
// that names another host, is refused, whatever the operating system would say.
public sealed class DeploySmokeTestTrustTests : IDisposable
{
    private readonly CertificateAuthority stackAuthority = CertificateAuthority.Create("Stack Root");
    private readonly CertificateAuthority otherAuthority = CertificateAuthority.Create("Other Root");
    private readonly List<IDisposable> disposables = [];

    public void Dispose()
    {
        foreach (var disposable in disposables)
        {
            disposable.Dispose();
        }

        stackAuthority.Dispose();
        otherAuthority.Dispose();
    }

    [Fact]
    public void ALeafThatChainsToTheStacksRootIsTrusted()
    {
        using var leaf = stackAuthority.IssueLeaf("localhost");

        Assert.True(StackTrust.IsTrusted(leaf, [stackAuthority.Intermediate], SslPolicyErrors.None, stackAuthority.Root));
    }

    [Fact]
    public void ALeafOfAnotherAuthorityIsRefusedEvenWithItsOwnIntermediate()
    {
        using var leaf = otherAuthority.IssueLeaf("localhost");

        Assert.False(StackTrust.IsTrusted(leaf, [otherAuthority.Intermediate], SslPolicyErrors.None, stackAuthority.Root));
    }

    [Fact]
    public void ALeafWithoutItsIntermediateIsRefused()
    {
        using var leaf = stackAuthority.IssueLeaf("localhost");

        Assert.False(StackTrust.IsTrusted(leaf, [], SslPolicyErrors.None, stackAuthority.Root));
    }

    [Fact]
    public void ASelfSignedCertificateIsRefused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        using var selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        Assert.False(StackTrust.IsTrusted(selfSigned, [], SslPolicyErrors.None, stackAuthority.Root));
    }

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch)]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors)]
    public void AHostNameThatDoesNotMatchIsRefusedWhateverTheChain(SslPolicyErrors errors)
    {
        using var leaf = stackAuthority.IssueLeaf("localhost");

        Assert.False(StackTrust.IsTrusted(leaf, [stackAuthority.Intermediate], errors, stackAuthority.Root));
    }

    [Fact]
    public void ANoCertificateIsRefused()
    {
        Assert.False(StackTrust.IsTrusted(null, [], SslPolicyErrors.None, stackAuthority.Root));
    }

    [Fact]
    public void TheRootIsReadFromTheFileThatTheProxyMade()
    {
        var path = Path.Combine(Path.GetTempPath(), "mmorpg-smoke-root-" + Guid.NewGuid().ToString("N") + ".crt");
        try
        {
            File.WriteAllText(path, stackAuthority.Root.ExportCertificatePem());

            using var loaded = StackTrust.LoadRoot(path);

            Assert.Equal(stackAuthority.Root.Thumbprint, loaded.Thumbprint);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // The whole path over a real TLS handshake: the client trusts the stack's root, so a server
    // with a certificate of that authority is reached, and a server with any other is not.
    [Fact]
    public async Task TheClientReachesAServerWithTheStacksCertificateAndNoOtherOne()
    {
        var reached = await Get(stackAuthority, stackAuthority.Root);
        Assert.Equal(HttpStatusCode.OK, reached.Status);
        Assert.Equal("ready", reached.Body);

        await Assert.ThrowsAsync<HttpRequestException>(() => Get(otherAuthority, stackAuthority.Root));
    }

    [Fact]
    public async Task TheClientRefusesAServerCertificateForAnotherHostName()
    {
        using var server = StartServer(stackAuthority, "another-host.example");
        using var client = StackTrust.CreateClient(new Uri($"https://localhost:{server.Port}"), stackAuthority.Root);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/health/ready", TestContext.Current.CancellationToken));
    }

    private async Task<(HttpStatusCode Status, string Body)> Get(CertificateAuthority serverAuthority, X509Certificate2 trustedRoot)
    {
        using var server = StartServer(serverAuthority, "localhost");
        using var client = StackTrust.CreateClient(new Uri($"https://localhost:{server.Port}"), trustedRoot);
        using var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    // A TLS server on loopback that answers one request with a short body.
    private TlsServer StartServer(CertificateAuthority authority, string host)
    {
        var server = new TlsServer(authority, host);
        disposables.Add(server);
        return server;
    }

    private sealed class TlsServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 leaf;
        private readonly SslStreamCertificateContext context;

        public TlsServer(CertificateAuthority authority, string host)
        {
            leaf = authority.IssueLeaf(host);
            context = SslStreamCertificateContext.Create(leaf, new X509Certificate2Collection(authority.Intermediate), offline: true);
            listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public void Dispose()
        {
            listener.Stop();
            leaf.Dispose();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    try
                    {
                        using var ssl = new SslStream(client.GetStream());
                        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = context });
                        var buffer = new byte[4096];
                        var read = 0;
                        while (read < buffer.Length && !Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n", StringComparison.Ordinal))
                        {
                            var count = await ssl.ReadAsync(buffer.AsMemory(read));
                            if (count == 0)
                            {
                                break;
                            }

                            read += count;
                        }

                        await ssl.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nready"));
                        await ssl.FlushAsync();
                    }
                    catch (Exception exception) when (exception is IOException or AuthenticationException or SocketException)
                    {
                        // A client that refuses the certificate ends the handshake; the next one may succeed.
                    }
                }
            }
            catch (Exception exception) when (exception is ObjectDisposedException or SocketException or InvalidOperationException)
            {
                // The listener stopped.
            }
        }
    }

    // A root, an intermediate it signed, and leaves that the intermediate signs, as Caddy's local
    // authority issues them: each certificate names its issuer's key, so a chain is built from the
    // issuer with that key and not from another certificate of the same name.
    private sealed class CertificateAuthority : IDisposable
    {
        private readonly ECDsa intermediateKey;

        private CertificateAuthority(X509Certificate2 root, X509Certificate2 intermediate, ECDsa intermediateKey)
        {
            Root = root;
            Intermediate = intermediate;
            this.intermediateKey = intermediateKey;
        }

        public X509Certificate2 Root { get; }

        public X509Certificate2 Intermediate { get; }

        public static CertificateAuthority Create(string name)
        {
            using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var rootRequest = new CertificateRequest($"CN={name}", rootKey, HashAlgorithmName.SHA256);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
            var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
            var notAfter = DateTimeOffset.UtcNow.AddDays(30);
            using var rootWithKey = rootRequest.CreateSelfSigned(notBefore, notAfter);
            // The root is what a client loads from a file: its public part only.
            var root = X509CertificateLoader.LoadCertificate(rootWithKey.Export(X509ContentType.Cert));

            var intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var intermediateRequest = new CertificateRequest($"CN={name} Intermediate", intermediateKey, HashAlgorithmName.SHA256);
            intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            intermediateRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(intermediateRequest.PublicKey, false));
            intermediateRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(rootWithKey, true, false));
            // A certificate may not outlive its issuer, so each level ends a day earlier.
            using var signedIntermediate = intermediateRequest.Create(rootWithKey, notBefore, notAfter.AddDays(-1), RandomNumberGenerator.GetBytes(8));
            var intermediate = X509CertificateLoader.LoadCertificate(signedIntermediate.Export(X509ContentType.Cert));
            return new CertificateAuthority(root, intermediate, intermediateKey);
        }

        // A leaf for one host name, with its private key, in a form that SslStream accepts on every
        // platform.
        public X509Certificate2 IssueLeaf(string host)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(host);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Intermediate, true, false));
            using var intermediateWithKey = Intermediate.CopyWithPrivateKey(intermediateKey);
            using var signed = request.Create(intermediateWithKey, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(20), RandomNumberGenerator.GetBytes(8));
            using var withKey = signed.CopyWithPrivateKey(key);
            return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
        }

        public void Dispose()
        {
            ForgetIntermediate();
            Root.Dispose();
            Intermediate.Dispose();
            intermediateKey.Dispose();
        }

        // On Windows a TLS server puts the intermediates of its certificate into the user's store of
        // intermediate authorities, where they would stay after the test and pile up over the runs.
        private void ForgetIntermediate()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using var store = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);
            store.Remove(Intermediate);
        }
    }
}
