// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// <see cref="SwiftPassingClassifier"/>: self convention and parameter passing per (member, type).
/// The same small frozen struct is lowered into a getter, inout to a setter or mutating method and
/// handed over to a consuming one; a wide one goes through an address; abstract and resilient selves
/// are always indirect; a parameter's convention picks borrowed, owned or inout; and an unknown
/// lowering or incomplete facts refuse instead of guessing.
/// </summary>
public class SwiftPassingClassifierTests
{
    private static readonly SwiftTypeFacts SmallFrozen = Struct("M.Pair", frozen: true);
    private static readonly SwiftTypeFacts WideFrozen = Struct("M.Wide", frozen: true);
    private static readonly SwiftTypeFacts Resilient = Struct("M.Resilient", frozen: false);
    private static readonly SwiftTypeFacts Token = new() { Spelling = "M.Token", Category = SwiftTypeCategory.Class, IsClassBound = true };

    /// <summary>A lowering that knows exactly the types it is given, by spelling.</summary>
    private sealed class TableLowering : ISwiftValueLowering
    {
        private readonly Dictionary<string, SwiftLoweringAnswer> _answers;
        public List<string> Asked { get; } = new();

        public TableLowering(params (string Spelling, SwiftLoweringAnswer Answer)[] answers) =>
            _answers = answers.ToDictionary(a => a.Spelling, a => a.Answer);

        public SwiftLoweringAnswer Lower(SwiftTypeFacts type)
        {
            Asked.Add(type.Spelling);
            return _answers.TryGetValue(type.Spelling, out var answer)
                ? answer
                : SwiftLoweringAnswer.UnknownBecause($"no entry for {type.Spelling}");
        }
    }

    private static TableLowering Lowering() => new(
        ("M.Pair", SwiftLoweringAnswer.DirectPieces(2, "two words")),
        ("M.Wide", SwiftLoweringAnswer.IndirectBecause("five pieces")));

    private static SwiftTypeFacts Struct(string spelling, bool? frozen) =>
        new() { Spelling = spelling, Category = SwiftTypeCategory.Struct, IsFrozen = frozen };

    private static SwiftCallFacts Facts(
        SwiftSelfFacts self,
        SwiftAccessorKind accessor = SwiftAccessorKind.None,
        params SwiftParameterFacts[] parameters) => new()
    {
        Target = new SwiftCallTarget
        {
            Kind = accessor == SwiftAccessorKind.None ? SwiftCallTargetKind.Method : SwiftCallTargetKind.Accessor,
            Accessor = accessor,
            Symbol = "$sTest",
        },
        Self = self,
        Parameters = parameters.ToImmutableArray(),
        Result = SwiftResultFacts.Void,
        Effects = SwiftCallEffects.None,
    };

    private static SwiftSelfFacts ValueSelf(SwiftTypeFacts type, SwiftSelfMutation mutation,
        SwiftSelfAbstraction abstraction = SwiftSelfAbstraction.Concrete) => new()
    {
        Kind = SwiftCallSelfKind.Value,
        Mutation = mutation,
        Abstraction = abstraction,
        Type = type,
    };

    private static SwiftParameterFacts Param(int index, SwiftTypeFacts type, SwiftParameterConvention convention) => new()
    {
        Index = index,
        Type = type,
        Convention = convention,
        ConventionSource = SwiftConventionSource.Default,
    };

    // ---- SelfConvention: one small frozen struct, four members ----

    [Theory]
    [InlineData(SwiftSelfMutation.Nonmutating, SwiftSelfConvention.Lowered, SwiftPassingOwnership.Borrowed)]
    [InlineData(SwiftSelfMutation.Borrowing, SwiftSelfConvention.Lowered, SwiftPassingOwnership.Borrowed)]
    [InlineData(SwiftSelfMutation.Mutating, SwiftSelfConvention.Inout, SwiftPassingOwnership.BorrowedMutable)]
    [InlineData(SwiftSelfMutation.Consuming, SwiftSelfConvention.Lowered, SwiftPassingOwnership.Consumed)]
    public void SmallFrozenSelf_DependsOnTheMember(SwiftSelfMutation mutation, SwiftSelfConvention expected, SwiftPassingOwnership ownership)
    {
        var result = SwiftPassingClassifier.SelfConvention(Facts(ValueSelf(SmallFrozen, mutation)), Lowering());

        Assert.True(result.IsDecided, result.Refusal);
        Assert.Equal(expected, result.Convention);
        Assert.Equal(ownership, result.Ownership);
        if (expected == SwiftSelfConvention.Lowered)
            Assert.Equal(2, result.Pieces);
    }

    [Fact]
    public void Setter_IsInout_EvenThoughTheGetterOfTheSameTypeIsLowered()
    {
        var lowering = Lowering();
        var getter = SwiftPassingClassifier.SelfConvention(
            Facts(ValueSelf(SmallFrozen, SwiftSelfMutation.Nonmutating), SwiftAccessorKind.Get), lowering);
        var setter = SwiftPassingClassifier.SelfConvention(
            Facts(ValueSelf(SmallFrozen, SwiftSelfMutation.Mutating), SwiftAccessorKind.Set,
                Param(0, new() { Spelling = "Swift.Int", Category = SwiftTypeCategory.Struct, IsFrozen = true }, SwiftParameterConvention.Owned)),
            lowering);

        Assert.Equal(SwiftSelfConvention.Lowered, getter.Convention);
        Assert.Equal(SwiftSelfConvention.Inout, setter.Convention);
    }

    [Fact]
    public void MutatingSelf_IsNeverInferredFromSize_AndNeedsNoLowering()
    {
        // Inout comes from the member, not the type: neither a wide struct nor an unknown layout
        // changes it, and the lowering is not even consulted.
        var lowering = new TableLowering();
        foreach (var type in new[] { SmallFrozen, WideFrozen, Struct("M.Unknown", frozen: null) })
        {
            var result = SwiftPassingClassifier.SelfConvention(Facts(ValueSelf(type, SwiftSelfMutation.Mutating)), lowering);
            Assert.Equal(SwiftSelfConvention.Inout, result.Convention);
        }
        Assert.Empty(lowering.Asked);
    }

    [Theory]
    [InlineData(SwiftSelfMutation.Nonmutating, SwiftSelfConvention.IndirectBorrowed, SwiftPassingOwnership.Borrowed)]
    [InlineData(SwiftSelfMutation.Consuming, SwiftSelfConvention.IndirectOwned, SwiftPassingOwnership.Consumed)]
    [InlineData(SwiftSelfMutation.Mutating, SwiftSelfConvention.Inout, SwiftPassingOwnership.BorrowedMutable)]
    public void WideFrozenSelf_GoesThroughAnAddress(SwiftSelfMutation mutation, SwiftSelfConvention expected, SwiftPassingOwnership ownership)
    {
        var result = SwiftPassingClassifier.SelfConvention(Facts(ValueSelf(WideFrozen, mutation)), Lowering());
        Assert.Equal(expected, result.Convention);
        Assert.Equal(ownership, result.Ownership);
    }

    [Fact]
    public void ResilientSelf_IsIndirect_FromTheFactsAlone()
    {
        var lowering = new TableLowering();
        var result = SwiftPassingClassifier.SelfConvention(Facts(ValueSelf(Resilient, SwiftSelfMutation.Nonmutating)), lowering);
        Assert.Equal(SwiftSelfConvention.IndirectBorrowed, result.Convention);
        Assert.Empty(lowering.Asked);
    }

    [Theory]
    [InlineData(SwiftSelfAbstraction.GenericParent)]
    [InlineData(SwiftSelfAbstraction.ProtocolExtension)]
    [InlineData(SwiftSelfAbstraction.ProtocolRequirement)]
    public void AbstractSelf_IsIndirectWhateverItIsBoundTo(SwiftSelfAbstraction abstraction)
    {
        // A generic parent's members use the unspecialized ABI: even a small frozen self is an address.
        var lowering = Lowering();
        Assert.Equal(SwiftSelfConvention.IndirectBorrowed,
            SwiftPassingClassifier.SelfConvention(Facts(ValueSelf(SmallFrozen, SwiftSelfMutation.Nonmutating, abstraction)), lowering).Convention);
        Assert.Equal(SwiftSelfConvention.IndirectOwned,
            SwiftPassingClassifier.SelfConvention(Facts(ValueSelf(SmallFrozen, SwiftSelfMutation.Consuming, abstraction)), lowering).Convention);
        Assert.Equal(SwiftSelfConvention.Inout,
            SwiftPassingClassifier.SelfConvention(Facts(ValueSelf(SmallFrozen, SwiftSelfMutation.Mutating, abstraction)), lowering).Convention);
        Assert.Empty(lowering.Asked);
    }

    [Theory]
    [InlineData(SwiftCallSelfKind.None, SwiftSelfConvention.None)]
    [InlineData(SwiftCallSelfKind.ThinMetatype, SwiftSelfConvention.None)]
    [InlineData(SwiftCallSelfKind.ThickMetatype, SwiftSelfConvention.Metatype)]
    [InlineData(SwiftCallSelfKind.ClosureContext, SwiftSelfConvention.Reference)]
    public void NonValueSelves(SwiftCallSelfKind kind, SwiftSelfConvention expected)
    {
        var self = new SwiftSelfFacts { Kind = kind, Mutation = SwiftSelfMutation.NotApplicable, Abstraction = SwiftSelfAbstraction.Concrete };
        var result = SwiftPassingClassifier.SelfConvention(Facts(self), new TableLowering());
        Assert.Equal(expected, result.Convention);
    }

    [Theory]
    [InlineData(SwiftSelfMutation.Nonmutating, SwiftPassingOwnership.Borrowed)]
    [InlineData(SwiftSelfMutation.Borrowing, SwiftPassingOwnership.Borrowed)]
    [InlineData(SwiftSelfMutation.Consuming, SwiftPassingOwnership.Consumed)]
    public void ReferenceSelf_IsBorrowedUnlessConsuming(SwiftSelfMutation mutation, SwiftPassingOwnership ownership)
    {
        var self = new SwiftSelfFacts { Kind = SwiftCallSelfKind.Reference, Mutation = mutation, Abstraction = SwiftSelfAbstraction.Concrete, Type = Token };
        var result = SwiftPassingClassifier.SelfConvention(Facts(self), new TableLowering());
        Assert.Equal(SwiftSelfConvention.Reference, result.Convention);
        Assert.Equal(ownership, result.Ownership);
    }

    [Fact]
    public void ReferenceSelf_Mutating_Refuses()
    {
        var self = new SwiftSelfFacts { Kind = SwiftCallSelfKind.Reference, Mutation = SwiftSelfMutation.Mutating, Abstraction = SwiftSelfAbstraction.Concrete, Type = Token };
        var result = SwiftPassingClassifier.SelfConvention(Facts(self), new TableLowering());
        Assert.False(result.IsDecided);
        Assert.Contains("Mutating", result.Refusal);
    }

    [Fact]
    public void FrozenSelf_WithAnUnknownLowering_Refuses()
    {
        var result = SwiftPassingClassifier.SelfConvention(
            Facts(ValueSelf(SmallFrozen, SwiftSelfMutation.Nonmutating)), UnknownSwiftValueLowering.Instance);
        Assert.False(result.IsDecided);
        Assert.Null(result.Convention);
        Assert.Contains("M.Pair", result.Refusal);
    }

    [Fact]
    public void IncompleteFacts_Refuse()
    {
        var facts = Facts(ValueSelf(SmallFrozen, SwiftSelfMutation.Nonmutating), SwiftAccessorKind.None,
            Param(0, Token, SwiftParameterConvention.Guaranteed)) with { Unknowns = ImmutableArray.Create("self: something unread") };

        var self = SwiftPassingClassifier.SelfConvention(facts, Lowering());
        var parameter = SwiftPassingClassifier.ParameterPassing(facts, 0, Lowering());

        Assert.False(self.IsDecided);
        Assert.Contains("something unread", self.Refusal);
        Assert.False(parameter.IsDecided);
        Assert.Contains("something unread", parameter.Refusal);
    }

    // ---- ParameterPassing ----

    [Theory]
    [InlineData(SwiftParameterConvention.Guaranteed, SwiftParameterPassingKind.Direct, SwiftPassingOwnership.Borrowed)]
    [InlineData(SwiftParameterConvention.Owned, SwiftParameterPassingKind.Direct, SwiftPassingOwnership.Consumed)]
    [InlineData(SwiftParameterConvention.Inout, SwiftParameterPassingKind.Inout, SwiftPassingOwnership.BorrowedMutable)]
    public void SmallFrozenArgument_ByConvention(SwiftParameterConvention convention, SwiftParameterPassingKind kind, SwiftPassingOwnership ownership)
    {
        var facts = Facts(SwiftSelfFacts.None, SwiftAccessorKind.None, Param(0, SmallFrozen, convention));
        var result = SwiftPassingClassifier.ParameterPassing(facts, 0, Lowering());
        Assert.Equal(kind, result.Kind);
        Assert.Equal(ownership, result.Ownership);
    }

    [Theory]
    [InlineData(SwiftParameterConvention.Guaranteed, SwiftParameterPassingKind.IndirectBorrowed, SwiftPassingOwnership.Borrowed)]
    [InlineData(SwiftParameterConvention.Owned, SwiftParameterPassingKind.IndirectOwned, SwiftPassingOwnership.Consumed)]
    [InlineData(SwiftParameterConvention.Inout, SwiftParameterPassingKind.Inout, SwiftPassingOwnership.BorrowedMutable)]
    public void WideArgument_ByConvention(SwiftParameterConvention convention, SwiftParameterPassingKind kind, SwiftPassingOwnership ownership)
    {
        var facts = Facts(SwiftSelfFacts.None, SwiftAccessorKind.None, Param(0, WideFrozen, convention));
        var result = SwiftPassingClassifier.ParameterPassing(facts, 0, Lowering());
        Assert.Equal(kind, result.Kind);
        Assert.Equal(ownership, result.Ownership);
    }

    [Fact]
    public void FactsSettledCategories_DoNotAskTheLowering()
    {
        var types = new[]
        {
            (Token, SwiftParameterPassingKind.Direct),
            (new SwiftTypeFacts { Spelling = "τ_0_0", Category = SwiftTypeCategory.GenericParameter }, SwiftParameterPassingKind.IndirectBorrowed),
            (new SwiftTypeFacts { Spelling = "τ_0_0", Category = SwiftTypeCategory.GenericParameter, IsClassBound = true }, SwiftParameterPassingKind.Direct),
            (new SwiftTypeFacts { Spelling = "some M.P", Category = SwiftTypeCategory.Opaque }, SwiftParameterPassingKind.IndirectBorrowed),
            (new SwiftTypeFacts { Spelling = "any M.P", Category = SwiftTypeCategory.Existential }, SwiftParameterPassingKind.IndirectBorrowed),
            (new SwiftTypeFacts { Spelling = "(Swift.Int) -> ()", Category = SwiftTypeCategory.Closure }, SwiftParameterPassingKind.Direct),
            (SwiftTypeFacts.Word("Swift.UnsafeRawPointer"), SwiftParameterPassingKind.Direct),
            (Resilient, SwiftParameterPassingKind.IndirectBorrowed),
            (new SwiftTypeFacts { Spelling = "M.ResilientEnum", Category = SwiftTypeCategory.Enum, IsFrozen = false }, SwiftParameterPassingKind.IndirectBorrowed),
        };
        var lowering = new TableLowering();
        var facts = Facts(SwiftSelfFacts.None, SwiftAccessorKind.None,
            types.Select((t, i) => Param(i, t.Item1, SwiftParameterConvention.Guaranteed)).ToArray());

        var passings = SwiftPassingClassifier.ParameterPassings(facts, lowering);

        Assert.Equal(types.Select(t => (SwiftParameterPassingKind?)t.Item2), passings.Select(p => p.Kind));
        Assert.Empty(lowering.Asked);
    }

    [Theory]
    [InlineData("Swift.AnyObject", SwiftGenericRequirementKind.Conformance)]
    [InlineData("M.Token", SwiftGenericRequirementKind.SameType)]
    [InlineData("Swift.Equatable", SwiftGenericRequirementKind.Conformance)]
    public void ConstrainedGenericArgument_NotRecordedClassBound_Refuses(string target, SwiftGenericRequirementKind kind)
    {
        // `func f<T: AnyObject>(_ value: T)` passes one reference; the facts do not record that a
        // constrained parameter is class-bound, so any requirement leaves it open.
        var generic = new SwiftTypeFacts { Spelling = "τ_0_0", Category = SwiftTypeCategory.GenericParameter };
        var facts = Facts(SwiftSelfFacts.None, SwiftAccessorKind.None, Param(0, generic, SwiftParameterConvention.Guaranteed)) with
        {
            Generics = new SwiftGenericSignatureFacts
            {
                Parameters = ImmutableArray.Create("τ_0_0"),
                Requirements = ImmutableArray.Create(new SwiftGenericRequirementFacts("τ_0_0", target, kind)),
            },
        };

        var result = SwiftPassingClassifier.ParameterPassing(facts, 0, new TableLowering());

        Assert.False(result.IsDecided);
        Assert.Contains("class-bound", result.Refusal);
    }

    [Fact]
    public void UnresolvedOrUnknownArgument_Refuses_WithItsIndex()
    {
        var facts = Facts(SwiftSelfFacts.None, SwiftAccessorKind.None,
            Param(0, SmallFrozen, SwiftParameterConvention.Guaranteed),
            Param(1, new SwiftTypeFacts { Spelling = "M.Mystery", Category = SwiftTypeCategory.Unresolved }, SwiftParameterConvention.Guaranteed),
            Param(2, new SwiftTypeFacts { Spelling = "(Swift.Int, Swift.Int)", Category = SwiftTypeCategory.Tuple }, SwiftParameterConvention.Owned));

        var passings = SwiftPassingClassifier.ParameterPassings(facts, Lowering());

        Assert.True(passings[0].IsDecided);
        Assert.False(passings[1].IsDecided);
        Assert.Contains("parameter 1", passings[1].Refusal);
        Assert.False(passings[2].IsDecided);
        Assert.Contains("parameter 2", passings[2].Refusal);
    }

    // ---- the type-database lowering ----

    private static TypeDatabase Db(params TypeRecord[] records)
    {
        var db = new TypeDatabase();
        var module = new ModuleTypeDatabase("M", "/fake/M.dylib");
        foreach (var record in records)
            module.RegisterType(record.SwiftTypeName, record);
        db.AddModuleDatabase(module);
        return db;
    }

    private static TypeRecord FrozenStruct(string name, string layout, int size) => new()
    {
        CSharpTypeName = CSharpTypeName.FromNamespaceAndName("M", name),
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"M.{name}"),
        MetadataAccessor = $"$s1M{name.Length}{name}V",
        Flags = TypeRecordFlags.Frozen,
        Kind = TypeRecordKind.Struct,
        InlineSize = size,
        AbiFieldLayout = layout,
    };

    [Fact]
    public void TypeDatabaseLowering_SmallIsDirect_WideIsIndirect_DeclinedIsUnknown()
    {
        var db = Db(
            FrozenStruct("Pair", "i8,i8", 16),
            FrozenStruct("Wide", "i8,i8,i8,i8,i8", 40),
            FrozenStruct("Floats", "f4,f4,f4,f4,f8", 24));
        var lowering = new TypeDatabaseSwiftValueLowering(db);

        var pair = lowering.Lower(Struct("M.Pair", frozen: true));
        var wide = lowering.Lower(Struct("M.Wide", frozen: true));
        var floats = lowering.Lower(Struct("M.Floats", frozen: true));
        var missing = lowering.Lower(Struct("M.Missing", frozen: true));
        var tuple = lowering.Lower(new SwiftTypeFacts { Spelling = "(Swift.Int)", Category = SwiftTypeCategory.Tuple });

        Assert.Equal((SwiftLoweringOutcome.Direct, (int?)2), (pair.Outcome, pair.Pieces));
        Assert.Equal(SwiftLoweringOutcome.Indirect, wide.Outcome);
        Assert.Equal(SwiftLoweringOutcome.Unknown, floats.Outcome);
        Assert.Equal(SwiftLoweringOutcome.Unknown, missing.Outcome);
        Assert.Equal(SwiftLoweringOutcome.Unknown, tuple.Outcome);

        var self = SwiftPassingClassifier.SelfConvention(Facts(ValueSelf(Struct("M.Wide", frozen: true), SwiftSelfMutation.Nonmutating)), lowering);
        Assert.Equal(SwiftSelfConvention.IndirectBorrowed, self.Convention);
    }

    [Theory]
    [InlineData("Swift.Int", 1)]
    [InlineData("Swift.Double", 1)]
    [InlineData("Swift.Bool", 1)]
    [InlineData("Swift.UnsafePointer<Swift.Int>", 1)]
    public void TypeDatabaseLowering_ScalarsAndTypedPointers_AreDirectWithoutAFieldLayout(string spelling, int pieces)
    {
        // The standard-library records carry no field layout; the register model knows them by name.
        var lowering = new TypeDatabaseSwiftValueLowering(Db());

        var answer = lowering.Lower(Struct(spelling, frozen: true));

        Assert.Equal((SwiftLoweringOutcome.Direct, (int?)pieces), (answer.Outcome, answer.Pieces));
    }
}
