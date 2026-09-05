// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// Accounting probes for the indirect-result cleanup: a Swift function returning an
/// address-only value writes it into a caller-allocated buffer at +1, so the caller owns
/// the storage AND the retains the value holds. How that balances is decided by the
/// managed carrier's DECLARED <c>PayloadConstructionSemantics</c> — adopt the buffer, copy
/// out of it, or read it inline — not by the carrier's structural shape.
///
/// Two carriers with opposite declarations are probed side by side:
///   - <c>TrackedRefStruct</c> (non-frozen struct → SafeHandle, semantics Adopt): the
///     managed wrapper takes the buffer over, so the seam must NOT free or destroy it.
///     Disposing the wrapper is what releases the embedded <c>TrackedRef</c>. A cleanup
///     that "helpfully" destroyed an adopted buffer would double-free instead of leaking,
///     so these loops are the regression gate on the release seam's Adopt arm.
///   - <c>Foundation.Data</c> (inline struct, semantics Inline): the seam reads the bytes
///     into a managed <c>byte[]</c> and the Swift value is fully consumed there, so the
///     seam owns releasing it. A payload past Data's inline threshold lives in a separate
///     heap allocation the buffer holds the only reference to — freeing the buffer's
///     storage without a value-witness Destroy orphans that allocation on every call.
///
/// Every probe is accounting-based, because a single correct round trip cannot tell an
/// orphaned +1 from a balanced one: the Swift fixtures record into the same counters
/// <see cref="LifetimeTracker"/> reads (via <c>TrackedRef.deinit</c> and, for Data, a
/// <c>.custom</c> deallocator on the out-of-line storage), so a leak surfaces as a live
/// count that never returns to zero rather than as "does not crash".
/// </summary>
public class OwnedReturnCleanupTests : TestBase
{
    public OwnedReturnCleanupTests(TestResults results) : base(results) { }

    /// <summary>Iterations for the Data probes — sustained enough that a per-call orphan
    /// of the 64 KB payload would be hundreds of megabytes, and the interim bound below
    /// trips long before that.</summary>
    private const int DataIterations = 10_000;

    /// <summary>How often the Data loops re-read the live count. A balanced seam releases
    /// synchronously inside the call, so the count is back at zero every iteration; the
    /// interim check turns a regression into a fast accounting failure instead of letting
    /// the loop run the footprint up first.</summary>
    private const int DataCheckInterval = 500;

    /// <summary>Slack for the interim bound: a couple of storages may legitimately be
    /// in flight across the read. Anything past this is a per-call orphan, not timing.</summary>
    private const int DataLiveSlack = 4;

    private static void DrainFinalizers()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    // MARK: - Non-frozen struct (Adopt carrier) through every member shape

    /// <summary>
    /// Class parent, instance method / static method / property getter: each builds its own
    /// indirect-result plan, so each gets its own cleanup. Disposing the returned wrapper must
    /// drive the embedded TrackedRef count back to zero; a seam that freed or destroyed the
    /// adopted buffer would instead crash or under-count here.
    /// </summary>
    public void TestClassParentIndirectReturnsBalanceAcrossMemberShapes()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        DriveClassParent(500);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("class-parent indirect returns (instance, static, property) must balance the buffer's +1");
        TestLogger.Info("class parent: 500 x (instance + static + property) owned returns all released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DriveClassParent(int iterations)
    {
        var factory = new OwnedReturnClassFactory();
        try
        {
            for (int i = 0; i < iterations; i++)
            {
                factory.MakeBox(i).Dispose();
                OwnedReturnClassFactory.MakeBoxStatic(i).Dispose();
                factory.BoxProperty.Dispose();
            }
        }
        finally
        {
            factory.Dispose();
        }
    }

    /// <summary>
    /// Struct parent: <c>self</c> arrives as an opaque payload pointer rather than a class
    /// reference, which is a separate emission path with its own cleanup.
    /// </summary>
    public void TestStructParentIndirectReturnsBalanceAcrossMemberShapes()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        DriveStructParent(500);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("struct-parent indirect returns (method, property) must balance the buffer's +1");
        TestLogger.Info("struct parent: 500 x (method + property) owned returns all released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DriveStructParent(int iterations)
    {
        var factory = new OwnedReturnStructFactory(3);
        try
        {
            for (int i = 0; i < iterations; i++)
            {
                factory.MakeBox(i).Dispose();
                factory.BoxProperty.Dispose();
            }
        }
        finally
        {
            factory.Dispose();
        }
    }

    /// <summary>
    /// Throwing producer, both arms interleaved. The success arm must balance exactly as the
    /// non-throwing members do. The throwing arm is the harder half: Swift never initializes
    /// the indirect result, so the buffer still holds whatever the allocator handed back —
    /// a cleanup that ran a value-witness Destroy unconditionally would dereference those
    /// bytes. Reaching the assertion at all is the observation; the counter proves the
    /// success arm was not quietly skipped along with it.
    /// </summary>
    public void TestThrowingProducerBalancesSuccessArmAndSurvivesFailureArm()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        int thrown = DriveThrowingProducer(500);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("throwing producer's success arm must balance the buffer's +1");
        AssertEqual(500, thrown, "every failure-arm call must surface its Swift error");
        TestLogger.Info($"throwing producer: 500 successes released, {thrown} failures raised without touching the uninitialized buffer");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int DriveThrowingProducer(int iterations)
    {
        int thrown = 0;
        var factory = new OwnedReturnClassFactory();
        try
        {
            for (int i = 0; i < iterations; i++)
            {
                factory.MakeBoxOrThrow(i, false).Dispose();

                try
                {
                    factory.MakeBoxOrThrow(i, true).Dispose();
                }
                catch (Exception)
                {
                    thrown++;
                }
            }
        }
        finally
        {
            factory.Dispose();
        }
        return thrown;
    }

    /// <summary>
    /// Abandon-and-collect variant of the class-parent loop: the buffer is adopted by the
    /// wrapper's SafeHandle, so with no explicit <c>Dispose</c> the finalizer is what runs
    /// value-witness Destroy. This is the counterpart assertion to the disposed loops — it
    /// fails if the seam released a buffer the wrapper still owns (the handle would then
    /// finalize over freed storage) or if adoption stopped happening at all.
    /// </summary>
    public void TestAbandonedOwnedReturnsAreReleasedByFinalizer()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        AbandonClassParentReturns(300);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("abandoned owned returns must be released when the adopting handle finalizes");
        TestLogger.Info("class parent: 300 abandoned owned returns released via finalization");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AbandonClassParentReturns(int iterations)
    {
        var factory = new OwnedReturnClassFactory();
        try
        {
            for (int i = 0; i < iterations; i++)
                _ = factory.MakeBox(i);
        }
        finally
        {
            factory.Dispose();
        }
    }

    // MARK: - Copy-declaring carrier through the bare-generic return arm

    /// <summary>
    /// The third declaration, and the one the two carriers above cannot reach. A generic Swift
    /// function's indirect result has no Swift declaration shape to read at emission time — the
    /// wrapper decides at runtime from <c>typeof(T)</c> — and <c>SwiftArray</c> declares
    /// <c>Copy</c>: its <c>NewFromPayload</c> runs <c>InitializeWithCopy</c>, taking its own
    /// <c>+1</c> and leaving the wire buffer's original orphaned. So the seam owes that buffer a
    /// value-witness Destroy on top of the free, and the orphan is exactly one retain on each
    /// element — observable here because the elements are <c>TrackedRef</c>s whose <c>deinit</c>
    /// feeds the tracker.
    /// </summary>
    public void TestGenericIndirectReturnReleasesACopyDeclaringCarrier()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        DriveGenericCopyCarrierReturns(500);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("generic return of a Copy-declaring carrier must destroy the wire buffer's orphaned +1");
        TestLogger.Info("generic Copy carrier: 500 x SwiftArray<TrackedRef> round trips released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DriveGenericCopyCarrierReturns(int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            using var element = new TrackedRef(i);
            using var source = new SwiftArray<TrackedRef>(new[] { element });
            using var echoed = TestLibFunctions.Identity(source);

            if (echoed.Count != 1)
                throw new Exception($"generic Copy-carrier return lost its element at iteration {i}: {echoed.Count} elements");
        }
    }

    // MARK: - Owned Foundation.Data returns (Inline carrier, consumed at the seam)

    /// <summary>
    /// Concrete-specialization (CSM) return path with an owned <c>Data</c> whose payload is
    /// past the inline threshold, so the bytes live in a separate heap allocation. The seam
    /// copies them into a <c>byte[]</c> and must then release the Swift value; releasing only
    /// the result buffer's storage orphans the allocation once per call. The Swift fixture
    /// hands that allocation a <c>.custom</c> deallocator wired to the same counters, so the
    /// orphan is an exact live count, not a footprint heuristic.
    /// </summary>
    public void TestConcreteSpecializationDataReturnReleasesOutOfLineStorage()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        int expectedBytes = (int)TestLibFunctions.GetTrackedDataPayloadByteCount();
        DriveConcreteSpecializationDataReturns(DataIterations, expectedBytes);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("CSM-specialized owned Data return must release the value's out-of-line storage");
        TestLogger.Info($"CSM Data return: {DataIterations} x {expectedBytes} B payloads all released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DriveConcreteSpecializationDataReturns(int iterations, int expectedBytes)
    {
        var vault = new OwnedDataVault();
        var seedA = new OwnedDataSeedA(11);
        var seedB = new OwnedDataSeedB(22);
        try
        {
            for (int i = 0; i < iterations; i++)
            {
                // Two conformers: the generic member is specialized once per conformer, so
                // each specialization emits its own return marshalling.
                byte[] a = vault.Produce(seedA);
                byte[] b = vault.Produce(seedB);

                if (a.Length != expectedBytes || b.Length != expectedBytes)
                    throw new Exception($"owned Data return lost its payload at iteration {i}: {a.Length}/{b.Length} bytes, expected {expectedBytes}");
                if (a[0] != 11 || b[0] != 22)
                    throw new Exception($"owned Data return carried the wrong seed at iteration {i}: {a[0]}/{b[0]}");

                if ((i + 1) % DataCheckInterval == 0)
                    AssertDataStorageBounded(i + 1);
            }
        }
        finally
        {
            seedB.Dispose();
            seedA.Dispose();
            vault.Dispose();
        }
    }

    /// <summary>
    /// The same owned <c>Data</c> return on the ordinary <c>@_cdecl</c> path — a plain global
    /// function rather than a specialized generic. It is a separate cleanup chain from the CSM
    /// one above (the plan builder's indirect-result arms, not the specialization emitter's
    /// return marshalling), and it is the shape the audit's CryptoKit <c>open</c> reports have:
    /// the body applies the projection, so the Swift value is consumed inside the seam and the
    /// seam owes it a value-witness Destroy before freeing the buffer.
    /// </summary>
    public void TestOrdinaryCdeclDataReturnReleasesOutOfLineStorage()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        int expectedBytes = (int)TestLibFunctions.GetTrackedDataPayloadByteCount();
        DriveOrdinaryCdeclDataReturns(DataIterations, expectedBytes);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("ordinary @_cdecl owned Data return must release the value's out-of-line storage");
        TestLogger.Info($"cdecl Data return: {DataIterations} x {expectedBytes} B payloads all released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DriveOrdinaryCdeclDataReturns(int iterations, int expectedBytes)
    {
        for (int i = 0; i < iterations; i++)
        {
            byte tag = (byte)(i & 0x7F);
            byte[] bytes = TestLibFunctions.MakeOwnedTrackedData(tag);

            if (bytes.Length != expectedBytes)
                throw new Exception($"owned Data return lost its payload at iteration {i}: {bytes.Length} bytes, expected {expectedBytes}");
            if (bytes[0] != tag || bytes[expectedBytes - 1] != tag)
                throw new Exception($"owned Data return carried the wrong bytes at iteration {i}: {bytes[0]}/{bytes[expectedBytes - 1]}, expected {tag}");

            if ((i + 1) % DataCheckInterval == 0)
                AssertDataStorageBounded(i + 1);
        }
    }

    /// <summary>
    /// The opposite ownership answer on the same carrier, and the reason the release seam takes
    /// an "does the value escape?" flag rather than always destroying. A property emits a private
    /// getter returning the RAW <c>Data</c> plus a public property that projects it, so the value
    /// is read AFTER the getter's cleanup has run and the managed struct aliases the buffer's
    /// payload. Destroying there is a use-after-free, not a leak fix — and a use-after-free of a
    /// 64 KB heap allocation reads back as corrupted bytes (or a crash) rather than as a counter.
    ///
    /// So this probe asserts both halves of that contract: every byte the caller reads is intact
    /// after the getter's cleanup, and the storage is provably still alive at read time — the
    /// tracker's live count rises by exactly one per call. The residual orphan that second
    /// assertion pins is the accessor seam's known ownership gap (the raw carrier escapes with no
    /// managed owner to release it); it is deliberately NOT papered over by destroying here, and
    /// the exact count means any change to the escape decision reds this test.
    /// </summary>
    public void TestAccessorSeamDataReturnEscapesIntactAndUnreleased()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        int expectedBytes = (int)TestLibFunctions.GetTrackedDataPayloadByteCount();
        const int iterations = 64;
        DriveAccessorSeamDataReturns(iterations, expectedBytes);

        var (_, _, live) = LifetimeTracker.GetStats();
        AssertEqual(iterations, live,
            "the accessor seam must leave the escaping Data's payload alive — destroying it would hand the public property a freed allocation");

        // The orphaned storages are this seam's known gap, not this test's residue: clear the
        // counters so the following probes start from a clean sheet.
        LifetimeTracker.Reset();
        TestLogger.Info($"accessor seam: {iterations} x {expectedBytes} B payloads escaped intact and unreleased");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DriveAccessorSeamDataReturns(int iterations, int expectedBytes)
    {
        for (int i = 0; i < iterations; i++)
        {
            byte tag = (byte)(i & 0x7F);
            var box = new OwnedDataAccessorBox(tag);
            try
            {
                byte[] bytes = box.TrackedBytes;

                if (bytes.Length != expectedBytes)
                    throw new Exception($"accessor-seam Data lost its payload at iteration {i}: {bytes.Length} bytes, expected {expectedBytes}");

                // Read the WHOLE payload, not just the ends: a destroyed-then-read allocation
                // is most likely to show damage wherever the allocator reused it first.
                for (int b = 0; b < expectedBytes; b++)
                {
                    if (bytes[b] != tag)
                        throw new Exception($"accessor-seam Data was corrupted at iteration {i}, offset {b}: {bytes[b]}, expected {tag}");
                }
            }
            finally
            {
                box.Dispose();
            }
        }
    }

    /// <summary>
    /// Interim accounting bound for the Data loops. A balanced seam releases the storage
    /// synchronously inside the call, so the live count sits at zero between iterations; a
    /// per-call orphan grows it without bound. Checking as we go keeps a regression from
    /// running the process footprint up before the final assertion is reached.
    /// </summary>
    private void AssertDataStorageBounded(int completedIterations)
    {
        var (_, _, live) = LifetimeTracker.GetStats();
        if (live > DataLiveSlack)
        {
            throw new Exception(
                $"owned Data storage is accumulating: {live} live allocations after {completedIterations} iterations " +
                $"(bound {DataLiveSlack}). The seam is freeing the result buffer without releasing the value inside it.");
        }
    }
}
