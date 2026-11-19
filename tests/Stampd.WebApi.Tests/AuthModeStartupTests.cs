using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Stampd.WebApi.Tests;

/// <summary>
/// v3.0 alpha.2 — startup-time guard tests. The auth-mode selector throws when
/// the configured mode is incompatible with the hosting environment, so misconfig
/// surfaces at boot instead of as silent 401s in production.
/// </summary>
/// <remarks>
/// Each test spins up a separate factory with the specific config + environment
/// combination under test. We don't share fixtures because we need to vary the
/// hosting environment per test, which can't be done after the host is built.
/// </remarks>
public sealed class AuthModeStartupTests
{
    /// <summary>
    /// DevJwt mode in Production must throw. v2.x deployments that accidentally
    /// leave the dev minter on need to fail loudly, not silently accept any token
    /// the developer minted on their laptop.
    /// </summary>
    [Fact]
    public void DevJwt_InProduction_ThrowsAtStartup()
    {
        var factory = new ScopedStampdWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Stampd:Auth:Mode"] = "DevJwt",
        }, environment: "Production");

        // CreateClient triggers the lazy host build, which runs Program.cs's
        // top-level statements. The guard throws there.
        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("Stampd:Auth:Mode=DevJwt", ex.Message);
    }

    /// <summary>
    /// DevJwt mode in Testing must succeed — that's how the rest of the test
    /// suite issues bearer tokens. Confirms the guard's Testing carve-out works.
    /// </summary>
    [Fact]
    public void DevJwt_InTesting_StartsCleanly()
    {
        var factory = new ScopedStampdWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Stampd:Auth:Mode"] = "DevJwt",
        });

        // No throw, healthy host.
        using var client = factory.CreateClient();
        Assert.NotNull(client);
    }

    /// <summary>
    /// The SAML2 SP validates no assertions yet, so Saml2 mode can't authenticate anyone
    /// outside Development and must not fall back to the development bearer key.
    /// </summary>
    [Fact]
    public void Saml2_InProduction_ThrowsAtStartup()
    {
        var factory = new ScopedStampdWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Stampd:Auth:Mode"] = "Saml2",
            // A complete SP configuration, so the SAML options validation passes and only
            // the development-auth guard can stop startup.
            ["Stampd:Auth:Saml2:SpEntityId"] = "https://stampd.example.test/saml",
            ["Stampd:Auth:Saml2:IdpEntityId"] = "https://idp.example.test",
            ["Stampd:Auth:Saml2:IdpMetadataUrl"] = "https://idp.example.test/metadata",
        }, environment: "Production");

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("Mode=Saml2 is only allowed in Development", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The anonymous dev-token endpoint exists only where development auth is allowed. An
    /// unmapped route answers 401 here, because the fallback policy also covers requests
    /// that match no endpoint, so the test checks the endpoint table as well.
    /// </summary>
    [Fact]
    public async Task Oidc_InProduction_DoesNotMapTheDevTokenEndpoint()
    {
        using var factory = new ScopedStampdWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Stampd:Auth:Mode"] = "Oidc",
            ["Stampd:Auth:Oidc:Authority"] = "https://idp.example.test",
            ["Stampd:Auth:Oidc:Audience"] = "stampd-api",
        }, environment: "Production");

        // HTTPS so the production pipeline's HTTPS redirection doesn't answer first.
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        using var response = await client.PostAsJsonAsync(
            "/api/auth/dev-token",
            new { subject = "anyone", roles = new[] { "Admin" } },
            TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(
            factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>(),
            e => e.RoutePattern.RawText?.TrimStart('/') == "api/auth/dev-token");
    }

    // Note: the Oidc-without-Authority guard is exercised as a focused unit test
    // in tests/Stampd.Engine.Tests/OidcRelayRegistrationTests.cs — proving
    // AddStampdOidcRelay throws synchronously when called with an empty section.
    // We don't repeat it here because the WebApplicationFactory pipeline reads
    // builder.Configuration eagerly at top-level Program.cs execution — BEFORE
    // ConfigureAppConfiguration callbacks apply the test factory's in-memory
    // overrides (see Program.cs's existing comment on the JwtOptions pattern).
    // That timing makes a startup-throw integration test fragile; the unit test
    // covers the same guard at the registration boundary.
}
