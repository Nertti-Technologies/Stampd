using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Stampd.Core.Entities;
using Stampd.Infrastructure;

namespace Stampd.WebApi.Endpoints;

/// <summary>Funnel endpoint: recipient stage counts, request-weighted completion and day-of-week split.</summary>
internal static partial class AdminAnalyticsEndpoints
{
    /// <summary>
    /// Counts recipients by max state reached in the window — the chosen window
    /// filters by SigningRequest.CreatedAtUtcEpochMs so each recipient contributes
    /// to exactly one stage (whatever's furthest in their lifecycle).
    /// </summary>
    private static async Task<IResult> GetFunnelAsync(
        [FromQuery] int? days,
        [FromServices] StampdDbContext db,
        CancellationToken ct)
    {
        var window = Math.Clamp(days ?? DefaultWindowDays, 1, MaxWindowDays);
        var (currentFromMs, priorFromMs, priorToMsExclusive) = WindowEpochs(window);

        // Run the same aggregation against current + prior windows. Both queries are
        // index-covered on (TenantId, CreatedAtUtcEpochMs) so the second call is
        // essentially free compared to the .NET-side materialization.
        var current = await ComputeFunnelAsync(db, currentFromMs, long.MaxValue, ct).ConfigureAwait(false);
        var prior = await ComputeFunnelAsync(db, priorFromMs, priorToMsExclusive, ct).ConfigureAwait(false);

        // v2.2 #225 — request-weighted completion. The recipient-level funnel above
        // is honest about per-person flow but loses request-level nuance: a 2-of-3
        // signed request reads as 2 signed / 3 invited there, while at the request
        // level it's "67% complete". Both views are useful — the UI shows them side
        // by side.
        var currentWeighted = await ComputeWeightedRequestFunnelAsync(db, currentFromMs, long.MaxValue, ct).ConfigureAwait(false);
        var priorWeighted = await ComputeWeightedRequestFunnelAsync(db, priorFromMs, priorToMsExclusive, ct).ConfigureAwait(false);

        // v2.3 #229 — weekday vs weekend split. Tells us whether weekend dispatches
        // convert at the same rate as weekday ones (often they don't — recipients
        // who get an email Saturday morning behave differently than a Monday-morning
        // arrival). Single materialization with day-of-week computed client-side,
        // since DateTimeOffset.DayOfWeek doesn't translate uniformly across providers.
        var byDayBucket = await ComputeFunnelByDayBucketAsync(db, currentFromMs, long.MaxValue, ct).ConfigureAwait(false);

        return Results.Ok(new
        {
            windowDays = window,
            total = current.Total,
            stages = new
            {
                invited = current.Invited,
                viewed = current.Viewed,
                signed = current.Signed,
                declined = current.Declined,
                expired = current.Expired,
            },
            dropOff = new
            {
                invitedToViewedPercent = DropOffPct(current.Invited, current.Viewed),
                viewedToSignedPercent = DropOffPct(current.Viewed, current.Signed),
            },
            conversionRatePercent = current.Invited == 0 ? 0.0 : Math.Round(current.Signed * 100.0 / current.Invited, 1),
            // v2.2 #223 — prior-window comparison. The UI uses these to render delta
            // chips. Keeping the shape symmetric with the current payload so the same
            // record-extraction code can read either side.
            previousWindow = new
            {
                total = prior.Total,
                stages = new
                {
                    invited = prior.Invited,
                    viewed = prior.Viewed,
                    signed = prior.Signed,
                    declined = prior.Declined,
                    expired = prior.Expired,
                },
                conversionRatePercent = prior.Invited == 0 ? 0.0 : Math.Round(prior.Signed * 100.0 / prior.Invited, 1),
            },
            deltas = new
            {
                totalPercent = DeltaPercent(current.Total, prior.Total),
                signedPercent = DeltaPercent(current.Signed, prior.Signed),
                conversionRatePercentPoints = Math.Round(
                    (current.Invited == 0 ? 0.0 : current.Signed * 100.0 / current.Invited) -
                    (prior.Invited == 0 ? 0.0 : prior.Signed * 100.0 / prior.Invited),
                    1),
            },
            // v2.2 #225 — request-weighted view. requestCount is the denominator;
            // weightedCompletionFraction is the request-weighted average of
            // (signed / recipients) per request. fullyCompletedRequests counts the
            // binary "all signed" subset so adopters can compare both notions side by
            // side and pick whichever their internal SLA targets.
            requestWeighted = new
            {
                requestCount = currentWeighted.RequestCount,
                fullyCompletedRequests = currentWeighted.FullyCompletedRequests,
                weightedCompletionFraction = currentWeighted.WeightedCompletionFraction,
                weightedCompletionPercent = Math.Round(currentWeighted.WeightedCompletionFraction * 100.0, 1),
                previousWindow = new
                {
                    requestCount = priorWeighted.RequestCount,
                    fullyCompletedRequests = priorWeighted.FullyCompletedRequests,
                    weightedCompletionPercent = Math.Round(priorWeighted.WeightedCompletionFraction * 100.0, 1),
                },
                deltas = new
                {
                    weightedCompletionPercentPoints = Math.Round(
                        (currentWeighted.WeightedCompletionFraction - priorWeighted.WeightedCompletionFraction) * 100.0,
                        1),
                },
            },
            // v2.3 #229 — weekday vs weekend split. invited/signed broken down by the
            // SigningRequest's CreatedAtUtc day-of-week bucket. UI renders two small
            // funnel mini-charts side by side. Conversion-rate gap between buckets
            // is the actionable metric.
            byDayBucket = new
            {
                weekday = new
                {
                    invited = byDayBucket.Weekday.Invited,
                    signed = byDayBucket.Weekday.Signed,
                    conversionRatePercent = byDayBucket.Weekday.Invited == 0
                        ? 0.0
                        : Math.Round(byDayBucket.Weekday.Signed * 100.0 / byDayBucket.Weekday.Invited, 1),
                },
                weekend = new
                {
                    invited = byDayBucket.Weekend.Invited,
                    signed = byDayBucket.Weekend.Signed,
                    conversionRatePercent = byDayBucket.Weekend.Invited == 0
                        ? 0.0
                        : Math.Round(byDayBucket.Weekend.Signed * 100.0 / byDayBucket.Weekend.Invited, 1),
                },
            },
        });

        // Drop-off percentages between adjacent stages. Each is computed against the
        // earlier stage's count so the UI can render "X% lost between Invited and
        // Viewed." When the earlier stage is 0 we surface 0 instead of NaN.
        static double DropOffPct(int from, int to) =>
            from == 0 ? 0.0 : Math.Round((from - to) * 100.0 / from, 1);
    }

    /// <summary>
    /// One-window funnel aggregation. Filters recipients to SigningRequests whose
    /// epoch is in the half-open interval [fromMs, toMsExclusive). Pass long.MaxValue
    /// for toMsExclusive to mean "no upper bound" (current window's "until now").
    /// </summary>
    private static async Task<FunnelCounts> ComputeFunnelAsync(
        StampdDbContext db, long fromMs, long toMsExclusive, CancellationToken ct)
    {
        var raw = await db.Recipients
            .Where(r => r.SigningRequest!.CreatedAtUtcEpochMs >= fromMs
                && r.SigningRequest.CreatedAtUtcEpochMs < toMsExclusive)
            .GroupBy(_ => 1)
            .Select(g => new FunnelCounts
            {
                Total = g.Count(),
                Invited = g.Count(r => r.InvitedAtUtc != null),
                Viewed = g.Count(r => r.FirstViewedAtUtc != null),
                Signed = g.Count(r => r.Status == RecipientStatus.Signed),
                Declined = g.Count(r => r.Status == RecipientStatus.Declined),
                Expired = g.Count(r => r.Status == RecipientStatus.Expired),
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return raw ?? new FunnelCounts();
    }

    /// <summary>Lightweight value carrier for the funnel-counts projection.</summary>
    private sealed class FunnelCounts
    {
        public int Total { get; init; }
        public int Invited { get; init; }
        public int Viewed { get; init; }
        public int Signed { get; init; }
        public int Declined { get; init; }
        public int Expired { get; init; }
    }

    /// <summary>
    /// Request-weighted completion view. For each SigningRequest in the window we
    /// compute <c>signed / total recipients</c>, then average across requests. A
    /// 2-of-3-signed request contributes 0.67 instead of 0 or 1, so the metric
    /// reflects partial progress on multi-recipient workflows. Requests with zero
    /// recipients are skipped (defensive — shouldn't exist in practice).
    /// </summary>
    private static async Task<WeightedRequestFunnel> ComputeWeightedRequestFunnelAsync(
        StampdDbContext db, long fromMs, long toMsExclusive, CancellationToken ct)
    {
        // Project just the counts per request — small materialization regardless of
        // recipient count. EF translates this to a single GROUP BY on the recipients
        // table joined to the index-covered SigningRequest predicate.
        var perRequest = await db.SigningRequests
            .Where(r => r.CreatedAtUtcEpochMs >= fromMs
                && r.CreatedAtUtcEpochMs < toMsExclusive)
            .Select(r => new
            {
                TotalRecipients = r.Recipients.Count,
                SignedRecipients = r.Recipients.Count(rc => rc.Status == RecipientStatus.Signed),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (perRequest.Count == 0)
        {
            return new WeightedRequestFunnel();
        }

        var sumFractions = 0.0;
        var fullyCompleted = 0;
        var contributingRequests = 0;
        foreach (var row in perRequest)
        {
            if (row.TotalRecipients == 0) continue;
            contributingRequests++;
            var fraction = (double)row.SignedRecipients / row.TotalRecipients;
            sumFractions += fraction;
            if (row.SignedRecipients == row.TotalRecipients) fullyCompleted++;
        }

        return new WeightedRequestFunnel
        {
            RequestCount = contributingRequests,
            FullyCompletedRequests = fullyCompleted,
            WeightedCompletionFraction = contributingRequests == 0
                ? 0.0
                : Math.Round(sumFractions / contributingRequests, 4),
        };
    }

    /// <summary>Value carrier for the request-weighted view.</summary>
    private sealed class WeightedRequestFunnel
    {
        public int RequestCount { get; init; }
        public int FullyCompletedRequests { get; init; }
        public double WeightedCompletionFraction { get; init; }
    }

    /// <summary>
    /// v2.3 #229 — bucket recipients by the day-of-week of their SigningRequest's
    /// CreatedAtUtc. Pull just the timestamp + status so the materialization stays
    /// tight; do the dow classification in .NET because DateTimeOffset.DayOfWeek
    /// translation isn't reliable across SQLite/SqlServer/Postgres. Weekday =
    /// Mon-Fri; weekend = Sat-Sun (UTC, not the recipient's local timezone — we
    /// don't have their TZ at dispatch time).
    /// </summary>
    private static async Task<FunnelByDayBucket> ComputeFunnelByDayBucketAsync(
        StampdDbContext db, long fromMs, long toMsExclusive, CancellationToken ct)
    {
        var rows = await db.Recipients
            .Where(r => r.SigningRequest!.CreatedAtUtcEpochMs >= fromMs
                && r.SigningRequest.CreatedAtUtcEpochMs < toMsExclusive)
            .Select(r => new
            {
                CreatedAtUtc = r.SigningRequest!.CreatedAtUtc,
                InvitedAt = r.InvitedAtUtc,
                Status = r.Status,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var weekdayInvited = 0;
        var weekdaySigned = 0;
        var weekendInvited = 0;
        var weekendSigned = 0;
        foreach (var r in rows)
        {
            var dow = r.CreatedAtUtc.DayOfWeek;
            var isWeekend = dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday;
            if (r.InvitedAt != null)
            {
                if (isWeekend) weekendInvited++; else weekdayInvited++;
            }
            if (r.Status == RecipientStatus.Signed)
            {
                if (isWeekend) weekendSigned++; else weekdaySigned++;
            }
        }

        return new FunnelByDayBucket
        {
            Weekday = new DayBucketCounts { Invited = weekdayInvited, Signed = weekdaySigned },
            Weekend = new DayBucketCounts { Invited = weekendInvited, Signed = weekendSigned },
        };
    }

    /// <summary>Value carrier for the day-bucket split.</summary>
    private sealed class FunnelByDayBucket
    {
        public DayBucketCounts Weekday { get; init; } = new();
        public DayBucketCounts Weekend { get; init; } = new();
    }

    private sealed class DayBucketCounts
    {
        public int Invited { get; init; }
        public int Signed { get; init; }
    }
}
