using Microsoft.Extensions.Hosting;

namespace Stampd.Identity.SmsOtp;

/// <summary>
/// Stops the host from starting outside the Development environment while
/// <see cref="MockSmsGateway"/> is the registered <see cref="ISmsGateway"/>, so a
/// deployment that forgot to register a real gateway fails loudly instead of running an
/// identity check nobody can pass.
/// </summary>
internal sealed class MockSmsGatewayGuard(
    ISmsGateway gateway,
    SmsOtpOptions options,
    IHostEnvironment environment) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (gateway is MockSmsGateway && !environment.IsDevelopment() && !options.AllowMockGateway)
        {
            throw new InvalidOperationException(
                "The mock SMS gateway is registered outside the Development environment. " +
                "Register a real ISmsGateway before calling AddSmsOtpIdentityVerification, " +
                "or set SmsOtpOptions.AllowMockGateway for a non-production environment.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
