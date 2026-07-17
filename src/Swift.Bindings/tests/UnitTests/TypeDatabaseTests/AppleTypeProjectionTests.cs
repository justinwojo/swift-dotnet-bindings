// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Behavioural coverage for the Apple-type projection layer — the decision tree that verifies a
/// synthesized ObjC-bridged reference against the real Microsoft.iOS surface and either corrects the
/// name, projects an integer enum as a value type, or skip-marks a value/static/absent/bridged-error
/// type the referencing member can't marshal. The tree is exercised through
/// <see cref="TypeDatabaseExtensions.TryProjectViaAppleSurface(SwiftTypeName, string?, string, string, AppleTypeSurfaceIndex?)"/>
/// with a hand-built index, so the decisions are observed without the installed iOS workload. The
/// pure USR helpers that feed it are covered directly.
/// </summary>
public class AppleTypeProjectionTests
{
    // ---- Pure USR helpers -------------------------------------------------------------------

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("c:objc(cs)UIView", "UIView")]
    [InlineData("c:objc(pl)UITableViewDataSource", "UITableViewDataSource")]
    [InlineData("c:@E@SKErrorCode", "SKErrorCode")]
    [InlineData("c:@T@CFStringRef", "CFStringRef")]
    [InlineData("c:@S@CGPoint", "CGPoint")]
    [InlineData("c:@M@StoreKit@E@SKErrorCode", "SKErrorCode")] // module-qualified → after LAST '@'
    [InlineData("s:So7NSErrorC", null)]                        // Swift USR → not clang-derived
    [InlineData("c:objc(cs)", null)]                           // nothing after ')'
    public void DeriveNameFromUsr_ExtractsLeafName(string? usr, string? expected)
        => Assert.Equal(expected, TypeDatabaseExtensions.DeriveNameFromUsr(usr));

    [Theory]
    [InlineData("c:@E@Foo", true)]
    [InlineData("c:@T@Foo", true)]
    [InlineData("c:@S@Foo", true)]
    [InlineData("c:@M@Mod@E@Foo", true)]
    [InlineData("c:objc(cs)Foo", false)]
    [InlineData("s:SiFoo", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsClangImportedValueTypeUsr_TrueOnlyForEnumTypedefStruct(string? usr, bool expected)
        => Assert.Equal(expected, TypeDatabaseExtensions.IsClangImportedValueTypeUsr(usr));

    [Theory]
    [InlineData("c:@E@SKErrorCode", "SKError", true)]              // the bridged NSError struct ref
    [InlineData("c:@M@StoreKit@E@SKErrorCode", "SKError", true)]   // module-qualified form
    [InlineData("c:@E@SKErrorCode", "Code", false)]               // direct ref to the nested .Code enum
    [InlineData("c:@E@SKErrorCode", "SKErrorCode", false)]        // flat enum Swift kept as SKErrorCode
    [InlineData("c:objc(cs)SKError", "SKError", false)]           // ObjC class USR, not a clang enum
    [InlineData("c:@E@FooBar", "FooBar", false)]                  // name doesn't end with "Code"
    [InlineData("c:@E@Code", "", false)]                          // struct name would be empty
    [InlineData(null, "SKError", false)]
    public void IsBridgedNSErrorReference_MatchesOnlyTheBridgedStructLeaf(
        string? usr, string leaf, bool expected)
        => Assert.Equal(expected, TypeDatabaseExtensions.IsBridgedNSErrorReference(usr, leaf));

    // ---- Projection decision tree ------------------------------------------------------------

    [Fact]
    public void Project_BridgedNSError_SkipMarked_EvenWithoutIndex()
    {
        // The NS_ERROR_ENUM decision is USR-only, so it must hold when the workload is absent.
        var r = Project("StoreKit.SKError", "c:@E@SKErrorCode", "StoreKit", "SKError", index: null);

        Assert.NotNull(r);
        Assert.True(Has(r!, TypeRecordFlags.AbsentAppleProjection));
        Assert.Equal(TypeRecordKind.Class, r.Kind);
    }

    [Fact]
    public void Project_BridgedNSError_SkipMarked_WithPopulatedIndex()
    {
        var index = IndexOf(Class("UIKit", "Unrelated"));
        var r = Project("StoreKit.SKError", "c:@E@SKErrorCode", "StoreKit", "SKError", index);

        Assert.True(Has(r!, TypeRecordFlags.AbsentAppleProjection));
    }

    [Fact]
    public void Project_NoIndex_NonErrorReference_DegradesToSynthesis()
    {
        // Workload absent → return null so the caller keeps its synthesized bridged class.
        var r = Project("UIKit.UIView", "c:objc(cs)UIView", "UIKit", "UIView", index: null);
        Assert.Null(r);
    }

    [Fact]
    public void Project_QualifiedEnumHit_ProjectsExternalValueTypeWithCorrectedName()
    {
        // The synthesized flattened name is wrong; the USR names the real enum. A qualified enum hit
        // corrects name+namespace and projects a value type, never OptionSet for a plain NS_ENUM.
        var index = IndexOf(Enum("UIKit", "UIImpactFeedbackStyle", underlying: "Int"));
        var r = Project(
            "UIKit.UIImpactFeedbackGenerator.FeedbackStyle", "c:@E@UIImpactFeedbackStyle",
            "UIKit", "UIImpactFeedbackGeneratorFeedbackStyle", index);

        Assert.NotNull(r);
        Assert.Equal(TypeRecordKind.Enum, r!.Kind);
        Assert.True(Has(r, TypeRecordFlags.SimpleEnum));
        Assert.True(Has(r, TypeRecordFlags.ExternalAppleEnum));
        Assert.True(Has(r, TypeRecordFlags.Frozen));
        Assert.False(Has(r, TypeRecordFlags.OptionSet));
        Assert.Equal("Int", r.RawValueTypeName);
        Assert.Equal("UIKit", r.CSharpTypeName.Namespace);
        Assert.Equal("UIImpactFeedbackStyle", r.CSharpTypeName.Name);
    }

    [Fact]
    public void Project_QualifiedFlagsEnumHit_AddsOptionSet()
    {
        var index = IndexOf(Enum("UIKit", "SomeOptions", underlying: "UInt", isFlags: true));
        var r = Project("UIKit.SomeOptions", "c:@E@SomeOptions", "UIKit", "SomeOptions", index);

        Assert.Equal(TypeRecordKind.Enum, r!.Kind);
        Assert.True(Has(r, TypeRecordFlags.ExternalAppleEnum));
        Assert.True(Has(r, TypeRecordFlags.OptionSet));
        Assert.Equal("UInt", r.RawValueTypeName);
    }

    [Fact]
    public void Project_QualifiedClassHit_CorrectsNameToTheRealClass()
    {
        var index = IndexOf(Class("UIKit", "UIRealClass"));
        var r = Project("UIKit.Whatever", "c:objc(cs)UIRealClass", "UIKit", "WrongSynthName", index);

        Assert.Equal(TypeRecordKind.Class, r!.Kind);
        Assert.True(Has(r, TypeRecordFlags.ObjCBridged));
        Assert.True(Has(r, TypeRecordFlags.RequiresMemoryManagement));
        Assert.False(Has(r, TypeRecordFlags.AbsentAppleProjection));
        Assert.Equal("UIKit", r.CSharpTypeName.Namespace);
        Assert.Equal("UIRealClass", r.CSharpTypeName.Name);
    }

    [Fact]
    public void Project_QualifiedStructHit_SkipMarked()
    {
        var index = IndexOf(Struct("CoreGraphics", "CGAffineTransform"));
        var r = Project(
            "CoreGraphics.CGAffineTransform", "c:@S@CGAffineTransform",
            "CoreGraphics", "CGAffineTransform", index);

        Assert.True(Has(r!, TypeRecordFlags.AbsentAppleProjection));
    }

    [Fact]
    public void Project_QualifiedStaticConstantsHit_SkipMarked()
    {
        var index = IndexOf(StaticConstants("UIKit", "UIWindowLevel"));
        var r = Project("UIKit.UIWindowLevel", "c:@T@UIWindowLevel", "UIKit", "UIWindowLevel", index);

        Assert.True(Has(r!, TypeRecordFlags.AbsentAppleProjection));
    }

    [Fact]
    public void Project_NoHit_ClangValueTypeUsr_SkipMarked()
    {
        // No surface match and a clang value-type USR → a phantom class would dangle, so skip.
        var r = Project("Foo.SomeTypedef", "c:@T@SomeTypedef", "Foo", "SomeTypedef", IndexOf());
        Assert.True(Has(r!, TypeRecordFlags.AbsentAppleProjection));
    }

    [Theory]
    [InlineData("c:objc(cs)SomeClass")] // ObjC class reference
    [InlineData(null)]                   // no USR at all
    public void Project_NoHit_ObjectReference_KeepsSynthesizedClass(string? usr)
    {
        var r = Project("Foo.SomeClass", usr, "Foo", "SomeClass", IndexOf());
        Assert.Null(r);
    }

    [Fact]
    public void Project_BareEnumHit_ProjectsExternalEnumUsingEntryNamespace()
    {
        // Integer-enum projection is namespace-independent — a cross-namespace (bare) match still
        // projects the value type, keyed off the reflected entry's own namespace.
        var index = IndexOf(Enum("RealFramework", "SomeEnum", underlying: "Int32"));
        var r = Project("SomeModule.SomeEnum", "c:@E@SomeEnum", "SomeModule", "SomeEnum", index);

        Assert.Equal(TypeRecordKind.Enum, r!.Kind);
        Assert.True(Has(r, TypeRecordFlags.ExternalAppleEnum));
        Assert.Equal("RealFramework", r.CSharpTypeName.Namespace);
        Assert.Equal("SomeEnum", r.CSharpTypeName.Name);
        Assert.Equal("Int32", r.RawValueTypeName);
    }

    [Fact]
    public void Project_BareClassHit_TooAmbiguous_KeepsSynthesizedClass()
    {
        // A bare (cross-namespace) class match must NOT override a name that may already compile.
        var index = IndexOf(Class("RealFramework", "SomeClass"));
        var r = Project("SomeModule.SomeClass", "c:objc(cs)SomeClass", "SomeModule", "SomeClass", index);
        Assert.Null(r);
    }

    // ---- Helpers ----------------------------------------------------------------------------

    private static TypeRecord? Project(
        string moduleQualified, string? usr, string synthNamespace, string synthName,
        AppleTypeSurfaceIndex? index)
        => TypeDatabaseExtensions.TryProjectViaAppleSurface(
            SwiftTypeName.FromModuleQualifiedName(moduleQualified), usr, synthNamespace, synthName, index);

    private static bool Has(TypeRecord record, TypeRecordFlags flag) => (record.Flags & flag) != 0;

    private static AppleTypeSurfaceIndex IndexOf(params AppleTypeSurfaceEntry[] entries)
    {
        var byFull = new Dictionary<string, AppleTypeSurfaceEntry>(StringComparer.Ordinal);
        var byBare = new Dictionary<string, AppleTypeSurfaceEntry>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            byFull[$"{e.Namespace}.{e.Name}"] = e;
            if (!byBare.ContainsKey(e.Name))
                byBare[e.Name] = e;
        }
        return new AppleTypeSurfaceIndex(byFull, byBare);
    }

    private static AppleTypeSurfaceEntry Enum(string ns, string name, string underlying, bool isFlags = false)
        => new(name, ns, AppleTypeSurfaceKind.Enum, underlying, isFlags);

    private static AppleTypeSurfaceEntry Class(string ns, string name)
        => new(name, ns, AppleTypeSurfaceKind.Class, null, false);

    private static AppleTypeSurfaceEntry Struct(string ns, string name)
        => new(name, ns, AppleTypeSurfaceKind.Struct, null, false);

    private static AppleTypeSurfaceEntry StaticConstants(string ns, string name)
        => new(name, ns, AppleTypeSurfaceKind.StaticConstants, null, false);
}
