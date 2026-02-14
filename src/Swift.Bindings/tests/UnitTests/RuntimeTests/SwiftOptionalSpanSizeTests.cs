// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Regression tests for SwiftOptional.ComputePayloadSpanSize — ensures the NewSome()
/// span is sized by the inner type (not Optional.Size - 1) to handle extra-inhabitant types.
/// </summary>
public class SwiftOptionalSpanSizeTests
{
    [Fact]
    public void ExtraInhabitant_String_SpanEqualsInnerSize()
    {
        // Optional<String>: optionalSize=16, innerSize=16 (extra inhabitants, no tag byte)
        // Bug (old code): metadata.Size - 1 = 15 → "Span size does not match type size"
        // Fix: innerSize = 16
        var result = SwiftOptional<int>.ComputePayloadSpanSize(16, 16);
        Assert.Equal(16, result);
    }

    [Fact]
    public void ExtraInhabitant_ClassRef_SpanEqualsInnerSize()
    {
        // Optional<ClassType>: optionalSize=8, innerSize=8 (pointer, extra inhabitants)
        var result = SwiftOptional<int>.ComputePayloadSpanSize(8, 8);
        Assert.Equal(8, result);
    }

    [Fact]
    public void ExtraInhabitant_Array_SpanEqualsInnerSize()
    {
        // Optional<Array<T>>: optionalSize=8, innerSize=8 (pointer, extra inhabitants)
        var result = SwiftOptional<int>.ComputePayloadSpanSize(8, 8);
        Assert.Equal(8, result);
    }

    [Fact]
    public void Discriminator_Int32_SpanEqualsInnerSize()
    {
        // Optional<Int32>: optionalSize=5, innerSize=4 (1-byte discriminator)
        // Both old (5-1=4) and new (innerSize=4) produce the same result
        var result = SwiftOptional<int>.ComputePayloadSpanSize(5, 4);
        Assert.Equal(4, result);
    }

    [Fact]
    public void Discriminator_Bool_SpanEqualsInnerSize()
    {
        // Optional<Bool>: optionalSize=1, innerSize=1 (extra inhabitants in Bool)
        var result = SwiftOptional<int>.ComputePayloadSpanSize(1, 1);
        Assert.Equal(1, result);
    }

    [Fact]
    public void Discriminator_Int64_SpanEqualsInnerSize()
    {
        // Optional<Int64>: optionalSize=9, innerSize=8 (1-byte discriminator)
        var result = SwiftOptional<int>.ComputePayloadSpanSize(9, 8);
        Assert.Equal(8, result);
    }

    [Fact]
    public void OldBugPattern_WouldHaveCrashed_ForExtraInhabitants()
    {
        // Verify the old pattern (optionalSize - 1) would produce wrong results
        // for extra-inhabitant types where optionalSize == innerSize
        int optionalSize = 16; // Optional<String>
        int innerSize = 16;    // String

        int correctSpan = SwiftOptional<int>.ComputePayloadSpanSize(optionalSize, innerSize);
        int oldBugSpan = optionalSize - 1; // The old code

        Assert.Equal(16, correctSpan);
        Assert.Equal(15, oldBugSpan);
        Assert.NotEqual(correctSpan, oldBugSpan); // Proves the fix is necessary
    }
}
