// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the B2 bridge dispatch table in MethodHandler.
/// Validates structural invariants, result semantics, and constructor WasEmitted fixes.
/// </summary>
public class BridgeDispatchTableTests
{
    // ─── Table Structure ──────────────────────────────────────────────

    [Fact]
    public void BridgeEmitters_HasExactly10Entries()
    {
        Assert.Equal(10, MethodHandler.BridgeEmitters.Count);
    }

    [Fact]
    public void BridgeEmitters_ExistentialBypassIsFirst()
    {
        Assert.IsType<ExistentialBypassBridgeAdapter>(MethodHandler.BridgeEmitters[0]);
    }

    [Fact]
    public void BridgeEmitters_OptionalClosureBypassIsLast()
    {
        Assert.IsType<OptionalClosureBypassAdapter>(MethodHandler.BridgeEmitters[MethodHandler.BridgeEmitters.Count - 1]);
    }

    [Fact]
    public void BridgeEmitters_ProtocolExtensionBeforeMethodClosure()
    {
        int protocolExtIdx = -1;
        int methodClosureIdx = -1;
        for (int i = 0; i < MethodHandler.BridgeEmitters.Count; i++)
        {
            if (MethodHandler.BridgeEmitters[i] is ProtocolExtensionClosureBridgeAdapter)
                protocolExtIdx = i;
            if (MethodHandler.BridgeEmitters[i] is MethodClosureBridgeAdapter)
                methodClosureIdx = i;
        }

        Assert.NotEqual(-1, protocolExtIdx);
        Assert.NotEqual(-1, methodClosureIdx);
        Assert.True(protocolExtIdx < methodClosureIdx,
            $"ProtocolExtensionClosureBridgeAdapter (index {protocolExtIdx}) must come before " +
            $"MethodClosureBridgeAdapter (index {methodClosureIdx})");
    }

    [Fact]
    public void BridgeEmitters_AsyncMethodGenericBeforeMethodGeneric()
    {
        // Both adapters match the same eligibility shape (method-own generic +
        // class-bound protocol). Sync emitter is gated by !IsAsync && !Throws,
        // async emitter requires IsAsync — but the table iterates in order,
        // so the async adapter must come first or the sync adapter would
        // claim the method first when a Throws method is sync (it isn't, but
        // the ordering invariant guards against future eligibility expansion).
        int asyncIdx = -1;
        int syncIdx = -1;
        for (int i = 0; i < MethodHandler.BridgeEmitters.Count; i++)
        {
            if (MethodHandler.BridgeEmitters[i] is AsyncMethodGenericBridgeAdapter)
                asyncIdx = i;
            if (MethodHandler.BridgeEmitters[i] is MethodGenericBridgeAdapter)
                syncIdx = i;
        }

        Assert.NotEqual(-1, asyncIdx);
        Assert.NotEqual(-1, syncIdx);
        Assert.True(asyncIdx < syncIdx,
            $"AsyncMethodGenericBridgeAdapter (index {asyncIdx}) must come before " +
            $"MethodGenericBridgeAdapter (index {syncIdx})");
    }

    [Fact]
    public void BridgeEmitters_ContainsAllExpectedAdapters()
    {
        var types = MethodHandler.BridgeEmitters.Select(b => b.GetType()).ToList();

        Assert.Contains(typeof(ExistentialBypassBridgeAdapter), types);
        Assert.Contains(typeof(MetatypeArrayBridgeAdapter), types);
        Assert.Contains(typeof(ArraySliceBridgeAdapter), types);
        Assert.Contains(typeof(GenericClosureBridgeAdapter), types);
        Assert.Contains(typeof(ProtocolExtensionClosureBridgeAdapter), types);
        Assert.Contains(typeof(MethodClosureBridgeAdapter), types);
        Assert.Contains(typeof(NestedClosureBridgeAdapter), types);
        Assert.Contains(typeof(MethodGenericBridgeAdapter), types);
        Assert.Contains(typeof(AsyncMethodGenericBridgeAdapter), types);
        Assert.Contains(typeof(OptionalClosureBypassAdapter), types);
    }

    [Fact]
    public void BridgeEmitters_PropertyTypeIsIReadOnlyList()
    {
        // Verify the property's compile-time type is IReadOnlyList<IMethodBridgeEmitter>.
        // This prevents callers from mutating elements without explicit unsafe casting.
        // We check via reflection on the property itself (not the runtime value, which
        // is an array and would satisfy IList<T> at runtime).
        var prop = typeof(MethodHandler).GetProperty(
            nameof(MethodHandler.BridgeEmitters),
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(prop);
        Assert.Equal(typeof(IReadOnlyList<IMethodBridgeEmitter>), prop!.PropertyType);
    }

    // ─── BridgeEmitResult Semantics ───────────────────────────────────

    [Fact]
    public void BridgeEmitResult_DefaultWasEmittedIsTrue()
    {
        var result = new BridgeEmitResult("TestBridge", "Test description");
        Assert.True(result.WasEmitted);
    }

    [Fact]
    public void BridgeEmitResult_CreateSkipped_WasEmittedIsFalse()
    {
        var result = BridgeEmitResult.CreateSkipped();
        Assert.False(result.WasEmitted);
    }

    [Fact]
    public void BridgeEmitResult_CreateSkipped_HasSentinelName()
    {
        var result = BridgeEmitResult.CreateSkipped();
        Assert.Equal("_Skipped", result.BridgeName);
    }

    [Fact]
    public void BridgeEmitResult_ExplicitWasEmittedFalse()
    {
        var result = new BridgeEmitResult("TestBridge", "desc", WasEmitted: false);
        Assert.False(result.WasEmitted);
    }

    // ─── Adapter Null Returns (Not Eligible) ──────────────────────────

    [Fact]
    public void ExistentialBypassAdapter_ReturnsNull_WhenNoExistentialArg()
    {
        var context = CreateMinimalContext(hasExistentialArg: false);
        var adapter = new ExistentialBypassBridgeAdapter();
        Assert.Null(adapter.TryEmit(context));
    }

    // ─── Constructor WasEmitted Bug Fixes ─────────────────────────────

    [Fact]
    public void ConstructorEmit_ExistentialBypass_SetsWasEmitted()
    {
        // B2 bug fix: ExistentialBypass success in ConstructorHandler must set WasEmitted.
        // Creates a frozen struct constructor with Optional<any UnknownProtocol> (HasDefaultArg=true)
        // so TryEmitConstructorBypass succeeds (unsupported existential → omitted with default).
        var typeDatabase = CreateTypeDatabaseWithOptional();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Widget", moduleDecl);

        // Register the parent type
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: parentDecl.SwiftTypeName, record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Widget"),
                SwiftTypeName = parentDecl.SwiftTypeName,
                MetadataAccessor = "$s10TestModule6WidgetVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            })
        });

        // Optional<any UnknownProtocol> with HasDefaultArg — triggers existential bypass
        var optionalExistentialSpec = new NamedTypeSpec("Swift.Optional");
        var existentialInner = new NamedTypeSpec("TestModule.UnknownProtocol") { IsAny = true };
        optionalExistentialSpec.GenericParameters.Add(existentialInner);

        var existentialArg = CreateArgument("handler", optionalExistentialSpec, moduleDecl);
        existentialArg.HasDefaultArg = true;

        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl> { existentialArg });

        Assert.False(constructor.WasEmitted);

        EmitConstructor(constructor, typeDatabase);

        Assert.True(constructor.WasEmitted,
            "ExistentialBypass success in ConstructorHandler must set WasEmitted = true");
    }

    [Fact]
    public void ConstructorEmit_OptionalClosureBypass_SetsWasEmitted()
    {
        // B2 bug fix: OptionalClosureBypass success in ConstructorHandler must set WasEmitted.
        // Creates a frozen struct constructor with Optional<UnsupportedClosure> (HasDefaultArg=true)
        // so the optional closure bypass path succeeds.
        var typeDatabase = CreateTypeDatabaseWithOptional();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Widget", moduleDecl);

        // Register the parent type
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: parentDecl.SwiftTypeName, record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Widget"),
                SwiftTypeName = parentDecl.SwiftTypeName,
                MetadataAccessor = "$s10TestModule6WidgetVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            })
        });

        // Optional<(UnknownModule.SomeError) -> ()> with HasDefaultArg —
        // closure has unsupported param type, triggers optional closure bypass
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec>
            {
                new NamedTypeSpec("UnknownModule.SomeError")
            }),
            TupleTypeSpec.Empty);
        var optionalClosure = new NamedTypeSpec("Swift.Optional");
        optionalClosure.GenericParameters.Add(closureType);

        var closureArg = CreateArgument("errorCallback", optionalClosure, moduleDecl);
        closureArg.HasDefaultArg = true;

        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl> { closureArg });

        Assert.False(constructor.WasEmitted);

        EmitConstructor(constructor, typeDatabase);

        Assert.True(constructor.WasEmitted,
            "OptionalClosureBypass success in ConstructorHandler must set WasEmitted = true");
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static BridgeEmitterContext CreateMinimalContext(
        bool hasExistentialArg = false,
        string? firstExistentialType = null)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
        var parentDecl = new ClassDecl
        {
            Name = "TestClass",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.TestClass"),
            MangledName = "$s10TestModule9TestClassCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        var methodDecl = new MethodDecl
        {
            Name = "testMethod",
            MangledName = "$s10TestModule9TestClassC10testMethodSiyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        var typeDatabase = new TypeDatabase();
        var methodEnv = new MethodEnvironment(methodDecl, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftWriter = new SwiftWriter(new StringWriter());
        var logger = new NullLogger<MethodHandler>();

        return new BridgeEmitterContext(
            csWriter, swiftWriter, methodEnv, logger, null,
            hasExistentialArg, firstExistentialType);
    }

    private static (string csOutput, string swiftOutput) EmitConstructor(
        MethodDecl methodDecl, TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new ConstructorHandler(
            new NullLogger<ConstructorHandler>(),
            new HashSet<string>());
        var env = new MethodEnvironment(methodDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        // Per-test ModuleEmissionContext so the structural-claim guard in ExistentialBypassEmitter
        // does not see prior tests' wrapper-symbol claims via the shared Default singleton.
        var context = TypeHandlerContext.Empty with { EmissionContext = new ModuleEmissionContext() };
        handler.Emit(csWriter, swiftWriter, env, conductor, context);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    private static ModuleDecl CreateModuleDecl(string name)
    {
        return new ModuleDecl
        {
            Name = name,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static StructDecl CreateFrozenStructDecl(string name, ModuleDecl moduleDecl)
    {
        var structDecl = new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}VMa"
        };
        moduleDecl.Types.Add(structDecl);
        return structDecl;
    }

    private static MethodDecl CreateConstructorDecl(
        string name,
        StructDecl parentDecl,
        ModuleDecl moduleDecl,
        bool throws = false,
        List<ArgumentDecl>? parameters = null)
    {
        var signature = new List<ArgumentDecl>
        {
            CreateArgument(string.Empty, new NamedTypeSpec($"{moduleDecl.Name}.{parentDecl.Name}"), moduleDecl)
        };
        if (parameters != null)
            signature.AddRange(parameters);

        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{parentDecl.Name.Length}{parentDecl.Name}V{name}yACyF",
            MethodType = MethodType.Static,
            IsConstructor = true,
            CSSignature = signature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = throws,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);
        return method;
    }

    private static ArgumentDecl CreateArgument(string name, TypeSpec typeSpec, ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = name,
            PrivateName = name,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    private static TypeDatabase CreateTypeDatabaseWithOptional()
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "$sSqMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
    }
}
