namespace Stampd.WebApi.Auth;

/// <summary>
/// Decides where development authentication (the HS256 dev signing key and the
/// anonymous <c>/api/auth/dev-token</c> endpoint) may exist. Both Program.cs gates use
/// this one check, so the endpoint and the key that validates its tokens can't drift apart.
/// </summary>
internal static class DevAuth
{
    /// <summary>
    /// Development, and the "Testing" environment the WebApplicationFactory tests run in.
    /// </summary>
    public static bool IsAllowed(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.IsDevelopment() || environment.IsEnvironment("Testing");
    }
}
