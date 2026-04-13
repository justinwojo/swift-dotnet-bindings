// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using SwiftBindingsTestLibDependency;

namespace RuntimeTestsApp.CrossModule;

/// <summary>
/// Tests for cross-module type alias resolution — Swift type aliases that reference
/// types from a different module (e.g., typealias TokenA = DependencyTokenA).
/// </summary>
[Skip("Generator resolves cross-module aliases at the type-database level but does not yet emit bound C# types for alias targets — the emitter skips types whose ABI module differs from the registered module")]
public class CrossModuleAliasTests : TestBase
{
    public CrossModuleAliasTests(TestResults results) : base(results) { }

    public void TestTokenHolder_Creation()
    {
        var holder = new TokenHolder(idA: 10, idB: 20);
        AssertEqual("Token(10)", holder.TokenADescription, "TokenA description");
        AssertEqual("Token(20)", holder.TokenBDescription, "TokenB description");
    }

    public void TestMakeTokenA_ReturnsToken()
    {
        var token = TestLibFunctions.MakeTokenA(id: 42);
        AssertEqual(42, token.Identifier, "makeTokenA should set identifier");
    }

    public void TestDescribeTokenA_ReturnsDescription()
    {
        var token = TestLibFunctions.MakeTokenA(id: 99);
        var desc = TestLibFunctions.DescribeTokenA(token);
        AssertEqual("Token(99)", desc, "describeTokenA should return Token(99)");
    }
}
