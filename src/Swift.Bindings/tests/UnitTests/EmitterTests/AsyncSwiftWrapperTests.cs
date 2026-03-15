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

    #region Async Tuple ObjC Retain Tests

    [Fact]
    public void AsyncWrapper_TupleWithObjCClass_RetainsClassElement()
    {
        // When an async method returns a tuple containing an ObjC class (e.g., URLResponse),
        // the Swift wrapper must explicitly retain the class element before passing through
        // @convention(c). Without retain, ARC releases the object after the callback returns,
        // leaving C#'s GetNSObject wrapper with a dangling pointer.
        var (csOutput, swiftOutput) = GenerateAsyncMethodWithTupleReturn(
            elements: new[]
            {
                ("Foundation.Data", TypeRecordKind.Struct, false),
                ("Foundation.URLResponse", TypeRecordKind.Class, true),
            });

        // Swift wrapper should contain Unmanaged.passRetained for the ObjC class element
        Assert.Contains("Unmanaged<AnyObject>.passRetained(", swiftOutput);
        // Should reference the correct tuple element (.1 for URLResponse)
        Assert.Contains(".1 as AnyObject)", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_TupleWithPrimitiveOnly_DoesNotRetain()
    {
        // Tuple of primitives doesn't need retain
        var (_, swiftOutput) = GenerateAsyncMethodWithTupleReturn(
            elements: new[]
            {
                ("Swift.Int", TypeRecordKind.Struct, false),
                ("Swift.Double", TypeRecordKind.Struct, false),
            });

        Assert.DoesNotContain("Unmanaged", swiftOutput);
        Assert.DoesNotContain("passRetained", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_TupleWithOptionalObjCClass_UsesConditionalRetain()
    {
        // Optional<ObjCClass> needs conditional retain (nil check)
        var (_, swiftOutput) = GenerateAsyncMethodWithTupleReturn(
            elements: new[]
            {
                ("Swift.Int", TypeRecordKind.Struct, false),
            },
            optionalObjCElement: ("Foundation.URLResponse", TypeRecordKind.Class, true));

        // Should use conditional retain: if let ... { passRetained }
        Assert.Contains("if let _tupleObj", swiftOutput);
        Assert.Contains("Unmanaged<AnyObject>.passRetained(", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_TupleWithMultipleObjCClasses_RetainsAll()
    {
        // Multiple ObjC class elements should all be retained
        var (_, swiftOutput) = GenerateAsyncMethodWithTupleReturn(
            elements: new[]
            {
                ("Foundation.URLResponse", TypeRecordKind.Class, true),
                ("UIKit.UIImage", TypeRecordKind.Class, true),
            });

        // Should have two passRetained calls
        Assert.Contains(".0 as AnyObject)", swiftOutput);
        Assert.Contains(".1 as AnyObject)", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_TupleWithStructElement_DoesNotRetainStruct()
    {
        // Struct elements (even ObjC-bridgeable like Foundation.Data) should NOT be
        // retained — Swift's auto-bridging handles the retain for bridgeable types,
        // and non-bridgeable structs are value types.
        var (_, swiftOutput) = GenerateAsyncMethodWithTupleReturn(
            elements: new[]
            {
                ("Foundation.Data", TypeRecordKind.Struct, false),
                ("Swift.Int", TypeRecordKind.Struct, false),
            });

        Assert.DoesNotContain("Unmanaged", swiftOutput);
        Assert.DoesNotContain("passRetained", swiftOutput);
    }

    #endregion

    #region Async DynamicSelf Return Type Tests

    [Fact]
    public void AsyncWrapper_DynamicSelfReturn_UsesParentClassName()
    {
        // DynamicSelf (Self return type) in async wrappers is emitted as a free function,
        // where bare "Self" is invalid Swift. The wrapper must resolve Self to the parent
        // class type name (e.g., "Alamofire.DataRequest") for MemoryLayout calculations.
        var (_, swiftOutput) = GenerateAsyncMethodWithDynamicSelfReturn();

        // Should NOT contain MemoryLayout<Self> (invalid in free functions)
        Assert.DoesNotContain("MemoryLayout<Self>", swiftOutput);

        // Should use Unmanaged.passRetained (class type path, not struct copyMemory path)
        Assert.Contains("Unmanaged.passRetained(", swiftOutput);
        Assert.Contains("MemoryLayout<UnsafeMutableRawPointer>.size", swiftOutput);
    }

    [Fact]
    public void AsyncWrapper_DynamicSelfReturn_TreatsAsClassType()
    {
        // DynamicSelf is only allowed for class parents (validated by WrapperValidation).
        // The async wrapper must treat it as a class type, using Unmanaged.passRetained
        // instead of the struct/enum copyMemory path.
        var (_, swiftOutput) = GenerateAsyncMethodWithDynamicSelfReturn();

        // Should use class path (Unmanaged.passRetained), NOT struct path (copyMemory)
        Assert.Contains("Unmanaged.passRetained(", swiftOutput);
        Assert.DoesNotContain("copyMemory(from:", swiftOutput);
        Assert.DoesNotContain("withUnsafePointer(to:", swiftOutput);
    }

    #endregion

    #region Async _sbwTask Parameter Naming Tests

    [Fact]
    public void AsyncWrapper_TaskBaseParam_Uses_sbwTask_NotTask()
    {
        // S11: Kingfisher's URLSession delegate methods have a parameter named "task",
        // which collides with the async wrapper's base parameter also named "task".
        // Fix: renamed base parameter to "_sbwTask".
        // Uses GenerateAsyncMethodWithComplexReturn (class return) which produces Swift output.
        var (_, swiftOutput) = GenerateAsyncMethodWithComplexReturn(
            returnTypeName: "TestModule.DataResult",
            returnKind: TypeRecordKind.Struct);

        // Must use _sbwTask, not bare "task"
        Assert.Contains("_sbwTask", swiftOutput);

        // The old name "task" should not appear as a standalone parameter
        // (it may appear as part of _sbwTask, so check for the exact old pattern)
        Assert.DoesNotContain("_ task:", swiftOutput);
        Assert.DoesNotContain("task: Int64", swiftOutput);
    }

    #endregion

    #region Async Free Function Tests

    [Fact]
    public void AsyncWrapper_FreeFunction_DoesNotEmitSelfPrefix()
    {
        // Free functions (methods on ModuleDecl, not a type) should NOT have "self." prefix
        // in the async wrapper. Before the fix, the else-branch unconditionally set
        // methodCallPrefix = "self." even when parentTypeName was null (free function).
        var (_, swiftOutput) = GenerateAsyncFreeFunctionWrapper(
            methodName: "fetchGlobalData",
            isAsync: true);

        // Verify we actually got output (async wrapper was emitted)
        Assert.NotEmpty(swiftOutput);

        // Should NOT contain "self." — free functions have no self
        Assert.DoesNotContain("self.", swiftOutput);

        // Should contain the function call without any prefix
        Assert.Contains("fetchGlobalData(", swiftOutput);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Generates Swift and C# output for an async method returning a tuple.
    /// Used to test ObjC element retain behavior in async tuple callbacks.
    /// </summary>
    private static (string csOutput, string swiftOutput) GenerateAsyncMethodWithTupleReturn(
        (string typeName, TypeRecordKind kind, bool isObjCBridged)[] elements,
        (string typeName, TypeRecordKind kind, bool isObjCBridged)? optionalObjCElement = null)
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
        // Add 'shared' property for singleton pattern
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

        // Build tuple TypeSpec
        var tupleElements = new List<TypeSpec>();
        foreach (var elem in elements)
        {
            tupleElements.Add(new NamedTypeSpec(elem.typeName));
        }
        if (optionalObjCElement.HasValue)
        {
            var opt = optionalObjCElement.Value;
            var optionalTypeSpec = new NamedTypeSpec("Swift.Optional");
            optionalTypeSpec.GenericParameters.Add(new NamedTypeSpec(opt.typeName));
            tupleElements.Add(optionalTypeSpec);
        }
        var tupleTypeSpec = new TupleTypeSpec(tupleElements);

        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = tupleTypeSpec,
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
            Name = "fetchData",
            MangledName = "$s10TestModule8PipelineC9fetchData_tYaKF",
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

        // Register parent class
        module.RegisterType(parentDecl.SwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Pipeline"),
            SwiftTypeName = parentDecl.SwiftTypeName,
            MetadataAccessor = "$s10TestModule8PipelineCMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class
        });

        // Track extra module databases needed for types in other modules
        var extraModules = new Dictionary<string, ModuleTypeDatabase>();

        // Register each element type
        void RegisterElementType(string typeName, TypeRecordKind kind, bool isObjCBridged)
        {
            var swiftName = SwiftTypeName.FromModuleQualifiedName(typeName);
            var flags = kind == TypeRecordKind.Class
                ? TypeRecordFlags.RequiresMemoryManagement
                : TypeRecordFlags.Frozen;
            if (isObjCBridged)
                flags |= TypeRecordFlags.ObjCBridged;
            var ns = typeName.Contains('.') ? typeName.Substring(0, typeName.IndexOf('.')) : "TestModule";
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(ns, typeName.Split('.').Last()),
                SwiftTypeName = swiftName,
                MetadataAccessor = $"$s{typeName.Replace(".", "")}Ma",
                Flags = flags,
                Kind = kind
            };
            var elemModule = swiftName.Module;
            if (elemModule == "TestModule")
            {
                module.RegisterType(swiftName, record);
            }
            else
            {
                if (!extraModules.TryGetValue(elemModule, out var elemModuleDb))
                {
                    elemModuleDb = new ModuleTypeDatabase(elemModule, $"/System/Library/Frameworks/{elemModule}.framework/{elemModule}");
                    extraModules[elemModule] = elemModuleDb;
                }
                elemModuleDb.RegisterType(swiftName, record);
            }
        }

        foreach (var elem in elements)
            RegisterElementType(elem.typeName, elem.kind, elem.isObjCBridged);
        if (optionalObjCElement.HasValue)
        {
            var opt = optionalObjCElement.Value;
            RegisterElementType(opt.typeName, opt.kind, opt.isObjCBridged);
        }

        // Register Swift built-in types
        if (!extraModules.TryGetValue("Swift", out var swiftModuleDb))
        {
            swiftModuleDb = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
            extraModules["Swift"] = swiftModuleDb;
        }

        // Swift.Optional
        var optionalSwiftName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional");
        swiftModuleDb.RegisterType(optionalSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
            SwiftTypeName = optionalSwiftName,
            MetadataAccessor = "$sSqMa",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Enum
        });

        // Swift.Int
        var intSwiftName = SwiftTypeName.FromModuleQualifiedName("Swift.Int");
        swiftModuleDb.RegisterType(intSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "nint"),
            SwiftTypeName = intSwiftName,
            MetadataAccessor = "$sSiMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        // Swift.Double
        var doubleSwiftName = SwiftTypeName.FromModuleQualifiedName("Swift.Double");
        swiftModuleDb.RegisterType(doubleSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Double"),
            SwiftTypeName = doubleSwiftName,
            MetadataAccessor = "$sSdMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        // Add all extra module databases
        foreach (var extraModule in extraModules.Values)
            typeDatabase.AddModuleDatabase(extraModule);

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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Pipeline"),
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
        var returnNamespace = returnTypeName.Contains('.') ? returnTypeName.Substring(0, returnTypeName.IndexOf('.')) : "TestModule";
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
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "TestStruct"),
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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "NonFrozenClass"),
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

    /// <summary>
    /// Generates C# and Swift output for an async free function (method on ModuleDecl, not a type).
    /// Used to verify that free functions don't get a "self." prefix in the wrapper.
    /// </summary>
    private static (string csOutput, string swiftOutput) GenerateAsyncFreeFunctionWrapper(string methodName, bool isAsync)
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

        // Build CSSignature with Int return type
        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = moduleDecl,
                ModuleDecl = moduleDecl
            }
        };

        // Free function: ParentDecl is the ModuleDecl, not a TypeDecl
        var methodDecl = new MethodDecl
        {
            Name = methodName,
            MangledName = $"$s10TestModule{methodName}yS2iFYaKF",
            // Use Instance to exercise the else-branch in EmitAsync where
            // parentTypeName==null would incorrectly set methodCallPrefix="self."
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = isAsync,
            Visibility = Visibility.Public
        };

        // Setup type database
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");
        typeDatabase.AddModuleDatabase(module);

        // Register Int type in the "Swift" module (TypeDatabase resolves by module name)
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        var intTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int");
        swiftModule.RegisterType(intTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
            SwiftTypeName = intTypeName,
            MetadataAccessor = "$sSiMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        typeDatabase.AddModuleDatabase(swiftModule);

        // Generate the wrapper — use WrapperEmitter directly (like GenerateAsyncMethodWithComplexReturn)
        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = (MethodEnvironment)handler.Marshal(methodDecl, typeDatabase);

        var signatureHandler = new SignatureHandler(env);
        var wrapperEmitter = new WrapperEmitter(env, signatureHandler);
        wrapperEmitter.EmitMethod(csWriter, swiftWriter);

        return (csStringWriter.ToString(), swiftStringWriter.ToString());
    }

    /// <summary>
    /// Generates Swift output for an async method returning DynamicSelf (Self).
    /// Used to test that Self is resolved to the parent class type in async free-function wrappers.
    /// </summary>
    private static (string csOutput, string swiftOutput) GenerateAsyncMethodWithDynamicSelfReturn()
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

        var parentDecl = new ClassDecl
        {
            Name = "DataRequest",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DataRequest"),
            MangledName = "$s10TestModule11DataRequestCN",
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

        // DynamicSelf return type: NamedTypeSpec("Self") makes IsDynamicSelf true
        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Self"),
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
            Name = "onHTTPResponse",
            MangledName = "$s10TestModule11DataRequestC14onHTTPResponseACXDySo17NSHTTPURLResponseCYaYbc_tF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = true,
            IsAsync = true,
            Visibility = Visibility.Public
        };

        // Setup type database
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");

        // Register the parent class
        module.RegisterType(parentDecl.SwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "DataRequest"),
            SwiftTypeName = parentDecl.SwiftTypeName,
            MetadataAccessor = "$s10TestModule11DataRequestCMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class
        });

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

    #endregion
}
