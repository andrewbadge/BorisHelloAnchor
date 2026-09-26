# Manual test results

Record the outcome of the manual checklist in [SPEC §10](SPEC.md#10-testing-checklist) for each release.
Automated tests run with `dotnet test --project tests/Boris.HelloAnchor.Tests` (or `.\build.ps1`).

**Version:** _x.y.z_  **Date:** _yyyy-mm-dd_  **Tester:** _name_
**Hardware:** _laptop model, external monitor(s)_  **Windows:** _edition and build_

| # | Test | Expected | Result | Notes |
|---|---|---|---|---|
| 1 | Install the MSI | Service running; one agent per session, Elevated = Yes | | |
| 2 | External monitor as main display, trigger a passkey prompt (camera covered) | Prompt centred on the laptop panel within ~0.5 s | | |
| 3 | Laptop and external monitor at different scaling | Prompt still centred correctly | | |
| 4 | Lid closed / internal display disabled | No errors, Debug log line, prompt left where Windows put it | | |
| 5 | Kill the agent in Task Manager | Service relaunches it after backoff | | |
| 6 | Sign out and back in | New agent for the new session; no orphans | | |
| 7 | Fast user switching, two users | Each session has its own agent | | |
| 8 | Stop the service | Agents exit within ~3 s | | |
| 9 | Edit `config.json` (e.g. `TargetDisplay: "Primary"`) | Agent applies it without restart | | |
| 10 | Upgrade to a newer version | `config.json` preserved | | |
| 11 | Uninstall | Service and binaries removed; ProgramData config remains | | |
| 12 | Standard user, fallback off | Warning logged once; no agent launched | | |
| 13 | `taskkill /f` the service | All agents exit; after SCM restart, exactly one agent per session | | |
| 14 | Standard user pre-creates `config.json` before install | After install it is owned by Administrators and not user-writable | | |
| 15 | Windows Administrator Protection enabled (if available) | Record behaviour | | |
| 16 | Start Settings from the Start menu | UAC prompt; all monitors listed with IDs; Identify labels each screen | | |
| 17 | Settings: choose an external monitor, save, trigger a prompt; then re-plug it into another port or dock | Prompt centred on that monitor both times | | |
| 18 | With a monitor chosen, undock and trigger a prompt | Prompt centred on the laptop panel; Debug fallback line | | |

## Useful commands

```powershell
# Service state and recovery settings
sc.exe query Boris.HelloAnchor
sc.exe qfailure Boris.HelloAnchor

# Agent processes (add the "Elevated" column in Task Manager > Details to confirm elevation)
Get-Process Boris.HelloAnchor.Agent | Select-Object Id, SessionId, StartTime

# Logs
Get-ChildItem "$env:ProgramData\Boris\HelloAnchor\logs"
Get-WinEvent -LogName Application -MaxEvents 20 | Where-Object ProviderName -eq 'Boris.HelloAnchor'

# ProgramData ownership and ACL (test 14)
icacls "$env:ProgramData\Boris\HelloAnchor"
icacls "$env:ProgramData\Boris\HelloAnchor\config.json"
```
