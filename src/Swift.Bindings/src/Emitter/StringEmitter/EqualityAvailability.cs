// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// The floor a type's equality and hash wrappers are exported at, the one floor the struct, enum and
/// class equality emitters read for the wrapper's <c>@available</c>, for the guard in the private
/// method that calls the wrapper, and for the <c>[SupportedOSPlatform]</c> of the public
/// <c>Equals</c>, <c>==</c> and <c>!=</c> that reach it.
///
/// <para>The wrapper's body compares two values of the type (a struct or enum with <c>l == r</c>, a
/// class through its Equatable witness), so it needs what that comparison needs: the type, the
/// <c>==</c> it reaches (not some other overload the type declares, such as one comparing it with an
/// <c>Int</c>), and the Equatable conformance, whose floor the OS symbol
/// inference records apart from the type's when the conformance is newer than the type on the
/// inventory's OS (<see cref="ModuleDecl.InferredConformanceFloors"/>).</para>
/// </summary>
internal static class EqualityAvailability
{
    private const string Equatable = "Swift.Equatable";
    private const string Hashable = "Swift.Hashable";

    /// <summary>
    /// The <c>==</c> Swift resolves <c>l == r</c> to when both operands are the type: a binary static
    /// operator whose two parameters are the type itself. Null when the type declares none, so the
    /// conformance's (possibly synthesized) witness is what the comparison reaches.
    /// </summary>
    internal static OperatorDecl? SelfEqualityOperator(TypeDecl type) =>
        type.Operators.FirstOrDefault(op =>
            op.OperatorSymbol == "=="
            && op.Kind == OperatorKind.Binary
            && op.UnderlyingMethod.MethodType == MethodType.Static
            && op.UnderlyingMethod.CSSignature.Count == 3
            && op.UnderlyingMethod.CSSignature.Skip(1).All(operand => NamesType(operand.SwiftTypeSpec, type)));

    /// <summary>
    /// The floor of the type's equality wrapper: the floor of the <c>==</c> the comparison reaches, the
    /// type's own (with its enclosing types') and the Equatable conformance's inferred floor. Null when
    /// none is floored.
    ///
    /// <para>A struct or enum compares with <c>l == r</c>, which resolves to the type's own self
    /// <c>==</c>. A class compares through its Equatable witness, which is the <c>==</c> of the class
    /// that declares the conformance (<see cref="ConformanceDeclarer"/>): a subclass that inherits the
    /// conformance and declares an <c>==</c> of its own (at a later floor, say) does not move the
    /// witness, so the subclass's operator floors only that operator, not the wrapper.</para>
    /// </summary>
    internal static IReadOnlyList<AvailabilityAnnotation>? EqualityFloor(TypeDecl type)
    {
        var declarer = ConformanceDeclarer(type, Equatable);
        var floor = AvailabilityHelpers.MergeAvailabilityFromAncestors(SelfEqualityOperator(declarer)?.AvailabilityAnnotations, declarer);
        if (!ReferenceEquals(declarer, type))
            floor = Merge(floor, AvailabilityHelpers.MergeAvailabilityFromAncestors(null, type));
        return WithConformanceFloors(floor, declarer, Equatable);
    }

    /// <summary>
    /// The floor of a hash wrapper over the type's Hashable witness: the equality floor (Hashable
    /// refines Equatable) and the inferred floor of the Hashable conformance, the one its declaring
    /// class holds for an inherited conformance.
    /// </summary>
    internal static IReadOnlyList<AvailabilityAnnotation>? HashFloor(TypeDecl type) =>
        WithConformanceFloors(EqualityFloor(type), ConformanceDeclarer(type, Hashable), Hashable);

    /// <summary>
    /// The type whose conformance to <paramref name="protocol"/> a value of <paramref name="type"/>
    /// reaches through the protocol's witness. A struct or enum declares its own. A class inheriting
    /// the conformance lists it with no conformance descriptor (Swift forbids restating it), so the
    /// declarer is the nearest superclass in the module listing it with one. When no class in the
    /// resolved chain lists it so (a superclass in another module or in Objective-C), the declarer
    /// cannot be named and the type itself stands in: its own operator and floors, never less.
    /// </summary>
    internal static TypeDecl ConformanceDeclarer(TypeDecl type, string protocol)
    {
        if (type is not ClassDecl cls)
            return type;
        for (ClassDecl? current = cls; current != null; current = current.ResolvedSuperclass)
        {
            if (current.Conformances.Any(c => c.Protocol.ModuleQualifiedName == protocol
                    && !string.IsNullOrEmpty(c.ProtocolConformanceDescriptor)))
                return current;
        }
        return type;
    }

    private static IReadOnlyList<AvailabilityAnnotation>? Merge(
        IReadOnlyList<AvailabilityAnnotation>? first, IReadOnlyList<AvailabilityAnnotation>? second) =>
        first is not { Count: > 0 } ? second
        : second is not { Count: > 0 } ? first
        : first.Concat(second).ToList();

    /// <summary>
    /// Writes the <c>[SupportedOSPlatform]</c> a public equality member carries when
    /// <paramref name="floor"/> is above the floor the type already advertises; nothing otherwise.
    /// </summary>
    internal static void EmitMemberAttributes(CSharpWriter writer, IReadOnlyList<AvailabilityAnnotation>? floor, TypeDecl type) =>
        AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(
            writer, floor, AvailabilityHelpers.MergeAvailabilityFromAncestors(null, type));

    private static bool NamesType(TypeSpec? spec, TypeDecl type) =>
        spec is { IsDynamicSelf: true }
        || (spec is NamedTypeSpec named
            && (string.Equals(named.Name, type.SwiftTypeName.ModuleQualifiedName, StringComparison.Ordinal)
                || string.Equals(named.Name, OriginalModuleSpelling(type), StringComparison.Ordinal)));

    /// <summary>
    /// The type's name under the module that first defined it, when that differs from its current
    /// module. The ABI spells a type moved by <c>@_originallyDefinedIn</c>, and every type nested in
    /// one, that way (<c>RealityKit.Entity.ConfigurationCatalog</c> for a type now in
    /// <c>RealityFoundation</c>), and the type's stable mangled name records that module. Null when
    /// the type was not moved.
    /// </summary>
    private static string? OriginalModuleSpelling(TypeDecl type)
    {
        var current = type.SwiftTypeName;
        if (!ManglingProbes.TryGetModuleFromMangledName(type.MangledName, out var original)
            || string.Equals(original, current.Module, StringComparison.Ordinal)
            || !current.ModuleQualifiedName.StartsWith(current.Module + ".", StringComparison.Ordinal))
            return null;
        return original + current.ModuleQualifiedName[current.Module.Length..];
    }

    private static IReadOnlyList<AvailabilityAnnotation>? WithConformanceFloors(
        IReadOnlyList<AvailabilityAnnotation>? floor, TypeDecl type, string protocol)
    {
        var conformanceFloor = AvailabilityHelpers.InferredConformanceFloor(type, protocol).ToList();
        return Merge(floor, conformanceFloor);
    }
}
