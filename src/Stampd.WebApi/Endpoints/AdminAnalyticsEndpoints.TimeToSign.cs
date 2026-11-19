using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Stampd.Core.Entities;
using Stampd.Infrastructure;

namespace Stampd.WebApi.Endpoints;

/// <summary>Time-to-sign endpoint: per-template and per-role Invited → Signed timings.</summary>
internal static partial class AdminAnalyticsEndpoints
{
    /// <summary>Default top-N for time-to-sign — fits one card on the dashboard cleanly.</summary>
    private const int DefaultTimeToSignTake = 10;
    private const int MaxTimeToSignTake = 50;

    /// <summary>
    /// Per-template average + median time-to-sign (Invited → Signed gap), top N by
    /// signed recipient count. Average can be computed server-side; median requires
    /// materializing the sorted list per template and picking the midpoint client-side.
    /// We keep the materialize set small via Top N and the window.
    ///
    /// v2.2 #223 — also computes prior-window avg/median per template so the UI can
    /// surface "this template's median is up 12% vs last 30 days". Templates that
    /// appeared in the current window but not the prior get null prior values.
    /// </summary>
    private static async Task<IResult> GetTimeToSignAsync(
        [FromQuery] int? days,
        [FromQuery] int? take,
        [FromServices] StampdDbContext db,
        CancellationToken ct)
    {
        var window = Math.Clamp(days ?? DefaultWindowDays, 1, MaxWindowDays);
        var n = Math.Clamp(take ?? DefaultTimeToSignTake, 1, MaxTimeToSignTake);
        var (currentFromMs, priorFromMs, priorToMsExclusive) = WindowEpochs(window);

        var currentByTemplate = await ComputeTimeToSignAsync(db, currentFromMs, long.MaxValue, ct).ConfigureAwait(false);
        var priorByTemplate = await ComputeTimeToSignAsync(db, priorFromMs, priorToMsExclusive, ct).ConfigureAwait(false);

        // Top N from current window — the prior values get joined in by templateId.
        var topCurrent = currentByTemplate
            .OrderByDescending(x => x.SignedCount)
            .Take(n)
            .ToList();

        var templateIds = topCurrent.Select(x => x.TemplateId).ToList();
        var nameByTemplate = await db.DocumentTemplates
            .Where(t => templateIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var nameLookup = nameByTemplate.ToDictionary(x => x.Id, x => x.Name);
        var priorLookup = priorByTemplate.ToDictionary(x => x.TemplateId);

        var items = topCurrent
            .Select(x =>
            {
                priorLookup.TryGetValue(x.TemplateId, out var p);
                return new
                {
                    templateId = x.TemplateId,
                    templateName = nameLookup.GetValueOrDefault(x.TemplateId, "(unknown)"),
                    signedCount = x.SignedCount,
                    avgMinutes = x.AvgMinutes,
                    medianMinutes = x.MedianMinutes,
                    // v2.2 #223 — nullable so the UI can render "—" for templates new
                    // in the current window. Delta is null when there's no prior baseline.
                    previousWindow = p is null ? null : new
                    {
                        signedCount = p.SignedCount,
                        avgMinutes = p.AvgMinutes,
                        medianMinutes = p.MedianMinutes,
                    },
                    avgMinutesDeltaPercent = p is null ? (double?)null : DeltaPercent(x.AvgMinutes, p.AvgMinutes),
                    medianMinutesDeltaPercent = p is null ? (double?)null : DeltaPercent(x.MedianMinutes, p.MedianMinutes),
                };
            })
            .ToList();

        // v2.2 #226 — per-role aggregation across templates. Surfaces "do Approvers
        // always take longer than Signers?" at the tenant level. Recipient.Role is
        // a template-level name (e.g. "Customer", "Witness", "Manager"), not the
        // RBAC role from StampdRoles — that one's on the dispatching user, not the
        // recipient. We bucket by role NAME so semantically-equivalent roles across
        // templates get aggregated (a "Customer" role on Template A and a "Customer"
        // role on Template B share the same name; their recipient cohorts merge).
        var byRoleCurrent = await ComputeTimeToSignByRoleAsync(db, currentFromMs, long.MaxValue, ct).ConfigureAwait(false);
        var byRolePrior = await ComputeTimeToSignByRoleAsync(db, priorFromMs, priorToMsExclusive, ct).ConfigureAwait(false);
        var byRolePriorLookup = byRolePrior.ToDictionary(x => x.RoleName, StringComparer.OrdinalIgnoreCase);

        var byRole = byRoleCurrent
            .OrderByDescending(r => r.SignedCount)
            .Select(r =>
            {
                byRolePriorLookup.TryGetValue(r.RoleName, out var p);
                return new
                {
                    roleName = r.RoleName,
                    signedCount = r.SignedCount,
                    avgMinutes = r.AvgMinutes,
                    medianMinutes = r.MedianMinutes,
                    previousWindow = p is null ? null : new
                    {
                        signedCount = p.SignedCount,
                        avgMinutes = p.AvgMinutes,
                        medianMinutes = p.MedianMinutes,
                    },
                    avgMinutesDeltaPercent = p is null ? (double?)null : DeltaPercent(r.AvgMinutes, p.AvgMinutes),
                    medianMinutesDeltaPercent = p is null ? (double?)null : DeltaPercent(r.MedianMinutes, p.MedianMinutes),
                };
            })
            .ToList();

        return Results.Ok(new { windowDays = window, take = n, items, byRole });
    }

    /// <summary>
    /// Per-role time-to-sign aggregation. Roles with no name (legacy data with
    /// Recipient.RoleId = null) bucket under "(unassigned)" so the row count adds
    /// back to the recipient-level total.
    /// </summary>
    private static async Task<List<TimeToSignRoleRow>> ComputeTimeToSignByRoleAsync(
        StampdDbContext db, long fromMs, long toMsExclusive, CancellationToken ct)
    {
        var rows = await db.Recipients
            .Where(r => r.Status == RecipientStatus.Signed
                && r.InvitedAtUtc != null
                && r.SignedAtUtc != null
                && r.SigningRequest!.CreatedAtUtcEpochMs >= fromMs
                && r.SigningRequest.CreatedAtUtcEpochMs < toMsExclusive)
            .Select(r => new
            {
                RoleName = r.Role != null ? r.Role.Name : null,
                r.InvitedAtUtc,
                r.SignedAtUtc,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => string.IsNullOrWhiteSpace(r.RoleName) ? "(unassigned)" : r.RoleName!,
                StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var durations = g
                    .Select(r => (long)(r.SignedAtUtc!.Value - r.InvitedAtUtc!.Value).TotalMinutes)
                    .OrderBy(d => d)
                    .ToList();
                var avg = durations.Average();
                var median = durations.Count switch
                {
                    0 => 0.0,
                    var c when c % 2 == 1 => durations[c / 2],
                    var c => (durations[c / 2 - 1] + durations[c / 2]) / 2.0,
                };
                return new TimeToSignRoleRow
                {
                    RoleName = g.Key,
                    SignedCount = durations.Count,
                    AvgMinutes = Math.Round(avg, 1),
                    MedianMinutes = Math.Round(median, 1),
                };
            })
            .ToList();
    }

    /// <summary>Per-role time-to-sign projection.</summary>
    private sealed class TimeToSignRoleRow
    {
        public string RoleName { get; init; } = string.Empty;
        public int SignedCount { get; init; }
        public double AvgMinutes { get; init; }
        public double MedianMinutes { get; init; }
    }

    /// <summary>
    /// One-window per-template time-to-sign aggregation. Returns one row per template
    /// that had at least one signed recipient in the window.
    /// </summary>
    private static async Task<List<TimeToSignRow>> ComputeTimeToSignAsync(
        StampdDbContext db, long fromMs, long toMsExclusive, CancellationToken ct)
    {
        var rows = await db.Recipients
            .Where(r => r.Status == RecipientStatus.Signed
                && r.InvitedAtUtc != null
                && r.SignedAtUtc != null
                && r.SigningRequest!.CreatedAtUtcEpochMs >= fromMs
                && r.SigningRequest.CreatedAtUtcEpochMs < toMsExclusive)
            .Select(r => new
            {
                TemplateId = r.SigningRequest!.DocumentTemplateId,
                r.InvitedAtUtc,
                r.SignedAtUtc,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => r.TemplateId)
            .Select(g =>
            {
                var durations = g
                    .Select(r => (long)(r.SignedAtUtc!.Value - r.InvitedAtUtc!.Value).TotalMinutes)
                    .OrderBy(d => d)
                    .ToList();
                var avg = durations.Average();
                var median = durations.Count switch
                {
                    0 => 0.0,
                    var c when c % 2 == 1 => durations[c / 2],
                    var c => (durations[c / 2 - 1] + durations[c / 2]) / 2.0,
                };
                return new TimeToSignRow
                {
                    TemplateId = g.Key,
                    SignedCount = durations.Count,
                    AvgMinutes = Math.Round(avg, 1),
                    MedianMinutes = Math.Round(median, 1),
                };
            })
            .ToList();
    }

    /// <summary>Per-template time-to-sign projection used by both windows.</summary>
    private sealed class TimeToSignRow
    {
        public Guid TemplateId { get; init; }
        public int SignedCount { get; init; }
        public double AvgMinutes { get; init; }
        public double MedianMinutes { get; init; }
    }
}
