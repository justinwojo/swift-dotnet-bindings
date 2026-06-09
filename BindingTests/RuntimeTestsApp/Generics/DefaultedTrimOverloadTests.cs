// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Coverage for option (b) of the generic-method default-overload gap: the
/// <c>DefaultParameterOverloadEmitter.TryEmitOverloads</c> tail call wired
/// into both CSM-sync and CSM-async emission paths.
///
/// The fixtures (<c>DefaultedHasher</c>, <c>DefaultedThrowingHasher</c>) carry a
/// method-level <c>D: DataProtocol</c> generic plus two trailing defaults — a
/// non-mappable <c>Set&lt;Int&gt;</c> and a mappable <c>Int</c>. The non-mappable
/// Set bypasses the trim emitter's <c>AllTrailingDefaultsAreCSharpMappable</c>
/// early-return; the mappable Int default exercises the intermediate-trim
/// path (one default exposed, one filled by the Swift shim).
///
/// Pre-fix the trim emitter's <c>methodDecl.IsGeneric</c> bail short-circuited
/// on the unspecialized generic decl — no <c>DBW_</c> symbol emitted, no
/// per-conformer trim P/Invoke. Post-fix the synthesized non-generic
/// methodDecl (substituted CSSignature, cleared GenericParameters, per-conformer
/// MangledName for unique hash) carries through to the trim emitter, which
/// produces per-conformer @_silgen_name shims and a <c>DBW_…</c> @_cdecl
/// wrapper alongside the existing CSM primary.
///
/// These tests pin three end-to-end properties for sync + sync-throws shapes:
///   1. The auto-trim primary (drops both defaults) is callable and Swift
///      observes the fixture's defaults — <c>options.count == 0</c>,
///      <c>tag == 7</c> (<c>DefaultedHasher</c>) / <c>tag == 11</c>
///      (<c>DefaultedThrowingHasher</c>).
///   2. The trim-1 variant (caller passes <c>options</c>, Swift fills the
///      mappable <c>tag</c>) round-trips the caller's set and the per-shape
///      default tag, proving the trim shim actually fires (no fall-through to
///      the auto-trim primary, which would record <c>options.count == 0</c>).
///   3. The throwing trim shim still surfaces a Swift error as a
///      <c>SwiftException</c> — the wiring doesn't drop the <c>throws</c>
///      annotation when constructing the per-conformer shim.
///
/// Async / async-throws coverage lives in <see cref="DefaultedAsyncTrimOverloadTests"/>.
/// </summary>
public class DefaultedTrimOverloadTests : TestBase
{
    public DefaultedTrimOverloadTests(TestResults results) : base(results) { }

    public void TestDefaultedHasher_AutoTrimPrimary_ByteArray_FillsBothDefaults()
    {
        // CSM-sync auto-trim primary: `Append(byte[] data)`. Both Swift defaults
        // (`options: Set<Int> = []`, `tag: Int = 7`) are filled by the Swift
        // wrapper itself — the C# call site doesn't expose them, so this is the
        // post-CSM "untouched defaults" baseline that the trim variants must
        // visibly differ from. After the call, Swift reports `lastOptionsCount ==
        // 0` and `lastTag == 7`, proving the wrapper invoked the original generic
        // method with both defaults.
        using var hasher = new DefaultedHasher();
        AssertEqual(0, hasher.Calls, "Hasher should start with 0 calls");

        hasher.Append(new byte[] { 0x01, 0x02, 0x03 });

        AssertEqual(1, hasher.Calls, "Append(byte[]) — auto-trim primary should record 1 call");
        AssertEqual(3, hasher.LastCount, "Append(byte[]) — should record byte-array length");
        AssertEqual(7, hasher.LastTag, "Append(byte[]) — auto-trim primary should observe tag default 7");
        AssertEqual(0, hasher.LastOptionsCount, "Append(byte[]) — auto-trim primary should observe empty options");
    }

    public void TestDefaultedHasher_AutoTrimPrimary_FoundationData_FillsBothDefaults()
    {
        // Sibling auto-trim primary keyed off the Foundation.Data Sequence-conformer
        // hint: `Append(Foundation.Data data)`. Same default-observation contract —
        // proves the wiring is per-conformer and doesn't collapse Data into the
        // [UInt8] specialization (or vice versa).
        using var hasher = new DefaultedHasher();

        var data = global::Swift.Foundation.Data.FromByteArray(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
        hasher.Append(data);

        AssertEqual(1, hasher.Calls, "Append(Foundation.Data) — should record 1 call");
        AssertEqual(4, hasher.LastCount, "Append(Foundation.Data) — should record byte length");
        AssertEqual(7, hasher.LastTag, "Append(Foundation.Data) — auto-trim primary should observe tag default 7");
        AssertEqual(0, hasher.LastOptionsCount,
            "Append(Foundation.Data) — auto-trim primary should observe empty options");
    }

    public void TestDefaultedHasher_TrimVariant_ByteArray_OptionsExposed_TagFilled()
    {
        // CSM-sync trim variant: `Append(IEnumerable<byte> data, IReadOnlySet<nint> options)`.
        // The C# caller now provides `options` — the trim shim's job is to fill
        // only the mappable `tag` default (7) inside Swift while routing the
        // caller-provided Set across the boundary. If the wiring fell through
        // to the auto-trim primary by accident, `lastOptionsCount` would be 0
        // (Swift's empty default) instead of the caller's count.
        using var hasher = new DefaultedHasher();

        var options = new HashSet<nint> { (nint)11, (nint)22, (nint)33 };
        hasher.Append((IEnumerable<byte>)new byte[] { 0x10, 0x20 }, options);

        AssertEqual(1, hasher.Calls, "Append(IEnumerable<byte>, options) — trim variant should record 1 call");
        AssertEqual(2, hasher.LastCount, "Append(IEnumerable<byte>, options) — byte length round-trip");
        AssertEqual(7, hasher.LastTag,
            "Append(IEnumerable<byte>, options) — trim variant should observe tag default 7");
        AssertEqual(3, hasher.LastOptionsCount,
            "Append(IEnumerable<byte>, options) — trim variant should round-trip caller's options.count");
    }

    public void TestDefaultedHasher_TrimVariant_FoundationData_OptionsExposed_TagFilled()
    {
        // Foundation.Data conformer's trim variant: `Append(byte[] data, IReadOnlySet<nint> options)`.
        // Note the byte[] surface — the Data CSM primary projects byte[] to
        // Foundation.Data internally, so its trim sibling reuses the same
        // byte[] caller surface. The DBW_ shim is keyed off the Data conformer's
        // per-conformer hash, distinct from the [UInt8] hash.
        using var hasher = new DefaultedHasher();

        var options = new HashSet<nint> { (nint)1, (nint)2 };
        hasher.Append(new byte[] { 0xAB, 0xCD, 0xEF }, options);

        AssertEqual(1, hasher.Calls, "Append(byte[] Data, options) — trim variant should record 1 call");
        AssertEqual(3, hasher.LastCount, "Append(byte[] Data, options) — byte length round-trip");
        AssertEqual(7, hasher.LastTag,
            "Append(byte[] Data, options) — trim variant should observe tag default 7");
        AssertEqual(2, hasher.LastOptionsCount,
            "Append(byte[] Data, options) — trim variant should round-trip caller's options.count");
    }

    public void TestDefaultedThrowingHasher_AutoTrimPrimary_ByteArray_HappyPath()
    {
        // Throwing fixture happy path on the auto-trim primary —
        // `AppendOrThrow(byte[] data)` — non-empty data, no error.
        // Locks in `tag == 11` (the per-shape Swift default; differs from
        // DefaultedHasher's 7 to catch any cross-fixture default leak).
        using var hasher = new DefaultedThrowingHasher();

        hasher.AppendOrThrow(new byte[] { 0x42 });

        AssertEqual(1, hasher.Calls, "AppendOrThrow(byte[]) — happy path should record 1 call");
        AssertEqual(1, hasher.LastCount, "AppendOrThrow(byte[]) — should record byte-array length");
        AssertEqual(11, hasher.LastTag,
            "AppendOrThrow(byte[]) — auto-trim primary should observe per-shape tag default 11");
        AssertEqual(0, hasher.LastOptionsCount,
            "AppendOrThrow(byte[]) — auto-trim primary should observe empty options");
    }

    public void TestDefaultedThrowingHasher_AutoTrimPrimary_ByteArray_EmptyThrows()
    {
        // Empty data triggers `BytesValidationError.empty` — the thrown error
        // must surface as SwiftException, proving the auto-trim CSM primary
        // still wires the throws plumbing (errorPtr out-param) end-to-end.
        // The pre-Phase-3a CSM-sync primary already had this path; this test
        // pins it as the baseline that the trim variant test below mirrors.
        using var hasher = new DefaultedThrowingHasher();

        SwiftException? caught = null;
        try
        {
            hasher.AppendOrThrow(System.Array.Empty<byte>());
        }
        catch (SwiftException e)
        {
            caught = e;
        }

        AssertTrue(caught is not null,
            "AppendOrThrow(empty byte[]) — auto-trim primary should surface BytesValidationError.empty");
        AssertEqual(0, hasher.Calls,
            "AppendOrThrow(empty byte[]) — should not record a call when error precedes mutation");
    }

    public void TestDefaultedThrowingHasher_TrimVariant_ByteArray_HappyPath_TagFilled()
    {
        // Throwing trim variant: `AppendOrThrow(IEnumerable<byte> data, IReadOnlySet<nint> options)`.
        // Caller exposes `options`, Swift fills `tag` (default 11). Verifies
        // the trim shim wires both the caller's Set and the throws path —
        // empty Set is fine here, just non-default to differ from the primary.
        using var hasher = new DefaultedThrowingHasher();

        var options = new HashSet<nint> { (nint)100, (nint)200 };
        hasher.AppendOrThrow((IEnumerable<byte>)new byte[] { 0x01, 0x02, 0x03, 0x04 }, options);

        AssertEqual(1, hasher.Calls,
            "AppendOrThrow(IEnumerable<byte>, options) — trim variant should record 1 call");
        AssertEqual(4, hasher.LastCount,
            "AppendOrThrow(IEnumerable<byte>, options) — byte length round-trip");
        AssertEqual(11, hasher.LastTag,
            "AppendOrThrow(IEnumerable<byte>, options) — trim variant should observe tag default 11");
        AssertEqual(2, hasher.LastOptionsCount,
            "AppendOrThrow(IEnumerable<byte>, options) — trim variant should round-trip caller's options.count");
    }

    public void TestDefaultedThrowingHasher_TrimVariant_ByteArray_EmptyThrows()
    {
        // Throws path through the trim variant — proves the per-conformer trim
        // shim isn't dropping the `throws` annotation when constructing the
        // synthesized non-generic methodDecl. Without throws-preservation the
        // empty input would silently no-op (or crash) instead of producing a
        // SwiftException.
        using var hasher = new DefaultedThrowingHasher();

        SwiftException? caught = null;
        try
        {
            hasher.AppendOrThrow((IEnumerable<byte>)System.Array.Empty<byte>(), new HashSet<nint>());
        }
        catch (SwiftException e)
        {
            caught = e;
        }

        AssertTrue(caught is not null,
            "AppendOrThrow(IEnumerable<byte> empty, options) — trim variant should surface BytesValidationError.empty");
        AssertEqual(0, hasher.Calls,
            "AppendOrThrow(IEnumerable<byte> empty, options) — should not record a call when error precedes mutation");
    }
}
