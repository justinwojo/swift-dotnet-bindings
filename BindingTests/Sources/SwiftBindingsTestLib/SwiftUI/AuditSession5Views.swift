// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// SwiftUI Views targeting audit-session-5 P0/P1 defect paths:
//
//   ArrayEnumView          (P0-03) BridgeArray with BoundEnum element
//   EnumModifierView       (P0-03) self-returning modifier with Optional<BoundEnum>
//   UrlParamView           (P0-04) ObjC-bridgeable struct (URL) param Create/Update
//   OptionalUrlParamView   (P0-04) Optional<ObjC-bridgeable struct> Create/Update
//   UrlResultView          (P1-19) Result<URL, ScanError> closure (ObjC-bridgeable success branch)
//   UrlClosureView         (review) typed (URL)->Void closure — ObjC-bridgeable struct arg
//   FrozenRefClosureView   (P1-20) closure arg is @frozen struct with ref-holding field
//   UserDataAsyncView      (P0-13) async View whose user param name collides with the
//                                  synthetic trailing `userData` param of async Create
//   HandleParamView        (P1-22) init params named `handle`/`session` collide with
//                                  generated C# locals in the Create factory

// SwiftUI types (View, Text, etc.) are not accessible in the Mac Catalyst
// compiler environment despite the module importing successfully.
#if !targetEnvironment(macCatalyst)
import SwiftUI
import Foundation

// MARK: - P0-03 Array of enum

/// Tests BridgeArray with BoundEnum element type.
/// C# bridge encodes as Int32 pointer+count; Swift reconstructs via
/// UnsafeBufferPointer.map { AlertStyle(rawValue: $0)! }.
public struct ArrayEnumView: View {
    public let styles: [AlertStyle]

    public init(styles: [AlertStyle]) {
        self.styles = styles
    }

    public var body: some View {
        Text("count: \(styles.count)")
    }
}

// MARK: - P0-03 Enum modifier with Optional<BoundEnum>

/// Tests self-returning modifier whose parameter is Optional<BoundEnum>.
/// Covers the modifier-Set Optional-enum path in the bridge modifier emitter.
public struct EnumModifierView: View {
    public let title: String

    public init(title: String) {
        self.title = title
    }

    /// Self-returning modifier taking Optional<AlertStyle>.
    /// Bridge emits SetStyled(handle, hasValue, rawValue) updating modifier state.
    public func styled(_ style: AlertStyle?) -> Self { return self }

    public var body: some View {
        Text(title)
    }
}

// MARK: - P0-04 ObjC-bridgeable struct param (URL)

/// Tests Create + Update for a plain ObjC-bridgeable struct parameter (Foundation.URL).
/// Bridge encodes URL as its absoluteString UTF-8 bytes and reconstructs via URL(string:).
public struct UrlParamView: View {
    public let target: URL

    public init(target: URL) {
        self.target = target
    }

    public var body: some View {
        Text(target.absoluteString)
    }
}

// MARK: - P0-04 Optional ObjC-bridgeable struct param (URL?)

/// Tests Create + Update for Optional<ObjC-bridgeable struct> (URL?).
/// Bridge encodes as hasValue (Int32) + UTF-8 bytes; nil encoded as hasValue=0.
public struct OptionalUrlParamView: View {
    public let target: URL?

    public init(target: URL? = nil) {
        self.target = target
    }

    public var body: some View {
        Text(target?.absoluteString ?? "nil")
    }
}

// MARK: - P1-19 Result<URL, ScanError> closure

/// Tests Result closure decomposition where the SUCCESS payload is Foundation.URL — an
/// ObjC-bridgeable struct (URL→NSURL) that crosses the callback ABI as an object pointer.
/// This is the P1-19 surface: the success branch must bind the bridged temporary to a local
/// and `withExtendedLifetime` it across the synchronous C# callback, or ARC can release the
/// `value as AnyObject` temporary before the callback dereferences it (use-after-free).
/// The error branch reuses the existing class-based `ScanError` (a plain BoundType) so the
/// fixture isolates the ObjC-bridgeable SUCCESS path.
///
/// URL (not Data) is the success type because URL is registered as a non-frozen,
/// memory-managed, ObjC-bridgeable struct and therefore resolves to a BoundStruct with
/// IsObjCBridgeable=true — the exact classification the P1-19 `withExtendedLifetime`
/// branch keys off. Foundation.Data is mis-registered as frozen+blittable in
/// FoundationDatabase.xml (see REMEDIATION-PLAN §6) and so never reaches that branch.
///
/// Trigger mechanism: explicit @_cdecl test-helper invocation (same as FormatMenuView,
/// ResultCompletionView, TypedClosureView, etc.). The body does NOT fire the closure
/// because @ViewBuilder blocks do not accept standalone Void expressions; the Swift
/// wrapper stores the decomposed onSuccess/onError C callbacks and an @_cdecl
/// BridgeTestHelpers function calls them with a synthetic URL/error value.
public struct UrlResultView: View {
    public let onResult: (Result<URL, ScanError>) -> Void

    public init(onResult: @escaping (Result<URL, ScanError>) -> Void) {
        self.onResult = onResult
    }

    public var body: some View {
        Text("UrlResult")
    }
}

// MARK: - Typed closure with ObjC-bridgeable struct arg (URL)

/// Tests a plain typed closure `(URL) -> Void` whose single argument is an ObjC-bridgeable
/// struct (URL→NSURL). This is the TypedClosure analogue of the P1-19 Result success branch
/// (surfaced by the Codex/Grok review of session 5): the generated decomposition closure must
/// deliver the bridged NSURL as an *object pointer* (Unmanaged.passUnretained, held alive by
/// withExtendedLifetime across the synchronous callback), and the C# trampoline must read it
/// via GetNSObject — NOT heap-allocate the raw URL struct bytes and read them via
/// MarshalFromSwift (assumingMemoryBound), which reinterprets an object pointer as struct
/// memory → type confusion / SIGSEGV (the same shape as P0-04/P1-19).
///
/// Trigger mechanism: explicit @_cdecl test-helper invocation (same as UrlResultView). Firing
/// `rootView.onPick(url)` runs the full pipeline Swift closure → C trampoline → managed Action.
public struct UrlClosureView: View {
    public let onPick: (URL) -> Void

    public init(onPick: @escaping (URL) -> Void) {
        self.onPick = onPick
    }

    public var body: some View {
        Text("UrlClosure")
    }
}

// MARK: - P1-20 @frozen struct with ref-holding field as closure arg

/// Tests closure whose argument is a @frozen struct that contains a String field
/// (String is a reference-holding value type → ClassWithBufferStruct in the bridge).
/// Trigger mechanism: explicit @_cdecl test-helper invocation (same pattern as
/// FormatMenuView/ResultCompletionView). The body does NOT fire the closure for the
/// same @ViewBuilder / Void-expression reason noted on DataResultView above.
public struct FrozenRefClosureView: View {
    public let onEvent: (FrozenRefArg) -> Void

    public init(onEvent: @escaping (FrozenRefArg) -> Void) {
        self.onEvent = onEvent
    }

    public var body: some View {
        Text("FrozenRefClosure")
    }
}

// MARK: - P0-13 Async View with colliding trailing userData param

/// Tests the async init pattern where the user supplies params literally named
/// `userData` and `onError` — the same names the async Create bridge appends as
/// synthetic trailing parameters (the opaque state pointer and the error callback).
/// The bridge must rename the synthetic params so the two do not collide in the
/// generated Swift `@_cdecl` signature or the C# P/Invoke `extern` declaration.
///
/// The colliding params are deliberately `Int32` (a primitive), NOT `String`: a
/// String flattens to `userDataPtr`/`userDataLen` (and likewise for `onError`),
/// which would NOT collide with the bare `userData`/`onError` trailing params. Only
/// a primitive leaf param emits the bare name on both surfaces, so a primitive is
/// what actually reproduces the duplicate-declaration defect.
///
/// Note: async Views in this repo use an async *dependency class* pattern
/// (AsyncService, Processor) rather than a directly-async init on the View itself.
/// A View init declared `async` is not currently reachable from the sync SwiftUI
/// framework calling site, so we model this as a View that takes an `AsyncService`
/// dependency whose *own* init is `async throws` — the init chain inference then
/// makes the Create factory async. The user params `userData`/`onError` on the View
/// init are what trigger the trailing-param collisions.
public struct UserDataAsyncView: View {
    public let userData: Int32
    public let onError: Int32
    public let service: AsyncService

    public init(userData: Int32, onError: Int32, service: AsyncService) {
        self.userData = userData
        self.onError = onError
        self.service = service
    }

    public var body: some View {
        Text("\(userData)-\(onError)")
    }
}

// MARK: - P1-22 Init params colliding with synthetic C# locals

/// Tests that the emitter does not produce duplicate C# local variable names when
/// the View's init param names (`handle`, `session`) happen to match the synthetic
/// local variables the generated Create factory introduces for its own bookkeeping.
/// Body is intentionally simple — the fixture value is in the init signature shape.
public struct HandleParamView: View {
    public let handle: Int32
    public let session: Int32

    public init(handle: Int32, session: Int32) {
        self.handle = handle
        self.session = session
    }

    public var body: some View {
        Text("\(handle)-\(session)")
    }
}

#endif
