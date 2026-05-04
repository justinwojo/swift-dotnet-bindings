// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for BaseHandler dedup key generation methods (GetProjectedCSharpMethodKey, GetMethodSignatureKey).
/// Verifies that:
/// 1. Known types produce correct resolved C# names in keys
/// 2. Unknown types fall back to AnyType via GetTypeRecordOrAnyType's default path
/// 3. Non-empty tuples resolve to AnyType via the `_ => AnyType` default (not the catch block)
/// 4. The catch blocks handle edge cases where ITypeDatabase.TryGetTypeRecord itself throws (H1+H1b fix),
///    using a ThrowingTypeDatabase to trigger the exception path
/// </summary>
public class BaseHandlerDedupTests
{
    #region GetProjectedCSharpMethodKey Tests

    [Fact]
    public void GetProjectedCSharpMethodKey_KnownType_UsesIdiomaticType()
    {
        // Swift.Int is converted to "long" via idiomatic type conversion
        var typeDatabase = new BasicTypeDatabase();
        var method = CreateMethod("doSomething", new NamedTypeSpec("Swift.Int"));

        var result = InvokeGetProjectedCSharpMethodKey(method, typeDatabase);

        Assert.NotNull(result);
        Assert.Contains("long", result);
    }

    [Fact]
    public void GetProjectedCSharpMethodKey_UnknownType_FallsBackToAnyType()
    {
        // Unknown types go through GetTypeRecordOrAnyType which returns AnyType (Swift.AnyType)
        var typeDatabase = new BasicTypeDatabase();
        var method = CreateMethod("doSomething", new NamedTypeSpec("Unknown.Module.SomeType"));

        var result = InvokeGetProjectedCSharpMethodKey(method, typeDatabase);

        // Should not throw; unknown types resolve to AnyType
        Assert.NotNull(result);
        Assert.Contains("Swift.AnyType", result);
    }

    [Fact]
    public void GetProjectedCSharpMethodKey_NonEmptyTuple_ResolvesToProjectedTuple()
    {
        // Factory resolves tuple elements (Swift.Int → long, Swift.Bool → bool),
        // producing a concrete projected tuple type in the dedup key.
        var typeDatabase = new BasicTypeDatabase();
        var moduleDecl = CreateModuleDecl();

        var tupleParam = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool")
        });
        var method = new MethodDecl
        {
            Name = "mixedMethod",
            MangledName = "$sTest",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgDecl(TupleTypeSpec.Empty), // return type (void)
                CreateArgDecl(tupleParam),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var result = InvokeGetProjectedCSharpMethodKey(method, typeDatabase);

        Assert.NotNull(result);
        // Factory resolves tuple: Swift.Int from DB → long (System.Int64 keyword alias),
        // Swift.Bool well-known → bool
        Assert.Contains("(long, bool)", result);
    }

    [Fact]
    public void GetProjectedCSharpMethodKey_ThrowingTypeDatabase_CatchBlockFallsBackToString()
    {
        // H1 catch-path test: When TryGetTypeRecord throws, the catch block in
        // GetProjectedCSharpMethodKey should swallow the exception and use the
        // type's ToString() as a fallback string in the dedup key.
        var typeDatabase = new ThrowingTypeDatabase();
        var method = CreateMethod("doSomething", new NamedTypeSpec("Crashing.Module.BadType"));

        var result = InvokeGetProjectedCSharpMethodKey(method, typeDatabase);

        // Catch block should produce a string fallback containing the type name
        Assert.NotNull(result);
        Assert.Contains("Crashing.Module.BadType", result);
    }

    [Fact]
    public void GetProjectedCSharpMethodKey_AsyncMethod_IncludesCancellationToken()
    {
        var typeDatabase = new BasicTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var method = new MethodDecl
        {
            Name = "fetchData",
            MangledName = "$sTest",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgDecl(TupleTypeSpec.Empty), // return type
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = true,
            Visibility = Visibility.Public
        };

        var result = InvokeGetProjectedCSharpMethodKey(method, typeDatabase);

        Assert.Contains("System.Threading.CancellationToken", result);
    }

    #endregion

    #region GetMethodSignatureKey Tests

    [Fact]
    public void GetMethodSignatureKey_KnownType_UsesResolvedCSharpName()
    {
        // Swift.Int resolves to CSharpTypeName "long" (keyword alias)
        var typeDatabase = new BasicTypeDatabase();
        var method = CreateMethod("doSomething", new NamedTypeSpec("Swift.Int"));

        var result = InvokeGetMethodSignatureKey(method, typeDatabase);

        Assert.NotNull(result);
        Assert.Contains("long", result);
        Assert.StartsWith("method:", result);
    }

    [Fact]
    public void GetMethodSignatureKey_UnknownType_FallsBackToAnyType()
    {
        var typeDatabase = new BasicTypeDatabase();
        var method = CreateMethod("doSomething", new NamedTypeSpec("Unknown.Module.SomeType"));

        var result = InvokeGetMethodSignatureKey(method, typeDatabase);

        // Unknown types resolve to AnyType
        Assert.NotNull(result);
        Assert.Contains("Swift.AnyType", result);
    }

    [Fact]
    public void GetMethodSignatureKey_Constructor_PrefixesWithCtor()
    {
        var typeDatabase = new BasicTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$sTest",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgDecl(TupleTypeSpec.Empty), // return type
                CreateArgDecl(new NamedTypeSpec("Swift.Int")),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var result = InvokeGetMethodSignatureKey(method, typeDatabase);

        Assert.StartsWith("ctor:", result);
    }

    [Fact]
    public void GetMethodSignatureKey_NonEmptyTuple_ResolvesToAnyType()
    {
        // Non-empty tuples hit the `_ => AnyType` default in GetTypeRecordOrAnyType(TypeSpec),
        // so they resolve without throwing. This tests the normal AnyType fallback, not the catch block.
        var typeDatabase = new BasicTypeDatabase();
        var moduleDecl = CreateModuleDecl();
        var tupleParam = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool")
        });
        var method = new MethodDecl
        {
            Name = "process",
            MangledName = "$sTest",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgDecl(TupleTypeSpec.Empty), // return type
                CreateArgDecl(tupleParam),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var result = InvokeGetMethodSignatureKey(method, typeDatabase);

        Assert.NotNull(result);
        Assert.Contains("Swift.AnyType", result);
    }

    [Fact]
    public void GetMethodSignatureKey_ThrowingTypeDatabase_CatchBlockFallsBackToString()
    {
        // H1b catch-path test: When TryGetTypeRecord throws, the catch block in
        // GetMethodSignatureKey should swallow the exception and use the
        // type's ToString() as a fallback string in the dedup key.
        var typeDatabase = new ThrowingTypeDatabase();
        var method = CreateMethod("process", new NamedTypeSpec("Crashing.Module.BadType"));

        var result = InvokeGetMethodSignatureKey(method, typeDatabase);

        // Catch block should produce a string fallback containing the type name
        Assert.NotNull(result);
        Assert.StartsWith("method:", result);
        Assert.Contains("Crashing.Module.BadType", result);
    }

    [Fact]
    public void GetMethodSignatureKey_DistinctLabels_ProduceDistinctKeys()
    {
        // Two methods with identical positional types but different argument labels
        // (e.g. `request(_:didCreateTask:)` vs `request(_:didReceiveTask:)`) must produce
        // different primary keys so both flow through to secondary (projected C#) dedup
        // for numeric-suffix disambiguation, instead of one being silently dropped.
        var typeDatabase = new BasicTypeDatabase();
        var moduleDecl = CreateModuleDecl();

        var paramType = new NamedTypeSpec("Swift.Int");
        var methodA = new MethodDecl
        {
            Name = "process",
            MangledName = "$sTestA",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgDecl(TupleTypeSpec.Empty),
                new ArgumentDecl
                {
                    Name = "valueLabel",
                    PrivateName = "value",
                    SwiftTypeSpec = paramType,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl,
                },
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
        };
        var methodB = new MethodDecl
        {
            Name = "process",
            MangledName = "$sTestB",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgDecl(TupleTypeSpec.Empty),
                new ArgumentDecl
                {
                    Name = "otherLabel",
                    PrivateName = "value",
                    SwiftTypeSpec = paramType,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl,
                },
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
        };

        var keyA = InvokeGetMethodSignatureKey(methodA, typeDatabase);
        var keyB = InvokeGetMethodSignatureKey(methodB, typeDatabase);

        Assert.NotEqual(keyA, keyB);
        Assert.Contains("valueLabel:", keyA);
        Assert.Contains("otherLabel:", keyB);
    }

    [Fact]
    public void GetMethodSignatureKey_AsyncDifference_ProducesDistinctKey()
    {
        // `f()` and `f() async` must dedup independently — Swift permits both.
        var typeDatabase = new BasicTypeDatabase();
        var sync = CreateMethod("fetch", new NamedTypeSpec("Swift.Int"));
        var asyncMethod = CreateMethod("fetch", new NamedTypeSpec("Swift.Int"));
        asyncMethod.IsAsync = true;

        var keySync = InvokeGetMethodSignatureKey(sync, typeDatabase);
        var keyAsync = InvokeGetMethodSignatureKey(asyncMethod, typeDatabase);

        Assert.NotEqual(keySync, keyAsync);
        Assert.Contains("|async", keyAsync);
        Assert.DoesNotContain("|async", keySync);
    }

    [Fact]
    public void GetMethodSignatureKey_ThrowsDifference_ProducesDistinctKey()
    {
        // `f()` and `f() throws` must dedup independently — Swift permits both.
        var typeDatabase = new BasicTypeDatabase();
        var nonThrowing = CreateMethod("compute", new NamedTypeSpec("Swift.Int"));
        var throwing = CreateMethod("compute", new NamedTypeSpec("Swift.Int"));
        throwing.Throws = true;

        var keyNonThrowing = InvokeGetMethodSignatureKey(nonThrowing, typeDatabase);
        var keyThrowing = InvokeGetMethodSignatureKey(throwing, typeDatabase);

        Assert.NotEqual(keyNonThrowing, keyThrowing);
        Assert.Contains("|throws", keyThrowing);
        Assert.DoesNotContain("|throws", keyNonThrowing);
    }

    [Fact]
    public void GetMethodSignatureKey_IdenticalLabelsAndTypes_ProducesIdenticalKeys()
    {
        // Genuine duplicates (same name, labels, types, qualifiers) must still collide
        // so the dedup HashSet drops the redundant copy.
        var typeDatabase = new BasicTypeDatabase();
        var methodA = CreateMethod("process", new NamedTypeSpec("Swift.Int"));
        var methodB = CreateMethod("process", new NamedTypeSpec("Swift.Int"));

        var keyA = InvokeGetMethodSignatureKey(methodA, typeDatabase);
        var keyB = InvokeGetMethodSignatureKey(methodB, typeDatabase);

        Assert.Equal(keyA, keyB);
    }

    #endregion

    #region Helpers

    private static MethodDecl CreateMethod(string name, TypeSpec paramType)
    {
        var moduleDecl = CreateModuleDecl();
        return new MethodDecl
        {
            Name = name,
            MangledName = "$sTest",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgDecl(TupleTypeSpec.Empty), // return type (void)
                CreateArgDecl(paramType), // parameter
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    /// <summary>
    /// Builds a method whose <see cref="MethodDecl.ParentDecl"/> is a generic StructDecl
    /// carrying a single generic parameter with the given <paramref name="sugaredName"/> and
    /// <paramref name="canonicalName"/>. Used to exercise the source-level (sugared) generic
    /// parameter recogniser path inside <c>BaseHandler.CollectVisibleGenericParamNames</c>.
    /// </summary>
    private static MethodDecl CreateMethodInGenericType(
        string name, TypeSpec paramType, string sugaredName, string canonicalName)
    {
        var moduleDecl = CreateModuleDecl();
        var parentStruct = new StructDecl
        {
            Name = "Container",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Container"),
            MangledName = "$sTestModuleContainer",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            IsFrozen = false,
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl(
                    TypeName: canonicalName,
                    SugaredTypeName: sugaredName,
                    GenericConformances: new List<GenericParameterConformance>(),
                    AssosiatedTypeConformances: new List<GenericParameterConformance>())
            }
        };

        return new MethodDecl
        {
            Name = name,
            MangledName = "$sTest",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgDecl(TupleTypeSpec.Empty), // return type (void)
                CreateArgDecl(paramType), // parameter
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentStruct,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static ArgumentDecl CreateArgDecl(TypeSpec typeSpec)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = "arg",
            PrivateName = "",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
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

    private static string InvokeGetProjectedCSharpMethodKey(MethodDecl methodDecl, ITypeDatabase typeDatabase)
    {
        var method = typeof(BaseHandler).GetMethod(
            "GetProjectedCSharpMethodKey",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (string)method!.Invoke(null, new object?[] { methodDecl, typeDatabase, null })!;
    }

    private static string InvokeGetMethodSignatureKey(MethodDecl methodDecl, ITypeDatabase typeDatabase)
    {
        var method = typeof(BaseHandler).GetMethod(
            "GetMethodSignatureKey",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (string)method!.Invoke(null, new object?[] { methodDecl, typeDatabase, null })!;
    }

    [Fact]
    public void GetProjectedCSharpMethodKey_DefaultTrimmedOverload_MatchesExplicitOverload()
    {
        // Verify that a method with defaults, when trimmed, produces the same projected key
        // as an explicit overload with the same signature.
        // find(query: String) should produce the same key regardless of where it came from.
        var typeDatabase = new BasicTypeDatabase();
        var moduleDecl = CreateModuleDecl();

        // Explicit 1-param method: find(query: String)
        var explicitMethod = new MethodDecl
        {
            Name = "find",
            MangledName = "$sExplicit",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgDecl(TupleTypeSpec.Empty), // return type
                new ArgumentDecl
                {
                    Name = "query",
                    PrivateName = "query",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        // Trimmed overload (simulated): find(query: String) — same signature
        var trimmedMethod = new MethodDecl
        {
            Name = "find",
            MangledName = "$sTrimmed",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgDecl(TupleTypeSpec.Empty), // return type
                new ArgumentDecl
                {
                    Name = "query",
                    PrivateName = "query",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var key1 = InvokeGetProjectedCSharpMethodKey(explicitMethod, typeDatabase);
        var key2 = InvokeGetProjectedCSharpMethodKey(trimmedMethod, typeDatabase);

        // Both should produce the same projected C# key
        Assert.Equal(key1, key2);
    }

    #endregion

    #region Nullable Reference Type Dedup Tests

    [Fact]
    public void GetProjectedCSharpMethodKey_OptionalClass_SameKeyAsNonOptionalClass()
    {
        // Optional<Class> and bare Class should produce the same projected key
        // because nullable reference annotations are erased at C# runtime
        var typeDatabase = new DedupTypeDatabase();
        var method1 = CreateMethod("request", new NamedTypeSpec("Alamofire.AFError"));
        var method2 = CreateMethod("request", new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Alamofire.AFError")));

        var key1 = InvokeGetProjectedCSharpMethodKey(method1, typeDatabase);
        var key2 = InvokeGetProjectedCSharpMethodKey(method2, typeDatabase);

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void GetProjectedCSharpMethodKey_OptionalValueType_DifferentKeyFromNonOptional()
    {
        // Optional<Int> and bare Int should produce DIFFERENT keys
        // because Nullable<T> is a distinct type for value types
        var typeDatabase = new DedupTypeDatabase();
        var method1 = CreateMethod("process", new NamedTypeSpec("Swift.Int"));
        var method2 = CreateMethod("process", new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Int")));

        var key1 = InvokeGetProjectedCSharpMethodKey(method1, typeDatabase);
        var key2 = InvokeGetProjectedCSharpMethodKey(method2, typeDatabase);

        Assert.NotEqual(key1, key2);
    }

    // RealityFoundation FromToByAction<Value>: ctor(from: Value?, to: Value, …) and
    // ctor(to: Value, by: Value, …) trigger CS0111 once their default-trimmed overloads
    // collapse together. C# treats T? and T as the same overload for reference-constrained
    // generics, so the projected dedup key MUST collapse Optional<GenericParam> onto the
    // bare GenericParam form. Without this, NormalizeContainerForOverloadKey returns
    // "Swift.SwiftOptional" for Optional<τ_0_0> vs "Swift.AnyType" for bare τ_0_0 and
    // dedup misses the collision. swift-api-digester emits the ABI-canonical τ_*_* form
    // for kGenericTypeParam — that's the shape the parser produces today.
    [Theory]
    [InlineData("τ_0_0")]
    [InlineData("τ_1_2")]
    [InlineData("T")]
    public void GetProjectedCSharpMethodKey_OptionalGenericParam_SameKeyAsBareGenericParam(string genericName)
    {
        var typeDatabase = new DedupTypeDatabase();
        var bare = CreateMethod("doSomething", new NamedTypeSpec(genericName));
        var optional = CreateMethod("doSomething", new NamedTypeSpec("Swift.Optional", new NamedTypeSpec(genericName)));

        var bareKey = InvokeGetProjectedCSharpMethodKey(bare, typeDatabase);
        var optionalKey = InvokeGetProjectedCSharpMethodKey(optional, typeDatabase);

        Assert.Equal(bareKey, optionalKey);
    }

    // Sugared generic parameter names: swift-api-digester emits the source-level form
    // (`Value`, `Element`) inside the method's TypeSpec when the parent TypeDecl is
    // compiled (not synthesised). The dedup key must still collapse Optional<Value> onto
    // bare Value, but TypeSpecHelpers.IsGenericTypeParameter cannot recognise multi-char
    // sugared names by heuristic alone — CollectVisibleGenericParamNames pulls them from
    // the parent's GenericArgumentDecl. Test fixture: a TypeDecl<Value> with a method
    // taking Value vs Optional<Value>.
    [Theory]
    [InlineData("Value", "τ_0_0")]
    [InlineData("Element", "τ_0_0")]
    public void GetProjectedCSharpMethodKey_OptionalSugaredGenericParam_SameKeyAsBareSugaredGenericParam(
        string sugaredName, string canonicalName)
    {
        var typeDatabase = new DedupTypeDatabase();
        var bare = CreateMethodInGenericType("doSomething", new NamedTypeSpec(sugaredName), sugaredName, canonicalName);
        var optional = CreateMethodInGenericType("doSomething",
            new NamedTypeSpec("Swift.Optional", new NamedTypeSpec(sugaredName)), sugaredName, canonicalName);

        var bareKey = InvokeGetProjectedCSharpMethodKey(bare, typeDatabase);
        var optionalKey = InvokeGetProjectedCSharpMethodKey(optional, typeDatabase);

        Assert.Equal(bareKey, optionalKey);
    }

    #endregion

    #region Test Type Database

    /// <summary>
    /// A type database with basic Swift types for testing dedup key generation.
    /// </summary>
    private class BasicTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types = new()
        {
            ["Swift.Int"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            },
            ["Swift.Bool"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            },
            ["Swift.String"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            }
        };

        public string? AsyncLibraryName => null;

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) =>
            _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [NotNullWhen(true)] out TypeRecord? record)
        {
            return _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record);
        }

        public string GetLibraryPath(string moduleName) => "";

        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion

    #region Throwing Type Database

    /// <summary>
    /// A type database that throws from TryGetTypeRecord for any type not in its
    /// internal set. Used to exercise the catch blocks in GetProjectedCSharpMethodKey
    /// and GetMethodSignatureKey (H1+H1b).
    /// </summary>
    private class ThrowingTypeDatabase : ITypeDatabase
    {
        public string? AsyncLibraryName => null;

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => false;

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [NotNullWhen(true)] out TypeRecord? record)
        {
            throw new InvalidOperationException(
                $"Simulated database error for type '{swiftTypeName.ModuleQualifiedName}'");
        }

        public string GetLibraryPath(string moduleName) => "";

        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion

    #region Dedup Type Database (with class types)

    /// <summary>
    /// A type database with class and value types for testing nullable reference type dedup.
    /// </summary>
    private class DedupTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types = new()
        {
            ["Swift.Int"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            },
            ["Swift.Optional"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            },
            ["Alamofire.AFError"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Alamofire", "AFError"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Alamofire.AFError"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            },
        };

        public string? AsyncLibraryName => null;

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) =>
            _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [NotNullWhen(true)] out TypeRecord? record)
        {
            return _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record);
        }

        public string GetLibraryPath(string moduleName) => "";

        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion
}
