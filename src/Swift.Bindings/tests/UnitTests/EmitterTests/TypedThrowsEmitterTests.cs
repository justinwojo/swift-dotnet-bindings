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
        var (csOutput, _) = GenerateThrowingMethod(
            isAsync: false,
            hasTypedThrows: true,
            errorTypeName: "TestModule.ParseError");

        Assert.Contains("SwiftException<Swift.TestModule.ParseError>", csOutput);
        Assert.DoesNotContain("SwiftRuntimeException", csOutput);
    }

    [Fact]
    public void SyncMethod_WithoutTypedThrows_EmitsSwiftRuntimeException()
    {
        var (csOutput, _) = GenerateThrowingMethod(
            isAsync: false,
            hasTypedThrows: false);

        Assert.Contains("SwiftRuntimeException", csOutput);
        Assert.DoesNotContain("SwiftException<", csOutput);
    }

    [Fact]
    public void SyncMethod_WithUnresolvableErrorType_FallsBackToSwiftRuntimeException()
    {
        // When error type is not in TypeDatabase, fall back to untyped
        var (csOutput, _) = GenerateThrowingMethod(
            isAsync: false,
            hasTypedThrows: true,
            errorTypeName: "UnknownModule.UnknownError",
            registerErrorType: false);

        Assert.Contains("SwiftRuntimeException", csOutput);
        Assert.DoesNotContain("SwiftException<", csOutput);
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

        // C# side: 4-param delegate with error ptr + size + message + task
        Assert.Contains("IntPtr, nint, IntPtr, IntPtr, void", csOutput);
        // Error type uses fully-qualified C# name from TypeDatabase
        Assert.Contains("MarshalFromSwift<Swift.TestModule.ParseError>", csOutput);
        Assert.Contains("SBW_Free(errorPtr)", csOutput);
        Assert.Contains("SwiftException<Swift.TestModule.ParseError>", csOutput);

        // Swift side: typed error callback with MemoryLayout + copyMemory
        Assert.Contains("MemoryLayout<TestModule.ParseError>.size", swiftOutput);
        Assert.Contains("copyMemory(from: UnsafeRawPointer(_src)", swiftOutput);
        Assert.Contains("UnsafeRawPointer, Int, UnsafePointer<CChar>, Int64", swiftOutput);
    }

    [Fact]
    public void AsyncMethod_WithoutTypedThrows_EmitsUntypedErrorCallback()
    {
        var (csOutput, swiftOutput) = GenerateThrowingMethod(
            isAsync: true,
            hasTypedThrows: false);

        // C# side: 2-param delegate (message + task)
        // Note: DoesNotContain checks are scoped to error-specific patterns
        // (MarshalFromSwift<int> exists for return value marshalling)
        Assert.DoesNotContain("SBW_Free", csOutput);
        Assert.DoesNotContain("SwiftException<", csOutput);
        Assert.Contains("SwiftException(errorMessage)", csOutput);

        // Swift side: untyped catch block
        Assert.Contains("errorCallback($0, task)", swiftOutput);
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

        // Should fall back to untyped pattern
        Assert.Contains("SwiftException(errorMessage)", csOutput);
        Assert.DoesNotContain("SBW_Free", csOutput);
        Assert.DoesNotContain("SwiftException<", csOutput);
        Assert.Contains("errorCallback($0, task)", swiftOutput);
    }

    [Fact]
    public void AsyncFreeFunction_WithTypedThrows_FallsBackToUntyped()
    {
        // D5 guard: free-function async typed throws should fall back to untyped
        var (csOutput, swiftOutput) = GenerateThrowingMethod(
            isAsync: true,
            hasTypedThrows: true,
            errorTypeName: "TestModule.ParseError",
            isFreeFunction: true);

        // Should fall back to untyped pattern (free-function async guard)
        Assert.Contains("SwiftException(errorMessage)", csOutput);
        Assert.DoesNotContain("SBW_Free", csOutput);
        Assert.DoesNotContain("SwiftException<", csOutput);
        Assert.Contains("errorCallback($0, task)", swiftOutput);
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
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Parser"),
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
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", errorSimpleName),
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

        handler.Emit(csWriter, swiftWriter, env, conductor);

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

    #endregion
}
