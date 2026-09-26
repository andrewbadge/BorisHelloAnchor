# Boris.HelloAnchor — Build Specification

You are building **Boris.HelloAnchor**, a Windows utility that forces the Windows Hello / "Windows Security" credential prompt to appear on the laptop's internal display (where the face-recognition camera is), instead of whichever monitor Windows picks.

Work in the phases listed at the end. Build and verify each phase before moving on. Before adding any NuGet package or WiX extension, check the current stable version rather than relying on remembered version numbers.

---

## 1. Background: what has already been proven

These facts were established by manual testing with a PowerShell script (`tools/Move-HelloPrompt.ps1` — keep it in the repo for reference):

- The prompt is a top-level window owned by **`CredentialUIBroker.exe`**, window class **`Credential Dialog Xaml Host`**, empty title.
- Calling `SetWindowPos` on it from a **non-elevated** process fails with **Win32 error 5** (UIPI blocks it).
- The same call from an **elevated** (high-integrity) process **succeeds**, and the window stays where it's moved.
- A Windows service cannot do this directly: services run in Session 0 and cannot see windows on the user's desktop.

Therefore the design is: a **LocalSystem service** that launches an **elevated agent process into each interactive user session**. The agent does the window moving.

---

## 2. Architecture

```
┌─────────────────────────── Session 0 ───────────────────────────┐
│ Boris.HelloAnchor.Service  (LocalSystem, auto-start)            │
│  - Tracks interactive sessions (logon/logoff/connect events)    │
│  - Launches one Agent per session using the user's ELEVATED     │
│    linked token (WTSQueryUserToken → TokenLinkedToken)          │
│  - Agents live in a kill-on-close Job Object                    │
│  - Restarts crashed agents with backoff; stops them on shutdown │
└──────────────────────────────┬──────────────────────────────────┘
                               │ CreateProcessAsUser (winsta0\default)
┌──────────────────────────────▼──── User session N ──────────────┐
│ Boris.HelloAnchor.Agent  (high integrity, no UI, WinExe)        │
│  - SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_UNCLOAKED)   │
│  - Filters for CredentialUIBroker / Credential Dialog Xaml Host │
│  - Resolves the target display, centres the prompt on it        │
│  - Verifies the move stuck; retries per config                  │
└─────────────────────────────────────────────────────────────────┘

  Boris.HelloAnchor.Settings (optional, started by an administrator,
  requireAdministrator) edits the display settings in config.json (§7.7).
```

---

## 3. Solution layout

```
Boris.HelloAnchor.sln
├─ src/
│  ├─ Boris.HelloAnchor.Core/        class library (net10.0-windows)
│  ├─ Boris.HelloAnchor.Service/     worker service (net10.0-windows)
│  ├─ Boris.HelloAnchor.Agent/       WinExe (net10.0-windows)
│  ├─ Boris.HelloAnchor.Displays/    class library: display enumeration (Agent + Settings)
│  ├─ Boris.HelloAnchor.Settings/    WinForms WinExe, requireAdministrator (net10.0-windows)
│  └─ Boris.HelloAnchor.Installer/   WiX SDK-style project (.wixproj)
├─ tests/
│  └─ Boris.HelloAnchor.Tests/       xUnit test project
├─ tools/
│  └─ Move-HelloPrompt.ps1
├─ Directory.Build.props
├─ Directory.Packages.props
└─ build.ps1
```

`Directory.Build.props` (shared settings):

- `TargetFramework` = `net10.0-windows`
- `Nullable` = `enable`, `ImplicitUsings` = `enable`, `TreatWarningsAsErrors` = `true`
- `RuntimeIdentifier` = `win-x64` (structure the build so `win-arm64` can be added later; many ARM laptops have Hello cameras)
- `Company` = `Boris`, `Product` = `Boris HelloAnchor`, a single shared `Version`. It must be MSI-compatible: `major.minor.build` with values ≤ 255.255.65535 (MSI ignores a fourth field).

`Directory.Packages.props` enables **Central Package Management** so every project uses identical package versions. This is required because Service and Agent are published into the same folder (§8.1); a version mismatch would silently overwrite a DLL.

Use **Microsoft.Windows.CsWin32** for all P/Invoke (source-generated, SafeHandle-based). Each project that needs Win32 APIs gets its own `NativeMethods.txt`. Do not hand-write `DllImport` declarations unless CsWin32 cannot generate something.

---

## 4. Configuration

Configuration lives in a single JSON file shared by the service and agent:

**Path:** `%ProgramData%\Boris\HelloAnchor\config.json`

**Exact shape:**

```json
{
  "HelloAnchor": {
    "TargetDisplay": "Internal",
    "TargetDeviceName": null,
    "TargetMonitorId": null,
    "TargetProcessNames": [ "CredentialUIBroker" ],
    "TargetWindowClasses": [ "Credential Dialog Xaml Host" ],
    "VerifyDelaysMs": [ 150, 300, 600, 1000, 2000 ],
    "SkipRemoteSessions": true,
    "AllowSystemTokenFallback": false,
    "AgentRestartBackoffSeconds": [ 2, 5, 15, 60 ],
    "LogLevel": "Information"
  }
}
```

| Field | Type | Meaning |
|---|---|---|
| `TargetDisplay` | `"Internal"` \| `"Primary"` \| `"DeviceName"` \| `"Monitor"` | Which display to move the prompt to. `Internal` = the built-in laptop panel. Any other mode falls back to `Internal` when its display isn't attached (§7.4). |
| `TargetDeviceName` | string or null | GDI device name (e.g. `\\.\DISPLAY1`). Used only when `TargetDisplay` is `DeviceName`. Not stable across docking; kept for compatibility. |
| `TargetMonitorId` | string or null | Stable monitor ID (§7.4), e.g. `DEL41B8-5KC0Q83` or model-only `DEL41B8`. Used only when `TargetDisplay` is `Monitor`. |
| `TargetProcessNames` | string[] | Process names (no `.exe`) whose windows are candidates. |
| `TargetWindowClasses` | string[] | Window class names that must also match. A window must match **both** lists. |
| `VerifyDelaysMs` | int[] | After a move, re-check position at each delay; re-apply the move if it snapped back or was resized. |
| `SkipRemoteSessions` | bool | Don't launch agents into RDP sessions (no internal display there). |
| `AllowSystemTokenFallback` | bool | If the user has no elevated token (standard user), run the agent as SYSTEM in their session. **Off by default** — see §6.4. Only honoured when the config passes the ACL check in §6.4. |
| `AgentRestartBackoffSeconds` | int[] | Delay before each successive restart of a crashed agent; last value repeats. Reset after the agent has run for 5 minutes. |
| `LogLevel` | string | `Microsoft.Extensions.Logging` level name. |

Put the options class and a loader in **Core**. Missing file or invalid values → use the defaults above and log a warning; never crash.

- Deserialize with **`System.Text.Json`** directly, not `Microsoft.Extensions.Configuration` binding (the binder appends bound arrays to default arrays instead of replacing them).
- Validation is **per field**: an invalid value resets only that field to its default. Invalid means: unknown enum or log-level name, empty process/class lists, empty/negative or > 10 000 ms delays, backoff values < 1 or > 3600 s, `DeviceName` mode with a null/empty `TargetDeviceName`, a `TargetMonitorId` that isn't `MMMPPPP` or `MMMPPPP-SERIAL`, and `Monitor` mode without a valid `TargetMonitorId` (both modes then fall back to `Internal`).
- The loaded options object is immutable. Reloads publish a new instance (volatile reference swap); consumers always read the current instance.
- Both the **agent and the service** re-read the file when it changes (a `FileSystemWatcher` on the folder, filtered to `config.json`, with a 500 ms debounce), so edits apply without restarting. The log level is applied through a Serilog `LoggingLevelSwitch`.

---

## 5. Boris.HelloAnchor.Core

Contains:

- `HelloAnchorOptions` (the config above), `ConfigLoader`, and `ConfigWatcher`.
- Path constants: ProgramData folder, config path, log folder (`%ProgramData%\Boris\HelloAnchor\logs`).
- Shared names: shutdown event name `Global\Boris.HelloAnchor.Shutdown`, agent mutex name `Local\Boris.HelloAnchor.Agent`, agent exit codes (§6.3).
- Pure logic that is unit-tested: centring math, backoff sequence, exit-code → restart decision, output-technology classification.
- Logging setup helper using **Serilog** (`Serilog.Extensions.Hosting` or `Serilog.Extensions.Logging` + `Serilog.Sinks.File`), rolling daily, 14 files retained. Service logs to `service-.log`, agent logs to `agent-s{sessionId}-.log`.

---

## 6. Boris.HelloAnchor.Service

### 6.1 Hosting

- Worker Service using `Microsoft.Extensions.Hosting.WindowsServices`, `AddWindowsService(o => o.ServiceName = "Boris.HelloAnchor")`.
- Runs as **LocalSystem** (required for `WTSQueryUserToken`).
- Also write start/stop and errors to the Windows Event Log. Set `EventLogSettings.SourceName = "Boris.HelloAnchor"` (the installer registers the source; see §8.2).
- Only register the custom lifetime (§6.2) when `WindowsServiceHelpers.IsWindowsService()` is true, so the service can still be run from a console or debugger.

### 6.2 Session tracking

.NET's hosting layer doesn't expose session-change events by default. Implement them like this:

- Subclass `Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceLifetime`, set `CanHandleSessionChangeEvent = true` in the constructor, and override `OnSessionChange(SessionChangeDescription)`.
- Register it with `services.AddSingleton<IHostLifetime, SessionAwareServiceLifetime>()` **after** `AddWindowsService`, and only when running under the SCM (§6.1).
- Forward events to a singleton `SessionManager` via a `Channel<SessionEvent>` so all launching and cleanup happens on one background loop (no work on the SCM callback thread).
- Act on `SessionLogon`, `ConsoleConnect`, and `RemoteConnect` (ensure an agent is running, subject to the skip rules below) and `SessionLogoff` (forget the session; the agent dies with it).
- **Reconciliation loop:** every 30 s, enumerate sessions with `WTSEnumerateSessions` and make reality match. Launch agents for **`WTSActive`** user sessions that lack one. Keep existing agents running in **`WTSDisconnected`** sessions (fast user switching). Drop entries for sessions that no longer exist. This also covers sessions already logged on when the service starts.
- Skip session 0, sessions where `WTSQueryUserToken` fails (no user), and RDP sessions if `SkipRemoteSessions` (check with `WTSQuerySessionInformation(WTSClientProtocolType)`; 0 = console). The skip rule applies to `RemoteConnect` events too. If a session with a running agent is later reconnected over RDP, the agent is left running (harmless; it will find no internal display).
- Per-session warnings (no elevated token, token query failure) are logged **once per session**, not on every reconciliation pass.

### 6.3 Launching the agent (elevated linked token)

For a session ID:

1. `WTSQueryUserToken(sessionId, out userToken)`.
2. `GetTokenInformation(userToken, TokenElevationType)`:
   - `TokenElevationTypeLimited` → `GetTokenInformation(TokenLinkedToken)` to get the full token. Because the service holds SeTcbPrivilege this is usable as a primary token, but still `DuplicateTokenEx(..., TokenPrimary)` it to be safe.
   - `TokenElevationTypeFull` → use `userToken` as-is.
   - `TokenElevationTypeDefault` → check `TokenIntegrityLevel`. If it is **High or above** (UAC disabled, or the built-in Administrator account), use `userToken` as-is. Otherwise it is a standard user with no elevated token; see §6.4.
3. `CreateEnvironmentBlock(out env, launchToken, false)`.
4. `CreateProcessAsUser(launchToken, agentPath, "\"agentPath\" --session {id}", ..., CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED, env, workingDir, STARTUPINFO { lpDesktop = "winsta0\\default" }, ...)`.
5. `AssignProcessToJobObject(job, process)`, then `ResumeThread`. The service creates one Job Object at start-up with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, so if the service crashes or is killed, every agent dies with it and a restarted service never finds orphans.
6. `DestroyEnvironmentBlock`, close thread handle, keep the process handle.

`agentPath` **must** be resolved relative to the service's own directory (`AppContext.BaseDirectory`), never from config or PATH. The service launches this binary with elevated rights, so it must only ever come from the Program Files install folder.

Track `Dictionary<int sessionId, AgentHandle>` (process handle + start time + restart count). Wait on process handles (e.g. `WaitForSingleObject` via `RegisteredWaitHandle` / `ThreadPool.RegisterWaitForSingleObject`).

**Agent exit codes:**

| Code | Meaning | Service action |
|---|---|---|
| 0 | Shutdown requested (event signalled or `WM_QUIT`) | Forget the entry; do not restart. |
| 2 | Another agent already holds the session mutex | Forget the entry; do not restart. Log a warning. |
| anything else | Crash / unexpected | Relaunch after backoff. |

On an unexpected exit, relaunch after the configured backoff, but **re-check the session when the backoff fires**, not when the process exits: at logoff the agent is often killed before `SessionLogoff` arrives. Relaunch only if the session still exists, is `WTSActive` or `WTSDisconnected`, and still yields a user token.

> **Risk to verify:** Windows 11 *Administrator Protection* elevates through a separate system-managed admin account. With it enabled, `TokenLinkedToken` may fail or return a token that is not usable for this purpose. If so, the service logs the failure once per session and the user is treated like a standard user (§6.4).

### 6.4 Standard users (no linked token)

- If `AllowSystemTokenFallback` is `false` (default): log a warning (once per session) that the agent can't be elevated for this user and don't launch. The move would fail with error 5 anyway.
- If `true`: duplicate the service's own token (`OpenProcessToken` → `DuplicateTokenEx` primary), `SetTokenInformation(TokenSessionId, sessionId)`, and launch with that. Log clearly that the agent is running as SYSTEM on a user desktop. Document the security trade-off in the README.
- **ACL check before honouring the fallback:** the service only honours `AllowSystemTokenFallback = true` if both the `%ProgramData%\Boris\HelloAnchor` folder and `config.json` are **owned by SYSTEM or Administrators** and **no ACE grants write-type rights** (write data, append, write attributes/EA, delete, write DAC, write owner, generic write/all) to any SID other than SYSTEM, Administrators, or TrustedInstaller. If the check fails, log a warning and behave as if the flag were `false`. This stops a standard user who pre-created or took ownership of the folder from enabling a SYSTEM process on their own desktop.

### 6.5 Shutdown

On service stop:

1. Set the named event `Global\Boris.HelloAnchor.Shutdown`. Create it at service start as a **manual-reset** event (so every agent sees it), with a security descriptor granting SYSTEM and Administrators full control and **Authenticated Users `SYNCHRONIZE` only**, so standard processes can wait on it but not signal it. Call **`ResetEvent`** immediately after creating it: if a handle to a previous instance still exists, the create call opens that object (still signalled) and would make new agents exit at once. `ERROR_ALREADY_EXISTS` is logged as a warning, not an error. `EventWaitHandleAcl.Create` (System.Threading.AccessControl) may be used instead of P/Invoke.
2. Wait up to 3 s for agents to exit.
3. `TerminateProcess` any that remain, then close the Job Object.

### 6.6 Service NativeMethods.txt (starting point)

`WTSQueryUserToken`, `WTSEnumerateSessions`, `WTSFreeMemory`, `WTSQuerySessionInformation`, `GetTokenInformation`, `DuplicateTokenEx`, `SetTokenInformation`, `OpenProcessToken`, `GetCurrentProcess`, `CreateProcessAsUser`, `CreateEnvironmentBlock`, `DestroyEnvironmentBlock`, `TerminateProcess`, `ResumeThread`, `GetExitCodeProcess`, `CreateJobObject`, `SetInformationJobObject`, `AssignProcessToJobObject`, `GetSidSubAuthority`, `GetSidSubAuthorityCount`, `TOKEN_ELEVATION_TYPE`, `TOKEN_LINKED_TOKEN`, `TOKEN_MANDATORY_LABEL`, `JOBOBJECT_EXTENDED_LIMIT_INFORMATION`.

---

## 7. Boris.HelloAnchor.Agent

### 7.1 Process basics

- `OutputType` = `WinExe` (no console window). No WinForms or WPF dependency.
- `app.manifest` declaring **Per-Monitor V2** DPI awareness (`<dpiAwareness>PerMonitorV2</dpiAwareness>`). This is essential; coordinates must be physical pixels across mixed-scaling monitors. Do **not** request `requireAdministrator` in the manifest. Elevation comes from the token the service provides.
- Single instance per session: acquire mutex `Local\Boris.HelloAnchor.Agent`; if already held, exit with **code 2** (§6.3).
- Determine the session ID with `ProcessIdToSessionId` on the current process. `--session {id}` is accepted for diagnostics but not trusted; a mismatch is logged.
- Exit code 0 on requested shutdown; any unhandled exception is logged and exits non-zero.

### 7.2 Main loop

1. Open `Global\Boris.HelloAnchor.Shutdown` with `SYNCHRONIZE`. If it can't be opened, log it and continue (dev runs without the service).
2. Two hooks sharing one callback, each `SetWinEventHook(evt, evt, 0, callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS)`:
   - `EVENT_OBJECT_SHOW`: the prompt window is created and shown. It is usually still **cloaked** (invisible) and full-screen sized at this point.
   - `EVENT_OBJECT_UNCLOAKED`: the prompt becomes visible. **CredentialUIBroker sizes the dialog and re-centres it on the primary monitor at this moment** (observed ~0.7 s after SHOW, varying between runs), undoing any earlier move. Handling this event runs the same move-and-verify cycle again after that re-centre, so correctness doesn't depend on a verify delay happening to fall after it. (Found in testing: with SHOW alone, a prompt that finished loading after the last verify check was left on the external monitor.)
   - Separate hooks rather than one range, because the SHOW..UNCLOAKED range includes `EVENT_OBJECT_LOCATIONCHANGE`, which fires constantly for every window.
   - A repeat arrival for an already-tracked window restarts its cycle, carrying over the move count and whether the agent has moved it.
   - **Keep the callback delegate in a static field** so it's never garbage-collected.
3. Pump messages with `MsgWaitForMultipleObjectsEx` on the shutdown event (`QS_ALLINPUT`, `MWMO_INPUTAVAILABLE`), then `PeekMessage`/`TranslateMessage`/`DispatchMessage`. Exit when the event is signalled or `WM_QUIT` arrives.
4. On exit: `UnhookWinEvent`, flush logs.

Use a thread-affine timer mechanism for the verify delays so everything runs on the hook thread: a small priority queue checked each loop iteration, with the `MsgWaitForMultipleObjectsEx` timeout computed from the next due item. (Preferred over `SetTimer` callbacks, which dispatch through the message queue.)

Config reloads (§4) arrive on thread-pool threads; the hook thread only ever reads the current immutable options instance.

### 7.3 Window filter (in the hook callback)

Proceed only if all of these are true:

- `idObject == OBJID_WINDOW` and `idChild == CHILDID_SELF`
- `hwnd` is a top-level window (`GetAncestor(hwnd, GA_ROOT) == hwnd`)
- `GetClassName` matches one of `TargetWindowClasses`
- The owning process (`GetWindowThreadProcessId` → `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` → `QueryFullProcessImageName`) matches one of `TargetProcessNames`. Cache PID→name keyed on **PID + process creation time** (`GetProcessTimes`), because PIDs are reused.

Check cheap conditions first (object IDs, then top-level, then class name) because `EVENT_OBJECT_SHOW` and `EVENT_OBJECT_UNCLOAKED` fire for every window on the desktop. Keep the callback fast; no file I/O except logging.

### 7.4 Resolving the target display

Resolve on **every** matching event (it's cheap and handles docking, undocking, and lid changes without extra plumbing). The enumeration lives in **Boris.HelloAnchor.Displays** (`DisplayTopology`), shared with the Settings app; the choice is pure logic in Core (`TargetDisplaySelector`) so it is unit-tested.

**Enumerate** the attached monitors:

1. `GetDisplayConfigBufferSizes` + `QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)`. If it returns `ERROR_INSUFFICIENT_BUFFER` (the topology changed between the two calls), re-query sizes and retry, up to 3 times.
2. For each path, `DisplayConfigGetDeviceInfo`:
   - `DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME` on `sourceInfo` → `viewGdiDeviceName` (e.g. `\\.\DISPLAY1`);
   - `DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME` on `targetInfo` → `monitorFriendlyDeviceName` and `monitorDevicePath`.
3. The path is **internal** if `targetInfo.outputTechnology` is one of `DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL` (0x80000000), `DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED` (11), `DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED` (13), or `DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS` (6). Many laptops report their eDP panel as `DISPLAYPORT_EMBEDDED` rather than `INTERNAL`, and older laptops use `LVDS`; all four must be accepted.
4. **Monitor ID.** The device path `\\?\DISPLAY#DEL41B8#5&2a8c1b0&0&UID4352#{guid}` names the device instance `DISPLAY\DEL41B8\5&2a8c1b0&0&UID4352`. Read its EDID from `HKLM\SYSTEM\CurrentControlSet\Enum\<instance>\Device Parameters\EDID` and build the ID as `MMMPPPP` (PNP manufacturer + product code in hex, the same as the Windows hardware ID) plus `-SERIAL` when the monitor reports one: the serial-number descriptor (tag `0xFF`) if present, else the numeric serial (bytes 12–15, `X8`) if non-zero. Characters that aren't printable ASCII, spaces, quotes and backslashes are dropped from the serial. If the EDID can't be read, use the model from the device path alone. Unlike GDI names, this ID follows the physical monitor across ports, docks and reboots.
5. `EnumDisplayMonitors` + `GetMonitorInfo` (`MONITORINFOEXW`) give each GDI source's `rcMonitor`, **`rcWork`** (work area, excludes the taskbar) and `MONITORINFOF_PRIMARY`. Monitors the CCD query didn't describe are still listed, without an ID and not internal.

**Choose**, keeping `QueryDisplayConfig` path order:

- **`Internal`:** the first internal monitor. With several (dual-screen laptops), the first wins; pick another with `Monitor`.
- **`Primary`:** the monitor with `MONITORINFOF_PRIMARY`.
- **`Monitor`:** the first monitor whose ID equals `TargetMonitorId` (case-insensitive); failing that, the first of the same model where either side has no serial. A model-only ID therefore matches any monitor of that model, and a different serial never matches.
- **`DeviceName`:** the monitor whose GDI name equals `TargetDeviceName`.

**Fallback.** If `Primary`, `Monitor` or `DeviceName` finds nothing (e.g. undocked), use the `Internal` choice instead and log it at Debug. If there is still nothing (lid closed, internal panel disabled), log at Debug and do nothing.

### 7.5 Moving and verifying

1. If the window's centre is already inside the target `rcWork`, do not move it now, but still schedule the verify checks (the broker may reposition it after showing).
2. Compute the centred position from the window's current size and `SetWindowPos(hwnd, HWND_TOP /*ignored*/, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE)`. Never steal focus.
3. If it fails, log the Win32 error. Error 5 means the agent isn't elevated; log that explicitly as the likely cause.
4. Schedule checks at each `VerifyDelaysMs` value. At each check:
   - If the window no longer exists (`IsWindow`), stop (the user authenticated).
   - If the centre is off the target, move it again.
   - If the agent moved it and it's on the target but no longer centred within a **±2 px tolerance** (it was resized by `WM_DPICHANGED` after crossing to a monitor with different scaling), re-centre it. The tolerance prevents re-centring forever over DPI rounding. Windows that were already on the target at step 1 are only moved if they later leave it.
5. Log one Information line per prompt handled: display used, attempts, final outcome.

### 7.6 Agent NativeMethods.txt (starting point)

`SetWinEventHook`, `UnhookWinEvent`, `MsgWaitForMultipleObjectsEx`, `PeekMessage`, `TranslateMessage`, `DispatchMessage`, `GetClassName`, `GetWindowThreadProcessId`, `OpenProcess`, `QueryFullProcessImageName`, `GetProcessTimes`, `ProcessIdToSessionId`, `GetAncestor`, `IsWindow`, `GetWindowRect`, `SetWindowPos`, `EVENT_OBJECT_SHOW`, `EVENT_OBJECT_UNCLOAKED`, `WINEVENT_OUTOFCONTEXT`, `WINEVENT_SKIPOWNPROCESS`.

(Named event and mutex access may use the .NET `EventWaitHandle` / `Mutex` types.)

The display APIs (`EnumDisplayMonitors`, `GetMonitorInfo`, `MONITORINFOEXW`, `GetDisplayConfigBufferSizes`, `QueryDisplayConfig`, `DisplayConfigGetDeviceInfo`, `DISPLAYCONFIG_SOURCE_DEVICE_NAME`, `DISPLAYCONFIG_TARGET_DEVICE_NAME`) are in **Boris.HelloAnchor.Displays**' own `NativeMethods.txt`.

### 7.7 Boris.HelloAnchor.Settings

An optional WinForms app that lets an administrator choose the target display without editing JSON, because monitor IDs differ on every desk and can't be scripted in advance.

- `app.manifest` requests **`requireAdministrator`**: `config.json` is writable only by administrators (§8.2). Per-Monitor V2 DPI awareness via `ApplicationHighDpiMode`, so monitor coordinates match the agent's.
- Never started automatically. The service and agent don't depend on it and don't talk to it: it only edits `config.json`, and the agent's config watcher (§4) applies the change.
- Offers `Internal` (default), `Primary` and `Monitor` (a list of attached monitors from `DisplayTopology`, showing name, connection, resolution and monitor ID). `DeviceName` is shown only if the file already uses it. A configured monitor that isn't attached is still listed as "(not connected)" so saving doesn't lose it.
- **Identify monitors** shows a borderless, non-activating label with the name and ID in the middle of each monitor for 4 seconds.
- Saving (`ConfigWriter`) changes only `TargetDisplay` and `TargetMonitorId`, keeping every other setting and key order. Comments can't be preserved, so the app warns first if the file has any. The result is re-parsed with `ConfigLoader` before writing, and written in place so the file keeps its owner and ACL. If the data folder doesn't exist, it refuses rather than creating one with `%ProgramData%`'s permissive ACL.

---

## 8. Boris.HelloAnchor.Installer (WiX)

Use the **WiX Toolset v5** SDK-style project (`<Project Sdk="WixToolset.Sdk/5.x.y">`, latest 5.x). v5 is chosen deliberately: WiX v6 and later require accepting the Open Source Maintenance Fee EULA, which is unsuitable for a public open-source build. Add the **WixToolset.Util.wixext** extension at the same version (service failure actions, event source).

### 8.1 Publishing inputs

- Publish **Service**, **Agent** and **Settings** as **self-contained**, `win-x64`, **not** single-file and **not** trimmed, **into the same output folder** (`artifacts/publish/win-x64/`) so the .NET runtime files are shared rather than duplicated. Central Package Management (§3) keeps shared dependencies identical.
- Harvest that folder with the WiX `<Files Include="...\**" />` element instead of listing files by hand.

### 8.2 Package requirements

- Per-machine, x64, `Scope="perMachine"`.
- A stable **UpgradeCode** GUID, generated once and committed. `MajorUpgrade` with a clear downgrade error message.
- Install to `ProgramFiles64Folder\Boris\HelloAnchor\`.
- **`ServiceInstall`**: Name `Boris.HelloAnchor`, DisplayName `Boris HelloAnchor`, Description "Keeps the Windows Hello prompt on the laptop's internal display.", `Account="LocalSystem"`, `Start="auto"`, `Type="ownProcess"`, `ErrorControl="normal"`.
- **`ServiceControl`**: `Start="install"`, `Stop="both"`, `Remove="uninstall"`, `Wait="yes"`.
- **`util:ServiceConfig`**: restart on first and second failure, restart on subsequent failures, reset period 1 day, restart delay 10 s.
- **ProgramData folders** `%ProgramData%\Boris\` and `%ProgramData%\Boris\HelloAnchor\` (and `logs\`): create them with `PermissionEx Sddl="O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)"` (MsiLockPermissionsEx): owner Administrators, SYSTEM and Administrators full control, Users **read & execute only**, and **inheritance from `%ProgramData%` disabled** (the default `%ProgramData%` ACL lets Users create files and makes them the owner). The service launches elevated code that reads this config, so standard users must not be able to modify it.
- The installer cannot re-secure a `config.json` a user created before install (`NeverOverwrite` skips that component), so the **service re-applies the protection at every start** (`DataFolderSecurity.Enforce`): it takes ownership, resets the ACLs, and deletes junctions, hard links and files it cannot re-secure in the data and logs folders. The service also verifies ownership/ACL before honouring the SYSTEM fallback (§6.4).
- **Default `config.json`**: install as `NeverOverwrite="yes"` and `Permanent="yes"` so user edits survive upgrades and uninstall.
- **Public property `ALLOWSYSTEMTOKENFALLBACK`** (`0`/`1`, default `0`; anything else fails a launch condition) writes the existing `AllowSystemTokenFallback` setting into `config.json` via a deferred, non-impersonated `WixQuietExec` step running the bundled `Set-ConfigFallback.ps1` after `InstallFiles`. That script changes only that one value. The chosen value is remembered in `HKLM\SOFTWARE\Boris\HelloAnchor\AllowSystemTokenFallback` and reused on upgrade/repair unless a new value is passed on the command line. No service code is involved.
- Register the Event Log source `Boris.HelloAnchor` in the Application log with `util:EventSource`, `EventMessageFile="[WindowsFolder]Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll"`.
- Start menu shortcut **Boris HelloAnchor Settings** (all users, `ProgramMenuFolder`) to `Boris.HelloAnchor.Settings.exe`. It is its own component with an HKCU marker value as key path, which ICE43/ICE57 require for a non-advertised shortcut.
- Add/Remove Programs: product icon, publisher `Boris`, help link placeholder.

### 8.3 Signing (optional, supported)

Make `build.ps1` accept an optional certificate thumbprint or PFX. If one is provided, sign `Boris.HelloAnchor.Service.exe`, `Boris.HelloAnchor.Agent.exe`, `Boris.HelloAnchor.Settings.exe` and the MSI with `signtool` and a timestamp server. If not, skip signing without failing.

---

## 9. Build script

`build.ps1` at the repo root should:

1. `dotnet restore` / `dotnet build -c Release` the solution with warnings as errors.
2. `dotnet test` the test project (skippable with `-SkipTests`).
3. `dotnet publish` Service and Agent to the shared publish folder.
4. Optionally sign the executables.
5. Build the `.wixproj` → `artifacts/Boris.HelloAnchor-{version}-x64.msi`.
6. Optionally sign the MSI.
7. Print the MSI path.

---

## 10. Testing checklist

Automated (unit tests in a `tests/Boris.HelloAnchor.Tests` xUnit project):

- Config loader: defaults, partial files, invalid values, missing file.
- Centring math: window bigger than work area (clamp to the top-left of the work area), negative monitor coordinates (monitor to the left of or above the primary), ±2 px centred tolerance.
- Backoff sequence logic (including reset after 5 minutes of uptime).
- Exit-code → restart decision.
- Internal-display output-technology classification.
- Monitor IDs: EDID parsing, device-path parsing, ID validation and matching; target selection and the built-in fallback; the Settings app's config writer.

Manual (document the results in `docs/TESTING.md`):

1. Install the MSI. The service is running and one agent process is visible in Task Manager with **Elevated = Yes**.
2. External monitor set as main display. Trigger a passkey prompt (e.g. register at webauthn.io) with the camera covered. The prompt appears centred on the laptop screen within about half a second.
3. Laptop and external monitor at **different scaling**. The prompt is still centred correctly.
4. Lid closed / internal display disabled. No errors, a Debug log line, and the prompt stays where Windows put it.
5. Kill the agent in Task Manager. The service relaunches it after backoff.
6. Sign out and sign back in. A new agent starts for the new session; no orphaned agents remain.
7. Fast user switching with two users. Each session has its own agent.
8. Stop the service. Agents exit within about 3 s.
9. Edit `config.json` (e.g. `TargetDisplay: "Primary"`). The agent picks it up without a restart.
10. Upgrade to a newer version. `config.json` is preserved.
11. Uninstall. The service and binaries are gone; ProgramData config remains.
12. As a standard (non-admin) user with fallback off, the service logs the warning (once) and no agent is launched.
13. Kill the service process (`taskkill /f`). All agents exit with it; after the SCM restarts the service, exactly one agent per session is running.
14. As a standard user, pre-create `%ProgramData%\Boris\HelloAnchor\config.json` before installing. After install, the file is owned by Administrators/SYSTEM and not writable by the user.
15. If available, repeat test 1 with Windows *Administrator Protection* enabled and record the result.
16. Start **Boris HelloAnchor Settings** from the Start menu. A UAC prompt appears; the window lists every attached monitor with an ID, and **Identify monitors** labels each screen.
17. In Settings choose an external monitor and save. A passkey prompt is centred on that monitor. Move the monitor to another port or dock: it still goes there.
18. With a monitor chosen, undock. The prompt is centred on the laptop panel (Debug log line about the fallback).

---

## 11. Non-goals and constraints

- Do **not** attempt to handle UAC prompts on the secure desktop or the lock/sign-in screen. These aren't reachable and aren't in scope.
- No UI in the service or agent, no tray icon, and no IPC listener. Keep the elevated agent's attack surface minimal. The only UI is the separate, optional Settings app (§7.7), which an administrator starts by hand and which communicates only through `config.json`.
- No `uiAccess="true"`. Testing showed elevation alone is sufficient.
- No telemetry or network access of any kind.

---

## 12. Suggested build phases

1. **Scaffold:** solution, `Directory.Build.props`, `Directory.Packages.props`, projects, CsWin32 wired up, Core config and logging. Builds clean.
2. **Agent standalone:** hook, filter, display resolution, move and verify. Test by running the agent manually from an **elevated** terminal (no service yet). Check manual tests 2–4.
3. **Service:** session tracking, elevated launch, job object, restart backoff, shutdown event. Test with `sc create` / `sc start` from the publish folder.
4. **Installer:** WiX package, service registration, permissions, config handling. Check manual tests 1 and 5–15.
5. **Polish:** `build.ps1`, optional signing, README (install, configure, security notes on `AllowSystemTokenFallback`), unit tests.
