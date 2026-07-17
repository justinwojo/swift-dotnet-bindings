// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.ObjC;
using Xunit;

namespace BindingsGeneration.Tests.ObjCTests;

/// <summary>
/// Classification of real clang AST-dump stderr into a specific, user-actionable SWIFTBIND109
/// diagnosis. The stderr strings below are the exact shapes captured from the mixed-framework
/// corpus repros (TelemetryDeck, CombineCocoa, FlexLayout, swift-system) — each maps to a distinct
/// upstream-packaging failure mode the generator cannot itself fix.
/// </summary>
public class ObjCClangDiagnosticsTests
{
    [Fact]
    public void Classify_MissingHeader_NamesHeaderAndCause()
    {
        // TelemetryDeck: the umbrella imports a self-qualified header the xcframework never ships.
        var stderr = "In file included from <built-in>:1:\n" +
            "/tmp/TelemetryClient.framework/Headers/TelemetryClient.h:9:9: fatal error: " +
            "'TelemetryDeck/TelemetryClient.h' file not found\n#import <TelemetryDeck/TelemetryClient.h>\n";

        var diag = ObjCClangDiagnostics.Classify("TelemetryClient", stderr);

        Assert.Equal(ObjCClangFailureCause.MissingHeader, diag.Cause);
        Assert.Equal("TelemetryDeck/TelemetryClient.h", diag.OffendingToken);
        Assert.Contains("SWIFTBIND109", diag.Message);
        Assert.Contains("TelemetryDeck/TelemetryClient.h", diag.Message);
        Assert.Contains("TelemetryClient", diag.Message);
    }

    [Fact]
    public void Classify_MissingHeader_NestedFrameworkCombineCocoa()
    {
        var stderr = "/tmp/CombineCocoa.framework/Headers/CombineCocoa.h:3:9: fatal error: " +
            "'CombineCocoa/ObjcDelegateProxy.h' file not found\n";

        var diag = ObjCClangDiagnostics.Classify("CombineCocoa", stderr);

        Assert.Equal(ObjCClangFailureCause.MissingHeader, diag.Cause);
        Assert.Equal("CombineCocoa/ObjcDelegateProxy.h", diag.OffendingToken);
        Assert.Contains("ObjcDelegateProxy.h", diag.Message);
    }

    [Fact]
    public void Classify_PlatformIncompatibleHeader_NamesIdentifierAndFile()
    {
        // swift-system: io_uring.h is a Linux include compiled unconditionally; _NSIG is undefined
        // on Apple platforms. The diagnosis must name both the offending identifier and the header.
        var stderr = "/tmp/SystemPackage.framework/Headers/io_uring.h:245:66: error: " +
            "use of undeclared identifier '_NSIG'\n";

        var diag = ObjCClangDiagnostics.Classify("SystemPackage", stderr);

        Assert.Equal(ObjCClangFailureCause.PlatformIncompatibleHeader, diag.Cause);
        Assert.Equal("_NSIG", diag.OffendingToken);
        Assert.Contains("io_uring.h", diag.Message);
        Assert.Contains("_NSIG", diag.Message);
        Assert.Contains("platform", diag.Message);
    }

    [Fact]
    public void Classify_MissingModule_NamesModule()
    {
        // FlexLayout's swiftinterface imports a Yoga module the distribution omits.
        var stderr = "arm64-apple-ios-simulator.private.swiftinterface:6:8: error: " +
            "no such module 'FlexLayoutYogaKit'\n";

        var diag = ObjCClangDiagnostics.Classify("FlexLayout", stderr);

        Assert.Equal(ObjCClangFailureCause.MissingModule, diag.Cause);
        Assert.Equal("FlexLayoutYogaKit", diag.OffendingToken);
        Assert.Contains("FlexLayoutYogaKit", diag.Message);
    }

    [Fact]
    public void Classify_MissingModule_ClangDriverWording()
    {
        // clang's own driver phrases the same failure differently from swift's frontend: an @import
        // that can't be resolved emits "module 'X' not found" (no "no such module").
        var stderr = "<module-includes>:1:9: fatal error: module 'FlexLayoutYogaKit' not found\n" +
            "@import FlexLayoutYogaKit;\n";

        var diag = ObjCClangDiagnostics.Classify("FlexLayout", stderr);

        Assert.Equal(ObjCClangFailureCause.MissingModule, diag.Cause);
        Assert.Equal("FlexLayoutYogaKit", diag.OffendingToken);
        Assert.Contains("FlexLayoutYogaKit", diag.Message);
    }

    [Fact]
    public void Classify_MissingHeader_TakesPrecedenceOverLaterCascade()
    {
        // A "file not found" fatal error aborts the parse before any cascade of undeclared-identifier
        // errors it triggers, so the header cause must win even when both appear in the stderr.
        var stderr = "a.h:1:9: fatal error: 'UIView+Yoga.h' file not found\n" +
            "b.h:2:2: error: use of undeclared identifier 'YGNodeRef'\n";

        var diag = ObjCClangDiagnostics.Classify("FlexLayout", stderr);

        Assert.Equal(ObjCClangFailureCause.MissingHeader, diag.Cause);
        Assert.Equal("UIView+Yoga.h", diag.OffendingToken);
    }

    [Fact]
    public void Classify_Unclassified_ReportsFirstErrorLineVerbatim()
    {
        var stderr = "some/path.h:10:1: error: expected ';' after top level declarator\n";

        var diag = ObjCClangDiagnostics.Classify("Whatever", stderr);

        Assert.Equal(ObjCClangFailureCause.Unknown, diag.Cause);
        Assert.Null(diag.OffendingToken);
        Assert.Contains("SWIFTBIND109", diag.Message);
        Assert.Contains("expected ';'", diag.Message);
    }
}
