# Multi-Framework — Remaining Future Work

**Created**: February 2026
**Completed work**: See `Completed/multi-framework-auto-detection.md` and `Completed/developer-experience.md`

Core auto-detection is complete (binary linkage analysis, dependency manifest, topological sort, CLI/MSBuild integration). The items below are not yet implemented.

---

## Not Yet Implemented

### Type-Level Cross-Framework Analysis

ABI-based `using` directives from cross-framework type references. When NukeUI methods accept Nuke types, the generated C# should automatically include the correct `using` directives. Requires parsing type references across module boundaries in the ABI JSON.

### `pack-all.sh` Orchestration Script

Multi-package build orchestration for libraries that ship as multiple frameworks. Uses topological sort output to build and pack in dependency order.

---

## Platform Coverage

Currently iOS-only. Extending to other Apple platforms that .NET supports.

| Platform | TFM | Status |
|----------|-----|--------|
| **iOS** | `net10.0-ios` | Implemented |
| **Mac Catalyst** | `net10.0-maccatalyst` | Under investigation |
| **macOS** | `net10.0-macos` | Under investigation |
| **tvOS** | `net10.0-tvos` | Under investigation |

Multi-platform packages drop the `.iOS` suffix (e.g., `Nuke.Swift` instead of `Nuke.Swift.iOS`).

---

## Open Questions

1. **Package naming convention**: `{Library}.Swift.iOS` vs `{Library}.Swift` (multi-platform) vs `Swift.{Library}`?
2. **SwiftUI bridge packaging**: Bundle bridge xcframework in the main package, or separate `*.Bridge` package?
3. **Source-module / overlay packaging**: Require all inputs to be prebuilt xcframeworks, or add source-compilation path?
4. **Resource bundles and linker flags**: Some vendor SDKs require non-framework assets. A `<SwiftFrameworkAsset>` item type is needed.
