using System.Text.RegularExpressions;

using Stampd.Core.Identity;
using Stampd.Core.Notifications;
using Stampd.Identity.EmailOtp;
using Stampd.Identity.SmsOtp;

using Xunit;

namespace Stampd.Engine.Tests;

/// <summary>
/// OTP verification never compares more codes than the attempt limit allows, even under
/// parallel requests, and a locked-out challenge keeps counting towards the initiate
/// rate limit.
/// </summary>
public sealed partial class OtpAttemptLimitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IdentityVerificationSubject Signer = new("signer@example.com", "Signer", "+15550001234");

    [Fact]
    public async Task Email_AttemptsUsedUpByParallelRequests_RejectEvenTheCorrectCode()
    {
        var mail = new CodeCapturingEmailSender();
        var store = new ExhaustedAttemptsStore(new InMemoryOtpChallengeStore(), reportedAttempts: 6);
        var provider = new EmailOtpProvider(mail, store, new EmailOtpOptions { MaxFailedAttempts = 5 });

        var challenge = await provider.InitiateAsync(Signer, Ct);
        var result = await provider.VerifyAsync(challenge.VerificationId, mail.LatestCode, Ct);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Sms_AttemptsUsedUpByParallelRequests_RejectEvenTheCorrectCode()
    {
        string? sent = null;
        var store = new ExhaustedAttemptsStore(new InMemoryOtpChallengeStore(), reportedAttempts: 6);
        var provider = new SmsOtpProvider(
            new MockSmsGateway((_, message) => sent = message), store, new SmsOtpOptions { MaxFailedAttempts = 5 });

        var challenge = await provider.InitiateAsync(Signer, Ct);
        var result = await provider.VerifyAsync(challenge.VerificationId, SixDigitCode().Match(sent!).Value, Ct);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Email_CorrectCodeWithinTheLimit_Verifies()
    {
        var mail = new CodeCapturingEmailSender();
        var provider = new EmailOtpProvider(mail, new InMemoryOtpChallengeStore(), new EmailOtpOptions { MaxFailedAttempts = 2 });

        var challenge = await provider.InitiateAsync(Signer, Ct);
        Assert.False((await provider.VerifyAsync(challenge.VerificationId, "000000x", Ct)).Succeeded);
        Assert.True((await provider.VerifyAsync(challenge.VerificationId, mail.LatestCode, Ct)).Succeeded);
    }

    [Fact]
    public async Task Email_LockedOutChallenge_StillCountsTowardsTheInitiateLimit()
    {
        var mail = new CodeCapturingEmailSender();
        var provider = new EmailOtpProvider(
            mail,
            new InMemoryOtpChallengeStore(),
            new EmailOtpOptions { MaxFailedAttempts = 1, InitiatesPerWindowMax = 2 });

        for (var i = 0; i < 2; i++)
        {
            var challenge = await provider.InitiateAsync(Signer, Ct);
            Assert.False((await provider.VerifyAsync(challenge.VerificationId, "wrong", Ct)).Succeeded);
        }

        await Assert.ThrowsAsync<OtpRateLimitExceededException>(() => provider.InitiateAsync(Signer, Ct));
    }

    [Fact]
    public async Task Sms_LockedOutChallenge_StillCountsTowardsTheInitiateLimit()
    {
        var provider = new SmsOtpProvider(
            new MockSmsGateway((_, _) => { }),
            new InMemoryOtpChallengeStore(),
            new SmsOtpOptions { MaxFailedAttempts = 1, InitiatesPerWindowMax = 2 });

        for (var i = 0; i < 2; i++)
        {
            var challenge = await provider.InitiateAsync(Signer, Ct);
            Assert.False((await provider.VerifyAsync(challenge.VerificationId, "wrong", Ct)).Succeeded);
        }

        await Assert.ThrowsAsync<OtpRateLimitExceededException>(() => provider.InitiateAsync(Signer, Ct));
    }

    [Fact]
    public async Task InMemoryStore_ParallelIncrements_EachGetADistinctCount()
    {
        var store = new InMemoryOtpChallengeStore();
        await store.StoreAsync(new OtpChallenge("id", "signer@example.com", "123456", DateTimeOffset.UtcNow.AddMinutes(10)), Ct);

        var counts = await Task.WhenAll(Enumerable.Range(0, 500)
            .Select(_ => Task.Run(() => store.IncrementFailedAttemptsAsync("id", Ct), Ct)));

        Assert.Equal(Enumerable.Range(1, 500), counts.Order());
    }

    [GeneratedRegex(@"\d{6}")]
    private static partial Regex SixDigitCode();

    /// <summary>
    /// Simulates parallel verify requests that have already used up every attempt
    /// between this request's read of the challenge and its own increment.
    /// </summary>
    private sealed class ExhaustedAttemptsStore(IOtpChallengeStore inner, int reportedAttempts) : IOtpChallengeStore
    {
        public Task StoreAsync(OtpChallenge challenge, CancellationToken cancellationToken = default)
            => inner.StoreAsync(challenge, cancellationToken);

        public Task<OtpChallenge?> RetrieveAsync(string verificationId, CancellationToken cancellationToken = default)
            => inner.RetrieveAsync(verificationId, cancellationToken);

        public Task RemoveAsync(string verificationId, CancellationToken cancellationToken = default)
            => inner.RemoveAsync(verificationId, cancellationToken);

        public Task<int> IncrementFailedAttemptsAsync(string verificationId, CancellationToken cancellationToken = default)
            => Task.FromResult(reportedAttempts);

        public Task<int> CountInitiatesSinceAsync(string identifier, DateTimeOffset since, CancellationToken cancellationToken = default)
            => inner.CountInitiatesSinceAsync(identifier, since, cancellationToken);
    }

    private sealed class CodeCapturingEmailSender : IEmailSender
    {
        public string Name => "capture";

        public string LatestCode { get; private set; } = string.Empty;

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            LatestCode = SixDigitCode().Match(message.Subject).Value;
            return Task.CompletedTask;
        }
    }
}
