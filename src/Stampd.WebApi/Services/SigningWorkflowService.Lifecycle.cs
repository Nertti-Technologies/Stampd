using Microsoft.EntityFrameworkCore;

using Stampd.Core.Entities;
using Stampd.Core.Notifications;

namespace Stampd.WebApi.Services;

public sealed partial class SigningWorkflowService
{
    /// <summary>
    /// v2.0 Slice D — voids a signing request. Idempotent: re-voiding an already-voided
    /// request returns <c>false</c> without re-writing the audit. Refuses to void
    /// terminal-non-Voided states (Completed, Declined, Expired) — those don't
    /// transition to Voided in the workflow state machine. Returns <c>true</c> when
    /// the void actually applied.
    /// </summary>
    /// <param name="signingRequestId">The signing request to void.</param>
    /// <param name="reason">
    /// Free-text reason persisted to <c>SigningRequest.TerminationReason</c>. Surfaced
    /// to the sender on the detail page so admins can answer "why was this voided?"
    /// </param>
    /// <param name="ct"></param>
    public async Task<bool> VoidAsync(
        Guid signingRequestId,
        string? reason,
        CancellationToken ct)
    {
        // Load with Recipients so the audit event can carry the right context and so
        // the workflow state-machine has the same shape as SubmitAsync.
        var request = await _db.SigningRequests
            .Include(r => r.Recipients)
            .FirstOrDefaultAsync(r => r.Id == signingRequestId, ct)
            .ConfigureAwait(false);

        if (request is null)
        {
            return false;
        }

        if (request.Status == SigningRequestStatus.Voided)
        {
            // Idempotent — already voided. No audit, no webhook, no error.
            return false;
        }

        if (request.Status is SigningRequestStatus.Completed
            or SigningRequestStatus.Declined
            or SigningRequestStatus.Expired)
        {
            throw new InvalidOperationException(
                $"Cannot void a signing request in terminal state {request.Status}.");
        }

        var now = DateTimeOffset.UtcNow;
        request.Status = SigningRequestStatus.Voided;
        request.VoidedAtUtc = now;
        request.TerminationReason = string.IsNullOrWhiteSpace(reason)
            ? "Voided by admin"
            : reason.Trim();

        AddAudit(request, AuditEventType.SigningRequestVoided, now);

        if (_webhookDispatcher is not null)
        {
            await _webhookDispatcher.EnqueueAsync(
                WebhookEventType.SigningRequestVoided,
                new
                {
                    SigningRequestId = request.Id,
                    Reason = request.TerminationReason,
                    VoidedAtUtc = now,
                },
                ct).ConfigureAwait(false);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// v2.0 Slice D — re-sends the invitation email to a single recipient. Only valid
    /// for recipients in <c>Invited</c> or <c>Viewed</c> state — refuses Signed,
    /// Declined, Expired, Pending. Returns true when an email was actually sent;
    /// false when the recipient row was missing or the SMTP send failed silently
    /// (per the workflow's existing "best-effort email" convention).
    /// </summary>
    public async Task<bool> ResendInvitationAsync(
        Guid recipientId,
        CancellationToken ct)
    {
        var recipient = await _db.Recipients
            .Include(r => r.SigningRequest!)
            .FirstOrDefaultAsync(r => r.Id == recipientId, ct)
            .ConfigureAwait(false);

        if (recipient is null) return false;

        if (recipient.Status is not (RecipientStatus.Invited or RecipientStatus.Viewed))
        {
            throw new InvalidOperationException(
                $"Cannot resend invitation to a recipient in state {recipient.Status}.");
        }

        if (_emailSender is null
            || _emailOptions is null
            || string.IsNullOrWhiteSpace(_emailOptions.SigningUrlTemplate))
        {
            // Email transport not configured — log + audit but skip the send. The
            // workflow already tolerates this on the dispatch path; resends inherit
            // the same convention so adopters running without SMTP don't get
            // exceptions just because they triggered a resend.
            _logger.LogWarning(
                "ResendInvitationAsync called for recipient {RecipientId} but email transport is not configured.",
                recipient.Id);
            return false;
        }

        try
        {
            var url = _emailOptions.SigningUrlTemplate.Replace(
                "{accessToken}",
                recipient.AccessToken,
                StringComparison.Ordinal);

            var subject = $"{_emailOptions.ProductName}: Reminder — {recipient.SigningRequest!.Subject}";
            var plain = InvitationEmailComposer.BuildInvitationPlainTextBody(recipient, recipient.SigningRequest, url, _emailOptions.ProductName);
            var html = InvitationEmailComposer.BuildInvitationHtmlBody(recipient, recipient.SigningRequest, url, _emailOptions.ProductName);

            await _emailSender.SendAsync(new EmailMessage(
                FromAddress: _emailOptions.FromAddress,
                FromDisplayName: _emailOptions.FromDisplayName,
                To: [new EmailAddress(recipient.Email, recipient.Name)],
                Subject: subject,
                PlainTextBody: plain,
                HtmlBody: html),
                ct).ConfigureAwait(false);

            AddAudit(recipient.SigningRequest!, AuditEventType.RecipientInvitationResent, DateTimeOffset.UtcNow, recipient);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "ResendInvitationAsync email send failed for recipient {RecipientId}.",
                recipient.Id);
            return false;
        }
    }

    /// <summary>Returns the (request, recipient) pair for a given access token, or null if not found.</summary>
    /// <remarks>
    /// Eagerly loads the SigningRequest's sibling Recipients collection — SubmitAsync
    /// relies on it for routing-order promotion, the all-done check, and (v1.3 #134)
    /// the sender completion email's recipient count. Without this Include the
    /// <c>request.Recipients.All(...)</c> check would vacuously succeed after the first
    /// signature and finalize early. Cost is one extra round-trip and a few rows; this
    /// is the canonical entry point for the recipient signing flow.
    /// </remarks>
    public async Task<(SigningRequest Request, Recipient Recipient)?> ResolveByAccessTokenAsync(
        string accessToken,
        CancellationToken ct)
    {
        var recipient = await _db.Recipients
            .Include(r => r.SigningRequest!)
                .ThenInclude(sr => sr.DocumentTemplate!)
                    .ThenInclude(t => t.Fields)
            .Include(r => r.SigningRequest!)
                .ThenInclude(sr => sr.Recipients)
            .Include(r => r.Role)
            .FirstOrDefaultAsync(r => r.AccessToken == accessToken, ct)
            .ConfigureAwait(false);

        return recipient is null ? null : (recipient.SigningRequest!, recipient);
    }

    /// <summary>
    /// Expires <paramref name="request"/> if its <see cref="SigningRequest.ExpiresAtUtc"/>
    /// has passed and it is still open (Sent or InProgress). Recipients who have not
    /// signed become Expired. Checked when a recipient link is used, so a request expires
    /// on its first use after the deadline. Returns <c>true</c> when it expired now.
    /// </summary>
    public async Task<bool> ExpireIfDueAsync(SigningRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = DateTimeOffset.UtcNow;
        if (request.ExpiresAtUtc is not { } expiresAt
            || expiresAt > now
            || request.Status is not (SigningRequestStatus.Sent or SigningRequestStatus.InProgress))
        {
            return false;
        }

        request.Status = SigningRequestStatus.Expired;
        request.TerminationReason = "Expired";
        AddAudit(request, AuditEventType.SigningRequestExpired, now);

        foreach (var r in request.Recipients.Where(r => r.Status is not (RecipientStatus.Signed or RecipientStatus.Declined)))
        {
            r.Status = RecipientStatus.Expired;
            AddAudit(request, AuditEventType.RecipientExpired, now, r);
        }

        if (_webhookDispatcher is not null)
        {
            await _webhookDispatcher.EnqueueAsync(
                WebhookEventType.SigningRequestExpired,
                new { SigningRequestId = request.Id, ExpiresAtUtc = expiresAt },
                ct).ConfigureAwait(false);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Records that the recipient viewed the signing page (idempotent).</summary>
    public async Task MarkViewedAsync(Recipient recipient, CancellationToken ct)
    {
        if (recipient.FirstViewedAtUtc is not null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        recipient.FirstViewedAtUtc = now;
        if (recipient.Status == RecipientStatus.Invited)
        {
            recipient.Status = RecipientStatus.Viewed;
        }

        AddAudit(recipient.SigningRequest!, AuditEventType.RecipientViewed, now, recipient);

        if (_webhookDispatcher is not null)
        {
            await _webhookDispatcher.EnqueueAsync(
                WebhookEventType.RecipientViewed,
                new { recipient.SigningRequestId, RecipientId = recipient.Id },
                ct).ConfigureAwait(false);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the identity challenge just issued to the recipient. Only this challenge
    /// can verify them; an earlier one stops working.
    /// </summary>
    public async Task RecordIdentityChallengeAsync(
        Recipient recipient,
        string verificationId,
        CancellationToken ct)
    {
        recipient.PendingVerificationId = verificationId;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a successful identity-verification challenge for the recipient. Stamps
    /// IdentityVerifiedAtUtc + IdentityVerificationMethod, writes a RecipientIdentityVerified
    /// audit event, and persists. Idempotent — re-marks return the existing timestamp.
    /// </summary>
    public async Task<DateTimeOffset> MarkIdentityVerifiedAsync(
        Recipient recipient,
        string verificationMethod,
        CancellationToken ct)
    {
        if (recipient.IdentityVerifiedAtUtc is { } already)
        {
            return already;
        }

        var now = DateTimeOffset.UtcNow;
        recipient.IdentityVerifiedAtUtc = now;
        recipient.IdentityVerificationMethod = verificationMethod;
        recipient.PendingVerificationId = null;

        AddAudit(recipient.SigningRequest!, AuditEventType.RecipientIdentityVerified, now, recipient);

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return now;
    }
}
