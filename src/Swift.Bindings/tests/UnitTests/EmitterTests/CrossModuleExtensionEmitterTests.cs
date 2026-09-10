// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for CrossModuleExtensionEmitter — cross-module extension type dispatch.
/// When module B extends a type from module A, this emitter generates static extension classes.
/// </summary>
public class CrossModuleExtensionEmitterTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    #region Emit: no members from current module → skips

    [Fact]
    public void Emit_NoMembersFromCurrentModule_NoOutput()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        // All methods belong to the original module, not the current module
        classDecl.Methods.Add(CreateMethodDecl("doWork", "OrigModule", classDecl));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        Assert.DoesNotContain("class", csOutput.ToString());
    }

    #endregion

    #region Emit: extension method from current module → emits class

    [Fact]
    public void Emit_ExtensionMethodFromCurrentModule_EmitsExtensionClass()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        // Method from current module (extension method)
        classDecl.Methods.Add(CreateMethodDecl("customAction", "TestModule", classDecl));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        Assert.Contains("public static partial class", result);
        Assert.Contains("TestModuleExtensions", result);
        Assert.Contains("Extension methods for", result);
    }

    #endregion

    #region Emit: gates — generic and mutating methods skipped; async/throws emit via trampoline

    [Fact]
    public void Emit_GenericMethod_Skipped()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        var method = CreateMethodDecl("doWork", "TestModule", classDecl);
        // Make method generic by adding a generic parameter
        method.GenericParameters.Add(new GenericArgumentDecl("τ_0_0", "T", new(), new()));
        classDecl.Methods.Add(method);

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        Assert.DoesNotContain("DoWork", result);
        // The lone member was gated out, so the now-empty class shell must be rolled back —
        // no dead `public static partial class TestModuleExtensions { }` left behind.
        Assert.DoesNotContain("public static partial class", result);
        Assert.DoesNotContain("TestModuleExtensions", result);
    }

    [Fact]
    public void Emit_AsyncMethod_EmitsViaAsyncTrampoline()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        var method = CreateMethodDecl("doWork", "TestModule", classDecl);
        method.IsAsync = true;
        classDecl.Methods.Add(method);

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        // Async methods on class receivers now route through the async-throws
        // trampoline path and surface as a Task-returning extension method.
        Assert.Contains("DoWorkAsync", result);
        Assert.Contains("System.Threading.Tasks.Task", result);
    }

    [Fact]
    public void Emit_ThrowingMethod_EmitsViaAsyncTrampoline()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        var method = CreateMethodDecl("doWork", "TestModule", classDecl);
        method.Throws = true;
        classDecl.Methods.Add(method);

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        // Throwing methods on class receivers now route through the async-throws
        // trampoline path and surface as a regular (synchronous) extension method.
        Assert.Contains("DoWork(this", result);
    }

    [Fact]
    public void Emit_MutatingMethod_Skipped()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        var method = CreateMethodDecl("doWork", "TestModule", classDecl);
        method.IsMutating = true;
        classDecl.Methods.Add(method);

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        Assert.DoesNotContain("DoWork", result);
        // Lone member gated out → empty class shell suppressed (rolled back).
        Assert.DoesNotContain("public static partial class", result);
        Assert.DoesNotContain("TestModuleExtensions", result);
    }

    [Fact]
    public void Emit_OneSurvivorAmongSkipped_KeepsClassShell()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        // One member is gated out (mutating), one survives — the shell must be KEPT,
        // proving dead-shell suppression rolls back only when EVERY member is unemittable.
        var skipped = CreateMethodDecl("doWork", "TestModule", classDecl);
        skipped.IsMutating = true;
        classDecl.Methods.Add(skipped);
        classDecl.Methods.Add(CreateMethodDecl("customAction", "TestModule", classDecl));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        Assert.Contains("public static partial class", result);
        Assert.Contains("TestModuleExtensions", result);
        Assert.Contains("CustomAction", result);
        Assert.DoesNotContain("DoWork", result);
    }

    #endregion

    #region Emit: property extension

    [Fact]
    public void Emit_PropertyExtension_EmitsGetMethod()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        var ownerModuleDecl = CreateFullModuleDecl("TestModule");
        var getterMethod = CreateMethodDecl("get_count", "TestModule", classDecl,
            mangledName: "$s10TestModule5count_getter");
        getterMethod.IsAccessor = true;
        var getter = new GetAccessorDecl { Method = getterMethod };

        var property = new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl> { getter },
            ParentDecl = classDecl,
            ModuleDecl = ownerModuleDecl
        };
        classDecl.Properties.Add(property);

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        Assert.Contains("GetCount", result);
    }

    [Fact]
    public void Emit_StaticProperty_Skipped()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        var ownerModuleDecl = CreateFullModuleDecl("TestModule");
        var property = new PropertyDecl
        {
            Name = "shared",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = true,
            HasStorage = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = ownerModuleDecl
        };
        classDecl.Properties.Add(property);

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        Assert.DoesNotContain("GetShared", result);
    }

    #endregion

    #region Emit: NativeMethods nested class

    [Fact]
    public void Emit_WithMembers_EmitsNativeMethodsClass()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        classDecl.Methods.Add(CreateMethodDecl("customAction", "TestModule", classDecl));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        Assert.Contains("NativeMethods", result);
        Assert.Contains("LibraryImport", result);
    }

    #endregion

    #region Emit: extension method with this parameter

    [Fact]
    public void Emit_InstanceMethod_HasThisParameter()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        classDecl.Methods.Add(CreateMethodDecl("doAction", "TestModule", classDecl));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        Assert.Contains("this", result);
    }

    #endregion

    #region Emit: calling convention

    [Fact]
    public void Emit_MethodPInvoke_PrefersCdeclTrampolineOverUntypedSelf()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        classDecl.Methods.Add(CreateMethodDecl("doAction", "TestModule", classDecl));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        // A method with no closure and no async/throws still takes the generated trampoline: it
        // is entered under plain C, which carries the receiver as an ordinary pointer argument.
        // The alternative is a swiftcc import whose receiver rides the untyped self register, and
        // nothing about an ordinary primitive method makes that the safer of the two.
        Assert.Contains("CallConvCdecl", result);
        Assert.DoesNotContain("SwiftSelf", result);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Emit_MethodWithConstOrInoutParam_DeclinesTheCdeclTrampolineAndKeepsTheMember(
        bool isConstLiteral, bool isInOut)
    {
        // A `_const` argument has to be a compile-time constant literal at the call site and an
        // `inout` one has to be forwarded through a mutable binding with `&`. The trampoline
        // declares ordinary immutable parameters and forwards them by value, so taking either
        // shape emits Swift that does not compile — and a wrapper that does not compile is
        // stripped, taking the member's emission with it. Both shapes belong on the route that
        // already carries them.
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        var method = CreateMethodDecl("doAction", "TestModule", classDecl);
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "value",
            PrivateName = "value",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsInOut = isInOut,
            IsConstLiteral = isConstLiteral,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        });
        classDecl.Methods.Add(method);

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        Assert.DoesNotContain("CallConvCdecl", result);
        // The member must not silently vanish either — an absence assertion on its own would be
        // satisfied by dropping it entirely, and keeping it is the whole reason to decline here
        // rather than let the wrapper fail to compile.
        Assert.Contains("DoAction", result);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Emit_ThrowsOrAsyncTrampolineOnIsolatedParent_StatesIsolationOnlyWhereItIsEntered(
        bool isAsync, bool expectMainActor)
    {
        // The async arm hops through a detached task that re-enters the member's own isolation,
        // so its entry point is correctly nonisolated. The sync-throws arm calls the member
        // inline on whatever thread entered the trampoline, so the isolation has to be stated on
        // the entry point — a main-actor member called from a nonisolated context is refused, and
        // a trampoline that does not compile is withdrawn along with the member.
        var (csWriter, swiftWriter, _, swiftOutput, moduleDecl, classDecl, conductor, env) =
            CreateSetupWithSwiftCapture();
        classDecl.IsMainActorIsolated = true;

        var method = CreateMethodDecl("doAction", "TestModule", classDecl);
        method.IsAsync = isAsync;
        method.Throws = !isAsync;
        classDecl.Methods.Add(method);

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var swift = swiftOutput.ToString();
        Assert.Contains("@_cdecl", swift); // sanity: the trampoline actually emitted
        Assert.Equal(expectMainActor, swift.Contains("@MainActor"));
    }

    [Fact]
    public void Emit_PropertyGetterPInvoke_UsesCallConvSwift()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        var ownerModuleDecl = CreateFullModuleDecl("TestModule");
        var getterMethod = CreateMethodDecl("get_count", "TestModule", classDecl,
            mangledName: "$s10TestModule5count_getter");
        getterMethod.IsAccessor = true;
        var getter = new GetAccessorDecl { Method = getterMethod };

        var property = new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl> { getter },
            ParentDecl = classDecl,
            ModuleDecl = ownerModuleDecl
        };
        classDecl.Properties.Add(property);

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        // Property getter P/Invoke must use CallConvSwift (see method test comment)
        Assert.Contains("CallConvSwift", result);
        Assert.DoesNotContain("CallConvCdecl", result);
    }

    #endregion

    #region Emit: async trampoline cancellation support

    [Fact]
    public void Emit_AsyncMethod_AppendsCancellationTokenWithPreCancelShortCircuit()
    {
        var (csWriter, swiftWriter, csOutput, _, moduleDecl, classDecl, conductor, env) = CreateSetupWithSwiftCapture();

        var method = CreateMethodDecl("doWork", "TestModule", classDecl);
        method.IsAsync = true;
        classDecl.Methods.Add(method);

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger,
            ThreadContext(new ModuleEmissionContext()));

        var result = csOutput.ToString();
        // The async surface takes a trailing defaulted CancellationToken like every
        // other async marshaller, short-circuits a pre-cancelled token without
        // crossing the native boundary, and can task-cancel the Swift producer.
        Assert.Contains("System.Threading.CancellationToken cancellationToken = default", result);
        Assert.Contains("Task.FromCanceled", result);
        Assert.Contains("SBW_CancelTask", result);
        Assert.Contains("TrySetCanceled", result);
    }

    [Fact]
    public void Emit_AsyncThrowsMethod_MapsSwiftCancellationToCanceledTask()
    {
        var (csWriter, swiftWriter, csOutput, swiftOutput, moduleDecl, classDecl, conductor, env) = CreateSetupWithSwiftCapture();

        var method = CreateMethodDecl("doWork", "TestModule", classDecl);
        method.IsAsync = true;
        method.Throws = true;
        classDecl.Methods.Add(method);

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger,
            ThreadContext(new ModuleEmissionContext()));

        var swiftResult = swiftOutput.ToString();
        // The Swift trampoline registers the launched Task with the producer-cancel
        // registry and reports a caught CancellationError distinctly from an ordinary
        // error so the C# side can cancel (not fault) the Task.
        Assert.Contains("_sbwRegisterTask", swiftResult);
        Assert.Contains("CancellationError", swiftResult);
        Assert.Contains("TrySetCanceled", csOutput.ToString());
    }

    [Fact]
    public void Emit_SyncThrowsMethod_HasNoCancellationToken()
    {
        var (csWriter, swiftWriter, csOutput, swiftOutput, moduleDecl, classDecl, conductor, env) = CreateSetupWithSwiftCapture();

        var method = CreateMethodDecl("doWork", "TestModule", classDecl);
        method.Throws = true;
        classDecl.Methods.Add(method);

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger,
            ThreadContext(new ModuleEmissionContext()));

        var result = csOutput.ToString();
        // Sync-throws blocks on the synchronously-fired completion — there is no
        // suspended Swift Task to cancel, so no CancellationToken parameter and no
        // cancel-registry wiring is emitted.
        Assert.Contains("DoWork(this", result);
        Assert.DoesNotContain("CancellationToken", result);
        Assert.DoesNotContain("SBW_CancelTask", result);
        Assert.DoesNotContain("_sbwRegisterTask", swiftOutput.ToString());
    }

    #endregion

    #region Struct receiver: @_cdecl trampoline path

    [Fact]
    public void EmitStruct_FrozenStructReceiver_EmitsExtensionClassAndCdeclTrampoline()
    {
        var (csWriter, swiftWriter, csOutput, swiftOutput, moduleDecl, structDecl, conductor, env)
            = CreateStructSetup(frozen: true, requiresMemoryManagement: false);

        structDecl.Methods.Add(CreateMethodDecl("doMath", "TestModule", structDecl));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, structDecl, moduleDecl, conductor, env, Logger);

        var csResult = csOutput.ToString();
        Assert.Contains("OrigPointTestModuleExtensions", csResult);
        // Struct-receiver path emits @_cdecl trampolines: P/Invoke MUST be Cdecl, NOT Swift.
        Assert.Contains("CallConvCdecl", csResult);
        Assert.DoesNotContain("CallConvSwift", csResult);
        // Receiver is the by-value frozen struct parameter — pinned via (&self), not via fixed.
        Assert.Contains("(IntPtr)(&self)", csResult);
        Assert.DoesNotContain("fixed (OrigModule.OrigPoint*", csResult);

        var swiftResult = swiftOutput.ToString();
        Assert.Contains("@_cdecl(\"SBW_TestModule_Ext_OrigPoint_doMath_", swiftResult);
        Assert.Contains("self_.assumingMemoryBound(to: OrigModule.OrigPoint.self).pointee", swiftResult);
    }

    [Fact]
    public void EmitStruct_NonFrozenStruct_NoExtensionClassEmitted()
    {
        var (csWriter, swiftWriter, csOutput, swiftOutput, moduleDecl, structDecl, conductor, env)
            = CreateStructSetup(frozen: false, requiresMemoryManagement: false);

        structDecl.Methods.Add(CreateMethodDecl("doMath", "TestModule", structDecl));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, structDecl, moduleDecl, conductor, env, Logger);

        Assert.DoesNotContain("OrigPointTestModuleExtensions", csOutput.ToString());
        Assert.DoesNotContain("@_cdecl", swiftOutput.ToString());
    }

    [Fact]
    public void EmitStruct_FrozenWithMemoryManagement_NoExtensionClassEmitted()
    {
        // Frozen + RequiresMemoryManagement means the struct projects as a C# class with a
        // SafeHandle/Buffer payload (ClassWithBufferStruct). The by-value `&self` pattern is
        // not applicable — guard skips emission.
        var (csWriter, swiftWriter, csOutput, swiftOutput, moduleDecl, structDecl, conductor, env)
            = CreateStructSetup(frozen: true, requiresMemoryManagement: true);

        structDecl.Methods.Add(CreateMethodDecl("doMath", "TestModule", structDecl));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, structDecl, moduleDecl, conductor, env, Logger);

        Assert.DoesNotContain("OrigPointTestModuleExtensions", csOutput.ToString());
        Assert.DoesNotContain("@_cdecl", swiftOutput.ToString());
    }

    [Fact]
    public void EmitStruct_NoMembersFromCurrentModule_NoExtensionClassEmitted()
    {
        var (csWriter, swiftWriter, csOutput, swiftOutput, moduleDecl, structDecl, conductor, env)
            = CreateStructSetup(frozen: true, requiresMemoryManagement: false);

        // Member belongs to OrigModule, not the current TestModule — should be skipped.
        structDecl.Methods.Add(CreateMethodDecl("origMember", "OrigModule", structDecl));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, structDecl, moduleDecl, conductor, env, Logger);

        Assert.DoesNotContain("OrigPointTestModuleExtensions", csOutput.ToString());
        Assert.DoesNotContain("@_cdecl", swiftOutput.ToString());
    }

    [Fact]
    public void EmitStruct_MutatingMethod_Skipped()
    {
        var (csWriter, swiftWriter, csOutput, swiftOutput, moduleDecl, structDecl, conductor, env)
            = CreateStructSetup(frozen: true, requiresMemoryManagement: false);

        var method = CreateMethodDecl("rotate", "TestModule", structDecl);
        method.IsMutating = true;
        structDecl.Methods.Add(method);

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, structDecl, moduleDecl, conductor, env, Logger);

        var csResult = csOutput.ToString();
        // The lone member is gated out, so the now-empty struct-extension shell must be
        // rolled back — no dead `public static partial class OrigPointTestModuleExtensions { }`.
        Assert.DoesNotContain("Rotate(", csResult);
        Assert.DoesNotContain("OrigPointTestModuleExtensions", csResult);
        Assert.DoesNotContain("public static partial class", csResult);
    }

    [Fact]
    public void EmitStruct_OneSurvivorAmongSkipped_KeepsClassShell()
    {
        var (csWriter, swiftWriter, csOutput, swiftOutput, moduleDecl, structDecl, conductor, env)
            = CreateStructSetup(frozen: true, requiresMemoryManagement: false);

        // One member is gated out (mutating), one survives — the struct-extension shell
        // must be KEPT, proving suppression rolls back only when EVERY member is unemittable.
        var skipped = CreateMethodDecl("rotate", "TestModule", structDecl);
        skipped.IsMutating = true;
        structDecl.Methods.Add(skipped);
        structDecl.Methods.Add(CreateMethodDecl("doMath", "TestModule", structDecl));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, structDecl, moduleDecl, conductor, env, Logger);

        var csResult = csOutput.ToString();
        Assert.Contains("OrigPointTestModuleExtensions", csResult);
        Assert.Contains("DoMath(", csResult);
        Assert.DoesNotContain("Rotate(", csResult);
    }

    [Fact]
    public void EmitStruct_SimpleEnumParamAndReturn_LowersToInt32AtCdeclBoundary()
    {
        // Cross-module extension on a frozen struct with a simple-enum param and a
        // simple-enum return must lower both to their raw integer across the @_cdecl
        // boundary — Swift @_cdecl cannot accept or return a Swift enum directly.
        var (csWriter, swiftWriter, csOutput, swiftOutput, moduleDecl, structDecl, conductor, env)
            = CreateStructSetupWithSimpleEnum(frozen: true);

        structDecl.Methods.Add(CreateMethodDeclWithEnumParamAndReturn("classify", "TestModule", structDecl));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, structDecl, moduleDecl, conductor, env, Logger);

        var csResult = csOutput.ToString();
        var swiftResult = swiftOutput.ToString();

        // C# public surface still uses the enum types.
        Assert.Contains("public static unsafe OrigModule.OrigStatus Classify(this OrigModule.OrigPoint self, OrigModule.OrigStatus status)", csResult);
        // C# call site casts to the underlying int for the cdecl boundary.
        Assert.Contains("(int)status", csResult);
        // P/Invoke param declared as the underlying int (NOT as the Swift enum).
        Assert.Contains("int status", csResult);
        // C# return marshalling casts the int back to the public enum.
        Assert.Contains("return (OrigModule.OrigStatus)NativeMethods.", csResult);

        // Swift signature uses Int32 for both param and return — the @_cdecl C ABI shape.
        Assert.Contains("_ status: Int32", swiftResult);
        Assert.Contains(") -> Int32", swiftResult);
        // Swift body reconstructs the enum via guard-let (preconditionFailure on
        // invalid raw, matching CdeclParamMapper) and re-exposes rawValue on return.
        Assert.Contains("guard let statusVal = OrigModule.OrigStatus(rawValue: status)", swiftResult);
        // Emitted traps carry the [SwiftBindings] breadcrumb so a raw-value abort is attributable.
        Assert.Contains("preconditionFailure(\"[SwiftBindings] Invalid raw value", swiftResult);
        Assert.Contains(".rawValue", swiftResult);
        // The Swift trampoline must NOT declare the Swift enum type in its @_cdecl signature.
        Assert.DoesNotContain("_ status: OrigModule.OrigStatus", swiftResult);
        Assert.DoesNotContain(") -> OrigModule.OrigStatus", swiftResult);
    }

    [Fact]
    public void EmitStruct_SimpleEnumProperty_SetterLowersValueAcrossCdeclBoundary()
    {
        // The getter path lowers a SimpleEnum return through .rawValue / (Enum)cast.
        // The setter path must do the same in the opposite direction: cast the
        // C# enum to its underlying int at the call site, declare the P/Invoke
        // parameter as the underlying scalar, and reconstruct T(rawValue:)! inside
        // the Swift @_cdecl trampoline. Otherwise the trampoline's @_cdecl
        // signature references a Swift enum across the C ABI and fails to compile.
        var (csWriter, swiftWriter, csOutput, swiftOutput, moduleDecl, structDecl, conductor, env)
            = CreateStructSetupWithSimpleEnum(frozen: true);

        var ownerModuleDecl = CreateFullModuleDecl("TestModule");
        var getterMethod = CreateMethodDecl("get_state", "TestModule", structDecl,
            mangledName: "$s10TestModule5state_getter");
        getterMethod.IsAccessor = true;
        var setterMethod = CreateMethodDecl("set_state", "TestModule", structDecl,
            mangledName: "$s10TestModule5state_setter");
        setterMethod.IsAccessor = true;

        var property = new PropertyDecl
        {
            Name = "state",
            SwiftTypeSpec = new NamedTypeSpec("OrigModule.OrigStatus"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = getterMethod },
                new SetAccessorDecl { Method = setterMethod }
            },
            ParentDecl = structDecl,
            ModuleDecl = ownerModuleDecl
        };
        structDecl.Properties.Add(property);

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, structDecl, moduleDecl, conductor, env, Logger);

        var csResult = csOutput.ToString();
        var swiftResult = swiftOutput.ToString();

        // C# setter signature uses the public enum type, but the call site casts to int.
        Assert.Contains("SetState(this ref OrigModule.OrigPoint self, OrigModule.OrigStatus value)", csResult);
        Assert.Contains("(int)value", csResult);
        // P/Invoke setter parameter is declared as the underlying int, NOT the enum.
        Assert.Contains("int value", csResult);
        Assert.DoesNotContain("PInvoke_SetState_", swiftResult); // sanity check the assertions below are about C#
        Assert.DoesNotContain("OrigModule.OrigStatus value, IntPtr __self", csResult);

        // Swift setter @_cdecl signature uses Int32, NOT the enum.
        Assert.Contains("_ newValue: Int32", swiftResult);
        Assert.DoesNotContain("_ newValue: OrigModule.OrigStatus", swiftResult);
        // Swift body reconstructs the enum via guard-let before assigning,
        // matching CdeclParamMapper's preconditionFailure shape.
        Assert.Contains("guard let newValueVal = OrigModule.OrigStatus(rawValue: newValue)", swiftResult);
        // Emitted traps carry the [SwiftBindings] breadcrumb so a raw-value abort is attributable.
        Assert.Contains("preconditionFailure(\"[SwiftBindings] Invalid raw value", swiftResult);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmitStruct_NoRawSimpleEnumParamOrReturn_SkippedCleanly(string? rawValueTypeName)
    {
        // No-raw simple enums (e.g. Swift `enum Direction { case north, south }` —
        // simpleEnum=true but no rawValueType) lack both `init(rawValue:)` and
        // `.rawValue`. Routing them through the integer-raw lowering would emit
        // Swift that fails to compile. They must be rejected at the lowering gate
        // and the surrounding method skipped from emission.
        var (csWriter, swiftWriter, csOutput, swiftOutput, moduleDecl, structDecl, conductor, env)
            = CreateStructSetupWithSimpleEnum(frozen: true, rawValueTypeName: rawValueTypeName);

        structDecl.Methods.Add(CreateMethodDeclWithEnumParamAndReturn("classify", "TestModule", structDecl));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, structDecl, moduleDecl, conductor, env, Logger);

        var csResult = csOutput.ToString();
        var swiftResult = swiftOutput.ToString();

        // No Classify method emitted at all — the surrounding member is skipped.
        Assert.DoesNotContain("Classify(", csResult);
        Assert.DoesNotContain("OrigStatus(rawValue:", swiftResult);
        Assert.DoesNotContain(".rawValue", swiftResult);
    }

    #endregion

    #region Entry-point promoted-symbol funnel (wrapper-lib members read the emission symbol, not raw MangledName)

    // A cross-module extension member that routes to the wrapper library (UsesWrapperLibrary) must
    // build its P/Invoke EntryPoint from the emission-scoped promoted symbol recorded on the
    // ModuleEmissionContext side table — not the decl's immutable pre-promotion silgen MangledName.
    // These three tests pin that funnel at each native-method call-site shape: instance method,
    // property getter, property setter. Before the migration these sites called the decl-only
    // ComputeEntryPoint overload, which read MangledName directly and ignored a promoted symbol.

    private const string PromotedSymbol = "$s10TestModule17promotedWrapperSymyyF";

    [Fact]
    public void Emit_WrapperLibMethod_EntryPointUsesPromotedEmissionSymbol()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        var method = CreateMethodDecl("customAction", "TestModule", classDecl,
            mangledName: "$s10TestModule12customActionyyF");
        method.UsesWrapperLibrary = true; // forces the wrapper-lib entry-point branch
        classDecl.Methods.Add(method);

        var emissionContext = new ModuleEmissionContext();
        emissionContext.RecordMethodEmissionSymbol(method, PromotedSymbol);
        var context = ThreadContext(emissionContext);

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger, context);

        var result = csOutput.ToString();
        Assert.Contains($"EntryPoint = \"{PromotedSymbol}\"", result);
        Assert.DoesNotContain("EntryPoint = \"$s10TestModule12customActionyyF\"", result);
    }

    [Fact]
    public void Emit_WrapperLibPropertyGetter_EntryPointUsesPromotedEmissionSymbol()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        var ownerModuleDecl = CreateFullModuleDecl("TestModule");
        var getterMethod = CreateMethodDecl("get_count", "TestModule", classDecl,
            mangledName: "$s10TestModule5count_getter");
        getterMethod.IsAccessor = true;
        getterMethod.UsesWrapperLibrary = true;

        var property = new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = classDecl,
            ModuleDecl = ownerModuleDecl
        };
        classDecl.Properties.Add(property);

        var emissionContext = new ModuleEmissionContext();
        emissionContext.RecordMethodEmissionSymbol(getterMethod, PromotedSymbol);
        var context = ThreadContext(emissionContext);

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger, context);

        var result = csOutput.ToString();
        Assert.Contains($"EntryPoint = \"{PromotedSymbol}\"", result);
        Assert.DoesNotContain("EntryPoint = \"$s10TestModule5count_getter\"", result);
    }

    [Fact]
    public void Emit_WrapperLibPropertySetter_EntryPointUsesPromotedEmissionSymbol()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        var ownerModuleDecl = CreateFullModuleDecl("TestModule");
        var getterMethod = CreateMethodDecl("get_count", "TestModule", classDecl,
            mangledName: "$s10TestModule5count_getter");
        getterMethod.IsAccessor = true;
        var setterMethod = CreateMethodDecl("set_count", "TestModule", classDecl,
            mangledName: "$s10TestModule5count_setter");
        setterMethod.IsAccessor = true;
        setterMethod.UsesWrapperLibrary = true;

        var property = new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"), // Primitive → setter native method emitted
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = getterMethod },
                new SetAccessorDecl { Method = setterMethod }
            },
            ParentDecl = classDecl,
            ModuleDecl = ownerModuleDecl
        };
        classDecl.Properties.Add(property);

        var emissionContext = new ModuleEmissionContext();
        emissionContext.RecordMethodEmissionSymbol(setterMethod, PromotedSymbol);
        var context = ThreadContext(emissionContext);

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger, context);

        var result = csOutput.ToString();
        Assert.Contains($"EntryPoint = \"{PromotedSymbol}\"", result);
        Assert.DoesNotContain("EntryPoint = \"$s10TestModule5count_setter\"", result);
    }

    private static TypeHandlerContext ThreadContext(ModuleEmissionContext emissionContext) =>
        new(PInvokeHelperContext: null,
            DeferredPInvokeHelperContexts: new List<PInvokeHelperContext>(),
            PropertyRenames: null,
            EmissionContext: emissionContext);

    #endregion

    #region Emit / EmitStruct: absent-Apple surface ingress withdrawal

    // A cross-module extension whose signature references an Apple-framework type absent from the
    // .NET binding surface (e.g. SwiftDate's `Date.dateAtStartOf(_: Calendar.Component)`, whose
    // Foundation.Calendar.Component flattens to a phantom Foundation.CalendarComponent). The coarse
    // ClassifyParameterType classifier treats any Foundation type as a marshalable ObjC-class
    // pointer, so pre-fix the emitter printed a `Foundation.CalendarComponent` parameter reference
    // that dangles as CS0234. The surface-authoritative ingress gate must withdraw the member — a
    // structured skip row AND a tombstone — rather than leak the phantom type or drop it silently.

    [Fact]
    public void EmitStruct_MethodWithAbsentAppleParam_WithdrawnAndReported()
    {
        var (csWriter, swiftWriter, csOutput, _, moduleDecl, structDecl, conductor, env)
            = CreateStructSetup(frozen: true, requiresMemoryManagement: false);
        RegisterAbsentAppleType(env.TypeDatabase);
        structDecl.Methods.Add(CreateMethodWithAbsentAppleParam("dateAtStartOf", "TestModule", structDecl));

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, structDecl, moduleDecl, conductor, env, Logger);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        var result = csOutput.ToString();
        // The phantom Apple type is never referenced ...
        Assert.DoesNotContain("Foundation.CalendarComponent", result);
        // ... the lone member was withdrawn, so the empty shell rolls back ...
        Assert.DoesNotContain("public static partial class", result);
        // ... and the withdrawal is reported, not silent.
        Assert.Contains(report!.SkippedItems, s => s.Reason == SkipReason.AbsentFrameworkType);
    }

    [Fact]
    public void EmitStruct_AbsentAppleAmongGoodMembers_WithdrawnWithTombstoneKeepsShell()
    {
        var (csWriter, swiftWriter, csOutput, _, moduleDecl, structDecl, conductor, env)
            = CreateStructSetup(frozen: true, requiresMemoryManagement: false);
        RegisterAbsentAppleType(env.TypeDatabase);
        // A good sibling keeps the extension class shell alive so the withdrawn member's tombstone
        // is observable in the output (not rolled back with an all-unemittable empty shell).
        structDecl.Methods.Add(CreateMethodDecl("doMath", "TestModule", structDecl));
        structDecl.Methods.Add(CreateMethodWithAbsentAppleParam("dateAtStartOf", "TestModule", structDecl));

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, structDecl, moduleDecl, conductor, env, Logger);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        var result = csOutput.ToString();
        // Good sibling still emits its shell ...
        Assert.Contains("OrigPointTestModuleExtensions", result);
        // ... the withdrawn member leaves an // Unsupported: tombstone ...
        Assert.Contains(result.Split('\n'), l => l.Contains("// Unsupported:") && l.Contains("dateAtStartOf"));
        // ... and the phantom type name appears ONLY in that tombstone comment, never as emitted
        // code (a leaked `Foundation.CalendarComponent units` parameter or a `.Handle` marshal).
        var phantomLines = result.Split('\n').Where(l => l.Contains("Foundation.CalendarComponent"));
        Assert.All(phantomLines, l => Assert.StartsWith("//", l.TrimStart()));
        // ... and lands a structured skip row.
        Assert.Contains(report!.SkippedItems, s => s.Reason == SkipReason.AbsentFrameworkType);
    }

    [Fact]
    public void Emit_ClassMethodWithAbsentAppleParam_WithdrawnAndReported()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();
        RegisterAbsentAppleType(env.TypeDatabase);
        classDecl.Methods.Add(CreateMethodWithAbsentAppleParam("configure", "TestModule", classDecl));

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        var result = csOutput.ToString();
        Assert.DoesNotContain("Foundation.CalendarComponent", result);
        // The withdrawn method must not leave an orphan native P/Invoke either.
        Assert.DoesNotContain("public static partial class", result);
        Assert.Contains(report!.SkippedItems, s => s.Reason == SkipReason.AbsentFrameworkType);
    }

    [Fact]
    public void Emit_ClassPropertyWithAbsentAppleType_WithdrawnAndReported()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();
        RegisterAbsentAppleType(env.TypeDatabase);

        var ownerModuleDecl = CreateFullModuleDecl("TestModule");
        var getterMethod = CreateMethodDecl("get_component", "TestModule", classDecl,
            mangledName: "$s10TestModule9component_getter");
        getterMethod.IsAccessor = true;
        var property = new PropertyDecl
        {
            Name = "component",
            SwiftTypeSpec = new NamedTypeSpec(AbsentAppleTypeName),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = classDecl,
            ModuleDecl = ownerModuleDecl
        };
        classDecl.Properties.Add(property);

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        var result = csOutput.ToString();
        Assert.DoesNotContain("Foundation.CalendarComponent", result);
        Assert.DoesNotContain("GetComponent", result);
        Assert.Contains(report!.SkippedItems, s => s.Reason == SkipReason.AbsentFrameworkType);
    }

    [Fact]
    public void EmitStruct_PrimitiveParam_StillEmitsUnderRegisteredAbsentAppleType()
    {
        // Surgical-fix guard: registering an absent Apple type must not withdraw a member whose own
        // signature is all known-good. A primitive-only sibling still emits its shell.
        var (csWriter, swiftWriter, csOutput, _, moduleDecl, structDecl, conductor, env)
            = CreateStructSetup(frozen: true, requiresMemoryManagement: false);
        RegisterAbsentAppleType(env.TypeDatabase);
        structDecl.Methods.Add(CreateMethodDecl("doMath", "TestModule", structDecl));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, structDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        Assert.Contains("OrigPointTestModuleExtensions", result);
    }

    // The shared detector must reach an absent Apple type carried inside a container shape (tuple
    // element, closure argument/return, protocol composition), not just a bare or generic-parameter
    // position. The classifier's own AbsentAppleProjection catch is bypassed for a registered
    // absent type (its cheap outer-name precheck short-circuits), so the direct-flag arm is the only
    // thing that can withdraw it — and it must recurse the same container shapes the classifier does.
    [Fact]
    public void ReferencesAbsentAppleType_AbsentTypeNestedInTuple_Detected()
    {
        var typeDatabase = new TypeDatabase();
        RegisterAbsentAppleType(typeDatabase);
        var tuple = new TupleTypeSpec(new TypeSpec[]
        {
            new NamedTypeSpec(AbsentAppleTypeName),
            new NamedTypeSpec("Swift.Int")
        });

        Assert.True(ExtensionMarshallingHelper.ReferencesAbsentAppleType(tuple, typeDatabase, out var offending));
        Assert.Equal(AbsentAppleTypeName, offending);
    }

    [Fact]
    public void ReferencesAbsentAppleType_AbsentTypeInClosureReturn_Detected()
    {
        var typeDatabase = new TypeDatabase();
        RegisterAbsentAppleType(typeDatabase);
        // (Int) -> Foundation.CalendarComponent — the absent type is the closure's return.
        var closure = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new NamedTypeSpec("Swift.Int")),
            returnType: new NamedTypeSpec(AbsentAppleTypeName));

        Assert.True(ExtensionMarshallingHelper.ReferencesAbsentAppleType(closure, typeDatabase, out var offending));
        Assert.Equal(AbsentAppleTypeName, offending);
    }

    #endregion


    #region Emit: receiver name is minted against the member's own parameters

    [Fact]
    public void Emit_MethodWhoseParameterIsSpelledLikeTheReceiver_KeepsTheParameterAndMovesTheReceiver()
    {
        // `self` is a legal Swift argument label, so a member can project a parameter spelled
        // exactly like the receiver the emitted extension method declares. The parameter is the
        // one a caller can name at the call site, so it is the receiver that has to move.
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        classDecl.Methods.Add(CreateMethodDeclWithIntParam("tagWith", "TestModule", classDecl, "self"));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var declaration = SingleDeclarationOf("TagWith", csOutput.ToString());
        Assert.Equal(1, CountParametersNamed(declaration, "self"));
        Assert.Contains("self)", declaration);
        Assert.DoesNotContain("this OrigModule.OrigType self,", declaration);
    }

    [Fact]
    public void Emit_MethodWithNoNameCollision_DeclaresTheReceiverAsSelf()
    {
        // The mint is idempotent when nothing collides, so the overwhelmingly common member
        // keeps declaring exactly the receiver it always did.
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        classDecl.Methods.Add(CreateMethodDeclWithIntParam("tagWith", "TestModule", classDecl, "amount"));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var declaration = SingleDeclarationOf("TagWith", csOutput.ToString());
        Assert.Contains("this OrigModule.OrigType self", declaration);
        Assert.Contains("amount", declaration);
    }

    [Fact]
    public void EmitStruct_MethodWhoseParameterIsSpelledLikeTheReceiver_KeepsTheParameterAndMovesTheReceiver()
    {
        // Same shape on the struct receiver, which is a separate signature builder.
        var (csWriter, swiftWriter, csOutput, _, moduleDecl, structDecl, conductor, env)
            = CreateStructSetup(frozen: true, requiresMemoryManagement: false);

        structDecl.Methods.Add(CreateMethodDeclWithIntParam("offsetWith", "TestModule", structDecl, "self"));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, structDecl, moduleDecl, conductor, env, Logger);

        var declaration = SingleDeclarationOf("OffsetWith", csOutput.ToString());
        Assert.Equal(1, CountParametersNamed(declaration, "self"));
        Assert.DoesNotContain("this OrigModule.OrigPoint self,", declaration);
    }

    [Fact]
    public void EmitStruct_MethodWithNoNameCollision_DeclaresTheReceiverAsSelf()
    {
        var (csWriter, swiftWriter, csOutput, _, moduleDecl, structDecl, conductor, env)
            = CreateStructSetup(frozen: true, requiresMemoryManagement: false);

        structDecl.Methods.Add(CreateMethodDeclWithIntParam("offsetWith", "TestModule", structDecl, "amount"));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, structDecl, moduleDecl, conductor, env, Logger);

        var declaration = SingleDeclarationOf("OffsetWith", csOutput.ToString());
        Assert.Contains("this OrigModule.OrigPoint self", declaration);
    }

    #endregion

    #region Emit: class receiver returning a resilient struct

    [Fact]
    public void Emit_ClassReceiverReturningResilientStruct_BindsAndReadsTheValueBackOutOfTheBufferItSupplied()
    {
        // A non-@frozen struct return cannot come back in registers — the caller does not know
        // the layout — so the value only ever reaches it through a buffer it allocates and reads
        // the carrier back out of. The member binds rather than being dropped, and every P/Invoke
        // the extension declares is called by a member it emitted.
        var (csWriter, swiftWriter, csOutput, _, moduleDecl, classDecl, conductor, env)
            = CreateClassSetupWithStructReturn(frozen: false, requiresMemoryManagement: false);

        classDecl.Methods.Add(CreateMethodDeclReturningStruct("configTagged", "TestModule", classDecl));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        Assert.Contains("ConfigTagged", result);
        Assert.Contains("SwiftMarshal.MarshalFromSwift<OrigModule.OrigPoint>", result);
        foreach (var declared in DeclaredPInvokeNames(result))
            Assert.Contains($"NativeMethods.{declared}(", result);
    }

    [Fact]
    public void Emit_ClassReceiverReturningResilientStruct_DoesNotPairAnIndirectResultWithAnUntypedSelfRegister()
    {
        // The receiver reaches Swift as an ordinary pointer argument on a generated entry point,
        // not as an untyped value in the self register alongside an indirect result. That pairing
        // was measured to overwrite the receiver object's metadata word, so the shape must not be
        // reachable from this emitter at all.
        var (csWriter, swiftWriter, csOutput, _, moduleDecl, classDecl, conductor, env)
            = CreateClassSetupWithStructReturn(frozen: false, requiresMemoryManagement: false);

        classDecl.Methods.Add(CreateMethodDeclReturningStruct("configTagged", "TestModule", classDecl));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        Assert.DoesNotContain("SwiftIndirectResult", result);
        Assert.DoesNotContain("SwiftSelf", result);
        Assert.Contains("CallConvCdecl", result);
    }

    [Fact]
    public void Emit_ClassReceiverReturningResilientStruct_WritesTheValueThroughTheResultPointerItWasHanded()
    {
        // The Swift side of the boundary takes the buffer as a leading pointer parameter and
        // value-witness-copies the returned struct into it, so any reference fields it carries are
        // retained for the caller instead of being released on the way out.
        var (csWriter, swiftWriter, _, swiftOutput, moduleDecl, classDecl, conductor, env)
            = CreateClassSetupWithStructReturn(frozen: false, requiresMemoryManagement: false);

        classDecl.Methods.Add(CreateMethodDeclReturningStruct("configTagged", "TestModule", classDecl));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var swift = swiftOutput.ToString();
        Assert.Contains("_ resultPtr: UnsafeMutableRawPointer", swift);
        Assert.Contains("resultPtr.initializeMemory(as: OrigModule.OrigPoint.self, repeating:", swift);
    }

    [Fact]
    public void Emit_ClassReceiverReturningFrozenStructWithReferenceFields_DeclinesRatherThanBindAnUnsupportedCarrier()
    {
        // A @frozen struct that carries reference fields (Swift.String is the everyday one) has a
        // layout the caller DOES know: a direct call returns it in registers, and its C# projection
        // is not a carrier that adopts a buffer either. It shares a return classification with the
        // resilient shape above, so the arm has to separate them rather than key on that
        // classification.
        var (csWriter, swiftWriter, csOutput, _, moduleDecl, classDecl, conductor, env)
            = CreateClassSetupWithStructReturn(frozen: true, requiresMemoryManagement: true);

        classDecl.Methods.Add(CreateMethodDeclReturningStruct("configTagged", "TestModule", classDecl));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        Assert.DoesNotContain("ConfigTagged", result);
        Assert.Empty(DeclaredPInvokeNames(result));
    }

    [Fact]
    public void Emit_ClassReceiverReturningNoncopyableResilientStruct_DeclinesRatherThanEmitACopyItCannotMake()
    {
        // Writing the value into the caller's buffer is a value-witness COPY. A noncopyable value
        // has no copy to make, and Swift rejects that at the write rather than at the boundary —
        // so accepting the member here would produce a wrapper that does not compile.
        var (csWriter, swiftWriter, csOutput, _, moduleDecl, classDecl, conductor, env)
            = CreateClassSetupWithStructReturn(frozen: false, requiresMemoryManagement: false, nonCopyable: true);

        classDecl.Methods.Add(CreateMethodDeclReturningStruct("configTagged", "TestModule", classDecl));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        Assert.DoesNotContain("ConfigTagged", result);
        Assert.Empty(DeclaredPInvokeNames(result));
    }

    [Fact]
    public void Emit_ClassReceiverReturningResilientStructWhoseParameterIsSpelledLikeTheResultPointer_DeclaresItOnce()
    {
        // A Swift signature is free to spell a parameter with the same name the arm synthesizes for
        // the caller's buffer. Two parameters under one identifier is a declaration the C# compiler
        // rejects outright, so the synthesized one has to be minted against the member's own names.
        var (csWriter, swiftWriter, csOutput, _, moduleDecl, classDecl, conductor, env)
            = CreateClassSetupWithStructReturn(frozen: false, requiresMemoryManagement: false);

        classDecl.Methods.Add(CreateMethodDeclReturningStruct("configTagged", "TestModule", classDecl, "resultPtr"));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        Assert.Contains("ConfigTagged", result);
        var declaration = Assert.Single(
            result.Split('\n').Select(line => line.Trim()),
            line => line.StartsWith("internal static") && line.Contains(" PInvoke_ConfigTagged"));
        Assert.Equal(1, CountParametersNamed(declaration, "resultPtr"));
    }

    #endregion

    #region Helpers

    private static (CSharpWriter csWriter, SwiftWriter swiftWriter, StringWriter csOutput,
        ModuleDecl moduleDecl, ClassDecl classDecl, Conductor conductor, MethodEnvironment env) CreateSetup()
    {
        var (csWriter, swiftWriter, csOutput, _, moduleDecl, classDecl, conductor, env) = CreateSetupWithSwiftCapture();
        return (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env);
    }

    private static (CSharpWriter csWriter, SwiftWriter swiftWriter, StringWriter csOutput, StringWriter swiftOutput,
        ModuleDecl moduleDecl, ClassDecl classDecl, Conductor conductor, MethodEnvironment env) CreateSetupWithSwiftCapture()
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        // ClassDecl from a different module (OrigModule) — simulates cross-module extension
        var classDecl = new ClassDecl
        {
            Name = "OrigType",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("OrigModule.OrigType"),
            MangledName = "$s10OrigModule8OrigTypeCN",
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

        var conductor = new Conductor(NullLoggerFactory.Instance);
        // Create a dummy method for MethodEnvironment
        var dummyMethod = CreateMethodDecl("_dummy", "TestModule", classDecl);
        var env = new MethodEnvironment(dummyMethod, typeDatabase);

        return (csWriter, swiftWriter, csOutput, swiftOutput, moduleDecl, classDecl, conductor, env);
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

    private static ModuleDecl CreateFullModuleDecl(string name)
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

    private static MethodDecl CreateMethodDecl(string name, string ownerModule, ClassDecl parentDecl,
        string? mangledName = null)
    {
        var ownerModuleDecl = new ModuleDecl
        {
            Name = ownerModule,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        return new MethodDecl
        {
            Name = name,
            MangledName = mangledName ?? $"$s10{ownerModule}{name.Length}{name}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = ownerModuleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = ownerModuleDecl,
            IsSynthesizedAccessor = false
        };
    }

    private static (CSharpWriter csWriter, SwiftWriter swiftWriter, StringWriter csOutput, StringWriter swiftOutput,
        ModuleDecl moduleDecl, StructDecl structDecl, Conductor conductor, MethodEnvironment env)
        CreateStructSetup(bool frozen, bool requiresMemoryManagement)
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabaseWithStruct(frozen, requiresMemoryManagement);

        var structDecl = new StructDecl
        {
            Name = "OrigPoint",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("OrigModule.OrigPoint"),
            MangledName = "$s10OrigModule9OrigPointV",
            MetadataAccessor = "$s10OrigModule9OrigPointVMa",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = frozen
        };

        var conductor = new Conductor(NullLoggerFactory.Instance);
        var dummyMethod = CreateMethodDecl("_dummy", "TestModule", structDecl);
        var env = new MethodEnvironment(dummyMethod, typeDatabase);

        return (csWriter, swiftWriter, csOutput, swiftOutput, moduleDecl, structDecl, conductor, env);
    }

    private static MethodDecl CreateMethodDecl(string name, string ownerModule, StructDecl parentDecl,
        string? mangledName = null)
    {
        var ownerModuleDecl = new ModuleDecl
        {
            Name = ownerModule,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        return new MethodDecl
        {
            Name = name,
            MangledName = mangledName ?? $"$s10{ownerModule}{name.Length}{name}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = ownerModuleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = ownerModuleDecl,
            IsSynthesizedAccessor = false
        };
    }

    private static (CSharpWriter csWriter, SwiftWriter swiftWriter, StringWriter csOutput, StringWriter swiftOutput,
        ModuleDecl moduleDecl, StructDecl structDecl, Conductor conductor, MethodEnvironment env)
        CreateStructSetupWithSimpleEnum(bool frozen, string? rawValueTypeName = "Int32")
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabaseWithStructAndEnum(frozen, rawValueTypeName);

        var structDecl = new StructDecl
        {
            Name = "OrigPoint",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("OrigModule.OrigPoint"),
            MangledName = "$s10OrigModule9OrigPointV",
            MetadataAccessor = "$s10OrigModule9OrigPointVMa",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = frozen
        };

        var conductor = new Conductor(NullLoggerFactory.Instance);
        var dummyMethod = CreateMethodDecl("_dummy", "TestModule", structDecl);
        var env = new MethodEnvironment(dummyMethod, typeDatabase);

        return (csWriter, swiftWriter, csOutput, swiftOutput, moduleDecl, structDecl, conductor, env);
    }

    private static MethodDecl CreateMethodDeclWithEnumParamAndReturn(string name, string ownerModule, StructDecl parentDecl)
    {
        var ownerModuleDecl = new ModuleDecl
        {
            Name = ownerModule,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var enumSpec = new NamedTypeSpec("OrigModule.OrigStatus");

        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10{ownerModule}{name.Length}{name}AA",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                // Return type at index 0
                new ArgumentDecl
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = enumSpec,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = ownerModuleDecl
                },
                // Single enum param
                new ArgumentDecl
                {
                    Name = "status",
                    PrivateName = "status",
                    SwiftTypeSpec = enumSpec,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = ownerModuleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = ownerModuleDecl,
            IsSynthesizedAccessor = false
        };
    }

    private static TypeDatabase CreateTypeDatabaseWithStructAndEnum(bool frozen, string? rawValueTypeName = "Int32")
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
        typeDatabase.AddModuleDatabase(testModule);

        var origModule = new ModuleTypeDatabase("OrigModule", "/tmp/OrigModule.dylib");
        origModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("OrigModule.OrigPoint"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("OrigModule", "OrigPoint"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("OrigModule.OrigPoint"),
                MetadataAccessor = "$s10OrigModule9OrigPointVMa",
                Flags = frozen ? TypeRecordFlags.Frozen : TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            });
        origModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("OrigModule.OrigStatus"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("OrigModule", "OrigStatus"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("OrigModule.OrigStatus"),
                MetadataAccessor = "$s10OrigModule10OrigStatusOMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = rawValueTypeName
            });
        typeDatabase.AddModuleDatabase(origModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithStruct(bool frozen, bool requiresMemoryManagement)
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
        typeDatabase.AddModuleDatabase(testModule);

        var flags = TypeRecordFlags.None;
        if (frozen) flags |= TypeRecordFlags.Frozen;
        if (requiresMemoryManagement) flags |= TypeRecordFlags.RequiresMemoryManagement;
        var origModule = new ModuleTypeDatabase("OrigModule", "/tmp/OrigModule.dylib");
        origModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("OrigModule.OrigPoint"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("OrigModule", "OrigPoint"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("OrigModule.OrigPoint"),
                MetadataAccessor = "$s10OrigModule9OrigPointVMa",
                Flags = flags,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(origModule);
        return typeDatabase;
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
        typeDatabase.AddModuleDatabase(testModule);

        var origModule = new ModuleTypeDatabase("OrigModule", "/tmp/OrigModule.dylib");
        origModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("OrigModule.OrigType"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("OrigModule", "OrigType"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("OrigModule.OrigType"),
                MetadataAccessor = "$s10OrigModule8OrigTypeCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(origModule);
        return typeDatabase;
    }

    private const string AbsentAppleTypeName = "Foundation.CalendarComponent";

    // Registers an Apple-framework type the .NET binding surface does not declare, synthesized as an
    // ObjC-bridged class record flagged AbsentAppleProjection — exactly what
    // TypeDatabaseExtensions.CreateAbsentAppleRecord produces for a surface-absent type such as
    // Foundation.Calendar.Component. Because ClassifyParameterType treats any Foundation type as a
    // marshalable ObjC-class pointer, without the ingress gate the emitter would print a reference
    // to a type Microsoft.iOS never declares (CS0234).
    private static void RegisterAbsentAppleType(ITypeDatabase typeDatabase)
    {
        var foundation = new ModuleTypeDatabase("Foundation", "/usr/lib/swift/libswiftFoundation.dylib");
        foundation.RegisterType(
            SwiftTypeName.FromModuleQualifiedName(AbsentAppleTypeName),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "CalendarComponent"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(AbsentAppleTypeName),
                MetadataAccessor = string.Empty,
                Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement | TypeRecordFlags.AbsentAppleProjection,
                Kind = TypeRecordKind.Class
            });
        ((TypeDatabase)typeDatabase).AddModuleDatabase(foundation);
    }

    // An instance method returning void with a single parameter typed as the absent Apple type.
    // Mirrors the shape of SwiftDate's Date.dateAtStartOf(_: Calendar.Component) extension.
    private static MethodDecl CreateMethodWithAbsentAppleParam(string name, string ownerModule, TypeDecl parentDecl)
    {
        var ownerModuleDecl = CreateFullModuleDecl(ownerModule);
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10{ownerModule}{name.Length}{name}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                // Return type at index 0 — void.
                new ArgumentDecl
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = ownerModuleDecl
                },
                // Single absent-Apple-typed parameter.
                new ArgumentDecl
                {
                    Name = "units",
                    PrivateName = "units",
                    SwiftTypeSpec = new NamedTypeSpec(AbsentAppleTypeName),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = ownerModuleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = ownerModuleDecl,
            IsSynthesizedAccessor = false
        };
    }


    // Returns the single emitted declaration line whose name matches, so a per-parameter
    // assertion reads one signature rather than the whole emitted file.
    private static string SingleDeclarationOf(string emittedName, string output)
    {
        var matches = output
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("public static") && line.Contains($" {emittedName}("))
            .ToList();
        Assert.Single(matches);
        return matches[0];
    }

    // Counts parameters declared under exactly this identifier in one signature. Two of them is
    // the duplicate-parameter shape the C# compiler rejects outright (CS0100).
    private static int CountParametersNamed(string declaration, string parameterName)
    {
        var open = declaration.IndexOf('(');
        var close = declaration.LastIndexOf(')');
        var parameterList = declaration.Substring(open + 1, close - open - 1);
        return parameterList
            .Split(',')
            .Count(part => part.Trim().EndsWith($" {parameterName}"));
    }

    // Every P/Invoke the extension's NativeMethods block declares.
    private static IEnumerable<string> DeclaredPInvokeNames(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.Contains("partial") || !line.Contains("PInvoke_"))
                continue;
            var start = line.IndexOf("PInvoke_");
            var end = line.IndexOf('(', start);
            if (end > start)
                yield return line.Substring(start, end - start);
        }
    }

    private static MethodDecl CreateMethodDeclWithIntParam(
        string name, string ownerModule, TypeDecl parentDecl, string parameterName)
    {
        var ownerModuleDecl = CreateFullModuleDecl(ownerModule);
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10{ownerModule}{name.Length}{name}ySiF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = ownerModuleDecl
                },
                new ArgumentDecl
                {
                    Name = parameterName,
                    PrivateName = parameterName,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = ownerModuleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = ownerModuleDecl,
            IsSynthesizedAccessor = false
        };
    }

    // An instance method returning the struct registered by CreateTypeDatabaseWithStruct, with
    // three primitive parameters spelled like the locals the resilient-return arm generates.
    private static MethodDecl CreateMethodDeclReturningStruct(
        string name, string ownerModule, TypeDecl parentDecl, params string[] parameterNames)
    {
        var ownerModuleDecl = CreateFullModuleDecl(ownerModule);
        var parameters = (parameterNames.Length > 0 ? parameterNames : new[] { "metadata", "buffer", "indirectResult" })
            .Select(parameterName => new ArgumentDecl
            {
                Name = parameterName,
                PrivateName = parameterName,
                SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = ownerModuleDecl
            });

        var signature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                Name = string.Empty,
                PrivateName = string.Empty,
                SwiftTypeSpec = new NamedTypeSpec("OrigModule.OrigPoint"),
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = ownerModuleDecl
            }
        };
        signature.AddRange(parameters);

        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10{ownerModule}{name.Length}{name}y10OrigModule9OrigPointVSi_S2itF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = signature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = ownerModuleDecl,
            IsSynthesizedAccessor = false
        };
    }

    // A CLASS receiver paired with the struct type database, so a class-receiver member can be
    // given a struct return. CreateStructSetup pairs that database with a struct receiver.
    private static (CSharpWriter csWriter, SwiftWriter swiftWriter, StringWriter csOutput, StringWriter swiftOutput,
        ModuleDecl moduleDecl, ClassDecl classDecl, Conductor conductor, MethodEnvironment env)
        CreateClassSetupWithStructReturn(bool frozen, bool requiresMemoryManagement, bool nonCopyable = false)
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var moduleDecl = CreateModuleDecl();

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
        typeDatabase.AddModuleDatabase(new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib"));

        var structFlags = TypeRecordFlags.None;
        if (frozen) structFlags |= TypeRecordFlags.Frozen;
        if (requiresMemoryManagement) structFlags |= TypeRecordFlags.RequiresMemoryManagement;
        if (nonCopyable) structFlags |= TypeRecordFlags.NonCopyable;
        var origModule = new ModuleTypeDatabase("OrigModule", "/tmp/OrigModule.dylib");
        origModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("OrigModule.OrigType"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("OrigModule", "OrigType"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("OrigModule.OrigType"),
                MetadataAccessor = "$s10OrigModule8OrigTypeCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        origModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("OrigModule.OrigPoint"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("OrigModule", "OrigPoint"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("OrigModule.OrigPoint"),
                MetadataAccessor = "$s10OrigModule9OrigPointVMa",
                Flags = structFlags,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(origModule);

        var classDecl = new ClassDecl
        {
            Name = "OrigType",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("OrigModule.OrigType"),
            MangledName = "$s10OrigModule8OrigTypeCN",
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

        var conductor = new Conductor(NullLoggerFactory.Instance);
        var env = new MethodEnvironment(CreateMethodDecl("_dummy", "TestModule", classDecl), typeDatabase);
        return (csWriter, swiftWriter, csOutput, swiftOutput, moduleDecl, classDecl, conductor, env);
    }

    #endregion
}
