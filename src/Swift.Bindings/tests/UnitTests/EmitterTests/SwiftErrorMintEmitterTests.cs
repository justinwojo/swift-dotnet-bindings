// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the per-module <c>SBW_CreateError_{module}</c> error-mint helper registration
/// policy in <see cref="SwiftErrorMintEmitter"/>, plus the handler-layer wiring that drives it.
/// </summary>
/// <remarks>
/// Regression coverage for the optional/native throwing-closure CS0103 class:
/// the C# side mints a Swift error via <c>SBW_CreateError_{module}</c> for EVERY synchronous
/// throwing-closure parameter, but the Swift helper was historically registered only by wrapper
/// paths that funnel through <see cref="ClosureEmitter.GetSwiftClosureAdapterCode"/>. Native
/// pass-through paths (optional-pointer/_optbuf wrapper, default-parameter shims, the non-optional
/// closure property setter) skipped that funnel, so the C# P/Invoke referenced an unregistered
/// wrapper symbol, the contract gate rejected it, and the co-gater stripped the callback method —
/// stranding its <c>s_&lt;cb&gt; = &amp;&lt;cb&gt;</c> field → CS0103. The fix registers the helper
/// at the handler dispatch layer (method/constructor/property), above the leaf emitters, so every
/// path is covered uniformly. These tests pin the policy (which decl shapes need the helper) and
/// the wiring (that MethodHandler.Emit actually fires it before the contract check).
/// </remarks>
public class SwiftErrorMintEmitterTests
{
    private const string ModuleName = "TestModule";
    private const string Symbol = "SBW_CreateError_TestModule";
    private static readonly string SwiftHelperMarker = $"@_cdecl(\"{Symbol}\")";

    // ---------------------------------------------------------------------
    // EmitForMethodIfNeeded — policy
    // ---------------------------------------------------------------------

    [Fact]
    public void Method_ThrowingVoidClosureParam_EmitsAndRegistersHelper()
    {
        // (Int32) throws -> Void — the canonical throwing-Void closure parameter.
        var env = MethodEnvWithClosureParam(ThrowingVoidClosure());
        var ctx = new ModuleEmissionContext();
        var (swift, _) = Run(w => SwiftErrorMintEmitter.EmitForMethodIfNeeded(w, env, ctx));

        Assert.Contains(SwiftHelperMarker, swift);
        Assert.True(ctx.IsWrapperSymbolRegistered(Symbol));
        Assert.True(ctx.SwiftErrorMintHelperEmitted);
    }

    [Fact]
    public void Method_OptionalThrowingClosureParam_EmitsAndRegistersHelper()
    {
        // Optional<(Int32) throws -> Void> = nil — a "large optional" throwing closure
        // forwarded to Swift natively with no adapter funnel.
        var env = MethodEnvWithClosureParam(OptionalOf(ThrowingVoidClosure()));
        var ctx = new ModuleEmissionContext();
        var (swift, _) = Run(w => SwiftErrorMintEmitter.EmitForMethodIfNeeded(w, env, ctx));

        Assert.Contains(SwiftHelperMarker, swift);
        Assert.True(ctx.IsWrapperSymbolRegistered(Symbol));
    }

    [Fact]
    public void Method_NonThrowingClosureParam_DoesNotEmitHelper()
    {
        var env = MethodEnvWithClosureParam(NonThrowingVoidClosure());
        var ctx = new ModuleEmissionContext();
        var (swift, _) = Run(w => SwiftErrorMintEmitter.EmitForMethodIfNeeded(w, env, ctx));

        Assert.DoesNotContain(Symbol, swift);
        Assert.False(ctx.IsWrapperSymbolRegistered(Symbol));
        Assert.False(ctx.SwiftErrorMintHelperEmitted);
    }

    [Fact]
    public void Method_AsyncThrowingClosureParam_DoesNotEmitHelper()
    {
        // An async-throwing closure propagates errors via its continuation, not SBW_CreateError —
        // the synchronous throwing-closure callback (the only minter) is never emitted for it, so
        // registering the helper here would add a symbol the binding never references.
        var env = MethodEnvWithClosureParam(AsyncThrowingStringClosure());
        var ctx = new ModuleEmissionContext();
        var (swift, _) = Run(w => SwiftErrorMintEmitter.EmitForMethodIfNeeded(w, env, ctx));

        Assert.DoesNotContain(Symbol, swift);
        Assert.False(ctx.IsWrapperSymbolRegistered(Symbol));
        Assert.False(ctx.SwiftErrorMintHelperEmitted);
    }

    [Fact]
    public void Method_NoClosureParam_DoesNotEmitHelper()
    {
        var env = MethodEnvWithClosureParam(new NamedTypeSpec("Swift.Int32"));
        var ctx = new ModuleEmissionContext();
        var (swift, _) = Run(w => SwiftErrorMintEmitter.EmitForMethodIfNeeded(w, env, ctx));

        Assert.DoesNotContain(Symbol, swift);
        Assert.False(ctx.SwiftErrorMintHelperEmitted);
    }

    [Fact]
    public void Method_NullContext_DoesNotThrowOrEmit()
    {
        // Mirrors EmitSwiftHelperIfNeeded's nullable-ctx contract: no ctx → no dedup state and
        // the matching C# P/Invoke contract is not enforced, so emitting would desync the registry.
        var env = MethodEnvWithClosureParam(ThrowingVoidClosure());
        var (swift, _) = Run(w => SwiftErrorMintEmitter.EmitForMethodIfNeeded(w, env, ctx: null));

        Assert.DoesNotContain(Symbol, swift);
    }

    [Fact]
    public void Method_Idempotent_EmitsHelperExactlyOnce_AcrossDecls()
    {
        // Two distinct throwing-closure decls in the same module must produce the helper once.
        var ctx = new ModuleEmissionContext();
        var env1 = MethodEnvWithClosureParam(ThrowingVoidClosure());
        var env2 = MethodEnvWithClosureParam(OptionalOf(ThrowingVoidClosure()));

        var (swift, _) = Run(w =>
        {
            SwiftErrorMintEmitter.EmitForMethodIfNeeded(w, env1, ctx);
            SwiftErrorMintEmitter.EmitForMethodIfNeeded(w, env2, ctx);
        });

        var helperEmissions = Regex.Matches(swift, Regex.Escape(SwiftHelperMarker)).Count;
        Assert.Equal(1, helperEmissions);
    }

    // ---------------------------------------------------------------------
    // EmitForPropertyIfNeeded — policy
    // ---------------------------------------------------------------------

    [Fact]
    public void Property_NonOptionalThrowingClosure_EmitsAndRegistersHelper()
    {
        // Non-optional throwing closure property: var htmlProvider: (T) throws -> String.
        // The non-optional closure-setter branch forwards the closure natively, so the setter
        // callback's SBW_CreateError reference would otherwise go unregistered.
        var property = PropertyWithType(ThrowingStringClosure());
        var ctx = new ModuleEmissionContext();
        var (swift, _) = Run(w => SwiftErrorMintEmitter.EmitForPropertyIfNeeded(w, property, ctx));

        Assert.Contains(SwiftHelperMarker, swift);
        Assert.True(ctx.IsWrapperSymbolRegistered(Symbol));
    }

    [Fact]
    public void Property_OptionalThrowingClosure_EmitsAndRegistersHelper()
    {
        var property = PropertyWithType(OptionalOf(ThrowingVoidClosure()));
        var ctx = new ModuleEmissionContext();
        var (swift, _) = Run(w => SwiftErrorMintEmitter.EmitForPropertyIfNeeded(w, property, ctx));

        Assert.Contains(SwiftHelperMarker, swift);
        Assert.True(ctx.IsWrapperSymbolRegistered(Symbol));
    }

    [Fact]
    public void Property_NonThrowingClosure_DoesNotEmitHelper()
    {
        var property = PropertyWithType(NonThrowingVoidClosure());
        var ctx = new ModuleEmissionContext();
        var (swift, _) = Run(w => SwiftErrorMintEmitter.EmitForPropertyIfNeeded(w, property, ctx));

        Assert.DoesNotContain(Symbol, swift);
        Assert.False(ctx.SwiftErrorMintHelperEmitted);
    }

    [Fact]
    public void Property_OptionalAsyncThrowingClosure_DoesNotEmitHelper()
    {
        // Regression for PropertyHandlerTests.Emit_OptionalAsyncThrowingClosureProperty_SkipsEmission:
        // an Optional<async-throwing closure> property is skipped from emission entirely and
        // propagates errors via its continuation, never via SBW_CreateError — the handler-layer
        // helper must NOT fire for it (else the skipped property leaves a stray @_cdecl helper
        // in the otherwise-empty Swift output).
        var property = PropertyWithType(OptionalOf(AsyncThrowingStringClosure()));
        var ctx = new ModuleEmissionContext();
        var (swift, _) = Run(w => SwiftErrorMintEmitter.EmitForPropertyIfNeeded(w, property, ctx));

        Assert.DoesNotContain(Symbol, swift);
        Assert.False(ctx.SwiftErrorMintHelperEmitted);
    }

    // ---------------------------------------------------------------------
    // MethodHandler.Emit — wiring
    // ---------------------------------------------------------------------

    [Fact]
    public void MethodHandlerEmit_ThrowingClosureParam_RegistersCreateError_BeforeContractCheck()
    {
        // Drives a real method with a throwing-closure parameter through MethodHandler.Emit and
        // asserts the handler-layer guard registered + emitted the Swift helper. This pins the
        // WIRING: deleting the SwiftErrorMintEmitter.EmitForMethodIfNeeded call from
        // MethodHandler.Emit would make this fail even though the policy unit tests still pass.
        // No contract violation may be recorded — the throwing-closure callback's SBW_CreateError
        // reference must be satisfied, not rejected-then-stripped.
        var (ctx, swift, cs) = EmitMethodThroughHandler(ThrowingVoidClosure());

        Assert.True(ctx.IsWrapperSymbolRegistered(Symbol),
            $"Handler guard did not register {Symbol}. Swift:\n{swift}\n\nCS:\n{cs}");
        Assert.Contains(SwiftHelperMarker, swift);
        Assert.Empty(ctx.ContractViolatedEntryPoints);
    }

    // ---------------------------------------------------------------------
    // Builders
    // ---------------------------------------------------------------------

    private static ClosureTypeSpec ThrowingVoidClosure() =>
        Escaping(new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int32") }), TupleTypeSpec.Empty) { Throws = true });

    private static ClosureTypeSpec NonThrowingVoidClosure() =>
        Escaping(new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int32") }), TupleTypeSpec.Empty) { Throws = false });

    private static ClosureTypeSpec ThrowingStringClosure() =>
        Escaping(new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int32") }),
            new NamedTypeSpec("Swift.String")) { Throws = true });

    private static ClosureTypeSpec AsyncThrowingStringClosure() =>
        Escaping(new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int32") }),
            new NamedTypeSpec("Swift.String")) { Throws = true, IsAsync = true });

    private static ClosureTypeSpec Escaping(ClosureTypeSpec closure)
    {
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));
        return closure;
    }

    private static NamedTypeSpec OptionalOf(TypeSpec inner)
    {
        var optional = new NamedTypeSpec("Swift.Optional");
        optional.GenericParameters.Add(inner);
        return optional;
    }

    private static void Run(System.Action<SwiftWriter> body, out string swift)
    {
        var sw = new StringWriter();
        body(new SwiftWriter(sw));
        swift = sw.ToString();
    }

    private static (string swift, string cs) Run(System.Action<SwiftWriter> body)
    {
        Run(body, out var swift);
        return (swift, string.Empty);
    }

    private static (ModuleDecl module, TypeDatabase typeDb, StructDecl parent) Environment()
    {
        var typeDb = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(SwiftTypeName.FromModuleQualifiedName("Swift.Int32"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int32"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
            MetadataAccessor = "$ss5Int32VMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        swiftModule.RegisterType(SwiftTypeName.FromModuleQualifiedName("Swift.String"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftString"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            MetadataAccessor = "$sSSMa",
            Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Struct
        });
        typeDb.AddModuleDatabase(swiftModule);

        var module = new ModuleDecl
        {
            Name = ModuleName,
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
        var parent = new StructDecl
        {
            Name = "Holder",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Holder"),
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule6HolderVMa",
            MangledName = "$s10TestModule6HolderV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Conformances = new List<TypeConformance>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            ParentDecl = module,
            ModuleDecl = module
        };
        module.Types.Add(parent);

        var moduleDb = new ModuleTypeDatabase(ModuleName, "/tmp/TestModule.dylib");
        moduleDb.RegisterType(parent.SwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName(ModuleName, "Holder"),
            SwiftTypeName = parent.SwiftTypeName,
            MetadataAccessor = parent.MetadataAccessor,
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        typeDb.AddModuleDatabase(moduleDb);

        return (module, typeDb, parent);
    }

    private static MethodEnvironment MethodEnvWithClosureParam(TypeSpec paramType)
    {
        var (module, typeDb, parent) = Environment();
        var method = MethodWithClosureParam(paramType, module, parent);
        return new MethodEnvironment(method, typeDb);
    }

    private static MethodDecl MethodWithClosureParam(TypeSpec paramType, ModuleDecl module, TypeDecl parent) => new()
    {
        Name = "register",
        MangledName = "$s10TestModule6HolderV8registeryyc_tF",
        MethodType = MethodType.Instance,
        IsConstructor = false,
        CSSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = TupleTypeSpec.Empty, Name = "", PrivateName = "",
                IsInOut = false, IsGeneric = false, ParentDecl = parent, ModuleDecl = module
            },
            new ArgumentDecl
            {
                SwiftTypeSpec = paramType, Name = "handler", PrivateName = "handler",
                IsInOut = false, IsGeneric = false, ParentDecl = parent, ModuleDecl = module
            }
        },
        GenericParameters = new List<GenericArgumentDecl>(),
        ParentDecl = parent,
        ModuleDecl = module,
        Throws = false,
        IsAsync = false,
        Visibility = Visibility.Public
    };

    private static PropertyDecl PropertyWithType(TypeSpec typeSpec)
    {
        var (module, _, parent) = Environment();
        var getter = new MethodDecl
        {
            Name = "htmlProvider_get",
            MangledName = "$s10TestModule6HolderV12htmlProvidervg",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = typeSpec, Name = "", PrivateName = "",
                    IsInOut = false, IsGeneric = false, ParentDecl = parent, ModuleDecl = module
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parent,
            ModuleDecl = module,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        return new PropertyDecl
        {
            Name = "htmlProvider",
            SwiftTypeSpec = typeSpec,
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getter } },
            ParentDecl = parent,
            ModuleDecl = module
        };
    }

    private static (ModuleEmissionContext ctx, string swift, string cs) EmitMethodThroughHandler(TypeSpec paramType)
    {
        var (module, typeDb, parent) = Environment();
        // xcframework mode: the cdecl wrapper paths are gated on AsyncLibraryName being set.
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var method = MethodWithClosureParam(paramType, module, parent);
        parent.Methods.Add(method);

        var ctx = new ModuleEmissionContext();
        var context = new TypeHandlerContext(null, new(), null, EmissionContext: ctx);
        var csSw = new StringWriter();
        var swiftSw = new StringWriter();
        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var conductor = new Conductor(new NullLoggerFactory());
        var env = handler.Marshal(method, typeDb);

        handler.Emit(new CSharpWriter(csSw), new SwiftWriter(swiftSw), env, conductor, context);

        return (ctx, swiftSw.ToString(), csSw.ToString());
    }
}
