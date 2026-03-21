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
/// including the 4 refinements from Phase 4B.
/// </summary>
public class AbiContractCheckerTests
{
    private static readonly ILogger TestLogger = NullLoggerFactory.Instance.CreateLogger("Test");

    #region CC-001: Non-blittable params in CallConvSwift

    [Fact]
    public void CC001_SafeHandleInCallConvSwift_DetectsViolation()
    {
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule5MyFoo3barSiAA0C0CHF",
            returnType: "int",
            methodName: "PInvoke_bar_ABC123",
            parameters: "SwiftSafeHandle handle, SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.Single(result.Violations);
        Assert.Equal("SWIFTBIND090", result.Violations[0].DiagnosticCode);
        Assert.Equal("CC-001", result.Violations[0].RuleId);
        Assert.Contains("SwiftSafeHandle", result.Violations[0].AffectedElements[0]);
    }

    [Fact]
    public void CC001_SwiftStringInCallConvSwift_DetectsViolation()
    {
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule5MyFoo3barSiAA0C0CHF",
            returnType: "int",
            methodName: "PInvoke_bar_ABC123",
            parameters: "SwiftString name");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.Single(result.Violations);
        Assert.Equal("CC-001", result.Violations[0].RuleId);
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
        // CC-001 only applies to CallConvSwift — cdecl handles all types
        var csOutput = BuildPInvoke(
            callConv: "CallConvCdecl",
            library: "TestModuleSwiftBindings",
            entryPoint: "SBW_TestModule_MyFoo_bar_ABC123",
            returnType: "void",
            methodName: "PInvoke_bar_ABC123",
            parameters: "SwiftSafeHandle handle");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        // No CC-001 violation (cdecl is fine with non-blittable)
        Assert.DoesNotContain(result.Violations, v => v.RuleId == "CC-001");
    }

    #endregion

    #region CC-002: Non-blittable return in CallConvSwift

    [Fact]
    public void CC002_NonBlittableReturn_DetectsViolation()
    {
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule5MyFoo3getSS0C0CHF",
            returnType: "SwiftString",
            methodName: "PInvoke_get_ABC123",
            parameters: "SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        var cc002 = result.Violations.Where(v => v.RuleId == "CC-002").ToList();
        Assert.Single(cc002);
        Assert.Equal("SWIFTBIND091", cc002[0].DiagnosticCode);
        Assert.Contains("SwiftString", cc002[0].AffectedElements[0]);
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

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

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

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.DoesNotContain(result.Violations, v => v.RuleId == "CC-003");
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
    public void TjThunk_CrossModuleMismatch_DetectsViolation()
    {
        // Entry point encodes "OtherModule" but targets "TestModule" library
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
        Assert.Contains("OtherModule", tj[0].Explanation);
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

    #region Refinement 1: De-duplication by (RuleId, MethodName)

    [Fact]
    public void Refinement1_DuplicateViolationsSamePInvoke_Deduplicated()
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
                parameters: "SwiftSafeHandle handle") +
            "\n" +
            BuildPInvoke(
                callConv: "CallConvSwift",
                library: "TestModule",
                entryPoint: "$s10TestModule5MyFoo3barSiAA0C0CHF",
                returnType: "int",
                methodName: "PInvoke_bar_ABC123",
                parameters: "SwiftSafeHandle handle");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        // Should be deduplicated to 1
        var cc001 = result.Violations.Where(v => v.RuleId == "CC-001").ToList();
        Assert.Single(cc001);
    }

    #endregion

    #region Refinement 2: Primitive type exclusion from float struct heuristic

    [Fact]
    public void Refinement2_PrimitiveTypesNotFlaggedAsNonBlittable()
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

    #endregion

    #region Refinement 3: Closure context adjacency

    [Fact]
    public void Refinement3_ClosureContext_AdjacentToFuncPtr_NotFlagged()
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
    public void Refinement3_NonClosureContext_IntPtrNotAdjacentToFuncPtr_NotFlagged()
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

    #endregion

    #region Refinement 4: Async detection via _async in entry point

    [Fact]
    public void Refinement4_AsyncDetectedByEntryPoint_NotParamName()
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
        Assert.Equal("Nuke", AbiContractChecker.ExtractModuleFromMangledSymbol("$s4Nuke11ImageLoaderC7loadFooyyF"));
        Assert.Equal("StripePayments", AbiContractChecker.ExtractModuleFromMangledSymbol("$s14StripePayments3FooCfdTj"));
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
    public void Blittability_SwiftString_IsNonBlittable()
    {
        // SwiftString itself (not .Buffer) IS non-blittable
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule5MyFoo7describeSiAA0C0CHF",
            returnType: "int",
            methodName: "PInvoke_describe_ABC123",
            parameters: "SwiftString name, SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        Assert.Contains(result.Violations, v => v.RuleId == "CC-001");
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
    public void Blittability_SwiftOptionalGeneric_IsNonBlittable()
    {
        var csOutput = BuildPInvoke(
            callConv: "CallConvSwift",
            library: "TestModule",
            entryPoint: "$s10TestModule5MyFoo6getOptSiAA0C0CHF",
            returnType: "SwiftOptional<int>",
            methodName: "PInvoke_getOpt_ABC123",
            parameters: "SwiftSelf<IntPtr> self");

        var result = AbiContractChecker.Validate(csOutput, "TestModule", TestLogger);

        // SwiftOptional is non-blittable → CC-002
        Assert.Contains(result.Violations, v => v.RuleId == "CC-002");
    }

    #endregion

    #region Integration: multiple checks in same output

    [Fact]
    public void MultipleViolations_DifferentRules_AllDetected()
    {
        var csOutput =
            // CC-001: non-blittable param in CallConvSwift
            BuildPInvoke("CallConvSwift", "TestModule",
                "$s10TestModule2f1SiAA0C0CHF", "int", "PInvoke_f1_A",
                "SwiftSafeHandle handle, SwiftSelf<IntPtr> self") +
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
    public void CC001_GlobalQualifiedSwiftWithNonBlittable_DetectsViolation()
    {
        // Fully-qualified attributes should still trigger violation checks
        var csOutput = @"
public sealed partial class TestClass
{
    [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvSwift) })]
    [global::System.Runtime.InteropServices.LibraryImport(""TestModule"", EntryPoint = ""$s10TestModule5MyFoo3barSiAA0C0CHF"")]
    internal static partial int PInvoke_bar_GLOBAL(SafeHandle handle, SwiftSelf<IntPtr> self);
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
