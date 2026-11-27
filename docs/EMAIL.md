# Email delivery (REOS-167, D61)

Approvals and escalations can reach people by email. It is **off by default**: with the flag off nothing is queued, nothing is sent and nothing is reported.

## Turn it on

Configuration (environment variables use `__` for `:`; none of these is a secret):

| Key | Default | Meaning |
|---|---|---|
| `Notifications:Smtp:Enabled` | `false` | The switch. |
| `Notifications:Email:Provider` | `Smtp` | `Smtp` or `SendGrid`. |
| `Notifications:Email:From:Address` | (required) | One plain address, e.g. `releaseos@example.com`. |
| `Notifications:Email:From:Name` | `ReleaseOS` | |
| `Notifications:Email:Smtp:Host` / `Port` | (required) / `587` | |
| `Notifications:Email:Smtp:Security` | `StartTls` | `StartTls` or `None`. Implicit TLS (port 465) is not supported. `None` with a user name is accepted only for a local relay. |
| `Notifications:Email:Smtp:UserName` | none | Set it if the relay needs a login; then store the password (below). |
| `Notifications:Email:ApiBase` | `https://api.sendgrid.com` | SendGrid only; https. |
| `Notifications:Email:MaxAttempts` | `6` | Attempts before a message is given up on. |
| `Notifications:Email:BackoffBaseSeconds` / `BackoffCapSeconds` | `60` / `21600` | Wait doubles from the base up to the cap. |
| `Notifications:Email:RetentionDays` | `90` | Finished messages are removed after this. |
| `Notifications:Email:DigestHourUtc` | `7` | The daily digest goes out after this hour (UTC). |
| `Notifications:Email:CredentialsDirectory` | `data/email` | Where the encrypted credential files live (back it up with the Data Protection key ring). |
| `App:PublicBaseUrl` | unset | Without it mail has no link (D52). |

An enabled configuration that cannot work stops the start with the reason.

**Credentials** (the SMTP password or the SendGrid API key) are never in configuration: store them on **Integrations > Email > Provider credential** (RTE or Release Manager). They are encrypted with ASP.NET Data Protection, never shown again, never in SQLite or an audit row (CLAUDE.md rule 11, D12).

## Who gets what

Four kinds are mailed: approval requested, decision made, escalation level 1, escalation level 2. Each person chooses, under **Notification settings** (from the Inbox, or Jump to), for email: Off, Immediately or In the daily digest; and for the team webhook: Off or Immediately. The inbox always has everything. The default with no choice made is email immediately.

A message holds **structure only**: the workspace, the release key, the kind of notification, a time (UTC, with the host's display zone beside it) and a link. Never ticket text, notes, release titles or customer data (D12).

## Delivery, retries, bounces

* The sender picks up queued mail every few seconds and sends it, retrying a temporary failure with growing waits and giving up after the last attempt (`DeadLetter`).
* A provider that says the recipient can never be reached (a hard bounce) puts the address on the **suppression list** at once; nothing more is mailed to it from that workspace until an administrator removes the entry.
* An undeliverable address or a message given up on raises **one alert per address** on Sync health (`Notifications/DeliveryFailed`); it resolves when a later send to the address succeeds or the suppression is lifted.
* Every message is a row in the send log (Integrations > Email) with its state, attempts and last error; every way a message ends is an audit row.

### Bounce and complaint events from the provider

For bounces that arrive after the provider accepted a message, make an **events secret** on the Email tab (shown once) and have the provider, or a small relay, `POST /api/v1/email/events` with the JSON body

```json
{ "type": "bounce", "address": "person@example.com", "messageId": "…", "reason": "550 mailbox unavailable" }
```

(`type` is `bounce` or `complaint`; `messageId` is the id the provider returned when it accepted the message, `address` is used when it is absent). Sign it like a webhook: `X-ReleaseOS-Timestamp` (Unix seconds, within 5 minutes), `X-ReleaseOS-Delivery` (a unique id) and `X-ReleaseOS-Signature: v1=hex(HMAC-SHA256(secret, "{timestamp}.{id}.{body}"))`. Answers: 202 `{matched}`, 401 bad or stale signature, 404 no events secret yet, 422 malformed.

## Operating notes

* Upgrade script: `db/upgrades/019_email_delivery.sql` (see `RUNBOOK_OPERATIONS.md` section 8). The tables start empty.
* If the Data Protection key ring changes, the stored credential reads as absent: store it again.
* Tests send no mail: a fake provider, a loopback fake SMTP server and a stub HTTP handler.
