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
        Assert.Contains("[global::System.Runtime.InteropServices.UnmanagedCallersOnly", csResult);
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
        // The throw now routes through the single-source SwiftMarshal.ThrowSwiftError carriage.
        var errThrowIdx = csResult.IndexOf("SwiftMarshal.ThrowSwiftError(_errorPtr", StringComparison.Ordinal);
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
        // semantics-dispatched MarshalCallbackArg — the emitter's IsClassType split routes a
        // statically-known class to the direct owning class marshal at generation time.
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
        Assert.DoesNotContain("SwiftMarshal.MarshalCallbackArg<TestModule.Database>", csResult);
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

        // Error handling should be present in throwing methods, routed through the single-source
        // SwiftMarshal.ThrowSwiftError carriage so the surfaced SwiftException carries the live error box.
        Assert.Contains("swiftError", csResult);
        Assert.Contains("ThrowSwiftError", csResult);
        Assert.Contains("SBW_GetErrorDescription", csResult);
        Assert.Contains("SBW_ReleaseError", csResult);
        // Pin the exact carriage routing: the description is read inline and the release delegate is
        // handed off (released on finalization), not invoked eagerly here.
        Assert.Contains("SwiftMarshal.ThrowSwiftError(_errorPtr, SBW_GetErrorDescription(_errorPtr), SBW_ReleaseError)", csResult);
        // Negative-assert the removed eager-release shape (parity with the ProtocolProxy throwing test):
        // the old path threw an identity-lossy SwiftRuntimeException, manually freed a _descPtr buffer,
        // and eagerly released the error box before throwing. None of that may survive the migration.
        Assert.DoesNotContain("throw new SwiftRuntimeException", csResult);
        Assert.DoesNotContain("SBW_Free(_descPtr)", csResult);
        Assert.DoesNotContain("SBW_ReleaseError(_errorPtr);", csResult);
    }

    #endregion

    #region Gate (c): generic type parameter in closure ARGUMENT / non-closure position

    // The historical gate rejected a method-generic parameter appearing in closure ARGUMENT position
    // (`(T) throws -> T`) because the C# callback declared one `void*` only per CONCRETE closure arg,
    // so the Swift cdecl callback — which passes one `void*` per arg, generic included — handed C# more
    // pointers than it declared (an ABI mismatch). The fix counts ALL closure args in the callback and
    // marshals an in-position generic value through a value-witness buffer: C# allocates a T-sized
    // buffer, marshals the value (+1) in, passes the buffer pointer, the Swift wrapper forwards it to
    // the closure, and the callback reads it back with a borrowed +1 (MarshalBorrowedValueFromSlot).
    // These pin the emitter side of that contract.

    [Fact]
    public void TryEmit_GenericClosureArgInput_MarshalsValueBufferAndBorrowedRead()
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();
        var parentDecl = CreateClassDecl("Database");

        // Closure: (τ_0_0) throws -> τ_0_0 — generic in BOTH argument and return position (the `apply`
        // shape). The method also takes a bare generic non-closure value `value: τ_0_0`.
        var closureSpec = new ClosureTypeSpec(
            new NamedTypeSpec("τ_0_0"), new NamedTypeSpec("τ_0_0")) { Throws = true };
        var closureArg = CreateArg("transform", closureSpec, moduleDecl);
        var valueArg = CreateArg("value", new NamedTypeSpec("τ_0_0"), moduleDecl);

        var method = new MethodDecl
        {
            Name = "apply",
            MangledName = "$s11RecordStore8Database5applyyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = true,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("τ_0_0"), moduleDecl),
                closureArg,
                valueArg
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
        var handled = GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parentDecl, new ModuleEmissionContext());
        Assert.True(handled);

        var cs = csOutput.ToString();

        // The public closure surfaces T in BOTH input and return position.
        Assert.Contains("Func<T, T>", cs);

        // The in-position generic argument is read back from the closure's value-buffer slot with a
        // BORROWED +1 (no consume) — the buffer's +1 stays owned by the caller's finally.
        Assert.Contains("SwiftMarshal.MarshalBorrowedValueFromSlot<T>", cs);

        // The generic non-closure value flows through a C#-allocated value-witness buffer: alloc, a
        // typed span, MarshalToSwift the +1 in, and a liveness flag.
        Assert.Contains("__valueBuf = NativeMemory.AlignedAlloc", cs);
        Assert.Contains("__valueBufSpan = new Span<byte>", cs);
        Assert.Contains("SwiftMarshal.MarshalToSwift(value, ref", cs);
        Assert.Contains("__valueBufLive = true;", cs);

        // The finally value-witness Destroys the buffer's +1 and frees it — symmetric with the +1 in.
        Assert.Contains("__valueBufLive) SwiftMarshal.DestroyWireBufferRetains((IntPtr)", cs);
        Assert.Contains("__valueBuf != null) NativeMemory.AlignedFree", cs);

        // Void (T = Void) variant suppressed: T is no longer confined to the closure return, so the
        // T = Void specialization is a type error and must NOT be emitted (nor emitted-then-stripped).
        Assert.DoesNotContain("VoidCallback", cs);
    }

    [Fact]
    public void TryEmit_MultipleGenericClosureArgs_CountsEachAsVoidStar()
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();
        var parentDecl = CreateClassDecl("Database");

        // Closure: (τ_0_0, τ_0_0) throws -> τ_0_0 — TWO generic args in input position (the `combine`
        // shape). Each generic arg must become its OWN void* in the cdecl callback — the exact ABI
        // count the historical gate got wrong.
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("τ_0_0"), new NamedTypeSpec("τ_0_0") }),
            new NamedTypeSpec("τ_0_0")) { Throws = true };
        var closureArg = CreateArg("merge", closureSpec, moduleDecl);
        var firstArg = CreateArg("first", new NamedTypeSpec("τ_0_0"), moduleDecl);
        var secondArg = CreateArg("second", new NamedTypeSpec("τ_0_0"), moduleDecl);

        var method = new MethodDecl
        {
            Name = "combine",
            MangledName = "$s11RecordStore8Database7combineyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = true,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("τ_0_0"), moduleDecl),
                closureArg,
                firstArg,
                secondArg
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
        var handled = GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parentDecl, new ModuleEmissionContext());
        Assert.True(handled);

        var cs = csOutput.ToString();

        // The cdecl callback declares one void* per closure arg — two here.
        Assert.Contains("void* arg0, void* arg1", cs);
        // The public closure surfaces two T inputs and a T return.
        Assert.Contains("Func<T, T, T>", cs);
        // Each generic arg is read back with its own borrowed slot read — exactly two.
        var borrowedReads = System.Text.RegularExpressions.Regex.Matches(
            cs, "MarshalBorrowedValueFromSlot<T>").Count;
        Assert.Equal(2, borrowedReads);
        Assert.DoesNotContain("VoidCallback", cs);
    }

    [Fact]
    public void TryEmit_GenericClosureReturnOnly_StillEmitsVoidVariant()
    {
        // Regression guard for the suppression: when T is confined to the closure RETURN (the classic
        // `(Database) throws -> T` shape), the T = Void specialization is still valid, so BOTH the
        // returning and void variants must continue to emit. The suppression must fire ONLY when T
        // escapes the return position.
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();
        var parentDecl = CreateClassDecl("Database");

        var closureSpec = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.Database"), new NamedTypeSpec("τ_0_0")) { Throws = true };

        var method = new MethodDecl
        {
            Name = "read",
            MangledName = "$s11RecordStore8Database4readyyF_gco",
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
        var handled = GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parentDecl, new ModuleEmissionContext());
        Assert.True(handled);

        var cs = csOutput.ToString();
        // The void variant survives for the return-only shape — and no generic value-buffer is needed.
        Assert.Contains("VoidCallback", cs);
        Assert.DoesNotContain("MarshalBorrowedValueFromSlot<T>", cs);
        Assert.DoesNotContain("__valueBuf = NativeMemory.AlignedAlloc", cs);
    }

    [Fact]
    public void TryEmit_TwoDistinctGenericsInClosure_Rejected()
    {
        // The bridge monomorphizes exactly ONE generic parameter (T = UnsafeMutableRawPointer). A
        // closure that mixes two distinct method generics — `(T, U) throws -> T` — would collapse U
        // onto T: wrong Func<> arity/types, wrong value-buffer metadata, and a Swift wrapper that
        // monomorphizes the wrong parameter. The method must be rejected before any output is emitted.
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();
        var parentDecl = CreateClassDecl("Database");

        // Closure: (τ_0_0, τ_0_1) throws -> τ_0_0 — two DISTINCT method generics in input position.
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("τ_0_0"), new NamedTypeSpec("τ_0_1") }),
            new NamedTypeSpec("τ_0_0")) { Throws = true };
        var closureArg = CreateArg("merge", closureSpec, moduleDecl);

        var method = new MethodDecl
        {
            Name = "choose",
            MangledName = "$s11RecordStore8Database6chooseyyF",
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
                new GenericArgumentDecl("τ_0_0", "T", new(), new()),
                new GenericArgumentDecl("τ_0_1", "U", new(), new())
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };

        var env = new MethodEnvironment(method, typeDatabase);
        var handled = GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parentDecl, new ModuleEmissionContext());

        Assert.False(handled);
        Assert.False(method.WasEmitted);
        Assert.Equal(string.Empty, csOutput.ToString());
    }

    [Fact]
    public void TryEmit_BareNonClosureParamOfDifferentGeneric_Rejected()
    {
        // The closure uses a single tau (`(T) throws -> T`), but the method also takes a bare generic
        // non-closure param of a DIFFERENT generic (`extra: U`). The bridge would mis-render that U as
        // the one monomorphized T; if U is separately constrained, the Swift wrapper fails to compile
        // while C# still references its symbol. Reject the method.
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();
        var parentDecl = CreateClassDecl("Database");

        // Closure: (τ_0_0) throws -> τ_0_0 — single tau. Method also takes `value: τ_0_0` (same tau,
        // fine) and `extra: τ_0_1` (a DIFFERENT generic, the rejection trigger).
        var closureSpec = new ClosureTypeSpec(
            new NamedTypeSpec("τ_0_0"), new NamedTypeSpec("τ_0_0")) { Throws = true };
        var closureArg = CreateArg("transform", closureSpec, moduleDecl);
        var valueArg = CreateArg("value", new NamedTypeSpec("τ_0_0"), moduleDecl);
        var extraArg = CreateArg("extra", new NamedTypeSpec("τ_0_1"), moduleDecl);

        var method = new MethodDecl
        {
            Name = "apply",
            MangledName = "$s11RecordStore8Database5applyyyF_mixed",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = true,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("τ_0_0"), moduleDecl),
                closureArg,
                valueArg,
                extraArg
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "T", new(), new()),
                new GenericArgumentDecl("τ_0_1", "U", new(), new())
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };

        var env = new MethodEnvironment(method, typeDatabase);
        var handled = GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parentDecl, new ModuleEmissionContext());

        Assert.False(handled);
        Assert.False(method.WasEmitted);
        Assert.Equal(string.Empty, csOutput.ToString());
    }

    [Fact]
    public void TryEmit_InoutGenericClosureArg_Rejected()
    {
        // The closure uses a single tau, but its argument is `inout τ_0_0` — `(inout T) throws -> T`.
        // The bridge specializes T = UnsafeMutableRawPointer and renders the argument as a by-value
        // void*, invoking the closure WITHOUT `&`, so the monomorphized Swift wrapper cannot
        // type-check against the inout closure signature. Every other gate passes (single tau,
        // same-tau bare param, matching return) so the inout flag is the SOLE rejection trigger.
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();
        var parentDecl = CreateClassDecl("Database");

        // Closure: (inout τ_0_0) throws -> τ_0_0 — single tau, but the argument is inout.
        var closureSpec = new ClosureTypeSpec(
            new NamedTypeSpec("τ_0_0") { IsInOut = true }, new NamedTypeSpec("τ_0_0")) { Throws = true };
        var closureArg = CreateArg("transform", closureSpec, moduleDecl);

        var method = new MethodDecl
        {
            Name = "apply",
            MangledName = "$s11RecordStore8Database5applyyyF_inoutclosure",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = true,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("τ_0_0"), moduleDecl),
                CreateArg("value", new NamedTypeSpec("τ_0_0"), moduleDecl),
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
        var handled = GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parentDecl, new ModuleEmissionContext());

        Assert.False(handled);
        Assert.False(method.WasEmitted);
        Assert.Equal(string.Empty, csOutput.ToString());
    }

    [Fact]
    public void TryEmit_InoutBareGenericParam_Rejected()
    {
        // The closure is plain `(τ_0_0) throws -> τ_0_0`, but the method takes a bare generic param of
        // the SAME tau passed `inout` — `func apply<T>(_ value: inout T, _ transform: (T) throws -> T)`.
        // The bridge passes the value buffer by value and never forwards `&`, so the Swift wrapper
        // fails to type-check against the inout method signature. The inout flag on the bare param is
        // the SOLE rejection trigger (same tau, matching return, non-inout closure arg).
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();
        var parentDecl = CreateClassDecl("Database");

        var closureSpec = new ClosureTypeSpec(
            new NamedTypeSpec("τ_0_0"), new NamedTypeSpec("τ_0_0")) { Throws = true };
        var closureArg = CreateArg("transform", closureSpec, moduleDecl);

        // Bare `value: inout τ_0_0` — same tau as the closure, but inout on the ArgumentDecl.
        var inoutValueArg = new ArgumentDecl
        {
            Name = "value",
            PrivateName = "value",
            SwiftTypeSpec = new NamedTypeSpec("τ_0_0"),
            IsInOut = true,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var method = new MethodDecl
        {
            Name = "apply",
            MangledName = "$s11RecordStore8Database5applyyyF_inoutbare",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = true,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("τ_0_0"), moduleDecl),
                inoutValueArg,
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
        var handled = GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parentDecl, new ModuleEmissionContext());

        Assert.False(handled);
        Assert.False(method.WasEmitted);
        Assert.Equal(string.Empty, csOutput.ToString());
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
