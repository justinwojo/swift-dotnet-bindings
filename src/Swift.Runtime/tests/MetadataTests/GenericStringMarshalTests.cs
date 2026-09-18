// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift.Runtime.InteropServices;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A generic value seam instantiated with a C# <c>string</c> writes an owned Swift String into the
/// destination. A null has no Swift String to stand for and is refused before anything is written.
/// </summary>
public class GenericStringMarshalTests
{
    [Fact]
    public void MarshalToSwift_NullString_ThrowsArgumentNull()
    {
        var buffer = new byte[16];
        string? value = null;
        Assert.Throws<ArgumentNullException>(() =>
        {
            var local = new Span<byte>(buffer);
            SwiftMarshal.MarshalToSwift(value!, ref local);
        });
        Assert.All(buffer, b => Assert.Equal(0, b));
    }

    [Fact]
    public void MarshalToSwift_String_RejectsTooSmallDestination()
    {
        var buffer = new byte[8];
        Assert.Throws<ArgumentException>(() =>
        {
            var local = new Span<byte>(buffer);
            SwiftMarshal.MarshalToSwift("text", ref local);
        });
    }
}
