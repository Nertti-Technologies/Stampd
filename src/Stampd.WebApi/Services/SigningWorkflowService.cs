using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Stampd.Core;
using Stampd.Core.Entities;
using Stampd.Core.Notifications;
using Stampd.Core.Sealing;
using Stampd.Core.Storage;
using Stampd.Infrastructure;
using Stampd.WebApi.Models;

namespace Stampd.WebApi.Services;

/// <summary>
/// Orchestrates the lifecycle of a <see cref="SigningRequest"/> — dispatch, recipient
/// submission, finalization. Sealed application service that owns the state machine
/// transitions so endpoints stay thin.
/// </summary>
/// <remarks>
/// v1 simplifications: every recipient signs all fields they're assigned (no per-field
/// optional/required nuance beyond what's already on the template). When all required
/// recipients have signed, the final PDF is rendered and persisted in one shot.
/// </remarks>
public sealed partial class SigningWorkflowService
{
    private readonly StampdDbContext _db;
    private readonly IDocumentStorageProvider _storage;
    private readonly IStampdEngine _engine;
    private readonly PadesDefaults _padesDefaults;
    private readonly IEmailSender? _emailSender;
    private readonly WorkflowEmailOptions? _emailOptions;
    private readonly WebhookDispatcher? _webhookDispatcher;
    private readonly SenderCompletionNotifier? _senderCompletionNotifier;
    private readonly Core.Authorization.ICurrentActorContext? _actorContext;
    private readonly ICryptographicSealingProvider? _sealingProvider;
    private readonly ITimestampAuthorityProvider? _timestampAuthority;
    private readonly ILogger<SigningWorkflowService> _logger;

    public SigningWorkflowService(
        StampdDbContext db,
        IDocumentStorageProvider storage,
        IStampdEngine engine,
        PadesDefaults padesDefaults,
        IEmailSender? emailSender = null,
        WorkflowEmailOptions? emailOptions = null,
        WebhookDispatcher? webhookDispatcher = null,
        SenderCompletionNotifier? senderCompletionNotifier = null,
        Core.Authorization.ICurrentActorContext? actorContext = null,
        ILogger<SigningWorkflowService>? logger = null,
        ICryptographicSealingProvider? sealingProvider = null,
        ITimestampAuthorityProvider? timestampAuthority = null)
    {
        _db = db;
        _storage = storage;
        _engine = engine;
        _padesDefaults = padesDefaults;
        _emailSender = emailSender;
        _emailOptions = emailOptions;
        _webhookDispatcher = webhookDispatcher;
        _senderCompletionNotifier = senderCompletionNotifier;
        // v2.0 Slice D — actor context for audit attribution. Optional so test fixtures
        // and pre-v2.0 wiring keep compiling; null means "system actor" — audit rows
        // get null ActorUserId/ActorRole, same as pre-v2.0 rows.
        _actorContext = actorContext;
        _logger = logger ?? NullLogger<SigningWorkflowService>.Instance;
        // The same providers the engine is built from (Program.cs), read here only to
        // record what sealed each document.
        _sealingProvider = sealingProvider;
        _timestampAuthority = timestampAuthority;
    }

    /// <summary>Creates a new SigningRequest from a template + recipient assignments, marks it Sent.</summary>
    /// <param name="body">The request to create.</param>
    /// <param name="bulkSendRow">
    /// The bulk-send job and row this request comes from, stored on the request so the
    /// row is never dispatched twice. Null outside bulk send.
    /// </param>
    /// <param name="ct"></param>
    public async Task<SigningRequest> DispatchAsync(
        CreateSigningRequestBody body,
        CancellationToken ct,
        (Guid JobId, int RowIndex)? bulkSendRow = null)
    {
        var template = await _db.DocumentTemplates
            .Include(t => t.Roles)
            .Include(t => t.Fields)
            .FirstOrDefaultAsync(t => t.Id == body.DocumentTemplateId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Template '{body.DocumentTemplateId}' not found.");

        var rolesByName = template.Roles.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;

        // CreatedBy is the signed-in caller (the token's subject); it decides who may see
        // recipient access URLs. Work without a signed-in user (bulk send) records "api".
        // SenderEmail only says where the completion notification goes.
        var senderEmail = body.SenderEmail?.Trim();
        var createdBy = _actorContext is { IsAuthenticated: true, UserId: { Length: > 0 } userId } ? userId : "api";
        var request = new SigningRequest
        {
            DocumentTemplateId = template.Id,
            Subject = body.Subject,
            Message = body.Message,
            ExpiresAtUtc = body.ExpiresAtUtc,
            Status = SigningRequestStatus.Sent,
            SentAtUtc = now,
            CreatedBy = createdBy,
            NotifyEmail = string.IsNullOrEmpty(senderEmail) ? null : senderEmail,
            BulkSendJobId = bulkSendRow?.JobId,
            BulkSendRowIndex = bulkSendRow?.RowIndex,
        };

        foreach (var assignment in body.Recipients)
        {
            if (!rolesByName.TryGetValue(assignment.RoleName, out var role))
            {
                throw new InvalidOperationException(
                    $"Recipient assignment references unknown role '{assignment.RoleName}'.");
            }

            request.Recipients.Add(new Recipient
            {
                Id = Guid.NewGuid(),
                Role = role,
                RoleId = role.Id,
                RoutingOrder = role.RoutingOrder,
                Email = assignment.Email,
                Name = assignment.Name,
                Status = role.RoutingOrder == 1 ? RecipientStatus.Invited : RecipientStatus.Pending,
                InvitedAtUtc = role.RoutingOrder == 1 ? now : null,
                AccessToken = GenerateAccessToken(),
            });
        }

        AddAudit(request, AuditEventType.SigningRequestCreated, now);
        AddAudit(request, AuditEventType.SigningRequestSent, now);
        foreach (var r in request.Recipients.Where(r => r.Status == RecipientStatus.Invited))
        {
            AddAudit(request, AuditEventType.RecipientInvited, now, r);
        }

        _db.SigningRequests.Add(request);

        // Emit a RecipientInvited webhook for each initial-routing recipient. The dispatcher
        // queues outbox rows on the SAME DbContext as the workflow state change, so both
        // commit atomically — no risk of a webhook firing for a state change that was rolled
        // back.
        if (_webhookDispatcher is not null)
        {
            foreach (var r in request.Recipients.Where(r => r.Status == RecipientStatus.Invited))
            {
                await _webhookDispatcher.EnqueueAsync(
                    WebhookEventType.RecipientInvited,
                    new { SigningRequestId = request.Id, RecipientId = r.Id, r.Email, r.Name },
                    ct).ConfigureAwait(false);
            }
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Best-effort: send invitation emails to recipients we just promoted to Invited.
        // Failures are logged but do NOT roll back the dispatch — the API response still
        // carries access tokens so the caller has a fallback notification channel.
        await TrySendInvitationEmailsAsync(
            request,
            request.Recipients.Where(r => r.Status == RecipientStatus.Invited).ToList(),
            ct).ConfigureAwait(false);

        return request;
    }

    private async Task TrySendInvitationEmailsAsync(
        SigningRequest request,
        IReadOnlyList<Recipient> recipients,
        CancellationToken ct)
    {
        if (_emailSender is null || _emailOptions is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_emailOptions.SigningUrlTemplate))
        {
            _logger.LogDebug(
                "Workflow email skipped for request {RequestId}: SigningUrlTemplate not configured.",
                request.Id);
            return;
        }

        foreach (var recipient in recipients)
        {
            try
            {
                var url = _emailOptions.SigningUrlTemplate.Replace(
                    "{accessToken}",
                    recipient.AccessToken,
                    StringComparison.Ordinal);

                var subject = $"{_emailOptions.ProductName}: Action required — {request.Subject}";
                var plain = InvitationEmailComposer.BuildInvitationPlainTextBody(recipient, request, url, _emailOptions.ProductName);
                var html = InvitationEmailComposer.BuildInvitationHtmlBody(recipient, request, url, _emailOptions.ProductName);

                await _emailSender.SendAsync(new EmailMessage(
                    FromAddress: _emailOptions.FromAddress,
                    FromDisplayName: _emailOptions.FromDisplayName,
                    To: [new EmailAddress(recipient.Email, recipient.Name)],
                    Subject: subject,
                    PlainTextBody: plain,
                    HtmlBody: html),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Invitation email failed for recipient {RecipientId} on request {RequestId}; recipient access URL is still returned via the API response.",
                    recipient.Id,
                    request.Id);
            }
        }
    }

    private void AddAudit(
        SigningRequest request,
        AuditEventType type,
        DateTimeOffset at,
        Recipient? recipient = null,
        string? documentHash = null)
    {
        // Critical: do NOT set Id here. When an AuditEvent is added via a tracked collection
        // navigation (request.AuditEvents on a request loaded from the DB), EF Core's change
        // tracker sees a non-empty Guid PK and infers "this row already exists" — marking it
        // Modified instead of Added. The append-only guard in StampdDbContext.UpdateAuditableFields
        // then throws. Leaving Id unset (Guid.Empty) lets EF correctly attach as Added, and
        // UpdateAuditableFields assigns a fresh Guid in the Added branch before save.
        request.AuditEvents.Add(new AuditEvent
        {
            EventType = type,
            OccurredAtUtc = at,
            Recipient = recipient,
            RecipientId = recipient?.Id,
            DocumentHashAtEvent = documentHash,
            // v2.0 Slice D — actor attribution. Read at audit-write time so a single
            // workflow service instance handling sequential calls per-scope sees the
            // correct caller. Null is the right default for recipient-flow events
            // (RecipientViewed/Signed) — the recipient is captured via RecipientId.
            ActorUserId = _actorContext?.IsAuthenticated == true ? _actorContext.UserId : null,
            ActorRole = _actorContext?.IsAuthenticated == true ? _actorContext.ActiveRole : null,
        });
    }

    private static string GenerateAccessToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
