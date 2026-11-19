using System.Security.Cryptography;

using Stampd.Core;
using Stampd.Core.Entities;
using Stampd.WebApi.Models;

namespace Stampd.WebApi.Services;

public sealed partial class SigningWorkflowService
{
    /// <summary>
    /// Records the recipient's submission. If all required recipients have now signed,
    /// finalizes the document: invokes the engine, persists the signed PDF, writes the
    /// SignedDocumentRecord, and transitions the workflow to Completed.
    /// </summary>
    public async Task<(RecipientStatus, SigningRequestStatus, Guid? SignedDocumentId, string? Hash)>
        SubmitAsync(
            Recipient recipient,
            IReadOnlyDictionary<int, ApiFieldValue> fieldValues,
            CancellationToken ct)
    {
        var request = recipient.SigningRequest!;
        var now = DateTimeOffset.UtcNow;

        recipient.Status = RecipientStatus.Signed;
        recipient.SignedAtUtc = now;
        // Persist this recipient's submission for later aggregation. Each recipient owns
        // the fields whose template AssignedRoleId matches this recipient's RoleId; we
        // honor that mapping at finalization rather than trusting the indexes blindly.
        recipient.SubmittedFieldValuesJson = System.Text.Json.JsonSerializer.Serialize(fieldValues);
        AddAudit(request, AuditEventType.RecipientSigned, now, recipient);

        // Promote next-routing-order recipients from Pending → Invited. They are emailed
        // after the commit below, like the first slot is at dispatch.
        var promoted = new List<Recipient>();
        var nextOrder = request.Recipients
            .Where(r => r.Status == RecipientStatus.Pending)
            .Select(r => (int?)r.RoutingOrder)
            .DefaultIfEmpty(null)
            .Min();
        if (nextOrder is not null)
        {
            foreach (var r in request.Recipients.Where(r => r.Status == RecipientStatus.Pending && r.RoutingOrder == nextOrder))
            {
                r.Status = RecipientStatus.Invited;
                r.InvitedAtUtc = now;
                AddAudit(request, AuditEventType.RecipientInvited, now, r);
                promoted.Add(r);
            }
        }

        // Determine if all recipients are now Signed.
        var allDone = request.Recipients.All(r => r.Status == RecipientStatus.Signed);

        Guid? signedDocumentId = null;
        string? signedHash = null;
        // Hold a reference so we can pass the completed record to the notifier after
        // SaveChanges — firing the email before commit would risk notifying on a row
        // the workflow ultimately rolls back.
        SignedDocumentRecord? completedSignedDocument = null;

        if (allDone)
        {
            // Sign the document with the accumulated values. For v1 we store the LAST
            // recipient's field values; multi-recipient field aggregation is a v1.1 concern.
            var sourcePdf = await _storage
                .RetrieveAsync(request.DocumentTemplate!.SourcePdfStorageKey, ct)
                .ConfigureAwait(false);

            var orderedFields = request.DocumentTemplate.Fields
                .OrderBy(f => f.PageNumber)
                .ThenBy(f => f.BoundsY)
                .ThenBy(f => f.BoundsX)
                .ToList();

            var signatureFields = orderedFields.Select(f =>
                new SignatureField(
                    f.PageNumber,
                    new PercentageRect(f.BoundsX, f.BoundsY, f.BoundsWidth, f.BoundsHeight),
                    f.Kind,
                    f.AssignedRoleId?.ToString() ?? "default"))
                .ToArray();

            // Aggregate per-recipient submissions. For each template field we accept
            // the value from the recipient whose RoleId matches the field's AssignedRoleId.
            // If a field is unassigned (AssignedRoleId == null) we fall back to the
            // current submitter's values, then to any recipient's, in routing order.
            var engineFieldValues = new Dictionary<int, ReadOnlyMemory<byte>>(orderedFields.Count);
            for (var fieldIndex = 0; fieldIndex < orderedFields.Count; fieldIndex++)
            {
                var templateField = orderedFields[fieldIndex];
                var source = ResolveFieldOwner(
                    request,
                    currentSubmitter: recipient,
                    fieldValues,
                    templateField.AssignedRoleId);

                if (source is null)
                {
                    continue;
                }

                if (!source.TryGetValue(fieldIndex, out var val))
                {
                    continue;
                }

                if (val.ImageBase64 is { Length: > 0 } img)
                {
                    engineFieldValues[fieldIndex] = Convert.FromBase64String(img);
                }
                else if (val.Text is { } text)
                {
                    engineFieldValues[fieldIndex] = System.Text.Encoding.UTF8.GetBytes(text);
                }
            }

            var signRequest = new SignatureRequest
            {
                SourcePdf = sourcePdf,
                Fields = signatureFields,
                FieldValues = engineFieldValues,
                Sealing = _padesDefaults.BuildSealingOptions(),
                Metadata = new SignatureMetadata(
                    Reason: request.Subject,
                    Location: "Stampd workflow",
                    SignerName: recipient.Name),
            };

            var signed = await _engine.SignAsync(signRequest, ct).ConfigureAwait(false);
            var signedBytes = signed.SignedPdf.ToArray();

            var storageKey = await _storage
                .StoreAsync(signedBytes, $"signed-{request.Id:N}.pdf", ct)
                .ConfigureAwait(false);

            var documentSha = Convert.ToHexString(SHA256.HashData(signedBytes)).ToLowerInvariant();

            var signedDocument = new SignedDocumentRecord
            {
                Id = Guid.NewGuid(),
                SigningRequest = request,
                StorageKey = storageKey,
                ContentSha256 = documentSha,
                SignedAtUtc = signed.SignedAtUtc,
                SealingProviderName = _sealingProvider?.Name ?? "Unknown",
                SealingKeyIdentifier = await SigningKeyIdentifierAsync(ct).ConfigureAwait(false),
                TimestampAuthorityUrl = signed.AchievedLevel >= PAdESLevel.BT ? _timestampAuthority?.Name : null,
                PAdESLevel = signed.AchievedLevel,
            };
            _db.SignedDocumentRecords.Add(signedDocument);

            request.Status = SigningRequestStatus.Completed;
            request.CompletedAtUtc = signed.SignedAtUtc;
            AddAudit(request, AuditEventType.DocumentSealed, signed.SignedAtUtc, documentHash: documentSha);
            AddAudit(request, AuditEventType.SigningRequestCompleted, signed.SignedAtUtc);

            signedDocumentId = signedDocument.Id;
            signedHash = documentSha;
            completedSignedDocument = signedDocument;
        }
        else
        {
            request.Status = SigningRequestStatus.InProgress;
        }

        // Emit lifecycle events. RecipientSigned always fires; SigningRequestCompleted
        // fires only when the workflow finalizes.
        if (_webhookDispatcher is not null)
        {
            await _webhookDispatcher.EnqueueAsync(
                WebhookEventType.RecipientSigned,
                new { SigningRequestId = request.Id, RecipientId = recipient.Id },
                ct).ConfigureAwait(false);

            if (allDone)
            {
                await _webhookDispatcher.EnqueueAsync(
                    WebhookEventType.SigningRequestCompleted,
                    new
                    {
                        SigningRequestId = request.Id,
                        SignedDocumentId = signedDocumentId,
                        DocumentHashSha256 = signedHash,
                    },
                    ct).ConfigureAwait(false);
            }
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (promoted.Count > 0)
        {
            await TrySendInvitationEmailsAsync(request, promoted, ct).ConfigureAwait(false);
        }

        // v1.3 #134 — sender completion notification. Fires only on the transition to
        // Completed and only AFTER commit, so a failed email can never leave the workflow
        // in an inconsistent state. The notifier itself swallows exceptions.
        if (allDone && completedSignedDocument is not null && _senderCompletionNotifier is not null)
        {
            await _senderCompletionNotifier
                .NotifyAsync(request, completedSignedDocument, ct)
                .ConfigureAwait(false);
        }

        return (recipient.Status, request.Status, signedDocumentId, signedHash);
    }

    /// <summary>
    /// Picks the field-value dictionary that "owns" a given template field for the
    /// multi-recipient finalize step. Resolution order:
    /// <list type="number">
    ///   <item>The recipient whose RoleId equals <paramref name="assignedRoleId"/>.</item>
    ///   <item>The current submitter (covers single-recipient and pre-filled fields).</item>
    ///   <item>The earliest-routing recipient with any submitted values (fallback).</item>
    /// </list>
    /// Returns null when nobody has submitted anything for this field.
    /// </summary>
    /// <summary>SHA-256 thumbprint of the signing certificate, or "Unknown" without a provider.</summary>
    private async Task<string> SigningKeyIdentifierAsync(CancellationToken ct)
    {
        if (_sealingProvider is null)
        {
            return "Unknown";
        }

        var certificate = await _sealingProvider.GetSigningCertificateAsync(ct).ConfigureAwait(false);
        return certificate.GetCertHashString(HashAlgorithmName.SHA256);
    }

    private static IReadOnlyDictionary<int, ApiFieldValue>? ResolveFieldOwner(
        SigningRequest request,
        Recipient currentSubmitter,
        IReadOnlyDictionary<int, ApiFieldValue> currentSubmitterValues,
        Guid? assignedRoleId)
    {
        if (assignedRoleId is not null)
        {
            var matched = request.Recipients
                .FirstOrDefault(r => r.RoleId == assignedRoleId);
            if (matched is not null)
            {
                if (matched.Id == currentSubmitter.Id)
                {
                    return currentSubmitterValues;
                }

                if (matched.SubmittedFieldValuesJson is not null)
                {
                    return System.Text.Json.JsonSerializer
                        .Deserialize<Dictionary<int, ApiFieldValue>>(matched.SubmittedFieldValuesJson);
                }
            }
        }

        // Pre-filled or unassigned: use the current submitter's values.
        if (currentSubmitterValues.Count > 0)
        {
            return currentSubmitterValues;
        }

        // Last-resort: the earliest-routing recipient who has submitted anything.
        var fallback = request.Recipients
            .Where(r => r.SubmittedFieldValuesJson is not null)
            .OrderBy(r => r.RoutingOrder)
            .FirstOrDefault();
        return fallback?.SubmittedFieldValuesJson is null
            ? null
            : System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<int, ApiFieldValue>>(fallback.SubmittedFieldValuesJson);
    }
}
