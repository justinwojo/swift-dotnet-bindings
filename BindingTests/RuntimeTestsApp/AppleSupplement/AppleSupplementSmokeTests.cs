// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.AppleSupplement;

/// <summary>
/// End-to-end smoke for the <c>SwiftBindingsAppleSupplement</c> shim framework.
/// Calls the trivial <c>SBW_AppleSupplement_Probe_AddOne</c> @_cdecl symbol
/// defined in <c>src/Swift.Bindings.Apple/Shims/AppleSupplementProbe.swift</c>.
///
/// A passing run proves the whole supplement pipeline is wired end-to-end:
///   1. <c>nuke build-apple-supplement-xcframework</c> produced a multi-slice
///      xcframework with the expected install name
///      (<c>@rpath/SwiftBindingsAppleSupplement.framework/SwiftBindingsAppleSupplement</c>).
///   2. The xcframework was bundled into the runtime test app via NativeReference
///      and shipped into <c>.app/Frameworks/SwiftBindingsAppleSupplement.framework/</c>.
///   3. SwiftFrameworkResolver's <c>@rpath/{name}.framework/{name}</c> search rule
///      resolves the bare <c>SwiftBindingsAppleSupplement</c> library name at
///      <c>[LibraryImport]</c> time.
///   4. The probe symbol is exported and dispatches correctly.
///
/// Any subsequent supplement feature (KVO observe extensions, AttributedString
/// attribute getters/setters, …) layers on the same framework target — if this
/// probe regresses, every supplement-backed feature regresses with it.
/// </summary>
public partial class AppleSupplementSmokeTests : TestBase
{
    public AppleSupplementSmokeTests(TestResults results) : base(results) { }

    [LibraryImport(
        "SwiftBindingsAppleSupplement",
        EntryPoint = "SBW_AppleSupplement_Probe_AddOne")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial nint Probe_AddOne(nint value);

    public void TestProbe_AddOne_ReturnsIncrementedValue()
    {
        AssertEqual((nint)1,  Probe_AddOne(0),         "AddOne(0) == 1");
        AssertEqual((nint)43, Probe_AddOne(42),        "AddOne(42) == 43");
        AssertEqual((nint)0,  Probe_AddOne(-1),        "AddOne(-1) == 0");
    }
}
