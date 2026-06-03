// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="ValueWitnessFlags"/> decoding. The flag layout mirrors Swift's
/// <c>swift/ABI/MetadataValues.h</c>: the alignment (minus one) lives in the low 8 bits and
/// bits 8..15 are reserved. The reserved byte is zero in real value-witness tables today, so
/// these tests guard the masking semantics against future flag additions and against the
/// previously-too-wide 0x0000FFFF alignment mask.
/// </summary>
public class ValueWitnessFlagsTests
{
    [Fact]
    public void AlignmentMask_IsLowByteOnly()
    {
        // Per swift/ABI/MetadataValues.h the alignment mask is the low 8 bits (max alignment 256).
        Assert.Equal(0x000000FFu, (uint)ValueWitnessFlags.AlignmentMask);
    }

    [Theory]
    [InlineData(0x00u, 0x00u)] // alignment 1
    [InlineData(0x0Fu, 0x0Fu)] // alignment 16
    [InlineData(0xFFu, 0xFFu)] // alignment 256
    public void AlignmentMask_DecodesLowByte_IgnoringReservedBits(uint alignmentMinusOne, uint expected)
    {
        // Set reserved bits 8..15 (which must NOT bleed into the decoded alignment) plus a
        // real flag bit above them. The old 0x0000FFFF mask would fold the reserved byte into
        // the alignment (e.g. 0xFF0F instead of 0x0F).
        var flags = (ValueWitnessFlags)(alignmentMinusOne | 0x0000FF00u | (uint)ValueWitnessFlags.IsNonPOD);

        var decoded = (uint)(flags & ValueWitnessFlags.AlignmentMask);

        Assert.Equal(expected, decoded);
    }
}
