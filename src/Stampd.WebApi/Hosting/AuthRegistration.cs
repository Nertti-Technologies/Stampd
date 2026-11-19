using System.Text;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

using Stampd.Identity.Oidc;
using Stampd.Identity.Saml2;
using Stampd.WebApi.Auth;

namespace Stampd.WebApi.Hosting;

/// <summary>
/// The authentication mode decided at startup. EndpointMapping uses it to decide whether
/// the dev-token endpoint is mapped.
/// </summary>
internal sealed record StampdAuthMode(bool IsOidc, bool DevAuthAllowed);

internal static class AuthRegistration
{
    /// <summary>
    /// Registers authentication for the configured mode (DevJwt, OIDC or SAML2) and throws
    /// at startup when the mode is not allowed in this environment.
    /// </summary>
    /// <remarks>
    /// Ordering invariant: the returned <see cref="StampdAuthMode"/> is the same decision
    /// the startup guard below enforces; EndpointMapping.MapStampdEndpoints maps
    /// /api/auth/dev-token only when <c>!IsOidc &amp;&amp; DevAuthAllowed</c>, so the two must
    /// keep using the same values. UseAuthentication precedes TenantResolutionMiddleware and
    /// UseAuthorization in the Program.cs pipeline.
    /// </remarks>
    public static StampdAuthMode AddStampdAuthentication(this WebApplicationBuilder builder)
    {
        // ---- AuthN/AuthZ ----
        //
        // IMPORTANT: bind JwtOptions via the DI options pattern (NOT by reading
        // builder.Configuration["..."] eagerly here). WebApplicationFactory adds its in-memory
        // config overrides via ConfigureAppConfiguration which runs DURING builder.Build() —
        // later than the top-level statements in Program.cs. If we read the JWT issuer/audience
        // here, integration tests would get the appsettings.json defaults instead of their
        // overrides, and every JWT would fail validation with 401.
        //
        // The fix: register JwtBearerOptions configuration as a DI callback that runs at
        // resolve-time via IOptionsMonitor<JwtOptions>, so it sees the final, fully-merged config.
        builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Stampd:Auth:Jwt"));

        // v3.0 alpha.2 — auth-mode selector. DevJwt mode keeps v2.x behavior (in-process
        // signing + dev token endpoint). Oidc mode hands JwtBearer over to an external
        // IdP via Stampd.Identity.Oidc.AddStampdOidcRelay. Default is DevJwt to preserve
        // existing local dev flows; production deployments MUST opt into Oidc and the
        // guard below throws if they don't.
        //
        // Read as a string then case-insensitive parse — GetValue<TEnum> binding for
        // in-memory config providers has been flaky across .NET 10 preview builds.
        // Manual string compare is rock-solid and avoids surprises.
        var authModeRaw = builder.Configuration["Stampd:Auth:Mode"];
        // v3.0 alpha.3 — third mode 'Saml2' for SAML federation. SAML2 alpha is scaffold
        // only (config + endpoint surface + claim mapper). v3.0.0 stable wires the real
        // ITfoxtec.Identity.Saml2-backed assertion validation.
        var isOidc = string.Equals(authModeRaw, "Oidc", StringComparison.OrdinalIgnoreCase);
        var isSaml2 = string.Equals(authModeRaw, "Saml2", StringComparison.OrdinalIgnoreCase);

        // Development authentication (the HS256 dev key and /api/auth/dev-token) exists only
        // where DevAuth.IsAllowed says so. Outside it, OIDC is the only mode: the SAML2 SP is a
        // scaffold that validates no assertions, so it can't authenticate anyone yet.
        var devAuthAllowed = DevAuth.IsAllowed(builder.Environment);
        if (!isOidc && !devAuthAllowed)
        {
            throw new InvalidOperationException(isSaml2
                ? "Stampd:Auth:Mode=Saml2 is only allowed in Development until the SAML2 SP validates " +
                  "assertions. Set Stampd:Auth:Mode=Oidc for production deployments."
                : "Stampd:Auth:Mode=DevJwt is only allowed in Development. Set Stampd:Auth:Mode=Oidc " +
                  "(real-IdP OIDC validation) for production deployments.");
        }

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        if (isSaml2)
        {
            // v3.0 alpha.3 — register the SAML2 SP scaffold's eager config validation.
            // Endpoint scaffolds map at app-bootstrap time (EndpointMapping).
            builder.Services.AddStampdSaml2Sp(builder.Configuration.GetSection("Stampd:Auth:Saml2"));
        }

        if (isOidc)
        {
            // OIDC relay wires JwtBearer against the external IdP's discovery document
            // and applies Stampd's role-claim mapping at token-validated time. Throws at
            // startup if Authority/Audience are missing.
            builder.Services.AddStampdOidcRelay(builder.Configuration.GetSection("Stampd:Auth:Oidc"));
        }
        else
        {
            builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                .Configure<IOptionsMonitor<JwtOptions>>((bearerOptions, jwtMonitor) =>
                {
                    var jwt = jwtMonitor.CurrentValue;
                    bearerOptions.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidIssuer = jwt.Issuer,
                        ValidAudience = jwt.Audience,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        ClockSkew = TimeSpan.FromMinutes(2),
                    };
                });
        }

        return new StampdAuthMode(isOidc, devAuthAllowed);
    }
}
