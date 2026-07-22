// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for ValidationRuleSet.ClassifyUnsupportedReference — the admission-control gate that
/// distinguishes a .NET-unavailable auto-bridged Foundation type (correctable: project or note
/// the gap) from a genuine SwiftUI/Combine-module / unemittable reference. Pins the
/// LocalizedStringResource scalar carve-out and the accurate NetUnavailableType skip reason for
/// Predicate, so neither is misreported as SwiftUIConstraint.
/// </summary>
public class ValidationRuleSetClassificationTests
{
    private const string Lsr = "Foundation.LocalizedStringResource";
    private const string Predicate = "Foundation.Predicate";

    [Fact]
    public void Classify_BareLocalizedStringResource_Strict_IsNetUnavailable()
    {
        // Without the carve-out a bare scalar LSR is .NET-unavailable, not a SwiftUI constraint.
        var kind = ValidationRuleSet.ClassifyUnsupportedReference(
            new NamedTypeSpec(Lsr), typeDatabase: null, out var offending);

        Assert.Equal(ValidationRuleSet.UnsupportedReferenceKind.NetUnavailable, kind);
        Assert.Equal(Lsr, offending);
    }

    [Fact]
    public void Classify_BareLocalizedStringResource_ScalarCarveOut_IsNone()
    {
        // On the simple concrete wire path a bare top-level scalar LSR is projectable as a string.
        var kind = ValidationRuleSet.ClassifyUnsupportedReference(
            new NamedTypeSpec(Lsr), typeDatabase: null, out _, allowProjectableScalar: true);

        Assert.Equal(ValidationRuleSet.UnsupportedReferenceKind.None, kind);
    }

    [Fact]
    public void Classify_OptionalLocalizedStringResource_ScalarCarveOut_StaysNetUnavailable()
    {
        // The carve-out never propagates into nested positions: Optional<LSR> reaches the
        // unbindable type through a generic argument and must stay dropped even when the caller
        // requested the scalar carve-out at the top level.
        var optional = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec(Lsr));

        var kind = ValidationRuleSet.ClassifyUnsupportedReference(
            optional, typeDatabase: null, out var offending, allowProjectableScalar: true);

        Assert.Equal(ValidationRuleSet.UnsupportedReferenceKind.NetUnavailable, kind);
        Assert.Equal(Lsr, offending);
    }

    [Fact]
    public void Classify_ArrayOfLocalizedStringResource_ScalarCarveOut_StaysNetUnavailable()
    {
        var array = new NamedTypeSpec("Swift.Array", new NamedTypeSpec(Lsr));

        var kind = ValidationRuleSet.ClassifyUnsupportedReference(
            array, typeDatabase: null, out _, allowProjectableScalar: true);

        Assert.Equal(ValidationRuleSet.UnsupportedReferenceKind.NetUnavailable, kind);
    }

    [Fact]
    public void Classify_Predicate_EvenWithScalarCarveOut_IsNetUnavailable()
    {
        // The carve-out is LocalizedStringResource-only. Predicate has no string projection, so it
        // stays NetUnavailable regardless of the flag — Plan 2's no-regression safety floor.
        var kind = ValidationRuleSet.ClassifyUnsupportedReference(
            new NamedTypeSpec(Predicate), typeDatabase: null, out var offending, allowProjectableScalar: true);

        Assert.Equal(ValidationRuleSet.UnsupportedReferenceKind.NetUnavailable, kind);
        Assert.Equal(Predicate, offending);
    }

    [Fact]
    public void Classify_SwiftUiModuleType_NullDb_IsOtherUnsupported()
    {
        // A genuine SwiftUI-module reference with no registered TypeRecord stays in the historical
        // "other unsupported" bucket — the unique name keeps it out of any shared skip set.
        var kind = ValidationRuleSet.ClassifyUnsupportedReference(
            new NamedTypeSpec("SwiftUI.ZzTestOnlyView"), typeDatabase: null, out var offending);

        Assert.Equal(ValidationRuleSet.UnsupportedReferenceKind.OtherUnsupported, kind);
        Assert.Equal("SwiftUI.ZzTestOnlyView", offending);
    }

    [Fact]
    public void ToSkipReason_NetUnavailable_MapsToNetUnavailableType()
    {
        Assert.Equal(
            SkipReason.NetUnavailableType,
            ValidationRuleSet.ToSkipReason(ValidationRuleSet.UnsupportedReferenceKind.NetUnavailable));
    }

    [Fact]
    public void ToSkipReason_OtherUnsupported_KeepsSwiftUIConstraint()
    {
        Assert.Equal(
            SkipReason.SwiftUIConstraint,
            ValidationRuleSet.ToSkipReason(ValidationRuleSet.UnsupportedReferenceKind.OtherUnsupported));
    }

    [Fact]
    public void ReferencesUnsupportedModule_BareLsr_StillTrue()
    {
        // The boolean shim defaults allowProjectableScalar=false, so the many existing callers that
        // gate on "references something unsupported" keep treating a bare LSR as unsupported — the
        // scalar carve-out is opt-in at the projecting wire sites only.
        Assert.True(ValidationRuleSet.ReferencesUnsupportedModule(new NamedTypeSpec(Lsr)));
        Assert.True(ValidationRuleSet.ReferencesUnsupportedModule(new NamedTypeSpec(Predicate)));
    }

    // --- C1: absent-framework-type guard (value-type USR + ObjC-bridge-synthesized record) ---
    //
    // StoreKit is autoBridge=true and `Transaction` is not in its value-type exclusion list, so
    // the loose ObjC-module test synthesizes a *class*-shaped ObjCBridged record for it even though
    // no `StoreKit.Transaction` exists in the .NET StoreKit namespace. Emitting that reference is
    // the CS0234 leak. The USR mangling suffix (`…TransactionV`, V = struct) is the precise signal
    // that the synthesized class record is wrong; the member must be skipped, not emitted.
    private const string StoreKitTransaction = "StoreKit.Transaction";
    private const string StoreKitTransactionValueUsr = "s:8StoreKit11TransactionV";
    private const string StoreKitTransactionClassUsr = "s:8StoreKit11TransactionC";

    [Fact]
    public void Classify_ValueTypeUsr_BridgeSynthesizedRecord_IsAbsentBridgedValueType()
    {
        var db = new TypeDatabase();
        var spec = new NamedTypeSpec(StoreKitTransaction) { Usr = StoreKitTransactionValueUsr };

        var kind = ValidationRuleSet.ClassifyUnsupportedReference(spec, db, out var offending);

        Assert.Equal(ValidationRuleSet.UnsupportedReferenceKind.AbsentBridgedValueType, kind);
        Assert.Equal(StoreKitTransaction, offending);
    }

    [Fact]
    public void Classify_ClassUsr_SameBridgedRecord_IsNotAbsentBridgedValueType()
    {
        // A class USR (suffix C) is legitimately bridgeable — the USR discriminator must not fire,
        // otherwise it would suppress every real ObjC class reference. Same type, same synthesized
        // record; only the USR kind differs. The surface is forced unavailable so this isolates the
        // USR discriminator: when the platform surface index can't confirm the type is absent, the
        // class USR alone must not trip the guard. (The surface-authoritative withdrawal of a
        // genuinely-absent type is pinned by Classify_ClassUsr_AbsentFromPresentSurface_* below.)
        using var surface = AppleTypeSurfaceIndex.OverrideDefaultForTest(index: null);
        var db = new TypeDatabase();
        var spec = new NamedTypeSpec(StoreKitTransaction) { Usr = StoreKitTransactionClassUsr };

        var kind = ValidationRuleSet.ClassifyUnsupportedReference(spec, db, out _);

        Assert.NotEqual(ValidationRuleSet.UnsupportedReferenceKind.AbsentBridgedValueType, kind);
    }

    [Fact]
    public void Classify_ValueTypeUsr_NoUsr_IsNotAbsentBridgedValueType()
    {
        // Without a USR the discriminator has no signal, so the USR guard cannot fire — this
        // preserves the pre-fix behavior for every reference the parser does not carry a USR for.
        // Surface forced unavailable so the assertion isolates the USR discriminator from the
        // separate surface-authoritative withdrawal path (pinned below).
        using var surface = AppleTypeSurfaceIndex.OverrideDefaultForTest(index: null);
        var db = new TypeDatabase();
        var spec = new NamedTypeSpec(StoreKitTransaction);

        var kind = ValidationRuleSet.ClassifyUnsupportedReference(spec, db, out _);

        Assert.NotEqual(ValidationRuleSet.UnsupportedReferenceKind.AbsentBridgedValueType, kind);
    }

    [Fact]
    public void Classify_ClassUsr_AbsentFromPresentSurface_IsAbsentBridgedValueType()
    {
        // Surface authority closes the gap the USR discriminator leaves: when the platform surface
        // index IS available and does not declare the type (StoreKit.Transaction is a Swift-only
        // StoreKit 2 struct with no Microsoft.iOS binding), the synthesized bridged class would
        // dangle — so the reference is withdrawn regardless of USR kind, including a class USR that
        // the USR discriminator alone would pass through. A present-but-empty surface models the
        // "type genuinely not in the binding" case deterministically.
        using var surface = AppleTypeSurfaceIndex.OverrideDefaultForTest(EmptySurface());
        var db = new TypeDatabase();
        var spec = new NamedTypeSpec(StoreKitTransaction) { Usr = StoreKitTransactionClassUsr };

        var kind = ValidationRuleSet.ClassifyUnsupportedReference(spec, db, out var offending);

        Assert.Equal(ValidationRuleSet.UnsupportedReferenceKind.AbsentBridgedValueType, kind);
        Assert.Equal(StoreKitTransaction, offending);
    }

    // A present, non-null surface index that declares no types — models "the reference assembly is
    // installed but genuinely does not contain the referenced type," so the withdraw-on-no-hit path
    // fires deterministically (distinct from the null/surface-unavailable fallback).
    private static AppleTypeSurfaceIndex EmptySurface()
        => new(
            new Dictionary<string, AppleTypeSurfaceEntry>(System.StringComparer.Ordinal),
            new Dictionary<string, AppleTypeSurfaceEntry>(System.StringComparer.Ordinal));

    [Fact]
    public void Classify_ValueTypeUsr_NullDb_IsNotAbsentBridgedValueType()
    {
        // The guard requires a TypeDatabase to confirm the bridge actually synthesizes an
        // ObjCBridged record; with no database it cannot positively identify the absent type.
        var spec = new NamedTypeSpec(StoreKitTransaction) { Usr = StoreKitTransactionValueUsr };

        var kind = ValidationRuleSet.ClassifyUnsupportedReference(spec, typeDatabase: null, out _);

        Assert.NotEqual(ValidationRuleSet.UnsupportedReferenceKind.AbsentBridgedValueType, kind);
    }

    [Fact]
    public void Classify_ValueTypeUsr_NonBridgingModule_IsNotAbsentBridgedValueType()
    {
        // A value-type USR in a module the ObjC bridge does not synthesize (no ObjCBridged record)
        // is not an absent *framework* type — the flag gate, not just the USR, must hold.
        var db = new TypeDatabase();
        var spec = new NamedTypeSpec("UnknownModule.Widget") { Usr = "s:13UnknownModule6WidgetV" };

        var kind = ValidationRuleSet.ClassifyUnsupportedReference(spec, db, out _);

        Assert.NotEqual(ValidationRuleSet.UnsupportedReferenceKind.AbsentBridgedValueType, kind);
    }

    [Fact]
    public void Classify_OptionalOfValueTypeUsr_IsAbsentBridgedValueType()
    {
        // The guard recurses through generic arguments: Optional<StoreKit.Transaction> with the
        // inner USR threaded must classify on the inner absent type (mirrors the parser fix that
        // threads the inner USR through Optional<>).
        var db = new TypeDatabase();
        var inner = new NamedTypeSpec(StoreKitTransaction) { Usr = StoreKitTransactionValueUsr };
        var optional = new NamedTypeSpec("Swift.Optional", inner);

        var kind = ValidationRuleSet.ClassifyUnsupportedReference(optional, db, out var offending);

        Assert.Equal(ValidationRuleSet.UnsupportedReferenceKind.AbsentBridgedValueType, kind);
        Assert.Equal(StoreKitTransaction, offending);
    }

    [Fact]
    public void ToSkipReason_AbsentBridgedValueType_MapsToAbsentFrameworkType()
    {
        Assert.Equal(
            SkipReason.AbsentFrameworkType,
            ValidationRuleSet.ToSkipReason(ValidationRuleSet.UnsupportedReferenceKind.AbsentBridgedValueType));
    }
}
