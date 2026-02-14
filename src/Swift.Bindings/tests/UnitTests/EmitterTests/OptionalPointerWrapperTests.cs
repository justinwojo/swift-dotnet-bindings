// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for Optional pointer wrapper emission — fixes IntPtr truncation of
/// large Optional parameters (e.g., Optional&lt;String&gt; is 16 bytes, IntPtr is 8).
/// </summary>
public class OptionalPointerWrapperTests
{
    #region Detection Tests (IsLargeOptionalParam / HasLargeOptionalParams)

    [Fact]
    public void IsLargeOptionalParam_OptionalString_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabase();
        var handler = new BoundGenericsHandler(typeDatabase);
        var typeSpec = new NamedTypeSpec("Swift.Optional");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        Assert.True(handler.IsLargeOptionalParam(typeSpec));
    }

    [Fact]
    public void IsLargeOptionalParam_OptionalInt32_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var handler = new BoundGenericsHandler(typeDatabase);
        var typeSpec = new NamedTypeSpec("Swift.Optional");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int32"));

        Assert.False(handler.IsLargeOptionalParam(typeSpec));
    }

    [Fact]
    public void IsLargeOptionalParam_NonOptional_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var handler = new BoundGenericsHandler(typeDatabase);
        var typeSpec = new NamedTypeSpec("Swift.String");

        Assert.False(handler.IsLargeOptionalParam(typeSpec));
    }

    [Fact]
    public void HasLargeOptionalParams_MethodWithOptionalString_True()
    {
        var typeDatabase = CreateTypeDatabase();
        var handler = new BoundGenericsHandler(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var method = CreateMethodDecl("describe", CreateClassDecl("Foo", moduleDecl), moduleDecl,
            returnType: new NamedTypeSpec("Swift.String"), isAsync: false, throws: false,
            methodType: MethodType.Static);
        method.CSSignature.Add(CreateArgument("value", optStringType, moduleDecl));

        Assert.True(handler.HasLargeOptionalParams(method));
    }

    [Fact]
    public void HasLargeOptionalParams_MethodWithOptionalInt32Only_False()
    {
        var typeDatabase = CreateTypeDatabase();
        var handler = new BoundGenericsHandler(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");

        var optIntType = new NamedTypeSpec("Swift.Optional");
        optIntType.GenericParameters.Add(new NamedTypeSpec("Swift.Int32"));

        var method = CreateMethodDecl("describe", CreateClassDecl("Foo", moduleDecl), moduleDecl,
            returnType: new NamedTypeSpec("Swift.String"), isAsync: false, throws: false,
            methodType: MethodType.Static);
        method.CSSignature.Add(CreateArgument("value", optIntType, moduleDecl));

        Assert.False(handler.HasLargeOptionalParams(method));
    }

    #endregion

    #region Entry-Point Routing Tests

    [Fact]
    public void EntryPoint_HasOptionalPointerWrapper_UsesOptbufSuffix()
    {
        var method = CreateBareBoneMethodDecl("test", "$s4Test");
        method.HasOptionalPointerWrapper = true;

        var mangledName = NameProvider.GetMangledName(method);
        Assert.Equal("$s4Test_optbuf", mangledName);
    }

    [Fact]
    public void EntryPoint_HasOptionalPointerWrapper_UsesWrapperLibrary()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Foo", moduleDecl);

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var method = CreateMethodDecl("describe", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.String"), isAsync: false, throws: false,
            methodType: MethodType.Static);
        method.CSSignature.Add(CreateArgument("value", optStringType, moduleDecl));

        var (_, _) = EmitMethod(method, typeDatabase);

        Assert.True(method.HasOptionalPointerWrapper);
        Assert.True(method.UsesWrapperLibrary);
    }

    [Fact]
    public void EntryPoint_HasOptionalPointerWrapper_NoTjSuffix()
    {
        // _optbuf suffix should NOT include dispatch thunk (Tj) — wrappers are free functions
        var method = CreateBareBoneMethodDecl("test", "$s4Test");
        method.HasOptionalPointerWrapper = true;
        method.UsesWrapperLibrary = true;

        var mangledName = NameProvider.GetMangledName(method);
        Assert.DoesNotContain("Tj", mangledName);
    }

    #endregion

    #region NameProvider Suffix Precedence Tests

    [Fact]
    public void GetMangledName_AsyncBeatsOptbuf()
    {
        // Async methods get _async suffix even if HasOptionalPointerWrapper is set
        // (in practice, async guard prevents HasOptionalPointerWrapper from being set)
        var method = CreateBareBoneMethodDecl("test", "$s4Test");
        method.IsAsync = true;
        method.HasOptionalPointerWrapper = true;

        var mangledName = NameProvider.GetMangledName(method);
        Assert.Equal("$s4Test_async", mangledName);
    }

    [Fact]
    public void GetMangledName_OpaqueBeatsOptbuf()
    {
        // Opaque return gets _opaque suffix; optbuf is excluded by guard
        var method = CreateBareBoneMethodDecl("test", "$s4Test");
        var opaqueReturn = new ProtocolListTypeSpec();
        opaqueReturn.IsOpaque = true;
        method.CSSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = opaqueReturn,
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = null
            }
        };
        method.HasOptionalPointerWrapper = true;

        var mangledName = NameProvider.GetMangledName(method);
        Assert.Equal("$s4Test_opaque", mangledName);
    }

    [Fact]
    public void GetMangledName_OptbufAndCdeclMutuallyExclusive()
    {
        // Both can't be true because !UsesWrapperLibrary guard prevents it.
        // If someone forces both, optbuf takes precedence (checked before cdecl).
        var method = CreateBareBoneMethodDecl("test", "$s4Test");
        method.HasOptionalPointerWrapper = true;
        method.HasClosureCdeclWrapper = true;

        var mangledName = NameProvider.GetMangledName(method);
        Assert.Equal("$s4Test_optbuf", mangledName);
    }

    #endregion

    #region Guard Regression Tests — Must NOT Wrap

    [Fact]
    public void Emit_OpaqueReturn_DoesNotSetOptionalWrapper()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Foo", moduleDecl);

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        // Opaque return type
        var opaqueReturn = new ProtocolListTypeSpec();
        opaqueReturn.IsOpaque = true;

        var method = new MethodDecl
        {
            Name = "getData",
            MangledName = "$s10TestModule3FooC7getDatayF",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl { SwiftTypeSpec = opaqueReturn, Name = string.Empty, PrivateName = string.Empty, IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl },
                CreateArgument("value", optStringType, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        // Opaque return → skipped by ContainsPlaceholder (no protocol records)
        // but the flag should NOT be set regardless
        EmitMethod(method, typeDatabase);
        Assert.False(method.HasOptionalPointerWrapper);
    }

    [Fact]
    public void Emit_Accessor_DoesNotSetOptionalWrapper()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Foo", moduleDecl);

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var method = CreateMethodDecl("label_Get", parentDecl, moduleDecl,
            returnType: optStringType, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.IsAccessor = true;

        EmitMethod(method, typeDatabase);
        Assert.False(method.HasOptionalPointerWrapper);
    }

    [Fact]
    public void Emit_AsyncMethod_DoesNotSetOptionalWrapper()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Foo", moduleDecl);

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var method = CreateMethodDecl("fetch", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.String"), isAsync: true, throws: false,
            methodType: MethodType.Static);
        method.CSSignature.Add(CreateArgument("value", optStringType, moduleDecl));

        EmitMethod(method, typeDatabase);
        Assert.False(method.HasOptionalPointerWrapper);
    }

    [Fact]
    public void Emit_AlreadyUsesWrapperLibrary_DoesNotSetOptionalWrapper()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Foo", moduleDecl);

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var method = CreateMethodDecl("describe", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.String"), isAsync: false, throws: false,
            methodType: MethodType.Static);
        method.CSSignature.Add(CreateArgument("value", optStringType, moduleDecl));
        method.UsesWrapperLibrary = true; // Already owned by another emitter

        EmitMethod(method, typeDatabase);
        Assert.False(method.HasOptionalPointerWrapper);
    }

    [Fact]
    public void Emit_MutatingMethod_DoesNotSetOptionalWrapper()
    {
        var typeDatabase = CreateConstructorTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var method = CreateMethodDecl("updateLabel", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("label", optStringType, moduleDecl));
        method.IsMutating = true;

        EmitMethod(method, typeDatabase);
        Assert.False(method.HasOptionalPointerWrapper);
    }

    #endregion

    #region Constructor Guard Tests

    [Fact]
    public void EmitConstructor_FrozenNonFailable_SetsOptionalWrapper()
    {
        var typeDatabase = CreateConstructorTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl> { CreateArgument("label", optStringType, moduleDecl) });

        EmitConstructor(constructor, typeDatabase);
        Assert.True(constructor.HasOptionalPointerWrapper);
        Assert.True(constructor.UsesWrapperLibrary);
    }

    [Fact]
    public void EmitConstructor_ClassConstructor_DoesNotSetWrapper()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var constructor = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule6LoaderC4inityACyF",
            MethodType = MethodType.Static,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, new NamedTypeSpec("TestModule.Loader"), moduleDecl),
                CreateArgument("label", optStringType, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(constructor);

        EmitConstructor(constructor, typeDatabase);
        Assert.False(constructor.HasOptionalPointerWrapper);
    }

    [Fact]
    public void EmitConstructor_NonFrozenStruct_DoesNotSetWrapper()
    {
        var typeDatabase = CreateNonFrozenStructTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        // Non-frozen struct
        var parentDecl = CreateStructDecl("Config", moduleDecl, isFrozen: false);

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl> { CreateArgument("label", optStringType, moduleDecl) });

        EmitConstructor(constructor, typeDatabase);
        Assert.False(constructor.HasOptionalPointerWrapper);
    }

    [Fact]
    public void EmitConstructor_AsyncInit_DoesNotSetWrapper()
    {
        var typeDatabase = CreateConstructorTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl> { CreateArgument("label", optStringType, moduleDecl) });
        constructor.IsAsync = true;

        EmitConstructor(constructor, typeDatabase);
        Assert.False(constructor.HasOptionalPointerWrapper);
    }

    [Fact]
    public void EmitConstructor_FailableInit_DoesNotSetWrapper()
    {
        var typeDatabase = CreateConstructorTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl> { CreateArgument("label", optStringType, moduleDecl) });
        constructor.IsFailable = true;

        EmitConstructor(constructor, typeDatabase);
        Assert.False(constructor.HasOptionalPointerWrapper);
    }

    #endregion

    #region C# Marshalling Output Tests

    [Fact]
    public void Emit_LargeOptional_UsesPayloadDangerousGetHandle()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Foo", moduleDecl);

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var method = CreateMethodDecl("describe", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.String"), isAsync: false, throws: false,
            methodType: MethodType.Static);
        method.CSSignature.Add(CreateArgument("value", optStringType, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("Payload.DangerousGetHandle()", csOutput);
    }

    [Fact]
    public void Emit_LargeOptional_DoesNotEmitPayloadBuffer()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Foo", moduleDecl);

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var method = CreateMethodDecl("describe", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.String"), isAsync: false, throws: false,
            methodType: MethodType.Static);
        method.CSSignature.Add(CreateArgument("value", optStringType, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // The old PayloadBuffer<IntPtr> path should NOT appear for Optional<String>
        Assert.DoesNotContain("PayloadBuffer<IntPtr>", csOutput);
    }

    [Fact]
    public void Emit_SmallOptional_StillUsesPayloadBuffer()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Foo", moduleDecl);

        var optIntType = new NamedTypeSpec("Swift.Optional");
        optIntType.GenericParameters.Add(new NamedTypeSpec("Swift.Int32"));

        var method = CreateMethodDecl("describe", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.String"), isAsync: false, throws: false,
            methodType: MethodType.Static);
        method.CSSignature.Add(CreateArgument("value", optIntType, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Small optional (Int32 = 5 bytes) uses original PayloadBuffer<IntPtr> path
        Assert.Contains("PayloadBuffer<IntPtr>", csOutput);
    }

    [Fact]
    public void Emit_MultipleLargeOptionals_AllUsePointerPassing()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Foo", moduleDecl);

        var optStr1 = new NamedTypeSpec("Swift.Optional");
        optStr1.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        var optStr2 = new NamedTypeSpec("Swift.Optional");
        optStr2.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var method = CreateMethodDecl("combine", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.String"), isAsync: false, throws: false,
            methodType: MethodType.Static);
        method.CSSignature.Add(CreateArgument("first", optStr1, moduleDecl));
        method.CSSignature.Add(CreateArgument("second", optStr2, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Both should use DangerousGetHandle
        Assert.DoesNotContain("PayloadBuffer<IntPtr>", csOutput);
        Assert.Contains("firstSwift.Payload.DangerousGetHandle()", csOutput);
        Assert.Contains("secondSwift.Payload.DangerousGetHandle()", csOutput);
    }

    #endregion

    #region Swift Wrapper Output Tests

    [Fact]
    public void Emit_SwiftWrapper_UsesSilgenNameWithOptbufSuffix()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Foo", moduleDecl);

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var method = CreateMethodDecl("describe", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.String"), isAsync: false, throws: false,
            methodType: MethodType.Static);
        method.CSSignature.Add(CreateArgument("value", optStringType, moduleDecl));

        var (_, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.Contains("@_silgen_name(\"", swiftOutput);
        Assert.Contains("_optbuf", swiftOutput);
    }

    [Fact]
    public void Emit_SwiftWrapper_UsesUnsafeRawPointer()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Foo", moduleDecl);

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var method = CreateMethodDecl("describe", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.String"), isAsync: false, throws: false,
            methodType: MethodType.Static);
        method.CSSignature.Add(CreateArgument("value", optStringType, moduleDecl));

        var (_, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.Contains("UnsafeRawPointer", swiftOutput);
    }

    [Fact]
    public void Emit_SwiftWrapper_AssumingMemoryBoundToOptionalString()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Foo", moduleDecl);

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var method = CreateMethodDecl("describe", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.String"), isAsync: false, throws: false,
            methodType: MethodType.Static);
        method.CSSignature.Add(CreateArgument("value", optStringType, moduleDecl));

        var (_, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.Contains("assumingMemoryBound(to: (Swift.String)?.self).pointee", swiftOutput);
    }

    [Fact]
    public void Emit_InstanceMethod_SwiftWrapperHasSelfParam()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Foo", moduleDecl);

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var method = CreateMethodDecl("describe", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.String"), isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("value", optStringType, moduleDecl));

        var (_, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.Contains("_self: UnsafeMutableRawPointer", swiftOutput);
        Assert.Contains("unsafeBitCast(OpaquePointer(_self)", swiftOutput);
    }

    [Fact]
    public void Emit_FreeFunction_SwiftWrapperIsModuleScoped()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        // Free function — no parent type
        var method = new MethodDecl
        {
            Name = "describe",
            MangledName = "$s10TestModule8describeSSSgF",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, new NamedTypeSpec("Swift.String"), moduleDecl),
                CreateArgument("value", optStringType, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var (_, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.Contains("TestModule.describe(", swiftOutput);
        Assert.DoesNotContain("_self", swiftOutput);
    }

    [Fact]
    public void Emit_ThrowingMethod_SwiftWrapperIncludesThrows()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Foo", moduleDecl);

        var optStringType = new NamedTypeSpec("Swift.Optional");
        optStringType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var method = CreateMethodDecl("parse", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.String"), isAsync: false, throws: true,
            methodType: MethodType.Static);
        method.CSSignature.Add(CreateArgument("value", optStringType, moduleDecl));

        var (_, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.Contains("throws", swiftOutput);
        Assert.Contains("try ", swiftOutput);
    }

    #endregion

    #region Test Helpers

    private static TypeDatabase CreateTypeDatabase()
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
            SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int32"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
                MetadataAccessor = "$ss5Int32VMa",
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
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
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
            SwiftTypeName.FromModuleQualifiedName("TestModule.Foo"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Foo"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Foo"),
                MetadataAccessor = "$s10TestModule3FooCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
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

    private static TypeDatabase CreateNonFrozenStructTypeDatabase()
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
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
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
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Config"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Config"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Config"),
                MetadataAccessor = "$s10TestModule6ConfigVMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
    }

    private static TypeDatabase CreateConstructorTypeDatabase()
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
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
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
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Point"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                MetadataAccessor = "$s10TestModule5PointVMa",
                Flags = TypeRecordFlags.Frozen,
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

    private static StructDecl CreateStructDecl(string name, ModuleDecl moduleDecl, bool isFrozen)
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
            IsFrozen = isFrozen,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa"
        };
        moduleDecl.Types.Add(structDecl);
        return structDecl;
    }

    private static StructDecl CreateFrozenStructDecl(string name, ModuleDecl moduleDecl)
    {
        return CreateStructDecl(name, moduleDecl, isFrozen: true);
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
            MangledName = $"$s10TestModule3FooC{name}SiyF",
            MethodType = methodType,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, returnType, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = throws,
            IsAsync = isAsync,
            Visibility = Visibility.Public
        };
        if (parentDecl is ClassDecl classDecl)
            classDecl.Methods.Add(method);
        else if (parentDecl is StructDecl structDecl)
            structDecl.Methods.Add(method);
        return method;
    }

    private static MethodDecl CreateBareBoneMethodDecl(string name, string mangledName)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = mangledName,
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
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static MethodDecl CreateConstructorDecl(
        string name,
        StructDecl parentDecl,
        ModuleDecl moduleDecl,
        bool throws = false,
        List<ArgumentDecl>? parameters = null)
    {
        var signature = new List<ArgumentDecl>
        {
            CreateArgument(string.Empty, new NamedTypeSpec($"{moduleDecl.Name}.{parentDecl.Name}"), moduleDecl)
        };
        if (parameters != null)
        {
            signature.AddRange(parameters);
        }

        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule5PointV{name}yACyF",
            MethodType = MethodType.Static,
            IsConstructor = true,
            CSSignature = signature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = throws,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);
        return method;
    }

    private static ArgumentDecl CreateArgument(string name, TypeSpec typeSpec, ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = name,
            PrivateName = string.Empty,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
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

    private static (string csOutput, string swiftOutput) EmitConstructor(
        MethodDecl methodDecl,
        TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new ConstructorHandler(new NullLogger<ConstructorHandler>(), new HashSet<string>());
        var env = new MethodEnvironment(methodDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    #endregion
}
