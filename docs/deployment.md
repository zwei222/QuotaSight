# Deploying the QuotaSight Usage API

This guide is for an operator who deploys `src/QuotaSight.Usage.Api` to **their
own** Cloudflare account: one Access application in front of one Worker with one
D1 database. Every step is executed by that operator, in the Cloudflare dashboard
or with `wrangler` in their own checkout. This repository contains no account id,
no hostname, no team domain and no credential, and nothing in it deploys
anything: the code, the tests and any automation that works in this repository
never hold, request or transmit those values, so an agent or a script needs no
access to the account to change this code.

The deployment is budgeted against the **Workers Free, D1 Free and Zero Trust Free**
plans. No subscription is bought for it. §1 records the limits that budget rests
on, and §6 is the measurement that decides whether it holds.

Everything below is a placeholder. Replace `<ZONE>`, `<HOST>`, `<DB_NAME>`,
`<TEAM_DOMAIN>`, `<AUD>`, `<CLIENT_ID_*>`, `<CLIENT_SECRET_*>`, `<COLLECTOR_ID>`
and `<OWNER>` with the operator's own values, and never commit the result.

One operator has run this runbook end to end against **one** real account, on
2026-11-22, and §8.1 records what that run observed: the status Access answered
for each rejected credential, the result of a valid write and read credential,
the four remote D1 checks, and the migrations applied remotely. Those are
observations with their conditions, not a guarantee for another account — §8.2
lists what is still open, and a new deployment re-runs §5's four checks instead
of inheriting that result.

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
first, so the Worker's check is defense-in-depth and not the primary gate. That
ordering was observed on 2026-11-22 — a request with no credential was answered
by Access with `403` and an HTML error page and never reached the Worker (§8.1).

## 1. Prerequisites

**Account and zone**

- A Cloudflare zone in the same account. A Custom Domain attaches a hostname
  from that zone to the Worker, and the Worker becomes the origin for it; without
  a zone there is no public hostname to protect.
- Zero Trust enabled for the account, with a team domain
  (`https://<name>.cloudflareaccess.com`). The team name can be changed; the
  `cloudflareaccess.com` suffix cannot.
- **Plans: Workers Free, D1 Free and Zero Trust Free.** No Workers Paid
  subscription is required or requested, and no step below asks for one.

**What the Free plans give you**

These are the numbers the deployment is budgeted against. Record the ones your
account actually reports in step 0; they are the ceiling, not a target.

| Limit | Free plan value | What it means for this API |
| --- | --- | --- |
| Requests | 100,000 requests per day, resetting at 00:00 UTC; exceeding it answers **Error 1027** | One collector poll plus desktop reads; a collector on a 1–6 hour interval cannot approach this |
| CPU time | 10 ms of CPU time per invocation | The open question — see below and §6 |
| Subrequests | 50 subrequests per invocation | One JWKS fetch per cold isolate; no other `fetch()` exists in the Worker |
| D1 queries | 50 queries per invocation, and a `batch()` counts as one query | Two lookups plus one `batch()` per write; two queries per read |
| D1 writes | 100,000 rows written per day | One write per changed day, plus one receipt per accepted snapshot |
| D1 reads | 5,000,000 rows read per day | The read is owner-indexed and bounded at 5000 rows per response |
| D1 storage | 5 GB per account | Days and receipts only; no raw provider data |
| Zero Trust service tokens | 50 service tokens, and a service token does not consume a seat | Two tokens (write, read) plus rotation grace |
| Zero Trust Access logs | Access logs are kept 24 hours | Enough to debug the day of a failure, not a week later (§3.7) |
| Workers Logs | 200,000 events per day, and Workers Logs keeps 3 days | An observability aid, and the reason the gate in §6 is decided immediately after the deploy |
| Recovery | A database queried past its limit fails and recovers at 00:00 UTC | A limit is an outage until midnight UTC, not an invoice |

**What is not charged**

Zero Trust onboarding asks for payment details even on the Free plan. On the Free
plan nothing is charged: the payment method is a requirement of the signup flow,
not a billing event. Three rules keep it that way:

- **Do not upgrade the account to Workers Paid.** The point of this runbook is
  that the deployment stays inside the free allowance; an upgrade would remove
  the CPU question in §6 by buying the answer, which is the one thing the
  operator's condition forbids.
- Do not enable a D1 or Workers feature that is billed separately (Tail Workers,
  Logpush, paid plan only settings). If a step seems to need one, stop and treat
  it as a failed gate rather than as a purchase.
- A limit that is reached is an outage until 00:00 UTC, never a charge. That is
  the accepted failure mode: the desktop app keeps its last values and shows that
  the read failed.

**Why the CPU budget, not the plan, is the open question**

Workers measures CPU time, not wall time:

> CPU time measures how long the CPU spends executing your Worker code. Waiting on
> network requests (such as `fetch()` calls, KV reads, or database queries) does
> not count toward CPU time.

Waiting on a D1 query does not count toward CPU time, so the D1 round trip — the
largest part of a 63-statement `batch()` — is outside the limit. What counts is
the work this Worker does itself:

- the RS256 verification of the Access assertion (WebCrypto, once per request;
  a JWKS cache miss adds a key fetch whose *wait* is not CPU but whose parsing is),
- `JSON.parse` of a body of up to 32 KiB,
- validation, then assembly of up to 62 day statements plus one receipt statement
  for a single `batch()`,
- `JSON.stringify` of the response — at most 5000 rows for a read, the small
  receipt for a write.

The D1 FAQ says:

> D1 query execution and result serialization run within the Workers platform CPU and memory limits.

Read that as scope, not as double counting. It means that using D1 does not put a
deployment outside the platform limits: the SQL duration inside D1 is D1's work,
while the part of result serialization that happens in the Worker — turning the
returned rows into this API's JSON, i.e. the `JSON.stringify` above — is counted
as Worker CPU. Both statements are consistent, and together they say the budget
question is about this Worker's own JavaScript, not about how long D1 takes.

Cloudflare's own profiling guidance says heavier workloads that handle
authentication typically use 10-20 ms, i.e. the same order as the Free plan's
10 ms. There is **no measurement of this implementation**: the test suite runs a
real local D1 and settles correctness, not CPU, and no number in this document is
a measurement of a deployed Worker. The honest position is therefore that this
slice is **conditionally feasible** on Workers Free (unverified): it plausibly
fits, and whether it fits is decided by the gate in §6, not by an assumption here.

Two things follow, and both are rules rather than advice:

- Do not "fix" the CPU question by lowering `MAX_DAYS_PER_SNAPSHOT` or the body
  bound. That removes coverage the collector needs, and the ceiling would still be
  unmeasured.
- Do not settle it by upgrading the plan. If the gate in §6 fails, the answer is
  §6.5, and §6.5 says the deployment is called off when nothing there helps.

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
| `<OWNER>` | Owner id shared by the collector row and the principal that writes to it |
| `<CONFIG_FILE>` | Deployment configuration file outside version control (step 6) |

## 3. Steps

Each row names who runs it, where, what to check afterwards, and how to undo it.
Roles: **account operator** (holds the Cloudflare account), **deployer** (runs
`wrangler` in their own checkout), **on-call operator** (owns alerts).

| # | Who | Where | Action | Check | Rollback |
| --- | --- | --- | --- | --- | --- |
| 0 | account operator | Dashboard → Workers & Pages, D1, and Zero Trust → Settings | Confirm the plan of all three: Workers **Free**, D1 **Free**, Zero Trust **Free** (payment details are on file from onboarding; nothing is charged). Write the §1 limits next to the account so the gate in §6 is judged against the real allowance. | All three read Free. Nothing on the account is on a paid plan, and no payment method is charged. | — |
| 1 | account operator | Zero Trust → Settings | Record the team domain. | Value is `https://<name>.cloudflareaccess.com`. | — |
| 2 | account operator | Zero Trust → Access → Applications | Create a self-hosted application for `<HOST>` with an empty path (all paths). Policy action **Service Auth**, include **Service Token**. Use one application per credential class (write, read) or one application with two policies. | Record `<AUD>`. No `Bypass` policy exists. Policy action is `Service Auth`. | Delete the application and re-put `ACCESS_AUD` (the AUD changes). |
| 3 | account operator | Zero Trust → Access controls → Access settings → Manage service tokens | Turn **strict service token authentication** on (recommended); on organizations created on or after 2026-10-05 it is already on and cannot be turned off. Do this before the smoke test. | The setting reads on. A bad credential then answers `401`/`403` instead of redirecting (§3.4). | Turn it off only if a client cannot be moved to `401`/`403` handling; record why. |
| 4 | account operator | Zero Trust → Access → Service credentials | Create two service tokens (write, read). Choose a duration: the dashboard offers up to **`10 years`**, and a decade-long credential outlives the operator who created it, so choose about **`one year`**. The API example uses `8760h`; the default is still unconfirmed (§8.2). Two tokens out of 50 available, and a service token does not consume a seat. | Each Client ID and Secret is shown **once**; store both in the OS credential store or a protected file. Record the expiry date. | Delete the token. |
| 5 | deployer | Own checkout | `npx wrangler d1 create <DB_NAME>` | Command prints a `database_id`. | `npx wrangler d1 delete <DB_NAME>` (destroys data). |
| 6 | deployer | Own checkout | Create `<CONFIG_FILE>` next to `wrangler.toml` (a copy of it) with `database_name` and `database_id` replaced, `workers_dev = false` and `preview_urls = false` kept, and **no** route yet. Pass it with `-c` on every command. Do not edit `wrangler.toml` itself. Exclude the file with `.git/info/exclude`. | `npx wrangler deploy -c <CONFIG_FILE> --dry-run` reads the account's own `database_id`; `npm test` still passes, because `test/config.test.ts` pins `wrangler.toml` to the local-only settings. `git status` does not list the file, and neither does `git ls-files -co --exclude-standard` — the file list the hygiene test scans. | Delete the file, or move it back out of the checkout. |
| 7 | deployer | Own checkout | `npx wrangler d1 migrations apply <DB_NAME> --local -c <CONFIG_FILE>`, then `npm test`. | All tests pass. Local D1 is not remote D1; this is not the remote proof. | Delete the local `.wrangler` state. |
| 8 | deployer | Own checkout | `npx wrangler d1 migrations list <DB_NAME> --remote -c <CONFIG_FILE>`, then `npx wrangler d1 migrations apply <DB_NAME> --remote -c <CONFIG_FILE>`. | `d1_migrations` lists `0001`–`0003`; `sqlite_master` has the three tables, the two indexes and the four triggers (§8.1). | Restore a Time Travel bookmark, or add a corrective migration. |
| 9 | deployer | Own checkout | `npx wrangler deploy -c <CONFIG_FILE>` **before** the Custom Domain exists. | The output says `No targets deployed` — no route, no `*.workers.dev` hostname — so the Worker exists but nothing outside can reach it yet. | `npx wrangler delete -c <CONFIG_FILE>`. |
| 10 | deployer | Own checkout | Now, with the Worker in place, `npx wrangler secret put ACCESS_TEAM_DOMAIN -c <CONFIG_FILE>`, then `ACCESS_AUD` and `ACCESS_PRINCIPALS` the same way. | `npx wrangler secret list -c <CONFIG_FILE>` shows the three names; values never appear in output. | `npx wrangler secret delete <NAME> -c <CONFIG_FILE>`. |
| 11 | deployer | Own checkout | Add `routes = [{ pattern = "<HOST>", custom_domain = true }]` to `<CONFIG_FILE>` and redeploy with `-c`. (The dashboard equivalent is Worker → Settings → Domains & Routes.) | DNS record and certificate exist; `<HOST>` resolves; the response carries Access's headers, not the Worker's. | Remove the route from `<CONFIG_FILE>` and redeploy, or delete the Custom Domain in the dashboard. |
| 12 | deployer | Own checkout | Denial check: `curl` the API with no credential. | **Observed: `403`**, `content-type: text/html`, the Access error page — and `npx wrangler tail` shows no request, so Access did not forward it (§3.4, §8.1). | — |
| 13 | deployer | Own checkout | Allow check: `GET /v1/usage/daily` with the read token, then `PUT .../snapshots/<revision>` with the write token. | **Observed: `200`** for the read (about 0.09 s, daily totals and `collectors[].lastReceivedAt`), and `403 forbidden_scope` from the Worker when the write token is used on `GET` — proof that the request passed Access. Then run §5, then the CPU gate in §6 before handing the endpoint to any user. | Rotate or delete the token. |
| 14 | deployer | Own checkout | Send a wrong Client Secret and a revoked secret (the previous secret after a rotation). | **Observed: `401`** for both, with the same Client ID. The Collector exits `11`, the GUI shows the credential message. A token from another application and an expired token are still unverified (§8.2). | — |
| 15 | on-call operator | Zero Trust → Logs, and notification settings | Enable the service-token expiry notification one week before expiry; confirm Access authentication logs record both outcomes. | The notification arrives; logs show non-identity authentication attempts. Access logs are kept 24 hours, so read them the same day (§3.7). | Disable the notification. |
| 16 | on-call operator | Dashboard + collector host + app | Run the rotation drill in §7. | Both secrets work during the grace period; the Client ID is unchanged. | Keep the old secret valid until the drill finishes. |

### 3.1 Order: the Access application comes first

Create the Access application before you attach the Custom Domain. The
application is defined for `<HOST>`, so it can exist before that hostname
resolves; the reverse order leaves a window in which the hostname reaches the
Worker with no policy in front of it, and a mis-set path or policy is then only
visible from a live request.

The `wrangler` side has the same shape, and the order it was actually run in is
the one steps 9 to 11 describe:

1. deploy the Worker **without a route and without a workers.dev subdomain**
   (`No targets deployed`), so it exists but nothing outside can reach it;
2. put the secrets, now that the Worker they belong to exists;
3. add the Custom Domain and redeploy.

Step 2 exists because `wrangler secret put` against a name that has never been
deployed can ask for confirmation (or offer to create the Worker) before it
writes anything — a prompt in the middle of a credential-handling step is the
wrong place to discover a missing deploy. Step 1 exists because it makes the
first two deploys harmless: a Worker with no hostname and no `workers.dev`
subdomain serves nobody, so an incomplete configuration cannot be reached.

### 3.2 Policies: `Service Auth` only, never `Bypass`

- The policy action must be `Service Auth` with the service token as the
  include. Any other action sends a non-browser client to an identity provider
  login it cannot complete.
- **Re-read the action before saving.** In the dashboard, changing another part
  of the policy (the include, the name, the session duration) can make the
  existing choice disappear: the selector **resets to `Allow`** without warning,
  which is the failure this step produced during a real deployment. Save only
  when the action row shows `Service Auth`.
- **Do not include `Any Access Service Token`.** Bind the specific token or
  tokens the deployment uses. The broader include accepts a token minted for a
  different application in the same organization, which defeats one application
  per credential class.
- **If the Value field offers no candidate, create the token first** (step 4) and
  come back: the field only lists tokens that already exist.
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

Turn this setting **on (recommended)** before the smoke test, at
Zero Trust → Access controls → Access settings → Manage service tokens. It is a
Zero Trust organization setting, not part of this repository, and it changes what
a bad or missing token looks like:

- **On** — only `Service Auth` policies can authorize the request, `Allow`
  policies and an existing `CF_Authorization` cookie are ignored, and no
  `CF_Authorization` cookie is issued after a successful service-token
  authentication. What that looks like at the client was observed on 2026-11-22
  against one account, with the setting on and a `Service Auth`-only policy:
  - **No credential at all → HTTP 403**, `content-type: text/html`, the Access
    error page (title `Error ・ Cloudflare Access`). The answer is Access's own page,
    not this API's JSON, so the request did not get as far as the Worker;
    `wrangler tail` was not watched during that run.
  - **Correct Client ID, wrong secret → HTTP 401.** Only the status was recorded;
    the body was not inspected.
  - **A revoked secret → HTTP 401 as well** — the previous secret after a
    rotation, with the Client ID unchanged.
  - **No `302` was observed** for any of the three, which is the whole point of
    the setting: a non-browser client is never sent to a login it cannot
    complete.
- **Off** — a non-browser client is sent to an interactive login it cannot
  complete, i.e. a `302`, and a rejected credential is therefore reported by the
  clients as a redirect rather than as a credential problem. This half is
  **not observed**: no organization with strict service token authentication off
  was tested, so a `302` remains the documented expectation rather than a
  measured one (§8.2).

A token from another application and an expired token were **not** observed
either; only a missing credential, a wrong secret and a revoked secret were. Do
not read the 403/401 split above as covering them.

The clients already classify these outcomes consistently with the observation
above: the Collector maps `401` to exit `11`, `403` to exit `12` and any `3xx` to
exit `13` with "redirect not followed", and the desktop app shows a `403` as a
credential/scope problem and a redirect as an endpoint problem it never follows.
Nothing in this repository needs to change with the setting — it only decides
which of those three a client sees.

Organizations created on or after 2026-10-05 have strict service token
authentication turned on by default and cannot turn it off. For an older
organization, turn it on before step 12 and before handing the endpoint to users,
so that a wrong secret reads as `401`/`403` (Collector exit codes `11` and `12`)
instead of `302` (exit code `13`). Access still evaluates the service token in
both cases.

### 3.5 The AUD changes when an application is recreated

The audience tag stays the same for the life of an application, including across
policy edits and secret rotation, but it changes if the application is deleted and
created again. In that case re-put `ACCESS_AUD` before testing, otherwise every
request is rejected as a wrong audience. Treat step 2's rollback as a two-part
operation for this reason.

### 3.6 The evidence a Free-plan account keeps

Three surfaces exist, and they age differently. Nothing here needs a paid
feature:

- **Worker metrics** — requests, errors and invocation statuses. This is where
  the `Exceeded CPU Time Limits` counter in §6 is read.
- **Workers Logs** — 200,000 events per day, and Workers Logs keeps 3 days. It is
  off in `wrangler.toml` (`[observability] enabled = false`) and this runbook
  does not change that; §6.6 says what enabling it would mean.
- **Access logs** — non-identity authentication attempts, kept 24 hours only.
  Logpush is not available on the Free plan, so there is no export and no archive.

### 3.7 Investigating an authentication failure

Because Access logs are kept only the last 24 hours, an authentication failure
has to be debugged the day it happens. Record these before the window closes:

1. The status Access returned (`401`, `403` or `302`) and the UTC time.
2. Whether the request appears in Access logs, and with which outcome.
3. Whether the request appears in `npx wrangler tail`. A request that reaches the
   Worker and gets its `401` from the Worker did **not** come through the Access
   application — check for another route, a Service Binding or a hostname outside
   the application (the Worker's `401` is defense-in-depth, §"What is deployed").
4. The token's Client ID suffix and its expiry date. Never the secret, never the
   full JWT, never the assertion header.

After 24 hours the Access side of that evidence is gone and only the Worker side
remains, which cannot tell a rejected credential from a request that never
arrived. That asymmetry is the reason step 15 asks for the notification to be set
a week ahead rather than relying on a log search later.

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

### 4.1 Rotating a secret instead of displaying the stored one

A service token's secret cannot be read back; it is shown once when created or
rotated. If a step needs the value and nobody has it to hand, do not ask anyone
to reveal the stored copy — **rotate the secret immediately before use**. The
rotation dialog's default is immediate revocation of the previous secret, so the
value displayed in that dialog is the only working one from that moment, and
every other client of that token stops working. That trade is acceptable for a
one-off smoke test and wrong for a running collector.

Rule for a demonstration or a test that shows a value to someone: rotate the
secret right before the test, use the displayed value, and **rotate again after
the test** so the value that was seen is no longer valid. With the grace period
(§7) both secrets work for the window, which is what the rotation drill measures;
without it, the old secret is invalid from the rotation on.

### 4.2 Logging `wrangler` in when the browser is on another machine

`wrangler login` completes an OAuth flow whose redirect target is
`http://localhost:8976`. If `wrangler` runs on a headless or remote host and the
browser is on another machine, the browser reaches *its own* localhost and the
callback never arrives — the login stays pending.

The flow can be finished by hand:

1. start `npx wrangler login`, open the printed URL in the browser on whatever
   machine has one, and approve;
2. the browser is then sent to `http://localhost:8976/oauth/callback?...` and
   fails to load it — copy that full callback URL from the address bar;
3. on the machine running `wrangler`, `curl` that exact URL once:
   `curl -sS 'http://localhost:8976/oauth/callback?<query>'`;
4. `wrangler` prints the login result and stores the token.

The `code` in that URL is single-use and time-limited; do not paste the URL into
a ticket, a chat or a commit, and do not keep it after the login succeeds. When
the deployment work is done, `wrangler logout` — a login that outlives the task
is a credential nobody is watching.

## 5. Remote smoke test

Two behaviours this slice depends on are not settled by the D1 documentation, so
they have to be proven against **remote D1**, not only against the local
Miniflare database:

1. `RETURNING` on the day upsert and on the receipt insert. `changedDays` and
   `ignoredReductions` are read out of the `batch()` results; if `RETURNING` were
   silently unsupported remotely, the writes would still land and the response
   would under-report as `changedDays: []`.
2. The rollback scope of `batch()`. The official wording says a failing statement
   "aborts or rolls back" the sequence while the same page also describes
   auto-commit per statement; the write path assumes the whole batch rolls back.

**Observed on 2026-11-22**, against remote D1 in one account, following this
section: all four checks matched the expectations below, including `changedDays`,
`ignoredReductions`, the stored day totals and the receipt count (§8.1). That
settles the two questions for that account and that D1 release. It does not
settle them for another account, another D1 release or a schema change, so a new
deployment **re-runs the same four checks** before the endpoint is handed to a
user: the result is a proof to reproduce, not a permission to skip this section.

The test uses a synthetic collector and synthetic daily values only. Never send
real usage data to prove a deployment.

### 5.1 Prepare the synthetic collector

Use a throwaway collector id such as `smoke-2026-01-04`, generation `1`, and an
owner id of its own (`smoke-owner`). Add its row and a matching principal:

```bash
# The collector row the write path looks up (owner-filtered).
npx wrangler d1 execute <DB_NAME> --remote -c <CONFIG_FILE> --command \
  "INSERT INTO collectors (id, owner_id, active_generation, created_at) VALUES ('smoke-2026-01-04', 'smoke-owner', 1, '2026-01-04T00:00:00Z')"
```

Add one principal to `ACCESS_PRINCIPALS` for `<CLIENT_ID_WRITE>`:
`{ "id": "<CLIENT_ID_WRITE>", "ownerId": "smoke-owner", "collectorId": "smoke-2026-01-04", "scopes": ["usage:write"] }`.
Write the full array (the read principal, plus this one) to the protected file
and re-put the secret. A writer may hold only `usage:write`, and the id in the
map is the service token's Client ID. If the write Client ID is already bound to
the real collector in that array, remove that entry first — §5.3 explains why it
cannot stay.

### 5.2 Register the real collector

The smoke collector is throwaway; the collector that actually reports needs a
row of its own, and no migration creates it. This step was missing from earlier
versions of this runbook, which meant a correctly deployed API had nothing to
write to:

```bash
npx wrangler d1 execute <DB_NAME> --remote -c <CONFIG_FILE> --command \
  "INSERT INTO collectors (id, owner_id, active_generation, created_at) VALUES ('<COLLECTOR_ID>', '<OWNER>', 1, strftime('%Y-%m-%dT%H:%M:%SZ','now'))"
```

Two things have to line up, and the database does not check either of them:

- `ownerId` must be the principal's `ownerId` for that service token. Every
  lookup is `WHERE id = ? AND owner_id = ?`, so a mismatch is not a permission
  error: writes answer `404 collector_not_found` and reads return nothing for
  that collector — a shape that looks like a missing collector rather than like
  a misconfigured owner.
- The id must be exactly the slug the collector is configured with. A write
  credential is bound to one collector, so any other id is `403
  forbidden_collector`, including an id that does not exist.

`active_generation` is `1` for a new collector. Changing it later is a direct
database update; this slice has no rotation operation (see the package README).

### 5.3 One Client ID, one subject: swapping the principals

`ACCESS_PRINCIPALS` is keyed by Client ID and the whole array is discarded if any
entry is invalid, so one Client ID cannot appear as two subjects. An array that
holds the same Client ID twice — once bound to the smoke collector, once to the
real one — is invalid as a whole: every request then answers `503
auth_unconfigured`, which looks like a broken deployment rather than like a bad
principal.

So this is a **swap**, not an addition:

1. before the smoke test, `ACCESS_PRINCIPALS` holds the write Client ID bound to
   the **smoke** collector, plus the read Client ID with `usage:read`;
2. after the cleanup in §5.5, re-put `ACCESS_PRINCIPALS` with the write Client ID
   bound to the real `<COLLECTOR_ID>` and `<OWNER>`;
3. check with one write from the real collector before considering the
   deployment finished. A read at that point shows the real collector with
   `lastReceivedAt` set, which is the only cheap proof that the swap landed.

Do not leave the smoke binding in place: the smoke collector then receives the
real collector's snapshots, and the desktop app shows a collector nobody
provisioned.

### 5.4 The four checks

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
npx wrangler d1 execute <DB_NAME> --remote -c <CONFIG_FILE> --command \
  "SELECT bucket_date, total_tokens FROM usage_day WHERE collector_id = 'smoke-2026-01-04' ORDER BY bucket_date"
```

The three rows must match the request. Mismatched day rows with an empty
`changedDays` is the failure mode to look for: it means `RETURNING` did not
behave and the write path must not be deployed as is.

*Observed on 2026-11-22: `changedDays` carried all three dates in ascending order
and `ignoredReductions` was `0`, with the three day rows present and matching.*

**Check 2 — a decrease is ignored while other days still move.** Revision
`900002` carries `2026-01-01: 500` (below the accepted 1000), `2026-01-02: 2500`
(above), and a new `2026-01-04: 700`.

Expected: `status: applied`, `changedDays` = `["2026-01-02", "2026-01-04"]`, and
`ignoredReductions: 1`. Then `usage_day` must still show `2026-01-01 = 1000`,
`2026-01-02 = 2500` and `2026-01-04 = 700`: the reduction is kept out of the
write, not merged into it. This is the case that shows the upsert's `RETURNING`
value (the stored, larger total) is being read, not the submitted one.

*Observed on 2026-11-22: `changedDays` was the two expected dates and
`ignoredReductions` was `1`, and `usage_day` kept `2026-01-01 = 1000`.*

**Check 3 — the same revision is a replay.** Resend revision `900002` with the
identical body. Expected: `status: replayed`, `changedDays: []`,
`ignoredReductions: 1` (the stored count of the first write, not a recomputation)
and the same `receivedAt`. The same revision with a different body is
`409 revision_conflict` and writes nothing.

```bash
npx wrangler d1 execute <DB_NAME> --remote -c <CONFIG_FILE> --command \
  "SELECT COUNT(*) FROM snapshot_receipts WHERE collector_id = 'smoke-2026-01-04'"
```

The count must be `2`.

*Observed on 2026-11-22: `status: replayed`, an empty `changedDays`,
`ignoredReductions: 1` and the first write's `receivedAt` — with the receipt count
still `2`.*

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

*Observed on 2026-11-22: `409 stale_revision`, no row for `2026-01-05`, and the
receipt count still `2` — the whole batch rolled back, so `RETURNING` and the
rollback scope are settled for this account and this D1 release.*

### 5.5 Clean up the synthetic data

```bash
npx wrangler d1 execute <DB_NAME> --remote -c <CONFIG_FILE> --command \
  "DELETE FROM usage_day WHERE collector_id = 'smoke-2026-01-04'"
npx wrangler d1 execute <DB_NAME> --remote -c <CONFIG_FILE> --command \
  "DELETE FROM snapshot_receipts WHERE collector_id = 'smoke-2026-01-04'"
npx wrangler d1 execute <DB_NAME> --remote -c <CONFIG_FILE> --command \
  "DELETE FROM collectors WHERE id = 'smoke-2026-01-04'"
```

Verify that all three counts are `0`, delete the smoke service token in Access if
you created one only for this test, and re-put `ACCESS_PRINCIPALS` without the
smoke principal — with the write Client ID moved to the real collector (§5.3).
Delete the snapshot bodies and header files from the protected directory. A
leftover synthetic day would be indistinguishable from real data in the desktop
app, and a leftover principal is a working credential.

## 6. CPU measurement gate

This is the check that decides whether the deployment stays. Rather than assuming
a plan, the operator measures the CPU this Worker actually uses on the Free plan
and keeps the endpoint unpublished until the measurement passes. Run it
immediately after step 11 and step 13, and keep the desktop client pointed at
nothing but the smoke-test collector until it passes.

### Local estimate before the gate

§1's position is unchanged: nothing here is a measurement of the implementation
in a real environment, so the real deployment is **still unmeasured**. What
exists is a local estimate of the same shapes, and it is useful only as the
baseline the gate result is compared against. It is
**not a substitute for the gate**: the pass/fail decision stays with the two
readings in §6.1.

Method: the package's real entry point (`src/index.ts`, `default.fetch`) is
bundled unchanged and executed on Node 22, so `getApp` memoisation, the JWKS
cache, Hono routing, the middleware and the error handler are all in the path.
The RS256 key pair, the JWTs and the JWKS document are generated in-process and
every outbound `fetch` is answered locally or throws, so no request leaves the
machine. D1 is a stand-in that resolves without waiting: it does the JS-side
work the Worker pays (SQL string building, argument lists, result mapping) with
D1's result shape and bind order, but no I/O and no SQL execution.
`process.cpuUsage()` deltas are sampled per request over thousands of warm
requests, with the request objects built before each window so that the
runtime's own request parsing is not charged to the Worker.

| Shape | p50 | p95 | p99 |
| --- | ---: | ---: | ---: |
| Largest write: 62 days, 32.7 KiB body, 63 statements | 0.49 ms | 1.65 ms | 3.79 ms |
| Typical write: 7 days | 0.30 ms | 1.32 ms | 2.34 ms |
| Largest read: 5000 rows, 64 collectors | 1.22 ms | 2.79 ms | 5.35 ms |
| Read: 62 rows, 1 collector | 0.19 ms | 1.10 ms | 1.74 ms |

Against the 10 ms budget the heaviest write is about 0.5 ms at the median — a
factor of about 20 at p50 and about 6 at p95. Where that median goes, from
isolated step medians: snapshot date validation ~28%, RS256 verification ~23%,
Hono dispatch and response ~20%, and the D1 glue 0.03 ms (the batch is cheap
here precisely because the stand-in executes nothing).

Read it with these discounts:

- deployed CPU is shared and slower, so a 2-3x higher per-request cost is
  plausible; the estimate stays inside 10 ms at 2-3x, which is why it is worth
  recording at all;
- Node is not workerd: RSA verification and SHA-256 run on a libuv thread pool
  here and in-process there, so the split between crypto and JavaScript differs
  even though both run V8;
- the D1 stand-in has no serialization across the binding boundary, so the
  JS-side binding cost is approximated, not measured;
- the first request on a fresh isolate was not measured in workerd, so cold
  start is absent from the table;
- a supplementary run in local workerd (Miniflare with a real SQLite D1) put the
  largest write at 6.7 ms, but that includes SQLite execution inside the same
  process, while production D1 runs off-box and is not charged to Worker CPU —
  it is an upper bound, not a comparable number.

Use it as the comparison baseline: if the P50 measured at the gate is off by an
order of magnitude — above 5 ms, say — suspect the implementation or the
measurement before trusting the number. This subsection changes nothing in
§6.5; those options stay documented and not applied.

### 6.1 Pass condition

Both of these, read from the account that owns the Worker:

- **`Exceeded CPU Time Limits` (Error 1102) is 0 over 24 hours**, counted in
  Metrics → Invocation Statuses for the Worker.
- **`CPU Time per execution` P99 is below 8 ms**, over the same window.

The 8 ms threshold is a deliberate margin below the 10 ms limit: it leaves room
for the slow tail that a P99 does not capture, so a deployment that passes has
not merely avoided the error by luck. When the two disagree, the error count
wins — see §6.2.

### 6.2 Why the graph alone cannot decide

CPU time is reported per execution, and the runtime can let an individual
execution borrow against the measured average instead of terminating it at
exactly 10 ms (rollover). The consequence is that a P99 above 10 ms can be
recorded without a single error, so the CPU-time graph is not by itself proof
that the deployment fits or that it does not. The only true criterion is the
`Exceeded CPU Time Limits` count: an invocation that is actually terminated is
counted there as Error 1102, and a terminated write is a failed request that the
collector retries at the same CPU cost. Use the P99 as the early warning, and the
1102 count as the verdict.

### 6.3 Windows for the measurement

Workers Logs keeps 3 days, and Access logs are kept 24 hours, so the gate is
decided in the first days after the deploy — not by a graph reviewed a week
later. Concretely:

- watch the 1102 counter for a full 24 hours after the boundary cases below have
  run at least a few dozen times each;
- read Workers Logs the same day if the counter moves, because the detail is gone
  after 3 days;
- treat any nonzero 1102 count as a failed gate even if the P99 looks fine.

### 6.4 Boundary cases to hit on purpose

The average request is not what the limit catches; the worst one is. With
synthetic data only (§5.1's throwaway collector and made-up totals), send each
of these repeatedly and watch the counter:

| Case | Shape | Why it is the worst case |
| --- | --- | --- |
| Largest write | 32 KiB body, `coveredRange` of 62 days, 62 day rows → 63 statements in one `batch()` | Maximum `JSON.parse`, maximum statement assembly, maximum `batch()` |
| JWKS cache miss | A `PUT` on a fresh isolate or after the Access keys rotate | Adds a key-set fetch and the parsing of the key set to an otherwise warm request |
| Largest read | 366-day window, 5000 rows, 64 collectors | Maximum `JSON.stringify` and aggregation |
| Cold start | First request after the Worker has been idle | Module initialisation is on the same invocation |

Serialize these rather than firing them concurrently: the point is CPU per
invocation, and a concurrent burst makes the per-execution numbers harder to
attribute.

### 6.5 If the gate fails: fallback options (not applied)

These are the design changes an operator may consider **in this order** if §6.1
fails. None of them is applied by this change: they are documented and not
implemented here, and applying one is a separate decision with its own change and
review.

1. **Delegate the signature check to Access.** Stop verifying the RS256
   assertion inside the Worker and let the Access application be the only
   authority. This removes the largest fixed cost per request. The price is real:
   the Worker's own `401` is defense-in-depth, and without it every request that
   bypasses Access would be served. It is only acceptable if every path other
   than the Custom Domain is closed — `workers_dev = false`,
   `preview_urls = false`, no route, no Service Binding, no second hostname — and
   because it weakens the security posture it needs the operator's explicit
   decision, not a quiet edit.
2. **Move the aggregation into SQL.** Return fewer, wider rows and let SQL do the
   sum, so `JSON.stringify` and the per-row work in JavaScript shrink. This helps
   the read path, which is the one with 5000 rows; it barely helps the write path,
   where the cost is statement assembly and 63 statements.
3. **Splitting the batch does not reduce CPU.** Sending the same statements in
   several `batch()` calls does the same work, statement by statement; it spreads
   it over more invocations and changes nothing about the total, so it is listed
   here to be ruled out rather than tried. The one thing it does change is the
   D1 query count, which is not the limit being tested.

If none of these brings §6.1 inside the limit, the conclusion is that this API
does not fit the Free plans. That conflicts with the operator's absolute
condition — the deployment must cost nothing — so the deployment is called off:
remove the Custom Domain and the Access application (the rollback table in §9
covers the order), keep the local-only package, and record the measured numbers
in the deployment ticket.

### 6.6 If you enable observability

`wrangler.toml` keeps `[observability] enabled = false`, and this change does not
alter it: turning Workers Logs on is a configuration change with its own review,
not a side effect of a runbook edit. If an operator does enable it:

- set `[observability] enabled = true` in their own checkout and redeploy; the
  setting is per Worker, and it does not change behaviour;
- confirm first that no secret can reach a log line. This package writes exactly
  one line of its own (`access_config_invalid`, once per isolate, with no
  configuration value), and it has no other `console` call, but the runtime's
  invocation logs include the request URL — so a token in a query parameter, or a
  JWT pasted into a path, would leak there. Keep credentials in headers, which
  invocation logs do not record;
- keep in mind the Free allowance: 200,000 events per day, and Workers Logs
  keeps 3 days. Exceeding the daily event allowance is a logging outage for the
  rest of that UTC day, so do not leave verbose logging on permanently.

## 7. Rotation drill

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

## 8. Verify in a real environment

Nothing below is settled by the official documentation or by this repository's
local test suite. §8.1 records what one run against one real account observed on
2026-11-22; §8.2 is what that run did not settle. Check each item of §8.2 in the
operator's own account before relying on this slice in production.

### 8.1 Observed in one real account (2026-11-22)

One operator deployed this slice to their own account (Workers Free, D1 Free,
Zero Trust Free) and ran §3, the migrations and §5. These are the results with
the conditions they were observed under — one account, one D1 release, the
Free plans — not a guarantee for another account.

**Access**, with strict service token authentication on, a `Service Auth`-only
policy, no `Bypass` and Managed OAuth off:

- no credential → `403`, `content-type: text/html`, the Access error page; the
  request never reached the Worker.
- correct Client ID, wrong secret → `401`.
- revoked secret (the previous secret after a rotation) → `401`.
- no `302` for any of the three.

**End to end**, with the tokens created for the smoke test:

- a write token on `GET /v1/usage/daily` passed Access and the Worker answered
  `403 forbidden_scope`: the request reached the Worker, so the status is about
  scope and not about the credential.
- the read token on the same `GET` answered `200` in about 0.09 s, with the daily
  totals of the synthetic snapshot and `collectors[].lastReceivedAt`.
- a read token cannot `PUT`: that answered `403 forbidden_scope` as well.

**Remote D1**, the four checks of §5.4:

- Check 1: `applied`, all three days changed, `ignoredReductions: 0`.
- Check 2: `applied`, the two expected days changed, `ignoredReductions: 1`, and
  the stored totals were the accepted ones.
- Check 3: `replayed`, empty `changedDays`, `ignoredReductions: 1`, two receipts.
- Check 4: `409 stale_revision`, no row for the new day, the receipt count still
  two.

That settles `RETURNING` and the rollback scope of a partly failing `batch()` for
this account and this D1 release. It does not settle them for a different account,
a different D1 release or a schema change, which is why §5 still asks a new
deployment to run the same checks.

**Migrations:** `0001`–`0003` applied remotely without error. The schema then
held three tables plus `d1_migrations`, two indexes and four triggers. That
describes this schema; it is not a proof that each migration file runs in one
transaction, and it says nothing about a migration that fails halfway.

**What that run found missing in the runbook:** the collector row. Nothing creates
a collector, so a correctly deployed API accepted nothing until the row in §5.2
existed. That step and the principal swap in §5.3 were added afterwards.

### 8.2 Still unverified

- [ ] Measure actual CPU time for a 32 KiB, 62-day `PUT` and for a 5000-row read, and confirm both stay inside the gate in §6.1 on the Free plan.
- [ ] Confirm whether `wrangler tail` reports `cpuTime` per invocation on the Free plan — §6 depends on one of these two readings being available.
- [ ] Confirm whether enabling `[observability]` on the Free plan makes CPU time visible at all, and whether Workers Logs then stays inside the 200,000-event daily allowance.
- [ ] Confirm that Access logs on the Free plan are really kept only 24 hours and that no export exists, i.e. that §3.7's window is the real one.
- [ ] Record the service token duration the dashboard offers by default: that dialog was observed to offer up to `10 years`, but the default itself is still unconfirmed, and the API example uses `8760h`.
- [ ] Confirm whether `wrangler d1 migrations apply` runs each migration file in one transaction, by observing a deliberately failing statement inside a copy of a migration file.
- [ ] Confirm whether Access forwards the `CF-Access-Client-Id` and `CF-Access-Client-Secret` request headers to the Worker, to judge whether they can appear in origin-side logs.
- [ ] Confirm that remote D1's SQLite version applies the existing-row check to `ALTER TABLE ... ADD COLUMN ... CHECK`, which `migrations/0003_receipt_ignored_reductions.sql` relies on.
- [ ] Confirm the operational effect of the AUD changing: after deleting and recreating the application, requests fail until `ACCESS_AUD` is re-put, and succeed immediately afterwards.

Two further behaviours were simply not exercised by that run, so they are neither
confirmed nor refuted: an organization with strict service token authentication
off (the `302` case in §3.4), and any request that approaches a Free-plan limit —
so §1's "a limit is an outage, not an invoice" remains an expectation rather than
something that run paid for.

## 9. Rollback

Undo in the reverse order of the steps, and prefer the narrowest action that
restores service.

| Situation | Action | Effect |
| --- | --- | --- |
| Bad Worker version deployed | `npx wrangler deployments list`, then `npx wrangler rollback <version-id>` | Previous Worker version serves again; D1 data is untouched. |
| Wrong or missing Access configuration | Re-put the `ACCESS_*` secrets | Until valid, every request answers `503 auth_unconfigured`; no client can fix that with a credential. |
| Wrong policy or a policy change denies clients | Restore the `Service Auth` policy or re-add the token to the include | Requests pass once the policy matches again. |
| Suspected credential leak | Rotate without grace period, then update every client | Old secret stops working immediately. |
| Bad migration data | `npx wrangler d1 time-travel restore <DB_NAME> --bookmark <bookmark>` for a bookmark taken before the step | Rows written after the bookmark are lost. |
| Hostname must stop serving | Remove the `routes` entry from `<CONFIG_FILE>` and redeploy, or delete the Custom Domain in the dashboard | With `workers_dev = false` and `preview_urls = false`, the Worker has no reachable hostname. |
| Application must be removed | Delete the application, then re-put `ACCESS_AUD` if a new one is created | The AUD changes; the old AUD is rejected. |
| CPU gate failed (§6) | Stop serving the hostname first, then remove routing, application and secrets | The free tier cannot carry it; the data stays in D1 until the full teardown below. |
| Full teardown | Remove routing, then the application, then the secrets, then `npx wrangler d1 delete <DB_NAME>` | Deleting the database destroys all stored usage. |

## Official references

- [Service tokens](https://developers.cloudflare.com/cloudflare-one/access-controls/service-credentials/service-tokens/) — headers, `Service Auth`, strict service token authentication, duration and grace period.
- [Application token](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/application-token/) — JWT claims, `common_name` as the service token's Client ID.
- [Validate JWTs](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/validating-json/) — `Cf-Access-Jwt-Assertion`, JWKS, RS256, six-week key rotation with a seven-day overlap.
- [Access policies](https://developers.cloudflare.com/cloudflare-one/access-controls/policies/) — `Service Auth` and `Bypass` semantics.
- [Logs in Cloudflare One](https://developers.cloudflare.com/cloudflare-one/insights/logs/) — which logs a plan keeps, and for how long.
- [Seat management](https://developers.cloudflare.com/cloudflare-one/team-and-resources/users/seat-management/) — seats and service tokens.
- [Workers and Cloudflare Access](https://developers.cloudflare.com/workers/configuration/cloudflare-access/) — protecting a hostname versus the whole Worker.
- [Custom Domains](https://developers.cloudflare.com/workers/configuration/routing/custom-domains/) — a zone in the same account is required.
- [Workers limits](https://developers.cloudflare.com/workers/platform/limits/) — CPU time, requests and subrequests per plan, and the definition of CPU time.
- [Workers pricing](https://developers.cloudflare.com/workers/platform/pricing/) — what the Free plan includes.
- [Error 1102](https://developers.cloudflare.com/support/troubleshooting/http-status-codes/cloudflare-1xxx-errors/error-1102/) — the CPU limit error the gate in §6 counts.
- [Metrics and analytics](https://developers.cloudflare.com/workers/observability/metrics-and-analytics/) — invocation statuses, including `Exceeded CPU Time Limits`.
- [Workers Logs](https://developers.cloudflare.com/workers/observability/logs/workers-logs/) — the free event allowance and the 3-day retention.
- [Profiling CPU usage](https://developers.cloudflare.com/workers/observability/dev-tools/cpu-usage/) — the guidance behind the 10-20 ms figure.
- [D1 limits](https://developers.cloudflare.com/d1/platform/limits/) — statements, parameters, result bounds, and the Free daily rows.
- [D1 FAQ](https://developers.cloudflare.com/d1/reference/faq/) — what happens past the daily limit, and the CPU/serialization wording quoted in §1.
- [D1 Worker API](https://developers.cloudflare.com/d1/worker-api/d1-database/) — `batch()`.
- [D1 migrations](https://developers.cloudflare.com/d1/reference/migrations/) — `d1_migrations`, apply order.
- [Wrangler D1 commands](https://developers.cloudflare.com/workers/wrangler/commands/d1/) — `create`, `migrations list/apply`, `execute --remote`, `time-travel`.
- [Worker secrets](https://developers.cloudflare.com/workers/configuration/secrets/) — keep values out of `[vars]`.

[Back to README](../README.md) · [Usage API package](../src/QuotaSight.Usage.Api/README.md) · [Security](security.md) · [Architecture](architecture.md)
