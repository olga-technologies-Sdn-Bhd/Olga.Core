# OLGA Connect Core API Architecture and Data Flow

## Purpose and boundary

The Core API is the policy and transaction authority for the OLGA Connect product. It owns members and profiles, consent, event participation, Live Mode and presence, social relationships, chat authorization, notification preferences, privacy-request initiation, mobile sync records, and product outbox events.

The Core API does not own NLP intents, embeddings, model versions, ranking configuration, match execution, explanations, feedback, suppression, or evaluation. Those belong to the separate `Olga.Nlp` repository. A semantic score is never permission: Core-owned consent, event, member, connection, and block state always decides whether discovery or communication is allowed.

## Solution projects

| Project | Responsibility | Dependency direction |
| --- | --- | --- |
| `Olga.Core.Domain` | Product entities, lifecycle state, and domain error type. Contains no web, database, or NLP dependencies. | None |
| `Olga.Core.Contracts` | JSON request/response and error contracts exposed at the HTTP boundary. | None |
| `Olga.Core.Application` | Use cases and invariant enforcement. Produces sync changes and outbox events in the same unit of work as business changes. | Domain, Contracts |
| `Olga.Core.Infrastructure` | EF Core/Npgsql unit of work, PostgreSQL schema mapping, indexes, and local seed data. | Application, Domain |
| `Olga.Core.Api` | Minimal API endpoints, correlation/error handling, identity extraction, OpenAPI, health, and readiness. | Application, Contracts, Infrastructure |
| `Olga.Core.Worker` | Publishes transactional outbox events to Service Bus and consumes NLP completion events into Core-owned notification and sync projections. | Infrastructure |
| `Olga.Core.Tests` | Boundary and invariant tests for ETag updates, consent-gated Live Mode, connection/chat creation, idempotent messages, and blocking. | Application, Infrastructure |

Dependencies point inward. Domain and Contracts never reference EF Core or ASP.NET. Application depends on the `ICoreStore` abstraction; Infrastructure implements it.

## Owned data

The EF mappings preserve the architecture document's schema boundaries:

- `core`: `MemberProfile`
- `consent`: `MemberConsent`, `PrivacyRequest`
- `event`: `Event`, `EventRegistration`, `LiveModeSession`, `EventPresence`
- `social`: `ConnectionRequest`, `Connection`, `MemberBlock`
- `chat`: `Conversation`, `Message`
- `notification`: `NotificationPreference`
- `ops`: `SyncChange`, `OutboxEvent`

The current code models only the implemented MVP slice. Missing tables from the database design are delivery gaps, not implicit ownership by NLP.

## Profile update flow

```text
Mobile client -> PATCH /v1/me/profile + If-Match
  -> API derives member identity
  -> Application validates fields and ETag
  -> Update core.MemberProfile
  -> Add member-scoped ops.SyncChange
  -> Add ops.OutboxEvent MemberProfileChanged.v1
  -> One SaveChanges transaction
  -> Return new ETag
```

The client cannot supply a member ID for this operation. An existing profile requires `If-Match`; a stale value returns `RESOURCE_VERSION_CONFLICT`.

## Event Live Mode and presence flow

```text
Register for event
  -> event.EventRegistration

Grant LIVE_MODE consent
  -> append consent.MemberConsent

Start Live Mode
  -> require published event + registration + latest consent = GRANTED
  -> create one active event.LiveModeSession with bounded expiry
  -> emit authorization-safe SyncChange and LiveModeChanged.v1

Refresh presence
  -> require current active session
  -> reject stale observation
  -> store coarse cell with expiry no later than session expiry
```

Withdrawing Live Mode consent revokes all active sessions immediately. Coarse cells are never returned through member endpoints or placed in event payloads.

## Connection and chat flow

```text
Sender -> connection request
  -> reject self, block, duplicate connection, or duplicate open pair
  -> social.ConnectionRequest + ConnectionRequestCreated.v1

Recipient -> ACCEPT
  -> re-check pending state, expiry, and block
  -> create canonical social.Connection pair
  -> create exactly one chat.Conversation
  -> create member-scoped sync projections for both members
  -> emit ConnectionAccepted.v1

Participant -> send message
  -> verify active connection and block state on every call
  -> deduplicate by client MessageId
  -> assign monotonically increasing ServerSequence
  -> write chat.Message, sync records, and MessageCreated.v1
```

A block is directional as evidence but suppresses discovery and communication in both directions. It changes an active connection to `BLOCKED`, emits protected tombstones to both members, and the API never discloses who initiated the block.

## Offline synchronization flow

Every business mutation that affects a mobile read model writes an authorization-scoped `ops.SyncChange`. `GET /v1/sync/changes` returns only global changes or changes scoped to the MVP-selected member, ordered by `SyncSequence`, with a bounded page and next cursor.

The production implementation still needs snapshot bootstrap, cursor-retention detection with `SYNC_CURSOR_EXPIRED`, acknowledgement policy, protected tombstones, and logout/device-revocation purge integration.

## NLP integration flow

```text
Core business mutation
  -> authoritative Core tables
  -> minimal versioned OutboxEvent
  -> transport publisher
  -> NLP refreshes/re-queries approved eligibility projections

Client match request -> directly exposed NLP API
  -> NLP obtains bounded eligible members and pair relationships from approved read-only views
  -> NLP filters before ranking
  -> NLP ranks and persists results in nlp schema
  -> NlpMatchRequestCompleted.v1
  -> Core notification/sync workers create product-visible outcomes
```

Core does not proxy client requests to NLP and does not expose internal NLP projection endpoints. Clients call the NLP API directly through the approved API gateway. NLP reads eligibility and relationship state from the least-privilege, read-only views `nlp.vw_member_context_eligibility` and `nlp.vw_member_relationship`. Core remains authoritative for consent, event participation, connections, and blocks.

## Security invariants

- No client connects directly to Azure Database for PostgreSQL, Blob Storage, or messaging infrastructure.
- During the initial MVP, member context comes from optional `X-Member-Id` and otherwise uses `Mvp__DefaultMemberId`; it is not an authenticated identity and must not be exposed to public or sensitive member data.
- The worker uses managed identity for Service Bus access; the HTTP API intentionally has no caller authentication in this open MVP configuration.
- Identity registration owns and creates `iam.member`. Profile onboarding never creates an identity row. `GET` or `PATCH /v1/me/profile` idempotently provisions a private `DRAFT` profile only after its parent member exists; an unknown identity fails with `MEMBER_NOT_REGISTERED`. PostgreSQL uses `ON CONFLICT DO NOTHING` so concurrent onboarding requests converge on one row. Drafts cannot be discovered or initiate connections. Existing lifecycle states are never overwritten or reactivated by profile updates, and only profile completion promotes a draft to `ACTIVE`.
- Every `/v1` mutation requires a client-generated `Idempotency-Key`; retries of one logical action reuse the key. Concurrency-controlled updates use the last returned ETag in `If-Match`. Swagger documents these headers on applicable operations.
- Consent and authorization fail closed.
- Presence is coarse, short-lived, and never exposed to another member.
- Chat authorization is re-evaluated on every read and send.
- Outbox and telemetry payloads exclude raw messages, intent text, location cells, credentials, and tokens.
- Core runtime identity must not receive write access to the `nlp` schema; NLP runtime identity must not receive write access to Core-owned schemas.

## Delivery status

The implemented slice demonstrates the solution boundary and the highest-risk interaction invariants. It is not yet production complete. The prioritized gaps and acceptance gates are maintained in [Senior architecture review](docs/SENIOR_ARCHITECT_REVIEW.md).

## PostgreSQL v2.4 integration

The code model follows the physical `lower_snake_case` names in the database repository. Profile summary and role category map to `professional_summary` and `role_category`; consent resolves an active `consent_policy` and records its immutable `policy_id`; Live Mode stores the required consent evidence and uses `ACTIVE`, `DISABLED`, and `EXPIRED` lifecycle values.

PostgreSQL deployments execute `social.accept_connection_request`, `chat.save_message`, and `chat.save_message_receipt` through typed Npgsql parameters. Those database-owned functions are the transaction boundary for authorization rechecks, idempotency records, participant creation, sync changes, and outbox events. The in-memory profile retains equivalent application logic for isolated tests only.

Every `/v1` mutation requires a bounded `Idempotency-Key`. Sync pagination exposes the numeric database sequence only as an opaque Base64 cursor. Mutable mapped resources use trigger-generated `row_version` concurrency tokens and API ETags.
