// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using CoreGraphics;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// A protocol requirement whose parameter is an ObjC-BRIDGED reference type (CGContext wraps a
/// CFTypeRef; its C# projection is a platform binding class exposing a bare <c>.Handle</c> and no
/// ISwiftObject <c>.Payload</c>). Both directions of the witness have to carry the native pointer
/// itself: the proxy's forward body when C# drives a Swift-valued existential, and the reverse
/// receiver when Swift dispatches into a C# implementation. These tests prove the pointer that
/// crosses is a live context, which no compile-time check can establish.
/// </summary>
public class BridgedReferenceWitnessParamTests : TestBase
{
    public BridgedReferenceWitnessParamTests(TestResults results) : base(results) { }

    public void TestExistentialParameterReachesSwiftConformer()
    {
        var renderer = TestLibFunctions.MakeSolidFillRenderer(4);

        AssertEqual("SolidFill(4)", TestLibFunctions.NameOfRenderer(renderer),
            "Swift-valued existential answers the non-bridged requirement through the proxy");
    }

    public void TestForwardWitnessCarriesLiveBridgedContext()
    {
        // C# holds the existential over a Swift conformer, so calling the bridged-reference
        // requirement runs the proxy's forward body. Swift fills the first pixel of the buffer
        // this context draws into — an untouched buffer means the forwarded pointer never named
        // this context.
        var renderer = TestLibFunctions.MakeSolidFillRenderer(1);

        IntPtr pixels = Marshal.AllocHGlobal(4);
        try
        {
            for (int i = 0; i < 4; i++)
                Marshal.WriteByte(pixels, i, 0);

            using var colorSpace = CGColorSpace.CreateDeviceRGB();
            using var context = new CGBitmapContext(pixels, 1, 1, 8, 4, colorSpace,
                CGImageAlphaInfo.PremultipliedLast);

            renderer.Render(context, 1);

            bool written = false;
            for (int i = 0; i < 4; i++)
                written |= Marshal.ReadByte(pixels, i) != 0;

            AssertTrue(written,
                "Swift drew through the forwarded context handle into this context's own buffer");
        }
        finally
        {
            Marshal.FreeHGlobal(pixels);
        }
    }

    public void TestReverseWitnessReceivesLiveBridgedContext()
    {
        // Swift creates a 1x1 context and dispatches the requirement into the C# implementation.
        var impl = new RecordingRenderer();

        AssertTrue(TestLibFunctions.RenderOnePixel(impl), "Swift built its context and dispatched");
        AssertTrue(impl.WasCalled, "Reverse dispatch reached the C# implementation");
        AssertEqual(1, impl.ReceivedWidth, "Scalar companion parameter survived alongside the bridged one");
        AssertTrue(impl.ReceivedHandle != IntPtr.Zero, "Bridged parameter arrived as a non-null native handle");
        AssertApproxEqual(1.0, impl.ReceivedClipWidth, 0.001,
            "Received handle names Swift's own 1x1 context, not an unrelated pointer");
    }

    private sealed class RecordingRenderer : ICanvasRendering
    {
        public bool WasCalled { get; private set; }
        public int ReceivedWidth { get; private set; }
        public IntPtr ReceivedHandle { get; private set; }
        public double ReceivedClipWidth { get; private set; }

        public string RendererName => "Recording";

        public void Render(CGContext context, int width)
        {
            WasCalled = true;
            ReceivedWidth = width;
            ReceivedHandle = (IntPtr)context.Handle;
            // Reading geometry back off the context proves the pointer refers to the live context
            // Swift allocated, not merely to non-null memory.
            ReceivedClipWidth = (double)context.GetClipBoundingBox().Width;
        }
    }
}
