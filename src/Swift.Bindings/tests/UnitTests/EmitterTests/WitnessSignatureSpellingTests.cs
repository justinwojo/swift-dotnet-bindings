// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Signature facts a synthesized protocol witness must reproduce for the conformance checker to
/// accept it: the typed-throws error, and whether a requirement is only distinguishable from a
/// sibling by its return type.
/// </summary>
public class WitnessSignatureSpellingTests
{
    [Fact]
    public void RenderThrowsClause_TypedThrows_SpellsErrorType()
    {
        var method = TestDecls.Method("count", throws: true);
        method.ThrownErrorType = new NamedTypeSpec("TestModule.LoaderError");

        Assert.Equal(" throws(TestModule.LoaderError)", EveryProtocolEmitter.RenderThrowsClause(method, method.Throws));
    }

    [Theory]
    [InlineData(true, " throws")]
    [InlineData(false, "")]
    public void RenderThrowsClause_UntypedOrNonThrowing(bool throws, string expected)
    {
        var method = TestDecls.Method("count", throws: throws);

        Assert.Equal(expected, EveryProtocolEmitter.RenderThrowsClause(method, throws));
    }

    [Fact]
    public void IsReturnTypeOnlyOverloadPair_SameParametersDifferentReturn_IsPair()
    {
        var token = TestDecls.Param("token", new NamedTypeSpec("Swift.String"));
        var code = TestDecls.Method("enroll", parameters: new[] { token }, returnType: new NamedTypeSpec("Swift.Int32"));
        var ticket = TestDecls.Method("enroll", parameters: new[] { token }, returnType: new NamedTypeSpec("TestModule.Ticket"));

        Assert.True(MethodWrapperEmitter.IsReturnTypeOnlyOverloadPair(code, ticket));
        Assert.False(MethodWrapperEmitter.IsReturnTypeOnlyOverloadPair(code, code));
    }

    [Fact]
    public void IsReturnTypeOnlyOverloadPair_DifferentArity_IsNotPair()
    {
        var token = TestDecls.Param("token", new NamedTypeSpec("Swift.String"));
        var email = TestDecls.Param("email", new NamedTypeSpec("Swift.String"));
        var code = TestDecls.Method("enroll", parameters: new[] { token }, returnType: new NamedTypeSpec("Swift.Int32"));
        var withEmail = TestDecls.Method("enroll", parameters: new[] { token, email }, returnType: new NamedTypeSpec("TestModule.Ticket"));

        Assert.False(MethodWrapperEmitter.IsReturnTypeOnlyOverloadPair(code, withEmail));
    }
}
