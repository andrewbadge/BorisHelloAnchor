# Known issue: no agent for standard user accounts

## Who this affects

Only machines where Windows Hello users sign in with **standard (non-administrator) accounts**. If the
account is a local administrator, HelloAnchor uses that account's own elevated token and works with the
default settings; nothing in this document applies.

## Problem

On a machine where the signed-in user is a **standard (non-administrator) account**, HelloAnchor installs
and the service runs, but it does nothing for that user. The Windows Hello prompt keeps appearing on
whichever monitor Windows picks.

This was first seen on a company-managed (Microsoft Entra ID joined) laptop:

- The MSI installed successfully (exit code 0). Because the account is not a local administrator, the
  UAC prompt asked for administrator credentials.
- The `Boris HelloAnchor` service was running, set to start automatically, as LocalSystem.
- **No `Boris.HelloAnchor.Agent.exe` process was started** for the user's session.
- The service log showed a single warning:

  ```
  Session 1: agent cannot be elevated for this user. The user has no elevated token (integrity 0x2000).
  AllowSystemTokenFallback is off, so no agent is launched; the prompt cannot be moved for this user.
  ```

### Why

Moving the prompt needs an elevated (high-integrity) process in the user's session. For an administrator
the service uses the elevated half of the user's own token. A standard user doesn't have one, so with
the default settings there is nothing suitable to start the agent with, and the service deliberately
starts nothing.

Checks on the affected machine ruled out other causes:

- The account is not in the local Administrators group, and the session runs at medium integrity.
- Windows 11 *Administrator Protection* is not enabled (`TypeOfAdminApprovalMode = 1`, classic UAC).

This is the designed default behaviour (SPEC §6.4, manual test 12), not a crash or a bug.

## Solution

The existing **`AllowSystemTokenFallback`** setting is the solution for standard user accounts. Its
security trade-off is described in the README under
[Standard users](../README.md#standard-users-allowsystemtokenfallback).

An administrator can set it at install time with the MSI property `ALLOWSYSTEMTOKENFALLBACK=1`
(default `0`); the value is kept across upgrades. See the README for details.

## Alternatives

- Make the user a local administrator. HelloAnchor then works with the default settings.
- Accept that the prompt isn't moved for standard users.
