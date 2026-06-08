// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Properties;

/// <summary>
/// Regression coverage: a non-frozen struct with two sub-word Optional&lt;primitive&gt;
/// stored properties (<c>Bool?</c>) must keep its initializer and static factories.
///
/// The by-value sub-word Optional layout risk applies ONLY to a frozen struct projected
/// by value. A non-frozen struct is projected as an opaque pointer-passed class and never
/// lowers through a by-value ABI, so it must not be added to the type-skip pre-pass set.
/// A too-broad guard once added it, silently dropping its constructor and static factories
/// even though the struct itself still emitted — these tests fail to COMPILE if that
/// regresses (the constructor / factory members disappear).
/// </summary>
public class NonFrozenOptionalStructTests : TestBase
{
    public NonFrozenOptionalStructTests(TestResults results) : base(results) { }

    public void TestConstructorRoundTripBothFlags()
    {
        using var opts = new ToggleOptions(primaryEnabled: true, secondaryEnabled: false);
        AssertNotNull(opts, "ToggleOptions constructed");
        AssertTrue(opts.PrimaryEnabled.HasValue, "primary has value");
        AssertTrue(opts.PrimaryEnabled!.Value, "primary == true");
        AssertTrue(opts.SecondaryEnabled.HasValue, "secondary has value");
        AssertFalse(opts.SecondaryEnabled!.Value, "secondary == false");
    }

    public void TestConstructorRoundTripNilPrimary()
    {
        using var opts = new ToggleOptions(primaryEnabled: null, secondaryEnabled: true);
        AssertFalse(opts.PrimaryEnabled.HasValue, "primary is nil");
        AssertTrue(opts.SecondaryEnabled == true, "secondary == true");
    }

    public void TestStaticFactoryAllOn()
    {
        using var opts = ToggleOptions.GetAllOn();
        AssertTrue(opts.PrimaryEnabled == true, "allOn primary == true");
        AssertTrue(opts.SecondaryEnabled == true, "allOn secondary == true");
    }

    public void TestStaticFactoryDefaults()
    {
        using var opts = ToggleOptions.GetDefaults();
        AssertFalse(opts.PrimaryEnabled.HasValue, "defaults primary is nil");
        AssertTrue(opts.SecondaryEnabled == false, "defaults secondary == false");
    }

    public void TestOptionalSetterRoundTrip()
    {
        using var opts = new ToggleOptions(primaryEnabled: null, secondaryEnabled: null);
        opts.PrimaryEnabled = true;
        AssertTrue(opts.PrimaryEnabled == true, "primary set to true");
        opts.PrimaryEnabled = null;
        AssertFalse(opts.PrimaryEnabled.HasValue, "primary set back to nil");
    }
}
