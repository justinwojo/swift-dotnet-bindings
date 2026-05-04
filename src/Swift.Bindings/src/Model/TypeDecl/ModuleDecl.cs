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
        /// Module-internal type names (short and module-qualified forms) collected by
        /// <c>Program.CollectInternalTypeNames</c> and merged with underscore-suppressed
        /// names. Read by emission-time gates (e.g.
        /// <see cref="MemberValidationPipeline"/> via
        /// <c>InternalTypeReferenceWalker</c>) to skip members whose signatures reach
        /// <c>@usableFromInline internal</c> or otherwise-suppressed types — Swift
        /// would refuse to compile a public wrapper that exposes them. Null until
        /// <c>Program.cs</c> populates it after parsing. Set, not init: the underscore
        /// merge runs after the initial assignment.
        /// </summary>
        public HashSet<string>? InternalTypeNames { get; set; }

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

        /// <summary>
        /// Module names from --framework-dependency that need `import` in the Swift wrapper.
        /// Distinct from <see cref="Dependencies"/> which is ABI-derived and filtered through AppleFrameworks.
        /// </summary>
        public List<string> DependencyModuleNames { get; set; } = new();

        /// <summary>
        /// TypeWitness mappings extracted from ABI JSON conformance entries.
        /// Maps (conformingType, protocol, associatedTypeName) → concrete TypeSpec.
        /// Populated by SwiftABIParser during HandleConformance.
        /// </summary>
        public ConformanceGraph ConformanceGraph { get; set; } = new();
    }
}
