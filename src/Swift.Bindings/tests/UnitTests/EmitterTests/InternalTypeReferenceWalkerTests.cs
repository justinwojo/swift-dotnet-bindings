// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="InternalTypeReferenceWalker"/>. The walker is the canonical
/// "does this signature reach a name in InternalTypeNames" predicate used by
/// <see cref="MemberValidationPipeline"/> to suppress emission for members whose
/// Swift wrapper would have to expose <c>@usableFromInline internal</c> types.
///
/// Critical case to cover: a public type in a different module whose short name
/// collides with an internal name in the current module must NOT be matched. This is
/// the trap the prior 4-library-regression attempt fell into.
/// </summary>
public class InternalTypeReferenceWalkerTests
{
    private const string Module = "TargetModule";

    private static IReadOnlySet<string> Internals(params string[] names) =>
        new HashSet<string>(names);

    private static MethodDecl Method(TypeSpec returnType, params TypeSpec[] paramTypes)
    {
        var sig = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = returnType,
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = null,
            },
        };
        for (int i = 0; i < paramTypes.Length; i++)
        {
            sig.Add(new ArgumentDecl
            {
                SwiftTypeSpec = paramTypes[i],
                Name = $"p{i}",
                PrivateName = $"p{i}",
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = null,
            });
        }
        return new MethodDecl
        {
            Name = "fn",
            MangledName = "$sFakeMangled",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = sig,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
        };
    }

    private static NamedTypeSpec NamedWithInner(string outerQualified, string inner) =>
        new NamedTypeSpec(outerQualified) { InnerType = new NamedTypeSpec(inner) };

    [Fact]
    public void EmptyInternalSet_ReturnsFalse()
    {
        var method = Method(new NamedTypeSpec($"{Module}.Internal"));
        Assert.False(InternalTypeReferenceWalker.SignatureReachesInternalType(
            method, new HashSet<string>(), Module));
    }

    [Fact]
    public void BareReference_ToInternalShortName_Matches()
    {
        // Unqualified reference (e.g., resolved within the module) matches the short name.
        var method = Method(new NamedTypeSpec("Internal"));
        Assert.True(InternalTypeReferenceWalker.SignatureReachesInternalType(
            method, Internals("Internal"), Module));
    }

    [Fact]
    public void ModuleQualifiedReference_ToInternal_Matches()
    {
        var method = Method(new NamedTypeSpec($"{Module}.Internal"));
        Assert.True(InternalTypeReferenceWalker.SignatureReachesInternalType(
            method, Internals($"{Module}.Internal", "Internal"), Module));
    }

    [Fact]
    public void NestedGenericArg_ReachesInternal()
    {
        // Foo<Internal>
        var method = Method(new NamedTypeSpec("Swift.Array",
            new NamedTypeSpec($"{Module}.Internal")));
        Assert.True(InternalTypeReferenceWalker.SignatureReachesInternalType(
            method, Internals($"{Module}.Internal"), Module));
    }

    [Fact]
    public void DeeplyNestedGenericArg_ReachesInternal()
    {
        // Dictionary<String, Optional<Internal>>
        var method = Method(new NamedTypeSpec("Swift.Dictionary",
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Swift.Optional",
                new NamedTypeSpec($"{Module}.Internal"))));
        Assert.True(InternalTypeReferenceWalker.SignatureReachesInternalType(
            method, Internals($"{Module}.Internal"), Module));
    }

    [Fact]
    public void OptionalWrapper_ReachesInternal()
    {
        // Swift.Optional<Internal>
        var method = Method(TupleTypeSpec.Empty,
            new NamedTypeSpec("Swift.Optional",
                new NamedTypeSpec($"{Module}.Internal")));
        Assert.True(InternalTypeReferenceWalker.SignatureReachesInternalType(
            method, Internals($"{Module}.Internal"), Module));
    }

    [Fact]
    public void TupleElement_ReachesInternal()
    {
        var tuple = new TupleTypeSpec(new[]
        {
            (TypeSpec)new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec($"{Module}.Internal"),
        });
        var method = Method(tuple);
        Assert.True(InternalTypeReferenceWalker.SignatureReachesInternalType(
            method, Internals($"{Module}.Internal"), Module));
    }

    [Fact]
    public void ClosureParameter_ReachesInternal()
    {
        // (Internal) -> Void
        var closure = new ClosureTypeSpec(
            new NamedTypeSpec($"{Module}.Internal"),
            TupleTypeSpec.Empty);
        var method = Method(TupleTypeSpec.Empty, closure);
        Assert.True(InternalTypeReferenceWalker.SignatureReachesInternalType(
            method, Internals($"{Module}.Internal"), Module));
    }

    [Fact]
    public void ClosureReturn_ReachesInternal()
    {
        // () -> Internal
        var closure = new ClosureTypeSpec(
            TupleTypeSpec.Empty,
            new NamedTypeSpec($"{Module}.Internal"));
        var method = Method(TupleTypeSpec.Empty, closure);
        Assert.True(InternalTypeReferenceWalker.SignatureReachesInternalType(
            method, Internals($"{Module}.Internal"), Module));
    }

    [Fact]
    public void GenericConstraint_ReachesInternal()
    {
        // <T : Internal>
        var generic = new GenericArgumentDecl(
            TypeName: "T",
            SugaredTypeName: "T",
            GenericConformances: new List<GenericParameterConformance>
            {
                new GenericParameterConformance(
                    Path: new[] { "T" },
                    ConformanceTarget: SwiftTypeName.FromModuleQualifiedName($"{Module}.Internal"),
                    Kind: ConformanceKind.Protocol),
            },
            AssosiatedTypeConformances: new List<GenericParameterConformance>());
        var method = Method(TupleTypeSpec.Empty);
        method.GenericParameters.Add(generic);
        Assert.True(InternalTypeReferenceWalker.SignatureReachesInternalType(
            method, Internals($"{Module}.Internal"), Module));
    }

    [Fact]
    public void NestedInnerType_ReachesInternal()
    {
        // TargetModule.Outer.Internal — InnerType chain
        var nested = NamedWithInner($"{Module}.Outer", "Internal");
        var method = Method(nested);
        Assert.True(InternalTypeReferenceWalker.SignatureReachesInternalType(
            method, Internals("Internal"), Module));
    }

    [Fact]
    public void NestedInnerType_CrossModule_DoesNotShortNameMatch()
    {
        // OtherModule.Outer.Internal — outer is qualified to a DIFFERENT module.
        // The inner link's NamedTypeSpec carries no module prefix, but the chain is
        // rooted in a foreign module — short-name fallback must NOT fire against the
        // current module's internal set. This is the exact regression guarded here.
        var nested = NamedWithInner("OtherModule.Outer", "Internal");
        var method = Method(nested);
        Assert.False(InternalTypeReferenceWalker.SignatureReachesInternalType(
            method, Internals($"{Module}.Internal", "Internal"), Module));
    }

    [Fact]
    public void NestedInnerType_CrossModule_QualifiedHitStillMatches()
    {
        // Same outer chain — but this time the internal set carries the full
        // qualified path of the nested type as it appears in the foreign module.
        // The walker should still match the qualified key (no short-name involvement).
        var nested = NamedWithInner("OtherModule.Outer", "Internal");
        var method = Method(nested);
        Assert.True(InternalTypeReferenceWalker.SignatureReachesInternalType(
            method, Internals("OtherModule.Outer.Internal"), Module));
    }

    [Fact]
    public void NestedInnerType_CurrentModule_QualifiedKeyMatches()
    {
        // TargetModule.Outer.Internal — the cumulative qualified path. When the
        // short name was removed for a public collision, the qualified form must
        // still hit. Without the cumulative-path build, this case would silently
        // return false.
        var nested = NamedWithInner($"{Module}.Outer", "Internal");
        var method = Method(nested);
        Assert.True(InternalTypeReferenceWalker.SignatureReachesInternalType(
            method, Internals($"{Module}.Outer.Internal"), Module));
    }

    [Fact]
    public void NestedInnerType_UnqualifiedRoot_ShortNameMatches()
    {
        // Outer.Internal where the outer has no module prefix — implicitly current
        // module. Short-name fallback should still work for the inner link.
        var nested = NamedWithInner("Outer", "Internal");
        var method = Method(nested);
        Assert.True(InternalTypeReferenceWalker.SignatureReachesInternalType(
            method, Internals("Internal"), Module));
    }

    [Fact]
    public void AssociatedTypeReference_DoesNotMatch()
    {
        // Self.Element / T.Element are projection paths over generic params, not
        // nominal types — they must never be matched against InternalTypeNames.
        var method = Method(new AssociatedTypeReferenceSpec("Self", "Internal"),
            new AssociatedTypeReferenceSpec("T", "Element"));
        Assert.False(InternalTypeReferenceWalker.SignatureReachesInternalType(
            method, Internals("Internal", "Element"), Module));
    }

    [Fact]
    public void NoInternalReached_ReturnsFalse()
    {
        // Negative case: signature exposes only public types.
        var method = Method(new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec($"{Module}.PublicType"));
        Assert.False(InternalTypeReferenceWalker.SignatureReachesInternalType(
            method, Internals($"{Module}.Internal", "Internal"), Module));
    }

    [Fact]
    public void CrossModuleShortNameCollision_DoesNotMatch()
    {
        // Critical trap case: a public type qualified to a DIFFERENT module
        // (OtherModule.Internal) has the same short name as the current module's
        // internal type. The walker must NOT match it — short-name fallback only
        // applies to current-module / unqualified references.
        var method = Method(new NamedTypeSpec("OtherModule.Internal"));
        Assert.False(InternalTypeReferenceWalker.SignatureReachesInternalType(
            method, Internals($"{Module}.Internal", "Internal"), Module));
    }

    [Fact]
    public void CrossModuleShortNameCollision_NestedInGeneric_DoesNotMatch()
    {
        // Same trap, but buried as a generic arg — walker must traverse without
        // accidentally short-name matching cross-module qualified names.
        var method = Method(TupleTypeSpec.Empty,
            new NamedTypeSpec("Swift.Array", new NamedTypeSpec("OtherModule.Internal")));
        Assert.False(InternalTypeReferenceWalker.SignatureReachesInternalType(
            method, Internals($"{Module}.Internal", "Internal"), Module));
    }

    [Fact]
    public void DeeplyNestedCombination_ReachesInternal()
    {
        // ((Optional<Internal>) -> Int)? buried inside a tuple inside a closure return.
        var optional = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec($"{Module}.Internal"));
        var closure = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)optional }),
            new NamedTypeSpec("Swift.Int"));
        var outer = new TupleTypeSpec(new[]
        {
            (TypeSpec)new NamedTypeSpec("Swift.String"),
            closure,
        });
        var method = Method(outer);
        Assert.True(InternalTypeReferenceWalker.SignatureReachesInternalType(
            method, Internals($"{Module}.Internal"), Module));
    }

    [Fact]
    public void PropertyDecl_ReachesInternal()
    {
        var property = new PropertyDecl
        {
            Name = "value",
            SwiftTypeSpec = new NamedTypeSpec($"{Module}.Internal"),
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
        Assert.True(InternalTypeReferenceWalker.SignatureReachesInternalType(
            property, Internals($"{Module}.Internal"), Module));
    }

    [Fact]
    public void SubscriptDecl_ReachesInternalViaIndex()
    {
        var subscript = new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$sFake_subscript",
            IsStatic = false,
            ReturnTypeSpec = new NamedTypeSpec("Swift.Int"),
            IndexParameters = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec($"{Module}.Internal"),
                    Name = "key",
                    PrivateName = "key",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null,
                },
            },
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
        Assert.True(InternalTypeReferenceWalker.SignatureReachesInternalType(
            subscript, Internals($"{Module}.Internal"), Module));
    }

    [Fact]
    public void SubscriptDecl_NoInternalReached_ReturnsFalse()
    {
        var subscript = new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$sFake_subscript",
            IsStatic = false,
            ReturnTypeSpec = new NamedTypeSpec("Swift.Int"),
            IndexParameters = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    Name = "key",
                    PrivateName = "key",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null,
                },
            },
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
        Assert.False(InternalTypeReferenceWalker.SignatureReachesInternalType(
            subscript, Internals($"{Module}.Internal"), Module));
    }
}
