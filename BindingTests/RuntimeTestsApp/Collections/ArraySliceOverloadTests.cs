// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collections;

/// <summary>
/// End-to-end coverage for Array/ArraySlice overloads that both project to IEnumerable.
/// The public names distinguish the original Swift collection kind even though the
/// ArraySlice wrapper normalizes its ABI boundary to Array.
/// </summary>
public class ArraySliceOverloadTests : TestBase
{
    public ArraySliceOverloadTests(TestResults results) : base(results) { }

    public void TestArrayOverloadDispatch()
    {
        var result = SliceProcessor.OverloadDispatchWithArrayUInt8(new byte[] { 1, 2, 3 });

        AssertEqual(1_006, result, "Array overload should keep its collision-safe name and dispatch target");
    }

    public void TestArraySliceOverloadDispatch()
    {
        var result = SliceProcessor.OverloadDispatchWithArraySliceUInt8(new byte[] { 1, 2, 3 });

        AssertEqual(2_006, result, "ArraySlice overload should keep its collision-safe name and dispatch target");
    }
}
