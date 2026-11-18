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
        /// Distinct from <see cref="Dependencies"/> which is ABI-derived and filtered through
        /// <see cref="AppleFrameworkRegistry.IsWrapperImportableModule"/>.
        /// </summary>
        public List<string> DependencyModuleNames { get; set; } = new();

        /// <summary>
        /// Path to the bound module's <c>.swiftinterface</c> file (private preferred, falling back
        /// to public). Read by <c>ModuleHandler.EmitSwiftImports</c> to intersect
        /// <see cref="DependencyModuleNames"/> with the bound module's actual textual imports —
        /// suppresses spurious wrapper-side <c>import</c> lines for siblings the bound source
        /// never references (e.g., C++-only modules like <c>absl</c>/<c>grpc</c> that swiftc
        /// can't load anyway). Null when the swiftinterface is unavailable (direct mode without
        /// <c>-s/--swiftinterface</c>, apple-framework-mode unit tests, etc.); in that case the
        /// emitter falls back to the legacy unfiltered behavior.
        /// </summary>
        public string? SwiftInterfacePath { get; set; }

        /// <summary>
        /// TypeWitness mappings extracted from ABI JSON conformance entries.
        /// Maps (conformingType, protocol, associatedTypeName) → concrete TypeSpec.
        /// Populated by SwiftABIParser during HandleConformance.
        /// </summary>
        public ConformanceGraph ConformanceGraph { get; set; } = new();

        /// <summary>
        /// ProtocolDecls parsed from <c>--framework-dependency</c> modules, keyed by the
        /// dependency module's name. Lets the bound module's EveryProtocol emission reach
        /// the full member list of cross-module parent protocols when the bound module's
        /// own protocols inherit across the module boundary
        /// (justinwojo/swift-dotnet-bindings#40 cross-module variant). Empty when the
        /// generator was invoked without dependency parsing or when dependency parses
        /// produced no protocols.
        /// </summary>
        public Dictionary<string, List<ProtocolDecl>> DependencyProtocols { get; set; } = new();

        /// <summary>
        /// The declaration kind (<c>Class</c>, <c>Struct</c>, <c>Enum</c>, <c>Protocol</c>) of every
        /// other module's type this module's ABI dump mentions as a type declaration, keyed by its
        /// module-qualified name (<c>Foundation.AttributeScopes</c> → <c>Enum</c>). The digester writes
        /// such a node for each foreign type the module extends, so this is the compiler's own word on
        /// what an extended foreign type is, independent of which module it lives in. Populated by
        /// <c>SwiftABIParser</c> before any of its re-export gates drop the node.
        /// </summary>
        public Dictionary<string, string> ForeignTypeDeclKinds { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Declarations this module adds to another module's type that the parser could not keep,
        /// because it dropped the foreign receiver that hosts them (an enum receiver, a non-frozen or
        /// non-trivial struct receiver, a re-exported receiver, or a receiver the binder faulted on).
        /// The parser runs long before the report session the emitter writes, so it parks each one
        /// here and the emitter publishes it as a skip row once emission settles
        /// (<c>ForeignTypeExtensionEmitter.RepublishWithdrawals</c>). Without this a member the
        /// interface declares would vanish from the binding with nothing in the report to say so.
        /// </summary>
        public List<UnboundForeignReceiverMember> UnboundForeignReceiverMembers { get; } = new();
    }

    /// <summary>
    /// One declaration of the module being bound that lives on a foreign receiver the parser dropped.
    /// </summary>
    /// <param name="ForeignTypeQualifiedName">The receiver, module-qualified (<c>Foundation.AttributeScopes</c>).</param>
    /// <param name="Kind">What the declaration is; a nested type is <see cref="BindingItemKind.Type"/>.</param>
    /// <param name="Name">The declaration's own name.</param>
    /// <param name="Details">Why the receiver, and with it this declaration, was not bound.</param>
    /// <param name="Symbol">The declaration's mangled name, which keeps overloads sharing
    /// <paramref name="Name"/> on separate rows.</param>
    public sealed record UnboundForeignReceiverMember(
        string ForeignTypeQualifiedName,
        BindingItemKind Kind,
        string Name,
        string Details,
        string? Symbol = null);
}
