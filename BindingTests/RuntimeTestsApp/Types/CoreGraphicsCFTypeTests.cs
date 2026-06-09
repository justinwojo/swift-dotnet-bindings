// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using CoreGraphics;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Types;

/// <summary>
/// Regression test for CGImage/CGColor projected as IntPtr instead of managed wrappers.
///
/// Pre-fix the typedb registered Swift CoreGraphics CFTypes (CGImage, CGColor,
/// CGColorSpace, CGContext, …) under <c>managedTypeName="IntPtr"</c>, so a Swift
/// method returning <c>CGColor?</c> emitted as <c>System.IntPtr?</c> in C# —
/// raw pointer, no managed wrapper, no compile-time type safety, no automatic
/// CFRetain/CFRelease management. The fix routes them through the canonical
/// dotnet/macios <c>CoreGraphics.CGImage</c> / <c>CoreGraphics.CGColor</c>
/// wrappers via <c>Runtime.GetINativeObject&lt;T&gt;(ptr, owns: false)</c>.
///
/// The compile-time fact that these tests bind to <c>CoreGraphics.CGColor</c> /
/// <c>CoreGraphics.CGImage</c> rather than <c>IntPtr</c> is itself the
/// regression assertion — pre-fix this fixture would not compile.
/// </summary>
public class CoreGraphicsCFTypeTests : TestBase
{
    public CoreGraphicsCFTypeTests(TestResults results) : base(results) { }

    public void TestCGColorReturnsCanonicalWrapper()
    {
        // The static return type assertion below is the structural regression.
        // Pre-fix this would have been `IntPtr` and the variable type would not compile.
        CGColor color = TestLibFunctions.MakeRedColor();
        AssertNotNull(color, "MakeRedColor should produce a non-null CGColor wrapper");
        AssertTrue(color.Handle != IntPtr.Zero, "CGColor wrapper should expose a non-zero CFType handle");
    }

    public void TestOptionalCGColorPreservesNullability()
    {
        // Optional path — the MusicKit Artwork.BackgroundColor shape.
        CGColor? present = TestLibFunctions.MaybeColor(true);
        AssertNotNull(present, "MaybeColor(true) should return a non-null CGColor");

        CGColor? absent = TestLibFunctions.MaybeColor(false);
        AssertNull(absent, "MaybeColor(false) should return null, not IntPtr.Zero");
    }

    public void TestCGImageReturnsCanonicalWrapper()
    {
        // The Lottie BundleImageProvider.imageForAsset shape — must return
        // CoreGraphics.CGImage, not System.IntPtr.
        CGImage? image = TestLibFunctions.MakeOnePixelImage();
        AssertNotNull(image, "MakeOnePixelImage should return a non-null CGImage wrapper");
        AssertEqual(1, (int)image!.Width, "1x1 CGImage width should be 1");
        AssertEqual(1, (int)image.Height, "1x1 CGImage height should be 1");
    }
}
