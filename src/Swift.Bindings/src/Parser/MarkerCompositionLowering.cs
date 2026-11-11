// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Rewrites a class composed only with marker protocols (<c>any SomeClass &amp; Sendable</c>)
/// to the class itself, everywhere a member signature names it.
///
/// <para>A marker protocol has no witness table and no runtime existence: Swift represents
/// <c>any SomeClass &amp; Sendable</c> with the metadata of <c>SomeClass</c> itself, lays it out
/// as the bare object reference, and passes it in one register with the same ownership as a
/// plain <c>SomeClass</c> parameter. The composition is therefore the class at every level the
/// binding touches — ABI, layout, and type metadata (so <c>[any SomeClass &amp; Sendable]</c> is
/// <c>Array&lt;SomeClass&gt;</c> at runtime). Projecting it as the class gives the consumer the
/// type they actually hold instead of an <c>object</c> parameter or a skipped member.</para>
///
/// <para>Left alone, deliberately:</para>
/// <list type="bullet">
/// <item>A composition with any protocol that has a witness table (<c>any SomeClass &amp;
/// SomeProtocol</c>): the existential then carries the table beside the reference, a two-word
/// layout the class does not have.</item>
/// <item>Protocol members. A generated Swift witness must spell its requirement's type exactly,
/// and the class is a different type to Swift's conformance checker even though it is the same
/// type at runtime.</item>
/// <item><c>inout</c> positions. Swift accepts the class where the composition is expected by
/// implicit conversion, which an <c>inout</c> argument does not allow.</item>
/// <item>Opaque (<c>some</c>) types, which are a different shape altogether.</item>
/// <item>A composition used as the argument of a generic type other than Optional, Array,
/// Dictionary or Set. Swift's generic types are invariant, so a wrapper could not pass
/// <c>Box&lt;SomeClass&gt;</c> where <c>Box&lt;any SomeClass &amp; Sendable&gt;</c> is
/// expected.</item>
/// </list>
///
/// <para>The rewrite keeps every member's mangled name, so a direct call still targets the
/// original symbol; a Swift wrapper spells the class and Swift converts it implicitly at the
/// call (the wrapper compiles with minimal concurrency checking, where the Sendable half of the
/// conversion is not enforced).</para>
/// </summary>
public static class MarkerCompositionLowering
{
    /// <summary>
    /// Applies the rewrite across the module's free functions, global properties, and types
    /// (recursively, including nested types). Protocols are not visited. Returns the number of
    /// type positions rewritten.
    /// </summary>
    public static int Apply(ModuleDecl module, ITypeDatabase typeDatabase)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(typeDatabase);

        var lowering = new Rewriter(typeDatabase);
        foreach (var method in module.Methods)
            lowering.Visit(method);
        foreach (var property in module.Properties)
            lowering.Visit(property);
        foreach (var type in module.Types)
            lowering.Visit(type);
        return lowering.Rewritten;
    }

    /// <summary>
    /// When <paramref name="composition"/> is exactly one class participant plus marker
    /// protocols, returns that class participant; otherwise null.
    /// </summary>
    public static NamedTypeSpec? TryGetErasedClass(ProtocolListTypeSpec composition, ITypeDatabase typeDatabase)
    {
        if (composition.IsOpaque || composition.IsInOut)
            return null;

        NamedTypeSpec? classParticipant = null;
        foreach (var participant in composition.Protocols.Keys)
        {
            if (IsSwiftMarkerProtocol(participant))
                continue;
            if (classParticipant is not null || !IsClass(participant, typeDatabase))
                return null;
            classParticipant = participant;
        }
        return classParticipant;
    }

    private static bool IsSwiftMarkerProtocol(NamedTypeSpec participant)
        => ExistentialHandler.IsMarkerProtocol(participant)
            && (!participant.HasModule() || participant.Module == "Swift");

    private static bool IsClass(NamedTypeSpec participant, ITypeDatabase typeDatabase)
    {
        try
        {
            if (typeDatabase.TryGetTypeRecord(SwiftTypeName.FromTypeSpec(participant), out var record))
                return record.Kind == TypeRecordKind.Class;
        }
        catch (ArgumentException)
        {
            return false;
        }
        // The Objective-C root classes are the only class participants identifiable by name
        // alone when no type database carries them.
        return participant.HasModule()
            && participant.Module is "Foundation" or "ObjectiveC"
            && AppleFrameworkRegistry.IsKnownObjCRootClass(participant.NameWithoutModule);
    }

    private sealed class Rewriter(ITypeDatabase typeDatabase)
    {
        public int Rewritten { get; private set; }

        public void Visit(TypeDecl type)
        {
            if (type is ProtocolDecl)
                return;
            foreach (var method in type.Methods)
                Visit(method);
            foreach (var property in type.Properties)
                Visit(property);
            foreach (var subscript in type.Subscripts)
            {
                foreach (var index in subscript.IndexParameters)
                {
                    if (!index.IsInOut)
                        index.SwiftTypeSpec = Lower(index.SwiftTypeSpec);
                }
                subscript.ReturnTypeSpec = Lower(subscript.ReturnTypeSpec);
                VisitAccessors(subscript.Accessors);
            }
            if (type is EnumDecl enumDecl)
            {
                foreach (var enumCase in enumDecl.Cases)
                {
                    for (int i = 0; i < enumCase.AssociatedValues.Count; i++)
                        enumCase.AssociatedValues[i] = Lower(enumCase.AssociatedValues[i]);
                }
            }
            foreach (var nested in type.Types)
                Visit(nested);
        }

        public void Visit(MethodDecl method)
        {
            if (method.IsProtocolRequirement)
                return;
            foreach (var argument in method.CSSignature)
            {
                if (!argument.IsInOut)
                    argument.SwiftTypeSpec = Lower(argument.SwiftTypeSpec);
            }
        }

        public void Visit(PropertyDecl property)
        {
            if (property.IsProtocolRequirement)
                return;
            property.SwiftTypeSpec = Lower(property.SwiftTypeSpec);
            VisitAccessors(property.Accessors);
        }

        // A property's or subscript's getter and setter are parsed as methods of their own that
        // carry the member's type again, and the emitter reads the accessor signatures, not just
        // the member's. They are rewritten together so the two never disagree.
        private void VisitAccessors(IReadOnlyList<AccessorDecl> accessors)
        {
            foreach (var accessor in accessors)
                Visit(accessor.Method);
        }

        private TypeSpec Lower(TypeSpec spec)
        {
            switch (spec)
            {
                case ProtocolListTypeSpec composition:
                    var erased = TryGetErasedClass(composition, typeDatabase);
                    if (erased is null)
                        return spec;
                    Rewritten++;
                    return CloneAsPosition(erased, composition);
                case NamedTypeSpec named when IsCovariantStdlibContainer(named):
                    LowerAll(named.GenericParameters);
                    return spec;
                case TupleTypeSpec tuple when !ReferenceEquals(tuple, TupleTypeSpec.Empty):
                    LowerAll(tuple.Elements);
                    return spec;
                case ClosureTypeSpec closure when !ReferenceEquals(closure, ClosureTypeSpec.VoidVoid):
                    closure.Arguments = Lower(closure.Arguments);
                    closure.ReturnType = Lower(closure.ReturnType);
                    return spec;
                default:
                    return spec;
            }
        }

        // Swift converts Optional, Array, Dictionary and Set element-wise, so a wrapper may hand
        // [SomeClass] where [any SomeClass & Sendable] is expected. Any other generic type is
        // invariant in its arguments: Box<SomeClass> does not convert to
        // Box<any SomeClass & Sendable>, so a composition nested in one stays as it is.
        private static bool IsCovariantStdlibContainer(NamedTypeSpec named)
            => named.InnerType is null
                && named.Name is "Swift.Optional" or "Swift.Array" or "Swift.Dictionary" or "Swift.Set";

        private void LowerAll(List<TypeSpec> specs)
        {
            for (int i = 0; i < specs.Count; i++)
                specs[i] = Lower(specs[i]);
        }

        // The class participant stands in the composition's position, so it takes over the
        // position's own facets (a tuple label, variadic-ness) rather than the participant's.
        // Its own identity carries over whole: generic arguments (a generic class is
        // exactly as erased by a marker as any other) and the nesting chain.
        private static NamedTypeSpec CloneAsPosition(NamedTypeSpec participant, ProtocolListTypeSpec position)
        {
            var replacement = new NamedTypeSpec(participant.Name, participant.GenericParameters.ToArray())
            {
                InnerType = participant.InnerType,
                Usr = participant.Usr,
                TypeLabel = position.TypeLabel,
                IsVariadic = position.IsVariadic,
                IsImplicitlyUnwrappedOptional = position.IsImplicitlyUnwrappedOptional,
            };
            replacement.Attributes.AddRange(position.Attributes);
            return replacement;
        }
    }
}
