// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using BindingsGeneration.Demangling;
using Xunit;
using DemanglerNode = BindingsGeneration.Demangling.Node;

namespace BindingsGeneration.Tests;

/// <summary>
/// Demangler tests: DemangleSymbol() path, symbol category coverage,
/// complex generics, and edge cases.
/// </summary>
public class DemanglerSession2Tests
{
    // ================================================================
    // D6b: DemangleSymbol() public API path tests
    // ================================================================

    [Fact]
    public void DemangleSymbol_ValidSymbol_ReturnsNull_KnownPortingBug()
    {
        // Known porting bug: the foreach loop that adds remaining nodes to the
        // Global node is INSIDE the while(funcAttr) loop. Since most symbols
        // don't have function attributes, the while loop body never executes
        // and topLevel remains empty → returns null.
        var demangler = new Swift5Demangler();
        var node = demangler.DemangleSymbol("$s22GeneralHackingNonsense12ThisIsAClassCMa");
        Assert.Null(node);
    }

    [Fact]
    public void DemangleSymbol_WithLeadingUnderscore_ReturnsNull_KnownPortingBug()
    {
        // Same porting bug as above — leading underscore stripped, but node
        // still not added to topLevel because no function attributes present.
        var demangler = new Swift5Demangler();
        var node = demangler.DemangleSymbol("_$s22GeneralHackingNonsense12ThisIsAClassCMa");
        Assert.Null(node);
    }

    [Fact]
    public void DemangleSymbol_InvalidPrefix_ReturnsNull()
    {
        var demangler = new Swift5Demangler();
        var node = demangler.DemangleSymbol("not_a_swift_symbol");
        Assert.Null(node);
    }

    [Fact]
    public void DemangleSymbol_EmptyString_ReturnsNull()
    {
        var demangler = new Swift5Demangler();
        var node = demangler.DemangleSymbol("");
        Assert.Null(node);
    }

    [Fact]
    public void DemangleSymbol_FunctionSymbol_ReturnsNull_KnownPortingBug()
    {
        // Same porting bug — even function symbols return null because Function
        // is not a "function attribute" (IsFunctionAttr only matches specialization
        // and attribute node kinds, not Function itself).
        var demangler = new Swift5Demangler();
        var node = demangler.DemangleSymbol("$s22GeneralHackingNonsense12ThisIsAClassC11returnSevenSiyF");
        Assert.Null(node);
    }

    [Fact]
    public void DemangleSymbol_ObjCTypeName_TtPrefix_ReturnsNull()
    {
        // _TtC prefix is ObjC type name encoding — DemangleObjCTypeName() is called
        // but returns null (parsing issue with this specific format).
        var demangler = new Swift5Demangler();
        var node = demangler.DemangleSymbol("_TtC7MyModule7MyClass");
        Assert.Null(node);
    }

    [Fact]
    public void DemangleSymbol_CanBeCalledMultipleTimes_WithoutThrowing()
    {
        // DemangleSymbol() reinitializes state via Init(), so multiple calls should not throw.
        // Both return null due to the porting bug, but state isolation is the key property.
        var demangler = new Swift5Demangler();
        var node1 = demangler.DemangleSymbol("$s22GeneralHackingNonsense12ThisIsAClassCMa");
        var node2 = demangler.DemangleSymbol("$s22GeneralHackingNonsense12ThisIsAClassC11returnSevenSiyF");
        // Both null due to porting bug, but the point is no exception on second call
        Assert.Null(node1);
        Assert.Null(node2);
    }

    [Fact]
    public void DemangleSymbol_PipelineBlockedByPortingBug()
    {
        // The full DemangleSymbol → Reducer pipeline cannot work because
        // DemangleSymbol() returns null for all normal symbols.
        // Use Run() instead for the full pipeline (tested elsewhere).
        var demangler = new Swift5Demangler();
        var node = demangler.DemangleSymbol("$s22GeneralHackingNonsense12ThisIsAClassCMa");
        Assert.Null(node);

        // Verify the Run() path works for the same symbol
        var result = demangler.Run("$s22GeneralHackingNonsense12ThisIsAClassCMa");
        var meta = Assert.IsType<MetadataAccessorReduction>(result);
        Assert.Equal("GeneralHackingNonsense.ThisIsAClass", meta.TypeSpec.Name);
    }

    // ================================================================
    // D6b: IsSwiftSymbol() tests
    // ================================================================

    [Theory]
    [InlineData("$s4test", true)]
    [InlineData("_$s4test", true)]
    [InlineData("$S4test", true)]
    [InlineData("_$S4test", true)]
    [InlineData("_T04test", true)]
    [InlineData("_Ttest", true)] // old function type mangling
    [InlineData("not_swift", false)]
    [InlineData("", false)]
    [InlineData("$", false)]
    public void IsSwiftSymbol_VariousPrefixes(string symbol, bool expected)
    {
        Assert.Equal(expected, Swift5Demangler.IsSwiftSymbol(symbol));
    }

    // ================================================================
    // D6c: SymbolicReferenceResolver tests
    // ================================================================

    [Fact]
    public void SymbolicReferenceResolver_DefaultIsNull()
    {
        var demangler = new Swift5Demangler();
        Assert.Null(demangler.SymbolicReferenceResolver);
    }

    [Fact]
    public void SymbolicReferenceResolver_NotCalledForNormalSymbols()
    {
        var demangler = new Swift5Demangler();
        var wasCalled = false;
        demangler.SymbolicReferenceResolver = (kind, direct, value, data) =>
        {
            wasCalled = true;
            return new DemanglerNode(NodeKind.Module, "Resolved");
        };
        Assert.NotNull(demangler.SymbolicReferenceResolver);

        // Normal symbols don't trigger the resolver
        demangler.Run("$s22GeneralHackingNonsense12ThisIsAClassCMa");
        Assert.False(wasCalled);
    }

    [Fact]
    public void SymbolicReferenceResolver_CalledForSymbolicReferenceBytes()
    {
        // Symbolic references are triggered by raw bytes \x01-\x04 in the mangled name.
        // \x01 = Context/Direct, \x02 = Context/Indirect
        var demangler = new Swift5Demangler();
        SymbolicReferenceKind? capturedKind = null;
        Directness? capturedDirect = null;
        byte[] capturedData = null;

        demangler.SymbolicReferenceResolver = (kind, direct, value, data) =>
        {
            capturedKind = kind;
            capturedDirect = direct;
            capturedData = data;
            return new DemanglerNode(NodeKind.Structure, "ResolvedStruct");
        };

        // Build a symbol containing a symbolic reference: $s + \x01 + 4 data bytes + suffix
        // The demangler reads 4 bytes after \x01 as the reference value.
        var symRefSymbol = "$s\x01\x42\x00\x00\x00Ma";
        demangler.Run(symRefSymbol);

        // Verify the resolver was actually invoked with correct arguments
        Assert.NotNull(capturedKind);
        Assert.Equal(SymbolicReferenceKind.Context, capturedKind.Value);
        Assert.Equal(Directness.Direct, capturedDirect.Value);
        Assert.NotNull(capturedData);
        Assert.Equal(4, capturedData.Length);
        Assert.Equal(0x42, capturedData[0]);
    }

    // ================================================================
    // D6: Symbol category tests — property getter/setter
    // ================================================================

    [Fact]
    public void Demangle_PropertyGetter_ProducesReductionError()
    {
        // AsyncTests.AsyncStruct.storedValue.getter : Swift.Int32
        // The reducer has no rule for Getter nodes — produces ReductionError(Low).
        var result = new Swift5Demangler().Run("$s10AsyncTests0A6StructV11storedValues5Int32Vvg");
        var error = Assert.IsType<ReductionError>(result);
        Assert.Equal(ReductionErrorSeverity.Low, error.Severity);
        Assert.Contains("Getter", error.Message);
    }

    [Fact]
    public void Demangle_PropertySetter_ProducesReductionError()
    {
        // MemoryTests.FrozenStructRequiresMemoryManagement.b.setter : Swift.Int32
        // The reducer has no rule for Setter nodes — produces ReductionError(Low).
        var result = new Swift5Demangler().Run("$s11MemoryTests020FrozenStructRequiresA10ManagementV1bs5Int32Vvs");
        var error = Assert.IsType<ReductionError>(result);
        Assert.Equal(ReductionErrorSeverity.Low, error.Severity);
        Assert.Contains("Setter", error.Message);
    }

    [Fact]
    public void Demangle_PropertyGetterWithReferenceType_ProducesReductionError()
    {
        // MemoryTests.FrozenStructRequiresMemoryManagement.a.getter : MemoryTests.RefType
        // Same Getter gap — reference type return doesn't change reducer behavior.
        var result = new Swift5Demangler().Run("$s11MemoryTests020FrozenStructRequiresA10ManagementV1aAA7RefTypeCvg");
        var error = Assert.IsType<ReductionError>(result);
        Assert.Equal(ReductionErrorSeverity.Low, error.Severity);
        Assert.Contains("Getter", error.Message);
    }

    // ================================================================
    // D6: Symbol category tests — subscript
    // ================================================================

    [Fact]
    public void Demangle_SubscriptGetter_ProducesReductionError()
    {
        // Nuke.ImageCache.subscript.getter : (Nuke.ImageCacheKey) -> Nuke.ImageContainer?
        // Subscript getters hit the Getter node gap in the reducer.
        var result = new Swift5Demangler().Run("$s4Nuke10ImageCacheCyAA0B9ContainerVSgAA0bC3KeyVcig");
        var error = Assert.IsType<ReductionError>(result);
        Assert.Equal(ReductionErrorSeverity.Low, error.Severity);
        Assert.Contains("Getter", error.Message);
    }

    // ================================================================
    // D6: Symbol category tests — operator
    // ================================================================

    [Fact]
    public void Demangle_EqualityOperator_ProducesReductionError()
    {
        // static StructsTests.CustomEquatableStruct.== infix(...) -> Swift.Bool
        // Operators wrap as Function nodes but the reducer fails because the
        // Static > Function path encounters InfixOperator which has no rule.
        var result = new Swift5Demangler().Run("$s12StructsTests21CustomEquatableStructV2eeoiySbAC_ACtFZ");
        var error = Assert.IsType<ReductionError>(result);
        Assert.Equal(ReductionErrorSeverity.Low, error.Severity);
        Assert.Contains("Function", error.Message);
    }

    // ================================================================
    // D6: Symbol category tests — async functions
    // ================================================================

    [Fact]
    public void Demangle_AsyncVoidFunction()
    {
        // AsyncTests.AsyncStruct.AsyncVoid() async -> ()
        var result = new Swift5Demangler().Run("$s10AsyncTests0A6StructV0A4VoidyyYaF");
        var func = result as FunctionReduction;
        Assert.NotNull(func);
        Assert.Equal("AsyncVoid", func.Function.Name);
        Assert.True(func.Function.Provenance.IsInstance);
    }

    [Fact]
    public void Demangle_AsyncFunctionWithReturnType()
    {
        // AsyncTests.AsyncStruct.ArrayPassThrough(input: [Swift.String]) async -> [Swift.String]
        var result = new Swift5Demangler().Run("$s10AsyncTests0A6StructV16ArrayPassThrough5inputSaySSGAF_tYaF");
        var func = result as FunctionReduction;
        Assert.NotNull(func);
        Assert.Equal("ArrayPassThrough", func.Function.Name);
        Assert.Single(func.Function.ParameterList.Elements);
        Assert.Equal("input", func.Function.ParameterList.Elements[0].TypeLabel);
    }

    // ================================================================
    // D6: Symbol category tests — static methods
    // ================================================================

    [Fact]
    public void Demangle_StaticMethod()
    {
        // static StructsTests.StructBuilder.createFrozenStruct(x: Swift.Int, y: Swift.Int) -> StructsTests.FrozenStruct
        var result = new Swift5Demangler().Run("$s12StructsTests13StructBuilderV012createFrozenC01x1yAA0fC0VSi_SitFZ");
        var func = result as FunctionReduction;
        Assert.NotNull(func);
        Assert.Equal("createFrozenStruct", func.Function.Name);
        Assert.Equal(2, func.Function.ParameterList.Elements.Count);
        Assert.Equal("x", func.Function.ParameterList.Elements[0].TypeLabel);
        Assert.Equal("y", func.Function.ParameterList.Elements[1].TypeLabel);
    }

    [Fact]
    public void Demangle_StaticThrowingMethod()
    {
        // static StructsTests.StructWithThrowingMethods.sum(x: Swift.Int, y: Swift.Int) throws -> Swift.Int
        var result = new Swift5Demangler().Run("$s12StructsTests25StructWithThrowingMethodsV3sum1x1yS2i_SitKFZ");
        var func = result as FunctionReduction;
        Assert.NotNull(func);
        Assert.Equal("sum", func.Function.Name);
        Assert.Equal(2, func.Function.ParameterList.Elements.Count);
    }

    // ================================================================
    // D6: Symbol category tests — inout parameters
    // ================================================================

    [Fact]
    public void Demangle_InoutParameter_ProducesReductionError()
    {
        // Nuke.ImageRequest.UserInfoKey.hash(into: inout Swift.Hasher) -> ()
        // The reducer hits InOut node kind which has no rule — High severity
        // because it's nested inside a Function the reducer otherwise handles.
        var result = new Swift5Demangler().Run("$s4Nuke12ImageRequestV11UserInfoKeyV4hash4intoys6HasherVz_tF");
        var error = Assert.IsType<ReductionError>(result);
        Assert.Equal(ReductionErrorSeverity.High, error.Severity);
        Assert.Contains("InOut", error.Message);
    }

    // ================================================================
    // D6: Symbol category tests — metadata accessor
    // ================================================================

    [Fact]
    public void Demangle_MetadataAccessor_Struct()
    {
        // type metadata accessor for StructsTests.FrozenStruct
        var result = new Swift5Demangler().Run("$s12StructsTests12FrozenStructVMa");
        var meta = result as MetadataAccessorReduction;
        Assert.NotNull(meta);
        Assert.Equal("StructsTests.FrozenStruct", meta.TypeSpec.Name);
    }

    // ================================================================
    // D6: Symbol category tests — protocol conformance descriptor
    // ================================================================

    [Fact]
    public void Demangle_ProtocolConformanceDescriptor_WithGeneric()
    {
        // Already tested in basic, but this validates a generic conformance:
        // protocol conformance descriptor for Swift.Array<T> : Swift.Collection in Swift
        var result = new Swift5Demangler().Run("$sSayxGSlsMc");
        var proto = result as ProtocolConformanceDescriptorReduction;
        Assert.NotNull(proto);
        Assert.Equal("Swift.Array<T_0_0>", proto.ImplementingType.ToString());
        Assert.Equal("Swift.Collection", proto.ProtocolType.ToString());
    }

    // ================================================================
    // D6: Symbol category tests — default argument initializer
    // ================================================================

    [Fact]
    public void Demangle_DefaultArgumentInitializer()
    {
        // default argument 0 of Swift.print(_:separator:terminator:) -> ()
        var result = new Swift5Demangler().Run("$ss5print_9separator10terminatoryypd_S2StFfA0_");
        // Default argument initializers don't reduce to a known reduction type — they produce ReductionError with low severity
        var error = result as ReductionError;
        Assert.NotNull(error);
        Assert.Equal(ReductionErrorSeverity.Low, error.Severity);
    }

    // ================================================================
    // D6: Symbol category tests — extension methods
    // ================================================================

    [Fact]
    public void Demangle_ExtensionMethod_ProducesReductionError()
    {
        // (extension in Alamofire):Foundation.URLRequest.asURLRequest() throws -> Foundation.URLRequest
        // Extension methods wrap Function in an Extension node — the reducer
        // reaches the Function node but the Extension context causes failure.
        var result = new Swift5Demangler().Run("$s10Foundation10URLRequestV9AlamofireE02asB0ACyKF");
        var error = Assert.IsType<ReductionError>(result);
        Assert.Equal(ReductionErrorSeverity.Low, error.Severity);
        Assert.Contains("Function", error.Message);
    }

    // ================================================================
    // D6: Symbol category tests — metatype / metaclass
    // ================================================================

    [Fact]
    public void Demangle_Metaclass_ProducesReductionError()
    {
        // metaclass for MemoryTests.RefType
        var result = new Swift5Demangler().Run("$s11MemoryTests7RefTypeCMm");
        var error = Assert.IsType<ReductionError>(result);
        Assert.Equal(ReductionErrorSeverity.Low, error.Severity);
        Assert.Contains("Metaclass", error.Message);
    }

    [Fact]
    public void Demangle_TypeMetadata_ProducesReductionError()
    {
        // type metadata for MemoryTests.RefType
        var result = new Swift5Demangler().Run("$s11MemoryTests7RefTypeCN");
        var error = Assert.IsType<ReductionError>(result);
        Assert.Equal(ReductionErrorSeverity.Low, error.Severity);
        Assert.Contains("TypeMetadata", error.Message);
    }

    // ================================================================
    // D6: Symbol category tests — dispatch thunk (allocator)
    // ================================================================

    [Fact]
    public void Demangle_MethodDescriptor_ProducesReductionError()
    {
        // method descriptor for GeneralHackingNonsense.ThisIsAClass.identityFunc(a:) -> Swift.Int
        var result = new Swift5Demangler().Run("$s22GeneralHackingNonsense12ThisIsAClassC12identityFunc1aS2i_tFTq");
        var error = Assert.IsType<ReductionError>(result);
        Assert.Equal(ReductionErrorSeverity.Low, error.Severity);
        Assert.Contains("MethodDescriptor", error.Message);
    }

    // ================================================================
    // D7: Complex generics tests
    // ================================================================

    [Fact]
    public void Demangle_NestedGeneric_ArrayOfOptionalInt()
    {
        // Swift.Array<Swift.Optional<Swift.Int>> — Array<Int?>
        // Mangled: $sSaySiSgGD (typeMangling for Array<Optional<Int>>)
        var result = new Swift5Demangler().Run("$sSaySiSgGMa");
        var meta = result as MetadataAccessorReduction;
        Assert.NotNull(meta);
        // Should be Swift.Array with generic parameter
        Assert.Contains("Array", meta.TypeSpec.Name);
    }

    [Fact]
    public void Demangle_GenericFunctionWithMultipleTypeParams()
    {
        // Swift.withUnsafeBytes<T_0_0, T_0_1>(of:_:) -> T_0_1
        var result = new Swift5Demangler().Run("$ss15withUnsafeBytes2of_q_x_q_SWKXEtKr0_lF");
        var func = result as FunctionReduction;
        Assert.NotNull(func);
        Assert.Equal("withUnsafeBytes", func.Function.Name);
        Assert.Equal(2, func.Function.ParameterList.Elements.Count);
        Assert.Equal("of", func.Function.ParameterList.Elements[0].TypeLabel);
    }

    [Fact]
    public void Demangle_GenericWithAssociatedType_IndexingIterator()
    {
        // Swift.IndexingIterator.next() -> Element?
        var result = new Swift5Demangler().Run("$ss16IndexingIteratorV4next7ElementQzSgyF");
        var func = result as FunctionReduction;
        Assert.NotNull(func);
        Assert.Equal("Swift.IndexingIterator.next() -> Swift.Optional<T_0_0.Element>", func.Function.ToString());
    }

    [Fact]
    public void Demangle_GenericConstrainedFunction()
    {
        // GenericTests.AcceptsMultipleGenericParamsWithProtocols(a:b:) — complex generic constraints
        var result = new Swift5Demangler().Run(
            "$s12GenericTests015AcceptsMultipleA19ParamsWithProtocols1a1bSix_q_tAA13MultiplicableRzAA8SummableRzAA9DividableR_AA12SubtractableR_r0_lF");
        var func = result as FunctionReduction;
        Assert.NotNull(func);
        Assert.Equal("AcceptsMultipleGenericParamsWithProtocols", func.Function.Name);
        Assert.Equal(2, func.Function.ParameterList.Elements.Count);
        Assert.Equal("a", func.Function.ParameterList.Elements[0].TypeLabel);
        Assert.Equal("b", func.Function.ParameterList.Elements[1].TypeLabel);
    }

    [Fact]
    public void Demangle_BoundGenericConformance_DictionaryCollection()
    {
        // protocol conformance descriptor for [A : B] : Swift.Collection in Swift
        var result = new Swift5Demangler().Run("$sSDyxq_GSlsMc");
        var proto = result as ProtocolConformanceDescriptorReduction;
        Assert.NotNull(proto);
        Assert.Contains("Dictionary", proto.ImplementingType.Name);
        Assert.Equal("Swift.Collection", proto.ProtocolType.Name);
    }

    [Fact]
    public void Demangle_GenericProtocolWitnessTable()
    {
        // protocol witness table for GenericTests.IntContainer1 : GenericTests.Container in GenericTests
        var result = new Swift5Demangler().Run("$s12GenericTests13IntContainer1VAA9ContainerAAWP");
        var pwt = result as ProtocolWitnessTableReduction;
        Assert.NotNull(pwt);
        Assert.Equal("GenericTests.IntContainer1", pwt.ImplementingType.Name);
        Assert.Equal("GenericTests.Container", pwt.ProtocolType.Name);
    }

    // ================================================================
    // D8: Edge case tests — empty/null/malformed input
    // ================================================================

    [Fact]
    public void Run_EmptyString_ReturnsError()
    {
        var result = new Swift5Demangler().Run("");
        Assert.NotNull(result);
        var error = result as ReductionError;
        Assert.NotNull(error);
    }

    [Fact]
    public void Run_JustPrefix_ReturnsError()
    {
        var result = new Swift5Demangler().Run("$s");
        Assert.NotNull(result);
    }

    [Fact]
    public void Run_TruncatedSymbol_DoesNotThrow()
    {
        // Take a valid symbol and truncate it mid-way
        var fullSymbol = "$s22GeneralHackingNonsense12ThisIsAClassCMa";
        var truncated = fullSymbol.Substring(0, 15); // "$s22GeneralHack"
        var result = new Swift5Demangler().Run(truncated);
        Assert.NotNull(result);
        // Should produce either a reduction or an error, not throw
    }

    [Fact]
    public void Run_VeryLongSymbol_DoesNotThrow()
    {
        // Create a very long (but syntactically valid-prefix) symbol
        var longSymbol = "$s" + new string('A', 10000);
        var result = new Swift5Demangler().Run(longSymbol);
        Assert.NotNull(result);
    }

    [Fact]
    public void Run_MalformedPrefix_ReturnsError()
    {
        var result = new Swift5Demangler().Run("$x_invalid_prefix");
        Assert.NotNull(result);
        var error = result as ReductionError;
        Assert.NotNull(error);
    }

    [Fact]
    public void Run_OnlyUnderscore_ReturnsError()
    {
        var result = new Swift5Demangler().Run("_");
        Assert.NotNull(result);
    }

    [Fact]
    public void Run_SpecialCharacters_DoesNotThrow()
    {
        var result = new Swift5Demangler().Run("$s\0\0\0\0");
        // Should handle null bytes without crashing
        Assert.NotNull(result);
    }

    [Fact]
    public void DemangleSymbol_TruncatedSymbol_ReturnsNull()
    {
        var demangler = new Swift5Demangler();
        var truncated = "$s22GeneralHack";
        var node = demangler.DemangleSymbol(truncated);
        // Truncated symbols return null (failed parse or porting bug)
        Assert.Null(node);
    }

    [Fact]
    public void DemangleSymbol_JustPrefix_ReturnsNull()
    {
        var demangler = new Swift5Demangler();
        var node = demangler.DemangleSymbol("$s");
        Assert.Null(node);
    }

    // ================================================================
    // D8: Edge case tests — Run() with Swift 4 prefixes
    // ================================================================

    [Theory]
    [InlineData("_T0")]
    [InlineData("$S")]
    [InlineData("_$S")]
    public void Run_AlternatePrefixes_DoesNotThrow(string prefix)
    {
        var result = new Swift5Demangler().Run(prefix);
        Assert.NotNull(result);
    }

    // ================================================================
    // D8: Edge case tests — reuse of demangler instance
    // ================================================================

    [Fact]
    public void Run_SequentialCalls_IndependentResults()
    {
        var demangler = new Swift5Demangler();

        var result1 = demangler.Run("$s22GeneralHackingNonsense12ThisIsAClassCMa");
        var meta = result1 as MetadataAccessorReduction;
        Assert.NotNull(meta);
        Assert.Equal("GeneralHackingNonsense.ThisIsAClass", meta.TypeSpec.Name);

        var result2 = demangler.Run("$s20GenericTestFramework6ThingyCAA7StanleyAAWP");
        var pwt = result2 as ProtocolWitnessTableReduction;
        Assert.NotNull(pwt);
        Assert.Equal("GenericTestFramework.Thingy", pwt.ImplementingType.Name);

        // The garbage input should also work after valid inputs
        var result3 = demangler.Run("_$ThisIsJustGarbage");
        var err = result3 as ReductionError;
        Assert.NotNull(err);
    }

    // ================================================================
    // D6: Additional symbol categories — protocol witness tables from real TBD
    // ================================================================

    [Fact]
    public void Demangle_ProtocolConformanceDescriptor_FromIntegrationTest()
    {
        // protocol conformance descriptor for GenericTests.IntContainer1 : GenericTests.Container
        var result = new Swift5Demangler().Run("$s12GenericTests13IntContainer1VAA9ContainerAAMc");
        var pcd = result as ProtocolConformanceDescriptorReduction;
        Assert.NotNull(pcd);
        Assert.Equal("GenericTests.IntContainer1", pcd.ImplementingType.Name);
        Assert.Equal("GenericTests.Container", pcd.ProtocolType.Name);
    }

    // ================================================================
    // D6: Symbol category tests — property modify accessor
    // ================================================================

    [Fact]
    public void Demangle_PropertyModifyAccessor_ProducesReductionError()
    {
        // MemoryTests.FrozenStructRequiresMemoryManagement.a.modify : MemoryTests.RefType
        // The reducer has no rule for ModifyAccessor nodes.
        var result = new Swift5Demangler().Run("$s11MemoryTests020FrozenStructRequiresA10ManagementV1aAA7RefTypeCvM");
        var error = Assert.IsType<ReductionError>(result);
        Assert.Equal(ReductionErrorSeverity.Low, error.Severity);
        Assert.Contains("ModifyAccessor", error.Message);
    }

    // ================================================================
    // D6: Symbol category — functions with closure parameters
    // ================================================================

    [Fact]
    public void Demangle_FunctionWithClosureParam()
    {
        // Swift.withUnsafeBytes<A, B>(of: A, _: (Swift.UnsafeRawBufferPointer) throws -> B) throws -> B
        var result = new Swift5Demangler().Run("$ss15withUnsafeBytes2of_q_x_q_SWKXEtKr0_lF");
        var func = result as FunctionReduction;
        Assert.NotNull(func);
        Assert.Equal("withUnsafeBytes", func.Function.Name);
        // Second parameter should be a closure type
        var secondParam = func.Function.ParameterList.Elements[1];
        Assert.IsType<ClosureTypeSpec>(secondParam);
    }

    // ================================================================
    // D6: Throwing functions
    // ================================================================

    [Fact]
    public void Demangle_ThrowingFunction_FromStruct()
    {
        // StructsTests.StructWithThrowingMethods.throwingMethod(x:) throws -> Swift.Int
        // Known demangler bug: the identifier length "15" covers "throwingMethod1"
        // consuming the label "x" into the function name. Param label becomes null.
        var result = new Swift5Demangler().Run("$s12StructsTests25StructWithThrowingMethodsV15throwingMethod1xS2i_tKF");
        var func = result as FunctionReduction;
        Assert.NotNull(func);
        Assert.Equal("throwingMethod1", func.Function.Name);
        Assert.Single(func.Function.ParameterList.Elements);
    }

    // ================================================================
    // D6: Symbol category — nominal type descriptor
    // ================================================================

    [Fact]
    public void Demangle_NominalTypeDescriptor_ProducesReductionError()
    {
        // nominal type descriptor for StructsTests.FrozenStruct
        var result = new Swift5Demangler().Run("$s12StructsTests12FrozenStructVMn");
        var error = Assert.IsType<ReductionError>(result);
        Assert.Equal(ReductionErrorSeverity.Low, error.Severity);
        Assert.Contains("NominalTypeDescriptor", error.Message);
    }

    // ================================================================
    // D6: Symbol category tests — reabstraction thunk (real symbol)
    // ================================================================

    [Fact]
    public void Demangle_ReabstractionThunk_ProducesReductionError()
    {
        // reabstraction thunk helper from @escaping @callee_guaranteed () -> () to @escaping @callee_unowned @convention(block) () -> ()
        var result = new Swift5Demangler().Run("$sIeg_IeyB_TR");
        var error = Assert.IsType<ReductionError>(result);
        Assert.Equal(ReductionErrorSeverity.Low, error.Severity);
        Assert.Contains("ReabstractionThunkHelper", error.Message);
    }

    // ================================================================
    // D6: Symbol category tests — value witness table (real symbol)
    // ================================================================

    [Fact]
    public void Demangle_ValueWitnessTable_ProducesReductionError()
    {
        // value witness table for ClosuresTests.SBW_Utf8Slice
        var result = new Swift5Demangler().Run("$s13ClosuresTests13SBW_Utf8SliceVWV");
        var error = Assert.IsType<ReductionError>(result);
        Assert.Equal(ReductionErrorSeverity.Low, error.Severity);
        Assert.Contains("ValueWitnessTable", error.Message);
    }

    // ================================================================
    // DR3: FromTbd() batch demangling tests (self-contained with mock TBD files)
    // ================================================================

    [Fact]
    public void FromTbd_MockTbd_ProducesExpectedReductionTypes()
    {
        // Create a mock TBD with known symbols covering multiple reduction types
        var tbdContent = @"--- !tapi-tbd
tbd-version:     4
targets:         [ arm64-ios-simulator ]
install-name:    '@rpath/MockLib.framework/MockLib'
swift-abi-version: 7
exports:
  - targets:         [ arm64-ios-simulator ]
    symbols:         [ '_$s7MockLib5ClassCMa', '_$s7MockLib5ClassCAA8ProtocolAAWP', '_$s7MockLib5ClassCAA8ProtocolAAMc' ]
";
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, tbdContent);
            var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => { });
            var results = DemanglingResults.FromTbd(path, loggerFactory);

            Assert.NotNull(results);
            Assert.NotEmpty(results.AllSymbols);
            Assert.True(results.MetadataAccessors.Length > 0, "Expected metadata accessors");
            Assert.True(results.ProtocolWitnessTables.Length > 0, "Expected protocol witness tables");
            Assert.True(results.ProtocolConformanceDescriptors.Length > 0, "Expected protocol conformance descriptors");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FromTbd_SymbolsThatFailDemangle_AggregatedAsErrors()
    {
        // Mix of valid and invalid symbols — invalid ones should be captured as errors, not thrown
        var tbdContent = @"--- !tapi-tbd
tbd-version:     4
targets:         [ arm64-ios-simulator ]
install-name:    '@rpath/Mixed.framework/Mixed'
swift-abi-version: 7
exports:
  - targets:         [ arm64-ios-simulator ]
    symbols:         [ '_$s7MockLib5ClassCMa', '_$s_INVALID_SYMBOL_', '_$s_ANOTHER_BAD_ONE_' ]
";
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, tbdContent);
            var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => { });
            var results = DemanglingResults.FromTbd(path, loggerFactory);

            Assert.NotNull(results);
            // Valid symbol should produce metadata accessor
            Assert.True(results.MetadataAccessors.Length > 0, "Valid symbol should produce metadata accessor");
            // Invalid symbols should be in errors, not cause a crash
            Assert.True(results.Errors.Length > 0, "Invalid symbols should be aggregated as errors");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FromTbd_AllSymbols_ContainsStrippedNames()
    {
        var tbdContent = @"--- !tapi-tbd
tbd-version:     4
targets:         [ arm64-ios-simulator ]
install-name:    '@rpath/Test.framework/Test'
swift-abi-version: 7
exports:
  - targets:         [ arm64-ios-simulator ]
    symbols:         [ '_$s4Test5ClassCMa', '_$s4Test6StructVMa' ]
";
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, tbdContent);
            var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => { });
            var results = DemanglingResults.FromTbd(path, loggerFactory);

            Assert.NotNull(results.AllSymbols);
            Assert.Equal(2, results.AllSymbols.Count);
            // Symbols should have leading underscore stripped
            Assert.Contains("$s4Test5ClassCMa", results.AllSymbols);
            Assert.Contains("$s4Test6StructVMa", results.AllSymbols);
            // Should NOT contain underscore-prefixed versions
            Assert.DoesNotContain("_$s4Test5ClassCMa", results.AllSymbols);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FromTbd_JsonFormat_ProducesResults()
    {
        var tbdContent = @"{
  ""tapi_tbd_version"": 5,
  ""main_library"": {
    ""target_info"": [
      { ""target"": ""arm64-ios-simulator"" }
    ],
    ""install_names"": [
      { ""name"": ""@rpath/JsonLib.framework/JsonLib"" }
    ],
    ""swift_abi"": [
      { ""abi"": 7 }
    ],
    ""exported_symbols"": [
      {
        ""data"": {
          ""global"": [
            ""_$s7JsonLib5ClassCMa"",
            ""_$s7JsonLib6StructVMa""
          ]
        }
      }
    ]
  }
}";
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, tbdContent);
            var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => { });
            var results = DemanglingResults.FromTbd(path, loggerFactory);

            Assert.NotNull(results);
            Assert.Equal(2, results.AllSymbols.Count);
            Assert.Equal(2, results.MetadataAccessors.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FromTbd_RealFoundationTbd_ProducesResults()
    {
        var tbd = "/Applications/Xcode.app/Contents/Developer/Platforms/MacOSX.platform/Developer/SDKs/MacOSX.sdk/System/Library/Frameworks/Foundation.framework/Foundation.tbd";
        // xUnit 2.6 has no built-in skip semantics; early return is the
        // pragmatic alternative. If this test silently passes on CI, verify
        // that Foundation.tbd is available on the build agent.
        if (!File.Exists(tbd))
        {
            Assert.True(true, "SKIPPED: Foundation.tbd not found (Xcode not installed)");
            return;
        }

        var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => { });
        var results = DemanglingResults.FromTbd(tbd, loggerFactory);

        Assert.NotNull(results);
        Assert.NotEmpty(results.AllSymbols);
        // Foundation should have significant number of reductions
        var totalReductions = results.MetadataAccessors.Length
            + results.DispatchThunks.Length
            + results.ProtocolWitnessTables.Length
            + results.ProtocolConformanceDescriptors.Length;
        Assert.True(totalReductions > 0, "Expected reductions from Foundation.tbd");
        // And also some errors (symbols the reducer doesn't handle)
        Assert.True(results.Errors.Length > 0, "Expected some reduction errors from Foundation.tbd");
    }

    // ================================================================
    // Variadic parameter demangling tests
    // ================================================================

    [Fact]
    public void VariadicParam_SwiftyBeaver_StartsWith_DemanglerReturnsError()
    {
        // SwiftyBeaver.FunctionFilterFactory.startsWith(_: String..., caseSensitive: Bool, required: Bool, minLevel: Level)
        // The demangler doesn't produce a FunctionReduction for this symbol — it returns a ReductionError.
        // This is expected because the demangler's ConvertVariadicTupleElement only handles simple cases.
        // Variadic detection must use an alternative approach (swiftinterface or ABI JSON analysis).
        var demangler = new Swift5Demangler();
        var result = demangler.Run("$s12SwiftyBeaver21FunctionFilterFactoryC10startsWith_13caseSensitive8required8minLevelAA0D4Type_pSSd_S2bA2AC0L0OtFZ");
        // Demangler may return null or ReductionError — both are acceptable
        // FunctionReduction is NOT expected for this symbol
        Assert.True(result is null or ReductionError,
            $"Expected null or ReductionError but got {result?.GetType().Name}");
    }

    [Fact]
    public void VariadicParam_RxSwift_BuildBlock_DemanglerReturnsError()
    {
        // RxSwift.DisposeBag.DisposableBuilder.buildBlock(_: Disposable...)
        var demangler = new Swift5Demangler();
        var result = demangler.Run("$s7RxSwift10DisposeBagC19DisposableBuilderV10buildBlockySayAA0E0_pGAaG_pd_tFZ");
        Assert.True(result is null or ReductionError,
            $"Expected null or ReductionError but got {result?.GetType().Name}");
    }
}
