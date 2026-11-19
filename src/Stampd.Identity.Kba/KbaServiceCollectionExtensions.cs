using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Stampd.Core.Identity;

namespace Stampd.Identity.Kba;

public static class KbaServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="KbaIdentityVerificationProvider"/> as the application's
    /// <see cref="IIdentityVerificationProvider"/>. Defaults to <see cref="MockKbaProvider"/>
    /// — adopters wire a real <see cref="IKbaProvider"/> (LexisNexis, Experian, ID.me) by
    /// registering their implementation BEFORE calling this. Outside the Development
    /// environment the host refuses to start while the mock is registered.
    /// </summary>
    public static IServiceCollection AddKbaIdentityVerification(this IServiceCollection services)
        => services.AddKbaIdentityVerification(configure: null);

    /// <inheritdoc cref="AddKbaIdentityVerification(IServiceCollection)"/>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Adjusts <see cref="KbaIdentityVerificationOptions"/>.</param>
    public static IServiceCollection AddKbaIdentityVerification(
        this IServiceCollection services,
        Action<KbaIdentityVerificationOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new KbaIdentityVerificationOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<IKbaProvider, MockKbaProvider>();
        services.TryAddSingleton<IIdentityVerificationProvider, KbaIdentityVerificationProvider>();
        services.AddHostedService<MockKbaProviderGuard>();

        return services;
    }
}
