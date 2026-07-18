// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A method taking a scalar Swift <c>Foundation.URL</c> parameter (projected to
/// <c>Foundation.NSUrl</c>) gets an additive <c>string</c> convenience overload that forwards through
/// <c>new global::Foundation.NSUrl(s)</c>. These tests pin the additive-only, scalar, non-optional
/// contract and the dedup that keeps it from colliding with a real <c>string</c>-param sibling.
/// </summary>
public class UrlStringConvenienceOverloadEmitterTests
{
    [Fact]
    public void TryEmitOverload_SingleUrlParam_EmitsStringOverload()
    {
        var method = CreateMethod("download", MethodType.Instance,
            returnType: TupleTypeSpec.Empty,
            ("url", "Foundation.URL"));

        var output = EmitMethodOverload(method);

        Assert.Contains("public void Download(string url) => Download(new global::Foundation.NSUrl(url));", output);
    }

    [Fact]
    public void TryEmitOverload_StringReturn_BorrowsPrimaryReturnType()
    {
        var method = CreateMethod("describe", MethodType.Instance,
            returnType: "Swift.String",
            ("url", "Foundation.URL"));

        var output = EmitMethodOverload(method);

        Assert.Contains("string Describe(string url) => Describe(new global::Foundation.NSUrl(url));", output);
    }

    [Fact]
    public void TryEmitOverload_MixedParams_OnlyConvertsUrlParam()
    {
        var method = CreateMethod("load", MethodType.Instance,
            returnType: TupleTypeSpec.Empty,
            ("url", "Foundation.URL"), ("name", "Swift.String"));

        var output = EmitMethodOverload(method);

        // The URL param becomes `string` and forwards through the NSUrl ctor; the real string param
        // stays a plain string and forwards untouched.
        Assert.Contains("public void Load(string url, string name) => Load(new global::Foundation.NSUrl(url), name);", output);
    }

    [Fact]
    public void TryEmitOverload_StaticMethod_IncludesStaticModifier()
    {
        var method = CreateMethod("open", MethodType.Static,
            returnType: TupleTypeSpec.Empty,
            ("url", "Foundation.URL"));

        var output = EmitMethodOverload(method);

        Assert.Contains("public static void Open(string url) => Open(new global::Foundation.NSUrl(url));", output);
    }

    [Fact]
    public void TryEmitOverload_NoUrlParam_EmitsNothing()
    {
        var method = CreateMethod("rename", MethodType.Instance,
            returnType: TupleTypeSpec.Empty,
            ("name", "Swift.String"));

        var output = EmitMethodOverload(method);

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void TryEmitOverload_OptionalUrlParam_EmitsNothing()
    {
        // An optional URL is left alone so an explicit `null` call stays unambiguous.
        var optUrl = new NamedTypeSpec("Swift.Optional");
        optUrl.GenericParameters.Add(new NamedTypeSpec("Foundation.URL"));
        var method = CreateMethodWithSpec("maybeOpen", MethodType.Instance,
            returnType: TupleTypeSpec.Empty,
            ("url", optUrl));

        var output = EmitMethodOverload(method);

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void TryEmitOverload_InOutUrlParam_EmitsNothing()
    {
        // An `inout URL` can't be forwarded from a fresh NSUrl rvalue by ref — no overload at all.
        var method = CreateMethodWithSpec("rewrite", MethodType.Instance,
            returnType: TupleTypeSpec.Empty,
            ("url", new NamedTypeSpec("Foundation.URL")), isInOut: true);

        var output = EmitMethodOverload(method);

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void TryEmitOverload_Constructor_Skips()
    {
        var method = CreateMethod("init", MethodType.Instance,
            returnType: TupleTypeSpec.Empty,
            ("url", "Foundation.URL"));
        method.IsConstructor = true;

        var output = EmitMethodOverload(method);

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void TryEmitOverload_Accessor_Skips()
    {
        var method = CreateMethod("target_Set", MethodType.Instance,
            returnType: TupleTypeSpec.Empty,
            ("url", "Foundation.URL"));
        method.IsAccessor = true;

        var output = EmitMethodOverload(method);

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void TryEmitOverload_AsyncMethod_Skips()
    {
        var method = CreateMethod("fetch", MethodType.Instance,
            returnType: TupleTypeSpec.Empty,
            ("url", "Foundation.URL"));
        method.IsAsync = true;

        var output = EmitMethodOverload(method);

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void TryEmitOverload_MissingExportedSymbol_Skips()
    {
        var method = CreateMethod("broken", MethodType.Instance,
            returnType: TupleTypeSpec.Empty,
            ("url", "Foundation.URL"));
        method.IsMissingExportedSymbol = true;

        var output = EmitMethodOverload(method);

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void TryEmitOverload_ExistingStringSibling_SkipsDuplicate()
    {
        // A real Swift sibling that already takes `string` at the URL position reserved the
        // `Load(string)` projected key in the main dedup loop. The convenience overload for a
        // URL-param `Load` would produce the identical signature → CS0111, so it must skip.
        var urlMethod = CreateMethod("load", MethodType.Instance,
            returnType: TupleTypeSpec.Empty,
            ("url", "Foundation.URL"));

        var typeDb = CreateTypeDatabase();
        var signatures = new HashSet<string>(System.StringComparer.Ordinal)
        {
            // Pre-seed the sibling `Load(string)` primary key (matches CSharpMethodName "Load").
            "Load(string)"
        };
        var writer = new StringWriter();
        var env = new MethodEnvironment(urlMethod, typeDb) { EmittedProjectedSignatures = signatures };
        UrlStringConvenienceOverloadEmitter.TryEmitOverload(new CSharpWriter(writer), env);

        Assert.Equal(string.Empty, writer.ToString());
    }

    [Fact]
    public void TryEmitOverload_DuplicateSignature_SkipsSecond()
    {
        var method1 = CreateMethod("open", MethodType.Instance,
            returnType: TupleTypeSpec.Empty,
            ("url", "Foundation.URL"));
        var method2 = CreateMethod("open", MethodType.Instance,
            returnType: TupleTypeSpec.Empty,
            ("url", "Foundation.URL"));

        var typeDb = CreateTypeDatabase();
        var signatures = new HashSet<string>(System.StringComparer.Ordinal);

        var writer1 = new StringWriter();
        var env1 = new MethodEnvironment(method1, typeDb) { EmittedProjectedSignatures = signatures };
        UrlStringConvenienceOverloadEmitter.TryEmitOverload(new CSharpWriter(writer1), env1);

        var writer2 = new StringWriter();
        var env2 = new MethodEnvironment(method2, typeDb) { EmittedProjectedSignatures = signatures };
        UrlStringConvenienceOverloadEmitter.TryEmitOverload(new CSharpWriter(writer2), env2);

        Assert.NotEmpty(writer1.ToString());
        Assert.Equal(string.Empty, writer2.ToString());
    }

    #region Helpers

    private static ModuleDecl CreateModuleDecl() => new()
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

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl) => new()
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
        MangledName = $"$s10TestModule{name.Length}{name}CN",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Subscripts = new List<SubscriptDecl>(),
        GenericParameters = new List<GenericArgumentDecl>(),
        Conformances = new List<TypeConformance>(),
        ParentDecl = moduleDecl,
        ModuleDecl = moduleDecl,
    };

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "String"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        // Foundation.URL bridges to Foundation.NSUrl (objcBridgeable struct) — mirror the real
        // FoundationDatabase.xml entry so the projection yields ObjCBridgeableProjection ("Foundation.NSUrl").
        var foundationModule = new ModuleTypeDatabase("Foundation", "/System/Library/Frameworks/Foundation.framework/Foundation");
        var urlRecord = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrl"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.URL"),
            NativeTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrl"),
            MetadataAccessor = "$s10Foundation3URLVMa",
            Flags = TypeRecordFlags.ObjCBridgeable,
            Kind = TypeRecordKind.Struct
        };
        foundationModule.RegisterType(SwiftTypeName.FromModuleQualifiedName("Foundation.URL"), urlRecord);
        typeDatabase.AddModuleDatabase(foundationModule);
        return typeDatabase;
    }

    private static MethodDecl CreateMethod(
        string name,
        MethodType methodType,
        object returnType,
        params (string name, string swiftType)[] parameters)
    {
        var specParams = new (string name, TypeSpec swiftType)[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
            specParams[i] = (parameters[i].name, new NamedTypeSpec(parameters[i].swiftType));
        return CreateMethodCore(name, methodType, returnType, isInOut: false, specParams);
    }

    private static MethodDecl CreateMethodWithSpec(
        string name,
        MethodType methodType,
        object returnType,
        (string name, TypeSpec swiftType) parameter,
        bool isInOut = false)
        => CreateMethodCore(name, methodType, returnType, isInOut, parameter);

    private static MethodDecl CreateMethodCore(
        string name,
        MethodType methodType,
        object returnType,
        bool isInOut,
        params (string name, TypeSpec swiftType)[] parameters)
    {
        var moduleDecl = CreateModuleDecl();
        var parentType = CreateClassDecl("TestClass", moduleDecl);

        TypeSpec returnTypeSpec = returnType switch
        {
            string s => new NamedTypeSpec(s),
            TypeSpec ts => ts,
            _ => TupleTypeSpec.Empty
        };

        var csSignature = new List<ArgumentDecl>
        {
            new()
            {
                Name = "",
                PrivateName = "",
                SwiftTypeSpec = returnTypeSpec,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentType,
                ModuleDecl = moduleDecl
            }
        };

        foreach (var (pName, pSpec) in parameters)
        {
            csSignature.Add(new ArgumentDecl
            {
                Name = pName,
                PrivateName = pName,
                SwiftTypeSpec = pSpec,
                IsInOut = isInOut,
                IsGeneric = false,
                ParentDecl = parentType,
                ModuleDecl = moduleDecl
            });
        }

        return new MethodDecl
        {
            Name = name,
            MangledName = "$s10TestModule9TestClassC",
            MethodType = methodType,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentType,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    private static string EmitMethodOverload(MethodDecl method)
    {
        var typeDb = CreateTypeDatabase();
        var writer = new StringWriter();
        var csWriter = new CSharpWriter(writer);
        var env = new MethodEnvironment(method, typeDb);
        UrlStringConvenienceOverloadEmitter.TryEmitOverload(csWriter, env);
        return writer.ToString();
    }

    #endregion
}
