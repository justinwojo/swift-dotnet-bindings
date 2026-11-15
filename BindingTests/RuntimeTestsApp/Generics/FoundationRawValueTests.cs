// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Foundation;
using Swift.Runtime;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// RunLoop.Mode (an NSString typedef Swift imports as a struct over its string) and ObjCBool (one byte)
/// on the wrapper route (<c>FoundationRawValues</c>), the direct route a native bridge carries
/// (<c>FoundationRawValueBox</c>, generic over a protocol-constrained parameter and an Error), and the
/// box's concrete specializations. The platform binding's NSRunLoopMode numbers the constants, and an
/// eight-byte ObjCBool carrier reads seven bytes past a one-byte result.
/// </summary>
public class FoundationRawValueTests : TestBase
{
    public FoundationRawValueTests(TestResults results) : base(results) { }

    private static FoundationRawValueBox<KeyedToken, DirectTransitionFailure> MakeBox(int key)
    {
        using var token = new KeyedToken(key);
        return new FoundationRawValueBox<KeyedToken, DirectTransitionFailure>(token);
    }

    private static readonly string Common = NSRunLoopMode.Common.GetConstant()!.ToString();

    public void TestRunLoopModeThroughWrappers()
    {
        using var values = new FoundationRawValues();
        using var common = values.GetCommonMode();
        AssertEqual(Common, common.ToString(), "commonMode() is .common");
        using var mode = new NSString("SBRawValueMode");
        using var echoed = values.EchoMode(mode);
        AssertEqual("SBRawValueMode", echoed.ToString(), "echoMode(_:) returns a mode outside the platform's constants");
        AssertEqual("SBRawValueMode", values.Name(mode), "name(ofMode:) reads the string C# passed");
        AssertEqual(Common, values.Name(common), ".common round-trips");
    }

    public void TestObjCBoolThroughWrappers()
    {
        using var values = new FoundationRawValues();
        AssertFalse(values.Flipped(true), "flipped(true) is false");
        AssertTrue(values.Flipped(false), "flipped(false) is true");
        AssertTrue(values.IsSet(true), "isSet(true) reads the byte C# passed");
        AssertFalse(values.IsSet(false), "isSet(false) reads the byte C# passed");
    }

    /// <summary>
    /// ObjCBool is a Bool on iOS and tvOS and an Int8 on Mac Catalyst, so the layout model derives no
    /// lowering for it and the box's direct transition keeps its guard (and its SB0001 marker): an engine
    /// that refuses the shape throws instead of calling, one that runs it gets the byte Swift returned.
    /// </summary>
    public void TestObjCBoolOnTheGenericBox()
    {
        const SwiftTransitionFacts shape = SwiftTransitionFacts.SelfRegister | SwiftTransitionFacts.LoweredSignature;
        using var odd = MakeBox(3);
        using var even = MakeBox(4);
        if (!SwiftTransitionGuard.IsSupported(shape))
        {
            SwiftTransitionUnsupportedException? refusal = null;
            try { odd.Flipped(true); }
            catch (SwiftTransitionUnsupportedException ex) { refusal = ex; }
            AssertNotNull(refusal, "the engine refuses the direct transition");
            AssertTrue(refusal!.Member.Contains("FoundationRawValueBox", StringComparison.Ordinal), $"refusal names the member: {refusal.Member}");
            AssertTrue((refusal.RefusedFacts & ~shape) == 0, $"refused for the member's own shape: {refusal.RefusedFacts}");
            return;
        }
        AssertFalse(odd.Flipped(true), "an odd key flips true");
        AssertTrue(odd.Flipped(false), "an odd key flips false");
        AssertTrue(even.Flipped(true), "an even key keeps true");
        AssertFalse(even.Flipped(false), "an even key keeps false");
    }

    public void TestRunLoopModeOnTheSpecialization()
    {
        using var box = MakeBox(7);
        using var mode = new NSString("SBRawValueMode");
        using var echoed = box.EchoMode(mode);
        AssertEqual("SBRawValueMode", echoed.ToString(), "echoMode(_:) returns the mode it was passed");
        AssertEqual("SBRawValueMode#7", box.Name(mode), "name(ofMode:) reads the mode and the key");
    }
}
