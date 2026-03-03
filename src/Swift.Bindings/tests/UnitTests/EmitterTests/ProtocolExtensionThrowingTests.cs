// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static BindingsGeneration.Tests.ProtocolExtensionTestHelpers;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for throwing method support in protocol extension methods.
/// Verifies: untyped "throws" injected with try/throws in wrapper and Throws=true on MethodDecl,
/// "rethrows" injected but Throws=false, typed "throws(ErrorType)" stays blocked.
/// </summary>
public class ProtocolExtensionThrowingTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    // ─── Signature detection helpers ───────────────────────────────────

    [Fact]
    public void IsThrowingSignature_UntypedThrows_ReturnsTrue()
    {
        Assert.True(ProtocolExtensionEmitter.IsThrowingSignature(
            "public func save() throws"));
    }

    [Fact]
    public void IsThrowingSignature_ThrowsWithReturn_ReturnsTrue()
    {
        Assert.True(ProtocolExtensionEmitter.IsThrowingSignature(
            "public func load() throws -> Swift.Int"));
    }

    [Fact]
    public void IsThrowingSignature_Rethrows_ReturnsFalse()
    {
        Assert.False(ProtocolExtensionEmitter.IsThrowingSignature(
            "public func process(_ block: () throws -> Swift.Void) rethrows"));
    }

    [Fact]
    public void IsThrowingSignature_TypedThrows_ReturnsFalse()
    {
        Assert.False(ProtocolExtensionEmitter.IsThrowingSignature(
            "public func parse() throws(ParseError)"));
    }

    [Fact]
    public void IsThrowingSignature_NonThrowing_ReturnsFalse()
    {
        Assert.False(ProtocolExtensionEmitter.IsThrowingSignature(
            "public func count() -> Swift.Int"));
    }

    [Fact]
    public void IsTypedThrowsSignature_TypedThrows_ReturnsTrue()
    {
        Assert.True(ProtocolExtensionEmitter.IsTypedThrowsSignature(
            "public func parse() throws(ParseError) -> Swift.Int"));
    }

    [Fact]
    public void IsTypedThrowsSignature_UntypedThrows_ReturnsFalse()
    {
        Assert.False(ProtocolExtensionEmitter.IsTypedThrowsSignature(
            "public func save() throws"));
    }

    [Fact]
    public void IsRethrowsSignature_Rethrows_ReturnsTrue()
    {
        Assert.True(ProtocolExtensionEmitter.IsRethrowsSignature(
            "public func process(_ block: () throws -> Swift.Void) rethrows"));
    }

    [Fact]
    public void IsRethrowsSignature_Throws_ReturnsFalse()
    {
        Assert.False(ProtocolExtensionEmitter.IsRethrowsSignature(
            "public func save() throws"));
    }

    // ─── End-to-end: throwing void method injected ─────────────────────

    [Fact]
    public void Throwing_VoidMethod_Injected_WithThrowsAndTry()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("save", "public func save() throws"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(conformingType.Methods);
        Assert.Equal("save", conformingType.Methods[0].Name);
        Assert.True(conformingType.Methods[0].Throws, "MethodDecl.Throws should be true");

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        Assert.Contains(" throws {", wrapperLines);
        Assert.Contains("try instance.save()", wrapperLines);
    }

    // ─── Throwing method with return value ─────────────────────────────

    [Fact]
    public void Throwing_ReturningMethod_Injected_WithThrowsAndTry()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("load", "public func load() throws -> Swift.Int"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(conformingType.Methods);
        Assert.True(conformingType.Methods[0].Throws);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        Assert.Contains(" throws -> Int", wrapperLines);
        Assert.Contains("return try instance.load()", wrapperLines);
    }

    // ─── Typed throws stays blocked ────────────────────────────────────

    [Fact]
    public void TypedThrows_MethodNotInjected()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("parse", "public func parse() throws(TestModule.ParseError) -> Swift.Int"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Empty(conformingType.Methods);
    }

    // ─── Rethrows injected but Throws=false ────────────────────────────

    [Fact]
    public void Rethrows_Injected_ThrowsFalse()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        // Rethrows with closure-only param — the closure gate should also pass
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("process",
                "public func process(_ block: @escaping () throws -> Swift.Void) rethrows"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(conformingType.Methods);
        Assert.False(conformingType.Methods[0].Throws, "Rethrows methods should have Throws=false");
    }

    // ─── Throws + class param combo ────────────────────────────────────

    [Fact]
    public void Throwing_WithClassParam_Injected()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("setTarget",
                "public func setTarget(_ target: TestModule.MyClass) throws"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(conformingType.Methods);
        Assert.True(conformingType.Methods[0].Throws);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        Assert.Contains(" throws {", wrapperLines);
        Assert.Contains("try instance.setTarget(", wrapperLines);
    }

    // ─── Throws + primitive param combo ────────────────────────────────

    [Fact]
    public void Throwing_WithPrimitiveParam_Injected()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("setIndex",
                "public func setIndex(_ index: Swift.Int) throws"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(conformingType.Methods);
        Assert.True(conformingType.Methods[0].Throws);
    }
}
