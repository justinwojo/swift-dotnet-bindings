// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A constructor or method whose FULL signature can't be bound because a TRAILING DEFAULTED
/// parameter resolves to the unbindable <c>AnyType</c> placeholder (e.g. a defaulted parameter
/// whose type is an unmapped platform enum) is still partially recoverable: Swift supplies the
/// trailing default, so a truncated overload that omits the unbindable tail binds cleanly. These
/// tests drive the FULL constructor/method emission path (through the handler's placeholder
/// rejection gate, which fires before the normal default-parameter post-processor pass would) and
/// assert the truncated overload is recovered rather than the whole member being dropped.
/// </summary>
public class PlaceholderDefaultRecoveryTests
{
    [Fact]
    public void Constructor_TrailingDefaultResolvesToPlaceholder_RecoversTruncatedOverload()
    {
        // init(a: Int, b: Int, style: <unmapped> = default): the full form trips ContainsPlaceholder
        // and is dropped, but the trailing default lets init(a: Int, b: Int) bind (Swift fills style).
        var (csOutput, swiftOutput, emissionCtx) = EmitTypeWithConstructor(
            "PayConfig", asClass: false,
            new[]
            {
                CreateArg("a", "Swift.Int", hasDefault: false),
                CreateArg("b", "Swift.Int", hasDefault: false),
                CreateArg("style", "UnmappedKit.ButtonStyle", hasDefault: true),
            });

        // The trimmed constructor's Swift shim is the durable signal that recovery fired — without
        // it the rejection branch returned before any overload emission and no _dbw_init_ exists.
        Assert.Contains("_dbw_init_", swiftOutput);
        // A working truncated form was recovered, so the loud "unsupported" drop comment for the
        // full constructor is suppressed (it would otherwise sit above a usable constructor).
        Assert.DoesNotContain("// Unsupported: method 'PayConfig.init'", csOutput);
        // The recovered overload carries a real, callable C# constructor body (not a broken/empty one).
        // This frozen blittable struct routes through the @_cdecl indirect-result path: a stack
        // `_cdeclResult`, its address as resultPtr, a void cdecl P/Invoke that writes through it, then
        // `this = _cdeclResult`. Asserting that shape proves the recovered ctor is well-formed. The
        // trimmed form drops the placeholder tail, so it takes exactly (a, b).
        Assert.Contains("PayConfig( nint a,  nint b)", csOutput);
        Assert.Contains("PayConfig _cdeclResult;", csOutput);
        Assert.Contains("this = _cdeclResult;", csOutput);
        // U-004/U-009 regression guard: the recovered constructor's FULL signature was rejected, so it
        // never flows through the main dedup loop's WasEmitted manifest recording. Without the manifest
        // entry the emitted-but-undocumented ctor is exactly the api-surface drift U-009 exists to kill.
        // A constructor is keyed by the name it is EMITTED under — the type's — and the trimmed form
        // takes (nint, nint).
        Assert.Contains(
            emissionCtx.ApiManifestEntries.Keys,
            k => k.Contains("PayConfig.PayConfig(") && k.Contains("nint") && !k.Contains("AnyType"));
    }

    [Fact]
    public void Constructor_ClassTrailingDefaultResolvesToPlaceholder_RecoversTruncatedOverload()
    {
        // Same trailing-placeholder shape on a Swift CLASS (not a frozen struct): the recovered
        // constructor must declare the P/Invoke result local and wrap it in SwiftClassHandle — NOT
        // assign an undeclared `result`. This locks the class-ctor recovery body shape.
        var (csOutput, swiftOutput, emissionCtx) = EmitTypeWithConstructor(
            "PayLoader", asClass: true,
            new[]
            {
                CreateArg("a", "Swift.Int", hasDefault: false),
                CreateArg("b", "Swift.Int", hasDefault: false),
                CreateArg("style", "UnmappedKit.ButtonStyle", hasDefault: true),
            });

        Assert.Contains("_dbw_init_", swiftOutput);
        Assert.DoesNotContain("// Unsupported: method 'PayLoader.init'", csOutput);
        // The class ctor wraps the pointer the P/Invoke returns into the handle. The P/Invoke return
        // local must be DECLARED (the class path uses a non-void IntPtr-returning P/Invoke), so the
        // handle-construction reads a bound local, not a phantom `result`.
        Assert.Contains("_handle = new SwiftClassHandle<", csOutput);
        Assert.Matches(@"var\s+\w+\s*=\s*PInvoke_init_[^;]*;", csOutput);
        Assert.Contains(
            emissionCtx.ApiManifestEntries.Keys,
            k => k.Contains("PayLoader.PayLoader(") && k.Contains("nint") && !k.Contains("AnyType"));
    }

    [Fact]
    public void Constructor_AllParamsBindable_EmitsFullConstructorNoRecoveryComment()
    {
        // Control: when nothing is a placeholder the primary constructor emits normally and the
        // recovery path is never reached — no _dbw_ trim wrapper, no unsupported comment.
        var (csOutput, _, _) = EmitTypeWithConstructor(
            "PlainConfig", asClass: false,
            new[]
            {
                CreateArg("a", "Swift.Int", hasDefault: false),
                CreateArg("b", "Swift.Int", hasDefault: false),
            });

        Assert.DoesNotContain("// Unsupported: method 'PlainConfig.init'", csOutput);
        // The primary constructor itself is present.
        Assert.Contains("PlainConfig(", csOutput);
    }

    [Fact]
    public void Constructor_RequiredParamIsPlaceholder_StaysDroppedWithComment()
    {
        // A placeholder in a REQUIRED (non-defaulted) leading parameter cannot be trimmed away —
        // every truncated overload retains it — so nothing recovers and the drop comment stays.
        var (csOutput, swiftOutput, emissionCtx) = EmitTypeWithConstructor(
            "HardConfig", asClass: false,
            new[]
            {
                CreateArg("style", "UnmappedKit.ButtonStyle", hasDefault: false),
                CreateArg("count", "Swift.Int", hasDefault: true),
            });

        Assert.DoesNotContain("_dbw_init_", swiftOutput);
        Assert.Contains("// Unsupported: method 'HardConfig.init'", csOutput);
        // Nothing recovered → nothing recorded for this type: a dropped member must NOT leak into the
        // documented surface. Matched on the key a constructor is actually recorded under — the
        // type's own name — because under this key scheme no entry is ever spelled `HardConfig.Init(`,
        // so forbidding that spelling would forbid nothing.
        Assert.DoesNotContain(emissionCtx.ApiManifestEntries.Keys,
            k => k.Contains("HardConfig.HardConfig(") || k.Contains("HardConfig.Init("));
    }

    #region Helpers

    private static ArgumentDecl CreateArg(string name, string swiftType, bool hasDefault)
        => new()
        {
            Name = name,
            PrivateName = name,
            SwiftTypeSpec = new NamedTypeSpec(swiftType),
            HasDefaultArg = hasDefault,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null,
        };

    private static ArgumentDecl CreateReturnArg(ModuleDecl moduleDecl, TypeSpec returnType)
        => new()
        {
            Name = string.Empty,
            PrivateName = string.Empty,
            SwiftTypeSpec = returnType,
            HasDefaultArg = false,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl,
        };

    /// <summary>
    /// Builds a single-module fixture with one type (a frozen struct when <paramref name="asClass"/>
    /// is false, else a Swift class) carrying one constructor whose parameters are
    /// <paramref name="ctorParams"/>, then runs the full module emission and returns the generated
    /// C# + Swift plus the emission context (for API-manifest assertions). Swift.Int and the type
    /// itself are registered; any other parameter type is left unregistered so it resolves to the
    /// AnyType placeholder.
    /// </summary>
    private static (string csOutput, string swiftOutput, ModuleEmissionContext emissionContext)
        EmitTypeWithConstructor(string typeName, bool asClass, ArgumentDecl[] ctorParams)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };

        // Struct mangling uses the `V` nominal tag, class uses `C`.
        string nominalTag = asClass ? "C" : "V";
        string metadataAccessor = $"$s10TestModule{typeName.Length}{typeName}{nominalTag}Ma";
        TypeDecl typeDecl = asClass
            ? new ClassDecl
            {
                Name = typeName,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
                MangledName = $"$s10TestModule{typeName.Length}{typeName}{nominalTag}N",
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Types = new List<TypeDecl>(),
                Operators = new List<OperatorDecl>(),
                Subscripts = new List<SubscriptDecl>(),
                GenericParameters = new List<GenericArgumentDecl>(),
                Conformances = new List<TypeConformance>(),
                ParentDecl = moduleDecl,
                ModuleDecl = moduleDecl,
            }
            : new StructDecl
            {
                Name = typeName,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
                MangledName = $"$s10TestModule{typeName.Length}{typeName}{nominalTag}N",
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
                MetadataAccessor = metadataAccessor,
            };

        // A constructor's return is the type being constructed (Self). For a class this resolves the
        // ctor's P/Invoke to an IntPtr-returning call whose result feeds the SwiftClassHandle; leaving
        // it an empty tuple would emit a void P/Invoke + undeclared result local (a synthetic-harness
        // artifact, not a real ctor shape). The frozen-struct cdecl path derives its `_cdeclResult`
        // buffer type from ParentDecl, so an empty-tuple return already produces its correct shape.
        TypeSpec returnTypeSpec = asClass
            ? new NamedTypeSpec($"TestModule.{typeName}")
            : TupleTypeSpec.Empty;
        var csSignature = new List<ArgumentDecl> { CreateReturnArg(moduleDecl, returnTypeSpec) };
        foreach (var p in ctorParams)
        {
            p.ParentDecl = typeDecl;
            p.ModuleDecl = moduleDecl;
            csSignature.Add(p);
        }

        var ctor = new MethodDecl
        {
            Name = "init",
            MangledName = $"$s10TestModule{typeName.Length}{typeName}{nominalTag}4inity...cfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = typeDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
        };
        typeDecl.Methods.Add(ctor);
        moduleDecl.Types.Add(typeDecl);

        var typeDatabase = new TypeDatabase();
        // Run in XCFramework (wrapper-library) mode — the ONLY mode third-party bindings ship in.
        // A non-empty AsyncLibraryName flips GenerationMode to XCFramework, so constructors route
        // through the @_cdecl wrapper path (the recovered overload's real production shape) rather
        // than the Direct-mode CallConvSwift return that a synthetic no-wrapper harness would take.
        typeDatabase.AsyncLibraryName = "libTestModule";
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");
        module.RegisterType(
            typeDecl.SwiftTypeName,
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", typeName),
                SwiftTypeName = typeDecl.SwiftTypeName,
                MetadataAccessor = metadataAccessor,
                Flags = asClass ? TypeRecordFlags.RequiresMemoryManagement : TypeRecordFlags.Frozen,
                Kind = asClass ? TypeRecordKind.Class : TypeRecordKind.Struct,
            });
        typeDatabase.AddModuleDatabase(module);

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var handler = new ModuleHandler(new NullLogger<ModuleHandler>(), null);
        var env = handler.Marshal(moduleDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory(), null);
        var emissionContext = new ModuleEmissionContext();
        var context = TypeHandlerContext.Empty with { EmissionContext = emissionContext };

        handler.Emit(csWriter, swiftWriter, env, conductor, context);

        return (csStringWriter.ToString(), swiftStringWriter.ToString(), emissionContext);
    }

    #endregion
}
