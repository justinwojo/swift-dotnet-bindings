// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static BindingsGeneration.Tests.ProtocolExtensionTestHelpers;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for Swift.Array parameter support in protocol extension methods.
/// Verifies: Int/class array params injected, wrapper uses unsafeBitCast,
/// Optional&lt;Array&gt; blocked, Optional&lt;Class&gt; still accepted.
/// </summary>
public class ProtocolExtensionArrayParamTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    // ─── Int array param injected ──────────────────────────────────────

    [Fact]
    public void ArrayParam_IntArray_MethodInjected()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("setItems", "public func setItems(_ items: Swift.Array<Swift.Int>)"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(conformingType.Methods);
        Assert.Equal("setItems", conformingType.Methods[0].Name);
    }

    // ─── Class array param injected ────────────────────────────────────

    [Fact]
    public void ArrayParam_ClassArray_MethodInjected()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("setItems", "public func setItems(_ items: Swift.Array<TestModule.MyClass>)"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(conformingType.Methods);
    }

    // ─── Wrapper uses unsafeBitCast ────────────────────────────────────

    [Fact]
    public void ArrayParam_SwiftWrapper_UsesUnsafeBitCast()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("setItems", "public func setItems(_ items: Swift.Array<Swift.Int>)"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        // Array param should be UnsafeMutableRawPointer in the wrapper signature
        Assert.Contains("UnsafeMutableRawPointer", wrapperLines);
        // Conversion via unsafeBitCast to [Int].self
        Assert.Contains("unsafeBitCast(", wrapperLines);
        Assert.Contains("[Int].self", wrapperLines);
    }

    // ─── TypeSpec preserved in MethodDecl ──────────────────────────────

    [Fact]
    public void ArrayParam_SyntheticMethodDecl_PreservesTypeSpec()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("setItems", "public func setItems(_ items: Swift.Array<Swift.Int>)"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(conformingType.Methods);
        // CSSignature[1] is the first param (CSSignature[0] is return type)
        var paramTypeSpec = conformingType.Methods[0].CSSignature[1].SwiftTypeSpec;
        Assert.IsType<NamedTypeSpec>(paramTypeSpec);
        var namedParam = (NamedTypeSpec)paramTypeSpec;
        Assert.Equal("Swift.Array", namedParam.Name);
        Assert.Single(namedParam.GenericParameters);
    }

    // ─── Optional<Array<Int>> blocked ──────────────────────────────────

    [Fact]
    public void OptionalArray_MethodNotInjected()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("setItems", "public func setItems(_ items: Swift.Optional<Swift.Array<Swift.Int>>)"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Empty(conformingType.Methods);
    }

    // ─── Optional<Class> still accepted (regression check) ─────────────

    [Fact]
    public void OptionalClass_StillAccepted()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("setTarget", "public func setTarget(_ target: Swift.Optional<TestModule.MyClass>)"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(conformingType.Methods);
    }

    // ─── Array + throws combo ──────────────────────────────────────────

    [Fact]
    public void ArrayParam_WithThrows_Injected()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("setItems", "public func setItems(_ items: Swift.Array<Swift.Int>) throws"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(conformingType.Methods);
        Assert.True(conformingType.Methods[0].Throws);
    }
}
