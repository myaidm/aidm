using System.Security.Cryptography.X509Certificates;
using AiDownloadManager.Core;
using AiDownloadManager.Infrastructure;
using AiDownloadManager.Protocols;
using AiDownloadManager.TestServer;

namespace AiDownloadManager.Engine.Tests;

/// <summary>
/// HTTPS tests against the deterministic test server with a local
/// self-signed certificate. The server speaks real TLS over the Unix
/// domain socket (the sandbox blocks .NET TCP); the engine is
/// transport-agnostic, so the same code paths are exercised. Clients
/// either pin the test certificate's thumbprint (test-only) or use
/// default validation, which correctly rejects the self-signed chain.
/// Certificate validation is NEVER disabled anywhere in this suite.
/// </summary>
public class HttpsTransportTests : IAsyncLifetime
{
    private string _workDir = string.Empty;

    public Task InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "aidm-https-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private async Task<(DeterministicTestServer Server, string Sha256, X509Certificate2 Cert)> StartServerAsync(
        long sizeBytes, TestServerOptions options, X509Certificate2 cert)
    {
        var source = Path.Combine(_workDir, $"source-{Guid.NewGuid():N}.bin");
        var sha = TestDataGenerator.Generate(source, sizeBytes, seed: 7);
        options = options with
        {
            UnixSocketPath = Path.Combine(_workDir, $"srv-{Guid.NewGuid():N}.sock"),
            ServerCertificate = cert
        };
        var server = new DeterministicTestServer(source, options);
        await server.StartAsync();
        return (server, sha, cert);
    }

    private IDownloadJournal CreateJournal()
    {
        var journal = new SqliteDownloadJournal(Path.Combine(_workDir, $"journal-{Guid.NewGuid():N}.db"));
        journal.InitializeAsync().GetAwaiter().GetResult();
        return journal;
    }

    [Fact]
    public async Task HttpsDownload_Succeeds_AndMatchesHash()
    {
        const long size = 5 * 1024 * 1024;
        using var cert = TestCertificates.CreateLocalhostCertificate();
        var (server, sha, _) = await StartServerAsync(size, new TestServerOptions(size), cert);
        await using var _ = server;
        using var client = server.CreateHttpClient(cert);
        var journal = CreateJournal();
        var destination = Path.Combine(_workDir, "out.bin");
        var job = new DownloadJob(Guid.NewGuid(), server.FileUrl, destination, size, sha, DownloadState.Queued, DateTimeOffset.UtcNow);
        Assert.StartsWith("https://", server.FileUrl);

        var outcome = await new SegmentedDownloader(new HttpTransport(client), journal).DownloadAsync(job);

        Assert.True(outcome.Success, outcome.ErrorMessage);
        Assert.Equal(sha, outcome.Sha256);
        Assert.Equal(size, new FileInfo(destination).Length);
    }

    [Fact]
    public async Task HttpsProbe_WrongCertificate_FailsWithCertificateError()
    {
        const long size = 5 * 1024 * 1024;
        using var goodCert = TestCertificates.CreateLocalhostCertificate();
        using var wrongCert = TestCertificates.CreateMismatchedCertificate();
        // The server presents the wrong certificate; the client pins the good one.
        var (server, _, _) = await StartServerAsync(size, new TestServerOptions(size), wrongCert);
        await using var _ = server;
        using var client = server.CreateHttpClient(goodCert);
        var transport = new HttpTransport(client);

        var ex = await Assert.ThrowsAsync<TransportException>(() => transport.ProbeAsync(server.FileUrl, CancellationToken.None));
        Assert.Equal(TransportFailureKind.CertificateError, ex.Kind);
        Assert.Contains("certificate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HttpsProbe_UntrustedSelfSignedChain_FailsWithCertificateError()
    {
        const long size = 5 * 1024 * 1024;
        using var cert = TestCertificates.CreateLocalhostCertificate();
        var (server, _, _) = await StartServerAsync(size, new TestServerOptions(size), cert);
        await using var _ = server;
        // Default validation, no pinning: the self-signed chain is untrusted.
        using var client = server.CreateHttpClient();
        var transport = new HttpTransport(client);

        var ex = await Assert.ThrowsAsync<TransportException>(() => transport.ProbeAsync(server.FileUrl, CancellationToken.None));
        Assert.Equal(TransportFailureKind.CertificateError, ex.Kind);
    }

    [Fact]
    public async Task HttpsDownload_CertificateError_FailsSafelyWithoutPromoting()
    {
        const long size = 5 * 1024 * 1024;
        using var goodCert = TestCertificates.CreateLocalhostCertificate();
        using var wrongCert = TestCertificates.CreateMismatchedCertificate();
        var (server, sha, _) = await StartServerAsync(size, new TestServerOptions(size), wrongCert);
        await using var _ = server;
        using var client = server.CreateHttpClient(goodCert);
        var journal = CreateJournal();
        var destination = Path.Combine(_workDir, "out.bin");
        var job = new DownloadJob(Guid.NewGuid(), server.FileUrl, destination, size, sha, DownloadState.Queued, DateTimeOffset.UtcNow);

        var outcome = await new SegmentedDownloader(new HttpTransport(client), journal).DownloadAsync(job);

        Assert.False(outcome.Success, "A certificate error must fail the download, never be retried or bypassed");
        Assert.Contains("certificate", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task HttpsDownload_Redirect_IsFollowed()
    {
        const long size = 5 * 1024 * 1024;
        using var cert = TestCertificates.CreateLocalhostCertificate();
        var (server, sha, _) = await StartServerAsync(size,
            new TestServerOptions(size, RedirectPath: "/redirect"), cert);
        await using var _ = server;
        using var client = server.CreateHttpClient(cert);
        var journal = CreateJournal();
        var destination = Path.Combine(_workDir, "out.bin");
        var redirectUrl = server.BaseUrl + "redirect";
        var job = new DownloadJob(Guid.NewGuid(), redirectUrl, destination, size, sha, DownloadState.Queued, DateTimeOffset.UtcNow);

        var outcome = await new SegmentedDownloader(new HttpTransport(client), journal).DownloadAsync(job);

        Assert.True(outcome.Success, outcome.ErrorMessage);
        Assert.Equal(sha, outcome.Sha256);
    }

    [Fact]
    public async Task HttpsDownload_DroppedConnection_RetrySucceeds_AndMatchesHash()
    {
        const long size = 10 * 1024 * 1024;
        using var cert = TestCertificates.CreateLocalhostCertificate();
        var (server, sha, _) = await StartServerAsync(size,
            new TestServerOptions(size, DropAfterBytes: size / 4, DropFirstNConnections: 2), cert);
        await using var _ = server;
        using var client = server.CreateHttpClient(cert);
        var journal = CreateJournal();
        var destination = Path.Combine(_workDir, "out.bin");
        var job = new DownloadJob(Guid.NewGuid(), server.FileUrl, destination, size, sha, DownloadState.Queued, DateTimeOffset.UtcNow);

        var outcome = await new SegmentedDownloader(new HttpTransport(client), journal).DownloadAsync(job);

        Assert.True(outcome.Success, outcome.ErrorMessage);
        Assert.Equal(sha, outcome.Sha256);
    }
}
