// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Regression fixture for the parent-only ASYNC CSM gap. This is the async
// sibling of `PatParentOnlyMethods.swift`: both pin a generic struct with PAT
// constraints whose instance methods have ZERO method-own generic parameters,
// the shape that `MusicLibraryRequest<T>.response()` exercises in MusicKit.
//
// Before the fix, the async CSM path hard-rejected generic parents at three sites:
//   1. `PassesAsyncMethodLevelGuards` — `parentTypeDecl.IsGeneric` blanket
//      rejection
//   2. `EmitConcreteSpecializationsForGenericParent` — `if (method.IsAsync)
//      continue` skip inside the per-conformer extension emission loop
//   3. `IsCsmAsyncEligible` — `ownParamCount == 0` rejection (no method-own
//      generics to specialize)
//
// All three lift in lockstep, scoped to parent-only async (no method-own
// generics), via a new `IsCsmAsyncEligibleForGenericParent` predicate
// (mirrors `IsCsmSyncEligibleForGenericParent`) plus a narrow
// hand-rolled async emission site inside the existing per-conformer
// `*CsmExtensions` class. The async harness lives inline so the return-type
// substitution (`Item.Response` → `StringResponse`) closes BEFORE the
// `[UnmanagedCallersOnly]` callback signature renders.
//
// `AsyncBagItem` is registered in `specialization-hints.json` with both
// `MockStringItem` and `MockIntItem` (plus their `Response` associated-type
// resolutions) so the engine's parent-baseline resolver finds non-empty
// conformer sets and the cross-conformer separation test has two closed
// instantiations to compare.

public protocol AsyncBagItem {
    associatedtype Response
    static func makeResponse() -> Response
}

/// Closed conformer return type carrying a Swift.String. Frozen + Sendable
/// so the C# binding projects as a frozen-struct class with SafeHandle (the
/// `*IndirectResult` path of the CSM-async return marshal) and the Task
/// completion can capture it across the async boundary without warnings.
public struct StringResponse: Sendable {
    public let s: String
    public init(_ s: String) { self.s = s }
}

/// Same shape as `StringResponse` but with an `Int` payload. Distinct nominal
/// type so the cross-conformer separation test can witness that
/// `AsyncBag<MockStringItem>` and `AsyncBag<MockIntItem>` produce different C# extension
/// methods with different return types (not just different runtime values on
/// the same shared method).
public struct IntResponse: Sendable {
    public let n: Int
    public init(_ n: Int) { self.n = n }
}

public struct MockStringItem: AsyncBagItem {
    public typealias Response = StringResponse
    public init() {}
    public static func makeResponse() -> StringResponse { StringResponse("ok") }
}

public struct MockIntItem: AsyncBagItem {
    public typealias Response = IntResponse
    public init() {}
    public static func makeResponse() -> IntResponse { IntResponse(42) }
}

/// Parent-only ASYNC CSM target. `Bag<Item: AsyncBagItem>` declares two
/// instance methods with no method-own generic parameters; both return the
/// parent's associated type `Item.Response`, which substitutes per conformer
/// (`StringResponse` for `MockStringItem`, `IntResponse` for `MockIntItem`).
///
/// The `where Item.Response: Sendable` clause is required so the `async`
/// boundary can carry the result across actor contexts — without it Swift 6
/// rejects the body with a Sendable warning that strips the wrapper symbol
/// silently.
public struct AsyncBag<Item: AsyncBagItem>: Sendable where Item.Response: Sendable {
    public init() {}

    /// Non-throwing parent-only async method. Forces the per-conformer
    /// success-only async harness emission site.
    public func respond() async -> Item.Response {
        return Item.makeResponse()
    }

    /// Throwing parent-only async method. Same return shape with the
    /// throwing-callback overload added to the per-conformer async harness;
    /// confirms `ThrowingClosureSimplificationEmitter` (constraint #40) does
    /// not accidentally fire on the synthesized async pseudo-method.
    public func tryRespond() async throws -> Item.Response {
        return Item.makeResponse()
    }

    /// Throwing parent-only async method that reports cancellation by throwing
    /// `CancellationError` from inside the async body — WITHOUT depending on a
    /// caller-supplied cancellation token. Returning `Item.Response` keeps it on
    /// the CSM parent-only async path (associated-type return can only emit per
    /// closed conformer). Exercises the error-callback's cancellation
    /// classification: a Swift `CancellationError` must surface as a cancelled
    /// Task, not a faulted one.
    public func cancelRespond() async throws -> Item.Response {
        throw CancellationError()
    }
}

// MARK: - Closed-conformer factories
//
// Mirror `PatParentOnlyMethods.swift` and `PatParentPlainProperties.swift`:
// the C# test path does not depend on a generic constructor surface. The CSM
// async extensions emit on `AsyncBag<MockStringItem>` and `AsyncBag<MockIntItem>`;
// callers obtain instances through these typed factories.

public func makeAsyncBagMockStringItem() -> AsyncBag<MockStringItem> {
    return AsyncBag<MockStringItem>()
}

public func makeAsyncBagMockIntItem() -> AsyncBag<MockIntItem> {
    return AsyncBag<MockIntItem>()
}
