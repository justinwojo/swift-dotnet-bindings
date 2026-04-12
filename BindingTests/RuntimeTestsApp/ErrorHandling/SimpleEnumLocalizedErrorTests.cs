// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.ErrorHandling;

/// <summary>
/// Runtime coverage for fix #4 (commit <c>4235d568</c>): simple-enum extensions
/// must be able to return <c>Optional&lt;String&gt;</c> so that
/// <c>LocalizedError.errorDescription</c> survives emission for a payloadless
/// Swift enum. Before fix #4 the property was silently dropped from the
/// generated C# extension, which meant <c>WeatherError.errorDescription</c> on
/// the real Apple WeatherKit framework produced no <c>GetErrorDescription()</c>
/// method at all. This test pins the generator fix against the synthetic
/// fixture <c>DemoLocalizedError</c> so a regression shows up instantly rather
/// than waiting for an Apple-framework snapshot run to notice.
///
/// The fixture <c>DemoLocalizedError</c> is deliberately a TRUE simple enum —
/// every case is payloadless. Adding even one associated-value case would
/// reclassify the enum out of the simple-enum emission branch and bypass
/// fix #4 entirely; the whole point of the fixture is the payloadless shape.
/// </summary>
public class SimpleEnumLocalizedErrorTests : TestBase
{
    public SimpleEnumLocalizedErrorTests(TestResults results) : base(results) { }

    /// <summary>
    /// Direct emission check: the <c>GetErrorDescription()</c> extension method
    /// exists on the generated <c>DemoLocalizedError</c> enum and returns the
    /// exact Swift <c>errorDescription</c> string — not the case name, not a
    /// null. If fix #4 regresses this assertion fails at compile time because
    /// the extension method is missing entirely.
    /// </summary>
    public void TestDemoLocalizedErrorMissingDescription()
    {
        var description = DemoLocalizedError.Missing.GetErrorDescription();
        TestLogger.Info($"DemoLocalizedError.Missing.GetErrorDescription() = \"{description}\"");
        AssertTrue(description is not null,
            "GetErrorDescription() must return non-null for .missing — " +
            "the Swift errorDescription returns a concrete String for this case.");
        AssertEqual("Demo: missing", description,
            "GetErrorDescription() must round-trip the exact LocalizedError value.");
    }

    /// <summary>
    /// Companion to TestDemoLocalizedErrorMissingDescription — proves both
    /// payloadless cases produce distinct descriptions, ruling out a
    /// regression where fix #4 emits the method but returns a constant.
    /// </summary>
    public void TestDemoLocalizedErrorTruncatedDescription()
    {
        var description = DemoLocalizedError.Truncated.GetErrorDescription();
        TestLogger.Info($"DemoLocalizedError.Truncated.GetErrorDescription() = \"{description}\"");
        AssertTrue(description is not null, "GetErrorDescription() must return non-null for .truncated.");
        AssertEqual("Demo: truncated", description,
            "GetErrorDescription() must round-trip the exact LocalizedError value.");
    }

    /// <summary>
    /// End-to-end throw/catch on the simple-enum LocalizedError path. Calls the
    /// Swift free function <c>throwDemoMissing</c> which throws
    /// <c>DemoLocalizedError.missing</c>, then verifies that the exception
    /// propagates across the interop boundary as a <see cref="SwiftException"/>.
    ///
    /// The <see cref="SwiftException.Message"/> comes from the runtime's
    /// <c>SBW_GetErrorDescription_SwiftBindingsTestLib</c> wrapper which uses
    /// <c>String(describing:)</c> — for a Swift enum value that returns the
    /// case name (<c>"missing"</c>), not the <c>LocalizedError.errorDescription</c>
    /// value. That is expected behavior: the generator's
    /// <c>GetErrorDescription()</c> extension is the C#-facing path for the
    /// localized description, and this test exercises both paths on the same
    /// fixture (the extension above, the throw/catch here).
    /// </summary>
    public void TestThrowDemoMissing()
    {
        try
        {
            TestLibFunctions.ThrowDemoMissing();
            throw new AssertionException("ThrowDemoMissing should throw");
        }
        catch (SwiftException ex)
        {
            TestLogger.Info($"ThrowDemoMissing threw with message: \"{ex.Message}\"");
            AssertTrue(ex.Message.Contains("missing"),
                $"Error message should contain the Swift case name 'missing', got: {ex.Message}");
        }
    }

    /// <summary>
    /// Companion to TestThrowDemoMissing — verifies the other payloadless case
    /// round-trips with a distinct case-name description.
    /// </summary>
    public void TestThrowDemoTruncated()
    {
        try
        {
            TestLibFunctions.ThrowDemoTruncated();
            throw new AssertionException("ThrowDemoTruncated should throw");
        }
        catch (SwiftException ex)
        {
            TestLogger.Info($"ThrowDemoTruncated threw with message: \"{ex.Message}\"");
            AssertTrue(ex.Message.Contains("truncated"),
                $"Error message should contain the Swift case name 'truncated', got: {ex.Message}");
        }
    }
}
