using Stampd.Core.Sealing;
using Stampd.Timestamp.FreeTsa;
using Stampd.Timestamp.Rfc3161;
using Stampd.WebApi.Services;

namespace Stampd.WebApi.Hosting;

internal static class TimestampRegistration
{
    /// <summary>
    /// Registers the configured <see cref="ITimestampAuthorityProvider"/> (when
    /// <paramref name="enableTsa"/> is set) and, optionally, the failover wrapper around it.
    /// <paramref name="enableTsa"/> and <paramref name="tsaEndpoint"/> are read by Program.cs
    /// before any registration.
    /// </summary>
    /// <remarks>
    /// Ordering invariant: the failover wrapper below takes the LAST
    /// <see cref="ITimestampAuthorityProvider"/> descriptor in the collection as the primary
    /// and replaces it. Nothing else may register an <see cref="ITimestampAuthorityProvider"/>
    /// after this method runs (Program.cs calls it once, after sealing and before the engine);
    /// the engine factory (EngineRegistration) resolves whichever registration is left.
    /// </remarks>
    public static void AddStampdTimestampAuthority(
        this WebApplicationBuilder builder, bool enableTsa, string? tsaEndpoint)
    {
        if (enableTsa)
        {
            // TSA provider selection:
            //   - "FreeTSA" (default) → free public TSA, dev / demo / low-volume.
            //   - "Rfc3161"           → generic provider, configurable URL + optional basic auth +
            //                           optional mTLS client cert. Use for DigiCert / GlobalSign /
            //                           Sectigo / internal Microsoft AD CS / EJBCA TSAs.
            var tsaProvider = builder.Configuration["Stampd:Tsa:Provider"] ?? "FreeTSA";
            if (string.Equals(tsaProvider, "Rfc3161", StringComparison.OrdinalIgnoreCase))
            {
                builder.Services.AddRfc3161TimestampAuthority(options =>
                {
                    options.Name = builder.Configuration["Stampd:Tsa:Name"] ?? "RFC3161";
                    options.Endpoint = new Uri(builder.Configuration["Stampd:Tsa:Endpoint"]
                        ?? throw new InvalidOperationException(
                            "Stampd:Tsa:Endpoint is required when Stampd:Tsa:Provider = 'Rfc3161'."));
                    options.BasicAuthUsername = builder.Configuration["Stampd:Tsa:BasicAuthUsername"];
                    options.BasicAuthPassword = builder.Configuration["Stampd:Tsa:BasicAuthPassword"];
                    options.ClientCertificatePkcs12Path = builder.Configuration["Stampd:Tsa:ClientCertificatePkcs12Path"];
                    options.ClientCertificatePassword = builder.Configuration["Stampd:Tsa:ClientCertificatePassword"];
                    options.RequestedPolicyOid = builder.Configuration["Stampd:Tsa:RequestedPolicyOid"];
                    options.RequestTsaCertificate = builder.Configuration.GetValue(
                        "Stampd:Tsa:RequestTsaCertificate", defaultValue: true);
                    options.IncludeNonce = builder.Configuration.GetValue(
                        "Stampd:Tsa:IncludeNonce", defaultValue: true);
                });
            }
            else
            {
                builder.Services.AddFreeTsaTimestampAuthority(tsaEndpoint is null ? null : new Uri(tsaEndpoint));
            }

            // v2.1 — opt-in DigiCert failover. When Stampd:Tsa:EnableDigiCertFailover=true,
            // we wrap whatever ITimestampAuthorityProvider was just registered with a
            // FailoverTimestampAuthorityProvider that prepends the primary and falls
            // back to DigiCert's free public TSA if the primary throws / times out /
            // refuses. Default is FALSE so existing deployments see zero behaviour
            // change. Adopters running FreeTSA in dev — where it flakes — get a
            // one-line config knob to make the demo reliable.
            var enableDigiCertFailover = builder.Configuration.GetValue(
                "Stampd:Tsa:EnableDigiCertFailover", defaultValue: false);
            if (enableDigiCertFailover)
            {
                builder.Services.AddHttpClient("stampd-tsa-failover-digicert", c =>
                {
                    c.Timeout = TimeSpan.FromSeconds(15);
                });
                // The trick: capture the singleton already registered above, then
                // replace its registration with one that wraps it. ServiceDescriptor
                // .DescribeKeyed isn't needed because there's only one provider
                // registration for the interface — the last one wins via
                // ServiceCollection.Replace.
                builder.Services.AddSingleton<FailoverTsaWrapperFactory>();
                var primaryDescriptor = builder.Services.Last(
                    d => d.ServiceType == typeof(ITimestampAuthorityProvider));
                builder.Services.Remove(primaryDescriptor);
                builder.Services.AddSingleton<ITimestampAuthorityProvider>(sp =>
                {
                    // Re-resolve the primary by hand using the original descriptor.
                    // We can't just call sp.GetService<ITimestampAuthorityProvider>()
                    // because that would loop back into this factory.
                    var primary = ResolvePrimary(sp, primaryDescriptor);
                    var factory = sp.GetRequiredService<FailoverTsaWrapperFactory>();
                    return factory.WrapWithDigiCert(primary);
                });
            }
        }
    }

    // Local helper — invokes the original primary-TSA descriptor without going
    // through DI (avoids self-recursion in the wrapped registration above).
    private static ITimestampAuthorityProvider ResolvePrimary(IServiceProvider sp, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationFactory is not null)
        {
            return (ITimestampAuthorityProvider)descriptor.ImplementationFactory(sp);
        }
        if (descriptor.ImplementationInstance is ITimestampAuthorityProvider instance)
        {
            return instance;
        }
        if (descriptor.ImplementationType is not null)
        {
            return (ITimestampAuthorityProvider)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType);
        }
        throw new InvalidOperationException(
            "Cannot resolve the primary ITimestampAuthorityProvider for failover wrapping.");
    }
}
