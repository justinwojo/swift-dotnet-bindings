// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for DefaultParameterOverloadEmitter.IsDebugParameter() —
/// detection of Swift compiler-injected #file, #line, #column, #function params.
/// </summary>
public class DebugParameterTests
{
    private static ArgumentDecl MakeArg(string name, string swiftTypeName, bool hasDefault = true) => new()
    {
        SwiftTypeSpec = new NamedTypeSpec(swiftTypeName),
        Name = name,
        PrivateName = name,
        IsInOut = false,
        IsGeneric = false,
        HasDefaultArg = hasDefault,
        ModuleDecl = null!,
        ParentDecl = null!
    };

    #region Positive matches (should be detected as debug params)

    [Fact]
    public void File_StaticString_WithDefault_IsDebugParam()
    {
        var arg = MakeArg("file", "Swift.StaticString");
        Assert.True(DefaultParameterOverloadEmitter.IsDebugParameter(arg));
    }

    [Fact]
    public void UnderscoreFile_StaticString_WithDefault_IsDebugParam()
    {
        var arg = MakeArg("_file", "Swift.StaticString");
        Assert.True(DefaultParameterOverloadEmitter.IsDebugParameter(arg));
    }

    [Fact]
    public void FilePath_StaticString_WithDefault_IsDebugParam()
    {
        var arg = MakeArg("filePath", "Swift.StaticString");
        Assert.True(DefaultParameterOverloadEmitter.IsDebugParameter(arg));
    }

    [Fact]
    public void Line_UInt_WithDefault_IsDebugParam()
    {
        var arg = MakeArg("line", "Swift.UInt");
        Assert.True(DefaultParameterOverloadEmitter.IsDebugParameter(arg));
    }

    [Fact]
    public void UnderscoreLine_UInt_WithDefault_IsDebugParam()
    {
        var arg = MakeArg("_line", "Swift.UInt");
        Assert.True(DefaultParameterOverloadEmitter.IsDebugParameter(arg));
    }

    [Fact]
    public void Column_UInt_WithDefault_IsDebugParam()
    {
        var arg = MakeArg("column", "Swift.UInt");
        Assert.True(DefaultParameterOverloadEmitter.IsDebugParameter(arg));
    }

    [Fact]
    public void Function_StaticString_WithDefault_IsDebugParam()
    {
        var arg = MakeArg("function", "Swift.StaticString");
        Assert.True(DefaultParameterOverloadEmitter.IsDebugParameter(arg));
    }

    #endregion

    #region Negative matches (should NOT be detected as debug params)

    [Fact]
    public void File_String_IsNotDebugParam()
    {
        // Real file-path params use String, not StaticString
        var arg = MakeArg("file", "Swift.String");
        Assert.False(DefaultParameterOverloadEmitter.IsDebugParameter(arg));
    }

    [Fact]
    public void Line_Int_IsNotDebugParam()
    {
        // #line produces UInt, not Int — real line params use Int
        var arg = MakeArg("line", "Swift.Int");
        Assert.False(DefaultParameterOverloadEmitter.IsDebugParameter(arg));
    }

    [Fact]
    public void File_StaticString_NoDefault_IsNotDebugParam()
    {
        // Debug params always have HasDefaultArg=true
        var arg = MakeArg("file", "Swift.StaticString", hasDefault: false);
        Assert.False(DefaultParameterOverloadEmitter.IsDebugParameter(arg));
    }

    [Fact]
    public void RandomName_StaticString_IsNotDebugParam()
    {
        // Only specific names match
        var arg = MakeArg("label", "Swift.StaticString");
        Assert.False(DefaultParameterOverloadEmitter.IsDebugParameter(arg));
    }

    [Fact]
    public void Function_String_IsNotDebugParam()
    {
        // #function produces StaticString, not String
        var arg = MakeArg("function", "Swift.String");
        Assert.False(DefaultParameterOverloadEmitter.IsDebugParameter(arg));
    }

    #endregion
}
