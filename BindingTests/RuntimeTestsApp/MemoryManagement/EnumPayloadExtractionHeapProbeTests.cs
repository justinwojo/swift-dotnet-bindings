// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// Counts native heap blocks across repeated payload extraction from a complex enum whose case
/// carries a <c>String</c>. <c>TryGet…</c> copies the payload element into a temporary buffer and
/// builds the managed wrapper from it; <c>SwiftString</c> copies that buffer into one of its own, so
/// the temporary has to be freed by the extraction itself. A buffer left behind costs one malloc
/// block per call, which neither the lifetime tracker (it counts wrapper objects) nor a Swift
/// deinit counter (a small string has no storage object) can see — so this probe reads the
/// malloc zones' live block count directly.
/// </summary>
public class EnumPayloadExtractionHeapProbeTests : TestBase
{
    public EnumPayloadExtractionHeapProbeTests(TestResults results) : base(results) { }

    private const int Warmup = 2000;
    private const int Iterations = 20000;

    private static int ExtractRepeatedly(EquatablePayloadEnum value, int count)
    {
        int matched = 0;
        for (int i = 0; i < count; i++)
        {
            if (value.TryGetLabelled(out var name, out var n) && name == "seven" && n == 7)
                matched++;
        }
        return matched;
    }

    public void TestStringPayloadExtractionFreesItsTemporaryBuffer()
    {
        using var value = TestLibFunctions.EquatablePayloadLabelled("seven", 7);

        AssertEqual(Warmup, ExtractRepeatedly(value, Warmup), "warmup extractions read the payload");
        long before = MallocProbe.LiveBlocksAfterCollection();

        AssertEqual(Iterations, ExtractRepeatedly(value, Iterations), "every extraction reads the payload");
        long after = MallocProbe.LiveBlocksAfterCollection();

        long growth = after - before;
        TestLogger.Info($"string payload TryGet: {Iterations} extractions, malloc blocks {before} -> {after} (growth {growth})");
        // One leaked buffer per call would add Iterations blocks; a quarter of that leaves ample
        // room for unrelated allocator churn while still failing a per-call leak outright.
        AssertTrue(growth < Iterations / 4,
            $"malloc live blocks grew by {growth} over {Iterations} extractions; each TryGet leaves a buffer behind");
    }

    private static int ExtractGenericRepeatedly(Holder<SwiftString> holder, int count)
    {
        int matched = 0;
        for (int i = 0; i < count; i++)
        {
            if (holder.TryGetWrapped(out var payload))
            {
                using (payload)
                {
                    if (payload!.ToString() == "seven")
                        matched++;
                }
            }
        }
        return matched;
    }

    // The same extraction through a generic enum's type-parameter payload, where the payload
    // type is only known at run time.
    public void TestGenericStringPayloadExtractionFreesItsTemporaryBuffer()
    {
        using var holder = TestLibFunctions.MakeWrappedString("seven");

        AssertEqual(Warmup, ExtractGenericRepeatedly(holder, Warmup), "warmup extractions read the payload");
        long before = MallocProbe.LiveBlocksAfterCollection();

        AssertEqual(Iterations, ExtractGenericRepeatedly(holder, Iterations), "every extraction reads the payload");
        long after = MallocProbe.LiveBlocksAfterCollection();

        long growth = after - before;
        TestLogger.Info($"generic string payload TryGet: {Iterations} extractions, malloc blocks {before} -> {after} (growth {growth})");
        AssertTrue(growth < Iterations / 4,
            $"malloc live blocks grew by {growth} over {Iterations} extractions; each TryGet leaves a buffer behind");
    }
}
