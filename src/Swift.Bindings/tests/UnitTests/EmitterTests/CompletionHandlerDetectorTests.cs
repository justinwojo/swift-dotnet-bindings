// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for CompletionHandlerDetector and callback-to-Task overload generation.
/// </summary>
public class CompletionHandlerDetectorTests
{
    #region CallbackShape Detection Tests

    [Fact]
    public void GetCallbackShape_VoidClosure_ReturnsVoidResult()
    {
        // () -> Void
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        Assert.Equal(CompletionHandlerDetector.CallbackShape.VoidResult, CompletionHandlerDetector.GetCallbackShape(closure));
    }

    [Fact]
    public void GetCallbackShape_SingleParam_ReturnsSingleResult()
    {
        // (Int) -> Void
        var closure = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty);
        Assert.Equal(CompletionHandlerDetector.CallbackShape.SingleResult, CompletionHandlerDetector.GetCallbackShape(closure));
    }

    [Fact]
    public void GetCallbackShape_OptionalError_ReturnsErrorOnly()
    {
        // (Error?) -> Void
        var optionalError = new NamedTypeSpec("Swift.Optional");
        optionalError.GenericParameters.Add(new NamedTypeSpec("Swift.Error"));
        var closure = new ClosureTypeSpec(optionalError, TupleTypeSpec.Empty);
        Assert.Equal(CompletionHandlerDetector.CallbackShape.ErrorOnly, CompletionHandlerDetector.GetCallbackShape(closure));
    }

    [Fact]
    public void GetCallbackShape_ResultAndError_ReturnsResultWithError()
    {
        // (String?, Error?) -> Void
        var optionalString = new NamedTypeSpec("Swift.Optional");
        optionalString.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        var optionalError = new NamedTypeSpec("Swift.Optional");
        optionalError.GenericParameters.Add(new NamedTypeSpec("Swift.Error"));
        var args = new TupleTypeSpec(new TypeSpec[] { optionalString, optionalError });
        var closure = new ClosureTypeSpec(args, TupleTypeSpec.Empty);
        Assert.Equal(CompletionHandlerDetector.CallbackShape.ResultWithError, CompletionHandlerDetector.GetCallbackShape(closure));
    }

    [Fact]
    public void GetCallbackShape_ThreeParams_ReturnsUnsupported()
    {
        // (Int, String, Bool) -> Void
        var args = new TupleTypeSpec(new TypeSpec[]
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.String")
        });
        args.Elements.Add(new NamedTypeSpec("Swift.Bool"));
        var closure = new ClosureTypeSpec(args, TupleTypeSpec.Empty);
        Assert.Equal(CompletionHandlerDetector.CallbackShape.Unsupported, CompletionHandlerDetector.GetCallbackShape(closure));
    }

    [Fact]
    public void GetCallbackShape_NonVoidReturn_ReturnsUnsupported()
    {
        // () -> Int
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, new NamedTypeSpec("Swift.Int"));
        Assert.Equal(CompletionHandlerDetector.CallbackShape.Unsupported, CompletionHandlerDetector.GetCallbackShape(closure));
    }

    [Fact]
    public void GetCallbackShape_TwoNonOptionalParams_ReturnsUnsupported()
    {
        // (Int, String) -> Void — neither optional, doesn't match (T?, Error?) pattern
        var args = new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("Swift.String") });
        var closure = new ClosureTypeSpec(args, TupleTypeSpec.Empty);
        Assert.Equal(CompletionHandlerDetector.CallbackShape.Unsupported, CompletionHandlerDetector.GetCallbackShape(closure));
    }

    #endregion

    #region IsCompletionHandler Detection Tests

    [Fact]
    public void IsCompletionHandler_TrailingVoidClosure_ReturnsTrue()
    {
        var (methodDecl, closureParam, closureHandler) = BuildMethodWithTrailingClosure(
            isAsync: false,
            returnVoid: true,
            closureSpec: new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty));

        Assert.True(CompletionHandlerDetector.IsCompletionHandler(methodDecl, closureParam, closureHandler));
    }

    [Fact]
    public void IsCompletionHandler_AsyncMethod_ReturnsFalse()
    {
        var (methodDecl, closureParam, closureHandler) = BuildMethodWithTrailingClosure(
            isAsync: true,
            returnVoid: true,
            closureSpec: new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty));

        Assert.False(CompletionHandlerDetector.IsCompletionHandler(methodDecl, closureParam, closureHandler));
    }

    [Fact]
    public void IsCompletionHandler_NonVoidReturn_ReturnsFalse()
    {
        var (methodDecl, closureParam, closureHandler) = BuildMethodWithTrailingClosure(
            isAsync: false,
            returnVoid: false,
            closureSpec: new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty));

        Assert.False(CompletionHandlerDetector.IsCompletionHandler(methodDecl, closureParam, closureHandler));
    }

    [Fact]
    public void IsCompletionHandler_NonTrailingClosure_ReturnsFalse()
    {
        // Closure is first param, not last
        var moduleDecl = CreateModuleDecl();
        var parentDecl = CreateParentDecl(moduleDecl);
        var closureSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);

        var closureParam = new ArgumentDecl
        {
            SwiftTypeSpec = closureSpec,
            Name = "onComplete",
            PrivateName = "onComplete",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };
        var regularParam = new ArgumentDecl
        {
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            Name = "count",
            PrivateName = "count",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = TupleTypeSpec.Empty,
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            },
            closureParam,
            regularParam // Closure is not last
        };

        var methodDecl = new MethodDecl
        {
            Name = "doWork",
            MangledName = "$s10TestModule8PipelineC6doWorkyySiyXEF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var typeDatabase = CreateTypeDatabase(parentDecl);
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(CompletionHandlerDetector.IsCompletionHandler(methodDecl, closureParam, closureHandler));
    }

    [Fact]
    public void IsCompletionHandler_ClosureWithReturn_ReturnsFalse()
    {
        // (Int) -> String — closure has non-void return
        var (methodDecl, closureParam, closureHandler) = BuildMethodWithTrailingClosure(
            isAsync: false,
            returnVoid: true,
            closureSpec: new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("Swift.String")));

        Assert.False(CompletionHandlerDetector.IsCompletionHandler(methodDecl, closureParam, closureHandler));
    }

    #endregion

    #region Task Overload Emission Tests

    [Fact]
    public void CompletionHandler_EmitsAsyncOverload_WithAsyncSuffix()
    {
        var csOutput = GenerateMethodWithCompletionHandler(
            new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty));

        Assert.Contains("Async(", csOutput);
        Assert.Contains("async Task", csOutput);
    }

    [Fact]
    public void CompletionHandler_EmitsCancellationTokenOnOverload()
    {
        var csOutput = GenerateMethodWithCompletionHandler(
            new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty));

        Assert.Contains("System.Threading.CancellationToken cancellationToken = default", csOutput);
    }

    [Fact]
    public void CompletionHandler_EmitsRunContinuationsAsynchronously()
    {
        var csOutput = GenerateMethodWithCompletionHandler(
            new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty));

        Assert.Contains("TaskCreationOptions.RunContinuationsAsynchronously", csOutput);
    }

    [Fact]
    public void CompletionHandler_EmitsCancellationDocComment()
    {
        var csOutput = GenerateMethodWithCompletionHandler(
            new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty));

        Assert.Contains("Cancels the returned Task but does not cancel the underlying operation", csOutput);
    }

    [Fact]
    public void CompletionHandler_SingleResult_EmitsTaskOfT()
    {
        var csOutput = GenerateMethodWithCompletionHandler(
            new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty));

        Assert.Contains("Task<", csOutput);
        Assert.Contains("tcs.TrySetResult(result)", csOutput);
    }

    [Fact]
    public void CompletionHandler_VoidResult_EmitsTask()
    {
        var csOutput = GenerateMethodWithCompletionHandler(
            new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty));

        Assert.Contains("async Task ", csOutput);
        Assert.Contains("tcs.TrySetResult(true)", csOutput);
    }

    [Fact]
    public void CompletionHandler_ErrorOnly_EmitsErrorBranch()
    {
        var optionalError = new NamedTypeSpec("Swift.Optional");
        optionalError.GenericParameters.Add(new NamedTypeSpec("Swift.Error"));
        var csOutput = GenerateMethodWithCompletionHandler(
            new ClosureTypeSpec(optionalError, TupleTypeSpec.Empty));

        Assert.Contains("TrySetException", csOutput);
        Assert.Contains("SwiftException", csOutput);
        Assert.Contains("tcs.TrySetResult(true)", csOutput);
    }

    [Fact]
    public void CompletionHandler_ResultWithError_EmitsErrorBranch()
    {
        // Use Optional<Int> (primitive) — the closure handler produces nint? which matches
        // the TCS result type. Optional<String> produces SwiftOptional<SwiftString> in closures,
        // which can't implicitly convert to the TCS's string? (CS1503), so the overload is skipped.
        var optionalInt = new NamedTypeSpec("Swift.Optional");
        optionalInt.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var optionalError = new NamedTypeSpec("Swift.Optional");
        optionalError.GenericParameters.Add(new NamedTypeSpec("Swift.Error"));
        var args = new TupleTypeSpec(new TypeSpec[] { optionalInt, optionalError });
        var csOutput = GenerateMethodWithCompletionHandler(
            new ClosureTypeSpec(args, TupleTypeSpec.Empty));

        Assert.Contains("TrySetException", csOutput);
        Assert.Contains("SwiftException", csOutput);
        Assert.Contains("tcs.TrySetResult(result)", csOutput);
    }

    #endregion

    #region GetResultTypeName Tests

    [Fact]
    public void GetResultTypeName_VoidResult_ReturnsNull()
    {
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        var typeDatabase = new TypeDatabase();
        var tch = new TypeConversionHandler(typeDatabase);
        var result = CompletionHandlerDetector.GetResultTypeName(
            closure, CompletionHandlerDetector.CallbackShape.VoidResult, typeDatabase, tch);
        Assert.Null(result);
    }

    [Fact]
    public void GetResultTypeName_ErrorOnly_ReturnsNull()
    {
        var optionalError = new NamedTypeSpec("Swift.Optional");
        optionalError.GenericParameters.Add(new NamedTypeSpec("Swift.Error"));
        var closure = new ClosureTypeSpec(optionalError, TupleTypeSpec.Empty);
        var typeDatabase = new TypeDatabase();
        var tch = new TypeConversionHandler(typeDatabase);
        var result = CompletionHandlerDetector.GetResultTypeName(
            closure, CompletionHandlerDetector.CallbackShape.ErrorOnly, typeDatabase, tch);
        Assert.Null(result);
    }

    #endregion

    #region Completion Handler Dedup with Native Async

    [Fact]
    public void CompletionHandler_SkipsOverload_WhenNativeAsyncExists()
    {
        // When a native async method is emitted first, its projected key now includes
        // CancellationToken. The completion handler overload also includes CancellationToken
        // in its key. So both keys match → overload is skipped → no CS0111.
        CancellationTaskEmitter.ResetForModule();

        var moduleDecl = CreateModuleDecl();
        var parentDecl = CreateParentDecl(moduleDecl);
        var typeDatabase = CreateTypeDatabase(parentDecl);

        // Emit native async method first
        var asyncMethod = new MethodDecl
        {
            Name = "presentPaymentOptions",
            MangledName = "$s10TestModule8PipelineC22presentPaymentOptionsyySo16UIViewControllerCYaKF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateAsyncReturnArg(moduleDecl, parentDecl),
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    Name = "from",
                    PrivateName = "from",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parentDecl,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = true,
            IsAsync = true,
            Visibility = Visibility.Public
        };

        // Pre-populate EmittedProjectedSignatures with the native async method's projected key.
        // In production, HandleBaseDecl calls GetProjectedCSharpMethodKey and adds it.
        // Here we simulate that by computing the key via reflection.
        var emittedSignatures = new HashSet<string>();
        var nativeAsyncKey = InvokeGetProjectedCSharpMethodKey(asyncMethod, typeDatabase);
        emittedSignatures.Add(nativeAsyncKey);

        // Now emit a completion handler method that would produce the same Async overload
        var closureSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        var completionMethod = new MethodDecl
        {
            Name = "presentPaymentOptions",
            MangledName = "$s10TestModule8PipelineC22presentPaymentOptionsyySi_yyXEtF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parentDecl,
                    ModuleDecl = moduleDecl
                },
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    Name = "from",
                    PrivateName = "from",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parentDecl,
                    ModuleDecl = moduleDecl
                },
                new ArgumentDecl
                {
                    SwiftTypeSpec = closureSpec,
                    Name = "completion",
                    PrivateName = "completion",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parentDecl,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var csOutput2 = EmitMethodWithSignatures(completionMethod, typeDatabase, emittedSignatures);
        // The completion handler Async overload should NOT be emitted (key collision with native async)
        var asyncMethodCount = csOutput2.Split('\n')
            .Count(l => l.Contains("PresentPaymentOptionsAsync(") && !l.TrimStart().StartsWith("//"));
        Assert.Equal(0, asyncMethodCount);
    }

    [Fact]
    public void CompletionHandler_EmitsOverload_WhenNoNativeAsyncConflict()
    {
        // Without a pre-existing native async with the same name,
        // the completion handler overload should be emitted normally.
        CancellationTaskEmitter.ResetForModule();

        var moduleDecl = CreateModuleDecl();
        var parentDecl = CreateParentDecl(moduleDecl);
        var typeDatabase = CreateTypeDatabase(parentDecl);

        var closureSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        var method = new MethodDecl
        {
            Name = "loadData",
            MangledName = "$s10TestModule8PipelineC8loadDatayyXEF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parentDecl,
                    ModuleDecl = moduleDecl
                },
                new ArgumentDecl
                {
                    SwiftTypeSpec = closureSpec,
                    Name = "completion",
                    PrivateName = "completion",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parentDecl,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var emittedSignatures = new HashSet<string>();
        var csOutput = EmitMethodWithSignatures(method, typeDatabase, emittedSignatures);
        Assert.Contains("LoadDataAsync(", csOutput);
    }

    #endregion

    #region Helper Methods

    private static ArgumentDecl CreateAsyncReturnArg(ModuleDecl moduleDecl, ClassDecl parentDecl)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = TupleTypeSpec.Empty,
            Name = string.Empty,
            PrivateName = string.Empty,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };
    }

    private static string EmitMethodWithSignatures(MethodDecl methodDecl, TypeDatabase typeDatabase, HashSet<string> emittedSignatures)
    {
        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var loggerFactory = new NullLoggerFactory();
        var conductor = new Conductor(loggerFactory);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = handler.Marshal(methodDecl, typeDatabase);

        // Inject EmittedProjectedSignatures for dedup testing
        if (env is MethodEnvironment methodEnv)
        {
            methodEnv.EmittedProjectedSignatures = emittedSignatures;
        }

        handler.Emit(csWriter, swiftWriter, env, conductor);

        return csStringWriter.ToString();
    }

    /// <summary>
    /// Invokes BaseHandler.GetProjectedCSharpMethodKey via reflection (private static).
    /// </summary>
    private static string InvokeGetProjectedCSharpMethodKey(MethodDecl methodDecl, ITypeDatabase typeDatabase)
    {
        var method = typeof(BaseHandler).GetMethod(
            "GetProjectedCSharpMethodKey",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string)method!.Invoke(null, new object[] { methodDecl, typeDatabase, null! })!;
    }

    private static ModuleDecl CreateModuleDecl() => new ModuleDecl
    {
        Name = "TestModule",
        Dependencies = new List<string>(),
        Types = new List<TypeDecl>(),
        Methods = new List<MethodDecl>(),
        Properties = new List<PropertyDecl>(),
        Protocols = new List<ProtocolDecl>(),
        ParentDecl = null,
        ModuleDecl = null
    };

    private static ClassDecl CreateParentDecl(ModuleDecl moduleDecl)
    {
        var parentDecl = new ClassDecl
        {
            Name = "Pipeline",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Pipeline"),
            MangledName = "$s10TestModule8PipelineCN",
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
        parentDecl.Properties.Add(new PropertyDecl
        {
            Name = "shared",
            IsStatic = true,
            HasStorage = true,
            SwiftTypeSpec = new NamedTypeSpec("TestModule.Pipeline"),
            Accessors = new List<AccessorDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        });
        moduleDecl.Types.Add(parentDecl);
        return parentDecl;
    }

    private static TypeDatabase CreateTypeDatabase(ClassDecl parentDecl)
    {
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");
        module.RegisterType(
            parentDecl.SwiftTypeName,
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Pipeline"),
                SwiftTypeName = parentDecl.SwiftTypeName,
                MetadataAccessor = "$s10TestModule8PipelineCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(module);

        // Register Swift stdlib types in the "Swift" module (TypeDatabase looks up by module name)
        var swiftModule = new ModuleTypeDatabase("Swift", "/fake/swift");
        var intTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int");
        swiftModule.RegisterType(intTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
            SwiftTypeName = intTypeName,
            MetadataAccessor = "$sSiMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        var stringTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String");
        swiftModule.RegisterType(stringTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
            SwiftTypeName = stringTypeName,
            MetadataAccessor = "$sSSMa",
            Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Struct
        });
        var optionalTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional");
        swiftModule.RegisterType(optionalTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
            SwiftTypeName = optionalTypeName,
            MetadataAccessor = "$sSqMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        var errorTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Error");
        swiftModule.RegisterType(errorTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftError"),
            SwiftTypeName = errorTypeName,
            MetadataAccessor = "$ss5ErrorMa",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Protocol
        });
        typeDatabase.AddModuleDatabase(swiftModule);
        return typeDatabase;
    }

    /// <summary>
    /// Builds a method with a trailing closure param and returns the components needed for detection tests.
    /// </summary>
    private static (MethodDecl methodDecl, ArgumentDecl closureParam, ClosureHandler closureHandler) BuildMethodWithTrailingClosure(
        bool isAsync,
        bool returnVoid,
        ClosureTypeSpec closureSpec)
    {
        var moduleDecl = CreateModuleDecl();
        var parentDecl = CreateParentDecl(moduleDecl);

        var closureParam = new ArgumentDecl
        {
            SwiftTypeSpec = closureSpec,
            Name = "completion",
            PrivateName = "completion",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var returnTypeSpec = returnVoid ? (TypeSpec)TupleTypeSpec.Empty : new NamedTypeSpec("Swift.Int");
        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = returnTypeSpec,
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            },
            closureParam
        };

        var methodDecl = new MethodDecl
        {
            Name = "loadData",
            MangledName = "$s10TestModule8PipelineC8loadDatayyXEF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = isAsync,
            Visibility = Visibility.Public
        };

        var typeDatabase = CreateTypeDatabase(parentDecl);
        var closureHandler = new ClosureHandler(typeDatabase);

        return (methodDecl, closureParam, closureHandler);
    }

    /// <summary>
    /// Generates the full C# output for a method with a completion handler closure,
    /// returning the output string for assertion.
    /// </summary>
    private static string GenerateMethodWithCompletionHandler(ClosureTypeSpec closureSpec)
    {
        CancellationTaskEmitter.ResetForModule();

        var moduleDecl = CreateModuleDecl();
        var parentDecl = CreateParentDecl(moduleDecl);

        var closureParam = new ArgumentDecl
        {
            SwiftTypeSpec = closureSpec,
            Name = "completion",
            PrivateName = "completion",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = TupleTypeSpec.Empty,
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            },
            closureParam
        };

        var methodDecl = new MethodDecl
        {
            Name = "loadData",
            MangledName = "$s10TestModule8PipelineC8loadDatayyXEF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var typeDatabase = CreateTypeDatabase(parentDecl);

        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var loggerFactory = new NullLoggerFactory();
        var conductor = new Conductor(loggerFactory);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = handler.Marshal(methodDecl, typeDatabase);
        handler.Emit(csWriter, swiftWriter, env, conductor);

        return csStringWriter.ToString();
    }

    #endregion
}
