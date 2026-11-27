# Outbound webhooks (REOS-164, D61)

ReleaseOS posts signed events to addresses an administrator adds. This page is for the people who build the receiver (the whole path from a token to a verified webhook is in [`DEVELOPERS.md`](DEVELOPERS.md)); the settings an operator needs are at the end. The screen is **Management → Integrations → Webhooks** (Release Train Engineers and Release Managers; a Viewer or Governance Officer does not see it and the API answers 403).

## What is sent

One HTTP `POST` per event per webhook, `Content-Type: application/json`, to the https address you gave. The body is compact JSON:

```json
{
  "id": "evt_4182",
  "type": "gate.failed",
  "occurredAt": "2026-11-26T12:00:00Z",
  "workspaceId": "default",
  "data": {
    "entityType": "StageGate", "entityId": "g1", "action": "Fail",
    "releaseId": "t1", "actorUserId": "9f0c…",
    "before": { "status": "InProgress", "version": 2 },
    "after":  { "status": "Failed", "version": 3 }
  }
}
```

* `id` is the **event** id (`evt_` and the audit row's number). It is the same for every webhook that gets the event and for a replay.
* `data.before` / `data.after` are what the audit log recorded (they never hold a secret). A body over 60 000 bytes keeps the identifying members and drops them, with `"truncated": true`.
* Times are UTC.

### Headers

| Header | Meaning |
|---|---|
| `X-ReleaseOS-Delivery` | The **delivery** id. Stable across retries of the same delivery; a replay is a new delivery with a new id. |
| `X-ReleaseOS-Timestamp` | Unix seconds when **this attempt** was signed. A retry is signed afresh. |
| `X-ReleaseOS-Signature` | `v1=` and the hex HMAC-SHA256 of `"{timestamp}.{delivery id}.{raw body}"` with the webhook's secret. |
| `X-ReleaseOS-Event` | The event type, e.g. `gate.failed`. |
| `X-ReleaseOS-Secret-Version` | Which of the webhook's secrets signed it (1 for the first, +1 at each rotation). |

(The same scheme signs the deployment control webhooks, so one verifier serves both.)

### Event catalogue

`release.created`, `release.status_changed`, `gate.certified`, `gate.failed`, `approval.requested`, `approval.decided`, `gonogo.recorded`, `freeze.override_requested`, `freeze.override_granted`, `deployment.created`, `deployment.state_changed`, `deployment.events_appended`, `pir.created`, `sync_alert.raised`. A webhook asks for any set, or for all. **Send test event** posts `webhook.ping`, which is never offered as something to subscribe to. The catalogue is code: a new event is a code change with a test and a line here.

An event is an audit row. It is queued in the same database transaction as the audit row, so nothing is sent for a change that rolled back, and the rows that database triggers write (the post-implementation review of an unsuccessful release) are events too.

## Verifying a delivery

1. Take the **raw** body, exactly as received. Do not parse and re-serialise it.
2. Refuse the request if `X-ReleaseOS-Timestamp` is more than **5 minutes** from your own clock (the replay window).
3. Compute `v1=` + hex HMAC-SHA256 over `timestamp + "." + deliveryId + "." + body` with the secret, and compare it with `X-ReleaseOS-Signature` **in constant time**.
4. Answer `2xx` quickly. Anything else (or no answer within the timeout) is a failed attempt and is retried.
5. **Delivery is at least once.** Remember the delivery ids you have handled (for a day or more) and answer `200` without acting when you see one again; remember event ids too if a replay must not act twice.

Samples (each runs a self-test against the vector below; `tests/tools/test_webhook_samples.py` runs all three):

* C#: [`docs/samples/webhooks/Verify.cs`](samples/webhooks/Verify.cs), `dotnet run docs/samples/webhooks/Verify.cs -- --selftest`
* JavaScript: [`docs/samples/webhooks/verify.mjs`](samples/webhooks/verify.mjs), `node docs/samples/webhooks/verify.mjs --selftest`
* Python: [`docs/samples/webhooks/verify.py`](samples/webhooks/verify.py), `python3 docs/samples/webhooks/verify.py --selftest`

**Test vector.** Secret `whsec_test_secret`, timestamp `1792504800`, delivery id `7d1c0a52-8d3e-4f5e-9d3b-0c1b6f6e2a11`, body `{"id":"evt_1","type":"gate.failed"}` give
`v1=ac276cd9bebb669b711a37e14b70c1398c4d7b8f6a6ed3f563b8813fb4f43dff`.

## The secret

The secret (`whsec_` and 43 characters) is made when the webhook is added and again when it is rotated. It is **shown once**, on screen, with a Copy button; it is not in the database, in configuration or in any log. ReleaseOS keeps it only in a Data Protection file under the credentials directory (`secrets/outbound-webhooks/{workspace}/{webhook id}.secret`), readable only with the host's key ring. **Rotate secret** asks first: the old secret stops verifying at once, so give the receiver the new one before, or accept refused deliveries until it has it (they are retried; see below). A lost secret is replaced, never recovered.

## Delivery, retries, dead letters

* Each event is a `WebhookDeliveries` row, `Pending` until a `2xx` answer. A failed attempt (any other status, a refused connection, no answer within 10 s, an address that is no longer allowed) schedules the next one: 60 s, 120 s, 240 s… doubling to at most 6 h; after **10 attempts** (about 8½ hours) the delivery is a **Dead letter**. Redirects are not followed and the response body is never read.
* Every attempt is a row in the log (time, status, how long, a short reason). The reason never contains your address.
* **Persistent failure raises one alert, not one per attempt.** When a delivery is dead-lettered, one `Webhook / DeliveryFailed` row appears in Sync health for that webhook (and in the live bell), and its count and text say how many deliveries were dead-lettered since the last good one. It resolves by itself when a delivery succeeds again.
* **Replay** (Delivery log → a delivery → Replay) sends a delivered or dead-lettered event again as a *new* delivery (new delivery id, same event id and body). The original stays as it was. Every replay is audited with who did it.
* A **paused** webhook queues nothing new and holds what is queued; resume releases it. Finished deliveries older than 30 days are deleted.

## Addresses

https only, no user name or password in the address, and the host (and port, 443 when none) must be on the administrator's list. Private, loopback and link-local addresses are refused when the webhook is saved, before each send, and again when the socket connects (so a DNS change cannot slip past). The address is stored encrypted and only its host is shown.

## Operator settings

| Key | Default | Meaning |
|---|---|---|
| `Webhooks:Out:AllowedHosts` | none | Hosts that may receive events, separated by commas, semicolons or spaces: `hooks.example.com`, `*.example.com`, optionally `:port`. **Outside Development an empty list refuses every address.** In Development an empty list allows any public host. `*` alone allows any public host, by explicit choice. |
| `Webhooks:Out:AllowPrivateTargets` | false | Lets an address resolve to a private network (a receiver on the same network). |
| `Webhooks:Out:TimeoutSeconds` | 10 | Per attempt. |
| `Webhooks:Out:MaxAttempts` | 10 | Attempts before a dead letter. |
| `Webhooks:Out:BackoffBaseSeconds` / `BackoffCapSeconds` | 60 / 21600 | First wait, and the longest wait. |
| `Webhooks:Out:PollSeconds` | 5 | How often the sender looks for due deliveries. |
| `Webhooks:Out:Batch` | 20 | Deliveries per pass. |
| `Webhooks:Out:RetentionDays` | 30 | Finished deliveries are pruned after this. |
| `Webhooks:Out:MaxPerWorkspace` | 20 | Webhooks per workspace. |
| `Webhooks:Out:Worker:Enabled` | true | false stops the sender (events still queue). |

Back up `secrets/outbound-webhooks/` with the rest of `secrets/` (runbook section 7): a restore without it leaves webhooks that cannot sign; rotate their secrets to recover.
