# Security policy

Boris HelloAnchor runs a LocalSystem service that starts an **elevated** process in each user's
session, so security reports are taken seriously.

## Reporting a vulnerability

Please **do not** open a public issue for a security problem. Instead, use GitHub's
[private vulnerability reporting](https://docs.github.com/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability)
on this repository ("Security" tab → "Report a vulnerability").

Include the version, Windows build, what an attacker needs (e.g. a standard local account), and the steps
to reproduce. You should get an acknowledgement within a week.

## Scope

In scope, for example:

- A standard (non-admin) user getting code to run elevated or as SYSTEM through the service or agent.
- A standard user changing `%ProgramData%\Boris\HelloAnchor\config.json` or redirecting files the
  service or agent write (links, junctions).
- Causing the service to launch anything other than the installed `Boris.HelloAnchor.Agent.exe`.
- A standard user signalling the shutdown event or otherwise stopping other users' agents.

Out of scope:

- Anything that already requires administrator rights.
- The documented trade-off of `AllowSystemTokenFallback` (see the README), when an administrator has
  deliberately turned it on.

## Supported versions

Only the latest release receives security fixes.
