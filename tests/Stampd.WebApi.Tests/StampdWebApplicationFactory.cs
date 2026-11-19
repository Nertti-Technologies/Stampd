using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Stampd.Infrastructure;

namespace Stampd.WebApi.Tests;

/// <summary>
/// Spins up the real Stampd.WebApi host with overridden config so tests don't touch the
/// developer's <c>~/.stampd</c>, hit FreeTSA, or talk to Vault.
/// </summary>
public class StampdWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _isolatedRoot;
    // A database file in this factory's temp folder, so per-factory state is isolated.
    // Each DbContext opens its own connection: background workers (bulk send, webhooks)
    // query while the test does, and one shared SqliteConnection is not safe across
    // threads. SQLite's busy timeout serialises the writers.
    private readonly string _connectionString;

    /// <summary>
    /// Single public parameterless ctor — xUnit's <c>IClassFixture&lt;T&gt;</c>
    /// requires exactly one public ctor on the fixture type and resolves it
    /// reflectively. Per-test config overrides ride on the
    /// <see cref="ScopedStampdWebApplicationFactory"/> subclass (v3.0 alpha.1+),
    /// which the tests that need it instantiate directly.
    /// </summary>
    public StampdWebApplicationFactory()
    {
        _isolatedRoot = Path.Combine(Path.GetTempPath(), $"stampd-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_isolatedRoot);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_isolatedRoot, "stampd-tests.db"),
            // No pooling, so no connection keeps the file open when Dispose deletes it.
            Pooling = false,
        }.ToString();
    }

    /// <summary>
    /// Subclass extension point — return additional key/value pairs to layer on
    /// top of the default in-memory baseline. Called once per host build.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string?>? ExtraConfig() => null;

    /// <summary>
    /// Subclass extension point — return the ASP.NET Core environment name. v3.0
    /// alpha.2 uses this so the auth-mode startup guard can be tested with
    /// "Production" without changing the rest of the test infrastructure.
    /// Defaults to "Testing".
    /// </summary>
    protected virtual string EnvironmentName() => "Testing";

    private Dictionary<string, string?> BaselineConfig() => new()
    {
        // Force LocalCertificate sealing — no Vault dependency in tests
        ["Stampd:Sealing:Provider"] = "Local",
        ["Stampd:SigningCertificate:Pkcs12Path"] = Path.Combine(_isolatedRoot, "test-signer.pfx"),
        ["Stampd:SigningCertificate:Password"] = "stampd-test",

        // No TSA — tests stay fully offline
        ["Stampd:Tsa:Enabled"] = "false",

        // Isolated storage and DB
        ["Stampd:Storage:FileSystemRoot"] = Path.Combine(_isolatedRoot, "documents"),
        ["Stampd:Database:SqlitePath"] = Path.Combine(_isolatedRoot, "stampd-tests.db"),

        // Deterministic JWT signing key for token issuance + validation
        ["Stampd:Auth:Jwt:SigningKey"] = "stampd-test-signing-key-do-not-use-in-prod-32+chars",
        ["Stampd:Auth:Jwt:Issuer"] = "stampd-test",
        ["Stampd:Auth:Jwt:Audience"] = "stampd-test-api",
        ["Stampd:Auth:Jwt:EnableDevTokenEndpoint"] = "true",

        // Log to /dev/null during tests
        ["Stampd:Logging:Directory"] = Path.Combine(_isolatedRoot, "logs"),
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName());

        // Program.cs reads many settings (signing certificate, TSA, storage root, auth mode,
        // log directory) while the WebApplicationBuilder is still being configured, before
        // ConfigureAppConfiguration sources apply. Host settings are visible at that point,
        // so the baseline and every subclass override are applied here as well. Without
        // this, test hosts sign with the developer's ~/.stampd certificate, write documents
        // to ~/.stampd/documents, call the public TSA, and a test setting Stampd:Auth:Mode
        // silently runs with the default mode. Overrides come last, so they win.
        foreach (var (key, value) in BaselineConfig())
        {
            builder.UseSetting(key, value);
        }

        foreach (var (key, value) in ExtraConfig() ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(BaselineConfig());

            // v3.0 alpha.1 — subclasses can layer additional overrides on top of
            // the baseline above. Added last so subclass keys take precedence.
            var extras = ExtraConfig();
            if (extras is { Count: > 0 })
            {
                config.AddInMemoryCollection(extras);
            }
        });

        // v3.0 alpha.1 — replace whatever DbContextOptions<StampdDbContext>
        // Program.cs registered with one bound to this factory's database file.
        // Then EnsureCreated so the schema lands without needing the per-provider
        // migration assembly.
        builder.ConfigureTestServices(services =>
        {
            var optionsDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<StampdDbContext>));
            if (optionsDescriptor is not null)
            {
                services.Remove(optionsDescriptor);
            }

            services.AddDbContext<StampdDbContext>(options =>
                options.UseSqlite(_connectionString));
        });

        base.ConfigureWebHost(builder);
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        // EnsureCreated builds the full model schema on first build, and is a no-op
        // for hosts derived through WithWebHostBuilder, which share the file.
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StampdDbContext>();
        db.Database.EnsureCreated();

        // WAL lets the background workers read while a test writes, instead of each
        // waiting out the other's lock. The mode is stored in the file.
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");

        return host;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            if (disposing && Directory.Exists(_isolatedRoot))
            {
                Directory.Delete(_isolatedRoot, recursive: true);
            }
        }
        catch
        {
            // best effort cleanup
        }
    }
}
