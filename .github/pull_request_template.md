## What and why

<!-- What does this change, and why? Link any issue it fixes. -->

## Version

<!--
  If this PR changes anything that ships (src/, Directory.*.props, global.json, build.ps1, LICENSE or notices),
  raise <Version> in Directory.Build.props. The Build workflow warns if you forget.

  MAJOR - breaking: a config setting renamed/removed or its meaning changed; install location, service name or
          data folder changed; supported Windows versions dropped; anything an admin must act on after upgrading.
  MINOR - new backward-compatible capability: a new setting or TargetDisplay mode, a new platform (e.g. ARM64),
          noticeably new behaviour that is on by default.
  PATCH - fixes and internal changes only: bug fixes, security hardening with no config change,
          dependency bumps, performance, logging.

  Docs-, test- and CI-only changes need no bump.
-->

- [ ] `<Version>` raised in `Directory.Build.props` — **major / minor / patch** (delete as appropriate) — or no shipped files changed
- [ ] `docs/SPEC.md` updated if behaviour or configuration changed
- [ ] New source files start with the GPL SPDX header
- [ ] `.\build.ps1` passes locally
