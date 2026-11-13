# OLGA Connect Core API

The Core API is the authoritative product API for OLGA Connect. It owns member-facing product state and policy enforcement. It deliberately contains no text normalization, embedding, semantic ranking, model evaluation, or other NLP/AI implementation.

The repository is independent from `Olga.Nlp` and can be versioned, built, tested, deployed, and granted database permissions separately. During the MVP both APIs may use one PostgreSQL 17 database hosted on Azure Database for PostgreSQL Flexible Server, but each runtime must receive least-privilege access only to its owned schemas, views, and functions.

## What is implemented

- Atomic member registration with encrypted email/phone identities and a private draft profile.
- Profile reads and optimistic-concurrency updates with ETags.
- Asynchronous profile-image upload, content validation, sanitized WebP variants, visibility-aware delivery, and delayed replacement cleanup.
- Active consent-policy discovery and append-only consent decisions, including immediate Live Mode revocation.
- Event listing, registration, attendee check-in, configurable matching eligibility, bounded Live Mode, and expiring coarse presence.
- Connection request, acceptance, canonical connection, conversation creation, and blocking.
- Idempotent client message IDs and server message ordering.
- Notification preferences, privacy-request initiation, and authorization-filtered sync changes.
- Transactional outbox records published as versioned Service Bus envelopes by the Core worker
  using bounded batch claims, expiring leases, lease renewal, jittered retry, and explicit exhaustion.
- Consumption of `NlpMatchRequestCompleted.v1` into authorization-scoped sync projections and policy-resolved match notifications.
- Transactional integration events that allow NLP to refresh its read-only eligibility projections without proxying NLP requests through Core.
- EF Core InMemory local development and PostgreSQL configuration through `ConnectionStrings__PostgreSql`.
- Anonymous MVP access for all API operations, with an optional member selector header.

## What is intentionally not implemented yet

This foundation is not the full product backlog. Entra token validation and external-identity linking, fine-grained permissions, general private-file lifecycle, provider notification delivery, privacy task orchestration, retention execution, database migrations, OpenTelemetry, and remaining production assets remain delivery work. See [Senior architecture review](docs/SENIOR_ARCHITECT_REVIEW.md).

PostgreSQL deployments must be at schema v2.6. The database repository owns the generated v2.4
baseline and the ordered v2.5 profile-image and v2.6 outbox-leasing migrations; application startup
does not perform DDL.

## Run locally

```powershell
dotnet restore Olga.Core.slnx
dotnet test Olga.Core.slnx
dotnet run --project src/Olga.Core.Api
```

All endpoints are anonymous for the initial MVP. Member-scoped endpoints use the optional `X-Member-Id` header to select a member and otherwise fall back to `Mvp__DefaultMemberId` (`A123` by default). Local development seeds members `A123`, `B456`, `D111` plus `event-001`. Do not treat this member selector as authentication or expose this deployment to public or sensitive member data.

## Member creation and onboarding

The client calls member registration only after the upstream email OTP step succeeds or a Twilio phone verification proof has been issued. The client generates an `Idempotency-Key`; Core generates the stable member ID.

### Phone verification and sign-in

Phone authentication uses Twilio Verify; Core never generates or stores OTP values. The client flow is:

1. `POST /v1/auth/phone/verifications` with `{ "phone": "+60123456789", "channel": "sms" }` to send a code.
2. `POST /v1/auth/phone/verification-checks` with the same phone and the submitted `code`. A successful response contains a short-lived `verification_token` and `expires_at`.
3. For a new phone-only member, pass that token as `phone_verification_token` to `POST /v1/members`. For an existing member, call `POST /v1/members/lookup/phone` with `phone` and `verification_token`.

All three calls require a distinct `Idempotency-Key`. Proofs are signed, bound to the normalized E.164 phone number, and valid for 300 seconds by default. They contain no OTP or plaintext phone number and are not persisted. Every phone supplied during registration requires a valid proof; an unverified phone never reserves an identity or becomes eligible for phone sign-in.

### 1. Create the member

Call `POST /v1/members` without `X-Member-Id`:

```http
POST /v1/members
Idempotency-Key: 70587dbd-b341-4345-9e18-241e0af50c61
Content-Type: application/json
```

```json
{
  "display_name": "New Member",
  "email": "member@example.com",
  "phone": "+60123456789",
  "phone_verification_token": "<token from the verification-check response>",
  "headline": "Software Engineer",
  "professional_summary": "Building professional connections",
  "role_category": "ENGINEERING",
  "locale": "en-MY",
  "visibility": "MEMBERS"
}
```

At least one of `email` or `phone` is required. Phone numbers must already use E.164 format. Registration atomically creates:

- `iam.member` with an API-generated `member_id` and `ACTIVE` account status.
- One `iam.member_identity` row per supplied contact.
- `core.member_profile` with `DRAFT` profile status.

Email is treated as verified by the completed upstream OTP step. A phone identity receives the database transaction timestamp in `verified_at` only when registration includes a valid phone-verification proof. The member receives `verified_at` when at least one identity is verified.

Email and phone are normalized, protected with AES-256-GCM, and stored in `provider_subject_ciphertext`. A keyed HMAC is stored in `provider_subject_hash` for equality lookup and uniqueness. Plaintext contact values are not stored or returned. The registration response exposes only masked hints:

```json
{
  "member_id": "mem_033ed47ab6da5f5ed7c51647fdcde24b",
  "email_hint": "m***@example.com",
  "phone_hint": "*********3210",
  "profile_status": "DRAFT",
  "etag": "\"1\""
}
```

Reuse the same `Idempotency-Key` only when retrying the identical registration request. Never reuse it with different member details.

### 2. Read the draft profile

Call `GET /v1/me/profile` with the returned member ID in `X-Member-Id`. This reads the existing draft; it does not create another member. It does not include email or phone, and no current endpoint returns decrypted contact values.

### 3. Complete the profile

Call `PATCH /v1/me/profile` with:

- `X-Member-Id`: the returned member ID.
- A new `Idempotency-Key`.
- `If-Match`: the ETag returned by registration or the latest profile read.

A successful update changes the profile from `DRAFT` to `ACTIVE`. Draft profiles are not visible through member lookup and cannot initiate connections. An unknown `X-Member-Id` returns `MEMBER_NOT_REGISTERED`; profile endpoints never create `iam.member`.

### 4. Continue product onboarding

After profile activation, the client can use the following operations:

| Operation | Purpose and prerequisite |
| --- | --- |
| `GET /v1/consent-policies/{purposeCode}` | Read the active policy metadata and the selected member's current decision for that policy version. |
| `POST /v1/me/consents` | Record a decision against an active consent-policy version. |
| `GET /v1/events` | List published or active events. Supply `X-Member-Id` to include member registration state. |
| `POST /v1/events/{eventId}/register` | Register the selected member for an event. |
| `POST /v1/events/{eventId}/live-mode` | Start Live Mode after event registration and Live Mode consent. |
| `POST /v1/connection-requests` | Create a request from an active, visible profile. |
| `GET /v1/connections` | List the selected member's active connections. |
| `GET/POST /v1/conversations/{conversationId}/messages` | Read or send messages after an active connection creates the conversation. |
| `GET /v1/sync/changes` | Read authorization-scoped mobile changes using the opaque cursor. |

## Matching consent and event check-in

The member UI reads `GET /v1/consent-policies/MATCHING` before requesting matching consent. The response identifies the exact active policy with `policy_id`, `version`, `locale`, `content_hash`, and `effective_from`, and includes `current_decision` and `decision_captured_at` when the selected member has responded to that version. Nullable member-decision fields are omitted until a decision exists. Core stores the policy content hash, not display text, so the client must use versioned localized copy that corresponds to the returned policy metadata.

Record the member's decision through `POST /v1/me/consents` using the returned version:

```json
{
  "purpose_code": "MATCHING",
  "policy_version": "1",
  "decision": "GRANTED",
  "capture_channel": "MOBILE"
}
```

When `GET /v1/events` receives `X-Member-Id`, each event includes `is_registered` and, for an attending member, `registration_status` (`REGISTERED` or `CHECKED_IN`). Every event also includes `check_in_required` from its active matching-policy version:

- `false`: an event registration can satisfy the registration portion of matching eligibility without physical check-in.
- `true`: matching eligibility requires the registration to be `CHECKED_IN`.

`check_in_required` does not prevent a registered member from starting Live Mode. The NLP eligibility view combines the active matching policy, registration/check-in state, Live Mode, matching consent, and other Core-owned eligibility rules before returning candidates.

Administrators configure the active event matching policy with `PUT /v1/admin/events/{eventId}/matching-policy` and body `{ "check_in_required": true }` or `{ "check_in_required": false }`. Administrators check in a registered attendee with `POST /v1/admin/events/{eventId}/attendees/{memberId}/check-in`. Check-in is idempotent, returns `checked_in_at`, and publishes the member sync/outbox change. Both mutations require `Idempotency-Key`.

## Identity protection configuration

PostgreSQL deployments must provide `IdentityProtection__MasterKeyBase64` as a Key Vault-backed secret containing exactly 32 cryptographically random bytes encoded as Base64. Core derives separate encryption and lookup keys from this one master secret. Local in-memory development generates an ephemeral key automatically.

Do not commit the master key, print it in logs, or rotate/delete it without a migration that re-encrypts existing identity rows. Losing the key makes existing ciphertext unrecoverable.

## Twilio Verify configuration

Set `Twilio__Enabled=true` only after the environment's credentials exist. An enabled environment requires `Twilio__AccountSid`, `Twilio__ApiKeySid`, `Twilio__VerifyServiceSid`, and the Key Vault-backed secret `Twilio__ApiKeySecret`. The provider authenticates with the restricted API key SID and secret; the account SID supplies the account context. `Twilio__TokenTtlSeconds` controls the phone-bound proof lifetime (default `300`, allowed range `120`-`86400`). Disabled environments do not register the provider or require its credentials. The API key secret must never be committed or logged.

The Azure/Terraform repository that owns the Container App must add those environment-variable bindings and map `Twilio__ApiKeySecret` to the existing Key Vault secret `twilio-api-key-secret`; this repository does not own that infrastructure definition.

`Twilio__RateLimitName` is optional and blank by default. When an operator configures a matching Programmable Rate Limit on the Verify Service, set this value and Core will pass a keyed phone fingerprint for its buckets. Core always keeps Twilio risk checks enabled and independently limits each source address to five send attempts and twenty code checks per ten minutes.

Phone send and check retries reserve the existing `ops.idempotency_record` before calling Twilio. Concurrent requests with the same key return `PHONE_VERIFICATION_IN_PROGRESS`; completed sends replay `PENDING`, completed checks regenerate the same signed proof, and the OTP itself is never persisted.

## Profile-image storage configuration

PostgreSQL deployments of the API and worker must set `Storage__BlobServiceEndpoint`. Optional settings are `Storage__ManagedIdentityClientId`, `Storage__PendingProfileImagesContainerName` (default `pending-profile-images`), `Storage__ProfileImagesContainerName` (default `profile-images`), and `Storage__ChatFilesContainerName` (default `chat-files`). Blob access uses `DefaultAzureCredential`; no account keys or SAS URLs are returned to clients. The worker consumes `ServiceBus__ContentScanQueueName` (default `content-scan`) for profile and chat uploads. With `MalwareScanning__Required=true`, it waits for the Defender for Storage blob-index malware verdict, then sends safely decoded and normalized images to `ContentSafety__Endpoint`. Images that meet a configured severity threshold and every malicious or failed malware scan are quarantined; an attachment is downloadable only after its aggregate scan status is `CLEAN`. Keep both scanning controls enabled for production. `MalwareScanning__Required=false` is honored only in the Development runtime and is an explicit bypass for Azure regions where Defender for Storage is unavailable; Content Safety remains enforced for images.

## MVP request headers

Swagger displays each applicable header on the operation that consumes it:

| Header | Applies to | Client behavior |
| --- | --- | --- |
| `X-Member-Id` | Member-scoped operations | Optional MVP member selector; defaults to `Mvp__DefaultMemberId` and is limited to 64 characters. The ID must already exist in `iam.member`. |
| `Idempotency-Key` | Every `POST`, `PUT`, `PATCH`, and `DELETE` under `/v1` | Required, maximum 128 characters. Generate a UUID for each new logical action and reuse that same value for retries of that action. Never reuse it with a different payload. |
| `If-Match` | `PATCH /v1/me/profile`, `PUT/DELETE /v1/me/profile-picture` | Send the ETag returned by `GET /v1/me/profile`. It may be omitted only while completing the initial empty draft. |

The UI or other calling client generates `Idempotency-Key`; identity/onboarding supplies the member ID. Durable replay storage is currently implemented only for selected transactional operations, as recorded in the senior architecture review, so extending it to every mutation remains a production-readiness requirement.

## Endpoint groups

- Member, profile, and consent: `POST /v1/members`, `GET/PATCH /v1/me/profile`, `PUT/DELETE /v1/me/profile-picture`, `GET /v1/members/{memberId}`, `GET /v1/members/{memberId}/profile-picture`, `GET /v1/consent-policies/{purposeCode}`, `POST /v1/me/consents`
- Events: `GET /v1/events`, `POST /v1/events/{id}/register`, `POST/DELETE /v1/events/{id}/live-mode`, `POST /v1/events/{id}/presence`
- Admin event matching and attendance: `PUT /v1/admin/events/{id}/matching-policy`, `GET /v1/admin/events/{id}/attendees`, `POST /v1/admin/events/{id}/attendees/{memberId}/check-in`
- Social: `POST/PATCH /v1/connection-requests`, `GET /v1/connections`, `POST /v1/members/block`
- Chat: `GET/POST /v1/conversations/{id}/messages`
- Preferences and privacy: `PATCH /v1/me/notification-preferences`, `POST /v1/me/privacy-requests`
- Offline sync: `GET /v1/sync/changes?cursor={opaque-cursor}&limit={n}`
- Operations: `GET /health`, `GET /ready`, `GET /swagger/v1/swagger.json`, Swagger UI at `/swagger`

## Developer orientation

Read [Architecture and data flow](ARCHITECTURE.md) before changing domain ownership or adding an endpoint. It explains each project, the main request flows, persistence schemas, cross-API events, and the rules that must remain true.

## Adding a new API endpoint

This project uses ASP.NET Core Minimal APIs rather than controller classes. Add a new API operation through the following layers:

```text
HTTP route in Olga.Core.Api
  -> ICoreService/CoreService in Olga.Core.Application
  -> ICoreStore/CoreDbContext in Olga.Core.Infrastructure
  -> PostgreSQL
```

### 1. Define the API contract

Add request and response records to `src/Olga.Core.Contracts/Contracts.cs`. Do not expose domain or EF Core entities directly from an endpoint.

```csharp
public sealed record MemberSearchResponse(
    string MemberId,
    string DisplayName);
```

### 2. Declare the application operation

Add the method to `ICoreService` in `src/Olga.Core.Application/CoreApplication.cs`:

```csharp
Task<IReadOnlyList<MemberSearchResponse>> SearchMembersAsync(
    string searchText,
    CancellationToken ct);
```

### 3. Implement the operation

Implement the method in `CoreService`. Keep authorization, validation, and business rules in the application layer rather than in the HTTP route.

```csharp
public Task<IReadOnlyList<MemberSearchResponse>> SearchMembersAsync(
    string searchText,
    CancellationToken ct)
{
    ct.ThrowIfCancellationRequested();

    var results = store.Profiles
        .Where(x => x.Status == "ACTIVE" && x.DisplayName.Contains(searchText))
        .Select(x => new MemberSearchResponse(x.MemberId, x.DisplayName))
        .ToList();

    return Task.FromResult<IReadOnlyList<MemberSearchResponse>>(results);
}
```

Use `ICoreStore` for normal data access. Add an explicit repository operation when a query or mutation needs database-specific SQL, transactional behavior, or Npgsql parameterization.

### 4. Map the HTTP route

Register the route in `src/Olga.Core.Api/Program.cs`:

```csharp
app.MapGet(
    "/v1/members/search",
    async (string query, ICoreService service, CancellationToken ct) =>
        Results.Ok(await service.SearchMembersAsync(query, ct)))
    .WithName("SearchMembers")
    .WithTags("Members");
```

All public REST routes must be explicitly versioned under `/v1`. Give each operation a unique name and a meaningful Swagger tag. New routes appear automatically in the Swagger UI at `/swagger`.

For `POST`, `PUT`, `PATCH`, and `DELETE` routes, clients must send an `Idempotency-Key` header; the OpenAPI transformer documents it automatically. Map member operations through `memberV1` so Swagger exposes the temporary MVP member selector. Mark concurrency-controlled routes with `IfMatchMetadata` so Swagger exposes `If-Match`. Use `If-Match` with the resource ETag when a mutation can lose concurrent updates. Feeds, chats, and notifications must use opaque cursor pagination.

### 5. Add tests and verify

Add application behavior tests to `tests/Olga.Core.Tests/CoreServiceTests.cs`. Cover the successful result and relevant validation, authorization, idempotency, and concurrency failures.

```powershell
dotnet test Olga.Core.slnx
dotnet run --project src/Olga.Core.Api
```

After starting the API, browse to `/swagger` to inspect and exercise the new endpoint.

The pull-request and environment deployment process is documented in [CI/CD operations](docs/CI_CD.md).
