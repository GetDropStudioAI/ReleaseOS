# Open questions from build sessions

Claude Code: add a question here instead of deciding. Format:

```
## Q-001 · M<n> · <short title>
Context: <what you were building>
Question: <the decision needed>
Options considered: <a / b, with tradeoffs>
Blocked: <what is blocked, what you did instead>
```

## Q-001 · M1 · How the EF migrations create the schema
Context: REOS-16/17. M1 asks for EF entities + fluent config (CHECKs, partial indexes, FKs) and a triggers migration.
Question: may migration `Schema` execute db/schema.sql's DDL verbatim instead of EF-generated DDL?
Options considered: (a) fluent-configure every CHECK/FK/index in EF (47 tables, ~100 CHECKs; drift risk between two definitions of the contract) / (b) run schema.sql's CREATE TABLE/INDEX text verbatim, map entities for columns and keys only, and assert with tests that migrated DDL text equals schema.sql and the EF model matches every column.
Blocked: nothing. Built (b): `SchemaContractTests` proves table/index/trigger DDL is byte-identical to schema.sql and the model maps every column. Relationships are not navigations; FKs live in the database. Say if you want (a).

## Q-002 · M1 · Where domain services live
Context: CLAUDE.md puts "domain services (no EF, no ASP.NET)" in ReleaseMgmt.Domain, but the services must read and write the database in one transaction with an audit row (rule 3).
Question: are services allowed to depend on the DbContext?
Options considered: (a) Domain holds pure guard logic over plain snapshot records; orchestration classes (TrainLifecycleService, GateService, …) live in Infrastructure/Services / (b) repository interfaces in Domain with EF implementations.
Blocked: nothing. Building (a) (fewer abstractions, guards stay unit-testable without a database).

## Q-003 · M1 · Two sources of truth for a user's role
Context: authorization uses the role claim (IdP group map, `Auth:RoleMap`), but the separation-of-duties triggers read `Users.Role`.
Question: which wins?
Options considered: (a) just-in-time provisioning: on every sign-in upsert the `Users` row (email, display name, role from the claim), so both always agree / (b) admins maintain `Users.Role` and the claim is ignored / (c) both, with a mismatch refused.
Blocked: nothing. Built (a): `UserProvisioner` runs at sign-in (dev login and OIDC) and puts the `Users.Id` in a `uid` claim. Admin-edited roles are overwritten at the next sign-in; say if you want (b).

## Q-004 · M1 · Is If-Match required?
Context: PROJECT_SCOPE §6 says "Mutations take If-Match: <Version>"; D8 says stale writes get 409.
Question: reject a mutation that has no If-Match header (428), or accept it and skip the check?
Blocked: nothing. Built: header optional; when present and stale -> 409 with the current row. Making it mandatory is a one-line change once the UI always sends it (M2).

## Q-005 · M1 · Waiver rejection has no column
Context: PROJECT_SCOPE §6 lists `POST /waivers/{id}:reject`, but GateWaivers has no rejected/decided-by fields.
Question: add columns (schema change, D-level decision) or drop the endpoint?
Blocked: rejection is not built; only request and approve are.
