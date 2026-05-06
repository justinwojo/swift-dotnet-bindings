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
}
