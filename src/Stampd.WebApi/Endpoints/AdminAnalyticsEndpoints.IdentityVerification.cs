using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Stampd.Core.Entities;
using Stampd.Infrastructure;

namespace Stampd.WebApi.Endpoints;

/// <summary>Identity-verification endpoint: challenge / verify / lockout counts, overall and per channel.</summary>
internal static partial class AdminAnalyticsEndpoints
{
    /// <summary>
    /// Identity-verification rollup: how many recipients required IV, how many
    /// succeeded, how many failed (locked out per the v1.3 #136 max-attempts), and
    /// the per-window verify rate. Joins audit events to recipients so we attribute
    /// IV stats to the recipient's role (which carried RequiresIdentityVerification).
    ///
    /// v2.2 #223 — adds prior-window comparison alongside current values.
    /// v2.2 #224 — adds per-channel breakdown (Email / SMS / KBA) so admins can spot
    /// which auth method is being brute-forced.
    /// </summary>
    private static async Task<IResult> GetIdentityVerificationAsync(
        [FromQuery] int? days,
        [FromServices] StampdDbContext db,
        CancellationToken ct)
    {
        var window = Math.Clamp(days ?? DefaultWindowDays, 1, MaxWindowDays);
        var (currentFromMs, priorFromMs, priorToMsExclusive) = WindowEpochs(window);

        var current = await ComputeIdentityVerificationAsync(db, currentFromMs, long.MaxValue, ct).ConfigureAwait(false);
        var prior = await ComputeIdentityVerificationAsync(db, priorFromMs, priorToMsExclusive, ct).ConfigureAwait(false);
        var byChannel = await ComputeIdentityVerificationByChannelAsync(db, currentFromMs, long.MaxValue, ct).ConfigureAwait(false);

        return Results.Ok(new
        {
            windowDays = window,
            requiredCount = current.RequiredCount,
            verifiedCount = current.VerifiedCount,
            verifyRatePercent = current.VerifyRatePercent,
            initiatesCount = current.InitiatesCount,
            lockedOutCount = current.LockedOutCount,
            previousWindow = new
            {
                requiredCount = prior.RequiredCount,
                verifiedCount = prior.VerifiedCount,
                verifyRatePercent = prior.VerifyRatePercent,
                initiatesCount = prior.InitiatesCount,
                lockedOutCount = prior.LockedOutCount,
            },
            deltas = new
            {
                requiredCountPercent = DeltaPercent(current.RequiredCount, prior.RequiredCount),
                verifiedCountPercent = DeltaPercent(current.VerifiedCount, prior.VerifiedCount),
                // Rate is already a percent, so the delta is "percentage points"
                // not "percent change". UI labels distinguish the two.
                verifyRatePercentPoints = Math.Round(current.VerifyRatePercent - prior.VerifyRatePercent, 1),
                initiatesCountPercent = DeltaPercent(current.InitiatesCount, prior.InitiatesCount),
                lockedOutCountPercent = DeltaPercent(current.LockedOutCount, prior.LockedOutCount),
            },
            // v2.2 #224 — per-channel breakdown. Lockout column shows which method is
            // taking the brute-force volume; verifies surface KBA usage which doesn't
            // flow through the OTP store. The UI renders a small stacked bar from this.
            byChannel = new
            {
                email = new
                {
                    initiatesCount = byChannel.Email.InitiatesCount,
                    verifiedCount = byChannel.Email.VerifiedCount,
                    lockedOutCount = byChannel.Email.LockedOutCount,
                },
                sms = new
                {
                    initiatesCount = byChannel.Sms.InitiatesCount,
                    verifiedCount = byChannel.Sms.VerifiedCount,
                    lockedOutCount = byChannel.Sms.LockedOutCount,
                },
                kba = new
                {
                    initiatesCount = byChannel.Kba.InitiatesCount,
                    verifiedCount = byChannel.Kba.VerifiedCount,
                    lockedOutCount = byChannel.Kba.LockedOutCount,
                },
            },
        });
    }

    /// <summary>
    /// Classify an OTP challenge's identifier as Email / SMS based on shape. The OTP
    /// store doesn't carry a Channel column — adding one would mean a V16 migration
    /// for a single derived field. The identifier itself is unambiguous in practice:
    /// EmailOtpProvider hands us an email address, SmsOtpProvider hands us a phone
    /// number, and the two character classes don't overlap. KBA bypasses the OTP
    /// store entirely, so any OTP row is Email or SMS by construction.
    /// </summary>
    private static string ClassifyOtpChannel(string identifier) =>
        identifier?.Contains('@', StringComparison.Ordinal) == true ? "Email" : "Sms";

    /// <summary>
    /// Per-channel IV aggregation for the current window. Pulls the per-channel
    /// slices in a single materialization each so the .NET-side grouping stays
    /// O(rows in window) and the SQL stays index-covered.
    /// </summary>
    private static async Task<IdentityVerificationByChannel> ComputeIdentityVerificationByChannelAsync(
        StampdDbContext db, long fromMs, long toMsExclusive, CancellationToken ct)
    {
        // Pull just the identifier + failed-attempts pair per OTP challenge — small
        // projection, .NET-side classification by ClassifyOtpChannel. Materialization
        // is bounded by the window so this stays cheap.
        const int lockoutThreshold = 5;
        var otpRows = await db.Set<Stampd.Infrastructure.Identity.OtpChallengeEntity>()
            .Where(c => c.CreatedAtUtcEpochMs >= fromMs && c.CreatedAtUtcEpochMs < toMsExclusive)
            .Select(c => new { c.Identifier, c.FailedAttempts })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var emailInitiates = 0;
        var emailLockedOut = 0;
        var smsInitiates = 0;
        var smsLockedOut = 0;
        foreach (var row in otpRows)
        {
            var channel = ClassifyOtpChannel(row.Identifier);
            if (channel == "Email")
            {
                emailInitiates++;
                if (row.FailedAttempts >= lockoutThreshold) emailLockedOut++;
            }
            else
            {
                smsInitiates++;
                if (row.FailedAttempts >= lockoutThreshold) smsLockedOut++;
            }
        }

        // Verified counts pivot on Recipient.IdentityVerificationMethod which is
        // stamped by the provider after a successful verify. EmailOtpProvider writes
        // "EmailOtp", SmsOtpProvider writes "Sms", KbaProvider writes "Kba" — exact
        // case-sensitive strings, so the WHERE is safe to translate.
        var verifiedByMethod = await db.Recipients
            .Where(r => r.SigningRequest!.CreatedAtUtcEpochMs >= fromMs
                && r.SigningRequest.CreatedAtUtcEpochMs < toMsExclusive
                && r.IdentityVerifiedAtUtc != null
                && r.IdentityVerificationMethod != null)
            .GroupBy(r => r.IdentityVerificationMethod!)
            .Select(g => new { Method = g.Key, Count = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        int VerifiedCount(string method) =>
            verifiedByMethod.FirstOrDefault(x =>
                string.Equals(x.Method, method, StringComparison.OrdinalIgnoreCase))?.Count ?? 0;

        // KBA doesn't flow through OTP challenges — initiate count comes from
        // recipients whose role required IV and who verified via KBA. We can't
        // distinguish "initiated but failed" from "never started" without a KBA-
        // specific attempt log, so KBA initiates == KBA verifieds for now and
        // lockedOutCount stays 0. Documented intentionally so adopters see the
        // limitation rather than a wrong number.
        var kbaVerified = VerifiedCount("Kba");

        return new IdentityVerificationByChannel
        {
            Email = new IdentityVerificationChannelCounts
            {
                InitiatesCount = emailInitiates,
                VerifiedCount = VerifiedCount("EmailOtp"),
                LockedOutCount = emailLockedOut,
            },
            Sms = new IdentityVerificationChannelCounts
            {
                InitiatesCount = smsInitiates,
                VerifiedCount = VerifiedCount("Sms"),
                LockedOutCount = smsLockedOut,
            },
            Kba = new IdentityVerificationChannelCounts
            {
                InitiatesCount = kbaVerified,
                VerifiedCount = kbaVerified,
                LockedOutCount = 0,
            },
        };
    }

    /// <summary>Value carrier for the per-channel breakdown.</summary>
    private sealed class IdentityVerificationByChannel
    {
        public IdentityVerificationChannelCounts Email { get; init; } = new();
        public IdentityVerificationChannelCounts Sms { get; init; } = new();
        public IdentityVerificationChannelCounts Kba { get; init; } = new();
    }

    private sealed class IdentityVerificationChannelCounts
    {
        public int InitiatesCount { get; init; }
        public int VerifiedCount { get; init; }
        public int LockedOutCount { get; init; }
    }

    /// <summary>
    /// One-window IV aggregation. The OTP-challenge counts use the raw
    /// <c>CreatedAtUtc</c> column (no epoch shadow on OtpChallengeEntity yet — it's a
    /// short-lived row so the column isn't a hot-path issue), wrapped in the same
    /// half-open interval as the recipient/audit queries.
    /// </summary>
    private static async Task<IdentityVerificationCounts> ComputeIdentityVerificationAsync(
        StampdDbContext db, long fromMs, long toMsExclusive, CancellationToken ct)
    {
        var requiredCount = await db.Recipients
            .Where(r => r.SigningRequest!.CreatedAtUtcEpochMs >= fromMs
                && r.SigningRequest.CreatedAtUtcEpochMs < toMsExclusive
                && r.Role != null
                && r.Role.RequiresIdentityVerification)
            .CountAsync(ct)
            .ConfigureAwait(false);

        var verifiedCount = await db.AuditEvents
            .Where(e => e.EventType == AuditEventType.RecipientIdentityVerified
                && e.SigningRequest!.CreatedAtUtcEpochMs >= fromMs
                && e.SigningRequest.CreatedAtUtcEpochMs < toMsExclusive)
            .CountAsync(ct)
            .ConfigureAwait(false);

        const int lockoutThreshold = 5;
        var lockedOutCount = await db.Set<Stampd.Infrastructure.Identity.OtpChallengeEntity>()
            .Where(c => c.CreatedAtUtcEpochMs >= fromMs && c.CreatedAtUtcEpochMs < toMsExclusive && c.FailedAttempts >= lockoutThreshold)
            .CountAsync(ct)
            .ConfigureAwait(false);

        var initiatesCount = await db.Set<Stampd.Infrastructure.Identity.OtpChallengeEntity>()
            .Where(c => c.CreatedAtUtcEpochMs >= fromMs && c.CreatedAtUtcEpochMs < toMsExclusive)
            .CountAsync(ct)
            .ConfigureAwait(false);

        var verifyRate = requiredCount == 0
            ? 0.0
            : Math.Round(verifiedCount * 100.0 / requiredCount, 1);

        return new IdentityVerificationCounts
        {
            RequiredCount = requiredCount,
            VerifiedCount = verifiedCount,
            VerifyRatePercent = verifyRate,
            InitiatesCount = initiatesCount,
            LockedOutCount = lockedOutCount,
        };
    }

    /// <summary>Value carrier for the IV aggregation.</summary>
    private sealed class IdentityVerificationCounts
    {
        public int RequiredCount { get; init; }
        public int VerifiedCount { get; init; }
        public double VerifyRatePercent { get; init; }
        public int InitiatesCount { get; init; }
        public int LockedOutCount { get; init; }
    }
}
