// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the OperatorHandler class.
/// </summary>
public class OperatorHandlerTests
{
    #region IsSupportedOperator Tests

    [Theory]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData("*")]
    [InlineData("/")]
    [InlineData("%")]
    [InlineData("==")]
    [InlineData("!=")]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("<=")]
    [InlineData(">=")]
    [InlineData("&")]
    [InlineData("|")]
    [InlineData("^")]
    [InlineData("<<")]
    [InlineData(">>")]
    [InlineData("!")]
    [InlineData("~")]
    public void IsSupportedOperator_WithSupportedOperator_ReturnsTrue(string symbol)
    {
        Assert.True(OperatorHandler.IsSupportedOperator(symbol));
    }

    [Theory]
    [InlineData("??")]
    [InlineData("?.")]
    [InlineData("=>")]
    [InlineData("&&")]
    [InlineData("||")]
    [InlineData("+=")]
    [InlineData("-=")]
    [InlineData("*=")]
    [InlineData("...")]
    [InlineData("..<")]
    [InlineData("~=")]
    public void IsSupportedOperator_WithUnsupportedOperator_ReturnsFalse(string symbol)
    {
        Assert.False(OperatorHandler.IsSupportedOperator(symbol));
    }

    #endregion

    #region GetCSharpOperator Tests

    [Theory]
    [InlineData("+", "+")]
    [InlineData("-", "-")]
    [InlineData("*", "*")]
    [InlineData("==", "==")]
    [InlineData("!=", "!=")]
    [InlineData("<", "<")]
    [InlineData("!", "!")]
    public void GetCSharpOperator_WithSupportedOperator_ReturnsCorrectMapping(string swiftOp, string expectedCSharpOp)
    {
        var result = OperatorHandler.GetCSharpOperator(swiftOp);
        Assert.Equal(expectedCSharpOp, result);
    }

    [Fact]
    public void GetCSharpOperator_WithUnsupportedOperator_ReturnsNull()
    {
        var result = OperatorHandler.GetCSharpOperator("??");
        Assert.Null(result);
    }

    #endregion

    #region GetPInvokeMethodName Tests

    [Theory]
    [InlineData("+", "PInvoke_op_Addition")]
    [InlineData("-", "PInvoke_op_Subtraction")]
    [InlineData("*", "PInvoke_op_Multiply")]
    [InlineData("/", "PInvoke_op_Division")]
    [InlineData("==", "PInvoke_op_Equality")]
    [InlineData("!=", "PInvoke_op_Inequality")]
    [InlineData("<", "PInvoke_op_LessThan")]
    [InlineData(">", "PInvoke_op_GreaterThan")]
    public void GetPInvokeMethodName_WithKnownOperator_ReturnsCorrectName(string symbol, string expectedName)
    {
        var result = OperatorHandler.GetPInvokeMethodName(symbol);
        Assert.Equal(expectedName, result);
    }

    #endregion

    #region GetRequiredPairedOperator Tests

    [Theory]
    [InlineData("==", "!=")]
    [InlineData("!=", "==")]
    [InlineData("<", ">")]
    [InlineData(">", "<")]
    [InlineData("<=", ">=")]
    [InlineData(">=", "<=")]
    public void GetRequiredPairedOperator_WithPairedOperator_ReturnsCorrectPair(string symbol, string expectedPair)
    {
        var result = OperatorHandler.GetRequiredPairedOperator(symbol);
        Assert.Equal(expectedPair, result);
    }

    [Theory]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData("*")]
    [InlineData("/")]
    [InlineData("!")]
    [InlineData("~")]
    public void GetRequiredPairedOperator_WithNonPairedOperator_ReturnsNull(string symbol)
    {
        var result = OperatorHandler.GetRequiredPairedOperator(symbol);
        Assert.Null(result);
    }

    #endregion

    #region HasExplicitEqualityOperator Tests

    [Fact]
    public void HasExplicitEqualityOperator_WithEqualityOperator_ReturnsTrue()
    {
        var operators = new List<OperatorDecl>
        {
            CreateOperatorDecl("==", OperatorKind.Binary),
            CreateOperatorDecl("+", OperatorKind.Binary)
        };

        Assert.True(OperatorHandler.HasExplicitEqualityOperator(operators));
    }

    [Fact]
    public void HasExplicitEqualityOperator_WithoutEqualityOperator_ReturnsFalse()
    {
        var operators = new List<OperatorDecl>
        {
            CreateOperatorDecl("+", OperatorKind.Binary),
            CreateOperatorDecl("-", OperatorKind.Binary)
        };

        Assert.False(OperatorHandler.HasExplicitEqualityOperator(operators));
    }

    [Fact]
    public void HasExplicitEqualityOperator_WithEmptyList_ReturnsFalse()
    {
        var operators = new List<OperatorDecl>();
        Assert.False(OperatorHandler.HasExplicitEqualityOperator(operators));
    }

    #endregion

    #region HasExplicitInequalityOperator Tests

    [Fact]
    public void HasExplicitInequalityOperator_WithInequalityOperator_ReturnsTrue()
    {
        var operators = new List<OperatorDecl>
        {
            CreateOperatorDecl("!=", OperatorKind.Binary),
            CreateOperatorDecl("+", OperatorKind.Binary)
        };

        Assert.True(OperatorHandler.HasExplicitInequalityOperator(operators));
    }

    [Fact]
    public void HasExplicitInequalityOperator_WithoutInequalityOperator_ReturnsFalse()
    {
        var operators = new List<OperatorDecl>
        {
            CreateOperatorDecl("+", OperatorKind.Binary),
            CreateOperatorDecl("==", OperatorKind.Binary)
        };

        Assert.False(OperatorHandler.HasExplicitInequalityOperator(operators));
    }

    #endregion

    #region OperatorDecl Kind Tests

    [Fact]
    public void OperatorDecl_BinaryOperator_HasCorrectKind()
    {
        var opDecl = CreateOperatorDecl("+", OperatorKind.Binary);
        Assert.Equal(OperatorKind.Binary, opDecl.Kind);
    }

    [Fact]
    public void OperatorDecl_UnaryOperator_HasCorrectKind()
    {
        var opDecl = CreateOperatorDecl("!", OperatorKind.Unary);
        Assert.Equal(OperatorKind.Unary, opDecl.Kind);
    }

    [Fact]
    public void OperatorDecl_PrefixUnaryOperator_IsPrefixTrue()
    {
        var opDecl = CreateOperatorDecl("!", OperatorKind.Unary, isPrefix: true);
        Assert.True(opDecl.IsPrefix);
    }

    [Fact]
    public void OperatorDecl_PostfixUnaryOperator_IsPrefixFalse()
    {
        var opDecl = CreateOperatorDecl("++", OperatorKind.Unary, isPrefix: false);
        Assert.False(opDecl.IsPrefix);
    }

    #endregion

    #region OperatorDecl UnderlyingMethod Tests

    [Fact]
    public void OperatorDecl_HasUnderlyingMethod()
    {
        var opDecl = CreateOperatorDecl("+", OperatorKind.Binary);
        Assert.NotNull(opDecl.UnderlyingMethod);
        Assert.NotEmpty(opDecl.UnderlyingMethod.MangledName);
    }

    [Fact]
    public void OperatorDecl_UnderlyingMethodIsStatic()
    {
        var opDecl = CreateOperatorDecl("+", OperatorKind.Binary);
        Assert.Equal(MethodType.Static, opDecl.UnderlyingMethod.MethodType);
    }

    #endregion

    #region Helper Methods

    private static OperatorDecl CreateOperatorDecl(string symbol, OperatorKind kind, bool isPrefix = true)
    {
        var methodDecl = new MethodDecl
        {
            Name = symbol,
            MangledName = $"$s{symbol}",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                // Return type
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Bool"),
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                // First parameter
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.Point"),
                    Name = "left",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        // Add second parameter for binary operators
        if (kind == OperatorKind.Binary)
        {
            methodDecl.CSSignature.Add(new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("TestModule.Point"),
                Name = "right",
                PrivateName = "",
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = null
            });
        }

        return new OperatorDecl
        {
            Name = symbol,
            OperatorSymbol = symbol,
            Kind = kind,
            IsPrefix = isPrefix,
            UnderlyingMethod = methodDecl,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    #endregion
}
