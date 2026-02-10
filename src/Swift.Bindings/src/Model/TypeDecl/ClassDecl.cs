// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Represents a class declaration (includes Swift actors, which are reference types with isolation).
    /// </summary>
    public sealed record ClassDecl : TypeDecl
    {
        /// <summary>
        /// Protocol conformances.
        /// </summary>
        public required List<TypeConformance> Conformances { get; set; }

        /// <summary>
        /// Whether this class declaration represents a Swift actor type.
        /// Actors are detected by their conformance to the Swift Actor protocol (s:ScA).
        /// </summary>
        public bool IsActor { get; set; }

        /// <summary>
        /// Whether this class is declared as 'final'.
        /// Final classes use direct dispatch for methods (bare symbols exported).
        /// Non-final classes use vtable dispatch (only Tj thunk symbols exported).
        /// </summary>
        public bool IsFinal { get; set; }
    }
}
