// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

#pragma warning disable SB0001 // CallConvSwift P/Invoke warning

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// End-to-end coverage for the cross-kind @_cdecl symbol dedup in
/// ModuleEmissionContext: a protocol extension method and a concrete class method
/// that project to the same @_cdecl symbol name. If the dedup regressed, the generated wrapper file would contain two
/// `@_cdecl` annotations for the same C symbol and swiftc would reject it
/// with "multiple definitions of symbol" — the BindingTests Swift compile
/// step would fail before this test even runs. Reaching the runtime check
/// proves the symbol was emitted exactly once and is callable from C#.
/// </summary>
public class ProtocolExtDuplicateSymbolTests : TestBase
{
    public ProtocolExtDuplicateSymbolTests(TestResults results) : base(results) { }

    public void TestExtensionDefaultIsCallable()
    {
        using var holder = new PExtDupSymHolder(statusFloor: 200);
        AssertTrue(holder.AcceptsStatus(250), "AcceptsStatus(250) within window");
        AssertTrue(!holder.AcceptsStatus(150), "AcceptsStatus(150) below window");
        AssertTrue(!holder.AcceptsStatus(300), "AcceptsStatus(300) above window");
    }

    public void TestExtensionDefaultBoundary()
    {
        using var holder = new PExtDupSymHolder(statusFloor: 400);
        AssertTrue(holder.AcceptsStatus(400), "AcceptsStatus floor inclusive");
        AssertTrue(!holder.AcceptsStatus(500), "AcceptsStatus ceiling exclusive");
    }
}
