// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Tests for the pointer-arg render-handler closure shape (RealityFoundation
/// AudioGenerator PlayAudio / PrepareAudio): <c>(UnsafeMutablePointer&lt;T&gt;) -&gt; OSStatus</c>.
/// The closure-parameter gate previously rejected ANY signature naming <c>OSStatus</c>
/// because <c>Darwin.OSStatus</c> resolved to a synthetic ObjC-bridged class record
/// instead of <c>Int32</c>, so the whole render-handler method was dropped. Registering
/// the Darwin/AVFAudio typealiases as primitives lets the gate see <c>OSStatus</c> as
/// <c>Int32</c> and the method is emitted. A plain <c>Int32</c> stands in for the opaque
/// <c>AudioBufferList</c> payload to keep the fixture hermetic — the wire shape
/// (pointer arg as IntPtr + Int32 return) is identical.
/// </summary>
public class PointerArgRenderHandlerTests : TestBase
{
    public PointerArgRenderHandlerTests(TestResults results) : base(results) { }

    public void TestRenderHandlerReadsPointerAndPropagatesStatus()
    {
        int observedSeed = -1;
        int status = TestLibFunctions.InvokeRenderHandler(123, ptr =>
        {
            // Read the Swift-seeded value through the UnsafeMutablePointer<Int32> argument.
            unsafe { observedSeed = *(int*)ptr; }
            return observedSeed + 1000; // OSStatus (Int32) derived from the observed value
        });

        AssertEqual(123, observedSeed,
            "C# delegate observed the Swift-seeded value through the UnsafeMutablePointer<Int32> argument");
        AssertEqual(1123, status,
            "Handler's OSStatus (Int32 return) propagated back across the boundary unchanged");
    }

    public void TestRenderHandlerNoErrStatus()
    {
        // OSStatus 0 == noErr — the success convention RealityFoundation's render handler
        // uses. Confirms a zero return isn't conflated with a dropped/missing wrapper, and
        // that a negative seed reads correctly through the pointer.
        int status = TestLibFunctions.InvokeRenderHandler(-7, ptr =>
        {
            int observed;
            unsafe { observed = *(int*)ptr; }
            return observed == -7 ? 0 : -1;
        });

        AssertEqual(0, status,
            "noErr (0) OSStatus round-trips for a negative seed read through the pointer");
    }
}
