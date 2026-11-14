using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AiDownloadManager.TestServer;

/// <summary>
/// Generates self-signed test certificates in memory for the local HTTPS
/// test server. Test-only: these certificates are never trusted by the
/// system store and are never used outside tests. Clients in tests either
/// pin the exact certificate thumbprint
/// (<see cref="DeterministicTestServer.CreateHttpClient"/>) or use default
/// validation, which correctly rejects the self-signed chain.
/// </summary>
public static class TestCertificates
{
    /// <summary>Creates a self-signed certificate for CN=localhost (SAN: localhost, 127.0.0.1).</summary>
    public static X509Certificate2 CreateLocalhostCertificate()
        => CreateCertificate("localhost", new[] { "localhost" }, new[] { IPAddress.Loopback });

    /// <summary>Creates a self-signed certificate for a different host (CN=example.com). Used to test validation failure.</summary>
    public static X509Certificate2 CreateMismatchedCertificate()
        => CreateCertificate("example.com", new[] { "example.com" }, Array.Empty<IPAddress>());

    private static X509Certificate2 CreateCertificate(
        string commonName, string[] dnsNames, IPAddress[] ipAddresses)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={commonName}"),
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth
        var san = new SubjectAlternativeNameBuilder();
        foreach (var dns in dnsNames)
            san.AddDnsName(dns);
        foreach (var ip in ipAddresses)
            san.AddIpAddress(ip);
        request.CertificateExtensions.Add(san.Build());

        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
    }
}
