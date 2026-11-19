using Stampd.Core.Tenancy;
using Stampd.WebApi.Tenancy;

namespace Stampd.WebApi.Hosting;

internal static class TenancyRegistration
{
    /// <summary>
    /// Registers tenant options and the per-request tenant context.
    /// </summary>
    /// <remarks>
    /// Ordering invariant: the tenant chosen here is filled in by TenantResolutionMiddleware,
    /// which Program.cs places between UseAuthentication and UseAuthorization so that
    /// AdminScopeAuthorizationHandler (see AuthorizationRegistration) sees the resolved tenant.
    /// </remarks>
    public static void AddStampdTenancy(this WebApplicationBuilder builder)
    {
        // Tenant context: TenantResolutionMiddleware picks the request's tenant from the
        // caller's token (see the pipeline in Program.cs); HttpTenantContext serves it to the DbContext.
        builder.Services.AddOptions<TenancyOptions>()
            .Bind(builder.Configuration.GetSection("Stampd:Tenancy"))
            .Configure(options =>
            {
                if (string.Equals(builder.Configuration["Stampd:Auth:Mode"], "Oidc", StringComparison.OrdinalIgnoreCase))
                {
                    options.TenantClaim = builder.Configuration["Stampd:Auth:Oidc:TenantClaim"] ?? TenancyOptions.DefaultTenantClaim;
                }
            })
            .Validate(options => options.DefaultTenantId != Guid.Empty, "Stampd:Tenancy:DefaultTenantId must name a tenant.")
            .ValidateOnStart();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<RequestTenant>();
        builder.Services.AddScoped<ITenantContext, HttpTenantContext>();
    }
}
