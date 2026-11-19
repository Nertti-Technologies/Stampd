using Stampd.WebApi.Services;

namespace Stampd.WebApi.Hosting;

internal static class WorkflowRegistration
{
    /// <summary>
    /// Registers the signing workflow services and the workflow email options.
    /// </summary>
    public static void AddStampdWorkflow(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<SigningWorkflowService>();
        builder.Services.AddScoped<WebhookDispatcher>();
        // v1.3 #134 — completion email notifier. Singleton because it holds no per-request state;
        // the workflow service captures it via constructor injection alongside the existing
        // IEmailSender / WorkflowEmailOptions surface.
        builder.Services.AddSingleton<SenderCompletionNotifier>();

        // Workflow email defaults — pulled from config so adopters can flip the dispatch path
        // on by setting Stampd:Workflow:Email:SigningUrlTemplate without changing code.
        // SigningUrlTemplate default: in Development point at the local Blazor UI's signer page
        // (5170/sign/{token}), so the access URLs the API returns are clickable without any extra
        // config. Adopters override Stampd:Workflow:Email:SigningUrlTemplate for production.
        var defaultSigningUrlTemplate = builder.Environment.IsDevelopment()
            ? "http://localhost:5170/sign/{accessToken}"
            : null;

        // SignedDocumentUrlTemplate default (v1.3 #134): point senders at the Blazor UI's signed
        // documents listing in Development, so the completion email's CTA is clickable end-to-end
        // out of the box. Adopters override Stampd:Workflow:Email:SignedDocumentUrlTemplate for
        // production deployments.
        var defaultSignedDocumentUrlTemplate = builder.Environment.IsDevelopment()
            ? "http://localhost:5170/signed/{signedDocumentId}"
            : null;

        builder.Services.AddSingleton(new WorkflowEmailOptions
        {
            FromAddress = builder.Configuration["Stampd:Workflow:Email:FromAddress"] ?? "noreply@stampd.local",
            FromDisplayName = builder.Configuration["Stampd:Workflow:Email:FromDisplayName"] ?? "Stampd",
            SigningUrlTemplate = builder.Configuration["Stampd:Workflow:Email:SigningUrlTemplate"] ?? defaultSigningUrlTemplate,
            SignedDocumentUrlTemplate = builder.Configuration["Stampd:Workflow:Email:SignedDocumentUrlTemplate"] ?? defaultSignedDocumentUrlTemplate,
            ProductName = builder.Configuration["Stampd:Workflow:Email:ProductName"] ?? "Stampd",
        });
    }

    /// <summary>
    /// Registers the background workers and the webhook delivery HTTP client.
    /// </summary>
    public static void AddStampdBackgroundWorkers(this WebApplicationBuilder builder)
    {
        // Background workers: bulk-send dispatcher and webhook delivery outbox drainer. Both
        // poll the DB; both gracefully cross-tenant via TenantScope.
        builder.Services.AddHostedService<BulkSendWorker>();
        builder.Services.AddHostedService<WebhookDeliveryWorker>();
        builder.Services.AddOptions<WebhookTargetOptions>().Bind(builder.Configuration.GetSection("Stampd:Webhooks"));
        builder.Services.AddSingleton<WebhookTargetPolicy>();
        // Every connection the delivery worker opens goes through WebhookTargetPolicy, so a
        // webhook can't reach internal addresses even if its host's DNS changes after
        // registration. Redirects are not followed: a redirect could lead to an internal address.
        builder.Services.AddHttpClient(nameof(WebhookDeliveryWorker))
            .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = sp.GetRequiredService<WebhookTargetPolicy>().ConnectAsync,
            });
    }
}
