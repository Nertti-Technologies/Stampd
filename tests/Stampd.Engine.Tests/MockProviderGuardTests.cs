using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

using Stampd.Identity.Kba;
using Stampd.Identity.SmsOtp;

using Xunit;

namespace Stampd.Engine.Tests;

/// <summary>
/// The mock SMS gateway and mock knowledge-check provider stop the host from starting
/// outside Development unless the adopter explicitly allows them.
/// </summary>
public sealed class MockProviderGuardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Production", false, true)]
    [InlineData("Staging", false, true)]
    [InlineData("Development", false, false)]
    [InlineData("Production", true, false)]
    public async Task MockSmsGateway_BlocksStartupOutsideDevelopment(string environment, bool allow, bool expectBlocked)
    {
        var services = Services(environment);
        services.AddSmsOtpIdentityVerification(o => o.AllowMockGateway = allow);

        await AssertStartupAsync(services, expectBlocked);
    }

    [Theory]
    [InlineData("Production", false, true)]
    [InlineData("Development", false, false)]
    [InlineData("Production", true, false)]
    public async Task MockKbaProvider_BlocksStartupOutsideDevelopment(string environment, bool allow, bool expectBlocked)
    {
        var services = Services(environment);
        services.AddKbaIdentityVerification(o => o.AllowMockProvider = allow);

        await AssertStartupAsync(services, expectBlocked);
    }

    [Fact]
    public async Task RealSmsGateway_StartsInProduction()
    {
        var services = Services("Production");
        services.AddSingleton<ISmsGateway, RealishGateway>();
        services.AddSmsOtpIdentityVerification();

        await AssertStartupAsync(services, expectBlocked: false);
    }

    private static ServiceCollection Services(string environment)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment(environment));
        return services;
    }

    private static async Task AssertStartupAsync(ServiceCollection services, bool expectBlocked)
    {
        await using var provider = services.BuildServiceProvider();
        var start = async () =>
        {
            foreach (var hosted in provider.GetServices<IHostedService>())
            {
                await hosted.StartAsync(Ct);
            }
        };

        if (expectBlocked)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(start);
        }
        else
        {
            await start();
        }
    }

    private sealed class RealishGateway : ISmsGateway
    {
        public string Name => "real";

        public Task SendAsync(string phoneNumber, string message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
