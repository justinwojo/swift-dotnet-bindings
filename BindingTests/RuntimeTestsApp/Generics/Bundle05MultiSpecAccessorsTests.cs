// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Bundle 05 #3 (MultiSpecialization drops generic property accessors)
/// regression coverage. Pre-fix, properties defined on
/// <c>extension Foo where Param == Concrete</c> blocks of a generic
/// type were skipped wholesale with skip reason
/// <c>MultiSpecialization</c> whenever more than one <c>where Param ==
/// Concrete</c> block existed for the same parent type — dropping
/// StoreKit2's <c>VerificationResult&lt;SignedType&gt;.jwsRepresentation</c>
/// surface and breaking server-side receipt verification end-to-end.
///
/// Post-fix the properties surface as closed-generic C# extension
/// methods (<c>GetAlphaTag(this Bundle05Container&lt;Bundle05SpecKeyA&gt;)</c>
/// etc.), one per realized specialization. This test invokes each
/// extension method on a freshly constructed specialization and
/// verifies the round-trip value matches the Swift body, proving:
///   1. The accessor is emitted (not dropped under MultiSpecialization).
///   2. The PInvoke entry point routes to the correct per-specialization
///      mangled Swift symbol.
///   3. The generic-class self pointer is accepted by the wrapper at
///      runtime under both Mono JIT (sim) and NativeAOT (device).
/// </summary>
public class Bundle05MultiSpecAccessorsTests : TestBase
{
    public Bundle05MultiSpecAccessorsTests(TestResults results) : base(results) { }

    /// <summary>
    /// Alpha specialization: <c>Bundle05Container&lt;Bundle05SpecKeyA&gt;.alphaTag</c>
    /// must round-trip the embedded <c>id</c> through the Swift body
    /// (<c>"alpha-\(id)"</c>).
    /// </summary>
    public void TestAlphaSpecialization_AlphaTagRoundTrip()
    {
        using var alpha = TestLibFunctions.MakeBundle05ContainerAlpha(7);
        var tag = alpha.GetAlphaTag();
        AssertEqual("alpha-7", tag,
            "Bundle 05 #3: Bundle05Container<Bundle05SpecKeyA>.alphaTag must " +
            "surface as a callable C# extension method that round-trips the Swift " +
            "specialization's body. A 'MultiSpecialization' regression would " +
            "drop GetAlphaTag and produce CS0103/CS1061 at compile time.");
    }

    /// <summary>
    /// Beta specialization: <c>Bundle05Container&lt;Bundle05SpecKeyB&gt;.betaTag</c>
    /// must round-trip independently of the alpha specialization.
    /// </summary>
    public void TestBetaSpecialization_BetaTagRoundTrip()
    {
        using var beta = TestLibFunctions.MakeBundle05ContainerBeta(11);
        var tag = beta.GetBetaTag();
        AssertEqual("beta-11", tag,
            "Bundle 05 #3: Bundle05Container<Bundle05SpecKeyB>.betaTag must " +
            "surface independently of the alpha specialization. Each specialization " +
            "monomorphizes to its own Swift symbol; the multispec fix keeps both " +
            "extensions reachable without dedup-skipping either.");
    }

    /// <summary>
    /// Non-frozen struct return shape regression coverage. The
    /// <c>alphaDescriptor</c> accessor returns
    /// <see cref="Bundle05DescriptorPayload"/>, a non-frozen Swift struct,
    /// which forces the generator down
    /// <c>ConstrainedExtensionEmitter.CEReturnShape.NonFrozenStruct</c> —
    /// the exact path Codex round 1 flagged for use-after-free / double-
    /// free on disposal. Pre-fix the wrapper allocated an indirect-result
    /// buffer, called <c>SwiftMarshal.MarshalFromSwift&lt;T&gt;(buffer)</c>
    /// (which transfers ownership of <c>buffer</c> to the returned
    /// SafeHandle), then freed <c>buffer</c> in <c>finally</c> — leaving
    /// the returned object with a dangling payload that traps on field
    /// access or on dispose. Post-fix the wrapper frees only on the catch
    /// path, mirroring <c>ExtensionMarshallingHelper.cs</c>'s
    /// <c>ReturnKind.NonFrozenStruct</c> shape.
    ///
    /// The test reads a field on the returned descriptor (forcing a
    /// payload read against the SafeHandle-owned buffer), forces a GC
    /// collection between operations to stress the SafeHandle's
    /// finalizer order, then disposes — all of which would trap or hang
    /// under the pre-fix double-free shape.
    /// </summary>
    public void TestAlphaSpecialization_AlphaDescriptorNonFrozenStructRoundTrip()
    {
        using (var alpha = TestLibFunctions.MakeBundle05ContainerAlpha(42))
        {
            using var descriptor = alpha.GetAlphaDescriptor();
            AssertNotNull(descriptor,
                "GetAlphaDescriptor must return a non-null Bundle05DescriptorPayload " +
                "instance — a null result would imply the indirect-result buffer was " +
                "freed before MarshalFromSwift took ownership.");

            AssertEqual(42, descriptor.Id,
                "Bundle 05 #3: Bundle05DescriptorPayload.id must read back as 42 " +
                "(matching MakeBundle05ContainerAlpha(42)). A use-after-free on the " +
                "indirect-result buffer would either crash on this read or return " +
                "uninitialized memory, since the buffer would have been Freed in the " +
                "wrapper's finally block immediately after MarshalFromSwift returned.");

            // Force a GC collection while the descriptor's SafeHandle is still
            // alive to stress the lifetime path: under the pre-fix shape the
            // SafeHandle owned a buffer that had ALREADY been Freed by the
            // wrapper's finally block, so the next finalizer pass would either
            // double-free or trap on payload read.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            AssertEqual(42, descriptor.Id,
                "After GC.Collect + WaitForPendingFinalizers, Bundle05DescriptorPayload.id " +
                "must still read back as 42. A double-free of the indirect-result buffer " +
                "would corrupt the payload between the first and second read.");
        }
        // Implicit Dispose on the using-block exit must not crash. Pre-fix
        // disposal ran after the wrapper had already freed the buffer in
        // finally; the SafeHandle's release path then double-freed.
    }

    /// <summary>
    /// Foundation.Date return shape regression coverage. The
    /// <c>alphaSignedDate</c> accessor returns a Swift <c>Date</c>, which
    /// drives the generator down
    /// <c>ConstrainedExtensionEmitter.CEReturnShape.FoundationDate</c> —
    /// the path StoreKit2's <c>VerificationResult.signedDate</c> needs.
    /// The Swift wrapper returns <c>timeIntervalSinceReferenceDate</c> as
    /// a single <c>Double</c> (no indirect-result buffer); the C# side
    /// applies the Swift epoch (2001-01-01 UTC) + AddSeconds and returns
    /// a <c>System.DateTimeOffset</c>.
    /// </summary>
    public void TestAlphaSpecialization_AlphaSignedDateRoundTrip()
    {
        using var alpha = TestLibFunctions.MakeBundle05ContainerAlpha(60);
        var date = alpha.GetAlphaSignedDate();
        // Swift epoch (2001-01-01 UTC) + 60 seconds = 2001-01-01 00:01:00 UTC.
        var expected = new DateTimeOffset(2001, 1, 1, 0, 1, 0, TimeSpan.Zero);
        AssertEqual(expected, date,
            "Bundle 05 #3: Foundation.Date constrained-extension property must " +
            "round-trip as System.DateTimeOffset relative to the Swift " +
            "reference epoch (2001-01-01 UTC). A regression in the FoundationDate " +
            "shape (e.g. routing through the indirect-result path or using a " +
            "wrong epoch) would shift the result by 31 years (Unix epoch) or by " +
            "the boundary buffer's uninitialized bytes.");
    }

    /// <summary>
    /// Foundation.UUID return shape regression coverage. The
    /// <c>alphaDeviceVerificationNonce</c> accessor returns a Swift
    /// <c>UUID</c>, driving the generator down
    /// <c>CEReturnShape.FoundationUUID</c> — the path StoreKit2's
    /// <c>VerificationResult.deviceVerificationNonce</c> needs. The
    /// fixture packs the parent's <c>id</c> into the trailing 4 bytes
    /// of the UUID so the test can assert the exact bytes round-trip
    /// through the indirect-result + System.Guid memcpy path.
    /// </summary>
    public void TestAlphaSpecialization_AlphaDeviceVerificationNonceRoundTrip()
    {
        using var alpha = TestLibFunctions.MakeBundle05ContainerAlpha(0x42);
        var nonce = alpha.GetAlphaDeviceVerificationNonce();
        var bytes = nonce.ToByteArray();
        AssertEqual(16, bytes.Length,
            "System.Guid bytes must be 16. Any other length implies the " +
            "indirect-result buffer was misallocated or the cast to *Guid* " +
            "read past the buffer.");

        // The Swift fixture writes a distinct, monotonically increasing pattern
        // into the leading 12 bytes (0x10..0x1B) and packs `id` into the trailing
        // four bytes (bytes[12..15]).
        //
        // System.Guid's in-memory layout is { Int32 _a; Int16 _b; Int16 _c; 8x byte }
        // with sequential layout. On every supported runtime (x86_64, ARM64) that's
        // little-endian, so MemoryMarshal.TryWrite-based ToByteArray() dumps the
        // memory directly: bytes[0..15] match what Swift wrote, byte-for-byte.
        // Asserting all 16 positions catches any byte-swap regression inside the
        // _a/_b/_c fields (which a single nonzero-trailing-byte fixture would miss).
        var expected = new byte[]
        {
            0x10, 0x11, 0x12, 0x13,
            0x14, 0x15, 0x16, 0x17,
            0x18, 0x19, 0x1A, 0x1B,
            0x00, 0x00, 0x00, 0x42,
        };
        for (int i = 0; i < 16; i++)
        {
            AssertEqual(expected[i], bytes[i],
                $"Bundle 05 #3: UUID byte {i} must round-trip as 0x{expected[i]:X2}. " +
                "A regression in the FoundationUUID shape (wrong indirect-result " +
                "alignment, missing initializeMemory, or a byte-swap inside the " +
                "*(Guid*)buffer cast) would scramble one or more positions in this array.");
        }
    }

    /// <summary>
    /// Constrained-extension METHOD shape (Fix J). Pre-fix,
    /// `ConstrainedExtensionEmitter` only iterated
    /// <c>typeDecl.Properties</c>; methods on `where Param == Concrete`
    /// extensions were dropped wholesale. Post-fix, zero-arg sync
    /// non-throwing methods re-surface as static extension methods on the
    /// closed-generic instance, mirroring the property pipeline.
    ///
    /// The instance String-return path exercises both the method emission
    /// shape (`TryEmitMethodExtension` -> `this`-extension signature with
    /// `_self` P/Invoke arg) and the same Utf8Slice return marshalling the
    /// property side already covers — proving the shared
    /// <c>CEReturnShape</c> classifier reaches the method emitter intact.
    /// </summary>
    public void TestAlphaSpecialization_ComputeAlphaLabelMethodRoundTrip()
    {
        using var alpha = TestLibFunctions.MakeBundle05ContainerAlpha(99);
        var label = alpha.ComputeAlphaLabel();
        AssertEqual("alpha-label-99", label,
            "Bundle 05 #3 (Fix J): Bundle05Container<Bundle05SpecKeyA>.computeAlphaLabel() " +
            "must surface as a callable C# extension method that round-trips through the " +
            "constrained-extension METHOD emission path. Pre-Fix-J the emitter only " +
            "iterated `typeDecl.Properties`; method-shape multispec siblings were dropped.");
    }

    /// <summary>
    /// Beta-side instance method round-trip — proves per-specialization
    /// mangling for the method-shape multispec keeps both alpha and beta
    /// extensions reachable at distinct symbols (parallel to the existing
    /// property-side beta coverage above).
    /// </summary>
    public void TestBetaSpecialization_ComputeBetaLabelMethodRoundTrip()
    {
        using var beta = TestLibFunctions.MakeBundle05ContainerBeta(101);
        var label = beta.ComputeBetaLabel();
        AssertEqual("beta-label-101", label,
            "Bundle 05 #3 (Fix J): per-specialization mangling for the method-shape " +
            "multispec must keep alpha and beta method extensions reachable at distinct " +
            "Swift symbols. A regression that conflated them would break either side " +
            "(or both — same name, different concrete types is exactly the multispec gap).");
    }

    /// <summary>
    /// Static-factory method shape (canonical WeatherKit
    /// <c>*Query.temperature()</c> / MusicKit no-arg accessor pattern).
    /// Static methods on a constrained extension emit on the per-spec
    /// extensions class itself (<c>Bundle05ContainerBundle05SpecKeyAExtensions
    /// .DefaultAlphaRank()</c>) — no <c>this</c> receiver — because C#
    /// can't dispatch static extension methods on closed generic
    /// instantiations.
    /// </summary>
    public void TestAlphaSpecialization_DefaultAlphaRankStaticMethod()
    {
        var rank = Bundle05ContainerBundle05SpecKeyAExtensions.DefaultAlphaRank();
        AssertEqual(17, rank,
            "Bundle 05 #3 (Fix J): static-factory method shape must emit on the per-spec " +
            "extensions class with no `this` receiver and round-trip the Swift body's " +
            "literal value (17). A regression that dropped static methods would surface " +
            "as a CS0117 (no such member) at compile time.");
    }

    /// <summary>
    /// Beta-side static-factory round-trip — confirms both alpha and beta
    /// static factories reach C# at independent mangled symbols.
    /// </summary>
    public void TestBetaSpecialization_DefaultBetaRankStaticMethod()
    {
        var rank = Bundle05ContainerBundle05SpecKeyBExtensions.DefaultBetaRank();
        AssertEqual(23, rank,
            "Bundle 05 #3 (Fix J): beta static-factory must reach C# at its own mangled " +
            "symbol (23). Combined with the alpha case, a single test would not catch " +
            "a per-spec symbol-conflation regression that broke only one side.");
    }

    /// <summary>
    /// Open-generic-return property (`payloadValue` shape). Pre-Fix-J
    /// properties whose return type was the parent's open generic
    /// parameter (e.g. <c>VerificationResult&lt;SignedType&gt;
    /// .payloadValue</c>) skipped under <c>AnyTypeFallback</c> because the
    /// projected return was unresolvable at emit time. Post-fix, the
    /// emitter substitutes the open parameter with each anchored concrete
    /// specialization, so the closed-generic instance gets a typed
    /// accessor (<c>GetCarriedPayload(this
    /// Bundle05PayloadCarrier&lt;Bundle05DescriptorPayload&gt; self)
    /// -> Bundle05DescriptorPayload</c>).
    ///
    /// Asserts the round-tripped descriptor's id matches the factory's
    /// input — proving the substituted return type reached the C# side
    /// intact AND that the indirect-result + SafeHandle ownership transfer
    /// for the substituted non-frozen-struct return shape did not regress
    /// from the existing `alphaDescriptor` test.
    /// </summary>
    public void TestPayloadCarrier_OpenGenericReturnRoundTrip()
    {
        using (var carrier = TestLibFunctions.MakeBundle05PayloadCarrierWithDescriptor(7))
        {
            var anchor = carrier.GetAnchorTag();
            AssertEqual("anchor-7", anchor,
                "Anchor property must round-trip — without an anchored constrained " +
                "specialization, FindOpenGenericReturnProperties would not run and the " +
                "open-generic-return surface would stay unreachable.");

            using var payload = carrier.GetCarriedPayload();
            AssertNotNull(payload,
                "GetCarriedPayload must return a non-null Bundle05DescriptorPayload — a " +
                "null result would imply the substituted indirect-result buffer was " +
                "freed before MarshalFromSwift took ownership.");
            AssertEqual(7, payload.Id,
                "Bundle 05 #3 (Fix J): open-generic-return shape must substitute the " +
                "parent's open generic parameter with the concrete specialization at emit " +
                "time and round-trip the substituted return value through the per-spec " +
                "extension method. Pre-fix this surface skipped under AnyTypeFallback.");
            AssertEqual("carried-7", payload.Label.ToString(),
                "Substituted-return struct must round-trip BOTH primitive and reference " +
                "fields, not just the Int32. The Swift factory builds the descriptor with " +
                "label `carried-\\(id)`; if Label drops in transit, the indirect-result " +
                "path is silently truncating the substituted struct's String slot.");
        }
    }

    /// <summary>
    /// Foundation.Data round-trip on the alpha specialization — exercises
    /// the path StoreKit2's <c>VerificationResult.headerData</c> /
    /// <c>.payloadData</c> / <c>.signatureData</c> /
    /// <c>.signedData</c> / <c>.deviceVerification</c> need (all
    /// previously skipped under MultiSpecialization). The fixture
    /// trails the parent's <c>id</c> as the final byte, so the test
    /// can assert both the count and the trailing byte round-trip
    /// through the indirect-result + Swift.Foundation.Data.ToByteArray
    /// path.
    /// </summary>
    public void TestAlphaSpecialization_AlphaHeaderDataRoundTrip()
    {
        using var alpha = TestLibFunctions.MakeBundle05ContainerAlpha(8);
        var bytes = alpha.GetAlphaHeaderData();
        AssertEqual(8, bytes.Length,
            "Bundle 05 #3: Foundation.Data.count must round-trip as 8 " +
            "(matching MakeBundle05ContainerAlpha(8)). A regression in the " +
            "FoundationData shape (e.g. wrong indirect-result alignment or " +
            "ToByteArray called on a freed buffer) would crash or return an " +
            "empty array.");
        AssertEqual((byte)0xAB, bytes[0],
            "Leading byte must be 0xAB (the fixture's fill pattern). A wholesale " +
            "buffer corruption regression would scramble this.");
        AssertEqual((byte)8, bytes[7],
            "Trailing byte must be id=8. Validates that ToByteArray()'s " +
            "CopyBytes P/Invoke read the Swift Data buffer correctly after the " +
            "indirect-result write, then freed the wrapper buffer in finally " +
            "without affecting the bytes already copied to managed memory.");

        // Force GC to stress the buffer-free-in-finally path: ToByteArray()
        // copies bytes into a managed array BEFORE we leave the try block, so
        // the managed array must remain valid even after the indirect-result
        // buffer is freed. A regression that returned a pointer-aliased array
        // would crash on this read.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        AssertEqual((byte)8, bytes[7],
            "After GC.Collect, the trailing byte must still read 8. A regression " +
            "where ToByteArray() returned a buffer-aliased view (instead of a " +
            "managed copy) would either crash here or read scrambled bytes.");
    }
}
