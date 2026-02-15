# NativeAOT Deployment Guide

This guide covers deploying .NET applications that use Swift bindings with NativeAOT compilation. NativeAOT produces ahead-of-time compiled native code, eliminating the Mono JIT runtime — which resolves several Swift interop limitations.

## Why NativeAOT?

The Mono JIT runtime has three known issues with Swift interop:

1. **JIT assertion crash** — `CallConvSwift` P/Invoke frames trigger a fatal `jit-info.c:918` assertion in Mono's stack walker
2. **Non-blittable type rejection** — Complex types like `SwiftOptional<T>` are rejected by the JIT's `CallConvSwift` validation
3. **SafeHandle lifetime in async** — The GC can collect SafeHandles during async suspension points

NativeAOT bypasses all three issues. The same generated bindings work on both runtimes — Mono workarounds (wrapper paths, Cdecl expansion) are harmless overhead on NativeAOT.

## Quick Start

### Device deployment (NativeAOT)

Add these properties to your `.csproj`:

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <PublishAotUsingRuntimePack>true</PublishAotUsingRuntimePack>
  <TrimMode>partial</TrimMode>
  <NoWarn>$(NoWarn);IL2026;IL2087;IL2091;IL3050</NoWarn>
</PropertyGroup>
```

Publish for device:

```bash
dotnet publish -c Release
```

### Simulator deployment (Mono)

No special configuration needed. Simulator builds use Mono JIT automatically — NativeAOT does not support `iossimulator-arm64`.

```bash
dotnet build    # Uses Mono, works on simulator
```

## Required `.csproj` Properties

| Property | Value | Purpose |
|----------|-------|---------|
| `PublishAot` | `true` | Enables NativeAOT compilation |
| `PublishAotUsingRuntimePack` | `true` | Uses the NativeAOT runtime pack for iOS |
| `TrimMode` | `partial` | Preserves reflection paths used by `SwiftMarshal` |

### Trimming Warning Suppressions

The Swift runtime uses reflection for tuple marshalling and generic type metadata. These produce trimming analysis warnings that are safe to suppress:

| Warning | Source | Why It's Safe |
|---------|--------|---------------|
| `IL2026` | `SwiftMarshal.MarshalFromSwift<T>` → tuple path | Only affects tuple marshalling; non-tuple paths verified safe |
| `IL2087` | `SwiftMarshal.MarshalToSwift<T>` generic annotations | Same — tuple-specific reflection |
| `IL2091` | `SwiftMarshal.MarshalFromSwift<T>` generic annotations | Same |
| `IL3050` | `TypeMetadata.TryGetTupleTypeMetadata` | `MakeGenericMethod` for tuple metadata construction |

Add to your `.csproj`:

```xml
<NoWarn>$(NoWarn);IL2026;IL2087;IL2091;IL3050</NoWarn>
```

Non-tuple types (primitives, `SwiftString`, `SwiftArray`, `SwiftOptional`, classes, structs) do not use these reflection paths and are fully trimming-safe.

## SwiftBindingsInteropMode

Generated binding packages include a `.targets` file that automatically detects your deployment target and adjusts diagnostic behavior.

### How It Works

The `SwiftBindingsInteropMode` property controls whether Mono JIT safety warnings are shown:

| Mode | Behavior | When Used |
|------|----------|-----------|
| `Auto` (default) | Detects `PublishAot` — resolves to `Direct` or `Safe` | Always, unless overridden |
| `Direct` | Suppresses `SB0001` warnings — full API access | NativeAOT builds (`PublishAot=true`) |
| `Safe` | Shows `SB0001` warnings on risky methods | Mono builds (simulator, no `PublishAot`) |

In `Auto` mode (the default), the build system checks `$(PublishAot)`:
- `PublishAot=true` → `Direct` → SB0001 suppressed → clean API, no warnings
- Otherwise → `Safe` → SB0001 visible as warnings on methods with Mono JIT crash risk

### Overriding

To force a specific mode, set it explicitly in your `.csproj`:

```xml
<PropertyGroup>
  <!-- Force full API access regardless of runtime (at your own risk on Mono) -->
  <SwiftBindingsInteropMode>Direct</SwiftBindingsInteropMode>
</PropertyGroup>
```

## Diagnostic IDs

The generator uses custom diagnostic IDs instead of generic `CS0618`/`CS0619`:

| ID | Meaning | Suppressible? |
|----|---------|---------------|
| `SB0001` | **Mono JIT crash risk** — method uses `CallConvSwift` P/Invoke patterns that crash on Mono. Safe on NativeAOT. | Yes — auto-suppressed in `Direct` mode |
| `SB0002` | **Missing symbol** — P/Invoke entry point not exported by the library. Will throw `EntryPointNotFoundException` at runtime on any runtime. | No — always relevant |

### Why Custom Diagnostic IDs?

Custom IDs (`SB0001`, `SB0002`) are scoped to Swift binding packages. Suppressing `SB0001` does not affect `[Obsolete]` warnings from other packages — unlike suppressing `CS0618` globally, which would hide unrelated deprecation warnings.

## Dual-Runtime Compatibility

The same generated bindings work on both Mono and NativeAOT without code changes. The runtime detects which environment it's in:

```
┌─────────────────────────┐     ┌─────────────────────────┐
│   Simulator (Mono JIT)  │     │   Device (NativeAOT)    │
├─────────────────────────┤     ├─────────────────────────┤
│ 1. Try wrapper path     │     │ 1. Try wrapper path     │
│    (@_cdecl wrappers)   │     │    (@_cdecl wrappers)   │
│ 2. If missing → detect  │     │ 2. If missing → fall    │
│    Mono.Runtime → THROW │     │    back to direct       │
│    (direct would crash) │     │    CallConvSwift (works) │
└─────────────────────────┘     └─────────────────────────┘
```

Workarounds built for Mono (SwiftString wrappers, closure Cdecl expansion, risk detection attributes) are harmless overhead on NativeAOT. They add unnecessary indirection but don't break anything.

## Device Publish Workflow

### Prerequisites

- Xcode with a valid Apple Development certificate
- A provisioning profile covering your app's bundle identifier
- A physical iOS device connected or on the same network

### Code Signing Properties

```xml
<PropertyGroup>
  <CodesignKey>Apple Development: Your Name (XXXXXXXXXX)</CodesignKey>
  <CodesignProvision>Your Provisioning Profile Name</CodesignProvision>
  <TeamIdentifierPrefix>XXXXXXXXXX</TeamIdentifierPrefix>
</PropertyGroup>
```

### Build and Deploy

```bash
# Publish NativeAOT for device
dotnet publish -c Release

# Install on device (requires Xcode 16+)
xcrun devicectl device install app \
  --device "Your Device Name" \
  bin/Release/net10.0-ios/ios-arm64/publish/YourApp.app

# Launch with console output
xcrun devicectl device process launch --console \
  --device "Your Device Name" \
  com.your.bundleid
```

### App Bundle Size

NativeAOT produces compact binaries. A test app with Swift bindings + runtime:
- **IPA**: ~2 MB (code-signed, includes Swift frameworks)

## Limitations

### NativeAOT is Device-Only

NativeAOT for iOS targets `ios-arm64` only. The `iossimulator-arm64` runtime identifier is not supported for `dotnet publish` with `PublishAot=true`. Simulator builds always use Mono JIT.

This means:
- **Development/debugging** → Simulator (Mono) — fast iteration, no code signing
- **Release/production** → Device (NativeAOT) — full API access, no JIT limitations

### DllImportResolver Conflict

Generated bindings use `[ModuleInitializer]` to register a `DllImportResolver` for the assembly. If your application also calls `NativeLibrary.SetDllImportResolver` for the same assembly, it will throw `InvalidOperationException`. Wrap your call in a try-catch if you need custom resolution alongside generated bindings.

## Further Reading

- [Known Issues and Workarounds](known-issues-workarounds.md) — detailed Mono JIT workaround documentation
- [NativeAOT Investigation](Future/nativeaot-investigation.md) — deep technical analysis of all three blockers
- [Microsoft NativeAOT iOS docs](https://learn.microsoft.com/en-us/dotnet/maui/deployment/nativeaot) — general .NET MAUI NativeAOT deployment
