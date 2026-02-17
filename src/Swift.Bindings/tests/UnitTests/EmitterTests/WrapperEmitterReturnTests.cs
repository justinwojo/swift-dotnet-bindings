// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

public class WrapperEmitterReturnTests
{
    [Fact]
    public void DirectReturn_FoundationURL_UsesMarshalFromSwift()
    {
        var typeDatabase = CreateTypeDatabaseWithURL();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var method = CreateMethodDecl(
            name: "getURL",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("Foundation.URL"),
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // WU2: URL return must use MarshalFromSwift (URL constructor is private)
        Assert.Contains("MarshalFromSwift", csOutput);
        Assert.DoesNotContain("new Swift.URL(result)", csOutput);
        Assert.Contains("ToNSUrl()", csOutput);
    }

    [Fact]
    public void IndirectReturn_FoundationURL_UsesMarshalFromSwift()
    {
        var typeDatabase = CreateTypeDatabaseWithURL();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateNonFrozenStructDecl("Response", moduleDecl);

        // Non-frozen struct method returning URL gets indirect result
        var method = CreateMethodDecl(
            name: "getURL",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("Foundation.URL"),
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // WU2: Indirect URL return must also use MarshalFromSwift
        Assert.Contains("MarshalFromSwift", csOutput);
        Assert.DoesNotContain("new Swift.URL(new IntPtr", csOutput);
        Assert.Contains("ToNSUrl()", csOutput);
    }

    [Fact]
    public void TupleReturn_SimpleEnumElement_UsesCastToEnumType()
    {
        // Simple enum in tuple return → P/Invoke uses underlying int type, marshal code casts to C# enum
        var typeDatabase = CreateTypeDatabaseWithSimpleEnum();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Container", moduleDecl);

        // Method returning (Direction, Int64) tuple
        var returnType = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("TestModule.Direction"),
            new NamedTypeSpec("Swift.Int")
        });
        var method = CreateMethodDecl(
            name: "getDirectionAndValue",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: returnType,
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // P/Invoke signature uses ValueTuple with underlying int type for the enum element
        Assert.Contains("ValueTuple<int, long>", csOutput);
        // Marshal code casts from underlying type back to C# enum
        Assert.Contains("(Swift.TestModule.Direction)result.Item1", csOutput);
        // The enum name should NOT appear in the P/Invoke ValueTuple type
        Assert.DoesNotContain("ValueTuple<Swift.TestModule.Direction", csOutput);
    }

    [Fact]
    public void DirectReturn_FrozenComplexEnum_UsesMarshalFromSwift()
    {
        // Frozen complex enums (non-simple, e.g. with associated values) are C# classes with SafeHandle
        // payloads. P/Invoke returns IntPtr, wrapper must marshal via MarshalFromSwift (SYSLIB1051 fix).
        // Frozen enums bypass the indirect result path, so the wrapper must handle them explicitly.
        var typeDatabase = CreateTypeDatabaseWithComplexEnum();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Container", moduleDecl);

        var method = CreateMethodDecl(
            name: "getVariant",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("TestModule.Variant"),
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // P/Invoke signature must use IntPtr, not the enum class name
        Assert.Contains("IntPtr", csOutput);
        Assert.Contains("MarshalFromSwift", csOutput);
        // The enum class name should not appear as a P/Invoke return type
        Assert.DoesNotContain("partial Swift.TestModule.Variant PInvoke_", csOutput);
    }

    [Fact]
    public void DirectReturn_Class_EmitsTryCatchAroundAlloc()
    {
        // Bug #11 regression: class return must use try/catch to free NativeMemory on failure
        var typeDatabase = CreateTypeDatabaseWithURL();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Container", moduleDecl);

        var method = CreateMethodDecl(
            name: "getLoader",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("TestModule.Loader"),
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Class return must have try-catch wrapping the allocation with free-on-exception
        Assert.Contains("try", csOutput);
        Assert.Contains("catch", csOutput);
        Assert.Contains("NativeMemory.Free", csOutput);
    }

    [Fact]
    public void DirectReturn_SwiftString_EmitsConversion()
    {
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var method = CreateMethodDecl(
            name: "getName",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("Swift.String"),
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // SwiftString return → string conversion in public API
        Assert.Contains("string", csOutput);
        Assert.Contains("SwiftString", csOutput);
    }

    [Fact]
    public void DirectReturn_SimpleEnum_EmitsCast()
    {
        var typeDatabase = CreateTypeDatabaseWithSimpleEnum();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Container", moduleDecl);

        var method = CreateMethodDecl(
            name: "getDirection",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("TestModule.Direction"),
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Simple enum return → cast from underlying type: (Swift.TestModule.Direction)result
        Assert.Contains("(Swift.TestModule.Direction)result", csOutput);
    }

    [Fact]
    public void DirectReturn_Void_EmitsNoReturnConversion()
    {
        var typeDatabase = CreateTypeDatabaseWithURL();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var method = CreateMethodDecl(
            name: "doWork",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: TupleTypeSpec.Empty,
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Void return → no return value conversion
        Assert.DoesNotContain("MarshalFromSwift", csOutput);
        Assert.DoesNotContain("return new", csOutput);
    }

    [Fact]
    public void Return_ClosureType_EmitsEscapingClosureWrapper()
    {
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        // Method returning a closure: (Int) -> Int
        var closureReturnType = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int"));

        var method = CreateMethodDecl(
            name: "getTransform",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: closureReturnType,
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("SwiftEscapingClosure", csOutput);
        Assert.Contains("FromSwift", csOutput);
    }

    [Fact]
    public void Return_Existential_EmitsProxyConstruction()
    {
        var typeDatabase = CreateTypeDatabaseWithProtocol();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Container", moduleDecl);

        // Method returning `any Drawable` — existential return
        var protocolList = new ProtocolListTypeSpec(
            new[] { new NamedTypeSpec("TestModule.Drawable") });

        var method = CreateMethodDecl(
            name: "getDrawable",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: protocolList,
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("new DrawableProxy(result)", csOutput);
    }

    [Fact]
    public void Return_WellKnownExistential_EmitsAnyError()
    {
        // Bug fix: `any Swift.Error` must emit `new Swift.AnyError(result)`,
        // not `new ErrorProxy(result)` (ErrorProxy doesn't exist)
        var typeDatabase = CreateTypeDatabaseWithProtocol();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Container", moduleDecl);

        var protocolList = new ProtocolListTypeSpec(
            new[] { new NamedTypeSpec("Swift.Error") });

        var method = CreateMethodDecl(
            name: "getError",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: protocolList,
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("new Swift.AnyError(result)", csOutput);
        Assert.DoesNotContain("ErrorProxy", csOutput);
    }

    [Fact]
    public void OptionalExistential_WellKnownProtocol_UnwrapsToAnyError()
    {
        // Bug fix: Optional<any Swift.Error> return must use TryGetWellKnownProtocolType
        // to emit `new Swift.AnyError(result)` instead of `new ErrorProxy(result)`.
        // This tests the ExistentialHandler logic used by WrapperEmitter.Return lines 170 and 451.
        var typeDatabase = CreateTypeDatabaseWithErrorProtocol();
        var existentialHandler = new ExistentialHandler(typeDatabase);

        var optionalExistential = new NamedTypeSpec("Swift.Optional",
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Error") }));

        // Verify Optional existential detection works
        Assert.True(existentialHandler.IsOptionalExistential(optionalExistential));

        // Verify unwrap + well-known protocol detection
        var innerProtocolList = existentialHandler.UnwrapOptionalExistential(optionalExistential);
        Assert.NotNull(innerProtocolList);
        Assert.True(existentialHandler.TryGetWellKnownProtocolType(innerProtocolList!, out var wellKnownType));
        Assert.Equal("Swift.AnyError", wellKnownType);

        // Verify GetProxyClassName would give the wrong answer (ErrorProxy)
        var proxyName = existentialHandler.GetProxyClassName(innerProtocolList!);
        Assert.Equal("ErrorProxy", proxyName);
    }

    [Fact]
    public void OptionalExistential_NonWellKnownProtocol_UsesProxyClassName()
    {
        // Complementary test: non-well-known protocol (e.g., TestModule.Drawable)
        // should NOT match TryGetWellKnownProtocolType and should use GetProxyClassName.
        var typeDatabase = CreateTypeDatabaseWithProtocol();
        var existentialHandler = new ExistentialHandler(typeDatabase);

        var optionalExistential = new NamedTypeSpec("Swift.Optional",
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Drawable") }));

        Assert.True(existentialHandler.IsOptionalExistential(optionalExistential));
        var innerProtocolList = existentialHandler.UnwrapOptionalExistential(optionalExistential);
        Assert.NotNull(innerProtocolList);
        Assert.False(existentialHandler.TryGetWellKnownProtocolType(innerProtocolList!, out _));
        Assert.Equal("DrawableProxy", existentialHandler.GetProxyClassName(innerProtocolList!));
    }

    private static TypeDatabase CreateTypeDatabaseWithProtocol()
    {
        var typeDatabase = new TypeDatabase();

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
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                MetadataAccessor = "$sSbMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Container"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Container"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Container"),
                MetadataAccessor = "$s10TestModule9ContainerCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Drawable"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Drawable"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Drawable"),
                MetadataAccessor = string.Empty,
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
    }

    /// <summary>
    /// Like CreateTypeDatabaseWithProtocol but also registers Swift.Error as a protocol,
    /// needed for Optional existential tests where CanEmitMethod checks AllProtocolsHaveTypeRecords.
    /// </summary>
    private static TypeDatabase CreateTypeDatabaseWithErrorProtocol()
    {
        var typeDatabase = new TypeDatabase();

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
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                MetadataAccessor = "$sSbMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "Error"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
                MetadataAccessor = string.Empty,
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Container"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Container"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Container"),
                MetadataAccessor = "$s10TestModule9ContainerCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithString()
    {
        var typeDatabase = new TypeDatabase();

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
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                MetadataAccessor = "$sSbMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Loader"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
                MetadataAccessor = "$s10TestModule6LoaderCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithComplexEnum()
    {
        var typeDatabase = new TypeDatabase();

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
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                MetadataAccessor = "$sSbMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Container"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Container"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Container"),
                MetadataAccessor = "$s10TestModule9ContainerCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Variant"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Variant"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Variant"),
                MetadataAccessor = "$s10TestModule7VariantOMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(testModule);

        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithSimpleEnum()
    {
        var typeDatabase = new TypeDatabase();

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
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                MetadataAccessor = "$sSbMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Container"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Container"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Container"),
                MetadataAccessor = "$s10TestModule9ContainerCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Direction"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Direction"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Direction"),
                MetadataAccessor = "$s10TestModule9DirectionOMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = "Int"
            });
        typeDatabase.AddModuleDatabase(testModule);

        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithURL()
    {
        var typeDatabase = new TypeDatabase();

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
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                MetadataAccessor = "$sSbMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var foundationModule = new ModuleTypeDatabase("Foundation", "/usr/lib/swift/libswiftFoundation.dylib");
        foundationModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Foundation.URL"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "URL"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.URL"),
                MetadataAccessor = "$s10Foundation3URLVMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
                NativeTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrl")
            });
        typeDatabase.AddModuleDatabase(foundationModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Loader"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
                MetadataAccessor = "$s10TestModule6LoaderCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Response"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Response"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Response"),
                MetadataAccessor = "$s10TestModule8ResponseVMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(module);

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

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
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
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(classDecl);
        return classDecl;
    }

    private static StructDecl CreateNonFrozenStructDecl(string name, ModuleDecl moduleDecl)
    {
        var structDecl = new StructDecl
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
            MetadataAccessor = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}VMa",
        };
        moduleDecl.Types.Add(structDecl);
        return structDecl;
    }

    private static MethodDecl CreateMethodDecl(
        string name,
        TypeDecl parentDecl,
        ModuleDecl moduleDecl,
        TypeSpec returnType,
        bool isAsync,
        bool throws,
        MethodType methodType)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule6LoaderC{name}SiyF",
            MethodType = methodType,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = returnType,
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = throws,
            IsAsync = isAsync,
            Visibility = Visibility.Public
        };
        if (parentDecl is ClassDecl cd)
            cd.Methods.Add(method);
        else if (parentDecl is StructDecl sd)
            sd.Methods.Add(method);
        return method;
    }

    private static (string csOutput, string swiftOutput) EmitMethod(
        MethodDecl methodDecl,
        TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = new MethodEnvironment(methodDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor);

        return (csOutput.ToString(), swiftOutput.ToString());
    }
}
