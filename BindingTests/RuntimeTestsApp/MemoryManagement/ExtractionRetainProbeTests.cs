// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// Deterministic regression probe for the extraction-side under-retain in
/// <c>SwiftOptional&lt;T&gt;.Some</c> and <c>SwiftResult.ExtractPayloadValue</c> (<c>.Success</c>).
///
/// Those getters copy a non-class payload (an <see cref="ISwiftStruct"/> like the SafeHandle-backed
/// <c>TrackedRefStruct</c>, mirroring String/Array/Dictionary COW storage) out of the wire carrier
/// into a fresh wrapper that value-witness-destroys on Dispose. The source payload outlives the
/// extraction, so the copy must take a value-witness retain — otherwise disposing the extracted
/// wrapper over-releases the embedded ref and prematurely frees an object the source still owns.
///
/// Each fixture stashes the embedded <c>TrackedRef</c> in a Swift global that holds it past the
/// extraction. After disposing the extracted wrapper (and the carrier), exactly ONE instance must
/// remain live — the one the global owns. Under the under-retain bug the extracted Dispose
/// over-releases that shared instance to zero, so the live count reads 0 instead of 1. This is
/// synchronous (no finalizer timing), making it a hard deterministic gate rather than the
/// intermittent double-free SIGSEGV the bug otherwise produced via the GC finalizer thread.
/// </summary>
public class ExtractionRetainProbeTests : TestBase
{
    public ExtractionRetainProbeTests(TestResults results) : base(results) { }

    private static void DrainFinalizers()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    /// <summary>
    /// <c>SwiftOptional&lt;TrackedRefStruct&gt;.Some</c> extraction: the generated factory creates
    /// the carrier, extracts <c>.Some</c>, and disposes the carrier internally. The embedded ref is
    /// also held by a Swift global. Disposing the extracted wrapper must leave the global's instance
    /// alive (live == 1); an under-retained copy over-releases it to 0.
    /// </summary>
    public void TestOptionalSomeExtractionDoesNotOverReleaseSharedRef()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        var extracted = TestLibFunctions.StashSharedRefAndReturnOptionalStruct(42);
        extracted?.Dispose();
        DrainFinalizers();

        LifetimeTracker.AssertLiveCount(1,
            "SwiftOptional.Some must value-witness-retain the extracted copy; the Swift global still owns the shared ref");

        TestLibFunctions.ClearSharedExtractionRef();
        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(0,
            "clearing the Swift global must release the last retain on the shared ref");

        TestLogger.Info("SwiftOptional.Some: extracted-copy Dispose left the global-owned ref intact");
    }

    /// <summary>
    /// <c>SwiftResult&lt;TrackedRefStruct, _&gt;.Success</c> extraction: the generated factory returns
    /// the carrier; the test extracts <c>.Success</c> and disposes BOTH the extracted wrapper and the
    /// carrier. The embedded ref is also held by a Swift global. After both disposals the global's
    /// instance must remain live (live == 1); an under-retained copy over-releases it to 0.
    /// </summary>
    public void TestResultSuccessExtractionDoesNotOverReleaseSharedRef()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        using (var result = TestLibFunctions.StashSharedRefAndReturnResultStruct(7))
        {
            if (result.IsSuccess)
                result.Success?.Dispose();
        }
        DrainFinalizers();

        LifetimeTracker.AssertLiveCount(1,
            "SwiftResult.Success must value-witness-retain the extracted copy; the Swift global still owns the shared ref");

        TestLibFunctions.ClearSharedExtractionRef();
        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(0,
            "clearing the Swift global must release the last retain on the shared ref");

        TestLogger.Info("SwiftResult.Success: extracted-copy + carrier Dispose left the global-owned ref intact");
    }

    /// <summary>
    /// <c>SwiftOptional&lt;TrackedRefEnum&gt;.Some</c> extraction of a COMPLEX (payload-carrying) enum
    /// whose <c>.present</c> case embeds a <c>TrackedRef</c>. A complex enum is emitted as
    /// <c>ISwiftObject, ISwiftStruct</c> and its <c>NewFromPayload</c> ADOPTS the heap copy directly —
    /// the same ADOPT shape as the SafeHandle-backed struct, but via the distinct complex-enum emission,
    /// so this drives the value-witness retain path for that projection. The embedded ref is also held
    /// by a Swift global; disposing the extracted enum must leave it live (live == 1).
    /// </summary>
    public void TestOptionalSomeComplexEnumDoesNotOverReleaseSharedRef()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        var extracted = TestLibFunctions.StashSharedRefAndReturnOptionalEnum(42);
        extracted?.Dispose();
        DrainFinalizers();

        LifetimeTracker.AssertLiveCount(1,
            "SwiftOptional.Some must value-witness-retain the extracted complex-enum copy; the Swift global still owns the embedded ref");

        TestLibFunctions.ClearSharedExtractionRef();
        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(0,
            "clearing the Swift global must release the last retain on the embedded ref");

        TestLogger.Info("SwiftOptional.Some (complex enum): extracted-copy Dispose left the global-owned ref intact");
    }

    /// <summary>
    /// <c>SwiftResult&lt;TrackedRefEnum, _&gt;.Success</c> companion to
    /// <see cref="TestOptionalSomeComplexEnumDoesNotOverReleaseSharedRef"/>, driving the complex-enum
    /// ADOPT shape through the Result extraction path.
    /// </summary>
    public void TestResultSuccessComplexEnumDoesNotOverReleaseSharedRef()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        using (var result = TestLibFunctions.StashSharedRefAndReturnResultEnum(7))
        {
            if (result.IsSuccess)
                result.Success?.Dispose();
        }
        DrainFinalizers();

        LifetimeTracker.AssertLiveCount(1,
            "SwiftResult.Success must value-witness-retain the extracted complex-enum copy; the Swift global still owns the embedded ref");

        TestLibFunctions.ClearSharedExtractionRef();
        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(0,
            "clearing the Swift global must release the last retain on the embedded ref");

        TestLogger.Info("SwiftResult.Success (complex enum): extracted-copy + carrier Dispose left the global-owned ref intact");
    }

    /// <summary>
    /// <c>Optional&lt;(TrackedRef, String)&gt;.Some</c> per-element extraction. Because the carrier's
    /// tuple metadata is built from the wrapper element types (class → <c>TrackedRef</c>, String →
    /// <c>SwiftString</c>) the carrier owns its class slot's <c>+1</c> and releases it on destroy; the
    /// per-element extraction hands the caller a self-owning <c>TrackedRef</c> (+1) which the test
    /// disposes. The String element is extracted self-owning (+1) and disposed in-place by the glue
    /// after <c>ToString()</c>. The embedded class ref is also stashed in a Swift global: after
    /// disposing the returned wrapper, exactly one instance must remain (the global's). If the carrier
    /// instead lowered the class slot to <c>IntPtr</c> (POD metadata), neither the carrier copy nor its
    /// destroy would touch the class ref and the wire <c>+1</c> would leak — this asserts it does not.
    /// </summary>
    public void TestOptionalSomeTupleExtractionDoesNotOverReleaseSharedRef()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        var extracted = TestLibFunctions.StashSharedRefAndReturnOptionalTuple(42);
        extracted?.Item1.Dispose();
        DrainFinalizers();

        LifetimeTracker.AssertLiveCount(1,
            "Optional<(class, String)>.Some must independently retain the escaping class element; the Swift global still owns the shared ref");

        TestLibFunctions.ClearSharedExtractionRef();
        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(0,
            "clearing the Swift global must release the last retain on the shared ref");

        TestLogger.Info("Optional<(class, String)>.Some: returned-wrapper Dispose left the global-owned ref intact");
    }

    /// <summary>
    /// <c>Result&lt;(TrackedRef, String), _&gt;.Success</c> companion to
    /// <see cref="TestOptionalSomeTupleExtractionDoesNotOverReleaseSharedRef"/>, driving the same
    /// per-element tuple extraction through <c>SwiftResult.ExtractPayloadValue</c>. The success tuple
    /// is returned raw as <c>(TrackedRef, SwiftString)</c>: the carrier's wrapper-typed tuple metadata
    /// extracts BOTH elements self-owning (+1) — a class <c>TrackedRef</c> and a <c>SwiftString</c> —
    /// so the consumer disposes both, then disposes the carrier. The shared class ref must survive at
    /// live == 1 (the Swift global). An under-retain in <c>ExtractCopiedElement</c> over-releases the
    /// shared storage; an over-retain leaks it — both surface against the global's balanced count.
    /// </summary>
    public void TestResultSuccessTupleExtractionDoesNotOverReleaseSharedRef()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        using (var result = TestLibFunctions.StashSharedRefAndReturnResultTuple(7))
        {
            if (result.IsSuccess)
            {
                // Access .Success ONCE (each access re-extracts). Both elements arrive self-owning:
                // the class element (Item1) as a +1 TrackedRef and the String element (Item2) as a
                // +1 SwiftString. Dispose both.
                var tuple = result.Success;
                tuple.Item1.Dispose();
                tuple.Item2.Dispose();
            }
        }
        DrainFinalizers();

        LifetimeTracker.AssertLiveCount(1,
            "Result<(class, String),_>.Success per-element extraction must balance: borrowed class + disposed String leave the global-owned ref alive");

        TestLibFunctions.ClearSharedExtractionRef();
        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(0,
            "clearing the Swift global must release the last retain on the shared ref");

        TestLogger.Info("Result<(class, String),_>.Success: extracted String + carrier Dispose left the global-owned ref intact");
    }

    /// <summary>
    /// SwiftString MOVE-bitwise extraction shape. <c>SwiftString</c>'s from-handle constructor
    /// (<c>ISwiftMovesPayloadOnConstruction</c>) allocates its own buffer and bitwise-copies the
    /// temporary, transferring the bridge-object retain WITHOUT taking a new one — so the extraction
    /// must NOT value-witness-destroy the temporary (that would over-release the shared string
    /// storage). The generated <c>Optional&lt;String&gt;</c>/<c>Result&lt;String, _&gt;</c> factories
    /// project to a managed <c>string</c>, so the MOVE shape is exercised <i>internally</i>: the
    /// factory builds <c>SwiftOptional&lt;SwiftString&gt;</c>, extracts <c>.Some</c> through
    /// <c>MarshalExtractedPayloadValue</c> (the bitwise move), then bridges to <c>string</c> and
    /// disposes the temporary SwiftString. A tight loop surfaces any over-release as a crash;
    /// correctness of the round-tripped value confirms the payload survived the move.
    /// </summary>
    public void TestStringMovePathExtractionDoesNotOverRelease()
    {
        const int iterations = 256;
        for (int i = 0; i < iterations; i++)
        {
            string? optValue = TestLibFunctions.MakeOptionalString(true, i);
            if (optValue != $"tracked-{i}")
                throw new Exception($"Optional<String> round-trip mismatch at {i}: got '{optValue ?? "<null>"}'");

            using (var result = TestLibFunctions.MakeResultString(i))
            {
                if (!result.IsSuccess)
                    throw new Exception($"Result<String> unexpectedly failed at {i}");
                // result.Success projects to SwiftString here (SwiftResult<SwiftString, _>); extracting
                // it drives the MOVE-bitwise SwiftString from-handle ctor. Dispose the extracted wrapper
                // to exercise the full extract+dispose cycle the over-release bug would crash on.
                using (var s = result.Success)
                {
                    string resultValue = s?.ToString() ?? "<null>";
                    if (resultValue != $"tracked-{i}")
                        throw new Exception($"Result<String> round-trip mismatch at {i}: got '{resultValue}'");
                }
            }
        }

        string? none = TestLibFunctions.MakeOptionalString(false, 0);
        if (none != null)
            throw new Exception($"Optional<String>.none should bridge to C# null; got '{none}'");

        DrainFinalizers();
        TestLogger.Info($"SwiftString MOVE path: {iterations} extract+dispose round-trips survived with correct values");
    }

    /// <summary>
    /// Value-type <c>ISwiftObject</c> struct extraction. A frozen blittable POD struct projects to a
    /// C# <c>struct : ISwiftObject</c> (NOT <c>ISwiftStruct</c>); its <c>NewFromPayload</c> returns the
    /// value via <c>*(T*)handle</c> and its <c>SwiftHandle</c> is the throwing default. The extraction
    /// cleanup must free the temporary buffer by value WITHOUT comparing <c>.SwiftHandle</c> — doing so
    /// throws <c>NotSupportedException</c> ("Only heap-backed Swift types support handle extraction").
    /// This path is otherwise covered only by a <c>[SkipOnSimulator]</c> device test, so this is the
    /// sim-runnable guard. Round-trip correctness confirms the by-value read; any handle comparison in
    /// the cleanup would surface here as a thrown exception.
    /// <para>
    /// The generated <c>MakeOptionalPodPoint</c> unwraps the optional inline (<c>Unsafe.ReadUnaligned</c>
    /// straight off the wire buffer) and so never reaches <c>SwiftOptional&lt;T&gt;.Some</c>; only the
    /// <c>MakeResultPodPoint</c> companion drives <c>SwiftResult.ExtractPayloadValue</c> → the helper.
    /// To genuinely exercise the <c>SwiftOptional&lt;T&gt;.Some</c> read-by-value branch as well, the
    /// loop also round-trips through <c>SwiftOptional&lt;ExtractionPodPoint&gt;.NewSome(...).Some</c>.
    /// </para>
    /// </summary>
    public void TestValueTypeStructExtractionReadsByValue()
    {
        const int iterations = 64;
        for (int i = 0; i < iterations; i++)
        {
            var opt = TestLibFunctions.MakeOptionalPodPoint(true, i, i * 2);
            if (opt is null)
                throw new Exception($"Optional<ExtractionPodPoint> unexpectedly null at {i}");
            if (opt.Value.X != i || opt.Value.Y != i * 2)
                throw new Exception($"Optional<ExtractionPodPoint> round-trip mismatch at {i}: ({opt.Value.X}, {opt.Value.Y})");

            // Drive SwiftOptional<T>.Some explicitly — the generated factory above inlines the unwrap
            // and never reaches it. This is the value-type read-by-value branch of the helper: the
            // struct is read by value and its SwiftHandle (the throwing default) must NOT be compared.
            using (var carrier = SwiftOptional<ExtractionPodPoint>.NewSome(opt.Value))
            {
                var extracted = carrier.Some;
                if (extracted.X != i || extracted.Y != i * 2)
                    throw new Exception($"SwiftOptional<ExtractionPodPoint>.Some round-trip mismatch at {i}: ({extracted.X}, {extracted.Y})");
            }

            using (var result = TestLibFunctions.MakeResultPodPoint(i, i * 2))
            {
                if (!result.IsSuccess)
                    throw new Exception($"Result<ExtractionPodPoint> unexpectedly failed at {i}");
                var p = result.Success;
                if (p.X != i || p.Y != i * 2)
                    throw new Exception($"Result<ExtractionPodPoint> round-trip mismatch at {i}: ({p.X}, {p.Y})");
            }
        }

        var none = TestLibFunctions.MakeOptionalPodPoint(false, 0, 0);
        if (none != null)
            throw new Exception("Optional<ExtractionPodPoint>.none should bridge to C# null");

        DrainFinalizers();
        TestLogger.Info($"value-type struct path: {iterations} Optional/Result extractions read by value without a SwiftHandle compare");
    }

    /// <summary>
    /// Bare-<c>ISwiftObject</c> <b>reference-type</b> wrapper ADOPT shape — the SwiftUI value wrappers
    /// (<c>Color</c>, <c>AnyView</c>, <c>Image</c>, <c>Font</c>, <c>Animation</c>, <c>EdgeInsets</c>).
    /// These are Swift structs projected to a <c>sealed class : ISwiftObject</c> <i>without</i>
    /// <c>ISwiftStruct</c>, whose <c>NewFromPayload</c> adopts the heap buffer into a SafeHandle. They
    /// are hand-written runtime types the generator never emits, so they cannot be reached through a
    /// generated factory; <see cref="ColorLikeWrapper"/> mirrors the shape over the real
    /// <c>TrackedRefStruct</c> metadata so the extraction ARC balance is observable.
    ///
    /// The extraction must (a) value-witness-retain the copy — the wrapper is non-POD — and (b)
    /// recognize the ADOPT shape and leave the adopted buffer alone. The earlier <c>ISwiftStruct</c>-only
    /// gate did neither: it skipped the retain (under-releasing the shared ref on dispose) and freed the
    /// buffer the wrapper had adopted (use-after-free / double-free).
    ///
    /// The single embedded <c>TrackedRef</c> object is shared by reference-count across the wrapper, the
    /// carrier copy, and the extracted copy. <c>wrapper</c> is held as an independent surviving owner
    /// (the analogue of the Swift-global owner in the sibling probes): after extracting <c>.Some</c> and
    /// disposing both the extracted copy and the carrier, the object must remain alive (live == 1)
    /// because <c>wrapper</c> still owns it. Under the bug the extraction under-retains and the dangling
    /// dispose over-releases, driving the shared object to deinit early (live == 0).
    /// </summary>
    public void TestBareISwiftObjectReferenceWrapperAdoptDoesNotOverRelease()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        ColorLikeWrapper wrapper;
        using (var seed = TestLibFunctions.MakeTrackedRefStruct(99))
        {
            wrapper = ColorLikeWrapper.FromTrackedRefStruct(seed);
        }
        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(1,
            "the wrapper owns the shared ref once the seed struct is disposed");

        // Round-trip through the carrier. .Some drives MarshalExtractedPayloadValue down the
        // reference-backed-but-not-ISwiftStruct ADOPT path: it must take an independent +1 and leave the
        // adopted buffer intact. wrapper is held across the whole round-trip.
        var carrier = SwiftOptional<ColorLikeWrapper>.NewSome(wrapper);
        var extracted = carrier.Some;
        extracted.Dispose();
        carrier.Dispose();
        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(1,
            "wrapper still owns the shared ref; an under-retain/over-release would have deinit'd it to 0");

        wrapper.Dispose();
        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(0,
            "disposing the last owner releases the final ref");

        TestLogger.Info("bare-ISwiftObject reference wrapper ADOPT: shared ref survived extract+dispose, balanced ARC");
    }

    /// <summary>
    /// COPY cleanup branch — the other side of the ADOPT probes. A <c>@frozen</c> struct carrying a
    /// <c>TrackedRef</c> (<c>FrozenTrackedRefStruct</c>) projects to the ClassWithBufferStruct path: a
    /// C# <c>class : ISwiftStruct</c> whose <c>NewFromPayload</c> allocates its <b>own</b> buffer and
    /// <c>InitializeWithCopy</c>s into it (taking a fresh <c>+1</c>) — the same COPY shape as
    /// <c>SwiftArray</c>/<c>SwiftDictionary</c>/<c>SwiftSet</c>. Because the wrapper's
    /// <c>SwiftHandle != heapCopy</c> and it is not <c>ISwiftMovesPayloadOnConstruction</c>, the helper
    /// must value-witness-<c>Destroy</c> the orphaned temporary's <c>+1</c> and then free it.
    ///
    /// The single embedded <c>TrackedRef</c> is shared by reference-count across <c>seed</c>, the
    /// carrier, and the extracted copy. <c>seed</c> is held as the surviving owner: after extracting
    /// <c>.Some</c> and disposing both the extracted copy and the carrier, the object must remain live
    /// (live == 1). A leaked temporary <c>+1</c> (COPY branch failing to Destroy, or misclassifying as
    /// MOVE) would keep it live past <c>seed</c>'s dispose (final live == 1, not 0); an under-retain
    /// would deinit it to 0 at the first assert.
    /// </summary>
    public void TestCopyShapeExtractionDestroysOrphanedTemporary()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        var seed = TestLibFunctions.MakeFrozenTrackedRefStruct(123);
        var carrier = SwiftOptional<FrozenTrackedRefStruct>.NewSome(seed);
        var extracted = carrier.Some;
        extracted.Dispose();
        carrier.Dispose();
        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(1,
            "the COPY wrapper took its own +1 and the helper destroyed the orphaned temporary; seed still owns the shared ref");

        seed.Dispose();
        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(0,
            "disposing the last owner releases the final ref; a leaked extraction +1 would have left it live at 1");

        TestLogger.Info("COPY branch: orphaned temporary +1 destroyed; shared ref balanced across extract+dispose");
    }
}
