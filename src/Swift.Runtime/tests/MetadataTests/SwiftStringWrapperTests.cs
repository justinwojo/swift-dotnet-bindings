// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Swift;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the SwiftString wrapper path that routes ToString()/Length
/// through SwiftBindingsRuntime via CallingConvention.Cdecl, avoiding
/// the Mono JIT CallConvSwift assertion crash.
/// </summary>
public class SwiftStringWrapperTests
{
    /// <summary>
    /// Probes whether the SwiftBindingsRuntime native library can be loaded
    /// and has the SwiftString wrapper entry points.
    /// </summary>
    private static bool IsWrapperAvailable()
    {
        try
        {
            if (!NativeLibrary.TryLoad("SwiftBindingsRuntime", out var handle))
                return false;
            // Verify all 3 entry points exist
            return NativeLibrary.TryGetExport(handle, "SBW_SwiftString_ToUtf8", out _)
                && NativeLibrary.TryGetExport(handle, "SBW_SwiftString_GetCount", out _)
                && NativeLibrary.TryGetExport(handle, "SBW_SwiftString_FreeUtf8", out _);
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public void ToString_SimpleAscii_RoundTrips()
    {
        if (!IsWrapperAvailable())
            return; // Skip: dylib not deployed. iOS Simulator runtime tests cover this.

        using var str = new SwiftString("hello");
        Assert.Equal("hello", str.ToString());
    }

    [Fact]
    public void Length_SimpleAscii_ReturnsCorrectCount()
    {
        if (!IsWrapperAvailable())
            return;

        using var str = new SwiftString("hello");
        Assert.Equal(5, str.Length);
    }

    [Fact]
    public void ToString_EmptyString_ReturnsEmpty()
    {
        if (!IsWrapperAvailable())
            return;

        using var str = new SwiftString("");
        Assert.Equal("", str.ToString());
    }

    [Fact]
    public void Length_EmptyString_ReturnsZero()
    {
        if (!IsWrapperAvailable())
            return;

        using var str = new SwiftString("");
        Assert.Equal(0, str.Length);
    }

    [Fact]
    public void ToString_UnicodeEmoji_RoundTrips()
    {
        if (!IsWrapperAvailable())
            return;

        using var str = new SwiftString("Hello 🌍!");
        Assert.Equal("Hello 🌍!", str.ToString());
    }

    [Fact]
    public void Length_UnicodeEmoji_ReturnsCharacterCount()
    {
        if (!IsWrapperAvailable())
            return;

        // Swift String.count returns grapheme cluster count.
        // "Hello 🌍!" has 8 grapheme clusters (H, e, l, l, o, space, 🌍, !)
        using var str = new SwiftString("Hello 🌍!");
        Assert.Equal(8, str.Length);
    }

    [Fact]
    public void ToString_LongString_RoundTrips()
    {
        if (!IsWrapperAvailable())
            return;

        // Long strings use heap-allocated storage (large string form) vs
        // short strings which are inline in the 2-word buffer (small string form).
        // This tests that the wrapper handles both correctly.
        var longText = new string('x', 1000);
        using var str = new SwiftString(longText);
        Assert.Equal(longText, str.ToString());
    }

    [Fact]
    public void ToString_MultiByteUtf8_RoundTrips()
    {
        if (!IsWrapperAvailable())
            return;

        // Japanese text uses 3-byte UTF-8 sequences per character.
        // Tests that the UTF-8 byte count differs from character count.
        using var str = new SwiftString("日本語テスト");
        Assert.Equal("日本語テスト", str.ToString());
    }

    [Fact]
    public void Length_MultiByteUtf8_ReturnsCharacterCount()
    {
        if (!IsWrapperAvailable())
            return;

        // 6 characters, but 18 UTF-8 bytes (3 bytes each).
        // Wrapper must return String.count (6), not UTF-8 byte count (18).
        using var str = new SwiftString("日本語テスト");
        Assert.Equal(6, str.Length);
    }

    [Fact]
    public void WrapperEntryPoints_ExportedFromDylib()
    {
        if (!NativeLibrary.TryLoad("SwiftBindingsRuntime", out var handle))
            return;

        Assert.True(NativeLibrary.TryGetExport(handle, "SBW_SwiftString_ToUtf8", out _),
            "SBW_SwiftString_ToUtf8 should be exported");
        Assert.True(NativeLibrary.TryGetExport(handle, "SBW_SwiftString_GetCount", out _),
            "SBW_SwiftString_GetCount should be exported");
        Assert.True(NativeLibrary.TryGetExport(handle, "SBW_SwiftString_FreeUtf8", out _),
            "SBW_SwiftString_FreeUtf8 should be exported");
    }
}
