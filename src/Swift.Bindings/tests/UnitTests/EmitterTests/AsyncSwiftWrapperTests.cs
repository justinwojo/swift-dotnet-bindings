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

    #region BitwiseCopyable Avoidance Tests

    [Fact]
    public void AsyncWrapper_ClassReturnType_UsesUnmanagedPassRetainedInsteadOfStoreBytes()
    {
        // Class types like UIImage are not BitwiseCopyable in Swift 6+.
        // The wrapper must use Unmanaged.passRetained().toOpaque() to get a raw pointer
        // and store it using storeBytes with UnsafeMutableRawPointer (which IS BitwiseCopyable),
        // instead of storeBytes(of: result, as: ClassName.self) which crashes.
        var (_, swiftOutput) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "TestModule.ImageResult",
            returnKind: TypeRecordKind.Class);

        // Should use Unmanaged.passRetained pattern
        Assert.Contains("Unmanaged.passRetained(", swiftOutput);
        Assert.Contains("as: UnsafeMutableRawPointer.self)", swiftOutput);

        // Should NOT use storeBytes with the class type directly (BitwiseCopyable crash)
        Assert.DoesNotContain("as: TestModule.ImageResult.self)", swiftOutput);

        // Should NOT use initializeMemory (that's for structs/enums)
        Assert.DoesNotContain("initializeMemory", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_StructReturnType_UsesCopyMemoryInsteadOfStoreBytes()
    {
        // Non-primitive struct types may not be BitwiseCopyable (e.g., structs with String fields).
        // The wrapper must use withUnsafePointer + copyMemory for a raw bitwise copy without the
        // BitwiseCopyable constraint. This avoids both the storeBytes crash AND the initializeMemory
        // leak (initializeMemory adds copy semantics that SBW_Free's raw deallocation can't undo).
        var (_, swiftOutput) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "TestModule.DataResult",
            returnKind: TypeRecordKind.Struct);

        // Should use withUnsafePointer + copyMemory pattern
        Assert.Contains("withUnsafePointer(to:", swiftOutput);
        Assert.Contains("copyMemory(from: UnsafeRawPointer(_srcPtr)", swiftOutput);

        // Should NOT use storeBytes with the struct type (BitwiseCopyable may fail)
        Assert.DoesNotContain("storeBytes(of:", swiftOutput);

        // Should NOT use initializeMemory (leaks internal references when SBW_Free deallocates)
        Assert.DoesNotContain("initializeMemory", swiftOutput);

        // Should NOT use Unmanaged.passRetained (that's for class types)
        Assert.DoesNotContain("Unmanaged.passRetained", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_EnumReturnType_UsesCopyMemoryInsteadOfStoreBytes()
    {
        // Non-primitive enum types may not be BitwiseCopyable.
        // The wrapper must use copyMemory instead of storeBytes or initializeMemory.
        var (_, swiftOutput) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "TestModule.StatusCode",
            returnKind: TypeRecordKind.Enum);

        // Should use withUnsafePointer + copyMemory pattern
        Assert.Contains("withUnsafePointer(to:", swiftOutput);
        Assert.Contains("copyMemory(from: UnsafeRawPointer(_srcPtr)", swiftOutput);

        // Should NOT use storeBytes with the enum type
        Assert.DoesNotContain("storeBytes(of:", swiftOutput);

        // Should NOT use initializeMemory (leaks)
        Assert.DoesNotContain("initializeMemory", swiftOutput);

        // Should NOT use Unmanaged.passRetained (that's for class types)
        Assert.DoesNotContain("Unmanaged.passRetained", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_PrimitiveReturnType_DoesNotUsePointerMarshalling()
    {
        // Primitive types (Int, Double, Bool) are passed directly through @convention(c)
        // callbacks without pointer indirection, so no marshalling patterns are needed.
        var swiftOutput = GenerateAsyncMethodWrapper(
            methodName: "fetchCount",
            isAsync: true,
            hasNonFrozenParam: false);

        // Should NOT contain any memory storage pattern
        Assert.DoesNotContain("storeBytes", swiftOutput);
        Assert.DoesNotContain("initializeMemory", swiftOutput);
        Assert.DoesNotContain("copyMemory", swiftOutput);
        Assert.DoesNotContain("OpaquePointer", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_ClassReturnType_AllocatesPointerSizedBuffer()
    {
        // For class types, the buffer should be pointer-sized (UnsafeMutableRawPointer),
        // not sized to the class type's metadata.
        var (_, swiftOutput) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "TestModule.ImageResult",
            returnKind: TypeRecordKind.Class);

        // Should allocate using UnsafeMutableRawPointer size (pointer-sized)
        Assert.Contains("MemoryLayout<UnsafeMutableRawPointer>.size", swiftOutput);
        Assert.Contains("MemoryLayout<UnsafeMutableRawPointer>.alignment", swiftOutput);

        // Should NOT allocate using the class type's size
        Assert.DoesNotContain("MemoryLayout<TestModule.ImageResult>.size", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_StructReturnType_AllocatesTypeSizedBuffer()
    {
        // For struct/enum types, the buffer should be sized to the type's metadata.
        var (_, swiftOutput) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "TestModule.DataResult",
            returnKind: TypeRecordKind.Struct);

        // Should allocate using the struct type's size
        Assert.Contains("MemoryLayout<TestModule.DataResult>.size", swiftOutput);
        Assert.Contains("MemoryLayout<TestModule.DataResult>.alignment", swiftOutput);
    }

    #endregion

    #region ObjC-Bridged Async Callback Tests

    [Fact]
    public void AsyncWrapper_ObjCBridgedReturnType_UsesGetNSObject()
    {
        // ObjC-bridged class types (like UIImage) must use GetNSObject<T> instead of
        // SwiftMarshal.MarshalFromSwift<T> in the C# async callback.
        var (csOutput, swiftOutput) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "UIKit.UIImage",
            returnKind: TypeRecordKind.Class,
            isObjCBridged: true);

        // C# callback should use GetNSObject<T> for ObjC types
        Assert.Contains("GetNSObject<", csOutput);

        // Should NOT use SwiftMarshal.MarshalFromSwift (that throws for ObjC types)
        Assert.DoesNotContain("MarshalFromSwift", csOutput);

        // Should read the object pointer from buffer (isClassType=true)
        Assert.Contains("_retainedObjPtr", csOutput);

        // GetNSObject takes ownership of the passRetained reference — Arc.Release would
        // deallocate the object while the wrapper still references it (use-after-free)
        Assert.DoesNotContain("Arc.Release(_retainedObjPtr)", csOutput);
    }

    [Fact]
    public void AsyncWrapper_NonObjCClassReturnType_UsesMarshalFromSwift()
    {
        // Non-ObjC class types (Swift classes) should use MarshalFromSwift, not GetNSObject.
        var (csOutput, _) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "TestModule.ImageResult",
            returnKind: TypeRecordKind.Class);

        // C# callback should use SwiftMarshal.MarshalFromSwift for Swift types
        Assert.Contains("MarshalFromSwift", csOutput);

        // Should NOT use GetNSObject (that's for ObjC types only)
        Assert.DoesNotContain("GetNSObject", csOutput);

        // Non-ObjC class types still need Arc.Release to balance passRetained
        Assert.Contains("Arc.Release(_retainedObjPtr)", csOutput);
    }

    [Fact]
    public void AsyncWrapper_ObjCBridgedReturnType_SwiftUsesUnmanagedRetain()
    {
        // The Swift side must use Unmanaged.passRetained for ObjC class types
        // (same as any class type).
        var (_, swiftOutput) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "UIKit.UIImage",
            returnKind: TypeRecordKind.Class,
            isObjCBridged: true);

        // Should use Unmanaged.passRetained pattern (class type)
        Assert.Contains("Unmanaged.passRetained(", swiftOutput);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Generates Swift output for an async method returning a complex (non-primitive) type.
    /// Used to test BitwiseCopyable-safe marshalling patterns.
    /// </summary>
    private static (string csOutput, string swiftOutput) GenerateAsyncMethodWithComplexReturn(
        string returnTypeName,
        TypeRecordKind returnKind,
        bool isObjCBridged = false)
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

        // Create parent class with singleton pattern (static 'shared' property)
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
        // Add 'shared' property so HasSingletonPattern returns true
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

        // Build CSSignature with complex return type
        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec(returnTypeName),
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            }
        };

        var methodDecl = new MethodDecl
        {
            Name = "fetchResult",
            MangledName = $"$s10TestModule8PipelineC11fetchResult_tYaKF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = true,
            Visibility = Visibility.Public
        };

        // Setup type database
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");

        // Register the parent class
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

        // Register the return type — may be in a different module (e.g., UIKit.UIImage)
        var returnSwiftTypeName = SwiftTypeName.FromModuleQualifiedName(returnTypeName);
        var returnFlags = returnKind == TypeRecordKind.Class
            ? TypeRecordFlags.RequiresMemoryManagement
            : returnKind == TypeRecordKind.Struct
                ? TypeRecordFlags.Frozen
                : TypeRecordFlags.None;
        if (isObjCBridged)
            returnFlags |= TypeRecordFlags.ObjCBridged;
        var returnNamespace = returnTypeName.Contains('.') ? returnTypeName.Substring(0, returnTypeName.IndexOf('.')) : "Swift.TestModule";
        var returnTypeRecord = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName(returnNamespace, returnTypeName.Split('.').Last()),
            SwiftTypeName = returnSwiftTypeName,
            MetadataAccessor = $"$s10TestModule{returnTypeName.Split('.').Last()}CMa",
            Flags = returnFlags,
            Kind = returnKind
        };
        // Register in the correct module database (UIKit.UIImage → UIKit module)
        var returnModule = returnSwiftTypeName.Module;
        if (returnModule == "TestModule")
        {
            module.RegisterType(returnSwiftTypeName, returnTypeRecord);
        }
        else
        {
            var returnModuleDb = new ModuleTypeDatabase(returnModule, $"/System/Library/Frameworks/{returnModule}.framework/{returnModule}");
            returnModuleDb.RegisterType(returnSwiftTypeName, returnTypeRecord);
            typeDatabase.AddModuleDatabase(returnModuleDb);
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

        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return (csStringWriter.ToString(), swiftStringWriter.ToString());
    }

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

        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return swiftStringWriter.ToString();
    }

    #endregion
}
