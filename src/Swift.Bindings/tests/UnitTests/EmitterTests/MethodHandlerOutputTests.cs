// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

public class MethodHandlerOutputTests
{
    [Fact]
    public void Emit_AsyncMethod_UsesAsyncLibraryAndReturnsTask()
    {
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "/tmp/AsyncWrapper.dylib";

        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethodDecl(
            name: "fetch",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"),
            isAsync: true,
            throws: false,
            methodType: MethodType.Instance);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("[LibraryImport(\"/tmp/AsyncWrapper.dylib\"", csOutput);
        Assert.Contains("public virtual Task<long> FetchAsync(global::System.Threading.CancellationToken cancellationToken = default)", csOutput);
        Assert.Contains("return _tcs.Task;", csOutput);
    }

    [Fact]
    public void Emit_ActorAsyncMethod_ReturnsTaskWithAsyncSuffix()
    {
        // Shell-stub projection for Swift `actor` types: async instance methods
        // emit as `Task<T> XxxAsync(...)` via the same async-wrapper pipeline as
        // plain classes. Executor routing / unownedExecutor hop is a post-ship
        // follow-up — this test pins the surface shape so the stub stays stable.
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "/tmp/AsyncWrapper.dylib";

        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("WorkItem", moduleDecl);
        parentDecl.IsActor = true;

        var runMethod = CreateMethodDecl(
            name: "run",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"),
            isAsync: true,
            throws: true,
            methodType: MethodType.Instance);

        var stopMethod = CreateMethodDecl(
            name: "stop",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: TupleTypeSpec.Empty,
            isAsync: true,
            throws: false,
            methodType: MethodType.Instance);

        var (runCs, _) = EmitMethod(runMethod, typeDatabase);
        var (stopCs, _) = EmitMethod(stopMethod, typeDatabase);

        Assert.Contains("public virtual Task<long> RunAsync(", runCs);
        Assert.Contains("return _tcs.Task;", runCs);

        Assert.Contains("public virtual Task StopAsync(", stopCs);
    }

    [Fact]
    public void Emit_CompletionHandlerOverload_SkippedWhenNativeAsyncCollides()
    {
        // Scenario: A native async method `collect(amount: Int) async -> String` has already
        // been emitted with key "CollectAsync(nint,System.Threading.CancellationToken)".
        // A sync method `collect(amount: Int, completion: (String) -> Void)` should detect
        // the collision and skip emitting its completion handler wrapper.
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "/tmp/AsyncWrapper.dylib";

        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        // Create the sync void method with a trailing completion handler closure
        var closureSpec = new ClosureTypeSpec(
            arguments: new NamedTypeSpec("Swift.String"), // (String) -> Void
            returnType: TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = new MethodDecl
        {
            Name = "collect",
            MangledName = "$s10TestModule6LoaderCcollectSiyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl), // void return
                CreateArgument("amount", new NamedTypeSpec("Swift.Int"), moduleDecl),
                CreateArgument("completion", closureSpec, moduleDecl),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        // Pre-populate with the key that a native async method would have produced.
        // Swift.Int projects to long (System.Int64) via TypeProjectionFactory.
        var emittedSignatures = new HashSet<string>
        {
            "CollectAsync(long,System.Threading.CancellationToken)"
        };

        var (csOutput, _) = EmitMethodWithSignatures(method, typeDatabase, emittedSignatures);

        // The sync method itself should still be emitted
        Assert.Contains("public virtual void Collect(", csOutput);
        // But the completion handler async wrapper should be skipped (collision)
        Assert.DoesNotContain("CollectAsync", csOutput);
    }

    [Fact]
    public void Emit_CompletionHandlerOverload_EmittedWhenNoCollision()
    {
        // Same setup as above but with no pre-existing key — the wrapper should be emitted
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "/tmp/AsyncWrapper.dylib";

        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureSpec = new ClosureTypeSpec(
            arguments: new NamedTypeSpec("Swift.String"),
            returnType: TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = new MethodDecl
        {
            Name = "collect",
            MangledName = "$s10TestModule6LoaderCcollectSiyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                CreateArgument("amount", new NamedTypeSpec("Swift.Int"), moduleDecl),
                CreateArgument("completion", closureSpec, moduleDecl),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        // Empty set — no collision
        var emittedSignatures = new HashSet<string>();

        var (csOutput, _) = EmitMethodWithSignatures(method, typeDatabase, emittedSignatures);

        // Both the sync method and the async wrapper should be emitted
        Assert.Contains("public virtual void Collect(", csOutput);
        Assert.Contains("CollectAsync", csOutput);
    }

    [Fact]
    public void Emit_ThrowingMethod_EmitsSwiftErrorHandling()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethodDecl(
            name: "load",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"),
            isAsync: false,
            throws: true,
            methodType: MethodType.Instance);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("ref SwiftError swiftError", csOutput);
        Assert.Contains("if (swiftError.Value != null)", csOutput);
        // Untyped throws uses SwiftMarshal.ThrowSwiftError (consolidates description read + release + throw)
        Assert.Contains("SwiftMarshal.ThrowSwiftError", csOutput);
        Assert.Contains("SBW_GetErrorDescription", csOutput);
        Assert.Contains("SBW_ReleaseError", csOutput);
    }

    [Fact]
    public void Emit_MethodNameCollidesWithProperty_AppendsMethodSuffix()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethodDecl(
            name: "fetch",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"),
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);

        var siblingProperties = new HashSet<string> { "Fetch" };
        var (csOutput, _) = EmitMethod(method, typeDatabase, siblingProperties);

        Assert.Contains("public virtual long FetchMethod()", csOutput);
    }

    [Fact]
    public void Emit_StaticMethod_DoesNotRequireUnsafe()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethodDecl(
            name: "count",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"),
            isAsync: false,
            throws: false,
            methodType: MethodType.Static);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("public static long GetCount()", csOutput);
        var signatureLine = Array.Find(csOutput.Split('\n'), line => line.Contains("public static long GetCount()", StringComparison.Ordinal));
        Assert.NotNull(signatureLine);
        Assert.DoesNotContain("unsafe", signatureLine!, StringComparison.Ordinal);
    }

    [Fact]
    public void Emit_GenericMethod_WithProtocolConstraint_EmitsWhereClause()
    {
        var typeDatabase = CreateTypeDatabase();
        RegisterProtocol(typeDatabase, "TestModule.Loadable", TypeRecordFlags.None);

        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethodDecl(
            name: "decode",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"),
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance,
            genericParameters: new List<GenericArgumentDecl>
            {
                CreateGenericArgumentWithProtocolConformance("T", "TestModule.Loadable")
            });

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("public virtual long Decode<T>()", csOutput);
        Assert.Contains("where T : ISwiftObject, ILoadable", csOutput);
    }

    [Fact]
    public void Emit_GenericMethod_WithAssociatedTypeProtocolConstraint_SkipsEmission()
    {
        // This gate is now in MemberValidationPipeline (Gate 4).
        var typeDatabase = CreateTypeDatabase();
        RegisterProtocol(typeDatabase, "TestModule.SequenceLike", TypeRecordFlags.HasAssociatedTypes);

        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethodDecl(
            name: "decode",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"),
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance,
            genericParameters: new List<GenericArgumentDecl>
            {
                CreateGenericArgumentWithProtocolConformance("T", "TestModule.SequenceLike")
            });

        var pipeline = new MemberValidationPipeline(typeDatabase);
        var result = pipeline.ValidateMethodEmission(method, null);

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.GenericProtocolConstraint, result.Reason);
    }

    [Fact]
    public void Emit_ModuleLevelMethod_IsAlwaysStatic()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var method = new MethodDecl
        {
            Name = "version",
            MangledName = "$s10TestModule7versionSiyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, new NamedTypeSpec("Swift.Int"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("public static long GetVersion()", csOutput);
    }

    [Fact]
    public void Emit_MethodWithUnknownParameterType_SkipsEmission()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethodDecl(
            name: "decode",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"),
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);

        method.CSSignature.Add(CreateArgument("unknown", new NamedTypeSpec("Missing.Type"), moduleDecl));

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        // No binding code emitted — only unsupported comment
        Assert.DoesNotContain("public", csOutput);
        Assert.Contains("// Unsupported:", csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void Emit_MethodWithUnsupportedClosureFallback_EmitsUnsupportedSwiftTypeAttribute()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);
        var unsupportedClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new NamedTypeSpec("T")),
            TupleTypeSpec.Empty);
        var method = CreateMethodDecl(
            name: "boxedHandler",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("TestModule.Box", unsupportedClosure),
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("[global::Swift.UnsupportedSwiftType(\"Unsupported closure fallback\",", csOutput);
        Assert.Contains("public virtual TestModule.Box<object> GetBoxedHandler()", csOutput);
    }

    [Fact]
    public void Emit_MethodWithNonFrozenEnumParameter_UsesIntPtrInPInvokeSignature()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethodDecl(
            name: "setDenominator",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: TupleTypeSpec.Empty,
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("denominator", new NamedTypeSpec("TestModule.ColorFormatDenominator"), moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("IntPtr denominator", csOutput);
        Assert.Contains("denominator.Payload.DangerousGetHandle()", csOutput);
    }

    [Fact]
    public void Emit_MethodWithFrozenEnumParameter_UsesIntPtrInPInvokeSignature()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethodDecl(
            name: "setVariant",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: TupleTypeSpec.Empty,
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("variant", new NamedTypeSpec("TestModule.Variant"), moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // The P/Invoke (partial) declaration should use IntPtr for the frozen enum
        var partialLine = Array.Find(csOutput.Split('\n'), line => line.Contains("partial", StringComparison.Ordinal) && line.Contains("PInvoke_", StringComparison.Ordinal));
        Assert.NotNull(partialLine);
        Assert.Contains("IntPtr variant", partialLine!);
        Assert.DoesNotContain("TestModule.Variant", partialLine!);
        // Wrapper should extract handle from payload
        Assert.Contains("variant.Payload.DangerousGetHandle()", csOutput);
    }

    [Fact]
    public void Emit_AsyncMethodWithFrozenEnumParameter_SynthesizesHandleVariable()
    {
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "/tmp/AsyncWrapper.dylib";
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethodDecl(
            name: "fetchWithVariant",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"),
            isAsync: true,
            throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("variant", new NamedTypeSpec("TestModule.Variant"), moduleDecl));

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        // P/Invoke should use IntPtr for the frozen enum param
        // Find the method's P/Invoke line
        var partialLine = Array.Find(csOutput.Split('\n'), line => line.Contains("partial", StringComparison.Ordinal) && line.Contains("variant", StringComparison.Ordinal));
        Assert.NotNull(partialLine);
        Assert.Contains("IntPtr variant", partialLine!);
        // C# wrapper must synthesize variantHandle via InitializeWithCopy (copy-buffer pattern)
        Assert.Contains("variantHandle", csOutput);
        Assert.Contains("variantCopyBuffer", csOutput);
        Assert.Contains("variant.Payload.DangerousGetHandle()", csOutput);
        // Swift wrapper should receive as UnsafeRawPointer and read via .pointee
        Assert.Contains("variant: UnsafeRawPointer", swiftOutput);
        Assert.Contains("variantValue", swiftOutput);
    }

    [Fact]
    public void Emit_AsyncMethodWithPrivateName_UsesNormalizedNameInBody()
    {
        // Regression test: async body used ABI p.Name (e.g. "_for")
        // instead of normalized NameProvider.GetCSharpParameterName (e.g. "request")
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "/tmp/AsyncWrapper.dylib";
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethodDecl(
            name: "fetchWithVariant",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"),
            isAsync: true,
            throws: false,
            methodType: MethodType.Instance);
        // Add param with ABI Name="_for" and PrivateName="request"
        method.CSSignature.Add(CreateArgument("_for", new NamedTypeSpec("TestModule.Variant"), moduleDecl, privateName: "request"));

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        // C# body must use normalized name "request" (from PrivateName), not ABI "_for"
        Assert.Contains("requestMetadata", csOutput);
        Assert.Contains("requestCopyBuffer", csOutput);
        Assert.Contains("requestHandle", csOutput);
        Assert.Contains("requestCopyBufferWrapper", csOutput);
        Assert.DoesNotContain("_forMetadata", csOutput);
        Assert.DoesNotContain("_forCopyBuffer", csOutput);

        // Method signature should use normalized name
        Assert.Contains("request", csOutput);

        // Swift wrapper should use ABI name (_for) — this is correct
        Assert.Contains("_for", swiftOutput);
    }

    [Fact]
    public void Emit_MethodWithEscapingClosureReturningFrozenStruct_UsesIndirectReturnCallback()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        // Frozen struct return uses indirect return (void*) because @convention(c) can't
        // return Swift struct types — even frozen ones.
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Double") }),
            new NamedTypeSpec("TestModule.Box"));
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl(
            name: "handle",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: TupleTypeSpec.Empty,
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Frozen struct return → indirect return via void* (not direct struct return)
        Assert.DoesNotContain("TestModule.Box handle_callback_", csOutput);
        // Callback uses void* return with MarshalToSwift for indirect return
        Assert.Contains("void*", csOutput);
    }

    [Fact]
    public void Emit_MethodWithEscapingClosureReturningFrozenStructMappedToScalar_UsesIndirectReturn()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        // Even scalar-mapped frozen structs (TestModule.Scalar → double) use indirect
        // return because CanUseDirectCallbackReturn only allows Swift primitive names.
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Double") }),
            new NamedTypeSpec("TestModule.Scalar"));
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl(
            name: "sample",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: TupleTypeSpec.Empty,
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Frozen struct mapped to scalar → indirect return (not direct double return)
        Assert.DoesNotContain("double sample_callback_", csOutput);
        Assert.Contains("void*", csOutput);
    }

    [Fact]
    public void Emit_MethodWithEscapingClosureReturningNonFrozenStruct_UsesIndirectReturnCallback()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Double") }),
            new NamedTypeSpec("TestModule.VectorAnimationColor"));
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl(
            name: "tint",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: TupleTypeSpec.Empty,
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("private static unsafe void tint_callback_", csOutput);
        // Closure returns non-frozen struct → now Cdecl-compatible via indirect return marshalling
        Assert.Contains("(void* indirectResult, double arg0, IntPtr contextPtr)", csOutput);
        Assert.Contains("delegate* unmanaged[Cdecl]<void*, double, IntPtr, void>", csOutput);
        Assert.DoesNotContain("private static unsafe void* tint_callback_", csOutput);
    }

    [Fact]
    public void Emit_MethodWithSupportedExistentialBoundGeneric_EmitsSuccessfully()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);
        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });
        var method = CreateMethodDecl(
            name: "existentialBox",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("TestModule.Box", existentialArg),
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        // Existentials now resolve to AnyType, causing bound generics with existential
        // args to be skipped (AnyType as generic arg is not supported)
        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void Emit_MethodWithUnsatisfiedBoundGenericConstraint_SkipsEmission()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var constrainedStorageDecl = CreateGenericStructDecl(
            "ValueProviderStorage",
            moduleDecl,
            "T",
            "TestModule.AnyInterpolatable");
        CreateStructDecl("VectorAnimationVector3D", moduleDecl);

        var method = CreateMethodDecl(
            name: "storage",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec(
                constrainedStorageDecl.SwiftTypeName.ModuleQualifiedName,
                new NamedTypeSpec("TestModule.VectorAnimationVector3D")),
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        // No binding code emitted — only unsupported comment
        Assert.DoesNotContain("public", csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void Emit_MethodWithExternalTypeUnsatisfiedBoundGenericConstraint_SkipsEmission()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var constrainedStorageDecl = CreateGenericStructDecl(
            "ValueProviderStorage",
            moduleDecl,
            "T",
            "TestModule.AnyInterpolatable");

        var method = CreateMethodDecl(
            name: "storageExternal",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec(
                constrainedStorageDecl.SwiftTypeName.ModuleQualifiedName,
                new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Double"))),
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        // No binding code emitted — only unsupported comment
        Assert.DoesNotContain("public", csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void Emit_MethodReturningTupleWithBoundGeneric_EmitsPerElementMarshalling()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        // Create tuple return type: (Swift.Array<Swift.Int>, Swift.Bool)
        var arrayOfInt = new NamedTypeSpec("Swift.Array");
        arrayOfInt.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var boolType = new NamedTypeSpec("Swift.Bool");
        var tupleType = new TupleTypeSpec(new List<TypeSpec> { arrayOfInt, boolType });

        var method = CreateMethodDecl(
            name: "encrypt",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: tupleType,
            isAsync: false,
            throws: false,
            methodType: MethodType.Static);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // P/Invoke should use IntPtr for bound generic, not void*
        Assert.DoesNotContain("void*", csOutput);
        Assert.Contains("ValueTuple<IntPtr, bool>", csOutput);
        // Return should use per-element marshalling, not raw `return result;`
        Assert.Contains("MarshalFromSwift", csOutput);
        Assert.Contains("result.Item1", csOutput);
        Assert.Contains("return (elem0, elem1);", csOutput);
    }

    [Fact]
    public void Emit_MethodReturningTupleWithOptionalNonObjC_UsesDirectIntPtrMarshalling()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        // Create tuple return type: (Optional<Swift.Int>, Swift.Bool)
        var optionalInt = new NamedTypeSpec("Swift.Optional");
        optionalInt.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var boolType = new NamedTypeSpec("Swift.Bool");
        var tupleType = new TupleTypeSpec(new List<TypeSpec> { optionalInt, boolType });

        var method = CreateMethodDecl(
            name: "decrypt",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: tupleType,
            isAsync: false,
            throws: false,
            methodType: MethodType.Static);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // P/Invoke should use IntPtr for optional non-ObjC, not void*
        Assert.DoesNotContain("void*", csOutput);
        Assert.Contains("ValueTuple<IntPtr, bool>", csOutput);
        // Marshal code should use result.Item1 directly (no &result.Item1)
        Assert.Contains("MarshalFromSwift", csOutput);
        Assert.Contains("(result.Item1)", csOutput);
        Assert.DoesNotContain("(&result.Item1)", csOutput);
    }

    [Fact]
    public void Emit_MethodReturningTupleOfPrimitives_ReturnsTupleDirectly()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        // Create tuple return type: (Swift.Int, Swift.Bool)
        var tupleType = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool")
        });

        var method = CreateMethodDecl(
            name: "status",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: tupleType,
            isAsync: false,
            throws: false,
            methodType: MethodType.Static);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // All-primitive tuple should use direct return (no per-element marshalling)
        Assert.Contains("return result;", csOutput);
        Assert.DoesNotContain("MarshalFromSwift", csOutput);
    }

    #region Tuple String Idiomatic Conversion

    [Fact]
    public void Emit_TupleReturnWithBareString_ConvertsToIdiomaticString()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        // Tuple return: (Swift.String, Swift.Int)
        var tupleType = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Swift.Int")
        });

        var method = CreateMethodDecl(
            name: "getName",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: tupleType,
            isAsync: false,
            throws: false,
            methodType: MethodType.Static);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Wrapper return type should use idiomatic 'string', not 'SwiftString'
        Assert.Contains("(string, long)", csOutput);
        // P/Invoke should still use SwiftString.Buffer (ABI type)
        Assert.Contains("Swift.SwiftString.Buffer", csOutput);
        // Marshalling should include .ToString() for the string element
        Assert.Contains(".ToString()", csOutput);
    }

    [Fact]
    public void Emit_TupleReturnWithBareString_PInvokeUsesBuffer()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        // Tuple return: (Swift.Int, Swift.String)
        var tupleType = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.String")
        });

        var method = CreateMethodDecl(
            name: "getInfo",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: tupleType,
            isAsync: false,
            throws: false,
            methodType: MethodType.Static);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // P/Invoke return uses ValueTuple with SwiftString.Buffer (fully qualified)
        Assert.Contains("ValueTuple<long, Swift.SwiftString.Buffer>", csOutput);
        // Wrapper signature uses idiomatic string
        Assert.Contains("(long, string)", csOutput);
    }

    [Fact]
    public void Emit_LabeledTupleReturnWithString_PreservesLabelsAndConvertsString()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        // Labeled tuple return: (name: Swift.String, count: Swift.Int)
        var nameElement = new NamedTypeSpec("Swift.String") { TypeLabel = "name" };
        var countElement = new NamedTypeSpec("Swift.Int") { TypeLabel = "count" };
        var tupleType = new TupleTypeSpec(new List<TypeSpec> { nameElement, countElement });

        var method = CreateMethodDecl(
            name: "getSummary",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: tupleType,
            isAsync: false,
            throws: false,
            methodType: MethodType.Static);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Labels preserved, String converted to string
        Assert.Contains("(string name, long count)", csOutput);
    }

    [Fact]
    public void Emit_TupleReturnWithOptionalString_ConvertsToIdiomaticType()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        // Tuple return: (Optional<Swift.String>, Swift.Int)
        var optionalString = new NamedTypeSpec("Swift.Optional");
        optionalString.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        var tupleType = new TupleTypeSpec(new List<TypeSpec>
        {
            optionalString,
            new NamedTypeSpec("Swift.Int")
        });

        var method = CreateMethodDecl(
            name: "getOptName",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: tupleType,
            isAsync: false,
            throws: false,
            methodType: MethodType.Static);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Optional<String> is now projected to string? via factory in the public signature
        Assert.Contains("(string?, long)", csOutput);
        // Raw types may still appear in marshalling body (MarshalFromSwift calls) — that's correct
    }

    [Fact]
    public void Emit_TupleReturnWithArrayString_ConvertsToIdiomaticType()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        // Tuple return: (Array<Swift.String>, Swift.Int)
        var arrayString = new NamedTypeSpec("Swift.Array");
        arrayString.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        var tupleType = new TupleTypeSpec(new List<TypeSpec>
        {
            arrayString,
            new NamedTypeSpec("Swift.Int")
        });

        var method = CreateMethodDecl(
            name: "getNames",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: tupleType,
            isAsync: false,
            throws: false,
            methodType: MethodType.Static);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Array<String> is now projected to IReadOnlyList<string> via factory in the public signature
        Assert.Contains("IReadOnlyList<string>", csOutput);
        // Raw types may still appear in marshalling body (MarshalFromSwift calls) — that's correct
    }

    [Fact]
    public void Emit_MixedTupleReturnWithBareAndOptionalString_ConvertsBoth()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        // Tuple return: (Swift.String, Optional<Swift.String>, Swift.Int)
        var optionalString = new NamedTypeSpec("Swift.Optional");
        optionalString.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        var tupleType = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.String"),
            optionalString,
            new NamedTypeSpec("Swift.Int")
        });

        var method = CreateMethodDecl(
            name: "getMixed",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: tupleType,
            isAsync: false,
            throws: false,
            methodType: MethodType.Static);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Both bare String and Optional<String> now use idiomatic types in the public signature
        Assert.Contains("(string, string?, long)", csOutput);
        // Raw types may still appear in marshalling body (MarshalFromSwift calls) — that's correct
    }

    [Fact]
    public void Emit_TupleReturnWithOptionalObjC_UsesIntPtrZeroCheck()
    {
        // P1 regression test: Optional<ObjC> tuple elements use bare IntPtr (null = IntPtr.Zero),
        // NOT SwiftOptional buffer layout. The factory projection must be skipped for these types.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        // Tuple return: (Optional<UIKit.UIImage>, Swift.Int)
        var optionalObjC = new NamedTypeSpec("Swift.Optional");
        optionalObjC.GenericParameters.Add(new NamedTypeSpec("UIKit.UIImage"));
        var tupleType = new TupleTypeSpec(new List<TypeSpec>
        {
            optionalObjC,
            new NamedTypeSpec("Swift.Int")
        });

        var method = CreateMethodDecl(
            name: "getImage",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: tupleType,
            isAsync: false,
            throws: false,
            methodType: MethodType.Static);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Optional<ObjC> should use IntPtr.Zero check, NOT MarshalFromSwift<SwiftOptional<IntPtr>>
        Assert.Contains("IntPtr.Zero", csOutput);
        Assert.Contains("GetNSObject", csOutput);
        Assert.DoesNotContain("MarshalFromSwift<SwiftOptional<IntPtr>>", csOutput);
    }

    #endregion

    [Fact]
    public void Emit_MethodReturningAny_EmitsUnboxWithoutProxyWrapping()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        // Return type is 'Any' — a zero-protocol existential (ProtocolListTypeSpec with 0 protocols)
        var method = CreateMethodDecl(
            name: "getValue",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new ProtocolListTypeSpec(),
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Bare Any uses ExistentialContainer0.Unbox() to convert the container to a C# object.
        // No proxy wrapping — bare Any has no protocols, so no proxy class.
        Assert.Contains("ExistentialContainer0.Unbox(result)", csOutput);
        Assert.DoesNotContain("Proxy(result)", csOutput);
    }

    #region Generic Type Callback Guards (WU4)

    [Fact]
    public void MethodHandler_ThunkClosureInGenericType_SkipsEmission()
    {
        // This gate is now in MemberValidationPipeline (Gate 3).
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        // Create a closure that requires thunk (escaping, non-@convention(c))
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl(
            name: "handle",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: TupleTypeSpec.Empty,
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var pipeline = new MemberValidationPipeline(typeDatabase);
        var pinvokeCtx = new PInvokeHelperContext("GenericBox", new[] { "T0" });
        var validationCtx = new ValidationContext(typeDatabase, pinvokeCtx, new ModuleEmissionContext(), null, null, null, null);
        var result = pipeline.ValidateMethodEmission(method, validationCtx);

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.GenericTypeCallback, result.Reason);
    }

    [Fact]
    public void MethodHandler_ConventionCClosureInGenericType_EmitsNormally()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        // Create a @convention(c) closure — RequiresThunk returns false
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));
        var conventionAttr = new TypeSpecAttribute("convention");
        conventionAttr.Parameters.Add("c");
        closureType.Attributes.Add(conventionAttr);

        var method = CreateMethodDecl(
            name: "handleCCallback",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: TupleTypeSpec.Empty,
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (csOutput, _) = EmitMethodInGenericContext(method, typeDatabase);

        // @convention(c) closures don't need thunks — emission should proceed
        Assert.NotEqual(string.Empty, csOutput);
    }

    [Fact]
    public void MethodHandler_AsyncMethodInGenericType_EmitsWithHoistedCallbacks()
    {
        // Pure async methods (no closure params) in generic types now emit —
        // callbacks are hoisted to the non-generic helper class by EmitAsyncWrapper.
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "/tmp/AsyncWrapper.dylib";
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var method = CreateMethodDecl(
            name: "fetch",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"),
            isAsync: true,
            throws: false,
            methodType: MethodType.Instance);

        var pipeline = new MemberValidationPipeline(typeDatabase);
        var pinvokeCtx = new PInvokeHelperContext("GenericBox", new[] { "T0" });
        var validationCtx = new ValidationContext(typeDatabase, pinvokeCtx, new ModuleEmissionContext(), null, null, null, null);
        var result = pipeline.ValidateMethodEmission(method, validationCtx);

        Assert.True(result.ShouldEmit);
    }

    [Fact]
    public void MethodHandler_NoClosureInGenericType_EmitsNormally()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var method = CreateMethodDecl(
            name: "count",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"),
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);

        var (csOutput, _) = EmitMethodInGenericContext(method, typeDatabase);

        // Method without closures/async should emit normally (DllImport hoisting handles it)
        Assert.NotEqual(string.Empty, csOutput);
        Assert.Contains("GetCount()", csOutput);
    }

    [Fact]
    public void ConstructorHandler_ThunkClosureInGenericType_SkipsEmission()
    {
        // This gate is now in MemberValidationPipeline (Gate 3).
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var ctor = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule6LoaderCyACSi_tYaKcfc",
            MethodType = MethodType.Instance,
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
        parentDecl.Methods.Add(ctor);

        var pipeline = new MemberValidationPipeline(typeDatabase);
        var pinvokeCtx = new PInvokeHelperContext("GenericBox", new[] { "T0" });
        var validationCtx = new ValidationContext(typeDatabase, pinvokeCtx, new ModuleEmissionContext(), null, null, null, null);
        var result = pipeline.ValidateMethodEmission(ctor, validationCtx);

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.GenericTypeCallback, result.Reason);
    }

    [Fact]
    public void Emit_StaticMethodWithOptionalClosureDefault_SkippedByBypass()
    {
        // Static method with Optional<Closure>+default: no bridge handles it (bridges need
        // specific closure patterns), and ExistentialBypassEmitter rejects static methods.
        // Verifies the fallback-skip fires and produces no output — the bypass doesn't
        // preempt bridges that could theoretically handle the method.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MPIMapView", moduleDecl);

        // Build an unsupported Optional<Closure> with default
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("IndoorMapsSdk.MPIError") }),
            TupleTypeSpec.Empty);
        var optionalClosure = new NamedTypeSpec("Swift.Optional");
        optionalClosure.GenericParameters.Add(closureType);

        var method = new MethodDecl
        {
            Name = "configure",
            MangledName = "$s10TestModule10MPIMapViewCconfigureSiyF",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl), // void return
                CreateArgument("errorCallback", optionalClosure, moduleDecl),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        method.CSSignature[1].HasDefaultArg = true;
        parentDecl.Methods.Add(method);

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        // Static method: bypass rejects → fallback skip → no output
        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void Emit_InstanceMethodWithOptionalClosureDefault_BypassSucceeds()
    {
        // Instance void method with Optional<Closure>+default: bypass should succeed,
        // emitting a Swift wrapper that omits the closure param and C# that calls it.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MPIMapView", moduleDecl);

        // Register parent type so bypass can resolve it
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: SwiftTypeName.FromModuleQualifiedName("TestModule.MPIMapView"), record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MPIMapView"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MPIMapView"),
                MetadataAccessor = "$s10TestModule10MPIMapViewCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            })
        });

        // Build an unsupported Optional<Closure> with default
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("IndoorMapsSdk.MPIError") }),
            TupleTypeSpec.Empty);
        var optionalClosure = new NamedTypeSpec("Swift.Optional");
        optionalClosure.GenericParameters.Add(closureType);

        var method = new MethodDecl
        {
            Name = "loadVenue",
            MangledName = "$s10TestModule10MPIMapViewCloadVenueSiyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl), // void return
                CreateArgument("options", new NamedTypeSpec("Swift.Int"), moduleDecl),
                CreateArgument("errorCallback", optionalClosure, moduleDecl),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        method.CSSignature[2].HasDefaultArg = true;
        parentDecl.Methods.Add(method);

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        // Bypass succeeds: C# method emitted (no errorCallback param), Swift wrapper emitted
        Assert.Contains("LoadVenue", csOutput);
        Assert.DoesNotContain("errorCallback", csOutput);
        Assert.Contains("@_silgen_name", swiftOutput);
        Assert.Contains("loadVenue", swiftOutput);
    }

    [Fact]
    public void WorkaroundRecommendations_GenericTypeCallback_ReturnsRecommendation()
    {
        var recommendation = WorkaroundRecommendations.GetRecommendation(SkipReason.GenericTypeCallback);

        Assert.NotNull(recommendation);
        Assert.NotEmpty(recommendation);
    }

    private static (string csOutput, string swiftOutput) EmitMethodInGenericContext(
        MethodDecl methodDecl,
        TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var pinvokeCtx = new PInvokeHelperContext("GenericBox", new[] { "T0" });
        var env = new MethodEnvironment(methodDecl, typeDatabase, pinvokeHelperContext: pinvokeCtx);
        var conductor = new Conductor(new NullLoggerFactory());
        var context = new TypeHandlerContext(pinvokeCtx, new(), null);
        handler.Emit(csWriter, swiftWriter, env, conductor, context);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    private static (string csOutput, string swiftOutput) EmitConstructorInGenericContext(
        MethodDecl methodDecl,
        TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new ConstructorHandler(new NullLogger<ConstructorHandler>(), new HashSet<string>());
        var pinvokeCtx = new PInvokeHelperContext("GenericBox", new[] { "T0" });
        var env = new MethodEnvironment(methodDecl, typeDatabase, pinvokeHelperContext: pinvokeCtx);
        var conductor = new Conductor(new NullLoggerFactory());
        var context = new TypeHandlerContext(pinvokeCtx, new(), null);
        handler.Emit(csWriter, swiftWriter, env, conductor, context);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    #endregion

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
            SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftArray"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
                MetadataAccessor = "$sSaMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
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
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Box"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Box"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Box"),
                MetadataAccessor = "$s10TestModule3BoxVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Scalar"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Double"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Scalar"),
                MetadataAccessor = "$s10TestModule6ScalarVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.VectorAnimationColor"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "VectorAnimationColor"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.VectorAnimationColor"),
                MetadataAccessor = "$s10TestModule20VectorAnimationColorVMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            });
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.ColorFormatDenominator"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ColorFormatDenominator"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ColorFormatDenominator"),
                MetadataAccessor = "$s10TestModule22ColorFormatDenominatorOMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Enum
            });
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Variant"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Variant"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Variant"),
                MetadataAccessor = "$s10TestModule7VariantOMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(module);

        // UIKit module with ObjC bridged type for Optional<ObjC> tests
        var uikitModule = new ModuleTypeDatabase("UIKit", "/System/Library/Frameworks/UIKit.framework/UIKit");
        uikitModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("UIKit.UIImage"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("UIKit", "UIImage"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIImage"),
                MetadataAccessor = "$sSo7UIImageCMa",
                Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(uikitModule);

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

    private static StructDecl CreateStructDecl(string name, ModuleDecl moduleDecl)
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
            MetadataAccessor = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}VMa",
        };
        moduleDecl.Types.Add(structDecl);
        return structDecl;
    }

    private static StructDecl CreateGenericStructDecl(string name, ModuleDecl moduleDecl, string typeParameterName, string constraintProtocolName)
    {
        var structDecl = CreateStructDecl(name, moduleDecl);
        structDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new(
                TypeName: $"τ_0_0",
                SugaredTypeName: typeParameterName,
                GenericConformances: new List<GenericParameterConformance>
                {
                    new(
                        Path: new[] { $"τ_0_0" },
                        ConformanceTarget: SwiftTypeName.FromModuleQualifiedName(constraintProtocolName),
                        Kind: ConformanceKind.Protocol)
                },
                AssosiatedTypeConformances: new List<GenericParameterConformance>())
        };
        return structDecl;
    }

    private static MethodDecl CreateMethodDecl(
        string name,
        ClassDecl parentDecl,
        ModuleDecl moduleDecl,
        TypeSpec returnType,
        bool isAsync,
        bool throws,
        MethodType methodType,
        List<GenericArgumentDecl>? genericParameters = null)
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
            GenericParameters = genericParameters ?? new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = throws,
            IsAsync = isAsync,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);
        return method;
    }

    private static GenericArgumentDecl CreateGenericArgumentWithProtocolConformance(string typeName, string protocolName)
    {
        return new GenericArgumentDecl(
            TypeName: typeName,
            SugaredTypeName: typeName,
            GenericConformances: new List<GenericParameterConformance>
            {
                new GenericParameterConformance(
                    Path: new[] { typeName },
                    ConformanceTarget: SwiftTypeName.FromModuleQualifiedName(protocolName),
                    Kind: ConformanceKind.Protocol)
            },
            AssosiatedTypeConformances: new List<GenericParameterConformance>());
    }

    private static void RegisterProtocol(TypeDatabase typeDatabase, string protocolName, TypeRecordFlags flags)
    {
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: SwiftTypeName.FromModuleQualifiedName(protocolName), record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", protocolName.Split('.')[1]),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(protocolName),
                MetadataAccessor = "$s10TestModule8ProtocolPAAWP",
                Flags = flags,
                Kind = TypeRecordKind.Protocol
            })
        });
    }

    private static ArgumentDecl CreateArgument(string name, TypeSpec typeSpec, ModuleDecl moduleDecl, string? privateName = null)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = name,
            PrivateName = privateName ?? string.Empty,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    [Fact]
    public void Emit_MethodWithOptionalClosure_FreesGCHandleOnlyWhenOwnershipNotTransferred()
    {
        // Optional closures are always escaping in Swift. The cleanup path used to
        // free unconditionally on the inner ClosureTypeSpec.IsEscaping check, which
        // was wrong for the stored-handler shape. After the closure-
        // context owner-token fix, Swift's `_SBClosureCtx` ARC box owns the GCHandle
        // once the wrapper body runs, so the C# `finally` must skip its own free in
        // the steady-state path. The remaining free is gated on a transfer flag —
        // if the P/Invoke never reached the wrapper body (C# threw between Alloc and
        // call, or the entry point could not be resolved), the flag stays false and
        // we close the leak window by freeing here.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var innerClosure = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            TupleTypeSpec.Empty);
        Assert.False(innerClosure.IsEscaping);
        var optionalClosure = new NamedTypeSpec("Swift.Optional", innerClosure);

        var method = CreateMethodDecl(
            name: "handle",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: TupleTypeSpec.Empty,
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("onComplete", optionalClosure, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("GCHandle.Alloc", csOutput);
        Assert.Contains("bool onCompleteTransferred = false;", csOutput);
        Assert.Contains("onCompleteTransferred = true;", csOutput);
        Assert.Contains("if (!onCompleteTransferred && onCompleteHandle.IsAllocated) onCompleteHandle.Free();", csOutput);
    }

    [Fact]
    public void Emit_MethodWithOptionalConventionCClosure_SkipsThreadStaticAndUsesMarshalGetFunctionPointer()
    {
        // Optional @convention(c) closures are effectively escaping — Swift may store and invoke
        // the function pointer later on any thread. ThreadStatic is unsound for this case, so
        // optional convention-c closures use Marshal.GetFunctionPointerForDelegate (escaping path).
        // Non-optional convention-c closures use [UnmanagedCallersOnly] + [ThreadStatic] instead.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        // Create Optional<@convention(c) (Int) -> Void>
        var innerClosure = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            TupleTypeSpec.Empty);
        // Mark as @convention(c) — this is what IsConventionC checks via attribute
        var conventionAttr = new TypeSpecAttribute("convention");
        conventionAttr.Parameters.Add("c");
        innerClosure.Attributes.Add(conventionAttr);
        Assert.False(innerClosure.IsEscaping, "Inner closure is not marked @escaping");
        var optionalClosure = new NamedTypeSpec("Swift.Optional", innerClosure);

        var method = CreateMethodDecl(
            name: "observe",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: TupleTypeSpec.Empty,
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", optionalClosure, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Optional convention-c: no ThreadStatic (escaping → unsafe for ThreadStatic)
        Assert.DoesNotContain("[ThreadStatic]", csOutput);

        // Optional convention-c: uses Marshal.GetFunctionPointerForDelegate (escaping path)
        Assert.Contains("Marshal.GetFunctionPointerForDelegate", csOutput);

        // Cleanup should NOT free the GCHandle
        Assert.DoesNotContain(".Free()", csOutput);
    }

    private static (string csOutput, string swiftOutput) EmitMethod(
        MethodDecl methodDecl,
        TypeDatabase typeDatabase,
        IReadOnlySet<string>? siblingPropertyNames = null)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = new MethodEnvironment(methodDecl, typeDatabase, siblingPropertyNames);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    private static (string csOutput, string swiftOutput) EmitMethodWithSignatures(
        MethodDecl methodDecl,
        TypeDatabase typeDatabase,
        HashSet<string> emittedProjectedSignatures)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = new MethodEnvironment(methodDecl, typeDatabase);
        env.EmittedProjectedSignatures = emittedProjectedSignatures;
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return (csOutput.ToString(), swiftOutput.ToString());
    }
}
