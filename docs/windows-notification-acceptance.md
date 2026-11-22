# Windows notification acceptance procedure

This is a manual acceptance check, separate from CI, for the Windows notification registration path and for visible delivery. GitHub-hosted Windows runners run with an elevated administrator token and UAC disabled, while Windows App SDK 2.x `IsSupported()` returns false for an elevated process. CI therefore stops after Native AOT publish and smoke testing.

**Scope of this build.** Every alert in the current UI is an in-app banner. No application flow calls the desktop notification backend, so this build has no path that sends an OS notification; threshold notifications and manual quota snapshots no longer exist. What remains executable is the `--notification-probe` registration diagnostic. Visible delivery is therefore recorded as **not applicable** until an OS notification trigger returns to the UI, and the earlier threshold-toast steps are removed rather than left as an impossible instruction.

## Windows App SDK packaging (unverified on real hardware)

The Windows build no longer references the `Microsoft.WindowsAppSDK` metapackage. QuotaSight only uses toast notifications (`Microsoft.Windows.AppNotifications`), which ships in `Microsoft.WindowsAppSDK.Foundation`, so `src/QuotaSight.UI/QuotaSight.UI.csproj` references `Microsoft.WindowsAppSDK.Foundation` 2.7.0 and its `Microsoft.WindowsAppSDK.InteractiveExperiences` 2.7.0 dependency directly. Foundation declares InteractiveExperiences as the range `[2.7.0, 3.0.0)`, so the explicit reference keeps the bundled framework on one reviewed version instead of floating to whichever 2.7.x a later restore finds. The metapackage also pulled in WinUI, onnxruntime and DirectML payloads that QuotaSight never loads, which is why the Windows archive carried roughly 300 files / 80 MiB of unused content. `WindowsAppSDKSelfContained` stays `true`; the framework is still bundled so the app never needs a separately installed Windows App SDK runtime, and no bootstrapper is used.

This composition has only been restored and inspected on a non-Windows machine: the Windows-only compile path (`#if WINDOWS`), Native AOT publish, and the resulting file count are verified only by the Windows CI runner, which cannot observe a visible toast. Toast display and toast click-through for the Foundation-only build are **not verified**, and this build has no UI action that would show one.

## Test environment and preparation

- Use a real Windows 11 x64 desktop in an interactive user session.
- Run QuotaSight as the signed-in standard/non-elevated user. Do not use “Run as administrator”; confirm the process is not elevated.
- Obtain a self-contained Windows x64 Native AOT ZIP built from the exact commit being evaluated. Extract it to a clean directory. The archive contains a single top-level `QuotaSight-<version>-win-x64/` folder; the executable and its assets are inside it. Do not use a framework-dependent build or a build from another commit.
- Record the commit SHA and the SHA-256 of the ZIP before testing. On Windows PowerShell, calculate the ZIP digest with `Get-FileHash -Algorithm SHA256 <path-to-zip>`.
- Record whether Windows notifications are allowed for QuotaSight and whether Focus assist/Do not disturb (集中モード) is enabled, so the environment is described even though no OS notification is expected. Do not record account, organization, device, or other identifying names.

## Registration probe

From the extracted ZIP directory, launch the actual packaged executable from a non-elevated terminal:

```powershell
$probe = Start-Process -FilePath .\QuotaSight.UI.exe -ArgumentList '--notification-probe' -Wait -PassThru
$probe.ExitCode
```

The released `QuotaSight.UI.exe` is a Windows GUI-subsystem binary, so it owns no console of its own and double-clicking it shows no terminal. The probe still reports to the console of the process that launched it, but only for `--smoke-test` and `--notification-probe`, and only when such a console exists. Start it from `cmd.exe` or PowerShell — not from Explorer — to see diagnostic text. A GUI-subsystem process also returns control to the shell immediately, so a direct `.\QuotaSight.UI.exe --notification-probe` call does not wait and does not set `$LASTEXITCODE`; `Start-Process -Wait -PassThru` is what waits for the process and gives you its real exit code.

Record the exact exit code and any fixed failure message printed by the probe. Exit code `0` is the programmatic evidence that the support check, event subscription, registration, unregistration, unregister-all, and event unsubscription all completed successfully. Successful runs normally print no message; do not expect or transcribe success-result strings. A nonzero exit code and its fixed failure message (if present) indicate a failed step. This probe is not a popup test: exit code `0` does not prove that Windows displayed a notification. The GUI-subsystem switch itself is only evidenced by CI reading the published PE header (`Subsystem == 2`, `WINDOWS_GUI`) and by the smoke test exit code; it has not been re-verified by double-clicking the executable on a physical desktop.

## Visible delivery (not applicable in this build)

There is nothing to observe: no UI action, refresh, or alert currently calls `IDesktopNotificationBackend.TryNotifyAsync`, and the threshold notification that earlier versions could trigger was removed with the quota views. Do not construct a scenario from removed features (threshold settings, manual percentage snapshots) and do not treat the in-app banner as an OS notification.

Keep the step in the record so the gap stays visible. When a trigger returns, this section must be replaced with a concrete, reproducible action before any result is claimed.

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
- Visible OS notification: not applicable in this build (no triggering flow) / displayed and visually confirmed / not displayed / not tested
- Visible-delivery acceptance: not applicable / pass / fail / not tested
- Notes (no credentials or identifying names):

A test record is evidence only for the exact commit, ZIP hash, OS, and settings recorded. Never report an untested or non-visible result as successful.
