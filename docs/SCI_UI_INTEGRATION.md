# OLGA SCI UI Integration Guide

This guide describes the current pilot API contract for integrating the Social Capital Index (SCI) into the OLGA UI. It reflects the current `Olga.Core` and `Olga.Nlp` implementations.

## 1. User journey

Implement the pilot in this order:

1. Create a Business or Project.
2. Capture its OFFER and WANT text and submit the paired intent set.
3. Import the owner's contacts.
4. Let the owner select imported contacts and create an invitation batch.
5. Share each returned invitation token through an app-owned invitation URL.
6. Let the authenticated invitee accept the invitation.
7. Display the business's SCI network and refresh it while asynchronous scoring completes.

Use **Business** or **Project** in UI copy. Keep `venture_id` in API payloads and client models.

## 2. API clients and common rules

SCI uses two API services:

| Client | Responsibility |
|---|---|
| Core API | Businesses, contact import, invitations, SCI projection |
| NLP API | Paired OFFER/WANT intent submission and intent status |

Configure the two base URLs separately, for example:

```text
CORE_API_BASE_URL=https://<core-host>
NLP_API_BASE_URL=https://<nlp-host>
```

All JSON request and response properties use `snake_case`. All dates are ISO 8601 timestamps with an offset, preferably UTC, such as `2027-02-02T10:30:00Z`.

Send these headers:

```http
Content-Type: application/json
X-Member-Id: <authenticated-member-id>
Idempotency-Key: <key-for-this-logical-mutation>
```

- Send `X-Member-Id` from the authenticated member context on every SCI request. Do not rely on the APIs' development fallback member.
- `Idempotency-Key` is required for every mutation. A UUID is suitable and must be no more than 128 characters.
- Generate a fresh key for each new logical mutation.
- Reuse the same key and identical request only when retrying that mutation after a timeout or transient failure.
- For `POST /v1/venture-intent-sets`, `intent_set_id` must exactly equal the `Idempotency-Key` header.
- Do not calculate authoritative SCI scores in the UI.
- Do not send imported contact data to NLP.

The examples below use placeholders named `CORE_API_BASE_URL`, `NLP_API_BASE_URL`, and `MEMBER_ID`.

## 3. Create a Business or Project

```http
POST {CORE_API_BASE_URL}/v1/ventures
X-Member-Id: {MEMBER_ID}
Idempotency-Key: 46d81a6f-9457-471d-9fb8-a3e1784b99ce
Content-Type: application/json
```

```json
{
  "venture_id": "venture_01JQ8J2M7X6JBY6Y3H1S6D2P9R",
  "name": "Acme Circular Packaging"
}
```

Success: `201 Created`

```json
{
  "venture_id": "venture_01JQ8J2M7X6JBY6Y3H1S6D2P9R",
  "name": "Acme Circular Packaging",
  "status": "ACTIVE",
  "geography_codes": [],
  "updated_at": "2026-11-04T10:30:00Z",
  "etag": "\"1\""
}
```

The response also includes an `ETag` header. The response includes optional `organization_id`, `industry_code`, `stage_code`, `revenue_band_code`, and `team_size_band_code` properties when they have values.

Recover the member's businesses after sign-in or reinstall with:

```http
GET {CORE_API_BASE_URL}/v1/ventures
X-Member-Id: {MEMBER_ID}
```

Read one owner-scoped business with:

```http
GET {CORE_API_BASE_URL}/v1/ventures/{venture_id}
X-Member-Id: {MEMBER_ID}
```

The detail response also returns its `ETag` header. A business owned by another member is not exposed and returns `404`.

Current validation:

- `venture_id`: required, maximum 64 characters.
- `name`: required, maximum 250 characters.
- `geography_codes`: maximum 20 entries; each entry is at most 64 characters.

## 4. Submit paired OFFER and WANT intents

Create three new stable IDs for every business-intent update:

- `intent_set_id`
- `offer_intent_id`
- `want_intent_id`

Never reuse old intent IDs for a new set. Set the request's `intent_set_id` and the `Idempotency-Key` header to the same value.

```http
POST {NLP_API_BASE_URL}/v1/venture-intent-sets
X-Member-Id: {MEMBER_ID}
Idempotency-Key: intent_set_01JQ8K1SFMKQ6B6ZMDH68W45ZP
Content-Type: application/json
```

```json
{
  "intent_set_id": "intent_set_01JQ8K1SFMKQ6B6ZMDH68W45ZP",
  "venture_id": "venture_01JQ8J2M7X6JBY6Y3H1S6D2P9R",
  "offer_intent_id": "intent_01JQ8K2CKW8V4P7V3E2M3A6B5C",
  "want_intent_id": "intent_01JQ8K2V83A6CH5T9Q1R7D4N2M",
  "offer_text": "We provide recyclable packaging design and small-batch manufacturing.",
  "want_text": "We want introductions to consumer brands and sustainable-material suppliers.",
  "expires_at": "2027-02-02T10:30:00Z",
  "structured_intent": {},
  "category": "BUSINESS",
  "industry": "PACKAGING",
  "geography": "IN",
  "language": "en"
}
```

`structured_intent` must be a non-null JSON value. Send `{}` by default. NLP currently treats a missing `competitor` flag as `false` and a missing `qualification_met` flag as `true`; the UI should not ask members to infer these scoring flags.

Success: `202 Accepted`

```json
{
  "intent_set_id": "intent_set_01JQ8K1SFMKQ6B6ZMDH68W45ZP",
  "venture_id": "venture_01JQ8J2M7X6JBY6Y3H1S6D2P9R",
  "offer": {
    "intent_id": "intent_01JQ8K2CKW8V4P7V3E2M3A6B5C",
    "status": "PROCESSING",
    "model_version": "",
    "preprocessing_version": "v1",
    "contains_pii": false
  },
  "want": {
    "intent_id": "intent_01JQ8K2V83A6CH5T9Q1R7D4N2M",
    "status": "PROCESSING",
    "model_version": "",
    "preprocessing_version": "v1",
    "contains_pii": false
  }
}
```

The response means processing was accepted, not that SCI scoring is complete. The intent set becomes active only after both intents are ready.

### Optional intent polling

Poll each intent when the UI needs explicit processing progress:

```http
GET {NLP_API_BASE_URL}/v1/intents/{intent_id}
X-Member-Id: {MEMBER_ID}
```

Treat both intents reaching `MATCH_READY` as completion of text processing. Stop polling on `FAILED`. Use bounded backoff, pause polling when the app is backgrounded, and provide a manual retry action after a reasonable timeout.

The current API does not expose a `GET /v1/venture-intent-sets/{id}` endpoint.

## 5. Import contacts

The UI must create stable `contact_record_id` and `link_id` values, each no more than 64 characters. One item must include an email, a phone number, or both.

```http
POST {CORE_API_BASE_URL}/v1/contacts/imports
X-Member-Id: {MEMBER_ID}
Idempotency-Key: 524dd158-3086-4eb3-98d4-38e28109ea09
Content-Type: application/json
```

```json
{
  "contacts": [
    {
      "contact_record_id": "contact_01JQ8M0X2D3G7Y5F4A9C6N8P1R",
      "link_id": "link_01JQ8M1QF7R4B8M6K3T2H9D5VW",
      "label": "Priya Sharma",
      "email": "priya@example.com",
      "phone": null
    }
  ],
  "retention_days": 365
}
```

Success: `200 OK`

```json
{
  "imported_count": 1,
  "link_ids": ["link_01JQ8M1QF7R4B8M6K3T2H9D5VW"]
}
```

Current validation:

- 1 to 500 contacts per request.
- `retention_days`: 30 to 730.
- `contact_record_id` and `link_id`: required, maximum 64 characters.
- `label`: required, maximum 250 characters.
- Email or phone is required for every contact.
- IDs must not be duplicated within the request.

Contact labels and lookup identifiers are protected by Core. The UI must not forward raw contact data to NLP or log request bodies containing contact details.

## 6. Create and share invitations

The owner selects imported `link_id` values and optionally associates the batch with the business.

```http
POST {CORE_API_BASE_URL}/v1/invite-batches
X-Member-Id: {MEMBER_ID}
Idempotency-Key: 038e27f2-bb18-4b57-9d1d-a9e459362d6a
Content-Type: application/json
```

```json
{
  "batch_id": "batch_01JQ8N5QV6J9T8R2H7F4P3D1MC",
  "venture_id": "venture_01JQ8J2M7X6JBY6Y3H1S6D2P9R",
  "link_ids": ["link_01JQ8M1QF7R4B8M6K3T2H9D5VW"],
  "expires_in_days": 14
}
```

Success: `201 Created`

```json
{
  "batch_id": "batch_01JQ8N5QV6J9T8R2H7F4P3D1MC",
  "status": "APPROVED",
  "invitations": [
    {
      "invitation_id": "c1d2e3f4a5b6c7d8",
      "link_id": "link_01JQ8M1QF7R4B8M6K3T2H9D5VW",
      "token": "<64-character-hex-token>",
      "expires_at": "2026-11-18T10:30:00Z"
    }
  ],
  "etag": "\"1\""
}
```

Current validation:

- `batch_id`: required, maximum 64 characters.
- 1 to 20 unique `link_ids` per batch.
- `expires_in_days`: 1 to 30.
- If supplied, `venture_id` must identify an active business owned by the caller.

Build an app-owned invitation URL from the returned token, for example:

```text
https://<ui-host>/invitations/<token>
```

Treat tokens as secrets. Do not expose them in analytics, crash reports, or application logs. The current pilot API has no invitation-batch status endpoint, so preserve the creation response if the UI needs to show immediate dispatch state.

## 7. Accept an invitation

After authentication, the invitee accepts the token from the UI route:

```http
POST {CORE_API_BASE_URL}/v1/invitations/{token}/accept
X-Member-Id: {INVITEE_MEMBER_ID}
Idempotency-Key: 1898b876-bdfa-4bc6-bc48-535108bb1642
Content-Type: application/json
```

No request body is required. The token is exactly 64 hexadecimal characters.

Success: `200 OK`

```json
{
  "invitation_id": "c1d2e3f4a5b6c7d8",
  "connection_id": "conn_2f91cdd17f0a4cb8a0c4d69db0ea976a",
  "conversation_id": "chat_e84b4107a9224b9ba3b407893be3d820",
  "link_id": "link_01JQ8M1QF7R4B8M6K3T2H9D5VW"
}
```

On success, show a connected state and optionally navigate to the returned conversation. Reusing the same idempotency key retries the same acceptance operation safely.

## 8. Display the SCI network

```http
GET {CORE_API_BASE_URL}/v1/ventures/{venture_id}/sci?limit=50
X-Member-Id: {MEMBER_ID}
```

`limit` defaults to 50 and must be between 1 and 100.

Success: `200 OK`

```json
{
  "items": [
    {
      "link_id": "link_01JQ8M1QF7R4B8M6K3T2H9D5VW",
      "contact_member_id": "member_01JQ8QK74C2G3S9M5P6R1V8XDW",
      "label": "Priya Sharma",
      "score": 82.0,
      "relationship_role": "CLIENT",
      "competitor_overlay": false,
      "is_stale": false,
      "origin_type": "IMPORT",
      "scored_at": "2026-11-04T10:42:00Z",
      "etag": "\"1\""
    }
  ],
  "next_cursor": null
}
```

Recommended presentation:

- Show `label` as the primary name.
- Render `score` as the returned 0–100 calibrated score; do not rescale or recompute it. Current bands are Emerging (10–24), Relevant (25–49), Strong (50–74), and Exact (75–100).
- Show `relationship_role` as a badge. The current pilot produces roles such as `CLIENT` and `SUPPLIER`.
- Show a competitor indicator when `competitor_overlay` is `true`.
- If `is_stale` is `true`, keep the existing score visible and add an “Updating” indicator.
- `contact_member_id` can be null before a contact is linked to a member.
- Use `scored_at` for “last updated” copy.

### Cursor pagination

When `next_cursor` is non-null, pass it back unchanged and URL-encoded:

```http
GET {CORE_API_BASE_URL}/v1/ventures/{venture_id}/sci?limit=50&cursor={next_cursor}
```

Do not decode, edit, persist assumptions about, or generate cursors in the UI. Append returned items in order and deduplicate defensively by `link_id`.

## 9. Eventual consistency and UI states

SCI processing is asynchronous. A successful intent submission or invitation acceptance does not mean a score is immediately available.

Use these states:

| State | UI behavior |
|---|---|
| Intent processing | Show that the Business profile is being analyzed; optionally poll both intent IDs. |
| SCI processing | Keep the SCI view available and refresh in the background. |
| Empty, processing likely | Show “We’re building your SCI network” with a refresh action. |
| Empty, no contacts/invites | Prompt the owner to import and invite contacts. |
| Stale item | Preserve its score and show “Updating”; do not remove it. |
| Failed request | Preserve entered data, show a retry action, and reuse the same idempotency key only for that exact request. |
| No more pages | Stop pagination when `next_cursor` is null. |

A practical polling policy is 2, 5, 10, and then 15 seconds, capped at 15 seconds while the screen remains visible. Stop automatic polling after a product-defined timeout and leave a manual refresh action. Treat an empty SCI page as valid data, not as an API failure.

## 10. Error handling

Core and NLP errors use `application/problem+json` with this general shape:

```json
{
  "code": "VENTURE_INVALID",
  "message": "The request is invalid or cannot be completed in its current state.",
  "correlation_id": "0HN..."
}
```

Always log `code` and `correlation_id` in sanitized diagnostics. Do not log tokens, contact details, or full request bodies.

Handle at least:

| HTTP status | Meaning | UI action |
|---|---|---|
| 400 | Invalid request or missing idempotency key | Keep form values and show validation/general error. |
| 403 | Caller cannot access the resource | Show access denied; do not retry automatically. |
| 404 | Resource or intent not found | Show not found or return to the preceding step. |
| 409 | ID conflict, state conflict, or idempotency mismatch | Do not silently generate a new key for the same submitted operation; inspect `code`. |
| 413 | Payload too large | Reduce contact batch/request size. |
| 429 | Rate limited | Honor `Retry-After` when present. |
| 503 | Temporary service/database failure | Retry with backoff and the same idempotency key for the same mutation. |

Important codes include:

- `IDEMPOTENCY_KEY_REQUIRED`
- `IDEMPOTENCY_KEY_MISMATCH`
- `VENTURE_INVALID`
- `VENTURE_ID_CONFLICT`
- `VENTURE_INTENT_SET_INVALID`
- `CONTACT_IMPORT_INVALID`
- `CONTACT_IMPORT_DUPLICATE_ID`
- `CONTACT_LINK_ID_CONFLICT`
- `INVITE_BATCH_INVALID`
- `INVITE_BATCH_LINK_INVALID`
- `INVITATION_TOKEN_INVALID`
- `INVITATION_NOT_FOUND`
- `INVITATION_SELF_ACCEPT_INVALID`
- `SCI_QUERY_INVALID`

## 11. Suggested client types

```ts
type VentureResponse = {
  venture_id: string;
  name: string;
  organization_id?: string;
  industry_code?: string;
  stage_code?: string;
  revenue_band_code?: string;
  team_size_band_code?: string;
  status: string;
  geography_codes: string[];
  updated_at: string;
  etag: string;
};

type IntentSummary = {
  intent_id: string;
  status: string;
  model_version: string;
  preprocessing_version: string;
  normalized_hash?: string;
  language?: string;
  contains_pii: boolean;
  updated_at?: string;
  etag?: string;
};

type VentureIntentSetResponse = {
  intent_set_id: string;
  venture_id: string;
  offer: IntentSummary;
  want: IntentSummary;
};

type SciContact = {
  link_id: string;
  contact_member_id?: string;
  label: string;
  score: number;
  relationship_role: string;
  competitor_overlay: boolean;
  is_stale: boolean;
  origin_type: string;
  scored_at: string;
  etag: string;
};

type SciContactPage = {
  items: SciContact[];
  next_cursor?: string;
};

type ApiError = {
  code: string;
  message: string;
  correlation_id: string;
  field_errors?: Record<string, string[]>;
  details?: unknown;
};
```

## 12. Current pilot limitations

Do not design the UI around endpoints that do not yet exist. The current API does not provide:

- Business edit endpoint.
- Contact list, import preview, deduplication, or merge endpoints.
- Invitation batch detail/status or resend endpoints.
- Venture intent-set status endpoint; only individual intents can be read.
- An explicit SCI job-status endpoint.

Preserve successful creation responses in client state where needed. If these capabilities are required for a production-quality flow, scope them as backend follow-up work rather than mocking authoritative server state.

## 13. Integration checklist

- [ ] Configure separate Core and NLP API base URLs.
- [ ] Send the authenticated `X-Member-Id` consistently to both services.
- [ ] Use `snake_case` JSON.
- [ ] Send an `Idempotency-Key` on every mutation.
- [ ] Make `intent_set_id` exactly match its idempotency key.
- [ ] Generate new intent-set, OFFER, and WANT IDs for every intent update.
- [ ] Never send imported contact details to NLP.
- [ ] Treat tokens as secrets.
- [ ] Treat intent creation and SCI scoring as asynchronous.
- [ ] Preserve stale scores while they update.
- [ ] Pass opaque cursors back unchanged.
- [ ] Display server SCI scores without local recalculation.
- [ ] Capture sanitized `code` and `correlation_id` values for support.
- [ ] Disable mock-data mode when connecting to deployed APIs.
