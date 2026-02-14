// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Represents a module declaration.
    /// </summary>
    public sealed record ModuleDecl : BaseDecl
    {
        /// <summary>
        /// Exported symbols from the library's TBD file.
        /// Used by emitters to detect P/Invoke entry points that will fail at runtime.
        /// Null when no TBD was provided.
        /// </summary>
        public HashSet<string>? ExportedSymbols { get; set; }

        /// <summary>
        /// The module's properties.
        /// </summary>
        public required List<PropertyDecl> Properties { get; set; }

        /// <summary>
        /// The module's methods.
        /// </summary>
        public required List<MethodDecl> Methods { get; set; }

        /// <summary>
        /// The module's type declarations.
        /// </summary>
        public required List<TypeDecl> Types { get; set; }

        // <summary>
        // The module's `using` dependencies.
        // </summary>
        public required List<string> Dependencies { get; set; }

        // <summary>
        // The module's protocols.
        // </summary>
        public required List<ProtocolDecl> Protocols { get; set; }
    }
}
