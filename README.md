# OLGA Connect Core API

The Core API is the authoritative product API for OLGA Connect. It owns member-facing product state and policy enforcement. It deliberately contains no text normalization, embedding, semantic ranking, model evaluation, or other NLP/AI implementation.

The repository is independent from `Olga.Nlp` and can be versioned, built, tested, deployed, and granted database permissions separately. During the MVP both APIs may use one PostgreSQL 17 database hosted on Azure Database for PostgreSQL Flexible Server, but each runtime must receive least-privilege access only to its owned schemas, views, and functions.

## What is implemented

- Atomic member registration with encrypted email/phone identities and a private draft profile.
- Profile reads and optimistic-concurrency updates with ETags.
- Append-only consent decisions, including immediate Live Mode revocation.
- Event listing, registration, bounded Live Mode, and expiring coarse presence.
- Connection request, acceptance, canonical connection, conversation creation, and blocking.
- Idempotent client message IDs and server message ordering.
- Notification preferences, privacy-request initiation, and authorization-filtered sync changes.
- Transactional outbox records published as versioned Service Bus envelopes by the Core worker.
- Consumption of `NlpMatchRequestCompleted.v1` into authorization-scoped sync projections and policy-resolved match notifications.
- Transactional integration events that allow NLP to refresh its read-only eligibility projections without proxying NLP requests through Core.
- EF Core InMemory local development and PostgreSQL configuration through `ConnectionStrings__PostgreSql`.
- Anonymous MVP access for all API operations, with an optional member selector header.

## What is intentionally not implemented yet

This foundation is not the full product backlog. Entra token validation and external-identity linking, fine-grained permissions, private file lifecycle, provider notification delivery, privacy task orchestration, retention execution, moderation/admin APIs, database migrations, OpenTelemetry, and remaining production assets remain delivery work. See [Senior architecture review](docs/SENIOR_ARCHITECT_REVIEW.md).

## Run locally

```powershell
dotnet restore Olga.Core.slnx
dotnet test Olga.Core.slnx
dotnet run --project src/Olga.Core.Api
```

All endpoints are anonymous for the initial MVP. Member-scoped endpoints use the optional `X-Member-Id` header to select a member and otherwise fall back to `Mvp__DefaultMemberId` (`A123` by default). Local development seeds members `A123`, `B456`, `D111` plus `event-001`. Do not treat this member selector as authentication or expose this deployment to public or sensitive member data.

## Member creation and onboarding

The client calls member registration only after the upstream email OTP step succeeds. The client generates an `Idempotency-Key`; Core generates the stable member ID.

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
  "phone": "+919876543210",
  "headline": "Software Engineer",
  "professional_summary": "Building professional connections",
  "role_category": "ENGINEERING",
  "locale": "en-IN",
  "visibility": "MEMBERS"
}
```

At least one of `email` or `phone` is required. Phone numbers must already use E.164 format. Registration atomically creates:

- `iam.member` with an API-generated `member_id` and `ACTIVE` account status.
- One `iam.member_identity` row per supplied contact.
- `core.member_profile` with `DRAFT` profile status.

Email is treated as verified by the completed upstream OTP step, so both the email identity and member receive the database transaction timestamp in `verified_at`. Phone is stored with `verified_at = NULL` until phone verification is implemented.

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
| `POST /v1/me/consents` | Record a decision against an active consent-policy version. |
| `GET /v1/events` | List published or active events. No member header is required. |
| `POST /v1/events/{eventId}/register` | Register the selected member for an event. |
| `POST /v1/events/{eventId}/live-mode` | Start Live Mode after event registration and Live Mode consent. |
| `POST /v1/connection-requests` | Create a request from an active, visible profile. |
| `GET /v1/connections` | List the selected member's active connections. |
| `GET/POST /v1/conversations/{conversationId}/messages` | Read or send messages after an active connection creates the conversation. |
| `GET /v1/sync/changes` | Read authorization-scoped mobile changes using the opaque cursor. |

## Identity protection configuration

PostgreSQL deployments must provide `IdentityProtection__MasterKeyBase64` as a Key Vault-backed secret containing exactly 32 cryptographically random bytes encoded as Base64. Core derives separate encryption and lookup keys from this one master secret. Local in-memory development generates an ephemeral key automatically.

Do not commit the master key, print it in logs, or rotate/delete it without a migration that re-encrypts existing identity rows. Losing the key makes existing ciphertext unrecoverable.

## MVP request headers

Swagger displays each applicable header on the operation that consumes it:

| Header | Applies to | Client behavior |
| --- | --- | --- |
| `X-Member-Id` | Member-scoped operations | Optional MVP member selector; defaults to `Mvp__DefaultMemberId` and is limited to 64 characters. The ID must already exist in `iam.member`. |
| `Idempotency-Key` | Every `POST`, `PUT`, `PATCH`, and `DELETE` under `/v1` | Required, maximum 128 characters. Generate a UUID for each new logical action and reuse that same value for retries of that action. Never reuse it with a different payload. |
| `If-Match` | `PATCH /v1/me/profile` | Send the ETag returned by `GET /v1/me/profile`. It may be omitted only while completing the initial empty draft. |

The UI or other calling client generates `Idempotency-Key`; identity/onboarding supplies the member ID. Durable replay storage is currently implemented only for selected transactional operations, as recorded in the senior architecture review, so extending it to every mutation remains a production-readiness requirement.

## Endpoint groups

- Member, profile, and consent: `POST /v1/members`, `GET/PATCH /v1/me/profile`, `GET /v1/members/{memberId}`, `POST /v1/me/consents`
- Events: `GET /v1/events`, `POST /v1/events/{id}/register`, `POST/DELETE /v1/events/{id}/live-mode`, `POST /v1/events/{id}/presence`
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
