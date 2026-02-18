# MSBuild SDK — Future Work

**Extracted from**: `Completed/dx-msbuild-sdk-design.md` (Steps 1-5 all complete)

---

## SPM Package Support (v2)

Many Swift libraries are distributed as SPM packages (source code + `Package.swift`), not prebuilt xcframeworks. SPM support is critical for long-term adoption but fits cleanly as an additive layer — no rework required on v1.

### Architecture: SPM as a Pre-Step

The v1 pipeline works entirely on xcframeworks:

```
xcframework → Validate → Fingerprint → Generate → CompileWrapper → CoreCompile → Pack
```

SPM support adds a resolution step at the front:

```
SwiftPackage → ResolveSwiftPackages → xcframework → [same pipeline unchanged]
```

The generator, wrapper compilation, NuGet packaging — none of that changes. `ResolveSwiftPackages` converts SPM input into a **dynamic** xcframework, then the v1 pipeline takes over.

**Critical constraint:** Many SPM libraries use `.automatic` product type, which often resolves to static linking. `ResolveSwiftPackages` MUST force dynamic library output (via `xcodebuild` build settings: `MACH_O_TYPE=mh_dylib`). v2 scope is **dynamic-capable SPM products only** — mirroring the v1 constraint on dynamic xcframeworks.

### SDK Item Type

```xml
<!-- v2: SPM package from URL -->
<SwiftPackage Include="https://github.com/kean/Nuke" Version="12.8.0" />

<!-- v2: local SPM package -->
<SwiftPackage Include="../my-swift-lib/" />
```

The `SwiftPackage` item type and `SWIFTBIND100` error stub already exist in `Sdk.props`/`Sdk.targets` (v1 groundwork). Using `<SwiftPackage>` today produces a clear error directing users to build an xcframework first.

### What `ResolveSwiftPackages` Would Do

1. **Resolve the package** — clone from URL or resolve local path, pin to resolved commit SHA
2. **Build for all platforms** — `xcodebuild` for device (arm64) and simulator (arm64 + x86_64), using `MACH_O_TYPE=mh_dylib`
3. **Create xcframework** — `xcodebuild -create-xcframework` from the platform builds
4. **Extract SPM metadata** — platform version, library version (from git tag), dependencies

### Reproducibility: Lock File

SPM inputs are mutable (version tags can move, branches advance). `ResolveSwiftPackages` must write a lock file (`swift-binding.lock.json`) recording the resolved commit SHA and content hash per package:

```json
{
  "packages": {
    "https://github.com/kean/Nuke": {
      "requestedVersion": "12.8.0",
      "resolvedCommit": "a1b2c3d4e5f6...",
      "resolvedVersion": "12.8.0",
      "contentHash": "sha256:..."
    }
  }
}
```

- On subsequent builds, if lock file exists and version unchanged → use pinned commit (no re-resolve)
- `dotnet build -p:SwiftPackageForceResolve=true` re-resolves and updates the lock file
- Lock file should be committed to source control
- Incremental build fingerprint includes lock file content hash

### Dependency Mapping

SPM's `Package.swift` declares dependencies at the package level, but target-level `dependencies` arrays determine actual linkage. The resolver must:

- Walk the target dependency graph (transitive closure), not just direct product deps
- Exclude test targets, build tool plugins, and platform-conditional deps that don't apply
- Cross-validate against `otool -L` on the built xcframework (warn on undeclared binary deps)

### Multi-Product Packages (Deferred)

One SPM package can produce multiple library products (e.g., `Nuke` + `NukeUI`). Each would become a separate `SwiftFramework` item. Deferred until single-product dependency mapping is proven.

---

## SwiftUI Bridge SDK Integration

The generator produces SwiftUI bridge files (`.cs` + `.swift`). Currently the bridge Swift code is compiled into a separate `{Library}Bridge.framework` by shell scripts (`build-bridge.sh`).

**In the SDK:** Bridge Swift compilation would be another build target, and the bridge framework would be included in the NuGet package alongside the main wrapper xcframework.

**Recommendation:** Defer until the core SDK workflow is proven. The bridge is well-tested via shell scripts.

---

## Error Experience Improvements

### Not Yet Implemented

- **Xcode version validation** — detect too-old Xcode before generator fails with cryptic `swift-frontend` errors
- **Binding report summary as MSBuild warnings** — surface skip counts and key coverage gaps as build warnings so users see them without opening `binding-report.json`

---

## Test Project Migration

The binding test projects (Nuke, Lottie, BlinkID, CryptoSwift) in `BindingTesting/` should eventually migrate to the SDK. They serve as integration tests for the SDK itself. Current shell-script workflow remains as fallback.

---

## Consumer Validation Test Matrix

Before shipping, these consumer scenarios need validation:

| Scenario | What to verify |
|----------|---------------|
| Single-package app | Install one binding, `dotnet build`, app runs on simulator |
| Multi-package app | Install 2+ bindings (e.g., Nuke + Lottie), no wrapper name collision, both work |
| Transitive dependency | App references project that references binding — `buildTransitive/` targets fire |
| Missing dependency | Remove required companion package → build error |
| iOS version mismatch | Consumer's `SupportedOSPlatformVersion` < framework minimum → build warning |
| Pack/restore round-trip | `dotnet pack` then `dotnet add package` from local feed → identical behavior to direct reference |
| Device + simulator parity | NuGet works for both device and simulator builds |
