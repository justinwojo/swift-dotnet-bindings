// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for IsConstrainedExistential, ConstrainedExistentialBridge,
/// ClassBound flag serialization, and demangling resilience.
/// </summary>
[Collection("ReportCollector")]
public class ConstrainedExistentialBridgeTests
{
    private readonly ILogger _logger = NullLogger.Instance;

    #region IsConstrainedExistential Tests

    [Fact]
    public void IsConstrainedExistential_ClassBoundWithConcreteArgs_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabaseWithClassBoundProtocol();
        var protocol = new NamedTypeSpec("TestModule.CameraFrameAnalyzer",
            new TypeSpec[] { new NamedTypeSpec("TestModule.CameraFrame"), new NamedTypeSpec("TestModule.UIEvent") });
        var protocolList = new ProtocolListTypeSpec(new[] { protocol });

        Assert.True(ExistentialHandler.IsConstrainedExistential(protocolList, typeDatabase));
    }

    [Fact]
    public void IsConstrainedExistential_NonClassBound_StillReturnsTrue()
    {
        // Non-class-bound protocols are still accepted — ISwiftObject.SwiftHandle
        // throws for non-heap types, providing runtime safety.
        var typeDatabase = CreateTypeDatabaseWithNonClassBoundProtocol();
        var protocol = new NamedTypeSpec("TestModule.ValueAnalyzer",
            new TypeSpec[] { new NamedTypeSpec("TestModule.CameraFrame") });
        var protocolList = new ProtocolListTypeSpec(new[] { protocol });

        Assert.True(ExistentialHandler.IsConstrainedExistential(protocolList, typeDatabase));
    }

    [Fact]
    public void IsConstrainedExistential_GenericArgs_ReturnsFalse()
    {
        // Generic type parameter τ_0_0 is not concrete
        var typeDatabase = CreateTypeDatabaseWithClassBoundProtocol();
        var protocol = new NamedTypeSpec("TestModule.CameraFrameAnalyzer",
            new TypeSpec[] { new NamedTypeSpec("τ_0_0") });
        var protocolList = new ProtocolListTypeSpec(new[] { protocol });

        Assert.False(ExistentialHandler.IsConstrainedExistential(protocolList, typeDatabase));
    }

    [Fact]
    public void IsConstrainedExistential_UnconstrainedProtocol_ReturnsFalse()
    {
        // Protocol with no generic parameters — not constrained
        var typeDatabase = CreateTypeDatabaseWithClassBoundProtocol();
        var protocol = new NamedTypeSpec("TestModule.CameraFrameAnalyzer");
        var protocolList = new ProtocolListTypeSpec(new[] { protocol });

        Assert.False(ExistentialHandler.IsConstrainedExistential(protocolList, typeDatabase));
    }

    [Fact]
    public void IsConstrainedExistential_NullTypeSpec_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabaseWithClassBoundProtocol();
        Assert.False(ExistentialHandler.IsConstrainedExistential(null, typeDatabase));
    }

    [Fact]
    public void IsConstrainedExistential_NamedTypeSpec_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabaseWithClassBoundProtocol();
        var namedType = new NamedTypeSpec("TestModule.SomeClass");
        Assert.False(ExistentialHandler.IsConstrainedExistential(namedType, typeDatabase));
    }

    #endregion

    #region ClassBound Flag Serialization Tests

    [Fact]
    public void ClassBoundFlag_SetInTypeRecordFlags()
    {
        var flags = TypeRecordFlags.ClassBound;
        Assert.True(flags.HasFlag(TypeRecordFlags.ClassBound));
    }

    [Fact]
    public void ClassBoundFlag_CombinesWithOtherFlags()
    {
        var flags = TypeRecordFlags.HasAssociatedTypes | TypeRecordFlags.ClassBound;
        Assert.True(flags.HasFlag(TypeRecordFlags.ClassBound));
        Assert.True(flags.HasFlag(TypeRecordFlags.HasAssociatedTypes));
        Assert.False(flags.HasFlag(TypeRecordFlags.HasSelfRequirement));
    }

    #endregion

    #region ConstrainedExistentialBridge Gate Tests

    [Fact]
    public void TryEmitConstructor_NoConstrainedExistential_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("MyModel", moduleDecl, typeDatabase);

        var constructor = CreateConstructorDecl(classDecl, moduleDecl, new[]
        {
            CreateArgumentDecl("name", new NamedTypeSpec("Swift.Int"))
        });

        var env = new MethodEnvironment(constructor, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftWriter = new SwiftWriter(new StringWriter());

        Assert.False(ConstrainedExistentialBridge.TryEmitConstructor(csWriter, swiftWriter, env, _logger));
    }

    [Fact]
    public void TryEmitConstructor_StructParent_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabaseWithClassBoundProtocol();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDecl("MyStruct", moduleDecl, typeDatabase);

        var protocol = new NamedTypeSpec("TestModule.CameraFrameAnalyzer",
            new TypeSpec[] { new NamedTypeSpec("TestModule.CameraFrame"), new NamedTypeSpec("TestModule.UIEvent") });
        var protocolList = new ProtocolListTypeSpec(new[] { protocol });

        var constructor = CreateConstructorDecl(structDecl, moduleDecl, new[]
        {
            CreateArgumentDecl("analyzer", protocolList)
        });

        var env = new MethodEnvironment(constructor, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftWriter = new SwiftWriter(new StringWriter());

        Assert.False(ConstrainedExistentialBridge.TryEmitConstructor(csWriter, swiftWriter, env, _logger));
    }

    [Fact]
    public void TryEmitConstructor_ConstrainedExistentialParam_EmitsSwiftWrapper()
    {
        var typeDatabase = CreateTypeDatabaseWithClassBoundProtocol();
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("ScannerModel", moduleDecl, typeDatabase);

        var protocol = new NamedTypeSpec("TestModule.CameraFrameAnalyzer",
            new TypeSpec[] { new NamedTypeSpec("TestModule.CameraFrame"), new NamedTypeSpec("TestModule.UIEvent") });
        var protocolList = new ProtocolListTypeSpec(new[] { protocol });

        var constructor = CreateConstructorDecl(classDecl, moduleDecl, new[]
        {
            CreateArgumentDecl("analyzer", protocolList),
            CreateArgumentDecl("sessionNumber", new NamedTypeSpec("Swift.Int"))
        });

        var env = new MethodEnvironment(constructor, typeDatabase);
        var csOut = new StringWriter();
        var swiftOut = new StringWriter();
        var csWriter = new CSharpWriter(csOut);
        var swiftWriter = new SwiftWriter(swiftOut);

        var result = ConstrainedExistentialBridge.TryEmitConstructor(csWriter, swiftWriter, env, _logger);

        Assert.True(result);
        var swift = swiftOut.ToString();
        Assert.Contains("@_silgen_name(\"SBW_ScannerModel_init_", swift);
        Assert.Contains("Unmanaged<AnyObject>.fromOpaque(", swift);
        Assert.Contains("as! any TestModule.CameraFrameAnalyzer<TestModule.CameraFrame, TestModule.UIEvent>", swift);
        Assert.Contains("Unmanaged.passRetained(result).toOpaque()", swift);
    }

    [Fact]
    public void TryEmitConstructor_ConstrainedExistentialParam_EmitsCSharpConstructor()
    {
        var typeDatabase = CreateTypeDatabaseWithClassBoundProtocol();
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("ScannerModel", moduleDecl, typeDatabase);

        var protocol = new NamedTypeSpec("TestModule.CameraFrameAnalyzer",
            new TypeSpec[] { new NamedTypeSpec("TestModule.CameraFrame"), new NamedTypeSpec("TestModule.UIEvent") });
        var protocolList = new ProtocolListTypeSpec(new[] { protocol });

        var constructor = CreateConstructorDecl(classDecl, moduleDecl, new[]
        {
            CreateArgumentDecl("analyzer", protocolList),
            CreateArgumentDecl("sessionNumber", new NamedTypeSpec("Swift.Int"))
        });

        var env = new MethodEnvironment(constructor, typeDatabase);
        var csOut = new StringWriter();
        var swiftOut = new StringWriter();
        var csWriter = new CSharpWriter(csOut);
        var swiftWriter = new SwiftWriter(swiftOut);

        ConstrainedExistentialBridge.TryEmitConstructor(csWriter, swiftWriter, env, _logger);

        var cs = csOut.ToString();
        Assert.Contains("ISwiftObject analyzer", cs);
        Assert.Contains("nint sessionNumber", cs);
        Assert.Contains(".SwiftHandle", cs);
        Assert.Contains("NativeMemory.Alloc", cs);
        Assert.Contains("new SwiftSafeHandle<ScannerModel>", cs);
        Assert.Contains("Arc.Release(resultPtr)", cs);
    }

    [Fact]
    public void TryEmitConstructor_ConstrainedExistentialParam_EmitsPInvoke()
    {
        var typeDatabase = CreateTypeDatabaseWithClassBoundProtocol();
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("ScannerModel", moduleDecl, typeDatabase);

        var protocol = new NamedTypeSpec("TestModule.CameraFrameAnalyzer",
            new TypeSpec[] { new NamedTypeSpec("TestModule.CameraFrame"), new NamedTypeSpec("TestModule.UIEvent") });
        var protocolList = new ProtocolListTypeSpec(new[] { protocol });

        var constructor = CreateConstructorDecl(classDecl, moduleDecl, new[]
        {
            CreateArgumentDecl("analyzer", protocolList)
        });

        var env = new MethodEnvironment(constructor, typeDatabase);
        var csOut = new StringWriter();
        var swiftOut = new StringWriter();
        var csWriter = new CSharpWriter(csOut);
        var swiftWriter = new SwiftWriter(swiftOut);

        ConstrainedExistentialBridge.TryEmitConstructor(csWriter, swiftWriter, env, _logger);

        var cs = csOut.ToString();
        Assert.Contains("LibraryImport", cs);
        Assert.Contains("IntPtr analyzer", cs);
        Assert.Contains("IntPtr", cs); // return type
    }

    [Fact]
    public void TryEmitConstructor_EmitsImportStatementsForConstraintModules()
    {
        var typeDatabase = CreateTypeDatabaseWithCrossModuleProtocol();
        var moduleDecl = CreateModuleDecl("ScanModule");
        var classDecl = CreateClassDecl("ScannerModel", moduleDecl, typeDatabase);

        // Constrained existential with cross-module constraint types
        var protocol = new NamedTypeSpec("ScanModule.CameraFrameAnalyzer",
            new TypeSpec[] { new NamedTypeSpec("CoreLib.CameraFrame"), new NamedTypeSpec("ScanModule.UIEvent") });
        var protocolList = new ProtocolListTypeSpec(new[] { protocol });

        var constructor = CreateConstructorDecl(classDecl, moduleDecl, new[]
        {
            CreateArgumentDecl("analyzer", protocolList),
            CreateArgumentDecl("sessionNumber", new NamedTypeSpec("Swift.Int"))
        });

        var env = new MethodEnvironment(constructor, typeDatabase);
        var csOut = new StringWriter();
        var swiftOut = new StringWriter();
        var csWriter = new CSharpWriter(csOut);
        var swiftWriter = new SwiftWriter(swiftOut);

        var result = ConstrainedExistentialBridge.TryEmitConstructor(csWriter, swiftWriter, env, _logger);

        Assert.True(result);
        var swift = swiftOut.ToString();
        // Verify import statements are emitted for constraint type modules
        Assert.Contains("import CoreLib", swift);
        Assert.Contains("import ScanModule", swift);
    }

    [Fact]
    public void TryEmitConstructor_NamedTypeSpecForm_EmitsBridge()
    {
        // This tests the ABI JSON form: NamedTypeSpec with generic params (not ProtocolListTypeSpec)
        var typeDatabase = CreateTypeDatabaseWithClassBoundProtocol();
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Scanner", moduleDecl, typeDatabase);

        // ABI JSON form: NamedTypeSpec with generic params
        var namedExistential = new NamedTypeSpec("TestModule.CameraFrameAnalyzer",
            new TypeSpec[] { new NamedTypeSpec("TestModule.CameraFrame"), new NamedTypeSpec("TestModule.UIEvent") });

        var constructor = CreateConstructorDecl(classDecl, moduleDecl, new[]
        {
            CreateArgumentDecl("analyzer", namedExistential),
            CreateArgumentDecl("count", new NamedTypeSpec("Swift.Int"))
        });

        var env = new MethodEnvironment(constructor, typeDatabase);
        var csOut = new StringWriter();
        var swiftOut = new StringWriter();
        var csWriter = new CSharpWriter(csOut);
        var swiftWriter = new SwiftWriter(swiftOut);

        var result = ConstrainedExistentialBridge.TryEmitConstructor(csWriter, swiftWriter, env, _logger);

        Assert.True(result);
        var cs = csOut.ToString();
        Assert.Contains("ISwiftObject analyzer", cs);
        Assert.Contains("nint count", cs);
        Assert.Contains(".SwiftHandle", cs);

        var swift = swiftOut.ToString();
        Assert.Contains("any TestModule.CameraFrameAnalyzer<TestModule.CameraFrame, TestModule.UIEvent>", swift);
        Assert.Contains("Unmanaged<AnyObject>.fromOpaque(", swift);
    }

    [Fact]
    public void TryEmitConstructor_MixedParams_ExistentialAndStructAndPrimitive()
    {
        var typeDatabase = CreateTypeDatabaseWithClassBoundProtocolAndStruct();
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Scanner", moduleDecl, typeDatabase);

        var protocol = new NamedTypeSpec("TestModule.CameraFrameAnalyzer",
            new TypeSpec[] { new NamedTypeSpec("TestModule.CameraFrame") });
        var protocolList = new ProtocolListTypeSpec(new[] { protocol });

        var constructor = CreateConstructorDecl(classDecl, moduleDecl, new[]
        {
            CreateArgumentDecl("analyzer", protocolList),
            CreateArgumentDecl("settings", new NamedTypeSpec("TestModule.Settings")),
            CreateArgumentDecl("count", new NamedTypeSpec("Swift.Int"))
        });

        var env = new MethodEnvironment(constructor, typeDatabase);
        var csOut = new StringWriter();
        var swiftOut = new StringWriter();
        var csWriter = new CSharpWriter(csOut);
        var swiftWriter = new SwiftWriter(swiftOut);

        var result = ConstrainedExistentialBridge.TryEmitConstructor(csWriter, swiftWriter, env, _logger);

        Assert.True(result);
        var cs = csOut.ToString();
        Assert.Contains("ISwiftObject analyzer", cs);
        Assert.Contains("Settings settings", cs);
        Assert.Contains("nint count", cs);

        var swift = swiftOut.ToString();
        Assert.Contains("assumingMemoryBound(to: TestModule.Settings.self).pointee", swift);
    }

    [Fact]
    public void TryEmitConstructor_DerivedClass_UsesRootBaseForSafeHandle()
    {
        var typeDatabase = CreateTypeDatabaseWithClassBoundProtocol();
        var moduleDecl = CreateModuleDecl("TestModule");

        // Create base class
        var baseClassDecl = CreateClassDecl("BaseModel", moduleDecl, typeDatabase);

        // Create derived class with ResolvedSuperclass
        var derivedClassDecl = CreateClassDecl("DerivedModel", moduleDecl, typeDatabase);
        derivedClassDecl.ResolvedSuperclass = baseClassDecl;

        var protocol = new NamedTypeSpec("TestModule.CameraFrameAnalyzer",
            new TypeSpec[] { new NamedTypeSpec("TestModule.CameraFrame"), new NamedTypeSpec("TestModule.UIEvent") });
        var protocolList = new ProtocolListTypeSpec(new[] { protocol });

        var constructor = CreateConstructorDecl(derivedClassDecl, moduleDecl, new[]
        {
            CreateArgumentDecl("analyzer", protocolList)
        });

        var env = new MethodEnvironment(constructor, typeDatabase);
        var csOut = new StringWriter();
        var swiftOut = new StringWriter();
        var csWriter = new CSharpWriter(csOut);
        var swiftWriter = new SwiftWriter(swiftOut);

        var result = ConstrainedExistentialBridge.TryEmitConstructor(csWriter, swiftWriter, env, _logger);

        Assert.True(result);
        var cs = csOut.ToString();
        // Derived class should use root base type (BaseModel) for SwiftSafeHandle, not DerivedModel
        Assert.Contains("new SwiftSafeHandle<BaseModel>", cs);
        Assert.DoesNotContain("SwiftSafeHandle<DerivedModel>", cs);
    }

    [Fact]
    public void TryEmitConstructor_NonDerivedClass_UsesSelfForSafeHandle()
    {
        var typeDatabase = CreateTypeDatabaseWithClassBoundProtocol();
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("ScannerModel", moduleDecl, typeDatabase);

        var protocol = new NamedTypeSpec("TestModule.CameraFrameAnalyzer",
            new TypeSpec[] { new NamedTypeSpec("TestModule.CameraFrame"), new NamedTypeSpec("TestModule.UIEvent") });
        var protocolList = new ProtocolListTypeSpec(new[] { protocol });

        var constructor = CreateConstructorDecl(classDecl, moduleDecl, new[]
        {
            CreateArgumentDecl("analyzer", protocolList)
        });

        var env = new MethodEnvironment(constructor, typeDatabase);
        var csOut = new StringWriter();
        var swiftOut = new StringWriter();
        var csWriter = new CSharpWriter(csOut);
        var swiftWriter = new SwiftWriter(swiftOut);

        var result = ConstrainedExistentialBridge.TryEmitConstructor(csWriter, swiftWriter, env, _logger);

        Assert.True(result);
        var cs = csOut.ToString();
        // Non-derived class uses its own type for SwiftSafeHandle
        Assert.Contains("new SwiftSafeHandle<ScannerModel>", cs);
    }

    #endregion

    #region RenderConstrainedExistentialSwiftType Tests

    [Fact]
    public void RenderConstrainedExistentialSwiftType_SingleArg()
    {
        var protocol = new NamedTypeSpec("ScanModule.CameraFrameAnalyzer",
            new TypeSpec[] { new NamedTypeSpec("CoreLib.CameraFrame") });
        var protocolList = new ProtocolListTypeSpec(new[] { protocol });

        var result = ConstrainedExistentialBridge.RenderConstrainedExistentialSwiftType(protocolList);
        Assert.Equal("any ScanModule.CameraFrameAnalyzer<CoreLib.CameraFrame>", result);
    }

    [Fact]
    public void RenderConstrainedExistentialSwiftType_MultipleArgs()
    {
        var protocol = new NamedTypeSpec("ScanModule.CameraFrameAnalyzer",
            new TypeSpec[] { new NamedTypeSpec("CoreLib.CameraFrame"), new NamedTypeSpec("ScanModule.UIEvent") });
        var protocolList = new ProtocolListTypeSpec(new[] { protocol });

        var result = ConstrainedExistentialBridge.RenderConstrainedExistentialSwiftType(protocolList);
        Assert.Equal("any ScanModule.CameraFrameAnalyzer<CoreLib.CameraFrame, ScanModule.UIEvent>", result);
    }

    [Fact]
    public void RenderConstrainedExistentialSwiftType_NamedTypeSpec_MatchesProtocolList()
    {
        // ABI JSON form: NamedTypeSpec with generic params (not wrapped in ProtocolListTypeSpec)
        var namedSpec = new NamedTypeSpec("ScanModule.CameraFrameAnalyzer",
            new TypeSpec[] { new NamedTypeSpec("CoreLib.CameraFrame"), new NamedTypeSpec("ScanModule.UIEvent") });

        var result = ConstrainedExistentialBridge.RenderConstrainedExistentialSwiftType(namedSpec);
        Assert.Equal("any ScanModule.CameraFrameAnalyzer<CoreLib.CameraFrame, ScanModule.UIEvent>", result);
    }

    [Fact]
    public void IsConstrainedExistential_NamedTypeSpecWithProtocol_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabaseWithClassBoundProtocol();
        // NamedTypeSpec form — how ABI JSON actually parses constrained existentials
        var named = new NamedTypeSpec("TestModule.CameraFrameAnalyzer",
            new TypeSpec[] { new NamedTypeSpec("TestModule.CameraFrame") });

        var result = ExistentialHandler.IsConstrainedExistential(named, typeDatabase);
        Assert.True(result);
    }

    [Fact]
    public void IsConstrainedExistential_NamedTypeSpecNonProtocol_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabaseWithClassBoundProtocol();
        // CameraFrame is a Class, not Protocol — should return false
        var named = new NamedTypeSpec("TestModule.CameraFrame",
            new TypeSpec[] { new NamedTypeSpec("TestModule.CameraFrame") });

        var result = ExistentialHandler.IsConstrainedExistential(named, typeDatabase);
        Assert.False(result);
    }

    #endregion

    #region Helpers

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/libTestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromKeyword("nint"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(module);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithClassBoundProtocol()
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromKeyword("nint"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/fake/libTestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.CameraFrameAnalyzer"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ICameraFrameAnalyzer"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.CameraFrameAnalyzer"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.ClassBound | TypeRecordFlags.HasAssociatedTypes,
                Kind = TypeRecordKind.Protocol
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.CameraFrame"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "CameraFrame"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.CameraFrame"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.UIEvent"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "UIEvent"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.UIEvent"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(testModule);

        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithNonClassBoundProtocol()
    {
        var typeDatabase = new TypeDatabase();
        var testModule = new ModuleTypeDatabase("TestModule", "/fake/libTestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.ValueAnalyzer"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IValueAnalyzer"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ValueAnalyzer"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.HasAssociatedTypes, // No ClassBound flag
                Kind = TypeRecordKind.Protocol
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.CameraFrame"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "CameraFrame"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.CameraFrame"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithClassBoundProtocolAndStruct()
    {
        // Build a fresh database with protocol + struct in the same module
        var typeDatabase = new TypeDatabase();
        var testModule = new ModuleTypeDatabase("TestModule", "/fake/libTestModule.dylib");

        // Class-bound protocol
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.CameraFrameAnalyzer"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ICameraFrameAnalyzer"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.CameraFrameAnalyzer"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.HasAssociatedTypes | TypeRecordFlags.ClassBound,
                Kind = TypeRecordKind.Protocol
            });
        // Constraint types
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.CameraFrame"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "CameraFrame"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.CameraFrame"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.UIEvent"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "UIEvent"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.UIEvent"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            });
        // Non-frozen struct
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Settings"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Settings"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Settings"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.RequiresMemoryManagement, // Non-frozen struct
                Kind = TypeRecordKind.Struct
            });

        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithCrossModuleProtocol()
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromKeyword("nint"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        // ScanModule module: protocol + UIEvent
        var scanModule = new ModuleTypeDatabase("ScanModule", "/fake/libScanModule.dylib");
        scanModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("ScanModule.CameraFrameAnalyzer"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ScanModule", "ICameraFrameAnalyzer"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ScanModule.CameraFrameAnalyzer"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.HasAssociatedTypes,
                Kind = TypeRecordKind.Protocol
            });
        scanModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("ScanModule.UIEvent"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ScanModule", "UIEvent"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ScanModule.UIEvent"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(scanModule);

        // CoreLib module: CameraFrame
        var coreLibModule = new ModuleTypeDatabase("CoreLib", "/fake/libCoreLib.dylib");
        coreLibModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("CoreLib.CameraFrame"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("CoreLib", "CameraFrame"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("CoreLib.CameraFrame"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(coreLibModule);

        return typeDatabase;
    }

    private static ModuleDecl CreateModuleDecl(string name)
    {
        return new ModuleDecl
        {
            Name = name,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl, TypeDatabase typeDatabase)
    {
        var classDecl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}CN",
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

        var classModule = new ModuleTypeDatabase($"{moduleDecl.Name}_class_{name}", $"/fake/lib{moduleDecl.Name}_{name}.dylib");
        classModule.RegisterType(
            classDecl.SwiftTypeName,
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(moduleDecl.Name, name),
                SwiftTypeName = classDecl.SwiftTypeName,
                MetadataAccessor = classDecl.MangledName,
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(classModule);

        return classDecl;
    }

    private static StructDecl CreateStructDecl(string name, ModuleDecl moduleDecl, TypeDatabase typeDatabase)
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = false,
            MetadataAccessor = "",
        };
    }

    private static MethodDecl CreateConstructorDecl(BaseDecl parentDecl, ModuleDecl moduleDecl, ArgumentDecl[] args)
    {
        var csSignature = new List<ArgumentDecl>
        {
            // Return type placeholder (first element)
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec($"{moduleDecl.Name}.{parentDecl.Name}"),
                Name = "",
                PrivateName = "",
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            }
        };
        csSignature.AddRange(args);

        return new MethodDecl
        {
            Name = "init",
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{parentDecl.Name.Length}{parentDecl.Name}CACycfc",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
        };
    }

    private static ArgumentDecl CreateArgumentDecl(string name, TypeSpec typeSpec)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = name,
            PrivateName = name,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    #endregion
}
