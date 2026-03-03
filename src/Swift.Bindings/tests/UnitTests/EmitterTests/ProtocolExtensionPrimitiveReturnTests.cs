// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static BindingsGeneration.Tests.ProtocolExtensionTestHelpers;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for primitive return type support in protocol extension methods.
/// Verifies that Int, Bool, Float-returning extension methods pass the return gate
/// and that the Swift wrapper renders the return type directly (not UnsafeMutableRawPointer).
/// </summary>
public class ProtocolExtensionPrimitiveReturnTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    // ─── Int return injected ───────────────────────────────────────────

    [Fact]
    public void PrimitiveReturn_Int_MethodInjected()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("count", "public func count() -> Swift.Int"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(conformingType.Methods);
        Assert.Equal("count", conformingType.Methods[0].Name);
    }

    // ─── Bool return injected ──────────────────────────────────────────

    [Fact]
    public void PrimitiveReturn_Bool_MethodInjected()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("isValid", "public func isValid() -> Swift.Bool"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(conformingType.Methods);
        Assert.Equal("isValid", conformingType.Methods[0].Name);
    }

    // ─── Swift wrapper renders primitive directly ──────────────────────

    [Fact]
    public void PrimitiveReturn_SwiftWrapper_RendersDirectReturn()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("count", "public func count() -> Swift.Int"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        // Primitive return: should NOT use UnsafeMutableRawPointer or Unmanaged
        Assert.DoesNotContain("-> UnsafeMutableRawPointer", wrapperLines);
        Assert.DoesNotContain("Unmanaged.passRetained", wrapperLines);
        // Should use direct return with Int type
        Assert.Contains("-> Int", wrapperLines);
        Assert.Contains("return instance.count()", wrapperLines);
    }
}
