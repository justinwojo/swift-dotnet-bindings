// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.FoundationInterop;

/// <summary>
/// Foundation.LocalizedStringResource (iOS 16+) is auto-bridged by Swift but is
/// absent from the .NET Foundation assembly, so the generator used to drop every
/// member touching it as a SwiftUIConstraint false positive. It now projects a
/// bare top-level scalar LocalizedStringResource to a C# <c>string</c>: a
/// parameter is rebuilt Swift-side with <c>LocalizedStringResource(stringLiteral:)</c>
/// and a return is resolved with <c>String(localized:)</c>.
///
/// A resource built from a string literal with no localization table resolves
/// back to that literal, so String -> LocalizedStringResource -> String is an
/// identity round-trip. These tests exercise that hop across all four wire
/// shapes the projection covers: method param, method return, init param, and a
/// stored-property get/set pair (fixture:
/// BindingTests/Sources/SwiftBindingsTestLib/Foundation/LocalizedStringResource.swift).
///
/// Scalar-only by design: an Optional/array LocalizedStringResource position
/// stays dropped (it reaches the unbindable type through a generic argument), so
/// <c>optionalLocalizedResource(_:)</c> must NOT be emitted — that absence is
/// pinned at the unit layer, not here.
/// </summary>
[global::System.Runtime.Versioning.SupportedOSPlatform("ios16.0")]
[global::System.Runtime.Versioning.SupportedOSPlatform("maccatalyst16.0")]
[global::System.Runtime.Versioning.SupportedOSPlatform("macos13.0")]
[global::System.Runtime.Versioning.SupportedOSPlatform("tvos16.0")]
public class LocalizedStringResourceTests : TestBase
{
    public LocalizedStringResourceTests(TestResults results) : base(results) { }

    // LocalizedStringResource as a method PARAMETER: the string we pass is rebuilt
    // into a resource Swift-side and resolved back to a String return.
    public void TestLocalizedResourceParameter_RoundTrips()
    {
        string result = TestLibFunctions.LocalizedResourceToString("Hello, world");
        AssertEqual("Hello, world", result, "LocalizedStringResource param resolves to its literal");
    }

    // LocalizedStringResource as a method RETURN: Swift builds the resource and the
    // projection resolves it to a String on the way out.
    public void TestLocalizedResourceReturn_RoundTrips()
    {
        string result = TestLibFunctions.MakeLocalizedResource("Greetings");
        AssertEqual("Greetings", result, "LocalizedStringResource return resolves to its literal");
    }

    // LocalizedStringResource as a CONSTRUCTOR parameter, read back through a
    // String-returning method.
    public void TestLocalizedResourceCtorParameter_RoundTrips()
    {
        using var banner = new LocalizedBanner("Breaking news");
        AssertEqual("Breaking news", banner.GetHeadlineString(), "ctor LocalizedStringResource param resolves to its literal");
    }

    // LocalizedStringResource as a stored PROPERTY (getter): a property projects to
    // SwiftString, so read it through ToString().
    public void TestLocalizedResourcePropertyGetter_RoundTrips()
    {
        using var banner = new LocalizedBanner("Initial headline");
        AssertEqual("Initial headline", banner.Headline.ToString(), "LocalizedStringResource property getter resolves to its literal");
    }

    // LocalizedStringResource as a stored PROPERTY (setter): assign a string, then
    // read it back through the String-returning method.
    public void TestLocalizedResourcePropertySetter_RoundTrips()
    {
        using var banner = new LocalizedBanner("Initial headline");
        banner.Headline = "Updated headline";
        AssertEqual("Updated headline", banner.GetHeadlineString(), "LocalizedStringResource property setter accepts a string and round-trips");
    }
}
