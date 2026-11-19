using Microsoft.Extensions.Options;

using Stampd.Core.Tenancy;

namespace Stampd.WebApi.Tenancy;

/// <summary>
/// The tenant the current request or background job runs under. Registered as scoped
/// so the DbContext's query filters see the right tenant per request.
/// </summary>
/// <remarks>
/// Resolution order:
/// <list type="number">
///   <item><see cref="TenantScope.Current"/>: background workers and tests switch tenant
///   across an async flow, including <see cref="Guid.Empty"/> for deliberate cross-tenant
///   reads. That marker is reachable only this way: <see cref="TenantResolutionMiddleware"/>
///   rejects it from HTTP input (StampdDbContext's query filters treat it as "all tenants").</item>
///   <item>The tenant <see cref="TenantResolutionMiddleware"/> resolved for this request
///   from the caller's token.</item>
///   <item><see cref="TenancyOptions.DefaultTenantId"/> for work outside a request.</item>
/// </list>
/// Read on every access, not at construction: the DbContext (and so this context) can be
/// created inside the resolution middleware, before the request's tenant is known.
/// </remarks>
public sealed class HttpTenantContext : ITenantContext
{
    private readonly RequestTenant _requestTenant;
    private readonly Guid _defaultTenantId;

    public HttpTenantContext(RequestTenant requestTenant, IOptions<TenancyOptions> options)
    {
        ArgumentNullException.ThrowIfNull(requestTenant);
        ArgumentNullException.ThrowIfNull(options);
        _requestTenant = requestTenant;
        _defaultTenantId = options.Value.DefaultTenantId;
    }

    public Guid TenantId => TenantScope.Current ?? _requestTenant.TenantId ?? _defaultTenantId;
}
