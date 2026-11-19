using System.Security.Cryptography;

using Stampd.Observability;

using Xunit;

namespace Stampd.WebApi.Tests;

/// <summary>
/// Serilog's request logging writes through the static <c>Log.Logger</c>, which each host
/// build replaces, so tests that read the log files must not run alongside other hosts.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StaticLoggerTests
{
    public const string Name = "Static Serilog logger";
}

[Collection(StaticLoggerTests.Name)]
public sealed class RequestLogRedactionTests
{
    [Theory]
    [InlineData("/api/sign/0a1b2c", "/api/sign/{accessToken}")]
    [InlineData("/api/sign/0a1b2c/document", "/api/sign/{accessToken}/document")]
    [InlineData("/api/sign/0a1b2c/verify-identity", "/api/sign/{accessToken}/verify-identity")]
    [InlineData("/sign/0a1b2c", "/sign/{accessToken}")]
    [InlineData("/API/SIGN/0a1b2c", "/API/SIGN/{accessToken}")]
    public void Redact_ReplacesTheTokenSegment(string path, string expected)
        => Assert.Equal(expected, AccessTokenPathRedactionEnricher.Redact(path));

    [Theory]
    [InlineData("/api/sign")]
    [InlineData("/api/sign/")]
    [InlineData("/api/sign/binary")]
    [InlineData("/api/signing-requests/5f0c")]
    [InlineData("/designer/sign/abc")]
    [InlineData("/health/live")]
    public void Redact_LeavesOtherPathsUnchanged(string path)
        => Assert.Same(path, AccessTokenPathRedactionEnricher.Redact(path));

    [Fact]
    public async Task SigningLinkRequest_TokenNeverReachesTheLogFiles()
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), $"stampd-log-redaction-{Guid.NewGuid():N}");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        try
        {
            using (var factory = new ScopedStampdWebApplicationFactory(new Dictionary<string, string?>
            {
                ["Stampd:Logging:Directory"] = logDirectory,
            }))
            {
                var client = factory.CreateClient();
                using var response = await client.GetAsync(
                    new Uri($"/api/sign/{token}", UriKind.Relative),
                    TestContext.Current.CancellationToken);
            }

            // Disposing the host closes the Serilog file sink, so the files are complete.
            var logText = string.Concat(Directory.GetFiles(logDirectory, "*.json")
                .Select(file => File.ReadAllText(file)));

            Assert.Contains("/api/sign/{accessToken}", logText, StringComparison.Ordinal);
            Assert.DoesNotContain(token, logText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(logDirectory))
            {
                Directory.Delete(logDirectory, recursive: true);
            }
        }
    }
}
