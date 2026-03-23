# Error Code Audit

Full audit of all SWIFTBIND and SB diagnostic codes. Evaluates accuracy, external readability, and whether each warning exists because of a genuine constraint vs. an incomplete implementation.

**Date**: 2026-03-22

---

## Ship-Ready (No issues)

| Code | Type | Notes |
|------|------|-------|
| SWIFTBIND001 | Error | No xcframework found. Correct validation, clear fix. |
| SWIFTBIND003 | Error | xcframework path doesn't exist. Correct validation. |
| SWIFTBIND035 | Error | Cannot resolve platform version for NuGet pack. NuGet requires versioned TFMs. |
| SWIFTBIND040 | Warning | Missing SwiftFrameworkDependency metadata. Can't safely infer defaults. |
| SWIFTBIND050 | Warning | Wrapper compilation failed. Some failures are inherent (3rd party internal types). |
| SWIFTBIND051 | Error/Warning | Wrapper required but failed. Correct dual-severity design. |
| SWIFTBIND060 | Warning | Dependency detected but xcframework not found. Clear CLI + SDK solutions. |
| SWIFTBIND070 | Error | Module database not found. User error. |
| SWIFTBIND072 | Error | Invalid module database XML. Corrupt input. |
| SWIFTBIND073 | Warning | Module database path doesn't exist (SDK). Graceful degradation. |
| SWIFTBIND080 | Warning | Cross-module dependency, no sibling project. Excellent — provides copy-pasteable XML. |
| SB0002 | Obsolete | Missing symbol. Library build config issue, not ours. |
| SB0004 | Obsolete | Empty protocol interface. Clear explanation + report pointer. |
| SB1001 | Analyzer | Roslyn analyzer for undisposed ISwiftObject. Proper severity tiering (Warning for structs, Info for classes). |

---

## Needs Fix (Bugs or misleading docs)

### SWIFTBIND002 — Wiki fix is wrong

**Problem**: The wiki says: *"Declare explicit `<SwiftFramework>` items in your `.csproj`"*

The MSBuild condition is `@(SwiftFramework->Count()) > 1` — fires even with explicitly declared items, not just auto-discovered ones. Declaring explicit items doesn't help if you have multiple frameworks. Source code comment says `"v1 supports one per project"`.

**Fix (wiki)**: Change to: *"The SDK supports one xcframework per project. Create separate binding projects for each xcframework, or keep only the one you want to bind."*

**Fix (code, optional)**: Could also improve the error message text in Sdk.targets to match.

---

### SWIFTBIND010 — Two different diagnostics sharing one code

**Problem**: Emitted in two completely different contexts:
1. **Sdk.targets line 16**: Unsupported TFM (e.g., `net10.0` without a platform suffix)
2. **ConsumerTargetsEmitter.cs line 112**: Platform version mismatch (e.g., consumer targets iOS 15 but library requires iOS 26)

An external user seeing SWIFTBIND010 finds the "unsupported TFM" fix in the wiki, but their actual issue may be a minimum platform version mismatch.

**Fix (code)**: The consumer-facing version warning should use a distinct code (e.g., `SWIFTBIND011`). The wiki should document both.

---

### SWIFTBIND020 — False positive when user has already set PackageVersion

**Problem**: In Sdk.targets, `PackageVersion` is set from auto-detected metadata only if unset:
```xml
<PackageVersion Condition="'$(PackageVersion)' == ''">$(_SwiftBindingPackageVersion)</PackageVersion>
```
But the SWIFTBIND020 warning checks `_SwiftBindingIsVersionPlaceholder` (what the Info.plist contained) without checking whether the user already overrode `PackageVersion`. A user with `<PackageVersion>2.0.0</PackageVersion>` still sees the warning.

**Fix (code)**: Gate the warning on the effective value: only warn when the user hasn't overridden the auto-detected version. E.g., `Condition="'$(_SwiftBindingIsVersionPlaceholder)' == 'True' AND '$(PackageVersion)' == '$(_SwiftBindingPackageVersion)'"`.

---

### SWIFTBIND031 — Wiki fix is wrong

**Problem**: The wiki says: *"Rebuild with `SwiftWrapperArchitectures=all` to compile both slices"*

SWIFTBIND031 fires when the **source xcframework** doesn't contain both platform slices. Rebuilding the wrapper won't help if the input xcframework only has a simulator slice. The actual error message in Sdk.targets correctly says to verify the source xcframework.

**Fix (wiki)**: Change to: *"Verify your source xcframework contains both device and simulator platform slices. If it only has one, rebuild the xcframework with both architectures, or set `<IsPackable>false</IsPackable>` for local-only use."*

---

### SB0001–SB0004 UrlFormat points to internal docs

**Problem**: All SB diagnostics use:
```
UrlFormat = "https://github.com/justinwojo/swift-dotnet-bindings/blob/main/src/docs/known-issues-workarounds.md"
```
This points to `src/docs/Completed/known-issues-workarounds.md` — an internal design document. External users clicking this link see developer notes, not consumer documentation.

**Fix (code)**: Change UrlFormat to `https://github.com/justinwojo/swift-dotnet-bindings/wiki/Troubleshooting`

---

### Generator errors have no diagnostic codes

**Problem**: Three generator errors documented in the wiki have no machine-parseable SWIFTBIND code:
- "Static xcframework detected"
- "No Swift module found"
- "swift-frontend failed"

Every MSBuild SDK error has a code. These generator errors don't, making them harder to filter/suppress in tooling.

**Fix (code)**: Assign codes (e.g., SWIFTBIND101–103) and emit them with the messages.

---

## Needs Improvement (Readability for external audience)

### SWIFTBIND090–094 — Internal diagnostics exposed to users

**Problem**: These are self-diagnostic checks that detect bugs in our own output. Messages reference internal concepts like "CallConvSwift," "Tj dispatch thunk," "SBW_ entry point," and "@_cdecl wrapper" — none of which an external user understands. The wiki correctly says "generator bugs — file an issue" but the build output is confusing.

**Options**:
1. Make these verbose/debug-level (don't show in normal build output), or
2. Rewrite messages to be consumer-facing: *"Internal validation detected an issue with the generated binding for method 'X'. This method may not work correctly at runtime. Please file an issue with your xcframework."*

---

### SB0003 — Jargon-heavy for external audience

**Problem**: Wiki says: *"can't dispatch through the witness table. Throws `NotSupportedException` on Swift-backed existentials."* External users don't know what witness tables or existentials are.

**Fix (wiki)**: Rewrite to: *"This protocol member cannot be called on protocol-typed values (e.g., `any MyProtocol`). Calling it on a concrete type works correctly. This is a Swift ABI limitation."*

**Fix (code)**: The `[Obsolete]` message text in ProtocolProxyEmitter.InterfaceImpl.cs could also be simplified.

---

### Mono JIT section — Misleading framing

**Problem**: The wiki says: *"This is a Mono JIT defect..."* Internal findings (MONO-JIT-FINDINGS.md) proved every crash attributed to Mono was actually a generator/runtime bug. The `jit-info.c:918` assertion IS a genuine Mono limitation for specific CallConvSwift patterns, but the framing makes it sound like Mono is fundamentally broken.

**Fix (wiki)**: Reframe as: *"The .NET Mono runtime (used on iOS Simulator) does not support `CallConvSwift` in certain patterns. The generator automatically routes ~67% of methods through wrapper functions that avoid this limitation. Methods that cannot receive wrappers (method-level generics) are annotated with `SB0001`."*

---

## Honest Feature Gaps

### SWIFTBIND100 — SwiftPackage items not yet available

This is a "we haven't built this yet" error. The user tries SPM integration and gets told it's not implemented. The error is transparent and provides a workaround (build to xcframework first). Acceptable for v1.

---

### SWIFTBIND071 — Could silently skip instead of erroring

Fires when the user passes their own module's database as a dependency. The auto-detected dependency path (Program.cs line 140) silently skips self-references, but the explicit `--module-database` path makes it a hard error. Inconsistent behavior. Could be demoted to info-level log.

---

## Acceptable (Minor observations, no action needed)

| Code | Notes |
|------|-------|
| SWIFTBIND021 | Could note "only relevant for NuGet pack" but message is clear enough. |
| SWIFTBIND030 | Correct. Could auto-set architectures but that would silently change behavior. |
| SB0001 | Well-engineered with clean NativeAOT suppression. Wiki framing is the only issue (see above). |

---

## Design Notes

**Suppression architecture is well-designed**:
- Binding author (Sdk.props): SB0001–SB0004 all suppressed — they see clean builds
- Consumer on Mono: sees SB0001, SB0002, SB0003, SB0004
- Consumer on NativeAOT: SB0001 auto-suppressed via `SwiftBindingsInteropMode=Direct`

**ABI Contract Checker (SWIFTBIND090–094) is a safety net**, not a limitation. If the generator is correct, these never fire. They detect our own bugs. The question is presentation, not existence.
