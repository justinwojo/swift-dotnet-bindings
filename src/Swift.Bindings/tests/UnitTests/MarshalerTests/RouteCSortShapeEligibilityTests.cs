// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Unit coverage for <see cref="RouteCSortShapeEligibility.IsRouteCSortShapeEligible"/>.
/// Drives the three-way contract from Session 6c's design doc (Route C emitter,
/// CSM open-generic suppression, CSM eligibility predicate) — drift between any of
/// the three is the bug shape D's lesson called out. These tests pin the
/// predicate's decisions so the consumers can rely on it.
/// </summary>
public class RouteCSortShapeEligibilityTests
{
    // -------------------------------------------------------------------------
    // Positive: the canonical MusicLibraryRequest<Item>.sort(by:) shape.
    // -------------------------------------------------------------------------

    [Fact]
    public void Sort_KeyPath_UnconstrainedV_OnPATParent_IsEligible()
    {
        var (method, parent) = BuildSortShape();
        var ok = RouteCSortShapeEligibility.IsRouteCSortShapeEligible(method, parent, out var shape);
        Assert.True(ok);
        Assert.NotNull(shape);
        Assert.Equal("τ_0_0", shape!.ParentGenericParamName);
        Assert.Equal("TestModule.Filterable", shape.ProtocolName.ModuleQualifiedName);
        Assert.Equal("LibrarySortProperties", shape.AssocBagName);
        Assert.Equal(0, shape.KeyPathParameterIndex);
        Assert.Equal("τ_1_0", shape.MethodOwnValueParamName);
    }

    [Fact]
    public void Sort_KeyPath_UnconstrainedV_DottedRootEncoding_IsEligible()
    {
        // Some ABI inputs encode `τ_0_0.LibrarySortProperties` as a NamedTypeSpec
        // with a dotted Name instead of an AssociatedTypeReferenceSpec — must
        // accept both encodings (mirrors KeyPathSingletonEmitter.ScanTypeSpec).
        var rootDotted = new NamedTypeSpec("τ_0_0.LibrarySortProperties");
        var (method, parent) = BuildSortShape(rootOverride: rootDotted);
        var ok = RouteCSortShapeEligibility.IsRouteCSortShapeEligible(method, parent, out var shape);
        Assert.True(ok);
        Assert.Equal("LibrarySortProperties", shape!.AssocBagName);
    }

    // -------------------------------------------------------------------------
    // Negative: each of the 6 design-doc conditions, plus the risk-table cases.
    // -------------------------------------------------------------------------

    [Fact]
    public void Condition3_VHasProtocolConstraint_IsRejected()
    {
        // <V: Comparable> trips the "zero constraints" gate (R4 from design risk table).
        var (method, parent) = BuildSortShape(vProtocolConstraint: SwiftName("Swift", "Comparable"));
        Assert.False(RouteCSortShapeEligibility.IsRouteCSortShapeEligible(method, parent, out var shape));
        Assert.Null(shape);
    }

    [Fact]
    public void Condition2_TwoMethodOwnGenerics_IsRejected()
    {
        var (method, parent) = BuildSortShape();
        method.GenericParameters.Add(new GenericArgumentDecl(
            "τ_1_1", "W", new List<GenericParameterConformance>(), new List<GenericParameterConformance>()));
        Assert.False(RouteCSortShapeEligibility.IsRouteCSortShapeEligible(method, parent, out _));
    }

    [Fact]
    public void Condition4_VAppearsInReturnType_IsRejected()
    {
        // Return type = V; KeyPath value slot = V — V occurs twice (R6 from design risk table).
        var (method, parent) = BuildSortShape();
        method.CSSignature[0] = new ArgumentDecl
        {
            SwiftTypeSpec = new NamedTypeSpec("τ_1_0"),
            Name = string.Empty, PrivateName = string.Empty,
            IsInOut = false, IsGeneric = false,
            ParentDecl = null, ModuleDecl = null
        };
        Assert.False(RouteCSortShapeEligibility.IsRouteCSortShapeEligible(method, parent, out _));
    }

    [Fact]
    public void Condition4_VAppearsInExtraParam_IsRejected()
    {
        // KP value slot V + a `V` parameter — must be exactly one site.
        var (method, parent) = BuildSortShape();
        method.CSSignature.Add(new ArgumentDecl
        {
            SwiftTypeSpec = new NamedTypeSpec("τ_1_0"),
            Name = "other", PrivateName = "other",
            IsInOut = false, IsGeneric = false,
            ParentDecl = null, ModuleDecl = null
        });
        Assert.False(RouteCSortShapeEligibility.IsRouteCSortShapeEligible(method, parent, out _));
    }

    [Fact]
    public void Condition4_VWrappedInOptional_IsRejected()
    {
        // KP value slot is Optional<V> — Route C's `unsafeDowncast(_, to: KP<R, V>.self)`
        // requires the V substitution to be the bare method-own param. Reject
        // wrapping conservatively (revisit if a real Apple shape ever exposes this).
        var optionalV = new NamedTypeSpec("Swift.Optional");
        optionalV.GenericParameters.Add(new NamedTypeSpec("τ_1_0"));
        var (method, parent) = BuildSortShape(valueOverride: optionalV);
        Assert.False(RouteCSortShapeEligibility.IsRouteCSortShapeEligible(method, parent, out _));
    }

    [Fact]
    public void Condition5_KeyPathRootNotParentGeneric_IsRejected()
    {
        // Root references a concrete type, not the parent's generic.
        var concreteRoot = new AssociatedTypeReferenceSpec("OtherType", "Properties");
        var (method, parent) = BuildSortShape(rootOverride: concreteRoot);
        Assert.False(RouteCSortShapeEligibility.IsRouteCSortShapeEligible(method, parent, out _));
    }

    [Fact]
    public void Condition6_AsyncMethod_IsRejected()
    {
        var (method, parent) = BuildSortShape();
        method.IsAsync = true;
        Assert.False(RouteCSortShapeEligibility.IsRouteCSortShapeEligible(method, parent, out _));
    }

    [Fact]
    public void Condition6_TypedThrows_IsRejected()
    {
        var (method, parent) = BuildSortShape();
        method.ThrownErrorType = new NamedTypeSpec("MyApp.MyError");
        Assert.False(RouteCSortShapeEligibility.IsRouteCSortShapeEligible(method, parent, out _));
    }

    [Fact]
    public void Condition6_ActorIsolated_IsRejected()
    {
        var (method, parent) = BuildSortShape();
        method.IsActorIsolated = true;
        Assert.False(RouteCSortShapeEligibility.IsRouteCSortShapeEligible(method, parent, out _));
    }

    [Fact]
    public void Condition1_NonGenericParent_IsRejected()
    {
        var (method, _) = BuildSortShape();
        var nonGenericParent = BuildStructShell("NotGeneric", new List<GenericArgumentDecl>());
        Assert.False(RouteCSortShapeEligibility.IsRouteCSortShapeEligible(method, nonGenericParent, out _));
    }

    [Fact]
    public void Condition1_ParentGenericWithoutPATConstraint_IsRejected()
    {
        // Parent <T> with no protocol constraint — no conformer set to walk.
        var (method, _) = BuildSortShape();
        var unconstrainedParent = BuildParentTypeDecl(constraintProtocol: null);
        Assert.False(RouteCSortShapeEligibility.IsRouteCSortShapeEligible(method, unconstrainedParent, out _));
    }

    [Fact]
    public void Constructor_IsRejected()
    {
        var (method, parent) = BuildSortShape();
        method.IsConstructor = true;
        Assert.False(RouteCSortShapeEligibility.IsRouteCSortShapeEligible(method, parent, out _));
    }

    [Fact]
    public void Accessor_IsRejected()
    {
        var (method, parent) = BuildSortShape();
        method.IsAccessor = true;
        Assert.False(RouteCSortShapeEligibility.IsRouteCSortShapeEligible(method, parent, out _));
    }

    // -------------------------------------------------------------------------
    // Helpers — build a sort-shape MethodDecl + parent StructDecl.
    // -------------------------------------------------------------------------

    private static SwiftTypeName SwiftName(string module, string name) =>
        SwiftTypeName.FromModuleQualifiedName($"{module}.{name}");

    private static StructDecl BuildStructShell(string name, List<GenericArgumentDecl> genericParameters) =>
        new StructDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            ModuleDecl = null,
            ParentDecl = null,
            GenericParameters = genericParameters,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            IsFrozen = true,
            MetadataAccessor = $"$s{name}Ma",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
        };

    private static StructDecl BuildParentTypeDecl(SwiftTypeName? constraintProtocol)
    {
        var conformances = new List<GenericParameterConformance>();
        if (constraintProtocol is not null)
        {
            conformances.Add(new GenericParameterConformance(
                new[] { "τ_0_0" }, constraintProtocol, ConformanceKind.Protocol));
        }
        var parentGeneric = new GenericArgumentDecl(
            "τ_0_0", "Item",
            conformances,
            new List<GenericParameterConformance>());
        return BuildStructShell("Request", new List<GenericArgumentDecl> { parentGeneric });
    }

    /// <summary>
    /// Build a Route-C-shaped <c>sort&lt;V&gt;(by: KeyPath&lt;τ_0_0.LibrarySortProperties, V&gt;, ascending: Bool)</c>
    /// method on a <c>Request&lt;Item: TestModule.Filterable&gt;</c> parent, with knobs
    /// for the negative-case overrides.
    /// </summary>
    private static (MethodDecl Method, StructDecl Parent) BuildSortShape(
        SwiftTypeName? vProtocolConstraint = null,
        TypeSpec? rootOverride = null,
        TypeSpec? valueOverride = null)
    {
        var parent = BuildParentTypeDecl(SwiftName("TestModule", "Filterable"));
        var vConformances = new List<GenericParameterConformance>();
        if (vProtocolConstraint is not null)
        {
            vConformances.Add(new GenericParameterConformance(
                new[] { "τ_1_0" }, vProtocolConstraint, ConformanceKind.Protocol));
        }
        var vGeneric = new GenericArgumentDecl(
            "τ_1_0", "V", vConformances, new List<GenericParameterConformance>());

        var rootSpec = rootOverride ?? new AssociatedTypeReferenceSpec("τ_0_0", "LibrarySortProperties");
        var valueSpec = valueOverride ?? new NamedTypeSpec("τ_1_0");
        var keyPathSpec = new NamedTypeSpec("Swift.KeyPath");
        keyPathSpec.GenericParameters.Add(rootSpec);
        keyPathSpec.GenericParameters.Add(valueSpec);

        var returnArg = new ArgumentDecl
        {
            SwiftTypeSpec = TupleTypeSpec.Empty,
            Name = string.Empty, PrivateName = string.Empty,
            IsInOut = false, IsGeneric = false,
            ParentDecl = null, ModuleDecl = null
        };
        var keyPathArg = new ArgumentDecl
        {
            SwiftTypeSpec = keyPathSpec,
            Name = "by", PrivateName = "keyPath",
            IsInOut = false, IsGeneric = true,
            ParentDecl = null, ModuleDecl = null
        };
        var ascendingArg = new ArgumentDecl
        {
            SwiftTypeSpec = new NamedTypeSpec("Swift.Bool"),
            Name = "ascending", PrivateName = "ascending",
            IsInOut = false, IsGeneric = false,
            ParentDecl = null, ModuleDecl = null
        };

        var method = new MethodDecl
        {
            Name = "sort",
            MangledName = "$sRequest4sort",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl> { returnArg, keyPathArg, ascendingArg },
            GenericParameters = new List<GenericArgumentDecl> { vGeneric },
            ParentDecl = parent,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsMutating = true,
            Visibility = Visibility.Public,
        };
        return (method, parent);
    }
}
