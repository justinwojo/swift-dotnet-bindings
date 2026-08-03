// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the MethodClosureBridge emitter — handles regular methods with closure
/// parameters whose closure argument types include bound generics.
/// </summary>
public class MethodClosureBridgeTests
{
    // ─── IsEligible ───────────────────────────────────────────────────

    [Fact]
    public void IsEligible_MethodWithBoundGenericClosureArg_ReturnsTrue()
    {
        var (method, typeDatabase) = CreateMethodWithBoundGenericClosure();
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_MethodWithPrimitiveOnlyClosureArgs_ReturnsFalse()
    {
        // Closures with all-primitive args go through the normal ClosureEmitter pipeline
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("doWork", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_AsyncMethod_ReturnsFalse()
    {
        var (method, typeDatabase) = CreateMethodWithBoundGenericClosure();
        method.IsAsync = true;
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_ThrowingMethod_ReturnsFalse()
    {
        var (method, typeDatabase) = CreateMethodWithBoundGenericClosure();
        method.Throws = true;
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_ProtocolExtensionMethod_ReturnsFalse()
    {
        var (method, typeDatabase) = CreateMethodWithBoundGenericClosure();
        method.IsProtocolExtensionMethod = true;
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_ClosureWithObjCBridgedGenericArg_ReturnsFalse()
    {
        // ObjC-bridged types don't implement ISwiftObject, so they can't be
        // generic args in bound generic types with ISwiftObject constraints.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // DataResponse<NSError> — NSError is ObjC-bridged
        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("Foundation.NSError"));

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("callback", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    // ─── IsEligible: any Swift.Error existential ──────────────────────

    [Fact]
    public void IsAnyErrorExistential_SwiftError_ReturnsTrue()
    {
        var errorList = new ProtocolListTypeSpec(new List<NamedTypeSpec> { new NamedTypeSpec("Swift.Error") });
        Assert.True(MethodClosureBridge.IsAnyErrorExistential(errorList));
    }

    [Fact]
    public void IsAnyErrorExistential_SwiftErrorNamedWithIsAny_ReturnsTrue()
    {
        // The TypeSpecParser path for bare `any Swift.Error` — e.g., ABI JSON
        // TypeNominal child with printedName "any Swift.Error" — produces a
        // NamedTypeSpec with IsAny=true, NOT a ProtocolListTypeSpec. Both shapes
        // must be recognized so MCB activates for real-world APIs.
        var errorNamed = new NamedTypeSpec("Swift.Error") { IsAny = true };
        Assert.True(MethodClosureBridge.IsAnyErrorExistential(errorNamed));
    }

    [Fact]
    public void IsAnyErrorExistential_SwiftErrorNamedWithoutIsAny_ReturnsFalse()
    {
        // Bare NamedTypeSpec("Swift.Error") without IsAny is the Error metatype,
        // not the existential value — reject it so we don't confuse the two.
        var errorNamed = new NamedTypeSpec("Swift.Error");
        Assert.False(MethodClosureBridge.IsAnyErrorExistential(errorNamed));
    }

    [Fact]
    public void IsAnyErrorExistential_OtherProtocol_ReturnsFalse()
    {
        // Non-Error single protocol should NOT match — the gate is narrow on purpose
        var hashableList = new ProtocolListTypeSpec(new List<NamedTypeSpec> { new NamedTypeSpec("Swift.Hashable") });
        Assert.False(MethodClosureBridge.IsAnyErrorExistential(hashableList));
    }

    [Fact]
    public void IsAnyErrorExistential_NamedType_ReturnsFalse()
    {
        // Primitive / concrete types are not existentials
        Assert.False(MethodClosureBridge.IsAnyErrorExistential(new NamedTypeSpec("Swift.Int")));
    }

    [Fact]
    public void IsAnyErrorExistential_MultiProtocolComposition_ReturnsFalse()
    {
        // `any Error & Sendable` — composition, not the plain AnyError case
        var compList = new ProtocolListTypeSpec(new List<NamedTypeSpec>
        {
            new NamedTypeSpec("Swift.Error"),
            new NamedTypeSpec("Swift.Sendable")
        });
        Assert.False(MethodClosureBridge.IsAnyErrorExistential(compList));
    }

    [Fact]
    public void IsEligible_ClosureWithAnyErrorArg_ReturnsTrue()
    {
        // `(any Error) -> Void` — MCB must activate so the error existential can be bridged
        // through ExistentialContainer1 to C# Swift.Foundation.AnyError. Covers
        // `Result<T, any Error>` completion handler patterns.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var errorExistential = new ProtocolListTypeSpec(new List<NamedTypeSpec> { new NamedTypeSpec("Swift.Error") });
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)errorExistential }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onError", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_ClosureWithMultipleOptionalAnyErrorArgs_ReturnsFalse()
    {
        // `((any Error)?, (any Error)?) -> Void` — the Swift body emitter only supports a
        // single if-let branch for Optional<any Error>; two would require nested branching
        // that isn't implemented. Reject at the eligibility gate so generation falls back
        // cleanly to the SB0001 diagnostic path instead of crashing mid-emit.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var errorExistential = new NamedTypeSpec("Swift.Error") { IsAny = true };
        var optionalError = new NamedTypeSpec("Swift.Optional", errorExistential);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)optionalError, (TypeSpec)optionalError }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onTwoErrors", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    // ─── TryEmit: Swift Wrapper ───────────────────────────────────────

    [Fact]
    public void TryEmit_BoolClosureArg_EmitsUInt8ConversionInSwiftWrapper()
    {
        var (method, typeDatabase, env) = CreateMethodWithMixedClosureArgs();
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var result = MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        Assert.True(result);
        var swift = swiftOutput.ToString();
        // Swift cdecl type must use UInt8 for Bool, not Bool
        Assert.Contains("UInt8", swift);
        // Bool args must be converted: (__p1 ? 1 : 0)
        Assert.Contains("? 1 : 0)", swift);
    }

    [Fact]
    public void TryEmit_BoolReturnClosure_EmitsUInt8ToBoolConversion()
    {
        var (method, typeDatabase, env) = CreateMethodWithBoolReturnClosure();
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var result = MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        Assert.True(result);
        var swift = swiftOutput.ToString();
        // cdecl returns UInt8, original expects Bool → need != 0
        Assert.Contains("!= 0", swift);
    }

    [Fact]
    public void TryEmit_ValueTypeClosureArg_EmitsWithUnsafePointer()
    {
        var (method, typeDatabase) = CreateMethodWithBoundGenericClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var result = MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        Assert.True(result);
        var swift = swiftOutput.ToString();
        // Bound generic value types need withUnsafePointer wrapping
        Assert.Contains("withUnsafePointer(to:", swift);
        Assert.Contains("UnsafeMutableRawPointer(mutating:", swift);
    }

    // ─── Non-throwing closure callbacks fail fast, never swallow ──

    [Fact]
    public void TryEmit_VoidReturnClosure_CallbackFailsFastOnManagedException()
    {
        // The void-return UCO callback invokes the managed delegate. A non-throwing Swift closure
        // has no error channel, so a managed exception escaping the delegate must route to the
        // fail-fast contract, not be swallowed by a bare `catch { }` (which would let Swift
        // proceed as if the callback succeeded).
        var (method, typeDatabase) = CreateMethodWithBoundGenericClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        var result = MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        Assert.True(result);
        var cs = csOutput.ToString();
        Assert.Contains("catch (global::System.Exception", cs);
        Assert.Contains("FailFastUnhandledClosureException", cs);
        Assert.DoesNotContain("catch { }", cs);
    }

    [Fact]
    public void TryEmit_BoolReturnClosure_CallbackFailsFastOnManagedException()
    {
        // The bool-return callback must NOT fabricate `return 0;` on a managed fault — that hands
        // Swift a bogus `false`. It must fail fast.
        var (method, typeDatabase, env) = CreateMethodWithBoolReturnClosure();
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        var result = MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        Assert.True(result);
        var cs = csOutput.ToString();
        Assert.Contains("catch (global::System.Exception", cs);
        Assert.Contains("FailFastUnhandledClosureException", cs);
        Assert.DoesNotContain("catch { return 0; }", cs);
    }

    [Fact]
    public void TryEmit_AnyErrorClosureArg_EmitsExistentialContainerMarshal()
    {
        // `(any Error) -> Void` — MCB bridges the 5-word existential container:
        //   Swift: withUnsafePointer(to: err) { UnsafeMutableRawPointer(mutating: $0) }
        //   C#:    new Swift.Foundation.AnyError(*(ExistentialContainer1*)ptr)
        // Public delegate must expose Swift.Foundation.AnyError to consumers so they can call
        // .LocalizedDescription without touching raw containers.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var errorExistential = new ProtocolListTypeSpec(new List<NamedTypeSpec> { new NamedTypeSpec("Swift.Error") });
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)errorExistential }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onError", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler");
        var env = new MethodEnvironment(method, typeDatabase);

        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var result = MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        Assert.True(result);
        var swift = swiftOutput.ToString();
        var cs = csOutput.ToString();

        // Swift adapter must bridge the existential via withUnsafePointer (same shape as bound generic path).
        Assert.Contains("withUnsafePointer(to:", swift);
        Assert.Contains("UnsafeMutableRawPointer(mutating:", swift);
        // Swift adapter closure param is typed as `any Swift.Error` (or `any Error`).
        Assert.Contains("any", swift);
        Assert.Contains("Error", swift);

        // Public API delegate must expose Swift.Foundation.AnyError to the consumer.
        Assert.Contains("Action<Swift.Foundation.AnyError>", cs);
        // C# callback marshal must dereference the ExistentialContainer1* into a new AnyError.
        Assert.Contains("new global::Swift.Foundation.AnyError(*(global::Swift.Runtime.ExistentialContainer1*)", cs);
    }

    // ─── Optional closure support ─────────────────────────────────────

    [Fact]
    public void IsEligible_OptionalClosureWithBoundGenericArg_ReturnsTrue()
    {
        var (method, typeDatabase) = CreateMethodWithOptionalBoundGenericClosure();
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void TryEmit_OptionalClosureWithBoundGenericArg_EmitsNullableDelegate()
    {
        var (method, typeDatabase) = CreateMethodWithOptionalBoundGenericClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        var result = MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        Assert.True(result);
        var cs = csOutput.ToString();
        // Public-facing delegate parameter must be nullable so callers can pass null.
        Assert.Contains("Action<TestModule.DataResponse<TestModule.MyData>>? handler", cs);
    }

    [Fact]
    public void TryEmit_OptionalClosureWithBoundGenericArg_EmitsMapAdapter()
    {
        var (method, typeDatabase) = CreateMethodWithOptionalBoundGenericClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var result = MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        Assert.True(result);
        var swift = swiftOutput.ToString();
        // Must NOT force-unwrap the funcPtr (force-unwrap of nil would trap on a null caller).
        Assert.DoesNotContain("handlerFuncPtr!", swift);
        // Must build the adapter via `.map { __fp in ... }` so nil passes through.
        Assert.Contains("handlerFuncPtr.map", swift);
    }

    [Fact]
    public void TryEmit_OptionalClosureWithBoundGenericArg_GuardsGCHandleOnNull()
    {
        var (method, typeDatabase) = CreateMethodWithOptionalBoundGenericClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // C# must guard the ClosureHandle alloc + funcPtr/ctxPtr population behind a null check.
        Assert.Contains("if (handler != null)", cs);
        Assert.Contains("new ClosureHandle(__inner,", cs);
    }

    [Theory]
    [InlineData(null, "OnResponse(")]
    [InlineData("onResponseWithEmail", "OnResponseWithEmail(")]
    [InlineData("onResponseWithLink", "OnResponseWithLink(")]
    public void TryEmit_AppliesDisambiguatedNameToPublicMethodName(string? disambiguatedNameInput, string expectedSignaturePrefix)
    {
        // Two Swift overloads that project to the same C# parameter list (e.g.
        // signIn(withEmail:password:) vs signIn(withEmail:link:)) collide on the projected key, and
        // IHandler.HandleBaseDecl hands the loser a label-derived name input. The closure-bridge path
        // must read env.CSharpMethodName (which applies that input) instead of recomputing the bare
        // name, otherwise both overloads emit as `public void DoWork(...)` and produce CS0111.
        var (method, typeDatabase) = CreateMethodWithBoundGenericClosure();
        var env = new MethodEnvironment(method, typeDatabase) { DisambiguatedNameInput = disambiguatedNameInput };
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        var result = MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        Assert.True(result);
        var cs = csOutput.ToString();
        Assert.Contains(expectedSignaturePrefix, cs);
    }

    // ─── TryEmit: C# Callback ─────────────────────────────────────────

    [Fact]
    public void TryEmit_PrimitiveClosureArg_UsesTypedCallbackParam()
    {
        var (method, typeDatabase, env) = CreateMethodWithMixedClosureArgs();
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // Bool closure args should be "byte" in callback, not IntPtr
        Assert.Contains("byte arg1", cs);
        // Bound generic closure args stay as IntPtr
        Assert.Contains("IntPtr arg0", cs);
    }

    [Fact]
    public void TryEmit_PrimitiveClosureArg_FunctionPointerFieldUsesTypedArgs()
    {
        var (method, typeDatabase, env) = CreateMethodWithMixedClosureArgs();
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // Function pointer delegate type should have typed args, not all IntPtr
        // delegate* unmanaged[Cdecl]<IntPtr, byte, IntPtr, void>
        Assert.Contains("delegate* unmanaged[Cdecl]<IntPtr, byte, IntPtr, void>", cs);
    }

    [Fact]
    public void TryEmit_BoolClosureArgInCallback_EmitsByteConversion()
    {
        var (method, typeDatabase, env) = CreateMethodWithMixedClosureArgs();
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // Inner delegate in public method should use byte for Bool arg
        Assert.Contains("byte __p1", cs);
        // Bool arg marshal: __p1 != 0 (not __p1 != IntPtr.Zero)
        Assert.Contains("__p1 != 0", cs);
    }

    // ─── TryEmit: ObjC-bridged non-closure params ─────────────────────

    [Fact]
    public void TryEmit_ObjCBridgedParam_UsesHandle()
    {
        var (method, typeDatabase, env) = CreateMethodWithObjCParam();
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // ObjC-bridged params use .Handle, not .Payload.DangerousGetHandle()
        Assert.Contains("presenter.Handle", cs);
        // The non-closure param should NOT use .Payload (SwiftSelf for 'self' is separate)
        Assert.DoesNotContain("presenter.Payload", cs);
    }

    [Fact]
    public void TryEmit_SwiftClassParam_UsesPayload()
    {
        var (method, typeDatabase, env) = CreateMethodWithSwiftClassParam();
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // Swift-native class params use .Payload.DangerousGetHandle()
        Assert.Contains(".Payload.DangerousGetHandle()", cs);
    }

    // ─── TryEmit: Static methods ──────────────────────────────────────

    [Fact]
    public void TryEmit_StaticMethod_EmitsSelfDotInSwift()
    {
        var (method, typeDatabase) = CreateMethodWithBoundGenericClosure();
        method.MethodType = MethodType.Static;
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var swift = swiftOutput.ToString();
        // Static methods use fully-qualified type name (not "self." for instance)
        Assert.Contains("TestModule.MyClass.", swift);
        // @_cdecl free function (not extension method)
        Assert.Contains("@_cdecl", swift);
        Assert.DoesNotContain("extension", swift);

        var cs = csOutput.ToString();
        // Static methods don't use SwiftSelf (now IntPtr-based for @_cdecl)
        Assert.DoesNotContain("SwiftSelf", cs);
        Assert.Contains("static unsafe", cs);
    }

    // ─── ClassifyParam ─────────────────────────────────────────────────

    [Fact]
    public void ClassifyParam_Primitive_ReturnsPrimitive()
    {
        var typeDatabase = CreateTypeDatabase();
        var arg = CreateArgument("value", new NamedTypeSpec("Swift.Int"), CreateModuleDecl("TestModule"));

        var result = MethodClosureBridge.ClassifyParam(arg, typeDatabase);

        Assert.Equal(MethodClosureBridge.ParamAbiCategory.Primitive, result);
    }

    [Fact]
    public void ClassifyParam_Class_ReturnsPayloadHandle()
    {
        var typeDatabase = CreateTypeDatabase();
        var arg = CreateArgument("obj", new NamedTypeSpec("TestModule.MyClass"), CreateModuleDecl("TestModule"));

        var result = MethodClosureBridge.ClassifyParam(arg, typeDatabase);

        Assert.Equal(MethodClosureBridge.ParamAbiCategory.PayloadHandle, result);
    }

    [Fact]
    public void ClassifyParam_SwiftString_ReturnsUtf8Slice()
    {
        var typeDatabase = CreateTypeDatabase();
        var arg = CreateArgument("name", new NamedTypeSpec("Swift.String"), CreateModuleDecl("TestModule"));

        var result = MethodClosureBridge.ClassifyParam(arg, typeDatabase);

        Assert.Equal(MethodClosureBridge.ParamAbiCategory.Utf8Slice, result);
    }

    [Fact]
    public void ClassifyParam_ObjCBridged_ReturnsObjCHandle()
    {
        var typeDatabase = CreateTypeDatabase();
        var arg = CreateArgument("error", new NamedTypeSpec("Foundation.NSError"), CreateModuleDecl("TestModule"));

        var result = MethodClosureBridge.ClassifyParam(arg, typeDatabase);

        Assert.Equal(MethodClosureBridge.ParamAbiCategory.ObjCHandle, result);
    }

    [Fact]
    public void ClassifyParam_NonFrozenStruct_ReturnsPayloadHandle()
    {
        // DataResponse has Flags = None (not frozen), Kind = Struct → non-frozen struct
        var typeDatabase = CreateTypeDatabase();
        var arg = CreateArgument("req", new NamedTypeSpec("TestModule.DataResponse"), CreateModuleDecl("TestModule"));

        var result = MethodClosureBridge.ClassifyParam(arg, typeDatabase);

        Assert.Equal(MethodClosureBridge.ParamAbiCategory.PayloadHandle, result);
    }

    [Fact]
    public void ClassifyParam_FrozenStruct_ReturnsFrozenStruct()
    {
        var typeDatabase = CreateTypeDatabaseWithExtendedTypes();
        var arg = CreateArgument("point", new NamedTypeSpec("TestModule.FrozenPoint"), CreateModuleDecl("TestModule"));

        var result = MethodClosureBridge.ClassifyParam(arg, typeDatabase);

        Assert.Equal(MethodClosureBridge.ParamAbiCategory.FrozenStruct, result);
    }

    [Fact]
    public void ClassifyParam_FrozenWithMemory_ReturnsFrozenStruct()
    {
        var typeDatabase = CreateTypeDatabaseWithExtendedTypes();
        var arg = CreateArgument("container", new NamedTypeSpec("TestModule.FrozenContainer"), CreateModuleDecl("TestModule"));

        var result = MethodClosureBridge.ClassifyParam(arg, typeDatabase);

        Assert.Equal(MethodClosureBridge.ParamAbiCategory.FrozenStruct, result);
    }

    [Fact]
    public void ClassifyParam_PointerType_ReturnsPointerType()
    {
        var typeDatabase = CreateTypeDatabase();
        var arg = CreateArgument("ptr", new NamedTypeSpec("Swift.UnsafePointer"), CreateModuleDecl("TestModule"));

        var result = MethodClosureBridge.ClassifyParam(arg, typeDatabase);

        Assert.Equal(MethodClosureBridge.ParamAbiCategory.PointerType, result);
    }

    [Fact]
    public void ClassifyParam_BufferPointerType_ReturnsPointerType()
    {
        var typeDatabase = CreateTypeDatabase();
        var arg = CreateArgument("buf", new NamedTypeSpec("Swift.UnsafeBufferPointer"), CreateModuleDecl("TestModule"));

        var result = MethodClosureBridge.ClassifyParam(arg, typeDatabase);

        Assert.Equal(MethodClosureBridge.ParamAbiCategory.PointerType, result);
    }

    [Fact]
    public void ClassifyParam_NativeRemapped_ReturnsNativeRemapped()
    {
        var typeDatabase = CreateTypeDatabaseWithExtendedTypes();
        var arg = CreateArgument("url", new NamedTypeSpec("Foundation.URL"), CreateModuleDecl("TestModule"));

        var result = MethodClosureBridge.ClassifyParam(arg, typeDatabase);

        Assert.Equal(MethodClosureBridge.ParamAbiCategory.NativeRemapped, result);
    }

    // ─── IsEligible: ParamAbiCategory integration ────────────────────

    [Fact]
    public void IsEligible_ResultClosure_NonFrozenStructParam_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabaseWithResultTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Closure: (Result<MyData, MyError>) -> Void (bound generic)
        var resultArg = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("TestModule.MyData"), new NamedTypeSpec("TestModule.MyError"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)resultArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Non-closure param: ImageRequest (non-frozen struct)
        var method = CreateMethodDeclWithNonClosureParam("loadImage", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "completion",
            new NamedTypeSpec("TestModule.ImageRequest"), "request");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_ResultClosure_SwiftStringParam_ReturnsTrue()
    {
        // Pattern C: Swift.String as a non-closure parameter on an MCB-eligible method.
        // MCB activates via the Result<T, any Error> closure arg; the string must be
        // accepted as Utf8Slice so MCB generates a fixed-block-pinned UTF-8 pair.
        var typeDatabase = CreateTypeDatabaseWithResultTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var resultArg = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("TestModule.MyData"), new NamedTypeSpec("TestModule.MyError"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)resultArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDeclWithNonClosureParam("loadImage", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "completion",
            new NamedTypeSpec("Swift.String"), "cardNumber");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void TryEmit_SwiftStringNonClosureParam_EmitsUtf8SliceAndFixedBlock()
    {
        var typeDatabase = CreateTypeDatabaseWithResultTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var resultArg = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("TestModule.MyData"), new NamedTypeSpec("TestModule.MyError"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)resultArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDeclWithNonClosureParam("loadImage", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "completion",
            new NamedTypeSpec("Swift.String"), "cardNumber");
        var env = new MethodEnvironment(method, typeDatabase);

        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var emitted = MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        Assert.True(emitted);
        var swift = swiftOutput.ToString();
        var cs = csOutput.ToString();

        // Swift wrapper splits the string into (pointer, length) and rebuilds via String(bytes:encoding:)
        Assert.Contains("cardNumberUtf8Ptr: UnsafePointer<UInt8>", swift);
        Assert.Contains("cardNumberUtf8Len: Int", swift);
        Assert.Contains("String(bytes: UnsafeBufferPointer(start: cardNumberUtf8Ptr, count: cardNumberUtf8Len), encoding: .utf8)!", swift);
        // And invokes the original Swift method with the reconstructed Val.
        Assert.Contains("cardNumberVal", swift);

        // C# public method accepts a string, pins UTF-8 bytes via fixed, and passes (ptr, len).
        Assert.Contains("string cardNumber", cs);
        Assert.Contains("System.Text.Encoding.UTF8.GetBytes(cardNumber)", cs);
        Assert.Contains("fixed (byte* __cardNumberPtr = __cardNumberUtf8)", cs);

        // P/Invoke signature has IntPtr + nint pair, not a SwiftString.Buffer.
        Assert.Contains("IntPtr cardNumberUtf8Ptr", cs);
        Assert.Contains("nint cardNumberUtf8Len", cs);
    }

    [Fact]
    public void IsEligible_ResultClosure_FrozenStructParam_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabaseWithResultTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var resultArg = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("TestModule.MyData"), new NamedTypeSpec("TestModule.MyError"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)resultArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Non-closure param: FrozenPoint (frozen struct) — NOT passable
        var method = CreateMethodDeclWithNonClosureParam("loadImage", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "completion",
            new NamedTypeSpec("TestModule.FrozenPoint"), "point");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_ResultClosure_PointerStructParam_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabaseWithResultTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var resultArg = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("TestModule.MyData"), new NamedTypeSpec("TestModule.MyError"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)resultArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Non-closure param: UnsafePointer — NOT passable
        var method = CreateMethodDeclWithNonClosureParam("loadImage", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "completion",
            new NamedTypeSpec("Swift.UnsafePointer"), "ptr");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_ResultClosure_NativeRemappedParam_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabaseWithResultTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var resultArg = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("TestModule.MyData"), new NamedTypeSpec("TestModule.MyError"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)resultArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Non-closure param: Foundation.URL (native-remapped) — NOT passable
        var method = CreateMethodDeclWithNonClosureParam("loadImage", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "completion",
            new NamedTypeSpec("Foundation.URL"), "url");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_ResultClosure_ClassParam_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabaseWithResultTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var resultArg = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("TestModule.MyData"), new NamedTypeSpec("TestModule.MyError"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)resultArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Non-closure param: MyData (Swift class) — passable
        var method = CreateMethodDeclWithNonClosureParam("loadImage", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "completion",
            new NamedTypeSpec("TestModule.MyData"), "data");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_ResultClosure_ObjCParam_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabaseWithResultTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var resultArg = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("TestModule.MyData"), new NamedTypeSpec("TestModule.MyError"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)resultArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Non-closure param: NSError (ObjC-bridged) — passable
        var method = CreateMethodDeclWithNonClosureParam("loadImage", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "completion",
            new NamedTypeSpec("Foundation.NSError"), "error");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    // ─── TryEmit: Non-frozen struct params ────────────────────────────

    [Fact]
    public void TryEmit_NonFrozenStructParam_EmitsPayloadDangerousGetHandle()
    {
        var typeDatabase = CreateTypeDatabaseWithResultTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var resultArg = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("TestModule.MyData"), new NamedTypeSpec("TestModule.MyError"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)resultArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDeclWithNonClosureParam("loadImage", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "completion",
            new NamedTypeSpec("TestModule.ImageRequest"), "request");
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // Non-frozen struct params use .Payload.DangerousGetHandle() (same ABI as classes)
        Assert.Contains("request.Payload.DangerousGetHandle()", cs);
    }

    [Fact]
    public void TryEmit_ResultClosureArg_EmitsWithUnsafePointer()
    {
        var typeDatabase = CreateTypeDatabaseWithResultTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var resultArg = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("TestModule.MyData"), new NamedTypeSpec("TestModule.MyError"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)resultArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDeclWithNonClosureParam("loadImage", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "completion",
            new NamedTypeSpec("TestModule.ImageRequest"), "request");
        var env = new MethodEnvironment(method, typeDatabase);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);
        var csWriter = new CSharpWriter(new StringWriter());

        MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var swift = swiftOutput.ToString();
        // Result<T,E> is a value type — needs withUnsafePointer wrapping
        Assert.Contains("withUnsafePointer(to:", swift);
    }

    [Fact]
    public void TryEmit_ResultClosureArg_EmitsSwiftMarshalForResult()
    {
        var typeDatabase = CreateTypeDatabaseWithResultTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var resultArg = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("TestModule.MyData"), new NamedTypeSpec("TestModule.MyError"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)resultArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDeclWithNonClosureParam("loadImage", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "completion",
            new NamedTypeSpec("TestModule.ImageRequest"), "request");
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // Result<T,E> closure args come as IntPtr, marshalled via MarshalCallbackArg
        // (callback parameters are borrowed references — dispatched on the wrapper's declared semantics)
        Assert.Contains("SwiftMarshal.MarshalCallbackArg<Swift.Runtime.SwiftResult<TestModule.MyData, TestModule.MyError>>", cs);
    }

    // ─── Multi-Closure (C1) ──────────────────────────────────────────

    [Fact]
    public void IsEligible_TwoClosures_OneWithBoundGeneric_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Closure 1: (DataResponse<MyData>) -> Void — bound generic
        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));
        var closure1 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }), TupleTypeSpec.Empty);
        closure1.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Closure 2: (Int) -> Void — primitive-only
        var closure2 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }), TupleTypeSpec.Empty);
        closure2.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDeclWithTwoClosures("dualCallback", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closure1, "onResult", closure2, "onProgress");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_TwoClosures_NeitherBoundGeneric_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Both closures have only primitives — no bound generics → not eligible
        var closure1 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }), TupleTypeSpec.Empty);
        closure1.Attributes.Add(new TypeSpecAttribute("escaping"));

        var closure2 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Bool") }), TupleTypeSpec.Empty);
        closure2.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDeclWithTwoClosures("dualCallback", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closure1, "onA", closure2, "onB");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_MultiClosure_AsyncClosure_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));
        var closure1 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }), TupleTypeSpec.Empty);
        closure1.Attributes.Add(new TypeSpecAttribute("escaping"));

        var closure2 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }), TupleTypeSpec.Empty);
        closure2.Attributes.Add(new TypeSpecAttribute("escaping"));
        closure2.IsAsync = true; // Async closure — not supported

        var method = CreateMethodDeclWithTwoClosures("dualCallback", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closure1, "onResult", closure2, "onProgress");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_MultiClosure_UnsupportedArg_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));
        var closure1 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }), TupleTypeSpec.Empty);
        closure1.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Closure 2 has an unsupported arg (unknown type not in database)
        var closure2 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("UnknownModule.UnknownType") }), TupleTypeSpec.Empty);
        closure2.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDeclWithTwoClosures("dualCallback", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closure1, "onResult", closure2, "onProgress");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_SingleClosure_BackwardCompatible()
    {
        // Verify single-closure behavior is unchanged after multi-closure refactor
        var (method, typeDatabase) = CreateMethodWithBoundGenericClosure();
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void TryEmit_TwoClosures_EmitsTwoCallbacksAndFuncPtrFields()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));
        var closure1 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }), TupleTypeSpec.Empty);
        closure1.Attributes.Add(new TypeSpecAttribute("escaping"));

        var closure2 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }), TupleTypeSpec.Empty);
        closure2.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDeclWithTwoClosures("dualCallback", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closure1, "onResult", closure2, "onProgress");
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var result = MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        Assert.True(result);
        var cs = csOutput.ToString();
        var swift = swiftOutput.ToString();

        // Two funcPtr+context pairs in Swift wrapper params
        Assert.Contains("onResultFuncPtr", swift);
        Assert.Contains("onResultContext", swift);
        Assert.Contains("onProgressFuncPtr", swift);
        Assert.Contains("onProgressContext", swift);

        // Two separate callback methods in C# (MCB_xxx_0 and MCB_xxx_1)
        Assert.Contains("s_MCB_", cs); // funcPtr field references

        // Two GCHandle allocations in public method
        Assert.Contains("__gcHandle", cs);
        Assert.Contains("__gcHandle_1", cs);

        // Two delegate params in public method
        Assert.Contains("onResult", cs);
        Assert.Contains("onProgress", cs);
    }

    [Fact]
    public void TryEmit_BoundGenericPlusVoidClosure_EmitsValidSwiftSyntax()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Closure 1: (DataResponse<MyData>) -> Void — bound generic (activates bridge)
        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));
        var closure1 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }), TupleTypeSpec.Empty);
        closure1.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Closure 2: () -> Void — zero-arg closure
        var closure2 = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        closure2.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDeclWithTwoClosures("dualCallback", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closure1, "onResult", closure2, "onComplete");
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var result = MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        Assert.True(result);
        var swift = swiftOutput.ToString();

        // Zero-arg closure must NOT produce "{ in" or "{  in" — just "{ cdecl(...) }"
        Assert.DoesNotMatch(@"\{\s+in\s", swift);
        // The bound-generic closure SHOULD have "in" (it has params)
        Assert.Contains("in", swift);
        // Both closures should appear in wrapper
        Assert.Contains("onResultFuncPtr", swift);
        Assert.Contains("onCompleteFuncPtr", swift);
    }

    // ─── Complex Enum Closure Bridge ────────────────────────────────

    [Fact]
    public void IsEligible_ClosureWithComplexEnumArg_ReturnsTrue()
    {
        // A closure with a complex enum arg (no bound generics) should trigger MCB
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Closure: (MyError) -> Void — complex enum
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("TestModule.MyError") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onError", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_ClosureWithComplexEnumAndPrimitive_ReturnsTrue()
    {
        // Mixed complex enum + primitive closure args
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                new NamedTypeSpec("TestModule.MyError"),
                new NamedTypeSpec("Swift.Int")
            }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onError", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void TryEmit_ComplexEnumClosure_EmitsHeapAllocation()
    {
        // Verify Swift wrapper contains heap allocation for complex enum
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("TestModule.MyError") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onError", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler");
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var result = MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        Assert.True(result);
        var swift = swiftOutput.ToString();
        var cs = csOutput.ToString();

        // Swift wrapper should contain heap allocation
        Assert.Contains("UnsafeMutableRawPointer.allocate", swift);
        Assert.Contains("initializeMemory", swift);
        Assert.Contains("MemoryLayout<MyError>", swift);

        // C# callback must take ownership of the heap buffer — Swift wrapper has no defer,
        // so MarshalFromSwift wraps the pointer in a SafeHandle whose ReleaseHandle pairs
        // VWT.Destroy + NativeMemory.Free. The borrowed MarshalCallbackArg path would not own
        // the buffer here, so it must not be used.
        Assert.Contains("SwiftMarshal.MarshalFromSwift<TestModule.MyError>", cs);
        Assert.DoesNotContain("SwiftMarshal.MarshalCallbackArg<TestModule.MyError>", cs);
    }

    [Fact]
    public void TryEmit_ComplexEnumClosure_EmitsHeapAllocationWithoutDefer()
    {
        // Complex enum heap buffers are allocated and initialized but NOT deallocated
        // by the Swift wrapper — C# takes ownership via SwiftSafeHandle (VWT Destroy +
        // NativeMemory.Free on disposal). Defer would cause use-after-free.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("TestModule.MyError") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onError", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler");
        var env = new MethodEnvironment(method, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var swift = swiftOutput.ToString();
        Assert.Contains("allocate(byteCount:", swift);
        Assert.Contains("initializeMemory(as:", swift);
        // No defer — C# takes ownership
        Assert.DoesNotContain("defer", swift);
        Assert.DoesNotContain("deallocate()", swift);
    }

    [Fact]
    public void TryEmit_ComplexEnumClosure_EmitsCorrectDelegateType()
    {
        // Public method should use typed Action<MyError>
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("TestModule.MyError") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onError", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler");
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        // Public method should have Action<TestModule.MyError>
        Assert.Contains("Action<TestModule.MyError>", cs);
    }

    // ─── Swift wrapper: class vs struct PayloadHandle param loading ────

    [Fact]
    public void TryEmit_SwiftClassParam_EmitsUnmanagedInSwiftWrapper()
    {
        // Class PayloadHandle params must use Unmanaged<T>.fromOpaque().takeUnretainedValue()
        // in the Swift wrapper — NOT .assumingMemoryBound(to:).pointee (which reads heap memory
        // as a value slot, corrupting the reference).
        var (method, typeDatabase, env) = CreateMethodWithSwiftClassParam();
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var swift = swiftOutput.ToString();
        Assert.Contains("Unmanaged<MyData>.fromOpaque(other).takeUnretainedValue()", swift);
        Assert.DoesNotContain("assumingMemoryBound(to: MyData.self).pointee", swift);
    }

    [Fact]
    public void TryEmit_NonFrozenStructParam_EmitsPointeeInSwiftWrapper()
    {
        // Non-frozen struct PayloadHandle params use .assumingMemoryBound(to:).pointee
        // because the pointer points to value storage (not an object reference).
        var typeDatabase = CreateTypeDatabaseWithResultTypes();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var resultArg = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("TestModule.MyData"), new NamedTypeSpec("TestModule.MyError"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)resultArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDeclWithNonClosureParam("loadImage", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "completion",
            new NamedTypeSpec("TestModule.ImageRequest"), "request");
        var env = new MethodEnvironment(method, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var swift = swiftOutput.ToString();
        Assert.Contains("assumingMemoryBound(to: ImageRequest.self).pointee", swift);
        Assert.DoesNotContain("Unmanaged<ImageRequest>", swift);
    }

    // ─── Throw-window + _SBClosureCtx Owner Token ───

    [Fact]
    public void TryEmit_EscapingClosure_UsesClosureHandleWithEscapingPolicy()
    {
        // Throw-window regression test: the C# bridge pre-declares a `ClosureHandle` at method
        // scope (default-constructed so optional-closure null paths can still dispose), allocates
        // with the Escaping policy so Swift's `_SBClosureCtx` deinit upcall owns the handle on
        // the happy path, calls MarkOwnershipTransferred only after the P/Invoke returns, and
        // disposes in finally so a throw between alloc and that mark frees the handle locally.
        var (method, typeDatabase) = CreateMethodWithBoundGenericClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var cs = csOutput.ToString();
        Assert.Contains("ClosureHandle __gcHandle = default;", cs);
        Assert.Contains("new ClosureHandle(__inner, ClosureHandlePolicy.Escaping)", cs);
        Assert.Contains("try", cs);
        Assert.Contains("finally", cs);
        Assert.Contains("__gcHandle.MarkOwnershipTransferred();", cs);
        Assert.Contains("__gcHandle.Dispose();", cs);
        // The raw `__transferred` flag and direct GCHandle.Free / GCHandle.ToIntPtr calls
        // are no longer emitted from MCB — the helper encapsulates that contract.
        Assert.DoesNotContain("bool __transferred", cs);
        Assert.DoesNotContain("__gcHandle.Free()", cs);
        Assert.DoesNotContain("GCHandle.ToIntPtr(__gcHandle", cs);
    }

    [Fact]
    public void TryEmit_EscapingClosure_SwiftWrapperConstructsClosureContextBox()
    {
        // Swift-side regression test: the wrapper must wrap the GCHandle pointer in an
        // _SBClosureCtx box (deinit upcalls C# and frees the handle exactly once when Swift
        // releases the closure) and explicitly capture _box in the synthesized closure so its
        // lifetime tracks the closure via Swift ARC. Without the capture, the box is released
        // after the wrapper returns and the C# delegate is freed mid-callback.
        var (method, typeDatabase) = CreateMethodWithBoundGenericClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, env.ParentDecl as TypeDecl);

        var swift = swiftOutput.ToString();
        Assert.Contains("_sbWrapClosureContext", swift);
        Assert.Contains("[_box", swift);
        Assert.Contains("_ = _box", swift);
    }

    // ─── IsEligible: generic parent types ─────────────────────────────

    [Fact]
    public void IsEligible_GenericParentType_InstanceMethod_ReturnsTrue()
    {
        // Instance methods on generic parents use @_silgen_name extension approach
        // to inherit generic context, with CallConvSwift + SwiftSelf on C# side.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateGenericClassDecl("GenericClass", moduleDecl, "T");

        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onResponse", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_GenericParentType_StaticMethod_ReturnsFalse()
    {
        // Static methods on generic parents are still blocked — type metadata passing is complex.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateGenericClassDecl("GenericClass", moduleDecl, "T");

        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onResponse", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler");
        method.MethodType = MethodType.Static;
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_NonGenericParentType_ReturnsTrue()
    {
        // Sanity: non-generic parent types remain eligible
        var (method, typeDatabase) = CreateMethodWithBoundGenericClosure();
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.True(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    // ─── Simple Enum Regression ────────────────────────────────────────

    [Fact]
    public void IsEligible_BoundGenericClosureWithSimpleEnumArg_ReturnsFalse()
    {
        // Regression: simple enums are blittable integers — MCB's pointer ABI
        // (IntPtr + MarshalFromSwift<T>) doesn't support C# enum types.
        // Methods with bound-generic + simple-enum closure args must be skipped.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Closure: (DataResponse<MyData>, Direction) -> Void
        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                boundGenericArg,
                new NamedTypeSpec("TestModule.Direction")
            }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onResult", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    [Fact]
    public void IsEligible_ClosureWithOnlySimpleEnumArg_ReturnsFalse()
    {
        // Simple enum alone should not trigger MCB — it goes through normal pipeline
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("TestModule.Direction") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onDirection", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler");
        var closureHandler = new ClosureHandler(typeDatabase);

        Assert.False(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
    }

    // ─── Fix 1: MCB Swift function name uniqueness ─────────────────────

    [Fact]
    public void TryEmit_OverloadedMethodsSameNameDifferentTypes_UniqueSwiftFunctionNames()
    {
        // Two methods named "response" on different parent types must produce
        // different _sbw_mcb_ Swift function names to avoid redeclaration errors.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var parentA = CreateClassDecl("TypeA", moduleDecl);
        var parentB = CreateClassDecl("TypeB", moduleDecl);

        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));
        var closureType1 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }), TupleTypeSpec.Empty);
        closureType1.Attributes.Add(new TypeSpecAttribute("escaping"));
        var closureType2 = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }), TupleTypeSpec.Empty);
        closureType2.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Same method name, different parents → different MangledName → different hash
        var methodA = CreateMethodDecl("response", parentA, moduleDecl,
            TupleTypeSpec.Empty, closureType1, "handler",
            mangledName: "$s10TestModule5TypeAC8responseyACyF");

        var methodB = CreateMethodDecl("response", parentB, moduleDecl,
            TupleTypeSpec.Empty, closureType2, "handler",
            mangledName: "$s10TestModule5TypeBC8responseyACyF");

        var swiftOutputA = new StringWriter();
        var swiftWriterA = new SwiftWriter(swiftOutputA);
        var envA = new MethodEnvironment(methodA, typeDatabase);
        MethodClosureBridge.TryEmit(new CSharpWriter(new StringWriter()), swiftWriterA, envA, parentA);

        var swiftOutputB = new StringWriter();
        var swiftWriterB = new SwiftWriter(swiftOutputB);
        var envB = new MethodEnvironment(methodB, typeDatabase);
        MethodClosureBridge.TryEmit(new CSharpWriter(new StringWriter()), swiftWriterB, envB, parentB);

        var swiftA = swiftOutputA.ToString();
        var swiftB = swiftOutputB.ToString();

        // Both should contain _sbw_mcb_ prefix
        Assert.Contains("_sbw_mcb_MCB_", swiftA);
        Assert.Contains("_sbw_mcb_MCB_", swiftB);

        // Extract the function names and verify they differ
        var funcNameA = ExtractSwiftFuncName(swiftA, "_sbw_mcb_");
        var funcNameB = ExtractSwiftFuncName(swiftB, "_sbw_mcb_");
        Assert.NotEqual(funcNameA, funcNameB);
    }

    [Fact]
    public void TryEmit_SwiftFunctionNameContainsHashAndMethodName()
    {
        // The Swift function name should contain both the hash prefix (for uniqueness)
        // and the original method name (for readability/debugging).
        var (method, typeDatabase) = CreateMethodWithBoundGenericClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        MethodClosureBridge.TryEmit(new CSharpWriter(new StringWriter()), swiftWriter, env, env.ParentDecl as TypeDecl);

        var swift = swiftOutput.ToString();
        // Should contain both hash (MCB_XXXXXXXX) and method name (onResponse)
        Assert.Contains("_sbw_mcb_MCB_", swift);
        Assert.Contains("_onResponse(", swift);
    }

    private static string ExtractSwiftFuncName(string swift, string prefix)
    {
        var idx = swift.IndexOf(prefix);
        if (idx < 0) return "";
        var end = swift.IndexOf('(', idx);
        return end < 0 ? swift[idx..] : swift[idx..end];
    }

    // ─── Helper Methods ───────────────────────────────────────────────

    /// <summary>
    /// Creates a method with closure (DataResponse&lt;MyData&gt;) -> Void.
    /// DataResponse is a bound generic struct. MyData is a Swift-native class.
    /// </summary>
    private static (MethodDecl method, TypeDatabase typeDatabase) CreateMethodWithBoundGenericClosure()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Closure: (DataResponse<MyData>) -> Void
        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onResponse", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler");

        return (method, typeDatabase);
    }

    /// <summary>
    /// Creates a method with an Optional closure (DataResponse&lt;MyData&gt;) -&gt; Void.
    /// Tests Optional closures: force-unwrap must be avoided, public delegate must be nullable,
    /// and GCHandle.Alloc must be guarded behind a null check.
    /// </summary>
    private static (MethodDecl method, TypeDatabase typeDatabase) CreateMethodWithOptionalBoundGenericClosure()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }),
            TupleTypeSpec.Empty);
        // Optional closures are always escaping in Swift.
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var optionalClosure = new NamedTypeSpec("Swift.Optional", closureType);

        var method = new MethodDecl
        {
            Name = "onResponse",
            MangledName = "$s10TestModule7MyClassC10onResponseyyACyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                CreateArgument("handler", optionalClosure, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parentDecl.Methods.Add(method);
        return (method, typeDatabase);
    }

    /// <summary>
    /// Creates a method with closure (DataResponse&lt;MyData&gt;, Bool) -> Void.
    /// Tests mixed bound-generic + primitive closure args.
    /// </summary>
    private static (MethodDecl method, TypeDatabase typeDatabase, MethodEnvironment env) CreateMethodWithMixedClosureArgs()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Closure: (DataResponse<MyData>, Bool) -> Void
        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] {
                boundGenericArg,
                new NamedTypeSpec("Swift.Bool")
            }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onUpdate", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler");
        var env = new MethodEnvironment(method, typeDatabase);

        return (method, typeDatabase, env);
    }

    /// <summary>
    /// Creates a method with closure (DataResponse&lt;MyData&gt;) -> Bool.
    /// Tests Bool return conversion in Swift wrapper.
    /// </summary>
    private static (MethodDecl method, TypeDatabase typeDatabase, MethodEnvironment env) CreateMethodWithBoolReturnClosure()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Closure: (DataResponse<MyData>) -> Bool
        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }),
            new NamedTypeSpec("Swift.Bool"));
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("shouldContinue", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "predicate");
        var env = new MethodEnvironment(method, typeDatabase);

        return (method, typeDatabase, env);
    }

    /// <summary>
    /// Creates a method with an ObjC-bridged non-closure param + bound generic closure.
    /// Tests that ObjC params use .Handle.
    /// </summary>
    private static (MethodDecl method, TypeDatabase typeDatabase, MethodEnvironment env) CreateMethodWithObjCParam()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Closure: (DataResponse<MyData>) -> Void
        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Method: doWork(presenter: NSError, handler: closure)
        // Using NSError as ObjC-bridged param (it's registered in CreateTypeDatabase)
        var method = CreateMethodDeclWithNonClosureParam("doWork", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler",
            new NamedTypeSpec("Foundation.NSError"), "presenter");
        var env = new MethodEnvironment(method, typeDatabase);

        return (method, typeDatabase, env);
    }

    /// <summary>
    /// Creates a method with a Swift-native class non-closure param + bound generic closure.
    /// Tests that Swift class params use .Payload.DangerousGetHandle().
    /// </summary>
    private static (MethodDecl method, TypeDatabase typeDatabase, MethodEnvironment env) CreateMethodWithSwiftClassParam()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        // Closure: (DataResponse<MyData>) -> Void
        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Method: doWork(other: MyData, handler: closure)
        // MyData is a Swift-native class
        var method = CreateMethodDeclWithNonClosureParam("doWork", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler",
            new NamedTypeSpec("TestModule.MyData"), "other");
        var env = new MethodEnvironment(method, typeDatabase);

        return (method, typeDatabase, env);
    }

    // ─── ObjC-Rooted Parameter Classification ─────────────────────────

    [Fact]
    public void ClassifyParam_ObjCRootedClass_ReturnsObjCHandle()
    {
        var typeDatabase = new TypeDatabase();
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.PaymentApiClient"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "PaymentApiClient"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.PaymentApiClient"),
                MetadataAccessor = "testAccessor",
                Kind = TypeRecordKind.Class,
                Flags = TypeRecordFlags.ObjCRooted | TypeRecordFlags.RequiresMemoryManagement
            });
        typeDatabase.AddModuleDatabase(testModule);

        var arg = new ArgumentDecl
        {
            Name = "client",
            PrivateName = "",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeSpec = new NamedTypeSpec("TestModule.PaymentApiClient"),
        };

        var category = MethodClosureBridge.ClassifyParam(arg, typeDatabase);
        Assert.Equal(MethodClosureBridge.ParamAbiCategory.ObjCHandle, category);
    }

    [Fact]
    public void ClassifyParam_PureSwiftClass_ReturnsPayloadHandle()
    {
        var typeDatabase = CreateTypeDatabase();
        var arg = new ArgumentDecl
        {
            Name = "obj",
            PrivateName = "",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeSpec = new NamedTypeSpec("TestModule.MyClass"),
        };

        var category = MethodClosureBridge.ClassifyParam(arg, typeDatabase);
        Assert.Equal(MethodClosureBridge.ParamAbiCategory.PayloadHandle, category);
    }

    // ─── Struct parent self-reconstruction ─────────────────────────────

    [Fact]
    public void TryEmit_StructParent_EmitsAssumingMemoryBoundForSelf()
    {
        // Struct parents must use assumingMemoryBound(to:).pointee for self-reconstruction,
        // NOT Unmanaged<T> which requires AnyObject (class protocol).
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateStructDecl("MyStruct", moduleDecl);

        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDeclOnTypeDecl("process", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "completion");
        var env = new MethodEnvironment(method, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var result = MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, parentDecl);

        Assert.True(result);
        var swift = swiftOutput.ToString();
        Assert.Contains("assumingMemoryBound(to: TestModule.MyStruct.self).pointee", swift);
        Assert.DoesNotContain("Unmanaged<TestModule.MyStruct>", swift);
    }

    [Fact]
    public void TryEmit_MutatingStructParent_UsesThroughPointerAccess()
    {
        // Mutating value-type methods must use through-pointer access so mutations
        // write back through self_ — NOT a copied `let selfObj = ...pointee`.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateStructDecl("MyStruct", moduleDecl);

        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDeclOnTypeDecl("process", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "completion");
        method.IsMutating = true;
        var env = new MethodEnvironment(method, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var result = MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, parentDecl);

        Assert.True(result);
        var swift = swiftOutput.ToString();
        // Must NOT emit `let selfObj = ...pointee` (immutable copy that loses mutations)
        Assert.DoesNotContain("let selfObj", swift);
        // Call target must use through-pointer access
        Assert.Contains("self_.assumingMemoryBound(to: TestModule.MyStruct.self).pointee.process", swift);
    }

    [Fact]
    public void TryEmit_ClassParent_EmitsUnmanagedForSelf()
    {
        // Class parents must still use Unmanaged<T> for self-reconstruction.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("process", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "completion");
        var env = new MethodEnvironment(method, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var result = MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, parentDecl);

        Assert.True(result);
        var swift = swiftOutput.ToString();
        Assert.Contains("Unmanaged<TestModule.MyClass>.fromOpaque(self_).takeUnretainedValue()", swift);
        Assert.DoesNotContain("assumingMemoryBound(to: TestModule.MyClass.self).pointee", swift);
    }

    [Fact]
    public void TryEmit_StructParent_StaticMethod_NoSelfReconstruction()
    {
        // Static methods on structs should not emit self-reconstruction at all.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateStructDecl("MyStruct", moduleDecl);

        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDeclOnTypeDecl("process", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "completion");
        method.MethodType = MethodType.Static;
        var env = new MethodEnvironment(method, typeDatabase);
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        // The escaping-closure owner-token preamble is module-shared: a real module emits it once
        // ahead of every per-method bridge, and it mentions Unmanaged for reasons unrelated to
        // self. Emit it elsewhere against this context so this writer holds only the per-method
        // output the assertions below are about.
        var ctx = new ModuleEmissionContext();
        ClosureContextHelperEmitter.EmitIfNeeded(new SwiftWriter(new StringWriter()), ctx);

        var result = MethodClosureBridge.TryEmit(csWriter, swiftWriter, env, parentDecl, ctx);

        Assert.True(result);
        var swift = swiftOutput.ToString();
        Assert.DoesNotContain("assumingMemoryBound", swift);
        Assert.DoesNotContain("Unmanaged", swift);
        Assert.DoesNotContain("self_", swift);
    }

    // ─── @MainActor annotation on @_cdecl ──────────────────────────────

    [Fact]
    public void TryEmit_MainActorParentClass_EmitsMainActorAnnotation()
    {
        // @MainActor parent type → @_cdecl wrapper must have @MainActor annotation
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("ActorVM", moduleDecl);
        parentDecl.IsMainActorIsolated = true;

        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onUpdate", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler");
        var env = new MethodEnvironment(method, typeDatabase);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        MethodClosureBridge.TryEmit(new CSharpWriter(new StringWriter()), swiftWriter, env, parentDecl);

        var swift = swiftOutput.ToString();
        Assert.Contains("@MainActor", swift);
        Assert.Contains("@_cdecl(", swift);
    }

    [Fact]
    public void TryEmit_NonActorParentClass_DoesNotEmitMainActorAnnotation()
    {
        // Non-actor parent type → @_cdecl wrapper must NOT have @MainActor
        var (method, typeDatabase) = CreateMethodWithBoundGenericClosure();
        var env = new MethodEnvironment(method, typeDatabase);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        MethodClosureBridge.TryEmit(new CSharpWriter(new StringWriter()), swiftWriter, env, env.ParentDecl as TypeDecl);

        var swift = swiftOutput.ToString();
        Assert.DoesNotContain("@MainActor", swift);
        Assert.Contains("@_cdecl(", swift);
    }

    [Fact]
    public void TryEmit_NonisolatedMethodOnMainActorParent_DoesNotEmitMainActorAnnotation()
    {
        // nonisolated method on @MainActor parent → no @MainActor on wrapper
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("ActorVM", moduleDecl);
        parentDecl.IsMainActorIsolated = true;

        var boundGenericArg = new NamedTypeSpec("TestModule.DataResponse",
            new NamedTypeSpec("TestModule.MyData"));
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)boundGenericArg }), TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl("onUpdate", parentDecl, moduleDecl,
            TupleTypeSpec.Empty, closureType, "handler");
        method.IsNonisolated = true;
        var env = new MethodEnvironment(method, typeDatabase);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        MethodClosureBridge.TryEmit(new CSharpWriter(new StringWriter()), swiftWriter, env, parentDecl);

        var swift = swiftOutput.ToString();
        Assert.DoesNotContain("@MainActor", swift);
    }

    // ─── Type/Declaration Factory Methods ─────────────────────────────

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();

        // Swift module — primitives
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "IntPtr"),
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

        // Foundation module — ObjC-bridged
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

        // TestModule — user types
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
                MetadataAccessor = "$s10TestModule7MyClassCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.DataResponse"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "DataResponse"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DataResponse"),
                MetadataAccessor = "$s10TestModule12DataResponseVMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyData"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyData"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyData"),
                MetadataAccessor = "$s10TestModule6MyDataCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        // Complex enum for closure bridge testing
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyError"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyError"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyError"),
                MetadataAccessor = "$s10TestModule7MyErrorOMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Enum
            });
        // Simple enum — blittable integer, NOT supported in MCB pointer ABI
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Direction"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Direction"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Direction"),
                MetadataAccessor = "$s10TestModule9DirectionOMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(testModule);

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
            MangledName = $"$s10TestModule{name.Length}{name}CN",
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

    private static ClassDecl CreateGenericClassDecl(string name, ModuleDecl moduleDecl, params string[] typeParams)
    {
        var classDecl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = typeParams.Select(tp =>
                new GenericArgumentDecl(tp, tp, new List<GenericParameterConformance>(), new List<GenericParameterConformance>()))
                .ToList(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(classDecl);
        return classDecl;
    }

    /// <summary>
    /// Creates a method with a single closure parameter.
    /// </summary>
    private static MethodDecl CreateMethodDecl(
        string name, ClassDecl parentDecl, ModuleDecl moduleDecl,
        TypeSpec returnType, ClosureTypeSpec closureType, string closureParamName,
        string? mangledName = null)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = mangledName ?? $"$s10TestModule7MyClassC{name.Length}{name}yACyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, returnType, moduleDecl),
                CreateArgument(closureParamName, closureType, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parentDecl.Methods.Add(method);
        return method;
    }

    /// <summary>
    /// Creates a method with a non-closure param + a closure parameter.
    /// </summary>
    private static MethodDecl CreateMethodDeclWithNonClosureParam(
        string name, ClassDecl parentDecl, ModuleDecl moduleDecl,
        TypeSpec returnType, ClosureTypeSpec closureType, string closureParamName,
        TypeSpec nonClosureType, string nonClosureParamName)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule7MyClassC{name.Length}{name}yACyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, returnType, moduleDecl),
                CreateArgument(nonClosureParamName, nonClosureType, moduleDecl),
                CreateArgument(closureParamName, closureType, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parentDecl.Methods.Add(method);
        return method;
    }

    /// <summary>
    /// Creates a method with two closure parameters.
    /// </summary>
    private static MethodDecl CreateMethodDeclWithTwoClosures(
        string name, ClassDecl parentDecl, ModuleDecl moduleDecl,
        TypeSpec returnType,
        ClosureTypeSpec closure1, string closure1Name,
        ClosureTypeSpec closure2, string closure2Name)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule7MyClassC{name.Length}{name}yACyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, returnType, moduleDecl),
                CreateArgument(closure1Name, closure1, moduleDecl),
                CreateArgument(closure2Name, closure2, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
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

    /// <summary>
    /// Creates a type database with extended types for classifier tests:
    /// FrozenPoint (frozen struct), FrozenContainer (frozen + memory), Foundation.URL (native-remapped).
    /// Each module has its own separate database since TypeDatabase rejects duplicate modules.
    /// </summary>
    private static TypeDatabase CreateTypeDatabaseWithExtendedTypes()
    {
        var typeDatabase = new TypeDatabase();

        // Swift module — primitives
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "IntPtr"),
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

        // Foundation module — ObjC-bridged + native-remapped
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
        foundationModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Foundation.URL"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "URL"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.URL"),
                MetadataAccessor = "$s10Foundation3URLVMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
                NativeTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrl")
            });
        typeDatabase.AddModuleDatabase(foundationModule);

        // TestModule — all test types in one module
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
                MetadataAccessor = "$s10TestModule7MyClassCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.DataResponse"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "DataResponse"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DataResponse"),
                MetadataAccessor = "$s10TestModule12DataResponseVMa",
                Flags = TypeRecordFlags.None, // Non-frozen struct
                Kind = TypeRecordKind.Struct
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyData"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyData"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyData"),
                MetadataAccessor = "$s10TestModule6MyDataCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.FrozenPoint"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "FrozenPoint"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.FrozenPoint"),
                MetadataAccessor = "$s10TestModule11FrozenPointVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.FrozenContainer"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "FrozenContainer"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.FrozenContainer"),
                MetadataAccessor = "$s10TestModule15FrozenContainerVMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(testModule);

        return typeDatabase;
    }

    /// <summary>
    /// Creates a type database with Result-related types for eligibility/emission tests:
    /// Swift.Result, TestModule.ImageRequest (non-frozen struct), TestModule.MyError (non-simple enum),
    /// plus all extended types.
    /// </summary>
    private static TypeDatabase CreateTypeDatabaseWithResultTypes()
    {
        var typeDatabase = new TypeDatabase();

        // Swift module — primitives + Result
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "IntPtr"),
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
            SwiftTypeName.FromModuleQualifiedName("Swift.Result"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftResult"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Result"),
                MetadataAccessor = "$ss6ResultOMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        // Foundation module — ObjC-bridged + native-remapped
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
        foundationModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Foundation.URL"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "URL"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.URL"),
                MetadataAccessor = "$s10Foundation3URLVMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
                NativeTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrl")
            });
        typeDatabase.AddModuleDatabase(foundationModule);

        // TestModule — all types including cross-module Result types
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
                MetadataAccessor = "$s10TestModule7MyClassCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.DataResponse"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "DataResponse"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DataResponse"),
                MetadataAccessor = "$s10TestModule12DataResponseVMa",
                Flags = TypeRecordFlags.None, // Non-frozen struct
                Kind = TypeRecordKind.Struct
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyData"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyData"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyData"),
                MetadataAccessor = "$s10TestModule6MyDataCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.FrozenPoint"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "FrozenPoint"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.FrozenPoint"),
                MetadataAccessor = "$s10TestModule11FrozenPointVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.ImageRequest"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ImageRequest"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ImageRequest"),
                MetadataAccessor = "$s10TestModule12ImageRequestVMa",
                Flags = TypeRecordFlags.None, // Non-frozen struct
                Kind = TypeRecordKind.Struct
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyError"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyError"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyError"),
                MetadataAccessor = "$s10TestModule7MyErrorOMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement, // Non-simple enum
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(testModule);

        return typeDatabase;
    }

    private static StructDecl CreateStructDecl(string name, ModuleDecl moduleDecl)
    {
        var structDecl = new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            IsFrozen = false,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(structDecl);
        return structDecl;
    }

    /// <summary>
    /// Creates a method with a single closure parameter on any TypeDecl (class or struct).
    /// </summary>
    private static MethodDecl CreateMethodDeclOnTypeDecl(
        string name, TypeDecl parentDecl, ModuleDecl moduleDecl,
        TypeSpec returnType, ClosureTypeSpec closureType, string closureParamName)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{parentDecl.Name.Length}{parentDecl.Name}V{name.Length}{name}yF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, returnType, moduleDecl),
                CreateArgument(closureParamName, closureType, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parentDecl.Methods.Add(method);
        return method;
    }
}
