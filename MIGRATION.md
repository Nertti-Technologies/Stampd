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
