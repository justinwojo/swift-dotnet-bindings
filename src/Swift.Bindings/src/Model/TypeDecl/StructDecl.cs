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
        /// Whether the struct is frozen. Once the module processor has run, whether it is loadable to
        /// clients: declared <c>@frozen</c> and storing nothing address-only to them.
        /// </summary>
        public required bool IsFrozen { get; set; }

        /// <summary>
        /// Whether the struct is declared <c>@frozen</c>, whatever <see cref="IsFrozen"/> later says
        /// about its loadability. A generic struct storing a bare parameter is not loadable
        /// unspecialized, but each specialization's layout follows from this declaration.
        /// </summary>
        public bool IsDeclaredFrozen
        {
            get => _isDeclaredFrozen ?? IsFrozen;
            set => _isDeclaredFrozen = value;
        }

        private bool? _isDeclaredFrozen;

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
