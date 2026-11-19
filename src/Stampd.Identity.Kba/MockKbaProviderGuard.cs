using Microsoft.Extensions.Hosting;

namespace Stampd.Identity.Kba;

/// <summary>
/// Stops the host from starting outside the Development environment while
/// <see cref="MockKbaProvider"/> is the registered <see cref="IKbaProvider"/>, so a
/// deployment that forgot to register a real provider fails loudly instead of passing
/// every signer.
/// </summary>
internal sealed class MockKbaProviderGuard(
    IKbaProvider provider,
    KbaIdentityVerificationOptions options,
    IHostEnvironment environment) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (provider is MockKbaProvider && !environment.IsDevelopment() && !options.AllowMockProvider)
        {
            throw new InvalidOperationException(
                "The mock knowledge-based verification provider is registered outside the " +
                "Development environment. Register a real IKbaProvider before calling " +
                "AddKbaIdentityVerification, or set KbaIdentityVerificationOptions.AllowMockProvider " +
                "for a non-production environment.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
