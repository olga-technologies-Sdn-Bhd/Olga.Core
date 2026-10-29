# Mobile Phone OTP Integration

This guide describes how the mobile application integrates with the OLGA Core Twilio Verify flow. Core owns the API contract and Twilio interaction. The mobile application never calls Twilio directly.

## Important MVP boundary

Phone OTP currently proves possession of a phone number. The API still uses the temporary open-MVP `X-Member-Id` selector for member-scoped operations; it does not yet issue a production authentication session or access token. Do not treat `X-Member-Id` as authentication or expose the MVP deployment to public or sensitive member data.

## API summary

| Step | Endpoint | Success |
| --- | --- | --- |
| Send OTP | `POST /v1/auth/phone/verifications` | `202 Accepted` |
| Check OTP | `POST /v1/auth/phone/verification-checks` | `200 OK` |
| Register new member | `POST /v1/members` | `201 Created` |
| Find existing member | `POST /v1/members/lookup/phone` | `200 OK` |

Every `POST` requires an `Idempotency-Key` header. Generate a UUID for each new logical action. Reuse the same key only when retrying that exact request; changing the phone, code, or payload requires a new key.

Phone numbers must be normalized to E.164 before submission, for example `+919876543210`. Do not send spaces, parentheses, or local-format numbers.

## 1. Send the OTP

```http
POST /v1/auth/phone/verifications
Idempotency-Key: 81f1170f-11d6-4e4f-9279-54cc27826ac6
Content-Type: application/json
```

```json
{
  "phone": "+919876543210",
  "channel": "sms"
}
```

Successful response:

```http
HTTP/1.1 202 Accepted
```

```json
{
  "status": "PENDING"
}
```

Disable the resend button while the request is active and apply a client countdown of at least 60 seconds. Retrying because of a timeout or lost response must reuse the same `Idempotency-Key`; Core then returns the existing `PENDING` result without intentionally sending another SMS.

## 2. Check the OTP

```http
POST /v1/auth/phone/verification-checks
Idempotency-Key: 9a9f5189-8e84-4a52-b1ce-2e822f44cb9f
Content-Type: application/json
```

```json
{
  "phone": "+919876543210",
  "code": "123456"
}
```

Successful response:

```json
{
  "verification_token": "<opaque signed proof>",
  "expires_at": "2026-10-29T12:10:00Z"
}
```

The verification token is bound to the submitted phone and expires after 10 minutes by default. Keep it only in application memory until registration or lookup finishes. Never log, persist, include in analytics, or send it to another service.

If the check response is lost, retry the identical phone and code with the same `Idempotency-Key`. Core regenerates the same proof without intentionally checking the approved Twilio verification again.

## 3A. Register a new member

Use the proof returned by the check operation:

```http
POST /v1/members
Idempotency-Key: e8e4e7ed-30eb-48ed-83d4-0eaecf9dcb59
Content-Type: application/json
```

```json
{
  "display_name": "New Member",
  "phone": "+919876543210",
  "phone_verification_token": "<opaque signed proof>",
  "locale": "en-IN",
  "visibility": "MEMBERS"
}
```

Every phone supplied during registration requires a valid phone-verification token, including when an email is also supplied. The phone in the registration request must exactly match the phone used for verification after E.164 normalization.

Successful response:

```json
{
  "member_id": "mem_033ed47ab6da5f5ed7c51647fdcde24b",
  "phone_hint": "*********3210",
  "profile_status": "DRAFT",
  "etag": "\"1\""
}
```

For subsequent MVP member-scoped calls, use the returned `member_id` as `X-Member-Id` until production authentication is introduced.

## 3B. Continue as an existing member

```http
POST /v1/members/lookup/phone
Idempotency-Key: b025f332-f06a-482a-a9cd-997f5981125b
Content-Type: application/json
```

```json
{
  "phone": "+919876543210",
  "verification_token": "<opaque signed proof>"
}
```

Successful response:

```json
{
  "member_id": "mem_033ed47ab6da5f5ed7c51647fdcde24b",
  "display_name": "Existing Member",
  "profile_status": "ACTIVE",
  "etag": "\"4\""
}
```

Use the returned `member_id` as the temporary MVP `X-Member-Id` value.

## Error handling

Errors use `application/problem+json` and include `code`, `message`, and `correlation_id`.

| HTTP | Code | Mobile behavior |
| --- | --- | --- |
| `400` | `MEMBER_PHONE_INVALID` | Ask the user to correct the country code/phone format. |
| `400` | `PHONE_VERIFICATION_CODE_INVALID` | Keep the verification screen open and allow another code entry. Use a new idempotency key for a different code. |
| `403` | `PHONE_VERIFICATION_REQUIRED` | Start or complete OTP verification before registration/lookup. |
| `403` | `PHONE_VERIFICATION_INVALID` | The proof is expired, malformed, or belongs to a different phone. Start a new OTP flow. |
| `404` | `MEMBER_NOT_REGISTERED` | Offer new-member registration after successful phone verification. |
| `409` | `IDEMPOTENCY_KEY_REUSED` | Generate a new key because the existing key was reused with different input. |
| `409` | `PHONE_VERIFICATION_IN_PROGRESS` | Keep the same key and retry after a short delay; do not create another logical action. |
| `409` | `MEMBER_IDENTITY_ALREADY_REGISTERED` | Switch to existing-member lookup rather than registration. |
| `429` | `PHONE_VERIFICATION_RATE_LIMITED` | Respect `Retry-After` when present, disable send/check actions, and show a neutral retry-later message. |
| `503` | `PHONE_VERIFICATION_UNAVAILABLE` | Preserve the current screen and retry the identical request with the same key using bounded exponential backoff. |

Do not automatically retry permanent `400`, `403`, `404`, or `409 IDENTITY_KEY_REUSED` responses. Automatic retries are appropriate only for transport failures, `503`, and selected `409 PHONE_VERIFICATION_IN_PROGRESS` responses.

## Recommended mobile state machine

1. `ENTER_PHONE`: validate and normalize the phone locally.
2. `SENDING`: submit the send request once with a new idempotency key.
3. `ENTER_CODE`: start the resend countdown and accept the OTP.
4. `CHECKING`: submit the code once with a new idempotency key.
5. `VERIFIED`: retain the proof in memory and choose registration or lookup.
6. `ONBOARDING`: call member registration or lookup with a third idempotency key.
7. `COMPLETE`: discard the OTP, proof, and temporary request state.

Persisting the send/check idempotency keys briefly is acceptable so an app restart or transport retry can resume the same logical request. Never persist the OTP or verification token.

## Security and privacy requirements

- Never write the full phone, OTP, Twilio response, or verification token to logs, crash reports, analytics, screenshots, or support tooling.
- Mask the phone in UI outside the phone-entry screen.
- Use TLS only and reject non-HTTPS API base URLs outside local development.
- Do not call Twilio directly or embed Twilio credentials in the mobile application.
- Do not interpret `member_id` or `X-Member-Id` as a production access token.
- Clear the OTP and proof when verification expires, the phone changes, the user cancels, or onboarding completes.

## QA scenarios

- Correct OTP for a new phone completes registration.
- Correct OTP for an existing verified phone completes lookup.
- Wrong OTP shows a recoverable validation error.
- A send timeout retried with the same key does not intentionally send a second SMS.
- A successful check retried with the same key returns the same proof.
- Reusing a key with a different phone or code returns `IDEMPOTENCY_KEY_REUSED`.
- Rapid send/check attempts return `429` and the UI respects `Retry-After`.
- An expired proof cannot register or look up a member.
- A proof for phone A cannot be used with phone B.
- App backgrounding, restart, and cancellation do not leak OTP or proof values.
