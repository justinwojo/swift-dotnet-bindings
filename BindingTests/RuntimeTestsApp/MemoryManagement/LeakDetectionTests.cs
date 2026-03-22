// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// Tests for DeinitTracker and struct-with-ref-field patterns.
/// Exercises Buffer vs SafeHandle emission paths for structs containing
/// reference type fields at various offsets.
/// </summary>
public class LeakDetectionTests : TestBase
{
    public LeakDetectionTests(TestResults results) : base(results) { }

    #region FrozenStructWithRef (ClassWithBufferStruct)

    public void TestFrozenStructWithRefCreation()
    {
        using var fs = new FrozenStructWithRef(42);
        AssertEqual(42, fs.GetValue(), "Frozen struct with ref preserves value");
    }

    [SkipOnSimulator("PassThroughFrozenWithRef uses CallConvSwift (no @_cdecl wrapper)")]
    public void TestFrozenStructWithRefPassThrough()
    {
        using var fs = new FrozenStructWithRef(99);
        using var result = TestLibFunctions.PassThroughFrozenWithRef(fs);
        AssertEqual(99, result.GetValue(), "Pass-through preserves frozen struct value");
    }

    #endregion

    #region NestedFrozenStructWithRef

    public void TestNestedFrozenStructWithRefCreation()
    {
        using var nfs = new NestedFrozenStructWithRef(77);
        AssertEqual(77, nfs.GetValue(), "Nested frozen struct preserves value");
    }

    [SkipOnSimulator("PassThroughNestedFrozenWithRef uses CallConvSwift (no @_cdecl wrapper)")]
    public void TestNestedFrozenStructWithRefPassThrough()
    {
        using var nfs = new NestedFrozenStructWithRef(55);
        using var result = TestLibFunctions.PassThroughNestedFrozenWithRef(nfs);
        AssertEqual(55, result.GetValue(), "Pass-through preserves nested struct value");
    }

    #endregion

    #region RetainCycles (Unsupported)

    [Skip("weak/unowned references not supported by generator")]
    public void TestStrongCycleCreation()
    {
        // StrongNodeA/B use weak/unowned — not yet supported
    }

    [Skip("weak/unowned references not supported by generator")]
    public void TestTreeCycleWithWeakParent()
    {
        // CycleTreeNode uses weak parent — not yet supported
    }

    [Skip("weak/unowned references not supported by generator")]
    public void TestOwnerResourceUnowned()
    {
        // ResourceOwner/OwnedResource use unowned — not yet supported
    }

    [Skip("weak/unowned references not supported by generator")]
    public void TestDelegatePatternWeakRef()
    {
        // DelegateHolder uses weak delegate — not yet supported
    }

    #endregion
}
