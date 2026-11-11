// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// A provisional <see cref="ISwiftValueLowering"/> over the type database: a frozen struct or enum is
/// lowered with <see cref="TypeLowering.LowerParameterType"/>, the swiftcc register model the native thunks already use,
/// and is direct when it lowers to at most four pieces, indirect over that. Every shape that model
/// declines (packed or mixed integer and floating-point eightbytes, tuples, class-bound existentials,
/// types the database does not know) is unknown, so the classifier refuses it instead of guessing.
/// The swiftc lowering oracle replaces this as the authority; it exists so the facts can be
/// classified over a corpus before then.
/// </summary>
public sealed class TypeDatabaseSwiftValueLowering : ISwiftValueLowering
{
    private readonly ITypeDatabase _typeDatabase;

    public TypeDatabaseSwiftValueLowering(ITypeDatabase typeDatabase)
    {
        ArgumentNullException.ThrowIfNull(typeDatabase);
        _typeDatabase = typeDatabase;
    }

    public SwiftLoweringAnswer Lower(SwiftTypeFacts type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.Category is not (SwiftTypeCategory.Struct or SwiftTypeCategory.Enum))
            return SwiftLoweringAnswer.UnknownBecause($"the type-database lowering does not model a {type.Category}");
        // The same entry the thunks lower a parameter through: scalars, Optional and the typed
        // pointers first, then the type's record.
        if (TypeSpecParser.Parse(type.Spelling) is not NamedTypeSpec spec)
            return SwiftLoweringAnswer.UnknownBecause($"'{type.Spelling}' does not parse as a named type");

        var lowered = TypeLowering.LowerParameterType(spec, _typeDatabase);
        if (lowered is null)
            return SwiftLoweringAnswer.UnknownBecause($"the register model declines '{type.Spelling}', or the type database does not know it");
        return lowered.IsIndirect
            ? SwiftLoweringAnswer.IndirectBecause(lowered.Slots.Count > TypeLowering.MaxDirectSlots
                ? $"'{type.Spelling}' lowers to {lowered.Slots.Count} pieces, over the {TypeLowering.MaxDirectSlots}-piece limit"
                : $"'{type.Spelling}' is resilient")
            : SwiftLoweringAnswer.DirectPieces(lowered.Slots.Count, $"'{type.Spelling}' lowers to {lowered.Slots.Count} piece(s)");
    }
}
