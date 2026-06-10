# AGENTS.md

## Scope
Instructions for Codex in this repository.

## Default agent mode
- Codex is primarily a focused plan and code reviewer in this repository.
- Do not execute a plan, run implementation steps, or make code changes unless the user explicitly asks for execution/implementation.
- If a request is ambiguous, stay in review mode and ask for confirmation before making edits or running execution workflows.

## Hard constraints
- Detect runtime first (`uname -s`).
- If runtime is not macOS (`Darwin`), do not run mac-only tooling or scripts (for example: `xcodebuild`, iOS simulator validation scripts, or any script that requires Xcode/simulator runtimes).
- If runtime is macOS and required tools are installed, mac-only validation may be run directly.
- If macOS validation is required but unavailable in the current runtime, prepare changes and ask the user (or Claude on a Mac) to run the commands and share results.

## Project guidance
- Follow project conventions and workflows documented in `CLAUDE.md`.
- If guidance conflicts, this file takes precedence for Codex execution constraints.

## Claude Memory.md references
- Mentions of `MEMORY.md` in plans/reviews refer to Claude's external system tracking document, not a repository file.
- Codex should not flag missing `MEMORY.md` in this workspace as an issue.

## Scoped Rules Loading (`.claude/rules`)
- Scoped rules live in `.claude/rules/*.md` and use frontmatter `paths` to define applicability.
- Always read `AGENTS.md` and `CLAUDE.md` first, then load only scoped rule files whose `paths` match the current task scope.
- Task scope is the union of:
  - Files explicitly mentioned by the user
  - Files being edited
  - Files being reviewed
  - File types implied by the request (for example, C# or Swift changes should include matching `**/*.cs` / `**/*.swift` scoped rules when present)
- If no concrete files are known yet, defer scoped rule loading until file scope is identified.
- If multiple scoped rules match, apply all of them.
- Precedence for Codex:
  1. `AGENTS.md`
  2. Matching `.claude/rules/*.md`
  3. `CLAUDE.md`

## Codex execution notes (macOS)
- Codex runs on macOS but still inside a sandbox by default. For simulator/process operations, rerun with escalated permissions if needed (for example: `xcrun simctl ...`, `dotnet build` for iOS app packaging, `ps`).
- The default command timeout can be too short for iOS builds; use a longer timeout before assuming a hang.
- If `dotnet build` appears stuck after restore in Codex, rerun with escalated permissions; sandboxed runs can stall without producing useful output.
- The test app that exercises a mixed ObjC+Swift xcframework currently avoids the `InstallNameTool` `libSwiftBindingsRuntime.dylib.tmp` failure by setting `IncludeSwiftBindingsRuntimeNative=false` on its Swift.Runtime project reference.
