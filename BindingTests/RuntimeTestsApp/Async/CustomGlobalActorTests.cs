// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// End-to-end coverage for custom-global-actor-isolated types.
///
/// A class annotated with @&lt;Actor&gt; (e.g., <c>@BindingsTestGlobalActor</c>) inherits
/// global-actor isolation on every member, including inits. Swift 6 has no synchronous
/// entry into a custom global actor's isolation domain — `&lt;Actor&gt;.shared.assumeIsolated`
/// propagates *instance*-actor isolation, a different domain than @&lt;Actor&gt; *global-actor*
/// isolation, and a direct CallConvSwift call to the Swift-native init crashes on
/// NativeAOT because the actor contract isn't established across the foreign-runtime
/// boundary. The synchronous `new T(...)` projection therefore stays skipped under
/// SWIFTBIND022.
///
/// The binding generator instead emits `static Task&lt;T&gt; CreateAsync(...)`. The Swift
/// wrapper schedules `Task { try await Type.init(...) }` and the implicit actor hop at
/// the await lands the init on the actor's executor. The C# side never crosses the
/// actor boundary directly — it only invokes a `@_cdecl` wrapper symbol and receives a
/// Cdecl callback when the Task completes (NativeAOT-safe).
/// </summary>
public class CustomGlobalActorTests : TestBase
{
    public CustomGlobalActorTests(TestResults results) : base(results) { }

    /// <summary>
    /// The class type itself must appear in the bindings — the marker-protocol metadata
    /// gap historically dropped non-init members alongside the constructor skip. This
    /// guards against regressions in marker-protocol conformance resolution.
    /// </summary>
    public void TestGlobalActorIsolatedClass_TypeIsGenerated()
    {
        var type = typeof(GlobalActorIsolatedClass);
        AssertNotNull(type, "GlobalActorIsolatedClass should be present in generated bindings");
    }

    /// <summary>
    /// SWIFTBIND022 still wholesale-skips synchronous `new T(...)` projection for
    /// constructors on custom-global-actor-isolated parents. The C# binding therefore
    /// must NOT declare any public synchronous constructors of its own — only the
    /// implicit/default ones inherited from the SwiftObject base. This guards against
    /// accidental regressions that would re-enable a broken CallConvSwift path. The
    /// async-factory path is exposed via <c>CreateAsync</c> — see
    /// <see cref="TestGlobalActorIsolatedClass_CreateAsyncRoundTrips"/>.
    /// </summary>
    public void TestGlobalActorIsolatedClass_SyncConstructorIsSkipped()
    {
        var declaredCtors = typeof(GlobalActorIsolatedClass)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.DeclaringType == typeof(GlobalActorIsolatedClass))
            .ToArray();

        AssertEqual(0, declaredCtors.Length,
            "SWIFTBIND022 still wholesale-skips synchronous constructors on custom-global-actor-isolated " +
            $"types; expected zero declared sync ctors on GlobalActorIsolatedClass, found {declaredCtors.Length}.");
    }

    /// <summary>
    /// Async-factory projection must exist as <c>static Task&lt;T&gt; CreateAsync(...)</c>.
    /// Unlike the sync ctor (skipped under SWIFTBIND022), this is callable from any
    /// thread because the Swift wrapper schedules the init in a Task and the implicit
    /// actor hop at `await` lands the init on the actor's executor.
    /// </summary>
    public void TestGlobalActorIsolatedClass_CreateAsyncFactoryEmitted()
    {
        var type = typeof(GlobalActorIsolatedClass);
        var createAsync = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "CreateAsync");
        AssertNotNull(createAsync,
            "GlobalActorIsolatedClass.CreateAsync must be emitted — the actor-isolated init is " +
            "surfaced as a static async factory rather than a sync constructor.");

        // Return type is Task<GlobalActorIsolatedClass>
        var returnType = createAsync!.ReturnType;
        AssertTrue(returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>),
            $"CreateAsync return type should be Task<T>, found {returnType}");
        AssertEqual(typeof(GlobalActorIsolatedClass), returnType.GetGenericArguments()[0],
            "CreateAsync return type argument should be GlobalActorIsolatedClass");
    }

    /// <summary>
    /// End-to-end factory round-trip: construct an instance via CreateAsync, then read
    /// stored properties and call an actor-isolated method. Validates that the Swift
    /// wrapper actually executes the init on the actor's executor and returns a usable
    /// retained instance pointer.
    /// </summary>
    public async Task TestGlobalActorIsolatedClass_CreateAsyncRoundTrips()
    {
        var instance = await WithTimeout(
            GlobalActorIsolatedClass.CreateAsync("hello", 7),
            DefaultAsyncTimeout);

        AssertNotNull(instance, "CreateAsync must return a non-null instance");

        AssertEqual("hello", instance.Label.ToString(), "label was not preserved across the actor boundary");
        AssertEqual(7, instance.Count, "count was not preserved across the actor boundary");

        // The describe() method is itself actor-isolated → exposed as GetDescribeAsync.
        // Calling it confirms the returned instance is a fully wired ISwiftObject and
        // its method dispatch lands on the actor's executor without crashing.
        var description = await WithTimeout(instance.GetDescribeAsync(), DefaultAsyncTimeout);
        AssertEqual("hello:7", description, "GetDescribeAsync should observe the constructor's stored state");

        instance.Dispose();
    }

    /// <summary>
    /// Throwing async-factory: success path. The Swift init succeeds and the Task
    /// resolves to a usable instance. Bridges the same plumbing as the non-throwing
    /// case but exercises the throws-aware Swift wrapper.
    /// </summary>
    public async Task TestGlobalActorIsolatedThrowingClass_CreateAsyncSuccess()
    {
        var instance = await WithTimeout(
            GlobalActorIsolatedThrowingClass.CreateAsync(value: 42, negative: false),
            DefaultAsyncTimeout);
        AssertNotNull(instance, "Throwing async-factory should return a non-null instance on the success path");
        AssertEqual(42, instance.Value, "value was not preserved across the actor boundary");
        instance.Dispose();
    }

    /// <summary>
    /// Throwing async-factory: failure path. The Swift init throws an NSError; the
    /// async harness routes the error string into a faulted Task whose exception
    /// message carries `String(describing: error)`. Validates that the throw doesn't
    /// crash the runtime, the Task transitions to Faulted state, and the message is
    /// propagated.
    /// </summary>
    public async Task TestGlobalActorIsolatedThrowingClass_CreateAsyncFault()
    {
        Exception? captured = null;
        try
        {
            // value=-1 with negative=true triggers the Swift-side throw.
            // (The Swift external label is `failIf`; the C# emitter projects the internal
            // name `negative` from `failIf negative: Bool`.)
            await WithTimeout(
                GlobalActorIsolatedThrowingClass.CreateAsync(value: -1, negative: true),
                DefaultAsyncTimeout);
        }
        catch (Exception ex)
        {
            captured = ex;
        }

        AssertNotNull(captured, "Throwing async-factory should fault when the Swift init throws");
        // Surface the Swift error description back through to C# — the exact substring is
        // the NSError's localizedDescription. The harness wraps with `SwiftException` whose
        // Message contains the Swift `String(describing: error)` text.
        AssertTrue(
            captured!.Message.Contains("negative value rejected"),
            $"Expected exception message to contain 'negative value rejected', got: {captured.Message}");
    }

    /// <summary>
    /// Cancellation: cancelling the token before the Task completes must surface as an
    /// OperationCanceledException (or wrapper). The Swift side cooperates via
    /// `_sbwUnregisterTask` cleanup; the C# side observes the cancellation through the
    /// CancellationToken plumbing on CreateAsync.
    /// </summary>
    public async Task TestGlobalActorIsolatedClass_CreateAsyncRespectsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Exception? captured = null;
        try
        {
            await WithTimeout(
                GlobalActorIsolatedClass.CreateAsync("cancelled", 0, cts.Token),
                DefaultAsyncTimeout);
        }
        catch (Exception ex)
        {
            captured = ex;
        }

        AssertNotNull(captured, "CreateAsync called with a pre-cancelled token must throw");
        AssertTrue(
            captured is OperationCanceledException || captured!.GetType().Name == "TaskCanceledException",
            $"Expected OperationCanceledException, got {captured!.GetType().FullName}: {captured.Message}");
    }

    /// <summary>
    /// Default-parameter overload coverage for actor-isolated async constructors. The
    /// Swift init `(label: String, config: GlobalActorConfig = GlobalActorConfig())`
    /// has a trailing default that isn't C#-mappable (the default expression is a Swift
    /// initializer call, not a literal). The DefaultParameterOverloadEmitter would
    /// historically have skipped this — its actor-isolated guard mirrored MethodHandler's
    /// wholesale SWIFTBIND022 skip — but the async-factory pipeline now legally accepts
    /// trimmed overloads via `extension Type { static func _dbw_*(...) async }`. The
    /// guard relaxation enables that here, so consumers can call `CreateAsync(label)`
    /// without constructing a `GlobalActorConfig` themselves.
    /// </summary>
    public void TestGlobalActorIsolatedDefaultArgClass_TrimmedCreateAsyncEmitted()
    {
        var createAsyncOverloads = typeof(GlobalActorIsolatedDefaultArgClass)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "CreateAsync")
            .ToArray();

        AssertTrue(createAsyncOverloads.Length >= 2,
            "Expected at least 2 CreateAsync overloads (primary + trimmed) on " +
            $"GlobalActorIsolatedDefaultArgClass; found {createAsyncOverloads.Length}. " +
            "If only the primary is present, the DefaultParameterOverloadEmitter is " +
            "still skipping default-arg overloads on actor-isolated async constructors.");

        // Trimmed overload: takes label + CancellationToken (no GlobalActorConfig param).
        var trimmed = createAsyncOverloads.FirstOrDefault(m =>
        {
            var ps = m.GetParameters();
            return ps.Length == 2
                && ps[0].ParameterType == typeof(string)
                && ps[1].ParameterType == typeof(CancellationToken);
        });
        AssertNotNull(trimmed,
            "Trimmed CreateAsync(string label, CancellationToken) overload should exist " +
            "when GlobalActorConfig has a non-C#-mappable default. The trimmed overload " +
            "lets Swift fill in `config: GlobalActorConfig()` at the call site.");
    }

    /// <summary>
    /// End-to-end round-trip through the trimmed async-factory overload. The Swift wrapper
    /// schedules `Task { try await GlobalActorIsolatedDefaultArgClass(label: label) }`,
    /// Swift fills in `config: GlobalActorConfig()` whose `depth` defaults to 7, the
    /// constructor runs on the actor's executor, and the resulting instance reaches C#.
    /// Reading `Depth == 7` confirms the Swift-side default was applied.
    /// </summary>
    public async Task TestGlobalActorIsolatedDefaultArgClass_TrimmedCreateAsyncRoundTrips()
    {
        var instance = await WithTimeout(
            GlobalActorIsolatedDefaultArgClass.CreateAsync("hello"),
            DefaultAsyncTimeout);

        AssertNotNull(instance, "Trimmed CreateAsync should return a non-null instance");
        AssertEqual("hello", instance.Label.ToString(),
            "label was not preserved across the actor boundary on the trimmed overload");
        AssertEqual(7, instance.Depth,
            "depth should reflect the Swift-side default GlobalActorConfig().depth = 7 — " +
            "if this is 0 or another value, the trimmed overload isn't routing through the " +
            "default-arg-trimming Swift wrapper.");

        instance.Dispose();
    }

    /// <summary>
    /// Re-entrancy check: when an async-factory continuation immediately schedules another
    /// CreateAsync, the second call must complete without deadlocking. This exercises the
    /// `RunContinuationsAsynchronously` flag on the underlying TaskCompletionSource — if
    /// the continuation ran inline on the Swift callback thread, a follow-up CreateAsync
    /// could starve the runtime by issuing a Task hop while the Swift callback thread is
    /// still pinned awaiting completion. Both calls share the same actor's executor, so a
    /// stuck callback thread would block both Tasks indefinitely.
    /// </summary>
    public async Task TestGlobalActorIsolatedClass_AwaitContinuationCanReenter()
    {
        var first = await WithTimeout(
            GlobalActorIsolatedClass.CreateAsync("first", 1),
            DefaultAsyncTimeout);
        AssertNotNull(first, "first CreateAsync must return a non-null instance");

        // The continuation runs after the first await. A second CreateAsync from inside
        // that continuation must complete cleanly — if the first TCS continuation ran
        // inline on the Swift callback thread, this would deadlock or starve.
        var second = await WithTimeout(
            GlobalActorIsolatedClass.CreateAsync("second", 2),
            DefaultAsyncTimeout);
        AssertNotNull(second, "second CreateAsync (in a continuation) must return a non-null instance");

        AssertEqual("first", first.Label.ToString(), "first instance label corrupted across the await");
        AssertEqual("second", second.Label.ToString(), "second instance label corrupted across the await");

        first.Dispose();
        second.Dispose();
    }

    /// <summary>
    /// Custom-global-actor types declare conformance to the four compile-time marker
    /// protocols (Sendable / Copyable / Escapable / SendableMetatype) plus
    /// _Concurrency.Actor in their ABI conformance arrays, and they reference
    /// _Concurrency.UnownedSerialExecutor through the implicit unownedExecutor accessor.
    /// Before the typedb stubs landed, none of those names had TypeRecords, so
    /// MemberEmissionValidator dropped the non-init members (describe(), label, count)
    /// along with the constructor — leaving the class as a nominal-only shell.
    ///
    /// This regression guard asserts every declared non-init member survives emission.
    /// If the marker protocols revert to AnyType-fallback or _Concurrency.UnownedSerialExecutor
    /// disappears from _ConcurrencyDatabase.xml, the count below drops and this fails
    /// before the constructor-skip blame propagates downstream.
    /// </summary>
    public void TestGlobalActorIsolatedClass_NonInitMembersReachable()
    {
        var type = typeof(GlobalActorIsolatedClass);

        // describe() — instance method on a custom-global-actor-isolated class. The
        // actor isolation forces async wrapping, so the method emits as
        // GetDescribeAsync(CancellationToken). What this guards: the method must exist
        // regardless of how the actor isolation is surfaced. If the marker conformance
        // metadata regressed, MemberEmissionValidator would drop the method along with
        // the constructor and only the type shell would remain.
        var describeAsync = type.GetMethod("GetDescribeAsync", BindingFlags.Public | BindingFlags.Instance);
        AssertNotNull(describeAsync,
            "GlobalActorIsolatedClass.GetDescribeAsync must survive emission — its disappearance " +
            "indicates the marker-protocol conformance metadata is no longer resolving and the " +
            "method is being tombstoned by a CanEmitMethod failure.");

        // label / count — stored properties whose getters are implicitly actor-isolated.
        // The emitter projects them as plain `string Label` / `int Count` (the getter
        // body is a direct memory read, not an actor hop), so PascalCase property
        // lookup is sufficient.
        var label = type.GetProperty("Label", BindingFlags.Public | BindingFlags.Instance);
        var count = type.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
        AssertNotNull(label, "GlobalActorIsolatedClass.label getter must survive emission.");
        AssertNotNull(count, "GlobalActorIsolatedClass.count getter must survive emission.");
    }

    /// <summary>
    /// `nonisolated public init` on a @&lt;CustomActor&gt;-isolated class must reach C#
    /// as a synchronous public constructor — NOT as a `static Task&lt;T&gt; CreateAsync(...)`.
    /// The `nonisolated` modifier opts the init out of the actor's isolation domain,
    /// so the binding generator must emit a sync `@_cdecl` wrapper for it (gate 6b in
    /// WrapperValidation honors `IsNonisolated`). Without this gate, every
    /// synchronous constructor on a custom-actor-isolated type is unreachable from C#.
    /// </summary>
    public void TestNonisolatedInitOnCustomActor_SyncConstructorEmitted()
    {
        var declaredCtors = typeof(NonisolatedInitOnCustomActor)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.DeclaringType == typeof(NonisolatedInitOnCustomActor))
            .ToArray();

        AssertTrue(declaredCtors.Length >= 2,
            "NonisolatedInitOnCustomActor declares two `nonisolated public init` overloads " +
            "(one taking a label, one parameter-less). Both must reach C# as synchronous " +
            $"public constructors; found {declaredCtors.Length} declared ctor(s). If this drops " +
            "below 2, gate 6b in WrapperValidation has regressed and is wholesale-skipping the " +
            "ctor under SWIFTBIND022 even though the parser tagged it `IsNonisolated`.");
    }

    /// <summary>
    /// End-to-end round-trip: synchronous `new NonisolatedInitOnCustomActor("hello")`
    /// must construct an instance and the `nonisolated let label` getter must return
    /// the value the ctor was called with. Validates the @_cdecl wrapper actually runs
    /// the init (no actor hop) and the stored property getter reads back through the
    /// foreign-runtime boundary correctly.
    /// </summary>
    public void TestNonisolatedInitOnCustomActor_SyncConstructorRoundTrips()
    {
        using var instance = new NonisolatedInitOnCustomActor("hello");
        AssertNotNull(instance, "Synchronous nonisolated ctor must return a non-null instance");
        AssertEqual("hello", instance.Label.ToString(), "label was not preserved across the ctor wrapper");

        using var defaulted = new NonisolatedInitOnCustomActor();
        AssertEqual("default", defaulted.Label.ToString(), "parameter-less nonisolated init must apply Swift-side default");
    }

    /// <summary>
    /// A `nonisolated` init on a custom-global-actor class taking a non-trivial config
    /// struct (with a non-C#-mappable Swift-side default) AND an optional existential
    /// delegate (with default `nil`). The synchronous public ctor must emit so consumers
    /// can call it directly from C#.
    /// </summary>
    public void TestPipelineLikeNonisolatedInit_SyncConstructorEmitted()
    {
        var declaredCtors = typeof(PipelineLikeNonisolatedInit)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.DeclaringType == typeof(PipelineLikeNonisolatedInit))
            .ToArray();

        AssertTrue(declaredCtors.Length >= 1,
            "PipelineLikeNonisolatedInit (nonisolated init on a custom-global-actor class) must emit at least one " +
            $"synchronous public constructor; found {declaredCtors.Length}. Without this, the " +
            "gate-6b nonisolated bypass regressed against the exact pattern that drove it.");
    }

    /// <summary>
    /// Round-trip through the nonisolated ctor with the `delegate: nil` default: the
    /// stored `delegateDescription` should resolve to "none" (Swift-side `?? "none"`),
    /// and `depth` should reflect either the explicitly-passed `NonisolatedInitConfig`
    /// or the Swift-side default (depth=11) when the trimmed overload is used. We use
    /// the explicit-config path here because the trimmed/default-arg overload's
    /// availability depends on a separate emitter pipeline; this test focuses on the
    /// gate-6b nonisolated bypass, not on default-arg trimming.
    /// </summary>
    public void TestPipelineLikeNonisolatedInit_SyncConstructorRoundTrips()
    {
        using var config = new NonisolatedInitConfig(13);
        using var instance = new PipelineLikeNonisolatedInit(config, null);
        AssertNotNull(instance, "Nonisolated ctor on custom-global-actor class must return a non-null instance");
        AssertEqual(13, instance.Depth, "config.depth was not preserved across the ctor wrapper");
        AssertEqual("none", instance.DelegateDescription.ToString(),
            "delegate=nil path should resolve `delegate?.describe() ?? \"none\"` to \"none\"; " +
            "if this returns the wrong value, the optional existential param marshalled incorrectly.");
    }
}
