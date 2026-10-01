# Security scan: resource consumption and injection

Scope: OWASP API Security Top 10 2023 **API4 Unrestricted Resource Consumption**; OWASP Top 10 2025 **A05 Injection**; ASVS 5.0 **V1 Encoding and Sanitization**, **V2 Validation and Business Logic**, **V5 File Handling** (and the log-injection requirement of V16, Security Logging, where a finding touched it). One of five parallel reviews. Base: `main` at `c9a1180`. Method: read every endpoint, service, parser, exporter and the React sources for sinks; reproduce each suspected defect with a test **against the unmodified base first** (the base was exported with `git archive c9a1180` into a scratch directory and the new test files run there), then fix minimally and rerun. Limits that need a product value are built behind a config key with a conservative default and a `docs/QUESTIONS.md` entry (Q-SEC-D1 to Q-SEC-D6). No schema change. Linux container, in-process test host; timings are from a shared 4-core machine and are indicative.

Severity scale for an internal, SSO-only tool: **High** = one request from any signed-in role (or anonymous) can take the single process down or corrupt shared records; **Medium** = needs a planner role or a deployment condition, or damages integrity of a downstream channel/log; **Low** = bounded, privileged or cosmetic.

## 1. Summary

| # | Finding | OWASP / ASVS | Severity | Status |
|---|---|---|---|---|
| SEC-D1 | Comm preview: a Viewer's ad-hoc template was parsed in quadratic time with one error object per brace; no length limit | API4; ASVS V2 (input length), V1 | **High** | **Fixed** |
| SEC-D2 | Import "did you mean": unbounded edit distance on unbounded `Owner`/`Members` cells and on 10,000 near-miss rows (one cell: 1 min 23 s of CPU) | API4; ASVS V2, V5 | Medium | **Fixed** |
| SEC-D3 | Import width and error list unbounded: a header of commas became one error per column, all returned in the preview response; an O(errors x rows) loop | API4; ASVS V5, V2 | Medium | **Fixed** (Q-SEC-D2) |
| SEC-D4 | Checklist paste: unbounded line length edit-distanced against every user, team and product (9 s for one 200 KB line); task text not held to 2,000 | API4; ASVS V2 | Medium | **Fixed** |
| SEC-D5 | Log injection: text typed into the app (a webhook channel name) is logged verbatim when a delivery fails, and a line break in it forges a whole log entry | A05 (A09 impact); ASVS V16 log injection, V1 | Low | **Fixed** (central, every log event) |
| SEC-D6 | Chat markup injection: Slack control sequences (`<!channel>`, `<https://x\|label>`) in values survived comm dispatch; team notices posted names raw to Teams/Slack | A05; ASVS V1 (output encoding for the interpreter) | Medium | **Fixed** |
| SEC-D7 | No request-body limit below Kestrel's ~30 MB and no per-field length limits on JSON endpoints | API4; ASVS V2 | Medium | **Fixed**: global 1 MiB limit; per-field limits on every JSON body (REOS-66, Q-SEC-D1) |
| SEC-D8 | Export jobs: any role could queue without bound behind a one-at-a-time worker; files are never deleted | API4 | Low | **Fixed** (queue, Q-SEC-D3); retention **open** (Q-050c) |
| SEC-D9 | Stored previews: import CSV + plan files unbounded per person within 30 min and only lazily deleted; `ParsePreviews` rows never deleted | API4; ASVS V5 | Low | **Fixed** (Q-SEC-D4) |
| SEC-D10 | ICS feed brake keyed by client address: behind a proxy, 61 anonymous bad requests block every calendar for a minute | API4 | Low (deployment-dependent) | **Fixed** (REOS-67): valid tokens are always served, only failed lookups are braked (Q-SEC-D5) |

No SQL injection, path traversal, zip slip, XXE, header injection, XSS, template re-expansion or ICS property injection was found (section 3). Informational items are in section 4.

## 2. Findings

### SEC-D1 Comm preview: quadratic parser, unbounded errors, no length limit (High)
- **Evidence (base):** `POST /trains/{id}/comms:preview` is `Policies.Read` (`src/ReleaseMgmt.Api/Endpoints/CommsEndpoints.cs:56`), so a Viewer may send any `text`. `CommHydrationService.HydrateAsync` (`src/ReleaseMgmt.Infrastructure/Services/CommHydrationService.cs:41`) had no length check (saved templates do: 200/20,000 in `CommLibraryService.ValidateText`). `TokenParser.Parse` (`src/ReleaseMgmt.Domain/Services/CommTokens.cs:157`) called `text.IndexOf('}', i + 1)` for every `{`, scanning to the end each time when none closes (O(n^2)), and added one `TokenError` (about 300 bytes) per brace.
- **Why High:** measured on base, 400,000 braces cost 3 s and 400,000 error objects; extrapolated, a 30 MB body (Kestrel's default limit) of `{` is about 10^14 character comparisons and about 15 million error objects (several GB): one request from the least-privileged role pins a core and can exhaust the memory of the only process.
- **Reproduction:** `ResourceBoundsTests.Token_parser_is_linear_and_caps_its_error_list_on_a_template_of_unclosed_braces` (400,000 braces: base kept 400,000 errors, 3 s); `ResourceInjectionTests.A_viewer_cannot_preview_ad_hoc_text_longer_than_a_template_may_be` (base: 200 with the whole error list).
- **Fix:** the parser remembers the next `}` and `{` positions (linear); at most `TokenParser.MaxErrors` (100) errors per part plus one `TooManyErrors` entry saying how many more; `CommTokens.Suggest` skips names over 40 characters; ad-hoc preview text is held to a saved template's limits (422 `CommPreviewTooLarge`). Error kinds, messages and positions of normal templates are unchanged (`Token_parser_still_reports_each_problem_of_a_normal_template`, existing comm tests).

### SEC-D2 Import suggestions: unbounded edit distance (Medium)
- **Evidence (base):** `RowParser.Cell` applies `Max` only to `Text` and `Email` columns (`src/ReleaseMgmt.Domain/Exchange/RowParser.cs:75`), so an `Owner` cell and each `Members` email had no limit; an unknown owner went to `RowParser.Suggest` (`RowParser.cs:124`), a full Levenshtein of the cell against every team handle, user handle and email, lower-casing the cell once per candidate (`ExchangeCore.cs:137`, `AdminHandlers.cs:100`, and seven other call sites).
- **Why Medium:** RTE/Release Manager only, but also triggered by accident: a CSV with one unclosed quote turns the rest of the file into one cell. Base measurements: one 1,000,002-character cell against 300 names took **1 min 23 s** of CPU; a Products preview of 10,000 rows each naming a train a few characters off 200 titles of 200 characters (titles an RTE can create by import too) took **19 min 57 s**, holding a request and a core for the whole time.
- **Reproduction:** `ResourceBoundsTests.A_suggestion_for_an_absurdly_long_cell_costs_nothing`, `Owner_and_member_cells_are_capped_at_the_length_of_an_email_address`; `ResourceInjectionTests.An_owner_cell_longer_than_an_email_is_a_cell_error_not_a_search`, `Ten_thousand_near_miss_rows_against_long_titles_preview_in_seconds_not_minutes`.
- **Fix:** `Owner` cells and each `Members` email are at most 254 characters (a cell error, as for other columns); `Suggest` skips inputs over 300 characters and candidates whose length difference already exceeds the allowed distance (same results, less work); one import plan shares a `SuggestBudget` of 50 million edit-distance cells (`PlanEnv.Suggest`), after which errors carry no suggestion (the preview shows only the first 1,000 errors anyway). Also replaced the per-error scan of every planned row with a lookup (`ExchangeCore.cs:246`, O(errors x rows)).

### SEC-D3 Import width and preview size (Medium)
- **Evidence (base):** `CsvFile.Read` allocated `new string[r.ColCount]` for any width (`src/ReleaseMgmt.Infrastructure/Exchange/CsvFile.cs:34`); `RowParser.ResolveHeader` adds one error per unnamed or unknown column; `CsvImportService.PreviewAsync` returned **all** errors (`CsvImportService.cs:100`) although `ErrorsJson` keeps 1,000. Extrapolated from the 5,000-column measurement, a 5 MB header of commas is 5 million columns, 5 million errors in memory and a response of hundreds of MB that the screen then renders as a table.
- **Reproduction:** `ResourceInjectionTests.A_file_wider_than_the_column_limit_is_rejected_unread` (5,000 columns: base previewed it with 5,000 errors), `The_preview_lists_at_most_1000_errors_and_counts_all_of_them` (base: 1,500 listed).
- **Fix:** `Imports:MaxColumns` (default 200, Q-SEC-D2) checked on `ColCount` before any cell is copied (422 `ImportRejected`, recorded as a `Rejected` job); the preview response lists the same capped list as `ErrorsJson`, `counts.errors` stays the true total.

### SEC-D4 Checklist paste parser (Medium)
- **Evidence (base):** `ChecklistParser.Parse` limits lines (500) but not their length (`src/ReleaseMgmt.Domain/Services/ChecklistParser.cs:45`); `Closest` (`:154`) computes the edit distance of the typed owner, product or gate name against every candidate, allocating a row array per character (`:166`). The task text had no limit, while the import holds `Tasks.Description` to 2,000.
- **Reproduction:** `ResourceBoundsTests.A_pasted_line_longer_than_the_limit_is_an_error_and_is_not_resolved` (base: 9 s for one 200 KB owner token against 250 names), `A_pasted_task_description_is_held_to_the_import_limit_of_2000_characters`, `Closest_match_ignores_a_typed_name_too_long_to_be_a_typo` (base: 14.7 s).
- **Fix:** a line over 2,500 characters is a `LineTooLong` error and is not resolved; a task over 2,000 characters is `TaskTooLong`; the whole paste is refused over 500 x 2,502 characters; `Closest` ignores names over 300 characters.

### SEC-D5 Log injection (Low)
- **Evidence (base):** the Serilog file sink (`src/ReleaseMgmt.Api/Program.cs:29`, default template `{Message:lj}`) writes string property values literally and nothing escapes them. A webhook channel name is accepted with line breaks (`SyncHealthService.AddWebhookAsync` checks only 1 to 80 characters) and is logged verbatim on every failed delivery (`CommDispatchService.RaiseFailureAsync`, `TeamWebhookSender.FailAsync`, `LoggingAlertSink`), so `ops\r\n2020-01-01 00:00:00.000 +00:00 [INF] FORGED-ENTRY backup ok` wrote a second, forged entry.
- **Why Low:** the input needs the Admin policy (RTE or Release Manager), but the log is where an investigation of those same people starts, and any future log call with a user-typed value would repeat it.
- **Checked and not exploitable:** the anonymous vector first suspected (the cross-site guard logs `req.Path` and `Origin` before sign-in, `RequestSecurity.cs:30`) does not forge lines: `PathString` logs its escaped form (`%0D%0A`) and header values cannot hold CR/LF. Connector error messages never carry response bodies (`ConnectorErrors`). Pinned by `LogInjectionTests.A_request_path_or_header_cannot_forge_a_line_in_the_log_file`.
- **Reproduction:** `LogInjectionTests.A_name_typed_into_the_app_cannot_forge_a_line_in_the_log_file` (base: the forged line starts a line of the log file).
- **Fix:** `LogSanitizer`, a Serilog enricher registered for every event, rewrites any property value holding a control character (C0 except tab, DEL, C1, U+2028/U+2029) to `\r`, `\n`, `\uXXXX`. Message templates are constants; exception stack traces keep their line breaks. Central, so new log calls are covered. **Not changed:** the webhook name itself may still contain a line break (a V2 validation gap, part of Q-SEC-D1's per-field list); it is now harmless in the log.

### SEC-D6 Chat markup injection into Slack and Teams (Medium)
- **Evidence (base):** comm dispatch renders webhook bodies with the `JsonString` target, which escapes values for **Markdown** with backslashes (`CommEscaper.Markdown`), then posts `{"text": ...}` to Slack and Teams alike (`src/ReleaseMgmt.Infrastructure/Comms/CommDispatchService.cs:118`, `:223`). Slack does not honour backslash escapes; its only escape is the HTML entity, and `<...>` is a control sequence. A train title, blocker, known issue or condition containing `<!channel>` or `<https://evil|Reset your password>` therefore posted a channel-wide ping or a link under the bot's name. Team notifications posted the message text unescaped to both (`src/ReleaseMgmt.Api/Reminders/TeamWebhookSender.cs:114`), so a gate or step named `[Sign in](https://...)` became a Teams link.
- **Reproduction:** `ChatMarkupInjectionTests.A_dispatched_value_cannot_ping_a_Slack_channel_or_post_a_Slack_link_and_Teams_keeps_its_Markdown_escaping`, `A_team_notice_carries_names_as_text_not_as_chat_markup` (Slack and Teams).
- **Fix:** for Slack destinations, the value-escaped `\<` / `\>` (the renderer marks every `<` `>` that came from a value; the template author's own text is untouched, so an intentional `<https://wiki|runbook>` still works) become `&lt;` / `&gt;` (`CommDispatchService.SlackSafe`). Team notices: entities for Slack, the same Markdown escaping as comm dispatch for Teams. Teams comm-dispatch output is unchanged.
- **Not verified:** rendering in a real Slack workspace or Teams channel (no connectivity); the fix follows Slack's documented escaping rules.

### SEC-D7 Request bodies and field lengths (Medium, partly fixed)
- **Evidence (base):** no `MaxRequestBodySize` or `FormOptions` is set (`Program.cs`), so every route accepted Kestrel's ~30 MB; JSON fields such as task text (`TaskService.cs:24`), step title and instructions, freeze name and `ProductPattern`, reasons, PIR text and conditions have no length limit, so one request could store 30 MB of text that every list, feed, message and PDF then carries.
- **Reproduction:** `ResourceInjectionTests.A_json_body_over_the_request_limit_is_refused_413_before_it_is_read` (base: a 2 MB body was read and deserialized, answered 422 by the service).
- **Fix:** `RequestBodyLimit` middleware, `Limits:MaxRequestBodyBytes` (default 1 MiB, Q-SEC-D1): a declared `Content-Length` over it is 413 `RequestTooLarge` unread, and Kestrel's per-request limit is set to the same value for chunked bodies. The attachment and import uploads opt out with `BodySizeLimit(null)` metadata and keep their own caps (the import endpoint now also sets Kestrel's limit to its 5 MB + 1 MB). **Per-field limits (REOS-66):** one table (`FieldLimits.cs`) and one endpoint filter on `/api/v1`: an over-long value is 422 `{guard:"FieldTooLong", field, max}` before the handler runs; the values and their sources are listed in Q-SEC-D1 and `FieldLimitTests` covers every JSON write route.

### SEC-D8 Export job queue (Low)
- **Evidence (base):** `ExportService.EnqueueAsync` deduplicates only an identical open request (`src/ReleaseMgmt.Infrastructure/Exports/ExportService.cs:68`); release report, run sheet and scorecard are `Read`, so a Viewer could queue one per train and kind (600 jobs at 200 trains) ahead of everyone else's, rendered one at a time; every Done file is kept forever (Q-050c) and every evidence ZIP copies all of a train's attachments.
- **Reproduction:** `ExportQueueLimitTests.One_person_cannot_queue_more_open_exports_than_the_limit` (base: the fourth job was queued).
- **Fix:** `Exports:MaxOpenJobsPerUser` (default 10, Q-SEC-D3), 422 `ExportQueueFull`; a repeat of an open request still returns that job. **Open:** retention of Done files and ZIP disk growth (Q-SEC-D3, Q-050c).

### SEC-D9 Stored previews (Low)
- **Evidence (base):** each import preview writes up to 5 MB of CSV plus a plan file (`CsvImportService.cs:97`); expiry runs only when someone previews (`:74`) and nothing bounds how many one person holds; `ParsePreviews` rows (`TaskParserService.cs:54`, JSON as large as the pasted text allows: up to the body limit) are never deleted and are copied into every 15-minute and nightly backup.
- **Reproduction:** `ResourceInjectionTests.One_person_keeps_at_most_five_open_import_previews_on_disk`, `Expired_checklist_previews_do_not_pile_up_in_the_database` (base: seven open previews and fourteen files; the stale row stayed).
- **Fix:** `Imports:MaxOpenPreviewsPerUser` (default 5, Q-SEC-D4): starting another preview expires that person's oldest and deletes its files. Uncommitted parse previews more than a day past expiry are deleted when anyone parses (committed ones stay: their commit's audit row names them).

### SEC-D10 ICS brake keyed by client address (Low, not fixed)
- **Evidence:** `CalendarEndpoints.Throttled` keys the failure counter by `RemoteIpAddress` and refuses **before** the token is checked (`src/ReleaseMgmt.Api/Endpoints/CalendarEndpoints.cs:25`, `:49`); no forwarded-headers handling is configured. Behind a reverse proxy every caller shares one address, so 61 anonymous bad requests make every valid calendar subscription answer 429 for the rest of the minute.
- **Reproduction:** `ResourceInjectionTests.Strangers_guessing_feed_tokens_behind_the_same_proxy_address_do_not_lock_out_a_valid_calendar` (fails with 429 on base and now; skipped with the reason so CI stays green).
- **Fixed (REOS-67):** the token is looked up first; a valid token is always served and only failed lookups count toward, and are refused by, the brake (Q-SEC-D5, Q-051e updated). The reproduction test is no longer skipped.

## 3. Checked, no issue

- **Raw SQL:** no `FromSqlRaw`, `ExecuteSqlRaw` or `SqlQueryRaw` takes input; the only `CommandText` values are constants (PRAGMAs, `SELECT 1`, integrity check) and `db/analytics.sql` statements, which bind `:from`, `:to`, `:now` as parameters (`AnalyticsService.Run`). Audit filters are LINQ (parameterised) with `LIKE` wildcards escaped; no `ORDER BY` or column name comes from input (grids and metrics are looked up in fixed catalogues). `FreezeService` uses `GLOB` with a stored pattern as a bound parameter.
- **CSV/formula injection:** every export escapes `= + - @`, TAB and CR: `/audit.csv` (`AuditEndpoints.Safe`), grid CSV and XLSX (`CsvSafety`, with ClosedXML's quote prefix handled), evidence `manifest.csv` (`ExportSupport.CsvField`), the Analytics CSV and the import error report built in the browser (`csvCell`, `csvSafe`). XLSX cells are text cells, never formulas (existing tests `CSV_injection_is_neutralised...`, `Formula_like_text_is_stored_as_text_not_a_formula`).
- **HTTP header injection:** file names go through `Results.File` (RFC 6266 encoding) and are sanitised (attachments) or ASCII stems (exports, grids, audit); `X-Content-SHA256`, `X-Export-*`, `X-Audit-*` carry hashes and numbers.
- **Path traversal:** attachments and export files are stored by id with a containment check (`ResolveStored`, `ExportService.Resolve`); import files are named by the job id read from the database; upload file names are reduced to their last segment; backup and restore paths are operator configuration or CLI arguments.
- **Zip slip:** evidence ZIP entries are `files/NNN-<sanitised name>` (no separators, no leading dots) and the PDF entry name is an ASCII stem.
- **XSS (React):** no `dangerouslySetInnerHTML`, `innerHTML` or `eval`; `href`s are fixed prefixes plus server ids, plus the password-reset URL validated as absolute https; the comm preview renders text nodes; SVG export escapes the title; the raw dispatch body is `text/plain` with `nosniff`; attachments carry `CSP: sandbox`.
- **Template re-expansion:** rendered output is never rescanned (a value `{Status}` stays literal), HTML target escapes literals and values (existing `CommTokenTests`).
- **XXE:** no XML is parsed anywhere in `src`; XLSX is only written (an uploaded `.xlsx` is refused by its signature); Ical.Net only serialises.
- **PDF:** QuestPDF documents contain no hyperlinks or URI actions.
- **ReDoS:** every regex applied to input is anchored or linear with no overlapping quantifiers (`EmailShape`, `HandleShape`, `ZoneSuffix`, `StepCode`, `ProductTag`, `OwnerTag`, `\s+`, media type, external keys, client id); .NET's default has no timeout but none needs one.
- **ICS property injection:** Ical.Net escapes CR/LF, `;` and `,` in TEXT values and in `X-WR-CALNAME` (pinned by `A_name_with_line_breaks_cannot_add_a_property_or_an_event_to_an_ICS_feed`).
- **List and paging endpoints:** every `limit`/`take` is clamped (audit 500, imports 200, import rows 1,000, export jobs 200, notifications 100, sync alerts 500, dispatch log); grid and audit exports stop at `Export:MaxRows` / `Audit:CsvMaxRows` (50,000) and say so.
- **Analytics:** no recursive CTE or generated series, so an unbounded `from`/`to` only widens a scan of existing rows; parameters bound.
- **JSON depth:** System.Text.Json's default maximum depth (64) applies to every body; UI state is `json_valid` and 256 KB.
- **CSV import:** 5 MB and 10,000 rows (Q-048g), UTF-8 strict, binary and `.xlsx` refused; Sep parsing.

## 4. Informational (not changed)

- **No general rate limiting** (`AddRateLimiter` is not used; only the ICS brake exists). Accepted for an SSO-only internal tool now that the expensive routes are bounded; see Q-SEC-D6 for when to add per-user concurrency limits.
- **Attachments:** 50 MB per file, no count or total per train, any type (served inert: `attachment`, `nosniff`, `CSP: sandbox`). Uploaders only. A per-train quota would refuse compliance evidence at a threshold, which is a product decision (not built; noted with Q-SEC-D3's retention question). The multipart `entityType`/`entityId` fields are read whole into memory up to the upload cap (`AttachmentEndpoints.cs:62`), about 51 MB, uploaders only.
- **Analytics XLSX** writes formula-like text as text cells without the OWASP apostrophe, unlike the grid XLSX. Excel does not evaluate them; the risk is a person editing the cell or re-saving as CSV. Kept: Q-047b and the existing test pin verbatim text.
- **Ical.Net does not double a backslash** in TEXT values (RFC 5545 3.3.11), so a name typed with a literal `\n` shows a line break in the calendar. Cosmetic; no property can be injected.
- **`FreezeWindows.ProductPattern`** has no length limit; SQLite refuses a `GLOB` pattern over 50,000 bytes, which would make Live Deploy steps fail to start during that window. Release Managers and Governance Officers only; bounded to about 1 MB by SEC-D7; part of Q-SEC-D1's per-field list.
- **SignalR:** server-to-client only (no hub methods), default 32 KB receive limit, authenticated; no per-user connection cap. Accepted.
- **Test isolation of the log file (tests only):** `UseSerilog` without `preserveStaticLogger` routes every in-process host through the static `Log.Logger`, which the last host built replaces and a disposed host resets. One production host is unaffected, but in the parallel test run an event can land in another host's file (the first version of the SEC-D5 test failed that way in the full suite). `SecretsTests`' log scan is exposed to the same effect. Fixed by REOS-70: `UseSerilog(..., preserveStaticLogger: true)` gives each host its own logger, and the log tests run in parallel again (Q-070).
- **Grid and audit XLSX** are built in memory (up to 50,000 rows) with no concurrency limit; peak memory under concurrency was not measured.

## 5. Not verified

- Slack and Teams rendering of the escaped payloads (no workspace); Kestrel's enforcement of the 1 MiB limit on **chunked** bodies (the in-process test server has no `IHttpMaxRequestBodySizeFeature`; the `Content-Length` path is tested).
- Behaviour behind a real reverse proxy (SEC-D10 was simulated by the test server's single client address).
- Log injection through sinks other than the file sink (only the file sink is configured; a sink added through Serilog settings would receive the same enriched events, not tested) and through exception text (stack traces keep line breaks by design). With OpenID Connect configured, the anonymous `/signin-oidc` callback's `error_description` reaches an exception message; not exercised (no identity provider in the test environment).
- Memory peaks of large exports under concurrency; long-running disk growth of export files.
- The time bounds in the new tests are generous (fixed code: milliseconds); they were measured on a shared container.

## 6. Changes

| File | Change |
|---|---|
| `src/ReleaseMgmt.Domain/Services/CommTokens.cs` | SEC-D1: linear `TokenParser`, `MaxErrors` + `TooManyErrors`, `Suggest` skips long names |
| `src/ReleaseMgmt.Infrastructure/Services/CommHydrationService.cs` | SEC-D1: ad-hoc preview held to template limits (`CommPreviewTooLarge`) |
| `src/ReleaseMgmt.Domain/Exchange/RowParser.cs` | SEC-D2: `Owner`/`Members` at most 254; bounded `Suggest`; `SuggestBudget` |
| `src/ReleaseMgmt.Infrastructure/Exchange/ExchangeCore.cs`, `PlanHandlers.cs`, `TrainHandlers.cs`, `AdminHandlers.cs` | SEC-D2: suggestions through `PlanEnv.Suggest` (shared budget); O(errors x rows) loop replaced |
| `src/ReleaseMgmt.Domain/Exchange/ImportSpecs.cs`, `src/ReleaseMgmt.Infrastructure/Exchange/CsvFile.cs` | SEC-D3: `DefaultMaxColumns`, width checked before copying cells |
| `src/ReleaseMgmt.Infrastructure/Services/CsvImportService.cs` | SEC-D3: capped error list in the response; SEC-D9: open previews per person |
| `src/ReleaseMgmt.Domain/Services/ChecklistParser.cs` | SEC-D4: line, task-text and paste length limits; `Closest` bound |
| `src/ReleaseMgmt.Api/LogSanitizer.cs` (new), `src/ReleaseMgmt.Api/Program.cs` | SEC-D5: control characters escaped in every log event |
| `src/ReleaseMgmt.Infrastructure/Comms/CommDispatchService.cs`, `src/ReleaseMgmt.Api/Reminders/TeamWebhookSender.cs` | SEC-D6: Slack entities, Teams Markdown escaping |
| `src/ReleaseMgmt.Api/RequestBodyLimit.cs` (new), `Program.cs`, `Endpoints/ImportEndpoints.cs`, `Endpoints/AttachmentEndpoints.cs` | SEC-D7: global body limit; upload routes opt out and keep their caps |
| `src/ReleaseMgmt.Infrastructure/Exports/ExportOptions.cs`, `ExportService.cs` | SEC-D8: open jobs per requester |
| `src/ReleaseMgmt.Infrastructure/Services/TaskParserService.cs` | SEC-D9: stale parse previews deleted |
| `docs/QUESTIONS.md` | Q-SEC-D1 to Q-SEC-D6 |

New configuration keys (all optional): `Limits:MaxRequestBodyBytes` (1 MiB), `Imports:MaxColumns` (200), `Imports:MaxOpenPreviewsPerUser` (5), `Exports:MaxOpenJobsPerUser` (10). No schema change, no new package, no web change.

New tests: `tests/ReleaseMgmt.Domain.Tests/ResourceBoundsTests.cs` (9), `tests/ReleaseMgmt.Api.Tests/ResourceInjectionTests.cs` (10, one skipped: SEC-D10), `tests/ReleaseMgmt.Api.Tests/LogInjectionTests.cs` (2, in a non-parallel collection: see section 4), `tests/ReleaseMgmt.Api.Tests/ChatMarkupInjectionTests.cs` (3), `tests/ReleaseMgmt.Infrastructure.Tests/ExportQueueLimitTests.cs` (2).

## 7. Test evidence

- **Against the base (`c9a1180` plus only the new test files):** Domain 6 of the 8 tests that compile there fail (the 2 that pass pin unchanged behaviour: normal template errors and near-miss suggestions; the 9th uses the `SuggestBudget` type added with the fix); API `ResourceInjectionTests` and `LogInjectionTests` 9 fail (including `Ten_thousand_near_miss_rows...`, 19 min 57 s, and the webhook-name log forging), 2 pass (the ICS and path/header pins, "checked, no issue"), 1 skipped (SEC-D10, which fails with 429 when unskipped); `ChatMarkupInjectionTests` 3 of 3 fail; `ExportQueueLimitTests` 2 of 2 fail.
- **After the fixes:** `dotnet build ReleaseMgmt.sln --no-incremental` 0 warnings, 0 errors; `dotnet test`: Domain 307/307, Infrastructure 202/202, DrDrill 2/2, Api 537 passed and 1 skipped (SEC-D10); `python3 tests/reference/test_schema.py` 82/82. No web file changed, so `npm run build` / `npm test` were not needed. One full run showed `SyncPollerTests.The_timer_polls_every_five_minutes...` failing once while several other suites loaded the shared machine; it passes in isolation (27/27) and no sync code changed.
