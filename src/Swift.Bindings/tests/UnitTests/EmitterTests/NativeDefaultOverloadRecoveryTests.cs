// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

[Collection("ReportCollector")]
public class NativeDefaultOverloadRecoveryTests
{
    [Fact]
    public void Producer_AttributesBothNativeAndManagedBundlesToEachTrim()
    {
        var env = CreateEnvironment();
        var (managed, native, cs, swift) = Emit(env);
        Assert.Equal(2, env.EmissionContext!.AbiCallPlans.Count);
        for (int trim = 1; trim <= 2; trim++)
        {
            var candidate = DefaultParameterOverloadEmitter.BuildOverloadDecl(env.EmissionSymbol, env.MethodDecl, trim);
            var id = DeclIdFactory.ForMethod(candidate);
            Assert.NotEqual(env.SourceDeclId, id);
            Assert.Contains(env.EmissionContext.AbiCallPlans, p => p.Owner?.Decl == id);
            Assert.Contains(cs.Fragments.BuildTiling(managed.Length, FragmentOwners.ForDecl(env.MethodDecl)),
                f => f.Owner.Artifact.Decl == id);
            Assert.Contains(swift.Fragments.BuildTiling(native.Length, FragmentOwners.ForDeclWrapper(env.MethodDecl)),
                f => f.Owner.Artifact.Decl == id);
        }
        Assert.Contains("ref Swift.SwiftString.Buffer text", managed);
        Assert.Contains("defer { text.assumingMemoryBound(to: Swift.String.self).pointee = textVal }", native);
        Assert.Contains("_dbw_configure_", native);
        Assert.Contains("@_cdecl", native);
    }

    [Fact]
    public void Producer_OwnDenialPrecedesPreparationAndReservations()
    {
        var env = CreateEnvironment();
        var denied = DefaultParameterOverloadEmitter.BuildOverloadDecl(env.EmissionSymbol, env.MethodDecl, 2);
        var id = DeclIdFactory.ForMethod(denied);
        var key = DefaultParameterOverloadEmitter.GetProjectedOverloadKey(denied, env.TypeDatabase);
        using var attempt = EmissionAttempt.Begin(WrapperDenylistSeed.Build(
            new HashSet<RecoveryUnitId> { RecoveryUnitId.Create(id, RecoveryScope.LeafApi) }));
        // Any preparation seam entered for the denied identity would make the attempt fail.
        using var injection = EmitterFaultInjector.Install(d => d == id ? new InvalidOperationException("denial was late") : null);
        var (managed, native, _, _) = Emit(env);
        Assert.Contains("Unsupported:", managed);
        Assert.DoesNotContain(denied.MangledName, native);
        Assert.DoesNotContain(key, env.EmittedProjectedSignatures!);
        Assert.False(env.ReservedOverloadShapes!.ContainsKey(key));
        Assert.Single(env.EmissionContext!.AbiCallPlans);
        Assert.DoesNotContain(env.EmissionContext.AbiCallPlans, p => p.Owner?.Decl == id);
        Assert.False(attempt.Abandoned);
    }

    [Fact]
    public void Producer_AllDeniedCommentsDoNotReportRecoveredCallable()
    {
        var env = CreateEnvironment();
        var units = Enumerable.Range(1, 2).Select(trim => RecoveryUnitId.Create(
            DeclIdFactory.ForMethod(DefaultParameterOverloadEmitter.BuildOverloadDecl(env.EmissionSymbol, env.MethodDecl, trim)),
            RecoveryScope.LeafApi)).ToHashSet();
        using var attempt = EmissionAttempt.Begin(WrapperDenylistSeed.Build(units));
        var managed = new StringWriter();
        var native = new StringWriter();
        Assert.False(DefaultParameterOverloadEmitter.TryEmitRecoveryOverloads(
            new CSharpWriter(managed), new SwiftWriter(native), env, NullLogger.Instance, env.EmissionContext));
        Assert.Contains("Unsupported:", managed.ToString());
        Assert.Equal(string.Empty, native.ToString());
        Assert.Empty(env.EmittedProjectedSignatures!);
        Assert.Empty(env.EmissionContext!.AbiCallPlans);
        Assert.Empty(env.EmissionContext.CompletedDefaultOverloadRecipes);
    }

    [Fact]
    public void Producer_ContainedCandidateFaultDoesNotPublishRecipeOrBlameOriginal()
    {
        var env = CreateEnvironment();
        var id = DeclIdFactory.ForMethod(DefaultParameterOverloadEmitter.BuildOverloadDecl(env.EmissionSymbol, env.MethodDecl, 2));
        using var attempt = EmissionAttempt.Begin(new EmitterPoisonList());
        using var injection = EmitterFaultInjector.Install(d => d == id ? new InvalidOperationException("candidate fault") : null);
        Assert.Throws<EmissionAttemptAbandoned>(() => Emit(env));
        Assert.True(attempt.Abandoned);
        Assert.True(EmissionAttempt.TryGetFault(id, out _));
        Assert.False(EmissionAttempt.TryGetFault(env.SourceDeclId, out _));
        Assert.Empty(env.EmissionContext!.CompletedDefaultOverloadRecipes);
        Assert.Empty(env.EmittedProjectedSignatures!);
    }

    [Fact]
    public void Producer_SeededRecipePreservesPromotedIdentityAcrossChangedPrimaryRoute()
    {
        var original = CreateEnvironment();
        original.PromoteSymbol("SBW_CapturedPrimary");
        original.DisambiguatedNameInput = "ConfigureCaptured";
        var (beforeCs, beforeSwift, _, _) = Emit(original);
        var recipes = original.EmissionContext!.CompletedDefaultOverloadRecipes;
        var recipe = Assert.Single(recipes).Value;
        var current = CreateEnvironment();
        current.PromoteSymbol("different-primary-route");
        current.DisambiguatedNameInput = "DifferentName";
        current.EmissionContext!.SeedDefaultOverloadRecipes(recipes);
        var (afterCs, afterSwift, _, _) = Emit(current);
        Assert.Equal(beforeCs, afterCs);
        Assert.Equal(beforeSwift, afterSwift);
        Assert.Same(recipe, Assert.Single(current.EmissionContext.CompletedDefaultOverloadRecipes).Value);
    }

    [Fact]
    public void Recipe_PreservesOriginalKeyAndDetachedSignatureNamesAndGenericCollections()
    {
        var env = CreateEnvironment();
        var sourceId = env.SourceDeclId;
        env.MethodDecl.CSSignature.RemoveAt(3); // Models normalization after environment construction.
        env.MethodDecl.GenericParameters.Add(new GenericArgumentDecl("T", "Element", new(), new(), new[] { "T==()" }));
        env.MethodDecl.GenericParameters[0].AssosiatedTypeConformances.Add(
            new GenericParameterConformance(new[] { "T", "Element" }, SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"), ConformanceKind.Protocol));
        env.FailableFactoryName = "TryCreateWithValue";
        env.InitFactoryName = "CreateWithValue";
        env.AdoptedOverrideCSharpName = "Adopted";
        env.MethodDecl.Documentation = new DocComment { Parameters = new() { ["text"] = "Original" }, Remarks = new() { "Remark" } };
        var recipe = DefaultOverloadReplayRecipe.Capture(env);
        env.MethodDecl.CSSignature[1].IsInOut = false;
        env.MethodDecl.GenericParameters[0].GenericConformances.Add(
            new GenericParameterConformance(new[] { "T" }, SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"), ConformanceKind.Protocol));
        env.MethodDecl.GenericParameters[0].AssosiatedTypeConformances[0].Path[1] = "Changed";
        ((string[])env.MethodDecl.GenericParameters[0].UnrepresentableConcreteSameTypePins!)[0] = "Changed";
        env.MethodDecl.CSSignature.Clear();
        env.FailableFactoryName = "Changed";
        env.MethodDecl.Documentation.Parameters["text"] = "Changed";
        env.MethodDecl.Documentation.Remarks.Clear();
        var first = recipe.CreateEnvironment(env);
        Assert.Equal(sourceId, first.SourceDeclId);
        Assert.NotEqual(sourceId, DeclIdFactory.ForMethod(first.MethodDecl));
        Assert.Equal(3, first.MethodDecl.CSSignature.Count);
        Assert.True(first.MethodDecl.CSSignature[1].IsInOut);
        Assert.Equal("TryCreateWithValue", first.FailableFactoryName);
        Assert.Equal("CreateWithValue", first.InitFactoryName);
        Assert.Equal("Adopted", first.AdoptedOverrideCSharpName);
        Assert.Equal("Original", first.MethodDecl.Documentation!.Parameters["text"]);
        Assert.Single(first.MethodDecl.Documentation.Remarks);
        Assert.Empty(first.MethodDecl.GenericParameters[0].GenericConformances);
        Assert.Equal("Element", first.MethodDecl.GenericParameters[0].AssosiatedTypeConformances[0].Path[1]);
        Assert.Equal("T==()", first.MethodDecl.GenericParameters[0].UnrepresentableConcreteSameTypePins![0]);
        Assert.Same(env.EmittedProjectedSignatures, first.EmittedProjectedSignatures);
        Assert.Same(env.ReservedOverloadShapes, first.ReservedOverloadShapes);
        first.MethodDecl.CSSignature[1].CSharpName = "mutated";
        first.MethodDecl.GenericParameters.Clear();
        var second = recipe.CreateEnvironment(env);
        Assert.Null(second.MethodDecl.CSSignature[1].CSharpName);
        Assert.Single(second.MethodDecl.GenericParameters);
        Assert.NotSame(first.MethodDecl, second.MethodDecl);
        Assert.Same(second.MethodDecl, second.MethodDecl.CSSignature[1].ParentDecl);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "This untrimmed unit test inventories every concrete parser TypeSpec so new shapes cannot silently escape replay clone coverage.")]
    public void Recipe_DeeplyDetachesAllConcreteTypeSpecShapesAndParserFacts()
    {
        var concrete = typeof(TypeSpec).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(TypeSpec).IsAssignableFrom(t)).ToHashSet();
        Assert.True(concrete.SetEquals(new[] { typeof(NamedTypeSpec), typeof(ClosureTypeSpec), typeof(TupleTypeSpec),
            typeof(ProtocolListTypeSpec), typeof(AssociatedTypeReferenceSpec) }));
        var named = new NamedTypeSpec("M.Outer") { Usr = "s:OuterV", InnerType = new NamedTypeSpec("Inner") };
        var associated = new AssociatedTypeReferenceSpec("T", "Element");
        var protocols = new ProtocolListTypeSpec { IsOpaque = true };
        protocols.Protocols.Add(new NamedTypeSpec("M.P"), true);
        var closure = new ClosureTypeSpec(new TupleTypeSpec(new TypeSpec[] { named, associated }), protocols)
        { Throws = true, IsAsync = true, IsConventionC = true, IsInOut = true, IsAny = true,
          IsVariadic = true, IsImplicitlyUnwrappedOptional = true, TypeLabel = "kept" };
        var attribute = new TypeSpecAttribute("convention");
        attribute.Parameters.Add("c");
        closure.Attributes.Add(attribute);
        closure.GenericParameters.Add(new NamedTypeSpec("Swift.Int32"));
        var env = CreateEnvironment();
        env.MethodDecl.CSSignature[1].SwiftTypeSpec = closure;
        env.MethodDecl.ThrownErrorType = named;
        var recipe = DefaultOverloadReplayRecipe.Capture(env);
        named.Usr = "changed";
        named.InnerType = null;
        attribute.Parameters[0] = "changed";
        protocols.Protocols.Clear();
        closure.GenericParameters.Clear();
        var rebuilt = recipe.CreateEnvironment(env);
        var copy = Assert.IsType<ClosureTypeSpec>(rebuilt.MethodDecl.CSSignature[1].SwiftTypeSpec);
        Assert.True(copy.Throws && copy.IsAsync && copy.IsConventionC && copy.IsInOut && copy.IsAny);
        Assert.True(copy.IsVariadic && copy.IsImplicitlyUnwrappedOptional);
        Assert.Equal("kept", copy.TypeLabel);
        Assert.Equal("c", Assert.Single(copy.Attributes).Parameters[0]);
        Assert.Single(copy.GenericParameters);
        var tuple = Assert.IsType<TupleTypeSpec>(copy.Arguments);
        var copiedName = Assert.IsType<NamedTypeSpec>(tuple.Elements[0]);
        Assert.Equal("s:OuterV", copiedName.Usr);
        Assert.Equal("Inner", copiedName.InnerType!.Name);
        Assert.Equal("Element", Assert.IsType<AssociatedTypeReferenceSpec>(tuple.Elements[1]).AssociatedTypeName);
        var copiedProtocols = Assert.IsType<ProtocolListTypeSpec>(copy.ReturnType);
        Assert.True(copiedProtocols.IsOpaque);
        Assert.True(Assert.Single(copiedProtocols.Protocols).Value);
        Assert.Equal("s:OuterV", Assert.IsType<NamedTypeSpec>(rebuilt.MethodDecl.ThrownErrorType).Usr);
        copiedName.InnerType!.IsAny = true;
        var again = Assert.IsType<ClosureTypeSpec>(recipe.CreateEnvironment(env).MethodDecl.CSSignature[1].SwiftTypeSpec);
        Assert.False(Assert.IsType<NamedTypeSpec>(Assert.IsType<TupleTypeSpec>(again.Arguments).Elements[0]).InnerType!.IsAny);
    }

    [Fact]
    public void Recipe_UnknownTypeSpecSubclassFailsCaptureInsteadOfAliasing()
    {
        var env = CreateEnvironment();
        env.MethodDecl.CSSignature[1].SwiftTypeSpec = new UnknownNamedSpec();
        Assert.Throws<NotSupportedException>(() => DefaultOverloadReplayRecipe.Capture(env));
    }

    [Fact]
    public void Producer_NoDefaultsDoesNotCaptureUnusedUnknownTypeSpec()
    {
        var env = CreateEnvironment();
        env.MethodDecl.CSSignature[1].SwiftTypeSpec = new UnknownNamedSpec();
        foreach (var arg in env.MethodDecl.CSSignature)
            arg.HasDefaultArg = false;
        var (managed, native, _, _) = Emit(env);
        Assert.Empty(managed);
        Assert.Empty(native);
        Assert.Empty(env.EmissionContext!.CompletedDefaultOverloadRecipes);
    }

    private sealed class UnknownNamedSpec : NamedTypeSpec
    {
        public UnknownNamedSpec() : base("M.Unknown") { }
    }

    private static (string Managed, string Native, CSharpWriter Cs, SwiftWriter Swift) Emit(MethodEnvironment env)
    {
        var csText = new StringWriter();
        var swiftText = new StringWriter();
        var cs = new CSharpWriter(csText);
        var swift = new SwiftWriter(swiftText);
        using (cs.BeginFragment(FragmentOwners.ForDecl(env.MethodDecl)))
        using (swift.BeginFragment(FragmentOwners.ForDeclWrapper(env.MethodDecl)))
            DefaultParameterOverloadEmitter.TryEmitOverloads(cs, swift, env, NullLogger.Instance, env.EmissionContext);
        return (csText.ToString(), swiftText.ToString(), cs, swift);
    }

    private static MethodEnvironment CreateEnvironment()
    {
        var db = new TypeDatabase { AsyncLibraryName = "MWrapper" };
        var swift = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        foreach (var (name, cs, flags) in new[]
        {
            ("Int32", CSharpTypeName.FromNamespaceAndName("System", "Int32"), TypeRecordFlags.Frozen),
            ("String", CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"), TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement),
        })
            swift.RegisterType(SwiftTypeName.FromModuleQualifiedName("Swift." + name), new TypeRecord
            {
                CSharpTypeName = cs, SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift." + name),
                MetadataAccessor = "$s" + name + "Ma", Flags = flags, Kind = TypeRecordKind.Struct,
            });
        db.AddModuleDatabase(swift);
        db.AddModuleDatabase(new ModuleTypeDatabase("M", "/tmp/M.dylib"));
        var module = new ModuleDecl { Name = "M", ParentDecl = null, ModuleDecl = null,
            Properties = new(), Methods = new(), Types = new(), Dependencies = new(), Protocols = new() };
        var method = new MethodDecl { Name = "configure", MangledName = "$s1M9configure_5first6seconds5Int32VSSz_A2DtF",
            MethodType = MethodType.Static, IsConstructor = false, ParentDecl = module, ModuleDecl = module,
            Throws = false, IsAsync = false, GenericParameters = new(), CSSignature = new() };
        foreach (var (name, type, inout, defaulted) in new[]
        {
            ("", "Int32", false, false), ("text", "String", true, false),
            ("first", "Int32", false, true), ("second", "Int32", false, true),
        })
            method.CSSignature.Add(new ArgumentDecl { Name = name, PrivateName = name,
                SwiftTypeSpec = new NamedTypeSpec("Swift." + type), IsInOut = inout, IsGeneric = false,
                HasDefaultArg = defaulted, ParentDecl = method, ModuleDecl = module });
        module.Methods.Add(method);
        return new MethodEnvironment(method, db) { EmissionContext = new ModuleEmissionContext(),
            EmittedProjectedSignatures = new(), ReservedOverloadShapes = new() };
    }
}
