// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

public class NativeIntOverloadEmitterTests
{
    [Fact]
    public void TryEmitOverload_SingleNintParam_EmitsIntOverload()
    {
        var method = CreateMethod("skip", MethodType.Instance,
            returnType: "Swift.Int",
            ("count", "Swift.Int"));

        var output = EmitMethodOverload(method);

        // Return type resolves via TypeDatabase: Swift.Int → System.IntPtr (nint in real pipeline)
        Assert.Contains("Skip(int count) => Skip((nint)count);", output);
    }

    [Fact]
    public void TryEmitOverload_SingleNuintParam_EmitsUintOverload()
    {
        var method = CreateMethod("index", MethodType.Instance,
            returnType: "Swift.UInt",
            ("position", "Swift.UInt"));

        var output = EmitMethodOverload(method);

        // Return type resolves via TypeDatabase: Swift.UInt → System.UIntPtr (nuint in real pipeline)
        Assert.Contains("Index(uint position) => Index((nuint)position);", output);
    }

    [Fact]
    public void TryEmitOverload_MultipleNintParams_EmitsOverloadForAll()
    {
        var method = CreateMethod("limit", MethodType.Instance,
            returnType: TupleTypeSpec.Empty,
            ("count", "Swift.Int"), ("offset", "Swift.Int"));

        var output = EmitMethodOverload(method);

        Assert.Contains("public void Limit(int count, int offset) => Limit((nint)count, (nint)offset);", output);
    }

    [Fact]
    public void TryEmitOverload_MixedParams_OnlyConvertsNintParams()
    {
        var method = CreateMethod("setItem", MethodType.Instance,
            returnType: TupleTypeSpec.Empty,
            ("name", "Swift.String"), ("index", "Swift.Int"));

        var output = EmitMethodOverload(method);

        Assert.Contains("int index", output);
        Assert.Contains("string name", output);
        Assert.Contains("(nint)index", output);
        Assert.DoesNotContain("(nint)name", output);
    }

    [Fact]
    public void TryEmitOverload_NoNintParams_EmitsNothing()
    {
        var method = CreateMethod("getName", MethodType.Instance,
            returnType: "Swift.String",
            ("id", "Swift.String"));

        var output = EmitMethodOverload(method);

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void TryEmitOverload_StaticMethod_IncludesStaticModifier()
    {
        var method = CreateMethod("create", MethodType.Static,
            returnType: TupleTypeSpec.Empty,
            ("count", "Swift.Int"));

        var output = EmitMethodOverload(method);

        Assert.Contains("public static void Create(int count)", output);
    }

    [Fact]
    public void TryEmitOverload_Constructor_Skips()
    {
        var method = CreateMethod("init", MethodType.Instance,
            returnType: TupleTypeSpec.Empty,
            ("count", "Swift.Int"));
        method.IsConstructor = true;

        var output = EmitMethodOverload(method);

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void TryEmitOverload_Accessor_Skips()
    {
        var method = CreateMethod("value_Get", MethodType.Instance,
            returnType: "Swift.Int");
        method.IsAccessor = true;

        var output = EmitMethodOverload(method);

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void TryEmitOverload_AsyncMethod_Skips()
    {
        var method = CreateMethod("fetch", MethodType.Instance,
            returnType: "Swift.Int",
            ("count", "Swift.Int"));
        method.IsAsync = true;

        var output = EmitMethodOverload(method);

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void TryEmitOverload_MissingExportedSymbol_Skips()
    {
        var method = CreateMethod("broken", MethodType.Instance,
            returnType: TupleTypeSpec.Empty,
            ("count", "Swift.Int"));
        method.IsMissingExportedSymbol = true;

        var output = EmitMethodOverload(method);

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void TryEmitOverload_MethodWithOwnGenerics_Skips()
    {
        var method = CreateMethod("randomInteger", MethodType.Static,
            returnType: "Swift.Int",
            ("width", "Swift.Int"));
        // Method has its own generic parameter beyond the parent type's
        method.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };

        var output = EmitMethodOverload(method);

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void TryEmitOverload_MethodOnGenericType_InheritsParentGenerics()
    {
        // Parent type has 1 generic param, method inherits it (no own generics)
        var moduleDecl = CreateModuleDecl();
        var parentType = CreateClassDecl("Observable", moduleDecl);
        parentType.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "Element", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };

        var method = CreateMethod("skip", MethodType.Instance,
            returnType: TupleTypeSpec.Empty,
            ("count", "Swift.Int"));
        method.ParentDecl = parentType;
        // Method inherits parent's generic params
        method.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "Element", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };

        var output = EmitMethodOverload(method);

        Assert.Contains("public void Skip(int count) => Skip((nint)count);", output);
    }

    [Fact]
    public void TryEmitOverload_UnqualifiedInt_RecognizedFromProtocolExtension()
    {
        // Protocol extension methods parse from swiftinterface → unqualified "Int"
        var method = CreateMethod("take", MethodType.Instance,
            returnType: TupleTypeSpec.Empty,
            ("count", "Int"));

        var output = EmitMethodOverload(method);

        Assert.Contains("public void Take(int count) => Take((nint)count);", output);
    }

    [Fact]
    public void TryEmitOverload_DuplicateSignature_SkipsSecond()
    {
        var method1 = CreateMethod("process", MethodType.Instance,
            returnType: TupleTypeSpec.Empty,
            ("count", "Swift.Int"));
        var method2 = CreateMethod("process", MethodType.Instance,
            returnType: TupleTypeSpec.Empty,
            ("count", "Swift.Int"));

        var typeDb = CreateTypeDatabase();
        var signatures = new HashSet<string>();
        var writer = new StringWriter();
        var csWriter = new CSharpWriter(writer);

        var env1 = new MethodEnvironment(method1, typeDb);
        env1.EmittedProjectedSignatures = signatures;
        NativeIntOverloadEmitter.TryEmitOverload(csWriter, env1);
        var firstOutput = writer.ToString();

        writer = new StringWriter();
        csWriter = new CSharpWriter(writer);
        var env2 = new MethodEnvironment(method2, typeDb);
        env2.EmittedProjectedSignatures = signatures;
        NativeIntOverloadEmitter.TryEmitOverload(csWriter, env2);
        var secondOutput = writer.ToString();

        Assert.NotEmpty(firstOutput);
        Assert.Equal(string.Empty, secondOutput);
    }

    [Fact]
    public void TryEmitIndexerOverload_NintParam_EmitsIntIndexer()
    {
        var subscriptDecl = CreateSubscriptDecl(hasGetter: true, hasSetter: false);
        var paramInfos = new List<(string typeName, string paramName, ITypeProjection? projection)>
        {
            ("nint", "index", null)
        };

        var writer = new StringWriter();
        var csWriter = new CSharpWriter(writer);
        NativeIntOverloadEmitter.TryEmitIndexerOverload(csWriter, subscriptDecl, "string", paramInfos);
        var output = writer.ToString();

        Assert.Contains("public string this[int index] => this[(nint)index];", output);
    }

    [Fact]
    public void TryEmitIndexerOverload_GetterAndSetter_EmitsBlockForm()
    {
        var subscriptDecl = CreateSubscriptDecl(hasGetter: true, hasSetter: true);
        var paramInfos = new List<(string typeName, string paramName, ITypeProjection? projection)>
        {
            ("nint", "index", null)
        };

        var writer = new StringWriter();
        var csWriter = new CSharpWriter(writer);
        NativeIntOverloadEmitter.TryEmitIndexerOverload(csWriter, subscriptDecl, "string", paramInfos);
        var output = writer.ToString();

        Assert.Contains("public string this[int index]", output);
        Assert.Contains("get => this[(nint)index];", output);
        Assert.Contains("set => this[(nint)index] = value;", output);
    }

    [Fact]
    public void TryEmitIndexerOverload_NoNintParams_EmitsNothing()
    {
        var subscriptDecl = CreateSubscriptDecl(hasGetter: true, hasSetter: false);
        var paramInfos = new List<(string typeName, string paramName, ITypeProjection? projection)>
        {
            ("string", "key", null)
        };

        var writer = new StringWriter();
        var csWriter = new CSharpWriter(writer);
        NativeIntOverloadEmitter.TryEmitIndexerOverload(csWriter, subscriptDecl, "string", paramInfos);
        var output = writer.ToString();

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void TryEmitIndexerOverload_DuplicateSignature_SkipsSecond()
    {
        var subscriptDecl = CreateSubscriptDecl(hasGetter: true, hasSetter: false);
        var paramInfos1 = new List<(string typeName, string paramName, ITypeProjection? projection)>
        {
            ("nint", "index", null)
        };
        var paramInfos2 = new List<(string typeName, string paramName, ITypeProjection? projection)>
        {
            ("nint", "position", null)
        };
        var emittedKeys = new HashSet<string>();

        var writer1 = new StringWriter();
        var csWriter1 = new CSharpWriter(writer1);
        NativeIntOverloadEmitter.TryEmitIndexerOverload(csWriter1, subscriptDecl, "string", paramInfos1, emittedKeys);

        var writer2 = new StringWriter();
        var csWriter2 = new CSharpWriter(writer2);
        NativeIntOverloadEmitter.TryEmitIndexerOverload(csWriter2, subscriptDecl, "string", paramInfos2, emittedKeys);

        Assert.NotEmpty(writer1.ToString());
        Assert.Equal(string.Empty, writer2.ToString());
    }

    [Fact]
    public void TryEmitIndexerOverload_ExistingIntIndexer_SkipsDuplicate()
    {
        // Simulates the two-pass approach in SubscriptHandler: all primary indexers
        // are emitted first (populating emittedKeys), then convenience overloads are
        // emitted in a second pass. A primary this[int] from subscript(Int32) takes
        // precedence over a convenience this[int] from subscript(Int).
        var subscriptDecl = CreateSubscriptDecl(hasGetter: true, hasSetter: false);
        var paramInfos = new List<(string typeName, string paramName, ITypeProjection? projection)>
        {
            ("nint", "index", null)
        };
        // Pre-populate with "int" to simulate existing subscript(Int32) → this[int]
        var emittedKeys = new HashSet<string> { "int" };

        var writer = new StringWriter();
        var csWriter = new CSharpWriter(writer);
        NativeIntOverloadEmitter.TryEmitIndexerOverload(csWriter, subscriptDecl, "string", paramInfos, emittedKeys);

        Assert.Equal(string.Empty, writer.ToString());
    }

    #region Optional nint/nuint Tests

    [Fact]
    public void TryEmitOverload_OptionalNintParam_EmitsOptionalIntOverload()
    {
        var optNint = new NamedTypeSpec("Swift.Optional");
        optNint.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var method = CreateMethodWithOptionalParam("setLimit", MethodType.Instance,
            returnType: TupleTypeSpec.Empty,
            ("limit", optNint));

        var output = EmitMethodOverload(method);

        Assert.Contains("int? limit", output);
        Assert.Contains("(nint?)limit", output);
    }

    [Fact]
    public void TryEmitOverload_OptionalNuintParam_EmitsOptionalUintOverload()
    {
        var optNuint = new NamedTypeSpec("Swift.Optional");
        optNuint.GenericParameters.Add(new NamedTypeSpec("Swift.UInt"));
        var method = CreateMethodWithOptionalParam("setIndex", MethodType.Instance,
            returnType: TupleTypeSpec.Empty,
            ("index", optNuint));

        var output = EmitMethodOverload(method);

        Assert.Contains("uint? index", output);
        Assert.Contains("(nuint?)index", output);
    }

    [Fact]
    public void TryEmitOverload_MixedOptionalAndNonOptional_ConvertsBoth()
    {
        var optNint = new NamedTypeSpec("Swift.Optional");
        optNint.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var method = CreateMethodWithMixedParams("setRange", MethodType.Instance,
            returnType: TupleTypeSpec.Empty,
            ("count", new NamedTypeSpec("Swift.Int")),
            ("limit", optNint));

        var output = EmitMethodOverload(method);

        Assert.Contains("int count", output);
        Assert.Contains("int? limit", output);
        Assert.Contains("(nint)count", output);
        Assert.Contains("(nint?)limit", output);
    }

    [Fact]
    public void TryEmitOverload_OptionalNonNintParam_EmitsNothing()
    {
        var optString = new NamedTypeSpec("Swift.Optional");
        optString.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        var method = CreateMethodWithOptionalParam("setName", MethodType.Instance,
            returnType: TupleTypeSpec.Empty,
            ("name", optString));

        var output = EmitMethodOverload(method);

        Assert.Equal(string.Empty, output);
    }

    #endregion

    #region Helpers

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
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
    }

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "IntPtr"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.UInt"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "UIntPtr"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.UInt"),
                MetadataAccessor = "$sSuMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
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
        return typeDatabase;
    }

    private static MethodDecl CreateMethod(
        string name,
        MethodType methodType,
        object returnType,
        params (string name, string swiftType)[] parameters)
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

        foreach (var (pName, pType) in parameters)
        {
            csSignature.Add(new ArgumentDecl
            {
                Name = pName,
                PrivateName = pName,
                SwiftTypeSpec = new NamedTypeSpec(pType),
                IsInOut = false,
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
            Visibility = Visibility.Public
        };
    }

    private static SubscriptDecl CreateSubscriptDecl(bool hasGetter, bool hasSetter)
    {
        var moduleDecl = CreateModuleDecl();
        var parentType = CreateClassDecl("TestClass", moduleDecl);

        var accessors = new List<AccessorDecl>();
        if (hasGetter)
            accessors.Add(new GetAccessorDecl { Method = new MethodDecl
            {
                Name = "subscript_Get",
                MangledName = "$sGet",
                MethodType = MethodType.Instance,
                IsConstructor = false,
                CSSignature = new List<ArgumentDecl>(),
                GenericParameters = new List<GenericArgumentDecl>(),
                ParentDecl = parentType,
                ModuleDecl = moduleDecl,
                Throws = false,
                IsAsync = false,
                Visibility = Visibility.Public
            }});
        if (hasSetter)
            accessors.Add(new SetAccessorDecl { Method = new MethodDecl
            {
                Name = "subscript_Set",
                MangledName = "$sSet",
                MethodType = MethodType.Instance,
                IsConstructor = false,
                CSSignature = new List<ArgumentDecl>(),
                GenericParameters = new List<GenericArgumentDecl>(),
                ParentDecl = parentType,
                ModuleDecl = moduleDecl,
                Throws = false,
                IsAsync = false,
                Visibility = Visibility.Public
            }});

        return new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$sSubscript",
            IsStatic = false,
            Accessors = accessors,
            ReturnTypeSpec = new NamedTypeSpec("Swift.String"),
            IndexParameters = new List<ArgumentDecl>(),
            ParentDecl = parentType,
            ModuleDecl = moduleDecl,
        };
    }

    private static string EmitMethodOverload(MethodDecl method)
    {
        var typeDb = CreateTypeDatabase();
        var writer = new StringWriter();
        var csWriter = new CSharpWriter(writer);
        var env = new MethodEnvironment(method, typeDb);
        NativeIntOverloadEmitter.TryEmitOverload(csWriter, env);
        return writer.ToString();
    }

    private static MethodDecl CreateMethodWithOptionalParam(
        string name,
        MethodType methodType,
        object returnType,
        (string name, TypeSpec swiftType) parameter)
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
            },
            new()
            {
                Name = parameter.name,
                PrivateName = parameter.name,
                SwiftTypeSpec = parameter.swiftType,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentType,
                ModuleDecl = moduleDecl
            }
        };

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
            Visibility = Visibility.Public
        };
    }

    private static MethodDecl CreateMethodWithMixedParams(
        string name,
        MethodType methodType,
        object returnType,
        (string name, TypeSpec swiftType) param1,
        (string name, TypeSpec swiftType) param2)
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
            },
            new()
            {
                Name = param1.name,
                PrivateName = param1.name,
                SwiftTypeSpec = param1.swiftType,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentType,
                ModuleDecl = moduleDecl
            },
            new()
            {
                Name = param2.name,
                PrivateName = param2.name,
                SwiftTypeSpec = param2.swiftType,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentType,
                ModuleDecl = moduleDecl
            }
        };

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
            Visibility = Visibility.Public
        };
    }

    #endregion
}
