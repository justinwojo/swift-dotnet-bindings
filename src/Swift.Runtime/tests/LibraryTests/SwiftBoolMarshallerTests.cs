// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift.Runtime.Marshalling;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A Swift-convention Bool arrives as an LLVM i1: only bit 0 is defined. The marshaller must read
/// that bit and ignore the rest, which an unoptimised x86_64 negation leaves set (0xfe is false,
/// 0xff is true).
/// </summary>
public class SwiftBoolMarshallerTests
{
    [Theory]
    [InlineData((byte)0x00, false)]
    [InlineData((byte)0x01, true)]
    [InlineData((byte)0xfe, false)]
    [InlineData((byte)0xff, true)]
    [InlineData((byte)0x02, false)]
    public void ConvertToManaged_ReadsBitZero(byte register, bool expected)
    {
        Assert.Equal(expected, SwiftBoolMarshaller.ConvertToManaged(register));
    }

    [Theory]
    [InlineData(true, (byte)1)]
    [InlineData(false, (byte)0)]
    public void ConvertToUnmanaged_IsZeroOrOne(bool value, byte expected)
    {
        Assert.Equal(expected, SwiftBoolMarshaller.ConvertToUnmanaged(value));
    }
}
