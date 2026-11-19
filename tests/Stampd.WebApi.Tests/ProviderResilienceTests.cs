using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Azure.Core;

using Microsoft.Extensions.Options;

using Stampd.Crypto.AzureKeyVault;
using Stampd.Revocation.Http;
using Stampd.WebApi.Endpoints;
using Stampd.WebApi.Models;
using Stampd.WebApi.Services;

using Xunit;

namespace Stampd.WebApi.Tests;

/// <summary>
/// Unit checks for providers and policies that must behave under failure or hostile
/// input: revocation timeouts versus cancellation, the Key Vault certificate cache,
/// webhook target addresses and retry window, and signature image validation.
/// </summary>
public sealed class ProviderResilienceTests
{
    // ---- Revocation over HTTP ----

    [Fact]
    public async Task Crl_ResponderTimesOut_ReturnsNoData()
    {
        using var cert = CertificateWithCrlUrl();
        using var http = new HttpClient(new HangingHandler()) { Timeout = TimeSpan.FromMilliseconds(100) };

        var info = await new CrlHttpRevocationProvider(http)
            .GetRevocationInfoAsync(cert, cert, TestContext.Current.CancellationToken);

        Assert.Null(info.Crl);
    }

    [Fact]
    public async Task Crl_CallerCancels_Throws()
    {
        using var cert = CertificateWithCrlUrl();
        using var http = new HttpClient(new HangingHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new CrlHttpRevocationProvider(http).GetRevocationInfoAsync(cert, cert, cts.Token));
    }

    // ---- Key Vault certificate cache ----

    [Fact]
    public async Task KeyVault_FailedCertificateLoad_IsRetriedOnNextCall()
    {
        using var cert = CertificateWithCrlUrl();
        var calls = 0;
        var provider = new AzureKeyVaultSealingProvider(
            KeyVaultOptions(),
            new NoCredential(),
            _ => ++calls == 1
                ? Task.FromException<X509Certificate2>(new HttpRequestException("transient"))
                : Task.FromResult(cert));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.GetSigningCertificateAsync(TestContext.Current.CancellationToken));
        var loaded = await provider.GetSigningCertificateAsync(TestContext.Current.CancellationToken);
        var again = await provider.GetSigningCertificateAsync(TestContext.Current.CancellationToken);

        Assert.Same(cert, loaded);
        Assert.Same(cert, again);
        Assert.Equal(2, calls); // the success is cached
    }

    // ---- Webhook targets ----

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    public void WebhookTarget_InternalAddress_IsBlocked(string address)
        => Assert.True(WebhookTargetPolicy.IsBlocked(IPAddress.Parse(address)));

    [Theory]
    [InlineData("93.184.215.14")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700::1111")]
    public void WebhookTarget_PublicAddress_IsAllowed(string address)
        => Assert.False(WebhookTargetPolicy.IsBlocked(IPAddress.Parse(address)));

    [Fact]
    public async Task WebhookTarget_AllowlistedHost_PassesEvenWhenPrivate()
    {
        var options = new WebhookTargetOptions();
        options.AllowedPrivateHosts.Add("Hooks.Internal.Test");
        var policy = new WebhookTargetPolicy(Options.Create(options));

        Assert.Null(await policy.ValidateAsync(new Uri("http://hooks.internal.test/x"), TestContext.Current.CancellationToken));
        Assert.NotNull(await policy.ValidateAsync(new Uri("http://127.0.0.1/x"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void WebhookRetry_LastsAboutTwoDays()
    {
        // Replays the worker's schedule: attempt, then wait BackoffAfter(attempts), until
        // the next attempt would fall outside the retry window.
        var elapsed = TimeSpan.Zero;
        var attempts = 0;
        while (true)
        {
            attempts++;
            var next = elapsed + WebhookDeliveryWorker.BackoffAfter(attempts);
            if (next > WebhookDeliveryWorker.RetryWindow)
            {
                break;
            }

            elapsed = next;
        }

        Assert.InRange(elapsed, TimeSpan.FromHours(47), TimeSpan.FromHours(48));
        Assert.InRange(attempts, 50, 60);
    }

    // ---- Signature images ----

    [Fact]
    public void SignatureImage_Png_IsAccepted()
        => Assert.Null(Validate(Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0])));

    [Fact]
    public void SignatureImage_Jpeg_IsAccepted()
        => Assert.Null(Validate(Convert.ToBase64String([0xFF, 0xD8, 0xFF, 0xE0, 0, 0])));

    [Theory]
    [InlineData("not base64!")]
    [InlineData("aGVsbG8gd29ybGQ=")] // "hello world": valid base64, not an image
    public void SignatureImage_Invalid_IsRefused(string imageBase64)
        => Assert.NotNull(Validate(imageBase64));

    [Fact]
    public void SignatureImage_TooLarge_IsRefused()
    {
        var bytes = new byte[RecipientSigningEndpoints.MaxSignatureImageBytes + 1];
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF;
        Assert.NotNull(Validate(Convert.ToBase64String(bytes)));
    }

    private static string? Validate(string imageBase64)
        => RecipientSigningEndpoints.ValidateFieldValues(
            new Dictionary<int, ApiFieldValue> { [0] = new(ImageBase64: imageBase64) });

    // ---- Helpers ----

    private static X509Certificate2 CertificateWithCrlUrl()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Stampd Revocation Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(["http://crl.example.test/ca.crl"]));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static AzureKeyVaultSealingOptions KeyVaultOptions() => new()
    {
        KeyIdentifier = new Uri("https://vault.example.test/keys/signing/1"),
        CertificateVaultUri = new Uri("https://vault.example.test"),
        CertificateName = "signing",
    };

    /// <summary>Never answers; completes only when the request is cancelled.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class NoCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new InvalidOperationException("No token in tests.");

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new InvalidOperationException("No token in tests.");
    }
}
