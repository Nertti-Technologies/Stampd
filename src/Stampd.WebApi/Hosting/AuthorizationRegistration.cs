using Stampd.WebApi.Auth;

namespace Stampd.WebApi.Hosting;

internal static class AuthorizationRegistration
{
    /// <summary>
    /// Registers the actor context, the Admin scope handler and the named authorization
    /// policies (with an authenticated-user fallback).
    /// </summary>
    /// <remarks>
    /// Ordering invariant: AdminScopeAuthorizationHandler reads the tenant that
    /// TenantResolutionMiddleware resolves, so Program.cs keeps that middleware between
    /// UseAuthentication and UseAuthorization. The policy names registered here are the ones
    /// EndpointMapping.MapStampdEndpoints applies to its endpoint groups.
    /// </remarks>
    public static void AddStampdAuthorization(this WebApplicationBuilder builder)
    {
        // v2.0 Slice D — server-side actor context for audit attribution. Scoped so each
        // HTTP request gets a fresh read of HttpContext.User. Background workers and tests
        // that don't have an HttpContext get IsAuthenticated=false / UserId=null which is
        // the correct shape for "system-initiated action".
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<Stampd.Core.Authorization.ICurrentActorContext,
            HttpCurrentActorContext>();

        // v2.0 — three named policies map onto the StampdRoles taxonomy. Endpoint groups gate
        // themselves on the appropriate policy (see managementGroup / adminGroup wiring in
        // EndpointMapping). Default fallback policy is RequireAuthenticatedUser so any endpoint
        // that forgets to tag a policy still requires a valid JWT — defense-in-depth.
        // v3.0 alpha.1 — register the per-tenant Admin scope handler. The policy itself
        // only carries the Admin scope requirement; the handler walks the AdminScopes
        // table to validate. Scoped registration so each request gets a handler bound to
        // the per-request ICurrentActorContext + ITenantContext.
        builder.Services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
            Stampd.WebApi.Auth.AdminScopeAuthorizationHandler>();

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(Stampd.Core.Authorization.StampdRoles.Admin, p => p
                .RequireAuthenticatedUser()
                // v3.0 alpha.1: was .RequireRole(Admin). The new requirement checks BOTH
                // the Admin role claim AND an active AdminScope row for the request's
                // tenant. The handler enforces both gates so existing role-aware code (UI
                // nav, dev JWT minter, etc.) doesn't need changes.
                .AddRequirements(new Stampd.WebApi.Auth.AdminScopeRequirement()))
            .AddPolicy(StampdPolicies.SenderOrAdmin, p => p
                .RequireAuthenticatedUser()
                .RequireRole(
                    Stampd.Core.Authorization.StampdRoles.Sender,
                    Stampd.Core.Authorization.StampdRoles.Admin))
            .AddPolicy(StampdPolicies.AuthenticatedAny, p => p
                .RequireAuthenticatedUser())
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());
    }
}
