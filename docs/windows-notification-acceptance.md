# Windows notification acceptance procedure

This is a manual acceptance check for the Windows notification registration and visible delivery paths. It is separate from CI: GitHub-hosted Windows runners run with an elevated administrator token and UAC disabled, while Windows App SDK 2.5.1 `IsSupported()` returns false for an elevated process. CI therefore stops after Native AOT publish and smoke testing. Do not interpret a successful smoke test or notification API registration as proof that a desktop notification appeared.

## Test environment and preparation

- Use a real Windows 11 x64 desktop in an interactive user session.
- Run QuotaSight as the signed-in standard/non-elevated user. Do not use “Run as administrator”; confirm the process is not elevated.
- Obtain a self-contained Windows x64 Native AOT ZIP built from the exact commit being evaluated. Extract it to a clean directory. Do not use a framework-dependent build or a build from another commit.
- Record the commit SHA and the SHA-256 of the ZIP before testing. On Windows PowerShell, calculate the ZIP digest with `Get-FileHash -Algorithm SHA256 <path-to-zip>`.
- Keep notification settings and OS state visible/recordable. Record whether Windows notifications are allowed for QuotaSight and whether Focus assist/Do not disturb (集中モード) is enabled. Do not record account, organization, device, or other identifying names.

## Registration probe

From the extracted ZIP directory, launch the actual packaged executable from a non-elevated terminal:

```powershell
.\QuotaSight.UI.exe --notification-probe
$LASTEXITCODE
```

Record the exact exit code and any fixed failure message printed by the probe. Exit code `0` is the programmatic evidence that the support check, event subscription, registration, unregistration, unregister-all, and event unsubscription all completed successfully. Successful runs normally print no message; do not expect or transcribe success-result strings. A nonzero exit code and its fixed failure message (if present) indicate a failed step. This probe is not a popup test: exit code `0` does not prove that Windows displayed a notification.

## Visible threshold notification

1. In Windows Settings, verify notification permission for QuotaSight is enabled. Record the observed state; do not change it without noting that change.
2. Record the Focus assist/Do not disturb state. For an observable test, turn it off if policy permits and record the before/after state.
3. Start the extracted `QuotaSight.UI.exe` normally (not elevated). In the UI, enable threshold notifications and set a test threshold first. Then add a fresh, new, unique manual percentage snapshot whose percentage reaches or exceeds that threshold for the first time. Observe the OS popup immediately after saving the snapshot; do not just wait for a notification. Manual snapshots are supported for threshold presentation (see `tests/QuotaSight.UI.Tests/ThresholdNotificationPresentationTests.cs`, `Manual_snapshot_threshold_is_presented`). The manual form requires an invented test account label, but no real-account credentials or production quota observation.
4. Do not treat the in-app banner as an OS notification.
5. A person must visually inspect the Windows desktop and record whether the OS notification appeared. Record the result as `displayed and visually confirmed`, `not displayed`, or `not tested`; include a brief observation, without screenshots containing identifying information. If not displayed or not tested, explicitly state that visible delivery has not been verified.

## Results record

Copy this template into the test record and fill it with observed values. Do not include credentials, tokens, Client IDs, organization/device identifiers, or other identifying information.

- Commit SHA:
- Windows x64 ZIP SHA-256:
- OS edition/version/build (non-identifying):
- Process elevation: non-elevated / elevated / unknown
- Windows notification permission for QuotaSight: allowed / blocked / unknown
- Focus assist / Do not disturb: on / off / unknown (before and after, if changed)
- Probe exit code (exact):
- Probe fixed failure message (exact, if present):
- Probe programmatic registration lifecycle result (exit code 0 / failed / not demonstrated):
- Manual snapshot freshness and threshold setup (brief factual note; no identifiers):
- Human pixel-level OS notification observation: displayed and visually confirmed / not displayed / not tested
- Visible-delivery acceptance: pass / fail / not tested
- Notes (no credentials or identifying names):

A test record is evidence only for the exact commit, ZIP hash, OS, and settings recorded. Never report an untested or non-visible result as successful.