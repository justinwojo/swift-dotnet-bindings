// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Members returning a non-frozen value type built from generic parameters. Swift returns such a
/// value through the indirect-result register for every member kind, and the direct P/Invoke
/// declares it by value with no result buffer. An instance member on a generic parent reaches it
/// through the static-dispatch wrapper, which now admits bound-generic arguments over the parent's
/// parameters and results nested in the parent; a member with no Swift-side carrier is tombstoned.
/// </summary>
public class GenericInstanceIndirectResultTests
{
    // ── Static-dispatch wrapper admits the instance shapes ─────────────

    [Fact]
    public void BoundGenericStructArgument_TakesWrapperAndLoadsPayload()
    {
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateGenericParent(moduleDecl);
        var method = CreateMethod("having", parent, moduleDecl, BoundOverParent("TestModule.Request"));
        method.CSSignature.Add(CreateArgument(BoundOverParent("TestModule.Aggregate"), "aggregate", moduleDecl));
        parent.Methods.Add(method);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(GenericDispatchEmitter.CanEmitStaticDispatch(env, parent, GenericDispatchKind.Method));
        var output = EmitSwift(env);
        Assert.Contains("aggregate.assumingMemoryBound(to: Aggregate<T>.self).pointee", output);
        Assert.Contains("resultPtr", output);
    }

    [Fact]
    public void BoundGenericClassArgument_TakesWrapperAndAdoptsReference()
    {
        // The managed side passes a class argument as the object reference itself, so loading
        // `.pointee` through it would read the object's isa word as the reference.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateGenericParent(moduleDecl);
        var method = CreateMethod("aliased", parent, moduleDecl, BoundOverParent("TestModule.Request"));
        method.CSSignature.Add(CreateArgument(BoundOverParent("TestModule.Alias"), "alias", moduleDecl));
        parent.Methods.Add(method);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(GenericDispatchEmitter.CanEmitStaticDispatch(env, parent, GenericDispatchKind.Method));
        var output = EmitSwift(env);
        Assert.Contains("Unmanaged<Alias<T>>.fromOpaque(alias).takeUnretainedValue()", output);
        Assert.DoesNotContain("assumingMemoryBound(to: Alias<T>.self)", output);
    }

    [Theory]
    [InlineData("Swift.Array")]
    [InlineData("Swift.Optional")]
    [InlineData("TestModule.Unknown")]
    public void UnprovenBoundGenericArgument_StaysOffTheWrapper(string outerName)
    {
        // Standard-library generics each have their own bridged managed form, and a nominal the
        // type database does not know has no proven managed form at all.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateGenericParent(moduleDecl);
        var method = CreateMethod("having", parent, moduleDecl, new NamedTypeSpec("Swift.Int"));
        method.CSSignature.Add(CreateArgument(BoundOverParent(outerName), "values", moduleDecl));

        Assert.False(GenericDispatchEmitter.CanEmitStaticDispatch(
            new MethodEnvironment(method, typeDb), parent, GenericDispatchKind.Method));
    }

    [Fact]
    public void InOutBoundGenericArgument_StaysOffTheWrapper()
    {
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateGenericParent(moduleDecl);
        var method = CreateMethod("fold", parent, moduleDecl, new NamedTypeSpec("Swift.Int"));
        var arg = CreateArgument(BoundOverParent("TestModule.Aggregate"), "aggregate", moduleDecl);
        arg.IsInOut = true;
        method.CSSignature.Add(arg);

        Assert.False(GenericDispatchEmitter.CanEmitStaticDispatch(
            new MethodEnvironment(method, typeDb), parent, GenericDispatchKind.Method));
    }

    // ── ABI floor for members no carrier took ──────────────────────────

    [Fact]
    public void MemberGenericResultWithoutCarrier_Tombstones()
    {
        // `func with<U>(_:) -> Request<T>` on a generic parent: nothing Swift-side takes it, and
        // the direct call would adopt its own stack slot as the result.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateGenericParent(moduleDecl);
        var method = CreateMethod("with", parent, moduleDecl, BoundOverParent("TestModule.Request"));
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.HasResilientGenericResultDirectDispatch(env));
        Assert.True(WrapperValidation.IsAbiFloorTombstoned(env));
        var issue = WrapperValidation.GetNonBlittableCallConvSwiftIssue(env);
        Assert.Equal(WrapperValidation.UncallableAbiDiagnosticId, issue?.DiagnosticId);
        Assert.Contains("result buffer", issue!.Value.Message);
    }

    [Fact]
    public void FreeFunctionResultWithoutCarrier_Tombstones()
    {
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateGenericParent(moduleDecl);
        var method = CreateMethod("makeRequest", parent, moduleDecl,
            new NamedTypeSpec("TestModule.Request", new NamedTypeSpec("τ_0_0")));
        method.ParentDecl = moduleDecl;
        method.MethodType = MethodType.Static;

        Assert.True(WrapperValidation.HasResilientGenericResultDirectDispatch(new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void ResultWithCarrier_DoesNotFire()
    {
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateGenericParent(moduleDecl);
        var method = CreateMethod("having", parent, moduleDecl, BoundOverParent("TestModule.Request"));
        method.UsesCdeclMethodWrapper = true;

        Assert.False(WrapperValidation.HasResilientGenericResultDirectDispatch(new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void ReferenceResult_DoesNotFire()
    {
        // A class comes back as one retained reference, so the direct call reads it whole.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateGenericParent(moduleDecl);
        var method = CreateMethod("make", parent, moduleDecl, BoundOverParent("TestModule.Alias"));
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.HasResilientGenericResultDirectDispatch(env));
        Assert.False(WrapperValidation.HasUnprojectableBoundGenericValueDirectResult(env));
        Assert.False(WrapperValidation.IsAbiFloorTombstoned(env));
    }

    [Fact]
    public void FrozenResultWithoutProjection_FiresOnlyTheProjectionFloor()
    {
        // A frozen result's layout is visible to the caller, so it is not the resilient floor's
        // case. With no projection for the bound type, though, the only spelling left reads it out
        // of one register-sized slot, which is the projection floor's case, and that alone.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateGenericParent(moduleDecl);
        var method = CreateMethod("make", parent, moduleDecl, BoundOverParent("TestModule.FrozenRequest"));
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.HasResilientGenericResultDirectDispatch(env));
        Assert.True(WrapperValidation.HasUnprojectableBoundGenericValueDirectResult(env));
        Assert.True(WrapperValidation.IsAbiFloorTombstoned(env));
    }

    [Fact]
    public void Constructor_DoesNotFire()
    {
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateGenericParent(moduleDecl);
        var method = CreateMethod("init", parent, moduleDecl, BoundOverParent("TestModule.Table"));
        method.IsConstructor = true;

        Assert.False(WrapperValidation.HasResilientGenericResultDirectDispatch(new MethodEnvironment(method, typeDb)));
    }

    // ── No stack-slot adoption for a value result ──────────────────────

    [Fact]
    public void UnprojectableBoundGenericValueResult_TombstonesInsteadOfAdoptingTheResultLocal()
    {
        // A value result the factory cannot project (a frozen one here, outside the resilient
        // floor) used to be read back through `new IntPtr(&result)`, which treats the one-word
        // return local as the value's storage. Nothing that local holds is the value's storage for
        // a T-dependent or multi-word layout, so the member keeps its declaration, carries the
        // ABI-floor marker and throws instead of emitting the read.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateGenericParent(moduleDecl);
        var method = CreateMethod("makeFrozen", parent, moduleDecl, BoundOverParent("TestModule.FrozenRequest"));
        method.ParentDecl = moduleDecl;
        method.MethodType = MethodType.Static;
        method.GenericParameters.Add(new GenericArgumentDecl(
            "τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>()));

        var env = (MethodEnvironment)new MethodHandler(new NullLogger<MethodHandler>()).Marshal(method, typeDb);
        Assert.False(WrapperValidation.HasResilientGenericResultDirectDispatch(env));
        Assert.True(WrapperValidation.HasUnprojectableBoundGenericValueDirectResult(env));

        var emitter = new WrapperEmitter(env, new SignatureHandler(env));
        var csText = new StringWriter();
        var exception = Record.Exception(() =>
            emitter.EmitMethod(new CSharpWriter(csText), new SwiftWriter(new StringWriter())));

        Assert.Null(exception);
        var cs = csText.ToString();
        Assert.DoesNotContain("new IntPtr(&result)", cs);
        Assert.Contains($"DiagnosticId = \"{WrapperValidation.UncallableAbiDiagnosticId}\"", cs);
        Assert.Contains("has no projection for", cs);
        Assert.Contains("throw new NotSupportedException(", cs);
    }

    [Fact]
    public void UnprojectableBoundGenericValueResult_UnderTheFloor_EmitsTheTombstone()
    {
        // The same unprojectable value result on a non-frozen type is covered by the ABI floor,
        // which rolls the body back and throws under the member's marker. Reaching the return arm
        // first must not stop that: the member keeps its declaration and its throwing body.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateGenericParent(moduleDecl);
        var method = CreateMethod("makeRequest", parent, moduleDecl, BoundOverParent("TestModule.Request"));
        method.ParentDecl = moduleDecl;
        method.MethodType = MethodType.Static;
        method.GenericParameters.Add(new GenericArgumentDecl(
            "τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>()));

        var env = (MethodEnvironment)new MethodHandler(new NullLogger<MethodHandler>()).Marshal(method, typeDb);
        Assert.True(WrapperValidation.IsAbiFloorTombstoned(env));

        var emitter = new WrapperEmitter(env, new SignatureHandler(env));
        var csText = new StringWriter();
        var exception = Record.Exception(() =>
            emitter.EmitMethod(new CSharpWriter(csText), new SwiftWriter(new StringWriter())));

        Assert.Null(exception);
        Assert.Contains("throw new NotSupportedException(", csText.ToString());
        Assert.DoesNotContain("new IntPtr(&result)", csText.ToString());
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static string EmitSwift(MethodEnvironment env)
    {
        var sw = new StringWriter();
        MethodWrapperEmitter.EmitSwiftMethodWrapper(new SwiftWriter(sw), env, new ModuleEmissionContext());
        return sw.ToString();
    }

    private static NamedTypeSpec BoundOverParent(string name)
        => new(name, new NamedTypeSpec("τ_0_0"));

    private static TypeDecl CreateGenericParent(ModuleDecl moduleDecl)
    {
        var decl = new StructDecl
        {
            Name = "Table",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Table"),
            MangledName = "$s10TestModule5TableVN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
            },
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = false,
            MetadataAccessor = "$s10TestModule5TableVMa",
        };
        moduleDecl.Types.Add(decl);
        return decl;
    }

    private static ArgumentDecl CreateArgument(TypeSpec type, string name, ModuleDecl moduleDecl)
        => new()
        {
            SwiftTypeSpec = type,
            Name = name,
            PrivateName = name,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl,
        };

    private static MethodDecl CreateMethod(string name, TypeDecl parent, ModuleDecl moduleDecl, TypeSpec returnType)
        => new()
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl> { CreateArgument(returnType, "", moduleDecl) },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parent,
            ModuleDecl = moduleDecl,
        };

    private static (ModuleDecl ModuleDecl, TypeDatabase TypeDb) CreateEnvironment()
    {
        var typeDb = new TypeDatabase();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        Register(testModule, "Table", TypeRecordKind.Struct, frozen: false);
        Register(testModule, "Request", TypeRecordKind.Struct, frozen: false);
        Register(testModule, "FrozenRequest", TypeRecordKind.Struct, frozen: true);
        Register(testModule, "Aggregate", TypeRecordKind.Struct, frozen: false);
        Register(testModule, "Alias", TypeRecordKind.Class, frozen: false);
        typeDb.AddModuleDatabase(testModule);

        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
        return (moduleDecl, typeDb);
    }

    private static void Register(ModuleTypeDatabase module, string name, TypeRecordKind kind, bool frozen)
        => module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", name),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
                MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
                Flags = frozen ? TypeRecordFlags.Frozen : TypeRecordFlags.None,
                Kind = kind
            });
}
