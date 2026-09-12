# Contributing to SeerrFin

Thank you for your interest in contributing to SeerrFin! This document contains everything you need to know when contributing.

## Ground rules

1. You must **test your changes yourself** on a real Jellyfin + Seerr setup. Don’t open a PR for something you don't know actually works.
2. Describe **how you tested** with steps, sceenshots/recording, logs, or any other means.
3. **Do not blindly edits using AI**, as unreviewed code doesn't help anyone. Try to understand this codebase before asking AI to make changes, and make sure the tool you use understands this codebase as context. If AI is used in a PR, please detail to what extent it was used, what harness you used, and model(s).
4. Only **one thing per pull request**. Don't make huge refactorings or unrelated formatting changes.

## Issues

**Security:** don’t open a public issue. Use [GitHub Security Advisories](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability) or contact me privately.

For bugs, before making an issue, search for any existing issues which mirror your issue first, then include: SeerrFin version + Jellyfin version, browser/client, themes/plugins, steps to reproduce, expected vs actual, and a screenshot/console log.

For features: describe the feature you are requesting in detail, what problem it solves, how it should work, (and if necessary) why is should be in SeerrFin.

## Development

**Need:** [.NET 10 SDK](https://dotnet.microsoft.com/download), Jellyfin 12.0, Seerr, File Transformation.

```bash
git clone https://github.com/pacoonrox/SeerrFin.git
cd SeerrFin
dotnet build SeerrFin.sln -c Release
```

Build output: `src/Jellyfin.Plugin.SeerrFin/bin/Release/net10.0/`. Copy the plugin into your Jellyfin plugins folder, restart Jellyfin, hard-refresh the web client, then configure under **Dashboard → SeerrFin**.

| Path | What |
| --- | --- |
| `src/Jellyfin.Plugin.SeerrFin/` | C# plugin code |
| `Inject/` | Client JS/CSS injected into Jellyfin web |
| `Configuration/` | Config UI and settings |

There is no automated testing setup yet. Manual testing is required. After injecting changes: rebuild → reinstall → restart → hard refresh → test.

## Commits

```text
type: Short description of what changed
```

Types: `feat`, `fix`, `style`, `refactor`, `misc`, `ci`.

- Capitalize after the colon (`feat: Added …`, `fix: Fixed …`)
- Prefer `Added` / `Fixed` / `Updated` / `Made` / `Improved`
- Describe the user-visible change, not technical aspects (unless the change is a technical change)

Examples:

```text
feat: Added auto-refresh for Requests tab, with customizable options in advanced settings
fix: Fixed disabled tabs reenabling after server restart
```

## Versioning

Four-part Jellyfin version: `MAJOR.MINOR.PATCH.BUILD` (e.g. `1.6.5.1`).

| Bump | When |
| --- | --- |
| **MINOR** | Large new feature area (Requests, Letterboxd, search) |
| **PATCH** | Smaller features / meaningful fixes |
| **BUILD** | Tiny fixes, toggles, polish |

Don’t bump versions in a PR. I will push a commit which will trigger the release workflow and update the plugin.

## Pull requests

1. Branch from latest `main` (`fix/…` or `feat/…`).
2. Keep the diff focused; match surrounding code style.
3. Build succeeds; tested on a real instance; console checked.
4. PR description includes summary, how you tested, environment (Jellyfin / Seerr / browser / theme), and screenshots for UI.

Review may take a few days. Respond to feedback as silent PRs may be closed. Merging will only be done by the maintainer (me for now).

By contributing, your work is licensed under the [MIT License](LICENSE).
