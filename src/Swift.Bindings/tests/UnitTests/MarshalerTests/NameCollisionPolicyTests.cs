// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins the naming SSOT itself: the two token vocabularies, the precedence table, and each
/// scheme's resolver. These are the invariants a reader of a generated binding relies on — a
/// token tells you which SIDE of the collision moved — so they are asserted directly rather
/// than only through the emitters that consume them.
/// </summary>
public class NameCollisionPolicyTests
{
    [Fact]
    public void TokenVocabularies_AreDisjoint()
    {
        // The whole point of two vocabularies is that a token identifies the renamed side.
        // A token appearing in both would make `FooInfo` ambiguous between "the type moved"
        // and "the member moved".
        foreach (var typeToken in NameCollisionPolicy.TypeSideTokens)
            Assert.DoesNotContain(typeToken, NameCollisionPolicy.MemberSideTokens);
    }

    [Fact]
    public void Precedence_ListsEverySchemeExactlyOnce()
    {
        var declared = Enum.GetValues<NameCollisionScheme>();
        Assert.Equal(declared.Length, NameCollisionPolicy.Precedence.Count);
        Assert.Equal(declared.Length, NameCollisionPolicy.Precedence.Distinct().Count());
        foreach (var scheme in declared)
            Assert.Contains(scheme, NameCollisionPolicy.Precedence);
    }

    [Fact]
    public void Precedence_TypeSideSchemesRunBeforeEveryMemberSideScheme()
    {
        // Renaming a type changes what every member-side decision sees, so the type-side
        // schemes have to be settled first — a member renamed against a stale type name is
        // a collision that survives the pass meant to remove it.
        var firstMemberSide = NameCollisionPolicy.Precedence
            .ToList()
            .FindIndex(s => NameCollisionPolicy.SideOf(s) == NameCollisionSide.Member);
        var lastTypeSide = NameCollisionPolicy.Precedence
            .ToList()
            .FindLastIndex(s => NameCollisionPolicy.SideOf(s) == NameCollisionSide.Type);

        Assert.True(lastTypeSide < firstMemberSide);
    }

    [Fact]
    public void Precedence_GetPrefixRunsBeforeMethodSuffix()
    {
        // Load-bearing: an async getter colliding with a property must read GetStatusAsync,
        // not StatusMethodAsync. Applying the suffix first dissolves the collision and the
        // Get prefix then never fires.
        var getIndex = NameCollisionPolicy.Precedence.ToList().IndexOf(NameCollisionScheme.MethodGetPrefix);
        var suffixIndex = NameCollisionPolicy.Precedence.ToList().IndexOf(NameCollisionScheme.MethodSuffix);
        Assert.True(getIndex < suffixIndex);
    }

    [Fact]
    public void Precedence_CaseOnlyMemberCollisionRunsBeforeTheOtherMemberSchemes()
    {
        // It decides a member's BASE name; the other member-side schemes operate on that base.
        var caseIndex = NameCollisionPolicy.Precedence.ToList()
            .IndexOf(NameCollisionScheme.CaseOnlyMemberCollision);
        var valueIndex = NameCollisionPolicy.Precedence.ToList()
            .IndexOf(NameCollisionScheme.PropertyValueSuffix);
        Assert.True(caseIndex < valueIndex);
    }

    [Theory]
    [InlineData(NameCollisionScheme.NestedTypeKindSuffix, NameCollisionSide.Type)]
    [InlineData(NameCollisionScheme.CaseOnlyNamespaceCollision, NameCollisionSide.Type)]
    [InlineData(NameCollisionScheme.CaseOnlyMemberCollision, NameCollisionSide.Member)]
    [InlineData(NameCollisionScheme.PropertyValueSuffix, NameCollisionSide.Member)]
    [InlineData(NameCollisionScheme.MethodGetPrefix, NameCollisionSide.Member)]
    [InlineData(NameCollisionScheme.MethodSuffix, NameCollisionSide.Member)]
    public void SideOf_MatchesTheDocumentedRenameTarget(NameCollisionScheme scheme, NameCollisionSide expected)
        => Assert.Equal(expected, NameCollisionPolicy.SideOf(scheme));

    // ---- Type-side resolver -----------------------------------------------------------------

    [Fact]
    public void TypeSuffixFor_EnumIsKind_AggregateIsInfo()
    {
        var moduleDecl = MakeModule();
        Assert.Equal(NameCollisionPolicy.EnumTypeSuffix,
            NameCollisionPolicy.TypeSuffixFor(MakeEnum("Status", moduleDecl)));
        Assert.Equal(NameCollisionPolicy.AggregateTypeSuffix,
            NameCollisionPolicy.TypeSuffixFor(MakeStruct("Payload", moduleDecl)));
    }

    [Fact]
    public void ResolveTypeSideName_AppendsTheSemanticSuffix()
        => Assert.Equal("StatusKind",
            NameCollisionPolicy.ResolveTypeSideName("Status", NameCollisionPolicy.EnumTypeSuffix, _ => false));

    [Fact]
    public void ResolveTypeSideName_LeafAlreadyEndsInSuffix_DoesNotStutter()
    {
        // `TokenKind` + "Kind" would read TokenKindKind. The leaf is used as-is, and because
        // that equals the name being renamed away from, the numeric fallback disambiguates.
        var resolved = NameCollisionPolicy.ResolveTypeSideName(
            "TokenKind", NameCollisionPolicy.EnumTypeSuffix, _ => false);
        Assert.Equal("TokenKind2", resolved);
        Assert.DoesNotContain("KindKind", resolved);
    }

    [Fact]
    public void ResolveTypeSideName_SemanticNameTaken_EscalatesNumerically()
    {
        var taken = new HashSet<string> { "PayloadInfo", "PayloadInfo2" };
        Assert.Equal("PayloadInfo3", NameCollisionPolicy.ResolveTypeSideName(
            "Payload", NameCollisionPolicy.AggregateTypeSuffix, taken.Contains));
    }

    [Fact]
    public void ResolveTypeSideName_NeverReturnsTheNameItIsRenamingAwayFrom()
    {
        // A no-op "rename" would leave the collision in place while reporting it resolved.
        var resolved = NameCollisionPolicy.ResolveTypeSideName(
            "Info", NameCollisionPolicy.AggregateTypeSuffix, _ => false);
        Assert.NotEqual("Info", resolved);
    }

    // ---- Member-side resolvers --------------------------------------------------------------

    [Fact]
    public void ResolveMemberValueName_AppendsValue()
        => Assert.Equal("ColorValue", NameCollisionPolicy.ResolveMemberValueName("Color", _ => false));

    [Fact]
    public void ResolveMemberValueName_Taken_EscalatesNumerically()
    {
        var taken = new HashSet<string> { "ColorValue" };
        Assert.Equal("ColorValue2", NameCollisionPolicy.ResolveMemberValueName("Color", taken.Contains));
    }

    [Theory]
    [InlineData(false, "ConfigureMethod")]
    [InlineData(true, "WithConfigure")]
    public void ResolveMethodCollisionName_KeepsBuilderMethodsFluent(bool isSelfReturning, string expected)
        => Assert.Equal(expected, NameCollisionPolicy.ResolveMethodCollisionName("Configure", isSelfReturning));

    [Fact]
    public void ResolveGetPrefixedName_PrefixesGet()
        => Assert.Equal("GetChecksum", NameCollisionPolicy.ResolveGetPrefixedName("Checksum"));

    [Fact]
    public void ResolveInheritedCollisionName_SuffixesSwift()
        => Assert.Equal("DisposeSwift", NameCollisionPolicy.ResolveInheritedCollisionName("Dispose"));

    [Fact]
    public void ResolveCaseOnlyMemberName_FirstWinnerKeepsTheNaturalName()
        => Assert.Equal("Url", NameCollisionPolicy.ResolveCaseOnlyMemberName("Url", _ => false));

    [Fact]
    public void ResolveCaseOnlyMemberName_EscalatesPastEveryTakenName()
    {
        var taken = new HashSet<string> { "Url", "Url2" };
        Assert.Equal("Url3", NameCollisionPolicy.ResolveCaseOnlyMemberName("Url", taken.Contains));
    }

    // ---- Delegation: NameProvider's scheme sites must produce the policy's tokens ------------

    [Fact]
    public void GetPropertyName_EnclosingTypeCollision_UsesTheMemberValueToken()
        => Assert.Equal($"Color{NameCollisionPolicy.MemberValueSuffix}",
            NameProvider.GetPropertyName("color", "Color"));

    [Fact]
    public void GetMethodName_PropertyCollision_UsesTheMemberMethodToken()
        => Assert.Equal($"Count{NameCollisionPolicy.MemberMethodSuffix}",
            NameProvider.GetMethodName("count", new HashSet<string> { "Count" }));

    [Fact]
    public void GetPublicMethodName_MethodNamedForItsEnclosingType_TakesTheGetPrefix()
    {
        // CS0542 — the C# member may not share its enclosing type's name.
        var ctx = new PublicMethodNameContext(
            MethodName: "checksum",
            IsAsync: false,
            HasReturnValue: true,
            PropertyNames: null,
            IsSelfReturning: false,
            ParentTypeName: "Checksum",
            ParameterCount: 0);
        Assert.Equal("GetChecksum", NameProvider.GetPublicMethodName(ctx));
    }

    [Fact]
    public void GetPublicMethodName_AsyncGetterCollidingWithProperty_PrefersGetOverMethodSuffix()
    {
        var ctx = new PublicMethodNameContext(
            MethodName: "status",
            IsAsync: true,
            HasReturnValue: true,
            PropertyNames: new HashSet<string> { "Status" },
            IsSelfReturning: false,
            ParentTypeName: null,
            ParameterCount: 0);
        Assert.Equal("GetStatusAsync", NameProvider.GetPublicMethodName(ctx));
    }

    private static ModuleDecl MakeModule() => new()
    {
        Name = "TestModule",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Dependencies = new List<string>(),
        Protocols = new List<ProtocolDecl>(),
        ParentDecl = null,
        ModuleDecl = null,
    };

    private static StructDecl MakeStruct(string name, ModuleDecl moduleDecl) => new()
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
        MangledName = "$sN",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Subscripts = new List<SubscriptDecl>(),
        GenericParameters = new List<GenericArgumentDecl>(),
        Conformances = new List<TypeConformance>(),
        ParentDecl = moduleDecl,
        ModuleDecl = moduleDecl,
        IsFrozen = true,
        MetadataAccessor = "$sMa",
    };

    private static EnumDecl MakeEnum(string name, ModuleDecl moduleDecl) => new()
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
        MangledName = "$sN",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Subscripts = new List<SubscriptDecl>(),
        GenericParameters = new List<GenericArgumentDecl>(),
        Conformances = new List<TypeConformance>(),
        Cases = new List<EnumCaseDecl>(),
        ParentDecl = moduleDecl,
        ModuleDecl = moduleDecl,
        IsFrozen = true,
        MetadataAccessor = "$sMa",
    };
}
