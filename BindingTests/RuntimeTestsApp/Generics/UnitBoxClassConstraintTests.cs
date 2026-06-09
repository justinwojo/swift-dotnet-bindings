// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Regression coverage for the Foundation.Dimension class-constraint projection
/// gap in WeatherKit's <c>Trend&lt;Dimension&gt;</c> family.
/// WeatherKit declares <c>Trend&lt;Dimension&gt; where Dimension : Foundation.Dimension</c>,
/// a TYPE-level generic constrained over a class hierarchy. Pre-fix the parser
/// tagged the <c>:</c> clause as <see cref="ConformanceKind.Protocol"/> and the
/// PInvokeHelperEmitter flatten-conformances gate added the constraint to
/// <c>unresolved</c> when the resolved type-database record's <c>Kind</c> came
/// back <c>Class</c>, tombstoning <c>Trend</c> / <c>TrendBaseline</c> /
/// <c>Percentiles</c> as <c>IndeterminatePwtShape</c> in <c>WeatherKit.cs</c>.
///
/// Post-fix:
///   - <c>PInvokeHelperEmitter.FlattenConformances</c> recognises the class
///     record and skips silently — class constraints don't add a witness-table
///     arg per Swift ABI; the metadata accessor takes only the TypeMetadata
///     arg already counted by <c>typeParams.Count</c>.
///   - <c>GenericTypeEmitter.GetWhereClause</c> and
///     <c>WrapperEmitter.Signature.BuildWhereClause</c> emit the projected C#
///     class name (<c>SwiftBindingsTestLib.UnitBase</c>) instead of an
///     <c>I{Name}</c> form, AND skip the <c>ISwiftObject</c> seed (a class
///     constraint already implies <c>ISwiftObject</c> and must come first per
///     CS0405/CS0406).
///   - <c>BoundGenericsHandler.SatisfiesConstraint</c> walks the
///     <c>SuperclassNames</c> chain to accept subclass type arguments — without
///     this the call-site to <c>UnitBox&lt;UnitKilometer&gt;</c> would fail
///     constraint resolution and the factory function would tombstone.
///   - <c>FoundationDatabase.xml</c> registers <c>Foundation.Dimension</c> →
///     <c>Foundation.NSDimension</c> so the cross-module WeatherKit case
///     resolves the same way.
///
/// Compile success of this file is itself a regression check — pre-fix
/// <c>UnitBox</c> was a tombstone comment, so <c>SwiftBindingsTestLib.UnitBox</c>
/// didn't exist as a C# type and these tests would not compile. The
/// runtime tests confirm the metadata accessor and class-typed property
/// getter round-trip correctly through the generated P/Invokes.
/// </summary>
public class UnitBoxClassConstraintTests : TestBase
{
    public UnitBoxClassConstraintTests(TestResults results) : base(results) { }

    public void TestUnitBoxFactory_ReturnsConstructedBoxedUnit()
    {
        // Compile-time gate: UnitBox<UnitKilometer> must exist as a usable
        // generic type with the class constraint satisfied. Pre-fix the type
        // emitted as a tombstone comment and this declaration would fail with
        // CS0246. The factory P/Invoke (Functions.MakeUnitKilometerBox) was
        // separately tombstoned with "generic constraint could not be
        // satisfied" before BoundGenericsHandler.SatisfiesConstraint learned
        // to walk SuperclassNames.
        using var box = Functions.MakeUnitKilometerBox();
        AssertNotNull(box, "UnitBox<UnitKilometer> factory should return a usable box");
    }

    public void TestUnitBoxFactory_UnitLabelRoundTrips()
    {
        // The property accessor on UnitBox<U>.UnitLabel calls the Swift
        // computed property `unitLabel` which dispatches through the U
        // metadata accessor. Verifies the metadata-accessor PInvoke shape
        // is correct — pre-fix the property couldn't even be reached because
        // the containing type was a tombstone.
        using var box = Functions.MakeUnitKilometerBox();
        AssertEqual("km", box.UnitLabel.ToString(),
            "UnitBox<UnitKilometer>.UnitLabel should round-trip the kilometer label");
    }

    public void TestUnitBoxFactory_UnitGetter_ReturnsConcreteSubclass()
    {
        // The class-typed property getter (`Unit: U`) marshals the U value
        // through the type-metadata-aware return path. The returned instance
        // must be the concrete UnitKilometer, not the abstract UnitBase —
        // class subtype identity must survive the round-trip.
        using var box = Functions.MakeUnitKilometerBox();
        var unit = box.Unit;
        AssertNotNull(unit, "UnitBox<UnitKilometer>.Unit must return a non-null instance");
        AssertEqual("km", unit.UnitLabel.ToString(),
            "Returned unit's UnitLabel should match the kilometer label");
    }

    public void TestUnitBoxConstructor_RoundTripsNewKilometer()
    {
        // Direct construction from C# proves the constructor P/Invoke accepts
        // a class-bound type argument: pre-fix the constructor was suppressed
        // alongside the tombstone, so consumers had no way into the type.
        using var unit = new UnitKilometer();
        using var box = new UnitBox<UnitKilometer>(unit);
        AssertEqual("km", box.UnitLabel.ToString(),
            "Constructor-built UnitBox<UnitKilometer> must round-trip the kilometer label");
    }
}
