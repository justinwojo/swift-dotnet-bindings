# Completed: Multi-Framework Automatic Dependency Detection

**Completed**: February 2026 (2026-02-15)
**Source**: `Future/dx-multi-framework-auto-detection.md`
**Context**: `Completed/developer-experience.md` contains the full DX design (Steps 1-5 all implemented).

---

## Implemented

### Binary Linkage Analysis (`BinaryDependencyAnalyzer.cs`)
- `otool -L` parsing of `LC_LOAD_DYLIB` / `LC_LOAD_WEAK_DYLIB` load commands
- Framework name extraction from `@rpath` entries (system dylibs filtered out)
- Sibling xcframework search for resolved dependencies
- Full analysis with resolution status

### Dependency Manifest (`DependencyManifestEmitter.cs`)
- `dependency-manifest.json` output with effective deps, unresolved, overridden, build order, graph warnings
- Feeds into layer 3 validation target generation, NuGet dependency declarations

### Topological Sort (`TopologicalSort.cs`)
- Kahn's algorithm with lexical tie-breaking for deterministic build ordering

### CLI Integration
- `--no-auto-detect` flag to disable auto-detection
- Manual `--framework-dependency` and `<SwiftFrameworkDependency>` remain available

### MSBuild SDK Integration
- `SwiftAutoDetectDependencies` property (defaults to `true`)
- Fingerprint integration for incremental builds

### Dependent Package Versioning
For multi-framework libraries, dependent packages use semver ranges matching the major version:
```
Nuke.Swift.iOS         version 12.8.0
NukeUI.Swift.iOS       version 12.8.0, depends on Nuke.Swift.iOS [12.0.0, 13.0.0)
```
