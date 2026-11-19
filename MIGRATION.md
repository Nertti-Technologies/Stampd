# Migrating Stampd v3.3.x → v3.4.0

v3.4.0 closes four security gaps in identity verification and template editing. Apply the V20 migration. No public package API is removed, but three behaviours change on purpose; review them below.

## TL;DR

1. Apply the V20 migration on every database provider. It adds the nullable `Recipients.PendingVerificationId` column. No data is filled.
2. If you use `Stampd.Identity.SmsOtp` or `Stampd.Identity.Kba` with their mock providers outside the Development environment, register a real `ISmsGateway` / `IKbaProvider`. Otherwise the host now refuses to start. For a test environment you can set `SmsOtpOptions.AllowMockGateway` or `KbaIdentityVerificationOptions.AllowMockProvider`.
3. A template's roles and fields can no longer be changed while a request that uses it is Draft, Sent or InProgress (`409`).

## Changes

### 1. An identity-check code only verifies the recipient it was sent to

- `POST /api/sign/{accessToken}/verify-identity` accepts only the verification ID most recently issued by `initiate-verification` on the same signing link. Before 3.4 a code issued on any link verified any recipient.
- Requesting a new code makes the earlier one stop working.
- A challenge started before the upgrade is not recorded against its recipient, so it is refused. The signer requests a new code.
- New on `Stampd.Core.Entities.Recipient`: `PendingVerificationId`.

### 2. OTP attempt limits hold under parallel requests

- `EmailOtpProvider` and `SmsOtpProvider` count each attempt before comparing the code, so parallel requests can't get more guesses than `MaxFailedAttempts`. A correct code counts as an attempt, so the limit is now the number of tries in total.
- A locked-out or expired challenge stays in the store until it is swept, so it keeps counting towards `InitiatesPerWindowMax`. Before 3.4 lockout deleted it, which reset the initiate limit. The admin dashboard's lockout counts now show real numbers.
- `InMemoryOtpChallengeStore.IncrementFailedAttemptsAsync` is now atomic.
- If you implement `IOtpChallengeStore`, make `IncrementFailedAttemptsAsync` atomic. It may return a value higher than this call's own increment, but never a lower one.

### 3. Mock identity providers refuse to run outside Development

- `AddSmsOtpIdentityVerification` and `AddKbaIdentityVerification` register a startup check. When the mock gateway or provider is the one in use and the host environment isn't Development, startup fails with a message naming the fix.
- New options: `SmsOtpOptions.AllowMockGateway`, and `KbaIdentityVerificationOptions.AllowMockProvider` through a new `AddKbaIdentityVerification(services, configure)` overload.
- `MockSmsGateway` no longer writes the phone number or message to stdout. It logs a masked number only; read messages in tests through its `onSend` callback.
- Both packages now depend on `Microsoft.Extensions.Hosting.Abstractions` 10.0.15.

### 4. Template edits can't change requests in flight

- `PUT /api/templates/{id}` returns `409` when the roles or fields change and any request using the template is Draft, Sent or InProgress. Changing only the name or description still succeeds and leaves the roles and fields untouched.
- Before 3.4 every edit re-created the roles, which detached in-flight recipients from their role and removed their identity-check requirement.
- The update now runs in one transaction.

---

# Migrating Stampd v3.2.x → v3.3.0

v3.3.0 makes the signed-document record accurate, fixes the admin downloads and makes the admin exports and analytics work on SQLite. Apply the V19 migration. No public package API is removed.

## TL;DR

1. Apply the V19 migration on every database provider. It adds `AuditEvents.OccurredAtUtcEpochMs`, `OtpChallenges.CreatedAtUtcEpochMs` and `WebhookDeliveries.CreatedAtUtcEpochMs`, fills them from the existing timestamps and indexes them. The fill is one `UPDATE` per table.
2. If you implement `IStampdEngine` yourself, set `SignedDocument.AchievedLevel`. Until you do, records show B-B.

## Changes

### 1. The signed-document record states what sealed the document

- `SealingProviderName` is the registered provider's `Name`, and `SealingKeyIdentifier` is the SHA-256 thumbprint of the signing certificate. Before 3.3 they were always `LocalCertificate` and `spike-signer`.
- `PAdESLevel` is the level the engine reached. It can be lower than `Stampd:Pades:TargetLevel`: B-T needs a timestamp authority, and B-LT/B-LTA need revocation responses (a DSS with certificates only is not B-LT). Before 3.3 it was always B-T.
- `TimestampAuthorityUrl` holds the timestamp authority's name when the level is B-T or higher. Before 3.3 it was never set.
- New on `Stampd.Core.SignedDocument`: `AchievedLevel`, an init-only property. Engines that don't set it report `BB`.
- Existing records keep their stored values.

### 2. Signing-request responses carry `signedDocumentId`

- `GET /api/signing-requests/{id}` and each item of `GET /api/signing-requests` include `signedDocumentId` once the request is Completed, and `null` before. Download the PDF from `GET /api/signed-documents/{id}/download`.
- The UI's "Download PDF" and the request list's download button use this with the signed-in user's credentials. They no longer borrow a recipient's signing link.

### 3. Admin downloads and analytics

- The admin dashboard's audit CSV buttons now send the user's credentials. Before 3.3 they were plain links, and the API refused them.
- On SQLite, `GET /api/admin/audit/export`, `GET /api/admin/analytics/identity-verification`, `GET /api/admin/analytics/webhooks-health`, `GET /api/webhooks` and `GET /api/signing-requests/{id}/audit` returned 500. They now work. Postgres and SQL Server were not affected.
- CSV export: a cell that starts with `=`, `+`, `-`, `@`, tab or carriage return is written with a leading `'`, so spreadsheet apps show it as text instead of running it as a formula.

---

# Migrating Stampd v3.1.x → v3.2.0

v3.2.0 fixes signing-request lifecycle and delivery defects. Apply the V18 migration. Several fixes change behaviour that integrations may have relied on, so review the changes below.

## TL;DR

1. Apply the V18 migration on every database provider. It adds the nullable `SigningRequests.BulkSendJobId` and `BulkSendRowIndex` columns with a unique index.
2. If any webhook posts to an internal host, list that host in `Stampd:Webhooks:AllowedPrivateHosts`.

## Behaviour changes

### 1. Closed requests return 410 on every recipient link

- When a request is Voided, Expired or Declined, every `/api/sign/{accessToken}` route returns **410 Gone**: view, document download, verification codes and submit.
- Before 3.2, a voided request's link still showed the document and sent verification codes. Submit returned 409.

### 2. `ExpiresAtUtc` is enforced

- A request whose `ExpiresAtUtc` has passed becomes `Expired` the first time a recipient link is used after the deadline. Recipients who have not signed become `Expired`.
- This applies to existing requests too. Live requests already past their date expire on next use.
- New webhook event: `SigningRequestExpired`. Endpoints with an empty `SubscribedEvents` (all events) start receiving it.

### 3. Webhook targets

- `POST /api/webhooks` refuses URLs that point at internal addresses, with **400**. These are loopback, link-local (including cloud metadata), private, carrier-grade NAT, unique-local and multicast addresses. Host names are resolved, so a name pointing at an internal address is refused too.
- Delivery checks the address actually dialled on every attempt, so a DNS change after registration can't redirect a webhook inside.
- Delivery no longer follows HTTP redirects. A 3xx response counts as a failed attempt.
- To allow an internal receiver, list its host name in `Stampd:Webhooks:AllowedPrivateHosts`.
- Retries now run for 2 days from when the event was queued: about 58 attempts, backing off to hourly. Before 3.2 they stopped after about 68 minutes.

### 4. Sequential signing emails

- When one routing slot finishes, the recipients in the next slot now get their invitation email. Before 3.2 only the first slot was emailed.

### 5. Signature images are checked at submit

- An `imageBase64` field value must be base64 PNG or JPEG, at most 1 MiB decoded. Anything else is refused with **400** at that recipient's submit.
- Before 3.2, a bad image was accepted and then failed the whole request when it was finalised.

### 6. Bulk send

- Each request created by bulk send records its job and row. A row that was dispatched just before a crash is not dispatched or emailed again.

---

# Migrating Stampd v3.0.x → v3.1.0

v3.1.0 changes how the WebApi decides a request's tenant, which roles may change data, and who receives recipient access URLs. No public package API is removed. Apply the V17 migration and review the changes below before upgrading.

## TL;DR

1. Apply the V17 migration on every database provider (adds the nullable `SigningRequests.NotifyEmail` column).
2. Make sure access tokens carry the caller's tenant in the `tenant_id` claim (or the claim named by `Stampd:Auth:Oidc:TenantClaim`).
3. If you run more than one tenant, set `Stampd:Tenancy:Mode=Multi`.
4. Give users who create or change data the `Sender` or `Admin` role.

## Behaviour changes

### 1. The tenant comes from the access token

- The request's tenant is the token's tenant claim.
- `X-Stampd-Tenant` may still be sent:
  - If it names the token's own tenant, it is accepted.
  - If it names a different tenant, it is honoured only for an `Admin` holding an `AdminScope` for that tenant, or a user listed in `Stampd:Auth:SuperAdminUserIds`. Anyone else gets **403**.
- A header that is not a tenant id, or is the all-zero id, gets **400**.
- **Tokens without a tenant claim:**
  - `Stampd:Tenancy:Mode=Single` (the default) runs them in `Stampd:Tenancy:DefaultTenantId`. That is the previous behaviour for one-tenant installs.
  - `Multi` rejects them with **403**.
- Anonymous routes (recipient signing behind its access token) keep using the header, falling back to the default tenant.

### 2. Development authentication is limited to Development

- `/api/auth/dev-token` and the HS256 development signing key now exist only in the `Development` and `Testing` environments.
- `Stampd:Auth:Mode=Saml2` now fails at startup outside Development, because the SAML2 SP does not validate assertions yet. Use `Stampd:Auth:Mode=Oidc` in production.

### 3. Changing data requires the `Sender` or `Admin` role

- These routes now return **403** for `ReadOnly` users:
  - creating, updating or immediately signing a template;
  - creating a signing request;
  - bulk send;
  - creating or deleting a webhook.
- Reads are unchanged.

### 4. Request ownership and access URLs

- `SigningRequest.CreatedBy` and `DocumentTemplate.CreatedBy` are now the caller's user id (the token's subject). Work without a signed-in user, such as bulk send, records `api`.
- `SenderEmail` on `POST /api/signing-requests` now only sets where the completion email goes. It is stored in the new `NotifyEmail` column. Requests created before 3.1 keep sending to the address in `CreatedBy`.
- Recipient `accessUrl` values in signing-request responses are returned only to the request's owner and to Admins. Other callers receive `null`.
- The `senderEmail` filter on `GET /api/signing-requests` matches either `CreatedBy` or `NotifyEmail`.

---

# Migrating Stampd v2.x → v3.0.0

v3.0 is a MAJOR release. This document covers the upgrade path for adopters running v2.3.0 stable.

## TL;DR

1. Apply V16 migration on every database provider (adds `AdminScopes` table).
2. Decide auth mode: `DevJwt` stays for Development; production must pick `Oidc` or `Saml2`.
3. (Optional) Add `Stampd.Compliance` if you need HIPAA / 21 CFR Part 11 / eIDAS QES gating.
4. Bootstrap at least one admin scope (`Stampd:Auth:SuperAdminUserIds` or insert into `AdminScopes`).

Everything else is additive.

## Breaking changes

### 1. The `Admin` authorization policy now requires BOTH the Admin role claim AND an active `AdminScope` row

v2.x: any JWT carrying `role: Admin` was tenant-wide admin on whatever tenant context the request landed in.

v3.0: same JWT also needs a row in the new `AdminScopes` table for the request's tenant — OR the user's JWT `sub` must be in `Stampd:Auth:SuperAdminUserIds`.

**Action required.** Two paths:

- **Quick (compatibility mode)**: list your existing admin user IDs in `Stampd:Auth:SuperAdminUserIds`. They keep working tenant-wide as before.
- **Proper RBAC**: insert `AdminScope` rows per (admin user, tenant). Use `POST /api/admin/scopes` once you've got one super-admin bootstrapped.

Without either step, the FIRST `/api/admin/*` request after upgrade returns 403.

### 2. `Stampd:Auth:Mode=DevJwt` throws at startup outside Development

Production deployments that left the v2.x dev JWT minter on get a clear startup error. Set `Stampd:Auth:Mode=Oidc` (most common) or `Stampd:Auth:Mode=Saml2` (enterprise).

`Mode=DevJwt` still works in `Development` and `Testing` environments — your existing local + CI flows are unchanged.

### 3. New required config when Mode=Oidc

```jsonc
{
  "Stampd": {
    "Auth": {
      "Mode": "Oidc",
      "Oidc": {
        "Authority": "https://example.auth0.com/",
        "Audience": "https://api.stampd.example.com",
        "RoleClaim": "groups",
        "RoleClaimMappings": {
          "stampd-admins": "Admin",
          "stampd-senders": "Sender"
        }
      }
    }
  }
}
```

External groups not listed in `RoleClaimMappings` are silently dropped. Adopters must explicitly opt every group → role.

## Additive surface

### `Stampd.Identity.Saml2` (alpha.3 scaffold)

Endpoints `/api/auth/saml/{metadata,login,acs}` are mapped and return 501 until v3.0.1 wires up real SAML2 SP. Eager config validation runs today so misconfig fails at startup. Adopters who need real SAML2 should pin to v3.0.0 and wait for the v3.0.1 patch — or contribute the ITfoxtec wiring; the surface is in place.

### `Stampd.Compliance` (new package)

Optional. Three industry bundles with runtime-enforced gates:

| Bundle | Min signature | IV required | Audit retention |
|---|---|---|---|
| `Hipaa` | B-LT | ✅ | 6 years |
| `Cfr21Part11` | B-LTA | ✅ | 7 years |
| `EidasQes` | B-LTA | ✅ | 10 years |

Adopters who need any of these:

```csharp
services.AddStampdCompliance();
// ...then in dispatch:
gate.Validate(new ComplianceDeclaration(...));
```

Persistent `SigningRequest.ComplianceBundle` column lands in v3.1 along with the maintenance worker that prunes audit rows older than the retention window.

## Database schema

V16 migration adds the `AdminScopes` table only. Apply on every provider you run:

```bash
dotnet ef database update --project src/Stampd.Infrastructure.Sqlite    --startup-project src/Stampd.WebApi
dotnet ef database update --project src/Stampd.Infrastructure.SqlServer --startup-project src/Stampd.WebApi
dotnet ef database update --project src/Stampd.Infrastructure.Postgres  --startup-project src/Stampd.WebApi
```

The table is cross-tenant by design — no global query filter. Don't add one in a custom DbContext subclass.

## Wire-format compatibility

- v2.x JWT shape unchanged. Same `role` + `sub` claims; v3.0 just adds a second gate after the role check.
- v2.x API surface unchanged. No endpoints renamed or removed. New endpoints (`/api/admin/scopes/*`, `/api/auth/saml/*`) are additive.
- v2.x response shapes unchanged. Analytics widgets etc. work identically.

## What v3.0.0 doesn't ship

- **Workflow rules engine** — moved to v3.1 candidates. The breadth of the rules DSL + designer UI deserved a dedicated alpha cycle that wouldn't fit in the v3.0 train.
- **SAML2 SP production wiring** — v3.0.0-alpha.3 scaffolding only. Real assertion validation in v3.0.1 or via community contribution.
- **Persistent `SigningRequest.ComplianceBundle` column** — declared per dispatch in v3.0; v3.1 adds the schema column.
- **PHI redaction for HIPAA** — existing GDPR-erase flow handles most cases; HIPAA-specific classification is post-v3.

## Rollback

v3.0.0 is reversible: drop the AdminScopes table, revert to v2.3.0 packages, restore Mode=DevJwt. No data migration is destructive.
