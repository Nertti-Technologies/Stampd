using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Stampd.Core.Revocation;
using Stampd.Crypto.LocalCertificate;
using Stampd.Engine.Pades;

using Xunit;

namespace Stampd.Engine.Tests;

/// <summary>
/// Revocation gathering and local signing under failure: a slow responder must not stop a
/// seal, a caller's cancellation must, and the local signer's platform path must produce
/// the same bytes as its BouncyCastle path.
/// </summary>
public sealed class SigningResilienceTests
{
    [Fact]
    public async Task RevocationFetcher_ResponderTimesOut_ContinuesWithoutRevocationData()
    {
        using var cert = SelfSigned();
        // An HttpClient timeout surfaces as TaskCanceledException even though the caller
        // never cancelled.
        var fetcher = new PadesRevocationFetcher(new ThrowingProvider(new TaskCanceledException("timeout")));

        var data = await fetcher.GatherAsync(cert, TestContext.Current.CancellationToken);

        Assert.Empty(data.OcspResponses);
        Assert.Empty(data.Crls);
        Assert.NotEmpty(data.Certificates);
    }

    [Fact]
    public async Task RevocationFetcher_CallerCancels_Throws()
    {
        using var cert = SelfSigned();
        using var cts = new CancellationTokenSource();
        var fetcher = new PadesRevocationFetcher(new CancellingProvider(cts));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fetcher.GatherAsync(cert, cts.Token));
    }

    [Theory]
    [InlineData("SHA256")]
    [InlineData("SHA384")]
    [InlineData("SHA512")]
    public async Task LocalCertificate_PlatformSignature_MatchesBouncyCastleSignature(string hash)
    {
        // Non-exportable keys sign through RSA.SignData; exportable ones through
        // BouncyCastle. PKCS#1 v1.5 is deterministic, so both must give identical bytes.
        using var cert = SelfSigned();
        var data = "document byte range"u8.ToArray();
        var algorithm = new HashAlgorithmName(hash);

        var viaProvider = await new LocalCertificateSealingProvider(cert)
            .SignAsync(data, algorithm, TestContext.Current.CancellationToken);
        using var rsa = cert.GetRSAPrivateKey()!;
        var viaPlatform = rsa.SignData(data, algorithm, RSASignaturePadding.Pkcs1);

        Assert.Equal(viaPlatform, viaProvider);
    }

    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Stampd Resilience Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class ThrowingProvider(Exception exception) : IRevocationProvider
    {
        public string Name => "throwing";

        public Task<CertificateRevocationInfo> GetRevocationInfoAsync(
            X509Certificate2 certificate, X509Certificate2 issuer, CancellationToken cancellationToken = default)
            => Task.FromException<CertificateRevocationInfo>(exception);
    }

    private sealed class CancellingProvider(CancellationTokenSource cts) : IRevocationProvider
    {
        public string Name => "cancelling";

        public async Task<CertificateRevocationInfo> GetRevocationInfoAsync(
            X509Certificate2 certificate, X509Certificate2 issuer, CancellationToken cancellationToken = default)
        {
            await cts.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return CertificateRevocationInfo.Empty;
        }
    }
}
