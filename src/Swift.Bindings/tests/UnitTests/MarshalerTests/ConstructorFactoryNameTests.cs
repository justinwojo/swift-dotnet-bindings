// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Non-failable initializer collision recovery — the constructor lane's counterpart to
/// <see cref="FailableFactoryNameTests"/>.
///
/// <para>Two Swift initializers whose argument labels differ but whose parameter TYPES do not project to
/// one C# constructor signature. They are different operations (an <c>init(paymentIntentClientSecret:)</c>
/// alongside an <c>init(setupIntentClientSecret:)</c> construct different things), so dropping the second
/// deletes half the type's construction surface. The recovery is a label-named static factory, sharing
/// the failable lane's naming core.</para>
///
/// <para>The assertions here are about the POLICY, not one library's spelling: which member (if any) keeps
/// the plain constructor, that the answer does not move when the declaration order does, that a name is a
/// function of the member's own labels rather than its neighbours', and that no name is ever a bare
/// numeric suffix (which the ship gate rejects for the public overload channel).</para>
/// </summary>
public class ConstructorFactoryNameTests
{
    [Fact]
    public void BuildFactoryLabelSuffix_NonFailableInit_ConcatenatesEveryUsableLabel()
    {
        var init = Init(("host", "Swift.String"), ("port", "Swift.Int"));

        var suffix = BaseHandler.BuildFactoryLabelSuffix(init);

        Assert.Equal("HostPort", suffix);
    }

    [Fact]
    public void BuildFactoryLabelSuffix_PositionalInit_IsEmpty()
    {
        // No label to name a factory by — the signal the ownership rule keys off.
        var init = Init(("_", "Swift.String"), ("arg1", "Swift.Int"));

        Assert.Equal(string.Empty, BaseHandler.BuildFactoryLabelSuffix(init));
    }

    [Fact]
    public void ResolveConstructorFactories_AllLabeledFamily_EveryMemberBecomesAFactory()
    {
        // Nobody is fully positional, so nobody has a claim on the plain constructor: both recover as
        // factories rather than one silently winning it.
        var paymentIntent = Init(("paymentIntentClientSecret", "Swift.String"), ("configuration", "TestModule.Configuration"));
        var setupIntent = Init(("setupIntentClientSecret", "Swift.String"), ("configuration", "TestModule.Configuration"));

        var map = Resolve(paymentIntent, setupIntent);

        Assert.Equal(2, map.Count);
        Assert.Contains("PaymentIntentClientSecret", map[paymentIntent].FactoryName);
        Assert.Contains("SetupIntentClientSecret", map[setupIntent].FactoryName);
        AssertNoNumericSuffix(map);
    }

    [Fact]
    public void ResolveConstructorFactories_NameIsIndependentOfSiblings()
    {
        // Both members carry the SHARED `configuration` label too. A family-relative trim would drop it,
        // which means inserting or removing a sibling upstream would rename a shipped factory; the name
        // is deliberately the member's whole own label set.
        var a = Init(("paymentIntentClientSecret", "Swift.String"), ("configuration", "TestModule.Configuration"));
        var b = Init(("setupIntentClientSecret", "Swift.String"), ("configuration", "TestModule.Configuration"));

        var pair = Resolve(a, b);

        var aAlone = Init(("paymentIntentClientSecret", "Swift.String"), ("configuration", "TestModule.Configuration"));
        var c = Init(("customerSessionClientSecret", "Swift.String"), ("configuration", "TestModule.Configuration"));
        var trio = Resolve(aAlone, b, c);

        Assert.Equal(pair[a].FactoryName, trio[aAlone].FactoryName);
    }

    [Fact]
    public void ResolveConstructorFactories_ThreeWayCollision_AllThreeRecoverDistinctly()
    {
        var a = Init(("id", "Swift.String"), ("ephemeralKeySecret", "Swift.String"));
        var b = Init(("id", "Swift.String"), ("customerSessionClientSecret", "Swift.String"));
        var c = Init(("id", "Swift.String"), ("legacyToken", "Swift.String"));

        var map = Resolve(a, b, c);

        Assert.Equal(3, map.Count);
        var names = new[] { map[a].FactoryName, map[b].FactoryName, map[c].FactoryName };
        Assert.Equal(3, names.Distinct().Count());
        AssertNoNumericSuffix(map);
    }

    [Fact]
    public void ResolveConstructorFactories_SinglePositionalMember_KeepsThePlainConstructor()
    {
        // The positional init has no label a factory could be named from, so it is the one member that
        // MUST keep the constructor. Its labeled sibling recovers.
        var positional = Init(("_", "Swift.String"), ("_", "Swift.Int"));
        var labeled = Init(("host", "Swift.String"), ("port", "Swift.Int"));

        var map = Resolve(positional, labeled);

        Assert.False(map.ContainsKey(positional));
        Assert.True(map.ContainsKey(labeled));
    }

    [Fact]
    public void ResolveConstructorFactories_OwnershipDoesNotFollowDeclarationOrder()
    {
        // The audit's core requirement: a re-ordered .swiftinterface must not swap which init the plain
        // `new T(...)` call reaches. Ownership is content-based, so reversing the walk changes nothing.
        var positional = Init(("_", "Swift.String"), ("_", "Swift.Int"));
        var labeled = Init(("host", "Swift.String"), ("port", "Swift.Int"));

        var forward = Resolve(positional, labeled);
        var reversed = Resolve(labeled, positional);

        Assert.False(reversed.ContainsKey(positional));
        Assert.Equal(forward[labeled].FactoryName, reversed[labeled].FactoryName);
    }

    [Fact]
    public void ResolveConstructorFactories_TwoPositionalMembers_LeavesTheFamilyAlone()
    {
        // Neither has a label, so neither can be named apart and both would want the constructor.
        // Recovering one arbitrarily would be the order-dependence this policy exists to avoid, so the
        // family stays on the pre-existing first-claimant-wins path instead.
        var a = Init(("_", "Swift.String"), ("_", "Swift.Int"));
        var b = Init(("_", "Swift.String"), ("_", "Swift.Int"));

        Assert.Empty(Resolve(a, b));
    }

    [Fact]
    public void ResolveConstructorFactories_UncontestedInit_IsNotRenamed()
    {
        var only = Init(("host", "Swift.String"));

        Assert.Empty(Resolve(only));
    }

    [Fact]
    public void ResolveConstructorFactories_LabelNameTakenByASiblingMethod_EscalatesToTheTypeRung()
    {
        // A recovered factory is an ordinary static method: same name shape, same parameter list, and no
        // trailing `out` to keep it apart from a real member. So a label name a sibling method already
        // occupies has to escalate, or the recovery emits CS0111.
        // Two distinct Swift types that erase to one C# parameter type — the shape that makes the
        // projected keys collide while leaving the type rung something to discriminate on.
        var a = Init(("host", "Swift.String"));
        var b = Init(("path", "Foundation.URL"));
        var taken = new HashSet<string>(StringComparer.Ordinal) { "CreateWithHost(string)" };

        var map = OverloadNameDisambiguator.ResolveConstructorFactories(
            new[] { (a, "ctor(string)"), (b, "ctor(string)") },
            taken);

        Assert.Equal(2, map.Count);
        Assert.DoesNotContain("CreateWithHost", map.Values.Select(v => v.FactoryName));
        Assert.All(map.Values, v => Assert.Equal(OverloadNameOutcome.TypeDerived, v.Outcome));
        AssertNoNumericSuffix(map);
    }

    private static Dictionary<MethodDecl, ConstructorFactoryAssignment> Resolve(params MethodDecl[] inits)
        => OverloadNameDisambiguator.ResolveConstructorFactories(
            inits.Select(i => (i, ProjectedKey(i))).ToList(),
            new HashSet<string>(StringComparer.Ordinal));

    /// <summary>
    /// Stands in for the emitter's projected key: the label-erased C# shape. Every fixture below uses
    /// distinct labels over IDENTICAL projected parameter types, which is exactly the collision under test.
    /// </summary>
    private static string ProjectedKey(MethodDecl init)
        => "ctor(" + string.Join(",", init.CSSignature.Skip(1).Select(a => ((NamedTypeSpec)a.SwiftTypeSpec!).NameWithoutModule)) + ")";

    private static void AssertNoNumericSuffix(Dictionary<MethodDecl, ConstructorFactoryAssignment> map)
    {
        foreach (var assignment in map.Values)
        {
            var name = assignment.FactoryName;
            Assert.False(string.IsNullOrEmpty(name));
            Assert.True(char.IsLetter(name[0]) || name[0] == '_');
            Assert.All(name, c => Assert.True(char.IsLetterOrDigit(c) || c == '_'));
            Assert.False(char.IsDigit(name[^1]), $"'{name}' ends in a digit — the ship gate rejects numeric overload names.");
        }
    }

    private static MethodDecl Init(params (string label, string type)[] labels)
    {
        var sig = new List<ArgumentDecl> { InitArg(string.Empty, "()") }; // CSSignature[0] = return
        foreach (var (label, type) in labels)
            sig.Add(InitArg(label, type));
        return new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule4initX",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            IsFailable = false,
            CSSignature = sig,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
        };
    }

    private static ArgumentDecl InitArg(string label, string type) => new ArgumentDecl
    {
        Name = label,
        PrivateName = label,
        SwiftTypeSpec = new NamedTypeSpec(type),
        IsInOut = false,
        IsGeneric = false,
        ParentDecl = null,
        ModuleDecl = null,
    };
}
