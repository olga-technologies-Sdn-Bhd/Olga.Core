# OLGA Connect Core and NLP API Senior Architecture Review

## Executive decision

Keep two independent repositories and deployable APIs: `Olga.Core` for product authority and `Olga.Nlp/olga-nlp-api` for NLP processing and ranking. They may share one PostgreSQL 17 database on Azure Database for PostgreSQL Flexible Server for the MVP only when schema ownership, runtime identities, migrations, and release responsibilities remain explicit.

The separation is correct and materially reduces the risk that matching logic can grant product access. Neither repository is production-ready today. Core is an intentionally bounded implementation foundation; NLP has broader functional depth and passing tests but still contains production integration placeholders.

## Repository boundary

| Concern | Core API authority | NLP API authority |
| --- | --- | --- |
| Member identity and profile | Authoritative | Read-only approved projection |
| Consent, events, Live Mode, presence | Authoritative | Eligibility input only |
| Connections, blocks, chat | Authoritative | Relationship exclusion input only |
| Notification, sync, privacy, moderation | Authoritative product workflow | Emits minimal events and supplies domain task results |
| Intent text and normalization | No ownership | Authoritative |
| Embeddings, models, ranking, explanations | No ownership | Authoritative |
| Match result visibility | Applies product authorization and delivery policy | Computes eligible ranked results |

The canonical NLP repository is `D:\OLGA\Projects\Olga.Nlp\olga-nlp-api`. The older duplicate at `D:\OLGA\Projects\Olga.Nlp\src` is a governance risk because it can be built or edited accidentally. Archive or remove it only after confirming no uncommitted work or external pipeline refers to it.

## What is sound

### Core API

- Independent repository and inward dependency direction.
- Domain ownership excludes all NLP/AI implementations.
- Schema-qualified EF mappings align to the approved modular-monolith direction.
- Core checks consent, registration, event state, blocks, and active connections before sensitive state transitions.
- Business state, sync projection, and outbox messages share one EF unit of work.
- Client message IDs are replay-safe and server ordering is explicit.
- Tests cover several security-critical negative paths.

### NLP API

- Clear ownership of intent normalization, embeddings, match requests/results, feedback, and evaluation.
- Candidate eligibility is treated as a prerequisite rather than a ranking feature.
- Match requests persist request hash and processing/model/ranking versions for reproducibility.
- Normal search reuses stored embeddings and bounds candidate retrieval before in-process ranking.
- Responses avoid vectors and sensitive relationship/location internals.
- Existing verification baseline passes 13 tests: 4 unit, 6 integration, and 3 contract tests.

## Critical gaps before a shared Azure test environment

### Priority 0 security and correctness

1. If the open MVP posture is later retired, configure and verify OIDC/JWT bearer validation in each environment for both APIs. A static service token is not a replacement for workload identity.
2. Implement application permission resolution and object-level authorization tests for member, admin, moderator, and NLP evaluator roles.
3. Create version-controlled PostgreSQL migrations for the full approved schemas, read-only NLP projections, grants, constraints, identity columns, the shared `row_version` increment trigger, and seed data. The code mappings alone are not a database delivery artifact.
4. Provision and operate the Service Bus integration topic/subscriptions used by the Core worker, including duplicate detection, subscription filters, dead-letter alerts, and operator recovery for exhausted database outbox publishes.
5. Replace NLP's `AzureEmbeddingProvider` placeholder and validate model identity, dimensions, timeout, retry, content handling, and managed-identity authentication.

### Priority 1 MVP capability gaps

1. Core: implement CIAM lifecycle and `AuthSession`, Role/Permission mapping, device revocation, and security audit evidence.
2. Core: implement `FileAsset`/`FileAssetLink`, short-lived upload/download URLs, type/hash validation, scanning, and lifecycle states.
3. Core: implement notification policy resolution, preferences, quiet hours, dedupe, caps, retry attempts, and provider-token revocation.
4. Core: add message receipts, connection deletion, reports, moderation/admin APIs, privacy tasks, retention policies/executions, background jobs, and audit events.
5. Core: complete sync snapshot bootstrap, expired-cursor handling, tombstones, logout purge contract, and resource-version conflict behavior.
6. Both: publish executable OpenAPI contracts and consumer-driven contract tests for Core-to-NLP projections and NLP completion events.

### Priority 2 operability and scale

1. Add OpenTelemetry traces, structured safe logging, metrics, dashboards, and alerts for latency, authorization denials, DB saturation, outbox lag, worker retries, and embedding backlog.
2. Run representative concurrency and data-volume tests against Azure Database for PostgreSQL Flexible Server. Confirm cached match p95 at or below 1.5 seconds and mutation acknowledgement p95 at or below 500 ms at the agreed load.
3. Validate row-level contention, canonical-pair uniqueness, filtered unique indexes, idempotency-key reuse, and outbox concurrency under parallel requests.
4. Execute point-in-time restore and verify the initial RPO of 15 minutes and RTO of 4 hours.
5. Add threat modeling and privacy reviews for intent text, coarse presence, chat, file assets, evaluation datasets, support exports, and telemetry.

## Design corrections recommended during implementation

- Replace application-managed profile version increments with the shared PostgreSQL `BEFORE UPDATE` trigger that increments a `bigint row_version`, mapped as an EF Core concurrency token and exposed as an ETag.
- Introduce a durable `ops.IdempotencyRecord` for every externally retryable mutation; current Core replay behavior covers only selected operations.
- Keep application services cohesive by bounded domain. Split the current `CoreService` into Profile, Consent/Event, Social/Chat, Sync, Privacy, and NLP-projection use cases as functionality grows.
- Prefer one integration mechanism per decision: read-only SQL views for high-volume candidate filtering or service calls for strict runtime isolation. Record the choice and failure behavior.
- Never put raw intent text, message bodies, feedback text, presence cells, identity subjects, blob paths, or provider payloads in outbox, sync, or telemetry records.
- Treat the static service token as local bootstrap only. Use managed workload identity and audience-scoped tokens between deployed services.

## Release gates

The system is ready for QA handover only when both repositories build from clean checkout and the following evidence is retained:

- repeatable database creation and previous-baseline upgrade;
- positive and negative API authorization matrix;
- consent grant, withdrawal, Live Mode expiry, presence purge, block, and chat-denial tests;
- outbox retry and dead-letter recovery tests;
- idempotency and concurrency tests under parallel load;
- private file scan and authorization lifecycle tests;
- NLP embedding, eligibility, ranking, explanation, feedback, and evaluation traceability;
- sync delta, tombstone, conflict, expired-cursor, and logout-purge tests;
- privacy task and retention execution evidence;
- performance, dependency failure, restore, observability, accessibility, and security reports.

## Recommended implementation order

1. Freeze CIAM, retention, event/location, notification, and QA-load decisions.
2. Deliver database migrations, grants, seed data, and approved NLP read projections.
3. Configure production authentication and application authorization in both APIs.
4. Publish versioned OpenAPI and cross-service event contracts.
5. Complete Core file, notification, sync, privacy/retention, moderation, and audit workflows.
6. Complete NLP Azure embedding and production worker/integration paths.
7. Provision Azure test infrastructure, observability, backup, and recovery controls.
8. Run the measurable QA gates and remediate all blocker, critical, and high-severity findings.
