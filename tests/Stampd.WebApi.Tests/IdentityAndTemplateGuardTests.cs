using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Stampd.Core.Notifications;
using Stampd.Engine.Rendering;
using Stampd.WebApi.Services;

using Xunit;

namespace Stampd.WebApi.Tests;

/// <summary>
/// An identity-check code verifies only the recipient it was issued to, and a template's
/// roles and fields can't change while a request using it is still out for signature.
/// </summary>
public sealed partial class IdentityAndTemplateGuardTests : IClassFixture<StampdWebApplicationFactory>
{
    private readonly StampdWebApplicationFactory _factory;

    public IdentityAndTemplateGuardTests(StampdWebApplicationFactory factory)
    {
        _factory = factory;
        PlatformFontResolver.Register();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CodeIssuedToAnotherRecipient_DoesNotVerify()
    {
        var mail = new CodeCapturingEmailSender();
        await using var factory = WithMail(mail);
        var client = await SenderClientAsync(factory);
        var templateId = await CreateTemplateAsync(client);
        var victim = await DispatchAsync(client, templateId, "victim@example.com");
        var attacker = await DispatchAsync(client, templateId, "attacker@example.com");
        var anon = factory.CreateClient();

        // The attacker requests a code on their own link, so it arrives in their inbox,
        // then presents it on the victim's link.
        var attackerChallenge = await InitiateAsync(anon, attacker);
        var refused = await VerifyAsync(anon, victim, attackerChallenge, mail.LatestCodeFor("attacker@example.com"));
        Assert.False(refused.GetProperty("succeeded").GetBoolean());

        var victimChallenge = await InitiateAsync(anon, victim);
        var accepted = await VerifyAsync(anon, victim, victimChallenge, mail.LatestCodeFor("victim@example.com"));
        Assert.True(accepted.GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public async Task NewChallenge_ReplacesTheEarlierOne()
    {
        var mail = new CodeCapturingEmailSender();
        await using var factory = WithMail(mail);
        var client = await SenderClientAsync(factory);
        var templateId = await CreateTemplateAsync(client);
        var token = await DispatchAsync(client, templateId, "signer@example.com");
        var anon = factory.CreateClient();

        var first = await InitiateAsync(anon, token);
        var firstCode = mail.LatestCodeFor("signer@example.com");
        var second = await InitiateAsync(anon, token);

        Assert.False((await VerifyAsync(anon, token, first, firstCode)).GetProperty("succeeded").GetBoolean());
        Assert.True((await VerifyAsync(anon, token, second, mail.LatestCodeFor("signer@example.com"))).GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public async Task TemplateWithRequestInFlight_KeepsItsLayout()
    {
        var client = await SenderClientAsync(_factory);
        var templateId = await CreateTemplateAsync(client);
        var requestId = await DispatchRequestAsync(client, templateId, "signer@example.com");
        var token = requestId.Token;

        var moved = await client.PutAsJsonAsync($"/api/templates/{templateId}", TemplateBody("Renamed", fieldY: 80.0), Ct);
        Assert.Equal(HttpStatusCode.Conflict, moved.StatusCode);

        var renamed = await client.PutAsJsonAsync($"/api/templates/{templateId}", TemplateBody("Renamed", fieldY: 70.0), Ct);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        // The rename kept the role, so the signer still has to verify their identity.
        var view = JsonDocument.Parse(await _factory.CreateClient().GetStringAsync(new Uri($"/api/sign/{token}", UriKind.Relative), Ct));
        Assert.True(view.RootElement.GetProperty("requiresIdentityVerification").GetBoolean());

        using (var scope = _factory.Services.CreateScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<SigningWorkflowService>().VoidAsync(requestId.RequestId, "test", Ct));
        }

        var afterVoid = await client.PutAsJsonAsync($"/api/templates/{templateId}", TemplateBody("Renamed", fieldY: 80.0), Ct);
        Assert.Equal(HttpStatusCode.OK, afterVoid.StatusCode);
    }

    private WebApplicationFactory<Program> WithMail(CodeCapturingEmailSender mail)
        => _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IEmailSender>();
            s.AddSingleton<IEmailSender>(mail);
        }));

    private static async Task<HttpClient> SenderClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var token = await AuthEndpointTests.IssueDevTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static object TemplateBody(string name, double fieldY) => new
    {
        name,
        sourcePdfBase64 = Convert.ToBase64String(WorkflowHappyPathTests.BuildSyntheticPdf()),
        roles = new[] { new { name = "Customer", routingOrder = 1, requiresIdentityVerification = true } },
        fields = new[]
        {
            new
            {
                pageNumber = 1,
                bounds = new { x = 10.0, y = fieldY, width = 40.0, height = 5.0 },
                kind = "Text",
                assignedRoleName = "Customer",
            },
        },
    };

    private static async Task<Guid> CreateTemplateAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/templates", TemplateBody("Identity Guard Test", fieldY: 70.0), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<string> DispatchAsync(HttpClient client, Guid templateId, string email)
        => (await DispatchRequestAsync(client, templateId, email)).Token;

    private static async Task<(Guid RequestId, string Token)> DispatchRequestAsync(HttpClient client, Guid templateId, string email)
    {
        var response = await client.PostAsJsonAsync("/api/signing-requests", new
        {
            documentTemplateId = templateId,
            subject = "Please sign",
            recipients = new[] { new { roleName = "Customer", email, name = email } },
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        var url = root.GetProperty("recipients")[0].GetProperty("accessUrl").GetString()!;
        return (root.GetProperty("id").GetGuid(), url[(url.LastIndexOf('/') + 1)..]);
    }

    private static async Task<string> InitiateAsync(HttpClient anon, string token)
    {
        var response = await anon.PostAsync(new Uri($"/api/sign/{token}/initiate-verification", UriKind.Relative), null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("verificationId").GetString()!;
    }

    private static async Task<JsonElement> VerifyAsync(HttpClient anon, string token, string verificationId, string code)
    {
        var response = await anon.PostAsJsonAsync($"/api/sign/{token}/verify-identity", new { verificationId, code }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    [GeneratedRegex(@"\b(\d{6})\b")]
    private static partial Regex SixDigitCode();

    private sealed class CodeCapturingEmailSender : IEmailSender
    {
        private readonly ConcurrentQueue<(string To, string Subject)> _sent = new();

        public string Name => "capture";

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            foreach (var to in message.To)
            {
                _sent.Enqueue((to.Address, message.Subject));
            }

            return Task.CompletedTask;
        }

        public string LatestCodeFor(string address)
            => _sent
                .Where(m => m.To == address && SixDigitCode().IsMatch(m.Subject))
                .Select(m => SixDigitCode().Match(m.Subject).Groups[1].Value)
                .Last();
    }
}
