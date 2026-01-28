// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Represents a single enum case (element) declaration.
    /// </summary>
    public sealed record EnumCaseDecl : BaseDecl
    {
        /// <summary>
        /// The mangled name for this enum case, used for P/Invoke.
        /// </summary>
        public required string MangledName { get; set; }

        /// <summary>
        /// The associated value types for this enum case, if any.
        /// Empty for simple enum cases without associated values.
        /// </summary>
        public List<TypeSpec> AssociatedValues { get; set; } = new();

        /// <summary>
        /// Whether this case has associated values.
        /// </summary>
        public bool HasAssociatedValues => AssociatedValues.Count > 0;
    }

    /// <summary>
    /// Represents an enum declaration.
    /// </summary>
    public sealed record EnumDecl : TypeDecl
    {
        /// <summary>
        /// The enum cases (elements) declared in this enum.
        /// </summary>
        public List<EnumCaseDecl> Cases { get; set; } = new();

        /// <summary>
        /// Whether this enum has any cases with associated values.
        /// </summary>
        public bool HasAssociatedValueCases => Cases.Any(c => c.HasAssociatedValues);
    }
}
