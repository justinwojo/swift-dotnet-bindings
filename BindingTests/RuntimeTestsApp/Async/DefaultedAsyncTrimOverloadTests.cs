// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Phase 3a coverage for option (b) of
/// <c>gap-0.10.0-generic-method-default-overload-missing.md</c> on the
/// CSM-async path. The fixtures (<c>DefaultedAsyncRoster</c>) carry a
/// method-level <c>S: Sequence</c> generic plus two trailing defaults — a
/// non-mappable <c>Set&lt;Int&gt;</c> and a mappable <c>Int</c> — applied to
/// both async no-throws (<c>appendAsync</c>) and async-throws
/// (<c>appendOrThrowAsync</c>) shapes.
///
/// Unlike the sync path (which emits an auto-trim primary that drops all
/// HasDefaultArg params), the CSM-async primary preserves the defaults and
/// renders mappable ones as inline C# defaults. So the surface for
/// <c>appendAsync</c> looks like:
/// <code>
///   AppendAsync(IEnumerable&lt;Animal&gt; source,
///               IReadOnlySet&lt;int&gt; options,         // exposed (non-mappable)
///               nint tag = 13,                       // exposed inline (mappable)
///               CancellationToken ct = default)
/// </code>
/// The trim-emitter wiring layers shorter variants on top of the primary, but
/// only at depths that aren't already covered by the primary's inline mappable
/// defaults. For these fixtures only the trim=2 (drops the entire defaulted
/// suffix — both <c>options</c> and <c>tag</c>) emits a new public surface:
///   <list type="bullet">
///     <item>trim=2: <c>AppendAsync(source, ct = default)</c> — Swift fills both defaults</item>
///   </list>
/// trim=1 (dropping only the mappable <c>tag</c>) is intentionally suppressed by
/// <c>BuildMappableSuffixShadowKeys</c> because the primary already exposes that
/// shape via its inline <c>nint tag = 13</c> default — emitting both would
/// produce an ambiguous overload.
///
/// These tests pin the visible shape of the surviving variants for both
/// no-throws and async-throws shapes, and prove the trim variant actually fires
/// (no fall-through to the primary, which would record a different tag default
/// across shapes — 13 for <c>appendAsync</c> vs 17 for <c>appendOrThrowAsync</c>).
/// Throws plumbing is verified end-to-end on the trim variant — the post-fix
/// synthesized methodDecl must keep <c>throws</c> through the trim emission.
/// </summary>
public class DefaultedAsyncTrimOverloadTests : TestBase
{
    public DefaultedAsyncTrimOverloadTests(TestResults results) : base(results) { }

    // ── appendAsync (async, no-throws) ────────────────────────────────

    public async Task TestDefaultedAsync_AppendAsync_PrimaryWithExplicitDefaults_RoundTrips()
    {
        // CSM-async primary surface — caller passes both `options` and an explicit
        // `tag` value. The Swift body stores `"TAG=<tag>;OPT=<options.count>"` into
        // the last animal's sound, which we read back to prove the values made the
        // round-trip across the @_cdecl async trampoline.
        using var roster = Functions.MakeDefaultedAsyncRoster(firstName: "Bear", secondName: "Wolf");
        var newcomers = new List<Animal>
        {
            new Animal(name: "Lion", sound: "Roar"),
        };

        var options = new HashSet<nint> { (nint)1, (nint)2, (nint)3 };
        await roster.AppendAsync(newcomers, options, tag: 99);

        AssertEqual(3, (int)roster.Count, "AppendAsync(primary) — roster should grow by 1");
        AssertEqual("Lion", roster[2].Name.ToString(),
            "AppendAsync(primary) — Lion should be appended at index 2");
        AssertEqual("TAG=99;OPT=3", roster[2].Sound.ToString(),
            "AppendAsync(primary) — Swift body should observe caller's tag=99 and options.count=3");
    }

    public async Task TestDefaultedAsync_AppendAsync_TrimDropsBoth_FillsSwiftDefaults()
    {
        // Trim=2 variant: `AppendAsync(source, ct)` drops both defaults.
        // Swift's per-shape default `tag = 13` (DefaultedAsyncRoster.appendAsync)
        // and empty `options` get filled by the synthesized shim. Differs from
        // the appendOrThrowAsync default `tag = 17` so a fall-through to the
        // wrong shape is detectable.
        using var roster = Functions.MakeDefaultedAsyncRoster(firstName: "Bear", secondName: "Wolf");
        var newcomers = new List<Animal>
        {
            new Animal(name: "Lynx", sound: "Hiss"),
        };

        await roster.AppendAsync(newcomers);

        AssertEqual(3, (int)roster.Count, "AppendAsync(trim=2) — roster should grow by 1");
        AssertEqual("Lynx", roster[2].Name.ToString(),
            "AppendAsync(trim=2) — Lynx should be appended at index 2");
        AssertEqual("TAG=13;OPT=0", roster[2].Sound.ToString(),
            "AppendAsync(trim=2) — Swift should fill per-shape defaults tag=13, options=[]");
    }

    public async Task TestDefaultedAsync_AppendAsync_PrimaryOptionsExplicit_TagDefaulted()
    {
        // Cover the primary's "options-only" call shape via its inline `tag = 13`
        // default. trim=1 is intentionally not emitted (would be ambiguous with
        // the primary on this very call shape — see BuildMappableSuffixShadowKeys).
        // Verifying the primary handles `(source, options)` correctly is the
        // structural proof the suppression is sound: Swift observes caller's
        // options.count and the inline tag=13 default.
        using var roster = Functions.MakeDefaultedAsyncRoster(firstName: "Bear", secondName: "Wolf");
        var newcomers = new List<Animal>
        {
            new Animal(name: "Otter", sound: "Click"),
        };

        var options = new HashSet<nint> { (nint)10, (nint)20 };
        await roster.AppendAsync(newcomers, options);

        AssertEqual(3, (int)roster.Count, "AppendAsync(primary, options-only) — roster should grow by 1");
        AssertEqual("Otter", roster[2].Name.ToString(),
            "AppendAsync(primary, options-only) — Otter should be appended at index 2");
        AssertEqual("TAG=13;OPT=2", roster[2].Sound.ToString(),
            "AppendAsync(primary, options-only) — primary's inline tag=13 default + caller's options.count=2");
    }

    public async Task TestDefaultedAsync_AppendAsync_DogConformer_Trim_FillsSwiftDefaults()
    {
        // Class-subtype conformer (Dog : Animal) on the trim=2 variant — the
        // per-conformer DBW shim must hash distinctly from the Animal-conformer
        // shim. If both shims collided on the same DBW symbol, only one would
        // win and the other call would hit the wrong wrapper or
        // EntryPointNotFoundException. Adding a Dog and reading back proves the
        // Dog-keyed DBW symbol resolves and wires through to the same Swift
        // body that records tag=13.
        using var roster = Functions.MakeDefaultedAsyncRoster(firstName: "Bear", secondName: "Wolf");
        var dogs = new List<Dog>
        {
            new Dog(name: "Rex", breed: "Labrador"),
        };

        await roster.AppendAsync(dogs);

        AssertEqual(3, (int)roster.Count, "AppendAsync(Dog, trim=2) — roster should grow by 1");
        AssertEqual("Rex", roster[2].Name.ToString(),
            "AppendAsync(Dog, trim=2) — Rex should be appended at index 2");
        AssertEqual("TAG=13;OPT=0", roster[2].Sound.ToString(),
            "AppendAsync(Dog, trim=2) — Dog conformer's DBW shim should fill tag=13, options=[]");
    }

    // ── appendOrThrowAsync (async, throws) ────────────────────────────

    public async Task TestDefaultedAsync_AppendOrThrowAsync_PrimaryHappyPath_RoundTrips()
    {
        // Async-throws CSM primary happy path — `shouldThrow: false`, both
        // defaults caller-provided. Swift records `tag=88, OPT=2` so we can
        // assert the round-trip end-to-end. Per-shape default differs from
        // appendAsync's 13 — locks in the no-cross-contamination contract.
        using var roster = Functions.MakeDefaultedAsyncRoster(firstName: "Bear", secondName: "Wolf");
        var newcomers = new List<Animal>
        {
            new Animal(name: "Owl", sound: "Hoot"),
        };

        var options = new HashSet<nint> { (nint)5, (nint)6 };
        await roster.AppendOrThrowAsync(newcomers, shouldThrow: false, options, tag: 88);

        AssertEqual(3, (int)roster.Count, "AppendOrThrowAsync(primary) — roster should grow by 1");
        AssertEqual("Owl", roster[2].Name.ToString(),
            "AppendOrThrowAsync(primary) — Owl should be appended at index 2");
        AssertEqual("TAG=88;OPT=2", roster[2].Sound.ToString(),
            "AppendOrThrowAsync(primary) — Swift should observe caller's tag=88 and options.count=2");
    }

    public async Task TestDefaultedAsync_AppendOrThrowAsync_TrimDropsBoth_FillsSwiftDefaults()
    {
        // Trim=2 on the async-throws shape: `AppendOrThrowAsync(source, shouldThrow, ct)`.
        // Per-shape default `tag = 17` proves the trim shim is keyed off the
        // appendOrThrowAsync method (not appendAsync). Empty options round-trip
        // as the Swift Set<Int> = [] default.
        using var roster = Functions.MakeDefaultedAsyncRoster(firstName: "Bear", secondName: "Wolf");
        var newcomers = new List<Animal>
        {
            new Animal(name: "Hawk", sound: "Screech"),
        };

        await roster.AppendOrThrowAsync(newcomers, shouldThrow: false);

        AssertEqual(3, (int)roster.Count, "AppendOrThrowAsync(trim=2) — roster should grow by 1");
        AssertEqual("Hawk", roster[2].Name.ToString(),
            "AppendOrThrowAsync(trim=2) — Hawk should be appended at index 2");
        AssertEqual("TAG=17;OPT=0", roster[2].Sound.ToString(),
            "AppendOrThrowAsync(trim=2) — Swift should fill per-shape defaults tag=17, options=[]");
    }

    public async Task TestDefaultedAsync_AppendOrThrowAsync_PrimaryOptionsExplicit_TagDefaulted()
    {
        // Async-throws primary call shape that the suppressed trim=1 would have
        // covered: caller passes `options`, primary's inline `tag = 17` default
        // fills the mappable suffix. Locks in shape-correct dispatch + per-shape
        // default tag (17 for appendOrThrowAsync vs 13 for appendAsync).
        using var roster = Functions.MakeDefaultedAsyncRoster(firstName: "Bear", secondName: "Wolf");
        var newcomers = new List<Animal>
        {
            new Animal(name: "Falcon", sound: "Cry"),
        };

        var options = new HashSet<nint> { (nint)42 };
        await roster.AppendOrThrowAsync(newcomers, shouldThrow: false, options);

        AssertEqual(3, (int)roster.Count,
            "AppendOrThrowAsync(primary, options-only) — roster should grow by 1");
        AssertEqual("Falcon", roster[2].Name.ToString(),
            "AppendOrThrowAsync(primary, options-only) — Falcon should be appended at index 2");
        AssertEqual("TAG=17;OPT=1", roster[2].Sound.ToString(),
            "AppendOrThrowAsync(primary, options-only) — primary's inline tag=17 default + caller's options.count=1");
    }

    public async Task TestDefaultedAsync_AppendOrThrowAsync_TrimDropsBoth_ThrowsFaultsTask()
    {
        // Throws path through the async-throws trim=2 variant — proves the
        // synthesized non-generic methodDecl preserved the `throws` annotation
        // when the trim emitter cleared GenericParameters and re-wrote the
        // CSSignature. Without throws-preservation, `shouldThrow: true` would
        // silently no-op instead of faulting the Task with a SwiftException.
        using var roster = Functions.MakeDefaultedAsyncRoster(firstName: "Bear", secondName: "Wolf");
        var newcomers = new List<Animal>
        {
            new Animal(name: "Crow", sound: "Caw"),
        };

        SwiftException? caught = null;
        try
        {
            await roster.AppendOrThrowAsync(newcomers, shouldThrow: true);
        }
        catch (SwiftException e)
        {
            caught = e;
        }

        AssertTrue(caught is not null,
            "AppendOrThrowAsync(trim=2, shouldThrow: true) — trim variant should fault Task with SwiftException");
        AssertEqual(2, (int)roster.Count,
            "AppendOrThrowAsync(trim=2, shouldThrow: true) — roster should be unchanged after thrown error");
    }

    public async Task TestDefaultedAsync_AppendOrThrowAsync_Primary_Throws_FaultsTask()
    {
        // Throws path through the async-throws primary with caller-supplied
        // options (the call shape that trim=1 would have covered). Verifies
        // throws plumbing on the primary still surfaces a SwiftException —
        // the suppression logic doesn't disturb error reporting on the
        // surviving primary.
        using var roster = Functions.MakeDefaultedAsyncRoster(firstName: "Bear", secondName: "Wolf");
        var newcomers = new List<Animal>
        {
            new Animal(name: "Magpie", sound: "Chatter"),
        };

        var options = new HashSet<nint> { (nint)1 };
        SwiftException? caught = null;
        try
        {
            await roster.AppendOrThrowAsync(newcomers, shouldThrow: true, options);
        }
        catch (SwiftException e)
        {
            caught = e;
        }

        AssertTrue(caught is not null,
            "AppendOrThrowAsync(primary, shouldThrow: true) — primary should fault Task with SwiftException");
        AssertEqual(2, (int)roster.Count,
            "AppendOrThrowAsync(primary, shouldThrow: true) — roster should be unchanged after thrown error");
    }
}
