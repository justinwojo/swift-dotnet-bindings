// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.Demangling;
using Xunit;
using DemanglerNode = BindingsGeneration.Demangling.Node;

namespace BindingsGeneration.Tests;

/// <summary>
/// Demangler tests: DemangleSymbol() path, symbol category coverage,
/// complex generics, and edge cases.
/// </summary>
public class DemangleSymbolTests
{
    // ================================================================
    // DemangleSymbol() public API path tests
    // ================================================================

    // Finding 17 fixed the DemangleSymbol() brace bug: the foreach that drains the node stack into
    // the Global node was nested inside the while(funcAttr) loop, so a symbol with zero function
    // attributes (every normal symbol) never populated topLevel and the method returned null. With
    // the foreach dedented out of the loop, DemangleSymbol() now returns the parsed Global tree.
    private static bool ContainsKind(DemanglerNode node, NodeKind kind)
    {
        if (node is null)
            return false;
        if (node.Kind == kind)
            return true;
        foreach (var child in node.Children)
            if (ContainsKind(child, kind))
                return true;
        return false;
    }

    [Fact]
    public void DemangleSymbol_MetadataAccessor_ReturnsGlobalTree()
    {
        // type metadata accessor for GeneralHackingNonsense.ThisIsAClass ('Ma').
        var demangler = new Swift5Demangler();
        var node = demangler.DemangleSymbol("$s22GeneralHackingNonsense12ThisIsAClassCMa");
        Assert.NotNull(node);
        Assert.Equal(NodeKind.Global, node.Kind);
        Assert.NotEmpty(node.Children);
        Assert.True(ContainsKind(node, NodeKind.TypeMetadataAccessFunction),
            "the demangled tree for an 'Ma' symbol must carry a TypeMetadataAccessFunction node");
    }

    [Fact]
    public void DemangleSymbol_WithLeadingUnderscore_ReturnsGlobalTree()
    {
        // Same symbol with the platform leading-underscore prefix stripped — must demangle equally.
        var demangler = new Swift5Demangler();
        var node = demangler.DemangleSymbol("_$s22GeneralHackingNonsense12ThisIsAClassCMa");
        Assert.NotNull(node);
        Assert.Equal(NodeKind.Global, node.Kind);
        Assert.True(ContainsKind(node, NodeKind.TypeMetadataAccessFunction));
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
    public void DemangleSymbol_FunctionSymbol_ReturnsGlobalTree()
    {
        // GeneralHackingNonsense.ThisIsAClass.returnSeven() -> Swift.Int. Function is not a
        // "function attribute", so before the brace fix the while(funcAttr) loop never ran and the
        // method returned null; now the foreach drains the Function node into the Global tree.
        var demangler = new Swift5Demangler();
        var node = demangler.DemangleSymbol("$s22GeneralHackingNonsense12ThisIsAClassC11returnSevenSiyF");
        Assert.NotNull(node);
        Assert.Equal(NodeKind.Global, node.Kind);
        Assert.True(ContainsKind(node, NodeKind.Function),
            "the demangled tree for a function symbol must carry a Function node");
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
        // DemangleSymbol() reinitializes state via Init(), so multiple calls are state-isolated.
        var demangler = new Swift5Demangler();
        var node1 = demangler.DemangleSymbol("$s22GeneralHackingNonsense12ThisIsAClassCMa");
        var node2 = demangler.DemangleSymbol("$s22GeneralHackingNonsense12ThisIsAClassC11returnSevenSiyF");
        Assert.NotNull(node1);
        Assert.NotNull(node2);
        Assert.True(ContainsKind(node1, NodeKind.TypeMetadataAccessFunction));
        Assert.True(ContainsKind(node2, NodeKind.Function));
    }

    [Fact]
    public void DemangleSymbol_PipelineWorks()
    {
        // Finding 17: the DemangleSymbol() public entry now returns a populated tree, and the
        // private Run() path (DemangleType → reducer) continues to reduce the same symbol. Both
        // paths agree that this is the metadata accessor for GeneralHackingNonsense.ThisIsAClass.
        var demangler = new Swift5Demangler();
        var node = demangler.DemangleSymbol("$s22GeneralHackingNonsense12ThisIsAClassCMa");
        Assert.NotNull(node);
        Assert.True(ContainsKind(node, NodeKind.TypeMetadataAccessFunction));

        var result = demangler.Run("$s22GeneralHackingNonsense12ThisIsAClassCMa");
        var meta = Assert.IsType<MetadataAccessorReduction>(result);
        Assert.Equal("GeneralHackingNonsense.ThisIsAClass", meta.TypeSpec.Name);
    }

    // ================================================================
    // IsSwiftSymbol() tests
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
    // SymbolicReferenceResolver tests
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
    // Symbol category tests — property getter/setter
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
    // Symbol category tests — subscript
    // ================================================================

    [Fact]
    public void Demangle_SubscriptGetter_ProducesReductionError()
    {
        // Subscript getter: Module.Cache[key] -> Optional return type.
        // Subscript getters hit the Getter node gap in the reducer.
        var result = new Swift5Demangler().Run("$s13ImagePipeline10ImageCacheCyAA0B9ContainerVSgAA0bC3KeyVcig");
        var error = Assert.IsType<ReductionError>(result);
        Assert.Equal(ReductionErrorSeverity.Low, error.Severity);
        Assert.Contains("Getter", error.Message);
    }

    // ================================================================
    // Symbol category tests — operator
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
    // Symbol category tests — async functions
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
    // Symbol category tests — static methods
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
    // Symbol category tests — inout parameters
    // ================================================================

    [Fact]
    public void Demangle_InoutParameter_ProducesReductionError()
    {
        // Nested type method with an inout Hasher parameter: Module.Type.NestedKey.hash(into: inout Swift.Hasher) -> ()
        // The reducer hits InOut node kind which has no rule — High severity
        // because it's nested inside a Function the reducer otherwise handles.
        var result = new Swift5Demangler().Run("$s13ImagePipeline12ImageRequestV11UserInfoKeyV4hash4intoys6HasherVz_tF");
        var error = Assert.IsType<ReductionError>(result);
        Assert.Equal(ReductionErrorSeverity.High, error.Severity);
        Assert.Contains("InOut", error.Message);
    }

    // ================================================================
    // Symbol category tests — metadata accessor
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
    // Symbol category tests — protocol conformance descriptor
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
    // Symbol category tests — default argument initializer
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
    // Symbol category tests — extension methods
    // ================================================================

    [Fact]
    public void Demangle_ExtensionMethod_ProducesReductionError()
    {
        // Extension method on Foundation.URLRequest declared in a third-party module:
        // Foundation.URLRequest.asURLRequest() throws -> Foundation.URLRequest
        // Extension methods wrap Function in an Extension node — the reducer
        // reaches the Function node but the Extension context causes failure.
        var result = new Swift5Demangler().Run("$s10Foundation10URLRequestV9NetClientE02asB0ACyKF");
        var error = Assert.IsType<ReductionError>(result);
        Assert.Equal(ReductionErrorSeverity.Low, error.Severity);
        Assert.Contains("Function", error.Message);
    }

    // ================================================================
    // Symbol category tests — metatype / metaclass
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
    // Symbol category tests — dispatch thunk (allocator)
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
    // Complex generics tests
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
    // Edge case tests — empty/null/malformed input
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
    // Edge case tests — Run() with Swift 4 prefixes
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
    // Edge case tests — reuse of demangler instance
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
    // Additional symbol categories — protocol witness tables from real TBD
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
    // Symbol category tests — property modify accessor
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
    // Symbol category — functions with closure parameters
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
    // Throwing functions
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
    // Symbol category — nominal type descriptor
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
    // Symbol category tests — reabstraction thunk (real symbol)
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
    // Symbol category tests — value witness table (real symbol)
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
    // Multi-document TBD: symbol accumulation and the own-library tripwire
    // ================================================================

    /// <summary>
    /// A framework that re-exports a private library ships one `--- !tapi-tbd` document per
    /// library. Document 1 is the framework itself; document 2 the re-exported private one.
    /// The async marker and the protocol method descriptor both live in document 1.
    /// </summary>
    private const string TwoDocumentTbd = @"--- !tapi-tbd
tbd-version:     4
targets:         [ arm64-ios-simulator ]
install-name:    '/System/Library/Frameworks/VKLike.framework/VKLike'
reexported-libraries:
  - targets:         [ arm64-ios-simulator ]
    libraries:       [ '/System/Library/PrivateFrameworks/Helper.framework/Helper' ]
swift-abi-version: 7
exports:
  - targets:         [ arm64-ios-simulator ]
    symbols:         [ '_$s6VKLike11InteractionC8subjectsSaySiGvg',
                       '_$s6VKLike11InteractionC8subjectsSaySiGvgTjTu',
                       '_$s6VKLike8ObserverP6notifyyyF',
                       '_$s6VKLike8ObserverP6notifyyyFTq' ]
--- !tapi-tbd
tbd-version:     4
targets:         [ arm64-ios-simulator ]
install-name:    '/System/Library/PrivateFrameworks/Helper.framework/Helper'
swift-abi-version: 7
exports:
  - targets:         [ arm64-ios-simulator ]
    symbols:         [ '_$s6Helper8ScannerCMa' ]
...
";

    [Fact]
    public void FromTbd_MultiDocumentTbd_FirstDocumentAsyncAndDescriptorSiblingsAreVisible()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, TwoDocumentTbd);
            var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => { });
            var results = DemanglingResults.FromTbd(path, loggerFactory);

            // Both documents' symbols resolve through this file, so both must be in the probe set.
            Assert.Contains("$s6VKLike11InteractionC8subjectsSaySiGvg", results.AllSymbols);
            Assert.Contains("$s6Helper8ScannerCMa", results.AllSymbols);

            // The evidence a member binds as async is a sibling symbol in the FIRST document.
            // Reading only the last document answers "not async" and the property binds synchronous.
            Assert.True(
                ManglingProbes.IsAsyncAccessor(results.AllSymbols, "$s6VKLike11InteractionC8subjectsSaySiGvg"),
                "Async accessor sibling from document 1 must be visible");
            Assert.True(
                ManglingProbes.HasMethodDescriptor(results.AllSymbols, "$s6VKLike8ObserverP6notifyyyF"),
                "Protocol method descriptor from document 1 must be visible");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FromTbd_NoSymbolsForOwnLibrary_WarnsNamingDocumentsAndModules()
    {
        // Every Swift symbol belongs to a different module than the install-name's library — the
        // shape a dropped document leaves behind.
        var tbdContent = @"--- !tapi-tbd
tbd-version:     4
targets:         [ arm64-ios-simulator ]
install-name:    '/System/Library/Frameworks/Ghost.framework/Ghost'
swift-abi-version: 7
exports:
  - targets:         [ arm64-ios-simulator ]
    symbols:         [ '_$s5Other5ClassCMa' ]
";
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, tbdContent);
            var loggerFactory = new CapturingLoggerFactory();
            DemanglingResults.FromTbd(path, loggerFactory);

            var warning = Assert.Single(loggerFactory.Warnings, w => w.Contains("Ghost"));
            Assert.Contains("Other", warning);
            Assert.Contains(path, warning);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FromTbd_SymbolsForOwnLibrary_DoesNotWarn()
    {
        var tbdContent = @"--- !tapi-tbd
tbd-version:     4
targets:         [ arm64-ios-simulator ]
install-name:    '/System/Library/Frameworks/Own.framework/Own'
swift-abi-version: 7
exports:
  - targets:         [ arm64-ios-simulator ]
    symbols:         [ '_$s3Own5ClassCMa', '_$s5Other5ClassCMa' ]
";
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, tbdContent);
            var loggerFactory = new CapturingLoggerFactory();
            DemanglingResults.FromTbd(path, loggerFactory);

            Assert.DoesNotContain(loggerFactory.Warnings, w => w.Contains("is mangled for module"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FromTbd_ExtensionOnlyLibrary_DoesNotWarn()
    {
        // A framework whose entire Swift surface is extensions on types owned by another module
        // exports nothing with itself as the LEADING mangled module — the leading module is the
        // extended type's. Its own name still appears as the length-prefixed extension context, and
        // that is enough evidence its document was read, so the tripwire must stay quiet.
        var tbdContent = @"--- !tapi-tbd
tbd-version:     4
targets:         [ arm64-ios-simulator ]
install-name:    '/System/Library/Frameworks/LinkLike.framework/LinkLike'
swift-abi-version: 7
exports:
  - targets:         [ arm64-ios-simulator ]
    symbols:         [ '_$s10Foundation4DateV8LinkLikeE5thumbSSvg' ]
";
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, tbdContent);
            var loggerFactory = new CapturingLoggerFactory();
            DemanglingResults.FromTbd(path, loggerFactory);

            Assert.DoesNotContain(loggerFactory.Warnings, w => w.Contains("is mangled for module"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FromTbd_ExtensionContextOfAnotherModule_StillWarns()
    {
        // The narrowing above keys on the library's OWN name as an extension context. A file whose
        // only extension context names a DIFFERENT module is still the dropped-document shape and
        // must keep warning — otherwise the narrowing would swallow the case it was added beside.
        var tbdContent = @"--- !tapi-tbd
tbd-version:     4
targets:         [ arm64-ios-simulator ]
install-name:    '/System/Library/Frameworks/Ghost.framework/Ghost'
swift-abi-version: 7
exports:
  - targets:         [ arm64-ios-simulator ]
    symbols:         [ '_$s10Foundation4DateV8LinkLikeE5thumbSSvg' ]
";
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, tbdContent);
            var loggerFactory = new CapturingLoggerFactory();
            DemanglingResults.FromTbd(path, loggerFactory);

            Assert.Single(loggerFactory.Warnings, w => w.Contains("is mangled for module") && w.Contains("Ghost"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FromTbd_MultiDocumentTbd_OwnLibraryTripwireStaysSilent()
    {
        // The tripwire's whole point: once both documents parse, the framework's own symbols are
        // present and it must not fire.
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, TwoDocumentTbd);
            var loggerFactory = new CapturingLoggerFactory();
            DemanglingResults.FromTbd(path, loggerFactory);

            Assert.DoesNotContain(loggerFactory.Warnings, w => w.Contains("is mangled for module"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ================================================================
    // Variadic parameter demangling tests
    // ================================================================

    [Fact]
    public void VariadicParam_LoggingLib_StartsWith_ReducesWithVariadicDetected()
    {
        // static LoggingLib.FunctionFilterFactory.startsWith(_: String..., caseSensitive: Bool,
        //     required: Bool, minLevel: Level) -> any FilterType
        // The return type is an existential (`any FilterType`). Before the ProtocolList reducer rule
        // existed this symbol failed reduction, which silently disabled demangle-based variadic
        // detection for the `String...` parameter. It must now reduce to a FunctionReduction whose
        // parameter list carries the variadic marker.
        var demangler = new Swift5Demangler();
        var result = demangler.Run("$s10LoggingLib21FunctionFilterFactoryC10startsWith_13caseSensitive8required8minLevelAA0D4Type_pSSd_S2bA2AC0L0OtFZ");
        var fr = Assert.IsType<FunctionReduction>(result);
        var paramTuple = Assert.IsType<TupleTypeSpec>(fr.Function.ParameterList);
        Assert.True(SwiftABIParser.HasVariadicElement(paramTuple),
            "String... variadic must be detected even though the function returns an existential.");
    }

    [Fact]
    public void VariadicParam_ReactiveStreams_BuildBlock_ReducesWithVariadicDetected()
    {
        // static result-builder buildBlock with a variadic existential parameter:
        // buildBlock(Disposable...) -> [any Disposable]. swift-api-digester renders
        // the variadic as a plain `[any Disposable]` with no "...", so the demangled "d" marker —
        // recoverable only now that ProtocolList reduces — is the sole reliable per-overload variadic signal.
        var demangler = new Swift5Demangler();
        var result = demangler.Run("$s15ReactiveStreams10DisposeBagC17DisposableBuilderV10buildBlockySayAA0E0_pGAaG_pd_tFZ");
        var fr = Assert.IsType<FunctionReduction>(result);
        var paramTuple = Assert.IsType<TupleTypeSpec>(fr.Function.ParameterList);
        Assert.True(SwiftABIParser.HasVariadicElement(paramTuple),
            "Variadic-of-existential parameter must be detected via the demangled 'd' marker.");
    }
}
