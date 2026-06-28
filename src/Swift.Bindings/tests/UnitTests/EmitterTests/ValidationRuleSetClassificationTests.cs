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
}
