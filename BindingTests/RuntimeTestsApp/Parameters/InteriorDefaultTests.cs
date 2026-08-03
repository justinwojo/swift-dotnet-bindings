// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Parameters;

/// <summary>
/// Tests for the "all-defaults" overload — the form that omits EVERY defaulted parameter,
/// interior ones included. A C# optional parameter has to be trailing, so a Swift default sitting
/// before a required parameter cannot survive as <c>= value</c> in the projected signature, and
/// trimming the trailing run alone never reaches it either.
///
/// Every test here is written as a CALL with concrete arguments, on purpose. Overload-set defects
/// (CS0121 — two emitted overloads that a single argument list binds equally well) exist only at a
/// consumer's call site; a gate that compiles the binding but never a caller cannot see them. The
/// assertions then double as the runtime check that Swift really did evaluate its own default
/// expression for each omitted argument, since the default's value is visible in the result.
/// </summary>
public class InteriorDefaultTests : TestBase
{
    public InteriorDefaultTests(TestResults results) : base(results) { }

    #region Interior defaults

    public void TestFormatInterior_AllDefaultsFormOmitsInteriorDefault()
    {
        // formatInterior(prefix:label:suffix:) — `label` is defaulted but `suffix` follows it, so
        // the primary cannot carry `= "mid"` and no trailing trim can drop it either.
        var shortForm = TestLibFunctions.FormatInterior("a", "z");
        AssertEqual("a|mid|z", shortForm,
            "Omitting the interior default must let Swift supply \"mid\"");

        var fullForm = TestLibFunctions.FormatInterior("a", "custom", "z");
        AssertEqual("a|custom|z", fullForm,
            "The full form must still pass the caller's value through");
    }

    public void TestDescribe_AllDefaultsFormOmitsBothInteriorAndTrailing()
    {
        // describe(a:b:c:d:) — `b` is interior (unreachable as a C# optional) and `d` is trailing
        // with a C#-expressible literal. The all-defaults form drops both together.
        using var box = new InteriorDefaultBox();

        var shortForm = box.Describe(1, 3);
        AssertEqual("a=1 b=7 c=3 d=9", shortForm,
            "Both the interior default (b) and the trailing default (d) must come from Swift");

        // The trailing default IS expressible in C#, so the primary carries `d = 9` inline.
        var withoutTrailing = box.Describe(1, 2, 3);
        AssertEqual("a=1 b=2 c=3 d=9", withoutTrailing,
            "The primary's C# optional must still default d to 9");

        var fullForm = box.Describe(1, 2, 3, 4);
        AssertEqual("a=1 b=2 c=3 d=4", fullForm, "Every argument supplied explicitly");
    }

    public void TestCapStranded_ShortestFormReachableBeyondTheOverloadCap()
    {
        // capStranded has five trailing defaults and a per-method overload cap of four, so trimming
        // alone stops at four remaining arguments. None of the defaults is a C# constant, so the
        // primary carries no inline defaults either — the all-defaults overload is the only way to
        // call this with the single argument Swift actually requires.
        var shortest = TestLibFunctions.CapStranded("r");
        AssertEqual("r/autoautoautoautoauto", shortest,
            "All five defaults must be evaluated by Swift");

        var partial = TestLibFunctions.CapStranded("r", "1", "2");
        AssertEqual("r/12autoautoauto", partial,
            "A trimmed overload must supply the caller's values and default the rest");

        var full = TestLibFunctions.CapStranded("r", "1", "2", "3", "4", "5");
        AssertEqual("r/12345", full, "The full form passes every value through");
    }

    public void TestUnlabeledInteriorDefault_LabeledFollowerStaysOmissible()
    {
        // unlabeledInteriorDefault(_:second:) — the defaulted parameter is UNLABELED, but the kept
        // parameter after it carries a label, and a label pins its own argument no matter what was
        // dropped ahead of it. The one-argument call below is the assertion that the all-defaults
        // form was synthesized, and its value proves Swift evaluated `first`'s own default.
        var shortForm = TestLibFunctions.UnlabeledInteriorDefault(6);
        AssertEqual("first=1 second=6", shortForm,
            "Omitting the unlabeled default must let Swift supply 1");

        var full = TestLibFunctions.UnlabeledInteriorDefault(5, 6);
        AssertEqual("first=5 second=6", full,
            "The full form must bind both arguments in declaration order");
    }

    public void TestUnlabeledPositionalDefault_FailsClosedToTheFullForm()
    {
        // unlabeledPositionalDefault(_:_:) — the defaulted parameter is UNLABELED and so is the kept
        // parameter after it, so omitting the default would leave one positional argument that Swift
        // binds to the FIRST parameter, leaving the second unfilled. No all-defaults overload is
        // synthesized. The absence is enforced structurally rather than by this call: a synthesized
        // form would emit a shim whose Swift call does not compile, which the compile gate catches as
        // an increase in post-processor-stripped wrapper blocks (baseline 0).
        var result = TestLibFunctions.UnlabeledPositionalDefault(5, 6);
        AssertEqual("first=5 second=6", result,
            "The full form must bind both arguments in declaration order");
    }

    public void TestAnnotate_SwiftAmbiguousReducedCallFailsClosed()
    {
        // Two `annotate` declarations differ only in the TYPE of a defaulted parameter. Omitting
        // every defaulted argument would leave `annotate(subject)`, which fits both — swiftc rejects
        // that as an ambiguous use, and since the wrapper library compiles as a unit it would take
        // every binding in this module down, not just this member. No all-defaults form is
        // synthesized; the shortest emitted overload keeps `note`, which is what tells them apart.
        var tagged = TestLibFunctions.Annotate("x", 5);
        AssertEqual("n=5 x/autoautoautoauto", tagged,
            "The int-note overload must default t1-t4 through Swift");

        var texted = TestLibFunctions.Annotate("x", "hi");
        AssertEqual("s=hi x/autoautoautoauto", texted,
            "The string-note overload must stay independently reachable");

        var full = TestLibFunctions.Annotate("x", 5, "1", "2", "3", "4");
        AssertEqual("n=5 x/1234", full, "The full form passes every value through");
    }

    #endregion

    #region Overload-set validity (CS0121)

    public async Task TestResolveAsync_SingleArgumentCallIsUnambiguous()
    {
        // AsyncDefaultLattice has two `resolve` overloads. The generator would otherwise synthesize
        // a trimmed `ResolveAsync(int, CancellationToken = default)` for `resolve(id:tag:)`, which a
        // one-argument call binds exactly as well as `resolve(id:retries:)`'s
        // `ResolveAsync(int, int = 3, CancellationToken = default)` — neither supplies every
        // parameter, so C# reports CS0121 and the consumer cannot compile this line at all. The
        // trimmed candidate is declined; this call compiling IS the assertion.
        using var lattice = new AsyncDefaultLattice();

        var defaulted = await WithTimeout(lattice.ResolveAsync(7), DefaultAsyncTimeout);
        AssertEqual("id=7 retries=3", defaulted,
            "A one-argument call must bind the retries overload and default retries to 3");

        var explicitRetries = await WithTimeout(lattice.ResolveAsync(7, 5), DefaultAsyncTimeout);
        AssertEqual("id=7 retries=5", explicitRetries, "Explicit retries must pass through");

        var tagged = await WithTimeout(lattice.ResolveAsync(7, "x"), DefaultAsyncTimeout);
        AssertEqual("id=7 tag=x", tagged, "The tag overload must still be reachable");
    }

    #endregion
}
