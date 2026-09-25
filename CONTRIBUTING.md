# Contributing to Boris HelloAnchor

Thanks for helping. Issues and pull requests are welcome.

By taking part you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Reporting problems

- **Security vulnerabilities:** do **not** open a public issue. Follow [SECURITY.md](SECURITY.md).
- **Bugs:** open an issue with the *Bug report* template. Include the HelloAnchor version, Windows build,
  your display setup (docked, lid open or closed, which monitor is primary) and the relevant lines from
  `%ProgramData%\Boris\HelloAnchor\logs`.
- **Ideas:** open an issue with the *Feature request* template before writing a large change, so the
  approach can be agreed first.

## Making a change

1. Fork the repository and branch from `main`.
2. Build and test with `.\build.ps1` (see [Building from source](README.md#building-from-source)).
   Warnings are treated as errors.
3. Open a pull request into `main` and fill in the template.

Please:

- keep changes consistent with [`docs/SPEC.md`](docs/SPEC.md), or update the spec in the same PR;
- comment new code the way existing code is commented (XML docs on every type and member, and
  *why* comments for anything non-obvious);
- start every new source file with the GPL SPDX header (`.editorconfig` has the template);
- add or update unit tests in `tests/Boris.HelloAnchor.Tests` for logic changes;
- raise `<Version>` in `Directory.Build.props` if the PR changes anything that ships
  (see [Versioning](README.md#versioning)). Docs-, test- and CI-only changes need no bump.

Manual test steps for behaviour that can't be unit-tested are in [`docs/TESTING.md`](docs/TESTING.md).

## Dependencies

Dependabot opens weekly PRs for NuGet packages and GitHub Actions. NuGet versions are managed centrally
in `Directory.Packages.props`. WiX is pinned to v5 on purpose (v6+ adds a maintenance-fee EULA), so
Dependabot is told to leave `WixToolset.*` alone.

## Licence

By contributing you agree that your contribution is licensed under the project's licence
(GPL-3.0-or-later). See [LICENSE](LICENSE).
