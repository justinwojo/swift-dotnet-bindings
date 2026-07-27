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
/// <see cref="TypeDatabaseExtensions.TryProjectViaAppleSurface(SwiftTypeName, string?, string, string, AppleTypeSurfaceIndex?, bool)"/>
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
    // An anonymous C aggregate named through a typedef carries clang's 'A' suffix on the tag kind
    // (`@SA@`), which the bare `@S@` probe does not match — the shape every simd_*-style vector
    // typedef and many CoreFoundation aggregates arrive as.
    [InlineData("c:@SA@Foo", true)]
    [InlineData("c:@M@Mod@SA@Foo", true)]
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
        // No surface match and a clang value-type USR → a phantom class would dangle, so skip. The
        // index carries another type in the same namespace, so it is an authority for that namespace.
        var r = Project("Foo.SomeTypedef", "c:@T@SomeTypedef", "Foo", "SomeTypedef",
            IndexOf(Class("Foo", "SomethingElse")));
        Assert.True(Has(r!, TypeRecordFlags.AbsentAppleProjection));
    }

    [Theory]
    [InlineData("c:objc(cs)SomeClass")] // ObjC class reference
    [InlineData(null)]                   // no USR at all
    public void Project_NoHit_ObjectReference_RegistryPath_KeepsSynthesizedClass(string? usr)
    {
        // Registry-verify mode (withdrawOnNoHit: false) trusts the caller's record: a no-hit returns
        // null so the caller keeps it — the surface index may simply not cover a remapped type.
        var r = Project("Foo.SomeClass", usr, "Foo", "SomeClass", IndexOf());
        Assert.Null(r);
    }

    [Theory]
    [InlineData("c:objc(cs)SomeClass")] // ObjC class reference
    [InlineData(null)]                   // no USR at all
    public void Project_NoHit_ObjectReference_SynthesisPath_SkipMarked(string? usr)
    {
        // Synthesis mode (withdrawOnNoHit: true): a genuine no-hit under a populated,
        // platform-authoritative index means the synthesized qualified name is provably absent — an
        // emitted bridged class would be a CS0234/CS0246 dangling reference, so withdraw the member.
        // The index declares another type in the SAME namespace, which is what makes it an authority
        // for this reference's absence.
        var r = Project("Foo.SomeClass", usr, "Foo", "SomeClass",
            IndexOf(Class("Foo", "Unrelated")), withdrawOnNoHit: true);
        Assert.NotNull(r);
        Assert.True(Has(r!, TypeRecordFlags.AbsentAppleProjection));
        Assert.Equal(TypeRecordKind.Class, r.Kind);
    }

    // ---- Absence authority: only namespaces the index covers ---------------------------------

    [Theory]
    [InlineData("c:objc(cs)SbSiblingPayload")] // ObjC class reference
    [InlineData(null)]                          // no USR at all
    public void Project_NoHit_UncoveredNamespace_SynthesisPath_KeepsSynthesizedClass(string? usr)
    {
        // The index reflects ONE platform reference assembly and never the sibling binding packages
        // a binding also references. A namespace it holds no entry for at all is a namespace it has
        // no opinion about, so a miss there is not evidence of absence: the type may be supplied by
        // a referenced sibling package. Fall through to synthesis instead of withdrawing the member.
        var r = Project("SbSibling.SbSiblingPayload", usr, "SbSibling", "SbSiblingPayload",
            IndexOf(Class("UIKit", "Unrelated")), withdrawOnNoHit: true);
        Assert.Null(r);
    }

    [Fact]
    public void Project_NoHit_EmptyIndex_SynthesisPath_KeepsSynthesizedClass()
    {
        // An index that declares no types covers no namespace, so it can prove nothing absent.
        var r = Project("Foo.SomeClass", "c:objc(cs)SomeClass", "Foo", "SomeClass",
            IndexOf(), withdrawOnNoHit: true);
        Assert.Null(r);
    }

    [Fact]
    public void Project_ClangValueTypeUsr_UncoveredNamespace_KeepsSynthesizedClass()
    {
        // The clang value-type arm is an absence verdict too ("the binding declares no such type"),
        // so it needs the same authority: an uncovered namespace cannot support it. Asserted on the
        // synthesis path, where the arm previously fired regardless of caller trust.
        var r = Project("SbSibling.SbSiblingRecord", "c:@S@SbSiblingRecord",
            "SbSibling", "SbSiblingRecord", IndexOf(Class("UIKit", "Unrelated")),
            withdrawOnNoHit: true);
        Assert.Null(r);
    }

    [Fact]
    public void Project_ClangValueTypeUsr_UncoveredNamespace_RegistryPath_KeepsRegistryRecord()
    {
        // Same on the registry-verify path: the hand-authoritative remap is kept when the index has
        // no opinion about the namespace.
        var r = Project("SbSibling.SbSiblingRecord", "c:@SA@SbSiblingRecord",
            "SbSibling", "SbSiblingRecord", IndexOf(Class("UIKit", "Unrelated")));
        Assert.Null(r);
    }

    [Fact]
    public void Project_ClangValueTypeUsr_CoveredNamespace_SkipMarked()
    {
        // Namespace covered, name absent → the value type genuinely isn't in the binding, so the
        // withdrawal stands. This is the legitimate arm the coverage gate must preserve.
        var r = Project("UIKit.SomeAggregate", "c:@SA@SomeAggregate", "UIKit", "SomeAggregate",
            IndexOf(Class("UIKit", "Unrelated")), withdrawOnNoHit: true);
        Assert.True(Has(r!, TypeRecordFlags.AbsentAppleProjection));
    }

    [Fact]
    public void Project_BareClassHit_CoveredNamespace_SynthesisPath_SkipMarked()
    {
        // A qualified miss followed by a bare hit in an UNRELATED namespace is declined for name
        // correction (too ambiguous), and it says nothing about the namespace asked about — the
        // exact lookup already proved the name absent there. It must not suppress the withdrawal the
        // covered namespace supports, or a name the index disproved is retained.
        var index = IndexOf(Class("UIKit", "Unrelated"), Class("OtherFramework", "SomeClass"));
        var r = Project("UIKit.SomeClass", "c:objc(cs)SomeClass", "UIKit", "SomeClass",
            index, withdrawOnNoHit: true);
        Assert.True(Has(r!, TypeRecordFlags.AbsentAppleProjection));
    }

    [Fact]
    public void Project_BareStructHit_CoveredNamespace_SynthesisPath_SkipMarked()
    {
        // Same for a bare hit of a non-class shape: it neither corrects nor confirms the reference.
        var index = IndexOf(Class("UIKit", "Unrelated"), Struct("OtherFramework", "SomeAggregate"));
        var r = Project("UIKit.SomeAggregate", "c:objc(cs)SomeAggregate", "UIKit", "SomeAggregate",
            index, withdrawOnNoHit: true);
        Assert.True(Has(r!, TypeRecordFlags.AbsentAppleProjection));
    }

    [Fact]
    public void Project_NoIndex_SynthesisPath_StillDegradesToSynthesis()
    {
        // The withdraw is gated on an authoritative surface being present. With the workload absent
        // (null index) there is nothing to prove the reference absent, so even synthesis mode must
        // degrade to name synthesis rather than withdraw — the non-iOS-target soundness guard.
        var r = Project("UIKit.UIView", "c:objc(cs)UIView", "UIKit", "UIView",
            index: null, withdrawOnNoHit: true);
        Assert.Null(r);
    }

    [Fact]
    public void Project_BareClassHit_UncoveredNamespace_SynthesisPath_NotWithdrawn()
    {
        // A bare (cross-namespace) class match is declined for correction, and the namespace asked
        // about carries no entries at all — nothing here can prove the reference absent, so it is
        // left to synthesis (null), keeping a name that may already compile.
        var index = IndexOf(Class("RealFramework", "SomeClass"));
        var r = Project("SomeModule.SomeClass", "c:objc(cs)SomeClass", "SomeModule", "SomeClass",
            index, withdrawOnNoHit: true);
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
        AppleTypeSurfaceIndex? index, bool withdrawOnNoHit = false)
        => TypeDatabaseExtensions.TryProjectViaAppleSurface(
            SwiftTypeName.FromModuleQualifiedName(moduleQualified), usr, synthNamespace, synthName,
            index, withdrawOnNoHit);

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
