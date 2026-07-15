// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for ObjC-rooted class projection: model flags, namespace mapping,
/// emission (interface list, payload gating, constructors, NewFromPayload,
/// MarshalToSwift, self-parameter), TypeRecord serialization, and hierarchy resolution.
/// </summary>
public class ClassObjCRootedTests
{
    #region Model — ClassDecl.HasObjCSuperclass + IsObjCRooted

    [Fact]
    public void HasObjCSuperclass_TrueForObjCUsr()
    {
        var cls = CreateClassDecl("MyLayer", "TestModule",
            superclassUsr: "c:objc(cs)CALayer",
            superclassNames: new[] { "QuartzCore.CALayer" });

        Assert.True(cls.HasObjCSuperclass);
    }

    [Fact]
    public void HasObjCSuperclass_FalseForSwiftUsr()
    {
        var cls = CreateClassDecl("Derived", "TestModule",
            superclassUsr: "s:10TestModule4BaseC",
            superclassNames: new[] { "TestModule.Base" });

        Assert.False(cls.HasObjCSuperclass);
    }

    [Fact]
    public void HasObjCSuperclass_FalseForNoSuperclass()
    {
        var cls = CreateClassDecl("Root", "TestModule");

        Assert.False(cls.HasObjCSuperclass);
    }

    [Fact]
    public void IsObjCRooted_DefaultsFalse()
    {
        var cls = CreateClassDecl("Root", "TestModule");
        Assert.False(cls.IsObjCRooted);
    }

    [Fact]
    public void IsObjCRooted_SettableManually()
    {
        var cls = CreateClassDecl("MyLayer", "TestModule");
        cls.IsObjCRooted = true;
        Assert.True(cls.IsObjCRooted);
    }

    #endregion

    #region Model — IsObjCRooted Resolution via ModuleProcessor

    [Fact]
    public void Resolution_DirectObjCBase_SetsIsObjCRooted()
    {
        var cls = CreateClassDecl("SessionDelegate", "TestModule",
            superclassUsr: "c:objc(cs)NSObject",
            superclassNames: new[] { "ObjectiveC.NSObject" });

        RunResolution(cls);

        Assert.True(cls.IsObjCRooted);
    }

    [Fact]
    public void Resolution_TransitiveObjCRooted_SetsIsObjCRooted()
    {
        // Parent has direct ObjC base, child inherits from parent
        var parent = CreateClassDecl("AnimatedControl", "TestModule",
            superclassUsr: "c:objc(cs)UIControl",
            superclassNames: new[] { "UIKit.UIControl" });
        var child = CreateClassDecl("AnimatedButton", "TestModule",
            superclassNames: new[] { "TestModule.AnimatedControl" });

        RunResolution(parent, child);

        Assert.True(parent.IsObjCRooted);
        Assert.True(child.IsObjCRooted);
    }

    [Fact]
    public void Resolution_ThreeLevelTransitive_SetsIsObjCRooted()
    {
        // Grandparent → Parent → Child, all should be ObjC-rooted
        var grandparent = CreateClassDecl("BaseLayer", "TestModule",
            superclassUsr: "c:objc(cs)CALayer",
            superclassNames: new[] { "QuartzCore.CALayer" });
        var parent = CreateClassDecl("AnimLayer", "TestModule",
            superclassNames: new[] { "TestModule.BaseLayer" });
        var child = CreateClassDecl("VectorAnimationLayer", "TestModule",
            superclassNames: new[] { "TestModule.AnimLayer" });

        RunResolution(grandparent, parent, child);

        Assert.True(grandparent.IsObjCRooted);
        Assert.True(parent.IsObjCRooted);
        Assert.True(child.IsObjCRooted);
    }

    [Fact]
    public void Resolution_PureSwiftClass_NotObjCRooted()
    {
        var cls = CreateClassDecl("PureSwift", "TestModule");

        RunResolution(cls);

        Assert.False(cls.IsObjCRooted);
    }

    [Fact]
    public void Resolution_SwiftHierarchy_NotObjCRooted()
    {
        var parent = CreateClassDecl("Base", "TestModule");
        var child = CreateClassDecl("Derived", "TestModule",
            superclassNames: new[] { "TestModule.Base" });

        RunResolution(parent, child);

        Assert.False(parent.IsObjCRooted);
        Assert.False(child.IsObjCRooted);
    }

    [Fact]
    public void Resolution_GenericSuperclassName_DoesNotCrash()
    {
        // Regression: a superclass name that contains '<' (e.g., a generic superclass like "Observable<Element>")
        // causes SwiftTypeName.FromModuleQualifiedName to throw.
        var cls = CreateClassDecl("BehaviorSubject", "ReactiveStreams",
            superclassNames: new[] { "ReactiveStreams.Observable<Element>" });

        // Should not throw — generic superclass names are skipped in cross-module lookup
        RunResolution(cls);

        Assert.False(cls.IsObjCRooted);
    }

    [Fact]
    public void Resolution_ReverseDeclarationOrder_StillResolves()
    {
        // Child declared before parent — fixed-point loop should handle this
        var parent = CreateClassDecl("BaseView", "TestModule",
            superclassUsr: "c:objc(cs)UIView",
            superclassNames: new[] { "UIKit.UIView" });
        var child = CreateClassDecl("CustomView", "TestModule",
            superclassNames: new[] { "TestModule.BaseView" });

        // Pass in reverse order
        RunResolution(child, parent);

        Assert.True(parent.IsObjCRooted);
        Assert.True(child.IsObjCRooted);
    }

    #endregion

    #region TypeRecord Flag

    [Fact]
    public void TypeRecordFlags_ObjCRooted_IsDistinctBit()
    {
        var flags = TypeRecordFlags.ObjCRooted;
        Assert.Equal(1 << 8, (int)flags);
    }

    [Fact]
    public void IsObjCRooted_Helper_ReturnsTrueWhenFlagIsSet()
    {
        var typeRecord = CreateTypeRecord(TypeRecordFlags.ObjCRooted);
        Assert.True(MarshallingHelpers.IsObjCRooted(typeRecord));
    }

    [Fact]
    public void IsObjCRooted_Helper_ReturnsFalseWhenFlagIsNotSet()
    {
        var typeRecord = CreateTypeRecord(TypeRecordFlags.None);
        Assert.False(MarshallingHelpers.IsObjCRooted(typeRecord));
    }

    [Fact]
    public void IsObjCRooted_Helper_ReturnsTrueWithCombinedFlags()
    {
        var typeRecord = CreateTypeRecord(TypeRecordFlags.ObjCRooted | TypeRecordFlags.RequiresMemoryManagement);
        Assert.True(MarshallingHelpers.IsObjCRooted(typeRecord));
    }

    [Fact]
    public void IsObjCRooted_Helper_ReturnsFalseForFrozenType()
    {
        var typeRecord = CreateTypeRecord(TypeRecordFlags.Frozen);
        Assert.False(MarshallingHelpers.IsObjCRooted(typeRecord));
    }

    #endregion

    #region TypeRecord Serialization Round-Trip

    [Fact]
    public async Task Emit_ObjCRooted_Flag_Survives_RoundTrip()
    {
        var dir = CreateTempDir();
        try
        {
            var module = new ModuleTypeDatabase("MyLib", "/fake/MyLib.dylib");
            var swiftName = SwiftTypeName.FromModuleQualifiedName("MyLib.MyLayer");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "MyLayer"),
                SwiftTypeName = swiftName,
                MetadataAccessor = "$s5MyLib7MyLayerCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement | TypeRecordFlags.ObjCRooted,
                Kind = TypeRecordKind.Class
            };
            module.RegisterType(swiftName, record);

            var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
            Assert.NotNull(path);

            var typeDatabase = new TypeDatabase();
            await typeDatabase.LoadModuleDatabaseFromFile(path);

            Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var loaded));
            Assert.True((loaded!.Flags & TypeRecordFlags.ObjCRooted) != 0);
            Assert.True((loaded.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Emit_WithoutObjCRooted_DefaultsFalse()
    {
        var dir = CreateTempDir();
        try
        {
            var module = new ModuleTypeDatabase("MyLib", "/fake/MyLib.dylib");
            var swiftName = SwiftTypeName.FromModuleQualifiedName("MyLib.PureClass");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "PureClass"),
                SwiftTypeName = swiftName,
                MetadataAccessor = "$s5MyLib9PureClassCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            };
            module.RegisterType(swiftName, record);

            var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
            Assert.NotNull(path);

            var typeDatabase = new TypeDatabase();
            await typeDatabase.LoadModuleDatabaseFromFile(path);

            Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var loaded));
            Assert.False((loaded!.Flags & TypeRecordFlags.ObjCRooted) != 0);
        }
        finally { Directory.Delete(dir, true); }
    }

    #endregion

    #region Namespace Mapping — GetObjCBaseTypeName

    [Fact]
    public void GetObjCBaseTypeName_QuartzCore_MapsToCorAnimation()
    {
        var cls = CreateClassDecl("MyLayer", "TestModule",
            superclassUsr: "c:objc(cs)CALayer",
            superclassNames: new[] { "QuartzCore.CALayer" });

        var result = MarshallingHelpers.GetObjCBaseTypeName(cls);

        Assert.Equal("CoreAnimation.CALayer", result);
    }

    [Fact]
    public void GetObjCBaseTypeName_ObjectiveC_MapsToFoundation()
    {
        var cls = CreateClassDecl("MyObj", "TestModule",
            superclassUsr: "c:objc(cs)NSObject",
            superclassNames: new[] { "ObjectiveC.NSObject" });

        var result = MarshallingHelpers.GetObjCBaseTypeName(cls);

        Assert.Equal("Foundation.NSObject", result);
    }

    [Fact]
    public void GetObjCBaseTypeName_Dispatch_MapsToCoreFoundation()
    {
        var cls = CreateClassDecl("MyQueue", "TestModule",
            superclassUsr: "c:objc(cs)OS_dispatch_queue",
            superclassNames: new[] { "Dispatch.DispatchQueue" });

        var result = MarshallingHelpers.GetObjCBaseTypeName(cls);

        Assert.Equal("CoreFoundation.DispatchQueue", result);
    }

    [Fact]
    public void GetObjCBaseTypeName_UIKit_PassesThrough()
    {
        var cls = CreateClassDecl("MyControl", "TestModule",
            superclassUsr: "c:objc(cs)UIControl",
            superclassNames: new[] { "UIKit.UIControl" });

        var result = MarshallingHelpers.GetObjCBaseTypeName(cls);

        Assert.Equal("UIKit.UIControl", result);
    }

    [Fact]
    public void GetObjCBaseTypeName_NoObjCSuperclass_ReturnsNull()
    {
        var cls = CreateClassDecl("PureSwift", "TestModule");

        var result = MarshallingHelpers.GetObjCBaseTypeName(cls);

        Assert.Null(result);
    }

    [Fact]
    public void GetObjCBaseTypeName_SwiftSuperclass_ReturnsNull()
    {
        var cls = CreateClassDecl("Derived", "TestModule",
            superclassUsr: "s:10TestModule4BaseC",
            superclassNames: new[] { "TestModule.Base" });

        var result = MarshallingHelpers.GetObjCBaseTypeName(cls);

        Assert.Null(result);
    }

    [Fact]
    public void GetObjCBaseTypeName_ThirdPartyRenamedObjCSuperclass_UsesObjCNameFromUsr()
    {
        // A third-party ObjC class `FBSDKButton` is imported into Swift as `FBButton` (a Clang
        // swift_name / NS_SWIFT_NAME rename), so the superclass carried in the ABI is the Swift
        // name `FBSDKCoreKit.FBButton`. The C# binding for that class is produced by the ObjC
        // ApiDefinition pipeline under its ObjC name (`FBSDKButton`), so the base reference must
        // use the ObjC name (recovered from the Clang superclass USR), not the Swift name.
        var cls = CreateClassDecl("FBLoginButton", "FBSDKLoginKit",
            superclassUsr: "c:objc(cs)FBSDKButton",
            superclassNames: new[] { "FBSDKCoreKit.FBButton" });

        var result = MarshallingHelpers.GetObjCBaseTypeName(cls);

        Assert.Equal("FBSDKCoreKit.FBSDKButton", result);
    }

    [Fact]
    public void GetObjCBaseTypeName_ThirdPartyUnrenamedObjCSuperclass_Unchanged()
    {
        // When a third-party ObjC class is NOT renamed for Swift, the Swift superclass name and
        // the ObjC name match, so the result is the same name whether derived from the USR or the
        // Swift superclass name — no spurious change.
        var cls = CreateClassDecl("MyWidget", "WidgetKitExtras",
            superclassUsr: "c:objc(cs)BaseWidget",
            superclassNames: new[] { "WidgetCore.BaseWidget" });

        var result = MarshallingHelpers.GetObjCBaseTypeName(cls);

        Assert.Equal("WidgetCore.BaseWidget", result);
    }

    [Fact]
    public void GetObjCBaseTypeName_ThirdPartyObjCSuperclass_NonClassUsr_FallsBackToSwiftName()
    {
        // Defensive: an ObjC superclass whose USR is not a Clang class USR (`c:objc(cs)<name>`)
        // yields no ObjC name to substitute, so the existing Swift-name mapping path is used.
        var cls = CreateClassDecl("Derived", "ThirdPartyKit",
            superclassUsr: "c:@CategoryDecl",
            superclassNames: new[] { "ThirdPartyCore.SomeBase" });

        var result = MarshallingHelpers.GetObjCBaseTypeName(cls);

        Assert.Equal("ThirdPartyCore.SomeBase", result);
    }

    [Fact]
    public void GetObjCBaseTypeName_ThirdPartyObjCExportedSwiftSuperclass_UsesSwiftName()
    {
        // An @objc-exported *Swift* class (e.g. `@objc(FBSDKIcon) open class FBIcon`) has a Clang
        // superclass USR, but carries a Swift-module origin marker (`c:@M@<module>@objc(cs)<Name>`).
        // The dependency binds it via the *Swift* pipeline under its Swift name (`FBIcon`), NOT the
        // ObjC ApiDefinition pipeline — so the base reference must use the Swift superclass name,
        // not the ObjC name recovered from the USR. (Contrast with the pure-ObjC `FBSDKButton`
        // case above, which IS bound under its ObjC name.)
        var cls = CreateClassDecl("MessengerIcon", "FBSDKShareKit",
            superclassUsr: "c:@M@FBSDKCoreKit@objc(cs)FBSDKIcon",
            superclassNames: new[] { "FBSDKCoreKit.FBIcon", "ObjectiveC.NSObject" });

        var result = MarshallingHelpers.GetObjCBaseTypeName(cls);

        Assert.Equal("FBSDKCoreKit.FBIcon", result);
    }

    [Fact]
    public void GetObjCBaseTypeName_AppleRenamedModule_StaysOnRemapPath()
    {
        // Regression guard for the gate: Apple modules are curated/known, so even though the Swift
        // superclass name (`Dispatch.DispatchQueue`) differs from the ObjC USR name
        // (`OS_dispatch_queue`), the curated remap path must win — substituting the raw USR name
        // would emit a nonexistent `OS_dispatch_queue`.
        var cls = CreateClassDecl("MyQueue", "TestModule",
            superclassUsr: "c:objc(cs)OS_dispatch_queue",
            superclassNames: new[] { "Dispatch.DispatchQueue" });

        var result = MarshallingHelpers.GetObjCBaseTypeName(cls);

        Assert.Equal("CoreFoundation.DispatchQueue", result);
    }

    [Fact]
    public void ObjCBoundary_ThirdPartyRenamedSuperclass_EmitsObjCNameInDeclaration()
    {
        // End-to-end through ClassHandler's isObjCBoundary branch: the emitted base reference must
        // be the dependency's ObjC-bound name (`FBSDKCoreKit.FBSDKButton`), not the Swift-renamed
        // name (`FBSDKCoreKit.FBButton`) which does not resolve (the reported CS0234).
        var cls = CreateClassDecl("FBLoginButton", "TestModule",
            superclassUsr: "c:objc(cs)FBSDKButton",
            superclassNames: new[] { "FBSDKCoreKit.FBButton" });
        cls.IsObjCRooted = true;

        var output = EmitSingleObjCRootedClass(cls);

        var declLine = GetClassDeclarationLine(output, "FBLoginButton");
        Assert.Contains("FBSDKCoreKit.FBSDKButton", declLine);
        Assert.DoesNotContain("FBSDKCoreKit.FBButton,", declLine);
    }

    #endregion

    #region Emission — Cross-Module Transitive ObjC-Rooted

    [Fact]
    public void CrossModule_TransitiveObjCRooted_FallsBackToNSObject()
    {
        // Cross-module case: direct parent is a Swift class in another module (ObjC-rooted),
        // not an ObjC class itself. GetObjCBaseTypeName returns null for this class.
        // Emission must not crash — falls back to Foundation.NSObject.
        var cls = CreateClassDecl("CustomLayer", "TestModule",
            superclassNames: new[] { "OtherModule.SwiftLayer" });
        cls.IsObjCRooted = true; // Set via cross-module TypeRecord flag

        var output = EmitSingleObjCRootedClass(cls);

        var declLine = GetClassDeclarationLine(output, "CustomLayer");
        Assert.Contains("Foundation.NSObject", declLine);
        Assert.Contains("ISwiftObject", declLine);
        Assert.DoesNotContain("IDisposable", declLine);
    }

    [Fact]
    public void CrossModule_TransitiveObjCRooted_NoPayload()
    {
        var cls = CreateClassDecl("CustomLayer", "TestModule",
            superclassNames: new[] { "OtherModule.SwiftLayer" });
        cls.IsObjCRooted = true;

        var output = EmitSingleObjCRootedClass(cls);

        var body = GetClassBody(output, "CustomLayer");
        Assert.DoesNotContain("_payload", body);
        Assert.DoesNotContain("SwiftSafeHandle", body);
    }

    [Fact]
    public void CrossModule_TransitiveObjCRooted_HasObjCRootedConstructors()
    {
        var cls = CreateClassDecl("CustomLayer", "TestModule",
            superclassNames: new[] { "OtherModule.SwiftLayer" });
        cls.IsObjCRooted = true;

        var output = EmitSingleObjCRootedClass(cls);

        var body = GetClassBody(output, "CustomLayer");
        Assert.Contains("base((ObjCRuntime.NativeHandle)handle.Handle)", body);
        Assert.Contains("DangerousRelease()", body);
        Assert.Contains("IntPtr ISwiftObject.SwiftHandle => Handle", body);
    }

    #endregion

    #region Emission — Declaration with ObjC Base Type

    [Fact]
    public void ObjCRooted_BoundaryClass_HasObjCBaseInDeclaration()
    {
        var output = EmitObjCRootedClass("MyLayer", "QuartzCore.CALayer");

        var declLine = GetClassDeclarationLine(output, "MyLayer");
        Assert.Contains("CoreAnimation.CALayer", declLine);
        Assert.Contains("ISwiftObject", declLine);
    }

    [Fact]
    public void ObjCRooted_BoundaryClass_NoIDisposable()
    {
        var output = EmitObjCRootedClass("MyLayer", "QuartzCore.CALayer");

        var declLine = GetClassDeclarationLine(output, "MyLayer");
        Assert.DoesNotContain("IDisposable", declLine);
    }

    [Fact]
    public void ObjCRooted_BoundaryClass_NoPayloadField()
    {
        var output = EmitObjCRootedClass("MyLayer", "QuartzCore.CALayer");

        var body = GetClassBody(output, "MyLayer");
        Assert.DoesNotContain("_payload", body);
        Assert.DoesNotContain("SwiftSafeHandle", body);
    }

    [Fact]
    public void ObjCRooted_BoundaryClass_NoDispose()
    {
        var output = EmitObjCRootedClass("MyLayer", "QuartzCore.CALayer");

        var body = GetClassBody(output, "MyLayer");
        Assert.DoesNotContain("public void Dispose()", body);
    }

    [Fact]
    public void ObjCRooted_BoundaryClass_NoFinalizer()
    {
        var output = EmitObjCRootedClass("MyLayer", "QuartzCore.CALayer");

        var body = GetClassBody(output, "MyLayer");
        Assert.DoesNotContain("~MyLayer()", body);
    }

    [Fact]
    public void ObjCRooted_BoundaryClass_HasSwiftHandleViaHandle()
    {
        var output = EmitObjCRootedClass("MyLayer", "QuartzCore.CALayer");

        var body = GetClassBody(output, "MyLayer");
        Assert.Contains("IntPtr ISwiftObject.SwiftHandle => Handle", body);
    }

    #endregion

    #region Emission — Constructor

    [Fact]
    public void ObjCRooted_Constructor_ChainsViaSwiftHandle()
    {
        var output = EmitObjCRootedClass("MyLayer", "QuartzCore.CALayer");

        var body = GetClassBody(output, "MyLayer");
        // Internal SwiftHandle constructor: base((ObjCRuntime.NativeHandle)handle.Handle)
        Assert.Contains("base((ObjCRuntime.NativeHandle)handle.Handle)", body);
    }

    [Fact]
    public void ObjCRooted_Constructor_HasDangerousRelease()
    {
        var output = EmitObjCRootedClass("MyLayer", "QuartzCore.CALayer");

        var body = GetClassBody(output, "MyLayer");
        Assert.Contains("DangerousRelease()", body);
    }

    [Fact]
    public void ObjCRooted_Constructor_HasProtectedNativeHandleCtor()
    {
        var output = EmitObjCRootedClass("MyLayer", "QuartzCore.CALayer");

        var body = GetClassBody(output, "MyLayer");
        Assert.Contains("protected MyLayer(ObjCRuntime.NativeHandle handle)", body);
        Assert.Contains("base(handle)", body);
    }

    #endregion

    #region Emission — NewFromPayload

    [Fact]
    public void ObjCRooted_NewFromPayload_DirectHandle()
    {
        var output = EmitObjCRootedClass("MyLayer", "QuartzCore.CALayer");

        var body = GetClassBody(output, "MyLayer");
        // No buffer allocation/free — handle is passed directly
        Assert.DoesNotContain("NativeMemory.Free", body);
        Assert.DoesNotContain("NativeMemory.Alloc", body);
        Assert.Contains("NewFromPayload", body);
    }

    [Fact]
    public void ObjCRooted_NewFromPayload_WrapsViaSwiftHandle()
    {
        var output = EmitObjCRootedClass("MyLayer", "QuartzCore.CALayer");

        var body = GetClassBody(output, "MyLayer");
        Assert.Contains("new SwiftHandle(handle)", body);
    }

    #endregion

    #region Emission — MarshalToSwift

    [Fact]
    public void ObjCRooted_MarshalToSwift_UsesHandle()
    {
        var output = EmitObjCRootedClass("MyLayer", "QuartzCore.CALayer");

        var body = GetClassBody(output, "MyLayer");
        Assert.Contains("MarshalToSwift", body);
        Assert.Contains("IntPtr selfPtr = Handle", body);
    }

    [Fact]
    public void ObjCRooted_MarshalToSwift_NoSafeHandleAddRelease()
    {
        var output = EmitObjCRootedClass("MyLayer", "QuartzCore.CALayer");

        var body = GetClassBody(output, "MyLayer");
        Assert.DoesNotContain("DangerousAddRef", body);
        // DangerousRelease is present (in constructor), but not in MarshalToSwift
        // Check the MarshalToSwift section specifically doesn't have SafeHandle patterns
        Assert.DoesNotContain("_payload.DangerousGetHandle", body);
    }

    #endregion

    #region Emission — Derived ObjC-Rooted Class

    [Fact]
    public void ObjCRooted_DerivedClass_InheritsFromSwiftParent()
    {
        var parent = CreateObjCRootedClassDecl("AnimatedControl",
            superclassUsr: "c:objc(cs)UIControl",
            superclassNames: new[] { "UIKit.UIControl" });
        var child = CreateObjCRootedClassDecl("AnimatedButton",
            superclassNames: new[] { "TestModule.AnimatedControl" });
        child.ResolvedSuperclass = parent;

        var output = EmitObjCRootedHierarchy(parent, child);

        // Child inherits from Swift parent, not directly from UIControl
        var childLine = GetClassDeclarationLine(output, "AnimatedButton");
        Assert.Contains("AnimatedControl", childLine);
        Assert.DoesNotContain("UIKit.UIControl", childLine);
    }

    [Fact]
    public void ObjCRooted_DerivedClass_NoPayload()
    {
        var parent = CreateObjCRootedClassDecl("AnimatedControl",
            superclassUsr: "c:objc(cs)UIControl",
            superclassNames: new[] { "UIKit.UIControl" });
        var child = CreateObjCRootedClassDecl("AnimatedButton",
            superclassNames: new[] { "TestModule.AnimatedControl" });
        child.ResolvedSuperclass = parent;

        var output = EmitObjCRootedHierarchy(parent, child);

        var childBody = GetClassBody(output, "AnimatedButton");
        Assert.DoesNotContain("_payload", childBody);
        Assert.DoesNotContain("SwiftSafeHandle", childBody);
    }

    [Fact]
    public void ObjCRooted_DerivedClass_NoDangerousRelease_InProtectedCtor()
    {
        var parent = CreateObjCRootedClassDecl("AnimatedControl",
            superclassUsr: "c:objc(cs)UIControl",
            superclassNames: new[] { "UIKit.UIControl" });
        var child = CreateObjCRootedClassDecl("AnimatedButton",
            superclassNames: new[] { "TestModule.AnimatedControl" });
        child.ResolvedSuperclass = parent;

        var output = EmitObjCRootedHierarchy(parent, child);

        // Derived SwiftHandle ctor DOES have DangerousRelease (entry point)
        var childBody = GetClassBody(output, "AnimatedButton");
        Assert.Contains("DangerousRelease()", childBody);
        // But the protected NativeHandle ctor should NOT
        Assert.Contains("protected AnimatedButton(ObjCRuntime.NativeHandle handle)", childBody);
    }

    #endregion

    #region Projection — ObjCRootedClassProjection

    [Fact]
    public void ObjCRootedClassProjection_PublicType_ReturnsTypeName()
    {
        var proj = new ObjCRootedClassProjection("MyLayer");
        Assert.Equal("MyLayer", proj.PublicType);
    }

    [Fact]
    public void ObjCRootedClassProjection_PInvokeType_IsIntPtr()
    {
        var proj = new ObjCRootedClassProjection("MyLayer");
        Assert.Equal("IntPtr", proj.PInvokeType);
    }

    [Fact]
    public void ObjCRootedClassProjection_ParameterPlan_UsesStackalloc()
    {
        var proj = new ObjCRootedClassProjection("MyLayer");
        var plan = proj.GetParameterPlan("layer");

        Assert.NotNull(plan.SetupStatements);
        Assert.True(plan.RequiresUnsafe);

        var setupCode = string.Join("\n", plan.SetupStatements.Select(s => RenderStatement(s)));
        Assert.Contains("stackalloc IntPtr[1]", setupCode);
        Assert.Contains("layer.Handle", setupCode);
    }

    [Fact]
    public void ObjCRootedClassProjection_ParameterPlan_PInvokeExpressionCastsToBufPtr()
    {
        var proj = new ObjCRootedClassProjection("MyLayer");
        var plan = proj.GetParameterPlan("layer");

        Assert.Contains("_layer_ptr", plan.PInvokeExpression);
    }

    [Fact]
    public void ObjCRootedClassProjection_ReturnPlan_DirectMarshalFromSwift()
    {
        var proj = new ObjCRootedClassProjection("MyLayer");
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        // No buffer allocation — direct MarshalFromSwift like ClassProjection
        Assert.False(plan.RequiresUnsafe);
        Assert.Contains("MarshalFromSwiftObject<MyLayer>", plan.PInvokeExpression);
        Assert.Contains("result", plan.PInvokeExpression);
    }

    [Fact]
    public void ObjCRootedClassProjection_DoesNotRequireSwiftWrapper()
    {
        var proj = new ObjCRootedClassProjection("MyLayer");
        Assert.False(proj.RequiresSwiftWrapper);
    }

    [Fact]
    public void ObjCRootedClassProjection_ParameterElementConversion_UsesHandle()
    {
        var proj = new ObjCRootedClassProjection("MyLayer");
        var conv = proj.GetParameterElementConversion("item");
        Assert.Equal("(IntPtr)item.Handle", conv);
    }

    #endregion

    #region Optional<ObjC reference> return — adopt the owned +1 (Fix A)

    // The Swift @_cdecl wrapper hands back the Some pointer of an Optional<ObjC reference>
    // return at +1 (`Unmanaged.passRetained($0 as AnyObject).toOpaque()`). The C# side must
    // ADOPT that +1 — GetINativeObject<T>(ptr, owns: true) — so the managed peer releases
    // exactly once on Dispose/finalize. A bare GetNSObject<T>(ptr) (owns: false) adds a SECOND
    // retain that nothing balances → one leaked object per call (issue: Optional<@objc-rooted>
    // return over-retain). These assert the adopt shape across all three ObjC inner projections
    // and both return strategies, mirroring the accessor getter path (OptionalAccessorGetterVisitor)
    // and the non-optional sibling (WrapperEmitter.Return: GetNSObject + DangerousRelease, net +0).
    //
    // Scope of THIS layer: the emitted plan expression is the only artifact the projection produces,
    // so these pin the ownership *shape* (adopt vs over-retain). The ARC no-leak *behavior* is pinned
    // at the runtime layer by ClassParamCallbackTests.TestOptionalObjCPayloadReturnNoLeak_KnownFixA
    // (alloc N + Dispose each → AssertNoLeaks), which runs on both Simulator (Mono) and device (NativeAOT).

    public static IEnumerable<object[]> OptionalObjCInnerProjections()
    {
        yield return new object[] { new ObjCRootedClassProjection("MyLayer") };
        yield return new object[] { new ObjCBridgedProjection("UIKit.UIImage") };
        yield return new object[] { new ObjCBridgeableProjection("Foundation.NSUrl") };
    }

    [Theory]
    [MemberData(nameof(OptionalObjCInnerProjections))]
    public void OptionalObjCReturn_Direct_AdoptsOwnedReference(ITypeProjection inner)
    {
        var proj = new OptionalProjection(inner);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.Direct);

        // Adopt: GetINativeObject<T>(ptr, true) — NOT a bare GetNSObject (which over-retains).
        Assert.Contains("GetINativeObject", plan.PInvokeExpression);
        Assert.Contains(", true)", plan.PInvokeExpression);
        Assert.DoesNotContain("GetNSObject", plan.PInvokeExpression);
        // nil → null guard preserved.
        Assert.Contains("result == IntPtr.Zero ? null", plan.PInvokeExpression);
    }

    [Theory]
    [MemberData(nameof(OptionalObjCInnerProjections))]
    public void OptionalObjCReturn_IndirectResult_AdoptsOwnedReference(ITypeProjection inner)
    {
        var proj = new OptionalProjection(inner);
        var plan = proj.GetReturnPlan("result", ReturnStrategy.IndirectResult);

        // Adopt: GetINativeObject<T>(ptr, true) — NOT a bare GetNSObject (which over-retains).
        Assert.Contains("GetINativeObject", plan.PInvokeExpression);
        Assert.Contains(", true)", plan.PInvokeExpression);
        Assert.DoesNotContain("GetNSObject", plan.PInvokeExpression);
        // Some pointer read through the sret/out buffer, with the nil → null guard preserved.
        Assert.Contains("*(IntPtr*)result", plan.PInvokeExpression);
        Assert.Contains("== IntPtr.Zero ? null", plan.PInvokeExpression);
        Assert.True(plan.RequiresUnsafe);
    }

    #endregion

    #region SwiftSelfKind — ObjCRootedClass

    [Fact]
    public void SwiftSelfKind_HasObjCRootedClassValue()
    {
        var kind = SwiftSelfKind.ObjCRootedClass;
        Assert.NotEqual(SwiftSelfKind.Class, kind);
        Assert.NotEqual(SwiftSelfKind.FrozenStructValue, kind);
    }

    #endregion

    #region ObjC-Rooted TypeRecord Classification

    [Fact]
    public void IsObjCRooted_ObjCRootedFlag_ReturnsTrue()
    {
        var record = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("PaymentSdkCore", "PaymentApiClient"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("PaymentSdkCore.PaymentApiClient"),
            MetadataAccessor = "testAccessor",
            Kind = TypeRecordKind.Class,
            Flags = TypeRecordFlags.ObjCRooted | TypeRecordFlags.RequiresMemoryManagement
        };

        Assert.True(MarshallingHelpers.IsObjCRooted(record));
        Assert.False(MarshallingHelpers.IsObjCBridged(record));
    }

    #endregion

    #region ObjC-Rooted Constructor Emission

    [Fact]
    public void Emit_ObjCRootedConstructor_UsesHandleDotHandle()
    {
        var output = EmitObjCRootedClass("MyLayer", "QuartzCore.CALayer");

        Assert.Contains("handle.Handle", output);
        Assert.DoesNotContain("handle.Pointer", output);
    }

    #endregion

    #region ObjC-Rooted Namespace Consistency

    [Fact]
    public void NamespaceMapping_ObjCBaseType_ConsistentAcrossPaths()
    {
        var mapped = MarshallingHelpers.MapQualifiedTypeToNet("QuartzCore.CALayer");
        Assert.Equal("CoreAnimation.CALayer", mapped);

        var cls = CreateClassDecl("MyLayer", "TestModule",
            superclassUsr: "c:objc(cs)CALayer",
            superclassNames: new[] { "QuartzCore.CALayer" });

        var baseName = MarshallingHelpers.GetObjCBaseTypeName(cls);
        Assert.Equal("CoreAnimation.CALayer", baseName);
    }

    #endregion

    #region Inherited-NSObject-property collision (Class 2 Bug A: Handle shadow)

    [Fact]
    public void ObjCRooted_MethodNamedHandle_RenamedToAvoidNSObjectPropertyShadow()
    {
        // An ObjC-rooted class inherits NSObject.Handle (a NativeHandle property). A Swift method
        // projected to `Handle` shadows it (CS0108) and breaks later `.Handle` reads (CS0428 — the
        // reported FBAEMKit crash). The sibling-property rename axis, seeded with the curated
        // NSObject property names, renames the method to `HandleMethod`.
        var cls = CreateClassDecl("AEMReporter", "TestModule",
            superclassUsr: "c:objc(cs)NSObject",
            superclassNames: new[] { "ObjectiveC.NSObject" });
        cls.IsObjCRooted = true;
        cls.Methods.Add(TestDecls.Method("handle"));

        var output = EmitSingleObjCRootedClass(cls);

        // The method is renamed (no bare `Handle(` method that would shadow the inherited property).
        Assert.Contains("HandleMethod(", output);
        Assert.DoesNotContain("public void Handle(", output);
    }

    [Fact]
    public void NonObjCRooted_MethodNamedHandle_KeepsBareHandle()
    {
        // Gating proof: a pure-Swift (non-ObjC-rooted) class does NOT inherit NSObject.Handle, so the
        // curated set must NOT be seeded — the method keeps its natural `Handle` name.
        var cls = CreateClassDecl("PureReporter", "TestModule");
        cls.IsObjCRooted = false;
        cls.Methods.Add(TestDecls.Method("handle"));

        var output = EmitNonObjCRootedClass(cls);

        Assert.Contains("Handle(", output);
        Assert.DoesNotContain("HandleMethod", output);
    }

    #endregion

    #region Test Helpers

    /// <summary>
    /// Emits a pure-Swift (non-ObjC-rooted) class through ClassHandler. Mirrors
    /// <see cref="EmitSingleObjCRootedClass"/> but registers the type WITHOUT the ObjCRooted flag and
    /// leaves <c>IsObjCRooted</c> false, so the curated NSObject-property seeding does not apply.
    /// </summary>
    private static string EmitNonObjCRootedClass(ClassDecl classDecl)
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");

        classDecl.ModuleDecl = moduleDecl;
        foreach (var method in classDecl.Methods)
        {
            method.ParentDecl = classDecl;
            method.ModuleDecl = moduleDecl;
        }
        foreach (var prop in classDecl.Properties)
        {
            prop.ParentDecl = classDecl;
            prop.ModuleDecl = moduleDecl;
            foreach (var accessor in prop.Accessors)
            {
                accessor.Method.ParentDecl = classDecl;
                accessor.Method.ModuleDecl = moduleDecl;
            }
        }
        testModule.RegisterType(
            classDecl.SwiftTypeName,
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", classDecl.Name),
                SwiftTypeName = classDecl.SwiftTypeName,
                MetadataAccessor = $"{classDecl.MangledName}Ma",
                Kind = TypeRecordKind.Class,
                Flags = TypeRecordFlags.RequiresMemoryManagement
            });

        var db = new TypeDatabase();
        db.AddModuleDatabase(new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib"));
        db.AddModuleDatabase(testModule);

        var csStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(new StringWriter());

        var handler = new ClassHandler(NullLogger<ClassHandler>.Instance);
        var conductor = new Conductor(NullLoggerFactory.Instance);

        var env = handler.Marshal(classDecl, db);
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return csStringWriter.ToString();
    }

    private static ClassDecl CreateClassDecl(
        string name,
        string moduleName,
        string? superclassUsr = null,
        string[]? superclassNames = null)
    {
        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            SuperclassUsr = superclassUsr,
            SuperclassNames = superclassNames?.ToList() ?? new List<string>(),
        };
    }

    private static ClassDecl CreateObjCRootedClassDecl(
        string name,
        string moduleName = "TestModule",
        string? superclassUsr = null,
        string[]? superclassNames = null)
    {
        var cls = CreateClassDecl(name, moduleName, superclassUsr, superclassNames);
        cls.IsObjCRooted = true;
        return cls;
    }

    private static TypeRecord CreateTypeRecord(TypeRecordFlags flags)
    {
        return new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "TestType"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.TestType"),
            MetadataAccessor = "testAccessor",
            Flags = flags,
            Kind = TypeRecordKind.Class
        };
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"objc_rooted_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Runs ModuleProcessor resolution which includes ResolveClassHierarchy and ObjCRooted computation.
    /// </summary>
    private static void RunResolution(params ClassDecl[] classDecls)
    {
        var typeDecls = new Dictionary<NamedTypeSpec, TypeDecl>();
        foreach (var cls in classDecls)
        {
            var typeSpec = new NamedTypeSpec(cls.SwiftTypeName.ModuleQualifiedName);
            typeDecls[typeSpec] = cls;
        }

        var typeDatabase = new TypeDatabase();
        var processor = new ModuleProcessor(
            classDecls[0].SwiftTypeName.Module,
            "/tmp/dummy.dylib",
            "TestModule",
            typeDecls,
            typeDatabase,
            NullLogger.Instance);

        processor.FinalizeTypeProcessingAndCreateModuleDatabase();
    }

    /// <summary>
    /// Emits a single ObjC-rooted boundary class (direct ObjC base).
    /// </summary>
    private static string EmitObjCRootedClass(string name, string superclassName)
    {
        var cls = CreateClassDecl(name, "TestModule",
            superclassUsr: "c:objc(cs)" + superclassName.Split('.').Last(),
            superclassNames: new[] { superclassName });
        cls.IsObjCRooted = true;

        return EmitSingleObjCRootedClass(cls);
    }

    private static string EmitSingleObjCRootedClass(ClassDecl classDecl)
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");

        classDecl.ModuleDecl = moduleDecl;
        foreach (var method in classDecl.Methods)
        {
            method.ParentDecl = classDecl;
            method.ModuleDecl = moduleDecl;
        }
        foreach (var prop in classDecl.Properties)
        {
            prop.ParentDecl = classDecl;
            prop.ModuleDecl = moduleDecl;
            foreach (var accessor in prop.Accessors)
            {
                accessor.Method.ParentDecl = classDecl;
                accessor.Method.ModuleDecl = moduleDecl;
            }
        }
        testModule.RegisterType(
            classDecl.SwiftTypeName,
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", classDecl.Name),
                SwiftTypeName = classDecl.SwiftTypeName,
                MetadataAccessor = $"{classDecl.MangledName}Ma",
                Kind = TypeRecordKind.Class,
                Flags = TypeRecordFlags.ObjCRooted | TypeRecordFlags.RequiresMemoryManagement
            });

        var db = new TypeDatabase();
        db.AddModuleDatabase(new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib"));
        db.AddModuleDatabase(testModule);

        var csStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(new StringWriter());

        var handler = new ClassHandler(NullLogger<ClassHandler>.Instance);
        var conductor = new Conductor(NullLoggerFactory.Instance);

        var env = handler.Marshal(classDecl, db);
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return csStringWriter.ToString();
    }

    /// <summary>
    /// Emits ObjC-rooted parent + derived pair.
    /// </summary>
    private static string EmitObjCRootedHierarchy(ClassDecl parent, ClassDecl child)
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");

        foreach (var cls in new[] { parent, child })
        {
            cls.ModuleDecl = moduleDecl;
            foreach (var method in cls.Methods)
            {
                method.ParentDecl = cls;
                method.ModuleDecl = moduleDecl;
            }
            foreach (var prop in cls.Properties)
            {
                prop.ParentDecl = cls;
                prop.ModuleDecl = moduleDecl;
                foreach (var accessor in prop.Accessors)
                {
                    accessor.Method.ParentDecl = cls;
                    accessor.Method.ModuleDecl = moduleDecl;
                }
            }
            testModule.RegisterType(
                cls.SwiftTypeName,
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", cls.Name),
                    SwiftTypeName = cls.SwiftTypeName,
                    MetadataAccessor = $"{cls.MangledName}Ma",
                    Kind = TypeRecordKind.Class,
                    Flags = TypeRecordFlags.ObjCRooted | TypeRecordFlags.RequiresMemoryManagement
                });
        }

        var db = new TypeDatabase();
        db.AddModuleDatabase(new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib"));
        db.AddModuleDatabase(testModule);

        var csStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(new StringWriter());

        var handler = new ClassHandler(NullLogger<ClassHandler>.Instance);
        var conductor = new Conductor(NullLoggerFactory.Instance);

        foreach (var cls in new[] { parent, child })
        {
            var env = handler.Marshal(cls, db);
            handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);
        }

        return csStringWriter.ToString();
    }

    private static ModuleDecl CreateModuleDecl(string name)
    {
        return new ModuleDecl
        {
            Name = name,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static string GetClassDeclarationLine(string output, string className)
    {
        var lines = output.Split('\n');
        foreach (var line in lines)
        {
            if (line.Contains($"class {className}") && line.Contains(":"))
                return line;
        }
        return string.Empty;
    }

    private static string GetClassBody(string output, string className)
    {
        var lines = output.Split('\n');
        int classLineIdx = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains($"class {className}") && lines[i].Contains(":"))
            {
                classLineIdx = i;
                break;
            }
        }
        if (classLineIdx < 0) return string.Empty;

        int start = classLineIdx;
        while (start < lines.Length && !lines[start].Contains("{"))
            start++;
        start++;

        int braceCount = 1;
        int end = start;
        for (int i = start; i < lines.Length; i++)
        {
            braceCount += lines[i].Count(c => c == '{');
            braceCount -= lines[i].Count(c => c == '}');
            if (braceCount <= 0)
            {
                end = i;
                break;
            }
        }

        return string.Join('\n', lines[start..end]);
    }

    /// <summary>
    /// Simple renderer for MarshalStatement to enable assertion on setup code.
    /// </summary>
    private static string RenderStatement(MarshalStatement statement)
    {
        return statement switch
        {
            MarshalStatement.Line line => line.Code,
            MarshalStatement.Block block => $"{block.Header} {{ {string.Join(" ", block.Body.Select(RenderStatement))} }}",
            _ => statement.ToString() ?? ""
        };
    }

    #endregion
}
