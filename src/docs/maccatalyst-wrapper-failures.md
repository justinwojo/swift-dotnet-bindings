# Mac Catalyst Swift Wrapper Compile Failures — Resolved

Historical record of three Apple framework targets whose Mac Catalyst Swift wrapper
slice failed to compile in the apple-framework validation tier from 2026-04-21
through 2026-04-27. C# bindings always compiled cleanly; only the per-slice Swift
wrapper binary was missing.

## Affected targets

- `LiveCommunicationKit@maccatalyst`
- `ProximityReader@maccatalyst`
- `StoreKit2@maccatalyst`

iOS, macOS, and tvOS slices for these frameworks were unaffected.

## How the failure surfaced

`nuke validate` runs `Build.Validation.CheckSwiftWrapper` over each target's
`*SwiftBindings.framework` directory and asserts that every slice contains both
the compiled binary and an embedded `Info.plist`. For the three targets the Mac
Catalyst slice contained only `Info.plist`. The wrapper compile path
(`SwiftWrapperCompiler.CompileSlice`) was capturing stderr only when `Verbose`
was set, and `CheckSwiftWrapper`'s artifact-presence branch — not the swiftc
exit code — was the path the failure reached the gate through. `result.SwiftVerbose`
stayed empty so the diagnostic was invisible.

## Root causes (two distinct bugs)

1. **macCatalyst availability mismatch (ProximityReader, StoreKit2, partial LCK).**
   `WrapperEmitterHelpers.CollectStrictestAvailabilityKeys` derived per-platform
   maxima independently. When a member's iOS availability rose above its
   declared macCatalyst availability, the wrapper emitted
   `@available(macCatalyst <older>, …)` and swiftc rejected the call as
   unavailable on the older Catalyst. Catalyst tracks iOS 1:1 for the unified
   SDK era (iOS 13+); the helper now lifts macCatalyst to the iOS max when iOS
   is newer and ≥ 13.0. `BuildAvailabilityLines` /
   `BuildAvailabilityAttributeLines` in `WrapperEmitter.Async`,
   `WrapperEmitter.Marshalling`, and `AsyncHarnessEmitter` now route through
   that single helper so the lift applies consistently.

2. **Missing protocol method-descriptor symbols (LiveCommunicationKit).**
   Apple's macCatalyst swiftinterface for
   `LiveCommunicationKit.ConversationManagerDelegate` declares
   `conversationManager(_:didActivate:)` and `conversationManager(_:didDeactivate:)`,
   but the macOS-platform TBD (which backs the Catalyst link) does not export
   the corresponding `Tq` (method-descriptor) symbols. The synthesized
   `extension EveryProtocol: ConversationManagerDelegate` referenced unresolved
   descriptors and ld64 rejected the wrapper. The parser
   (`SwiftABIParser.HandleNominalDecl`) now checks each required method's
   `MangledName + "Tq"` against `_demangledTbd.AllSymbols` and sets
   `ProtocolDecl.HasMissingTbdMethodDescriptors` when any descriptor is missing;
   `EveryProtocolEmitter.WillSkipConformance` /
   `EmitProtocolConformance` honor that flag. The protocol's existential
   surface (vtable + `*Proxy` C# class) is unaffected — only the
   `EveryProtocol` conformance is suppressed on the slice that lacks the
   descriptor.

## Diagnostic propagation

`Build.Validation.CompileWrapper` and `GenerateAppleFrameworkTarget` now
capture and surface `swiftc` stderr regardless of `Verbose` whenever swift
compilation reports `fail`. The `ExtractSwiftDiagnosticLines` helper renders
the first eight `\.swift:\d+:\d+: (?:error|warning):` lines (with sensible
fallbacks) so future failures aren't silent.

## Fixed in

- Parser: `SwiftABIParser.HandleNominalDecl` (TBD `Tq` cross-check)
- Model: `ProtocolDecl.HasMissingTbdMethodDescriptors`
- Emitter: `EveryProtocolEmitter.WillSkipConformance` and
  `EveryProtocolEmitter.EmitProtocolConformance`
- Emitter helpers: `WrapperEmitterHelpers.CollectStrictestAvailabilityKeys`
  (macCatalyst→iOS lift)
- Emitter dispatch: `WrapperEmitter.Async`, `WrapperEmitter.Marshalling`,
  `AsyncHarnessEmitter` route through the shared helper
- Build: `Build.Validation.CompileWrapper`, `GenerateAppleFrameworkTarget`
  (always capture swiftc stderr)
- Tests: `EveryProtocolEmitterTests` —
  `WillSkipConformance_HasMissingTbdMethodDescriptors_ReturnsTrue`,
  `EmitProtocolConformance_HasMissingTbdMethodDescriptors_SkipsConformance`
- Baseline: `swift_compile` flipped from `fail` → `ok` for all three targets.
