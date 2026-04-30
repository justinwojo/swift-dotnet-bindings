# M1 Redesign Proposal — `libswiftDemangle` Swap

**Status**: Awaiting user decision. M1 of `architecture-gameplan-v2.md` is paused; no production code has been written.

## Executive Summary

Milestone 1 of `architecture-gameplan-v2.md` proposed replacing the ~5,800 LOC managed Swift demangler with a P/Invoke into Apple's `libswiftDemangle.dylib`, hidden behind a single `IDemangler` seam, with byte-equal parity tests between the managed and native strategies. A pre-implementation Codex audit (research mode) plus direct repo and host inspection found three blockers that make M1-as-written undeliverable:

1. **The native dylib's public C API does not expose AST traversal.** It returns demangled *display strings* only. Every existing call site (`SwiftABIParser.cs`, `DemanglingResults.cs`) consumes a *structured* `IReduction` tree (`TypeSpecReduction`, `FunctionReduction`, `MetadataAccessorReduction`, etc.). A single `IDemangler` interface satisfied by both strategies is therefore impossible without writing a string→`IReduction` parser — which is larger and riskier than the managed port we'd be retiring.
2. **"Byte-equal parity" has no operational definition.** The managed port has no textual printer; `Node.ToString()` emits a debug tree, not Apple's display form. The two strategies produce fundamentally different shapes, so direct comparison is undefined until we write a printer.
3. **The plan's dylib path and exported symbol names are wrong.** `/usr/lib/swift/libswiftDemangle.dylib` does not exist on macOS. The actual public C exports of `libswiftDemangle.dylib` are `swift_demangle_getDemangledName` / `getSimplifiedDemangledName` / `getModuleName` / `hasSwiftCallingConvention` — not `swift_demangle` (which lives in `libswiftCore.dylib` and has different ownership semantics).

The remaining decision is whether the swap is worth doing in some reduced form, deferred to a later track, or dropped entirely in favor of going straight to M2 (SwiftSyntax producer), which is independent and still passes the v2 litmus test on its own.

## Options

### Option A — Native text demangler **as a sidecar**, managed structured port retained

Land an `ITextDemangler` seam with `NativeLibSwiftDemangleTextStrategy` (P/Invoke into `swift_demangle_getDemangledName`) for diagnostic / corpus-validation use. The structured `IReduction` path (the entire managed port) stays exactly where it is. Parity gate = the native strategy's output matches `swift demangle --compact` for a corpus.

- **Litmus**: **fails**. Retires zero LOC. Doesn't prevent any class of bad generated binding — the structured demangler is what governs binding correctness, and it is unchanged. Adds a usable diagnostic surface but that's purely additive.
- **Scope**: 1 session.
- **Reversibility**: trivial. New code only; nothing existing is touched.

### Option B — String→`IReduction` reverse parser

Write a parser that converts Apple's display string output back into the existing `IReduction` types (`TypeSpecReduction`, `FunctionReduction`, etc.). Then the native strategy can satisfy the existing `IReduction Run(string)` contract end-to-end and the managed port can be retired.

- **Litmus**: **fails on its own merits**. We'd be re-implementing a structured demangler in C# *to validate against* the managed structured demangler we're trying to retire — the validator is the thing we're trying to retire. Snake-eating-tail.
- **Scope**: unbounded. The display grammar is large and not formally specified; closures/conformances/dispatch thunks/protocol witness tables/dependent generics each need their own grammar slice; failures from regressions surface only at the consumer (mis-emitted bindings).
- **Reversibility**: poor. Once consumers depend on a parser-derived `IReduction`, divergence between managed and native output becomes a binding-correctness incident.

### Option C — Drop M1, go straight to M2

Remove M1 from v2. Move "demangler swap" back to `Future/post-1.0-architecture-roadmap.md`, possibly with a redirect note explaining why the obvious version of it doesn't ship. Open the v2 track at M2 (SwiftSyntax producer behind `SwiftInterfaceFacts`).

- **Litmus**: **passes via M2 alone**. M2 is independent of M1, retires ~4.2k LOC of regex parsing (4,223 lines of `SwiftInterfaceAccessParser.cs` as of this commit — see receipts below), and addresses the surface flagged by the parent gameplan as "the single largest 'silent wrong binding' risk surface in the codebase". The v2 ROI bar is met without a demangler change.
- **Scope**: 0 sessions for "the M1 drop"; M2 itself is 3–5 sessions, unchanged.
- **Reversibility**: trivial. The deferred item is documented, not deleted.

### Option D — Split M1: ship sidecar now, defer structured retirement to its own milestone

Land Option A this session (text-demangler sidecar) **and** schedule a separate, larger milestone whose explicit goal is "retire the managed structured demangler" — with its own audit and its own design. Treat the sidecar as a foundation: it gives a trusted reference output that any later structured-replacement work can validate against.

- **Litmus**: **fails as proposed.** The sidecar half is purely additive (zero LOC retired) and the structured-retirement half *is* Option B by another name — it inherits Option B's snake-eating-tail problem (the parser would be validated against the managed port we're trying to retire) and Option B's unbounded grammar scope. Option D therefore offers no path to litmus-pass that Option B does not. Its only honest claim is "do Option A now and re-pose the structured-retirement question later", at which point the question is just Option B with the receipts moved.
- **Scope**: 1 session for the sidecar; the structured-retirement follow-up is unscoped — and would not be undertaken without first resolving Option B's design problems.
- **Reversibility**: sidecar is trivially reversible; any structured follow-up is reversible only up to the point where consumers depend on parser-derived reductions.

## Codex's Recommended Pivot (verbatim)

From the M1 audit, Session ID `019ddd18-d177-7ef2-b183-513402238d73`:

> Bottom line: M1 as written is not a clean swap. The safe Session 1 is a seam around the existing structured demangler plus a probed/cached native text demangler used for diagnostics and corpus discovery. Retiring the managed port requires a separate design for reconstructing `IReduction` from native data, or a private/unstable C++ AST integration that I would not recommend for this generator.

Codex also recommended:
> Recommendation: change M1 from "native strategy replaces managed strategy" to "introduce a structured demangler seam and native text demangler sidecar." Keep `Run(string): IReduction` managed-only for Session 1.

That is, in this proposal's vocabulary: **Option D**.

## Worker Recommendation

**Recommend Option C — drop M1.**

Reasoning:
- Option A and Option D do not retire any LOC and do not address any class of bad binding on their own. Option D's structured-retirement half is Option B by another name and inherits Option B's snake-eating-tail problem.
- M1 was framed in `architecture-gameplan-v2.md` around retiring ~5,800 LOC of drift surface. The audit findings indicate that retirement is not reachable through the public C API. A redesign that quietly walks back the LOC-retirement claim while keeping M1 as a milestone risks giving the impression of progress that isn't being made.
- M2 is independent of M1 and meets the v2 ROI bar by itself (see "M2 receipts" below). Going straight to M2 loses no time in the v2 track and avoids burning a session on a sidecar whose downstream value depends on a milestone that hasn't been scoped.
- The demangler-swap idea is not *wrong* — it just isn't deliverable in the shape the doc currently claims. Moving it back to `Future/post-1.0-architecture-roadmap.md` with a redirect to this proposal preserves the audit work for whoever picks it back up.

If there is a current diagnostic / debugging need for a text demangler — e.g., a consumer-visible mismangling under investigation — Option A is a reasonable response to that need. That is a product decision, not an architecture-track decision, and should not be bundled into the v2 plan.

## Specific Questions for the User

1. **Adopt Option C and drop M1 from v2?** If yes: should "demangler swap" return to `Future/post-1.0-architecture-roadmap.md` with a redirect to this proposal, or be removed entirely?
2. **If overriding to Option D (or A):** is there a current diagnostic / debugging need that justifies the sidecar this session, or is approval purely speculative future-utility? The answer changes how aggressively the sidecar gets factored, tested, and shipped.

## What I Verified by Direct Inspection

**Existing managed-demangler call sites** (all consume structured `IReduction`, none consume display strings):
- `src/Swift.Bindings/src/Demangler/DemanglingResults.cs:79` — instantiates `Swift5Demangler`
- `src/Swift.Bindings/src/Demangler/DemanglingResults.cs:93` — `demangler.Run(symbolName)` → aggregated by reduction subtype
- `src/Swift.Bindings/src/Parser/SwiftABIParser.cs:159` — owns a `private readonly Swift5Demangler demangler = new()`
- `src/Swift.Bindings/src/Parser/SwiftABIParser.cs:1010` — `demangler.Run(node.MangledName)` cast to `TypeSpecReduction`
- `src/Swift.Bindings/src/Parser/SwiftABIParser.cs:1440` — `demangler.Run(conformance.MangledName)`
- `src/Swift.Bindings/src/Parser/SwiftABIParser.cs:1556` — `demangler.Run(mangledName)`
- `src/Swift.Bindings/src/Program.cs:180`, `:222` — `DemanglingResults.FromTbd(...)`

**Public API surface of the managed port** (from `Swift5Demangler.cs` and `DemanglingResults.cs`):
- `IReduction Run(string)` — main entry point. Returns one of `TypeSpecReduction`, `FunctionReduction`, `DispatchThunkFunctionReduction`, `MetadataAccessorReduction`, `ProtocolWitnessTableReduction`, `ProtocolConformanceDescriptorReduction`, `ProvenanceReduction`, or `ReductionError`.
- `Node DemangleSymbol(string)` — raw AST. Documented as broken for typical symbols; only exercised by tests.
- `static bool IsSwiftSymbol(string)` — prefix check.
- `DemanglingResults.GetMetadataAccessor`, `TryGetMetadataAccessor`, `GetProtocolConformanceDescriptor` — string lookups over the aggregated reductions.
- **Confirmed: no method on `Swift5Demangler`, `Node`, or any reduction type emits Apple's display-form demangled string.** `Node.ToString()` (`Node.cs:264`) emits a debug tree of the form `->Kind ("Text")`.

**Reducer-handled `NodeKind` set** (regenerated from `Swift5Reducer.cs` via `grep -oE 'NodeKind\.[A-Z][A-Za-z]+' | sort -u` — **35 distinct kinds**):
`Allocator`, `ArgumentTuple`, `AsyncAnnotation`, `BoundGenericClass`, `BoundGenericEnum`, `BoundGenericStructure`, `Class`, `DependentGenericParamType`, `DependentGenericSignature`, `DependentGenericType`, `DependentMemberType`, `DispatchThunk`, `Enum`, `Function`, `FunctionType`, `Global`, `Identifier`, `LabelList`, `Module`, `NoEscapeFunctionType`, `Protocol`, `ProtocolConformance`, `ProtocolConformanceDescriptor`, `ProtocolWitnessTable`, `ReturnType`, `Static`, `Structure`, `ThrowsAnnotation`, `Tuple`, `TupleElement`, `TupleElementName`, `Type`, `TypeList`, `TypeMetadataAccessFunction`, `VariadicMarker`.

**Dylib paths on this host** (Apple Silicon Mac, Xcode + Command Line Tools both installed):
- `/usr/lib/swift/libswiftDemangle.dylib` — **does NOT exist** (the path proposed by the original plan).
- `/Applications/Xcode.app/Contents/Developer/Toolchains/XcodeDefault.xctoolchain/usr/lib/libswiftDemangle.dylib` — exists. `current version 6.2.3`. Universal (x86_64 + arm64).
- `/Library/Developer/CommandLineTools/usr/lib/libswiftDemangle.dylib` — exists. Same size; presumably the CLT-bundled copy.
- `xcrun --find swift` → `/Applications/Xcode.app/.../usr/bin/swift`. So the toolchain-derived path `$(dirname $(dirname $(xcrun --find swift)))/lib/libswiftDemangle.dylib` resolves correctly.

**Public C exports of `libswiftDemangle.dylib`** (`nm -gU ... | grep -E "^[0-9a-f]+ T _swift_demangle"`):
- `_swift_demangle_getDemangledName`
- `_swift_demangle_getModuleName`
- `_swift_demangle_getSimplifiedDemangledName`
- `_swift_demangle_hasSwiftCallingConvention`

**Not exported by `libswiftDemangle.dylib`**: `_swift_demangle` (the malloc'd-`char*` runtime ABI form). That symbol is exported by per-platform copies of `libswiftCore.dylib` shipped under `usr/lib/swift-5.0/<platform>/` in the toolchain. Confirmed locally: `_swift_demangle` is exported by `/Applications/Xcode.app/Contents/Developer/Toolchains/XcodeDefault.xctoolchain/usr/lib/swift-5.0/macosx/libswiftCore.dylib`. Its memory ownership and signature differ from the buffer-based exports of `libswiftDemangle.dylib`; using it would mean binding a different dylib (one of many platform-specific runtime copies) with malloc/`free` semantics.

**Header signature** (per Swift open-source `include/swift/SwiftDemangle/SwiftDemangle.h`): `size_t swift_demangle_getDemangledName(const char *MangledName, char *OutputBuffer, size_t Length)`. Caller-allocated buffer. Returns required length; if buffer is too small, the buffer is filled to capacity and the full length is returned.

**`xcrun swift-demangle` is installed and works**: `/Applications/Xcode.app/.../usr/bin/swift-demangle`. Confirmed `swift-demangle --compact '$ss5Int32V'` → `Swift.Int32`. This is the natural reference oracle for any text-parity gate.

**Existing demangler tests** (would need to migrate to a new seam if any restructuring lands):
- `src/Swift.Bindings/tests/UnitTests/DemanglerTests/BasicDemanglingTests.cs`
- `src/Swift.Bindings/tests/UnitTests/DemanglerTests/DemanglerSession2Tests.cs`
- `src/Swift.Bindings/tests/UnitTests/DemanglerTests/Swift5ReducerTests.cs`
- 9 parser-test files use a reflection helper to construct empty `DemanglingResults` instances; any refactor of `DemanglingResults`'s shape touches all of them.

## M2 Receipts (for Option C's "M2 stands on its own" claim)

The Option C recommendation is partly grounded in the assertion that M2 (SwiftSyntax producer) meets the v2 ROI bar without M1. Confirming that here so the user does not need to re-verify:

- `src/Swift.Bindings/src/Parser/SwiftInterfaceAccessParser.cs` — **4,223 lines** as of this commit (the parent gameplan's "4,066 LOC" figure was a snapshot; current size is in that order of magnitude). This file is the regex-driven parser of `.swiftinterface` text and is the producer that M2 proposes to replace with a SwiftSyntax-driven host program.
- `src/Swift.Bindings/src/Parser/SwiftInterfaceFacts.cs` — defines **14 dictionary-shaped facts** plumbed through `SwiftInterfaceFacts` (counted directly: `ParameterNames`, `TypedThrowsErrors`, `EnumCaseLabels`, `EnumCaseRawValues`, `CustomActorIsolatorMap`, `MarkerProtocolConformances`, `AvailabilityAnnotations`, `DefaultParameterValues`, `AutoclosureParameters`, `SubscriptLabels`, `HiddenRequirementProtocols`, `MainActorTypePositions`, `AvailabilityAnnotationPositions`, `ConventionCProtocolPositions`). The parent gameplan's "23 nullable side-channel maps" referred to a pre-consolidation count; current count after M4's aggregator landing is 14. Either way, each dict represents one regex-inferred fact whose drift risk M2 retires.
- `architecture-gameplan-v2.md` lines 22–23 frame regex parsing as "the single largest 'silent wrong binding' risk surface in the codebase". That framing is the parent doc's claim, not separately re-verified here. M2's pre-implementation audit is its own scope and is not pre-approved by this proposal.

Bottom line: the LOC-retirement and surface-area claims for M2 are concretely backed by the file sizes above. The "single largest silent-wrong-binding surface" claim is inherited from the parent gameplan and remains its responsibility.
