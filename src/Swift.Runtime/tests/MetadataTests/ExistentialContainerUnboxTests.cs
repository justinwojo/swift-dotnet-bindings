// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// <see cref="ExistentialContainer0.Unbox"/> reads a value at the width Swift stored it. Swift
/// writes only a Bool's one byte into the inline buffer, so the rest of the word holds whatever
/// the buffer held before, as in an async result carrier from an uninitialized allocation.
/// </summary>
public class ExistentialContainerUnboxTests
{
    [Theory]
    [InlineData(unchecked((long)0xAAAAAAAAAAAAAA00), false)]
    [InlineData(unchecked((long)0xAAAAAAAAAAAAAA01), true)]
    [InlineData(0L, false)]
    [InlineData(1L, true)]
    public void Unbox_Bool_ReadsOnlyTheValueByte(long word, bool expected)
    {
        Assert.True(TypeMetadata.TryGetTypeMetadata<bool>(out var boolMetadata));
        var container = new ExistentialContainer0
        {
            Payload0 = (IntPtr)word,
            Payload1 = unchecked((IntPtr)(long)0xAAAAAAAAAAAAAAAA),
            Payload2 = unchecked((IntPtr)(long)0xAAAAAAAAAAAAAAAA),
            ObjectMetadata = boolMetadata.Value,
        };

        Assert.Equal(expected, ExistentialContainer0.Unbox(container));
    }
}
