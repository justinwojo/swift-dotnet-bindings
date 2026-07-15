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

    #region Emit: calling convention — all cross-module P/Invokes use CallConvSwift

    [Fact]
    public void Emit_MethodPInvoke_UsesCallConvSwift()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        classDecl.Methods.Add(CreateMethodDecl("doAction", "TestModule", classDecl));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        // Cross-module extension P/Invokes always use CallConvSwift because both direct
        // symbols and @_silgen_name wrappers use swiftcc. SwiftSelf (x20) and
        // SwiftIndirectResult (x8) only map to correct registers under swiftcc.
        Assert.Contains("CallConvSwift", result);
        Assert.DoesNotContain("CallConvCdecl", result);
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

    #endregion
}
