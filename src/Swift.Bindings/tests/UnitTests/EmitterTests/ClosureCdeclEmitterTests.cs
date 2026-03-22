// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the Closure Cdecl Expansion (Strategy B) — Mono JIT crash mitigation.
/// Verifies that non-async escaping closures use CallConvCdecl + IntPtr context
/// instead of CallConvSwift + SwiftSelf, and that async paths are preserved.
/// </summary>
public class ClosureCdeclEmitterTests
{
    #region Detection Helper Tests (NeedsClosureCdeclWrapper)

    [Fact]
    public void NeedsClosureCdeclWrapper_NonAsyncMethodWithEscapingClosure_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("handle", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        Assert.True(ClosureEmitter.NeedsClosureCdeclWrapper(method, closureHandler));
    }

    [Fact]
    public void NeedsClosureCdeclWrapper_AsyncMethodWithEscapingClosure_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("handle", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: true, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        // Detection helper returns false for async methods
        Assert.False(ClosureEmitter.NeedsClosureCdeclWrapper(method, closureHandler));

        // Verify emission preserves legacy path: HasClosureCdeclWrapper must stay false
        // (this controls SwiftClosureData vs CdeclClosureFuncPtr in P/Invoke)
        var (csOutput, _) = EmitMethod(method, typeDatabase);
        Assert.False(method.HasClosureCdeclWrapper);
        // Cdecl closure params must NOT appear in the output
        Assert.DoesNotContain("CdeclClosureFuncPtr", csOutput);
        Assert.DoesNotContain("CdeclClosureContext", csOutput);
    }

    [Fact]
    public void NeedsClosureCdeclWrapper_ConventionCClosureOnly_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        // @convention(c) closures don't need thunks — no CallConvSwift callback
        var convAttr = new TypeSpecAttribute("convention");
        convAttr.Parameters.Add("c");
        closureType.Attributes.Add(convAttr);

        var method = CreateMethodDecl("handle", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        Assert.False(ClosureEmitter.NeedsClosureCdeclWrapper(method, closureHandler));
    }

    [Fact]
    public void NeedsClosureCdeclWrapper_NoClosures_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var method = CreateMethodDecl("handle", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("x", new NamedTypeSpec("Swift.Int"), moduleDecl));

        Assert.False(ClosureEmitter.NeedsClosureCdeclWrapper(method, closureHandler));
    }

    [Fact]
    public void NeedsClosureCdeclWrapper_MultipleClosuresOneEscaping_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var escapingClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        escapingClosure.Attributes.Add(new TypeSpecAttribute("escaping"));

        var conventionCClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        var convAttr = new TypeSpecAttribute("convention");
        convAttr.Parameters.Add("c");
        conventionCClosure.Attributes.Add(convAttr);

        var method = CreateMethodDecl("handle", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("onProgress", conventionCClosure, moduleDecl));
        method.CSSignature.Add(CreateArgument("onComplete", escapingClosure, moduleDecl));

        Assert.True(ClosureEmitter.NeedsClosureCdeclWrapper(method, closureHandler));
    }

    [Fact]
    public void NeedsClosureCdeclWrapper_PropertyAccessorWithClosureValue_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            TupleTypeSpec.Empty,
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Property accessor (setter) — closure is the value, not a callback parameter
        var method = CreateMethodDecl("didComplete_Set", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.IsAccessor = true;
        method.CSSignature.Add(CreateArgument("value", closureType, moduleDecl));

        Assert.False(ClosureEmitter.NeedsClosureCdeclWrapper(method, closureHandler));
    }

    [Fact]
    public void NeedsClosureCdeclWrapper_OpaqueReturnWithEscapingClosure_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Opaque return (some Protocol)
        var opaqueReturn = new ProtocolListTypeSpec(
            new[] { new NamedTypeSpec("TestModule.Loader") });
        opaqueReturn.IsOpaque = true;

        var method = CreateMethodDecl("handle", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        // Replace return type (first element of CSSignature)
        method.CSSignature[0] = CreateArgument(string.Empty, opaqueReturn, moduleDecl);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        Assert.False(ClosureEmitter.NeedsClosureCdeclWrapper(method, closureHandler));
    }

    #endregion

    #region C# Callback Cdecl Emission Tests

    [Fact]
    public void Emit_NonAsyncMethodWithEscapingClosure_UsesCallConvCdecl()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("handle", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Cdecl wrapper: closure callback uses CallConvCdecl
        Assert.Contains("typeof(global::System.Runtime.CompilerServices.CallConvCdecl)", csOutput);
        // Callback function pointer uses Cdecl convention
        Assert.Contains("delegate* unmanaged[Cdecl]<", csOutput);
    }

    [Fact]
    public void Emit_NonAsyncMethodWithEscapingClosure_UsesIntPtrContextInCallback()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("handle", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Cdecl wrapper: callback uses IntPtr contextPtr, not SwiftSelf context
        Assert.Contains("IntPtr contextPtr", csOutput);
        Assert.DoesNotContain("SwiftSelf context", csOutput);
    }

    [Fact]
    public void Emit_ThrowingClosureNonAsync_UsesCallConvCdecl()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Double") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));
        closureType.Attributes.Add(new TypeSpecAttribute("throws"));

        var method = CreateMethodDecl("tryHandle", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("typeof(global::System.Runtime.CompilerServices.CallConvCdecl)", csOutput);
        Assert.Contains("IntPtr contextPtr", csOutput);
    }

    [Fact]
    public void Emit_IndirectReturnClosureNonAsync_NonPrimitiveReturn_FallsBackToSwift()
    {
        var typeDatabase = CreateTypeDatabase();
        // Register a non-frozen struct for genuinely non-Cdecl-compatible closure return
        var extraModule = new ModuleTypeDatabase("TestExtra", "/tmp/TestExtra.dylib");
        extraModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestExtra.RuntimeData"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestExtra", "RuntimeData"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestExtra.RuntimeData"),
                MetadataAccessor = "$s9TestExtra11RuntimeDataVMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement, // NOT frozen
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(extraModule);
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        // Closure returning non-frozen struct → NOT Cdecl-compatible → legacy Swift path
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Double") }),
            new NamedTypeSpec("TestExtra.RuntimeData"));
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("tint", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Non-primitive closure return → falls back to legacy CallConvSwift path
        Assert.Contains("delegate* unmanaged[Swift]<", csOutput);
        Assert.DoesNotContain("CdeclClosureFuncPtr", csOutput);
    }

    [Fact]
    public void Emit_FuncPtrType_UsesCdeclWithIntPtr()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("handle", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Function pointer type uses unmanaged[Cdecl] with IntPtr, not Swift with SwiftSelf
        Assert.Contains("delegate* unmanaged[Cdecl]<", csOutput);
        Assert.DoesNotContain("delegate* unmanaged[Swift]<", csOutput);
    }

    #endregion

    #region AddCdeclContextToFunctionPointerType Tests

    [Fact]
    public void AddCdeclContextToFunctionPointerType_WithParams_InsertsIntPtrBeforeReturn()
    {
        var result = ClosureEmitter.AddCdeclContextToFunctionPointerType(
            "delegate* unmanaged[Cdecl]<long, void>");

        Assert.Equal("delegate* unmanaged[Cdecl]<long, IntPtr, void>", result);
    }

    [Fact]
    public void AddCdeclContextToFunctionPointerType_NoParams_InsertsIntPtrBeforeReturn()
    {
        var result = ClosureEmitter.AddCdeclContextToFunctionPointerType(
            "delegate* unmanaged[Cdecl]<void>");

        Assert.Equal("delegate* unmanaged[Cdecl]<IntPtr, void>", result);
    }

    [Fact]
    public void AddCdeclContextToFunctionPointerType_MultipleParams_InsertsIntPtrAtEnd()
    {
        var result = ClosureEmitter.AddCdeclContextToFunctionPointerType(
            "delegate* unmanaged[Cdecl]<long, double, byte>");

        Assert.Equal("delegate* unmanaged[Cdecl]<long, double, IntPtr, byte>", result);
    }

    [Fact]
    public void AddCdeclContext_NestedGeneric_InsertsBeforeReturn()
    {
        // Bug #5 regression: nested generic brackets must not confuse comma detection
        var result = ClosureEmitter.AddCdeclContextToFunctionPointerType(
            "delegate* unmanaged[Cdecl]<SwiftOptional<int>, void>");

        Assert.Equal("delegate* unmanaged[Cdecl]<SwiftOptional<int>, IntPtr, void>", result);
    }

    [Fact]
    public void AddCdeclContext_MultipleNestedGenerics_InsertsCorrectly()
    {
        // Bug #5 regression: multiple nested generics with top-level comma
        var result = ClosureEmitter.AddCdeclContextToFunctionPointerType(
            "delegate* unmanaged[Cdecl]<SwiftOptional<int>, SwiftArray<long>, void>");

        Assert.Equal("delegate* unmanaged[Cdecl]<SwiftOptional<int>, SwiftArray<long>, IntPtr, void>", result);
    }

    #endregion

    #region P/Invoke and Routing Tests

    [Fact]
    public void Emit_CdeclClosureMethod_PInvokeHasIntPtrParams()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("handle", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // P/Invoke should have separate IntPtr params, not SwiftClosureData
        Assert.Contains("IntPtr callbackFuncPtr", csOutput);
        Assert.Contains("IntPtr callbackContext", csOutput);
        Assert.DoesNotContain("SwiftClosureData", csOutput);
    }

    [Fact]
    public void Emit_CdeclClosureStandaloneMethod_SelfIsIntPtr()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("handle", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Standalone closure wrapper: self as IntPtr (free function), no SwiftSelf
        Assert.Contains("IntPtr _selfClass", csOutput);
        Assert.DoesNotContain("new SwiftSelf", csOutput);
    }

    [Fact]
    public void Emit_CdeclClosureMethod_SetsUsesWrapperLibraryFlag()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("handle", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        // Emit to trigger flag-setting
        EmitMethod(method, typeDatabase);

        Assert.True(method.HasClosureCdeclWrapper);
        Assert.True(method.UsesFreeFunctionWrapper);
        Assert.True(method.UsesWrapperLibrary);
    }

    #endregion

    #region Swift Wrapper Tests

    [Fact]
    public void Emit_CdeclClosureMethod_EmitsSwiftWrapperWithSilgenName()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("handle", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (_, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.Contains("@_silgen_name", swiftOutput);
        Assert.Contains("@convention(c)", swiftOutput);
        Assert.Contains("UnsafeMutableRawPointer", swiftOutput);
    }

    [Fact]
    public void Emit_CdeclClosureWithBoolParam_UsesBoolToUInt8Conversion()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Bool") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("toggle", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (_, swiftOutput) = EmitMethod(method, typeDatabase);

        // Swift wrapper should use UInt8 for Bool in @convention(c) function type
        Assert.Contains("UInt8", swiftOutput);
        Assert.Contains("@convention(c)", swiftOutput);
    }

    [Fact]
    public void Emit_CdeclClosureOptional_SwiftWrapperChecksNullFuncPtr()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Wrap in Optional
        var optionalClosure = new NamedTypeSpec("Swift.Optional", closureType);

        var method = CreateMethodDecl("maybeHandle", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", optionalClosure, moduleDecl));

        var (_, swiftOutput) = EmitMethod(method, typeDatabase);

        // Swift wrapper should check for nil function pointer
        Assert.Contains("if let", swiftOutput);
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void Emit_ConstructorWithClosure_SetsCdeclAndFreeFunctionFlags()
    {
        var typeDatabase = CreateConstructorTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);

        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            returnType: TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl> { CreateArgument("callback", closureType, moduleDecl) });

        EmitConstructor(constructor, typeDatabase);

        Assert.True(constructor.HasClosureCdeclWrapper);
        Assert.True(constructor.UsesFreeFunctionWrapper);
    }

    [Fact]
    public void Emit_ConstructorWithClosure_EmitsIntPtrParamsInPInvoke()
    {
        var typeDatabase = CreateConstructorTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);

        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            returnType: TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl> { CreateArgument("callback", closureType, moduleDecl) });

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        Assert.DoesNotContain("SwiftClosureData", csOutput);
        Assert.Contains("IntPtr callbackFuncPtr", csOutput);
        Assert.Contains("IntPtr callbackContext", csOutput);
        Assert.Contains("GCHandle callbackHandle", csOutput);
    }

    [Fact]
    public void Emit_ConstructorWithClosure_EmitsSwiftFreeFunction()
    {
        var typeDatabase = CreateConstructorTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);

        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            returnType: TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl> { CreateArgument("callback", closureType, moduleDecl) });

        var (_, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        // Constructor wrapper is a free function (not extension)
        Assert.Contains("@_silgen_name", swiftOutput);
        Assert.Contains("Point(", swiftOutput);
    }

    #endregion

    #region Async Regression Guards

    [Fact]
    public void Emit_AsyncMethodWithEscapingClosure_PreservesSwiftClosureDataPath()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("handle", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: true, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        // Async methods don't set HasClosureCdeclWrapper
        Assert.False(method.HasClosureCdeclWrapper);
        Assert.False(method.UsesFreeFunctionWrapper);
    }

    [Fact]
    public void Emit_ConventionCClosure_UnchangedByStrategy()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        var convAttr2 = new TypeSpecAttribute("convention");
        convAttr2.Parameters.Add("c");
        closureType.Attributes.Add(convAttr2);

        var method = CreateMethodDecl("handle", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        // @convention(c) closures are already safe — no Cdecl wrapper needed
        EmitMethod(method, typeDatabase);
        Assert.False(method.HasClosureCdeclWrapper);
    }

    [Fact]
    public void Detect_ConventionCFromMangledName_SkipsCdeclWrapper()
    {
        // Mangled names with XC indicate @convention(c) closures.
        // ABI JSON doesn't include convention attributes, so mangled name is the only signal.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        // (Int32) -> Int32 closure — primitive, would be Cdecl-compatible if not @convention(c)
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int32") }),
            new NamedTypeSpec("Swift.Int32"));
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var parentDecl = CreateClassDecl("CFunctionTest", moduleDecl);
        var method = CreateMethodDecl("callCFunction", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int32"), isAsync: false, throws: false,
            methodType: MethodType.Static);
        // Set mangled name with XC marker for @convention(c)
        method.MangledName = "$s20SwiftBindingsTestLib13callCFunctionys5Int32VA2DXCF";
        method.CSSignature.Add(CreateArgument("fn", closureType, moduleDecl));

        EmitMethod(method, typeDatabase);

        // XC in mangled name → @convention(c) detected → no Cdecl wrapper
        Assert.False(method.HasClosureCdeclWrapper);
    }

    [Fact]
    public void Emit_NonAsyncMethodWithOptionalClosure_CdeclPInvokePassesIntPtrZeroForNil()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var optionalClosure = new NamedTypeSpec("Swift.Optional", closureType);

        var method = CreateMethodDecl("maybeHandle", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", optionalClosure, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Optional nil → IntPtr.Zero for both func ptr and context
        Assert.Contains("IntPtr.Zero", csOutput);
        Assert.Contains("Handle.IsAllocated", csOutput);
    }

    #endregion

    #region Closure Marshalling Tests

    [Fact]
    public void Emit_CdeclClosureMarshalling_UsesGCHandleOnly()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("handle", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Cdecl closure wrapper: GCHandle.Alloc without SwiftClosureData construction
        Assert.Contains("GCHandle.Alloc", csOutput);
        Assert.DoesNotContain("new SwiftClosureData", csOutput);
        Assert.DoesNotContain("SwiftClosureData", csOutput);
    }

    [Fact]
    public void Emit_CdeclClosureMethod_EmitsCdeclFuncPtrStaticField()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("handle", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Static field for callback function pointer should use Cdecl
        Assert.Contains("private static unsafe readonly delegate* unmanaged[Cdecl]<", csOutput);
    }

    #endregion

    #region MethodDecl Flag Tests

    [Fact]
    public void MethodDecl_HasClosureCdeclWrapper_DefaultsFalse()
    {
        var method = new MethodDecl
        {
            Name = "test",
            MangledName = "$sTest",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        Assert.False(method.HasClosureCdeclWrapper);
        Assert.False(method.UsesFreeFunctionWrapper);
    }

    #endregion

    #region Codex Review Regression Tests

    [Fact]
    public void NeedsClosureCdeclWrapper_MethodWithAsyncThrowingAndRegularClosure_ReturnsFalse()
    {
        // P0 fix: If a method has ANY async-throwing closures, the standalone Swift wrapper
        // can't handle them (renders as native types, but C# emits AsyncThrowingContext/StartFunc).
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        // Regular escaping closure (primitive, Cdecl-compatible)
        var regularClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        regularClosure.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Async-throwing closure
        var asyncThrowingClosure = new ClosureTypeSpec(
            TupleTypeSpec.Empty,
            new NamedTypeSpec("Swift.Int"));
        asyncThrowingClosure.Attributes.Add(new TypeSpecAttribute("escaping"));
        asyncThrowingClosure.IsAsync = true;
        asyncThrowingClosure.Throws = true;

        var method = CreateMethodDecl("process", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("onProgress", regularClosure, moduleDecl));
        method.CSSignature.Add(CreateArgument("onComplete", asyncThrowingClosure, moduleDecl));

        Assert.False(ClosureEmitter.NeedsClosureCdeclWrapper(method, closureHandler));
    }

    [Fact]
    public void NeedsClosureCdeclWrapper_ClassConstructorWithClosure_ReturnsTrueButMethodHandlerGuards()
    {
        // P0 fix: Non-frozen struct and class constructors require indirect return ABI.
        // NeedsClosureCdeclWrapper itself returns true (it doesn't check constructor return ABI),
        // but MethodHandler guards the constructor path to only allow frozen structs.
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var constructor = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule6LoaderC4inityACyF",
            MethodType = MethodType.Static,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, new NamedTypeSpec("TestModule.Loader"), moduleDecl),
                CreateArgument("callback", closureType, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        // Detection returns true (it doesn't know about constructor return ABI)
        Assert.True(ClosureEmitter.NeedsClosureCdeclWrapper(constructor, closureHandler));

        // But emitting through ConstructorHandler should NOT set the flag for class types
        EmitConstructor(constructor, typeDatabase);
        Assert.False(constructor.HasClosureCdeclWrapper);
    }

    [Fact]
    public void NeedsClosureCdeclWrapper_FailableConstructorWithClosure_NotWrapped()
    {
        // Failable constructors (init?) require indirect return for Optional<Self>.
        var typeDatabase = CreateConstructorTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl> { CreateArgument("callback", closureType, moduleDecl) });
        constructor.IsFailable = true;

        EmitConstructor(constructor, typeDatabase);

        // Failable constructor → indirect return → no Cdecl wrapper
        Assert.False(constructor.HasClosureCdeclWrapper);
    }

    [Fact]
    public void HasConventionCInMangledName_XCInFunctionTypeSection_Detected()
    {
        // XC after 'y' (function type start) → correctly detected
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int32") }),
            new NamedTypeSpec("Swift.Int32"));
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("callCFunction", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int32"), isAsync: false, throws: false,
            methodType: MethodType.Static);
        method.MangledName = "$s20SwiftBindingsTestLib13callCFunctionys5Int32VA2DXCF";
        method.CSSignature.Add(CreateArgument("fn", closureType, moduleDecl));

        Assert.False(ClosureEmitter.NeedsClosureCdeclWrapper(method, closureHandler));
    }

    [Fact]
    public void HasConventionCInMangledName_XCInIdentifierOnly_SafeFalsePositive()
    {
        // XC in identifier name (e.g., "processXCData") triggers a safe false positive:
        // the method is suppressed from Cdecl wrapping and stays on the functional legacy
        // CallConvSwift path. This is safe — the alternative (false negative) would cause
        // Swift compile errors when wrapping actual @convention(c) closures.
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("processXCData", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Instance);
        // XC appears in the identifier — detected as @convention(c) (safe false positive)
        method.MangledName = "$s10TestModule6LoaderC13processXCDataySiyF";
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        // Safe false positive: suppresses Cdecl wrapper, stays on legacy path
        Assert.False(ClosureEmitter.NeedsClosureCdeclWrapper(method, closureHandler));
    }

    [Fact]
    public void Emit_FrozenStructValueType_InstanceMethodWithClosure_UsesFixedBlockNotPayload()
    {
        // P0 fix: Frozen struct value types have no _payload SafeHandle.
        // Instance methods must use `fixed (T* __self = &this)` + `(IntPtr)__self`
        // instead of `_payload.DangerousGetHandle()`.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        // LottieColor is registered as frozen struct with no memory management (value type)
        var parentDecl = CreateStructDecl("LottieColor", moduleDecl, isFrozen: true);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("transform", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"), isAsync: false, throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Must use fixed block to pin 'this' — frozen struct value types have no _payload
        Assert.Contains("fixed (", csOutput);
        Assert.Contains("__self", csOutput);
        // Must NOT reference _payload which doesn't exist on value types
        Assert.DoesNotContain("_payload", csOutput);
    }

    [Fact]
    public void Emit_FrozenStructValueType_StaticMethodWithClosure_NoFixedBlock()
    {
        // Static methods don't have self — no fixed block needed even on frozen struct value types
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateStructDecl("LottieColor", moduleDecl, isFrozen: true);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("process", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"), isAsync: false, throws: false,
            methodType: MethodType.Static);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Static methods have no self — no fixed block, no _payload, no __self
        Assert.DoesNotContain("fixed (", csOutput);
        Assert.DoesNotContain("_payload", csOutput);
    }

    #endregion

    #region Unsafe Body Detection for Closures

    [Fact]
    public void StaticMethodWithClosure_EmitsUnsafeBody()
    {
        // Static/module-level method with a supported closure parameter.
        // No SwiftSelf, no IndirectResult, no SwiftAsync, no generics —
        // closure is the ONLY reason unsafe is required (delegate* unmanaged).
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("callWithInt", parentDecl, moduleDecl,
            returnType: TupleTypeSpec.Empty, isAsync: false, throws: false,
            methodType: MethodType.Static);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // The wrapper method body must contain an unsafe { } block because closure
        // marshalling emits delegate* unmanaged function pointers.
        // We assert specifically on the method body — not just any "unsafe" token,
        // since callback field declarations (e.g. "private static unsafe readonly delegate*")
        // already contain "unsafe" and would make a blanket assertion pass vacuously.
        var lines = csOutput.Split('\n').Select(l => l.Trim()).ToArray();
        var methodLineIdx = Array.FindIndex(lines, l => l.Contains("CallWithInt("));
        Assert.True(methodLineIdx >= 0, "Expected wrapper method 'CallWithInt' in output");

        // Find "unsafe" block inside the method body (after the method signature line)
        var bodyLines = lines.Skip(methodLineIdx + 1).ToArray();
        var unsafeIdx = Array.FindIndex(bodyLines, l => l == "unsafe");
        Assert.True(unsafeIdx >= 0, "Expected 'unsafe' block inside CallWithInt method body");
        Assert.Equal("{", bodyLines[unsafeIdx + 1]);
    }

    #endregion

    #region Unsafe Body Detection for Class Returns

    [Fact]
    public void StaticMethodReturningClass_EmitsUnsafeBody()
    {
        // Static method returning a class type (e.g., ImageCache.Shared getter).
        // No SwiftSelf, no IndirectResult, no SwiftAsync, no generics, no closures —
        // class-return marshalling (sizeof(IntPtr) + pointer deref) is the ONLY
        // reason unsafe is required.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var method = CreateMethodDecl("shared", parentDecl, moduleDecl,
            returnType: new NamedTypeSpec("TestModule.Loader"), isAsync: false, throws: false,
            methodType: MethodType.Static);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // The method body must contain unsafe { } because class-return marshalling
        // uses sizeof(IntPtr) and *(IntPtr*) pointer dereference.
        var lines = csOutput.Split('\n').Select(l => l.Trim()).ToArray();
        var methodLineIdx = Array.FindIndex(lines, l => l.Contains("GetShared("));
        Assert.True(methodLineIdx >= 0, "Expected wrapper method 'GetShared' in output");

        var bodyLines = lines.Skip(methodLineIdx + 1).ToArray();
        var unsafeIdx = Array.FindIndex(bodyLines, l => l == "unsafe");
        Assert.True(unsafeIdx >= 0, "Expected 'unsafe' block inside GetShared method body");
        Assert.Equal("{", bodyLines[unsafeIdx + 1]);
    }

    #endregion

    #region Q3 Closure Parameter Relaxation Tests

    [Fact]
    public void IsClosureCdeclCompatible_ClassParam_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);

        // Closure: (Loader) -> Void — class param should be Cdecl-compatible
        var closureType = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.Loader"),
            TupleTypeSpec.Empty);

        Assert.True(ClosureEmitter.IsClosureCdeclCompatible(closureType, closureHandler));
    }

    [Fact]
    public void IsClosureCdeclCompatible_SimpleEnumParam_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);

        // Closure: (ColorMode) -> Void — simple enum should be Cdecl-compatible
        var closureType = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.ColorMode"),
            TupleTypeSpec.Empty);

        Assert.True(ClosureEmitter.IsClosureCdeclCompatible(closureType, closureHandler));
    }

    [Fact]
    public void IsClosureCdeclCompatible_ObjCBridgedParam_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);

        // Closure: (NSError) -> Void — ObjC-bridged should be Cdecl-compatible
        var closureType = new ClosureTypeSpec(
            new NamedTypeSpec("Foundation.NSError"),
            TupleTypeSpec.Empty);

        Assert.True(ClosureEmitter.IsClosureCdeclCompatible(closureType, closureHandler));
    }

    [Fact]
    public void IsClosureCdeclCompatible_OptionalClass_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);

        // Closure: (Optional<Loader>) -> Void — Optional<Class> uses nil-pointer ABI
        var optionalClass = new NamedTypeSpec("Swift.Optional",
            new NamedTypeSpec("TestModule.Loader"));
        var closureType = new ClosureTypeSpec(optionalClass, TupleTypeSpec.Empty);

        Assert.True(ClosureEmitter.IsClosureCdeclCompatible(closureType, closureHandler));
    }

    [Fact]
    public void IsClosureCdeclCompatible_OptionalObjCBridged_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);

        // Closure: (Optional<NSError>) -> Void — Optional<ObjC> uses nil-pointer ABI
        var optionalNSError = new NamedTypeSpec("Swift.Optional",
            new NamedTypeSpec("Foundation.NSError"));
        var closureType = new ClosureTypeSpec(optionalNSError, TupleTypeSpec.Empty);

        Assert.True(ClosureEmitter.IsClosureCdeclCompatible(closureType, closureHandler));
    }

    [Fact]
    public void SwiftWrapper_SimpleEnumParam_UsesIntegerType()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);

        // Closure: (ColorMode) -> Void
        var closureType = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.ColorMode"),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var conventionCType = ClosureEmitter.GetSwiftConventionCType(closureType, closureHandler);
        // Simple enum should use the underlying Swift integer type (Int32), not UnsafeMutableRawPointer
        Assert.Contains("Int32", conventionCType);
        Assert.DoesNotContain("UnsafeMutableRawPointer, UnsafeMutableRawPointer?", conventionCType);
    }

    [Fact]
    public void SwiftWrapper_ClassParam_UsesPointerType()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);

        // Closure: (Loader) -> Void
        var closureType = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.Loader"),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var conventionCType = ClosureEmitter.GetSwiftConventionCType(closureType, closureHandler);
        // Class params use UnsafeMutableRawPointer (pointer ABI)
        Assert.Contains("UnsafeMutableRawPointer", conventionCType);
    }

    [Fact]
    public void SwiftWrapper_OptionalClassParam_UsesOptionalPointerType()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);

        // Closure: (Optional<Loader>) -> Void
        var optionalLoader = new NamedTypeSpec("Swift.Optional",
            new NamedTypeSpec("TestModule.Loader"));
        var closureType = new ClosureTypeSpec(optionalLoader, TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var conventionCType = ClosureEmitter.GetSwiftConventionCType(closureType, closureHandler);
        // Optional<Class> should use UnsafeMutableRawPointer? (nullable pointer)
        Assert.Contains("UnsafeMutableRawPointer?", conventionCType);
    }

    [Fact]
    public void IsClosureCdeclCompatible_OptionalPrimitive_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);

        // Closure: (Optional<Int32>) -> Void — Optional<Primitive> uses heap-allocated pointer ABI
        var optionalInt = new NamedTypeSpec("Swift.Optional",
            new NamedTypeSpec("Swift.Int32"));
        var closureType = new ClosureTypeSpec(optionalInt, TupleTypeSpec.Empty);

        Assert.True(ClosureEmitter.IsClosureCdeclCompatible(closureType, closureHandler));
    }

    [Fact]
    public void IsClosureCdeclCompatible_OptionalBool_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);

        // Closure: (Optional<Bool>) -> Void — Optional<Bool> uses extra inhabitant encoding
        // (value > 1 for None), not tag-byte layout. Runtime MarshalOptionalFromSwift can't handle it.
        var optionalBool = new NamedTypeSpec("Swift.Optional",
            new NamedTypeSpec("Swift.Bool"));
        var closureType = new ClosureTypeSpec(optionalBool, TupleTypeSpec.Empty);

        Assert.False(ClosureEmitter.IsClosureCdeclCompatible(closureType, closureHandler));
    }

    [Fact]
    public void IsClosureCdeclCompatible_OptionalSimpleEnum_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);

        // Closure: (Optional<ColorMode>) -> Void — Optional<SimpleEnum> uses extra inhabitant encoding,
        // not tag-byte layout. Runtime MarshalOptionalFromSwift can't handle it.
        var optionalEnum = new NamedTypeSpec("Swift.Optional",
            new NamedTypeSpec("TestModule.ColorMode"));
        var closureType = new ClosureTypeSpec(optionalEnum, TupleTypeSpec.Empty);

        Assert.False(ClosureEmitter.IsClosureCdeclCompatible(closureType, closureHandler));
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
            SwiftTypeName.FromModuleQualifiedName("Swift.Double"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Double"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Double"),
                MetadataAccessor = "$sSdMa",
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
                MetadataAccessor = "$sSqMa",
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
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.LottieColor"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "LottieColor"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.LottieColor"),
                MetadataAccessor = "$s10TestModule11LottieColorVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.ColorMode"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ColorMode"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ColorMode"),
                MetadataAccessor = "$s10TestModule9ColorModeOMa",
                Flags = TypeRecordFlags.SimpleEnum,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = "Int32"
            });
        typeDatabase.AddModuleDatabase(module);

        var foundationModule = new ModuleTypeDatabase("Foundation", "/usr/lib/swift/libswiftFoundation.dylib");
        foundationModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Foundation.NSError"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSError"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.NSError"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(foundationModule);

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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Point"),
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
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa"
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
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

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
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    #endregion
}
