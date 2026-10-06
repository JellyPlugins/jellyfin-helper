using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests;

/// <summary>
///     Proves the insecure named HTTP clients complete a TLS handshake against a certificate the
///     machine does not trust (self-signed root, the private-CA case), while the strict clients
///     with the same configuration reject it. Runs against a loopback SslStream server, so no
///     external network is touched and the ephemeral port never collides.
/// </summary>
public sealed class InsecureNamedClientTlsTests : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly RSA _serverKey;
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _serverCts = new();
    private readonly Task _serverTask;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _serverErrors = new();

    public InsecureNamedClientTlsTests()
    {
        // Schannel (Windows) refuses server authentication with an ephemeral key, so the key
        // lives in a uniquely-named user key container that Dispose deletes again. OpenSSL
        // (Linux) is fine with the ephemeral key either way.
        if (OperatingSystem.IsWindows())
        {
            var csp = new System.Security.Cryptography.CspParameters
            {
                KeyContainerName = "JellyfinHelperTlsTest_" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture),
            };
            _serverKey = new RSACryptoServiceProvider(2048, csp);
        }
        else
        {
            _serverKey = RSA.Create(2048);
        }

        var request = new CertificateRequest("CN=localhost", _serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        _certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        _listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        _listener.Start();
        _serverTask = RunServerAsync(_serverCts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _serverCts.Cancel();
        _listener.Stop();
        try
        {
            await _serverTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        _serverCts.Dispose();
        _certificate.Dispose();
        if (_serverKey is RSACryptoServiceProvider cspRsa)
        {
            // Managed cleanup of the uniquely-named test key container: clearing PersistKeyInCsp
            // before Dispose removes the key material so repeated runs do not accumulate containers.
            // Avoids a P/Invoke (which would force AllowUnsafeBlocks on the test project).
            cspRsa.PersistKeyInCsp = false;
        }

        _serverKey.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("ArrIntegration", "ArrIntegrationInsecure")]
    [InlineData("SeerrIntegration", "SeerrIntegrationInsecure")]
    [InlineData("SeerrDiscovery", "SeerrDiscoveryInsecure")]
    public async Task NamedClients_StrictRejectsUntrustedCert_InsecureAccepts(string strictName, string insecureName)
    {
        var factory = BuildFactory();
        var url = $"https://localhost:{Port}/";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var strictEx = await Record.ExceptionAsync(
            () => factory.CreateClient(strictName).GetAsync(url, timeout.Token));
        var strictFailure = Assert.IsType<HttpRequestException>(strictEx);
        // .NET nests the handshake failure (HttpRequestException -> IOException -> AuthenticationException).
        var hasAuthFailure = false;
        for (var current = strictFailure.InnerException; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
            {
                hasAuthFailure = true;
                break;
            }
        }

        Assert.True(hasAuthFailure, "Expected an AuthenticationException (untrusted chain) somewhere in the exception chain. Server errors: " + string.Join(" || ", _serverErrors));

        using var response = await factory.CreateClient(insecureName).GetAsync(url, timeout.Token);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync(timeout.Token));
    }

    private int Port => ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;

    private static System.Net.Http.IHttpClientFactory BuildFactory()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, new Mock<IServerApplicationHost>().Object);
        return services.BuildServiceProvider().GetRequiredService<System.Net.Http.IHttpClientFactory>();
    }

    private async Task RunServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _ = HandleConnectionAsync(client, cancellationToken);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false))
        {
            try
            {
                await stream.AuthenticateAsServerAsync(_certificate, false, SslProtocols.None, false).ConfigureAwait(false);
                var buffer = new byte[4096];
                var seen = new System.Text.StringBuilder();
                while (!seen.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return;
                    }

                    seen.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, read));
                }

                var body = "ok"u8.ToArray();
                var header = System.Text.Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException)
            {
                // Client went away (notably the strict client aborting the handshake) or we are stopping.
                _serverErrors.Enqueue(ex.GetType().FullName + ": " + ex.Message);
            }
        }
    }
}
