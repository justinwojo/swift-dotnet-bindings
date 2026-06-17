// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.CrossModule;

/// <summary>
/// R6-1 regression gate (audit Regression-R6, finding #1). The generated wrapper
/// post-processor used to strip any <c>@_cdecl</c>/<c>@_silgen_name</c> block whose
/// body matched the bare short name of ANY internal type. The test library declares
/// an <c>@usableFromInline internal</c> type nested under <c>ShortNameCollisionFixture</c>
/// whose short name is <c>Data</c> — colliding with <c>Foundation.Data</c>'s short name —
/// and a public <c>makeCollisionData()</c> returning <c>Foundation.Data</c>. The nested
/// internal <c>Data</c> must contribute NO bare short name to the strip set, so neither
/// <c>makeCollisionData()</c>'s wrapper (which spells <c>Foundation.Data</c>) nor any
/// unrelated wrapper taking a bare <c>Data</c> parameter is deleted. If the regression
/// returns, <c>MakeCollisionData()</c> is absent (compile error) or its P/Invoke symbol
/// was stripped (<c>EntryPointNotFoundException</c> at call time).
/// </summary>
public class CrossModuleShortNameCollisionTests : TestBase
{
    public CrossModuleShortNameCollisionTests(TestResults results) : base(results) { }

    public void TestMakeCollisionData_RoundTripsBytes()
    {
        // The fixture returns a fixed 4-byte payload [0x2A, 0x2B, 0x2C, 0x2D].
        byte[] bytes = TestLibFunctions.MakeCollisionData();

        AssertNotNull(bytes, "makeCollisionData() should return a non-null byte[]");
        AssertEqual(4, bytes.Length, "makeCollisionData() should return 4 bytes");
        AssertEqual((byte)0x2A, bytes[0], "byte[0]");
        AssertEqual((byte)0x2B, bytes[1], "byte[1]");
        AssertEqual((byte)0x2C, bytes[2], "byte[2]");
        AssertEqual((byte)0x2D, bytes[3], "byte[3]");
    }
}
