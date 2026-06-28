// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="SubclassClosedParentTrampolineEmitter"/>: per-method concrete
/// <c>@_cdecl</c> shims that surface a bound-generic base class's instance methods on a concrete
/// (non-generic) subclass which closes ALL of the base's type parameters.
///
/// The crux this pins is the τ_0 (class-level) vs τ_1 (method-own) generic-parameter distinction:
/// every instance method of a generic class carries the enclosing class's type parameters in its
/// <c>GenericParameters</c> list, so a naive <c>method.IsGeneric</c> check would reject every
/// inherited method. Only a method that introduces its OWN generics (canonical depth ≥ 1) is
/// genuinely un-specializable from a single concrete subclass.
/// </summary>
public class SubclassClosedParentTrampolineEmitterTests
{
    private const string Module = "TestModule";

    #region Emission: concrete leaf of a bound-generic base

    [Fact]
    public void Emit_ConcreteLeafOfGenericBase_EmitsExtensionClassAndCdeclShim()
    {
        var (moduleDecl, typeDb) = CreateXcframeworkEnvironment();
        var baseDecl = CreateGenericBase("Base", moduleDecl,
            CreateInheritedVoidMethod("pause", moduleDecl));
        var leaf = CreateConcreteLeaf("Leaf", "Base", moduleDecl);

        var (csOut, swiftOut) = Emit(leaf, moduleDecl, typeDb);

        // C# extension class + zero-arg void extension method on the leaf, backed by a P/Invoke.
        Assert.Contains("public static partial class LeafBaseTrampolines", csOut);
        Assert.Contains("public static void Pause(this Leaf self)", csOut);
        Assert.Contains("LibraryImport", csOut);
        Assert.Contains("CallConvCdecl", csOut);

        // Swift @_cdecl shim that unsafeBitCasts opaque self to the CONCRETE leaf and calls through.
        Assert.Contains("@_cdecl(", swiftOut);
        Assert.Contains($"unsafeBitCast(OpaquePointer(self_), to: {Module}.Leaf.self)", swiftOut);
        Assert.Contains("__self.pause()", swiftOut);
        // No metadata/PWT parameter crosses the boundary — the only Swift param is the opaque self.
        Assert.Contains("_ self_: UnsafeMutableRawPointer", swiftOut);
    }

    [Fact]
    public void Emit_BlittableScalarReturn_ProjectsToCSharpScalar()
    {
        var (moduleDecl, typeDb) = CreateXcframeworkEnvironment();
        var baseDecl = CreateGenericBase("Base", moduleDecl,
            CreateInheritedScalarMethod("currentPhase", "Swift.Int32", moduleDecl));
        var leaf = CreateConcreteLeaf("Leaf", "Base", moduleDecl);

        var (csOut, swiftOut) = Emit(leaf, moduleDecl, typeDb);

        Assert.Contains("public static int CurrentPhase(this Leaf self)", csOut);
        Assert.Contains("-> Int32", swiftOut);
        Assert.Contains("return __self.currentPhase()", swiftOut);
    }

    [Fact]
    public void Emit_SymbolRegisteredOnce_SecondPassWithSameContextDoesNotReEmit()
    {
        var (moduleDecl, typeDb) = CreateXcframeworkEnvironment();
        var baseDecl = CreateGenericBase("Base", moduleDecl,
            CreateInheritedVoidMethod("pause", moduleDecl));
        var leaf = CreateConcreteLeaf("Leaf", "Base", moduleDecl);
        var ctx = new ModuleEmissionContext();

        var first = Emit(leaf, moduleDecl, typeDb, ctx);
        var second = Emit(leaf, moduleDecl, typeDb, ctx);

        // The wrapper symbol is registered on the shared context the first time; the second pass
        // finds it already claimed and skips the method (no duplicate SBW_ symbol).
        Assert.Contains("public static void Pause(this Leaf self)", first.cs);
        Assert.DoesNotContain("public static void Pause(this Leaf self)", second.cs);
    }

    #endregion

    #region Eligibility: rejected shapes produce no member

    [Fact]
    public void Emit_MethodOwnGeneric_IsRejected()
    {
        // `transform` carries the class params (τ_0_0, τ_0_1) AND a method-own generic (τ_1_0).
        // It cannot be specialized from a single concrete subclass, so it must not emit.
        var (moduleDecl, typeDb) = CreateXcframeworkEnvironment();
        var transform = CreateInheritedVoidMethod("transform", moduleDecl);
        transform.GenericParameters.Add(GenericParam("τ_1_0"));
        var baseDecl = CreateGenericBase("Base", moduleDecl, transform);
        var leaf = CreateConcreteLeaf("Leaf", "Base", moduleDecl);

        var (csOut, swiftOut) = Emit(leaf, moduleDecl, typeDb);

        Assert.DoesNotContain("Transform", csOut);
        Assert.DoesNotContain("transform", swiftOut);
    }

    [Fact]
    public void Emit_MethodWithParameter_IsRejected()
    {
        // First cut covers zero-arg methods only; a method with a real parameter is declined.
        var (moduleDecl, typeDb) = CreateXcframeworkEnvironment();
        var configure = CreateInheritedVoidMethod("configure", moduleDecl);
        configure.CSSignature.Add(new ArgumentDecl
        {
            Name = "value",
            PrivateName = "value",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = configure,
            ModuleDecl = moduleDecl
        });
        var baseDecl = CreateGenericBase("Base", moduleDecl, configure);
        var leaf = CreateConcreteLeaf("Leaf", "Base", moduleDecl);

        var (csOut, _) = Emit(leaf, moduleDecl, typeDb);

        Assert.DoesNotContain("Configure", csOut);
    }

    [Fact]
    public void Emit_AsyncAndThrowingMethods_AreRejected()
    {
        var (moduleDecl, typeDb) = CreateXcframeworkEnvironment();
        var asyncMethod = CreateInheritedVoidMethod("load", moduleDecl);
        asyncMethod.IsAsync = true;
        var throwingMethod = CreateInheritedVoidMethod("validate", moduleDecl);
        throwingMethod.Throws = true;
        var baseDecl = CreateGenericBase("Base", moduleDecl, asyncMethod, throwingMethod);
        var leaf = CreateConcreteLeaf("Leaf", "Base", moduleDecl);

        var (csOut, _) = Emit(leaf, moduleDecl, typeDb);

        Assert.DoesNotContain("Load", csOut);
        Assert.DoesNotContain("Validate", csOut);
    }

    [Fact]
    public void Emit_NonBlittableReturn_IsRejected()
    {
        // A class-typed (non-scalar) return has no identity-marshalled @_cdecl representation here.
        var (moduleDecl, typeDb) = CreateXcframeworkEnvironment();
        var baseDecl = CreateGenericBase("Base", moduleDecl,
            CreateInheritedScalarMethod("snapshot", "TestModule.Readout", moduleDecl));
        var leaf = CreateConcreteLeaf("Leaf", "Base", moduleDecl);

        var (csOut, _) = Emit(leaf, moduleDecl, typeDb);

        Assert.DoesNotContain("Snapshot", csOut);
    }

    [Fact]
    public void Emit_BoolReturn_IsRejectedInFirstCut()
    {
        // Bool needs an Int8 projection on the cdecl boundary; intentionally excluded for now.
        var (moduleDecl, typeDb) = CreateXcframeworkEnvironment();
        var baseDecl = CreateGenericBase("Base", moduleDecl,
            CreateInheritedScalarMethod("isReady", "Swift.Bool", moduleDecl));
        var leaf = CreateConcreteLeaf("Leaf", "Base", moduleDecl);

        var (csOut, _) = Emit(leaf, moduleDecl, typeDb);

        Assert.DoesNotContain("IsReady", csOut);
    }

    [Fact]
    public void Emit_LeafOwnMethod_IsNotShadowedByTrampoline()
    {
        // A method the leaf declares itself already emits on the flat leaf class; the trampoline
        // must skip it to avoid a redundant, shadowing extension method.
        var (moduleDecl, typeDb) = CreateXcframeworkEnvironment();
        var baseDecl = CreateGenericBase("Base", moduleDecl,
            CreateInheritedVoidMethod("pause", moduleDecl));
        var leaf = CreateConcreteLeaf("Leaf", "Base", moduleDecl);
        // The leaf overrides pause() itself.
        leaf.Methods.Add(CreateInheritedVoidMethod("pause", moduleDecl, parent: leaf));

        var (csOut, _) = Emit(leaf, moduleDecl, typeDb);

        Assert.DoesNotContain("public static void Pause(this Leaf self)", csOut);
    }

    #endregion

    #region Shape guards: leaf / base / mode

    [Fact]
    public void Emit_NonGenericBase_ProducesNoEmission()
    {
        // SuperclassNames carries no `<...>` — an ordinary (non-bound-generic) base is handled by
        // the regular inheritance pipeline, not this emitter.
        var (moduleDecl, typeDb) = CreateXcframeworkEnvironment();
        var baseDecl = CreateGenericBase("Base", moduleDecl,
            CreateInheritedVoidMethod("pause", moduleDecl));
        var leaf = CreateConcreteLeaf("Leaf", "Base", moduleDecl, boundGeneric: false);

        var (csOut, _) = Emit(leaf, moduleDecl, typeDb);

        Assert.DoesNotContain("LeafBaseTrampolines", csOut);
    }

    [Fact]
    public void Emit_RootClass_ProducesNoEmission()
    {
        var (moduleDecl, typeDb) = CreateXcframeworkEnvironment();
        var leaf = CreateConcreteLeaf("Leaf", baseSimpleName: null, moduleDecl); // no superclass

        var (csOut, _) = Emit(leaf, moduleDecl, typeDb);

        Assert.DoesNotContain("LeafBaseTrampolines", csOut);
    }

    [Fact]
    public void Emit_GenericLeaf_ProducesNoEmission()
    {
        // A generic leaf is the open-generic case, handled elsewhere.
        var (moduleDecl, typeDb) = CreateXcframeworkEnvironment();
        var baseDecl = CreateGenericBase("Base", moduleDecl,
            CreateInheritedVoidMethod("pause", moduleDecl));
        var leaf = CreateConcreteLeaf("Leaf", "Base", moduleDecl);
        leaf.GenericParameters.Add(GenericParam("τ_0_0"));

        var (csOut, _) = Emit(leaf, moduleDecl, typeDb);

        Assert.DoesNotContain("LeafBaseTrampolines", csOut);
    }

    [Fact]
    public void Emit_DirectMode_ProducesNoEmission()
    {
        // No wrapper library (AsyncLibraryName unset) ⇒ Direct mode ⇒ no @_cdecl host.
        var (moduleDecl, typeDb) = CreateXcframeworkEnvironment();
        typeDb.AsyncLibraryName = null;
        var baseDecl = CreateGenericBase("Base", moduleDecl,
            CreateInheritedVoidMethod("pause", moduleDecl));
        var leaf = CreateConcreteLeaf("Leaf", "Base", moduleDecl);

        var (csOut, _) = Emit(leaf, moduleDecl, typeDb);

        Assert.DoesNotContain("LeafBaseTrampolines", csOut);
    }

    [Fact]
    public void Emit_InternalLeaf_ProducesNoEmission()
    {
        var (moduleDecl, typeDb) = CreateXcframeworkEnvironment();
        var baseDecl = CreateGenericBase("Base", moduleDecl,
            CreateInheritedVoidMethod("pause", moduleDecl));
        var leaf = CreateConcreteLeaf("Leaf", "Base", moduleDecl);
        leaf.IsModuleInternal = true;

        var (csOut, _) = Emit(leaf, moduleDecl, typeDb);

        Assert.DoesNotContain("LeafBaseTrampolines", csOut);
    }

    [Fact]
    public void Emit_NestedLeaf_ProducesNoEmission()
    {
        // A nested leaf is emitted INLINE inside its enclosing type's body; emitting the
        // namespace-scope trampoline extension class there would yield CS1109. Such a leaf
        // (ParentDecl is a TypeDecl, not the module) must be declined.
        var (moduleDecl, typeDb) = CreateXcframeworkEnvironment();
        var baseDecl = CreateGenericBase("Base", moduleDecl,
            CreateInheritedVoidMethod("pause", moduleDecl));
        var outer = CreateConcreteLeaf("Outer", baseSimpleName: null, moduleDecl);
        var leaf = CreateConcreteLeaf("Leaf", "Base", moduleDecl);
        leaf.ParentDecl = outer; // nested inside Outer

        var (csOut, _) = Emit(leaf, moduleDecl, typeDb);

        Assert.DoesNotContain("LeafBaseTrampolines", csOut);
    }

    #endregion

    #region Helpers

    private static (string cs, string swift) Emit(
        ClassDecl leaf, ModuleDecl moduleDecl, TypeDatabase typeDb, ModuleEmissionContext? ctx = null)
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        SubclassClosedParentTrampolineEmitter.EmitSubclassClosedParentTrampolines(
            csWriter, swiftWriter, leaf, moduleDecl, typeDb,
            ctx ?? new ModuleEmissionContext(), NullLogger.Instance);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    private static GenericArgumentDecl GenericParam(string canonical) =>
        new(canonical, canonical, new List<GenericParameterConformance>(), new List<GenericParameterConformance>());

    /// <summary>
    /// An instance method as it appears on a generic class: it carries the enclosing class's two
    /// type parameters (τ_0_0, τ_0_1) in its GenericParameters list but introduces none of its own.
    /// </summary>
    private static MethodDecl CreateInheritedVoidMethod(string name, ModuleDecl moduleDecl, BaseDecl? parent = null)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}_mangled",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl> { ReturnSlot(TupleTypeSpec.Empty, moduleDecl) },
            GenericParameters = new List<GenericArgumentDecl> { GenericParam("τ_0_0"), GenericParam("τ_0_1") },
            ParentDecl = parent,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false
        };
        return method;
    }

    private static MethodDecl CreateInheritedScalarMethod(string name, string returnTypeName, ModuleDecl moduleDecl)
    {
        var method = CreateInheritedVoidMethod(name, moduleDecl);
        method.CSSignature[0] = ReturnSlot(new NamedTypeSpec(returnTypeName), moduleDecl);
        return method;
    }

    private static ArgumentDecl ReturnSlot(TypeSpec spec, ModuleDecl moduleDecl) => new()
    {
        Name = string.Empty,
        PrivateName = string.Empty,
        SwiftTypeSpec = spec,
        IsInOut = false,
        IsGeneric = false,
        ParentDecl = null,
        ModuleDecl = moduleDecl
    };

    private static ClassDecl CreateGenericBase(string name, ModuleDecl moduleDecl, params MethodDecl[] methods)
    {
        var decl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(methods),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl> { GenericParam("τ_0_0"), GenericParam("τ_0_1") },
            Conformances = new List<TypeConformance>(),
            IsFinal = false,
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        foreach (var m in methods)
            m.ParentDecl ??= decl;
        moduleDecl.Types.Add(decl);
        return decl;
    }

    private static ClassDecl CreateConcreteLeaf(
        string name, string? baseSimpleName, ModuleDecl moduleDecl, bool boundGeneric = true)
    {
        var superclassNames = new List<string>();
        if (baseSimpleName != null)
        {
            superclassNames.Add(boundGeneric
                ? $"{Module}.{baseSimpleName}<{Module}.A, {Module}.B>"
                : $"{Module}.{baseSimpleName}");
        }

        var decl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            IsFinal = true,
            SuperclassNames = superclassNames,
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(decl);
        return decl;
    }

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateXcframeworkEnvironment()
    {
        var typeDb = new TypeDatabase
        {
            // Non-empty AsyncLibraryName ⇒ GenerationMode.XCFramework ⇒ wrapper-host mode.
            AsyncLibraryName = "TestModuleSwiftBindings"
        };

        var moduleDecl = new ModuleDecl
        {
            Name = Module,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        return (moduleDecl, typeDb);
    }

    #endregion
}
