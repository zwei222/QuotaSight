# Deploying the QuotaSight Usage API

This guide is for an operator who deploys `src/QuotaSight.Usage.Api` to **their
own** Cloudflare account: one Access application in front of one Worker with one
D1 database. Every step is executed by that operator, in the Cloudflare dashboard
or with `wrangler` in their own checkout. This repository contains no account id,
no hostname, no team domain and no credential, and nothing in it deploys
anything: the code, the tests and any automation that works in this repository
never hold, request or transmit those values, so an agent or a script needs no
access to the account to change this code.

Everything below is a placeholder. Replace `<ZONE>`, `<HOST>`, `<DB_NAME>`,
`<TEAM_DOMAIN>`, `<AUD>`, `<CLIENT_ID_*>`, `<CLIENT_SECRET_*>` and
`<COLLECTOR_ID>` with the operator's own values, and never commit the result.

## What is deployed

```text
collector / desktop app
  └─ CF-Access-Client-Id + CF-Access-Client-Secret
       └─ Cloudflare Access application (policy action: Service Auth)
            └─ Worker  (verifies Cf-Access-Jwt-Assertion, RS256, aud/iss)
                 └─ D1  (one batch() per accepted snapshot)
```

Access is the only intended way in. The Worker's own `401` exists for a request
that reached it while bypassing Access: with Access in front, Access answers
first, so the Worker's check is defense-in-depth and not the primary gate.

## 1. Prerequisites

**Account and zone**

- A Cloudflare zone in the same account. A Custom Domain attaches a hostname
  from that zone to the Worker, and the Worker becomes the origin for it; without
  a zone there is no public hostname to protect.
- Zero Trust enabled for the account, with a team domain
  (`https://<name>.cloudflareaccess.com`). The team name can be changed; the
  `cloudflareaccess.com` suffix cannot.
- **Workers Paid.**

**Why Workers Paid is a prerequisite, not a tuning choice**

Workers Free allows 10 ms of CPU time per request. Cloudflare states that:

> D1 query execution and result serialization run within the Workers platform CPU and memory limits.

One `PUT` here verifies an RS256 service-token JWT, parses a body of up to
32 KiB, validates up to 62 days and then runs up to 63 statements in a single D1
`batch()`. The
package's test suite — including maximum-size snapshots — passes against a local
D1 that runs the same D1 version, so the ceiling is a property of the plan, not of
the code: on Free the request can be terminated for exceeding CPU (error 1102)
while every local test still passes. Workers Paid allows a 30-second default CPU
limit. This is a plan prerequisite, not a code defect — do not "fix" it by
lowering `MAX_DAYS_PER_SNAPSHOT`, and do not deploy this slice to a Free account.

**Tooling**

- Node 22.12 or newer, and the package's pinned `wrangler` from
  `package-lock.json`.

```bash
cd src/QuotaSight.Usage.Api
npm ci
npm run typecheck
npm test
```

## 2. Placeholders used below

| Placeholder | Meaning |
| --- | --- |
| `<ZONE>` | Zone that owns the hostname |
| `<HOST>` | Public hostname, for example `usage.example.test` |
| `<DB_NAME>` | D1 database name, for example `quotasight-usage` |
| `<TEAM_DOMAIN>` | `https://<name>.cloudflareaccess.com` |
| `<AUD>` | Access application audience tag |
| `<CLIENT_ID_WRITE>`, `<CLIENT_SECRET_WRITE>` | Service token used by a collector |
| `<CLIENT_ID_READ>`, `<CLIENT_SECRET_READ>` | Service token used by the desktop app |
| `<COLLECTOR_ID>` | Collector id: a lowercase slug, 3–64 characters of `[a-z0-9_-]` |

## 3. Steps

Each row names who runs it, where, what to check afterwards, and how to undo it.
Roles: **account operator** (holds the Cloudflare account), **deployer** (runs
`wrangler` in their own checkout), **on-call operator** (owns alerts).

| # | Who | Where | Action | Check | Rollback |
| --- | --- | --- | --- | --- | --- |
| 0 | account operator | Dashboard → Workers & Pages | Confirm the account is on the Workers **Paid** plan. | Plan shows Paid; CPU limit is not 10 ms. | — |
| 1 | account operator | Zero Trust → Settings | Record the team domain. | Value is `https://<name>.cloudflareaccess.com`. | — |
| 2 | account operator | Zero Trust → Access → Applications | Create a self-hosted application for `<HOST>` with an empty path (all paths). Policy action **Service Auth**, include **Service Token**. Use one application per credential class (write, read) or one application with two policies. | Record `<AUD>`. No `Bypass` policy exists. Policy action is `Service Auth`. | Delete the application and re-put `ACCESS_AUD` (the AUD changes). |
| 3 | account operator | Zero Trust → Access → Service credentials | Create two service tokens (write, read). Choose a duration; the API example uses `8760h`, the dashboard default is not documented. | Each Client ID and Secret is shown **once**; store both in the OS credential store or a protected file. Record the expiry date. | Delete the token. |
| 4 | deployer | Own checkout | `npx wrangler d1 create <DB_NAME>` | Command prints a `database_id`. | `npx wrangler d1 delete <DB_NAME>` (destroys data). |
| 5 | deployer | Own checkout | Set `database_name` and `database_id` in `wrangler.toml`. Keep `workers_dev = false` and `preview_urls = false`. | `npm test` still passes (`test/config.test.ts` pins the local-only settings). | `git checkout -- wrangler.toml`. |
| 6 | deployer | Own checkout | `npx wrangler d1 migrations apply <DB_NAME> --local`, then `npm test`. | All tests pass. Local D1 is not remote D1; this is not the remote proof. | Delete the local `.wrangler` state. |
| 7 | deployer | Own checkout | `npx wrangler d1 migrations list <DB_NAME> --remote`, then `npx wrangler d1 migrations apply <DB_NAME> --remote`. | `d1_migrations` lists `0001`, `0002`, `0003`; `sqlite_master` has the three tables, the indexes and the triggers. | Restore a Time Travel bookmark, or add a corrective migration. |
| 8 | deployer | Own checkout | `npx wrangler secret put ACCESS_TEAM_DOMAIN`, `ACCESS_AUD`, `ACCESS_PRINCIPALS`. | `npx wrangler secret list` shows the three names; values never appear in output. | `npx wrangler secret delete <NAME>`. |
| 9 | deployer | Own checkout | `npx wrangler deploy` | The output contains no `*.workers.dev` hostname. `npx wrangler deployments list` shows the new version. | `npx wrangler rollback` to the previous version. |
| 10 | account operator | Dashboard → Worker → Settings → Domains & Routes | Attach the Custom Domain `<HOST>` (or add a route with `custom_domain = true` in the operator's own configuration). | DNS record and certificate exist; `<HOST>` resolves. | Delete the Custom Domain or route. |
| 11 | deployer | Own checkout | Denial check: `curl` the API with no credential. | `401` or `403` when strict service token authentication is on; `302` when it is off (see §3.4). `npx wrangler tail` shows no request — Access did not forward it. | — |
| 12 | deployer | Own checkout | Allow check: `GET /v1/usage/daily` and `PUT .../snapshots/<revision>` with a valid token. | `200` with the expected bodies; then run the smoke test in §5. | Rotate or delete the token. |
| 13 | deployer | Own checkout | Send a wrong Client Secret, a token from another application, and an expired token. | `401` or `403`; the Collector exits `11`/`12` and the GUI shows the credential/redirect message. | — |
| 14 | on-call operator | Zero Trust → Logs, and notification settings | Enable the service-token expiry notification one week before expiry; confirm Access authentication logs record both outcomes. | The notification arrives; logs show non-identity authentication attempts. | Disable the notification. |
| 15 | on-call operator | Dashboard + collector host + app | Run the rotation drill in §6. | Both secrets work during the grace period; the Client ID is unchanged. | Keep the old secret valid until the drill finishes. |

### 3.1 Order: the Access application comes first

Create the Access application before you attach the Custom Domain. The
application is defined for `<HOST>`, so it can exist before that hostname
resolves; the reverse order leaves a window in which the hostname reaches the
Worker with no policy in front of it, and a mis-set path or policy is then only
visible from a live request.

### 3.2 Policies: `Service Auth` only, never `Bypass`

- The policy action must be `Service Auth` with the service token as the
  include. Any other action sends a non-browser client to an identity provider
  login it cannot complete.
- Never add a `Bypass` policy to the application. It enforces no Access control
  and its requests are not logged, which turns the hostname into an
  unauthenticated Worker endpoint.
- Keep the application path empty so every path under `<HOST>` is covered, and do
  not publish the Worker through a Service Binding: a binding invokes the Worker
  directly, so Access never authenticates the caller.

### 3.3 Managed OAuth stays off

Do not enable Managed OAuth on these applications. Managed OAuth is for
interactive clients. With it on, a non-browser client receives `401` with a `WWW-Authenticate` challenge instead of a service-token
outcome, which is indistinguishable at the client from a rejected credential.
Leave it off; service tokens are the only intended caller.

### 3.4 Strict service token authentication decides `401`/`403` versus `302`

The Zero Trust organization setting **strict service token authentication**
changes what a bad or missing token looks like:

- **On:** Access returns `401` or `403` and never redirects a non-browser client.
  Only `Service Auth` policies can authorize the request, `Allow` policies and an
  existing `CF_Authorization` cookie are ignored, and no `CF_Authorization` cookie
  is issued after a successful service-token authentication.
- **Off:** a non-browser client is sent to an interactive login it cannot
  complete, i.e. a `302`, and a rejected credential is therefore reported by the
  clients as a redirect rather than as a credential problem.

Organizations created on or after 2026-10-05 have strict service token
authentication turned on by default and cannot turn it off. For an older
organization, turn it on before the smoke test and before handing the endpoint to
users, so that a wrong secret reads as `401`/`403` (Collector exit codes `11` and
`12`) instead of `302` (exit code `13`).

### 3.5 The AUD changes when an application is recreated

The audience tag stays the same for the life of an application, including across
policy edits and secret rotation, but it changes if the application is deleted and
created again. In that case re-put `ACCESS_AUD` before testing, otherwise every
request is rejected as a wrong audience. Treat step 2's rollback as a two-part
operation for this reason.

## 4. Credential handling during any of these steps

Nothing here belongs in a command argument. Arguments are visible to every local
process through the process list, and they land in shell history.

```bash
# One shell, one secret file directory, no history for these commands.
set +o history
umask 077
mkdir -p "$HOME/.config/quotasight-deploy"
chmod 700 "$HOME/.config/quotasight-deploy"

# Read the values from protected files; never echo them.
read -r CLIENT_ID < "$HOME/.config/quotasight-deploy/client-id"
read -r CLIENT_SECRET < "$HOME/.config/quotasight-deploy/client-secret"

# curl reads request headers from a file: -H @<file>
printf 'CF-Access-Client-Id: %s\n' "$CLIENT_ID" > "$HOME/.config/quotasight-deploy/headers"
printf 'CF-Access-Client-Secret: %s\n' "$CLIENT_SECRET" >> "$HOME/.config/quotasight-deploy/headers"
chmod 600 "$HOME/.config/quotasight-deploy/headers"
curl -sS -H @"$HOME/.config/quotasight-deploy/headers" "https://<HOST>/v1/usage/daily?from=2026-01-01&to=2026-01-04"

# Or keep the same headers in a curl config file and pass only its path.
# headers.cfg:
#   header = "CF-Access-Client-Id: <value>"
#   header = "CF-Access-Client-Secret: <value>"
curl -sS --config "$HOME/.config/quotasight-deploy/headers.cfg" "https://<HOST>/v1/usage/daily?from=2026-01-01&to=2026-01-04"
```

For the Worker side, feed `wrangler secret put` from standard input so the value
is never an argument:

```bash
printf '%s' "$TEAM_DOMAIN" | npx wrangler secret put ACCESS_TEAM_DOMAIN
printf '%s' "$AUD"         | npx wrangler secret put ACCESS_AUD
npx wrangler secret put ACCESS_PRINCIPALS < "$HOME/.config/quotasight-deploy/principals.json"
```

`wrangler secret put` creates a new deployment immediately, so it is a
deployment action, not a local edit. Afterwards, delete the header files and any
temporary body files, and re-enable history in that shell. Do not paste these
values into a ticket, a chat, a review comment or a commit.

## 5. Remote smoke test

Two behaviours this slice depends on are not settled by the D1 documentation, so
they must be proven against **remote D1**, not only against the local Miniflare
database:

1. `RETURNING` on the day upsert and on the receipt insert. `changedDays` and
   `ignoredReductions` are read out of the `batch()` results; if `RETURNING` were
   silently unsupported remotely, the writes would still land and the response
   would under-report as `changedDays: []`.
2. The rollback scope of `batch()`. The official wording says a failing statement
   "aborts or rolls back" the sequence while the same page also describes
   auto-commit per statement; the write path assumes the whole batch rolls back.

The test uses a synthetic collector and synthetic daily values only. Never send
real usage data to prove a deployment.

### 5.1 Prepare the synthetic collector

Use a throwaway collector id such as `smoke-2026-01-04`, generation `1`, and an
owner id of its own (`smoke-owner`). Add its row and a matching principal:

```bash
# The collector row the write path looks up (owner-filtered).
npx wrangler d1 execute <DB_NAME> --remote --command \
  "INSERT INTO collectors (id, owner_id, active_generation, created_at) VALUES ('smoke-2026-01-04', 'smoke-owner', 1, '2026-01-04T00:00:00Z')"
```

Add one principal to `ACCESS_PRINCIPALS` for `<CLIENT_ID_WRITE>`:
`{ "id": "<CLIENT_ID_WRITE>", "ownerId": "smoke-owner", "collectorId": "smoke-2026-01-04", "scopes": ["usage:write"] }`.
Write the full array (the operator's real principals plus this one) to the
protected file and re-put the secret. A writer may hold only `usage:write`, and
the id in the map is the service token's Client ID.

### 5.2 The four checks

Send each body from a file so the payload is not in the shell:

```bash
cat > "$HOME/.config/quotasight-deploy/snapshot-1.json" <<'JSON'
{
  "schemaVersion": 1,
  "adapterVersion": "smoke-1",
  "generation": 1,
  "collectedAt": "2026-01-04T00:00:00Z",
  "bucketTimeZone": "Asia/Tokyo",
  "coveredRange": { "from": "2026-01-01", "to": "2026-01-04" },
  "days": [
    { "date": "2026-01-01", "totalTokens": 1000 },
    { "date": "2026-01-02", "totalTokens": 2000 },
    { "date": "2026-01-03", "totalTokens": 0 }
  ]
}
JSON

curl -sS -X PUT -H @"$HOME/.config/quotasight-deploy/headers" \
  -H 'Content-Type: application/json' \
  --data-binary @"$HOME/.config/quotasight-deploy/snapshot-1.json" \
  "https://<HOST>/v1/collectors/smoke-2026-01-04/snapshots/900001"
```

**Check 1 — a normal write reports exactly what it wrote.** Revision `900001`
must answer `200` with `status: applied`, `changedDays` equal to all three dates
in ascending order, `ignoredReductions: 0`, and a `receivedAt`. A measured zero
for a new day is data and counts as a change.

```bash
npx wrangler d1 execute <DB_NAME> --remote --command \
  "SELECT bucket_date, total_tokens FROM usage_day WHERE collector_id = 'smoke-2026-01-04' ORDER BY bucket_date"
```

The three rows must match the request. Mismatched day rows with an empty
`changedDays` is the failure mode to look for: it means `RETURNING` did not
behave and the write path must not be deployed as is.

**Check 2 — a decrease is ignored while other days still move.** Revision
`900002` carries `2026-01-01: 500` (below the accepted 1000), `2026-01-02: 2500`
(above), and a new `2026-01-04: 700`.

Expected: `status: applied`, `changedDays` = `["2026-01-02", "2026-01-04"]`, and
`ignoredReductions: 1`. Then `usage_day` must still show `2026-01-01 = 1000`,
`2026-01-02 = 2500` and `2026-01-04 = 700`: the reduction is kept out of the
write, not merged into it. This is the case that shows the upsert's `RETURNING`
value (the stored, larger total) is being read, not the submitted one.

**Check 3 — the same revision is a replay.** Resend revision `900002` with the
identical body. Expected: `status: replayed`, `changedDays: []`,
`ignoredReductions: 1` (the stored count of the first write, not a recomputation)
and the same `receivedAt`. The same revision with a different body is
`409 revision_conflict` and writes nothing.

```bash
npx wrangler d1 execute <DB_NAME> --remote --command \
  "SELECT COUNT(*) FROM snapshot_receipts WHERE collector_id = 'smoke-2026-01-04'"
```

The count must be `2`.

**Check 4 — one failing statement rolls the whole batch back.** Send revision
`900000`, which is lower than the accepted `900002`, carrying one brand-new day
(`2026-01-05: 9999`, with `coveredRange` covering it). The day upsert runs before
the receipt insert, and the receipt insert is what aborts, so this isolates the
rollback scope:

- the response is `409 stale_revision` (never `200`, never `500`);
- `usage_day` has **no** row for `2026-01-05`;
- `snapshot_receipts` still has exactly `2` rows.

A row for `2026-01-05` would mean each statement committed on its own and the
write path's atomicity claim is false.

### 5.3 Clean up the synthetic data

```bash
npx wrangler d1 execute <DB_NAME> --remote --command \
  "DELETE FROM usage_day WHERE collector_id = 'smoke-2026-01-04'"
npx wrangler d1 execute <DB_NAME> --remote --command \
  "DELETE FROM snapshot_receipts WHERE collector_id = 'smoke-2026-01-04'"
npx wrangler d1 execute <DB_NAME> --remote --command \
  "DELETE FROM collectors WHERE id = 'smoke-2026-01-04'"
```

Verify that all three counts are `0`, delete the throwaway service token in
Access, and re-put `ACCESS_PRINCIPALS` without the smoke principal. Delete the
snapshot bodies and header files from the protected directory. A leftover
synthetic day would be indistinguishable from real data in the desktop app, and a
leftover principal is a working credential.

## 6. Rotation drill

The Client ID does not change when a service token's secret is rotated, so
`ACCESS_PRINCIPALS` needs no edit: its `id` is the Client ID, and the JWT
`common_name` keeps matching. Run the drill once before you need it.

1. **Rotate the secret** in Zero Trust → Access → Service credentials, with a
   grace period (available range: one hour to 30 days). Both secrets are valid
   for the whole grace period. The new secret is displayed once.
2. **Collector host:** replace the secret file that `LoadCredential=` points at.
   systemd reads `LoadCredential` files when the process starts, so run
   `systemctl restart <UNIT>`; without the restart the old secret stays in the
   running service's credential directory. The value is never in the unit
   environment or on the command line.
3. **Desktop app:** update the stored credential on its settings screen so it is
   written to the OS credential store.
4. **Prove both:** send one request with the new secret and one with the old
   secret during the grace period; both must be accepted. After the grace period
   ends, the old secret must be rejected (`401`/`403`) and the new one must still
   work.
5. **Alerts:** keep the service-token expiry notification set to one week before
   the expiry date, and record the expiry date next to the token's owner.

If a secret leaks, rotate without a grace period: the old secret stops working
immediately, and every client must be updated in the same window.

## 7. Verify in a real environment

The following are not settled by the official documentation and are not proven by
this repository's local test suite. Check each in the operator's own account
before relying on this slice in production.

- [ ] Measure actual CPU time for a 32 KiB, 62-day `PUT` (`wrangler tail` or Workers metrics) and confirm it stays well inside the deployed plan's limit.
- [ ] Confirm on remote D1 that `RETURNING` returns rows from the day upsert and the receipt insert inside `batch()`, i.e. checks 1 and 2 above report the days that were written.
- [ ] Confirm the rollback scope of a `batch()` whose last statement fails, i.e. check 4 above leaves no day row behind.
- [ ] Record the exact status Access returns for a missing token, a wrong secret and a wrong audience, with strict service token authentication on and off, and with Managed OAuth off.
- [ ] Record the service token duration the dashboard offers by default (the API example uses `8760h`; no default is documented) and compare it with the expiry you recorded.
- [ ] Confirm whether `wrangler d1 migrations apply` runs each migration file in one transaction, by observing a deliberately failing statement inside a copy of a migration file.
- [ ] Confirm whether Access forwards the `CF-Access-Client-Id` and `CF-Access-Client-Secret` request headers to the Worker, to judge whether they can appear in origin-side logs.
- [ ] Confirm that remote D1's SQLite version applies the existing-row check to `ALTER TABLE ... ADD COLUMN ... CHECK`, which `migrations/0003_receipt_ignored_reductions.sql` relies on.
- [ ] Confirm the operational effect of the AUD changing: after deleting and recreating the application, requests fail until `ACCESS_AUD` is re-put, and succeed immediately afterwards.

## 8. Rollback

Undo in the reverse order of the steps, and prefer the narrowest action that
restores service.

| Situation | Action | Effect |
| --- | --- | --- |
| Bad Worker version deployed | `npx wrangler deployments list`, then `npx wrangler rollback <version-id>` | Previous Worker version serves again; D1 data is untouched. |
| Wrong or missing Access configuration | Re-put the `ACCESS_*` secrets | Until valid, every request answers `503 auth_unconfigured`; no client can fix that with a credential. |
| Wrong policy or a policy change denies clients | Restore the `Service Auth` policy or re-add the token to the include | Requests pass once the policy matches again. |
| Suspected credential leak | Rotate without grace period, then update every client | Old secret stops working immediately. |
| Bad migration data | `npx wrangler d1 time-travel restore <DB_NAME> --bookmark <bookmark>` for a bookmark taken before the step | Rows written after the bookmark are lost. |
| Hostname must stop serving | Delete the Custom Domain or route | With `workers_dev = false` and `preview_urls = false`, the Worker has no reachable hostname. |
| Application must be removed | Delete the application, then re-put `ACCESS_AUD` if a new one is created | The AUD changes; the old AUD is rejected. |
| Full teardown | Remove routing, then the application, then the secrets, then `npx wrangler d1 delete <DB_NAME>` | Deleting the database destroys all stored usage. |

## Official references

- [Service tokens](https://developers.cloudflare.com/cloudflare-one/access-controls/service-credentials/service-tokens/) — headers, `Service Auth`, strict service token authentication, duration and grace period.
- [Application token](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/application-token/) — JWT claims, `common_name` as the service token's Client ID.
- [Validate JWTs](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/validating-json/) — `Cf-Access-Jwt-Assertion`, JWKS, RS256, six-week key rotation with a seven-day overlap.
- [Access policies](https://developers.cloudflare.com/cloudflare-one/access-controls/policies/) — `Service Auth` and `Bypass` semantics.
- [Workers and Cloudflare Access](https://developers.cloudflare.com/workers/configuration/cloudflare-access/) — protecting a hostname versus the whole Worker.
- [Custom Domains](https://developers.cloudflare.com/workers/configuration/routing/custom-domains/) — a zone in the same account is required.
- [Workers limits](https://developers.cloudflare.com/workers/platform/limits/) — CPU time per plan.
- [D1 limits](https://developers.cloudflare.com/d1/platform/limits/) — statements, parameters and result bounds.
- [D1 Worker API](https://developers.cloudflare.com/d1/worker-api/d1-database/) — `batch()`.
- [D1 migrations](https://developers.cloudflare.com/d1/reference/migrations/) — `d1_migrations`, apply order.
- [Wrangler D1 commands](https://developers.cloudflare.com/workers/wrangler/commands/d1/) — `create`, `migrations list/apply`, `execute --remote`, `time-travel`.
- [Worker secrets](https://developers.cloudflare.com/workers/configuration/secrets/) — keep values out of `[vars]`.

[Back to README](../README.md) · [Usage API package](../src/QuotaSight.Usage.Api/README.md) · [Security](security.md) · [Architecture](architecture.md)
