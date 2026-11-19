using Stampd.Identity.Saml2;
using Stampd.WebApi.Auth;
using Stampd.WebApi.Endpoints;
using Stampd.WebApi.Observability.HealthChecks;

namespace Stampd.WebApi.Hosting;

internal static class EndpointMapping
{
    /// <summary>
    /// Maps the health probes and every endpoint group with its authorization and rate-limit
    /// policy. Call after the middleware pipeline is configured.
    /// </summary>
    /// <remarks>
    /// Ordering invariants: the "live"/"ready" tags filtered below are the tags Program.cs
    /// gives the health checks; the "sign" rate-limit policy comes from
    /// RequestLimitsRegistration; the dev-token endpoint follows the auth decision made by
    /// AuthRegistration.AddStampdAuthentication (<paramref name="authMode"/>).
    /// </remarks>
    public static void MapStampdEndpoints(this WebApplication app, StampdAuthMode authMode)
    {
        var isOidc = authMode.IsOidc;
        var devAuthAllowed = authMode.DevAuthAllowed;

        // ---- Endpoints ----
        app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("live"),
            ResponseWriter = HealthCheckJsonResponseWriter.WriteResponseAsync,
        }).AllowAnonymous();

        app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = HealthCheckJsonResponseWriter.WriteResponseAsync,
        }).AllowAnonymous();

        // Endpoint extension methods return IEndpointRouteBuilder, which can't chain
        // RequireAuthorization / AllowAnonymous / RequireRateLimiting directly. Wrap each
        // extension call in a MapGroup that pre-applies the policy — the group's conventions
        // propagate to every endpoint mapped inside it.

        // Anonymous surface: health, dev token issuance, recipient signing (recipient is
        // authenticated by the per-recipient access token in the URL, not by JWT).
        var anonGroup = app.MapGroup("").AllowAnonymous();
        anonGroup.MapHealth();
        if (!isOidc && devAuthAllowed)
        {
            anonGroup.MapAuth();
        }
        anonGroup.MapRecipientSigning();
        // Demo bootstrap — registers /api/demo/seed only in Development.
        anonGroup.MapDemo(app.Environment);

        // Sign endpoints: rate-limited AND require auth. v2.0 — gate on SenderOrAdmin so
        // view-only users can't submit signatures.
        var signGroup = app.MapGroup("")
            .RequireAuthorization(StampdPolicies.SenderOrAdmin)
            .RequireRateLimiting("sign");
        signGroup.MapSign();
        signGroup.MapBinarySign();

        // Management endpoints: the group lets any authenticated user (ReadOnly included) read.
        // Each route that creates, changes or deletes data adds StampdPolicies.SenderOrAdmin
        // where it is mapped; WriteRoutePolicyTests fails CI if a write route lacks it.
        var managementGroup = app.MapGroup("").RequireAuthorization(StampdPolicies.AuthenticatedAny);
        managementGroup.MapTemplates();
        managementGroup.MapSigningRequests();
        managementGroup.MapSignedDocuments();
        managementGroup.MapBulkSend();
        managementGroup.MapWebhooks();

        // v2.0 — admin-only surface. Slice A maps /api/admin/dashboard, /api/admin/dashboard/trend,
        // /api/admin/dashboard/top-templates here. Slices B/C/D extend with bulk operations and
        // cross-sender analytics.
        var adminGroup = app.MapGroup("").RequireAuthorization(Stampd.Core.Authorization.StampdRoles.Admin);
        adminGroup.MapAdminDashboard();
        adminGroup.MapAdminOperations();
        adminGroup.MapAdminAnalytics();
        adminGroup.MapAdminAuditExport();
        adminGroup.MapAdminScopes();

        // v3.0 alpha.3 — SAML2 endpoint scaffolds. Always mapped (regardless of Mode)
        // so adopters can probe `/api/auth/saml/metadata` to verify reverse-proxy /
        // DNS routing even before flipping Mode=Saml2. Bodies 501 until v3.0.0 stable.
        app.MapStampdSaml2Endpoints();
    }
}
