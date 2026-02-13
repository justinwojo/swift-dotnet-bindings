# Multi-Framework Automatic Dependency Detection

**Date**: February 2026
**Status**: Design only — not yet implemented
**Context**: `Completed/developer-experience.md` contains the full DX design (Steps 1-5 all implemented).
Manual `--framework-dependency` and `<SwiftFrameworkDependency>` are available today.

---

## Problem

When a library ships as multiple dependent frameworks (e.g., Nuke + NukeUI + NukeExtensions), the user currently must manually specify each dependency. Automatic detection would eliminate this manual step.

## Binary Linkage Analysis

The SDK can detect dependencies from the Mach-O binary (authoritative source):

- Inspects `LC_LOAD_DYLIB` / `LC_LOAD_WEAK_DYLIB` load commands
- Catches dependencies that don't surface in public API signatures
- Extraction: `otool -L <binary>` lists all linked dylibs

```bash
$ otool -L NukeUI.xcframework/ios-arm64/NukeUI.framework/NukeUI
  @rpath/Nuke.framework/Nuke (compatibility version 0.0.0)
  /usr/lib/libobjc.A.dylib (compatibility version 1.0.0)
  ...
```

Only `@rpath` entries indicate companion framework dependencies — system dylibs (`/usr/lib/*`) are filtered out.

## Type-Level Analysis

Cross-framework type references in the ABI (e.g., NukeUI methods that accept Nuke types) provide detail about *which* types are referenced. This feeds into correct `using` directives for cross-framework types.

## Dependency Manifest

Both methods feed into a `dependency-manifest.json` that drives:
- Layer 3 validation target generation
- NuGet dependency declarations
- Topological sort for multi-package build ordering (`pack-all.sh`)

## Dependent Package Versioning

For multi-framework libraries, dependent packages use semver ranges matching the major version:

```
Nuke.Swift.iOS         version 12.8.0
NukeUI.Swift.iOS       version 12.8.0, depends on Nuke.Swift.iOS [12.0.0, 13.0.0)
```

This allows independent patch releases while preventing ABI-breaking mismatches across major versions.

---

## Platform Coverage (Future)

The packaging design targets all Apple platforms that .NET supports:

| Platform | TFM | Status |
|----------|-----|--------|
| **iOS** | `net10.0-ios` | Primary target (implemented) |
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
