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

        /// <summary>
        /// The USR (Unified Symbol Resolution) identifier of the direct superclass.
        /// Null for root classes. ObjC superclasses use "c:" prefix (e.g., "c:objc(cs)NSObject").
        /// Swift superclasses use "s:" prefix (e.g., "s:9Alamofire11DataRequestC").
        /// </summary>
        public string? SuperclassUsr { get; set; }

        /// <summary>
        /// Full superclass chain from direct parent to root (e.g., ["Alamofire.DataRequest", "Alamofire.Request"]).
        /// Each entry is a module-qualified Swift type name. Empty for root classes.
        /// </summary>
        public List<string> SuperclassNames { get; set; } = new();

        /// <summary>
        /// The module-qualified name of the direct superclass (first in chain), or null for root classes.
        /// </summary>
        public string? DirectSuperclassName => SuperclassNames.Count > 0 ? SuperclassNames[0] : null;

        /// <summary>
        /// Whether this class inherits convenience initializers from its superclass.
        /// </summary>
        public bool InheritsConvenienceInitializers { get; set; }

        /// <summary>
        /// Whether this class has missing designated initializers (hidden from Swift callers).
        /// </summary>
        public bool HasMissingDesignatedInitializers { get; set; }

        /// <summary>
        /// The resolved superclass ClassDecl, or null for root classes and classes with external
        /// (cross-module/ObjC) bases. Set during hierarchy resolution in ModuleProcessor.
        /// </summary>
        public ClassDecl? ResolvedSuperclass { get; set; }

        /// <summary>
        /// Whether this class has a resolved in-module superclass.
        /// </summary>
        public bool HasResolvedSuperclass => ResolvedSuperclass != null;

        /// <summary>
        /// Whether this class has a superclass that could not be resolved within the current module
        /// (cross-module or ObjC base class).
        /// </summary>
        public bool HasExternalSuperclass => DirectSuperclassName != null && ResolvedSuperclass == null;

        /// <summary>
        /// Whether the direct superclass is an Objective-C class (USR starts with "c:").
        /// </summary>
        public bool HasObjCSuperclass => SuperclassUsr?.StartsWith("c:") == true;

        /// <summary>
        /// Whether this class is rooted in an ObjC type hierarchy (directly or transitively).
        /// Set by ModuleProcessor after hierarchy resolution via fixed-point computation.
        /// </summary>
        public bool IsObjCRooted { get; set; }
    }
}
