// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// @_cdecl trampolines that let .NET drive ActivityKit Live Activities over a
// C ABI. This is the "tier 1" design: a SINGLE fixed, hand-authored
// ActivityAttributes-conforming type (`DotNetLiveActivityAttributes`) whose
// ContentState carries an opaque JSON blob, plus request/update/end/observe
// trampolines. Because the generic `Activity<DotNetLiveActivityAttributes>` is
// fully concrete *inside* this framework, the Codable/Hashable witnesses are
// synthesised by the Swift compiler at our build time and no protocol-witness
// table ever crosses the C boundary — which is what makes a precompiled,
// non-generated ActivityKit binding possible at all.
//
// Cross-process pairing: at runtime ActivityKit matches a running activity to a
// widget's `ActivityConfiguration` by the attributes type's UNQUALIFIED name
// plus a Codable round-trip — NOT by module-qualified/witness identity. So the
// consumer's WidgetKit extension supplies its OWN copy of this exact type (same
// 15-line source, added to the widget target — Apple's canonical "attributes in
// two targets" pattern) and the pairing still succeeds. The widget never links
// this framework.
//
// Platform gating: ActivityKit exists only on iOS/iPadOS. SBApple is compiled
// for iOS, macOS, Mac Catalyst, and tvOS from a glob of this directory, so this
// file must compile to nothing on the platforms that lack ActivityKit, or those
// slices fail to build. `#if os(iOS) && !targetEnvironment(macCatalyst)` keeps
// the symbols in the iOS device + simulator slices only — the only slices where
// Live Activities run.
//
// Red-team hardening baked in (see the .NET LiveActivity facade for the rest):
//   * id -> Activity registry (no raw Unmanaged pointers handed to .NET) so an
//     update-after-end cannot dereference a dangling pointer (use-after-free).
//   * idempotent end (the registry removal returns nil on a second end for the
//     same id).
//   * a per-handle state watcher self-evicts the registry entry when the system
//     ends an activity OUTSIDE the facade (user swipe-dismiss, staleDate expiry,
//     the system's hours-cap, a push end) — so update-after-external-end reports
//     0 instead of succeeding forever, and the registry cannot grow unboundedly
//     in a long-lived process.
//   * update/end work is chained per handle — each async op awaits the previous
//     one — so content states apply in call order even though the synchronous
//     C caller cannot await. End additionally blocks (bounded) until the end has
//     applied, so a process exiting right after End() does not orphan the
//     OS-side activity.
//   * push-token observation runs in a cancellable Task stored per handle; a
//     replacing observer or end() cancels it, and the task hands its managed
//     context back through a release callback once stopped — so the
//     @convention(c) trampoline can never fire into a freed GCHandle.
// The content-based request/update/end APIs are iOS 16.2+; the attributes type
// is iOS 16.1+. Strings cross as null-terminated UTF-8 (JSON cannot contain an
// embedded NUL, so C strings are lossless here and let the managed side use the
// idiomatic StringMarshalling.Utf8 LibraryImport path). The single returned
// string (a request error message) is Swift-allocated (allocate/deallocate, the
// same pairing every other SBW_*_Free* export uses — never C strdup/free) and
// freed via SBW_LiveActivity_FreeString.

#if os(iOS) && !targetEnvironment(macCatalyst)

import ActivityKit
import Foundation

// MARK: - The single fixed attributes type (shared by app side + widget extension)

/// The one concrete `ActivityAttributes` the .NET ActivityKit binding ships.
/// `name` selects which widget UI to render; `json` carries static attributes;
/// `ContentState.json` carries the updatable state. The consumer's widget
/// extension declares a byte-for-byte copy of this type so ActivityKit can pair
/// the activity to the widget by unqualified type name.
@available(iOS 16.1, *)
public struct DotNetLiveActivityAttributes: ActivityAttributes {
    public struct ContentState: Codable, Hashable {
        public var json: String
        public init(json: String) { self.json = json }
    }

    /// Identifies the activity "kind"; the widget switches on this to pick a UI.
    public var name: String
    /// Static (non-updating) attributes, as a JSON blob.
    public var json: String

    public init(name: String, json: String) {
        self.name = name
        self.json = json
    }
}

// MARK: - Handle registry (id -> Activity), avoids handing raw pointers to .NET

@available(iOS 16.2, *)
final class LiveActivityRegistry {
    static let shared = LiveActivityRegistry()

    private let lock = NSLock()
    private var nextHandle: Int64 = 1
    private var activities: [Int64: Activity<DotNetLiveActivityAttributes>] = [:]
    // Live push-token observer task per handle, so a replacing observer or end()
    // can cancel it. Cancellation lets the task's `for await` loop exit, which runs
    // its `defer` and releases the managed GCHandle context — the use-after-free
    // guard for the @convention(c) token callback.
    private var observers: [Int64: Task<Void, Never>] = [:]
    // Per-handle activityStateUpdates watcher that evicts the entry when the
    // system ends the activity outside the facade. Without it, a swipe-dismissed
    // activity stays registered forever: update keeps reporting success for a
    // card nobody can see, and the strongly-held Activity leaks.
    private var stateWatchers: [Int64: Task<Void, Never>] = [:]
    // Tail of the per-handle serial chain for update/end work. Each chained op
    // awaits the previous tail, so async content states apply in call order;
    // without this, two rapid updates race on the concurrency executor and the
    // OLDER state can land last, sticking on the lock screen.
    private var chains: [Int64: Task<Void, Never>] = [:]

    func add(_ activity: Activity<DotNetLiveActivityAttributes>) -> Int64 {
        lock.lock(); defer { lock.unlock() }
        let handle = nextHandle
        nextHandle += 1
        activities[handle] = activity
        // Self-eviction watcher. Built under the lock like beginObserving's task:
        // the body runs later on the concurrency executor and takes the lock fresh
        // (inside remove), so creating it here cannot deadlock.
        stateWatchers[handle] = Task {
            for await state in activity.activityStateUpdates {
                if state == .ended || state == .dismissed {
                    _ = LiveActivityRegistry.shared.remove(handle)
                    break
                }
            }
        }
        return handle
    }

    /// Atomically validates that `handle` is still live and appends `op` to its
    /// serial update/end chain — lookup and enqueue happen under one lock hold, so
    /// an interleaved remove() cannot let an op slip in after end. Returns false on
    /// an unknown/ended handle (nothing enqueued).
    func enqueueIfLive(
        _ handle: Int64,
        _ op: @escaping (Activity<DotNetLiveActivityAttributes>) async -> Void
    ) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard let activity = activities[handle] else { return false }
        let previous = chains[handle]
        chains[handle] = Task {
            await previous?.value
            await op(activity)
        }
        return true
    }

    /// End-path removal: atomically removes `handle` (idempotent — nil on a second
    /// end), cancels its observer + state watcher, and returns a task that runs
    /// `endOp` AFTER any still-pending chained updates, preserving call order. The
    /// chain bookkeeping is dropped here; nothing can enqueue afterwards because
    /// enqueueIfLive is gated on registry membership under the same lock.
    func removeAndEnqueueEnd(
        _ handle: Int64,
        _ endOp: @escaping (Activity<DotNetLiveActivityAttributes>) async -> Void
    ) -> Task<Void, Never>? {
        lock.lock(); defer { lock.unlock() }
        guard let activity = activities.removeValue(forKey: handle) else { return nil }
        cancelAuxiliaryTasksLocked(handle)
        let previous = chains.removeValue(forKey: handle)
        return Task {
            await previous?.value
            await endOp(activity)
        }
    }

    /// Atomically validates that `handle` is still live and installs its push-token
    /// observer, cancelling any prior one for the same handle. The task is built by
    /// `makeTask` *under the lock*, so an interleaved `remove()` (from end()) either
    /// runs fully before — the handle is absent, we install nothing and return false —
    /// or fully after — its `cancel()` stops the task we just stored. That closes the
    /// get()/install window where an observer could be left attached to an
    /// already-ended handle (never cancelled, its managed context never released).
    /// `makeTask` is invoked synchronously here; the Task body it returns runs later
    /// on the concurrency executor and never re-enters this lock, so building it while
    /// holding the lock cannot deadlock. Returns false on an unknown/ended handle, in
    /// which case no task started and the caller owns freeing the context.
    func beginObserving(
        _ handle: Int64,
        _ makeTask: (Activity<DotNetLiveActivityAttributes>) -> Task<Void, Never>
    ) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard let activity = activities[handle] else { return false }
        observers[handle]?.cancel()
        observers[handle] = makeTask(activity)
        return true
    }

    /// Eviction-path removal (the state watcher saw .ended/.dismissed): drops the
    /// entry and all per-handle tasks. Any updates already chained keep running and
    /// no-op against the ended activity. Idempotent; safe for the watcher to call
    /// about itself (cancelling the current task merely sets its flag).
    func remove(_ handle: Int64) -> Activity<DotNetLiveActivityAttributes>? {
        lock.lock(); defer { lock.unlock() }
        cancelAuxiliaryTasksLocked(handle)
        chains.removeValue(forKey: handle)
        return activities.removeValue(forKey: handle)
    }

    /// Caller must hold `lock`. Cancels + drops the push-token observer (its loop
    /// exits and releases the managed GCHandle context) and the state watcher.
    private func cancelAuxiliaryTasksLocked(_ handle: Int64) {
        observers[handle]?.cancel()
        observers.removeValue(forKey: handle)
        stateWatchers[handle]?.cancel()
        stateWatchers.removeValue(forKey: handle)
    }
}

@inline(__always)
private func cString(_ ptr: UnsafePointer<CChar>?) -> String {
    guard let ptr = ptr else { return "" }
    return String(cString: ptr)
}

/// Decodes an incoming JSON payload pointer ONCE, defaulting nil/empty to "{}" so
/// the widget's Codable decode always has an object to chew on. The single
/// normalization point for all three trampolines.
private func jsonOrEmptyObject(_ ptr: UnsafePointer<CChar>?) -> String {
    let json = cString(ptr)
    return json.isEmpty ? "{}" : json
}

@available(iOS 16.2, *)
private func makeContent(_ json: String) -> ActivityContent<DotNetLiveActivityAttributes.ContentState> {
    ActivityContent(
        state: DotNetLiveActivityAttributes.ContentState(json: json),
        staleDate: nil)
}

/// Copies `s` into a Swift-allocated (UnsafeMutablePointer.allocate),
/// NUL-terminated UTF-8 buffer, released by SBW_LiveActivity_FreeString via
/// deallocate() — the same Swift allocate/deallocate pairing every other
/// SBW_*_Free* export uses. Never C strdup/malloc: mixing the two allocator
/// families across the visually-identical free exports is the exact hazard the
/// generated bindings' error-string convention exists to prevent.
private func swiftAllocatedCString(_ s: String) -> UnsafeMutablePointer<CChar> {
    let utf8 = Array(s.utf8)
    let buffer = UnsafeMutablePointer<CChar>.allocate(capacity: utf8.count + 1)
    for (i, byte) in utf8.enumerated() {
        buffer[i] = CChar(bitPattern: byte)
    }
    buffer[utf8.count] = 0
    return buffer
}

// MARK: - @_cdecl trampolines (the C ABI the .NET LiveActivity facade speaks)

/// Start a Live Activity. Returns a non-zero handle on success, 0 on failure
/// (with a Swift-allocated UTF-8 error string written to `outError`, to be freed
/// via SBW_LiveActivity_FreeString). `usePushToken` is 0/1.
@available(iOS 16.2, *)
@_cdecl("SBW_LiveActivity_Request")
public func SBW_LiveActivity_Request(
    _ namePtr: UnsafePointer<CChar>?,
    _ attrsJsonPtr: UnsafePointer<CChar>?,
    _ stateJsonPtr: UnsafePointer<CChar>?,
    _ usePushToken: Int,
    _ outError: UnsafeMutablePointer<UnsafeMutablePointer<CChar>?>?
) -> Int64 {
    let attributes = DotNetLiveActivityAttributes(
        name: cString(namePtr),
        json: jsonOrEmptyObject(attrsJsonPtr))
    let content = makeContent(jsonOrEmptyObject(stateJsonPtr))

    do {
        let activity = try Activity<DotNetLiveActivityAttributes>.request(
            attributes: attributes,
            content: content,
            pushType: usePushToken != 0 ? .token : nil)
        return LiveActivityRegistry.shared.add(activity)
    } catch {
        outError?.pointee = swiftAllocatedCString("\(error)")
        return 0
    }
}

/// Update an existing activity's content state. Dispatch is asynchronous, but
/// updates are chained per handle so consecutive content states apply in call
/// order (see LiveActivityRegistry.enqueueIfLive). Returns 1 on a known handle,
/// 0 if the handle is unknown / already ended — including an activity the system
/// ended outside the facade, which the state watcher evicts.
@available(iOS 16.2, *)
@_cdecl("SBW_LiveActivity_Update")
public func SBW_LiveActivity_Update(
    _ handle: Int64,
    _ stateJsonPtr: UnsafePointer<CChar>?
) -> Int {
    let content = makeContent(jsonOrEmptyObject(stateJsonPtr))
    let enqueued = LiveActivityRegistry.shared.enqueueIfLive(handle) { activity in
        await activity.update(content)
    }
    return enqueued ? 1 : 0
}

/// End an activity. Removes it from the registry first (idempotent), so a later
/// update on the same handle is a clean no-op rather than a use-after-free. The
/// end itself is chained after any still-pending updates (call order holds), and
/// this function then blocks — bounded — until the end has applied: End is
/// terminal, and callers (test teardown, app shutdown) reasonably expect the
/// activity to be gone when it returns; pure fire-and-forget would orphan the
/// OS-side activity when the process exits right after. On timeout (a stalled
/// system) the end is still dispatched and ordered; we just stop blocking.
/// `immediate` 0 = `.default` (the system may keep it briefly on screen);
/// `immediate` 1 = `.immediate` (drop now). A nil `stateJsonPtr` ends without a
/// final content update. Returns 1 on a known handle, 0 otherwise.
@available(iOS 16.2, *)
@_cdecl("SBW_LiveActivity_End")
public func SBW_LiveActivity_End(
    _ handle: Int64,
    _ stateJsonPtr: UnsafePointer<CChar>?,
    _ immediate: Int
) -> Int {
    let content: ActivityContent<DotNetLiveActivityAttributes.ContentState>? =
        stateJsonPtr.map { makeContent(jsonOrEmptyObject($0)) }
    let policy: ActivityUIDismissalPolicy = immediate != 0 ? .immediate : .default
    guard let endTask = LiveActivityRegistry.shared.removeAndEnqueueEnd(handle, { activity in
        await activity.end(content, dismissalPolicy: policy)
    }) else { return 0 }

    let done = DispatchSemaphore(value: 0)
    Task {
        await endTask.value
        done.signal()
    }
    _ = done.wait(timeout: .now() + 10)
    return 1
}

/// Observe APNs push-token updates for an activity. The observer runs in a
/// cancellable Task stored in the registry: a second observe on the same handle,
/// or End, cancels the prior task so it stops delivering. Each token is passed to
/// `callback` as a lowercase hex UTF-8 C string with the opaque `context`. When the
/// task finishes — the token stream ended or the task was cancelled — it calls
/// `release(context)` exactly once; after that no further `callback` can fire for
/// that context, which is where the managed side frees its rooted GCHandle. So the
/// @convention(c) trampoline never fires into freed memory. Returns 1 on a known
/// handle, 0 otherwise — and on 0 the managed side owns freeing `context`, since no
/// task started and `release` will never be called. Requires the consumer app to
/// carry the push-notifications capability; without it no tokens arrive but the
/// call is still a safe no-op.
@available(iOS 16.2, *)
@_cdecl("SBW_LiveActivity_ObservePushToken")
public func SBW_LiveActivity_ObservePushToken(
    _ handle: Int64,
    _ context: UnsafeMutableRawPointer?,
    _ callback: @escaping @convention(c) (UnsafeMutableRawPointer?, UnsafePointer<CChar>?) -> Void,
    _ release: @escaping @convention(c) (UnsafeMutableRawPointer?) -> Void
) -> Int {
    // Look up the activity and install the observer atomically — see beginObserving.
    // If the handle is unknown/ended no task starts; we return 0 and the managed side
    // frees `context` (no release callback will fire).
    let installed = LiveActivityRegistry.shared.beginObserving(handle) { activity in
        Task {
            // Runs when the loop exits — token stream finished OR this task was
            // cancelled. After it returns the managed GCHandle behind `context` is
            // freed, so no later callback may reference it.
            defer { release(context) }
            for await tokenData in activity.pushTokenUpdates {
                if Task.isCancelled { break }
                let hex = tokenData.map { String(format: "%02x", $0) }.joined()
                hex.withCString { callback(context, $0) }
            }
        }
    }
    return installed ? 1 : 0
}

/// Whether Live Activities are enabled for this app (the per-app Settings toggle
/// plus the NSSupportsLiveActivities capability). Returns 0/1.
@available(iOS 16.2, *)
@_cdecl("SBW_LiveActivity_AreActivitiesEnabled")
public func SBW_LiveActivity_AreActivitiesEnabled() -> Int {
    return ActivityAuthorizationInfo().areActivitiesEnabled ? 1 : 0
}

/// Free a string returned by SBW_LiveActivity_Request's `outError`. The buffer
/// comes from UnsafeMutablePointer.allocate (swiftAllocatedCString), so
/// deallocate() — not C free() — is the correct release. Safe to call with nil.
@_cdecl("SBW_LiveActivity_FreeString")
public func SBW_LiveActivity_FreeString(_ ptr: UnsafeMutablePointer<CChar>?) {
    ptr?.deallocate()
}

#endif
