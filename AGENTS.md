# AGENTS.md

## Scope
Instructions for Codex in this repository.

## Hard constraints
- Detect runtime first (`uname -s`).
- If runtime is not macOS (`Darwin`), do not run mac-only tooling or scripts (for example: `xcodebuild`, iOS simulator validation scripts, or any script that requires Xcode/simulator runtimes).
- If runtime is macOS and required tools are installed, mac-only validation may be run directly.
- If macOS validation is required but unavailable in the current runtime, prepare changes and ask the user (or Claude on a Mac) to run the commands and share results.

## Project guidance
- Follow project conventions and workflows documented in `CLAUDE.md`.
- If guidance conflicts, this file takes precedence for Codex execution constraints.
