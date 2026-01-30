// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Represents a reference to a protocol associated type.
/// For example, in "Self.Element" or "τ_0_0.Element", this represents
/// a type that depends on an associated type of the conforming type.
/// </summary>
public class AssociatedTypeReferenceSpec : TypeSpec
{
    /// <summary>
    /// The base type that has the associated type (e.g., "Self", "τ_0_0", "T").
    /// </summary>
    public string BaseType { get; }

    /// <summary>
    /// The name of the associated type (e.g., "Element", "Index").
    /// </summary>
    public string AssociatedTypeName { get; }

    /// <summary>
    /// Creates a new AssociatedTypeReferenceSpec from a printed name like "Self.Element" or "τ_0_0.Element".
    /// </summary>
    /// <param name="printedName">The printed name containing the base type and associated type.</param>
    public AssociatedTypeReferenceSpec(string printedName) : base(TypeSpecKind.Named)
    {
        var dotIndex = printedName.IndexOf('.');
        if (dotIndex >= 0)
        {
            BaseType = printedName.Substring(0, dotIndex);
            AssociatedTypeName = printedName.Substring(dotIndex + 1);
        }
        else
        {
            // If no dot, treat the whole thing as the base type
            // This shouldn't normally happen for DependentMember types
            BaseType = printedName;
            AssociatedTypeName = string.Empty;
        }
    }

    /// <summary>
    /// Creates a new AssociatedTypeReferenceSpec with explicit base and associated type names.
    /// </summary>
    /// <param name="baseType">The base type name.</param>
    /// <param name="associatedTypeName">The associated type name.</param>
    public AssociatedTypeReferenceSpec(string baseType, string associatedTypeName) : base(TypeSpecKind.Named)
    {
        BaseType = baseType;
        AssociatedTypeName = associatedTypeName;
    }

    /// <inheritdoc/>
    protected override string LLToString(bool useFullName)
    {
        if (string.IsNullOrEmpty(AssociatedTypeName))
            return BaseType;
        return $"{BaseType}.{AssociatedTypeName}";
    }

    /// <inheritdoc/>
    protected override bool LLEquals(TypeSpec? other, bool partialNameMatch)
    {
        if (other is not AssociatedTypeReferenceSpec otherRef)
            return false;

        return BaseType == otherRef.BaseType && AssociatedTypeName == otherRef.AssociatedTypeName;
    }

    /// <inheritdoc/>
    public override bool HasDynamicSelf => BaseType == "Self";
}
