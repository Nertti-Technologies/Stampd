using System.Globalization;

using Microsoft.Extensions.Options;

using Stampd.Core.Authorization;

namespace Stampd.WebApi.Tenancy;

/// <summary>
/// Chooses the tenant for each request. For authenticated callers the token's tenant
/// claim decides; the <c>X-Stampd-Tenant</c> header may switch to another tenant only
/// for an Admin who holds an AdminScope for it (or a super-admin).
/// </summary>
/// <remarks>
/// <para>
/// Must run after <c>UseAuthentication</c> (it reads the principal) and before
/// <c>UseAuthorization</c> (AdminScopeAuthorizationHandler checks scope against the
/// tenant chosen here).
/// </para>
/// <para>
/// Anonymous requests reach only the explicitly anonymous routes (health, recipient
/// signing behind its access token, dev auth). They keep the header-or-default rule so
/// recipients in other tenants can still be resolved, but never the all-tenants marker.
/// </para>
/// </remarks>
public sealed class TenantResolutionMiddleware
{
    public const string HeaderName = "X-Stampd-Tenant";

    private readonly RequestDelegate _next;

    public TenantResolutionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        RequestTenant requestTenant,
        ICurrentActorContext actor,
        IOptions<TenancyOptions> options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requestTenant);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(options);

        var settings = options.Value;
        var authenticated = context.User.Identity?.IsAuthenticated == true;

        Guid? requested = null;
        if (context.Request.Headers.TryGetValue(HeaderName, out var values)
            && !string.IsNullOrWhiteSpace(values[0]))
        {
            if (!Guid.TryParse(values[0], CultureInfo.InvariantCulture, out var parsed))
            {
                if (authenticated)
                {
                    await RejectAsync(context, StatusCodes.Status400BadRequest, $"{HeaderName} must be a tenant id.").ConfigureAwait(false);
                    return;
                }
            }
            else if (parsed == Guid.Empty)
            {
                await RejectAsync(context, StatusCodes.Status400BadRequest, $"{HeaderName} must name a tenant.").ConfigureAwait(false);
                return;
            }
            else
            {
                requested = parsed;
            }
        }

        if (!authenticated)
        {
            requestTenant.TenantId = requested ?? settings.DefaultTenantId;
            await _next(context).ConfigureAwait(false);
            return;
        }

        var rawClaim = context.User.FindFirst(settings.TenantClaim)?.Value;
        Guid home;
        if (rawClaim is null)
        {
            if (settings.Mode == TenancyMode.Multi)
            {
                await RejectAsync(context, StatusCodes.Status403Forbidden, "The access token does not name a tenant.").ConfigureAwait(false);
                return;
            }

            home = settings.DefaultTenantId;
        }
        else if (!Guid.TryParse(rawClaim, CultureInfo.InvariantCulture, out home) || home == Guid.Empty)
        {
            await RejectAsync(context, StatusCodes.Status403Forbidden, "The access token's tenant is not valid.").ConfigureAwait(false);
            return;
        }

        if (requested is { } other && other != home)
        {
            if (!actor.IsAdmin || !await HoldsScopeAsync(actor, other, context.RequestAborted).ConfigureAwait(false))
            {
                await RejectAsync(context, StatusCodes.Status403Forbidden, "Not permitted to act in the requested tenant.").ConfigureAwait(false);
                return;
            }

            home = other;
        }

        requestTenant.TenantId = home;
        await _next(context).ConfigureAwait(false);
    }

    private static async Task<bool> HoldsScopeAsync(ICurrentActorContext actor, Guid tenantId, CancellationToken ct)
    {
        var scoped = await actor.GetAdminScopedTenantIdsAsync(ct).ConfigureAwait(false);

        // A single Guid.Empty entry is the super-admin "any tenant" marker.
        return scoped.Contains(tenantId) || (scoped.Count == 1 && scoped.Contains(Guid.Empty));
    }

    private static Task RejectAsync(HttpContext context, int statusCode, string detail)
        => Results.Problem(detail: detail, statusCode: statusCode).ExecuteAsync(context);
}
