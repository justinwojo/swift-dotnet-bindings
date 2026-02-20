---
paths:
  - "validate-libraries.sh"
  - "validation-libraries.json"
  - "scripts/**"
  - ".libraries/**"
---

# Library Validation

## Architecture

Library validation uses a manifest-driven system:
- `validation-libraries.json` — declares all 31 libraries with SPM repos, versions, and expected results
- `scripts/fetch-libraries.sh` — clones repos and builds xcframeworks into `.libraries/` (gitignored)
- `validate-libraries.sh` — generates + compiles C# bindings for each library, tracks baselines

## Validation Profiles

- **public** (27 targets): Auto-fetchable via SPM. Any contributor can run this.
- **full** (31 targets): Includes 4 proprietary/manual libraries. Maintainer-only.

## Scripts

| Script | Purpose |
|--------|---------|
| `scripts/fetch-libraries.sh` | Fetch/build xcframeworks from manifest |
| `scripts/fetch-libraries.sh --list` | Show library cache status |
| `scripts/fetch-libraries.sh --filter Nuke` | Fetch specific library |
| `scripts/fetch-libraries.sh --force` | Rebuild even if cached |
| `validate-libraries.sh` | Run compile gate on all available libraries |
| `validate-libraries.sh --fetch` | Fetch then validate |
| `validate-libraries.sh --filter Nuke --verbose` | Validate one library with error detail |
| `validate-libraries.sh --quick` | Reuse existing /tmp output |

## Adding a Library

1. Add entry to `validation-libraries.json` with repo URL, version, and mode (`source`/`binary`/`manual`)
2. Run `scripts/fetch-libraries.sh --filter NewLib` to build its xcframework
3. Run `validate-libraries.sh --filter NewLib` to verify
4. Update `.validation-baseline.json` by running a full validation pass

## Manifest Modes

- **source**: Clone repo, build with xcodebuild (device + simulator), create xcframework
- **binary**: Use `swift package resolve` to download pre-built xcframeworks
- **manual**: User provides xcframework in `.libraries/<name>/`
