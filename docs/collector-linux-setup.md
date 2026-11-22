# Running the QuotaSight Collector on a Linux host

The QuotaSight app reads the Usage API. It does not scan a machine's local CLI logs
itself, so the usage of a machine appears in the app only when a Collector on that
machine sends it. The Collector reads one `tokscale graph` JSON document from stdin,
normalizes it into a snapshot, and `PUT`s that snapshot to the API.

This guide sets up that sender on Linux x86-64 with systemd, from the released binary
to a timer that sends every three hours.

Everything below uses placeholders: `example.invalid` for the API origin, `collector-id`
for the collector, `example-user` for the account, `<version>` for the release version.
Replace them with the values issued for the deployment; no real host, id or credential
belongs in this repository or in a shared document.

## 0. What the host needs

- Linux x86-64 with glibc 2.33 or newer (musl is accepted but unverified), and systemd
  250 or newer (`systemctl --version`), which is the first release with `systemd-creds`
  and `LoadCredentialEncrypted=`. The state directory, its lock and its ownership/mode
  checks exist for Linux x86-64 only: on another host `--preview` still works, and
  `--send` refuses with exit `23` (`state_unavailable: UnsupportedPlatform`).
- The API **HTTPS origin** only. `http` is refused (loopback included), redirects are not
  followed, and an empty path, a path, user info, a query or a fragment is refused.
- A write-scoped Access service token for that origin: a **Client ID** and a **Client
  Secret**. Read-only tokens fail with `403`.
- The **collector id** (lowercase slug, 3-64 of `[a-z0-9_-]`) and the **generation** this
  machine was registered with. Two collectors that count the same logs cannot be told
  apart by the API, so one host, one collector id.
- Node.js, because TokScale is distributed on npm (see §3).
- A user manager for the account whose CLI session logs are counted, and a way to keep it
  running while nobody is logged in (`loginctl enable-linger`).

## 1. Install the Collector binary

Each release attaches `QuotaSight-Collector-<version>-linux-x64.zip`. The archive holds
exactly one file, `QuotaSight.Collector`: a self-contained Native AOT executable with no
.NET runtime to install and no debug symbols.

```bash
# Run from an empty directory; the archive unpacks into its own versioned folder.
gh release download <tag> --repo <owner>/<repo> --pattern 'QuotaSight-Collector-*-linux-x64.zip'
unzip QuotaSight-Collector-<version>-linux-x64.zip
sha256sum QuotaSight-Collector-<version>-linux-x64/QuotaSight.Collector   # record it
install -m 0755 QuotaSight-Collector-<version>-linux-x64/QuotaSight.Collector \
  /usr/local/bin/QuotaSight.Collector
```

`install` needs root for `/usr/local/bin`; that is the only privileged step in this guide.
Keep the binary where a non-root unit can execute it and nowhere else; the Collector writes
nothing next to itself (its state lives in `STATE_DIRECTORY`, see §6).

## 2. Register the API credentials as systemd credentials

The Collector reads its client id and client secret from exactly two files in the directory
named by `CREDENTIALS_DIRECTORY`:

```text
$CREDENTIALS_DIRECTORY/access-client-id
$CREDENTIALS_DIRECTORY/access-client-secret
```

They are not accepted as command-line arguments or as environment variables, and there is
no plaintext-file fallback. That variable is only set by systemd when the unit declares
credentials, so the credentials are registered with `systemd-creds` once and never stored
in cleartext for the unit to read:

```bash
install -d -m 0700 ~/.config/example/credentials
set +o history                     # the values must not reach the shell history
read -r -s -p 'Access Client ID: ' collector_client_id; echo
printf '%s' "$collector_client_id" \
  | systemd-creds encrypt --user --name=access-client-id - \
      ~/.config/example/credentials/access-client-id
read -r -s -p 'Access Client Secret: ' collector_client_secret; echo
printf '%s' "$collector_client_secret" \
  | systemd-creds encrypt --user --name=access-client-secret - \
      ~/.config/example/credentials/access-client-secret
unset collector_client_id collector_client_secret
set -o history
chmod 0600 ~/.config/example/credentials/access-client-id \
           ~/.config/example/credentials/access-client-secret
```

The values arrive on stdin, so they never appear in a process argument, and `read -s` keeps
them off the terminal. Each ciphertext embeds the credential name it was encrypted for, and
`LoadCredentialEncrypted=` refuses a file whose embedded name is not the credential id it is
loaded as — that is why the files are named exactly `access-client-id` and
`access-client-secret` and `--name=` repeats the same string.

The ciphertext is user-scoped (`--user`), which is what a user unit's manager can decrypt; a
system unit needs the same files encrypted **without** `--user` (system-scoped), under a
root-owned directory such as `/etc/example/credentials/` (§6.2). A user manager that cannot
reach the host credential key reports `Failed to determine local credential key` when the
unit starts; the unit file itself is fine, and the system manager runs the identical file
with the decryption done as root (§6.2).

Declare them in the unit with `LoadCredentialEncrypted=` (§6). Plain `LoadCredential=` also
works but points at a cleartext file, which is exactly what this avoids. Rotating a
credential is: re-encrypt over the same file name with the same `--name=`, then
`systemctl --user restart` (or `start`) the unit — a running process only ever sees the
decrypted copy systemd made for it.

## 3. Install TokScale 4.23.0

The upstream scanner is [TokScale](https://github.com/junhoyeo/tokscale). Its README
distributes it on npm, documents `npx tokscale@latest` (and `bunx`/`deno x npm:tokscale@…`)
as the ways to run it, lists Node.js or Bun as the prerequisite, and notes that the native
Rust core is bundled with the npm package, so no Rust toolchain is needed. The package
`tokscale` is an alias of `@tokscale/cli`.

**The version is pinned to 4.23.0.** The Collector accepts only `meta.version` `4.23.0`
(adapter `tokscale-4.23.0`) and refuses anything else with exit `4` rather than guessing,
so install and run exactly that version. It is also the reason the wrapper in §4 spells the
version out: `npx --yes tokscale@4.23.0` caches that one version after the first download
and never floats.

`tokscale graph` always writes JSON. There is no `--json` flag, and no `--days` flag: the
window is given with date filters (`--since`, `--until`, `--week`, `--month`, `--today`).
`--no-spinner` keeps the report on stdout clean, and `--offline` avoids network requests
when only local logs are needed.

Run it as the account whose CLI session logs should be counted — TokScale reads the
invoking user's own logs, so a root shell counts root's usage, not the operator's:

```bash
node --version          # TokScale's documented prerequisites are Node.js or Bun
npx --yes tokscale@4.23.0 graph --since "$(date -d '-30 days' +%F)" --no-spinner > graph.json
head -c 200 graph.json  # pretty-printed: "meta" first, then "version": "4.23.0"
rm -f graph.json        # the document contains usage data; it is not needed on disk
```

Keep the window between 7 and 62 days: a longer window is not trimmed, the whole document
is rejected and the run exits `4` sending nothing. Thirty days is a good default, and it
must stay comfortably longer than the send interval from §6.

## 4. The wrapper script

systemd has no shell, so the scan → Collector pipe lives in a small script that the unit
runs. It is the only thing `ExecStart=` names.

`/usr/local/bin/example-collector-run` (root-owned, mode `0755`):

```sh
#!/bin/sh
set -eu
# Placeholders only — replace the endpoint, the collector id and the paths.
# tokscale 4.23.0 writes JSON unconditionally (`graph` has neither --json nor --days), so
# the report window is a --since filter and --no-spinner keeps stdout clean.
npx --yes tokscale@4.23.0 graph \
    --since "$(date -d '-30 days' +%F)" \
    --no-spinner \
  | /usr/local/bin/QuotaSight.Collector --send \
      --endpoint https://example.invalid \
      --collector-id collector-id \
      --generation 1 \
      --bucket-time-zone Asia/Tokyo
```

The pipeline's exit status is the Collector's, so a refused input or a failed send fails
the unit run; systemd records the exit code in the journal. stderr carries one fixed reason
class per run and is the only output — never a body, a header, the full URL or an id.

`--bucket-time-zone` must be the zone the operator verified that the upstream scan buckets
its calendar days in; it is never inferred from the dates, and API v1 accepts `Asia/Tokyo`
only today.

Warm the npm cache once, in the unit's account, with a scan that goes nowhere:

```bash
npx --yes tokscale@4.23.0 graph --today > /dev/null
```

## 5. Preview first, then send

`--preview` is the default mode and `--send` is explicit. Preview opens no socket, touches
no state and prints the exact bytes a send would transmit. Run it before the first send of
real usage, and again after any credential rotation or upstream version change:

```bash
npx --yes tokscale@4.23.0 graph --since "$(date -d '-30 days' +%F)" --no-spinner \
  | QuotaSight.Collector --preview --generation 1 --bucket-time-zone Asia/Tokyo
```

Read the result before sending:

- Exit `0` — a candidate was printed. Check `generation`, `bucketTimeZone`,
  `coveredRange` (`from`/`to`) and that `days` is ascending and plausible.
- Exit `3` — a candidate was printed **but** upstream reported partial, unpriced or
  stale-priced usage. Do not treat it as complete; re-run later. `--upstream-partial`
  cannot be combined with `--send`, so this signal is never hidden in a scheduled send.
- Exit `4` — the document violates the pinned contract or the API bounds (unknown
  `meta.version`, a window over 62 days, a body over 32 KiB, an input over 512 KiB).
  Nothing was written and nothing was sent; fix the input.
- Exit `2` — upstream had no dated rows. Nothing to send yet.

The first send also needs a revision floor, because a revision is allocated locally and
never reused. A state command reads `STATE_DIRECTORY`, which only the unit sets, so run it
inside the same environment — for a fresh collector, once:

```bash
systemd-run --user --wait --pipe --collect \
  -p StateDirectory=example-collector -p StateDirectoryMode=0700 \
  /usr/local/bin/QuotaSight.Collector --state-init --first-revision 1 \
    --collector-id collector-id --generation 1
# system unit: the same command with sudo and without --user
```

It prints `state_initialized: next revision 1` (exit `0`); `--state-show` in the same
environment prints the canonical state document. `StateDirectoryMode=0700` is not optional —
the Collector refuses a state directory with any group or other permission bits.

`--state-init` is only for this first run and for recovery; it can raise the generation or
raise the revision floor, never lower either. Do not run it again after sends have
happened — pick a floor above every revision this `(collector, generation)` pair has
already used, or the API answers `409` and the run blocks until `--state-advance`.

## 6. service and timer

### User units (default)

User units need no root and already run as the account whose logs are counted, so they are
the recommended form. `~/.config/systemd/user/example-collector.service`:

```ini
[Unit]
Description=QuotaSight collector run (example)
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
# A user unit already runs as its own account, so User= is neither needed nor allowed.
StateDirectory=example-collector
StateDirectoryMode=0700
# systemd cannot pipe; the wrapper supplies stdin and the exit status.
ExecStart=/usr/local/bin/example-collector-run
# Decrypted by this user's manager into $CREDENTIALS_DIRECTORY for this run only.
LoadCredentialEncrypted=access-client-id:%h/.config/example/credentials/access-client-id
LoadCredentialEncrypted=access-client-secret:%h/.config/example/credentials/access-client-secret

[Install]
WantedBy=default.target
```

`StateDirectory=example-collector` becomes `STATE_DIRECTORY` pointing at
`~/.local/state/example-collector`, created with mode `0700` for this user — the same
ownership and mode rules the Collector checks for `/var/lib/example-collector` in a system
unit. `DynamicUser=` is not supported: the directory must be owned by a fixed user.

`~/.config/systemd/user/example-collector.timer`:

```ini
[Unit]
Description=Run the QuotaSight collector every three hours

[Timer]
OnBootSec=15min
OnUnitActiveSec=3h

[Install]
WantedBy=timers.target
```

Enable both, and keep the user manager alive without an interactive login:

```bash
systemctl --user daemon-reload
systemctl --user enable --now example-collector.timer
loginctl enable-linger "$USER"
```

One run every 1-6 hours is the intended cadence; the upstream window from §3 must stay
comfortably longer than the interval, and inside the API's 62-day bound.

### System units (alternative)

The same files work system-wide. Copy them to `/etc/systemd/system/`, then:

- add `User=example-user` and `Group=example-user` to `[Service]` (required there);
- change `[Install]` to `WantedBy=multi-user.target`;
- `StateDirectory=example-collector` now resolves to `/var/lib/example-collector`;
- encrypt the credentials **without** `--user` (system-scoped), into a root-owned directory
  (e.g. `/etc/example/credentials/`, mode `0700`), and point `LoadCredentialEncrypted=` at
  those files with absolute paths instead of `%h/...`. The embedded credential name is still
  `access-client-id` / `access-client-secret`, so `--name=` is the same.

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now example-collector.timer
```

Either way the Collector sees `STATE_DIRECTORY` and `CREDENTIALS_DIRECTORY` and nothing
else: no path on the command line, no secret in the environment.

## 7. Verify that data arrives

The unit's own record is the journal, one run per line or a fixed reason class:

```bash
systemctl --user start example-collector.service   # one run now, instead of waiting for the timer
journalctl --user -u example-collector.service -n 30 --no-pager
# system unit: sudo journalctl -u example-collector.service -n 30 --no-pager
systemctl --user list-timers example-collector.timer
```

A clean run ends with `sent: accepted` and exit `0`. Anything else is a typed reason:
`send_refused`, `send_failed`, `state_unavailable`, `state_blocked`, `state_mismatch`,
`credential_unavailable`, `send_receipt_unverified` — each maps to an exit code and an
action in the Collector README ("409 reasons and what to do", "Exit codes"). A `409`, or a
`2xx` whose receipt could not be verified, blocks the next send until
`--state-advance`; nothing recovers by itself.

Two setup failures look alike in the journal and are not the same:

- `credential_unavailable` (exit `21`) — the run started but `$CREDENTIALS_DIRECTORY` had no
  `access-client-id` / `access-client-secret`. The unit's `LoadCredentialEncrypted=` lines
  are missing, point at the wrong file, or the embedded credential name does not match the
  unit's credential id.
- `status=243/CREDENTIALS` with `Failed to determine local credential key` — systemd could
  not unlock the ciphertext at all, so the run never started. Use the system unit (§6.2),
  where the same file is decrypted by the system manager.

Then check the app: while an installed collector has never sent, the usage freshness
line says **"A collector has not reported yet"** (「未受信の collector あり」). After the
first accepted snapshot that note disappears, and the machine's usage appears with its
observation time. If the note is still there after a `sent: accepted` run:

- confirm the collector id and generation are the same pair the app registered (a send for
  another generation is accepted for that generation, not for the active one);
- confirm the endpoint is the same HTTPS origin the app reads from;
- confirm the send went to the snapshot route (`PUT /v1/collectors/{id}/snapshots/{rev}`)
  and not to a preview host.

The app never fills the gap from local data: an unreceived collector stays visible until
its first accepted snapshot, by design.

## 8. Optional: the per-client breakdown

`--wire-schema 2` adds the client/provider/model breakdown that the app's breakdown view
reads. It is opt-in and only valid once the API has the v2 migration applied; a v2 body
sent to a database without it fails server-side and consumes the revision. Preview it
first (`--preview --wire-schema 2`) and compare `keys`/`parts` against what should have
been counted: identifiers are normalized, so the upstream spelling can differ, and a
path-shaped model id arrives as `""`.
