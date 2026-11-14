using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AiDownloadManager.Core;
using AiDownloadManager.Engine;
using AiDownloadManager.Infrastructure;
using AiDownloadManager.Protocols;
using AiDownloadManager.TestServer;

namespace AiDownloadManager.Engine.Tests;

/// <summary>
/// FTP transport tests against the deterministic FTP test server.
/// The server listens on IPv6 loopback (::1): this sandbox redirects
/// .NET IPv4 TCP connections to the egress proxy, but IPv6 loopback
/// works (verified with a standalone probe). No test touches the
/// public internet. Files are small (5 MB) so the suite stays fast.
/// </summary>
public class FtpTransportTests : IAsyncLifetime
{
    private string _workDir = string.Empty;

    public Task InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "aidm-ftp-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private async Task<(DeterministicFtpServer Server, string Sha256, string SourcePath)> StartServerAsync(
        long sizeBytes, FtpServerOptions options)
    {
        var source = Path.Combine(_workDir, $"ftp-source-{Guid.NewGuid():N}.bin");
        var sha = TestDataGenerator.Generate(source, sizeBytes, seed: 11);
        var server = new DeterministicFtpServer(source, options);
        await server.StartAsync();
        return (server, sha, source);
    }

    private static async Task<byte[]> DownloadToEndToMemoryAsync(FtpTransport transport, string url)
    {
        using var ms = new MemoryStream();
        await transport.DownloadToEndAsync(url, (chunk, offset) =>
        {
            ms.Position = offset;
            ms.Write(chunk.Span);
            return Task.CompletedTask;
        }, CancellationToken.None);
        return ms.ToArray();
    }

    private static async Task<byte[]> DownloadRangeToMemoryAsync(
        FtpTransport transport, string url, long start, long end)
    {
        using var ms = new MemoryStream();
        await transport.DownloadRangeAsync(url, start, end, (chunk, offset) =>
        {
            ms.Position = offset - start;
            ms.Write(chunk.Span);
            return Task.CompletedTask;
        }, CancellationToken.None);
        return ms.ToArray();
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static FtpTransport CreateTransport(FtpTransportOptions? options = null) =>
        new(options ?? new FtpTransportOptions());

    [Fact]
    public async Task DownloadToEnd_MatchesSourceHash()
    {
        const long size = 5 * 1024 * 1024;
        var (server, sha, _) = await StartServerAsync(size, new FtpServerOptions(size));
        await using var _ = server;

        var bytes = await DownloadToEndToMemoryAsync(CreateTransport(), server.FileUrl);

        Assert.Equal(size, bytes.Length);
        Assert.Equal(sha, Sha256Hex(bytes));
    }

    [Fact]
    public async Task DownloadRange_MatchesSourceSlice()
    {
        const long size = 5 * 1024 * 1024;
        var (server, _, source) = await StartServerAsync(size, new FtpServerOptions(size));
        await using var _ = server;

        const long start = 1024 * 1024;
        const long end = 3 * 1024 * 1024 - 1;
        var bytes = await DownloadRangeToMemoryAsync(CreateTransport(), server.FileUrl, start, end);

        Assert.Equal(end - start + 1, bytes.Length);
        var expected = await File.ReadAllBytesAsync(source);
        Assert.Equal(expected.AsSpan((int)start, (int)(end - start + 1)).ToArray(), bytes);
    }

    [Fact]
    public async Task Probe_ReportsSizeAndRestSupport()
    {
        const long size = 5 * 1024 * 1024;
        var (server, _, _) = await StartServerAsync(size, new FtpServerOptions(size));
        await using var _ = server;

        var probe = await CreateTransport().ProbeAsync(server.FileUrl, CancellationToken.None);

        Assert.Equal(size, probe.TotalBytes);
        Assert.True(probe.SupportsRange);
        Assert.Equal("file.bin", probe.FileName);
    }

    [Fact]
    public async Task NoRestServer_ProbeReportsNoRange_AndDownloadToEndWorks()
    {
        const long size = 5 * 1024 * 1024;
        var (server, sha, _) = await StartServerAsync(size,
            new FtpServerOptions(size, Behavior: FtpServerBehavior.NoRest));
        await using var _ = server;

        var probe = await CreateTransport().ProbeAsync(server.FileUrl, CancellationToken.None);
        Assert.False(probe.SupportsRange);
        Assert.Equal(size, probe.TotalBytes);

        var bytes = await DownloadToEndToMemoryAsync(CreateTransport(), server.FileUrl);
        Assert.Equal(sha, Sha256Hex(bytes));
    }

    [Fact]
    public async Task WrongCredentials_FailSafely()
    {
        const long size = 5 * 1024 * 1024;
        var (server, _, _) = await StartServerAsync(size, new FtpServerOptions(size));
        await using var _ = server;

        var authority = new Uri(server.BaseUrl).Authority;
        var badUrl = $"ftp://test:wrongpass@{authority}/file.bin";

        var ex = await Assert.ThrowsAsync<TransportException>(() =>
            CreateTransport().ProbeAsync(badUrl, CancellationToken.None));
        Assert.Equal(TransportFailureKind.NotFoundOrForbidden, ex.Kind);
        // The password must never leak into an error message.
        Assert.DoesNotContain("wrongpass", ex.Message);
    }

    [Fact]
    public async Task DroppedDataConnection_MapsToTransient_AndRetryCompletes()
    {
        const long size = 5 * 1024 * 1024;
        var (server, sha, _) = await StartServerAsync(size,
            new FtpServerOptions(size, Behavior: FtpServerBehavior.DropData,
                DropAfterBytes: size / 4, DropFirstNTransfers: 1));
        await using var _ = server;

        var first = await Assert.ThrowsAsync<TransportException>(() =>
            DownloadToEndToMemoryAsync(CreateTransport(), server.FileUrl));
        Assert.Equal(TransportFailureKind.Transient, first.Kind);

        // A fresh attempt (what the engine's retry does) completes.
        var bytes = await DownloadToEndToMemoryAsync(CreateTransport(), server.FileUrl);
        Assert.Equal(sha, Sha256Hex(bytes));
    }

    [Fact]
    public async Task IgnoreRestServer_RangeDownloadReturnsWrongBytes()
    {
        const long size = 5 * 1024 * 1024;
        var (server, _, source) = await StartServerAsync(size,
            new FtpServerOptions(size, Behavior: FtpServerBehavior.IgnoreRest));
        await using var _ = server;

        // FTP has no Content-Range, so a server that accepts REST and
        // then sends the whole file cannot be detected at this layer:
        // the transport reads exactly the requested byte count, but
        // the bytes are file[0..count), not file[start..end). This test
        // pins that behaviour; the engine's SHA-256 verification is
        // what rejects the assembled file (see the engine test below).
        const long start = 1024 * 1024;
        const long end = 3 * 1024 * 1024 - 1;
        var bytes = await DownloadRangeToMemoryAsync(CreateTransport(), server.FileUrl, start, end);

        Assert.Equal(end - start + 1, bytes.Length);
        var sourceBytes = await File.ReadAllBytesAsync(source);
        Assert.NotEqual(sourceBytes.AsSpan((int)start, (int)(end - start + 1)).ToArray(), bytes);
        Assert.Equal(sourceBytes.AsSpan(0, (int)(end - start + 1)).ToArray(), bytes);
    }

    [Fact]
    public async Task IgnoreRestServer_EngineRejectsMismatchedFile()
    {
        const long size = 5 * 1024 * 1024;
        var (server, sha, _) = await StartServerAsync(size,
            new FtpServerOptions(size, Behavior: FtpServerBehavior.IgnoreRest));
        await using var _ = server;

        var journal = new SqliteDownloadJournal(Path.Combine(_workDir, "ignore-rest.db"));
        await journal.InitializeAsync();
        var destination = Path.Combine(_workDir, "ignore-rest-out.bin");
        var job = new DownloadJob(Guid.NewGuid(), server.FileUrl, destination, size, sha,
            DownloadState.Queued, DateTimeOffset.UtcNow);
        var downloader = new SegmentedDownloader(new FtpTransport(), journal,
            options: new SegmentedDownloaderOptions(MaxConnections: 4));

        var outcome = await downloader.DownloadAsync(job);

        Assert.False(outcome.Success);
        Assert.Contains("SHA-256 mismatch", outcome.ErrorMessage);
        Assert.False(File.Exists(destination),
            "A file assembled from wrong bytes must never be promoted");
    }

    [Fact]
    public async Task BadGreeting_FailsSafely()
    {
        const long size = 5 * 1024 * 1024;
        var (server, _, _) = await StartServerAsync(size,
            new FtpServerOptions(size, Behavior: FtpServerBehavior.BadGreeting));
        await using var _ = server;

        // FluentFTP's cleanup of a half-connected client waits up to
        // the configured read timeout (verified: 30 s timeout -> 30 s
        // Dispose, 5 s -> 5 s). A short timeout keeps this test fast;
        // the behaviour under test (TransportException) is unaffected.
        var transport = CreateTransport(new FtpTransportOptions
        {
            Timeout = TimeSpan.FromSeconds(5)
        });
        await Assert.ThrowsAsync<TransportException>(() =>
            transport.ProbeAsync(server.FileUrl, CancellationToken.None));
    }

    [Fact]
    public async Task MissingFile_ProbeFailsWithNotFound()
    {
        const long size = 5 * 1024 * 1024;
        var (server, _, _) = await StartServerAsync(size, new FtpServerOptions(size));
        await using var _ = server;

        var ex = await Assert.ThrowsAsync<TransportException>(() =>
            CreateTransport().ProbeAsync(server.BaseUrl + "nope.bin", CancellationToken.None));
        Assert.Equal(TransportFailureKind.NotFoundOrForbidden, ex.Kind);
    }

    [Fact]
    public async Task Ftps_DownloadSucceeds_WithPinnedCertificate()
    {
        const long size = 5 * 1024 * 1024;
        var (server, sha, _) = await StartServerAsync(size,
            new FtpServerOptions(size, EnableTls: true));
        await using var _ = server;
        Assert.NotNull(server.CertificateThumbprint);

        var transport = CreateTransport(new FtpTransportOptions
        {
            ExpectedCertificateThumbprint = server.CertificateThumbprint
        });
        var bytes = await DownloadToEndToMemoryAsync(transport, server.FtpsFileUrl);

        Assert.Equal(sha, Sha256Hex(bytes));
    }

    [Fact]
    public async Task Ftps_WrongThumbprintPin_RejectsCertificate()
    {
        const long size = 5 * 1024 * 1024;
        var (server, _, _) = await StartServerAsync(size,
            new FtpServerOptions(size, EnableTls: true));
        await using var _ = server;

        var transport = CreateTransport(new FtpTransportOptions
        {
            // A well-formed but wrong thumbprint: the server's real
            // certificate must be rejected.
            ExpectedCertificateThumbprint = new string('A', 40)
        });

        await Assert.ThrowsAsync<TransportException>(() =>
            DownloadToEndToMemoryAsync(transport, server.FtpsFileUrl));
    }

    [Fact]
    public async Task MalformedUrl_FailsWithUnreachable_WithoutNetwork()
    {
        var transport = CreateTransport();

        var ex1 = await Assert.ThrowsAsync<TransportException>(() =>
            transport.ProbeAsync("not a url", CancellationToken.None));
        Assert.Equal(TransportFailureKind.Unreachable, ex1.Kind);

        var ex2 = await Assert.ThrowsAsync<TransportException>(() =>
            transport.ProbeAsync("http://example.com/file.bin", CancellationToken.None));
        Assert.Equal(TransportFailureKind.Unreachable, ex2.Kind);

        var ex3 = await Assert.ThrowsAsync<TransportException>(() =>
            transport.ProbeAsync("ftp://127.0.0.1/", CancellationToken.None));
        Assert.Equal(TransportFailureKind.Unreachable, ex3.Kind);
    }

    [Fact]
    public async Task FtpServer_AcceptsCommandsOverUnixSocket()
    {
        const long size = 1024 * 1024;
        var socketPath = Path.Combine(_workDir, "ftp.sock");
        var (server, _, _) = await StartServerAsync(size,
            new FtpServerOptions(size, UnixSocketPath: socketPath));
        await using var _ = server;

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        using var stream = new NetworkStream(socket, ownsSocket: true);
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { AutoFlush = true };

        async Task<string?> SendAsync(string command)
        {
            await writer.WriteAsync(command + "\r\n");
            await writer.FlushAsync();
            return await reader.ReadLineAsync();
        }

        Assert.StartsWith("220", await reader.ReadLineAsync());
        Assert.StartsWith("331", await SendAsync("USER test"));
        Assert.StartsWith("230", await SendAsync("PASS test"));
        Assert.Equal($"213 {size}", await SendAsync("SIZE /file.bin"));
        Assert.StartsWith("221", await SendAsync("QUIT"));
    }
}
