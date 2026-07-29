// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if SWIFTUI_BRIDGE

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.SwiftUIBridge;

/// <summary>
/// A View whose init parameter is an ObjC-ROOTED (NSObject-derived) Swift class. The generated
/// bridge has to hand Swift that object's bare native pointer — an NSObject-derived binding class
/// carries no ISwiftObject payload handle — and the Swift side reconstitutes it with
/// <c>Unmanaged.fromOpaque</c>. Building the hosting controller and letting SwiftUI evaluate the
/// body reads a property off the reconstituted object, so a pointer that named anything else does
/// not survive these tests.
/// </summary>
public class ObjCRootedParamViewTests : TestBase
{
    public ObjCRootedParamViewTests(TestResults results) : base(results) { }

    public void TestObjCRootedParamViewHostsItsModel()
    {
        var item = new LabeledItem("widget", 7);
        var session = ObjCRootedModelViewSession.Create(item);
        try
        {
            AssertTrue(session.Handle != IntPtr.Zero, "Session created from an ObjC-rooted model");

            var vc = session.ViewController;
            AssertNotNull(vc, "Hosting controller for the ObjC-rooted-model view is a live UIViewController");

            // Loading the view evaluates the SwiftUI body, which reads `item.displayName` off the
            // object rebuilt from the pointer the bridge forwarded.
            vc!.View!.LayoutIfNeeded();
            AssertEqual("widget (#7)", item.DisplayName,
                "The model the bridge forwarded is the one C# still holds");
        }
        finally
        {
            session.Dispose();
        }
    }

    public void TestObjCRootedParamViewClassifiesPerParameter()
    {
        // ObjC-rooted class alongside a plain scalar: the scalar must keep its by-value treatment
        // while the class goes through its native pointer.
        var item = new SimpleNSObject("gauge");
        var session = ObjCRootedModelWithScalarViewSession.Create(item, 3);
        try
        {
            AssertTrue(session.Handle != IntPtr.Zero, "Session created from a mixed rooted-class/scalar init");

            var vc = session.ViewController;
            AssertNotNull(vc, "Hosting controller for the mixed-parameter view is a live UIViewController");

            vc!.View!.LayoutIfNeeded();
            AssertEqual("SimpleNSObject: gauge", item.GetDescribe(),
                "The rooted-class parameter survived alongside the scalar");
        }
        finally
        {
            session.Dispose();
        }
    }
}

#endif
