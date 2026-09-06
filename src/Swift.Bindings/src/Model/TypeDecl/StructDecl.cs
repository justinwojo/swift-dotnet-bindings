// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Represents a struct declaration.
    /// </summary>
    public sealed record StructDecl : TypeDecl
    {
        /// <summary>
        /// Whether the struct is frozen.
        /// </summary>
        public required bool IsFrozen { get; set; }

        /// <summary>
        /// Whether the struct raises its own alignment past what its stored fields require
        /// (Swift's <c>@_alignment(N)</c>). The ABI descriptor records only that the attribute is
        /// present, never N, so such a struct's inline layout cannot be derived from its fields:
        /// the alignment it lays out under, and therefore the padding before every field and the
        /// offset it takes inside a containing struct, are unknown.
        /// </summary>
        public bool HasCustomAlignment { get; set; }

        /// <summary>
        /// Protocol conformances.
        /// </summary>
        public required List<TypeConformance> Conformances { get; set; }

        /// <summary>
        /// Metadata accessor.
        /// </summary>
        public required string MetadataAccessor { get; set; }
    }
}
