using Stampd.Core;
using Stampd.Core.Revocation;
using Stampd.Core.Sealing;
using Stampd.Engine;
using Stampd.Revocation.Http;
using Stampd.WebApi.Services;

namespace Stampd.WebApi.Hosting;

internal static class EngineRegistration
{
    /// <summary>
    /// Registers the revocation providers, the process-wide PAdES level and the engine.
    /// </summary>
    /// <remarks>
    /// Ordering invariant: the engine factory resolves the sealing provider
    /// (SealingRegistration) and the timestamp provider (TimestampRegistration, possibly the
    /// failover wrapper) registered before this call in Program.cs.
    /// </remarks>
    public static void AddStampdEngine(this WebApplicationBuilder builder)
    {
        // Revocation providers for PAdES B-LT. When enabled, the engine pre-fetches OCSP / CRL
        // material for each cert in the signer chain and embeds it in a /DSS catalog entry.
        // Adopters who don't need B-LT can leave this disabled — engine output stays at B-T.
        var enableRevocation = builder.Configuration.GetValue("Stampd:Revocation:Enabled", defaultValue: false);
        if (enableRevocation)
        {
            builder.Services.AddHttpRevocationProviders();
        }

        // Process-wide PAdES level. Endpoints read this via PadesDefaults so callers don't have to
        // pass it in every request body. Adopters who want B-LT set Stampd:Pades:TargetLevel="BLT"
        // (and Stampd:Revocation:Enabled=true). For B-LTA set it to "BLTA" — adds a second TSA
        // round-trip per signature for the archive timestamp.
        var padesLevel = Enum.TryParse<Stampd.Core.Entities.PAdESLevel>(
            builder.Configuration["Stampd:Pades:TargetLevel"], ignoreCase: true, out var parsedLevel)
            ? parsedLevel
            : Stampd.Core.Entities.PAdESLevel.BT;
        builder.Services.AddSingleton(new PadesDefaults(padesLevel));

        builder.Services.AddSingleton<IStampdEngine>(sp =>
        {
            var sealing = sp.GetRequiredService<ICryptographicSealingProvider>();
            var tsa = sp.GetService<ITimestampAuthorityProvider>();
            var revocation = sp.GetService<IRevocationProvider>();
            return new PdfSharpStampdEngine(sealing, tsa, revocation);
        });
    }
}
