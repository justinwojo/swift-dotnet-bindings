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
    public void DirectReturn_FoundationURL_UsesObjCBridge()
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

        // ObjCBridgeable: URL return uses GetNSObject (IntPtr → NSUrl)
        Assert.Contains("GetNSObject<Foundation.NSUrl>", csOutput);
        Assert.Contains("Foundation.NSUrl", csOutput);
    }

    [Fact]
    public void IndirectReturn_FoundationURL_UsesObjCBridge()
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

        // ObjCBridgeable: indirect URL return also uses GetNSObject
        Assert.Contains("GetNSObject<Foundation.NSUrl>", csOutput);
        Assert.Contains("Foundation.NSUrl", csOutput);
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

        // P/Invoke signature uses ValueTuple with underlying long type for the enum element
        Assert.Contains("ValueTuple<long, long>", csOutput);
        // Marshal code casts from underlying type back to C# enum
        Assert.Contains("(TestModule.Direction)result.Item1", csOutput);
        // The enum name should NOT appear in the P/Invoke ValueTuple type
        Assert.DoesNotContain("ValueTuple<TestModule.Direction", csOutput);
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
        Assert.DoesNotContain("partial TestModule.Variant PInvoke_", csOutput);
    }

    [Fact]
    public void DirectReturn_Class_EmitsDirectMarshalFromSwift()
    {
        // ARC bridge: class return uses direct MarshalFromSwift, no buffer allocation
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

        // ARC bridge: direct MarshalFromSwift, no buffer or try/catch
        Assert.Contains("MarshalFromSwift", csOutput);
        Assert.DoesNotContain("NativeMemory", csOutput);
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

        // Simple enum return → cast from underlying type: (TestModule.Direction)result
        Assert.Contains("(TestModule.Direction)result", csOutput);
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

        Assert.Contains("new DrawableProxy(result, ownsContainer: true)", csOutput);
    }

    [Fact]
    public void Return_OptionalExistential_EmitsOwningProxyConstruction()
    {
        // Owned optional-existential return: Swift transfers the inner existential at +1, so the
        // wrapping EC1 proxy must adopt and release it on Dispose/finalize (ownsContainer: true) or
        // the payload's +1 leaks. Mirrors the non-optional case above; guards the owned-return
        // routing across the Optional wrapper (the borrowed receiver-callback path stays non-owning).
        var typeDatabase = CreateTypeDatabaseWithProtocol();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Container", moduleDecl);

        // Method returning `(any Drawable)?` — optional existential return
        var optionalExistential = new NamedTypeSpec("Swift.Optional",
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Drawable") }));

        var method = CreateMethodDecl(
            name: "getDrawableMaybe",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: optionalExistential,
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("ownsContainer: true", csOutput);
        Assert.Contains("DrawableProxy", csOutput);
    }

    [Fact]
    public void Return_WellKnownExistential_EmitsOwnedAnyError()
    {
        // `any Swift.Error` must emit the well-known `Swift.Foundation.AnyError`,
        // not `new ErrorProxy(result)` (ErrorProxy doesn't exist). A direct existential
        // return transfers the boxed error at +1, so the wrapper adopts it via
        // `ownsContainer: true` and releases it on Dispose/finalize.
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

        Assert.Contains("new Swift.Foundation.AnyError(result, ownsContainer: true)", csOutput);
        Assert.DoesNotContain("ErrorProxy", csOutput);
    }

    [Fact]
    public void OptionalExistential_WellKnownProtocol_UnwrapsToAnyError()
    {
        // Bug fix: Optional<any Swift.Error> return must use TryGetWellKnownProtocolType
        // to emit `new Swift.Foundation.AnyError(result)` instead of `new ErrorProxy(result)`.
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
        Assert.Equal("Swift.Foundation.AnyError", wellKnownType);

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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Container"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Container"),
                MetadataAccessor = "$s10TestModule9ContainerCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Drawable"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Drawable"),
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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Container"),
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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Loader"),
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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Container"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Container"),
                MetadataAccessor = "$s10TestModule9ContainerCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Variant"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Variant"),
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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Container"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Container"),
                MetadataAccessor = "$s10TestModule9ContainerCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Direction"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Direction"),
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
                Flags = TypeRecordFlags.RequiresMemoryManagement | TypeRecordFlags.ObjCBridgeable,
                Kind = TypeRecordKind.Struct,
                NativeTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrl")
            });
        typeDatabase.AddModuleDatabase(foundationModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Loader"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
                MetadataAccessor = "$s10TestModule6LoaderCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Response"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Response"),
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

    [Fact]
    public void ClassConstructor_SkippedSuperclass_UsesSelfTypeName()
    {
        // P1 regression: when a class has a resolved superclass with unsupported generic
        // constraints (e.g., SwiftUI.View), the superclass is intentionally skipped.
        // IsEffectivelyDerived returns false, and GetRootBaseTypeNameWithGenerics must
        // return the child's own type name — NOT the skipped base's name.
        var moduleDecl = CreateModuleDecl("TestModule");

        // Create base class with unsupported generic constraint (SwiftUI module)
        var baseDecl = new ClassDecl
        {
            Name = "ViewWrapper",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ViewWrapper"),
            MangledName = "$s10TestModule11ViewWrapperCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl(
                    TypeName: "Content",
                    SugaredTypeName: "Content",
                    GenericConformances: new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            Path: new[] { "Content" },
                            ConformanceTarget: SwiftTypeName.FromModuleQualifiedName("SwiftUI.View"),
                            Kind: ConformanceKind.Protocol)
                    },
                    AssosiatedTypeConformances: new List<GenericParameterConformance>())
            }
        };
        moduleDecl.Types.Add(baseDecl);

        // Create child class that resolves to the skipped base
        var childDecl = new ClassDecl
        {
            Name = "MyWidget",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyWidget"),
            MangledName = "$s10TestModule8MyWidgetCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            ResolvedSuperclass = baseDecl
        };
        moduleDecl.Types.Add(childDecl);

        // Verify the predicates used by the fix
        Assert.True(childDecl.HasResolvedSuperclass, "HasResolvedSuperclass should be true");
        Assert.False(ClassHandler.IsEffectivelyDerived(childDecl),
            "IsEffectivelyDerived should be false when base has unsupported constraint");

        // GetRootBaseTypeNameWithGenerics must return child's own name, NOT base's
        var rootTypeName = ClassISwiftObjectMethodWriter.GetRootBaseTypeNameWithGenerics(childDecl);
        Assert.Equal("MyWidget", rootTypeName);
        Assert.DoesNotContain("ViewWrapper", rootTypeName);
    }

    [Fact]
    public void GetRootBaseTypeNameWithGenerics_UsesRenamedName_WhenTypeDatabaseProvided()
    {
        // Bug: When a nested type is renamed (e.g., Animator → AnimatorType to avoid property
        // collision), GetRootBaseTypeNameWithGenerics was not passing typeDatabase through to
        // GetTypeNameWithGenerics, so SwiftClassHandle<T> in the constructor used the old name.
        var moduleDecl = CreateModuleDecl("Kingfisher");

        // Create a class that would be a renamed nested type
        var classDecl = new ClassDecl
        {
            Name = "Animator", // Swift name
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Kingfisher.ImageTransition.Animator"),
            MangledName = "$s10Kingfisher15ImageTransitionC8AnimatorCN",
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

        // Register with renamed C# name in TypeDatabase
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("Kingfisher", "/tmp/Kingfisher.dylib");
        module.RegisterType(classDecl.SwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Kingfisher", "ImageTransition.AnimatorType"),
            SwiftTypeName = classDecl.SwiftTypeName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Class
        });
        typeDatabase.AddModuleDatabase(module);

        // Without typeDatabase: returns Swift name "Animator"
        var withoutDb = ClassISwiftObjectMethodWriter.GetRootBaseTypeNameWithGenerics(classDecl);
        Assert.Equal("Animator", withoutDb);

        // With typeDatabase: returns renamed C# name "AnimatorType"
        var withDb = ClassISwiftObjectMethodWriter.GetRootBaseTypeNameWithGenerics(classDecl, typeDatabase);
        Assert.Equal("AnimatorType", withDb);
    }

    [Fact]
    public void AsyncTupleReturn_OptionalObjCBridged_UsesNullableReferenceType()
    {
        // N4 regression: async method returning tuple with Optional<ObjC-bridged> element
        // must generate nullable reference type cast, not SwiftOptional<T> wrapper.
        // SwiftOptional<T> calls GetTypeMetadataOrThrow<T>() which crashes for ObjC types
        // (they don't implement ISwiftObject).
        var typeDatabase = CreateTypeDatabaseWithObjCBridgedType();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        // async method returning (Data, NSURLResponse?) — mirrors Nuke.DataAsync
        var returnType = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Foundation.Data"),
            new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Foundation.NSURLResponse"))
        });

        var method = CreateMethodDecl(
            name: "loadData",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: returnType,
            isAsync: true,
            throws: false,
            methodType: MethodType.Instance);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Must NOT contain SwiftOptional — ObjC types crash SwiftOptional's static constructor
        Assert.DoesNotContain("SwiftOptional", csOutput);
        // Must use nullable reference type pattern for the ObjC type
        Assert.Contains("(Foundation.NSURLResponse?)null", csOutput);
        // Must use GetNSObject for non-null case
        Assert.Contains("GetNSObject<Foundation.NSURLResponse>", csOutput);
    }

    private static TypeDatabase CreateTypeDatabaseWithObjCBridgedType()
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

        var foundationModule = new ModuleTypeDatabase("Foundation", "/usr/lib/swift/libswiftFoundation.dylib");
        foundationModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Foundation.Data"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSData"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.Data"),
                MetadataAccessor = "$s10Foundation4DataVMa",
                Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        foundationModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Foundation.NSURLResponse"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSURLResponse"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.NSURLResponse"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.ObjCBridged,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(foundationModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Loader"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
                MetadataAccessor = "$s10TestModule6LoaderCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
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
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return (csOutput.ToString(), swiftOutput.ToString());
    }
}
