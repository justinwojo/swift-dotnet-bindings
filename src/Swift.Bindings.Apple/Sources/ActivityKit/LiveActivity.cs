// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// User-facing .NET surface for ActivityKit Live Activities ("tier 1"). The
// Apple Supplement framework (SBApple.xcframework, built by
// `nuke build-apple-supplement-xcframework`) carries a SINGLE fixed
// `DotNetLiveActivityAttributes` Swift type plus SBW_LiveActivity_* @_cdecl
// trampolines; this class projects those over [LibraryImport]. The attributes
// type is concrete inside SBApple, so no protocol-witness table crosses the
// boundary and no per-app code generation is required.
//
// Activity state is carried as JSON: `attributesJson` is the static attributes,
// `contentStateJson` is the updatable state. The consumer's WidgetKit extension
// decodes those blobs to render the lock-screen + Dynamic Island UI (see the
// wiki for the ~25-line widget template). Cross-process pairing is by the
// attributes type's unqualified name, so the widget supplies its own copy of
// the type — it never links this assembly.
//
// Lifetime: a Live Activity outlives this object. The Swift registry holds the
// underlying Activity strongly, so letting a LiveActivity be garbage-collected
// does NOT end the activity (that is the correct ActivityKit behaviour — an
// order-tracking activity should survive the view model that started it). End
// it explicitly with End(). There is deliberately no finalizer: nothing here
// owns native heap storage that a GC could leak.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace Swift.ActivityKit;

/// <summary>
/// A handle to a running ActivityKit Live Activity, started from .NET via
/// <see cref="Request(string, string, string, bool)"/>. Update its content with
/// <see cref="Update(string)"/> and finish it with
/// <see cref="End(string, bool)"/>. Requires iOS 16.2+, the
/// <c>NSSupportsLiveActivities</c> Info.plist key on the host app, and a
/// WidgetKit extension that declares a matching <c>DotNetLiveActivityAttributes</c>.
/// </summary>
public sealed partial class LiveActivity
{
    private long _handle;

    private LiveActivity(long handle) => _handle = handle;

    // Platform invariant: the only producer of a LiveActivity is Request(), which
    // calls EnsureSupported() before it can return a non-zero handle, and the ctor
    // is private. So a live (_handle != 0) instance can only exist on iOS 16.2+.
    // The instance members below therefore need no EnsureSupported() of their own —
    // on an unsupported target _handle is always 0 and they short-circuit to a safe
    // no-op (false), never reaching a P/Invoke into the absent symbols.

    // Live Activities exist only on iOS/iPadOS 16.2+. SBApple exports the
    // SBW_LiveActivity_* symbols solely in its iOS device + simulator slices (the
    // shim is #if'd out of the macOS, Mac Catalyst, and tvOS slices), so on any
    // other target the P/Invokes below would fail to bind. Fail fast with a clear
    // reason instead — the same runtime-guard pattern SwiftUI.Text uses for its
    // Catalyst-absent symbol.
    private static void EnsureSupported()
    {
        if (OperatingSystem.IsIOS() && !OperatingSystem.IsMacCatalyst()
            && OperatingSystem.IsIOSVersionAtLeast(16, 2))
        {
            return;
        }
        throw new PlatformNotSupportedException(
            "ActivityKit Live Activities are available only on iOS/iPadOS 16.2 or later "
            + "(not on Mac Catalyst, macOS, or tvOS — SBW_LiveActivity_* is not exported there).");
    }

    // The name payload crosses as a null-terminated UTF-8 C string, so an embedded
    // NUL would silently truncate on the Swift side (String(cString:)). Reject it
    // explicitly rather than corrupt the payload. The JSON parameters don't need
    // this guard: RejectInvalidJson rejects a raw NUL as invalid JSON.
    private static void RejectEmbeddedNul(string? value, string paramName)
    {
        if (value is not null && value.Contains('\0'))
        {
            throw new ArgumentException(
                "Value must not contain an embedded NUL character.", paramName);
        }
    }

    // The JSON payloads cross the boundary as opaque strings: the Swift shim stores
    // them in a plain Codable String field, so ActivityKit accepts ANY text and a
    // malformed payload only surfaces as the widget process silently rendering
    // nothing — a failure invisible from .NET. Fail fast here instead. A raw
    // (unescaped) NUL is invalid JSON, so this also subsumes the embedded-NUL
    // truncation hazard of the null-terminated UTF-8 crossing for these parameters.
    private static void RejectInvalidJson(string? json, string paramName)
    {
        if (string.IsNullOrEmpty(json)) return; // defaults to {} in the shim
        JsonValueKind rootKind;
        try
        {
            using var doc = JsonDocument.Parse(json);
            rootKind = doc.RootElement.ValueKind;
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(
                $"Value is not valid JSON ({ex.Message}). A malformed payload would "
                + "start the activity but render nothing in the widget process.",
                paramName);
        }
        if (rootKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                $"Value must be a JSON object (e.g. {{\"key\":\"value\"}}), not {rootKind} — "
                + "the widget extension decodes this payload as an object.",
                paramName);
        }
    }

    /// <summary>
    /// Whether Live Activities are currently enabled for this app — the per-app
    /// Settings → Live Activities toggle combined with the
    /// <c>NSSupportsLiveActivities</c> capability. Always check before
    /// <see cref="Request"/>: a request on a disabled app throws.
    /// </summary>
    public static bool AreActivitiesEnabled
    {
        get
        {
            EnsureSupported();
            return SupplementNative.AreActivitiesEnabled() != 0;
        }
    }

    /// <summary>
    /// True until <see cref="End"/> is called on this instance. An activity the
    /// user or system dismissed outside this facade may still report true here —
    /// <see cref="Update"/> returns false in that case.
    /// </summary>
    public bool IsActive => _handle != 0;

    /// <summary>
    /// Starts a Live Activity.
    /// </summary>
    /// <param name="name">Selects which widget UI renders this activity; the
    /// widget switches on it. Must match a case the widget knows.</param>
    /// <param name="attributesJson">Static (non-updating) attributes as a JSON
    /// object string, e.g. <c>{"orderId":"42"}</c>. Defaults to <c>{}</c> when
    /// null/empty; anything else must parse as a JSON object (malformed JSON would
    /// start an activity whose widget silently renders nothing, so it is rejected
    /// here with <see cref="ArgumentException"/>).</param>
    /// <param name="contentStateJson">Initial updatable state as a JSON object
    /// string, e.g. <c>{"status":"preparing"}</c>. Defaults to <c>{}</c>; same
    /// JSON-object validation as <paramref name="attributesJson"/>.</param>
    /// <param name="usePushToken">When true, requests an APNs push token for
    /// server-driven updates (requires the push-notifications capability;
    /// observe the token via <see cref="ObservePushToken"/>).</param>
    /// <returns>A live handle you can <see cref="Update"/> and <see cref="End"/>.</returns>
    /// <exception cref="LiveActivityException">The system refused the request —
    /// the message carries the ActivityKit reason (e.g. activities disabled, the
    /// ~4&#160;KB attributes-too-large budget exceeded, or unsupported target).</exception>
    public static unsafe LiveActivity Request(
        string name,
        string attributesJson = "{}",
        string contentStateJson = "{}",
        bool usePushToken = false)
    {
        EnsureSupported();
        ArgumentNullException.ThrowIfNull(name);
        RejectEmbeddedNul(name, nameof(name));
        RejectInvalidJson(attributesJson, nameof(attributesJson));
        RejectInvalidJson(contentStateJson, nameof(contentStateJson));
        nint errorPtr = 0;
        long handle = SupplementNative.Request(
            name, attributesJson, contentStateJson,
            usePushToken ? 1 : 0, &errorPtr);
        if (handle == 0)
        {
            string message = errorPtr != 0
                ? (Marshal.PtrToStringUTF8(errorPtr) ?? "unknown ActivityKit error")
                : "unknown ActivityKit error";
            if (errorPtr != 0) SupplementNative.FreeString(errorPtr);
            throw new LiveActivityException(message);
        }
        return new LiveActivity(handle);
    }

    /// <summary>
    /// Replaces the activity's updatable content state. The system applies the
    /// update asynchronously, but consecutive updates apply in call order (the
    /// shim chains them per activity). Returns false once the activity has ended —
    /// by <see cref="End"/>, or outside this facade (user dismissal from the lock
    /// screen, staleDate expiry, the system's hours-cap); external ends are
    /// observed asynchronously, so a just-dismissed activity may accept one more
    /// update, which the system ignores.
    /// </summary>
    /// <param name="contentStateJson">The new content state as a JSON object
    /// string. Defaults to <c>{}</c> when null/empty; same JSON-object validation
    /// as <see cref="Request"/>.</param>
    /// <returns>True if the update was dispatched; false if already ended.</returns>
    public bool Update(string contentStateJson)
    {
        if (_handle == 0) return false;
        RejectInvalidJson(contentStateJson, nameof(contentStateJson));
        return SupplementNative.Update(_handle, contentStateJson) != 0;
    }

    /// <summary>
    /// Ends the activity. Idempotent — a second call is a safe no-op. The end is
    /// ordered after any still-pending updates, and this call blocks (bounded)
    /// until the system has applied it, so the activity is actually gone when this
    /// returns and a process exiting right afterwards cannot orphan it. After this
    /// returns the handle is dead and <see cref="Update"/> will no-op.
    /// </summary>
    /// <param name="finalContentStateJson">An optional last content state to
    /// display as the activity ends; null ends without a final update. Same
    /// JSON-object validation as <see cref="Request"/>.</param>
    /// <param name="immediate">When true, removes the activity from the screen
    /// at once (<c>.immediate</c>); when false the system may keep it briefly
    /// (<c>.default</c>).</param>
    /// <returns>True if this call ended a live activity; false if already ended —
    /// including an activity the user or system already dismissed.</returns>
    public bool End(string? finalContentStateJson = null, bool immediate = false)
    {
        RejectInvalidJson(finalContentStateJson, nameof(finalContentStateJson));
        // Claim the handle atomically so two concurrent End() calls can't both
        // dispatch a native end for the same activity. The Swift registry's
        // remove() also cancels any push-token observer, which releases its managed
        // context — so End() no longer frees the callback handle itself.
        long handle = Interlocked.Exchange(ref _handle, 0);
        if (handle == 0) return false;
        return SupplementNative.End(handle, finalContentStateJson, immediate ? 1 : 0) != 0;
    }

    /// <summary>
    /// Observes APNs push-token refreshes for this activity, invoking
    /// <paramref name="onToken"/> with each token as a lowercase hex string.
    /// <paramref name="onToken"/> runs on a background thread (the Swift
    /// concurrency pool) — marshal to the main thread before touching UI. An
    /// exception it throws cannot be propagated (it would cross into native code
    /// and abort the process); it is caught and written to standard error.
    /// Requires the push-notifications capability on the host app and that the
    /// activity was started with <c>usePushToken: true</c>; otherwise no tokens
    /// arrive and this is a harmless no-op. Only one observer per activity — a
    /// second call replaces the first.
    /// </summary>
    /// <returns>True if observation was registered; false if already ended.</returns>
    public unsafe bool ObservePushToken(Action<string> onToken)
    {
        ArgumentNullException.ThrowIfNull(onToken);
        if (_handle == 0) return false;
        // Each observe owns its own context handle. The Swift side cancels any prior
        // observer for this handle and releases ITS context through
        // OnObserverReleasedNative, so we never free a context the Swift task might
        // still touch. We free here only when Swift declined to start a task (an
        // unknown/ended handle), because then no release callback will come.
        var ctx = GCHandle.Alloc(onToken);
        bool ok = SupplementNative.ObservePushToken(
            _handle,
            (void*)GCHandle.ToIntPtr(ctx),
            &OnPushTokenNative,
            &OnObserverReleasedNative) != 0;
        if (!ok) ctx.Free();
        return ok;
    }

    // Static + [UnmanagedCallersOnly] so it marshals to a bare C function pointer
    // on both Mono and NativeAOT (instance delegates can't cross to @convention(c)
    // under AOT). The Swift side passes back the GCHandle we handed it as context.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void OnPushTokenNative(void* context, byte* hexPtr)
    {
        if (context == null || hexPtr == null) return;
        try
        {
            var gch = GCHandle.FromIntPtr((nint)context);
            if (gch.Target is Action<string> cb)
            {
                string hex = Marshal.PtrToStringUTF8((nint)hexPtr) ?? string.Empty;
                cb(hex);
            }
        }
        catch (Exception ex)
        {
            // Nothing may propagate back into @convention(c) — that aborts the
            // process. This catch also covers a throwing consumer callback (an
            // entirely reachable input, unlike the released-context case the
            // handshake prevents), so surface it instead of dropping it silently.
            Console.Error.WriteLine(
                $"Swift.ActivityKit: push-token observer callback threw: {ex}");
        }
    }

    // Swift calls this once its observer task has fully stopped (token stream ended
    // or the task was cancelled), guaranteeing no further OnPushTokenNative for this
    // context. Freeing the rooted callback here — not in End() — is what closes the
    // use-after-free window.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void OnObserverReleasedNative(void* context)
    {
        if (context == null) return;
        try
        {
            var gch = GCHandle.FromIntPtr((nint)context);
            if (gch.IsAllocated) gch.Free();
        }
        catch
        {
            // Symmetry with OnPushTokenNative: the release handshake fires this
            // exactly once per started observer, with the still-valid handle we
            // handed Swift, so FromIntPtr cannot throw in practice — but stay
            // defensive rather than let an exception cross back into @convention(c).
        }
    }

    /// <summary>
    /// P/Invoke surface for the Apple Supplement shim framework. Cdecl + plain
    /// scalars / UTF-8 C strings — no CallConvSwift — so Mono and NativeAOT take
    /// identical fast paths. The framework ships in this NuGet at
    /// <c>runtimes/native/SBApple.xcframework/</c> and is resolved at
    /// LoadLibrary time by SwiftFrameworkResolver's
    /// <c>@rpath/{name}.framework/{name}</c> rule. Swift <c>Int</c> is
    /// pointer-width, hence <c>nint</c>; the activity handle is a Swift
    /// <c>Int64</c>, hence <c>long</c>.
    /// </summary>
    private static partial class SupplementNative
    {
        private const string Library = "SBApple";

        [LibraryImport(Library, EntryPoint = "SBW_LiveActivity_Request",
            StringMarshalling = StringMarshalling.Utf8)]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        public static unsafe partial long Request(
            string name, string attrsJson, string stateJson, nint usePushToken, nint* outError);

        [LibraryImport(Library, EntryPoint = "SBW_LiveActivity_Update",
            StringMarshalling = StringMarshalling.Utf8)]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        public static partial nint Update(long handle, string stateJson);

        [LibraryImport(Library, EntryPoint = "SBW_LiveActivity_End",
            StringMarshalling = StringMarshalling.Utf8)]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        public static partial nint End(long handle, string? stateJson, nint immediate);

        [LibraryImport(Library, EntryPoint = "SBW_LiveActivity_ObservePushToken")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        public static unsafe partial nint ObservePushToken(
            long handle, void* context,
            delegate* unmanaged[Cdecl]<void*, byte*, void> callback,
            delegate* unmanaged[Cdecl]<void*, void> release);

        [LibraryImport(Library, EntryPoint = "SBW_LiveActivity_AreActivitiesEnabled")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        public static partial nint AreActivitiesEnabled();

        [LibraryImport(Library, EntryPoint = "SBW_LiveActivity_FreeString")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        public static partial void FreeString(nint ptr);
    }
}

/// <summary>
/// Thrown when ActivityKit refuses to start a Live Activity. The
/// <see cref="Exception.Message"/> carries the underlying reason as reported by
/// the system (for example: Live Activities disabled in Settings, the
/// attributes payload exceeding the ~4&#160;KB budget, or an unsupported target).
/// </summary>
public sealed class LiveActivityException : Exception
{
    /// <summary>Creates the exception with the system-reported failure reason.</summary>
    public LiveActivityException(string message) : base(message) { }
}
