// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Regression coverage for the Codex r1 Medium concern on Phase 3a:
/// CSM-routed generic + collection default + trailing <c>#file</c> debug
/// parameter. The hypothesis was that <c>BuildOverloadDecl</c> trims the last
/// N raw args from <c>CSSignature.Skip(1)</c> without skipping debug params,
/// so a method like
/// <code>
///     func append&lt;D: DataProtocol&gt;(_ data: D,
///         options: Set&lt;Int&gt; = [],
///         tag: Int = 7,
///         file: StaticString = #file)
/// </code>
/// would emit only one trim variant (data + options + tag) instead of the
/// expected two (data + options + tag, and data + options).
///
/// Empirically this does NOT reproduce — the parser stops the public surface
/// at the last non-debug default, so <c>CountTrailingDefaults</c> and
/// <c>BuildOverloadDecl</c> agree on what counts as a default. Generated
/// bindings for <c>DefaultedHasherWithFile</c> emit the same trim shape as
/// the no-debug-param <c>DefaultedHasher</c>:
///   - auto-trim primary: <c>Append(byte[] data)</c>
///   - trim variant: <c>Append(IEnumerable&lt;byte&gt; data, IReadOnlySet&lt;nint&gt; options)</c>
///
/// These tests pin that empirical equivalence at the runtime layer — caller
/// surface, default observation, and Set round-trip all match
/// <see cref="DefaultedTrimOverloadTests"/>. If a future emitter change
/// regresses the debug-param handling to the asymmetric shape originally
/// theorized, these assertions will fail (either because the trim variant
/// disappears or because <c>file</c> leaks into the C# surface).
/// </summary>
public class DefaultedTrimOverloadWithFileTests : TestBase
{
    public DefaultedTrimOverloadWithFileTests(TestResults results) : base(results) { }

    public void TestDefaultedHasherWithFile_AutoTrimPrimary_ByteArray_FillsAllDefaults()
    {
        // CSM-sync auto-trim primary: `Append(byte[] data)`. Three Swift defaults
        // — `options: Set<Int> = []`, `tag: Int = 7`, `file: StaticString = #file` —
        // are all filled by Swift. The C# surface must NOT expose any of them
        // (in particular, `file` must not leak as an extra parameter).
        // After the call, Swift reports:
        //   - lastOptionsCount == 0 (empty default)
        //   - lastTag == 7 (per-shape default)
        //   - lastFile non-empty (Swift fills #file with the call site)
        using var hasher = new DefaultedHasherWithFile();
        AssertEqual(0, hasher.Calls, "Hasher should start with 0 calls");

        hasher.Append(new byte[] { 0x01, 0x02, 0x03 });

        AssertEqual(1, hasher.Calls, "Append(byte[]) — auto-trim primary should record 1 call");
        AssertEqual(3, hasher.LastCount, "Append(byte[]) — should record byte-array length");
        AssertEqual(7, hasher.LastTag, "Append(byte[]) — auto-trim primary should observe tag default 7");
        AssertEqual(0, hasher.LastOptionsCount,
            "Append(byte[]) — auto-trim primary should observe empty options");
        AssertTrue(hasher.LastFile.ToString().Length > 0,
            "Append(byte[]) — Swift should fill the #file default with a non-empty string");
    }

    public void TestDefaultedHasherWithFile_AutoTrimPrimary_FoundationData_FillsAllDefaults()
    {
        // Sibling auto-trim primary keyed off the Foundation.Data conformer:
        // `Append(Foundation.Data data)`. Same default-observation contract as
        // the [UInt8] sibling — proves the per-conformer wiring is intact under
        // the trailing #file debug param.
        using var hasher = new DefaultedHasherWithFile();

        var data = global::Swift.Foundation.Data.FromByteArray(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
        hasher.Append(data);

        AssertEqual(1, hasher.Calls, "Append(Foundation.Data) — should record 1 call");
        AssertEqual(4, hasher.LastCount, "Append(Foundation.Data) — should record byte length");
        AssertEqual(7, hasher.LastTag,
            "Append(Foundation.Data) — auto-trim primary should observe tag default 7");
        AssertEqual(0, hasher.LastOptionsCount,
            "Append(Foundation.Data) — auto-trim primary should observe empty options");
        AssertTrue(hasher.LastFile.ToString().Length > 0,
            "Append(Foundation.Data) — Swift should fill the #file default");
    }

    public void TestDefaultedHasherWithFile_TrimVariant_ByteArray_OptionsExposed_TagAndFileFilled()
    {
        // CSM-sync trim variant for the [UInt8] conformer:
        // `Append(IEnumerable<byte> data, IReadOnlySet<nint> options)`.
        // The trim shim exposes only `options`; `tag` and `file` remain Swift
        // defaults. If the debug-param hypothesis had been correct, this
        // overload would be missing entirely (only the data+options+tag shape
        // would have emitted). Assertion that the call compiles and routes
        // through the trim shim is the regression guard.
        using var hasher = new DefaultedHasherWithFile();

        var options = new HashSet<nint> { (nint)11, (nint)22, (nint)33 };
        hasher.Append((IEnumerable<byte>)new byte[] { 0x10, 0x20 }, options);

        AssertEqual(1, hasher.Calls,
            "Append(IEnumerable<byte>, options) — trim variant should record 1 call");
        AssertEqual(2, hasher.LastCount,
            "Append(IEnumerable<byte>, options) — byte length round-trip");
        AssertEqual(7, hasher.LastTag,
            "Append(IEnumerable<byte>, options) — trim variant should observe tag default 7");
        AssertEqual(3, hasher.LastOptionsCount,
            "Append(IEnumerable<byte>, options) — trim variant should round-trip caller's options.count");
        AssertTrue(hasher.LastFile.ToString().Length > 0,
            "Append(IEnumerable<byte>, options) — Swift should fill the #file default");
    }

    public void TestDefaultedHasherWithFile_TrimVariant_FoundationData_OptionsExposed_TagAndFileFilled()
    {
        // Foundation.Data conformer's trim variant:
        // `Append(byte[] data, IReadOnlySet<nint> options)`. The DBW_ shim is
        // keyed off the Data conformer's per-conformer hash, distinct from the
        // [UInt8] hash — proves the per-conformer trim wiring is intact under
        // the trailing #file debug param.
        using var hasher = new DefaultedHasherWithFile();

        var options = new HashSet<nint> { (nint)1, (nint)2 };
        hasher.Append(new byte[] { 0xAB, 0xCD, 0xEF }, options);

        AssertEqual(1, hasher.Calls,
            "Append(byte[] Data, options) — trim variant should record 1 call");
        AssertEqual(3, hasher.LastCount,
            "Append(byte[] Data, options) — byte length round-trip");
        AssertEqual(7, hasher.LastTag,
            "Append(byte[] Data, options) — trim variant should observe tag default 7");
        AssertEqual(2, hasher.LastOptionsCount,
            "Append(byte[] Data, options) — trim variant should round-trip caller's options.count");
        AssertTrue(hasher.LastFile.ToString().Length > 0,
            "Append(byte[] Data, options) — Swift should fill the #file default");
    }
}
