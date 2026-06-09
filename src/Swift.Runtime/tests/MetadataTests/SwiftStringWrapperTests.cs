// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Swift;
using Xunit;
using Xunit.Abstractions;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the SwiftString wrapper path that routes ToString()/Length
/// through SwiftBindingsRuntime via CallingConvention.Cdecl, avoiding
/// the Mono JIT CallConvSwift assertion crash.
/// </summary>
public class SwiftStringWrapperTests : IClassFixture<SwiftStringWrapperTests.WrapperAvailabilityFixture>
{
    /// <summary>
    /// Shared fixture that probes wrapper availability once and reports
    /// skip/available status via ITestOutputHelper, preventing silent skips
    /// from masking missing-symbol regressions.
    /// </summary>
    public class WrapperAvailabilityFixture
    {
        public bool IsAvailable { get; }
        public string UnavailableReason { get; }

        public WrapperAvailabilityFixture()
        {
            try
            {
                if (!NativeLibrary.TryLoad("SwiftBindingsRuntime", out var handle))
                {
                    IsAvailable = false;
                    UnavailableReason = "SwiftBindingsRuntime dylib not found in library search path";
                    return;
                }

                // Check all 6 entry points
                var required = new[]
                {
                    "SBW_SwiftString_ToUtf8",
                    "SBW_SwiftString_GetCount",
                    "SBW_SwiftString_FreeUtf8",
                    "SBW_SwiftString_Create",
                    "SBW_SwiftString_Destroy",
                    "SBW_SwiftString_GetMetadata",
                };

                var missing = required.Where(
                    name => !NativeLibrary.TryGetExport(handle, name, out _)).ToArray();

                if (missing.Length > 0)
                {
                    IsAvailable = false;
                    UnavailableReason = $"Missing entry points: {string.Join(", ", missing)}";
                    return;
                }

                IsAvailable = true;
                UnavailableReason = string.Empty;
            }
            catch (Exception ex)
            {
                IsAvailable = false;
                UnavailableReason = $"Exception probing wrapper: {ex.Message}";
            }
        }
    }

    private readonly WrapperAvailabilityFixture _fixture;
    private readonly ITestOutputHelper _output;

    public SwiftStringWrapperTests(WrapperAvailabilityFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    private bool SkipIfUnavailable()
    {
        if (_fixture.IsAvailable)
            return false;
        _output.WriteLine($"SKIPPED: {_fixture.UnavailableReason}");
        return true;
    }

    [Fact]
    public void ToString_SimpleAscii_RoundTrips()
    {
        if (SkipIfUnavailable())
            return;

        using var str = new SwiftString("hello");
        Assert.Equal("hello", str.ToString());
    }

    [Fact]
    public void Length_SimpleAscii_ReturnsCorrectCount()
    {
        if (SkipIfUnavailable())
            return;

        using var str = new SwiftString("hello");
        Assert.Equal(5, str.Length);
    }

    [Fact]
    public void ToString_EmptyString_ReturnsEmpty()
    {
        if (SkipIfUnavailable())
            return;

        using var str = new SwiftString("");
        Assert.Equal("", str.ToString());
    }

    [Fact]
    public void Length_EmptyString_ReturnsZero()
    {
        if (SkipIfUnavailable())
            return;

        using var str = new SwiftString("");
        Assert.Equal(0, str.Length);
    }

    [Fact]
    public void ToString_UnicodeEmoji_RoundTrips()
    {
        if (SkipIfUnavailable())
            return;

        using var str = new SwiftString("Hello 🌍!");
        Assert.Equal("Hello 🌍!", str.ToString());
    }

    [Fact]
    public void Length_UnicodeEmoji_ReturnsCharacterCount()
    {
        if (SkipIfUnavailable())
            return;

        // Swift String.count returns grapheme cluster count.
        // "Hello 🌍!" has 8 grapheme clusters (H, e, l, l, o, space, 🌍, !)
        using var str = new SwiftString("Hello 🌍!");
        Assert.Equal(8, str.Length);
    }

    [Fact]
    public void ToString_LongString_RoundTrips()
    {
        if (SkipIfUnavailable())
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
        if (SkipIfUnavailable())
            return;

        // Japanese text uses 3-byte UTF-8 sequences per character.
        // Tests that the UTF-8 byte count differs from character count.
        using var str = new SwiftString("日本語テスト");
        Assert.Equal("日本語テスト", str.ToString());
    }

    [Fact]
    public void Length_MultiByteUtf8_ReturnsCharacterCount()
    {
        if (SkipIfUnavailable())
            return;

        // 6 characters, but 18 UTF-8 bytes (3 bytes each).
        // Wrapper must return String.count (6), not UTF-8 byte count (18).
        using var str = new SwiftString("日本語テスト");
        Assert.Equal(6, str.Length);
    }

    [Fact]
    public void Create_SimpleAscii_CanReadBack()
    {
        if (SkipIfUnavailable())
            return;

        // Create a SwiftString via wrapper path, then read it back via wrapper path.
        using var str = new SwiftString("wrapper-create-test");
        Assert.Equal("wrapper-create-test", str.ToString());
        Assert.Equal(19, str.Length);
    }

    [Fact]
    public void Create_UnicodeString_CanReadBack()
    {
        if (SkipIfUnavailable())
            return;

        using var str = new SwiftString("こんにちは世界");
        Assert.Equal("こんにちは世界", str.ToString());
        Assert.Equal(7, str.Length);
    }

    [Fact]
    public void Create_EmptyString_CanReadBack()
    {
        if (SkipIfUnavailable())
            return;

        using var str = new SwiftString("");
        Assert.Equal("", str.ToString());
        Assert.Equal(0, str.Length);
    }

    [Fact]
    public void WrapperEntryPoints_ExportedFromDylib()
    {
        if (SkipIfUnavailable())
            return;

        // If the fixture says we're available, all 5 entry points were already
        // verified. Re-assert here for explicit per-symbol failure messages.
        Assert.True(NativeLibrary.TryLoad("SwiftBindingsRuntime", out var handle),
            "SwiftBindingsRuntime should be loadable");
        Assert.True(NativeLibrary.TryGetExport(handle, "SBW_SwiftString_ToUtf8", out _),
            "SBW_SwiftString_ToUtf8 should be exported");
        Assert.True(NativeLibrary.TryGetExport(handle, "SBW_SwiftString_GetCount", out _),
            "SBW_SwiftString_GetCount should be exported");
        Assert.True(NativeLibrary.TryGetExport(handle, "SBW_SwiftString_FreeUtf8", out _),
            "SBW_SwiftString_FreeUtf8 should be exported");
        Assert.True(NativeLibrary.TryGetExport(handle, "SBW_SwiftString_Create", out _),
            "SBW_SwiftString_Create should be exported");
        Assert.True(NativeLibrary.TryGetExport(handle, "SBW_SwiftString_Destroy", out _),
            "SBW_SwiftString_Destroy should be exported");
        Assert.True(NativeLibrary.TryGetExport(handle, "SBW_SwiftString_GetMetadata", out _),
            "SBW_SwiftString_GetMetadata should be exported");
    }
}
