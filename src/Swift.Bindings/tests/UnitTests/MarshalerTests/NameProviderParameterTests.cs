// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for NameProvider.GetCSharpParameterName(), IsGeneratedArgName(),
/// StripCSharpKeywordPrefix(), and SanitizeForCSharp().
/// </summary>
public class NameProviderParameterTests
{
    private static ArgumentDecl MakeArg(string name, string privateName = "")
    {
        return new ArgumentDecl
        {
            Name = name,
            PrivateName = privateName,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    #region GetCSharpParameterName Tests

    [Fact]
    public void GetCSharpParameterName_PrefersPrivateName()
    {
        var arg = MakeArg("arg0", "count");
        Assert.Equal("count", NameProvider.GetCSharpParameterName(arg));
    }

    [Fact]
    public void GetCSharpParameterName_FallsBackToArgName()
    {
        var arg = MakeArg("arg0");
        Assert.Equal("arg0", NameProvider.GetCSharpParameterName(arg));
    }

    [Fact]
    public void GetCSharpParameterName_KeywordEscapedNamePreserved()
    {
        // _for stays as _for (not @for) because derived names like _forHandle must be valid
        var arg = MakeArg("_for");
        Assert.Equal("_for", NameProvider.GetCSharpParameterName(arg));
    }

    [Fact]
    public void GetCSharpParameterName_PrivateNameKeywordSanitized()
    {
        // If the internal Swift name is a C# keyword, prefix with _
        var arg = MakeArg("arg0", "class");
        Assert.Equal("_class", NameProvider.GetCSharpParameterName(arg));
    }

    [Fact]
    public void GetCSharpParameterName_RegularNamePassthrough()
    {
        var arg = MakeArg("name");
        Assert.Equal("name", NameProvider.GetCSharpParameterName(arg));
    }

    [Fact]
    public void GetCSharpParameterName_PrivateNameStartingWithDigit()
    {
        var arg = MakeArg("arg0", "3dPoint");
        Assert.Equal("_3dPoint", NameProvider.GetCSharpParameterName(arg));
    }

    [Fact]
    public void GetCSharpParameterName_PrivateNameOverridesKeywordEscaped()
    {
        // Even when Name is keyword-escaped, PrivateName takes precedence
        var arg = MakeArg("_for", "range");
        Assert.Equal("range", NameProvider.GetCSharpParameterName(arg));
    }

    #endregion

    #region IsGeneratedArgName Tests

    [Fact]
    public void IsGeneratedArgName_Arg0_ReturnsTrue()
    {
        Assert.True(NameProvider.IsGeneratedArgName("arg0"));
    }

    [Fact]
    public void IsGeneratedArgName_Arg12_ReturnsTrue()
    {
        Assert.True(NameProvider.IsGeneratedArgName("arg12"));
    }

    [Fact]
    public void IsGeneratedArgName_ArgOnly_ReturnsFalse()
    {
        Assert.False(NameProvider.IsGeneratedArgName("arg"));
    }

    [Fact]
    public void IsGeneratedArgName_RegularName_ReturnsFalse()
    {
        Assert.False(NameProvider.IsGeneratedArgName("value"));
    }

    [Fact]
    public void IsGeneratedArgName_Null_ReturnsFalse()
    {
        Assert.False(NameProvider.IsGeneratedArgName(null));
    }

    [Fact]
    public void IsGeneratedArgName_Empty_ReturnsFalse()
    {
        Assert.False(NameProvider.IsGeneratedArgName(""));
    }

    #endregion

    #region StripCSharpKeywordPrefix Tests

    [Fact]
    public void StripCSharpKeywordPrefix_ForKeyword()
    {
        Assert.Equal("for", NameProvider.StripCSharpKeywordPrefix("_for"));
    }

    [Fact]
    public void StripCSharpKeywordPrefix_UsingKeyword()
    {
        Assert.Equal("using", NameProvider.StripCSharpKeywordPrefix("_using"));
    }

    [Fact]
    public void StripCSharpKeywordPrefix_NonKeywordWithUnderscore()
    {
        // _data is NOT a keyword-escaped name since "data" is not a C# keyword
        Assert.Equal("_data", NameProvider.StripCSharpKeywordPrefix("_data"));
    }

    [Fact]
    public void StripCSharpKeywordPrefix_NoPrefix()
    {
        Assert.Equal("name", NameProvider.StripCSharpKeywordPrefix("name"));
    }

    #endregion
}
