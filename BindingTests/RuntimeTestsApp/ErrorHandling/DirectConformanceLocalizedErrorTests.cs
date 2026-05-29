// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.ErrorHandling;

/// <summary>
/// Pins parity between the C# <c>[LibraryImport]</c> and Swift <c>@_cdecl</c>
/// emission paths when a simple enum conforms to <c>LocalizedError</c> directly
/// in its declaration (not via an extension). This is the shape used by real
/// Apple-shipping enums such as <c>ProximityReader.MobileDocumentReaderError</c>
/// and <c>FamilyControls.FamilyControlsError</c>. If the parity ever regresses,
/// <c>GetErrorDescription()</c> would throw <see cref="EntryPointNotFoundException"/>
/// at runtime instead of returning the string. Companion runtime gate for the
/// audit-doc RC-CDECL-PARITY claim on <c>MobileDocumentReaderError</c>.
/// </summary>
public class DirectConformanceLocalizedErrorTests : TestBase
{
    public DirectConformanceLocalizedErrorTests(TestResults results) : base(results) { }

    public void TestDirectDemoLocalizedErrorMissingDescription()
    {
        var description = DirectDemoLocalizedError.Missing.GetErrorDescription();
        TestLogger.Info($"DirectDemoLocalizedError.Missing.GetErrorDescription() = \"{description}\"");
        AssertTrue(description is not null,
            "GetErrorDescription() must return non-null for .missing — the Swift " +
            "errorDescription returns a concrete String for this case.");
        AssertEqual("Direct: missing", description,
            "GetErrorDescription() must round-trip the exact LocalizedError value " +
            "even when LocalizedError is declared directly on the enum.");
    }

    public void TestDirectDemoLocalizedErrorTruncatedDescription()
    {
        var description = DirectDemoLocalizedError.Truncated.GetErrorDescription();
        TestLogger.Info($"DirectDemoLocalizedError.Truncated.GetErrorDescription() = \"{description}\"");
        AssertTrue(description is not null, "GetErrorDescription() must return non-null for .truncated.");
        AssertEqual("Direct: truncated", description,
            "GetErrorDescription() must round-trip the exact LocalizedError value.");
    }
}
