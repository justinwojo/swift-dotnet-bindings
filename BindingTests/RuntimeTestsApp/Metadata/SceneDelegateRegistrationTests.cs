// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if IOS || TVOS
using System.Runtime.InteropServices;
using ObjCRuntime;
using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.Metadata;

/// <summary>Checks the class-level native conformance UIKit validates before creating a scene.</summary>
public class SceneDelegateRegistrationTests : TestBase
{
    public SceneDelegateRegistrationTests(TestResults results) : base(results) { }

    public void TestNativeSceneDelegateConformance()
    {
        // Resolve by native name so the assertion does not itself root managed protocol interfaces.
        var delegateClass = Class.GetHandle("SceneDelegate");
        AssertTrue(delegateClass != IntPtr.Zero, "Scene delegate must be registered with Objective-C");
        foreach (var protocolName in new[] { "UISceneDelegate", "UIWindowSceneDelegate" })
        {
            var protocol = Protocol.GetHandle(protocolName);
            AssertTrue(protocol != IntPtr.Zero, $"UIKit must provide {protocolName}");
            AssertTrue(ClassConformsToProtocol(delegateClass, protocol) != 0,
                $"Native scene delegate class must conform to {protocolName} after trimming");
        }
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "class_conformsToProtocol")]
    private static extern byte ClassConformsToProtocol(IntPtr nativeClass, IntPtr protocol);
}
#endif
