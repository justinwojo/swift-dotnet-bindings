// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for AbiContractChecker — post-generation ABI contract validation.
/// Verifies CC-001, CC-002, CC-003, CC-004, and Tj thunk cross-module checks,
/// including the 4 checker refinements.
/// </summary>
public class AbiContractCheckerTests
{
    private static readonly ILogger TestLogger = NullLoggerFactory.Instance.CreateLogger("Test");

    #region CC-001: ABI-incompatible param carrier in CallConvSwift

    [Theory]
    [InlineData("string name")]
    [InlineData("global::System.String name")]
    public void CC001_ManagedStringParamInCallConvSwift_DetectsViolation(string parameters)
    {
        // A managed string marshals to a pointer to a C string — one word. Swift reads a
        // String parameter as a two-word _StringObject, so no Swift signature makes the
        // pairing correct and it is judgeable from the declared C# type alone.
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule5MyFoo3barSiAA0C0CHF",
            returnType: "int",
            methodName: "PInvoke_bar_ABC123",
            parameters: parameters);

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.Single(result.Violations);
        Assert.Equal("SWIFTBIND090", result.Violations[0].DiagnosticCode);
        Assert.Equal("CC-001", result.Violations[0].RuleId);
        Assert.Equal("PInvoke_bar_ABC123", result.Violations[0].MethodName);
        Assert.Equal("$s10TestModule5MyFoo3barSiAA0C0CHF", result.Violations[0].EntryPoint);
        Assert.Contains("name", result.Violations[0].AffectedElements[0]);
    }

    [Theory]
    [InlineData("SafeHandle handle, SwiftSelf<IntPtr> self")]
    [InlineData("SwiftSafeHandle handle, SwiftSelf<IntPtr> self")]
    [InlineData("SwiftClassHandle handle, SwiftSelf<IntPtr> self")]
    public void CC001_SafeHandleParam_NoViolation(string parameters)
    {
        // Discrimination control. Every P/Invoke here is [LibraryImport]: its source
        // generator marshals a SafeHandle in managed code (DangerousGetHandle) and the
        // extern that actually carries [UnmanagedCallConv(CallConvSwift)] takes an nint —
        // exactly the pointer Swift expects for a class reference or a resilient value's
        // address. Judging the DECLARED type here is what made this rule report every
        // opaque-payload binding in the corpus.
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule5MyFoo3barSiAA0C0CHF",
            returnType: "int",
            methodName: "PInvoke_bar_ABC123",
            parameters: parameters);

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void CC001_BlittableOnlyParams_NoViolation()
    {
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule5MyFoo3barSiAA0C0CHF",
            returnType: "int",
            methodName: "PInvoke_bar_ABC123",
            parameters: "int value, SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void CC001_CallConvCdecl_NotChecked()
    {
        // Convention gate. The same managed string that trips CC-001 under CallConvSwift is
        // correct under cdecl: a @_cdecl wrapper declares a C pointer parameter, which is
        // precisely what the string marshals to.
        var csOutput = BuildPInvoke(
            callConv: "CallConvCdecl",
            library: "TestModuleSwiftBindings",
            entryPoint: "SBW_TestModule_MyFoo_bar_ABC123",
            returnType: "void",
            methodName: "PInvoke_bar_ABC123",
            parameters: "string name");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        // No CC-001 violation (cdecl is fine with non-blittable)
        Assert.DoesNotContain(result.Violations, v => v.RuleId == "CC-001");
    }

    #endregion

    #region CC-002: ABI-incompatible return carrier in CallConvSwift

    [Fact]
    public void CC002_ManagedStringReturn_DetectsViolation()
    {
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule5MyFoo3getSS0C0CHF",
            returnType: "string",
            methodName: "PInvoke_get_ABC123",
            parameters: "SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        var cc002 = result.Violations.Where(v => v.RuleId == "CC-002").ToList();
        Assert.Single(cc002);
        Assert.Equal("SWIFTBIND091", cc002[0].DiagnosticCode);
        Assert.Equal("PInvoke_get_ABC123", cc002[0].MethodName);
        Assert.Contains("return:", cc002[0].AffectedElements[0]);
    }

    [Fact]
    public void CC002_SafeHandleReturn_NoViolation()
    {
        // Discrimination control, mirroring CC001_SafeHandleParam_NoViolation on the result
        // side: the LibraryImport-generated extern returns the nint that Swift hands back.
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule5MyFoo3getSS0C0CHF",
            returnType: "SwiftSafeHandle",
            methodName: "PInvoke_get_ABC123",
            parameters: "SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.DoesNotContain(result.Violations, v => v.RuleId == "CC-002");
    }

    [Fact]
    public void CC002_BlittableReturn_NoViolation()
    {
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule5MyFoo3getSiAA0C0CHF",
            returnType: "int",
            methodName: "PInvoke_get_ABC123",
            parameters: "SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.DoesNotContain(result.Violations, v => v.RuleId == "CC-002");
    }

    [Fact]
    public void CC002_VoidReturn_NoViolation()
    {
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule5doItyyF",
            returnType: "void",
            methodName: "PInvoke_doIt_ABC123",
            parameters: "");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.DoesNotContain(result.Violations, v => v.RuleId == "CC-002");
    }

    [Fact]
    public void CC002_ExistentialContainerReturn_NoViolation()
    {
        // ExistentialContainer types are blittable — should not trigger CC-002
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule18makeOpaqueItemyAA0C0CHF_opaque",
            returnType: "ExistentialContainer1",
            methodName: "PInvoke_makeOpaqueItem_ABC123",
            parameters: "SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.DoesNotContain(result.Violations, v => v.RuleId == "CC-002");
    }

    #endregion

    #region CC-003: @_cdecl wrapper targeting wrong library

    [Fact]
    public void CC003_SbwEntryPointTargetingOriginalLib_DetectsViolation()
    {
        var csOutput = BuildPInvoke(
            callConv: "CallConvCdecl",
            library: "TestModule",       // Wrong! Should be wrapper lib
            entryPoint: "SBW_TestModule_MyFoo_bar_ABC123",
            returnType: "void",
            methodName: "PInvoke_bar_ABC123",
            parameters: "IntPtr self");

        var result = AbiContractChecker.Validate(
            csOutput, "TestModule", TestLogger, wrapperLibraryName: "TestModuleSwiftBindings");

        var cc003 = result.Violations.Where(v => v.RuleId == "CC-003").ToList();
        Assert.Single(cc003);
        Assert.Equal("SWIFTBIND093", cc003[0].DiagnosticCode);
    }

    [Fact]
    public void CC003_SbwEntryPointTargetingWrapperLib_NoViolation()
    {
        var csOutput = BuildPInvoke(
            callConv: "CallConvCdecl",
            library: "TestModuleSwiftBindings",
            entryPoint: "SBW_TestModule_MyFoo_bar_ABC123",
            returnType: "void",
            methodName: "PInvoke_bar_ABC123",
            parameters: "IntPtr self");

        var result = AbiContractChecker.Validate(
            csOutput, "TestModule", TestLogger, wrapperLibraryName: "TestModuleSwiftBindings");

        Assert.DoesNotContain(result.Violations, v => v.RuleId == "CC-003");
    }

    [Fact]
    public void CC003_SbwEntryPointTargetingADifferentWrapperShapedLib_DetectsViolation()
    {
        // With a wrapper configured, it is the SOLE wrapper. A symbol misrouted to some other
        // library that merely LOOKS like a wrapper is exactly the EntryPointNotFoundException
        // this rule exists for, so the name-shape fallback must not rescue it.
        var csOutput = BuildPInvoke(
            callConv: "CallConvCdecl",
            library: "OtherModuleSwiftBindings",
            entryPoint: "SBW_TestModule_MyFoo_bar_ABC123",
            returnType: "void",
            methodName: "PInvoke_bar_ABC123",
            parameters: "IntPtr self");

        var result = AbiContractChecker.Validate(
            csOutput, "TestModule", TestLogger, wrapperLibraryName: "CustomWrapperLib");

        Assert.Contains(result.Violations, v => v.RuleId == "CC-003");
    }

    [Fact]
    public void CC003_NoWrapperLibraryConfigured_NoViolation()
    {
        // Discrimination control for the rule's precondition. With no companion wrapper
        // (direct mode), the generator binds SBW_ symbols against the module's own library
        // by design — "If null, uses module library". There is no wrong library to point
        // at, so the rule has nothing to assert and must stay silent rather than report
        // every binding produced in that mode.
        var csOutput = BuildPInvoke(
            callConv: "CallConvCdecl",
            library: "/fake/TestModule.dylib",
            entryPoint: "SBW_TestModule_MyFoo_bar_ABC123",
            returnType: "void",
            methodName: "PInvoke_bar_ABC123",
            parameters: "IntPtr self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.DoesNotContain(result.Violations, v => v.RuleId == "CC-003");
    }

    [Fact]
    public void CC003_SbwEntryPointTargetingConfiguredWrapperLib_NoViolation()
    {
        // Discrimination control for wrapper identity. The wrapper library name is
        // configurable, so a name that does not end in "SwiftBindings" is still the
        // wrapper — recognizing it only by that suffix reports a correct binding.
        var csOutput = BuildPInvoke(
            callConv: "CallConvCdecl",
            library: "CustomWrapperLib",
            entryPoint: "SBW_TestModule_MyFoo_bar_ABC123",
            returnType: "void",
            methodName: "PInvoke_bar_ABC123",
            parameters: "IntPtr self");

        var result = AbiContractChecker.Validate(
            csOutput, "TestModule", TestLogger, wrapperLibraryName: "CustomWrapperLib");

        Assert.DoesNotContain(result.Violations, v => v.RuleId == "CC-003");
    }

    [Fact]
    public void CC003_SbwEntryPointTargetingOtherLib_ConfiguredWrapper_DetectsViolation()
    {
        // The positive half of the control above: with the wrapper named, a SBW_ symbol
        // bound anywhere else is still the EntryPointNotFoundException this rule exists for.
        var csOutput = BuildPInvoke(
            callConv: "CallConvCdecl",
            library: "TestModule",
            entryPoint: "SBW_TestModule_MyFoo_bar_ABC123",
            returnType: "void",
            methodName: "PInvoke_bar_ABC123",
            parameters: "IntPtr self");

        var result = AbiContractChecker.Validate(
            csOutput, "TestModule", TestLogger, wrapperLibraryName: "CustomWrapperLib");

        var cc003 = result.Violations.Where(v => v.RuleId == "CC-003").ToList();
        Assert.Single(cc003);
        Assert.Equal("SWIFTBIND093", cc003[0].DiagnosticCode);
        Assert.Contains("TestModule", cc003[0].AffectedElements[0]);
    }

    #endregion

    #region CC-004: CallConvCdecl targeting mangled symbol

    [Fact]
    public void CC004_CdeclWithMangledSymbol_DetectsViolation()
    {
        var csOutput = BuildPInvoke(
            callConv: "CallConvCdecl",
            library: "TestModule",
            entryPoint: "$s10TestModule5MyFoo3barSiAA0C0CHF",
            returnType: "int",
            methodName: "PInvoke_bar_ABC123",
            parameters: "int value");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        var cc004 = result.Violations.Where(v => v.RuleId == "CC-004").ToList();
        Assert.Single(cc004);
        Assert.Equal("SWIFTBIND094", cc004[0].DiagnosticCode);
    }

    [Fact]
    public void CC004_CdeclWithSbwEntryPoint_NoViolation()
    {
        var csOutput = BuildPInvoke(
            callConv: "CallConvCdecl",
            library: "TestModuleSwiftBindings",
            entryPoint: "SBW_TestModule_bar_ABC123",
            returnType: "int",
            methodName: "PInvoke_bar_ABC123",
            parameters: "int value");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.DoesNotContain(result.Violations, v => v.RuleId == "CC-004");
    }

    #endregion

    #region Tj Thunk Cross-Module Detection

    [Fact]
    public void TjThunk_SymbolModuleDiffersFromBoundLibrary_DetectsViolation()
    {
        // The thunk is exported by the dylib of the module that declares the class, so a
        // symbol naming "OtherModule" bound against library "TestModule" resolves to nothing.
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s11OtherModule5MyFoo3barSiAA0C0CHFTj",
            returnType: "int",
            methodName: "PInvoke_bar_ABC123",
            parameters: "SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        var tj = result.Violations.Where(v => v.RuleId == "Tj-XM").ToList();
        Assert.Single(tj);
        Assert.Equal("SWIFTBIND092", tj[0].DiagnosticCode);
        Assert.Equal("$s11OtherModule5MyFoo3barSiAA0C0CHFTj", tj[0].EntryPoint);
        Assert.Contains(tj[0].AffectedElements, e => e.Contains("OtherModule"));
        Assert.Contains(tj[0].AffectedElements, e => e.Contains("TestModule"));
    }

    [Fact]
    public void TjThunk_SameModule_NoViolation()
    {
        // Entry point encodes "TestModule" and targets "TestModule" — correct
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule5MyFoo3barSiAA0C0CHFTj",
            returnType: "int",
            methodName: "PInvoke_bar_ABC123",
            parameters: "SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.DoesNotContain(result.Violations, v => v.RuleId == "Tj-XM");
    }

    [Theory]
    [InlineData("/tmp/build/MyLib.dylib")]
    [InlineData("/tmp/build/libMyLib.dylib")]
    [InlineData("@rpath/MyLib.framework/MyLib")]
    [InlineData("/System/Library/Frameworks/MyLib.framework/MyLib")]
    public void TjThunk_PathFormLibraryNamingTheSameModule_NoViolation(string library)
    {
        // The generator embeds whatever library name it was given, and with none supplied it
        // falls back to the dylib path. A path naming the very module that exports the thunk
        // is a correct binding; comparing it to the bare module name as raw text reports it,
        // which under a blocking gate fails a build that was fine.
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: library,
            entryPoint: "$s5MyLib3FooC3barSiyFTj",
            returnType: "int",
            methodName: "PInvoke_bar_ABC123",
            parameters: "SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, "MyLib", TestLogger);

        Assert.DoesNotContain(result.Violations, v => v.RuleId == "Tj-XM");
    }

    [Fact]
    public void TjThunk_PathFormLibraryNamingAnotherModule_DetectsViolation()
    {
        // The positive half: reducing a path to its identity must not blunt the rule — a thunk
        // exported by OtherModule bound against MyLib's dylib is still unresolvable.
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "/tmp/build/libMyLib.dylib",
            entryPoint: "$s11OtherModule5MyFoo3barSiAA0C0CHFTj",
            returnType: "int",
            methodName: "PInvoke_bar_ABC123",
            parameters: "SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, "MyLib", TestLogger);

        Assert.Contains(result.Violations, v => v.RuleId == "Tj-XM");
    }

    [Theory]
    [InlineData("/usr/lib/swift/libswiftDispatch.dylib", "Dispatch", "$s8Dispatch3FooC3barSiyFTj")]
    [InlineData("/usr/lib/swift/libswift_Concurrency.dylib", "_Concurrency", "$s12_Concurrency3FooC3barSiyFTj")]
    [InlineData("/tmp/out/library.dylib", "library", "$s7library3FooC3barSiyFTj")]
    [InlineData("@rpath/libMyLib.1.dylib", "MyLib", "$s5MyLib3FooC3barSiyFTj")]
    public void TjThunk_PathFormLibraryUnderAnyPrefixOrVersion_NoViolation(
        string library, string module, string entryPoint)
    {
        // End-to-end through Validate for the library spellings a single reduction rule cannot
        // serve at once: Swift's own overlays are "libswift" + module, an ordinary library is
        // "lib" + module, and a module may itself be named "library". Each of these binds its
        // OWN thunk, so blocking any of them fails a correct binding.
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: library,
            entryPoint: entryPoint,
            returnType: "int",
            methodName: "PInvoke_bar_ABC123",
            parameters: "SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, module, TestLogger);

        Assert.DoesNotContain(result.Violations, v => v.RuleId == "Tj-XM");
    }

    [Theory]
    [InlineData("MyLib", "MyLib")]
    [InlineData("/tmp/MyLib.framework/MyLib", "MyLib")]
    [InlineData("/tmp/libMyLib.dylib", "MyLib")]
    [InlineData("@rpath/libMyLib.dylib", "MyLib")]
    [InlineData("libMyLib.tbd", "MyLib")]
    // Swift's own overlays prefix the module with "libswift" rather than "lib". Both spellings
    // are shipped in this repo's type databases as the modulePath for these very modules.
    [InlineData("/usr/lib/swift/libswiftDispatch.dylib", "Dispatch")]
    [InlineData("/usr/lib/swift/libswift_Concurrency.dylib", "_Concurrency")]
    // A module whose own name starts with "lib" must survive the prefix reading.
    [InlineData("/tmp/out/library.dylib", "library")]
    [InlineData("library", "library")]
    [InlineData("libDispatch", "libDispatch")]
    // Versioned install names, spelled either way round.
    [InlineData("@rpath/libMyLib.1.dylib", "MyLib")]
    [InlineData("@rpath/libMyLib.dylib.1", "MyLib")]
    [InlineData("libMyLib.1.2.3.dylib", "MyLib")]
    public void LibraryIdentity_AcceptsEverySpellingOfItsOwnModulesLibrary(string library, string module)
    {
        Assert.True(AbiContractChecker.LibraryIdentityMatchesModule(library, module));
    }

    [Theory]
    [InlineData("MyLib", "OtherModule")]
    [InlineData("/tmp/build/libMyLib.dylib", "OtherModule")]
    [InlineData("/usr/lib/swift/libswiftDispatch.dylib", "Foundation")]
    [InlineData("", "MyLib")]
    public void LibraryIdentity_RejectsALibraryThatNamesADifferentModule(string library, string module)
    {
        // The discrimination control: accepting more spellings must not make the predicate
        // accept a library that names some other module, which is the bug the rule exists for.
        Assert.False(AbiContractChecker.LibraryIdentityMatchesModule(library, module));
    }

    [Fact]
    public void TjThunk_DependencyThunkBoundToDependencyLibrary_NoViolation()
    {
        // Discrimination control for the rule's key. A binding for "TestModule" legitimately
        // calls a dependency's dispatch thunk THROUGH that dependency's library: symbol
        // module and bound library agree, and only the EMITTING module differs. Keying the
        // comparison on the emitting module reports every such call.
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "OtherModule",
            entryPoint: "$s11OtherModule5MyFoo3barSiAA0C0CHFTj",
            returnType: "int",
            methodName: "PInvoke_bar_ABC123",
            parameters: "SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.DoesNotContain(result.Violations, v => v.RuleId == "Tj-XM");
    }

    [Fact]
    public void TjThunk_NotATjSuffix_NoViolation()
    {
        // Regular mangled symbol (no Tj suffix) — not a dispatch thunk
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s11OtherModule5MyFoo3barSiAA0C0CHF",
            returnType: "int",
            methodName: "PInvoke_bar_ABC123",
            parameters: "SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.DoesNotContain(result.Violations, v => v.RuleId == "Tj-XM");
    }

    #endregion

    #region De-duplication by (RuleId, MethodName)

    [Fact]
    public void DuplicateViolationsSamePInvoke_Deduplicated()
    {
        // Two P/Invoke blocks with the same method name (partial class declarations)
        // should only produce one violation per (RuleId, MethodName)
        var csOutput =
            BuildPInvoke(
                callConv: "CallConvSwift",
                library: "TestModule",
                entryPoint: "$s10TestModule5MyFoo3barSiAA0C0CHF",
                returnType: "int",
                methodName: "PInvoke_bar_ABC123",
                parameters: "string name") +
            "\n" +
            BuildPInvoke(
                callConv: "CallConvSwift",
                library: "TestModule",
                entryPoint: "$s10TestModule5MyFoo3barSiAA0C0CHF",
                returnType: "int",
                methodName: "PInvoke_bar_ABC123",
                parameters: "string name");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        // Should be deduplicated to 1
        var cc001 = result.Violations.Where(v => v.RuleId == "CC-001").ToList();
        Assert.Single(cc001);
    }

    #endregion

    #region Carrier classification: primitives, closure context, async wrappers

    [Fact]
    public void PrimitiveTypes_NotFlagged()
    {
        // "double", "float", "int" etc. should not be considered non-blittable
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule5MyFoo6doCalcSdSd_SftF",
            returnType: "double",
            methodName: "PInvoke_doCalc_ABC123",
            parameters: "double value, float ratio");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void ClosureContext_AdjacentToFuncPtr_NotFlagged()
    {
        // IntPtr context adjacent to delegate* funcPtr should not trigger CC-001
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule5MyFoo7withFunSiAA0C0CHF",
            returnType: "int",
            methodName: "PInvoke_withFun_ABC123",
            parameters: "delegate* unmanaged[Cdecl]<IntPtr, int> closureFuncPtr, IntPtr closureContext, SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        // closureContext is adjacent to funcPtr — should be excluded from CC-001
        Assert.DoesNotContain(result.Violations, v => v.RuleId == "CC-001");
    }

    [Fact]
    public void NonClosureContext_IntPtrNotAdjacentToFuncPtr_NotFlagged()
    {
        // IntPtr context that is NOT adjacent to a funcPtr — still IntPtr so blittable
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule5MyFoo7doThingSiAA0C0CHF",
            returnType: "int",
            methodName: "PInvoke_doThing_ABC123",
            parameters: "IntPtr apiContext, SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        // IntPtr is blittable regardless, so no violation
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void AsyncCdeclWrapper_NoViolations()
    {
        // Entry point contains _async → correctly classified as async
        var csOutput = BuildPInvoke(
            callConv: "CallConvCdecl",
            library: "TestModuleSwiftBindings",
            entryPoint: "SBW_TestModule_MyFoo_fetchData_async_ABC123",
            returnType: "void",
            methodName: "PInvoke_fetchData_ABC123",
            parameters: "IntPtr self, delegate* unmanaged[Cdecl]<IntPtr, void> successCallback, delegate* unmanaged[Cdecl]<IntPtr, void> errorCallback, IntPtr taskContext");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        // Async cdecl targeting wrapper lib — no violations
        Assert.Empty(result.Violations);
    }

    #endregion

    #region Intentional _opaque patterns (no false positives)

    [Fact]
    public void OpaquePatterns_ExistentialContainerReturn_NoFalsePositive()
    {
        // _opaque suffix returns ExistentialContainer1 via CallConvSwift — intentional, not a violation
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule20makeOpaqueDescribableyAA0D0CHF_opaque",
            returnType: "ExistentialContainer1",
            methodName: "PInvoke_makeOpaqueDescribable_ABC123",
            parameters: "SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void OpaquePatterns_ExistentialContainer2Return_NoFalsePositive()
    {
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule22makeOpaqueCompositionyAA0D0CHF_opaque",
            returnType: "ExistentialContainer2",
            methodName: "PInvoke_makeOpaqueComposition_ABC123",
            parameters: "SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.Empty(result.Violations);
    }

    #endregion

    #region P/Invoke Extraction

    [Fact]
    public void ExtractPInvokes_SinglePInvoke_ExtractsCorrectly()
    {
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule5doItSiyF",
            returnType: "int",
            methodName: "PInvoke_doIt_ABC123",
            parameters: "int value, SwiftSelf<IntPtr> self");

        var pinvokes = AbiContractChecker.ExtractPInvokes(csOutput, "TestModule");

        Assert.Single(pinvokes);
        Assert.Equal("PInvoke_doIt_ABC123", pinvokes[0].MethodName);
        Assert.Equal("$s10TestModule5doItSiyF", pinvokes[0].EntryPoint);
        Assert.Equal("CallConvSwift", pinvokes[0].CallingConvention);
        Assert.Equal("int", pinvokes[0].ReturnType);
        Assert.Equal(2, pinvokes[0].Parameters.Length);
    }

    [Fact]
    public void ExtractPInvokes_MultiplePInvokes_ExtractsAll()
    {
        var csOutput =
            BuildPInvoke("CallConvSwift", "TestModule", "$s10TestModule2f1SiyF", "int", "PInvoke_f1_A", "int x") +
            "\n" +
            BuildPInvoke("CallConvCdecl", "TestModuleSwiftBindings", "SBW_TestModule_f2_B", "void", "PInvoke_f2_B", "IntPtr self");

        var pinvokes = AbiContractChecker.ExtractPInvokes(csOutput, "TestModule");

        Assert.Equal(2, pinvokes.Length);
    }

    [Fact]
    public void ExtractModuleFromMangledSymbol_ValidSymbols()
    {
        Assert.Equal("TestModule", AbiContractChecker.ExtractModuleFromMangledSymbol("$s10TestModule5doItyyF"));
        Assert.Equal("ImagePipeline", AbiContractChecker.ExtractModuleFromMangledSymbol("$s13ImagePipeline11ImageLoaderC7loadFooyyF"));
        Assert.Equal("PaymentSdkPayments", AbiContractChecker.ExtractModuleFromMangledSymbol("$s18PaymentSdkPayments3FooCfdTj"));
    }

    [Fact]
    public void ExtractModuleFromMangledSymbol_InvalidSymbols()
    {
        Assert.Null(AbiContractChecker.ExtractModuleFromMangledSymbol("SBW_TestModule_foo"));
        Assert.Null(AbiContractChecker.ExtractModuleFromMangledSymbol("sbw_witness_get_foo"));
        Assert.Null(AbiContractChecker.ExtractModuleFromMangledSymbol("$s"));
    }

    #endregion

    #region Clean output (no violations)

    [Fact]
    public void CleanOutput_TypicalCallConvSwift_NoViolations()
    {
        // Typical blittable CallConvSwift P/Invoke — should be clean
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule9factorialSiSiF",
            returnType: "nint",
            methodName: "PInvoke_factorial_ABC123",
            parameters: "nint n");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.True(result.IsClean);
        Assert.Equal(1, result.PInvokeCount);
    }

    [Fact]
    public void CleanOutput_TypicalCdeclWrapper_NoViolations()
    {
        // Typical @_cdecl wrapper — should be clean
        var csOutput = BuildPInvoke(
            callConv: "CallConvCdecl",
            library: "TestModuleSwiftBindings",
            entryPoint: "SBW_TestModule_MyFoo_setName_ABC123",
            returnType: "void",
            methodName: "PInvoke_setName_ABC123",
            parameters: "IntPtr namePtr, nint nameLen, IntPtr self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.True(result.IsClean);
    }

    [Fact]
    public void CleanOutput_EmptyInput_NoViolations()
    {
        var result = AbiContractChecker.Validate("// empty file", "TestModule", TestLogger);

        Assert.True(result.IsClean);
        Assert.Equal(0, result.PInvokeCount);
    }

    #endregion

    #region Blittability classification edge cases

    [Fact]
    public void Blittability_SwiftStringBuffer_IsBlittable()
    {
        // SwiftString.Buffer (PayloadBuffer) is a blittable 16-byte struct —
        // must NOT be flagged as non-blittable despite containing "SwiftString" in name
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule5MyFoo7describeSiAA0C0CHF",
            returnType: "Swift.SwiftString.Buffer",
            methodName: "PInvoke_describe_ABC123",
            parameters: "Swift.SwiftString.Buffer input, SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        // No violations — Buffer is blittable
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Blittability_SwiftStringParam_NotReportedByThisChecker()
    {
        // SwiftString is a managed class, so [LibraryImport] has no marshaller for it and
        // the C# compiler rejects the declaration outright. Reporting an ABI fault here
        // claims a runtime failure for source that never compiles — a shape the emitter
        // cannot ship. What the compiler already rejects is not this checker's job.
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule5MyFoo7describeSiAA0C0CHF",
            returnType: "int",
            methodName: "PInvoke_describe_ABC123",
            parameters: "SwiftString name, SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Blittability_SwiftClosureData_IsBlittable()
    {
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule7closureSiAA0C0CHF",
            returnType: "SwiftClosureData",
            methodName: "PInvoke_closure_ABC123",
            parameters: "SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Blittability_GenericSwiftSelf_IsBlittable()
    {
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule5MyFoo3barSiAA0C0CHF",
            returnType: "int",
            methodName: "PInvoke_bar_ABC123",
            parameters: "int value, SwiftSelf<MyStruct.Buffer> self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Blittability_SwiftOptionalGeneric_NotReportedByThisChecker()
    {
        // Same reasoning as SwiftString: a managed generic [LibraryImport] cannot marshal,
        // so the compiler is the gate, not this checker.
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule5MyFoo6getOptSiAA0C0CHF",
            returnType: "SwiftOptional<int>",
            methodName: "PInvoke_getOpt_ABC123",
            parameters: "SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Blittability_MarshalAsAttributedString_StillReported()
    {
        // The signature text carries any parameter attributes along with the type. An attribute
        // does not change the carrier, so a string wearing one must still be judged as a string —
        // otherwise the one carrier class this rule claims to catch is invisible whenever the
        // emitter spells out the marshalling.
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule5MyFoo7describeSiAA0C0CHF",
            returnType: "int",
            methodName: "PInvoke_describe_ABC123",
            parameters: "[MarshalAs(UnmanagedType.LPUTF8Str)] string name, SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.Contains(result.Violations, v => v.RuleId == "CC-001");
    }

    [Fact]
    public void Blittability_NullableAnnotatedString_StillReported()
    {
        // A nullable annotation is compile-time only — `string?` marshals exactly as
        // `string` does, so stripping the annotation must not lose the violation.
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule5MyFoo7describeSiAA0C0CHF",
            returnType: "int",
            methodName: "PInvoke_describe_ABC123",
            parameters: "string? name, SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.Contains(result.Violations, v => v.RuleId == "CC-001");
    }

    #endregion

    #region Integration: multiple checks in same output

    [Fact]
    public void MultipleViolations_DifferentRules_AllDetected()
    {
        var csOutput =
            // CC-001: C-string carrier param in CallConvSwift
            BuildPInvoke("CallConvSwift", "TestModule",
                "$s10TestModule2f1SiAA0C0CHF", "int", "PInvoke_f1_A",
                "string name, SwiftSelf<IntPtr> self") +
            "\n" +
            // CC-004: CallConvCdecl with mangled symbol
            BuildPInvoke("CallConvCdecl", "TestModule",
                "$s10TestModule2f2SiyF", "int", "PInvoke_f2_B",
                "int value");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.Contains(result.Violations, v => v.RuleId == "CC-001");
        Assert.Contains(result.Violations, v => v.RuleId == "CC-004");
        Assert.Equal(2, result.PInvokeCount);
    }

    #endregion

    #region Library classification

    [Fact]
    public void LibraryClassification_SwiftCore_CorrectlyClassified()
    {
        // SwiftCore P/Invokes should not trigger Tj thunk cross-module check
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "libswiftCore",
            entryPoint: "$ss27_finalizeUninitializedArrayySayxGABnlF",
            returnType: "void",
            methodName: "PInvoke_finalizeArray_ABC123",
            parameters: "IntPtr array");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        // No Tj-XM violation (SwiftCore is correctly classified, not OriginalLibrary)
        Assert.DoesNotContain(result.Violations, v => v.RuleId == "Tj-XM");
    }

    #endregion

    #region P1: Global:: qualified attributes (MarkerProtocolOverloadEmitter)

    [Fact]
    public void Extract_GlobalQualifiedAttributes_Extracted()
    {
        // MarkerProtocolOverloadEmitter emits fully-qualified global:: attributes
        var csOutput = @"
public sealed partial class TestClass
{
    [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvSwift) })]
    [global::System.Runtime.InteropServices.LibraryImport(""TestModule"", EntryPoint = ""$s10TestModule5MyFoo3barSiAA0C0CHF"")]
    internal static partial int PInvoke_bar_GLOBAL(int value, SwiftSelf<IntPtr> self);
}";
        var pinvokes = AbiContractChecker.ExtractPInvokes(csOutput, "TestModule");

        Assert.Single(pinvokes);
        Assert.Equal("PInvoke_bar_GLOBAL", pinvokes[0].MethodName);
        Assert.Equal("CallConvSwift", pinvokes[0].CallingConvention);
        Assert.Equal("$s10TestModule5MyFoo3barSiAA0C0CHF", pinvokes[0].EntryPoint);
    }

    [Fact]
    public void Extract_GlobalQualifiedCdecl_Extracted()
    {
        var csOutput = @"
public sealed partial class TestClass
{
    [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
    [global::System.Runtime.InteropServices.LibraryImport(""TestModuleSwiftBindings"", EntryPoint = ""SBW_TestModule_bar_ABC"")]
    internal static partial void PInvoke_bar_GLOBALCDECL(IntPtr self);
}";
        var pinvokes = AbiContractChecker.ExtractPInvokes(csOutput, "TestModule");

        Assert.Single(pinvokes);
        Assert.Equal("CallConvCdecl", pinvokes[0].CallingConvention);
    }

    [Fact]
    public void CC001_GlobalQualifiedSwiftWithIncompatibleCarrier_DetectsViolation()
    {
        // Fully-qualified attributes should still trigger violation checks
        var csOutput = @"
public sealed partial class TestClass
{
    [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvSwift) })]
    [global::System.Runtime.InteropServices.LibraryImport(""TestModule"", EntryPoint = ""$s10TestModule5MyFoo3barSiAA0C0CHF"")]
    internal static partial int PInvoke_bar_GLOBAL(string name, SwiftSelf<IntPtr> self);
}";
        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.Contains(result.Violations, v => v.RuleId == "CC-001");
    }

    #endregion

    #region R6-3: LibraryImport-first layout + alternate array forms

    [Fact]
    public void Extract_CallConvAfterLibraryImport_TargetTypedNewArray_ReadsSwift()
    {
        // The generic-metadata accessor helper (PInvokeHelperEmitter) emits
        // [LibraryImport] FIRST, then [UnmanagedCallConv] on the FOLLOWING line, using
        // target-typed `new[]`. A backward-only scan + a `new Type[]`-only regex both
        // miss this and mis-default it to Cdecl. The convention here is genuinely Swift.
        var csOutput = @"
public sealed partial class TestClass
{
    [global::System.Runtime.InteropServices.LibraryImport(""TestModule"", EntryPoint = ""$s10TestModule5MyFoo0B0CMa"")]
    [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvSwift) })]
    private static partial global::Swift.Runtime.TypeMetadata PInvoke_getMetadata_buffer(global::Swift.Runtime.TypeMetadataRequest request, global::System.IntPtr parameters);
}";
        var pinvokes = AbiContractChecker.ExtractPInvokes(csOutput, "TestModule");

        Assert.Single(pinvokes);
        Assert.Equal("PInvoke_getMetadata_buffer", pinvokes[0].MethodName);
        Assert.Equal("CallConvSwift", pinvokes[0].CallingConvention);
    }

    [Fact]
    public void Validate_MetadataAccessorHelper_NoFalseCC004()
    {
        // The real metadata-accessor helper shape: a CallConvSwift P/Invoke targeting a
        // $s…Ma mangled symbol, with [UnmanagedCallConv] AFTER [LibraryImport] in
        // target-typed `new[]` form. Mis-reading it as Cdecl fires a false SWIFTBIND094
        // (CC-004: Cdecl-targets-mangled-symbol). Reading it correctly as Swift must not.
        var csOutput = @"
public sealed partial class TestClass
{
    [global::System.Runtime.InteropServices.LibraryImport(""TestModule"", EntryPoint = ""$s10TestModule5MyFoo0B0CMa"")]
    [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvSwift) })]
    private static partial global::Swift.Runtime.TypeMetadata PInvoke_getMetadata_buffer(global::Swift.Runtime.TypeMetadataRequest request, global::System.IntPtr parameters);
}";
        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.DoesNotContain(result.Violations, v => v.RuleId == "CC-004");
    }

    [Fact]
    public void Extract_TargetTypedNewArray_CallConvFirst_ReadsSwift()
    {
        // KvoExtensionEmitter emits [UnmanagedCallConv] first using target-typed `new[]`.
        // The convention-first order is fine for the backward scan, but the `new[]` form
        // must still be recognized by the regex (it was only matching literal `Type[]`).
        var csOutput = @"
public sealed partial class TestClass
{
    [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvSwift) })]
    [global::System.Runtime.InteropServices.LibraryImport(""TestModule"", EntryPoint = ""$s10TestModule5MyFoo3barSiyF"")]
    private static partial int PInvoke_bar_NEWARR(int value);
}";
        var pinvokes = AbiContractChecker.ExtractPInvokes(csOutput, "TestModule");

        Assert.Single(pinvokes);
        Assert.Equal("CallConvSwift", pinvokes[0].CallingConvention);
    }

    [Fact]
    public void Extract_CollectionExpressionCallConvs_ReadsSwift()
    {
        // AppleTypesCsEmitter emits the C# 12 collection-expression form
        // `CallConvs = [typeof(CallConvSwift)]` — no `new`, square brackets. The regex
        // must recognize this too, else a genuine CallConvSwift is mis-read as Cdecl.
        var csOutput = @"
public sealed partial class TestClass
{
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [LibraryImport(""TestModule"", EntryPoint = ""$s10TestModule5MyFoo3barSiyF"")]
    private static partial int PInvoke_bar_COLLEXPR(int value);
}";
        var pinvokes = AbiContractChecker.ExtractPInvokes(csOutput, "TestModule");

        Assert.Single(pinvokes);
        Assert.Equal("CallConvSwift", pinvokes[0].CallingConvention);
    }

    [Fact]
    public void CC001_CallConvAfterLibraryImport_IncompatibleCarrier_DetectsViolation()
    {
        // Defense-in-depth: a CallConvSwift P/Invoke with the LibraryImport-first layout
        // AND an incompatible parameter carrier must still trip CC-001 — the layout fix
        // re-enables the carrier checks that the mis-default to Cdecl silently disabled.
        var csOutput = @"
public sealed partial class TestClass
{
    [global::System.Runtime.InteropServices.LibraryImport(""TestModule"", EntryPoint = ""$s10TestModule5MyFoo3barSiyF"")]
    [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvSwift) })]
    private static partial int PInvoke_bar_LATECC(string name, SwiftSelf<IntPtr> self);
}";
        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.Contains(result.Violations, v => v.RuleId == "CC-001");
    }

    #endregion

    #region P1: Internal/public visibility

    [Fact]
    public void Extract_InternalVisibility_Extracted()
    {
        var csOutput = @"
public sealed partial class TestClass
{
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    [LibraryImport(""TestModule"", EntryPoint = ""$s10TestModule5doItSiyF"")]
    internal static partial int PInvoke_doIt_INTERNAL(int value);
}";
        var pinvokes = AbiContractChecker.ExtractPInvokes(csOutput, "TestModule");

        Assert.Single(pinvokes);
        Assert.Equal("PInvoke_doIt_INTERNAL", pinvokes[0].MethodName);
    }

    [Fact]
    public void Extract_PublicVisibility_Extracted()
    {
        var csOutput = @"
public sealed partial class TestClass
{
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
    [LibraryImport(""TestModuleSwiftBindings"", EntryPoint = ""SBW_TestModule_foo_ABC"")]
    public static partial void PInvoke_foo_PUBLIC(IntPtr self);
}";
        var pinvokes = AbiContractChecker.ExtractPInvokes(csOutput, "TestModule");

        Assert.Single(pinvokes);
        Assert.Equal("PInvoke_foo_PUBLIC", pinvokes[0].MethodName);
    }

    #endregion

    #region P1: No [UnmanagedCallConv] attribute (EnumHandler direct emission)

    [Fact]
    public void Extract_NoUnmanagedCallConv_SbwEntryPoint_InfersCdecl()
    {
        // EnumHandler emits [LibraryImport] without [UnmanagedCallConv]
        var csOutput = @"
public sealed partial class TestClass
{
    [LibraryImport(""TestModuleSwiftBindings"", EntryPoint = ""SBW_TestModule_MyEnum_init_ABC"")]
    private static partial int PInvoke_MyEnum_init_NOCALCONV(int rawValue);
}";
        var pinvokes = AbiContractChecker.ExtractPInvokes(csOutput, "TestModule");

        Assert.Single(pinvokes);
        Assert.Equal("PInvoke_MyEnum_init_NOCALCONV", pinvokes[0].MethodName);
        // Should infer CallConvCdecl since entry point is SBW_
        Assert.Equal("CallConvCdecl", pinvokes[0].CallingConvention);
    }

    [Fact]
    public void Extract_NoUnmanagedCallConv_MangledSymbol_DefaultsToCdecl()
    {
        // Without [UnmanagedCallConv], the runtime uses platform default (C convention).
        // No real generator path emits $s... without [UnmanagedCallConv], but if it did,
        // the actual calling convention would be C, not Swift.
        var csOutput = @"
public sealed partial class TestClass
{
    [LibraryImport(""TestModule"", EntryPoint = ""$s10TestModule5MyFoo3barSiyF"")]
    private static partial int PInvoke_bar_NOCALCONV(int value);
}";
        var pinvokes = AbiContractChecker.ExtractPInvokes(csOutput, "TestModule");

        Assert.Single(pinvokes);
        Assert.Equal("CallConvCdecl", pinvokes[0].CallingConvention);
    }

    #endregion

    #region P2: Multiline signatures (AsyncStreamEmitter)

    [Fact]
    public void Extract_MultilineSignature_Extracted()
    {
        // AsyncStreamEmitter emits multiline P/Invoke signatures
        var csOutput = @"
public sealed partial class TestClass
{
    [LibraryImport(""TestModuleSwiftBindings"", EntryPoint = ""SBW_TestModule_stream_ABC"")]
    private static unsafe partial void PInvoke_stream_MULTILINE(
        void* self,
        delegate* unmanaged[Cdecl]<void*, long, byte> elementCallback,
        delegate* unmanaged[Cdecl]<long, void> completionCallback,
        long context);
}";
        var pinvokes = AbiContractChecker.ExtractPInvokes(csOutput, "TestModule");

        Assert.Single(pinvokes);
        Assert.Equal("PInvoke_stream_MULTILINE", pinvokes[0].MethodName);
        Assert.Equal("SBW_TestModule_stream_ABC", pinvokes[0].EntryPoint);
        // Should have 4 parameters
        Assert.Equal(4, pinvokes[0].Parameters.Length);
    }

    [Fact]
    public void Extract_MultilineSignature_ParametersParsedCorrectly()
    {
        var csOutput = @"
public sealed partial class TestClass
{
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
    [LibraryImport(""TestModuleSwiftBindings"", EntryPoint = ""SBW_TestModule_fetch_async_ABC"")]
    private static unsafe partial void PInvoke_fetch_MULTILINE(
        IntPtr self,
        delegate* unmanaged[Cdecl]<IntPtr, void> successCallback,
        delegate* unmanaged[Cdecl]<IntPtr, void> errorCallback,
        IntPtr taskContext);
}";
        var pinvokes = AbiContractChecker.ExtractPInvokes(csOutput, "TestModule");

        Assert.Single(pinvokes);
        Assert.Equal(4, pinvokes[0].Parameters.Length);
        Assert.Equal("IntPtr", pinvokes[0].Parameters[0].CSharpType);
        Assert.Equal("self", pinvokes[0].Parameters[0].Name);
    }

    #endregion

    #region Coverage: extraction completeness

    [Fact]
    public void Extract_CountMatchesLibraryImportOccurrences()
    {
        // Verify that the number of extracted P/Invokes matches the number of
        // [LibraryImport] occurrences in the input — no silent drops
        var csOutput =
            BuildPInvoke("CallConvSwift", "M", "$s1M2f1SiyF", "int", "P1", "int x") + "\n" +
            BuildPInvoke("CallConvCdecl", "MSwiftBindings", "SBW_M_f2", "void", "P2", "IntPtr s") + "\n" +
            // No [UnmanagedCallConv] form
            @"
public partial class C
{
    [LibraryImport(""MSwiftBindings"", EntryPoint = ""SBW_M_f3"")]
    private static partial int P3(int x);
}" + "\n" +
            // Multiline form
            @"
public partial class D
{
    [LibraryImport(""MSwiftBindings"", EntryPoint = ""SBW_M_f4"")]
    private static unsafe partial void P4(
        void* self,
        delegate* unmanaged[Cdecl]<long, void> cb,
        long ctx);
}";

        var pinvokes = AbiContractChecker.ExtractPInvokes(csOutput, "M");

        // Count [LibraryImport occurrences
        var libraryImportCount = System.Text.RegularExpressions.Regex.Matches(
            csOutput, @"\[(?:global::System\.Runtime\.InteropServices\.)?LibraryImport\(").Count;

        Assert.Equal(libraryImportCount, pinvokes.Length);
        Assert.Equal(4, pinvokes.Length);
    }

    #endregion

    #region Blocking: violations fail publication with an actionable report

    [Fact]
    public void Describe_CarriesEveryFieldNeededToAct()
    {
        // One canonical rendering backs both the warn log and the blocking report, so a
        // consumer reading either sees the code, rule, member, symbol, and the elements
        // at fault without having to correlate two formats.
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule5MyFoo3barSiAA0C0CHF",
            returnType: "int",
            methodName: "PInvoke_bar_ABC123",
            parameters: "string name");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);
        var described = Assert.Single(result.Violations).Describe();

        Assert.Contains("SWIFTBIND090", described);
        Assert.Contains("CC-001", described);
        Assert.Contains("PInvoke_bar_ABC123", described);
        Assert.Contains("$s10TestModule5MyFoo3barSiAA0C0CHF", described);
        Assert.Contains("name", described);
    }

    [Fact]
    public void ViolationException_ListsEveryViolationAndNamesTheModule()
    {
        var csOutput =
            BuildPInvoke("CallConvSwift", "TestModule",
                "$s10TestModule2f1SiAA0C0CHF", "int", "PInvoke_f1_A", "string name") +
            "\n" +
            BuildPInvoke("CallConvCdecl", "TestModule",
                "$s10TestModule2f2SiyF", "int", "PInvoke_f2_B", "int value");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);
        var ex = new AbiContractViolationException("TestModule", result.Violations);

        Assert.Equal("TestModule", ex.ModuleName);
        Assert.Equal(2, ex.Violations.Length);
        Assert.Contains("SWIFTBIND095", ex.Message);
        Assert.Contains("TestModule", ex.Message);
        // Every violation must reach the message — a report that summarizes the count but
        // drops the members is not actionable.
        foreach (var violation in result.Violations)
            Assert.Contains(violation.Describe(), ex.Message);
    }

    [Fact]
    public void CleanResult_IsCleanTrue_SoNothingBlocks()
    {
        // The blocking decision keys off IsClean; a module with P/Invokes and no
        // violations must not report itself as failing.
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule5MyFoo3barSiAA0C0CHF",
            returnType: "int",
            methodName: "PInvoke_bar_ABC123",
            parameters: "SwiftSafeHandle handle, SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.True(result.IsClean);
        Assert.Equal(1, result.PInvokeCount);
    }

    #endregion

    #region Test Helpers

    /// <summary>
    /// Build a synthetic P/Invoke declaration block for testing.
    /// </summary>
    private static string BuildPInvoke(
        string callConv,
        string library,
        string entryPoint,
        string returnType,
        string methodName,
        string parameters)
    {
        return $@"
public sealed partial class TestClass
{{
    [UnmanagedCallConv(CallConvs = new Type[] {{ typeof({callConv}) }})]
    [LibraryImport(""{library}"", EntryPoint = ""{entryPoint}"")]
    private static partial {returnType} {methodName}({parameters});
}}";
    }

    #endregion
}
