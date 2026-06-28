# LiveCommunicationKit — Binding Audit

- **Package**: SwiftBindings.Apple.LiveCommunicationKit v26.2.8   **Mode**: apple   **TFM(s)**: net10.0-ios26.2
- **Native**: Apple LiveCommunicationKit.framework (iOS 17.4+, watchOS 10.4+, visionOS 1.1+, macCatalyst 17.4+)
- **Audited at**: swift-dotnet-packages main 1e8c27a, generated 2026-06-27T19:49Z

## Verdict

Solid foundation: all 33 types emitted and the primary call-lifecycle flow (ConversationManager init → report incoming → report event → perform actions) is fully surfaced with correct async/Task/CancellationToken wiring. Real gap surface after discounting 12 intentional SynthesizedCodable skips is 8 members — the most significant is a DuplicateSignature collision that silently drops one of two AVAudioSession delegate callbacks (`didActivate` vs. `didDeactivate`), leaving consumers unable to distinguish audio session activation from deactivation. Tests are 100% metadata-only; no round-trip value or async call coverage exists.

## 1. Coverage

### Totals

| Dimension | Count | % |
|---|---|---|
| Types emitted / total | 33 / 33 | 100% |
| Members emitted / total | 107 / 146 | 73.3% |
| Skipped members | 20 | — |
| Synthesized members (generator-added) | 97 | — |

### Skip-reason breakdown

| Reason | Count | Classification |
|---|---|---|
| SynthesizedCodable | 12 | (a) Correctly excluded — `Encoder`/`Decoder` existential protocols unresolvable by design |
| UnsupportedSignature | 4 | (b) Real gaps — see below |
| DuplicateSignature | 2 | (b) Real gaps — functionally significant |
| SwiftUIConstraint | 1 | (b) Real gap (marginal) — Foundation.Predicate gated by SwiftUI module check |
| EveryProtocolConformanceSkipped | 1 | (b) Real gap (low value) — marker protocol, empty interface |

**Effective real-gap surface**: 8 / 134 non-SynthesizedCodable members (6%).

### SynthesizedCodable items (correctly excluded — all 12)

`Conversation.Event.encode`, `Conversation.Update.encode/init`, `ConversationAction.State.encode`, `StartCellularConversationAction.encode/init`, `CellularService.encode/init`, `ConversationHistoryManager.RecentConversation.encode/init`, `Handle.encode/init`. All dropped because `Encoder`/`Decoder` are unresolvable existential protocols — intentional.

### Real gaps (8)

**Gap 1 — ConversationManager.pendingConversationActions (UnsupportedSignature)**  
Swift: `pendingConversationActions` on `ConversationManager`. Details: "unsupported placeholder type." Likely returns `[any ConversationAction]` or similar existential array — the same placeholder-type blocker seen in other frameworks. The method queries outgoing conversation actions not yet completed; consumers need it to process pending work. **Worth investigating**: if the return type is `[ConversationAction]` (a concrete class already bound), a manual wrapper would unblock it.

**Gap 2 — ConversationHistoryManager.ConversationHistoryDidUpdate.makeMessage (UnsupportedSignature)**  
Swift: `makeMessage` on `ConversationHistoryDidUpdate` notification struct. Details: "unsupported placeholder type." Returns a notification payload — medium value for history UI. Lower priority than Gap 1.

**Gaps 3 & 4 — ConversationHistoryManager.RecentConversation.Status.encode / Direction.encode (UnsupportedSignature)**  
Both: "Parameter 'to' has unsupported type for simple enum extension method." These are `Encodable` extension methods on simple enums — the `Encoder` existential blocks them. Codable-adjacent; low consumer value. No action needed.

**Gaps 5 & 6 — ConversationManagerDelegate.conversationManager (DuplicateSignature) × 2 — FUNCTIONALLY SIGNIFICANT**  
Swift `ConversationManagerDelegate` includes two distinct callbacks both taking `(ConversationManager, AVAudioSession)`:  
- `conversationManager(_:didActivate:AVAudioSession)` — audio session is now active  
- `conversationManager(_:didDeactivate:AVAudioSession)` — audio session is now inactive  

After label stripping both map to `void ConversationManager(ConversationManager, AVAudioSession)` in C#. The generator keeps the first-processed one (line 6777) and drops two as DuplicateSignature. The surviving overload `void ConversationManager(ConversationManager manager, AVAudioSession audioSession)` gives consumers no indication of whether activation or deactivation fired. A consumer implementing `IConversationManagerDelegate` can react to *an* audio event but cannot distinguish the two. **Medium priority**: workaround is to check `audioSession.IsOtherAudioPlaying` or similar heuristic, but that is fragile. A generator fix (rename with suffix from Swift label, e.g. `ConversationManagerDidActivateAudioSession` / `ConversationManagerDidDeactivateAudioSession`) would close this correctly.

**Gap 7 — ConversationHistoryManager.recentConversations (SwiftUIConstraint)**  
The predicate-based query API uses `Foundation.Predicate<RecentConversation>`. The generator flags the `Foundation.Predicate` signature as referencing an unsupported module. Low consumer priority for this framework; a Swift wrapper returning a filtered array would suffice.

**Gap 8 — Foundation.MessageIdentifier EveryProtocol proxy (EveryProtocolConformanceSkipped)**  
`IMessageIdentifier` is emitted as an empty C# interface (line 9273) but the EveryProtocol proxy was not generated. C# types cannot be passed as `any MessageIdentifier` to Swift. Low value — the protocol appears to be a marker; `Handle` already carries identity via `Uuid`.

### Prioritized generator unlocks

| Priority | Gap | Fix | Value |
|---|---|---|---|
| **P1** | Gaps 5+6: DuplicateSignature on `(ConversationManager, AVAudioSession)` | Emit Swift-label–derived suffix when C# signature collision exists (e.g. `…DidActivateAudioSession` / `…DidDeactivateAudioSession`) | High — audio session lifecycle is required for proper VoIP behavior |
| **P2** | Gap 1: `pendingConversationActions` placeholder type | Inspect the concrete return type; if it is `[ConversationAction]`, special-case the placeholder to emit a typed list | Medium — needed for full action-pump loop |
| **P3** | Gap 7: `Foundation.Predicate` SwiftUIConstraint | Treat Foundation.Predicate as a first-class supported type separate from SwiftUI views | Low — history manager query |

## 2. C# Quality

**Naming / shape**: PascalCase throughout, no mangled symbols. Types are well-organised — `ConversationManager` at top level, nested types (`Conversation.Event`, `Conversation.Update`, `Conversation.Capabilities`, `ConversationManager.ConfigurationType`, `ConversationHistoryManager.RecentConversation`, etc.) reflect the Swift hierarchy. Enums (`Conversation.StateType`, `Conversation.EndedReason`, `PlayToneAction.ToneType`, `SetTranslatingAction.TranslationEngine`, `Handle.Kind`) are plain C# `enum` with `long` or `int` backing — correct.

**Async**: All four async Swift methods surface correctly as `Task`-returning methods with `CancellationToken`:
- `ConversationManager.ReportNewIncomingConversationAsync(Guid, Conversation.Update, CancellationToken)` (line 6004)
- `ConversationManager.PerformAsync(IEnumerable<ConversationAction>, CancellationToken)` (line 5836)
- `ConversationManager.ReportNewIncomingVoIPPushPayloadAsync(IDictionary<SwiftAnyHashable, object>, CancellationToken)` (line 6211)
- `TelephonyConversationManager.StartCellularConversationAsync(StartCellularConversationAction, CancellationToken)` (line 3996)

Each includes the Swift cancellation bridge (SBW_CancelTask → `TrySetCanceled`), proper SwiftAsyncCallHolder cleanup on cancellation/error/success paths, and `SwiftException` on non-cancellation errors. ✓

**IConversationManagerDelegate ergonomics — awkward but structurally correct**  
The interface (lines 6771–6778) contains three overloads all named `ConversationManager`:
```csharp
void ConversationManager(ConversationManager manager, Conversation conversation);
void ConversationManager(ConversationManager manager, ConversationAction action);
void ConversationManager(ConversationManager manager, AVAudioSession audioSession);
```
These overloads compile and are distinguishable by parameter type, but the method name `ConversationManager` clashes with the class name and communicates nothing about the event semantics. A consumer must inspect the second parameter type to understand which callback fired. This is a natural consequence of 1:1 Swift-selector mapping without argument-label preservation; no change is needed unless the generator gets a label-preservation pass, but it is worth flagging so consumers are warned in the package README.

**Nullability**: `Delegate` property is `IConversationManagerDelegate?` (line 4626); `Conversation.LocalMember` is `Handle?` (line 162); optional returns throughout use nullable reference types correctly. ✓

**Lifetime / IDisposable**: All class wrappers (`Conversation`, `ConversationManager`, `TelephonyConversationManager`, `ConversationHistoryManager`, action classes) implement `ISwiftObject, IDisposable` with ARC SafeHandle bridging and GC finalizer. Struct wrappers (`Conversation.Event`, `Conversation.Update`, `Conversation.Capabilities`, `Handle`, etc.) implement `ISwiftObject, ISwiftStruct, IDisposable` with SafeHandle payload. Platform guard throws `PlatformNotSupportedException` before any native call if the OS version is below the minimum. ✓

**`ConversationHistoryManager.SharedInstance`**: Correctly emitted as a `static` property (line 7345) returning the singleton. ✓

**`Conversation.Uuid` returns `System.Guid`**: Swift `UUID` → `System.Guid` mapping (line 56). ✓

**Platform annotations on `IConversationManagerDelegate`**: All four platforms annotated correctly — ios17.4, watchos10.4, visionos1.1, maccatalyst17.4 (lines 6767–6770). ✓

## 3. Test Coverage

**Test file**: `tests/Tests.cs` (run via `Program.UIKit.cs` on iOS/Catalyst).

**Case count**: 18 total — 17 `MetadataTest<T>` calls + 1 enum-value check (`SetTranslatingAction.TranslationEngine`). All guarded by `#if IOS`.

**Depth**: Weak — metadata-only. `MetadataTest<T>` calls `SwiftObjectHelper<T>.GetTypeMetadata()` and asserts the handle is non-zero. This exercises the class/struct registration path but proves nothing about ABI correctness, property values, method dispatch, or async behavior.

**Coverage map**:

| Surface | Test coverage |
|---|---|
| `Conversation` metadata | ✓ metadata only |
| `Conversation.Event`, `Update`, `Capabilities` | ✓ metadata only |
| `ConversationAction` + 9 action subclasses | ✓ metadata only (9/9 in test: Start, StartCellular, Join, End, Merge, Unmerge, Mute, Pause, PlayTone, SetTranslating — 10 total, 10 covered) |
| `Handle`, `CellularService` | ✓ metadata only |
| `SetTranslatingAction.TranslationEngine` values | ✓ enum raw-value round-trip |
| `Conversation.State`, `Conversation.EndedReason` | ✗ no test |
| `ConversationManager.Init(configuration)` | ✗ no test |
| `ConversationManager.Conversations` | ✗ no test |
| `ConversationManager.Delegate` set/get | ✗ no test |
| `ConversationManager.ReportNewIncomingConversationAsync` | ✗ no test |
| `ConversationManager.ReportConversationEvent` | ✗ no test |
| `ConversationManager.PerformAsync` | ✗ no test |
| `ConversationHistoryManager.SharedInstance` | ✗ no test |
| `Conversation.Uuid`, `.LocalMember`, `.State` properties | ✗ no test |
| `IConversationManagerDelegate` impl / proxy round-trip | ✗ no test |
| `TelephonyConversationManager.StartCellularConversationAsync` | ✗ no test |

**High-value tests to add** (in rough priority order):

1. **ConversationManager construction** (`ConversationManager.Init(configuration)`) — verify the class can be instantiated and `Conversations` returns an empty list. Proves the constructor and `SwiftArray<Conversation>` → `IReadOnlyList` conversion path.

2. **Delegate set/get round-trip** — set a C# implementation of `IConversationManagerDelegate`, read it back via `manager.Delegate`, assert non-null. Proves the EveryProtocol proxy wire path for this framework.

3. **Conversation.Event enum-case round-trip** — construct each `Conversation.Event` case (`ConversationUpdated`, `ConversationStartedConnecting`, etc.) and assert `.CurrentCase` returns the expected `CaseTag`. Proves struct payload allocation + discriminator read.

4. **Conversation.State enum value check** — assert `Conversation.StateType.Active/Inactive` raw values match Swift. Validates the metadata-registered enum mapping.

5. **ConversationHistoryManager.SharedInstance** — access the singleton and verify non-null. Proves the static property bridging and iOS 26+ version guard.

6. **Handle construction** — verify `Handle` can be created and its properties (`Uuid`, `Kind`) round-trip cleanly.

All of the above are achievable as metadata+property tests without requiring a live CallKit provider registration (which isn't available in the test app harness).

## Action Items

| # | Dimension | Finding | Recommendation | Effort | Value |
|---|---|---|---|---|---|
| A1 | Coverage | DuplicateSignature on `conversationManager(_:didActivate:)` and `conversationManager(_:didDeactivate:)` — only one `(ConversationManager, AVAudioSession)` overload survives; audio-session lifecycle is indistinguishable | Generator: preserve Swift argument label as method-name suffix when a C# overload collision exists (e.g. `ConversationManagerDidActivateAudioSession` / `ConversationManagerDidDeactivateAudioSession`) | Medium | High |
| A2 | Coverage | `ConversationManager.pendingConversationActions` skipped as UnsupportedSignature (placeholder type) | Investigate the concrete return type in the symbol graph; if `[ConversationAction]`, add a concrete-array special-case for this placeholder | Medium | Medium |
| A3 | C# Quality | `IConversationManagerDelegate` overloads all named `ConversationManager(…)` — no semantic clue without reading the parameter type | Document in package README/GUIDE; consider label-preservation as a future generator feature | Low | Medium |
| A4 | Tests | All 18 tests are metadata-only — no round-trip values, no async calls, no delegate exercise | Add 6 targeted tests per §3 above; ConversationManager ctor + Delegate round-trip are highest priority | Low | High |
| A5 | Coverage | `ConversationHistoryManager.recentConversations` blocked by Foundation.Predicate SwiftUIConstraint | Decouple Foundation.Predicate from SwiftUI module gate | Medium | Low |
