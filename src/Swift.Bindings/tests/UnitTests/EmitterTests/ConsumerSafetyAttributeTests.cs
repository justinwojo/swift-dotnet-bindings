// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for consumer safety attributes: [Obsolete] with DiagnosticId for JIT-risk (SB0001)
/// and missing-symbol (SB0002) methods, [OriginalSwiftType] for AnyType-fallback parameters and return types.
/// </summary>
public class ConsumerSafetyAttributeTests
{
    #region Deliverable 1: No Wrapper/Thunk Fallback [Obsolete]

    [Fact]
    public void NoWrapperOrThunk_EmitsObsoleteWithSB0001()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("handle", classDecl, moduleDecl);
        // Swift.String (16-byte frozen+RefFields) makes the P/Invoke signature non-blittable,
        // so the narrowed SB0001 gate still fires when no wrapper/thunk is present.
        method.CSSignature.Add(CreateArg("name", new NamedTypeSpec("Swift.String"), moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("[Obsolete(\"", csOutput);
        Assert.Contains("No @_cdecl wrapper or native thunk available", csOutput);
        Assert.Contains("DiagnosticId = \"SB0001\"", csOutput);
        Assert.DoesNotContain(", true)]", csOutput);
    }

    [Fact]
    public void NoWrapperOrThunk_BlittableSignature_NoObsolete()
    {
        // A sync CallConvSwift P/Invoke with only blittable types is ABI-stable on both
        // Mono and NativeAOT, so the SB0001 gate must not fire even without a wrapper.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("handle", classDecl, moduleDecl);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.DoesNotContain("DiagnosticId = \"SB0001\"", csOutput);
    }

    [Fact]
    public void CdeclMethodWrapper_NoObsolete()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("handle", classDecl, moduleDecl);
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.DoesNotContain("[Obsolete(", csOutput);
    }

    [Fact]
    public void CdeclConstructorWrapper_NoObsolete()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var ctor = CreateConstructor(classDecl, moduleDecl);
        ctor.UsesCdeclConstructorWrapper = true;
        ctor.UsesWrapperLibrary = true;

        var (csOutput, _) = EmitConstructor(ctor, typeDatabase);

        Assert.DoesNotContain("[Obsolete(", csOutput);
    }

    [Fact]
    public void NoWrapperOrThunk_Constructor_EmitsObsoleteWithSB0001()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var ctor = CreateConstructor(classDecl, moduleDecl);
        // Swift.String forces a non-blittable P/Invoke signature so the narrowed
        // SB0001 gate still triggers without a @_cdecl constructor wrapper.
        ctor.CSSignature.Add(CreateArg("label", new NamedTypeSpec("Swift.String"), moduleDecl));

        var (csOutput, _) = EmitConstructor(ctor, typeDatabase);

        Assert.Contains("[Obsolete(\"", csOutput);
        Assert.Contains("No @_cdecl wrapper or native thunk available", csOutput);
        Assert.Contains("DiagnosticId = \"SB0001\"", csOutput);
    }

    #endregion

    #region Deliverable 2: Symbol Cross-Referencing

    [Fact]
    public void ComputeEntryPoint_BasicMethod_ReturnsMangledName()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("doWork", classDecl, moduleDecl, isStatic: true);

        var (entryPoint, needsWrapper) = PInvokeEmitter.ComputeEntryPoint(method);

        Assert.Equal(method.MangledName, entryPoint);
        Assert.False(needsWrapper);
    }

    [Fact]
    public void ComputeEntryPoint_NonFinalClassInstance_AppendsTj()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        classDecl.IsFinal = false;
        var method = CreateMethod("doWork", classDecl, moduleDecl);
        method.IsFinal = false;

        var (entryPoint, needsWrapper) = PInvokeEmitter.ComputeEntryPoint(method);

        Assert.EndsWith("Tj", entryPoint);
        Assert.False(needsWrapper);
    }

    [Fact]
    public void ComputeEntryPoint_AsyncMethod_ReturnsNeedsWrapperTrue()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("fetch", classDecl, moduleDecl, isStatic: true);
        method.IsAsync = true;

        var (_, needsWrapper) = PInvokeEmitter.ComputeEntryPoint(method);

        Assert.True(needsWrapper);
    }

    [Fact]
    public void SymbolPresent_IsMissingExportedSymbol_StaysFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("doWork", classDecl, moduleDecl, isStatic: true);
        // Add the method's mangled name to the exported symbols
        moduleDecl.ExportedSymbols = new HashSet<string> { method.MangledName };

        var env = new MethodEnvironment(method, typeDatabase);
        MethodHandler.CheckExportedSymbol(env);

        Assert.False(method.IsMissingExportedSymbol);
    }

    [Fact]
    public void SymbolMissing_IsMissingExportedSymbol_SetTrue_EmitsObsoleteWithSB0002()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("doWork", classDecl, moduleDecl, isStatic: true);
        method.UsesCdeclMethodWrapper = true; // Has wrapper, so only missing symbol warning fires
        // Exported symbols set exists but doesn't contain this method's symbol
        moduleDecl.ExportedSymbols = new HashSet<string> { "$sOtherSymbol" };

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("[Obsolete(\"", csOutput);
        Assert.Contains("EntryPointNotFoundException", csOutput);
        Assert.Contains("DiagnosticId = \"SB0002\"", csOutput);
    }

    [Fact]
    public void SilentTombstoneReturn_EmitsObsoleteWithSB0002()
    {
        // A method returning a type that was emitted with [OpaqueSwiftType] but has zero
        // usable members should be flagged SB0002 so audits grep them out.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("getBox", classDecl, moduleDecl, isStatic: true);
        method.UsesCdeclMethodWrapper = true; // no SB0001 — signature has a wrapper
        moduleDecl.ExportedSymbols = new HashSet<string> { method.MangledName };
        // Return type points at TestModule.Box, which the emission context marks as a tombstone.
        method.CSSignature[0] = CreateArg("", new NamedTypeSpec("TestModule.Box"), moduleDecl);

        var emissionContext = new ModuleEmissionContext();
        emissionContext.AddSilentTombstone("TestModule.Box");

        var (csOutput, _) = EmitMethod(method, typeDatabase, emissionContext);

        Assert.Contains("[Obsolete(\"", csOutput);
        Assert.Contains("Return type has no usable surface", csOutput);
        Assert.Contains("DiagnosticId = \"SB0002\"", csOutput);
    }

    [Fact]
    public void OptionalSilentTombstoneReturn_EmitsObsoleteWithSB0002()
    {
        // Optional<Tombstone> return should also be flagged — the inner type is what matters.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("getBoxIfAny", classDecl, moduleDecl, isStatic: true);
        method.UsesCdeclMethodWrapper = true;
        moduleDecl.ExportedSymbols = new HashSet<string> { method.MangledName };
        var optionalBox = new NamedTypeSpec("Swift.Optional");
        optionalBox.GenericParameters.Add(new NamedTypeSpec("TestModule.Box"));
        method.CSSignature[0] = CreateArg("", optionalBox, moduleDecl);

        var emissionContext = new ModuleEmissionContext();
        emissionContext.AddSilentTombstone("TestModule.Box");

        var (csOutput, _) = EmitMethod(method, typeDatabase, emissionContext);

        Assert.Contains("Return type has no usable surface", csOutput);
        Assert.Contains("DiagnosticId = \"SB0002\"", csOutput);
    }

    [Fact]
    public void NonTombstoneReturn_DoesNotEmitSB0002()
    {
        // A method returning a non-tombstoned type must not be flagged, even when the
        // emission context tracks other tombstones.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("getBox", classDecl, moduleDecl, isStatic: true);
        method.UsesCdeclMethodWrapper = true;
        moduleDecl.ExportedSymbols = new HashSet<string> { method.MangledName };
        method.CSSignature[0] = CreateArg("", new NamedTypeSpec("TestModule.Box"), moduleDecl);

        var emissionContext = new ModuleEmissionContext();
        emissionContext.AddSilentTombstone("OtherModule.SomethingElse");

        var (csOutput, _) = EmitMethod(method, typeDatabase, emissionContext);

        Assert.DoesNotContain("Return type has no usable surface", csOutput);
        Assert.DoesNotContain("DiagnosticId = \"SB0002\"", csOutput);
    }

    [Fact]
    public void ExportedSymbolsNull_NoCheck_NoFlag()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("doWork", classDecl, moduleDecl, isStatic: true);
        moduleDecl.ExportedSymbols = null;

        var env = new MethodEnvironment(method, typeDatabase);
        MethodHandler.CheckExportedSymbol(env);

        Assert.False(method.IsMissingExportedSymbol);
    }

    [Fact]
    public void CombinedNoWrapperAndMissingSymbol_SingleObsoleteWithSB0001()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("crash", classDecl, moduleDecl, isStatic: true);
        // No @_cdecl wrapper + missing symbol → both warnings combined
        method.IsMissingExportedSymbol = true;
        // Non-blittable param keeps SB0001 in scope under the narrower blittability gate.
        method.CSSignature.Add(CreateArg("tag", new NamedTypeSpec("Swift.String"), moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Should have a single [Obsolete] with both messages, using SB0001 (broader scope)
        Assert.Contains("No @_cdecl wrapper or native thunk available", csOutput);
        Assert.Contains("EntryPointNotFoundException", csOutput);
        Assert.Contains("DiagnosticId = \"SB0001\"", csOutput);
        // Only one [Obsolete] attribute
        var obsoleteCount = CountOccurrences(csOutput, "[Obsolete(\"");
        Assert.Equal(1, obsoleteCount);
    }

    [Fact]
    public void Accessor_WithMissingSymbol_NoObsolete()
    {
        // Accessor methods (property getters/setters) must NOT get [Obsolete]
        // because property bodies call them directly — CS0619 would break generated code.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("name", classDecl, moduleDecl, isStatic: true);
        method.IsAccessor = true;
        method.IsMissingExportedSymbol = true;

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.DoesNotContain("[Obsolete(", csOutput);
    }

    #endregion

    #region Deliverable 3: [OriginalSwiftType] Attribute

    [Fact]
    public void OriginalSwiftTypeAttribute_StoresSwiftTypeName()
    {
        var attr = new Swift.OriginalSwiftTypeAttribute("Swift.UnsafePointer<Swift.UInt8>");
        Assert.Equal("Swift.UnsafePointer<Swift.UInt8>", attr.SwiftTypeName);
    }

    [Fact]
    public void Parameter_WithNestedAnyTypeFallback_EmitsOriginalSwiftType()
    {
        // Use bound generic Box<unsupported_closure> — Box resolves to Box<object>
        // (passes ContainsPlaceholder) but TryFindFallbackInfo detects the nested closure
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("process", classDecl, moduleDecl);
        // Unsupported closure: generic parameter T can't be resolved
        var unsupportedClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new NamedTypeSpec("T")),
            TupleTypeSpec.Empty);
        method.CSSignature.Add(CreateArg("data",
            new NamedTypeSpec("TestModule.Box", unsupportedClosure), moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("[global::Swift.OriginalSwiftType(", csOutput);
        Assert.Contains("(T) -> ()", csOutput);
    }

    [Fact]
    public void Parameter_WithResolvedType_NoOriginalSwiftType()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("process", classDecl, moduleDecl);
        method.CSSignature.Add(CreateArg("value", new NamedTypeSpec("Swift.Int"), moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.DoesNotContain("OriginalSwiftType", csOutput);
    }

    [Fact]
    public void MultipleNestedFallbackParams_FirstGetsAttribute()
    {
        // Both params have unsupported nested types — attribute emitted for the first match
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("merge", classDecl, moduleDecl);
        var closureA = new ClosureTypeSpec(
            new TupleTypeSpec(new NamedTypeSpec("T")),
            TupleTypeSpec.Empty);
        var closureB = new ClosureTypeSpec(
            new TupleTypeSpec(new NamedTypeSpec("U")),
            TupleTypeSpec.Empty);
        method.CSSignature.Add(CreateArg("first",
            new NamedTypeSpec("TestModule.Box", closureA), moduleDecl));
        method.CSSignature.Add(CreateArg("second",
            new NamedTypeSpec("TestModule.Box", closureB), moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // At least one [OriginalSwiftType] attribute should be emitted
        Assert.Contains("[global::Swift.OriginalSwiftType(", csOutput);
    }

    [Fact]
    public void ParametersString_NullAttributes_ReturnsDefault()
    {
        var sig = new Signature("void", new[] { new Parameter(new MarshalledType.Simple("int"), "x") });

        var result = sig.ParametersString((IReadOnlyDictionary<string, string>?)null);

        Assert.Equal(sig.ParametersString(), result);
    }

    [Fact]
    public void ReturnType_NestedAnyTypeFallback_EmitsReturnAttribute()
    {
        // Use bound generic Box<unsupported_closure> as return type
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var unsupportedClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new NamedTypeSpec("T")),
            TupleTypeSpec.Empty);
        var method = CreateMethod("getData", classDecl, moduleDecl);
        method.CSSignature[0] = CreateArg("",
            new NamedTypeSpec("TestModule.Box", unsupportedClosure), moduleDecl);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("[return: global::Swift.OriginalSwiftType(", csOutput);
        Assert.Contains("(T) -> ()", csOutput);
    }

    [Fact]
    public void ReturnType_Resolved_NoReturnAttribute()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethod("getCount", classDecl, moduleDecl);
        method.CSSignature[0] = CreateArg("", new NamedTypeSpec("Swift.Int"), moduleDecl);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.DoesNotContain("[return:", csOutput);
    }

    [Fact]
    public void ParametersString_EmptyAttributes_ReturnsDefault()
    {
        var sig = new Signature("void", new[] { new Parameter(new MarshalledType.Simple("int"), "x") });

        var result = sig.ParametersString(new Dictionary<string, string>());

        Assert.Equal(sig.ParametersString(), result);
    }

    #endregion

    #region GetSafetyObsoleteAttribute (async wrapper propagation)

    [Fact]
    public void GetSafetyObsoleteAttribute_NoWrapperOrThunk_ReturnsSB0001()
    {
        var moduleDecl = CreateModuleDecl();
        var method = CreateMethod("present", CreateClassDecl("Foo", moduleDecl), moduleDecl);
        // Non-blittable param so the narrowed SB0001 gate actually fires.
        method.CSSignature.Add(CreateArg("name", new NamedTypeSpec("Swift.String"), moduleDecl));
        var env = new MethodEnvironment(method, CreateTypeDatabase());

        var attr = MethodHandler.GetSafetyObsoleteAttribute(env);

        Assert.NotNull(attr);
        Assert.Contains("SB0001", attr);
        Assert.Contains("No @_cdecl wrapper or native thunk available", attr);
    }

    [Fact]
    public void GetSafetyObsoleteAttribute_BlittableSignature_ReturnsNull()
    {
        // No wrapper, but the P/Invoke signature is fully blittable — SB0001 must not fire
        // for the propagated completion-handler overload either.
        var moduleDecl = CreateModuleDecl();
        var method = CreateMethod("present", CreateClassDecl("Foo", moduleDecl), moduleDecl);
        var env = new MethodEnvironment(method, CreateTypeDatabase());

        var attr = MethodHandler.GetSafetyObsoleteAttribute(env);

        Assert.Null(attr);
    }

    [Fact]
    public void GetSafetyObsoleteAttribute_MissingSymbol_ReturnsSB0002()
    {
        var moduleDecl = CreateModuleDecl();
        var method = CreateMethod("present", CreateClassDecl("Foo", moduleDecl), moduleDecl);
        method.UsesCdeclMethodWrapper = true; // Has wrapper, so no SB0001 warning
        method.IsMissingExportedSymbol = true;
        var env = new MethodEnvironment(method, CreateTypeDatabase());

        var attr = MethodHandler.GetSafetyObsoleteAttribute(env);

        Assert.NotNull(attr);
        Assert.Contains("SB0002", attr);
        Assert.Contains("EntryPointNotFoundException", attr);
    }

    [Fact]
    public void GetSafetyObsoleteAttribute_CdeclWrapped_ReturnsNull()
    {
        var moduleDecl = CreateModuleDecl();
        var method = CreateMethod("present", CreateClassDecl("Foo", moduleDecl), moduleDecl);
        method.UsesCdeclMethodWrapper = true;
        var env = new MethodEnvironment(method, CreateTypeDatabase());

        var attr = MethodHandler.GetSafetyObsoleteAttribute(env);

        Assert.Null(attr);
    }

    #endregion

    #region Helper Methods

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
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

    private static MethodDecl CreateMethod(
        string name,
        TypeDecl parentDecl,
        ModuleDecl moduleDecl,
        bool isStatic = false)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{parentDecl.Name.Length}{parentDecl.Name}C{name.Length}{name}SiyF",
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

    private static MethodDecl CreateConstructor(
        ClassDecl parentDecl,
        ModuleDecl moduleDecl)
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
            SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Loader"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
                MetadataAccessor = "$s10TestModule6LoaderCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Box"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Box"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Box"),
                MetadataAccessor = "$s10TestModule3BoxVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(testModule);

        return typeDb;
    }

    private static (string csOutput, string swiftOutput) EmitMethod(
        MethodDecl methodDecl,
        TypeDatabase typeDatabase,
        ModuleEmissionContext? emissionContext = null)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = new MethodEnvironment(methodDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        var context = emissionContext is null
            ? TypeHandlerContext.Empty
            : new TypeHandlerContext(null, new(), null, EmissionContext: emissionContext);
        handler.Emit(csWriter, swiftWriter, env, conductor, context);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    private static (string csOutput, string swiftOutput) EmitConstructor(
        MethodDecl methodDecl,
        TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new ConstructorHandler(new NullLogger<ConstructorHandler>(), new HashSet<string>());
        var env = new MethodEnvironment(methodDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    #endregion
}
