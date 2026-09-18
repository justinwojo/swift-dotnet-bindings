// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Static members declared on generic parents. Swift passes them differently from instance
/// members: a static on a generic struct or enum takes no self (with a resilient generic result
/// coming back through the indirect-result register), and a static on a generic class takes its
/// metatype as self. The static-dispatch wrapper reaches both from Swift source as
/// <c>Self.member</c>; the direct P/Invoke spells neither correctly, so whatever the wrapper
/// turns down in a provably-wrong shape is tombstoned rather than left calling into a register
/// nobody wrote.
/// </summary>
public class GenericParentStaticMemberTests
{
    // ── Static-dispatch method wrapper ──────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaticMethodWrapper_CallsSelfWithoutReceiver(bool classParent)
    {
        var (moduleDecl, typeDb) = CreateEnvironment(holderIsClass: classParent);
        var parent = CreateGenericParent("Holder", moduleDecl, classParent);
        var method = CreateMethod("elementSize", parent, moduleDecl, new NamedTypeSpec("Swift.Int"));
        method.MethodType = MethodType.Static;
        parent.Methods.Add(method);

        var env = new MethodEnvironment(method, typeDb);
        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));

        var sw = new StringWriter();
        MethodWrapperEmitter.EmitSwiftMethodWrapper(new SwiftWriter(sw), env, new ModuleEmissionContext());
        var output = sw.ToString();

        Assert.Contains("_SBW_GSM_", output);
        Assert.Contains("Self.elementSize(", output);
        var cdeclLine = CdeclSignatureLine(output);
        Assert.Contains("_metadata0", cdeclLine);
        Assert.DoesNotContain("self_", cdeclLine);
        Assert.DoesNotContain("selfPtr", output);
    }

    [Fact]
    public void StaticMethodWrapper_ClassReturnBoundToOwnParameter_ReturnsRetainedPointer()
    {
        // `static func make() -> Holder<T>` on a generic class: the result mentions T but is one
        // object reference, which the managed side reads as a returned pointer. A result buffer
        // here would take the managed caller's first argument as the destination.
        var (moduleDecl, typeDb) = CreateEnvironment(holderIsClass: true);
        var parent = CreateGenericParent("Holder", moduleDecl, classParent: true);
        var returnType = new NamedTypeSpec("TestModule.Holder");
        returnType.GenericParameters.Add(new NamedTypeSpec("τ_0_0"));
        var method = CreateMethod("make", parent, moduleDecl, returnType);
        method.MethodType = MethodType.Static;
        parent.Methods.Add(method);

        var env = new MethodEnvironment(method, typeDb);
        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));

        var sw = new StringWriter();
        MethodWrapperEmitter.EmitSwiftMethodWrapper(new SwiftWriter(sw), env, new ModuleEmissionContext());
        var output = sw.ToString();

        Assert.Contains("Self.make(", output);
        Assert.DoesNotContain("resultPtr", output);
        Assert.Contains("passRetained", output);
        Assert.Contains("-> UnsafeMutableRawPointer", CdeclSignatureLine(output));
    }

    // ── Static-dispatch property wrapper ────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaticPropertyGetter_ReadsSelfWithoutReceiver(bool classParent)
    {
        var (moduleDecl, typeDb) = CreateEnvironment(holderIsClass: classParent);
        var parent = CreateGenericParent("Holder", moduleDecl, classParent);
        var (property, getter) = CreateStaticProperty("elementStride", parent, moduleDecl, settable: false);

        Assert.True(PropertyWrapperEmitter.CanEmitGenericClassPropertyWrapper(property, parent, typeDb));
        var env = new MethodEnvironment(getter, typeDb);
        Assert.True(GenericDispatchEmitter.NeedsStaticDispatchForProperty(env, parent, property));

        var sw = new StringWriter();
        PropertyWrapperEmitter.EmitSwiftGetterWrapper(new SwiftWriter(sw), property,
            "SBW_Get_TestModule_Holder_elementStride", env, new ModuleEmissionContext());
        var output = sw.ToString();

        Assert.Contains("_SBW_GSPG_", output);
        Assert.Contains("Self.elementStride", output);
        var cdeclLine = CdeclSignatureLine(output);
        Assert.Contains("_metadata0", cdeclLine);
        Assert.DoesNotContain("self_", cdeclLine);
        Assert.DoesNotContain("selfPtr", output);
        Assert.DoesNotContain("let obj", output);
    }

    [Fact]
    public void StaticPropertySetter_AssignsSelfWithoutReceiver()
    {
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateGenericParent("Holder", moduleDecl, classParent: false);
        var (property, _) = CreateStaticProperty("counter", parent, moduleDecl, settable: true);
        var setter = property.Accessors.OfType<SetAccessorDecl>().Single().Method;

        var sw = new StringWriter();
        PropertyWrapperEmitter.EmitSwiftSetterWrapper(new SwiftWriter(sw), property,
            "SBW_Set_TestModule_Holder_counter", new MethodEnvironment(setter, typeDb), new ModuleEmissionContext());
        var output = sw.ToString();

        Assert.Contains("Self.counter = ", output);
        var cdeclLine = CdeclSignatureLine(output);
        Assert.Contains("_metadata0", cdeclLine);
        Assert.DoesNotContain("self_", cdeclLine);
        Assert.DoesNotContain("selfPtr", output);
    }

    // ── Statics from an extension that pins a parent parameter ─────────

    // The ABI dump spells the pin in either dialect: desugared with depth-0 parameters, or
    // sugared with the parent's own parameter names.
    private const string DesugaredPin = "<τ_0_0 where τ_0_0 == Swift.Int>";
    private const string SugaredPin = "<T where T == Swift.Int>";

    [Theory]
    [InlineData(false, DesugaredPin)]
    [InlineData(true, DesugaredPin)]
    [InlineData(false, SugaredPin)]
    [InlineData(true, SugaredPin)]
    public void PinnedExtensionStaticMethod_TakesNoStaticDispatchWrapper(bool resilientResult, string genericSig)
    {
        // `extension Query where T == Int { static func make(…) }`: `Self.make` does not
        // type-check in the unconditional conformance extension, so a wrapper here only ever
        // reaches the compile to be withdrawn, taking the declaration with it. The member stays
        // on the direct path instead, where the ABI floor tombstones the resilient result and
        // leaves a scalar result callable.
        var (moduleDecl, typeDb) = CreateEnvironment(holderFrozen: !resilientResult);
        var parent = CreateGenericParent("Holder", moduleDecl, classParent: false);
        TypeSpec returnType = resilientResult
            ? new NamedTypeSpec("TestModule.Holder", new NamedTypeSpec("τ_0_0"))
            : new NamedTypeSpec("Swift.Int");
        var method = CreateMethod("make", parent, moduleDecl, returnType);
        method.MethodType = MethodType.Static;
        method.IsExtensionMethod = true;
        method.RawGenericSig = genericSig;
        parent.Methods.Add(method);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(GenericDispatchEmitter.CanEmitStaticDispatch(env, parent, GenericDispatchKind.Method));
        var sw = new StringWriter();
        if (MethodWrapperEmitter.ShouldEmitWrapper(env))
            MethodWrapperEmitter.EmitSwiftMethodWrapper(new SwiftWriter(sw), env, new ModuleEmissionContext());
        Assert.DoesNotContain("Self.make(", sw.ToString());

        Assert.Equal(resilientResult, WrapperValidation.IsAbiFloorTombstoned(env));
    }

    [Theory]
    [InlineData("<τ_0_0>")]
    [InlineData("<T>")]
    public void PinnedExtensionStaticMethod_UnpinnedSiblingKeepsWrapper(string genericSig)
    {
        // A conformance requirement is not a pin: the static stays on the wrapper route.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateGenericParent("Holder", moduleDecl, classParent: false);
        var method = CreateMethod("elementSize", parent, moduleDecl, new NamedTypeSpec("Swift.Int"));
        method.MethodType = MethodType.Static;
        method.RawGenericSig = genericSig;
        parent.Methods.Add(method);

        Assert.True(GenericDispatchEmitter.CanEmitStaticDispatch(
            new MethodEnvironment(method, typeDb), parent, GenericDispatchKind.Method));
    }

    [Theory]
    [InlineData(DesugaredPin)]
    [InlineData(SugaredPin)]
    public void PinnedExtensionStaticProperty_TakesNoStaticDispatchWrapper(string genericSig)
    {
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateGenericParent("Holder", moduleDecl, classParent: false);
        var (property, getter) = CreateStaticProperty("limit", parent, moduleDecl, settable: false);
        getter.RawGenericSig = genericSig;

        Assert.False(PropertyWrapperEmitter.CanEmitGenericClassPropertyWrapper(property, parent, typeDb));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PinnedExtensionInit_TakesNoStaticFactory(bool sugaredDump)
    {
        // `extension Holder where T == Int { init(tag:) }`: the static factory is emitted in an
        // unconditional extension where this init does not exist. swift-api-digester dumps (every
        // Apple-direct binding) spell the pin only in the sugared dialect, with the parent's own
        // parameter name as the subject; the ctor path must recognize it the same way statics do.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateGenericParent("Holder", moduleDecl, classParent: false, sugaredDump);
        var init = CreateMethod("init", parent, moduleDecl,
            new NamedTypeSpec("TestModule.Holder", new NamedTypeSpec(sugaredDump ? "T" : "τ_0_0")));
        init.MethodType = MethodType.Static;
        init.IsConstructor = true;
        init.IsExtensionMethod = true;
        init.CSSignature.Add(CreateArgument(new NamedTypeSpec("Swift.Int"), "tag", moduleDecl));
        ApplyPin(init, sugaredDump);
        parent.Methods.Add(init);
        var env = new MethodEnvironment(init, typeDb);

        Assert.True(GenericDispatchEmitter.HasSameTypeConstraintOnParentGenericParam(init, parent));
        Assert.False(GenericDispatchEmitter.CanEmitStaticDispatch(env, parent, GenericDispatchKind.Constructor));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PinnedExtensionStaticMethod_RecognizedInEitherDumpDialect(bool sugaredDump)
    {
        // The same detector the constructor path uses, reached from a static.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateGenericParent("Holder", moduleDecl, classParent: false, sugaredDump);
        var method = CreateMethod("elementSize", parent, moduleDecl, new NamedTypeSpec("Swift.Int"));
        method.MethodType = MethodType.Static;
        method.IsExtensionMethod = true;
        ApplyPin(method, sugaredDump);
        parent.Methods.Add(method);

        Assert.False(GenericDispatchEmitter.CanEmitStaticDispatch(
            new MethodEnvironment(method, typeDb), parent, GenericDispatchKind.Method));
    }

    // ── Statics returning a type nested in the generic parent ───────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaticReturningOwnNestedType_TakesWrapper(bool nestedIsClass)
    {
        // `static func makeLeaf() -> Holder<T>.Leaf`: the nested value comes back through the
        // indirect-result register the direct call never passes, so the wrapper is its only route.
        var (moduleDecl, typeDb) = CreateEnvironment(nestedIsClass: nestedIsClass);
        var parent = CreateGenericParent("Holder", moduleDecl, classParent: false);
        var method = CreateMethod("makeLeaf", parent, moduleDecl, NestedLeafSpec());
        method.MethodType = MethodType.Static;
        parent.Methods.Add(method);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(GenericDispatchEmitter.CanEmitStaticDispatch(env, parent, GenericDispatchKind.Method));
        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
        var sw = new StringWriter();
        MethodWrapperEmitter.EmitSwiftMethodWrapper(new SwiftWriter(sw), env, new ModuleEmissionContext());
        var output = sw.ToString();

        Assert.Contains("Self.makeLeaf(", output);
        if (nestedIsClass)
        {
            Assert.Contains("passRetained", output);
            Assert.DoesNotContain("resultPtr", output);
        }
        else
        {
            Assert.Contains("initializeMemory(as: Holder<T>.Leaf.self", output);
        }
    }

    [Fact]
    public void InstanceReturningOwnNestedType_KeepsConservativeGate()
    {
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateGenericParent("Holder", moduleDecl, classParent: false);
        var method = CreateMethod("leaf", parent, moduleDecl, NestedLeafSpec());
        parent.Methods.Add(method);

        Assert.False(GenericDispatchEmitter.CanEmitStaticDispatch(
            new MethodEnvironment(method, typeDb), parent, GenericDispatchKind.Method));
    }

    [Fact]
    public void Floor_ValueStaticReturningNestedClass_DoesNotFire()
    {
        // The floor resolves the nested name, not its outer: a class comes back in a register,
        // so a resilient outer must not tombstone it.
        var (moduleDecl, typeDb) = CreateEnvironment(holderFrozen: false, nestedIsClass: true);
        var parent = CreateGenericParent("Holder", moduleDecl, classParent: false);
        var method = CreateMethod("makeLeaf", parent, moduleDecl, NestedLeafSpec());
        method.MethodType = MethodType.Static;

        Assert.False(WrapperValidation.HasMisconventionedGenericParentStaticDirectDispatch(new MethodEnvironment(method, typeDb)));
    }

    // ── Residual ABI floor ──────────────────────────────────────────────

    [Fact]
    public void Floor_ClassStaticWithoutCarrier_Tombstones()
    {
        // The direct call passes the metadata in argument registers and never writes the self
        // register a class static reads its metatype from.
        var (moduleDecl, typeDb) = CreateEnvironment(holderIsClass: true);
        var parent = CreateGenericParent("Holder", moduleDecl, classParent: true);
        var method = CreateMethod("kind", parent, moduleDecl, new NamedTypeSpec("Swift.Int"));
        method.MethodType = MethodType.Static;
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.HasMisconventionedGenericParentStaticDirectDispatch(env));
        Assert.True(WrapperValidation.IsAbiFloorTombstoned(env));
        var issue = WrapperValidation.GetNonBlittableCallConvSwiftIssue(env);
        Assert.NotNull(issue);
        Assert.Equal(WrapperValidation.UncallableAbiDiagnosticId, issue!.Value.DiagnosticId);
        Assert.Contains("metatype", issue.Value.Message);
    }

    [Fact]
    public void Floor_ClassStaticWithWrapper_DoesNotFire()
    {
        var (moduleDecl, typeDb) = CreateEnvironment(holderIsClass: true);
        var parent = CreateGenericParent("Holder", moduleDecl, classParent: true);
        var method = CreateMethod("kind", parent, moduleDecl, new NamedTypeSpec("Swift.Int"));
        method.MethodType = MethodType.Static;
        method.UsesCdeclMethodWrapper = true;

        Assert.False(WrapperValidation.HasMisconventionedGenericParentStaticDirectDispatch(new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_ClassStaticAccessor_TombstonesWithoutDeclarationMarker()
    {
        // The accessor body is floored like any other; the marker stays off the private
        // synthesized accessor so the public property that calls it still compiles.
        var (moduleDecl, typeDb) = CreateEnvironment(holderIsClass: true);
        var parent = CreateGenericParent("Holder", moduleDecl, classParent: true);
        var (_, getter) = CreateStaticProperty("elementStride", parent, moduleDecl, settable: false);
        var env = new MethodEnvironment(getter, typeDb);

        Assert.True(WrapperValidation.IsAbiFloorTombstoned(env));
        Assert.NotEqual(WrapperValidation.UncallableAbiDiagnosticId,
            WrapperValidation.GetNonBlittableCallConvSwiftIssue(env)?.DiagnosticId);
    }

    [Fact]
    public void Floor_ValueStaticReturningResilientParentGeneric_Tombstones()
    {
        // `static func make(_:) -> Holder<T>` on a non-frozen type: Swift returns it through the
        // indirect-result register, the direct call declares a by-value return and passes none.
        var (moduleDecl, typeDb) = CreateEnvironment(holderFrozen: false);
        var parent = CreateGenericParent("Holder", moduleDecl, classParent: false);
        var method = CreateMethod("make", parent, moduleDecl, new NamedTypeSpec("TestModule.Holder", new NamedTypeSpec("τ_0_0")));
        method.MethodType = MethodType.Static;
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.HasMisconventionedGenericParentStaticDirectDispatch(env));
        Assert.True(WrapperValidation.IsAbiFloorTombstoned(env));
        Assert.Equal(WrapperValidation.UncallableAbiDiagnosticId,
            WrapperValidation.GetNonBlittableCallConvSwiftIssue(env)?.DiagnosticId);
    }

    [Fact]
    public void Floor_ValueStaticReturningFrozenParentGeneric_DoesNotFire()
    {
        // A frozen result's layout is known to the caller; whether it is returned in registers
        // depends on its fields, so nothing is provably wrong and the member keeps its call.
        var (moduleDecl, typeDb) = CreateEnvironment(holderFrozen: true);
        var parent = CreateGenericParent("Holder", moduleDecl, classParent: false);
        var method = CreateMethod("make", parent, moduleDecl, new NamedTypeSpec("TestModule.Holder", new NamedTypeSpec("τ_0_0")));
        method.MethodType = MethodType.Static;

        Assert.False(WrapperValidation.HasMisconventionedGenericParentStaticDirectDispatch(new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_ValueStaticReturningScalar_DoesNotFire()
    {
        // The direct path spells this one correctly: no self, the parent's metadata trailing.
        var (moduleDecl, typeDb) = CreateEnvironment(holderFrozen: false);
        var parent = CreateGenericParent("Holder", moduleDecl, classParent: false);
        var method = CreateMethod("elementSize", parent, moduleDecl, new NamedTypeSpec("Swift.Int"));
        method.MethodType = MethodType.Static;
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.HasMisconventionedGenericParentStaticDirectDispatch(env));
        Assert.False(WrapperValidation.IsAbiFloorTombstoned(env));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Floor_NarrowingOverloadCarriesTombstoneMarker(bool hasWrapper)
    {
        // `static func kind(_ n: Int) -> Int` also gets a `Kind(int)` forwarder. A binding build
        // refuses an unmarked reference to an SB0009 member, so the forwarder of a tombstoned
        // primary carries the marker too; a primary with a sound route leaves it unmarked.
        var (moduleDecl, typeDb) = CreateEnvironment(holderIsClass: true);
        var parent = CreateGenericParent("Holder", moduleDecl, classParent: true);
        var method = CreateMethod("kind", parent, moduleDecl, new NamedTypeSpec("Swift.Int"));
        method.MethodType = MethodType.Static;
        method.UsesCdeclMethodWrapper = hasWrapper;
        method.CSSignature.Add(CreateArgument(new NamedTypeSpec("Swift.Int"), "n", moduleDecl));
        var env = new MethodEnvironment(method, typeDb);

        var sw = new StringWriter();
        NativeIntOverloadEmitter.TryEmitOverload(new CSharpWriter(sw), env);
        var output = sw.ToString();

        Assert.Contains("(int n)", output);
        if (hasWrapper)
            Assert.DoesNotContain("SB0009", output);
        else
            Assert.Contains($"DiagnosticId = \"{WrapperValidation.UncallableAbiDiagnosticId}\"", output);
    }

    [Fact]
    public void Floor_ClassInstanceMethod_DoesNotFire()
    {
        var (moduleDecl, typeDb) = CreateEnvironment(holderIsClass: true);
        var parent = CreateGenericParent("Holder", moduleDecl, classParent: true);
        var method = CreateMethod("kind", parent, moduleDecl, new NamedTypeSpec("Swift.Int"));

        Assert.False(WrapperValidation.HasMisconventionedGenericParentStaticDirectDispatch(new MethodEnvironment(method, typeDb)));
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static string CdeclSignatureLine(string output)
    {
        var lines = output.Split('\n');
        var at = Array.FindIndex(lines, l => l.Contains("@_cdecl("));
        Assert.True(at >= 0, $"Expected a @_cdecl wrapper in:\n{output}");
        return lines.Skip(at + 1).First(l => l.Contains("public func "));
    }

    /// <summary>
    /// Pins the member's parent parameter to <c>Swift.Int</c> the way each dump producer spells it:
    /// swiftc's ABI descriptor gives both a desugared and a sugared signature, swift-api-digester
    /// only the sugared one, which then arrives as the signature itself.
    /// </summary>
    private static void ApplyPin(MethodDecl member, bool sugaredDump)
    {
        member.RawGenericSig = sugaredDump ? SugaredPin : DesugaredPin;
        member.GenericParameters = GenericSignatureParser.ParseGenericSignature(
            member.RawGenericSig, sugaredDump ? null : SugaredPin);
    }

    private static NamedTypeSpec NestedLeafSpec()
    {
        var spec = new NamedTypeSpec("TestModule.Holder", new NamedTypeSpec("τ_0_0"));
        spec.InnerType = new NamedTypeSpec("Leaf");
        return spec;
    }

    private static TypeDecl CreateGenericParent(string name, ModuleDecl moduleDecl, bool classParent, bool sugaredDump = false)
    {
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}");
        var genericParameters = new List<GenericArgumentDecl>
        {
            new(sugaredDump ? "T" : "τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        TypeDecl decl = classParent
            ? new ClassDecl
            {
                Name = name,
                SwiftTypeName = swiftTypeName,
                MangledName = $"$s10TestModule{name.Length}{name}CN",
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Types = new List<TypeDecl>(),
                Operators = new List<OperatorDecl>(),
                Subscripts = new List<SubscriptDecl>(),
                GenericParameters = genericParameters,
                Conformances = new List<TypeConformance>(),
                ParentDecl = moduleDecl,
                ModuleDecl = moduleDecl,
            }
            : new StructDecl
            {
                Name = name,
                SwiftTypeName = swiftTypeName,
                MangledName = $"$s10TestModule{name.Length}{name}VN",
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Types = new List<TypeDecl>(),
                Operators = new List<OperatorDecl>(),
                Subscripts = new List<SubscriptDecl>(),
                GenericParameters = genericParameters,
                Conformances = new List<TypeConformance>(),
                ParentDecl = moduleDecl,
                ModuleDecl = moduleDecl,
                IsFrozen = true,
                MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
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

    private static (PropertyDecl Property, MethodDecl Getter) CreateStaticProperty(
        string name, TypeDecl parent, ModuleDecl moduleDecl, bool settable)
    {
        var intSpec = new NamedTypeSpec("Swift.Int");
        var getter = CreateMethod($"getter:{name}", parent, moduleDecl, intSpec);
        getter.MethodType = MethodType.Static;
        getter.IsAccessor = true;
        var accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getter } };
        if (settable)
        {
            var setter = CreateMethod($"setter:{name}", parent, moduleDecl, TupleTypeSpec.Empty);
            setter.MethodType = MethodType.Static;
            setter.IsAccessor = true;
            setter.CSSignature.Add(CreateArgument(intSpec, "newValue", moduleDecl));
            accessors.Add(new SetAccessorDecl { Method = setter });
        }

        var property = new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = intSpec,
            HasStorage = true,
            IsStatic = true,
            Accessors = accessors,
            ParentDecl = parent,
            ModuleDecl = moduleDecl,
        };
        parent.Properties.Add(property);
        return (property, getter);
    }

    private static (ModuleDecl ModuleDecl, TypeDatabase TypeDb) CreateEnvironment(
        bool holderFrozen = true, bool holderIsClass = false, bool nestedIsClass = false)
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
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Holder"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Holder"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Holder"),
                MetadataAccessor = holderIsClass ? "$s10TestModule6HolderCMa" : "$s10TestModule6HolderVMa",
                Flags = holderFrozen ? TypeRecordFlags.Frozen : TypeRecordFlags.None,
                Kind = holderIsClass ? TypeRecordKind.Class : TypeRecordKind.Struct
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Holder.Leaf"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Holder.Leaf"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Holder.Leaf"),
                MetadataAccessor = nestedIsClass ? "$s10TestModule6HolderV4LeafCMa" : "$s10TestModule6HolderV4LeafVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = nestedIsClass ? TypeRecordKind.Class : TypeRecordKind.Struct
            });
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
}
