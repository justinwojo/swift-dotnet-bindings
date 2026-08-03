// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The overload-collision resolver assigns names from each member's own Swift signature, replacing the
/// declaration-rank numeric suffixes (<c>Process</c> / <c>Process2</c>) the generator used to emit.
/// These tests pin the properties that made that change worth making — above all that no assignment
/// depends on the order the emission walk visits the family, so an upstream library inserting an
/// overload cannot rename a binding a consumer already compiled against.
///
/// <para><see cref="OverloadNameDisambiguator.Resolve"/> is exercised directly: the caller supplies the
/// projected-key builder and the natural-name function, so a test can model a family's projected shape
/// without standing up a TypeDatabase. The stubs below mirror the real key shape
/// (<c>Name(param,types)</c>) and the real natural-name pass (PascalCase of the Swift name).</para>
/// </summary>
public class OverloadNameDisambiguatorTests
{
    // ===================================================================
    //  Stubs modelling the two functions the production callers pass in
    // ===================================================================

    /// <summary>Projected params per method, keyed by reference — the family's shared C# shape.</summary>
    private sealed class Scope
    {
        private readonly Dictionary<MethodDecl, string> _params = new(ReferenceEqualityComparer.Instance);

        public MethodDecl Add(string swiftName, string projectedParams, params (string label, string type)[] args)
        {
            var m = TestDecls.Method(
                swiftName,
                parameters: args.Select(a => TestDecls.Param(a.label, new NamedTypeSpec(a.type))));
            _params[m] = projectedParams;
            return m;
        }

        public string Key(MethodDecl m) => $"{Pascal(m.Name)}({_params[m]})";

        public string KeyWithOverride(MethodDecl m, string nameInput) => $"{Pascal(nameInput)}({_params[m]})";

        public string Natural(MethodDecl m) => Pascal(m.Name);

        public Dictionary<MethodDecl, OverloadNameAssignment> Resolve(params MethodDecl[] declarationOrder)
            => OverloadNameDisambiguator.Resolve(
                declarationOrder.Select(m => (m, Key(m))).ToList(),
                KeyWithOverride,
                Natural);

        private static string Pascal(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }

    private static string? NameInputOf(IReadOnlyDictionary<MethodDecl, OverloadNameAssignment> map, MethodDecl m)
        => map.TryGetValue(m, out var a) ? a.NameInput : null;

    private static OverloadNameOutcome OutcomeOf(IReadOnlyDictionary<MethodDecl, OverloadNameAssignment> map, MethodDecl m)
        => map.TryGetValue(m, out var a) ? a.Outcome : OverloadNameOutcome.Natural;

    // ===================================================================
    //  Order independence — the property the numeric scheme could not hold
    // ===================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_LabelledFamily_AssignmentsAreIndependentOfDeclarationOrder(bool reversed)
    {
        var scope = new Scope();
        var first = scope.Add("process", "int", ("first", "Swift.Int"));
        var second = scope.Add("process", "int", ("second", "Swift.Int"));

        var map = reversed ? scope.Resolve(second, first) : scope.Resolve(first, second);

        // Each name is a function of that member's own labels, so the same two members produce the same
        // two names whichever one the walk sees first. Under the old rank scheme this pair swapped
        // between `Process`/`Process2` on reversal.
        Assert.Equal("processFirst", NameInputOf(map, first));
        Assert.Equal("processSecond", NameInputOf(map, second));
    }

    [Fact]
    public void Resolve_ThreeWayFamily_NamesFollowLabelsNotPosition()
    {
        var scope = new Scope();
        var alpha = scope.Add("render", "int", ("alpha", "Swift.Int"));
        var beta = scope.Add("render", "int", ("beta", "Swift.Int"));
        var gamma = scope.Add("render", "int", ("gamma", "Swift.Int"));

        var declared = scope.Resolve(gamma, alpha, beta);
        var shuffled = scope.Resolve(beta, gamma, alpha);

        foreach (var m in new[] { alpha, beta, gamma })
            Assert.Equal(NameInputOf(declared, m), NameInputOf(shuffled, m));

        Assert.Equal("renderAlpha", NameInputOf(declared, alpha));
        Assert.Equal("renderBeta", NameInputOf(declared, beta));
        Assert.Equal("renderGamma", NameInputOf(declared, gamma));
    }

    // ===================================================================
    //  Bare-name ownership is a content fact
    // ===================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_SingleLabellessMember_OwnsTheBareNameFromEitherPosition(bool labelledFirst)
    {
        var scope = new Scope();
        var bare = scope.Add("configure", "int", ("_", "Swift.Int"));
        var labelled = scope.Add("configure", "int", ("timeout", "Swift.Int"));

        var map = labelledFirst ? scope.Resolve(labelled, bare) : scope.Resolve(bare, labelled);

        // The label-less member has nothing to be discriminated BY, so it is the one that keeps
        // `Configure` — regardless of which member the emission walk reaches first.
        Assert.Equal(OverloadNameOutcome.Natural, OutcomeOf(map, bare));
        Assert.Null(NameInputOf(map, bare));
        Assert.Equal("configureTimeout", NameInputOf(map, labelled));
    }

    [Fact]
    public void Resolve_AllMembersLabelled_NobodyKeepsTheBareName()
    {
        var scope = new Scope();
        var zebra = scope.Add("configure", "int", ("zebra", "Swift.Int"));
        var alpha = scope.Add("configure", "int", ("alpha", "Swift.Int"));

        var map = scope.Resolve(zebra, alpha);

        // Handing the bare name to one of them would be an arbitrary choice that a later upstream
        // insertion could revisit — exactly the instability this scheme exists to remove.
        Assert.Equal("configureZebra", NameInputOf(map, zebra));
        Assert.Equal("configureAlpha", NameInputOf(map, alpha));
    }

    [Fact]
    public void Resolve_TwoLabellessMembers_NeitherOwnsTheBareName()
    {
        var scope = new Scope();
        // Both positional, differing only in Swift type — the projected key erases the difference
        // (both are class-like, so C# nullability collapses them), so they collide.
        var plain = scope.Add("transform", "RefBox", ("_", "TestModule.RefBox"));
        var optional = scope.Add("transform", "RefBox", ("_", "Swift.Optional"));

        var map = scope.Resolve(plain, optional);

        // Two equally-valid claimants means no claimant: both fall to the type rung.
        Assert.Equal(OverloadNameOutcome.TypeDerived, OutcomeOf(map, plain));
        Assert.Equal(OverloadNameOutcome.TypeDerived, OutcomeOf(map, optional));
        Assert.Equal("transformWithRefBox", NameInputOf(map, plain));
        Assert.Equal("transformWithOptional", NameInputOf(map, optional));
    }

    [Fact]
    public void Resolve_MemberRenamedOntoTheContestedName_DoesNotOutclaimTheRealOwner()
    {
        var scope = new Scope();
        // `conflict(_:)` reaches the contested spelling only because a sibling property forced
        // `Conflict` → `ConflictMethod`; the member literally named `conflictMethod` is the one whose
        // own natural name IS the contested spelling.
        var renamedOnto = scope.Add("conflict", "int", ("_", "Swift.Int"));
        var trueOwner = scope.Add("conflictMethod", "int", ("_", "Swift.Int"));

        var map = OverloadNameDisambiguator.Resolve(
            new List<(MethodDecl, string)>
            {
                (renamedOnto, "ConflictMethod(int)"),
                (trueOwner, "ConflictMethod(int)"),
            },
            // The key builder carries the property-collision rename, so BOTH `conflict` and
            // `conflictMethod` project onto `ConflictMethod(int)` — modelling that is the point of this
            // case, since it is what makes the two members collide in the first place.
            (m, nameInput) => nameInput is "conflict" or "conflictMethod"
                ? "ConflictMethod(int)"
                : $"{char.ToUpperInvariant(nameInput[0])}{nameInput.Substring(1)}(int)",
            // The rename is suppressed here by construction: this function reports the name the member
            // would carry with no collision-avoidance applied.
            m => m == renamedOnto ? "Conflict" : "ConflictMethod");

        Assert.Equal(OverloadNameOutcome.Natural, OutcomeOf(map, trueOwner));
        Assert.Equal(OverloadNameOutcome.TypeDerived, OutcomeOf(map, renamedOnto));
    }

    // ===================================================================
    //  Rung selection
    // ===================================================================

    [Fact]
    public void Resolve_IdenticalLabelsDifferentTypes_FallsToTheTypeRung()
    {
        var scope = new Scope();
        var byInt = scope.Add("lookup", "object", ("key", "Swift.Int"));
        var byString = scope.Add("lookup", "object", ("key", "Swift.String"));

        var map = scope.Resolve(byInt, byString);

        // The label rung produces `lookupKey` for both, so it cannot be the answer for either — the
        // whole family drops to the next rung together rather than one member taking `lookupKey`.
        Assert.Equal(OverloadNameOutcome.TypeDerived, OutcomeOf(map, byInt));
        Assert.Equal(OverloadNameOutcome.TypeDerived, OutcomeOf(map, byString));
        Assert.Equal("lookupKeyWithInt", NameInputOf(map, byInt));
        Assert.Equal("lookupKeyWithString", NameInputOf(map, byString));
    }

    [Fact]
    public void Resolve_LabelNameAlreadyTakenByAnUncontestedSibling_FallsToTheTypeRung()
    {
        var scope = new Scope();
        var withMode = scope.Add("configure", "int", ("mode", "Swift.Int"));
        var withOther = scope.Add("configure", "int", ("other", "Swift.Int"));
        // A real, uncontested sibling already emits as `ConfigureMode(int)`.
        var existing = scope.Add("configureMode", "int", ("_", "Swift.Int"));

        var map = scope.Resolve(withMode, withOther, existing);

        Assert.Equal(OverloadNameOutcome.Natural, OutcomeOf(map, existing));
        Assert.Equal(OverloadNameOutcome.TypeDerived, OutcomeOf(map, withMode));
        Assert.Equal(OverloadNameOutcome.TypeDerived, OutcomeOf(map, withOther));
    }

    [Fact]
    public void Resolve_UncontestedMember_IsAbsentFromTheMap()
    {
        var scope = new Scope();
        var lone = scope.Add("configure", "");
        var a = scope.Add("process", "int", ("first", "Swift.Int"));
        var b = scope.Add("process", "int", ("second", "Swift.Int"));

        var map = scope.Resolve(lone, a, b);

        Assert.False(map.ContainsKey(lone));
        Assert.True(map.ContainsKey(a));
        Assert.True(map.ContainsKey(b));
    }

    // ===================================================================
    //  Refusal — the numeric fallback's replacement
    // ===================================================================

    [Fact]
    public void Resolve_IndistinguishableMembers_RefusesTheDuplicateInsteadOfNumbering()
    {
        var scope = new Scope();
        // Same labels, same Swift types, same projected key: the pair differs only by return type, which
        // is not part of C# overload identity. No token can name them apart.
        var first = scope.Add("value", "", ("_", "Swift.Int"));
        var second = scope.Add("value", "", ("_", "Swift.Int"));

        var map = scope.Resolve(first, second);

        var refused = new[] { first, second }.Where(m => OutcomeOf(map, m) == OverloadNameOutcome.Refused).ToList();
        Assert.Single(refused);
        Assert.All(map.Values, a => Assert.DoesNotContain("2", a.NameInput ?? ""));

        var detail = map[refused[0]].Detail;
        Assert.NotNull(detail);
        // The report has to name BOTH signatures or a consumer cannot tell which declaration vanished.
        Assert.Contains("value(", detail);
    }

    [Fact]
    public void Resolve_ZeroParameterFamily_RefusesRatherThanNumbering()
    {
        var scope = new Scope();
        var a = scope.Add("reset", "");
        var b = scope.Add("reset", "");

        var map = scope.Resolve(a, b);

        // Nothing to build a label or type token from at all.
        Assert.Contains(map.Values, v => v.Outcome == OverloadNameOutcome.Refused);
    }

    // ===================================================================
    //  Name-input construction
    // ===================================================================

    [Fact]
    public void BuildLabelDerivedNameInput_AppendsEachExternalLabelSelectorStyle()
    {
        var m = TestDecls.Method("conversationManager", parameters: new[]
        {
            TestDecls.Param("_", new NamedTypeSpec("TestModule.Manager")),
            TestDecls.Param("didActivate", new NamedTypeSpec("Swift.Bool")),
        });

        Assert.Equal("conversationManagerDidActivate", OverloadNameDisambiguator.BuildLabelDerivedNameInput(m));
    }

    [Fact]
    public void BuildLabelDerivedNameInput_NoUsableLabels_ReturnsTheBareName()
    {
        var m = TestDecls.Method("configure", parameters: new[]
        {
            TestDecls.Param("_", new NamedTypeSpec("Swift.Int")),
        });

        // Equality with the bare name is how the resolver detects "the label rung added nothing".
        Assert.Equal("configure", OverloadNameDisambiguator.BuildLabelDerivedNameInput(m));
    }

    [Fact]
    public void BuildTypeDerivedNameInput_UsesWithAndAndBetweenParameters()
    {
        var m = TestDecls.Method("merge", parameters: new[]
        {
            TestDecls.Param("_", new NamedTypeSpec("Swift.Int")),
            TestDecls.Param("_", new NamedTypeSpec("Swift.String")),
        });

        Assert.Equal("mergeWithIntAndString", OverloadNameDisambiguator.BuildTypeDerivedNameInput(m, "merge"));
    }

    [Theory]
    [InlineData("Swift.Int", "Int")]
    [InlineData("TestModule.RefBox", "RefBox")]
    [InlineData("Swift.String", "String")]
    public void BuildSwiftTypeToken_DropsModuleQualification(string swiftType, string expected)
        => Assert.Equal(expected, OverloadNameDisambiguator.BuildSwiftTypeToken(new NamedTypeSpec(swiftType)));

    [Fact]
    public void BuildSwiftTypeToken_GenericType_FlattensItsArguments()
    {
        var spec = new NamedTypeSpec("Swift.Array");
        spec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        Assert.Equal("ArrayString", OverloadNameDisambiguator.BuildSwiftTypeToken(spec));
    }

    [Fact]
    public void BuildSwiftTypeToken_IsAlwaysAUsableIdentifierFragment()
    {
        foreach (var token in new[]
        {
            OverloadNameDisambiguator.BuildSwiftTypeToken(new NamedTypeSpec("Swift.Int")),
            OverloadNameDisambiguator.BuildSwiftTypeToken(TupleTypeSpec.Empty),
        })
        {
            Assert.NotEmpty(token);
            Assert.All(token, c => Assert.True(char.IsLetterOrDigit(c) || c == '_', $"'{c}' in '{token}'"));
        }
    }
}
