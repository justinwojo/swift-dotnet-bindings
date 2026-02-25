# Session 3 Results: Existential Default-Arg Bypass & Protocol Receiver Relaxation

**Completed**: February 2026
**Plan**: `src/docs/session-3-plan.md`
**Baseline**: Unit 4161, Integration 700, Validation 32/32

---

## What Was Done

### 3a: Method Bypass Generalization

Added `TryEmitMethodBypass` to `ExistentialBypassEmitter.cs` — extends the existing constructor bypass pattern to void-returning, non-throwing instance methods on classes and non-frozen structs.

**Key design decisions:**
- **Class self**: `*(IntPtr*)_payload.DangerousGetHandle()` (dereference buffer to get object pointer)
- **Struct self**: `_payload.DangerousGetHandle()` (buffer IS the struct data), with write-back for mutating methods
- **Frozen struct exclusion**: C# value types have no `_payload` field — bypass returns false
- **Parity check**: All passthrough params must have identical wrapper/P/Invoke signatures (no marshalling)

**Refactored MethodHandler.cs** from early-return to accumulate pattern — two inline existential gates now collect `hasMethodExistentialArg` + `firstMethodExistentialType` and attempt bypass after the parameter loop.

**Files changed:**
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ExistentialBypassEmitter.cs` — added `TryEmitMethodBypass`, `EmitMethodSwiftWrapper`, `EmitMethodCSharpBinding`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` — refactored existential gates from early-return to accumulate + bypass

### 3b: Protocol Interface Recovery

Converted ProtocolHandler's B9 gate from hard-skip (`continue`) to fall-through, following the Q4b closure recovery pattern. Methods with existential parameters now:
1. **Appear in protocol interfaces** — concrete C# types can implement them
2. **Get `NotSupportedException` stubs in proxy classes** — proxy can't dispatch existential containers

**Files changed:**
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ProtocolHandler.cs` — B9 gate fall-through, `existentialSkippedMethodKeys` tracking
- `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.cs` — accepts `existentialSkippedMethodKeys` parameter
- `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.InterfaceImpl.cs` — emits `NotSupportedException` stubs for existential methods
- `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolConformanceValidator.cs` — removed existential skip from `IsMethodSkippedFromInterface`

---

## Measured Impact

### Method Bypass (3a): 0 methods bypassed

The bypass infrastructure is correct but no real-world methods pass the parity check. The primary blocker is `String?` passthrough parameters — the wrapper uses `string?` but P/Invoke uses `SwiftOptional<SwiftString>`, so signatures differ. This is a fundamental limitation of the passthrough-only bypass pattern.

**Example**: Mixpanel `track(event: String?, properties: [String: any MixpanelType]?)` — the bypass correctly identifies `properties` as the existential param to omit, but `event: String?` is a passthrough param that requires marshalling.

**The ~67 upper bound from the plan was explicitly flagged as an upper bound** assuming all passthrough params are blittable. In practice, most existential methods in analytics/networking libraries have `String` parameters.

Constructor bypass (pre-existing from Session U1) continues to work unaffected.

### Protocol Interface Recovery (3b): 45 methods recovered across 13 libraries

| Library | Methods Recovered | Protocols Affected |
|---------|:-:|---|
| Kingfisher | 8 | IImageProcessor, IKFOptionSetter |
| StripeConnect | 7 | 7 delegate protocols |
| Starscream | 7 | IHTTPHandler, IHTTPServerHandler, IWebSocketDelegate, IEngine, IFramer, ITransport |
| Alamofire | 6 | IParameterEncoding, IAuthenticator, IRequestRetrier, IRequestInterceptor, IEventMonitor |
| StripeUICore | 4 | IContainerElement, IElementDelegate |
| Mixpanel | 3 | IMixpanelFlags, IMixpanelType |
| GRDB | 3 | IColumnExpression, IDatabaseReader |
| SkeletonView | 2 | IAssociatedObjects |
| RxSwift | 1 | IObserverType (16 others blocked by AnyType-in-generic-arg) |
| StripeCore | 1 | ISTPAnalyticsClientProtocol |
| Stripe | 1 | ISTPApplePayContextDelegate |
| StripeApplePay | 1 | IApplePayContextDelegate |
| BlinkIDUX | 1 | IPreviewSource |
| **Total** | **45** | **32 unique protocols** |

These 45 methods were previously invisible in C# interfaces. Concrete types implementing these protocols can now provide real implementations. The proxy stubs throw `NotSupportedException` (existential containers can't be marshalled in callbacks), matching the Q4b closure recovery pattern.

### Test Results

| Suite | Result | Baseline |
|-------|--------|----------|
| Unit tests | 4161 passed, 0 failed, 1 skipped | Matches |
| Integration tests | 700 passed, 0 failed, 11 skipped | Matches |
| Library validation | 32/32 passed, 0 regressions | Matches |

---

## What Was NOT Done (Deferred)

The session plan describes "Session 3" in the usability roadmap as covering `[String: Any]` dictionary projection and existential parameter projection. **The actual session 3 plan (`session-3-plan.md`) was scoped to the bypass + protocol recovery work only** — the dictionary/existential projection work (roadmap sessions 3a-3c) remains future work.

Specific deferrals:
- **Method bypass for non-void returns**: Would need return value marshalling in the wrapper
- **Method bypass with marshalled passthrough params**: Would need the bypass to handle string/optional conversion (significant complexity)
- **Property bypass**: PropertyHandler has its own unconditional existential skip; needs separate relaxation
- **`[String: Any]` → `Dictionary<string, object>` projection**: Requires runtime boxing infrastructure
- **`any Protocol` → `object` parameter projection**: Requires existential container unpacking

---

## Architectural Notes for Future Work

1. **Bypass parity constraint**: The fundamental limitation is that the bypass emits a `@_silgen_name` function called via `[LibraryImport]`. All passthrough params must have identical representations in the wrapper (C# types) and the P/Invoke (Swift ABI types). Extending bypass to handle marshalled params would require the bypass to replicate the marshalling layer — at that point it's simpler to emit a proper Swift wrapper with full type conversion.

2. **Protocol recovery is the real win**: The 45 recovered interface methods make C# protocol interfaces more complete. This directly improves the Protocol/Interface scoring category for 13 libraries and enables concrete type implementations.

3. **SB0003 message cosmetic issue**: Both closure and existential proxy stubs use `EmitNotSupportedMethodStub`, which emits an SB0003 diagnostic mentioning "closure parameters". The existential stubs should ideally mention "existential parameters". This is cosmetic only — the behavior is correct.
