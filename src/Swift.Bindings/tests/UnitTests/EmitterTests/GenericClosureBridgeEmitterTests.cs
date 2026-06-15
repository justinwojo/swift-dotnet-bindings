// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for GenericClosureBridgeEmitter — monomorphized Swift wrapper bridges
/// for methods with generic closure parameters.
/// </summary>
public class GenericClosureBridgeEmitterTests
{
    #region TryEmit gate: constructor returns false

    [Fact]
    public void TryEmit_Constructor_ReturnsFalse()
    {
        var (csWriter, swiftWriter) = CreateWriters();
        var method = CreateMethodDecl("init", isConstructor: true);
        var env = CreateMethodEnvironment(method);

        var result = GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, null);

        Assert.False(result);
    }

    #endregion

    #region TryEmit gate: method already using wrapper library returns false

    [Fact]
    public void TryEmit_AlreadyUsesWrapperLib_ReturnsFalse()
    {
        var (csWriter, swiftWriter) = CreateWriters();
        var method = CreateMethodDecl("doWork");
        method.UsesWrapperLibrary = true;
        var env = CreateMethodEnvironment(method);

        var result = GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, null);

        Assert.False(result);
    }

    #endregion

    #region TryEmit gate: no generic closure returns false

    [Fact]
    public void TryEmit_NoClosureParam_ReturnsFalse()
    {
        var (csWriter, swiftWriter) = CreateWriters();
        var method = CreateMethodDecl("doWork");
        var env = CreateMethodEnvironment(method);

        var result = GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, null);

        Assert.False(result);
    }

    #endregion

    #region AreNonClosureParamsCompatible

    [Fact]
    public void AreNonClosureParamsCompatible_NoOtherParams_ReturnsTrue()
    {
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var closureArg = CreateArg("block", new ClosureTypeSpec(
            new NamedTypeSpec("τ_0_0"), new NamedTypeSpec("τ_0_0")), moduleDecl);

        var method = new MethodDecl
        {
            Name = "read",
            MangledName = "$s11RecordStore8Database4readyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                closureArg
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "T", new(), new())
            },
            ParentDecl = CreateClassDecl("Database"),
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };

        var result = GenericClosureBridgeEmitter.AreNonClosureParamsCompatible(
            method, closureArg, typeDatabase);

        Assert.True(result);
    }

    [Fact]
    public void AreNonClosureParamsCompatible_WithNonClosureParam_ReturnsFalse()
    {
        // IsIntPtrCompatibleParam returns false for all params currently
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var closureArg = CreateArg("block", new ClosureTypeSpec(
            new NamedTypeSpec("τ_0_0"), new NamedTypeSpec("τ_0_0")), moduleDecl);
        var extraArg = CreateArg("config", new NamedTypeSpec("TestModule.Config"), moduleDecl);

        var method = new MethodDecl
        {
            Name = "read",
            MangledName = "$s11RecordStore8Database4readyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                closureArg,
                extraArg
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "T", new(), new())
            },
            ParentDecl = CreateClassDecl("Database"),
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };

        var result = GenericClosureBridgeEmitter.AreNonClosureParamsCompatible(
            method, closureArg, typeDatabase);

        Assert.False(result);
    }

    #endregion

    #region TryEmit: eligible method emits bridge

    [Fact]
    public void TryEmit_EligibleGenericClosure_EmitsBridge()
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();
        var parentDecl = CreateClassDecl("Database");

        // Closure: (Database) throws -> τ_0_0 — concrete class input, generic return
        var closureSpec = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.Database"), new NamedTypeSpec("τ_0_0"))
        {
            Throws = true
        };

        var closureArg = CreateArg("block", closureSpec, moduleDecl);

        var method = new MethodDecl
        {
            Name = "read",
            MangledName = "$s11RecordStore8Database4readyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = true,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                // Return type = τ_0_0 (identity-forwarding)
                CreateArg("", new NamedTypeSpec("τ_0_0"), moduleDecl),
                closureArg
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "T", new(), new())
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };

        var env = new MethodEnvironment(method, typeDatabase);
        var ctx = new ModuleEmissionContext();

        var handled = GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parentDecl, ctx);

        Assert.True(handled);
        Assert.True(method.WasEmitted);
        Assert.True(method.HasGenericClosureBridge);
        Assert.True(method.UsesWrapperLibrary);

        var csResult = csOutput.ToString();
        var swResult = swiftOutput.ToString();

        // C# output should contain callbacks, P/Invokes, and public methods
        Assert.Contains("[UnmanagedCallersOnly", csResult);
        Assert.Contains("LibraryImport", csResult);
        Assert.Contains("SBW_CreateError", csResult);

        // Swift output should contain wrapper functions
        Assert.Contains("@_silgen_name", swResult);
        Assert.Contains("@_cdecl", swResult);
        Assert.Contains("SBW_CreateError", swResult);
    }

    [Fact]
    public void TryEmit_GenericClosureReturn_GuardsResultBufExceptionPath()
    {
        // The returning bridge has the Swift callback write the closure result (+1) into resultBuf,
        // then MarshalMovedValueFromSlot consumes it. That consume leaves the slot intact on every
        // throw path (by contract), so a throw before the +1 is adopted leaves an unconsumed +1 in
        // resultBuf. The outer finally must value-witness Destroy it before the raw AlignedFree —
        // otherwise the conformer / COW-storage +1 leaks (same shape as the SwiftArray/Dictionary
        // slotLive guards).
        //
        // Regression: liveness must be marked the instant the callback writes
        // resultBuf, NOT after the post-P/Invoke Swift-error check. The Swift wrapper passes the same
        // resultBuf to the closure callback, so a generic method that invokes the closure (populating
        // the slot) and THEN throws would, under the old "set live after the error check" shape, exit
        // via the error throw with the +1 still in resultBuf and the finally skipping Destroy → leak.
        // Asserts the live-set is emitted inside the invoke delegate (after MarshalToSwift, before the
        // _XC P/Invoke and its error-check throw), that a prior +1 is released before a re-invoke
        // overwrite, and that the finally Destroys an unconsumed slot before freeing.
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();
        var parentDecl = CreateClassDecl("Database");

        // Closure: (Database) throws -> τ_0_0 — concrete class input, generic (class) return. The
        // resultBuf exception-path guard is emitted in the returning path regardless of Throws; the
        // throwing shape is used here because it is the eligible-bridge shape in this minimal harness.
        var closureSpec = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.Database"), new NamedTypeSpec("τ_0_0"))
        {
            Throws = true
        };
        var closureArg = CreateArg("block", closureSpec, moduleDecl);

        var method = new MethodDecl
        {
            Name = "read",
            MangledName = "$s11RecordStore8Database4readyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = true,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("τ_0_0"), moduleDecl),
                closureArg
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "T", new(), new())
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };

        var env = new MethodEnvironment(method, typeDatabase);
        var ctx = new ModuleEmissionContext();

        var handled = GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parentDecl, ctx);
        Assert.True(handled);

        var csResult = csOutput.ToString();

        // Liveness flag is set (in the delegate) and cleared (after the moved read adopts the +1).
        Assert.Contains("resultSlotLive = true;", csResult);
        Assert.Contains("resultSlotLive = false;", csResult);
        // The delegate releases any prior +1 before overwriting on a re-invoke, then writes, then marks live.
        Assert.Contains("if (resultSlotLive) SwiftMarshal.DestroyWireBufferRetains(resBufPtr, metadata);", csResult);
        Assert.Contains("SwiftMarshal.MarshalToSwift(result, ref resBufSpan);", csResult);
        // The finally releases an unconsumed slot's +1 via the non-generic wire-buffer destroy.
        Assert.Contains("if (resultSlotLive) SwiftMarshal.DestroyWireBufferRetains((IntPtr)resultBuf, metadata);", csResult);

        // Exactly one live-set, emitted inside the invoke delegate after the write and BEFORE the
        // Swift-error-check throw. This is the regression guard: the old shape set liveness AFTER the
        // error check, so a generic method that invoked the closure then threw leaked the +1.
        var liveSetIdx = csResult.IndexOf("resultSlotLive = true;", StringComparison.Ordinal);
        Assert.Equal(liveSetIdx, csResult.LastIndexOf("resultSlotLive = true;", StringComparison.Ordinal));
        var delegateMarshalIdx = csResult.IndexOf("SwiftMarshal.MarshalToSwift(result, ref resBufSpan);", StringComparison.Ordinal);
        var preOverwriteDestroyIdx = csResult.IndexOf("if (resultSlotLive) SwiftMarshal.DestroyWireBufferRetains(resBufPtr, metadata);", StringComparison.Ordinal);
        var errThrowIdx = csResult.IndexOf("throw new SwiftRuntimeException", StringComparison.Ordinal);
        Assert.True(preOverwriteDestroyIdx >= 0 && preOverwriteDestroyIdx < delegateMarshalIdx,
            "the prior-+1 release must precede the MarshalToSwift overwrite in the invoke delegate");
        Assert.True(delegateMarshalIdx < liveSetIdx,
            "liveness must be marked after the callback writes resultBuf");
        Assert.True(errThrowIdx > liveSetIdx,
            "liveness must be set before the Swift-error-check throw so a throw-after-callback releases the +1");

        // Ordering: the value-witness Destroy must precede the raw AlignedFree (release the +1, then
        // free the buffer) — freeing first would lose the slot the Destroy needs to read.
        var destroyIdx = csResult.IndexOf("(IntPtr)resultBuf, metadata);", StringComparison.Ordinal);
        var freeIdx = csResult.IndexOf("NativeMemory.AlignedFree(resultBuf)", destroyIdx, StringComparison.Ordinal);
        Assert.True(destroyIdx >= 0 && freeIdx > destroyIdx,
            "value-witness Destroy must be emitted before the raw AlignedFree in the result-buffer finally");
    }

    [Fact]
    public void TryEmit_ClassClosureArg_UsesOwningBorrowedClassMarshal()
    {
        // A class-typed closure argument is handed to Swift with passUnretained (+0) and surfaced to
        // the user's closure body, where it may be Disposed. It must marshal via the OWNING
        // MarshalBorrowedClassFromSwift (real +1 that Dispose/finalize both balance), not the
        // SuppressFinalize-only MarshalBorrowedFromSwift — whose reflection-based finalizer
        // suppression is trimmed on NativeAOT, over-releasing the borrowed object (device SIGTRAP).
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();
        var parentDecl = CreateClassDecl("Database");

        // Closure: (Database) throws -> τ_0_0 — TestModule.Database is registered as a class.
        var closureSpec = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.Database"), new NamedTypeSpec("τ_0_0"))
        {
            Throws = true
        };
        var closureArg = CreateArg("block", closureSpec, moduleDecl);

        var method = new MethodDecl
        {
            Name = "read",
            MangledName = "$s11RecordStore8Database4readyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = true,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("τ_0_0"), moduleDecl),
                closureArg
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "T", new(), new())
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };

        var env = new MethodEnvironment(method, typeDatabase);
        var ctx = new ModuleEmissionContext();

        var handled = GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parentDecl, ctx);
        Assert.True(handled);

        var csResult = csOutput.ToString();
        Assert.Contains("SwiftMarshal.MarshalBorrowedClassFromSwift<TestModule.Database>", csResult);
        Assert.DoesNotContain("SwiftMarshal.MarshalBorrowedFromSwift<TestModule.Database>", csResult);
    }

    #endregion

    #region TryEmit: error handling in throwing methods

    [Fact]
    public void TryEmit_ThrowingMethod_EmitsErrorHandling()
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();
        var parentDecl = CreateClassDecl("Database");

        // Closure: (Database) throws -> τ_0_0 — concrete class input, generic return
        var closureSpec = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.Database"), new NamedTypeSpec("τ_0_0"))
        {
            Throws = true
        };

        var method = new MethodDecl
        {
            Name = "read",
            MangledName = "$s11RecordStore8Database4readyyF_v2",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = true,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                // Return type = τ_0_0 (identity-forwarding)
                CreateArg("", new NamedTypeSpec("τ_0_0"), moduleDecl),
                CreateArg("block", closureSpec, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "T", new(), new())
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };

        var env = new MethodEnvironment(method, typeDatabase);
        GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parentDecl);

        var csResult = csOutput.ToString();

        // Error handling should be present in throwing methods
        Assert.Contains("swiftError", csResult);
        Assert.Contains("SwiftRuntimeException", csResult);
        Assert.Contains("SBW_GetErrorDescription", csResult);
        Assert.Contains("SBW_ReleaseError", csResult);
    }

    #endregion

    #region @MainActor annotation on @_silgen_name

    [Fact]
    public void TryEmit_MainActorParent_EmitsMainActorAnnotation()
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();
        var parentDecl = CreateClassDecl("ViewModel");
        parentDecl.IsMainActorIsolated = true;

        var closureSpec = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.Database"), new NamedTypeSpec("τ_0_0"))
        {
            Throws = true
        };

        var method = new MethodDecl
        {
            Name = "read",
            MangledName = "$s11RecordStore9ViewModel4readyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = true,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("τ_0_0"), moduleDecl),
                CreateArg("block", closureSpec, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "T", new(), new())
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };

        var env = new MethodEnvironment(method, typeDatabase);
        GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parentDecl);

        var swift = swiftOutput.ToString();
        // Both returning and void variants should have @MainActor before @_silgen_name
        Assert.Contains("@MainActor", swift);
        Assert.Contains("@_silgen_name", swift);
        // Count: exactly 2 @MainActor annotations (returning + void variants)
        var mainActorCount = System.Text.RegularExpressions.Regex.Matches(swift, "@MainActor").Count;
        Assert.Equal(2, mainActorCount);
    }

    [Fact]
    public void TryEmit_NonActorParent_DoesNotEmitMainActorAnnotation()
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();
        var parentDecl = CreateClassDecl("Database");

        var closureSpec = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.Database"), new NamedTypeSpec("τ_0_0"))
        {
            Throws = true
        };

        var method = new MethodDecl
        {
            Name = "read",
            MangledName = "$s11RecordStore8Database4readyyF_v3",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = true,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("τ_0_0"), moduleDecl),
                CreateArg("block", closureSpec, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "T", new(), new())
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };

        var env = new MethodEnvironment(method, typeDatabase);
        GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parentDecl);

        var swift = swiftOutput.ToString();
        Assert.DoesNotContain("@MainActor", swift);
        Assert.Contains("@_silgen_name", swift);
    }

    #endregion

    #region Synthetic-name guard wiring

    // The GenericClosureBridge @_silgen_name wrapper hardcodes synthetic Swift identifiers in the
    // same scope as the user's non-closure params: the `cdecl` func-ptr rebind local, the self
    // pointer param + its reconstruction local, the result-buffer param, and the thrown-error
    // locals. A user non-closure param spelled the same identifier used to produce an "invalid
    // redeclaration" emitted at swiftc time (silently stripped at exit 0). The emitter now seeds a
    // SyntheticNameScope with the user param names (and the closure's FuncPtr/Context params) and
    // reserves each synthetic through it, renaming a collision to its `__`-prefixed form. These
    // assert the wiring at the emitter layer — the layer where the guard's behavior is observable
    // independent of the runtime path.

    [Fact]
    public void TryEmit_UserParamNamedCdecl_RenamesFuncPtrSynthetic()
    {
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftWriter = new SwiftWriter(swiftOutput);

        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();
        var parentDecl = CreateClassDecl("Database");

        var closureSpec = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.Database"), new NamedTypeSpec("τ_0_0")) { Throws = true };
        var closureArg = CreateArg("block", closureSpec, moduleDecl);
        // User non-closure class param spelled `cdecl` — collides with the synthetic func-ptr local.
        var cdeclArg = CreateArg("cdecl", new NamedTypeSpec("TestModule.Database"), moduleDecl);

        var method = new MethodDecl
        {
            Name = "readWithCdecl",
            MangledName = "$s11RecordStore8Database13readWithCdeclyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = true,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("τ_0_0"), moduleDecl),
                closureArg,
                cdeclArg
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "T", new(), new())
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };

        var env = new MethodEnvironment(method, typeDatabase);
        GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parentDecl, new ModuleEmissionContext());

        var swift = swiftOutput.ToString();
        // The synthetic func-ptr rebind escaped to `__cdecl`; the user param `cdecl` survives as-is.
        Assert.Contains("let __cdecl = unsafeBitCast", swift);
        Assert.Contains("__cdecl(", swift); // invoked under the renamed identifier
        // No bare-`cdecl` redeclaration (the "invalid redeclaration" the guard exists to prevent).
        Assert.DoesNotContain("let cdecl = unsafeBitCast", swift);
    }

    [Fact]
    public void TryEmit_UserParamNamedUnderscoreSelf_RenamesSelfPointerTransitively()
    {
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftWriter = new SwiftWriter(swiftOutput);

        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();
        var parentDecl = CreateClassDecl("Database");

        var closureSpec = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.Database"), new NamedTypeSpec("τ_0_0")) { Throws = true };
        var closureArg = CreateArg("block", closureSpec, moduleDecl);
        // User non-closure class param spelled `_self` — collides with the synthetic self-pointer
        // param, forcing a transitive rename (`_self`→`___self`; the `__self` reconstruction local
        // is then free).
        var selfArg = CreateArg("_self", new NamedTypeSpec("TestModule.Database"), moduleDecl);

        var method = new MethodDecl
        {
            Name = "readWithSelf",
            MangledName = "$s11RecordStore8Database12readWithSelfyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = true,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("τ_0_0"), moduleDecl),
                closureArg,
                selfArg
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "T", new(), new())
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };

        var env = new MethodEnvironment(method, typeDatabase);
        GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parentDecl, new ModuleEmissionContext());

        var swift = swiftOutput.ToString();
        // The self-pointer param escaped to `___self`; the reconstruction local reads from it.
        Assert.Contains("___self: UnsafeMutableRawPointer", swift);
        Assert.Contains("unsafeBitCast(OpaquePointer(___self)", swift);
        // The user param `_self` survives as a distinct, label-less wrapper parameter (`_ _self:`),
        // so the synthetic (`___self`) and the user identifier never collide into an "invalid
        // redeclaration". `_ _self:` is not a substring of `_ ___self:`, so this uniquely matches
        // the user param regardless of how its type renders in this fixture.
        Assert.Contains("_ _self:", swift);
    }

    #endregion

    #region Helpers

    private static (CSharpWriter csWriter, SwiftWriter swiftWriter) CreateWriters()
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);
        return (csWriter, swiftWriter);
    }

    private static ModuleDecl CreateModuleDecl()
    {
        return new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ClassDecl CreateClassDecl(string name)
    {
        var moduleDecl = CreateModuleDecl();
        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
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
    }

    private static MethodDecl CreateMethodDecl(string name, bool isConstructor = false)
    {
        var moduleDecl = CreateModuleDecl();
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name.Length}{name}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = isConstructor,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = CreateClassDecl("TestType"),
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };
    }

    private static ArgumentDecl CreateArg(string name, TypeSpec typeSpec, ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            Name = name,
            PrivateName = name,
            SwiftTypeSpec = typeSpec,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "nint"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Database"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Database"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Database"),
                MetadataAccessor = "$s10TestModule8DatabaseCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static MethodEnvironment CreateMethodEnvironment(MethodDecl method)
    {
        return new MethodEnvironment(method, CreateTypeDatabase());
    }

    #endregion
}
