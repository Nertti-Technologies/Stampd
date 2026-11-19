using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Stampd.WebApi.Tests;

/// <summary>
/// Tenant resolution, role checks on write routes, and who may see recipient access URLs.
/// </summary>
public sealed class AuthorizationModelTests
{
    private const string TenantA = "0000000a-0000-0000-0000-00000000000a";
    private const string TenantB = "0000000b-0000-0000-0000-00000000000b";
    private const string DefaultTenant = "00000000-0000-0000-0000-000000000001";
    private const string SuperAdmin = "root@test";

    // ---- Tenant resolution ----

    [Fact]
    public async Task Sender_SeesOnlyTheTenantInTheirToken()
    {
        using var factory = new StampdWebApplicationFactory();
        var client = factory.CreateClient();
        var senderA = await TokenAsync(client, "a@test", TenantA, "Sender");
        var senderB = await TokenAsync(client, "b@test", TenantB, "Sender");

        var templateId = await CreateTemplateAsync(client, senderA);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, $"/api/templates/{templateId}", senderA)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, HttpMethod.Get, $"/api/templates/{templateId}", senderB)).StatusCode);
    }

    [Fact]
    public async Task NonAdmin_HeaderNamingAnotherTenant_IsForbidden()
    {
        using var factory = new StampdWebApplicationFactory();
        var client = factory.CreateClient();
        var senderB = await TokenAsync(client, "b@test", TenantB, "Sender");

        var response = await SendAsync(client, HttpMethod.Get, "/api/templates", senderB, tenantHeader: TenantA);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task HeaderMatchingTheToken_IsAccepted()
    {
        using var factory = new StampdWebApplicationFactory();
        var client = factory.CreateClient();
        var senderA = await TokenAsync(client, "a@test", TenantA, "Sender");

        var response = await SendAsync(client, HttpMethod.Get, "/api/templates", senderA, tenantHeader: TenantA);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("not-a-guid")]
    public async Task InvalidOrAllTenantsHeader_IsRejected(string header)
    {
        using var factory = new StampdWebApplicationFactory();
        var client = factory.CreateClient();
        var admin = await TokenAsync(client, SuperAdmin, TenantA, "Admin");

        var response = await SendAsync(client, HttpMethod.Get, "/api/templates", admin, tenantHeader: header);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AllTenantsHeader_IsRejectedForAnonymousRequestsToo()
    {
        using var factory = new StampdWebApplicationFactory();
        var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/sign/{new string('a', 64)}");
        request.Headers.Add("X-Stampd-Tenant", Guid.Empty.ToString());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task TokenWithoutTenant_SingleMode_UsesDefaultTenant()
    {
        using var factory = new StampdWebApplicationFactory();
        var client = factory.CreateClient();
        var noTenant = await TokenAsync(client, "a@test", tenantId: null, "Sender");
        var defaultSender = await TokenAsync(client, "d@test", DefaultTenant, "Sender");

        var templateId = await CreateTemplateAsync(client, noTenant);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, $"/api/templates/{templateId}", defaultSender)).StatusCode);
    }

    [Fact]
    public async Task TokenWithoutTenant_MultiMode_IsForbidden()
    {
        using var factory = new ScopedStampdWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Stampd:Tenancy:Mode"] = "Multi",
        });
        var client = factory.CreateClient();
        var noTenant = await TokenAsync(client, "a@test", tenantId: null, "Sender");

        var response = await SendAsync(client, HttpMethod.Get, "/api/templates", noTenant);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Admin_MaySwitchOnlyToTenantsTheyAreScopedTo()
    {
        using var factory = new ScopedStampdWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Stampd:Auth:SuperAdminUserIds"] = SuperAdmin,
        });
        var client = factory.CreateClient();
        var root = await TokenAsync(client, SuperAdmin, TenantB, "Admin");
        var alice = await TokenAsync(client, "alice@test", TenantB, "Admin");

        // Before any grant, alice (home tenant B) cannot act in tenant A.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Get, "/api/templates", alice, tenantHeader: TenantA)).StatusCode);

        // A super-admin may switch anywhere, and grants alice a scope on tenant A.
        using var grant = await SendAsync(client, HttpMethod.Post, "/api/admin/scopes", root, tenantHeader: TenantA, body: new { userId = "alice@test" });
        Assert.Equal(HttpStatusCode.Created, grant.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/api/templates", alice, tenantHeader: TenantA)).StatusCode);
    }

    // ---- Role checks ----

    public static TheoryData<string, string> WriteRoutes => new()
    {
        { "POST", "/api/templates" },
        { "PUT", "/api/templates/{id}" },
        { "POST", "/api/templates/{id}/sign-immediate" },
        { "POST", "/api/signing-requests" },
        { "POST", "/api/templates/{id}/bulk-send" },
        { "POST", "/api/webhooks" },
        { "DELETE", "/api/webhooks/{id}" },
    };

    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task ReadOnly_IsForbiddenOnWriteRoutes(string method, string route)
    {
        using var factory = new StampdWebApplicationFactory();
        var client = factory.CreateClient();
        var readOnly = await TokenAsync(client, "r@test", TenantA, "ReadOnly");

        var path = route.Replace("{id}", Guid.NewGuid().ToString(), StringComparison.Ordinal);
        var response = await SendAsync(client, new HttpMethod(method), path, readOnly, body: new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ReadOnly_MayStillRead()
    {
        using var factory = new StampdWebApplicationFactory();
        var client = factory.CreateClient();
        var readOnly = await TokenAsync(client, "r@test", TenantA, "ReadOnly");

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/api/templates", readOnly)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/api/signing-requests", readOnly)).StatusCode);
    }

    /// <summary>
    /// Every route that changes data must carry a role policy, unless it sits under a
    /// prefix that is anonymous by design (recipient signing behind its access token,
    /// auth protocol endpoints, demo seed). A new write route
    /// mapped without one fails here.
    /// </summary>
    [Fact]
    public void EveryWriteRoute_RequiresARolePolicyOrIsDeliberatelyAnonymous()
    {
        using var factory = new StampdWebApplicationFactory();
        _ = factory.CreateClient();

        string[] anonymousPrefixes = ["/api/sign/{accessToken}", "/api/auth/", "/api/demo/"];
        string[] rolePolicies = ["SenderOrAdmin", "Admin"];

        var unprotected = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                .Any(m => m is "POST" or "PUT" or "PATCH" or "DELETE") == true)
            .Where(e =>
            {
                var route = "/" + (e.RoutePattern.RawText ?? string.Empty).TrimStart('/');
                if (anonymousPrefixes.Any(p => route.StartsWith(p, StringComparison.Ordinal)))
                {
                    return false;
                }

                return e.Metadata.GetMetadata<IAllowAnonymous>() is not null
                    || !e.Metadata.GetOrderedMetadata<IAuthorizeData>()
                    .Any(a => rolePolicies.Contains(a.Policy) || !string.IsNullOrEmpty(a.Roles));
            })
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        Assert.Empty(unprotected);
    }

    // ---- Access URL visibility ----

    [Fact]
    public async Task AccessUrls_VisibleOnlyToTheOwnerAndAdmins()
    {
        using var factory = new ScopedStampdWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Stampd:Auth:SuperAdminUserIds"] = SuperAdmin,
        });
        var client = factory.CreateClient();
        var owner = await TokenAsync(client, "owner@test", TenantA, "Sender");
        var colleague = await TokenAsync(client, "colleague@test", TenantA, "Sender");
        var readOnly = await TokenAsync(client, "r@test", TenantA, "ReadOnly");
        var admin = await TokenAsync(client, SuperAdmin, TenantA, "Admin");

        var templateId = await CreateTemplateAsync(client, owner);
        using var created = await SendAsync(client, HttpMethod.Post, "/api/signing-requests", owner, body: new
        {
            documentTemplateId = templateId,
            subject = "Please sign",
            recipients = new[] { new { roleName = "Customer", email = "alice@example.com", name = "Alice" } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(FirstAccessUrl(createdDoc.RootElement));
        var requestId = createdDoc.RootElement.GetProperty("id").GetGuid();

        Assert.NotNull(await AccessUrlSeenByAsync(client, requestId, owner));
        Assert.NotNull(await AccessUrlSeenByAsync(client, requestId, admin));
        Assert.Null(await AccessUrlSeenByAsync(client, requestId, colleague));
        Assert.Null(await AccessUrlSeenByAsync(client, requestId, readOnly));

        // The list endpoint follows the same rule.
        using var list = await SendAsync(client, HttpMethod.Get, "/api/signing-requests", colleague);
        var items = JsonDocument.Parse(await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .RootElement.GetProperty("items");
        Assert.All(items.EnumerateArray(), item => Assert.Null(FirstAccessUrl(item)));
    }

    [Fact]
    public async Task RequestOwner_ComesFromTheTokenNotTheBody()
    {
        using var factory = new StampdWebApplicationFactory();
        var client = factory.CreateClient();
        var owner = await TokenAsync(client, "owner@test", TenantA, "Sender");
        var other = await TokenAsync(client, "other@test", TenantA, "Sender");

        var templateId = await CreateTemplateAsync(client, owner);

        // Naming someone else as SenderEmail does not make them the owner.
        using var created = await SendAsync(client, HttpMethod.Post, "/api/signing-requests", owner, body: new
        {
            documentTemplateId = templateId,
            subject = "Please sign",
            senderEmail = "other@test",
            recipients = new[] { new { roleName = "Customer", email = "alice@example.com", name = "Alice" } },
        });
        var requestId = JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .RootElement.GetProperty("id").GetGuid();

        Assert.NotNull(await AccessUrlSeenByAsync(client, requestId, owner));
        Assert.Null(await AccessUrlSeenByAsync(client, requestId, other));
    }

    // ---- Helpers ----

    private static async Task<string?> AccessUrlSeenByAsync(HttpClient client, Guid requestId, string token)
    {
        using var response = await SendAsync(client, HttpMethod.Get, $"/api/signing-requests/{requestId}", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return FirstAccessUrl(doc.RootElement);
    }

    private static string? FirstAccessUrl(JsonElement request)
        => request.GetProperty("recipients")[0].GetProperty("accessUrl").GetString();

    private static async Task<Guid> CreateTemplateAsync(HttpClient client, string token)
    {
        using var response = await SendAsync(client, HttpMethod.Post, "/api/templates", token, body: new
        {
            name = "Authorization Test Contract",
            sourcePdfBase64 = Convert.ToBase64String(WorkflowHappyPathTests.BuildSyntheticPdf()),
            roles = new[] { new { name = "Customer", routingOrder = 1 } },
            fields = new[]
            {
                new
                {
                    pageNumber = 1,
                    bounds = new { x = 10.0, y = 80.0, width = 40.0, height = 5.0 },
                    kind = "Text",
                    assignedRoleName = "Customer",
                },
            },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<string> TokenAsync(HttpClient client, string subject, string? tenantId, params string[] roles)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/auth/dev-token",
            new { subject, tenantId, roles },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return doc.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string token,
        string? tenantHeader = null,
        object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (tenantHeader is not null)
        {
            request.Headers.Add("X-Stampd-Tenant", tenantHeader);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
