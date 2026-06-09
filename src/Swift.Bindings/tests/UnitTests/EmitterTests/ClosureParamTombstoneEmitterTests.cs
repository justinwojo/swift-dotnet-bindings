// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for closure-parameter tombstone emission.
///
/// When a method's only blocker is an unsupported closure parameter shape, the emitter writes a
/// tombstoned-but-reachable surface (object? for the closure, throws at runtime, [Obsolete SB0005] +
/// [UnsupportedSwiftType]) instead of dropping the API wholesale.
/// </summary>
public class ClosureParamTombstoneEmitterTests
{
    [Fact]
    public void IsEligible_ClassMethodWithUnsupportedClosureParam_True()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Registry", moduleDecl);
        var method = CreateMethod("register", classDecl, moduleDecl);
        method.CSSignature.Add(CreateArg("decoder", BuildUnsupportedClosure(), moduleDecl));

        Assert.True(ClosureParamTombstoneEmitter.IsEligible(method, typeDatabase));
    }

    [Fact]
    public void IsEligible_ClassConstructor_True()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("DataLoader", moduleDecl);
        var ctor = CreateConstructor(classDecl, moduleDecl);
        ctor.CSSignature.Add(CreateArg("transform", BuildUnsupportedClosure(), moduleDecl));

        Assert.True(ClosureParamTombstoneEmitter.IsEligible(ctor, typeDatabase));
    }

    [Fact]
    public void IsEligible_FreeFunction_True()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var method = CreateModuleMethod("makeImageDecoder", moduleDecl);
        method.CSSignature.Add(CreateArg("closure", BuildUnsupportedClosure(), moduleDecl));

        Assert.True(ClosureParamTombstoneEmitter.IsEligible(method, typeDatabase));
    }

    [Fact]
    public void IsEligible_StaticMethod_True()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Registry", moduleDecl);
        var method = CreateMethod("registerStatic", classDecl, moduleDecl, isStatic: true);
        method.CSSignature.Add(CreateArg("decoder", BuildUnsupportedClosure(), moduleDecl));

        Assert.True(ClosureParamTombstoneEmitter.IsEligible(method, typeDatabase));
    }

    [Fact]
    public void IsEligible_Accessor_False()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Registry", moduleDecl);
        var method = CreateMethod("get", classDecl, moduleDecl);
        method.IsAccessor = true;
        method.CSSignature.Add(CreateArg("decoder", BuildUnsupportedClosure(), moduleDecl));

        Assert.False(ClosureParamTombstoneEmitter.IsEligible(method, typeDatabase));
    }

    [Fact]
    public void IsEligible_AsyncMethod_False()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Registry", moduleDecl);
        var method = CreateMethod("registerAsync", classDecl, moduleDecl);
        method.IsAsync = true;
        method.CSSignature.Add(CreateArg("decoder", BuildUnsupportedClosure(), moduleDecl));

        Assert.False(ClosureParamTombstoneEmitter.IsEligible(method, typeDatabase));
    }

    [Fact]
    public void IsEligible_MutatingMethod_False()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Registry", moduleDecl);
        var method = CreateMethod("mutate", classDecl, moduleDecl);
        method.IsMutating = true;
        method.CSSignature.Add(CreateArg("decoder", BuildUnsupportedClosure(), moduleDecl));

        Assert.False(ClosureParamTombstoneEmitter.IsEligible(method, typeDatabase));
    }

    [Fact]
    public void IsEligible_StructConstructor_False()
    {
        // Struct constructors are excluded — definite-assignment rules require all
        // fields be assigned, which a throw body doesn't satisfy.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var structDecl = CreateStructDecl("Anonymous", moduleDecl);
        var ctor = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule9AnonymousVACyACycfc",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("TestModule.Anonymous"), moduleDecl),
                CreateArg("closure", BuildUnsupportedClosure(), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = structDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        structDecl.Methods.Add(ctor);

        Assert.False(ClosureParamTombstoneEmitter.IsEligible(ctor, typeDatabase));
    }

    [Fact]
    public void IsEligible_NoUnsupportedClosure_False()
    {
        // A supported closure or no closure means no tombstone needed.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Registry", moduleDecl);
        var method = CreateMethod("plain", classDecl, moduleDecl);
        method.CSSignature.Add(CreateArg("name", new NamedTypeSpec("Swift.String"), moduleDecl));

        Assert.False(ClosureParamTombstoneEmitter.IsEligible(method, typeDatabase));
    }

    [Fact]
    public void IsEligible_MethodOwnGenericParam_False()
    {
        // Method-own generic parameters complicate signature emission and are out of v1 scope.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Registry", moduleDecl);
        var method = CreateMethod("registerT", classDecl, moduleDecl);
        method.GenericParameters.Add(new GenericArgumentDecl(
            TypeName: "τ_1_0",
            SugaredTypeName: "T",
            GenericConformances: new List<GenericParameterConformance>(),
            AssosiatedTypeConformances: new List<GenericParameterConformance>()));
        method.CSSignature.Add(CreateArg("decoder", BuildUnsupportedClosure(), moduleDecl));

        Assert.False(ClosureParamTombstoneEmitter.IsEligible(method, typeDatabase));
    }

    [Fact]
    public void Emit_ClassMethod_WritesObsoleteSB0005AndUnsupportedSwiftType()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Registry", moduleDecl);
        var method = CreateMethod("register", classDecl, moduleDecl);
        method.CSSignature.Add(CreateArg("decoder", BuildUnsupportedClosure(), moduleDecl));
        method.IsClosureParamTombstone = true;

        var output = EmitTombstone(method, typeDatabase);

        Assert.Contains("[global::Swift.UnsupportedSwiftType(\"Unsupported closure fallback\"", output);
        Assert.Contains("DiagnosticId = \"SB0005\"", output);
        Assert.Contains("object? decoder", output);
        Assert.Contains("throw new global::System.NotSupportedException", output);
    }

    [Fact]
    public void Emit_ClassMethod_NotStatic()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Registry", moduleDecl);
        var method = CreateMethod("register", classDecl, moduleDecl);
        method.CSSignature.Add(CreateArg("decoder", BuildUnsupportedClosure(), moduleDecl));
        method.IsClosureParamTombstone = true;

        var output = EmitTombstone(method, typeDatabase);

        Assert.Contains("public void Register", output);
        Assert.DoesNotContain("public static void Register", output);
    }

    [Fact]
    public void Emit_StaticMethod_IsStatic()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Registry", moduleDecl);
        var method = CreateMethod("registerStatic", classDecl, moduleDecl, isStatic: true);
        method.CSSignature.Add(CreateArg("decoder", BuildUnsupportedClosure(), moduleDecl));
        method.IsClosureParamTombstone = true;

        var output = EmitTombstone(method, typeDatabase);

        Assert.Contains("public static void RegisterStatic", output);
    }

    [Fact]
    public void Emit_FreeFunction_IsStatic()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var method = CreateModuleMethod("makeImageDecoder", moduleDecl);
        method.CSSignature.Add(CreateArg("closure", BuildUnsupportedClosure(), moduleDecl));
        method.IsClosureParamTombstone = true;

        var output = EmitTombstone(method, typeDatabase);

        Assert.Contains("public static void MakeImageDecoder", output);
        Assert.Contains("object? closure", output);
    }

    [Fact]
    public void Emit_Constructor_NoSuperclassWritesNoBaseChain()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("DataLoader", moduleDecl);
        var ctor = CreateConstructor(classDecl, moduleDecl);
        ctor.CSSignature.Add(CreateArg("transform", BuildUnsupportedClosure(), moduleDecl));
        ctor.IsClosureParamTombstone = true;

        var output = EmitTombstone(ctor, typeDatabase);

        Assert.Contains("public DataLoader(object? transform)", output);
        Assert.DoesNotContain(": base(default(SwiftInheritanceChain))", output);
        Assert.Contains("throw new global::System.NotSupportedException", output);
    }

    [Fact]
    public void Emit_Constructor_WithSuperclassWritesBaseChain()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var baseClass = CreateClassDecl("LoaderBase", moduleDecl);
        var classDecl = CreateClassDecl("DataLoader", moduleDecl);
        classDecl.ResolvedSuperclass = baseClass;
        var ctor = CreateConstructor(classDecl, moduleDecl);
        ctor.CSSignature.Add(CreateArg("transform", BuildUnsupportedClosure(), moduleDecl));
        ctor.IsClosureParamTombstone = true;

        var output = EmitTombstone(ctor, typeDatabase);

        Assert.Contains("public DataLoader(object? transform) : base(default(SwiftInheritanceChain))", output);
    }

    [Fact]
    public void Emit_MixedParams_NonClosureProjectsToPublicType()
    {
        // A param mix: Swift.String + unsupported closure → string + object?.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Registry", moduleDecl);
        var method = CreateMethod("register", classDecl, moduleDecl);
        method.CSSignature.Add(CreateArg("name", new NamedTypeSpec("Swift.String"), moduleDecl));
        method.CSSignature.Add(CreateArg("decoder", BuildUnsupportedClosure(), moduleDecl));
        method.IsClosureParamTombstone = true;

        var output = EmitTombstone(method, typeDatabase);

        Assert.Contains("string name", output);
        Assert.Contains("object? decoder", output);
    }

    [Fact]
    public void Emit_ThrowMessageContainsExposedForVisibilityHint()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Registry", moduleDecl);
        var method = CreateMethod("register", classDecl, moduleDecl);
        method.CSSignature.Add(CreateArg("decoder", BuildUnsupportedClosure(), moduleDecl));
        method.IsClosureParamTombstone = true;

        var output = EmitTombstone(method, typeDatabase);

        Assert.Contains("exposed for visibility only", output);
        Assert.Contains("cannot be invoked", output);
    }

    [Fact]
    public void Emit_UrlFormatWiresWikiTroubleshooting()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Registry", moduleDecl);
        var method = CreateMethod("register", classDecl, moduleDecl);
        method.CSSignature.Add(CreateArg("decoder", BuildUnsupportedClosure(), moduleDecl));
        method.IsClosureParamTombstone = true;

        var output = EmitTombstone(method, typeDatabase);

        Assert.Contains("UrlFormat = \"https://github.com/justinwojo/swift-dotnet-bindings/wiki/Troubleshooting\"", output);
    }

    [Fact]
    public void GetProjectedCSharpMethodKey_TombstonedOverloadsWithDifferentClosureShapes_CollapseToSameKey()
    {
        // Two overloads `handle(callback: (() -> Void) -> Void)` and
        // `handle(callback: ((Int) -> Void) -> Void)` differ in Swift but both
        // emit `public void Handle(object? callback)` after closure-tombstone
        // routing. Without dedup-key collapsing, distinct projected delegate
        // types produce different keys → both pass dedup → CS0111 in the
        // generated C#. Verify the key collapses unsupported closure params to
        // `object?` when IsClosureParamTombstone is set.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Collision", moduleDecl);

        var voidVoidClosure = BuildUnsupportedClosure();
        var intVoidClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new ClosureTypeSpec(
                    new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("Swift.Int") }),
                    TupleTypeSpec.Empty)
            }),
            TupleTypeSpec.Empty);

        var first = CreateMethod("handle", classDecl, moduleDecl);
        first.CSSignature.Add(CreateArg("callback", voidVoidClosure, moduleDecl));
        first.IsClosureParamTombstone = true;

        var second = CreateMethod("handle", classDecl, moduleDecl);
        second.CSSignature.Add(CreateArg("callback", intVoidClosure, moduleDecl));
        second.IsClosureParamTombstone = true;

        var firstKey = BaseHandler.GetProjectedCSharpMethodKey(first, typeDatabase);
        var secondKey = BaseHandler.GetProjectedCSharpMethodKey(second, typeDatabase);

        Assert.Equal(firstKey, secondKey);
        Assert.Contains("object?", firstKey);
    }

    [Fact]
    public void GetProjectedCSharpMethodKey_TombstoneFlagOff_DoesNotCollapseClosurePArams()
    {
        // Sanity check: without IsClosureParamTombstone, two overloads with
        // different closure shapes still produce distinct keys (the normal
        // dedup behavior). Catches a regression where the tombstone branch
        // accidentally fires for non-tombstoned methods.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("NoTombstone", moduleDecl);

        var voidVoidClosure = BuildUnsupportedClosure();
        var intVoidClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new ClosureTypeSpec(
                    new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("Swift.Int") }),
                    TupleTypeSpec.Empty)
            }),
            TupleTypeSpec.Empty);

        var first = CreateMethod("handle", classDecl, moduleDecl);
        first.CSSignature.Add(CreateArg("callback", voidVoidClosure, moduleDecl));

        var second = CreateMethod("handle", classDecl, moduleDecl);
        second.CSSignature.Add(CreateArg("callback", intVoidClosure, moduleDecl));

        var firstKey = BaseHandler.GetProjectedCSharpMethodKey(first, typeDatabase);
        var secondKey = BaseHandler.GetProjectedCSharpMethodKey(second, typeDatabase);

        Assert.NotEqual(firstKey, secondKey);
    }

    [Fact]
    public void GetProjectedCSharpMethodKey_PrePassTombstoneView_MatchesMainLoopFlagView()
    {
        // PreReserveAdoptedOverrideNames runs BEFORE the main loop sets
        // IsClosureParamTombstone, so it requests the tombstone view via the
        // treatAsClosureTombstone flag. That pre-pass key MUST byte-match the key the
        // main loop computes AFTER setting the field — otherwise a closure-tombstone
        // override's pre-reserved slot keys off the un-collapsed Swift closure shape
        // while the loop dedups on the object?-collapsed shape, silently missing the
        // collision the pre-reservation exists to catch (declaration-order regression).
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Collision", moduleDecl);

        // Pre-pass view: field still false (loop hasn't run), tombstone view requested explicitly.
        var prePass = CreateMethod("handle", classDecl, moduleDecl);
        prePass.CSSignature.Add(CreateArg("callback", BuildUnsupportedClosure(), moduleDecl));
        var prePassKey = BaseHandler.GetProjectedCSharpMethodKey(
            prePass, typeDatabase, treatAsClosureTombstone: true);

        // Main-loop view: field set, default param.
        var mainLoop = CreateMethod("handle", classDecl, moduleDecl);
        mainLoop.CSSignature.Add(CreateArg("callback", BuildUnsupportedClosure(), moduleDecl));
        mainLoop.IsClosureParamTombstone = true;
        var mainLoopKey = BaseHandler.GetProjectedCSharpMethodKey(mainLoop, typeDatabase);

        Assert.Equal(mainLoopKey, prePassKey);
        Assert.Contains("object?", prePassKey);
    }

    [Fact]
    public void ClassifyOverridePrePassEmission_ValidationSkippedSibling_ExcludedFromEmittingPartition()
    {
        // PreReserveAdoptedOverrideNames builds its local projected-key multiset from ONLY the
        // methods that will actually emit (ClassifyOverridePrePassEmission), mirroring the main
        // loop's collision counter (a validation-skipped method `continue`s before the dedup Add).
        // A skipped sibling sharing an override's projected key must therefore NOT inflate the
        // override's count — otherwise the count!=1 gate suppresses a valid adopted-name
        // pre-reservation and an earlier-declared natural sibling steals the slot (CS0111,
        // declaration-order regression). The projected key carries name + param types only (no
        // return type), so a sibling skipped solely for an unsupported RETURN still shares the
        // key — exactly the suppressor shape. Here the skip is modeled with @_spi (a deterministic
        // skip the SPI gate forces regardless of the return type) while the return types
        // deliberately differ, pinning BOTH halves: the keys still collapse, yet the skipped
        // sibling classifies as non-emitting and drops out of the partition.
        var typeDatabase = CreateTypeDatabase();
        var pipeline = new MemberValidationPipeline(typeDatabase);
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Worker", moduleDecl);

        // Emitting: process(_ n: Int32) -> String. One param keeps parameterCount > 0, so the
        // zero-arg "Get" name-shaping can't diverge between the two and confound the key match.
        var emitting = CreateMethod("process", classDecl, moduleDecl);
        emitting.CSSignature[0] = CreateArg("", new NamedTypeSpec("Swift.String"), moduleDecl);
        emitting.CSSignature.Add(CreateArg("n", new NamedTypeSpec("Swift.Int32"), moduleDecl));

        // Same name + param types (=> same projected key) but a DIFFERENT return type AND @_spi.
        var skipped = CreateMethod("process", classDecl, moduleDecl);
        skipped.CSSignature[0] = CreateArg("", new NamedTypeSpec("Swift.Int32"), moduleDecl);
        skipped.CSSignature.Add(CreateArg("n", new NamedTypeSpec("Swift.Int32"), moduleDecl));
        skipped.IsSpiProtected = true;

        // Precondition: differing return types still collapse to one projected key, so the skipped
        // sibling WOULD inflate the count if the emitting partition didn't exclude it.
        Assert.Equal(
            BaseHandler.GetProjectedCSharpMethodKey(emitting, typeDatabase),
            BaseHandler.GetProjectedCSharpMethodKey(skipped, typeDatabase));

        // The fix: the partition keeps the emitter and drops the validation-skipped sibling.
        var emittingClass = BaseHandler.ClassifyOverridePrePassEmission(emitting, pipeline, null!, typeDatabase);
        var skippedClass = BaseHandler.ClassifyOverridePrePassEmission(skipped, pipeline, null!, typeDatabase);

        Assert.True(emittingClass.WillEmit);
        Assert.False(skippedClass.WillEmit);
        Assert.False(skippedClass.IsClosureTombstone);
    }

    // ---- Helpers ----

    /// <summary>
    /// Builds an unsupported closure: <c>((() -&gt; ()) -&gt; ())</c>. Closure-of-closure is rejected
    /// by <see cref="ClosureHandler.IsSupportedClosure"/> at the parameter-of-parameter check.
    /// </summary>
    private static ClosureTypeSpec BuildUnsupportedClosure()
    {
        var innerClosure = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        var args = new TupleTypeSpec(new TypeSpec[] { innerClosure });
        return new ClosureTypeSpec(args, TupleTypeSpec.Empty);
    }

    private static string EmitTombstone(MethodDecl method, TypeDatabase typeDatabase)
    {
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);
        var env = new MethodEnvironment(method, typeDatabase);
        ClosureParamTombstoneEmitter.Emit(csWriter, env);
        return sw.ToString();
    }

    private static ModuleDecl CreateModuleDecl()
    {
        return new ModuleDecl
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
    }

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
    {
        var classDecl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
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
        moduleDecl.Types.Add(classDecl);
        return classDecl;
    }

    private static StructDecl CreateStructDecl(string name, ModuleDecl moduleDecl)
    {
        var structDecl = new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
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
        moduleDecl.Types.Add(structDecl);
        return structDecl;
    }

    private static MethodDecl CreateMethod(
        string name,
        TypeDecl parentDecl,
        ModuleDecl moduleDecl,
        bool isStatic = false)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{parentDecl.Name.Length}{parentDecl.Name}C{name.Length}{name}yyyycF",
            MethodType = isStatic ? MethodType.Static : MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);
        return method;
    }

    private static MethodDecl CreateModuleMethod(string name, ModuleDecl moduleDecl)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name.Length}{name}yyyycF",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        moduleDecl.Methods.Add(method);
        return method;
    }

    private static MethodDecl CreateConstructor(ClassDecl parentDecl, ModuleDecl moduleDecl)
    {
        var ctor = new MethodDecl
        {
            Name = "init",
            MangledName = $"$s10TestModule{parentDecl.Name.Length}{parentDecl.Name}CACycfc",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec($"TestModule.{parentDecl.Name}"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(ctor);
        return ctor;
    }

    private static ArgumentDecl CreateArg(string name, TypeSpec typeSpec, ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            Name = name,
            PrivateName = name,
            SwiftTypeSpec = typeSpec,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDb = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16
            });
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Registry"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Registry"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Registry"),
                MetadataAccessor = "$s10TestModule8RegistryCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.DataLoader"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "DataLoader"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DataLoader"),
                MetadataAccessor = "$s10TestModule10DataLoaderCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Anonymous"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Anonymous"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Anonymous"),
                MetadataAccessor = "$s10TestModule9AnonymousVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(testModule);

        return typeDb;
    }
}
