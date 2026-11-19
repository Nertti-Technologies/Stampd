using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Stampd.Engine.Rendering;

using Xunit;

namespace Stampd.WebApi.Tests;

/// <summary>
/// Endpoints that filter or order by time. The test host runs on SQLite, which cannot
/// compare or sort <see cref="DateTimeOffset"/> columns in SQL, so these guard the
/// queries that must use the epoch-millisecond columns or sort in memory.
/// </summary>
public sealed class TimeWindowQueryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TimeWindowQueryTests() => PlatformFontResolver.Register();

    [Fact]
    public async Task AuditExport_FiltersByTimeWindow()
    {
        await using var factory = AdminFactory();
        var client = await AdminClientAsync(factory);
        var requestId = await DispatchAsync(client);

        var recent = await client.GetAsync(ExportUri(DateTimeOffset.UtcNow.AddDays(-1)), Ct);
        Assert.Equal(HttpStatusCode.OK, recent.StatusCode);
        Assert.Contains(requestId.ToString(), await recent.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var future = await client.GetAsync(ExportUri(DateTimeOffset.UtcNow.AddDays(1)), Ct);
        Assert.Equal(HttpStatusCode.OK, future.StatusCode);
        var lines = (await future.Content.ReadAsStringAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines); // header only
    }

    [Theory]
    [InlineData("/api/admin/analytics/identity-verification?days=30")]
    [InlineData("/api/admin/analytics/webhooks-health")]
    [InlineData("/api/webhooks")]
    public async Task TimeOrderedEndpoint_Responds(string path)
    {
        await using var factory = AdminFactory();
        var client = await AdminClientAsync(factory);

        var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RequestAudit_ListsEventsOldestFirst()
    {
        await using var factory = AdminFactory();
        var client = await AdminClientAsync(factory);
        var requestId = await DispatchAsync(client);

        var response = await client.GetAsync(new Uri($"/api/signing-requests/{requestId}/audit", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var times = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement
            .GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("occurredAtUtc").GetDateTimeOffset())
            .ToList();
        Assert.NotEmpty(times);
        Assert.Equal(times.Order().ToList(), times);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("+1", "\"'+1\"")]
    [InlineData("-1", "\"'-1\"")]
    [InlineData("@SUM(A1)", "\"'@SUM(A1)\"")]
    [InlineData("Mozilla/5.0", "\"Mozilla/5.0\"")]
    public void AuditExport_FormulaLikeCells_AreWrittenAsText(string value, string expected)
        => Assert.Equal(expected, Endpoints.AdminAuditExportEndpoints.CsvField(value));

    private static ScopedStampdWebApplicationFactory AdminFactory()
        => new(new Dictionary<string, string?> { ["Stampd:Auth:SuperAdminUserIds"] = "test@example.com" });

    private static Uri ExportUri(DateTimeOffset from)
        => new($"/api/admin/audit/export?from={Uri.EscapeDataString(from.ToString("O"))}", UriKind.Relative);

    private static async Task<HttpClient> AdminClientAsync(ScopedStampdWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        var token = await AuthEndpointTests.IssueDevTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<Guid> DispatchAsync(HttpClient client)
    {
        var template = await client.PostAsJsonAsync("/api/templates", new
        {
            name = "Time Window Test",
            sourcePdfBase64 = Convert.ToBase64String(WorkflowHappyPathTests.BuildSyntheticPdf()),
            roles = new[] { new { name = "Customer", routingOrder = 1 } },
            fields = new[]
            {
                new
                {
                    pageNumber = 1,
                    bounds = new { x = 10.0, y = 70.0, width = 40.0, height = 5.0 },
                    kind = "Text",
                    assignedRoleName = "Customer",
                },
            },
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, template.StatusCode);
        var templateId = JsonDocument.Parse(await template.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("id").GetGuid();

        var request = await client.PostAsJsonAsync("/api/signing-requests", new
        {
            documentTemplateId = templateId,
            subject = "Please sign",
            recipients = new[] { new { roleName = "Customer", email = "alice@example.com", name = "Alice" } },
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, request.StatusCode);
        return JsonDocument.Parse(await request.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("id").GetGuid();
    }
}
