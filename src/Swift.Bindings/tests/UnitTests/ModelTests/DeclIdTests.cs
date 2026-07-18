// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Immutable;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Behavior of <see cref="DeclId"/> and <see cref="DeclIdFactory"/>: the same declaration must
/// always produce the same id, distinguishable declarations must never share one, and the
/// canonical string must survive a write/read round-trip unchanged.
/// </summary>
public class DeclIdTests
{
    // ──────────────────────────── Stability ────────────────────────────

    [Fact]
    public void SameDeclaration_ComputedTwice_ProducesIdenticalId()
    {
        var module = TestModelFactory.CreateModuleDecl();
        var method = TestModelFactory.CreateMethod(
            "fetch", module, new[] { ("from", "Swift.String") });

        var first = DeclIdFactory.ForMethod(method);
        var second = DeclIdFactory.ForMethod(method);

        Assert.Equal(first, second);
        Assert.Equal(first.Canonical, second.Canonical);
        Assert.Equal(first.ShortHash, second.ShortHash);
    }

    [Fact]
    public void StructurallyIdenticalDeclarations_ProduceEqualIds()
    {
        // Two separately-constructed decls describing the same Swift declaration — the shape a
        // second parse of the same ABI produces. Ids must agree across those object identities.
        var module = TestModelFactory.CreateModuleDecl();
        var left = TestModelFactory.CreateMethod("fetch", module, new[] { ("from", "Swift.String") });
        var right = TestModelFactory.CreateMethod("fetch", module, new[] { ("from", "Swift.String") });

        Assert.Equal(DeclIdFactory.ForMethod(left), DeclIdFactory.ForMethod(right));
        Assert.Equal(
            DeclIdFactory.ForMethod(left).GetHashCode(),
            DeclIdFactory.ForMethod(right).GetHashCode());
    }

    [Fact]
    public void EntireModule_WalkedTwice_ProducesIdenticalIdSequence()
    {
        // The doc's stability requirement at module scope: running the id pass over a whole
        // declaration tree twice must give the same ids in the same order.
        var module = TestModelFactory.CreateModuleDecl();

        var first = WalkIds(module);
        var second = WalkIds(module);

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void ShortHash_IsEightUppercaseHexCharacters()
    {
        var module = TestModelFactory.CreateModuleDecl();
        var hash = DeclIdFactory.ForMethod(TestModelFactory.CreateMethod("fetch", module)).ShortHash;

        Assert.Equal(8, hash.Length);
        Assert.All(hash, c => Assert.True(char.IsAsciiDigit(c) || (c >= 'A' && c <= 'F'), $"'{c}' is not uppercase hex."));
    }

    // ──────────────────────────── Discrimination ────────────────────────────

    [Fact]
    public void Overloads_DifferingOnlyByParameterLabel_GetDistinctIds()
    {
        var module = TestModelFactory.CreateModuleDecl();
        var byName = TestModelFactory.CreateMethod("fetch", module, new[] { ("name", "Swift.String") });
        var byPath = TestModelFactory.CreateMethod("fetch", module, new[] { ("path", "Swift.String") });

        Assert.NotEqual(DeclIdFactory.ForMethod(byName), DeclIdFactory.ForMethod(byPath));
    }

    [Fact]
    public void Overloads_DifferingOnlyByParameterType_GetDistinctIds()
    {
        var module = TestModelFactory.CreateModuleDecl();
        var takesString = TestModelFactory.CreateMethod("fetch", module, new[] { ("from", "Swift.String") });
        var takesInt = TestModelFactory.CreateMethod("fetch", module, new[] { ("from", "Swift.Int") });

        Assert.NotEqual(DeclIdFactory.ForMethod(takesString), DeclIdFactory.ForMethod(takesInt));
    }

    [Fact]
    public void PropertyGetter_AndSetter_GetDistinctIds()
    {
        var module = TestModelFactory.CreateModuleDecl();
        var property = TestModelFactory.CreateProperty("state", module);

        var getter = DeclIdFactory.ForProperty(property, AccessorKind.Getter);
        var setter = DeclIdFactory.ForProperty(property, AccessorKind.Setter);
        var whole = DeclIdFactory.ForProperty(property);

        Assert.NotEqual(getter, setter);
        Assert.NotEqual(getter, whole);
        Assert.NotEqual(setter, whole);
    }

    [Fact]
    public void StaticAndInstanceProperty_OfTheSameName_GetDistinctIds()
    {
        // Swift permits `var count` and `static var count` on one type. A property id carries no
        // mangled symbol (the symbol lives on the accessors), so without an explicit static/instance
        // discriminator these two agree on every other component and collapse into one id —
        // silently merging two different declarations in any consumer keyed on it.
        var module = TestModelFactory.CreateModuleDecl();
        var instance = TestModelFactory.CreateProperty("count", module);
        var @static = TestModelFactory.CreateProperty("count", module, isStatic: true);

        Assert.NotEqual(DeclIdFactory.ForProperty(instance), DeclIdFactory.ForProperty(@static));
    }

    [Fact]
    public void SameMemberName_InDifferentModules_GetsDistinctIds()
    {
        var left = TestModelFactory.CreateModuleDecl("AlphaModule");
        var right = TestModelFactory.CreateModuleDecl("BetaModule");

        var leftId = DeclIdFactory.ForMethod(TestModelFactory.CreateMethod("fetch", left));
        var rightId = DeclIdFactory.ForMethod(TestModelFactory.CreateMethod("fetch", right));

        Assert.NotEqual(leftId, rightId);
    }

    [Fact]
    public void SameMemberName_OnDifferentTypes_GetsDistinctIds()
    {
        var module = TestModelFactory.CreateModuleDecl();
        var loader = module.Types.First(t => t.Name == "Loader");
        var payload = loader.Types.First(t => t.Name == "Payload");

        var onLoader = DeclIdFactory.ForMethod(TestModelFactory.CreateMethod("read", loader), loader);
        var onPayload = DeclIdFactory.ForMethod(TestModelFactory.CreateMethod("read", payload), payload);

        Assert.NotEqual(onLoader, onPayload);
    }

    [Fact]
    public void GenericAndNonGenericMethod_OfTheSameShape_GetDistinctIds()
    {
        var module = TestModelFactory.CreateModuleDecl();
        var concrete = TestModelFactory.CreateMethod("map", module, mangledName: "$s4Test3mapyyF");
        var generic = TestModelFactory.CreateMethod("map", module, mangledName: "$s4Test3mapyyF");
        generic.RawGenericSig = "<τ_0_0 where τ_0_0 : Swift.Equatable>";

        Assert.NotEqual(DeclIdFactory.ForMethod(concrete), DeclIdFactory.ForMethod(generic));
    }

    [Fact]
    public void GenericSignature_DifferingOnlyInWhitespace_ProducesTheSameId()
    {
        // Whitespace is not an ABI fact; two spellings of one signature must not fork the id.
        var module = TestModelFactory.CreateModuleDecl();
        var tight = TestModelFactory.CreateMethod("map", module);
        var loose = TestModelFactory.CreateMethod("map", module);
        tight.RawGenericSig = "<τ_0_0 where τ_0_0 : Swift.Equatable>";
        loose.RawGenericSig = "  <τ_0_0   where τ_0_0 :  Swift.Equatable>  ";

        Assert.Equal(DeclIdFactory.ForMethod(tight), DeclIdFactory.ForMethod(loose));
    }

    [Fact]
    public void MethodAndProperty_SharingAName_GetDistinctIds()
    {
        var module = TestModelFactory.CreateModuleDecl();

        var asMethod = DeclIdFactory.ForMethod(TestModelFactory.CreateMethod("value", module));
        var asProperty = DeclIdFactory.ForProperty(TestModelFactory.CreateProperty("value", module));

        Assert.NotEqual(asMethod, asProperty);
    }

    [Fact]
    public void Subscripts_DifferingOnlyByIndexType_GetDistinctIds()
    {
        var module = TestModelFactory.CreateModuleDecl();
        var byInt = TestModelFactory.CreateSubscript(module, new[] { ("index", "Swift.Int") });
        var byString = TestModelFactory.CreateSubscript(module, new[] { ("index", "Swift.String") });

        Assert.NotEqual(DeclIdFactory.ForSubscript(byInt), DeclIdFactory.ForSubscript(byString));
    }

    // ──────────────────────────── Serialization ────────────────────────────

    [Fact]
    public void Canonical_RoundTripsThroughParse()
    {
        var module = TestModelFactory.CreateModuleDecl();
        var method = TestModelFactory.CreateMethod(
            "fetch", module, new[] { ("from", "Swift.String"), ("limit", "Swift.Int") });
        var id = DeclIdFactory.ForMethod(method);

        var parsed = DeclId.Parse(id.Canonical);

        Assert.Equal(id, parsed);
        Assert.Equal(id.Canonical, parsed.Canonical);
    }

    [Theory]
    // Every structural character of the canonical form, embedded in real component positions.
    [InlineData("Mod|ule", "Ty:pe", "na,me", "lab|el", "(A, B) -> C", "sym\\bol")]
    [InlineData("", "", "", "", "", "")]
    [InlineData("M", "T", "n", "", "Swift.Dictionary<Swift.String, Swift.Int>", "")]
    public void Canonical_RoundTripsThroughParse_ForComponentsContainingDelimiters(
        string module, string declPath, string name, string label, string type, string symbol)
    {
        var id = DeclId.Create(
            module,
            declPath,
            BindingItemKind.Method,
            name,
            ImmutableArray.Create(label),
            ImmutableArray.Create(type),
            AccessorKind.Getter,
            "<τ_0_0 where τ_0_0 : P>",
            symbol,
            "static");

        var parsed = DeclId.Parse(id.Canonical);

        Assert.Equal(id, parsed);
        Assert.Equal(id.Canonical, parsed.Canonical);
        Assert.Equal(label, parsed.ParameterLabels.Single());
        Assert.Equal(type, parsed.ParameterTypes.Single());
    }

    [Fact]
    public void Parse_PreservesTheDistinctionBetweenNoParametersAndOneEmptyParameter()
    {
        var none = DeclId.Create("M", "T", BindingItemKind.Method, "f");
        var oneEmpty = DeclId.Create(
            "M", "T", BindingItemKind.Method, "f",
            ImmutableArray.Create(""), ImmutableArray.Create(""));

        Assert.NotEqual(none.Canonical, oneEmpty.Canonical);
        Assert.Empty(DeclId.Parse(none.Canonical).ParameterLabels);
        Assert.Single(DeclId.Parse(oneEmpty.Canonical).ParameterLabels);
    }

    [Theory]
    [InlineData("")]
    [InlineData("too|few|fields")]
    [InlineData("M|T|Method|f||None||")]                    // 8 fields — the pre-Discriminator shape
    [InlineData("M|T|1|f||None|||")]                        // numeric kind
    [InlineData("M|T|NotAKind|f||None|||")]                 // undefined kind
    [InlineData("M|T|Method|f||99|||")]                     // numeric accessor
    [InlineData("M|T|Method|f|onlyalabel|None|||")]         // parameter entry with no type half
    [InlineData(@"M|T|Method|f||None|||foo\q")]             // escape before a non-structural char
    [InlineData(@"M|T|Method|f||None|||foo\")]              // trailing lone escape prefix
    [InlineData(@"M\q|T|Method|f||None|||")]                // same, in an earlier field
    [InlineData(@"M|T|Method|f|a\q:Int|None|||")]           // same, inside a parameter entry
    public void TryParse_RejectsMalformedInput(string canonical)
    {
        Assert.False(DeclId.TryParse(canonical, out _));
        Assert.Throws<FormatException>(() => DeclId.Parse(canonical));
    }

    [Theory]
    [InlineData("Mod|Type|Method|f||None|||")]
    [InlineData(@"Mod|Out\|er|Method|f|a:Swift.Int|Getter|<T>|$sSym|static")]
    [InlineData(@"Mod|Type|Method|f|a:Dict\,Key|None|||")]
    [InlineData(@"Mod|Type|Method|f|a\:b:Int|None|||")]
    [InlineData(@"Mod|Type|Method|f||None||back\\slash|")]
    public void EveryAcceptedCanonicalString_ReproducesItself(string canonical)
    {
        // The canonical form is a persisted key: two distinct strings must never name the same
        // declaration. That holds only if parsing is exact — accepting an escape sequence the
        // writer could not have produced would make `foo\q` and `fooq` aliases of one id.
        Assert.True(DeclId.TryParse(canonical, out var id), $"'{canonical}' should parse");
        Assert.Equal(canonical, id.Canonical);
    }

    [Fact]
    public void Create_RejectsMismatchedParameterArrays()
    {
        Assert.Throws<ArgumentException>(() => DeclId.Create(
            "M", "T", BindingItemKind.Method, "f",
            ImmutableArray.Create("a", "b"),
            ImmutableArray.Create("Swift.Int")));
    }

    [Fact]
    public void Equality_TreatsNullAndEmptyComponentsAsTheSame()
    {
        var normalized = DeclId.Create("M", "T", BindingItemKind.Method, "f");
        var nulled = normalized with { Module = "M", DeclPath = "T", Symbol = null! };

        Assert.Equal(normalized, nulled);
        Assert.Equal(normalized.GetHashCode(), nulled.GetHashCode());
    }

    [Fact]
    public void QualifiedPath_ElidesEmptySegments()
    {
        var nested = DeclId.Create("TestModule", "Loader.Payload", BindingItemKind.Method, "read");
        var topLevel = DeclId.Create("TestModule", null, BindingItemKind.Method, "read");

        Assert.Equal("TestModule.Loader.Payload.read", nested.QualifiedPath);
        Assert.Equal("TestModule.read", topLevel.QualifiedPath);
    }

    private static List<string> WalkIds(ModuleDecl module)
    {
        var ids = new List<string> { DeclIdFactory.ForModule(module).Canonical };
        foreach (var method in module.Methods)
            ids.Add(DeclIdFactory.ForMethod(method).Canonical);
        foreach (var property in module.Properties)
            ids.Add(DeclIdFactory.ForProperty(property).Canonical);
        foreach (var type in module.Types)
            WalkType(type, ids);
        return ids;
    }

    private static void WalkType(TypeDecl type, List<string> ids)
    {
        ids.Add(DeclIdFactory.ForType(type).Canonical);
        foreach (var method in type.Methods)
            ids.Add(DeclIdFactory.ForMethod(method, type).Canonical);
        foreach (var property in type.Properties)
            ids.Add(DeclIdFactory.ForProperty(property, AccessorKind.None, type).Canonical);
        foreach (var subscriptDecl in type.Subscripts)
            ids.Add(DeclIdFactory.ForSubscript(subscriptDecl, AccessorKind.None, type).Canonical);
        foreach (var nested in type.Types)
            WalkType(nested, ids);
    }
}
