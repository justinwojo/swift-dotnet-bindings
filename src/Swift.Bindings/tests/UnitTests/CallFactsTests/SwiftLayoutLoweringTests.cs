// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Immutable;
using System.Linq;
using BindingsGeneration.Demangling;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The layout lowering behind <see cref="SwiftLayoutValueLowering"/>: Swift's aggregate lowering of a
/// resolved layout into register pieces, the resolver's layouts for carriers, generics, metatypes and
/// type-database records, the function representation the symbol records, the synchronous-call
/// refusals, and the physical signature the predictor builds from them. The swiftc oracle checks the
/// same rules against real IR; these pin each rule on its own, without a toolchain.
/// </summary>
public class SwiftLayoutLoweringTests
{
    // ---- aggregate lowering ----

    private static SwiftLeafLayout Fields(params SwiftScalarKind[] kinds) =>
        SwiftLeafLayout.Sequence(kinds.Select(k => SwiftLeafLayout.Scalar(k)));

    private static string Pieces(SwiftLeafLayout layout) =>
        string.Join(", ", SwiftAggregateLowering.Lower(layout).Select(p => p.IrType));

    [Theory]
    [InlineData("double, i64", SwiftScalarKind.Double, SwiftScalarKind.Int64)]
    [InlineData("float, float, i64", SwiftScalarKind.Float, SwiftScalarKind.Float, SwiftScalarKind.Int64)]
    [InlineData("i32, float, double", SwiftScalarKind.Int32, SwiftScalarKind.Float, SwiftScalarKind.Double)]
    [InlineData("float, double", SwiftScalarKind.Float, SwiftScalarKind.Double)]
    // Integers sharing an 8-byte chunk merge into the smallest integer that covers them.
    [InlineData("i64, i64", SwiftScalarKind.Int8, SwiftScalarKind.Int16, SwiftScalarKind.Int32, SwiftScalarKind.Int64)]
    [InlineData("i32", SwiftScalarKind.Int8, SwiftScalarKind.Int8, SwiftScalarKind.Int16)]
    [InlineData("i64, ptr", SwiftScalarKind.Int64, SwiftScalarKind.Pointer)]
    [InlineData("i1", SwiftScalarKind.Bool)]
    [InlineData("", new SwiftScalarKind[0])]
    public void AggregateLowering_SplitsAStructIntoSwiftsRegisterPieces(string expected, params SwiftScalarKind[] fields)
    {
        Assert.Equal(expected, Pieces(Fields(fields)));
    }

    [Fact]
    public void AggregateLowering_FourPiecesAreDirect_FiveAreIndirect()
    {
        var four = SwiftAggregateLowering.Lower(Fields(Enumerable.Repeat(SwiftScalarKind.Double, 4).ToArray()));
        var five = SwiftAggregateLowering.Lower(Fields(Enumerable.Repeat(SwiftScalarKind.Double, 5).ToArray()));

        Assert.False(SwiftAggregateLowering.PassesIndirectly(four));
        Assert.True(SwiftAggregateLowering.PassesIndirectly(five));
    }

    // ---- the resolver ----

    private static TypeDatabase Db(params TypeRecord[] records)
    {
        var db = new TypeDatabase();
        var module = new ModuleTypeDatabase("M", "/fake/M.dylib");
        foreach (var record in records)
            module.RegisterType(record.SwiftTypeName, record);
        db.AddModuleDatabase(module);
        return db;
    }

    private static TypeRecord Record(string name, TypeRecordKind kind, TypeRecordFlags flags, string? layout = null, int size = 0) => new()
    {
        CSharpTypeName = CSharpTypeName.FromNamespaceAndName("M", name),
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"M.{name}"),
        MetadataAccessor = $"$s1M{name.Length}{name}V",
        Flags = flags,
        Kind = kind,
        InlineSize = size,
        AbiFieldLayout = layout,
    };

    private static SwiftLayoutValueLowering Lowering(TypeDatabase? db = null) =>
        new(Array.Empty<ModuleDecl>(), db ?? Db());

    private static SwiftTypeFacts Type(string spelling, SwiftTypeCategory category = SwiftTypeCategory.Struct,
        SwiftFunctionRepresentation? representation = null) =>
        new() { Spelling = spelling, Category = category, IsFrozen = true, Representation = representation };

    private static string Direct(SwiftLoweringAnswer answer)
    {
        Assert.True(answer is { Outcome: SwiftLoweringOutcome.Direct, HasLayout: true }, $"{answer.Outcome}: {answer.Reason}");
        return string.Join(", ", answer.Layout.Select(p => p.IrType));
    }

    [Theory]
    [InlineData("Swift.Int", "i64")]
    [InlineData("Swift.UInt8", "i8")]
    [InlineData("Swift.Bool", "i1")]
    [InlineData("Swift.Double", "double")]
    [InlineData("CoreGraphics.CGFloat", "double")]
    [InlineData("Swift.String", "i64, ptr")]
    [InlineData("Swift.Character", "i64, ptr")]
    [InlineData("Swift.Substring", "i64, i64, i64, ptr")]
    [InlineData("Swift.Array<Swift.Int>", "ptr")]
    [InlineData("Swift.ContiguousArray<Swift.Int>", "ptr")]
    [InlineData("Swift.Dictionary<Swift.String, Swift.Int>", "ptr")]
    [InlineData("Swift.Unicode.Scalar", "i32")]
    [InlineData("Swift.UnsafePointer<Swift.Int>", "ptr")]
    [InlineData("Swift.UnsafeBufferPointer<Swift.Int>", "i64, i64")]
    [InlineData("Swift.Optional<Swift.Int>", "i64, i8")]
    [InlineData("CoreGraphics.CGPoint", "double, double")]
    [InlineData("Foundation.Decimal", "i64, i64, i32")]
    // A range is its lower bound, then its upper bound.
    [InlineData("Swift.Range<Swift.Int>", "i64, i64")]
    [InlineData("Swift.ClosedRange<Swift.Int32>", "i64")]
    // CMTime packs to 4-byte alignment: value, timescale and flags, epoch.
    [InlineData("CoreMedia.CMTime", "i64, i64, i64")]
    // An Error existential is one boxed reference, so its optional is one word.
    [InlineData("Swift.Optional<any Swift.Error>", "i64")]
    public void Resolver_CarriersLowerToTheirSwiftPieces(string spelling, string expected)
    {
        Assert.Equal(expected, Direct(Lowering().Lower(Type(spelling))));
    }

    [Fact]
    public void Resolver_AnErrorExistentialIsOneReference_OtherExistentialsAreAddressOnly()
    {
        var lowering = Lowering();

        Assert.Equal("ptr", Direct(lowering.Lower(Type("any Swift.Error", SwiftTypeCategory.Existential))));
        Assert.Equal(SwiftLoweringOutcome.Indirect, lowering.Lower(Type("Any", SwiftTypeCategory.Existential)).Outcome);
    }

    [Fact]
    public void Resolver_CMTimeKeepsItsPackedAlignmentInsideAStruct()
    {
        // Behind an Int32, a 4-byte-aligned CMTime starts at offset 4, not 8: 28 bytes in all.
        var tagged = Lowering().Resolver.Resolve(TypeSpecParser.Parse("(Swift.Int32, CoreMedia.CMTime)")!);

        Assert.NotNull(tagged.Layout);
        Assert.Equal(28, tagged.Layout!.Size);
        Assert.Equal(4, tagged.Layout.Alignment);
    }

    [Fact]
    public void Resolver_TupleParameterSplits_TupleResultStaysWhole()
    {
        var tuple = Type("(Swift.Int, Swift.Double)", SwiftTypeCategory.Tuple);

        Assert.Equal("i64, double", Direct(Lowering().Lower(tuple, generics: null, asResult: false)));
        Assert.Equal("i64, double", Direct(Lowering().Lower(tuple, generics: null, asResult: true)));
    }

    [Theory]
    [InlineData(null, SwiftLoweringOutcome.Unknown)]
    [InlineData(SwiftFunctionRepresentation.Thick, SwiftLoweringOutcome.Direct)]
    [InlineData(SwiftFunctionRepresentation.Thin, SwiftLoweringOutcome.Direct)]
    [InlineData(SwiftFunctionRepresentation.C, SwiftLoweringOutcome.Direct)]
    public void Resolver_AnUnattributedFunctionTypeNeedsItsRecordedRepresentation(SwiftFunctionRepresentation? representation, SwiftLoweringOutcome outcome)
    {
        var answer = Lowering().Lower(Type("(Swift.Int) -> Swift.Int", SwiftTypeCategory.Closure, representation));

        Assert.Equal(outcome, answer.Outcome);
        if (outcome == SwiftLoweringOutcome.Direct)
            Assert.Equal(representation == SwiftFunctionRepresentation.Thick ? "ptr, ptr" : "ptr", Direct(answer));
    }

    [Fact]
    public void Resolver_GenericParameters_AreAddressOnlyUnlessBoundToClasses()
    {
        var unbound = new SwiftGenericSignatureFacts { Parameters = ImmutableArray.Create("τ_0_0") };
        var boundToClasses = unbound with
        {
            Requirements = ImmutableArray.Create(new SwiftGenericRequirementFacts("τ_0_0", "Swift.AnyObject", SwiftGenericRequirementKind.Conformance)
            {
                TargetKind = SwiftRequirementTargetKind.AnyObject,
            }),
        };
        var lowering = Lowering();
        var parameter = Type("τ_0_0", SwiftTypeCategory.GenericParameter);

        Assert.Equal(SwiftLoweringOutcome.Indirect, lowering.Lower(parameter, unbound, asResult: false).Outcome);
        Assert.Equal("ptr", Direct(lowering.Lower(parameter, boundToClasses, asResult: false)));
        Assert.Equal(SwiftLoweringOutcome.Unknown, lowering.Lower(parameter, generics: null, asResult: false).Outcome);
    }

    [Theory]
    [InlineData("τ_0_0.Type", "ptr")]
    [InlineData("any Swift.Any.Type", "ptr")]
    [InlineData("Swift.AnyObject", "ptr")]
    public void Resolver_ThickMetatypesAndReferencesAreOnePointer(string spelling, string expected)
    {
        var generics = new SwiftGenericSignatureFacts { Parameters = ImmutableArray.Create("τ_0_0") };

        Assert.Equal(expected, Direct(Lowering().Lower(Type(spelling, SwiftTypeCategory.Metatype), generics, asResult: false)));
    }

    [Fact]
    public void Resolver_TypeDatabaseRecords_ClassIsAReference_ResilientIsIndirect_ForeignFrozenIsNotGuessed()
    {
        // A frozen struct from another module that is not a known carrier is refused rather than laid
        // out from its database record: a record's flags do not say how swiftc passes it.
        var db = Db(
            Record("Token", TypeRecordKind.Class, TypeRecordFlags.None),
            Record("Resilient", TypeRecordKind.Struct, TypeRecordFlags.None),
            Record("Pair", TypeRecordKind.Struct, TypeRecordFlags.Frozen, "i8,i8", 16));
        var lowering = Lowering(db);

        Assert.Equal("ptr", Direct(lowering.Lower(Type("M.Token", SwiftTypeCategory.Class))));
        Assert.Equal(SwiftLoweringOutcome.Indirect, lowering.Lower(Type("M.Resilient")).Outcome);
        Assert.Equal(SwiftLoweringOutcome.Unknown, lowering.Lower(Type("M.Pair")).Outcome);
        Assert.Equal(SwiftLoweringOutcome.Unknown, lowering.Lower(Type("M.Missing")).Outcome);
    }

    // ---- the representation the symbol records ----

    [Theory]
    [InlineData("$s1M5thickyyS2icF", SwiftFunctionRepresentation.Thick)]
    [InlineData("$s13LoweringProbe11carrierThinyyyyXfF", SwiftFunctionRepresentation.Thin)]
    [InlineData("$s13LoweringProbe16carrierCFunctionys5Int32VA2DXCF", SwiftFunctionRepresentation.C)]
    [InlineData("$s1M5blockyyyyXBF", SwiftFunctionRepresentation.Block)]
    // A subscript's own signature is left out; its C function parameter is what remains.
    [InlineData("$s1M1SVySiyyXCcig", SwiftFunctionRepresentation.C)]
    // A property's type is a value, not the declaration's signature.
    [InlineData("$s1M7handleryySicvg", SwiftFunctionRepresentation.Thick)]
    [InlineData("$s1M5mixedyyyyc_yyXBtF", null)]
    [InlineData("not a symbol", null)]
    public void Symbol_RecordsTheOneRepresentationItsFunctionTypesShare(string symbol, SwiftFunctionRepresentation? expected)
    {
        var kinds = new Swift5Demangler().FunctionTypeKindsInSignature(symbol);

        Assert.Equal(expected, SwiftCallFactsBuilder.UniformRepresentation(kinds));
    }

    [Fact]
    public void Symbol_PropertyOfFunctionTypeKeepsItsValueType()
    {
        var kinds = new Swift5Demangler().FunctionTypeKindsInSignature("$s1M7handleryySicvg");

        Assert.Equal(new[] { NodeKind.FunctionType }, kinds);
    }

    [Fact]
    public void Symbol_TypedThrowsIsReadFromTheDeclarationsOwnFunctionType()
    {
        var demangler = new Swift5Demangler();

        Assert.True(demangler.TryGetTypedThrows("$s13LoweringProbe18refusalTypedThrowsyS2iAA12RefusalErrorOYKF", out var error));
        Assert.Equal("LoweringProbe.RefusalError", error.ToString(true));
        Assert.False(demangler.TryGetTypedThrows("$s1M5thickyyS2icF", out _));
        // A property's value is a typed-throwing closure; its getter does not throw.
        Assert.False(demangler.TryGetTypedThrows("$s1M1HC8callbackyyAA1EOYKcvg", out _));
        // A subscript returning one: the subscript's own signature is the shallowest.
        Assert.False(demangler.TryGetTypedThrows("$s1M1HCyyyAA1EOYKcSicig", out _));
    }

    // ---- synchronous-call refusals ----

    private static SwiftCallFacts Free(SwiftCallEffects effects, string symbol = "$s1M1fyyF", params SwiftParameterFacts[] parameters) => new()
    {
        Target = new SwiftCallTarget { Kind = SwiftCallTargetKind.FreeFunction, Symbol = symbol },
        Self = SwiftSelfFacts.None,
        Parameters = parameters.ToImmutableArray(),
        Result = new SwiftResultFacts { Type = Type("Swift.Int") },
        Effects = effects,
    };

    [Theory]
    [InlineData("async", "swifttailcc")]
    [InlineData("typed", "indirect error result")]
    [InlineData("coroutine", "coroutine accessor")]
    [InlineData("pack", "parameter packs")]
    [InlineData("cdecl", "C convention")]
    [InlineData("objc", "Objective-C convention")]
    [InlineData("objcRequirement", "Objective-C message")]
    public void SynchronousCall_RefusesEveryOtherKindOfCallByName(string kind, string reason)
    {
        var facts = kind switch
        {
            "async" => Free(new SwiftCallEffects { IsAsync = true }),
            "typed" => Free(new SwiftCallEffects { Throws = true, TypedThrows = "M.Failure" }),
            "coroutine" => Free(new SwiftCallEffects { IsCoroutine = true }),
            "pack" => Free(new SwiftCallEffects { HasParameterPacks = true }),
            "cdecl" => Free(SwiftCallEffects.None, symbol: "m_function"),
            "objc" => Free(SwiftCallEffects.None, symbol: "$s1M1CC1fyyFTo"),
            "objcRequirement" => Free(SwiftCallEffects.None, symbol: "$s1M1PP1fyyF") with
            {
                Target = new SwiftCallTarget { Kind = SwiftCallTargetKind.Method, Symbol = "$s1M1PP1fyyF", DeclaringType = "M.P" },
                Self = new SwiftSelfFacts
                {
                    Kind = SwiftCallSelfKind.Reference,
                    Mutation = SwiftSelfMutation.Nonmutating,
                    Abstraction = SwiftSelfAbstraction.ProtocolRequirement,
                    Type = Type("τ_0_0", SwiftTypeCategory.GenericParameter),
                },
                Generics = new SwiftGenericSignatureFacts
                {
                    Parameters = ImmutableArray.Create("τ_0_0"),
                    Requirements = ImmutableArray.Create(new SwiftGenericRequirementFacts("τ_0_0", "M.P", SwiftGenericRequirementKind.Conformance)
                    {
                        TargetKind = SwiftRequirementTargetKind.ObjCProtocol,
                    }),
                },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        Assert.Contains(reason, SwiftSynchronousCall.Refusal(facts));
        Assert.False(SwiftPhysicalSignaturePredictor.Predict(facts, Lowering()).IsPredicted);
    }

    [Theory]
    [InlineData("Swift.Error")]
    [InlineData("any Swift.Error")]
    public void SynchronousCall_ThrowsAnyErrorIsUntypedThrows(string thrown)
    {
        Assert.Null(SwiftSynchronousCall.Refusal(Free(new SwiftCallEffects { Throws = true, TypedThrows = thrown })));
    }

    // ---- the physical signature ----

    private static SwiftParameterFacts Param(int index, SwiftTypeFacts type) => new()
    {
        Index = index,
        Type = type,
        Convention = SwiftParameterConvention.Guaranteed,
        ConventionSource = SwiftConventionSource.Default,
    };

    [Fact]
    public void Predictor_ThrowingFreeFunctionKeepsAnUnusedSelfBeforeTheErrorRegister()
    {
        var facts = Free(new SwiftCallEffects { Throws = true }, "$s1M1fyS2iKF", Param(0, Type("Swift.Int")));

        var prediction = SwiftPhysicalSignaturePredictor.Predict(facts, Lowering());

        Assert.Equal("i64 (i64, ptr swiftself, ptr noalias swifterror)", prediction.Signature!.Normalized);
    }

    [Fact]
    public void Predictor_AConstrainedGenericTakesItsMetadataThenItsWitnessTable()
    {
        var facts = Free(SwiftCallEffects.None, "$s1M1fySixAA1PRzlF", Param(0, Type("τ_0_0", SwiftTypeCategory.GenericParameter))) with
        {
            Generics = new SwiftGenericSignatureFacts
            {
                Parameters = ImmutableArray.Create("τ_0_0"),
                OwnParameters = ImmutableArray.Create("τ_0_0"),
                Requirements = ImmutableArray.Create(new SwiftGenericRequirementFacts("τ_0_0", "M.P", SwiftGenericRequirementKind.Conformance)
                {
                    TargetKind = SwiftRequirementTargetKind.Protocol,
                }),
            },
        };

        var prediction = SwiftPhysicalSignaturePredictor.Predict(facts, Lowering());

        Assert.Equal("i64 (ptr noalias, ptr, ptr)", prediction.Signature!.Normalized);
    }

    [Fact]
    public void Predictor_AnObjCProtocolConstraintTakesNoWitnessTable()
    {
        var facts = Free(SwiftCallEffects.None, "$s1M1fySixAA1PRzlF", Param(0, Type("τ_0_0", SwiftTypeCategory.GenericParameter))) with
        {
            Generics = new SwiftGenericSignatureFacts
            {
                Parameters = ImmutableArray.Create("τ_0_0"),
                OwnParameters = ImmutableArray.Create("τ_0_0"),
                Requirements = ImmutableArray.Create(new SwiftGenericRequirementFacts("τ_0_0", "M.P", SwiftGenericRequirementKind.Conformance)
                {
                    TargetKind = SwiftRequirementTargetKind.ObjCProtocol,
                }),
            },
        };

        var prediction = SwiftPhysicalSignaturePredictor.Predict(facts, Lowering());

        // Bound to classes, so the value is one reference; only its metadata follows.
        Assert.Equal("i64 (ptr, ptr)", prediction.Signature!.Normalized);
    }
}
