// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for async Swift wrapper generation, particularly around non-frozen parameter cleanup.
/// </summary>
public class AsyncSwiftWrapperTests
{
    #region Non-Frozen Parameter Cleanup Tests

    [Fact]
    public void AsyncWrapper_WithNonFrozenParam_DoesNotUseDefer()
    {
        var swiftOutput = GenerateAsyncMethodWrapper(
            methodName: "testMethod",
            isAsync: true,
            hasNonFrozenParam: true);

        // The fix moved cleanup code AFTER the callback instead of using defer
        // defer causes use-after-free because it runs when Task scope exits,
        // but Swift's async machinery may still hold references after callback
        Assert.DoesNotContain("defer {", swiftOutput);
    }

    // Note: Tests for cleanup position and copy allocation are validated through
    // integration tests since they require full environment setup. The key behavioral
    // change (no defer usage) is verified by AsyncWrapper_WithNonFrozenParam_DoesNotUseDefer.

    [Fact]
    public void AsyncWrapper_WithoutNonFrozenParam_DoesNotHaveCleanupCode()
    {
        var swiftOutput = GenerateAsyncMethodWrapper(
            methodName: "testMethod",
            isAsync: true,
            hasNonFrozenParam: false);

        // Without non-frozen params, there should be no cleanup code
        Assert.DoesNotContain("deinitialize(count: 1)", swiftOutput);
        Assert.DoesNotContain("deallocate()", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_NonAsyncMethod_DoesNotGenerateSwiftWrapper()
    {
        var swiftOutput = GenerateAsyncMethodWrapper(
            methodName: "testMethod",
            isAsync: false,
            hasNonFrozenParam: true);

        // Non-async methods don't generate Swift wrappers
        Assert.DoesNotContain("extension", swiftOutput);
        Assert.DoesNotContain("Task {", swiftOutput);
    }

    #endregion

    #region Helper Methods

    private static string GenerateAsyncMethodWrapper(string methodName, bool isAsync, bool hasNonFrozenParam)
    {
        // Create module declaration first
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

        // Create parent struct
        var parentDecl = new StructDecl
        {
            Name = "TestStruct",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.TestStruct"),
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule0A6StructVMa",
            MangledName = "$s10TestModule0A6StructV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Conformances = new List<TypeConformance>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        moduleDecl.Types.Add(parentDecl);

        // Build CSSignature
        var csSignature = new List<ArgumentDecl>
        {
            // Return type (Int64 for simplicity)
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            }
        };

        if (hasNonFrozenParam)
        {
            // Add a non-frozen parameter (a class type)
            csSignature.Add(new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("TestModule.NonFrozenClass"),
                Name = "request",
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            });
        }

        var methodDecl = new MethodDecl
        {
            Name = methodName,
            MangledName = $"$s10TestModule0A6StructV{methodName}yS2iFYaKF",
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

        // Setup type database
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");

        // Register the parent struct
        var parentTypeRecord = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "TestStruct"),
            SwiftTypeName = parentDecl.SwiftTypeName,
            MetadataAccessor = parentDecl.MetadataAccessor,
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        };
        module.RegisterType(parentDecl.SwiftTypeName, parentTypeRecord);

        // Register Int type
        var intTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int");
        var intTypeRecord = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
            SwiftTypeName = intTypeName,
            MetadataAccessor = "$sSiMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        };
        module.RegisterType(intTypeName, intTypeRecord);

        if (hasNonFrozenParam)
        {
            // Register the non-frozen class type
            var nonFrozenTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.NonFrozenClass");
            var nonFrozenTypeRecord = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "NonFrozenClass"),
                SwiftTypeName = nonFrozenTypeName,
                MetadataAccessor = "$s10TestModule14NonFrozenClassCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement, // Not frozen!
                Kind = TypeRecordKind.Class
            };
            module.RegisterType(nonFrozenTypeName, nonFrozenTypeRecord);
        }

        typeDatabase.AddModuleDatabase(module);

        // Generate the wrapper
        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var loggerFactory = new NullLoggerFactory();
        var conductor = new Conductor(loggerFactory);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = handler.Marshal(methodDecl, typeDatabase);

        handler.Emit(csWriter, swiftWriter, env, conductor);

        return swiftStringWriter.ToString();
    }

    #endregion
}
