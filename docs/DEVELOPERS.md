# Developer guide: API tokens, the public API and webhooks (REOS-165)

From nothing to a working call in 15 minutes. This page is for the people who build against ReleaseOS: **call it** with an API token, and **listen to it** with signed webhooks. Everything here is also in the app, under **Management → Integrations** (Release Train Engineers and Release Managers).

| You want to | Read |
|---|---|
| Make a first call | [Quickstart](#quickstart) |
| Know what a token can do | [Authentication](#authentication-with-api-tokens) and [Scopes](#scopes) |
| Know what every call returns | [Conventions](#conventions) and [`docs/api/public-v1.openapi.json`](api/public-v1.openapi.json) |
| Receive events | [Webhooks](#webhooks) |
| Check a signature in C#, JavaScript or Python | [Verifying a delivery](#verifying-a-delivery) |
| See what changed | [Versioning, changelog and deprecations](#versioning-changelog-and-deprecations) |

## Quickstart

1. **Make a token.** Sign in as a Release Train Engineer or Release Manager, open **Management → Integrations → API tokens**, choose **Make token**, name it for what will use it (for example `Build server`), leave **Access** on *Read* and choose **Make token**. Copy the token now: it is shown once, and ReleaseOS keeps only a hash of it.
2. **Call the API.** Put the token in the environment (never on a command line that is logged) and ask who it acts as:

   ```bash
   export RELEASEOS_TOKEN=rt_...    # the token you copied
   curl -s -H "Authorization: Bearer $RELEASEOS_TOKEN" https://YOUR-HOST/api/public/v1/me
   ```

   You get the person the token acts as, their role in the workspace, and the workspace. A `401` means the token is wrong, revoked or expired; a `403` with `ScopeDenied` names the scope it lacks.
3. **List releases.**

   ```bash
   curl -s -H "Authorization: Bearer $RELEASEOS_TOKEN" "https://YOUR-HOST/api/public/v1/releases?limit=5"
   ```

4. **Add a webhook.** Open **Integrations → Webhooks → Add webhook**, give your https address (its host must be on the administrator's list, see [Addresses](#addresses)) and choose the events. Copy the secret (`whsec_...`): it is shown once.
5. **Send a test event.** On the webhook choose **Send test event**. Your receiver gets a `webhook.ping`, signed with the secret. Verify it with the sample for your language below. When your receiver answers `2xx`, the delivery shows **Delivered** in the delivery log; until then it shows **Retrying** and the log says why.

## Authentication with API tokens

* Send `Authorization: Bearer rt_...` on every call. The public API is `/api/public/v1`; a token is refused anywhere else, and cannot manage tokens.
* **A token acts as a person.** It acts as the person who made it (or last rotated it), with the role that person holds in the workspace *at the time of each request*. Its scopes narrow that person; they never widen it. If the person is deactivated or leaves the workspace, the token stops working (`401`). Rotating hands the token to the person who rotates it.
* **The secret is shown once**, when the token is made or rotated, and only its SHA-256 is stored. Lose it and you rotate the token (the old secret stops working at once) or make another.
* **Expiry** is optional; an expired token answers `401` until it is rotated with a new expiry. **Revoke** stops a token on its next request.
* Tokens have the format `rt_` and 43 characters; the first 8 characters are shown in the app so you can tell tokens apart.
* Use a separate token per system and the least access it needs; a leaked token is revoked in one click, and the use of a revoked, expired or orphaned token is audited.

### Scopes

| Scope | What a token with it can do |
|---|---|
| `read` | Read releases, readiness, gates, approval requests, deployments, the calendar and analytics; `GET /me` |
| `write` | The service actions on releases (`:advance`, `:complete`, `:abort`), gates (`:start`, `:fail`, `:reopen`) and checklist tasks (`:complete`, `:reopen`) |
| `approvals` | `:certify` and `:waive` on a gate, waivers, approval requests and decisions |
| `deployments` | Record deployments and their events, and the deployment controls (`:pause`, `:resume`, `:promote`, `:rollback`, `:arm-rollback`, `:disarm-rollback`) |

The scopes are independent (`write` does not include `read`). The app offers seven combinations that include `read`; the API accepts any non-empty set. A call is also refused when the person the token acts as could not do it themselves (`RoleDenied`, or the rule's own guard such as `GateLockout`).

## Conventions

The full surface is [`docs/api/public-v1.openapi.json`](api/public-v1.openapi.json) (OpenAPI 3.1; also served, without a token, at `/api/public/v1/openapi.json`). The rules every call follows:

* **Errors** are `application/problem+json` (RFC 9457) with `type`, `title`, `status`, `detail`, `instance`, plus `guard` (the rule's name) and `message`. Treat an unknown `guard` as the status class it came with.
* **Concurrency.** A write that changes a record needs `If-Match: <version>` (the `version` you read). Without it the answer is `428`; with a stale one it is `409 VersionConflict` and `current` holds the row as it is now.
* **Lists** are `{ "data": [...], "page": { "limit", "hasMore", "nextCursor" } }`. `limit` is 1 to 200 (default 50); pass `page.nextCursor` as `cursor` with the same filters.
* **Idempotency.** A `POST` may carry `Idempotency-Key` (1 to 255 printable ASCII characters, kept 24 hours per token). Repeating a successful request with the same key returns the stored answer with `Idempotent-Replayed: true` and runs nothing, so the change and its audit row happen once. Use a new key per intended action and the same key for a retry.
* **Rate limits** are per token and per workspace. Every answer carries `X-RateLimit-Limit`, `X-RateLimit-Remaining` and `X-RateLimit-Reset`; past the limit the answer is `429 RateLimited` with `Retry-After` (seconds): wait that long, do not retry at once.
* **Time** is UTC ISO-8601 with `Z`. Status changes happen only through the actions (`POST /releases/{id}:advance`, ...), never by setting a field.

## Webhooks

ReleaseOS posts one signed HTTP request per event per webhook, to the https address you gave. The details (headers, secrets, operator settings) are in [`docs/WEBHOOKS.md`](WEBHOOKS.md); the parts a receiver needs are here.

### What is sent

`POST` with `Content-Type: application/json` and a compact JSON body:

```json
{
  "id": "evt_4182",
  "type": "gate.failed",
  "occurredAt": "2026-11-26T12:00:00Z",
  "workspaceId": "default",
  "data": {
    "entityType": "StageGate", "entityId": "g1", "action": "Fail",
    "releaseId": "t1", "actorUserId": "9f0c...",
    "before": { "status": "InProgress", "version": 2 },
    "after":  { "status": "Failed", "version": 3 }
  }
}
```

`id` is the **event** id and is the same for every webhook that gets the event and for a replay. Headers: `X-ReleaseOS-Delivery` (the delivery id, stable across retries), `X-ReleaseOS-Timestamp` (Unix seconds when this attempt was signed), `X-ReleaseOS-Signature` (`v1=` and the hex HMAC), `X-ReleaseOS-Event` and `X-ReleaseOS-Secret-Version`.

### Event catalogue

A webhook asks for any set of these, or for all. **Send test event** posts `webhook.ping`, which is never offered as something to subscribe to. A new event is a code change with a test and a line here (a test fails when this table and the code differ).

| Event | Name | When |
|---|---|---|
| `release.created` | Release created | A release was created, from scratch, a template or another release. |
| `release.status_changed` | Release status changed | A release moved to another status (advanced, completed or aborted). |
| `gate.certified` | Gate certified | A gate was certified. |
| `gate.failed` | Gate failed | A gate was marked failed. |
| `approval.requested` | Approval requested | An approval was requested for a release in an environment, or sent again after changes. |
| `approval.decided` | Approval decided | An approval was approved by every seat, or sent back with changes requested. |
| `gonogo.recorded` | Go/No-Go recorded | A Go, Conditional Go or No-Go decision was recorded. |
| `freeze.override_requested` | Freeze override requested | A release asked for an exception to a freeze window. |
| `freeze.override_granted` | Freeze override granted | A freeze override was granted or renewed. |
| `deployment.created` | Deployment created | A deployment was reported by the deployment tool. |
| `deployment.state_changed` | Deployment state changed | A deployment moved to another state (running, successful, failed, rolled back). |
| `deployment.events_appended` | Deployment events appended | The deployment tool added events to a deployment. |
| `pir.created` | Post-implementation review created | A review was required for a release (a bad close code, or an abort during execution). |
| `sync_alert.raised` | Sync alert raised | A background failure was recorded as an alert (the first time it occurs, not each repeat). |

### Verifying a delivery

1. Take the **raw** body, exactly as received. Do not parse and re-serialise it (that changes bytes, and the signature fails).
2. Refuse the request if `X-ReleaseOS-Timestamp` is more than **5 minutes** from your own clock. This is the **replay window**: a captured request cannot be sent again later.
3. Compute `v1=` plus the hex HMAC-SHA256 of `timestamp + "." + deliveryId + "." + rawBody` with the secret, and compare it with `X-ReleaseOS-Signature` **in constant time**.
4. Answer `2xx` quickly (within 10 seconds) and do the work afterwards. Anything else is a failed attempt and is retried.
5. **Delivery is at least once.** Remember the delivery ids you have handled (a day or more) and answer `200` without acting when you see one again. Remember event ids too if a replay must not act twice.

Test vector: secret `whsec_test_secret`, timestamp `1792504800`, delivery id `7d1c0a52-8d3e-4f5e-9d3b-0c1b6f6e2a11`, body `{"id":"evt_1","type":"gate.failed"}` give `v1=ac276cd9bebb669b711a37e14b70c1398c4d7b8f6a6ed3f563b8813fb4f43dff`.

**These samples are tested.** Each runs a self-test against the vector (`--selftest`) and each is given a delivery signed by the real sender's signer and posted through the real sender, with non-ASCII text in the body, and must accept it and refuse a changed body (`--check`; `WebhookSamplesTests` runs all three in CI).

#### C#

`dotnet run docs/samples/webhooks/Verify.cs -- --selftest`

<!-- sample: docs/samples/webhooks/Verify.cs -->
```csharp
// Verifies a ReleaseOS webhook delivery (docs/WEBHOOKS.md). .NET 10 single-file app:  dotnet run docs/samples/webhooks/Verify.cs -- --selftest
// In a real receiver, call ReleaseOsWebhook.Verify(...) with the RAW request body (the exact bytes, not a re-serialised object) and the three headers.
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;

if (args.Contains("--check"))
{
    // --check <file.json>: a captured delivery {secret, timestamp, deliveryId, signature, body, now}; exit 0 and print ok when it verifies (docs/DEVELOPERS.md).
    using var doc = JsonDocument.Parse(File.ReadAllText(args[Array.IndexOf(args, "--check") + 1]));
    var c = doc.RootElement;
    var good = ReleaseOsWebhook.Verify(c.GetProperty("secret").GetString()!, c.GetProperty("timestamp").GetString()!, c.GetProperty("deliveryId").GetString()!,
        c.GetProperty("body").GetString()!, c.GetProperty("signature").GetString(), c.GetProperty("now").GetInt64());
    Console.WriteLine(good ? "ok" : "FAILED");
    return good ? 0 : 1;
}
if (args.Contains("--selftest"))
{
    const string secret = "whsec_test_secret", delivery = "7d1c0a52-8d3e-4f5e-9d3b-0c1b6f6e2a11", body = "{\"id\":\"evt_1\",\"type\":\"gate.failed\"}";
    const long ts = 1792504800;
    const string expected = "v1=ac276cd9bebb669b711a37e14b70c1398c4d7b8f6a6ed3f563b8813fb4f43dff";
    var ok = ReleaseOsWebhook.Verify(secret, ts.ToString(), delivery, body, expected, ts)
        && !ReleaseOsWebhook.Verify(secret, ts.ToString(), delivery, body + " ", expected, ts)               // body changed
        && !ReleaseOsWebhook.Verify("whsec_other", ts.ToString(), delivery, body, expected, ts)              // wrong secret
        && !ReleaseOsWebhook.Verify(secret, ts.ToString(), delivery, body, expected, ts + 301)               // outside the replay window
        && ReleaseOsWebhook.Verify(secret, ts.ToString(), delivery, body, expected, ts + 300);               // inside it
    Console.WriteLine(ok ? "ok" : "FAILED");
    return ok ? 0 : 1;
}
Console.WriteLine("Use ReleaseOsWebhook.Verify from your own endpoint; run with --selftest to check this sample.");
return 0;

public static class ReleaseOsWebhook
{
    public const int ToleranceSeconds = 300;

    /// <param name="secret">The whsec_ secret shown once when the webhook was added or its secret rotated.</param>
    /// <param name="timestampHeader">X-ReleaseOS-Timestamp (Unix seconds).</param>
    /// <param name="deliveryId">X-ReleaseOS-Delivery.</param>
    /// <param name="rawBody">The request body exactly as received.</param>
    /// <param name="signatureHeader">X-ReleaseOS-Signature, "v1=" and 64 hex characters.</param>
    /// <param name="nowUnixSeconds">Your own clock, DateTimeOffset.UtcNow.ToUnixTimeSeconds().</param>
    public static bool Verify(string secret, string timestampHeader, string deliveryId, string rawBody, string? signatureHeader, long nowUnixSeconds)
    {
        if (signatureHeader is null || !long.TryParse(timestampHeader, out var ts)) return false;
        if (Math.Abs(nowUnixSeconds - ts) > ToleranceSeconds) return false;                    // replay window
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{ts}.{deliveryId}.{rawBody}"));
        var expected = Encoding.ASCII.GetBytes("v1=" + Convert.ToHexStringLower(mac));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(signatureHeader));   // constant time
    }
    // After Verify: store deliveryId (for a day or more) and answer 200 without acting when you have seen it: delivery is at least once.
}
```

#### JavaScript (Node 18+)

`node docs/samples/webhooks/verify.mjs --selftest`

<!-- sample: docs/samples/webhooks/verify.mjs -->
```js
// Verifies a ReleaseOS webhook delivery (docs/WEBHOOKS.md). Node 18+:  node docs/samples/webhooks/verify.mjs --selftest
// In a real receiver, call verify() with the RAW request body (a Buffer or string exactly as received, not JSON.stringify of a parsed object) and the three headers.
import { createHmac, timingSafeEqual } from 'node:crypto'
import { readFileSync } from 'node:fs'

export const TOLERANCE_SECONDS = 300

/** secret: the whsec_ secret shown once. timestamp, deliveryId, signature: the X-ReleaseOS-Timestamp, -Delivery and -Signature headers. now: your clock in Unix seconds. */
export function verify(secret, timestamp, deliveryId, rawBody, signature, now = Math.floor(Date.now() / 1000)) {
  const ts = Number(timestamp)
  if (!signature || !Number.isInteger(ts) || Math.abs(now - ts) > TOLERANCE_SECONDS) return false   // replay window
  const mac = createHmac('sha256', secret).update(`${ts}.${deliveryId}.`).update(rawBody).digest('hex')
  const expected = Buffer.from(`v1=${mac}`)
  const given = Buffer.from(signature)
  return expected.length === given.length && timingSafeEqual(expected, given)   // constant time
}
// After verify(): remember deliveryId (for a day or more) and answer 200 without acting when you have seen it: delivery is at least once.

if (process.argv.includes('--check')) {
  // --check <file.json>: a captured delivery {secret, timestamp, deliveryId, signature, body, now}; exit 0 and print ok when it verifies (docs/DEVELOPERS.md).
  const c = JSON.parse(readFileSync(process.argv[process.argv.indexOf('--check') + 1], 'utf8'))
  const ok = verify(c.secret, c.timestamp, c.deliveryId, Buffer.from(c.body, 'utf8'), c.signature, c.now)
  console.log(ok ? 'ok' : 'FAILED')
  process.exit(ok ? 0 : 1)
}

if (process.argv.includes('--selftest')) {
  const secret = 'whsec_test_secret', delivery = '7d1c0a52-8d3e-4f5e-9d3b-0c1b6f6e2a11', body = '{"id":"evt_1","type":"gate.failed"}', ts = 1792504800
  const sig = 'v1=ac276cd9bebb669b711a37e14b70c1398c4d7b8f6a6ed3f563b8813fb4f43dff'
  const ok = verify(secret, String(ts), delivery, body, sig, ts)
    && !verify(secret, String(ts), delivery, body + ' ', sig, ts)        // body changed
    && !verify('whsec_other', String(ts), delivery, body, sig, ts)       // wrong secret
    && !verify(secret, String(ts), delivery, body, sig, ts + 301)        // outside the replay window
    && verify(secret, String(ts), delivery, body, sig, ts + 300)         // inside it
  console.log(ok ? 'ok' : 'FAILED')
  process.exit(ok ? 0 : 1)
}
```

#### Python (3.10+)

`python3 docs/samples/webhooks/verify.py --selftest`

<!-- sample: docs/samples/webhooks/verify.py -->
```python
"""Verifies a ReleaseOS webhook delivery (docs/WEBHOOKS.md). Python 3.8+, standard library only:  python3 docs/samples/webhooks/verify.py --selftest
In a real receiver, call verify() with the RAW request body (bytes exactly as received, not json.dumps of a parsed object) and the three headers."""
import hashlib, hmac, json, sys, time

TOLERANCE_SECONDS = 300


def verify(secret: str, timestamp: str, delivery_id: str, raw_body: bytes, signature: str, now: int | None = None) -> bool:
    """secret: the whsec_ secret shown once. timestamp, delivery_id, signature: the X-ReleaseOS-Timestamp, -Delivery and -Signature headers. now: your clock in Unix seconds."""
    try:
        ts = int(timestamp)
    except (TypeError, ValueError):
        return False
    now = int(time.time()) if now is None else now
    if not signature or abs(now - ts) > TOLERANCE_SECONDS:        # replay window
        return False
    mac = hmac.new(secret.encode(), f"{ts}.{delivery_id}.".encode() + raw_body, hashlib.sha256).hexdigest()
    return hmac.compare_digest(f"v1={mac}", signature)             # constant time
# After verify(): remember delivery_id (for a day or more) and answer 200 without acting when you have seen it: delivery is at least once.


if __name__ == "__main__" and "--check" in sys.argv:
    # --check <file.json>: a captured delivery {secret, timestamp, deliveryId, signature, body, now}; exit 0 and print ok when it verifies (docs/DEVELOPERS.md, the CI test that runs the real signer's output through this).
    c = json.load(open(sys.argv[sys.argv.index("--check") + 1], encoding="utf-8"))
    ok = verify(c["secret"], c["timestamp"], c["deliveryId"], c["body"].encode("utf-8"), c["signature"], c.get("now"))
    print("ok" if ok else "FAILED")
    sys.exit(0 if ok else 1)

if __name__ == "__main__" and "--selftest" in sys.argv:
    secret, delivery, body, ts = "whsec_test_secret", "7d1c0a52-8d3e-4f5e-9d3b-0c1b6f6e2a11", b'{"id":"evt_1","type":"gate.failed"}', 1792504800
    sig = "v1=ac276cd9bebb669b711a37e14b70c1398c4d7b8f6a6ed3f563b8813fb4f43dff"
    ok = (verify(secret, str(ts), delivery, body, sig, ts)
          and not verify(secret, str(ts), delivery, body + b" ", sig, ts)        # body changed
          and not verify("whsec_other", str(ts), delivery, body, sig, ts)        # wrong secret
          and not verify(secret, str(ts), delivery, body, sig, ts + 301)         # outside the replay window
          and verify(secret, str(ts), delivery, body, sig, ts + 300))            # inside it
    print("ok" if ok else "FAILED")
    sys.exit(0 if ok else 1)
```

In a web framework, make sure you hand `verify` the **raw** bytes: in Express use `express.raw({ type: 'application/json' })`, in ASP.NET Core read `Request.Body` before any model binding, in Flask use `request.get_data()`.

### Retries, dead letters and replay

* A delivery is `Pending` until your receiver answers `2xx`. A failed attempt (another status, a refused connection, no answer within 10 seconds, an address that is no longer allowed) schedules the next one after 60 s, 120 s, 240 s and so on, doubling to at most 6 hours. After **10 attempts** (about 8½ hours) the delivery is a **dead letter**. A retry is signed afresh (new timestamp, same delivery id), so a slow recovery never trips your replay window.
* Redirects are not followed and your response body is never read.
* Persistent failure raises **one** alert per webhook (in Sync health and the live bell), not one per attempt; it resolves when a delivery succeeds.
* **Replay** (Delivery log → a delivery → Replay) sends a delivered or dead-lettered event again as a *new* delivery (new delivery id, same event id and body), audited with who did it.
* A **paused** webhook queues nothing new and holds what is queued; **resume** releases it. Finished deliveries are kept 30 days.

### Addresses

https only, no user name or password in the address, and the host must be on the administrator's list (`Webhooks:Out:AllowedHosts`, see [`docs/WEBHOOKS.md`](WEBHOOKS.md)). Private, loopback and link-local addresses are refused. The address is stored encrypted and only its host is shown. **Rotate secret** asks first: the old secret stops verifying at once, so give your receiver the new one first or accept refused deliveries (they are retried) until it has it.

## Versioning, changelog and deprecations

The major version is in the path (`/api/public/v1`). Inside v1 the API only grows: new operations, optional fields, response members and enumerated values, so **ignore response members you do not know**. A breaking change is a new major version. A deprecated operation keeps working for at least 12 months after it is announced. The rules, the changelog and the list of deprecations (none yet) are in [`docs/API_VERSIONING.md`](API_VERSIONING.md).

## When something is wrong

| You see | Cause and fix |
|---|---|
| `401` on every call | The token is wrong, revoked or expired, or the person it acts as was deactivated. Rotate it, or make a new one. |
| `403 ScopeDenied` | The token lacks the scope named in `requiredScope`. Make a token with more access. |
| `403 RoleDenied` | The person the token acts as may not do this. Scopes never widen a role. |
| `428` or `409 VersionConflict` | Send `If-Match` with the version you read; on `409` read `current` and decide again. |
| `429 RateLimited` | Wait `Retry-After` seconds. |
| Webhook shows **Retrying** | Your receiver answered something other than `2xx`, or did not answer in 10 seconds. The delivery's attempts say which. |
| Your signature check fails | You parsed the body before hashing, used the wrong secret (a rotated one has a new `X-ReleaseOS-Secret-Version`), or your clock is off by more than 5 minutes. |
| **Add webhook** is off | No receiver host is allowed yet. An administrator sets `Webhooks:Out:AllowedHosts`. |
