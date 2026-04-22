#nullable enable
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for PInvokeHelperContext and PInvokeDeclaration.
/// </summary>
public class PInvokeHelperEmitterTests
{
    #region CreateIfGeneric Tests

    [Fact]
    public void CreateIfGeneric_NonGenericType_ReturnsNull()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = new StructDecl
        {
            Name = "Point",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
            MangledName = "$s10TestModule5PointVN",
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
            MetadataAccessor = "$s10TestModule5PointVMa"
        };

        var result = PInvokeHelperContext.CreateIfGeneric(structDecl);

        Assert.Null(result);
    }

    [Fact]
    public void CreateIfGeneric_GenericType_ReturnsContext()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = new StructDecl
        {
            Name = "Container",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Container"),
            MangledName = "$s10TestModule9ContainerVN",
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
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule9ContainerVMa"
        };

        var result = PInvokeHelperContext.CreateIfGeneric(structDecl);

        Assert.NotNull(result);
        Assert.Equal("Container_PInvoke", result.HelperClassName);
        Assert.Single(result.GenericTypeParameters);
        Assert.Equal("T", result.GenericTypeParameters[0]);
    }

    [Fact]
    public void CreateIfGeneric_TwoTypeParams_HasT0T1()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = new StructDecl
        {
            Name = "Pair",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Pair"),
            MangledName = "$s10TestModule4PairVN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "A", new List<GenericParameterConformance>(), new List<GenericParameterConformance>()),
                new("τ_0_1", "B", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
            },
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule4PairVMa"
        };

        var result = PInvokeHelperContext.CreateIfGeneric(structDecl);

        Assert.NotNull(result);
        Assert.Equal(2, result.GenericTypeParameters.Count);
        Assert.Equal("A", result.GenericTypeParameters[0]);
        Assert.Equal("B", result.GenericTypeParameters[1]);
    }

    #endregion

    #region GetQualifiedTypeName Tests (via HelperClassName)

    [Fact]
    public void GetQualifiedTypeName_SimpleType_ReturnsName()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateGenericStructDecl("Simple", moduleDecl, null);

        var result = PInvokeHelperContext.CreateIfGeneric(structDecl);

        Assert.NotNull(result);
        Assert.Equal("Simple_PInvoke", result.HelperClassName);
    }

    [Fact]
    public void GetQualifiedTypeName_NestedType_ReturnsParent_Child()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = new StructDecl
        {
            Name = "Outer",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Outer"),
            MangledName = "$s10TestModule5OuterVN",
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
            MetadataAccessor = "$s10TestModule5OuterVMa"
        };

        var childDecl = CreateGenericStructDecl("Inner", moduleDecl, parentDecl);

        var result = PInvokeHelperContext.CreateIfGeneric(childDecl);

        Assert.NotNull(result);
        Assert.Equal("Outer_Inner_PInvoke", result.HelperClassName);
    }

    #endregion

    #region AddDeclaration Tests

    [Fact]
    public void AddDeclaration_Unique_AddsToList()
    {
        var context = new PInvokeHelperContext("MyType", new[] { "T0" });
        var decl = new PInvokeDeclaration
        {
            LibraryPath = "/tmp/lib.dylib",
            EntryPoint = "$sTest",
            MethodName = "PInvoke_doWork",
            ReturnType = "void",
            ParametersString = "IntPtr self",
            IsAsync = false
        };

        context.AddDeclaration(decl);

        Assert.Single(context.Declarations);
    }

    [Fact]
    public void AddDeclaration_DuplicateMethodName_Deduplicates()
    {
        var context = new PInvokeHelperContext("MyType", new[] { "T0" });
        var decl1 = new PInvokeDeclaration
        {
            LibraryPath = "/tmp/lib.dylib",
            EntryPoint = "$sTest1",
            MethodName = "PInvoke_getMetadata",
            ReturnType = "TypeMetadata",
            ParametersString = "",
            IsAsync = false
        };
        var decl2 = new PInvokeDeclaration
        {
            LibraryPath = "/tmp/lib.dylib",
            EntryPoint = "$sTest2",
            MethodName = "PInvoke_getMetadata",
            ReturnType = "TypeMetadata",
            ParametersString = "",
            IsAsync = false
        };

        context.AddDeclaration(decl1);
        context.AddDeclaration(decl2);

        Assert.Single(context.Declarations);
    }

    [Fact]
    public void AddDeclaration_PerEnumSuffixedNames_NotDeduped()
    {
        // Verifies that per-enum suffixed method names (e.g., PInvoke_CaseByIndex_StatusA
        // vs PInvoke_CaseByIndex_StatusB) are NOT deduped when multiple string enums
        // share the same PInvokeHelperContext (nested in same generic parent).
        var context = new PInvokeHelperContext("GenericParent", new[] { "T0" });
        var declA = new PInvokeDeclaration
        {
            LibraryPath = "/tmp/wrapper.dylib",
            EntryPoint = "SBW_TestModule_StatusA_CaseByIndex",
            MethodName = "PInvoke_CaseByIndex_TestModule_StatusA",
            ReturnType = "IntPtr",
            ParametersString = "nint index",
            IsAsync = false
        };
        var declB = new PInvokeDeclaration
        {
            LibraryPath = "/tmp/wrapper.dylib",
            EntryPoint = "SBW_TestModule_StatusB_CaseByIndex",
            MethodName = "PInvoke_CaseByIndex_TestModule_StatusB",
            ReturnType = "IntPtr",
            ParametersString = "nint index",
            IsAsync = false
        };

        context.AddDeclaration(declA);
        context.AddDeclaration(declB);

        Assert.Equal(2, context.Declarations.Count);
        Assert.Equal("SBW_TestModule_StatusA_CaseByIndex", context.Declarations[0].EntryPoint);
        Assert.Equal("SBW_TestModule_StatusB_CaseByIndex", context.Declarations[1].EntryPoint);
    }

    [Fact]
    public void AddDeclaration_SameLeafNameDifferentPaths_NotDeduped()
    {
        // Verifies that same-named enums under different nested paths
        // (e.g., Outer.Foo.Status vs Outer.Bar.Status) get unique method names
        // via module-qualified suffix and are NOT deduped.
        var context = new PInvokeHelperContext("GenericParent", new[] { "T0" });
        var declFoo = new PInvokeDeclaration
        {
            LibraryPath = "/tmp/wrapper.dylib",
            EntryPoint = "SBW_Mod_Outer_Foo_Status_CaseByIndex",
            MethodName = "PInvoke_CaseByIndex_Mod_Outer_Foo_Status",
            ReturnType = "IntPtr",
            ParametersString = "nint index",
            IsAsync = false
        };
        var declBar = new PInvokeDeclaration
        {
            LibraryPath = "/tmp/wrapper.dylib",
            EntryPoint = "SBW_Mod_Outer_Bar_Status_CaseByIndex",
            MethodName = "PInvoke_CaseByIndex_Mod_Outer_Bar_Status",
            ReturnType = "IntPtr",
            ParametersString = "nint index",
            IsAsync = false
        };

        context.AddDeclaration(declFoo);
        context.AddDeclaration(declBar);

        Assert.Equal(2, context.Declarations.Count);
        Assert.Equal("SBW_Mod_Outer_Foo_Status_CaseByIndex", context.Declarations[0].EntryPoint);
        Assert.Equal("SBW_Mod_Outer_Bar_Status_CaseByIndex", context.Declarations[1].EntryPoint);
    }

    #endregion

    #region EmitHelperClass Tests

    [Fact]
    public void EmitHelperClass_EmitsPartialClass()
    {
        var context = new PInvokeHelperContext("MyType", new[] { "T0" });
        context.AddDeclaration(new PInvokeDeclaration
        {
            LibraryPath = "/tmp/lib.dylib",
            EntryPoint = "$sTest",
            MethodName = "PInvoke_doWork",
            ReturnType = "void",
            ParametersString = "IntPtr self",
            IsAsync = false
        });

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        context.EmitHelperClass(csWriter);

        var result = output.ToString();
        Assert.Contains("internal static unsafe partial class MyType_PInvoke", result);
    }

    [Fact]
    public void EmitHelperClass_EmitsLibraryImportDeclarations()
    {
        var context = new PInvokeHelperContext("MyType", new[] { "T0" });
        context.AddDeclaration(new PInvokeDeclaration
        {
            LibraryPath = "/tmp/lib.dylib",
            EntryPoint = "$sTestEntryPoint",
            MethodName = "PInvoke_doWork",
            ReturnType = "void",
            ParametersString = "IntPtr self",
            IsAsync = false
        });

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        context.EmitHelperClass(csWriter);

        var result = output.ToString();
        Assert.Contains("[LibraryImport(\"/tmp/lib.dylib\", EntryPoint = \"$sTestEntryPoint\")]", result);
        Assert.Contains("internal static partial void PInvoke_doWork(IntPtr self);", result);
    }

    #endregion

    #region PInvokeDeclaration.Emit Tests

    [Fact]
    public void PInvokeDeclaration_Emit_BoolReturn_AddsMarshalAs()
    {
        var decl = new PInvokeDeclaration
        {
            LibraryPath = "/tmp/lib.dylib",
            EntryPoint = "$sTest",
            MethodName = "PInvoke_isValid",
            ReturnType = "bool",
            ParametersString = "",
            IsAsync = false
        };

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        decl.Emit(csWriter);

        var result = output.ToString();
        Assert.Contains("[return: MarshalAs(UnmanagedType.U1)]", result);
        Assert.Contains("internal static partial bool PInvoke_isValid();", result);
    }

    [Fact]
    public void PInvokeDeclaration_Emit_AsyncMethod_ReturnsVoid()
    {
        var decl = new PInvokeDeclaration
        {
            LibraryPath = "/tmp/lib.dylib",
            EntryPoint = "$sTest",
            MethodName = "PInvoke_load",
            ReturnType = "Int64",
            ParametersString = "void* callback",
            IsAsync = true
        };

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        decl.Emit(csWriter);

        var result = output.ToString();
        Assert.Contains("internal static partial void PInvoke_load(void* callback);", result);
        Assert.DoesNotContain("Int64", result);
    }

    [Fact]
    public void PInvokeDeclaration_Emit_WithMetadataParams_AppendsToSignature()
    {
        var decl = new PInvokeDeclaration
        {
            LibraryPath = "/tmp/lib.dylib",
            EntryPoint = "$sTest",
            MethodName = "PInvoke_doWork",
            ReturnType = "void",
            ParametersString = "IntPtr self",
            IsAsync = false,
            MetadataParameters = new[] { "TypeMetadata t0Metadata" }
        };

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        decl.Emit(csWriter);

        var result = output.ToString();
        Assert.Contains("PInvoke_doWork(IntPtr self, TypeMetadata t0Metadata);", result);
    }

    [Fact]
    public void PInvokeDeclaration_Emit_GenericMetadataAccessor_HasMetadataRequestParameter()
    {
        // Generic type metadata accessor P/Invokes must have TypeMetadataRequest as first
        // parameter per Swift ABI: (MetadataRequest, T_metadata...) -> MetadataResponse.
        // Without this, TypeMetadata lands in x0 (where Swift expects MetadataRequest)
        // causing Mono JIT crashes when the metadata cache misses.
        var decl = new PInvokeDeclaration
        {
            LibraryPath = "/tmp/lib.dylib",
            EntryPoint = "$s10TestModule7WrapperVMa",
            MethodName = "PInvoke_getMetadata",
            ReturnType = "TypeMetadata",
            ParametersString = "TypeMetadataRequest request",
            IsAsync = false,
            MetadataParameters = new[] { "TypeMetadata tMetadata" }
        };

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        decl.Emit(csWriter);

        var result = output.ToString();
        // Verify TypeMetadataRequest is the first parameter
        Assert.Contains("PInvoke_getMetadata(TypeMetadataRequest request, TypeMetadata tMetadata);", result);
    }

    #endregion

    #region Helper Methods

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

    private static StructDecl CreateGenericStructDecl(string name, ModuleDecl moduleDecl, TypeDecl? parentDecl)
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
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
            ParentDecl = (BaseDecl?)parentDecl ?? moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa"
        };
    }

    #endregion

    #region Constrained-generic metadata accessor tests
    // These tests cover the conformance pre-flattening introduced in
    // src/docs/constrained-generic-metadata-witness-tables.md. They run against
    // every type-decl shape that the four type handlers feed into
    // PInvokeHelperContext.CreateIfGeneric(decl, typeDb): generic enum, generic
    // frozen struct, generic non-frozen struct, generic class. Test names use
    // the form `Emit_Generic{Kind}_*` so each handler path is exercised at least
    // once for the most important behaviours (single resolvable, single
    // self-requirement, lex order, dedup, skip-gate).

    [Fact]
    public void Emit_GenericEnum_SingleResolvableUserConstraint_EmitsResolvableArg()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateConstrainedGenericEnum(
            moduleDecl,
            "Box",
            constraints: new[] { ("TestModule", "Describable") });
        var typeDb = new ConstrainedGenericMockTypeDatabase()
            .WithProtocol("TestModule", "Describable", "$s10TestModule11DescribableMp");

        var ctx = PInvokeHelperContext.CreateIfGeneric(enumDecl, typeDb)!;

        Assert.False(ctx.ExceedsRegisterArgumentThreshold);
        Assert.Single(ctx.PwtEntries);
        var entry = ctx.PwtEntries[0];
        Assert.True(entry.IsResolvable);
        Assert.Equal("IDescribable", entry.ResolvableInterfaceName);

        var parameters = ctx.GetTypeMetadataAccessorParameterDeclarations();
        Assert.Equal(2, parameters.Count);
        Assert.Equal("IntPtr tMetadata", parameters[0]);
        Assert.Equal("IntPtr tDescribablePWT", parameters[1]);

        var args = ctx.GetTypeMetadataAccessorArgumentList();
        Assert.Equal("SwiftObjectHelper<T>.GetTypeMetadata().Handle", args[0]);
        Assert.Equal(
            "ProtocolWitnessTable.GetOrThrowAuto<T, IDescribable>().Handle",
            args[1]);
    }

    [Fact]
    public void Emit_GenericFrozenStruct_SingleResolvableSwiftStdlibConstraint_UsesISwiftPrefix()
    {
        // Hashable is a Swift stdlib protocol — NameProvider.GetInterfaceName
        // applies the ISwift prefix only when moduleName == "Swift". This test
        // pins the ISwift mapping so future NameProvider edits don't silently
        // re-route Hashable through the user-protocol path.
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateConstrainedGenericFrozenStruct(
            moduleDecl,
            "Cache",
            constraints: new[] { ("Swift", "Hashable") });
        var typeDb = new ConstrainedGenericMockTypeDatabase()
            .WithProtocol("Swift", "Hashable", "$ss8HashableMp");

        var ctx = PInvokeHelperContext.CreateIfGeneric(structDecl, typeDb)!;

        Assert.Single(ctx.PwtEntries);
        Assert.Equal("ISwiftHashable", ctx.PwtEntries[0].ResolvableInterfaceName);

        var args = ctx.GetTypeMetadataAccessorArgumentList();
        Assert.Equal(
            "ProtocolWitnessTable.GetOrThrowAuto<T, ISwiftHashable>().Handle",
            args[1]);
    }

    [Fact]
    public void Emit_GenericNonFrozenStruct_MultipleConstraintsLexOrder_OrderedAlphabetically()
    {
        // runtime-metadata.md: PWTs for a single generic param are emitted in
        // lexicographic order of the protocol's module-qualified name. We
        // intentionally feed the conformances in REVERSE alphabetical order so
        // any test failure is unambiguous (the natural list order would otherwise
        // also pass).
        //
        // Keep the total at 1 metadata + 2 PWT = 3 args (AT the register threshold,
        // not exceeding) so PwtEntries is not cleared by the legacy fallback.
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateConstrainedGenericNonFrozenStruct(
            moduleDecl,
            "Holder",
            constraints: new[]
            {
                ("TestModule", "Zeta"),
                ("TestModule", "Alpha"),
            });
        var typeDb = new ConstrainedGenericMockTypeDatabase()
            .WithProtocol("TestModule", "Zeta", "$s10TestModule4ZetaMp")
            .WithProtocol("TestModule", "Alpha", "$s10TestModule5AlphaMp");

        var ctx = PInvokeHelperContext.CreateIfGeneric(structDecl, typeDb)!;

        Assert.False(ctx.ExceedsRegisterArgumentThreshold);
        Assert.Equal(2, ctx.PwtEntries.Count);
        Assert.Equal(
            new[] { "Alpha", "Zeta" },
            ctx.PwtEntries.Select(e => e.ProtocolName).ToArray());
    }

    [Fact]
    public void Emit_GenericClass_MultipleParamsAndConstraints_FollowsRuntimeMetadataOrdering()
    {
        // runtime-metadata.md ordering: type metadata for every generic param
        // first (declaration order), THEN PWT args grouped by generic param,
        // sorted lex by protocol module-qualified name within each param.
        // This fixture uses 2 params × 3 total conformances = 2 metadata + 3 PWT
        // = 5 args → exceeds the register threshold, routes through buffer mode
        // in AddMetadataAccessorDeclaration. PwtEntries stay populated so the
        // buffer-mode wrapper can produce the thin-mode parameter shape.
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateConstrainedGenericClass(
            moduleDecl,
            "Pair",
            paramConstraints: new[]
            {
                new[] { ("TestModule", "Beta"), ("TestModule", "Alpha") },
                new[] { ("TestModule", "Carrier") },
            });
        var typeDb = new ConstrainedGenericMockTypeDatabase()
            .WithProtocol("TestModule", "Beta", "$s10TestModule4BetaMp")
            .WithProtocol("TestModule", "Alpha", "$s10TestModule5AlphaMp")
            .WithProtocol("TestModule", "Carrier", "$s10TestModule7CarrierMp");

        var ctx = PInvokeHelperContext.CreateIfGeneric(classDecl, typeDb)!;

        Assert.True(ctx.ExceedsRegisterArgumentThreshold);
        // Entries are retained in buffer mode so AddMetadataAccessorDeclaration
        // can synthesize the buffer packing wrapper with matching param names.
        Assert.Equal(3, ctx.PwtEntries.Count);
        // Per-param lex ordering by module-qualified name:
        // Param T0: (Alpha, Beta), Param T1: (Carrier)
        Assert.Equal(new[] { "Alpha", "Beta", "Carrier" },
            ctx.PwtEntries.Select(e => e.ProtocolName).ToArray());
    }

    [Fact]
    public void Emit_GenericClass_UnderThresholdConstraintsArePreservedInOrder()
    {
        // Same as above but with a single conformance per param so total is
        // 2 metadata + 2 PWT = 4 → still exceeds. Use 1 metadata + 2 PWT
        // (single param, two conformances) to stay UNDER threshold and
        // observe the lex-ordering by ProtocolModuleQualifiedName.
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateConstrainedGenericEnum(
            moduleDecl,
            "Box",
            constraints: new[] { ("TestModule", "Beta"), ("TestModule", "Alpha") });
        var typeDb = new ConstrainedGenericMockTypeDatabase()
            .WithProtocol("TestModule", "Beta", "$s10TestModule4BetaMp")
            .WithProtocol("TestModule", "Alpha", "$s10TestModule5AlphaMp");

        var ctx = PInvokeHelperContext.CreateIfGeneric(enumDecl, typeDb)!;

        // 1 metadata + 2 PWT = 3 args → at the threshold (NOT exceeding).
        Assert.False(ctx.ExceedsRegisterArgumentThreshold);
        Assert.Equal(2, ctx.PwtEntries.Count);
        Assert.Equal("Alpha", ctx.PwtEntries[0].ProtocolName);
        Assert.Equal("Beta", ctx.PwtEntries[1].ProtocolName);
    }

    [Fact]
    public void Emit_GenericEnum_SelfRequirementConstraint_UsesDynamicHelper()
    {
        // Protocols with HasSelfRequirement (or HasAssociatedTypes) cannot be
        // expressed as a static C# interface bound. The pre-flattener must mark
        // them as unresolvable and emit a runtime descriptor + witness-table
        // helper into the helper class.
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateConstrainedGenericEnum(
            moduleDecl,
            "Wrapper",
            constraints: new[] { ("TestModule", "AnyInterpolatable") });
        var typeDb = new ConstrainedGenericMockTypeDatabase()
            .WithProtocol(
                "TestModule",
                "AnyInterpolatable",
                "$s10TestModule17AnyInterpolatableMp",
                flags: TypeRecordFlags.HasSelfRequirement);

        var ctx = PInvokeHelperContext.CreateIfGeneric(enumDecl, typeDb)!;

        Assert.Single(ctx.PwtEntries);
        var entry = ctx.PwtEntries[0];
        Assert.False(entry.IsResolvable);
        Assert.Equal("$s10TestModule17AnyInterpolatableMp", entry.DescriptorSymbol);
        Assert.Equal("/tmp/TestModule.dylib", entry.LibraryPath);

        // Triggers EmitDynamicPwtHelperIfNeeded — the call site expression must
        // route through the dynamic helper method on the P/Invoke class.
        var args = ctx.GetTypeMetadataAccessorArgumentList();
        Assert.Equal(2, args.Count);
        Assert.Equal(
            "Wrapper_PInvoke.GetAnyInterpolatablePWT(SwiftObjectHelper<T>.GetTypeMetadata()).Handle",
            args[1]);

        // The helper class must contain exactly one cached descriptor + one cache
        // + one accessor method.
        Assert.Single(ctx.RawCodeBlocks);
        var block = ctx.RawCodeBlocks[0];
        Assert.Contains("_anyInterpolatableDescriptor", block);
        Assert.Contains("_anyInterpolatableWitnessTableCache", block);
        Assert.Contains("GetAnyInterpolatablePWT", block);
        Assert.Contains("$s10TestModule17AnyInterpolatableMp", block);
        Assert.Contains("/tmp/TestModule.dylib", block);
    }

    [Fact]
    public void Emit_GenericEnum_TwoSameNamedProtocolsFromDifferentModules_EmitsDistinctIdentifiers()
    {
        // Regression: when two protocols with the same simple name come from
        // different modules (e.g. ModuleA.Syncable + ModuleB.Syncable on the
        // same constrained-generic type — Swift permits `<T: A.Syncable & B.Syncable>`),
        // the emitted PWT parameter names, helper field names, and dynamic
        // accessor method names must be distinct or the generated C# fails to
        // compile with duplicate-member errors. The discriminator only kicks in
        // when there's an actual collision so the unique-name case keeps stable
        // identifiers.
        //
        // Use the single-generic-param shape so the total arg count stays under
        // the register-passing threshold (1 metadata + 2 PWT = 3, at the limit).
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateConstrainedGenericEnum(
            moduleDecl,
            "Bridge",
            constraints: new[] { ("ModuleA", "Syncable"), ("ModuleB", "Syncable") });
        var typeDb = new ConstrainedGenericMockTypeDatabase()
            .WithProtocol(
                "ModuleA",
                "Syncable",
                "$s7ModuleA8SyncableMp",
                flags: TypeRecordFlags.HasSelfRequirement)
            .WithProtocol(
                "ModuleB",
                "Syncable",
                "$s7ModuleB8SyncableMp",
                flags: TypeRecordFlags.HasSelfRequirement);

        var ctx = PInvokeHelperContext.CreateIfGeneric(enumDecl, typeDb)!;

        Assert.Equal(2, ctx.PwtEntries.Count);
        Assert.False(ctx.ExceedsRegisterArgumentThreshold);

        var parameters = ctx.GetTypeMetadataAccessorParameterDeclarations();
        // 1 metadata + 2 PWT = 3 entries, with both PWT param names containing
        // a discriminator suffix so they don't collide on `Syncable`.
        Assert.Equal(3, parameters.Count);
        var pwtParams = parameters.Skip(1).ToList();
        Assert.Equal(2, pwtParams.Distinct().Count());
        Assert.All(pwtParams, p => Assert.Contains("Syncable", p));
        Assert.All(pwtParams, p => Assert.Contains("PWT", p));

        // Trigger helper emission for both call-site args. Two distinct
        // descriptor + accessor blocks must be produced (one per protocol).
        var args = ctx.GetTypeMetadataAccessorArgumentList();
        Assert.Equal(3, args.Count);
        Assert.Equal(2, ctx.RawCodeBlocks.Count);

        // The two helper accessor methods must have distinct names so the
        // generated helper class compiles.
        var helperMethodLines = ctx.RawCodeBlocks
            .Select(b => b.Split('\n').First(line => line.Contains("ProtocolWitnessTable Get")))
            .ToList();
        Assert.Equal(2, helperMethodLines.Count);
        Assert.NotEqual(helperMethodLines[0], helperMethodLines[1]);

        // Same for the descriptor and cache fields.
        var descriptorFieldLines = ctx.RawCodeBlocks
            .Select(b => b.Split('\n').First(line => line.Contains("Descriptor =")))
            .ToList();
        Assert.Equal(2, descriptorFieldLines.Count);
        Assert.NotEqual(descriptorFieldLines[0], descriptorFieldLines[1]);
    }

    [Fact]
    public void Emit_GenericEnum_DescriptorCacheDeduplication_OneHelperPerProtocol()
    {
        // When the same unresolvable protocol descriptor appears in multiple
        // PwtEntries (e.g. via two distinct generic params constrained on the
        // same protocol), only ONE descriptor field + cache + accessor should
        // be emitted into RawCodeBlocks (deduped by lib + symbol).
        //
        // The natural shape — `Foo<T: AnyInterpolatable, U: AnyInterpolatable>` —
        // has 2 metadata + 2 PWT = 4 args which exceeds the register-passing
        // threshold and causes the legacy fallback to clear PwtEntries before
        // the dedup logic ever runs. Construct a context directly so the
        // dedup behaviour can be observed in isolation.
        var entries = new List<HelperPwtEntry>
        {
            new HelperPwtEntry(
                GenericParamIndex: 0,
                GenericParamCsName: "T",
                ProtocolName: "AnyInterpolatable",
                ProtocolModuleQualifiedName: "TestModule.AnyInterpolatable",
                IsResolvable: false,
                ResolvableInterfaceName: null,
                DescriptorSymbol: "$s10TestModule17AnyInterpolatableMp",
                LibraryPath: "/tmp/TestModule.dylib"),
            new HelperPwtEntry(
                GenericParamIndex: 1,
                GenericParamCsName: "U",
                ProtocolName: "AnyInterpolatable",
                ProtocolModuleQualifiedName: "TestModule.AnyInterpolatable",
                IsResolvable: false,
                ResolvableInterfaceName: null,
                DescriptorSymbol: "$s10TestModule17AnyInterpolatableMp",
                LibraryPath: "/tmp/TestModule.dylib"),
        };
        var ctx = new PInvokeHelperContext(
            typeName: "DualWrapper",
            genericTypeParameters: new[] { "T", "U" },
            pwtEntries: entries,
            exceedsRegisterThreshold: false);

        Assert.Equal(2, ctx.PwtEntries.Count);

        // Triggers helper emission for each call-site arg.
        var args = ctx.GetTypeMetadataAccessorArgumentList();
        Assert.Equal(4, args.Count);

        // Even though two PWT call-site args were generated, the dynamic helper
        // class should contain a SINGLE descriptor/cache/accessor block.
        Assert.Single(ctx.RawCodeBlocks);
    }

    [Fact]
    public void Emit_GenericEnum_MarkerProtocolConstraint_DoesNotAddPwtArg()
    {
        // Marker protocols (Sendable/Copyable/Escapable/...) carry no runtime
        // witness table — the Swift compiler does not pass a PWT arg to the
        // metadata accessor. The pre-flattener must filter them out so the
        // emitted P/Invoke signature matches Swift's ABI.
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateConstrainedGenericEnum(
            moduleDecl,
            "Bag",
            constraints: new[] { ("Swift", "Sendable") });
        // Sendable doesn't even need to be in the type DB — the marker filter
        // runs before TryGetTypeRecord — but we add it anyway to mirror reality.
        var typeDb = new ConstrainedGenericMockTypeDatabase()
            .WithProtocol("Swift", "Sendable", "$ss8SendableMp");

        var ctx = PInvokeHelperContext.CreateIfGeneric(enumDecl, typeDb)!;

        Assert.Empty(ctx.PwtEntries);
        Assert.False(ctx.ExceedsRegisterArgumentThreshold);

        var parameters = ctx.GetTypeMetadataAccessorParameterDeclarations();
        Assert.Single(parameters);
        Assert.Equal("IntPtr tMetadata", parameters[0]);
    }

    [Fact]
    public void Emit_GenericEnum_UnknownProtocolConstraint_RecordedAsUnresolved()
    {
        // A constraint protocol the type database has never heard of cannot be
        // lowered to a HelperPwtEntry, but Swift's metadata accessor still
        // expects a PWT slot at that position. Recording the constraint in
        // UnresolvedPwtConstraints lets TypeMetadataAccessorSkipGate refuse
        // the type instead of undercounting PWT args and picking the wrong ABI.
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateConstrainedGenericEnum(
            moduleDecl,
            "Mystery",
            constraints: new[] { ("OtherModule", "GhostProtocol") });
        var typeDb = new ConstrainedGenericMockTypeDatabase(); // empty

        var ctx = PInvokeHelperContext.CreateIfGeneric(enumDecl, typeDb)!;

        Assert.Empty(ctx.PwtEntries);
        var unresolved = Assert.Single(ctx.UnresolvedPwtConstraints);
        Assert.Equal("GhostProtocol", unresolved.ProtocolName);
        Assert.Equal("OtherModule.GhostProtocol", unresolved.ProtocolModuleQualifiedName);
        Assert.True(ctx.HasIndeterminatePwtShape);
    }

    [Fact]
    public void Emit_GenericEnum_ProtocolWithoutDescriptorSymbol_RecordedAsUnresolved()
    {
        // A PAT/Self-requirement protocol whose descriptor symbol the parser
        // did not capture cannot be lowered to a runtime witness-table lookup.
        // Previously silently dropped; now recorded so the skip gate fails closed.
        var moduleDecl = CreateModuleDecl("TestModule");
        var enumDecl = CreateConstrainedGenericEnum(
            moduleDecl,
            "Broken",
            constraints: new[] { ("TestModule", "OpaqueProto") });
        var typeDb = new ConstrainedGenericMockTypeDatabase()
            .WithProtocol(
                "TestModule",
                "OpaqueProto",
                descriptorSymbol: null,
                flags: TypeRecordFlags.HasSelfRequirement);

        var ctx = PInvokeHelperContext.CreateIfGeneric(enumDecl, typeDb)!;

        Assert.Empty(ctx.PwtEntries);
        var unresolved = Assert.Single(ctx.UnresolvedPwtConstraints);
        Assert.Equal("OpaqueProto", unresolved.ProtocolName);
        Assert.Contains("descriptor symbol", unresolved.Reason);
    }

    [Fact]
    public void Emit_GenericEnum_UnresolvedPushesAccessorOverThreshold()
    {
        // 2 generic params each with a resolvable conformance + one unresolvable
        // conformance on param 1 = 2 metadata + 2 known-PWT + 1 unresolved-PWT = 5.
        // Swift's real ABI switches to the indirect-buffer variant; the threshold
        // flag MUST reflect that real count, not just the knowable subset.
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateConstrainedGenericClass(
            moduleDecl,
            "Triple",
            paramConstraints: new[]
            {
                new[] { ("TestModule", "Known1"), ("OtherModule", "Unknown") },
                new[] { ("TestModule", "Known2") },
            });
        var typeDb = new ConstrainedGenericMockTypeDatabase()
            .WithProtocol("TestModule", "Known1", "$s10TestModule6Known1Mp")
            .WithProtocol("TestModule", "Known2", "$s10TestModule6Known2Mp");

        var ctx = PInvokeHelperContext.CreateIfGeneric(classDecl, typeDb)!;

        Assert.Single(ctx.UnresolvedPwtConstraints);
        Assert.True(ctx.HasIndeterminatePwtShape);
        Assert.True(ctx.ExceedsRegisterArgumentThreshold);
    }

    [Fact]
    public void FlattenConformances_StdlibProtocolsIncrementallyRegistered_UnresolvedCountDecrementsToZero()
    {
        // Regression scaffold for WeatherKit Forecast<TElement>: TElement carried
        // conformances to Swift.Equatable (Self-requirement, descriptor $sSQMp) and
        // Swift.Decodable / Swift.Encodable (associated-type) that historically were
        // absent from SwiftDatabase.xml. Each missing entry produced an
        // UnresolvedPwtConstraint and tombstoned Forecast<T>. Registering the three
        // protocols must walk the unresolved count down 3 → 2 → 1 → 0 without any
        // other surface changes.
        var moduleDecl = CreateModuleDecl("TestModule");
        var constraints = new[]
        {
            ("Swift", "Equatable"),
            ("Swift", "Decodable"),
            ("Swift", "Encodable"),
        };

        var emptyDb = new ConstrainedGenericMockTypeDatabase();
        var ctx0 = PInvokeHelperContext.CreateIfGeneric(
            CreateConstrainedGenericFrozenStruct(moduleDecl, "Forecast", constraints),
            emptyDb)!;
        Assert.Equal(3, ctx0.UnresolvedPwtConstraints.Count);
        Assert.True(ctx0.HasIndeterminatePwtShape);

        var dbEq = new ConstrainedGenericMockTypeDatabase()
            .WithProtocol("Swift", "Equatable", "$sSQMp", TypeRecordFlags.HasSelfRequirement);
        var ctx1 = PInvokeHelperContext.CreateIfGeneric(
            CreateConstrainedGenericFrozenStruct(moduleDecl, "Forecast", constraints),
            dbEq)!;
        Assert.Equal(2, ctx1.UnresolvedPwtConstraints.Count);
        Assert.DoesNotContain(
            ctx1.UnresolvedPwtConstraints,
            u => u.ProtocolName == "Equatable");

        var dbEqDec = new ConstrainedGenericMockTypeDatabase()
            .WithProtocol("Swift", "Equatable", "$sSQMp", TypeRecordFlags.HasSelfRequirement)
            .WithProtocol("Swift", "Decodable", "$ss9DecodableMp", TypeRecordFlags.HasAssociatedTypes);
        var ctx2 = PInvokeHelperContext.CreateIfGeneric(
            CreateConstrainedGenericFrozenStruct(moduleDecl, "Forecast", constraints),
            dbEqDec)!;
        Assert.Single(ctx2.UnresolvedPwtConstraints);
        Assert.Equal("Encodable", ctx2.UnresolvedPwtConstraints[0].ProtocolName);

        var dbAll = new ConstrainedGenericMockTypeDatabase()
            .WithProtocol("Swift", "Equatable", "$sSQMp", TypeRecordFlags.HasSelfRequirement)
            .WithProtocol("Swift", "Decodable", "$ss9DecodableMp", TypeRecordFlags.HasAssociatedTypes)
            .WithProtocol("Swift", "Encodable", "$ss9EncodableMp", TypeRecordFlags.HasAssociatedTypes);
        var ctx3 = PInvokeHelperContext.CreateIfGeneric(
            CreateConstrainedGenericFrozenStruct(moduleDecl, "Forecast", constraints),
            dbAll)!;
        Assert.Empty(ctx3.UnresolvedPwtConstraints);
        Assert.False(ctx3.HasIndeterminatePwtShape);
        Assert.Equal(3, ctx3.PwtEntries.Count);
    }

    [Fact]
    public void AddMetadataAccessorDeclaration_ThinMode_EmitsStandardPInvoke()
    {
        // <= 3 metadata/PWT args: route to the existing thin-mode PInvokeDeclaration
        // path. No raw code block should be emitted.
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateConstrainedGenericFrozenStructTwoParams(
            moduleDecl,
            "Pair",
            secondParamConstraints: new[] { ("TestModule", "Describable") });
        var typeDb = new ConstrainedGenericMockTypeDatabase()
            .WithProtocol("TestModule", "Describable", "$s10TestModule11DescribableMp");

        var ctx = PInvokeHelperContext.CreateIfGeneric(structDecl, typeDb)!;
        Assert.False(ctx.ExceedsRegisterArgumentThreshold);

        ctx.AddMetadataAccessorDeclaration("/tmp/TestModule.dylib", "$s10TestModule4PairVMa");

        Assert.Single(ctx.Declarations);
        var decl = ctx.Declarations[0];
        Assert.Equal("PInvoke_getMetadata", decl.MethodName);
        Assert.Equal("TypeMetadata", decl.ReturnType);
        Assert.Equal("TypeMetadataRequest request", decl.ParametersString);
        Assert.Equal("$s10TestModule4PairVMa", decl.EntryPoint);
        Assert.NotNull(decl.MetadataParameters);
        Assert.Equal(3, decl.MetadataParameters!.Count);
        Assert.Empty(ctx.RawCodeBlocks);
    }

    [Fact]
    public void AddMetadataAccessorDeclaration_BufferMode_EmitsBufferPInvokeAndWrapper()
    {
        // > 3 metadata/PWT args: indirect-buffer ABI. Expect a single raw code
        // block containing both a private PInvoke_getMetadata_buffer declaration
        // (single IntPtr parameters arg) and an internal wrapper method with the
        // thin-mode parameter shape that stackallocs an IntPtr buffer.
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateConstrainedGenericClass(
            moduleDecl,
            "Quad",
            paramConstraints: new[]
            {
                new[] { ("TestModule", "Alpha") },
                new[] { ("TestModule", "Beta") },
                new[] { ("TestModule", "Gamma") },
                new[] { ("TestModule", "Delta") },
            });
        var typeDb = new ConstrainedGenericMockTypeDatabase()
            .WithProtocol("TestModule", "Alpha", "$s10TestModule5AlphaMp")
            .WithProtocol("TestModule", "Beta", "$s10TestModule4BetaMp")
            .WithProtocol("TestModule", "Gamma", "$s10TestModule5GammaMp")
            .WithProtocol("TestModule", "Delta", "$s10TestModule5DeltaMp");

        var ctx = PInvokeHelperContext.CreateIfGeneric(classDecl, typeDb)!;
        Assert.True(ctx.ExceedsRegisterArgumentThreshold);
        // 4 metadata + 4 PWT = 8 thin-mode param slots.
        Assert.Equal(8, ctx.GetTypeMetadataAccessorParameterDeclarations().Count);

        ctx.AddMetadataAccessorDeclaration("/tmp/TestModule.dylib", "$s10TestModule4QuadCMa");

        // Thin-mode Declarations list stays empty — everything lives in the raw block.
        Assert.Empty(ctx.Declarations);
        Assert.Single(ctx.RawCodeBlocks);

        var block = ctx.RawCodeBlocks[0];

        // Private P/Invoke targeting the Ma symbol with a single buffer arg.
        Assert.Contains("LibraryImport(\"/tmp/TestModule.dylib\", EntryPoint = \"$s10TestModule4QuadCMa\")", block);
        Assert.Contains("private static partial", block);
        Assert.Contains("PInvoke_getMetadata_buffer", block);
        Assert.Contains("TypeMetadataRequest request, global::System.IntPtr parameters", block);

        // Internal wrapper method with the thin-mode parameter shape and stackalloc buffer.
        // Generic-param sugar names are T/U/V/W → lowercased to t/u/v/w for metadata slots.
        Assert.Contains("internal static", block);
        Assert.Contains("PInvoke_getMetadata(TypeMetadataRequest request", block);
        Assert.Contains("stackalloc global::System.IntPtr[8]", block);
        Assert.Contains("buffer[0] = tMetadata;", block);
        Assert.Contains("buffer[7] =", block); // last slot assigned
        Assert.Contains("PInvoke_getMetadata_buffer(request, (global::System.IntPtr)buffer)", block);
    }

    [Fact]
    public void AddMetadataAccessorDeclaration_BufferMode_IdempotentForSameSymbol()
    {
        // Re-entry with the same metadata symbol must not emit a second block.
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateConstrainedGenericClass(
            moduleDecl,
            "Quad",
            paramConstraints: new[]
            {
                new[] { ("TestModule", "Alpha") },
                new[] { ("TestModule", "Beta") },
                new[] { ("TestModule", "Gamma") },
                new[] { ("TestModule", "Delta") },
            });
        var typeDb = new ConstrainedGenericMockTypeDatabase()
            .WithProtocol("TestModule", "Alpha", "$s10TestModule5AlphaMp")
            .WithProtocol("TestModule", "Beta", "$s10TestModule4BetaMp")
            .WithProtocol("TestModule", "Gamma", "$s10TestModule5GammaMp")
            .WithProtocol("TestModule", "Delta", "$s10TestModule5DeltaMp");

        var ctx = PInvokeHelperContext.CreateIfGeneric(classDecl, typeDb)!;

        ctx.AddMetadataAccessorDeclaration("/tmp/TestModule.dylib", "$s10TestModule4QuadCMa");
        ctx.AddMetadataAccessorDeclaration("/tmp/TestModule.dylib", "$s10TestModule4QuadCMa");

        Assert.Single(ctx.RawCodeBlocks);
    }

    [Fact]
    public void Emit_GenericFrozenStruct_TwoMetadataOnePwt_DoesNotExceedThreshold()
    {
        // 2 metadata + 1 PWT = 3 args → exactly at the threshold (not exceeding).
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateConstrainedGenericFrozenStructTwoParams(
            moduleDecl,
            "Pair",
            secondParamConstraints: new[] { ("TestModule", "Describable") });
        var typeDb = new ConstrainedGenericMockTypeDatabase()
            .WithProtocol("TestModule", "Describable", "$s10TestModule11DescribableMp");

        var ctx = PInvokeHelperContext.CreateIfGeneric(structDecl, typeDb)!;

        Assert.False(ctx.ExceedsRegisterArgumentThreshold);

        var parameters = ctx.GetTypeMetadataAccessorParameterDeclarations();
        Assert.Equal(3, parameters.Count);
        Assert.Equal("IntPtr aMetadata", parameters[0]);
        Assert.Equal("IntPtr bMetadata", parameters[1]);
        Assert.Equal("IntPtr bDescribablePWT", parameters[2]);
    }

    #region Constrained-generic test helpers

    private static EnumDecl CreateConstrainedGenericEnum(
        ModuleDecl moduleDecl,
        string name,
        IReadOnlyList<(string Module, string Protocol)> constraints)
    {
        return new EnumDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}ON",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T", BuildConformanceList(constraints), new List<GenericParameterConformance>())
            },
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = false,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}OMa",
            Cases = new List<EnumCaseDecl>()
        };
    }

    private static StructDecl CreateConstrainedGenericFrozenStruct(
        ModuleDecl moduleDecl,
        string name,
        IReadOnlyList<(string Module, string Protocol)> constraints)
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T", BuildConformanceList(constraints), new List<GenericParameterConformance>())
            },
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa"
        };
    }

    private static StructDecl CreateConstrainedGenericFrozenStructTwoParams(
        ModuleDecl moduleDecl,
        string name,
        IReadOnlyList<(string Module, string Protocol)> secondParamConstraints)
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "A", new List<GenericParameterConformance>(), new List<GenericParameterConformance>()),
                new("τ_0_1", "B", BuildConformanceList(secondParamConstraints), new List<GenericParameterConformance>())
            },
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa"
        };
    }

    private static StructDecl CreateConstrainedGenericNonFrozenStruct(
        ModuleDecl moduleDecl,
        string name,
        IReadOnlyList<(string Module, string Protocol)> constraints)
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T", BuildConformanceList(constraints), new List<GenericParameterConformance>())
            },
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = false,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa"
        };
    }

    private static ClassDecl CreateConstrainedGenericClass(
        ModuleDecl moduleDecl,
        string name,
        IReadOnlyList<(string Module, string Protocol)[]> paramConstraints)
    {
        var sugarNames = new[] { "T", "U", "V", "W" };
        var genericParams = paramConstraints
            .Select((constraints, idx) => new GenericArgumentDecl(
                $"τ_0_{idx}",
                sugarNames[idx],
                BuildConformanceList(constraints),
                new List<GenericParameterConformance>()))
            .ToList();

        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = genericParams,
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
    }

    private static List<GenericParameterConformance> BuildConformanceList(
        IReadOnlyList<(string Module, string Protocol)> constraints)
    {
        return constraints
            .Select(c => new GenericParameterConformance(
                Path: new[] { "τ_0_0" },
                ConformanceTarget: SwiftTypeName.FromModuleQualifiedName($"{c.Module}.{c.Protocol}"),
                Kind: ConformanceKind.Protocol))
            .ToList();
    }

    /// <summary>
    /// Minimal ITypeDatabase fake — only registers protocol records keyed by
    /// module-qualified name. Suitable for the constrained-generic emitter
    /// path that only ever calls TryGetTypeRecord + GetLibraryPath.
    /// </summary>
    private sealed class ConstrainedGenericMockTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types = new();

        public string? AsyncLibraryName => null;

        public ConstrainedGenericMockTypeDatabase WithProtocol(
            string moduleName,
            string protocolName,
            string? descriptorSymbol = null,
            TypeRecordFlags flags = TypeRecordFlags.None)
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{protocolName}");
            _types[swiftTypeName.ModuleQualifiedName] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(moduleName, protocolName),
                SwiftTypeName = swiftTypeName,
                MetadataAccessor = "",
                Flags = flags,
                Kind = TypeRecordKind.Protocol,
                ProtocolDescriptorSymbol = descriptorSymbol
            };
            return this;
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) =>
            _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(
            SwiftTypeName swiftTypeName,
            [NotNullWhen(returnValue: true)] out TypeRecord? record) =>
            _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record);

        public string GetLibraryPath(string moduleName) => $"/tmp/{moduleName}.dylib";

        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion

    #endregion
}
