// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static BindingsGeneration.Tests.ProtocolExtensionTestHelpers;

namespace BindingsGeneration.Tests;

/// <summary>
/// An <c>Optional</c> closure parameter on a protocol-extension default implementation has no
/// carrier on this path, so the member must not be injected onto the conforming type.
///
/// A closure occupies TWO C ABI words wherever it crosses the boundary — a function pointer and a
/// context — and the C# P/Invoke synthesized for an extension member renders exactly that (a
/// by-value two-word <c>SwiftClosureData</c>), because the P/Invoke's closure classifier looks
/// through <c>Optional</c>. This emitter's parameter renderer does not: it has a closure arm only
/// for a BARE closure (which is handed to the separate protocol-extension closure bridge), so an
/// <c>Optional</c> closure fell through to the ordinary one-word <c>UnsafeRawPointer</c> +
/// <c>assumingMemoryBound(to: Optional&lt;…&gt;)</c> treatment. Both sides compiled; at runtime the
/// argument after the callback — and the receiver behind it — shifted by a register, and the Swift
/// body dereferenced a function pointer as if it were memory.
///
/// The positive controls below keep the gate narrow: it rejects an Optional whose payload is a
/// closure, not Optionals in general and not closures in general.
/// </summary>
public class ProtocolExtensionOptionalClosureParamTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    [Fact]
    public void OptionalClosureParam_MethodNotInjected()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("notify",
                "public func notify(_ callback: Swift.Optional<(TestModule.MyClass) -> ()>, trailing: Swift.Int32)"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Empty(conformingType.Methods);
    }

    [Fact]
    public void OptionalClosureParam_NoSwiftWrapperEmitted()
    {
        // The wrapper plane matters independently of the injected member: a wrapper rendered here
        // would claim the @_cdecl symbol the (absent) C# side would never call with a matching
        // signature.
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("notify",
                "public func notify(_ callback: Swift.Optional<(TestModule.MyClass) -> ()>, trailing: Swift.Int32)"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        Assert.DoesNotContain("notify", wrapperLines);
    }

    [Fact]
    public void OptionalClassParam_StillInjected()
    {
        // Positive control: the gate keys on the closure payload, not on Optional-ness.
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("setTarget", "public func setTarget(_ target: Swift.Optional<TestModule.MyClass>)"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(conformingType.Methods);
    }

    [Fact]
    public void BareClosureParam_StillInjected()
    {
        // Positive control: the bare closure keeps its existing bridge.
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("run", "public func run(_ callback: @escaping (TestModule.MyClass) -> ())"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(conformingType.Methods);
    }
}
