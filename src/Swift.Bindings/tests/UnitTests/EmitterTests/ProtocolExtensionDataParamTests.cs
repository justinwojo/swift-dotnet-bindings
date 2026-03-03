// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static BindingsGeneration.Tests.ProtocolExtensionTestHelpers;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for Foundation.Data parameter support in protocol extension methods.
/// Verifies: Data param method injected, wrapper declares "Foundation.Data" (not UnsafeMutableRawPointer),
/// no Unmanaged conversion.
/// </summary>
public class ProtocolExtensionDataParamTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    // ─── Foundation.Data param injected ─────────────────────────────────

    [Fact]
    public void DataParam_MethodInjected()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("upload", "public func upload(_ payload: Foundation.Data)"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(conformingType.Methods);
        Assert.Equal("upload", conformingType.Methods[0].Name);
    }

    // ─── Wrapper declares Foundation.Data (not UnsafeMutableRawPointer) ─

    [Fact]
    public void DataParam_SwiftWrapper_DeclaresFoundationData()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("upload", "public func upload(_ payload: Foundation.Data)"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        Assert.Contains("Foundation.Data", wrapperLines);
        // Should NOT use UnsafeMutableRawPointer for the Data param
        Assert.DoesNotContain("UnsafeMutableRawPointer", wrapperLines.Replace("_ self_: UnsafeMutableRawPointer", ""));
    }

    // ─── No Unmanaged conversion for Data ──────────────────────────────

    [Fact]
    public void DataParam_NoUnmanagedConversion()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("upload", "public func upload(_ payload: Foundation.Data)"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        // No Unmanaged conversion for the Data param (only self uses Unmanaged)
        Assert.DoesNotContain("Unmanaged<Data>", wrapperLines);
        Assert.Contains("instance.upload(data)", wrapperLines);
    }

    // ─── Combo: Data + throws ──────────────────────────────────────────

    [Fact]
    public void DataParam_WithThrows_Injected()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("upload", "public func upload(_ payload: Foundation.Data) throws"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(conformingType.Methods);
        Assert.True(conformingType.Methods[0].Throws);
        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        Assert.Contains("Foundation.Data", wrapperLines);
        Assert.Contains(" throws {", wrapperLines);
    }
}
