using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Stampd.Core.Entities;
using Stampd.Core.Notifications;
using Stampd.Core.Sealing;
using Stampd.Engine.Rendering;
using Stampd.Infrastructure;
using Stampd.WebApi.Models;
using Stampd.WebApi.Services;

using Xunit;

namespace Stampd.WebApi.Tests;

/// <summary>
/// Signing-request lifecycle over HTTP: closed requests stop working, expiry is
/// enforced, later signers in a sequence are emailed, bad images are refused at submit,
/// webhooks can't target internal addresses, bulk-send rows are dispatched once, and
/// the signed-document record names what actually sealed the document.
/// </summary>
public sealed class WorkflowLifecycleTests : IClassFixture<StampdWebApplicationFactory>
{
    private readonly StampdWebApplicationFactory _factory;

    public WorkflowLifecycleTests(StampdWebApplicationFactory factory)
    {
        _factory = factory;
        PlatformFontResolver.Register();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task VoidedRequest_LinkNoLongerServesOrSendsCodes()
    {
        var client = await SenderClientAsync(_factory);
        var templateId = await CreateTemplateAsync(client, ("Customer", 1));
        var (requestId, tokens) = await DispatchAsync(client, templateId, expiresAtUtc: null, ("Customer", "alice@example.com"));

        using (var scope = _factory.Services.CreateScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<SigningWorkflowService>().VoidAsync(requestId, "test", Ct));
        }

        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Gone, (await anon.GetAsync(new Uri($"/api/sign/{tokens[0]}", UriKind.Relative), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await anon.GetAsync(new Uri($"/api/sign/{tokens[0]}/document", UriKind.Relative), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await anon.PostAsync(new Uri($"/api/sign/{tokens[0]}/initiate-verification", UriKind.Relative), null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await anon.PostAsJsonAsync($"/api/sign/{tokens[0]}", TextValue("x"), Ct)).StatusCode);
    }

    [Fact]
    public async Task PastExpiry_ExpiresOnFirstUse()
    {
        var client = await SenderClientAsync(_factory);
        var templateId = await CreateTemplateAsync(client, ("Customer", 1));
        var (requestId, tokens) = await DispatchAsync(
            client, templateId, expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1), ("Customer", "alice@example.com"));

        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Gone, (await anon.GetAsync(new Uri($"/api/sign/{tokens[0]}", UriKind.Relative), Ct)).StatusCode);

        var request = JsonDocument.Parse(await client.GetStringAsync(new Uri($"/api/signing-requests/{requestId}", UriKind.Relative), Ct));
        Assert.Equal("Expired", request.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task FutureExpiry_StillSignable()
    {
        var client = await SenderClientAsync(_factory);
        var templateId = await CreateTemplateAsync(client, ("Customer", 1));
        var (_, tokens) = await DispatchAsync(
            client, templateId, expiresAtUtc: DateTimeOffset.UtcNow.AddDays(1), ("Customer", "alice@example.com"));

        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync(new Uri($"/api/sign/{tokens[0]}", UriKind.Relative), Ct)).StatusCode);
    }

    [Fact]
    public async Task Sequence_NextSignerIsEmailedWhenTheirTurnComes()
    {
        var mail = new CapturingEmailSender();
        await using var factory = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Stampd:Workflow:Email:SigningUrlTemplate", "https://sign.example.test/{accessToken}");
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IEmailSender>();
                s.AddSingleton<IEmailSender>(mail);
            });
        });

        var client = await SenderClientAsync(factory);
        var templateId = await CreateTemplateAsync(client, ("First", 1), ("Second", 2));
        var (_, tokens) = await DispatchAsync(
            client, templateId, expiresAtUtc: null, ("First", "first@example.com"), ("Second", "second@example.com"));

        Assert.Equal(["first@example.com"], mail.Recipients());

        var anon = factory.CreateClient();
        var submit = await anon.PostAsJsonAsync($"/api/sign/{tokens[0]}", TextValue("first"), Ct);
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

        Assert.Equal(["first@example.com", "second@example.com"], mail.Recipients());
    }

    [Fact]
    public async Task CompletedRequest_RecordsTheProviderAndKeyThatSealedIt()
    {
        var client = await SenderClientAsync(_factory);
        var templateId = await CreateTemplateAsync(client, ("Customer", 1));
        var (requestId, tokens) = await DispatchAsync(client, templateId, expiresAtUtc: null, ("Customer", "alice@example.com"));

        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anon.PostAsJsonAsync($"/api/sign/{tokens[0]}", TextValue("Alice"), Ct)).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var record = await services.GetRequiredService<StampdDbContext>().SignedDocumentRecords
            .IgnoreQueryFilters()
            .SingleAsync(r => r.SigningRequestId == requestId, Ct);
        var sealing = services.GetRequiredService<ICryptographicSealingProvider>();
        var certificate = await sealing.GetSigningCertificateAsync(Ct);

        Assert.Equal(sealing.Name, record.SealingProviderName);
        Assert.Equal(certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256), record.SealingKeyIdentifier);
        // The test host has no TSA, so the configured B-T target falls back to B-B.
        Assert.Equal(PAdESLevel.BB, record.PAdESLevel);
        Assert.Null(record.TimestampAuthorityUrl);
    }

    [Fact]
    public async Task CompletedRequest_SenderDownloadsTheSignedPdfById()
    {
        var client = await SenderClientAsync(_factory);
        var templateId = await CreateTemplateAsync(client, ("Customer", 1));
        var (requestId, tokens) = await DispatchAsync(client, templateId, expiresAtUtc: null, ("Customer", "alice@example.com"));

        var before = JsonDocument.Parse(await client.GetStringAsync(new Uri($"/api/signing-requests/{requestId}", UriKind.Relative), Ct)).RootElement;
        Assert.Equal(JsonValueKind.Null, before.GetProperty("signedDocumentId").ValueKind);

        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anon.PostAsJsonAsync($"/api/sign/{tokens[0]}", TextValue("Alice"), Ct)).StatusCode);

        var detail = JsonDocument.Parse(await client.GetStringAsync(new Uri($"/api/signing-requests/{requestId}", UriKind.Relative), Ct)).RootElement;
        var documentId = detail.GetProperty("signedDocumentId").GetGuid();

        var list = JsonDocument.Parse(await client.GetStringAsync(new Uri("/api/signing-requests?pageSize=200", UriKind.Relative), Ct)).RootElement;
        var row = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == requestId);
        Assert.Equal(documentId, row.GetProperty("signedDocumentId").GetGuid());

        using var download = await client.GetAsync(new Uri($"/api/signed-documents/{documentId}/download", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var pdf = await download.Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal("%PDF"u8.ToArray(), pdf[..4]);
    }

    [Fact]
    public async Task InvalidSignatureImage_IsRefusedAtSubmit_AndRequestStaysOpen()
    {
        var client = await SenderClientAsync(_factory);
        var templateId = await CreateTemplateAsync(client, ("Customer", 1));
        var (_, tokens) = await DispatchAsync(client, templateId, expiresAtUtc: null, ("Customer", "alice@example.com"));

        var anon = _factory.CreateClient();
        var bad = await anon.PostAsJsonAsync(
            $"/api/sign/{tokens[0]}",
            new { fieldValues = new Dictionary<string, object> { ["0"] = new { imageBase64 = "aGVsbG8=" } } },
            Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var good = await anon.PostAsJsonAsync($"/api/sign/{tokens[0]}", TextValue("Alice"), Ct);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
    }

    [Theory]
    [InlineData("http://127.0.0.1:9/hook")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[::1]/hook")]
    [InlineData("http://10.0.0.5/hook")]
    public async Task Webhook_InternalTarget_IsRefused(string url)
    {
        var client = await SenderClientAsync(_factory);
        var response = await client.PostAsJsonAsync("/api/webhooks", new { url, subscribedEvents = "" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BulkSend_RowDispatchedBeforeACrash_IsNotDispatchedAgain()
    {
        var client = await SenderClientAsync(_factory);
        var templateId = await CreateTemplateAsync(client, ("Customer", 1));
        var jobId = Guid.NewGuid();
        var rows = new[]
        {
            new BulkSendRow([new BulkSendRecipientAssignment("Customer", "row0@example.com", "Row Zero")]),
            new BulkSendRow([new BulkSendRecipientAssignment("Customer", "row1@example.com", "Row One")]),
        };

        // Simulate a crash after row 0's request was committed but before the job's
        // progress was: the request exists, the job still lists both rows as pending.
        using (var scope = _factory.Services.CreateScope())
        {
            var workflow = scope.ServiceProvider.GetRequiredService<SigningWorkflowService>();
            await workflow.DispatchAsync(
                new CreateSigningRequestBody(templateId, "Bulk", null, null,
                    [new RecipientAssignment("Customer", "row0@example.com", "Row Zero")]),
                Ct,
                (jobId, 0));

            var db = scope.ServiceProvider.GetRequiredService<StampdDbContext>();
            db.BulkSendJobs.Add(new BulkSendJob
            {
                Id = jobId,
                DocumentTemplateId = templateId,
                Status = BulkSendJobStatus.InProgress,
                TotalRows = 2,
                Subject = "Bulk",
                PendingRowsJson = JsonSerializer.Serialize(rows),
            });
            await db.SaveChangesAsync(Ct);
        }

        var job = await WaitForJobAsync(jobId);

        Assert.Equal(BulkSendJobStatus.Completed, job.Status);
        Assert.Equal(2, job.CompletedRows);
        using var check = _factory.Services.CreateScope();
        var requests = await check.ServiceProvider.GetRequiredService<StampdDbContext>().SigningRequests
            .Where(r => r.BulkSendJobId == jobId)
            .Select(r => r.BulkSendRowIndex)
            .ToListAsync(Ct);
        Assert.Equal([0, 1], requests.Order());
    }

    // ---- Helpers ----

    private async Task<BulkSendJob> WaitForJobAsync(Guid jobId)
    {
        // The bulk worker polls every 2 seconds; allow several polls.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            using var scope = _factory.Services.CreateScope();
            var job = await scope.ServiceProvider.GetRequiredService<StampdDbContext>().BulkSendJobs
                .AsNoTracking()
                .FirstAsync(j => j.Id == jobId, Ct);
            if (job.Status is not (BulkSendJobStatus.Pending or BulkSendJobStatus.InProgress) || DateTimeOffset.UtcNow > deadline)
            {
                return job;
            }

            await Task.Delay(250, Ct);
        }
    }

    private static object TextValue(string text)
        => new { fieldValues = new Dictionary<string, object> { ["0"] = new { text } } };

    private static async Task<HttpClient> SenderClientAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var token = await AuthEndpointTests.IssueDevTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<Guid> CreateTemplateAsync(HttpClient client, params (string Role, int Order)[] roles)
    {
        var response = await client.PostAsJsonAsync("/api/templates", new
        {
            name = "Lifecycle Test Contract",
            sourcePdfBase64 = Convert.ToBase64String(WorkflowHappyPathTests.BuildSyntheticPdf()),
            roles = roles.Select(r => new { name = r.Role, routingOrder = r.Order }).ToArray(),
            fields = roles.Select((r, i) => new
            {
                pageNumber = 1,
                bounds = new { x = 10.0, y = 70.0 + (i * 10), width = 40.0, height = 5.0 },
                kind = "Text",
                assignedRoleName = r.Role,
            }).ToArray(),
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<(Guid RequestId, string[] Tokens)> DispatchAsync(
        HttpClient client,
        Guid templateId,
        DateTimeOffset? expiresAtUtc,
        params (string Role, string Email)[] recipients)
    {
        var response = await client.PostAsJsonAsync("/api/signing-requests", new
        {
            documentTemplateId = templateId,
            subject = "Please sign",
            expiresAtUtc,
            recipients = recipients.Select(r => new { roleName = r.Role, email = r.Email, name = r.Email }).ToArray(),
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        var tokens = root.GetProperty("recipients").EnumerateArray()
            .OrderBy(r => r.GetProperty("routingOrder").GetInt32())
            .Select(r => r.GetProperty("accessUrl").GetString()!)
            .Select(url => url[(url.LastIndexOf('/') + 1)..])
            .ToArray();
        return (root.GetProperty("id").GetGuid(), tokens);
    }

    private sealed class CapturingEmailSender : IEmailSender
    {
        private readonly ConcurrentQueue<string> _to = new();

        public string Name => "capture";

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            foreach (var to in message.To)
            {
                _to.Enqueue(to.Address);
            }

            return Task.CompletedTask;
        }

        public string[] Recipients() => [.. _to];
    }
}
