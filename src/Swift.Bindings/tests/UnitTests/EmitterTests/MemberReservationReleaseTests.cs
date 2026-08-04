// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The member loops reserve a projected C# name in a dedup set BEFORE handing the member to the
/// emitter, but emission has internal refusal paths that return without writing anything. A name
/// held by a member that produced no output costs a SECOND member: a sibling projecting to the
/// same name is dropped as a duplicate of something that never reached the binding. These tests
/// assert the rule the dedup sets are supposed to enforce — first EMITTING claimant wins — for
/// type-body methods, module-level free functions, and subscripts, with a matching control per
/// site proving a name a member DID emit under is still never handed to a sibling.
/// </summary>
public class MemberReservationReleaseTests
{
    [Fact]
    public void TypeMethod_FirstClaimantRefusedInsideEmission_SiblingSharingNameStillEmits()
    {
        // Both methods project to C# `Value()` — the dedup keys are return-insensitive — and the
        // first one's return type is unregistered, so it passes validation and is refused during
        // emission. The bindable sibling must still reach the output.
        var (csOutput, _) = EmitFixture((moduleDecl, typeDecl) =>
        {
            typeDecl.Methods.Add(CreateMethod("value", "unmapped", typeDecl, moduleDecl,
                returnType: new NamedTypeSpec("UnmappedKit.Widget")));
            typeDecl.Methods.Add(CreateMethod("value", "bindable", typeDecl, moduleDecl,
                returnType: new NamedTypeSpec("Swift.Int")));
        });

        Assert.Equal(1, CountPublicMethod(csOutput, "Value"));
    }

    [Fact]
    public void TypeMethod_FirstClaimantEmits_SiblingSharingNameIsStillDropped()
    {
        // Control for the release: the name is given back ONLY when nothing was written under it.
        // Two bindable same-shape methods still collapse to one declaration, because emitting the
        // second would be a duplicate C# member.
        var (csOutput, _) = EmitFixture((moduleDecl, typeDecl) =>
        {
            typeDecl.Methods.Add(CreateMethod("value", "first", typeDecl, moduleDecl,
                returnType: new NamedTypeSpec("Swift.Int")));
            typeDecl.Methods.Add(CreateMethod("value", "second", typeDecl, moduleDecl,
                returnType: new NamedTypeSpec("Swift.Int")));
        });

        Assert.Equal(1, CountPublicMethod(csOutput, "Value"));
    }

    [Fact]
    public void ModuleFunction_FirstClaimantRefusedInsideEmission_SiblingSharingNameStillEmits()
    {
        // Same shape at module scope: free functions dedup through their own copy of the loop.
        var (csOutput, _) = EmitFixture((moduleDecl, _) =>
        {
            moduleDecl.Methods.Add(CreateMethod("compute", "unmapped", moduleDecl, moduleDecl,
                returnType: new NamedTypeSpec("UnmappedKit.Widget"), methodType: MethodType.Static));
            moduleDecl.Methods.Add(CreateMethod("compute", "bindable", moduleDecl, moduleDecl,
                returnType: new NamedTypeSpec("Swift.Int"), methodType: MethodType.Static));
        });

        Assert.Equal(1, CountPublicMethod(csOutput, "Compute"));
    }

    [Fact]
    public void ModuleFunction_FirstClaimantEmits_SiblingSharingNameIsStillDropped()
    {
        var (csOutput, _) = EmitFixture((moduleDecl, _) =>
        {
            moduleDecl.Methods.Add(CreateMethod("compute", "first", moduleDecl, moduleDecl,
                returnType: new NamedTypeSpec("Swift.Int"), methodType: MethodType.Static));
            moduleDecl.Methods.Add(CreateMethod("compute", "second", moduleDecl, moduleDecl,
                returnType: new NamedTypeSpec("Swift.Int"), methodType: MethodType.Static));
        });

        Assert.Equal(1, CountPublicMethod(csOutput, "Compute"));
    }

    [Fact]
    public void Subscript_FirstClaimantRefusedByAccessorPreflight_SiblingSharingIndexSignatureStillEmits()
    {
        // Subscripts key on their projected index-parameter types, so both of these claim `[nint]`.
        // The first one's getter is async — refused by the accessor preflight that runs AFTER the
        // key is taken — so the indexer that CAN be emitted must not be dropped behind it.
        var (csOutput, _) = EmitFixture((moduleDecl, typeDecl) =>
        {
            typeDecl.Subscripts.Add(CreateSubscript("refused", typeDecl, moduleDecl, isAsyncGetter: true));
            typeDecl.Subscripts.Add(CreateSubscript("bindable", typeDecl, moduleDecl, isAsyncGetter: false));
        });

        Assert.Equal(1, CountPublicIndexer(csOutput));
    }

    [Fact]
    public void Subscript_FirstClaimantEmits_SiblingSharingIndexSignatureIsStillDropped()
    {
        var (csOutput, _) = EmitFixture((moduleDecl, typeDecl) =>
        {
            typeDecl.Subscripts.Add(CreateSubscript("first", typeDecl, moduleDecl, isAsyncGetter: false));
            typeDecl.Subscripts.Add(CreateSubscript("second", typeDecl, moduleDecl, isAsyncGetter: false));
        });

        Assert.Equal(1, CountPublicIndexer(csOutput));
    }

    #region Helpers

    /// <summary>
    /// Counts public declarations of a named C# method — the member a consumer can call — ignoring
    /// both the P/Invoke externs that share the name and the convenience <c>int</c> overload that
    /// rides along with every native-int parameter, so one Swift method counts once.
    /// </summary>
    private static int CountPublicMethod(string csOutput, string methodName)
        => Regex.Matches(csOutput, $@"public\s+(static\s+)?[\w\.<>,\?]+\s+{methodName}\s*\(\s*nint\b").Count;

    /// <summary>
    /// Counts emitted indexers, ignoring the convenience <c>int</c> overload that rides along with
    /// every native-int indexer — one subscript yields one <c>this[nint …]</c> declaration.
    /// </summary>
    private static int CountPublicIndexer(string csOutput)
        => Regex.Matches(csOutput, @"public\s+[\w\.<>,\?]+\s+this\s*\[\s*nint\b").Count;

    private static ArgumentDecl CreateArg(string name, TypeSpec typeSpec, ModuleDecl moduleDecl)
        => new()
        {
            Name = name,
            PrivateName = name,
            SwiftTypeSpec = typeSpec,
            HasDefaultArg = false,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl,
        };

    private static MethodDecl CreateMethod(
        string name,
        string symbolSuffix,
        BaseDecl parentDecl,
        ModuleDecl moduleDecl,
        TypeSpec returnType,
        MethodType methodType = MethodType.Instance)
        => new()
        {
            Name = name,
            MangledName = $"$s10TestModule{name.Length}{name}_{symbolSuffix}F",
            MethodType = methodType,
            IsConstructor = false,
            // One bindable parameter, identical across a fixture's siblings: it keeps them sharing
            // one dedup key while keeping the emitted C# name free of the zero-argument `Get`
            // prefix, so the assertions read the name the Swift member was declared under.
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg(string.Empty, returnType, moduleDecl),
                CreateArg("of", new NamedTypeSpec("Swift.Int"), moduleDecl),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
        };

    /// <summary>
    /// A read-only `subscript(index: Int) -> Int`. When <paramref name="isAsyncGetter"/> is set the
    /// getter is async, which the subscript accessor preflight refuses.
    /// </summary>
    private static SubscriptDecl CreateSubscript(
        string symbolSuffix, TypeDecl parentDecl, ModuleDecl moduleDecl, bool isAsyncGetter)
    {
        var intSpec = new NamedTypeSpec("Swift.Int");
        var getter = new MethodDecl
        {
            Name = "getter:subscript",
            MangledName = $"$s10TestModule_subscript_get_{symbolSuffix}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAccessor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg(string.Empty, intSpec, moduleDecl),
                CreateArg("index", intSpec, moduleDecl),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = isAsyncGetter,
            IsSynthesizedAccessor = false,
        };

        return new SubscriptDecl
        {
            Name = "subscript",
            ReturnTypeSpec = intSpec,
            IndexParameters = new List<ArgumentDecl> { CreateArg("index", intSpec, moduleDecl) },
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getter } },
            MangledName = $"$s10TestModule_subscript_{symbolSuffix}",
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
        };
    }

    /// <summary>
    /// Builds a single-module fixture carrying one frozen struct, lets the caller hang members off
    /// the module and/or the type, then runs the full module emission. Swift.Int and the struct are
    /// registered; anything else a member names is left unregistered so it resolves to the
    /// unbindable placeholder that emission refuses.
    /// </summary>
    private static (string csOutput, string swiftOutput) EmitFixture(Action<ModuleDecl, TypeDecl> addMembers)
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

        const string typeName = "Loader";
        const string metadataAccessor = "$s10TestModule6LoaderVMa";
        var typeDecl = new StructDecl
        {
            Name = typeName,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
            MangledName = "$s10TestModule6LoaderVN",
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
        moduleDecl.Types.Add(typeDecl);

        addMembers(moduleDecl, typeDecl);

        var typeDatabase = new TypeDatabase();
        // XCFramework (wrapper-library) mode — the mode third-party bindings ship in.
        typeDatabase.AsyncLibraryName = "libTestModule";
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");
        module.RegisterType(
            typeDecl.SwiftTypeName,
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", typeName),
                SwiftTypeName = typeDecl.SwiftTypeName,
                MetadataAccessor = metadataAccessor,
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
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

        return (csStringWriter.ToString(), swiftStringWriter.ToString());
    }

    #endregion
}
