# AnonPulse administrative API

This document is the backend contract for the first administration stage. All
`/api/admin/*` routes require an ASP.NET Core Identity authentication cookie and
the `admin` role. Read responses use `Cache-Control: no-store`. Authentication
and authorization failures are JSON `ProblemDetails` responses, not redirects.

## CSRF for state-changing requests

Before `PATCH` or `DELETE`, an authenticated browser must call:

```http
GET /api/auth/csrf-token
```

```json
{ "token": "opaque-request-token" }
```

The browser sends the returned value in `X-CSRF-TOKEN`. The associated
`__Host-AnonPulse.Csrf` cookie is `HttpOnly`, `Secure`, `SameSite=Strict`, and
scoped to `/`. Missing, mismatched, or invalid tokens return
`400 application/problem+json` with `code = "csrf_validation_failed"`.

The frontend must use credentials on both requests. With the current
`SameSite=Strict` authentication design, the production frontend and API must be
deployed in a same-site topology; a cross-site frontend requires a separately
reviewed cookie and CSRF design.

## Dashboard

`GET /api/admin/dashboard`

Returns role counts, active and administratively blocked accounts, poll counts
by status, total participation count, number of polls with a published
decision, and one UTC day bucket for each of the last 30 days.

```json
{
  "users": {
    "total": 12,
    "active": 11,
    "blocked": 1,
    "byRole": [{ "role": "teacher", "count": 10 }, { "role": "admin", "count": 2 }]
  },
  "polls": {
    "total": 20,
    "draft": 3,
    "open": 5,
    "closed": 12,
    "withPublishedDecisions": 6
  },
  "totalParticipations": 125,
  "pollCreationLast30Days": [{ "date": "2026-10-23", "count": 2 }]
}
```

`blocked` means explicit administrative deactivation. Identity's temporary
failed-login lockout remains separate and is exposed as `lockoutEnd` in user
responses.

## Users

### List and details

```http
GET /api/admin/users?page=1&pageSize=20&search=teacher@example&role=teacher&state=active
GET /api/admin/users/{id}
```

`pageSize` is limited to 100. `role` accepts `teacher` or `admin`; `state`
accepts `active` or `blocked`. Details include roles, administrative state,
temporary `lockoutEnd`, and counts of authored polls and decisions.

### Block or unblock

```http
PATCH /api/admin/users/{id}/status
X-CSRF-TOKEN: ...
Content-Type: application/json

{ "isDeactivated": true }
```

Administrative deactivation is stored independently of Identity lockout. It
rotates the Identity security stamp; the application also validates the stored
flag on every cookie-authenticated request, so an already issued session loses
access immediately. A deactivated account cannot sign in.

An administrator cannot deactivate their own account or perform an operation
that would leave no active administrator. These conflicts return `409` with a
machine-readable `code`.

### Delete

```http
DELETE /api/admin/users/{id}
X-CSRF-TOKEN: ...
```

Physical deletion is allowed only when the user has no authored polls and no
authored decisions. Otherwise the response is `409` with
`code = "user_has_dependencies"`, `pollCount`, `decisionCount`, and an explicit
recommendation to deactivate the account. There is no business-data cascade.
Self-deletion and removal of the last active administrator are forbidden.

## Poll administration

### List and details

```http
GET /api/admin/polls?page=1&pageSize=20&search=feedback&creatorId={uuid}&status=open&createdFrom=2026-10-01&createdTo=2026-10-31
GET /api/admin/polls/{id}
```

The list covers all owners. Date filters are inclusive UTC calendar dates;
questions and options in details preserve their positions. Responses include
aggregate counts only, never voter tokens, token hashes, or individual answer
sets.

### Delete as administrator

```http
DELETE /api/admin/polls/{id}?confirm=true
X-CSRF-TOKEN: ...
```

Deleting an `open` or `closed` poll requires the explicit `confirm=true` query
parameter. Omitting it returns `409` with
`code = "poll_deletion_confirmation_required"`. A draft does not require this
extra confirmation.

The backend locks the poll row and deletes its decision, participation rows,
votes, options, questions, and poll inside one PostgreSQL transaction. Voting,
closing, editing, and deletion therefore serialize on the same poll-row lock;
partial deletion is not committed.

### Delete own draft as teacher

```http
DELETE /api/polls/{id}
X-CSRF-TOKEN: ...
```

Only the owning teacher may delete a `draft`. A foreign poll is reported as
`404`; an `open` or `closed` poll returns `409` with
`code = "only_draft_poll_can_be_deleted"`.

## Audit

```http
GET /api/admin/audit?page=1&pageSize=20&actionType=user.deactivated&targetType=user&targetId={uuid}&from=2026-10-01&to=2026-10-31
```

Successful administrative mutations write an audit event in the same database
transaction. Events contain the administrator ID, action type, target type/ID,
UTC timestamp, and minimal result/count metadata. They never contain passwords,
cookies, anonymous voter tokens or hashes, decision text, or answer text. The
administrator ID is intentionally not a foreign key so an immutable event can
remain after a user is later deleted.

## HTTP errors

- `400` — invalid pagination/filter/body or CSRF validation failure.
- `401` — no valid authentication cookie.
- `403` — authenticated account lacks active `admin` access.
- `404` — resource absent (and for a foreign teacher's poll).
- `409` — destructive confirmation missing, wrong poll state, self/last-admin
  protection, or user business dependencies.

## Migration and deployment order

Migration `20261023064551_AddAdministrationAndAudit` adds
`users.is_administratively_deactivated boolean NOT NULL DEFAULT FALSE` and the
`admin_audit_events` table with indexes. It does not alter poll relationships or
delete data. The reviewed SQL is
`scripts/migrations/20261023064551_add_administration_and_audit.sql`.

For production: take and restore-verify a PostgreSQL backup; stop writes; verify
the migration history and baseline counts; apply only this transactional SQL;
verify the new column/table, migration history, counts, and foreign-key
integrity; only then deploy the matching application. The application does not
run migrations automatically. This development PR does not apply anything to
Railway.

## Deferred architecture (not implemented here)

Stage 2 should add an explicit question type while backfilling existing rows
from `allows_multiple_answers`, and a separate anonymous text-answer table with
no participant/account foreign key. Private knowledge-check settings should be
stored separately from public question data.

Stage 3 should add persistent analysis jobs/results and reviewable knowledge
scores, with an `IAiAnalysisProvider` boundary. Provider calls remain disabled
by default, use configuration secrets and `store: false`, and are processed by
a recoverable background worker rather than `Task.Run` in HTTP requests.
