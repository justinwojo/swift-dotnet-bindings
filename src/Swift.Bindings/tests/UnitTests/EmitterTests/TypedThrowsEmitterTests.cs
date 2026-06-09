// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for typed throws emission — SwiftException&lt;TError&gt; for sync methods,
/// 4-param error callback for async methods, and fallback behavior.
/// </summary>
public class TypedThrowsEmitterTests
{
    #region Sync Typed Throws

    [Fact]
    public void SyncMethod_WithTypedThrows_EmitsSwiftExceptionGeneric()
    {
        var (csOutput, swiftOutput) = GenerateThrowingMethod(
            isAsync: false,
            hasTypedThrows: true,
            errorTypeName: "TestModule.ParseError");

        // C# side: typed error extraction with nil-check fallback
        Assert.Contains("SwiftException<TestModule.ParseError>", csOutput);
        Assert.DoesNotContain("SwiftRuntimeException", csOutput);
        Assert.Contains("SBW_ExtractTypedError_TestModule_ParseError", csOutput);
        Assert.Contains("MarshalFromSwift<TestModule.ParseError>", csOutput);
        Assert.Contains("SBW_Free(_typedErrorPtr)", csOutput);
        Assert.Contains("_typedError, _errorMessage", csOutput);
        Assert.Contains("_typedErrorPtr != IntPtr.Zero", csOutput);

        // Swift side: typed error extractor function
        Assert.Contains("SBW_ExtractTypedError_", swiftOutput);
        Assert.Contains("as? TestModule.ParseError", swiftOutput);
        Assert.Contains("MemoryLayout<TestModule.ParseError>", swiftOutput);
    }

    [Fact]
    public void SyncMethod_WithoutTypedThrows_EmitsSwiftException()
    {
        var (csOutput, swiftOutput) = GenerateThrowingMethod(
            isAsync: false,
            hasTypedThrows: false);

        // Untyped throws uses SwiftMarshal.ThrowSwiftError helper (consolidates description + release + throw)
        Assert.Contains("SwiftMarshal.ThrowSwiftError", csOutput);
        Assert.DoesNotContain("SwiftException<", csOutput);
        Assert.DoesNotContain("SBW_ExtractTypedError", csOutput);
        Assert.DoesNotContain("SBW_ExtractTypedError", swiftOutput);
    }

    [Fact]
    public void SyncMethod_WithUnresolvableErrorType_FallsBackToSwiftException()
    {
        // When error type is not in TypeDatabase, fall back to untyped
        var (csOutput, _) = GenerateThrowingMethod(
            isAsync: false,
            hasTypedThrows: true,
            errorTypeName: "UnknownModule.UnknownError",
            registerErrorType: false);

        // Falls back to untyped SwiftMarshal.ThrowSwiftError (no typed extraction)
        Assert.Contains("SwiftMarshal.ThrowSwiftError", csOutput);
        Assert.DoesNotContain("SwiftException<", csOutput);
    }

    [Fact]
    public void SyncMethod_WithTypedThrows_EmitsReleaseInOuterFinally()
    {
        var (csOutput, _) = GenerateThrowingMethod(
            isAsync: false,
            hasTypedThrows: true,
            errorTypeName: "TestModule.ParseError");

        // SBW_ReleaseError must be in the outermost finally (not alongside SBW_Free)
        // for guaranteed release on all paths
        Assert.Contains("SBW_ReleaseError(_errorPtr)", csOutput);
        // The typed path should NOT have SBW_ReleaseError in the same finally as SBW_Free(_descPtr)
        Assert.DoesNotContain("SBW_Free(_descPtr);\n                    SBW_ReleaseError", csOutput);
    }

    #endregion

    #region Failable Constructor Typed Throws

    [Fact]
    public void FailableConstructor_WithTypedThrows_EmitsExtractor()
    {
        var (csOutput, swiftOutput) = GenerateThrowingFailableConstructor(
            hasTypedThrows: true,
            errorTypeName: "TestModule.ParseError");

        // C# side: typed error extraction with nil-check fallback
        Assert.Contains("SBW_ExtractTypedError_TestModule_ParseError", csOutput);
        Assert.Contains("MarshalFromSwift<TestModule.ParseError>", csOutput);
        Assert.Contains("_typedErrorPtr != IntPtr.Zero", csOutput);
        Assert.Contains("SwiftException<TestModule.ParseError>", csOutput);

        // Swift side: typed error extractor function
        Assert.Contains("SBW_ExtractTypedError_", swiftOutput);
        Assert.Contains("as? TestModule.ParseError", swiftOutput);
        Assert.Contains("MemoryLayout<TestModule.ParseError>", swiftOutput);
    }

    [Fact]
    public void FailableConstructor_WithoutTypedThrows_NoExtractor()
    {
        var (csOutput, swiftOutput) = GenerateThrowingFailableConstructor(
            hasTypedThrows: false);

        Assert.DoesNotContain("SBW_ExtractTypedError", csOutput);
        Assert.DoesNotContain("SBW_ExtractTypedError", swiftOutput);
        // Untyped failable constructor uses SwiftMarshal.ThrowSwiftError
        Assert.Contains("SwiftMarshal.ThrowSwiftError", csOutput);
    }

    #endregion

    #region Async Typed Throws

    [Fact]
    public void AsyncMethod_WithTypedThrows_EmitsTypedErrorCallback()
    {
        var (csOutput, swiftOutput) = GenerateThrowingMethod(
            isAsync: true,
            hasTypedThrows: true,
            errorTypeName: "TestModule.ParseError");

        // Unified wire: 6-param delegate
        // (errorPtr?, errorSize, msgPtr?, isCancellation, _sbwTask, errorTypeId).
        Assert.Contains("IntPtr, nint, IntPtr, int, IntPtr, int, void", csOutput);
        // Typed-throws body still marshals from the static error type and frees the
        // Swift-allocated buffer (errorTypeId is 0 on this path, ignored by C#).
        Assert.Contains("MarshalFromSwift<TestModule.ParseError>", csOutput);
        Assert.Contains("SBW_Free(errorPtr)", csOutput);
        Assert.Contains("SwiftException<TestModule.ParseError>", csOutput);

        // Swift side: typed error callback with MemoryLayout + initializeMemory.
        // Uses initializeMemory (not copyMemory) to properly retain internal references
        // in error enum associated values (e.g., String fields in ParseError.overflow).
        Assert.Contains("MemoryLayout<TestModule.ParseError>.size", swiftOutput);
        Assert.Contains("initializeMemory(as: TestModule.ParseError.self", swiftOutput);
        // Unified Swift signature uses Optional pointers + trailing Int32 errorTypeId.
        Assert.Contains("UnsafeRawPointer?, Int, UnsafePointer<CChar>?, Int32, Int64, Int32", swiftOutput);
        // Typed-throws catch passes errorTypeId 0 (C# uses static error type for dispatch).
        Assert.Contains("_isCancelled, _sbwTask, 0)", swiftOutput);
    }

    [Fact]
    public void AsyncMethod_WithoutTypedThrows_EmitsUntypedErrorCallback()
    {
        var (csOutput, swiftOutput) = GenerateThrowingMethod(
            isAsync: true,
            hasTypedThrows: false);

        // Even untyped throws emit the 6-param delegate. The C#
        // body still constructs a bare SwiftException (no marshalling of the payload
        // pointers, which the Swift catch fills with nil/0). Test fixture has no
        // registered error types, so the cascade gate is also off — pure untyped.
        Assert.Contains("IntPtr, nint, IntPtr, int, IntPtr, int, void", csOutput);
        Assert.DoesNotContain("SBW_Free", csOutput);
        Assert.DoesNotContain("SwiftException<", csOutput);
        Assert.Contains("SwiftException(errorMessage)", csOutput);

        // Swift side: untyped catch passes nil/0 fillers for the payload fields.
        Assert.Contains("UnsafeRawPointer?, Int, UnsafePointer<CChar>?, Int32, Int64, Int32", swiftOutput);
        Assert.Contains("errorCallback(nil, 0, _msgPtr, _isCancelled, _sbwTask, 0)", swiftOutput);
        Assert.DoesNotContain("MemoryLayout<", swiftOutput);
    }

    [Fact]
    public void AsyncMethod_WithUnresolvableErrorType_FallsBackToUntyped()
    {
        var (csOutput, swiftOutput) = GenerateThrowingMethod(
            isAsync: true,
            hasTypedThrows: true,
            errorTypeName: "UnknownModule.UnknownError",
            registerErrorType: false);

        // Should fall back to untyped pattern (still on the unified 6-param wire).
        Assert.Contains("SwiftException(errorMessage)", csOutput);
        Assert.DoesNotContain("SBW_Free", csOutput);
        Assert.DoesNotContain("SwiftException<", csOutput);
        Assert.Contains("UnsafeRawPointer?, Int, UnsafePointer<CChar>?, Int32, Int64, Int32", swiftOutput);
        Assert.Contains("errorCallback(nil, 0, _msgPtr, _isCancelled, _sbwTask, 0)", swiftOutput);
    }

    [Fact]
    public void AsyncFreeFunction_WithTypedThrows_EmitsTypedErrorCallback()
    {
        // Free-function async typed throws now generates typed pattern (D5 guard removed)
        var (csOutput, swiftOutput) = GenerateThrowingMethod(
            isAsync: true,
            hasTypedThrows: true,
            errorTypeName: "TestModule.ParseError",
            isFreeFunction: true);

        // Same 6-param shape across all three paths.
        Assert.Contains("IntPtr, nint, IntPtr, int, IntPtr, int, void", csOutput);
        Assert.Contains("MarshalFromSwift<TestModule.ParseError>", csOutput);
        Assert.Contains("SBW_Free(errorPtr)", csOutput);
        Assert.Contains("SwiftException<TestModule.ParseError>", csOutput);

        // Swift side: typed error callback with MemoryLayout + initializeMemory.
        Assert.Contains("MemoryLayout<TestModule.ParseError>.size", swiftOutput);
        Assert.Contains("initializeMemory(as: TestModule.ParseError.self", swiftOutput);
        Assert.Contains("UnsafeRawPointer?, Int, UnsafePointer<CChar>?, Int32, Int64, Int32", swiftOutput);
        Assert.Contains("_isCancelled, _sbwTask, 0)", swiftOutput);
    }

    #endregion

    #region Error Ownership — SBW_Free Behavior

    [Fact]
    public void SyncMethod_ComplexEnumError_UsesCatchNotFinally()
    {
        // Complex enums (non-SimpleEnum) are projected as C# classes with SafeHandle.
        // MarshalFromSwift takes ownership of the buffer — SBW_Free must only run on exception,
        // not in finally (which would double-free when SafeHandle finalizes).
        // IMPORTANT: The throw new SwiftException must be OUTSIDE the try-catch that frees
        // the buffer, otherwise re-throwing the exception triggers the catch handler and
        // frees the buffer while the SwiftException still holds a reference to it.
        var (csOutput, _) = GenerateThrowingMethod(
            isAsync: false,
            hasTypedThrows: true,
            errorTypeName: "TestModule.ParseError");

        // The error type is registered as complex enum (Kind=Enum, no SimpleEnum flag).
        // Should use catch block, not finally, for SBW_Free.
        Assert.Contains("catch { SBW_Free(_typedErrorPtr); throw; }", csOutput);
        // The throw new SwiftException must be AFTER the try-catch, not inside it.
        // Split at MarshalFromSwift — the catch should come before the throw SwiftException.
        var afterMarshal = csOutput.Substring(csOutput.IndexOf("MarshalFromSwift"));
        var catchIndex = afterMarshal.IndexOf("catch { SBW_Free(_typedErrorPtr)");
        var throwExceptionIndex = afterMarshal.IndexOf("throw new SwiftException");
        Assert.True(catchIndex < throwExceptionIndex,
            "catch { SBW_Free } should come before throw new SwiftException (not wrap it)");
    }

    [Fact]
    public void SyncMethod_SimpleEnumError_UsesFinallyForFree()
    {
        // Simple enums are projected as C# value types — MarshalFromSwift copies the value,
        // does NOT take ownership. SBW_Free should be in finally (always free the buffer).
        var (csOutput, _) = GenerateThrowingMethodWithSimpleEnum();

        Assert.Contains("SBW_Free(_typedErrorPtr)", csOutput);
        Assert.Contains("finally", csOutput);
    }

    #endregion

    #region MethodDecl Properties

    [Fact]
    public void HasTypedThrows_WhenThrownErrorTypeSet_ReturnsTrue()
    {
        var method = CreateMethodDecl(throws_: true);
        method.ThrownErrorType = new NamedTypeSpec("TestModule.ParseError");

        Assert.True(method.HasTypedThrows);
    }

    [Fact]
    public void HasTypedThrows_WhenThrownErrorTypeNull_ReturnsFalse()
    {
        var method = CreateMethodDecl(throws_: true);

        Assert.False(method.HasTypedThrows);
        Assert.Null(method.ThrownErrorType);
    }

    [Fact]
    public void HasTypedThrows_WhenNotThrowing_ReturnsFalse()
    {
        var method = CreateMethodDecl(throws_: false);

        Assert.False(method.HasTypedThrows);
    }

    #endregion

    #region Helpers

    private static (string CsOutput, string SwiftOutput) GenerateThrowingMethod(
        bool isAsync,
        bool hasTypedThrows,
        string errorTypeName = "TestModule.ParseError",
        bool registerErrorType = true,
        bool isFreeFunction = false)
    {
        // Reset static emitter state — these tests need clean dedup tracking
        // Context-based tracking: tests use default context (no parallelism)

        var moduleDecl = new ModuleDecl
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

        BaseDecl parentDecl;
        if (isFreeFunction)
        {
            parentDecl = moduleDecl;
        }
        else
        {
            var structDecl = new StructDecl
            {
                Name = "Parser",
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Parser"),
                IsFrozen = true,
                MetadataAccessor = "$s10TestModule6ParserVMa",
                MangledName = "$s10TestModule6ParserV",
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Conformances = new List<TypeConformance>(),
                Types = new List<TypeDecl>(),
                Operators = new List<OperatorDecl>(),
                ParentDecl = moduleDecl,
                ModuleDecl = moduleDecl
            };
            moduleDecl.Types.Add(structDecl);
            parentDecl = structDecl;
        }

        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"),
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            },
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                Name = "input",
                PrivateName = "input",
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            }
        };

        var methodDecl = new MethodDecl
        {
            Name = "parse",
            MangledName = isFreeFunction
                ? "$s10TestModule5parse_s5Int32VSSF"
                : "$s10TestModule6ParserV5parse_s5Int32VSSF",
            MethodType = isFreeFunction ? MethodType.Static : MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = true,
            IsAsync = isAsync,
            Visibility = Visibility.Public,
            ThrownErrorType = hasTypedThrows ? TypeSpecParser.Parse(errorTypeName) : null
        };

        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");

        if (!isFreeFunction)
        {
            module.RegisterType(
                ((TypeDecl)parentDecl).SwiftTypeName,
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Parser"),
                    SwiftTypeName = ((TypeDecl)parentDecl).SwiftTypeName,
                    MetadataAccessor = "$s10TestModule6ParserVMa",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                });
        }

        // Register common types
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int32"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
                MetadataAccessor = "$ss5Int32VMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });

        if (registerErrorType && hasTypedThrows)
        {
            var errorSwiftName = SwiftTypeName.FromModuleQualifiedName(errorTypeName);
            var errorSimpleName = errorTypeName.Split('.').Last();
            module.RegisterType(
                errorSwiftName,
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", errorSimpleName),
                    SwiftTypeName = errorSwiftName,
                    MetadataAccessor = $"$s10TestModule{errorSimpleName}OMa",
                    Flags = TypeRecordFlags.None,
                    Kind = TypeRecordKind.Enum
                });
        }

        typeDatabase.AddModuleDatabase(module);

        // Load Swift database for String
        typeDatabase.LoadModuleDatabaseFromFile(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift", "SwiftDatabase.xml")).Wait();

        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var loggerFactory = new NullLoggerFactory();
        var conductor = new Conductor(loggerFactory);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = handler.Marshal(methodDecl, typeDatabase);

        var context = new TypeHandlerContext(null, new(), null, EmissionContext: new ModuleEmissionContext());
        handler.Emit(csWriter, swiftWriter, env, conductor, context);

        return (csStringWriter.ToString(), swiftStringWriter.ToString());
    }

    private static (string CsOutput, string SwiftOutput) GenerateThrowingFailableConstructor(
        bool hasTypedThrows,
        string errorTypeName = "TestModule.ParseError")
    {
        // Reset static emitter state — these tests need clean dedup tracking
        // Context-based tracking: tests use default context (no parallelism)

        var moduleDecl = new ModuleDecl
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

        var structDecl = new StructDecl
        {
            Name = "Parser",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Parser"),
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule6ParserVMa",
            MangledName = "$s10TestModule6ParserV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Conformances = new List<TypeConformance>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(structDecl);

        // Failable constructor: init?(input:) throws(ParseError)
        // Return type is Optional<Self> for failable
        var optionalReturn = new NamedTypeSpec("Swift.Optional");
        optionalReturn.GenericParameters.Add(new NamedTypeSpec("TestModule.Parser"));

        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = optionalReturn,
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = structDecl,
                ModuleDecl = moduleDecl
            },
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"),
                Name = "value",
                PrivateName = "value",
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = structDecl,
                ModuleDecl = moduleDecl
            }
        };

        var constructorDecl = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule6ParserV5valueyACSgSiKcfC",
            MethodType = MethodType.Static,
            IsConstructor = true,
            IsFailable = true,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = structDecl,
            ModuleDecl = moduleDecl,
            Throws = true,
            IsAsync = false,
            Visibility = Visibility.Public,
            ThrownErrorType = hasTypedThrows ? TypeSpecParser.Parse(errorTypeName) : null
        };
        structDecl.Methods.Add(constructorDecl);

        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "$sSqMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int32"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
                MetadataAccessor = "$ss5Int32VMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/fake/path");
        module.RegisterType(
            structDecl.SwiftTypeName,
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Parser"),
                SwiftTypeName = structDecl.SwiftTypeName,
                MetadataAccessor = "$s10TestModule6ParserVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });

        if (hasTypedThrows)
        {
            var errorSwiftName = SwiftTypeName.FromModuleQualifiedName(errorTypeName);
            var errorSimpleName = errorTypeName.Split('.').Last();
            module.RegisterType(
                errorSwiftName,
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", errorSimpleName),
                    SwiftTypeName = errorSwiftName,
                    MetadataAccessor = $"$s10TestModule{errorSimpleName}OMa",
                    Flags = TypeRecordFlags.None,
                    Kind = TypeRecordKind.Enum
                });
        }
        typeDatabase.AddModuleDatabase(module);

        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var handler = new ConstructorHandler(new NullLogger<ConstructorHandler>(), new HashSet<string>());
        var env = new MethodEnvironment(constructorDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        var context = new TypeHandlerContext(null, new(), null, EmissionContext: new ModuleEmissionContext());
        handler.Emit(csWriter, swiftWriter, env, conductor, context);

        return (csStringWriter.ToString(), swiftStringWriter.ToString());
    }

    private static MethodDecl CreateMethodDecl(bool throws_)
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
            ModuleDecl = null
        };

        return new MethodDecl
        {
            Name = "test",
            MangledName = "$s10TestModule4testyyF",
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
                    ParentDecl = moduleDecl,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = throws_,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    /// <summary>
    /// Generates a throwing method with a simple enum error type (has SimpleEnum flag).
    /// Simple enums are projected as C# value types — MarshalFromSwift copies, no ownership transfer.
    /// </summary>
    private static (string CsOutput, string SwiftOutput) GenerateThrowingMethodWithSimpleEnum()
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
            ModuleDecl = null
        };

        var structDecl = new StructDecl
        {
            Name = "Parser",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Parser"),
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule6ParserVMa",
            MangledName = "$s10TestModule6ParserV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Conformances = new List<TypeConformance>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(structDecl);

        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"),
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = structDecl,
                ModuleDecl = moduleDecl
            },
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                Name = "input",
                PrivateName = "input",
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = structDecl,
                ModuleDecl = moduleDecl
            }
        };

        var methodDecl = new MethodDecl
        {
            Name = "parse",
            MangledName = "$s10TestModule6ParserV5parse_s5Int32VSSF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = structDecl,
            ModuleDecl = moduleDecl,
            Throws = true,
            IsAsync = false,
            Visibility = Visibility.Public,
            ThrownErrorType = TypeSpecParser.Parse("TestModule.SimpleError")
        };

        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");

        module.RegisterType(
            structDecl.SwiftTypeName,
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Parser"),
                SwiftTypeName = structDecl.SwiftTypeName,
                MetadataAccessor = "$s10TestModule6ParserVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });

        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int32"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
                MetadataAccessor = "$ss5Int32VMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });

        // Register as SIMPLE enum (SimpleEnum flag) — no ownership transfer
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.SimpleError"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "SimpleError"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SimpleError"),
                MetadataAccessor = "$s10TestModule11SimpleErrorOMa",
                Flags = TypeRecordFlags.SimpleEnum,
                Kind = TypeRecordKind.Enum
            });

        typeDatabase.AddModuleDatabase(module);
        typeDatabase.LoadModuleDatabaseFromFile(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift", "SwiftDatabase.xml")).Wait();

        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var loggerFactory = new NullLoggerFactory();
        var conductor = new Conductor(loggerFactory);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = handler.Marshal(methodDecl, typeDatabase);

        var context = new TypeHandlerContext(null, new(), null, EmissionContext: new ModuleEmissionContext());
        handler.Emit(csWriter, swiftWriter, env, conductor, context);

        return (csStringWriter.ToString(), swiftStringWriter.ToString());
    }

    #endregion
}
