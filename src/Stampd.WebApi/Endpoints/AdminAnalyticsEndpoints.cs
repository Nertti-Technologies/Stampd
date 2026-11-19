namespace Stampd.WebApi.Endpoints;

/// <summary>
/// v2.0 Slice C — admin-only read endpoints for the analytics section of the /admin
/// dashboard. Three independent endpoints (funnel, time-to-sign, identity-verification)
/// so the UI can refetch any single widget without re-running the others.
/// </summary>
/// <remarks>
/// All three live under <c>/api/admin/analytics</c> and inherit the <c>Admin</c>-only
/// policy from the adminGroup in Program.cs. None mutate state; they're pure
/// aggregations against the v1.3 epoch shadow columns + the v2.0 audit/actor schema.
///
/// v2.2 #223 — each endpoint now ALSO runs the same aggregation over the equally-sized
/// prior window (since-2W → since-W) and returns the comparison values alongside the
/// current window. The UI uses these to render "▲ 18% vs prior 30d" style chips so
/// admins see trends, not just snapshots. Two queries per endpoint instead of one;
/// both are index-covered on CreatedAtUtcEpochMs so the cost stays sub-millisecond.
/// </remarks>
internal static partial class AdminAnalyticsEndpoints
{
    /// <summary>Default analytics window. 30 days matches Slice A's trend chart.</summary>
    private const int DefaultWindowDays = 30;

    /// <summary>Hard cap on the window. Keeps the materialize-then-aggregate paths bounded.</summary>
    private const int MaxWindowDays = 365;

    public static IEndpointRouteBuilder MapAdminAnalytics(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/admin/analytics").WithTags("AdminAnalytics");

        group.MapGet("/funnel", GetFunnelAsync)
            .WithName("GetAdminAnalyticsFunnel")
            .WithSummary("Invited → Viewed → Signed funnel across the tenant. v2.0 Slice C #207, v2.2 adds prior-window comparison.");

        group.MapGet("/time-to-sign", GetTimeToSignAsync)
            .WithName("GetAdminAnalyticsTimeToSign")
            .WithSummary("Per-template avg + median time from Invited to Signed. v2.0 Slice C #208, v2.2 adds prior-window comparison.");

        group.MapGet("/identity-verification", GetIdentityVerificationAsync)
            .WithName("GetAdminAnalyticsIdentityVerification")
            .WithSummary("Identity-verification challenge / verify / failure counts. v2.0 Slice C #209, v2.2 adds prior-window comparison.");

        group.MapGet("/webhooks-health", GetWebhooksHealthAsync)
            .WithName("GetAdminAnalyticsWebhooksHealth")
            .WithSummary("Webhook endpoint health + recent delivery failures. v2.3 #230.");

        group.MapGet("/by-sender", GetBySenderAsync)
            .WithName("GetAdminAnalyticsBySender")
            .WithSummary("Per-sender productivity: dispatched / completed / avg time-to-sign. v2.3 #231.");

        return builder;
    }

    /// <summary>
    /// Compute the (fromEpochMs, toEpochMsExclusive) pair for the current window AND
    /// the equally-sized prior window. The prior window ends exactly where the current
    /// starts, so each request contributes to at most one — no double-counting.
    /// </summary>
    private static (long CurrentFromMs, long PriorFromMs, long PriorToMsExclusive) WindowEpochs(int windowDays)
    {
        var now = DateTimeOffset.UtcNow;
        var currentFrom = now.AddDays(-windowDays);
        var priorFrom = now.AddDays(-windowDays * 2L);
        return (currentFrom.ToUnixTimeMilliseconds(),
                priorFrom.ToUnixTimeMilliseconds(),
                currentFrom.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// Percent change from <paramref name="prior"/> to <paramref name="current"/>.
    /// Returns 0 when prior is 0 to avoid a divide-by-zero blowing up the response;
    /// the UI shows "—" for that case rather than "+∞%".
    /// </summary>
    private static double DeltaPercent(double current, double prior) =>
        prior == 0 ? 0.0 : Math.Round((current - prior) * 100.0 / prior, 1);
}
