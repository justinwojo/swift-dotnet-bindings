// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for EmitterUtility — DeterministicHash8 and FindLastTopLevelComma.
/// </summary>
public class EmitterUtilityTests
{
    #region DeterministicHash8 Tests

    [Fact]
    public void DeterministicHash8_SameInput_ReturnsSameOutput()
    {
        var hash1 = EmitterUtility.DeterministicHash8("TestModule.Loader.handle");
        var hash2 = EmitterUtility.DeterministicHash8("TestModule.Loader.handle");

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void DeterministicHash8_DifferentInputs_ReturnDifferentOutputs()
    {
        var hash1 = EmitterUtility.DeterministicHash8("TestModule.Loader.handle");
        var hash2 = EmitterUtility.DeterministicHash8("TestModule.Loader.process");

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void DeterministicHash8_Returns8CharHexString()
    {
        var hash = EmitterUtility.DeterministicHash8("TestModule.Loader.handle");

        Assert.Equal(8, hash.Length);
        Assert.True(hash.All(c => "0123456789ABCDEF".Contains(c)),
            $"Hash '{hash}' contains non-hex characters");
    }

    [Fact]
    public void DeterministicHash8_EmptyString_DoesNotThrow()
    {
        var hash = EmitterUtility.DeterministicHash8("");

        Assert.Equal(8, hash.Length);
        Assert.True(hash.All(c => "0123456789ABCDEF".Contains(c)));
    }

    #endregion

    #region FindLastTopLevelComma Tests

    [Fact]
    public void FindLastTopLevelComma_SimpleTypes_FindsLastComma()
    {
        // <int, string, void>
        //  0123456789012345678
        var input = "<int, string, void>";
        var result = EmitterUtility.FindLastTopLevelComma(input, input.Length - 1);

        // Last top-level comma is at index 12 (before " void"), not index 4 (before " string")
        Assert.Equal(12, result);
    }

    [Fact]
    public void FindLastTopLevelComma_NestedGeneric_SkipsInner()
    {
        // <SwiftOptional<int>, void> — comma inside <int> should be skipped
        var input = "<SwiftOptional<int>, void>";
        var result = EmitterUtility.FindLastTopLevelComma(input, input.Length - 1);

        Assert.True(result > 0);
        Assert.Equal(',', input[result]);
        // The found comma should be after the closing > of SwiftOptional<int>
        Assert.True(result > input.IndexOf('>'));
    }

    [Fact]
    public void FindLastTopLevelComma_DeepNesting_TopLevelCommaOnly()
    {
        // <Dict<string, int>, void> — comma inside Dict<> should be skipped
        var input = "<Dict<string, int>, void>";
        var result = EmitterUtility.FindLastTopLevelComma(input, input.Length - 1);

        Assert.True(result > 0);
        // Should find the comma between "Dict<string, int>" and "void"
        // not the comma inside "Dict<string, int>"
        var afterComma = input.Substring(result + 1).Trim();
        Assert.StartsWith("void", afterComma);
    }

    [Fact]
    public void FindLastTopLevelComma_NoComma_ReturnsMinus1()
    {
        var input = "<void>";
        var result = EmitterUtility.FindLastTopLevelComma(input, input.Length - 1);

        Assert.Equal(-1, result);
    }

    #endregion
}
