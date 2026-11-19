using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Stampd.Infrastructure;

namespace Stampd.WebApi.Endpoints;

/// <summary>Webhooks-health endpoint: endpoint status, retry-queue depth and recent delivery failures.</summary>
internal static partial class AdminAnalyticsEndpoints
{
    // ---------------------------------------------------------------------
    // v2.3 #230 — webhook delivery retry observability
    // ---------------------------------------------------------------------

    /// <summary>Top-N for the recent-failures table. Keeps the response bounded.</summary>
    private const int WebhooksRecentFailuresTake = 10;

    /// <summary>
    /// Webhook health rollup. Surfaces total + active + degraded endpoints, the
    /// retry-queue depth, and the most recent failed deliveries — everything an
    /// admin needs to spot a broken subscriber without opening server logs. All
    /// counts are tenant-scoped (the WebhookEndpoint / WebhookDelivery query filters
    /// in StampdDbContext do the tenant filtering for us).
    /// </summary>
    private static async Task<IResult> GetWebhooksHealthAsync(
        [FromServices] StampdDbContext db,
        CancellationToken ct)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var oneDayAgoMs = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();

        // Endpoint-level snapshot. ConsecutiveFailures > 0 is the "this endpoint is
        // currently flapping" signal — we report it as "degraded" rather than
        // "failed" because the worker may still recover on the next attempt.
        var endpoints = await db.WebhookEndpoints
            .Select(e => new
            {
                e.Id,
                e.Url,
                e.IsActive,
                e.ConsecutiveFailures,
                e.LastDeliveryAttemptAtUtc,
                e.LastSuccessAtUtc,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var totalEndpoints = endpoints.Count;
        var activeEndpoints = endpoints.Count(e => e.IsActive);
        var degradedEndpoints = endpoints.Count(e => e.IsActive && e.ConsecutiveFailures > 0);

        // Retry-queue depth: deliveries scheduled to run later. AttemptCount > 0
        // means they've already failed at least once. Useful to distinguish a
        // healthy queue (lots of new deliveries) from a backed-up retry storm.
        var pendingDeliveries = await db.WebhookDeliveries
            .Where(d => d.NextAttemptAtUtcEpochMs > nowMs)
            .CountAsync(ct)
            .ConfigureAwait(false);

        var retryingDeliveries = await db.WebhookDeliveries
            .Where(d => d.AttemptCount > 0)
            .CountAsync(ct)
            .ConfigureAwait(false);

        // Last 24h activity. The outbox worker deletes on success, so a row that
        // exists with AttemptCount > 0 and a populated LastErrorMessage is what
        // "currently failed" looks like in this design.
        var failuresLast24h = await db.WebhookDeliveries
            .Where(d => d.CreatedAtUtcEpochMs >= oneDayAgoMs && d.AttemptCount > 0)
            .CountAsync(ct)
            .ConfigureAwait(false);

        // Most recent failed deliveries with endpoint context. Pull a small fixed
        // top-N so the table renders cleanly on the dashboard.
        var recentFailures = await db.WebhookDeliveries
            .Where(d => d.AttemptCount > 0 && d.LastErrorMessage != null)
            .OrderByDescending(d => d.NextAttemptAtUtcEpochMs)
            .Take(WebhooksRecentFailuresTake)
            .Select(d => new
            {
                d.Id,
                endpointUrl = d.WebhookEndpoint!.Url,
                eventType = d.EventType.ToString(),
                attemptCount = d.AttemptCount,
                lastResponseStatus = d.LastResponseStatus,
                lastErrorMessage = d.LastErrorMessage,
                nextAttemptAtUtc = d.NextAttemptAtUtc,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Results.Ok(new
        {
            totalEndpoints,
            activeEndpoints,
            degradedEndpoints,
            pendingDeliveries,
            retryingDeliveries,
            failuresLast24h,
            recentFailures,
        });
    }
}
