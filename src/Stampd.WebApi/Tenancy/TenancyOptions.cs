namespace Stampd.WebApi.Tenancy;

/// <summary>How tenants are assigned to authenticated callers.</summary>
public enum TenancyMode
{
    /// <summary>One-tenant install: a token without a tenant claim runs in <see cref="TenancyOptions.DefaultTenantId"/>.</summary>
    Single,

    /// <summary>Several tenants: every token must carry a tenant claim.</summary>
    Multi,
}

/// <summary>
/// Tenant resolution settings, bound from <c>Stampd:Tenancy</c>. The tenant claim type
/// comes from <c>Stampd:Auth:Oidc:TenantClaim</c> in OIDC mode, else <c>tenant_id</c>.
/// </summary>
public sealed class TenancyOptions
{
    public const string DefaultTenantClaim = "tenant_id";

    public TenancyMode Mode { get; set; } = TenancyMode.Single;

    public Guid DefaultTenantId { get; set; } = new("00000000-0000-0000-0000-000000000001");

    public string TenantClaim { get; set; } = DefaultTenantClaim;
}

/// <summary>
/// Per-request holder for the tenant chosen by <see cref="TenantResolutionMiddleware"/>.
/// Null until the middleware runs, and for requests that never reach it.
/// </summary>
public sealed class RequestTenant
{
    public Guid? TenantId { get; set; }
}
