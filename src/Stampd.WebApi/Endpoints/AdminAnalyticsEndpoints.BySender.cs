using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Stampd.Core.Entities;
using Stampd.Infrastructure;

namespace Stampd.WebApi.Endpoints;

/// <summary>By-sender endpoint: per-sender dispatched / completed / voided counts and time-to-sign.</summary>
internal static partial class AdminAnalyticsEndpoints
{
    // ---------------------------------------------------------------------
    // v2.3 #231 — per-sender productivity
    // ---------------------------------------------------------------------

    private const int DefaultBySenderTake = 10;
    private const int MaxBySenderTake = 50;

    /// <summary>
    /// Per-sender (i.e. per <c>SigningRequest.CreatedBy</c>) dispatched / completed /
    /// voided counts plus avg time-to-sign for the sender's workflows in the window.
    /// Lets admins spot which senders are productive vs. which are sending dead-end
    /// requests. CreatedBy is a string identifier (typically the JWT <c>sub</c>); we
    /// surface it as-is and let the UI handle display.
    /// </summary>
    private static async Task<IResult> GetBySenderAsync(
        [FromQuery] int? days,
        [FromQuery] int? take,
        [FromServices] StampdDbContext db,
        CancellationToken ct)
    {
        var window = Math.Clamp(days ?? DefaultWindowDays, 1, MaxWindowDays);
        var n = Math.Clamp(take ?? DefaultBySenderTake, 1, MaxBySenderTake);
        var (currentFromMs, _, _) = WindowEpochs(window);

        // Group on the indexed CreatedBy + epoch column. EF translates this to a
        // single SELECT with a GROUP BY — no .NET-side materialization beyond the
        // grouped projection.
        var grouped = await db.SigningRequests
            .Where(r => r.CreatedAtUtcEpochMs >= currentFromMs)
            .GroupBy(r => r.CreatedBy)
            .Select(g => new
            {
                CreatedBy = g.Key,
                Dispatched = g.Count(),
                Completed = g.Count(r => r.Status == SigningRequestStatus.Completed),
                Voided = g.Count(r => r.Status == SigningRequestStatus.Voided),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (grouped.Count == 0)
        {
            return Results.Ok(new { windowDays = window, take = n, items = Array.Empty<object>() });
        }

        // Compute per-sender avg time-to-sign by pulling the signed-recipient
        // timestamps in a second query. Same pattern as time-to-sign — duration
        // arithmetic in .NET because DateTimeOffset translation isn't uniform.
        var senderIds = grouped.Select(g => g.CreatedBy).ToList();
        var signedRows = await db.Recipients
            .Where(r => r.Status == RecipientStatus.Signed
                && r.InvitedAtUtc != null
                && r.SignedAtUtc != null
                && r.SigningRequest!.CreatedAtUtcEpochMs >= currentFromMs
                && senderIds.Contains(r.SigningRequest.CreatedBy))
            .Select(r => new
            {
                Sender = r.SigningRequest!.CreatedBy,
                r.InvitedAtUtc,
                r.SignedAtUtc,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var avgBySender = signedRows
            .GroupBy(r => r.Sender)
            .ToDictionary(
                g => g.Key,
                g => Math.Round(g.Average(r => (r.SignedAtUtc!.Value - r.InvitedAtUtc!.Value).TotalMinutes), 1));

        var items = grouped
            .OrderByDescending(g => g.Dispatched)
            .Take(n)
            .Select(g => new
            {
                sender = g.CreatedBy,
                dispatched = g.Dispatched,
                completed = g.Completed,
                voided = g.Voided,
                completionRatePercent = g.Dispatched == 0
                    ? 0.0
                    : Math.Round(g.Completed * 100.0 / g.Dispatched, 1),
                avgTimeToSignMinutes = avgBySender.GetValueOrDefault(g.CreatedBy, 0.0),
            })
            .ToList();

        return Results.Ok(new { windowDays = window, take = n, items });
    }
}
