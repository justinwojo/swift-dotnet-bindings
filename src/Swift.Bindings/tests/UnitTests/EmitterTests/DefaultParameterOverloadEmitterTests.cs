// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Unit tests for DefaultParameterOverloadEmitter.
/// Validates CountTrailingDefaults, BuildOverloadDecl, and TryEmitOverloads skip guards.
/// </summary>
public class DefaultParameterOverloadEmitterTests
{
    #region CountTrailingDefaults Tests

    [Fact]
    public void CountTrailingDefaults_ZeroParams_ReturnsZero()
    {
        var method = CreateMethodWithArgs();
        Assert.Equal(0, DefaultParameterOverloadEmitter.CountTrailingDefaults(method));
    }

    [Fact]
    public void CountTrailingDefaults_AllDefaults_ReturnsAll()
    {
        var method = CreateMethodWithArgs(
            CreateArg("limit", hasDefault: true),
            CreateArg("offset", hasDefault: true),
            CreateArg("page", hasDefault: true));
        Assert.Equal(3, DefaultParameterOverloadEmitter.CountTrailingDefaults(method));
    }

    [Fact]
    public void CountTrailingDefaults_NonTrailingOnly_ReturnsZero()
    {
        // (query: String = "", page: Int) — default is NOT trailing
        var method = CreateMethodWithArgs(
            CreateArg("query", hasDefault: true),
            CreateArg("page", hasDefault: false));
        Assert.Equal(0, DefaultParameterOverloadEmitter.CountTrailingDefaults(method));
    }

    [Fact]
    public void CountTrailingDefaults_Mixed_ReturnsTrailingCount()
    {
        // (query: String, limit: Int = 10, offset: Int = 0) — 2 trailing defaults
        var method = CreateMethodWithArgs(
            CreateArg("query", hasDefault: false),
            CreateArg("limit", hasDefault: true),
            CreateArg("offset", hasDefault: true));
        Assert.Equal(2, DefaultParameterOverloadEmitter.CountTrailingDefaults(method));
    }

    [Fact]
    public void CountTrailingDefaults_OneTrailing_ReturnsOne()
    {
        var method = CreateMethodWithArgs(
            CreateArg("name", hasDefault: false),
            CreateArg("verbose", hasDefault: true));
        Assert.Equal(1, DefaultParameterOverloadEmitter.CountTrailingDefaults(method));
    }

    #endregion

    #region BuildOverloadDecl Tests

    [Fact]
    public void BuildOverloadDecl_SetsUsesWrapperLibrary()
    {
        var method = CreateMethodWithArgs(
            CreateArg("a", hasDefault: false),
            CreateArg("b", hasDefault: true));

        var overload = DefaultParameterOverloadEmitter.BuildOverloadDecl(method, trimCount: 1);

        Assert.True(overload.UsesWrapperLibrary);
    }

    [Fact]
    public void BuildOverloadDecl_CorrectParamCount()
    {
        // Original: return + 3 params, trim 2 → return + 1 param
        var method = CreateMethodWithArgs(
            CreateArg("query", hasDefault: false),
            CreateArg("limit", hasDefault: true),
            CreateArg("offset", hasDefault: true));

        var overload = DefaultParameterOverloadEmitter.BuildOverloadDecl(method, trimCount: 2);

        // CSSignature[0] is return type, rest are params
        Assert.Equal(2, overload.CSSignature.Count); // return + 1 kept param
        Assert.Equal("query", overload.CSSignature[1].Name);
    }

    #endregion

    #region TryEmitOverloads Skip Guard Tests

    [Fact]
    public void TryEmitOverloads_GenericParentType_SkipsOverloads()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericContainer");

        var parentDecl = new StructDecl
        {
            Name = "GenericContainer",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.GenericContainer"),
            MangledName = "$s10TestModule16GenericContainerVN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
            },
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule16GenericContainerVMa"
        };

        var method = new MethodDecl
        {
            Name = "process",
            MangledName = "$s10TestModule16GenericContainerV7processSiyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                CreateArg("value", hasDefault: true)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        var (csOutput, swiftOutput) = EmitOverloads(method, typeDb);

        // Generic parent type → no overloads emitted
        Assert.Empty(csOutput);
        Assert.Empty(swiftOutput);
    }

    [Fact]
    public void TryEmitOverloads_SiblingCollision_SkipsOverload()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("Fetcher");

        var parentDecl = new StructDecl
        {
            Name = "Fetcher",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Fetcher"),
            MangledName = "$s10TestModule7FetcherVN",
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
            MetadataAccessor = "$s10TestModule7FetcherVMa"
        };

        // Existing sibling: fetch(query: Int) — 1 param
        var existingSibling = new MethodDecl
        {
            Name = "fetch",
            MangledName = "$s10TestModule7FetcherV5fetchySiF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                CreateArg("query", hasDefault: false)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(existingSibling);

        // Method with default: fetch(query: Int, limit: Int = 10) — trim=1 would produce fetch(query:)
        // which collides with the existing sibling
        var method = new MethodDecl
        {
            Name = "fetch",
            MangledName = "$s10TestModule7FetcherV5fetchySi_SitF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                CreateArg("query", hasDefault: false),
                CreateArg("limit", hasDefault: true)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        var (csOutput, swiftOutput) = EmitOverloads(method, typeDb);

        // Sibling collision → overload skipped
        Assert.Empty(csOutput);
        Assert.Empty(swiftOutput);
    }

    [Fact]
    public void TryEmitOverloads_Constructor_NoBackticksInFuncName()
    {
        // Regression: constructors (Name="init") were getting backtick-escaped
        // via ParserNameToSwift, producing `_dbw_`init`_HASH_N` — invalid Swift syntax.
        var (moduleDecl, typeDb) = CreateTestEnvironment("Counter");

        var parentDecl = new StructDecl
        {
            Name = "Counter",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Counter"),
            MangledName = "$s10TestModule7CounterVN",
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
            MetadataAccessor = "$s10TestModule7CounterVMa"
        };

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule7CounterVySiSiSitcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                CreateArg("start", hasDefault: false),
                CreateArg("step", hasDefault: false),
                CreateArg("limit", hasDefault: true)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        var (_, swiftOutput) = EmitOverloads(method, typeDb);

        // Must contain _dbw_init_ (no backticks)
        Assert.Contains("_dbw_init_", swiftOutput);
        // Must NOT contain backtick-escaped init
        Assert.DoesNotContain("`init`", swiftOutput);
        // Verify it's a valid static func declaration
        Assert.Contains("public static func _dbw_init_", swiftOutput);
    }

    [Fact]
    public void TryEmitOverloads_ProjectedKeyCollision_SkipsOverload()
    {
        // Pattern from ObjectMapper/Parchment: an explicit 1-param overload collides
        // with the trimmed default-param overload after C# projection.
        // find(query: Int, limit: Int = 10) → trimmed to find(query: Int)
        // find(query: Int) → explicit 1-param overload
        // Both produce the same projected C# key → skip the trimmed overload.
        var (moduleDecl, typeDb) = CreateTestEnvironment("SearchService");

        var parentDecl = new StructDecl
        {
            Name = "SearchService",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SearchService"),
            MangledName = "$s10TestModule13SearchServiceVN",
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
            MetadataAccessor = "$s10TestModule13SearchServiceVMa"
        };

        // Explicit 1-param overload: find(query: Int)
        var explicitOverload = new MethodDecl
        {
            Name = "find",
            MangledName = "$s10TestModule13SearchServiceV4findySSSgSSF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                CreateArg("query", hasDefault: false)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(explicitOverload);

        // Method with default param: find(query: Int, limit: Int = 10)
        // Trimming limit produces find(query: Int) → collides with explicit overload
        var methodWithDefault = new MethodDecl
        {
            Name = "find",
            MangledName = "$s10TestModule13SearchServiceV4findySSSgSS_SitF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                CreateArg("query", hasDefault: false),
                CreateArg("limit", hasDefault: true)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(methodWithDefault);

        var (csOutput, swiftOutput) = EmitOverloads(methodWithDefault, typeDb);

        // Sibling collision with explicit overload → trimmed overload skipped
        Assert.Empty(csOutput);
        Assert.Empty(swiftOutput);
    }

    #endregion

    #region EC-5: Method-Level Generic Skip

    [Fact]
    public void TryEmitOverloads_MethodLevelGeneric_SkipsOverloads()
    {
        // EC-5: Method-level generics produce unresolved τ_0_0 type parameters
        // in wrapper code. TryEmitOverloads must skip these methods entirely.
        var (moduleDecl, typeDb) = CreateTestEnvironment("DataRequest");

        var parentDecl = new StructDecl
        {
            Name = "DataRequest",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DataRequest"),
            MangledName = "$s10TestModule11DataRequestVN",
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
            MetadataAccessor = "$s10TestModule11DataRequestVMa"
        };

        // Method with method-level generic (τ_0_0 in parameter type)
        var method = new MethodDecl
        {
            Name = "publishResponse",
            MangledName = "$s10TestModule11DataRequestV15publishResponseyx_tF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "serializer",
                    PrivateName = "serializer",
                    SwiftTypeSpec = new NamedTypeSpec("τ_0_0"),
                    HasDefaultArg = false,
                    IsInOut = false,
                    IsGeneric = true,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                CreateArg("queue", hasDefault: true)
            },
            // Method-level generic parameters — NOT class-level
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        var (csOutput, swiftOutput) = EmitOverloads(method, typeDb);

        // Method-level generic → no overloads emitted (would produce invalid τ_0_0 in Swift code)
        Assert.Empty(csOutput);
        Assert.Empty(swiftOutput);
    }

    [Fact]
    public void TryEmitOverloads_NonGenericMethod_EmitsOverloads()
    {
        // Verify that non-generic methods with defaults still produce overloads
        var (moduleDecl, typeDb) = CreateTestEnvironment("Processor");

        var parentDecl = new StructDecl
        {
            Name = "Processor",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Processor"),
            MangledName = "$s10TestModule9ProcessorVN",
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
            MetadataAccessor = "$s10TestModule9ProcessorVMa"
        };

        var method = new MethodDecl
        {
            Name = "process",
            MangledName = "$s10TestModule9ProcessorV7processSiyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                CreateArg("value", hasDefault: false),
                CreateArg("limit", hasDefault: true)
            },
            GenericParameters = new List<GenericArgumentDecl>(), // No method-level generics
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        var (csOutput, swiftOutput) = EmitOverloads(method, typeDb);

        // Non-generic → overloads should be emitted
        Assert.NotEmpty(swiftOutput);
        Assert.Contains("_dbw_process_", swiftOutput);
    }

    #endregion

    #region EC-11: Silgen Function Name Consistency

    [Fact]
    public void GetSilgenFuncName_ProducesConsistentName()
    {
        // EC-11: The silgen function name must be consistent between EmitSwiftWrapper
        // and the @_cdecl dispatch section. GetSilgenFuncName is the single source of truth.
        var moduleDecl = new ModuleDecl
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

        var method = new MethodDecl
        {
            Name = "getFormattedExampleNumber",
            MangledName = "$s14PhoneNumberKit0bC9FormatterV24getFormattedExampleNumberySSSg_tF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                CreateArg("countryCode", hasDefault: false),
                CreateArg("type", hasDefault: true),
                CreateArg("format", hasDefault: true)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        // trim=2 (remove type + format, keep countryCode)
        var silgenName_trim2 = DefaultParameterOverloadEmitter.GetSilgenFuncName(method, 2);
        Assert.Contains("_dbw_getFormattedExampleNumber_", silgenName_trim2);
        Assert.EndsWith("_2", silgenName_trim2);

        // trim=1 (remove format, keep countryCode + type)
        var silgenName_trim1 = DefaultParameterOverloadEmitter.GetSilgenFuncName(method, 1);
        Assert.Contains("_dbw_getFormattedExampleNumber_", silgenName_trim1);
        Assert.EndsWith("_1", silgenName_trim1);

        // Different trim values produce different names
        Assert.NotEqual(silgenName_trim1, silgenName_trim2);

        // Same trim value produces same name (idempotent)
        Assert.Equal(silgenName_trim2, DefaultParameterOverloadEmitter.GetSilgenFuncName(method, 2));
    }

    [Fact]
    public void TryEmitOverloads_CdeclOverload_SilgenNameMatchesBetweenWrappers()
    {
        // EC-11: Verify that the @_silgen_name function name in EmitSwiftWrapper
        // matches the silgenTarget passed to MethodWrapperEmitter.EmitSwiftMethodWrapper.
        // If they diverge, the @_cdecl wrapper calls a non-existent function → compile error.
        var typeDb = new TypeDatabase();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

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
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.FinalFormatter"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "FinalFormatter"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.FinalFormatter"),
                MetadataAccessor = "$s10TestModule14FinalFormatterCMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
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
            ModuleDecl = null
        };

        var parentDecl = new ClassDecl
        {
            Name = "FinalFormatter",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.FinalFormatter"),
            MangledName = "$s10TestModule14FinalFormatterCN",
            IsFinal = true,
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
        moduleDecl.Types.Add(parentDecl);

        // Method with 3 params, 2 trailing defaults → 2 overloads
        var method = new MethodDecl
        {
            Name = "format",
            MangledName = "$s10TestModule14FinalFormatterC6formatySS_S2itF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                CreateArg("value", hasDefault: false),
                CreateArg("precision", hasDefault: true),
                CreateArg("style", hasDefault: true),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            UsesCdeclMethodWrapper = true,
            UsesWrapperLibrary = true,
        };
        parentDecl.Methods.Add(method);

        var emissionContext = new ModuleEmissionContext();
        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);
        var env = new MethodEnvironment(method, typeDb);
        var logger = NullLogger.Instance;

        DefaultParameterOverloadEmitter.TryEmitOverloads(
            csWriter, swiftWriter, env, logger, emissionContext);

        var swiftOutput = swiftStringWriter.ToString();

        // For each @_silgen_name function emitted, the @_cdecl wrapper must call it by name.
        // Extract all _dbw_ function names from @_silgen_name declarations and @_cdecl call sites.
        var silgenFuncNames = System.Text.RegularExpressions.Regex.Matches(
            swiftOutput, @"func (_dbw_\w+)\(")
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(m => m.Groups[1].Value)
            .ToList();

        // Must have silgen functions for trim=2 and trim=1
        Assert.True(silgenFuncNames.Count >= 2,
            $"Expected at least 2 silgen functions, got {silgenFuncNames.Count}. Output:\n{swiftOutput}");

        // Each _dbw_ function declared must also appear as a call target
        foreach (var funcName in silgenFuncNames)
        {
            // The function should appear as a call: obj.funcName( or TypeName.funcName(
            var callPattern = $".{funcName}(";
            Assert.Contains(callPattern, swiftOutput);
        }

        // The trim suffixes should be _1 and _2 (not _3)
        Assert.Contains(silgenFuncNames, n => n.EndsWith("_1"));
        Assert.Contains(silgenFuncNames, n => n.EndsWith("_2"));
    }

    #endregion

    #region Availability Propagation Tests

    [Fact]
    public void TryEmitOverloads_ConstructorOnAvailableType_EmitsAvailabilityOnExtension()
    {
        // Regression for CryptoKit: SecureEnclave.MLDSA65.PrivateKey is iOS 26+. The
        // @_silgen_name wrapper inside `extension ... {}` must carry @available or the
        // Swift compiler rejects it as "referencing iOS 26 API from older context".
        var (moduleDecl, typeDb) = CreateTestEnvironment("Vault");

        var parentDecl = new StructDecl
        {
            Name = "Vault",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Vault"),
            MangledName = "$s10TestModule5VaultVN",
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
            MetadataAccessor = "$s10TestModule5VaultVMa",
            AvailabilityAnnotations = new List<AvailabilityAnnotation>
            {
                new("iOS", "26.0", null, null, false, false, null, null),
                new("macOS", "26.0", null, null, false, false, null, null),
            }
        };

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule5VaultV4nameSSAeA5TokenVSgtcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                CreateArg("name", hasDefault: false),
                CreateArg("token", hasDefault: true)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        var (_, swiftOutput) = EmitOverloads(method, typeDb);

        Assert.NotEmpty(swiftOutput);
        Assert.Contains("@available(iOS 26.0, *)", swiftOutput);
        Assert.Contains("@available(macOS 26.0, *)", swiftOutput);
    }

    [Fact]
    public void TryEmitOverloads_MethodOnAvailableType_EmitsAvailabilityOnExtension()
    {
        // Non-constructor methods also go inside extensions. The @available annotation
        // must precede the @_silgen_name attribute inside the extension block.
        var (moduleDecl, typeDb) = CreateTestEnvironment("Hasher");

        var parentDecl = new StructDecl
        {
            Name = "Hasher",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Hasher"),
            MangledName = "$s10TestModule6HasherVN",
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
            MetadataAccessor = "$s10TestModule6HasherVMa",
            AvailabilityAnnotations = new List<AvailabilityAnnotation>
            {
                new("iOS", "17.0", null, null, false, false, null, null),
            }
        };

        var method = new MethodDecl
        {
            Name = "update",
            MangledName = "$s10TestModule6HasherV6updateySiSi_SitF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                CreateArg("length", hasDefault: false),
                CreateArg("padding", hasDefault: true)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        var (_, swiftOutput) = EmitOverloads(method, typeDb);

        Assert.NotEmpty(swiftOutput);
        Assert.Contains("@available(iOS 17.0, *)", swiftOutput);
        // The availability attribute must precede the `extension` keyword — the
        // extended type name itself is gated by availability, so an inner-function
        // @available arrives too late for the Swift compiler.
        var availIdx = swiftOutput.IndexOf("@available(iOS 17.0, *)");
        var extensionIdx = swiftOutput.IndexOf("extension TestModule.Hasher");
        Assert.True(availIdx >= 0 && extensionIdx >= 0 && availIdx < extensionIdx,
            $"@available must precede the extension line. Output:\n{swiftOutput}");
    }

    [Fact]
    public void TryEmitOverloads_MemberLevelAvailability_OverridesParentInherit()
    {
        // If the method itself has a stricter availability annotation, the strictest
        // floor per platform wins — the looser parent annotation is redundant and gets
        // collapsed. Previously we emitted both lines; that was confusing and masked
        // availability bugs in stacked CSM wrappers (SHA3 conformers losing iOS 26).
        var (moduleDecl, typeDb) = CreateTestEnvironment("Lightweight");

        var parentDecl = new StructDecl
        {
            Name = "Lightweight",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Lightweight"),
            MangledName = "$s10TestModule11LightweightVN",
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
            MetadataAccessor = "$s10TestModule11LightweightVMa",
            AvailabilityAnnotations = new List<AvailabilityAnnotation>
            {
                new("iOS", "15.0", null, null, false, false, null, null),
            }
        };

        var method = new MethodDecl
        {
            Name = "configure",
            MangledName = "$s10TestModule11LightweightV9configureySi_SitF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                CreateArg("value", hasDefault: false),
                CreateArg("fallback", hasDefault: true)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            AvailabilityAnnotations = new List<AvailabilityAnnotation>
            {
                new("iOS", "18.0", null, null, false, false, null, null),
            }
        };
        parentDecl.Methods.Add(method);

        var (_, swiftOutput) = EmitOverloads(method, typeDb);

        Assert.NotEmpty(swiftOutput);
        // Strictest wins: iOS 18.0 (member) is emitted; iOS 15.0 (parent) is redundant and
        // deliberately dropped so stacked annotations don't under-guard the call site.
        Assert.Contains("@available(iOS 18.0, *)", swiftOutput);
        Assert.DoesNotContain("@available(iOS 15.0, *)", swiftOutput);
    }

    #endregion

    #region Helpers

    private static ArgumentDecl CreateArg(string name, bool hasDefault)
    {
        return new ArgumentDecl
        {
            Name = name,
            PrivateName = name,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasDefaultArg = hasDefault,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ArgumentDecl CreateReturnArg(ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            Name = "",
            PrivateName = "",
            SwiftTypeSpec = TupleTypeSpec.Empty,
            HasDefaultArg = false,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    /// <summary>
    /// Creates a MethodDecl with the given args as parameters (return type auto-added as void).
    /// </summary>
    private static MethodDecl CreateMethodWithArgs(params ArgumentDecl[] args)
    {
        var moduleDecl = new ModuleDecl
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

        var csSignature = new List<ArgumentDecl>
        {
            CreateReturnArg(moduleDecl)
        };
        csSignature.AddRange(args);

        return new MethodDecl
        {
            Name = "testMethod",
            MangledName = "$s10TestModule10testMethodyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironment(string typeName)
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
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", typeName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
                MetadataAccessor = $"$s10TestModule{typeName.Length}{typeName}VMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
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
            ModuleDecl = null
        };

        return (moduleDecl, typeDb);
    }

    private static (string csOutput, string swiftOutput) EmitOverloads(MethodDecl methodDecl, TypeDatabase typeDatabase)
    {
        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var env = new MethodEnvironment(methodDecl, typeDatabase);
        var logger = NullLogger.Instance;

        DefaultParameterOverloadEmitter.TryEmitOverloads(csWriter, swiftWriter, env, logger);

        return (csStringWriter.ToString(), swiftStringWriter.ToString());
    }

    #endregion

    #region EmitDebugParamWrapper Autoclosure Tests

    [Fact]
    public void EmitDebugParamWrapper_AutoclosureParam_InvokedWithParens()
    {
        // Issue N: @autoclosure params in _dbg_* wrappers must be invoked with ()
        // when forwarded to the original method. Without this, Swift complains about
        // "add () to forward '@autoclosure' parameter".
        var (moduleDecl, typeDb) = CreateTestEnvironment("LottieLogger");
        var parentDecl = new StructDecl
        {
            Name = "LottieLogger",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.LottieLogger"),
            MangledName = "$s10TestModule12LottieLoggerVN",
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
            MetadataAccessor = "$s10TestModule12LottieLoggerVMa"
        };

        // Create @autoclosure () -> Bool parameter
        var autoclosureType = new ClosureTypeSpec(TupleTypeSpec.Empty, new NamedTypeSpec("Swift.Bool"));
        autoclosureType.Attributes.Add(new TypeSpecAttribute("autoclosure"));
        autoclosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Create debug parameter (file: StaticString = #file)
        var debugParam = new ArgumentDecl
        {
            Name = "file",
            PrivateName = "file",
            SwiftTypeSpec = new NamedTypeSpec("Swift.StaticString"),
            HasDefaultArg = true,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var method = new MethodDecl
        {
            Name = "assert",
            MangledName = "$s10TestModule12LottieLoggerV6assertyyXK_SSXKSSzcFtF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "arg0",
                    PrivateName = "arg0",
                    SwiftTypeSpec = autoclosureType,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                debugParam
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        // Emit the debug param wrapper
        var stringWriter = new StringWriter();
        var swiftWriter = new SwiftWriter(stringWriter);
        var env = new MethodEnvironment(method, typeDb);
        DefaultParameterOverloadEmitter.EmitDebugParamWrapper(swiftWriter, env);
        var output = stringWriter.ToString();

        // The autoclosure param should be invoked with () in the call
        Assert.Contains("arg0()", output);
        // The wrapper should strip the debug param (file)
        Assert.DoesNotContain("file:", output);
    }

    #endregion

    #region AllTrailingDefaultsAreCSharpMappable Tests

    [Fact]
    public void AllTrailingDefaultsAreCSharpMappable_AllLiterals_ReturnsTrue()
    {
        var (_, typeDb) = CreateTestEnvironment("Config");
        var method = CreateMethodWithArgs(
            CreateArg("query", hasDefault: false),
            CreateArgWithDefault("limit", "10"),
            CreateArgWithDefault("offset", "0"));
        Assert.True(DefaultParameterOverloadEmitter.AllTrailingDefaultsAreCSharpMappable(method, typeDb));
    }

    [Fact]
    public void AllTrailingDefaultsAreCSharpMappable_BoolLiterals_ReturnsTrue()
    {
        var (_, typeDb) = CreateTestEnvironment("Config");
        var method = CreateMethodWithArgs(
            CreateArgWithDefault("verbose", "true"),
            CreateArgWithDefault("strict", "false"));
        Assert.True(DefaultParameterOverloadEmitter.AllTrailingDefaultsAreCSharpMappable(method, typeDb));
    }

    [Fact]
    public void AllTrailingDefaultsAreCSharpMappable_MixedWithComplex_ReturnsFalse()
    {
        var (_, typeDb) = CreateTestEnvironment("Config");
        var method = CreateMethodWithArgs(
            CreateArg("query", hasDefault: false),
            CreateArgWithDefault("config", "Config()"), // unmappable
            CreateArgWithDefault("limit", "10")); // mappable but gap before
        Assert.False(DefaultParameterOverloadEmitter.AllTrailingDefaultsAreCSharpMappable(method, typeDb));
    }

    [Fact]
    public void AllTrailingDefaultsAreCSharpMappable_NoDefaults_ReturnsFalse()
    {
        var (_, typeDb) = CreateTestEnvironment("Config");
        var method = CreateMethodWithArgs(
            CreateArg("x", hasDefault: false),
            CreateArg("y", hasDefault: false));
        Assert.False(DefaultParameterOverloadEmitter.AllTrailingDefaultsAreCSharpMappable(method, typeDb));
    }

    [Fact]
    public void AllTrailingDefaultsAreCSharpMappable_MissingExpression_ReturnsFalse()
    {
        var (_, typeDb) = CreateTestEnvironment("Config");
        // HasDefaultArg=true but no SwiftDefaultExpression (ABI-only default, no swiftinterface)
        var method = CreateMethodWithArgs(
            CreateArg("x", hasDefault: false),
            CreateArg("y", hasDefault: true));
        Assert.False(DefaultParameterOverloadEmitter.AllTrailingDefaultsAreCSharpMappable(method, typeDb));
    }

    [Fact]
    public void AllTrailingDefaultsAreCSharpMappable_NilDefault_ReturnsTrue()
    {
        var (_, typeDb) = CreateTestEnvironment("Config");
        var optionalArg = CreateArgWithDefault("value", "nil");
        optionalArg.SwiftTypeSpec = new NamedTypeSpec("Swift.Optional");
        ((NamedTypeSpec)optionalArg.SwiftTypeSpec).GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var method = CreateMethodWithArgs(optionalArg);
        Assert.True(DefaultParameterOverloadEmitter.AllTrailingDefaultsAreCSharpMappable(method, typeDb));
    }

    private static ArgumentDecl CreateArgWithDefault(string name, string swiftDefaultExpr)
    {
        return new ArgumentDecl
        {
            Name = name,
            PrivateName = name,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasDefaultArg = true,
            SwiftDefaultExpression = swiftDefaultExpr,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    #endregion

    #region @_cdecl Method Wrapper Inheritance

    [Fact]
    public void BuildOverloadDecl_SetsUsesWrapperLibrary_True()
    {
        // BuildOverloadDecl unconditionally sets UsesWrapperLibrary = true.
        // This is expected — overloads always go through the wrapper library.
        var method = CreateMethodWithArgs(
            CreateArg("a", hasDefault: false),
            CreateArg("b", hasDefault: true));
        method.UsesWrapperLibrary = false;

        var overload = DefaultParameterOverloadEmitter.BuildOverloadDecl(method, 1);

        Assert.True(overload.UsesWrapperLibrary);
    }

    [Fact]
    public void OverloadCdeclCheck_UsesOriginalMethod_NotOverload()
    {
        // The overload emitter should check the ORIGINAL method's UsesCdeclMethodWrapper
        // flag, not the overload's. BuildOverloadDecl sets UsesWrapperLibrary=true on
        // overloads, which would cause ShouldEmitWrapper to return false if called on
        // the overload directly.
        var method = CreateMethodWithArgs(
            CreateArg("a", hasDefault: false),
            CreateArg("b", hasDefault: true));
        method.UsesCdeclMethodWrapper = true;

        var overload = DefaultParameterOverloadEmitter.BuildOverloadDecl(method, 1);

        // Overload has UsesWrapperLibrary=true, so ShouldEmitWrapper would reject it
        Assert.True(overload.UsesWrapperLibrary);
        Assert.False(overload.UsesCdeclMethodWrapper); // Not set yet (set by overload emitter)

        // But original method has the flag — the overload emitter should check this
        Assert.True(method.UsesCdeclMethodWrapper);
    }

    [Fact]
    public void TryEmitOverloads_MethodWithCdecl_EmitsBothSilgenAndCdeclWrappers()
    {
        // Full integration: a class instance method with UsesCdeclMethodWrapper=true
        // and a trailing default param should produce:
        //   1. A @_silgen_name Swift wrapper (calls original method with fewer args)
        //   2. A @_cdecl Swift wrapper on top (calls the @_silgen_name function)
        //   3. C# P/Invoke routed through the @_cdecl symbol
        // Build TypeDatabase with class type registered
        var typeDb = new TypeDatabase();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

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
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.FinalCounter"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "FinalCounter"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.FinalCounter"),
                MetadataAccessor = "$s10TestModule12FinalCounterCMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
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
            ModuleDecl = null
        };

        var parentDecl = new ClassDecl
        {
            Name = "FinalCounter",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.FinalCounter"),
            MangledName = "$s10TestModule12FinalCounterCN",
            IsFinal = true,
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
        moduleDecl.Types.Add(parentDecl);

        var method = new MethodDecl
        {
            Name = "add",
            MangledName = "$s10TestModule12FinalCounterC3add6amount2bySi_SitF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                // Return: Swift.Int
                new ArgumentDecl
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                CreateArg("amount", hasDefault: false),
                CreateArg("by", hasDefault: true),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            // Simulate that MethodHandler already set this flag on the primary method
            UsesCdeclMethodWrapper = true,
            UsesWrapperLibrary = true,
        };
        parentDecl.Methods.Add(method);

        var emissionContext = new ModuleEmissionContext();
        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);
        var env = new MethodEnvironment(method, typeDb);
        var logger = NullLogger.Instance;

        DefaultParameterOverloadEmitter.TryEmitOverloads(
            csWriter, swiftWriter, env, logger, emissionContext);

        var swiftOutput = swiftStringWriter.ToString();
        var csOutput = csStringWriter.ToString();

        // 1. Must have @_silgen_name wrapper (calls original Swift method with default)
        Assert.Contains("@_silgen_name", swiftOutput);
        Assert.Contains("_dbw_add_", swiftOutput);

        // 2. Must have @_cdecl wrapper on top of the @_silgen_name function
        Assert.Contains("@_cdecl", swiftOutput);
        Assert.Contains("SBW_TestModule_FinalCounter_add_", swiftOutput);

        // 3. The @_cdecl wrapper must call the @_silgen_name function (not the original method)
        // The silgen function name follows the pattern _dbw_{methodName}_{hash}_{trimCount}
        Assert.Matches(@"_dbw_add_\w+_1", swiftOutput);

        // 4. C# output must have a P/Invoke with the @_cdecl symbol as entry point
        Assert.Contains("SBW_TestModule_FinalCounter_add_", csOutput);
        Assert.Contains("LibraryImport", csOutput);
    }

    [Fact]
    public void TryEmitOverloads_AsyncMethodWithCdecl_SkipsCdeclWrapper()
    {
        // Issue O: Async methods should NOT get @_cdecl wrappers — @_cdecl functions
        // are synchronous and cannot call async _dbw_ extension methods (missing 'await').
        var typeDb = new TypeDatabase();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

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
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.FinalCounter"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "FinalCounter"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.FinalCounter"),
                MetadataAccessor = "$s10TestModule12FinalCounterCMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
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
            ModuleDecl = null
        };

        var parentDecl = new ClassDecl
        {
            Name = "FinalCounter",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.FinalCounter"),
            MangledName = "$s10TestModule12FinalCounterCN",
            IsFinal = true,
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
        moduleDecl.Types.Add(parentDecl);

        var method = new MethodDecl
        {
            Name = "fetch",
            MangledName = "$s10TestModule12FinalCounterC5fetch5limitSi_SitF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                CreateArg("limit", hasDefault: false),
                CreateArg("offset", hasDefault: true),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = true, // ASYNC method
            Visibility = Visibility.Public,
            UsesCdeclMethodWrapper = true,
            UsesWrapperLibrary = true,
        };
        parentDecl.Methods.Add(method);

        var emissionContext = new ModuleEmissionContext();
        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);
        var env = new MethodEnvironment(method, typeDb);
        var logger = NullLogger.Instance;

        DefaultParameterOverloadEmitter.TryEmitOverloads(
            csWriter, swiftWriter, env, logger, emissionContext);

        var swiftOutput = swiftStringWriter.ToString();

        // Should still emit the @_silgen_name wrapper (synchronous factory calling with defaults)
        Assert.Contains("@_silgen_name", swiftOutput);
        Assert.Contains("_dbw_fetch_", swiftOutput);

        // Should NOT emit a synchronous @_cdecl method wrapper (Issue O).
        // The async callback @_cdecl (with _async suffix + Task { await }) IS expected —
        // it's the correct way to bridge async methods. What must NOT happen is
        // MethodWrapperEmitter emitting a synchronous @_cdecl that calls the async _dbw_
        // extension method without await.
        // The synchronous wrapper would have symbol like "SBW_TestModule_FinalCounter_fetch_..."
        // WITHOUT the _async suffix. The async wrapper correctly wraps with Task { await }.
        var cdeclMatches = System.Text.RegularExpressions.Regex.Matches(swiftOutput, @"@_cdecl\(""([^""]+)""\)");
        foreach (System.Text.RegularExpressions.Match match in cdeclMatches)
        {
            var symbol = match.Groups[1].Value;
            // Async wrapper symbols end in _async — those are fine
            Assert.True(symbol.EndsWith("_async"),
                $"Non-async @_cdecl wrapper found for async method: {symbol}. " +
                "Synchronous @_cdecl wrappers cannot call async _dbw_ extension methods.");
        }
    }

    #endregion
}
