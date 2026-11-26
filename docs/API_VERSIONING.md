# Public API: surface, versioning and deprecation (REOS-161 to REOS-163, D61)

The public API is `/api/public/v1`. Its description is [`docs/api/public-v1.openapi.json`](api/public-v1.openapi.json) (OpenAPI 3.1), generated from the code and served without a token at `GET /api/public/v1/openapi.json`. A contract test (`PublicApiContractTests`) fails when the committed file and the code differ, and when the list of operations and the mapped routes differ. To change the surface: change `PublicApiCatalog`, run `UPDATE_OPENAPI=1 dotnet test --filter PublicApiContractTests`, review the diff of the JSON file against the rules below, commit both.

## What it is

| Scope | What a token with it can do |
|---|---|
| `read` | Read releases, readiness, gates, approval requests, deployments, the calendar and the analytics metrics (M1 to M24); `GET /me` |
| `write` | The service actions on releases (`:advance`, `:complete`, `:abort`), gates (`:start`, `:fail`, `:reopen`) and checklist tasks (`:complete`, `:reopen`) |
| `approvals` | `:certify` and `:waive` on a gate, waiver requests, approvals and declines, approval requests (create, `:decide`, `:resubmit`, `:cancel`) |
| `deployments` | Record deployments and their events (`POST /deployments`, `POST /deployments/events`), and the deployment controls (`:pause`, `:resume`, `:promote`, `:rollback`, `:arm-rollback`, `:disarm-rollback`) |

The four scopes are independent; the Admin screen offers combinations that include `read`.

**A token narrows a person, it never widens one.** It acts as the person who made it, or last rotated it, in the token's workspace, with the role that person holds there at the time of each request. Each operation runs the internal endpoint's own policy and service, so a refusal is the same refusal (`RoleDenied`, or the service's own guard such as `GateLockout`), separation of duties included, and `If-Match` and the audit row are the same. If the person is deactivated or leaves the workspace, the token stops working (401). Rotating hands the token to the person who rotates, so nobody holds a secret that acts as someone with more rights than their own.

Everything else is not there. `GET /api/public/v1/<anything not in the document>` is 404 `NotInPublicApi`; a known path with another method is 405 with `Allow`. There is no `PATCH`, `PUT` or `DELETE` and no way to set a status (CLAUDE.md rule 2). A token is refused on `/api/v1` (that is for signed-in people) and cannot manage tokens; the ingest keys (`rk_…`, `/api/v1/ingest/*`) are a separate credential for a deployment tool that pushes without a person and are unchanged.

## Conventions (REOS-163)

- **Authentication:** `Authorization: Bearer rt_…`. A token is shown once, when it is made or rotated, and only its SHA-256 is stored. 401 for a missing, unknown, malformed, revoked or expired token; a revoked token is refused on its next request.
- **Errors** are `application/problem+json` (RFC 9457): `type` (`urn:releaseos:problem:{guard}`), `title`, `status`, `detail`, `instance`, plus `guard` (the rule's name, `DbRule` for a database rule) and `message` (same text as `detail`). Members the internal API returns stay where a client expects them: `items`, `failures`, `gates`, and on a 409 `current` (the row as it is now). 403 `ScopeDenied` carries `requiredScope`.
- **Concurrency:** writes that change a record need `If-Match: <version>` (428 without it, 409 `VersionConflict` with `current` when stale).
- **Lists** are `{ "data": [...], "page": { "limit", "hasMore", "nextCursor" } }`. `limit` is 1 to 200 (default 50); pass `page.nextCursor` as `cursor` with the same filters. A cursor is opaque, names the last record returned and the filters it belongs to; one that does not match is 400 `InvalidCursor`. Lists keep the order the application gives them and are bounded by the internal cap (500 deployments and 500 approval requests: narrow with the filters for older ones).
- **Idempotency:** a `POST` may carry `Idempotency-Key` (1 to 255 printable ASCII characters). The key belongs to the token and is kept 24 hours. A repeat of a successful request with the same key returns the stored status and body with `Idempotent-Replayed: true` and runs nothing, so a service write and its audit row happen once. The same key with another request (method, path, query or body) is 422 `IdempotencyKeyReused`; a repeat while the first is still running is 409 `IdempotencyInProgress`. A request that failed is not kept, so its retry runs.
- **Rate limits** per token (default 20 requests a second, burst 40) and per workspace (100 and 200), `PublicApi:*` in configuration. Every response carries `X-RateLimit-Limit` (the bucket's size), `X-RateLimit-Remaining` and `X-RateLimit-Reset` (seconds until the bucket is full); beyond the limit the answer is 429 `RateLimited` with `Retry-After` and `limit` naming `token` or `workspace`. Too many refused tokens from one address is 429 as well.
- **Time** is UTC ISO-8601 with `Z`.

## Versioning

- The major version is in the path. `v1` is this document; its `info.version` is `1.x.y`.
- **Non-breaking, allowed inside v1** (minor): a new operation, a new optional request field or query parameter, a new member in a response, a new value in an enumerated text (a status, a guard name, a scope). Clients must ignore response members they do not know and must treat an unknown `guard` as the status class it came with.
- **Breaking, only in a new major version:** removing or renaming an operation, path, parameter or response member; making an optional field required; changing a type, a meaning or a status code; narrowing what a scope allows; changing the pagination or error shape.
- A **patch** version is a documentation or description fix.
- Every change to the document is a change to `docs/api/public-v1.openapi.json` in the same commit as the code, so a client's diff is the review.

## Deprecation

- An operation is deprecated by announcing it in this file's changelog and in the release notes, setting `deprecated: true` on it in the document, and answering it with `Deprecation: true` and `Sunset: <date>` headers (RFC 9745, RFC 8594).
- A deprecated operation keeps working, unchanged, for at least **12 months** after the announcement, and `v1` stays available for at least 12 months after `v2` is announced. After the sunset date the operation answers 410 `Gone` with a problem naming its replacement.
- Security fixes are the exception: a hole may be closed at once, with the notice given as soon as it is safe to give.
- Nothing is deprecated yet. The headers and the `410` are built when the first deprecation needs them (see QUESTIONS Q-161a).

## Changelog

| Version | Date | Change |
|---|---|---|
| 1.0.0 | 2026-11-25 | First release: the operations in `docs/api/public-v1.openapi.json`, tokens with four scopes, problem details, cursor pages, `Idempotency-Key`, rate-limit headers |
