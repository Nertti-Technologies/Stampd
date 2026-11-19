using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using Scalar.AspNetCore;

using Serilog;

using Stampd.Engine.Rendering;
using Stampd.Infrastructure;
using Stampd.WebApi.Hosting;
using Stampd.WebApi.Observability;
using Stampd.WebApi.Observability.HealthChecks;
using Stampd.WebApi.Tenancy;

// Composition root. Registration blocks live in Hosting/*Registration.cs; the calls below
// run in a fixed order that several of them rely on (each dependency is noted where it is
// made, and on the other side in the Hosting file). Keep the order when adding to this file.

// PdfSharp 6.x has no default font resolver outside Windows — register at process start.
PlatformFontResolver.Register();

var builder = WebApplication.CreateBuilder(args);

// ---- Observability: Serilog FIRST so config loading is captured ----
builder.ConfigureSerilog();

// OpenTelemetry traces + metrics
builder.Services.AddStampdOpenTelemetry(builder.Configuration);

// ---- Configuration ----
// Read here, before any registration, and passed to the Hosting extensions below.
var certPath = builder.Configuration["Stampd:SigningCertificate:Pkcs12Path"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".stampd", "spike-signer.pfx");
var certPassword = builder.Configuration["Stampd:SigningCertificate:Password"] ?? "stampd-spike";
var enableTsa = builder.Configuration.GetValue("Stampd:Tsa:Enabled", defaultValue: true);
var tsaEndpoint = builder.Configuration["Stampd:Tsa:Endpoint"];
var storageRoot = builder.Configuration["Stampd:Storage:FileSystemRoot"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".stampd", "documents");
var sqlitePath = builder.Configuration["Stampd:Database:SqlitePath"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".stampd", "stampd.db");

Directory.CreateDirectory(Path.GetDirectoryName(sqlitePath)!);
Directory.CreateDirectory(storageRoot);

// ---- Services ----
builder.Services.AddOpenApi();

// Persistence + document storage (Hosting/StorageRegistration.cs).
builder.AddStampdPersistenceAndStorage(sqlitePath, storageRoot);

// Tenant context (Hosting/TenancyRegistration.cs).
builder.AddStampdTenancy();

// Sealing, then TSA, then the engine. Ordering invariant: the TSA failover wrapper replaces
// the last ITimestampAuthorityProvider registration, so no TSA provider may be registered
// after AddStampdTimestampAuthority; the engine factory resolves the sealing and TSA
// providers registered by the two calls before it.
builder.AddStampdSealing(certPath, certPassword);
builder.AddStampdTimestampAuthority(enableTsa, tsaEndpoint);
builder.AddStampdEngine();

// Workflow services + workflow email options (Hosting/WorkflowRegistration.cs).
builder.AddStampdWorkflow();

// SMTP transport + Email OTP identity verification (Hosting/EmailRegistration.cs).
builder.AddStampdEmail();

// Background workers + webhook delivery client (Hosting/WorkflowRegistration.cs).
builder.AddStampdBackgroundWorkers();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

// ---- Health checks ----
// The "live" / "ready" tags are the ones the /health/live and /health/ready probes filter
// on in EndpointMapping.MapStampdEndpoints; keep the two in step.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<StampdDbContext>(
        name: "database",
        tags: ["live", "ready"])
    .AddCheck<SealingProviderHealthCheck>(
        name: "sealing-provider",
        tags: ["ready"])
    .AddCheck<TimestampAuthorityHealthCheck>(
        name: "timestamp-authority",
        failureStatus: HealthStatus.Degraded,
        tags: ["ready"]);

// ---- AuthN/AuthZ ----
// Authentication mode, startup guard and JwtBearer setup (Hosting/AuthRegistration.cs).
// Ordering invariant: authMode is the decision the startup guard enforced; the endpoint
// mapping at the bottom of this file uses it to decide whether the dev-token route exists.
var authMode = builder.AddStampdAuthentication();

// Actor context, Admin scope handler and named policies (Hosting/AuthorizationRegistration.cs).
builder.AddStampdAuthorization();

// ---- Rate limiting + request size caps (Hosting/RequestLimitsRegistration.cs) ----
// Ordering invariant: the "sign" policy registered here is applied by name in
// EndpointMapping.MapStampdEndpoints; UseRateLimiter stays after UseAuthorization below.
builder.AddStampdRequestLimits();

var app = builder.Build();

// ---- Apply migrations on startup (dev convenience) ----
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<StampdDbContext>();
    await db.Database.MigrateAsync().ConfigureAwait(false);
}

// ---- Pipeline ----
app.UseSerilogRequestLogging();
app.UseMiddleware<CorrelationIdMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.WithTitle("Stampd API");
        options.WithTheme(ScalarTheme.BluePlanet);
    });
}

// HTTPS redirection in production is typically handled at the load balancer / ingress.
// In-process redirection in the Testing environment causes a 307 that the test HttpClient
// follows, stripping the Authorization header in the process — every protected-endpoint
// test then fails with 401. Skip the middleware when running under WebApplicationFactory.
if (!app.Environment.IsEnvironment("Testing"))
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
// Between authentication and authorization: reads the principal, and
// AdminScopeAuthorizationHandler checks scope against the tenant it chooses.
// Ordering invariant documented on the other side in Hosting/TenancyRegistration.cs and
// Hosting/AuthorizationRegistration.cs.
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthorization();
// After UseAuthorization; the "sign" policy is registered in Hosting/RequestLimitsRegistration.cs.
app.UseRateLimiter();

// ---- Endpoints (Hosting/EndpointMapping.cs) ----
// Mapped after the pipeline above; group policies, rate-limit policy names and the dev-token
// route follow the registrations made before builder.Build().
app.MapStampdEndpoints(authMode);

try
{
    Log.Information("Stampd.WebApi starting on {EnvName}", app.Environment.EnvironmentName);
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Stampd.WebApi terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
