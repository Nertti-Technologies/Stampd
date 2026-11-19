using System.Threading.RateLimiting;

namespace Stampd.WebApi.Hosting;

internal static class RequestLimitsRegistration
{
    /// <summary>
    /// Registers the rate-limiter policies and the request body size caps.
    /// </summary>
    /// <remarks>
    /// Ordering invariant: the "sign" policy registered here is applied by name to the sign
    /// endpoint group in EndpointMapping.MapStampdEndpoints, and Program.cs places
    /// UseRateLimiter after UseAuthorization in the pipeline.
    /// </remarks>
    public static void AddStampdRequestLimits(this WebApplicationBuilder builder)
    {
        // ---- Rate limiting ----
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("sign", httpContext =>
                RateLimitPartition.GetTokenBucketLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = 20,
                        ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                        TokensPerPeriod = 10,
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));
        });

        // ---- Request size caps (50 MB) ----
        builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = 50 * 1024 * 1024;
        });
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = 50 * 1024 * 1024;
        });
    }
}
