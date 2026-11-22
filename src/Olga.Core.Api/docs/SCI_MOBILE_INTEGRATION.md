# OLGA SCI Mobile Integration Contract v1.0

Status: implementation handoff  
Date: 22 November 2026  
Audience: OLGA mobile development and QA teams

## 1. Integration boundary

The mobile application uses two public APIs:

| API | Mobile responsibility |
|---|---|
| Core API | Ventures, imported contacts, invitations, SCI summary/contact list, reach-out recommendations, relationship outcomes, privacy preference, connections and in-app notifications |
| NLP API | Primary-intent preview, save and history |

Do not call workers, PostgreSQL, Service Bus, `/v1/internal/*`, `/v1/admin/*`, `/v1/matches/score-pair`, or `/v1/normalize` from mobile.

Configure environment-specific values; never hard-code URLs:

```text
CORE_API_BASE_URL=https://<core-host>
NLP_API_BASE_URL=https://<nlp-host>
```

Runtime API descriptions:

```text
Core: {CORE_API_BASE_URL}/swagger/v1/swagger.json
NLP:  {NLP_API_BASE_URL}/openapi/v1.json
```

## 2. Common HTTP contract

- JSON property names use `snake_case`.
- Dates are ISO-8601 timestamps with an offset; send UTC where possible.
- Always send `Accept: application/json` and, for JSON bodies, `Content-Type: application/json`.
- Until the consolidated security implementation is delivered, always send the actual signed-in member as `X-Member-Id`. Do not rely on the development default member.
- Generate a new `Idempotency-Key` for each logical mutation. Reuse it only when retrying the identical request. Maximum length is 128 characters.
- Preserve opaque cursors exactly. Never decode or construct them in mobile.
- Persist returned ETags and send `If-Match` when updating the same resource.
- Capture `X-Correlation-Id` in client logs and support reports.
- Do not calculate SCI, bands, layers, goals or match scores in mobile. Render server values.

Error body (`application/problem+json`):

```json
{
  "code": "RESOURCE_STATE_CONFLICT",
  "message": "The request is invalid or cannot be completed in its current state.",
  "correlation_id": "server-correlation-id",
  "field_errors": null,
  "details": null
}
```

Recommended handling:

| HTTP | Mobile handling |
|---|---|
| 400 | Show validated request error; do not retry unchanged input |
| 403 | Show unavailable/forbidden state |
| 404 | Remove stale local reference or refresh parent screen |
| 409 | Refresh state; for ETag conflicts, show the latest server value |
| 429 | Respect `Retry-After` |
| 503 | Retry with capped exponential backoff and the same idempotency key |

## 3. Primary intent flow

One primary intent is required. A semantic tag change is allowed at most once every 24 hours. Rewording while keeping the same canonical tags is allowed. Always preview before saving.

### 3.1 Create or select a venture — Core

```http
POST /v1/ventures
X-Member-Id: <member_id>
Idempotency-Key: <new_key>
```

```json
{
  "venture_id": "venture_<client-generated-id>",
  "name": "Acme Growth",
  "organization_id": null,
  "industry_code": "technology",
  "stage_code": null,
  "revenue_band_code": null,
  "team_size_band_code": null,
  "geography_codes": ["MY"]
}
```

Related reads:

```text
GET /v1/ventures
GET /v1/ventures/{venture_id}
```

### 3.2 Preview — NLP

This endpoint is replay-safe and does not require an idempotency key.

```http
POST /v1/primary-intents/preview
X-Member-Id: <member_id>
```

```json
{
  "offer_text": "I help manufacturers automate procurement.",
  "want_text": "I want introductions to regional manufacturers.",
  "tags": ["Manufacturing", "Procurement", "Malaysia"]
}
```

Response:

```json
{
  "canonical_tags": ["malaysia", "manufacturing", "procurement"],
  "tag_fingerprint": "<64-character-hash>",
  "previewed_at": "2026-11-22T10:00:00Z",
  "preview_text": "You offer: ..."
}
```

Show `preview_text` to the member. Pass `canonical_tags`, `tag_fingerprint`, and `previewed_at` back unchanged when saving.

Limits: offer and want text are each 1–4,000 characters; 1–50 distinct tags; each canonical tag is at most 64 characters.

### 3.3 Save — NLP

The `intent_set_id` must be identical to the `Idempotency-Key`. This one request creates or updates the offer and want intents; mobile must not call `/v1/intents` separately for this flow.

```http
POST /v1/venture-intent-sets
X-Member-Id: <member_id>
Idempotency-Key: intentset_<client-generated-id>
```

```json
{
  "intent_set_id": "intentset_<client-generated-id>",
  "venture_id": "venture_<id>",
  "offer_intent_id": "intent_offer_<stable-id>",
  "want_intent_id": "intent_want_<stable-id>",
  "offer_text": "I help manufacturers automate procurement.",
  "want_text": "I want introductions to regional manufacturers.",
  "expires_at": "2027-11-22T10:00:00Z",
  "structured_intent": {
    "tags": ["malaysia", "manufacturing", "procurement"]
  },
  "category": null,
  "industry": "manufacturing",
  "geography": "MY",
  "language": "en",
  "tags": ["malaysia", "manufacturing", "procurement"],
  "tag_fingerprint": "<value-from-preview>",
  "previewed_at": "<value-from-preview>"
}
```

Expected response: `202 Accepted`, with `projection_status: "RECALCULATING"`.

After saving:

1. Keep the previous SCI visible with a recalculating indicator.
2. Poll `GET /v1/sci/summary` on Core every 3–5 seconds while `projection_status` is not `READY`.
3. Stop after 60 seconds and offer manual refresh; background processing may continue.
4. When ready, refresh summary, contacts and reach-out recommendations.

A second semantic change inside 24 hours returns `409 RESOURCE_STATE_CONFLICT`. Keep the existing intent and tell the member when they can try again. Do not implement the 24-hour decision locally.

History:

```text
GET /v1/primary-intents/history?limit=50&cursor=<opaque_cursor>
```

## 4. SCI dashboard — Core

### Summary and goal

```text
GET /v1/sci/summary
```

Use the response ETag for caching. Important fields:

- `sci_count`: accepted/Connected contacts whose rounded primary-intent score is 30 or above.
- `potential_count`, `waitlist_count`, `repository_count`, `total_contact_count`, `core_count`.
- `projection_status`: show recalculation state when not `READY`.
- `projection_version`: use to detect a completed refresh.
- `goal`: null for company/event-organiser ownership; otherwise render server-provided value and milestones.
- Never cap the displayed SCI at 1,500. Display `1,742` as `1,742`.

The API already returns whole-number scores. Bands are:

| Rounded score | Band |
|---:|---|
| `< 10` | `REPOSITORY` |
| `10–29` | `WAITLIST` |
| `30+` | `NETWORK` and eligible to count in SCI when Connected |

### SCI contacts

```text
GET /v1/sci/contacts?limit=50&cursor=<opaque_cursor>
```

Use response flags as the authority:

- `counts_in_sci`
- `counts_in_potential`
- `is_estimated`
- `projection_state`
- `frozen_at` / `freeze_reason`
- `layer_code`, `layer_ordinal`, `outcome_points`, `last_activity_at`

Supported lifecycle values are `ESTIMATED`, `INVITED`, `JOINED`, `REQUEST_PENDING`, `CONNECTED`, and `REMOVED`. Removed/blocked history may remain visible as frozen history but must not be treated as active.

### Reach-out recommendations

Initial dashboard call:

```text
GET /v1/sci/reach-out?limit=3
```

Show the first three results. Show “See more” only when `next_cursor` is present, then request the next page using that cursor. Rank by the returned order; do not re-sort locally.

## 5. Imported contacts and invitations — Core

### Import contacts

```http
POST /v1/contacts/imports
X-Member-Id: <member_id>
Idempotency-Key: <new_key>
```

```json
{
  "contacts": [
    {
      "contact_record_id": "contact_<client-generated-id>",
      "link_id": "link_<client-generated-id>",
      "label": "Priya Sharma",
      "email": "priya@example.com",
      "phone": null,
      "job_title": "Operations Director",
      "company": "Example Manufacturing",
      "industry": "manufacturing",
      "city": "Kuala Lumpur"
    }
  ]
}
```

Only name/label, phone, email, job title, company, industry and city may be collected. Do not collect notes, social profiles, photographs or other fields. Omit `retention_days`; retention is a backend policy, not a mobile choice.

Estimated scores never count in SCI and are capped by the backend at 85. Imported contacts are private to their importer.

List and delete:

```text
GET    /v1/contacts?limit=50&cursor=<opaque_cursor>
DELETE /v1/contacts/{link_id}
```

### Invite batch

```http
POST /v1/invite-batches
X-Member-Id: <member_id>
Idempotency-Key: <new_key>
```

```json
{
  "batch_id": "batch_<client-generated-id>",
  "venture_id": "venture_<id>",
  "link_ids": ["link_<id>"],
  "expires_in_days": 14
}
```

Related operations:

```text
GET  /v1/invite-batches/{batch_id}
POST /v1/invitations/{invitation_id}/resend
POST /v1/invitations/{invitation_id}/revoke
POST /v1/invitations/{token}/open       (no Idempotency-Key required)
POST /v1/invitations/{token}/accept
GET  /v1/me/invitations
POST /v1/me/invitations/{invitation_id}/accept
```

Do not show an accepted member who originally imported them.

## 6. Connections, removal and blocking — Core

```text
POST   /v1/connection-requests
PATCH  /v1/connection-requests/{request_id}
GET    /v1/connections
DELETE /v1/connections/{connection_id}
POST   /v1/members/block
DELETE /v1/members/block/{member_id}
GET    /v1/me/blocks
```

All mutations require `Idempotency-Key`. A block or removal immediately excludes the contact from active SCI and layers. Unblocking does not reconnect members; a new connection flow is required. Declines and blocks must not generate user-visible notifications.

## 7. Verified relationship outcomes — Core

Create an outcome only for a Connected member:

```http
POST /v1/relationship-outcomes
X-Member-Id: <member_id>
Idempotency-Key: <new_key>
```

```json
{
  "outcome_id": "outcome_<client-generated-id>",
  "contact_member_id": "<connected-member-id>",
  "outcome_type": "MEETING",
  "occurred_at": "2026-11-22T09:00:00Z",
  "result_type": null
}
```

`outcome_type` is `MEETING`, `INTRODUCTION`, or `RESULT`. `result_type` is required only for `RESULT` and is at most 32 characters.

Decision:

```http
POST /v1/relationship-outcomes/{outcome_id}/decisions
X-Member-Id: <member_id>
Idempotency-Key: <new_key>
```

```json
{ "decision": "CONFIRM" }
```

Decisions are `CONFIRM`, `DENY`, or `WITHDRAW`. A result earns points only after both members confirm. Confirmation deadlines are returned by the API: meeting 7 days, introduction 14 days, result 30 days. Verified outcomes remain valid for 12 months and may be withdrawn within 30 days.

## 8. In-app notifications and push — Core

Feed:

```text
GET /v1/notifications?limit=20&cursor=<opaque_cursor>
```

Register or revoke a mobile device:

```text
PUT    /v1/me/devices/{device_id}
DELETE /v1/me/devices/{device_id}
```

Registration body:

```json
{
  "platform": "IOS",
  "app_version": "1.0.0",
  "push_provider": "APNS",
  "push_token": "<token>"
}
```

Android uses `platform: "ANDROID"` and `push_provider: "FCM"`.

Update a notification preference:

```http
PATCH /v1/me/notification-preferences
X-Member-Id: <member_id>
Idempotency-Key: <new_key>
```

```json
{
  "purpose_code": "<server-supported-purpose>",
  "push_enabled": true,
  "email_enabled": false,
  "quiet_start_local": "22:00:00",
  "quiet_end_local": "08:00:00",
  "timezone_id": "Asia/Kuala_Lumpur"
}
```

There is no WhatsApp or SMS notification channel in Phase 1.

## 9. Discovery privacy — Core

Default is off. Read before editing:

```text
GET /v1/me/discovery-preference
```

Update using the ETag returned in the response/header:

```http
PUT /v1/me/discovery-preference
X-Member-Id: <member_id>
Idempotency-Key: <new_key>
If-Match: "<etag>"
```

```json
{
  "find_by_phone": false,
  "find_by_email": false
}
```

## 10. Client refresh strategy

- On foreground/resume: refresh SCI summary and notification first page.
- After intent save: poll summary until `READY`, then refresh contacts and reach-out.
- After connection, removal, block or outcome decision: refresh summary and affected contact.
- For paged lists, refresh from the first page; do not reuse an old cursor after a mutation.
- The optional incremental endpoint `GET /v1/sync/changes` may be used by the existing app sync layer, but it does not replace authoritative resource reads.
- Never optimistically add to SCI. Wait for the server projection.

## 11. Backend contract gaps affecting mobile

These are backend/API gaps, not mobile workarounds:

1. The notification feed returns template/resource metadata but no display title/body, and there is no read/dismiss mutation. Mobile cannot implement a complete notification inbox without an agreed local template catalogue or an API enhancement.
2. Relationship outcomes have create/decision endpoints but no member list/detail endpoint. An incoming confirmation screen cannot reliably restore state after restart from the current contract.
3. Primary-intent history returns metadata only; there is no endpoint returning the full current intent set. Mobile cannot reconstruct the current edit form after reinstall/logout unless it retains the intent IDs and reads both underlying intents.
4. `expires_at` is required from mobile although the approved business rules do not define a primary-intent expiry. Backend/product must define a server-managed value or an approved duration; mobile must not invent policy.
5. Notification preferences support update but no read/list endpoint for all current preferences or supported purpose codes.
6. Security is intentionally deferred. `X-Member-Id` is the current integration identity and must be replaced consistently when the consolidated authentication design is implemented.

Do not hide these gaps with client-only state. Complete the affected screens after the corresponding API contracts are agreed.

## 12. Mobile acceptance checks

- A returned score of 30 appears in Network and may count only when `counts_in_sci` is true.
- Estimated score never displays above 85 and never increments SCI.
- Pending/imported/invited contacts never increment SCI.
- The second semantic intent change within 24 hours shows the server conflict and preserves the current intent.
- A one-sided meeting confirmation shows zero points.
- SCI 1,742 displays as 1,742, without a 1,500 cap.
- A removed or blocked contact leaves active SCI immediately and appears only as server-provided frozen history.
- Initial reach-out displays three items; “See more” uses `next_cursor`.
- Quiet hours and notification toggles use the member's selected IANA timezone.
- No screen calls admin, internal, worker or database interfaces.
