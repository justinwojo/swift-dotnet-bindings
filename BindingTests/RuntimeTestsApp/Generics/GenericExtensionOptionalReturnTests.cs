// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Generic-extension static dispatch with <c>Optional&lt;N&gt;</c>-shape returns
/// (<c>Mapper&lt;N&gt;.map(...) -&gt; N?</c> family).
///
/// Two distinct bugs covered here:
/// 1. <c>WrapperValidation.IsOptionalSupportedForCdecl</c> previously classified
///    <c>Optional&lt;τ_0_0&gt;</c> as <c>Optional&lt;protocol existential&gt;</c>
///    (the ABI <c>τ_0_X</c> lookup returns a Protocol-kind TypeRecord), so the
///    <c>@_cdecl</c> wrapper was never emitted and the C# P/Invoke silently fell
///    back to <c>CallConvSwift</c>. Nested <c>Optional&lt;Array&lt;N&gt;&gt;</c>
///    was rejected by the static-dispatch "simply parameterized" gate.
/// 2. <c>MarshallingHelpers.IsCdeclIndirectResultRequired</c>'s bound-generic
///    branch incorrectly required an indirect-result buffer for bound-generic
///    CLASS returns (e.g. <c>Mapper&lt;N&gt;</c>). The Swift @_cdecl wrapper
///    returned a retained <c>UnsafeMutableRawPointer</c> by value while the C#
///    P/Invoke expected the void+resultPtr shape — leaving the buffer
///    uninitialized and crashing on <c>swift_release</c> at dispose.
/// </summary>
public class GenericExtensionOptionalReturnTests : TestBase
{
    public GenericExtensionOptionalReturnTests(TestResults results) : base(results) { }

    // Factory + dispose round-trip — covers the bound-generic class ClassPointer
    // return convention. Crashed before the IsCdeclIndirectResultRequired fix.
    public void TestFactoryAndDispose_RoundTrips()
    {
        using var mapper = Functions.MakeGenericExtensionOptionalReturnMapper(tag: "smoke");
        AssertNotNull(mapper, "factory returns non-null mapper");
    }

    // Non-optional sibling — same dispatch shape with `N` (not `Optional<N>`) return.
    public void TestMapRequired_NonOptionalSibling_RoundTrips()
    {
        using var mapper = Functions.MakeGenericExtensionOptionalReturnMapper(tag: "gamma");
        var result = mapper.MapRequired();
        AssertNotNull(result, "MapRequired returns non-null");
        AssertEqual("gamma", result.Tag, "non-optional sibling preserves value");
    }

    public void TestMap_OptionalGenericParam_Some()
    {
        using var mapper = Functions.MakeGenericExtensionOptionalReturnMapper(tag: "alpha");
        var result = mapper.Map(false);
        AssertNotNull(result, "Map(false) returns Some");
        AssertEqual("alpha", result!.Tag, "stored tag round-trips");
    }

    public void TestMap_OptionalGenericParam_None()
    {
        using var mapper = Functions.MakeGenericExtensionOptionalReturnMapper(tag: "beta");
        var result = mapper.Map(true);
        AssertNull(result, "Map(true) returns nil");
    }

    public void TestMapArrayOptional_NestedShape_Some()
    {
        using var mapper = Functions.MakeGenericExtensionOptionalReturnMapper(tag: "delta");
        var result = mapper.MapArrayOptional(false);
        AssertNotNull(result, "MapArrayOptional(false) returns non-null array");
        AssertEqual(1, result!.Count, "array has one element");
        AssertEqual("delta", result[0].Tag, "array element round-trips");
    }

    public void TestMapArrayOptional_NestedShape_None()
    {
        using var mapper = Functions.MakeGenericExtensionOptionalReturnMapper(tag: "epsilon");
        var result = mapper.MapArrayOptional(true);
        AssertNull(result, "MapArrayOptional(true) returns nil");
    }

    public void TestLookup_DistinctSelectors_OptionalAndNonOptional()
    {
        using var mapper = Functions.MakeGenericExtensionOptionalReturnMapper(tag: "zeta");

        // Optional-return overload — LookupByOptional(bool) maps to lookup(byOptional:).
        var optResult = mapper.LookupByOptional(true);
        AssertNotNull(optResult, "LookupByOptional(true) returns Some");
        AssertEqual("zeta", optResult!.Tag, "byOptional payload preserved");

        var optNil = mapper.LookupByOptional(false);
        AssertNull(optNil, "LookupByOptional(false) returns nil");

        // Non-optional sibling — LookupByRequired(bool) maps to lookup(byRequired:).
        var reqResult = mapper.LookupByRequired(true);
        AssertNotNull(reqResult, "LookupByRequired(true) returns non-null");
        AssertEqual("zeta", reqResult.Tag, "byRequired payload preserved");
    }
}
