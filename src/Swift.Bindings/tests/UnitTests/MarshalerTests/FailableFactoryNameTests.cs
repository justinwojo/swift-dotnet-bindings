// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// FB-1b — failable-init overload-collapse factory naming.
///
/// <para>Two <c>init?</c> overloads whose parameter labels differ (messengerPageId vs nonce) but erase
/// to the same projected C# <c>TryCreate(IEnumerable&lt;string&gt;, LoginTracking, string, out …)</c>
/// signature. Before FB-1b the second was dropped as DuplicateSignature; now the first-declared keeps
/// the plain <c>TryCreate</c> and the colliding sibling recovers under a label-disambiguated
/// static-factory name.</para>
///
/// <para>This is the constructor lane, which is deliberately NOT the method-overload lane: a
/// constructor's C# name is fixed, so the recovered member is a static factory rather than a renamed
/// method, and its numeric last resort stays (an internal recovery name, not a public overload name —
/// see <see cref="OverloadNameDisambiguatorTests"/> for the method lane's no-numeric-suffix rule).</para>
///
/// <para>Assertions are behavioral, not exact strings: the distinguishing label appears in the
/// sibling's name, the SHARED labels do not, and the recovered name never collapses onto the winner's
/// plain <c>TryCreate</c>.</para>
/// </summary>
public class FailableFactoryNameTests
{
    [Fact]
    public void BuildFailableFactoryName_CollidingSibling_SuffixesOnlyTheDistinguishingLabel()
    {
        var winner = FailableInit(("permissions", "Swift.Array"), ("tracking", "TestModule.LoginTracking"), ("nonce", "Swift.String"));
        var sibling = FailableInit(("permissions", "Swift.Array"), ("tracking", "TestModule.LoginTracking"), ("messengerPageId", "Swift.String"));

        var name = BaseHandler.BuildFailableFactoryName(sibling, winner, "ctor(...)", new Dictionary<string, int>());

        Assert.NotEqual("TryCreate", name);
        Assert.Contains("MessengerPageId", name);   // the label that distinguishes this overload
        Assert.DoesNotContain("Permissions", name);  // shared with the winner → not a distinguisher
        Assert.DoesNotContain("Tracking", name);
        AssertValidIdentifier(name);
    }

    [Fact]
    public void BuildFailableFactoryName_NoWinner_AllLabelsDistinguish()
    {
        // When the plain slot was claimed by a non-failable constructor (winner == null), every usable
        // label distinguishes the recovered factory.
        var sibling = FailableInit(("host", "Swift.String"), ("port", "Swift.Int"));

        var name = BaseHandler.BuildFailableFactoryName(sibling, winner: null, "ctor(...)", new Dictionary<string, int>());

        Assert.NotEqual("TryCreate", name);
        Assert.Contains("Host", name);
        Assert.Contains("Port", name);
        AssertValidIdentifier(name);
    }

    [Fact]
    public void BuildFailableFactoryName_NoUsableLabels_FallsBackToUniqueNumericName()
    {
        // Pathological all-synthesized-label case: no distinguishing label to suffix, so a numeric
        // fallback keeps the name unique and off the winner's plain `TryCreate`.
        var sibling = FailableInit(("arg0", "Swift.String"), ("arg1", "Swift.Int"));

        var name = BaseHandler.BuildFailableFactoryName(sibling, winner: null, "ctor(...)", new Dictionary<string, int>());

        Assert.NotEqual("TryCreate", name);
        Assert.StartsWith("TryCreate", name);
        AssertValidIdentifier(name);
    }

    private static void AssertValidIdentifier(string name)
    {
        Assert.False(string.IsNullOrEmpty(name));
        Assert.True(char.IsLetter(name[0]) || name[0] == '_');
        Assert.All(name, c => Assert.True(char.IsLetterOrDigit(c) || c == '_'));
    }

    private static MethodDecl FailableInit(params (string label, string type)[] labels)
    {
        var sig = new List<ArgumentDecl> { InitArg(string.Empty, "Swift.Optional") }; // CSSignature[0] = return
        foreach (var (label, type) in labels)
            sig.Add(InitArg(label, type));
        return new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule4initX",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            IsFailable = true,
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
