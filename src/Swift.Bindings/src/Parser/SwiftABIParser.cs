// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using BindingsGeneration.Demangling;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

[assembly: InternalsVisibleTo("Swift.Bindings.Unit.Tests")]

namespace BindingsGeneration
{
    /// <summary>
    /// Represents the result of parsing a module.
    /// </summary>
    /// <param name="ModuleDecl">The module declaration.</param>
    /// <param name="TypeDecls">The type declarations.</param>
    public sealed record ModuleParsingResult(ModuleDecl ModuleDecl, Dictionary<NamedTypeSpec, TypeDecl> TypeDecls);

    /// <summary>
    /// Represents the root node of the ABI.
    /// </summary>
    public record ABIRootNode
    {
        public required RootNode ABIRoot { get; set; }
    }

    /// <summary>
    /// Represents the root node of a module.
    /// </summary>
    public record RootNode
    {
        public required string Kind { get; set; }
        public required string Name { get; set; }
        public required string PrintedName { get; set; }
        public required IEnumerable<Node> Children { get; set; } = Enumerable.Empty<Node>();
    }

    /// <summary>
    /// Represents a node.
    /// </summary>
    public record Node
    {
        public required string Kind { get; set; }
        public required string DeclKind { get; set; }
        public required string Name { get; set; }
        public required string MangledName { get; set; }
        public required string PrintedName { get; set; }
        public required string ModuleName { get; set; }
        public required string[] DeclAttributes { get; set; }
        public required bool? @static { get; set; }
        public required bool? IsInternal { get; set; }
        public required string? GenericSig { get; set; }
        public required string? sugared_genericSig { get; set; }
        public required bool? throwing { get; set; }
        public required string? AccessorKind { get; set; }
        public required string? EnumRawTypeName { get; set; }
        public required string? paramValueOwnership { get; set; }
        public required bool? hasDefaultArg { get; set; }
        public bool? overriding { get; set; }
        public bool? @implicit { get; set; }
        public bool? isFromExtension { get; set; }
        public string? funcSelfKind { get; set; }
        public string? usr { get; set; }
        public string? superclassUsr { get; set; }
        public string[]? superclassNames { get; set; }
        public bool? inheritsConvenienceInitializers { get; set; }
        public bool? hasMissingDesignatedInitializers { get; set; }
        public bool? protocolReq { get; set; }
        public string[]? typeAttributes { get; set; }
        public string[]? spi_group_names { get; set; }
        public required IEnumerable<Node> Children { get; set; } = Enumerable.Empty<Node>();
        public required IEnumerable<Node> Conformances { get; set; } = Enumerable.Empty<Node>();
        public required IEnumerable<Node> Accessors { get; set; } = Enumerable.Empty<Node>();
    }

    /// <summary>
    /// Represents a parser for Swift ABI.
    /// </summary>
    public sealed class SwiftABIParser : ISwiftParser
    {
        const string kNominal = "TypeNominal";
        const string kFunc = "TypeFunc";
        const string kTuple = "Tuple";
        const string kGenericTypeParam = "GenericTypeParam";

        // Swift operator characters per Swift Language Reference §Lexical Structure.
        // Operators are built from: / = - + ! * % < > & | ^ ~ ? .
        private static readonly HashSet<char> _operatorChars = new()
        {
            '/', '=', '-', '+', '!', '*', '%', '<', '>', '&', '|', '^', '~', '?', '.'
        };

        /// <summary>
        /// The ABI file path.
        /// </summary>
        private readonly string _filePath;

        /// <summary>
        /// The type database.
        /// </summary>
        private readonly ITypeDatabase _typeDatabase;

        /// <summary>
        /// The demangled TBD.
        /// </summary>
        private readonly DemanglingResults _demangledTbd;


        /// <summary>
        /// Logger instance.
        /// </summary>
        private readonly ILogger _logger;

        /// <summary>
        /// The module root node.
        /// </summary>
        private readonly ABIRootNode _moduleRoot;

        /// <summary>
        /// Types declared in the module.
        /// </summary>
        private readonly Dictionary<NamedTypeSpec, TypeDecl> _moduleTypes = new();

        /// <summary>
        /// TypeWitness mappings from conformance entries.
        /// Populated during HandleConformance, assigned to ModuleDecl at end of ParseModule.
        /// </summary>
        private readonly ConformanceGraph _conformanceGraph = new();

        /// <summary>
        /// The Swift demangler.
        /// </summary>
        private readonly Swift5Demangler demangler = new();

        /// <summary>
        /// Determines if a declaration node represents a module-internal declaration
        /// that is ABI-visible but not accessible from external Swift code.
        /// Detection layers:
        /// 1. node.IsInternal == true (explicit ABI JSON flag)
        /// 2. "UsableFromInline" in declAttributes (always means internal — @usableFromInline is only
        ///    used on internal declarations, regardless of whether AccessControl is also present)
        /// 3. "Inlinable" WITHOUT "AccessControl" (means @inlinable internal with implicit access)
        /// 4. "SPIAccessControl" in declAttributes (@_spi types — only visible to SPI consumers,
        ///    not part of the public API surface)
        /// 5. Supplementary swiftinterface data for @inlinable internal WITH AccessControl
        ///    (handled separately via _internalMemberKeys)
        /// </summary>
        private static bool IsNodeModuleInternal(Node node)
        {
            if (node.IsInternal == true)
                return true;

            if (node.DeclAttributes is null)
                return false;

            bool hasUsableFromInline = Array.IndexOf(node.DeclAttributes, "UsableFromInline") != -1;

            // @usableFromInline is exclusively used on internal declarations.
            // It means "this internal member has ABI stability requirements for inlining."
            if (hasUsableFromInline)
                return true;

            bool hasInlinable = Array.IndexOf(node.DeclAttributes, "Inlinable") != -1;
            bool hasAccessControl = Array.IndexOf(node.DeclAttributes, "AccessControl") != -1;

            // @inlinable without explicit access control means implicit internal access
            if (hasInlinable && !hasAccessControl)
                return true;

            // @_spi types are only visible to SPI consumers (e.g., other Stripe modules).
            // They are not part of the public API and should not appear in generated bindings.
            // Check both declAttributes and spi_group_names (different Swift compiler versions
            // use one or the other).
            if (Array.IndexOf(node.DeclAttributes, "SPIAccessControl") != -1)
                return true;
            if (node.spi_group_names is not null && node.spi_group_names.Length > 0)
                return true;

            return false;
        }

        /// <summary>
        /// Returns true if the node has @_spi protection.
        /// Checks both "SPIAccessControl" in declAttributes and the presence of spi_group_names.
        /// Some Swift compiler versions emit one or the other depending on how @_spi is applied
        /// (e.g., on the member directly vs. inherited from an @_spi extension).
        /// </summary>
        private static bool IsNodeSpiProtected(Node node)
        {
            if (node.DeclAttributes is not null &&
                Array.IndexOf(node.DeclAttributes, "SPIAccessControl") != -1)
                return true;

            return node.spi_group_names is not null && node.spi_group_names.Length > 0;
        }

        /// <summary>
        /// Sets actor isolation flags on a type declaration based on swiftinterface data.
        /// </summary>
        private void ApplyActorIsolation(TypeDecl typeDecl)
        {
            var qualifiedPath = BuildTypeQualifiedPath(typeDecl);

            if (_mainActorTypes != null && _mainActorTypes.Contains(qualifiedPath))
                typeDecl.IsMainActorIsolated = true;

            if (_customActorTypes != null && _customActorTypes.Contains(qualifiedPath))
                typeDecl.IsCustomActor = true;
        }

        /// <summary>
        /// Sets actor isolation flags on a method declaration based on swiftinterface data.
        /// Uses qualified type path + PrintedName (e.g., "Outer.Inner.foo(_:bar:)")
        /// to distinguish overloads and avoid nested-type name collisions.
        /// </summary>
        private void ApplyMemberActorIsolation(MethodDecl methodDecl, TypeDecl parentTypeDecl, string printedName)
        {
            var qualifiedPath = BuildTypeQualifiedPath(parentTypeDecl);
            var key = $"{qualifiedPath}.{printedName}";
            var shortKey = $"{parentTypeDecl.Name}.{printedName}";

            if (_actorIsolatedMembers != null)
            {
                if (_actorIsolatedMembers.Contains(key))
                    methodDecl.IsActorIsolated = true;
                else if (shortKey != key && _actorIsolatedMembers.Contains(shortKey))
                    methodDecl.IsActorIsolated = true;
            }

            // Set @MainActor-specific flag (subset of IsActorIsolated)
            if (_mainActorIsolatedMembers != null)
            {
                if (_mainActorIsolatedMembers.Contains(key) ||
                    (shortKey != key && _mainActorIsolatedMembers.Contains(shortKey)))
                    methodDecl.IsMainActorIsolated = true;
            }

            if (_nonisolatedMembers != null && _nonisolatedMembers.Contains(key))
                methodDecl.IsNonisolated = true;
        }

        /// <summary>
        /// Sets actor isolation flags on a property declaration based on swiftinterface data.
        /// Uses qualified type path to avoid nested-type name collisions.
        /// </summary>
        private void ApplyPropertyActorIsolation(PropertyDecl propertyDecl, TypeDecl parentTypeDecl)
        {
            var qualifiedPath = BuildTypeQualifiedPath(parentTypeDecl);
            var key = $"{qualifiedPath}.{propertyDecl.Name}";
            var shortKey = $"{parentTypeDecl.Name}.{propertyDecl.Name}";

            if (_actorIsolatedMembers != null)
            {
                if (_actorIsolatedMembers.Contains(key))
                    propertyDecl.IsActorIsolated = true;
                else if (shortKey != key && _actorIsolatedMembers.Contains(shortKey))
                    propertyDecl.IsActorIsolated = true;
            }

            // Set @MainActor-specific flag (subset of IsActorIsolated)
            if (_mainActorIsolatedMembers != null)
            {
                if (_mainActorIsolatedMembers.Contains(key) ||
                    (shortKey != key && _mainActorIsolatedMembers.Contains(shortKey)))
                    propertyDecl.IsMainActorIsolated = true;
            }

            if (_nonisolatedMembers != null && _nonisolatedMembers.Contains(key))
                propertyDecl.IsNonisolated = true;
        }

        /// <summary>
        /// Sets availability annotations on a type declaration from swiftinterface data.
        /// </summary>
        private void ApplyAvailability(TypeDecl typeDecl)
        {
            if (_availabilityAnnotations == null) return;
            var qualifiedPath = BuildTypeQualifiedPath(typeDecl);
            if (_availabilityAnnotations.TryGetValue(qualifiedPath, out var annotations))
                typeDecl.AvailabilityAnnotations = annotations;
        }

        /// <summary>
        /// Sets availability annotations on a member declaration from swiftinterface data.
        /// </summary>
        private void ApplyMemberAvailability(BaseDecl decl, TypeDecl parentTypeDecl, string printedName)
        {
            if (_availabilityAnnotations == null) return;
            var key = $"{BuildTypeQualifiedPath(parentTypeDecl)}.{printedName}";
            if (_availabilityAnnotations.TryGetValue(key, out var annotations))
                decl.AvailabilityAnnotations = annotations;
        }

        /// <summary>
        /// Applies default parameter value expressions from swiftinterface data to a method's arguments.
        /// Must be called AFTER all ArgumentDecl instances have been added to CSSignature.
        /// </summary>
        private void ApplyMemberDefaultValues(MethodDecl methodDecl, TypeDecl parentTypeDecl, string printedName)
        {
            if (_defaultParameterValues == null) return;
            var key = $"{BuildTypeQualifiedPath(parentTypeDecl)}.{printedName}";
            if (!_defaultParameterValues.TryGetValue(key, out var defaultValues))
                return;
            // Apply to arguments (skip i=0, the return type)
            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var argIdx = i - 1;
                if (argIdx < defaultValues.Count && methodDecl.CSSignature[i].HasDefaultArg)
                    methodDecl.CSSignature[i].SwiftDefaultExpression = defaultValues[argIdx];
            }
        }

        /// <summary>
        /// Applies default parameter values for free functions (module-level, not inside a type).
        /// Uses the bare printedName as key (matching the swiftinterface parser's output for top-level funcs).
        /// </summary>
        private void ApplyFreeFunctionDefaultValues(MethodDecl methodDecl, string printedName)
        {
            if (_defaultParameterValues == null) return;
            if (!_defaultParameterValues.TryGetValue(printedName, out var defaultValues))
                return;
            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var argIdx = i - 1;
                if (argIdx < defaultValues.Count && methodDecl.CSSignature[i].HasDefaultArg)
                    methodDecl.CSSignature[i].SwiftDefaultExpression = defaultValues[argIdx];
            }
        }

        /// <summary>
        /// Applies @autoclosure flags from swiftinterface data to closure parameters.
        /// Sets the "autoclosure" attribute on ClosureTypeSpec parameters so that wrapper
        /// emitters can add "()" when forwarding autoclosure arguments.
        /// </summary>
        private void ApplyMemberAutoclosureFlags(MethodDecl methodDecl, TypeDecl parentTypeDecl, string printedName)
        {
            if (_autoclosureParameters == null) return;
            var key = $"{BuildTypeQualifiedPath(parentTypeDecl)}.{printedName}";
            if (!_autoclosureParameters.TryGetValue(key, out var flags))
                return;
            ApplyAutoclosureFlagsToSignature(methodDecl, flags);
        }

        private void ApplyFreeFunctionAutoclosureFlags(MethodDecl methodDecl, string printedName)
        {
            if (_autoclosureParameters == null) return;
            if (!_autoclosureParameters.TryGetValue(printedName, out var flags))
                return;
            ApplyAutoclosureFlagsToSignature(methodDecl, flags);
        }

        private static void ApplyAutoclosureFlagsToSignature(MethodDecl methodDecl, List<bool> flags)
        {
            // CSSignature[0] is the return type, [1..] are parameters
            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var argIdx = i - 1;
                if (argIdx < flags.Count && flags[argIdx] &&
                    methodDecl.CSSignature[i].SwiftTypeSpec is ClosureTypeSpec closureSpec)
                {
                    closureSpec.Attributes.Add(new TypeSpecAttribute("autoclosure"));
                }
            }
        }

        /// <summary>
        /// Checks if a member is unconditionally unavailable from swiftinterface availability annotations.
        /// </summary>
        private bool IsUnavailableFromSwiftInterface(TypeDecl parentTypeDecl, string printedName)
        {
            if (_availabilityAnnotations == null) return false;
            var key = $"{BuildTypeQualifiedPath(parentTypeDecl)}.{printedName}";
            return _availabilityAnnotations.TryGetValue(key, out var annotations)
                && annotations.Any(a => a.IsUnconditionallyUnavailable);
        }

        /// <summary>
        /// Checks if a type is unconditionally unavailable from swiftinterface availability annotations.
        /// </summary>
        private bool IsTypeUnavailableFromSwiftInterface(TypeDecl typeDecl)
        {
            if (_availabilityAnnotations == null) return false;
            var key = BuildTypeQualifiedPath(typeDecl);
            return _availabilityAnnotations.TryGetValue(key, out var annotations)
                && annotations.Any(a => a.IsUnconditionallyUnavailable);
        }

        /// <summary>
        /// Checks if a type is internal based on the public type names set from swiftinterface.
        /// Returns true if the set is available, non-empty, and the type is NOT in it.
        /// </summary>
        private bool IsInternalFromPublicTypeNames(TypeDecl typeDecl)
        {
            if (_publicTypeNames == null || _publicTypeNames.Count == 0)
                return false;

            var qualifiedPath = BuildTypeQualifiedPath(typeDecl);
            return !_publicTypeNames.Contains(qualifiedPath);
        }

        /// <summary>
        /// Checks if a member is marked as internal in the supplementary swiftinterface data.
        /// This catches @inlinable internal members with AccessControl in declAttributes,
        /// which are indistinguishable from @inlinable public in ABI JSON alone.
        /// </summary>
        private bool IsInternalFromSwiftInterface(string parentTypeName, string printedName)
        {
            if (_internalMemberKeys == null || _internalMemberKeys.Count == 0)
                return false;

            var key = $"{parentTypeName}.{printedName}";
            return _internalMemberKeys.Contains(key);
        }

        /// <summary>
        /// Checks if a member is internal by negative-space detection: if the public
        /// swiftinterface has a set of public member names, any ABI member NOT in that
        /// set is internal. For type members, the key is "TypeName.printedName".
        /// For module-level functions, the key is the bare printedName.
        /// </summary>
        private bool IsInternalFromPublicMemberNames(BaseDecl parentDecl, string printedName)
        {
            if (_publicMemberNames == null || _publicMemberNames.Count == 0)
                return false;

            if (parentDecl is TypeDecl typeDecl)
            {
                // Skip types that are themselves internal — their members are already suppressed
                if (typeDecl.IsModuleInternal)
                    return false;

                var key = $"{typeDecl.Name}.{printedName}";
                return !_publicMemberNames.Contains(key);
            }

            // Module-level (free functions/variables): bare printedName
            return !_publicMemberNames.Contains(printedName);
        }

        /// <summary>
        /// Optional set of internal member keys from swiftinterface parsing.
        /// Keys are formatted as "TypeName.printedName" (e.g., "AES.encrypt(block:)").
        /// Used to detect @inlinable internal members that can't be distinguished
        /// from @inlinable public in the ABI JSON alone.
        /// </summary>
        private readonly HashSet<string>? _internalMemberKeys;

        /// <summary>
        /// Optional dictionary mapping "TypeName.printedName" keys to lists of internal
        /// parameter names from swiftinterface parsing. Used to populate PrivateName on
        /// ArgumentDecl so that generated C# uses meaningful parameter names instead of arg0/arg1.
        /// </summary>
        private readonly Dictionary<string, List<string>>? _parameterNames;

        /// <summary>
        /// Optional doc comments from symbol graph, keyed by USR.
        /// </summary>
        private readonly Dictionary<string, DocComment>? _docComments;

        /// <summary>
        /// Optional typed throws error types from swiftinterface parsing.
        /// Keys are "TypeName.printedName" or "printedName" (free functions).
        /// Values are fully-qualified Swift error type names (e.g., "SwiftBindingsTestLib.ParseError").
        /// </summary>
        private readonly Dictionary<string, string>? _typedThrowsErrors;

        /// <summary>
        /// Optional enum case parameter labels from swiftinterface parsing.
        /// Keys are "TypeName.caseName" (e.g., "Shape.circle").
        /// Values are lists of labels (null entries for unlabeled parameters).
        /// </summary>
        private readonly Dictionary<string, List<string?>>? _enumCaseLabels;

        /// <summary>
        /// Optional string enum raw values from swiftinterface parsing.
        /// Keys are "TypeName.caseName" (e.g., "HttpMethod.get").
        /// Values are the string raw value literals (e.g., "GET").
        /// </summary>
        private readonly Dictionary<string, string>? _enumCaseRawValues;

        /// <summary>
        /// Optional set of public type names from swiftinterface parsing.
        /// Types NOT in this set (when non-null and non-empty) are internal to the module.
        /// </summary>
        private readonly HashSet<string>? _publicTypeNames;

        /// <summary>
        /// Optional set of type names annotated with @MainActor from swiftinterface parsing.
        /// </summary>
        private readonly HashSet<string>? _mainActorTypes;

        /// <summary>
        /// Optional set of type names declared with the 'actor' keyword from swiftinterface parsing.
        /// </summary>
        private readonly HashSet<string>? _customActorTypes;

        /// <summary>
        /// Optional set of "TypeName.memberName" keys for actor-isolated members (both @MainActor and custom actors).
        /// </summary>
        private readonly HashSet<string>? _actorIsolatedMembers;

        /// <summary>
        /// Optional set of "TypeName.memberName" keys for @MainActor-isolated members only (subset of _actorIsolatedMembers).
        /// Used to distinguish @MainActor from custom actor isolation when setting IsMainActorIsolated.
        /// </summary>
        private readonly HashSet<string>? _mainActorIsolatedMembers;

        /// <summary>
        /// Optional set of "TypeName.memberName" keys for nonisolated members.
        /// </summary>
        private readonly HashSet<string>? _nonisolatedMembers;

        /// <summary>
        /// Optional availability annotations from swiftinterface parsing.
        /// Keys are qualified type paths or "TypePath.printedName" for members.
        /// </summary>
        private readonly Dictionary<string, List<AvailabilityAnnotation>>? _availabilityAnnotations;

        /// <summary>
        /// Optional default parameter value expressions from swiftinterface parsing.
        /// Keys are "QualifiedType.printedName". Values are index-aligned lists of
        /// raw Swift default expressions (null for params without defaults).
        /// </summary>
        private readonly Dictionary<string, List<string?>>? _defaultParameterValues;

        /// <summary>
        /// Optional @autoclosure parameter flags from swiftinterface parsing.
        /// Keys are "QualifiedType.printedName". Values are index-aligned lists of
        /// booleans indicating which parameters have @autoclosure.
        /// </summary>
        private readonly Dictionary<string, List<bool>>? _autoclosureParameters;
        private readonly HashSet<string>? _publicMemberNames;

        /// <summary>
        /// Optional set of "TypeName.printedName" keys for members with variadic parameters
        /// from swiftinterface parsing. The ABI JSON represents variadic params as Array&lt;T&gt;,
        /// making them indistinguishable from regular array params. @_cdecl wrappers can't call
        /// variadic methods correctly — passing [T] where T... is expected causes compilation error.
        /// </summary>
        private readonly HashSet<string>? _variadicMembers;

        /// <summary>
        /// Optional subscript parameter labels from swiftinterface parsing.
        /// Keys are "TypeName.subscript(label1:label2:)" (e.g., "AES.subscript(bitAt:)").
        /// Values are lists of external labels (e.g., ["bitAt"]).
        /// </summary>
        private readonly Dictionary<string, List<string>>? _subscriptLabels;

        /// <summary>
        /// Optional set of protocol names whose methods have @convention(c) or @convention(block)
        /// closure parameters. Detected from swiftinterface cross-reference since ABI JSON lacks
        /// convention attributes on TypeFunc nodes.
        /// </summary>
        private readonly HashSet<string>? _conventionCProtocols;

        public SwiftABIParser(
            string filePath,
            ITypeDatabase typeDatabase,
            DemanglingResults demangledTbd,
            ILogger logger,
            HashSet<string>? internalMemberKeys = null,
            Dictionary<string, List<string>>? parameterNames = null,
            Dictionary<string, DocComment>? docComments = null,
            Dictionary<string, string>? typedThrowsErrors = null,
            Dictionary<string, List<string?>>? enumCaseLabels = null,
            Dictionary<string, string>? enumCaseRawValues = null,
            HashSet<string>? publicTypeNames = null,
            HashSet<string>? mainActorTypes = null,
            HashSet<string>? customActorTypes = null,
            HashSet<string>? actorIsolatedMembers = null,
            HashSet<string>? nonisolatedMembers = null,
            Dictionary<string, List<AvailabilityAnnotation>>? availabilityAnnotations = null,
            Dictionary<string, List<string?>>? defaultParameterValues = null,
            Dictionary<string, List<bool>>? autoclosureParameters = null,
            HashSet<string>? publicMemberNames = null,
            Dictionary<string, List<string>>? subscriptLabels = null,
            HashSet<string>? mainActorIsolatedMembers = null,
            HashSet<string>? variadicMembers = null,
            HashSet<string>? conventionCProtocols = null)
        {
            _filePath = filePath;
            _typeDatabase = typeDatabase;
            _demangledTbd = demangledTbd;
            _logger = logger;
            _internalMemberKeys = internalMemberKeys;
            _parameterNames = parameterNames;
            _docComments = docComments;
            _typedThrowsErrors = typedThrowsErrors;
            _enumCaseLabels = enumCaseLabels;
            _enumCaseRawValues = enumCaseRawValues;
            _publicTypeNames = publicTypeNames;
            _mainActorTypes = mainActorTypes;
            _customActorTypes = customActorTypes;
            _actorIsolatedMembers = actorIsolatedMembers;
            _nonisolatedMembers = nonisolatedMembers;
            _availabilityAnnotations = availabilityAnnotations;
            _defaultParameterValues = defaultParameterValues;
            _autoclosureParameters = autoclosureParameters;
            _publicMemberNames = publicMemberNames;
            _subscriptLabels = subscriptLabels;
            _mainActorIsolatedMembers = mainActorIsolatedMembers;
            _variadicMembers = variadicMembers;
            _conventionCProtocols = conventionCProtocols;

            string jsonContent = File.ReadAllText(_filePath);
            _moduleRoot = JsonConvert.DeserializeObject<ABIRootNode>(jsonContent) ?? throw new InvalidOperationException("Invalid ABI structure.");
        }

        /// <summary>
        /// Gets the module name.
        /// </summary>
        /// <returns>The module name.</returns>
        public string GetModuleName()
        {
            var moduleName = _moduleRoot.ABIRoot.Children.FirstOrDefault()?.ModuleName ?? string.Empty;

            if (string.IsNullOrEmpty(moduleName) || moduleName == "NO_MODULE")
            {
                throw new InvalidOperationException(
                    $"ABI JSON has invalid module name '{moduleName}'. " +
                    "The Swift library must be compiled with BUILD_LIBRARY_FOR_DISTRIBUTION=YES " +
                    "(swiftc -enable-library-evolution) to produce valid ABI metadata.");
            }

            return moduleName;
        }

        /// <summary>
        /// Processes the module ABI. Processes all declarations and builds the ModuleDecl.
        /// </summary>
        /// <returns>The module ABI processing result.</returns>
        public ModuleParsingResult ParseModule()
        {
            var dependencies = new List<string>();
            var moduleName = GetModuleName();
            var moduleDecl = new ModuleDecl
            {
                Name = ExtractUniqueName(moduleName),
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Types = new List<TypeDecl>(),
                Dependencies = dependencies,
                Protocols = new List<ProtocolDecl>(),
                ParentDecl = null,
                ModuleDecl = null
            };

            var decls = CollectDeclarations(_moduleRoot.ABIRoot.Children, moduleDecl, moduleDecl);

            dependencies.Remove(moduleName);

            moduleDecl.Properties = decls.OfType<PropertyDecl>().ToList();
            moduleDecl.Methods = decls.OfType<MethodDecl>().ToList();
            moduleDecl.Types = decls.OfType<TypeDecl>().ToList();
            moduleDecl.Dependencies = dependencies;
            moduleDecl.Protocols = decls.OfType<ProtocolDecl>().ToList();

            foreach (var type in moduleDecl.Types)
            {
                _moduleTypes.TryAdd(new NamedTypeSpec(type.SwiftTypeName.ModuleQualifiedName), type);
            }

            moduleDecl.ConformanceGraph = _conformanceGraph;

            return new ModuleParsingResult(moduleDecl, _moduleTypes);
        }

        /// <summary>
        /// Collects declarations from a list of nodes.
        /// </summary>
        /// <param name="nodes">The list of nodes to collect declarations from.</param>
        /// <param name="parentDecl">The parent declaration.</param>
        /// <param name="moduleDecl">The module declaration.</param>
        /// <returns>The list of collected declarations.</returns>
        private List<BaseDecl> CollectDeclarations(IEnumerable<Node> nodes, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            var declarations = new List<BaseDecl>();
            foreach (var node in nodes)
            {
                var nodeDeclaration = HandleNode(node, parentDecl, moduleDecl);
                if (nodeDeclaration is not null)
                    declarations.Add(nodeDeclaration);
            }
            return declarations;
        }

        /// <summary>
        /// Handles an ABI node and returns the corresponding declaration.
        /// </summary>
        /// <param name="node">The node representing a declaration.</param>
        /// <param name="parentDecl">The parent declaration.</param>
        /// <param name="moduleDecl">The module declaration.</param>
        /// <returns>The declaration.</returns>
        private BaseDecl? HandleNode(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            BaseDecl? result = null;
            try
            {
                switch (node.Kind)
                {
                    case "TypeDecl":
                        result = HandleTypeDecl(node, parentDecl, moduleDecl);
                        break;
                    case "Function":
                    case "Constructor":
                        if (IsOperator(node.Name))
                            result = CreateOperatorDecl(node, parentDecl, moduleDecl);
                        else
                            result = CreateMethodDecl(node, parentDecl, moduleDecl);
                        break;
                    case "Var":
                        // Check if this is an enum element (enum case)
                        if (node.DeclKind == "EnumElement")
                            result = CreateEnumCaseDecl(node, parentDecl, moduleDecl);
                        else
                            result = CreatePropertyDecl(node, parentDecl, moduleDecl);
                        break;
                    case "Subscript":
                        result = CreateSubscriptDecl(node, parentDecl, moduleDecl);
                        break;
                    case "Import":
                        break;
                    default:
                        throw new NotImplementedException($"Unsupported node kind '{node.Kind}' encountered.");
                }
            }
            catch (NotImplementedException e)
            {
                _logger.LogWarning($"Not implemented '{node.Name}' ({node.MangledName}): {e.Message}");
            }
            catch (Exception e)
            {
                _logger.LogWarning($"Error while processing node '{node.Name} ({node.MangledName})': {e.Message}");
            }

            return result;
        }

        /// <summary>
        /// Handles a type declaration node and returns the corresponding declaration.
        /// </summary>
        /// <param name="node">The node representing a type declaration.</param>
        /// <param name="parentDecl">The parent declaration.</param>
        /// <param name="moduleDecl">The module declaration.</param>
        /// <returns>The type declaration.</returns>
        private TypeDecl? HandleTypeDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            // Cross-module re-export detection: if the node's ModuleName differs from the
            // module being parsed AND the source module is a third-party module (not Apple/system),
            // this type is re-exported and should not be bound here.
            // System module re-exports (Swift.Error, Foundation.URL, etc.) are kept because
            // the generated code legitimately extends or conforms to them.
            // Example: StripeCryptoOnramp re-exports StripeCore.STPAPIClient — skip it.
            if (!string.IsNullOrEmpty(node.ModuleName) &&
                !string.IsNullOrEmpty(moduleDecl.Name) &&
                node.ModuleName != moduleDecl.Name &&
                !AppleFrameworkRegistry.IsKnownAppleOrSystemModule(node.ModuleName))
            {
                _logger.LogInformation($"Skipping re-exported type '{node.Name}' (canonical module: {node.ModuleName}, current module: {moduleDecl.Name}).");
                return null;
            }

            // When a system-module type appears in another module's ABI (e.g., Swift.KeyPath
            // extended by RichTextKit), use the type's actual module for name qualification.
            string? moduleNameOverride = null;
            if (!string.IsNullOrEmpty(node.ModuleName) && parentDecl is ModuleDecl md2 && node.ModuleName != md2.Name)
            {
                moduleNameOverride = node.ModuleName;
            }

            var typeName = GetSwiftTypeName(parentDecl, node.Name, moduleNameOverride);
            var typeNameSpec = new NamedTypeSpec(typeName.ModuleQualifiedName);
            if (_typeDatabase.IsTypeProcessed(typeName) || _moduleTypes.ContainsKey(typeNameSpec))
            {
                // Cross-module re-exports (moduleNameOverride set) may appear multiple times
                // across ABI JSON entries. Skip silently — the first occurrence was already processed.
                if (moduleNameOverride != null)
                {
                    _logger.LogDebug($"Skipping duplicate cross-module type '{typeName}' (already processed).");
                    return null;
                }
                throw new InvalidOperationException($"Type '{node.Name}' already processed.");
            }

            if (string.IsNullOrEmpty(node.MangledName))
            {
                _logger.LogWarning($"Type '{node.Name}' has no mangled name. Skipping.");
                return null;
            }

            TypeDecl? decl;

            // Parse generic parameters if present (except for protocols which handle them differently)
            List<GenericArgumentDecl> genericParameters = new();
            if (node.GenericSig is not null && node.DeclKind != "Protocol")
            {
                genericParameters = GenericSignatureParser.ParseGenericSignature(node.GenericSig, node.sugared_genericSig);
            }

            switch (node.DeclKind)
            {
                case "Struct":
                    decl = CreateStructDecl(node, parentDecl, moduleDecl, genericParameters, moduleNameOverride);
                    break;

                case "Enum":
                    decl = CreateEnumDecl(node, parentDecl, moduleDecl, genericParameters, moduleNameOverride);
                    break;

                case "Class":
                    decl = CreateClassDecl(node, parentDecl, moduleDecl, genericParameters, moduleNameOverride);
                    break;

                case "Protocol":
                    decl = CreateProtocolDecl(node, parentDecl, moduleDecl, moduleNameOverride);
                    break;

                default:
                    _logger.LogWarning($"Unsupported declaration type '{node.DeclKind} {node.Name}' encountered.");
                    return null;
            }

            if (decl is not null)
            {
                // Register immediately so duplicate cross-module re-exports are caught
                _moduleTypes.TryAdd(new NamedTypeSpec(decl.SwiftTypeName.ModuleQualifiedName), decl);

                var childDecls = CollectDeclarations(node.Children, decl, moduleDecl);
                decl.Properties.AddRange(childDecls.OfType<PropertyDecl>());
                decl.Methods.AddRange(childDecls.OfType<MethodDecl>());
                decl.Types.AddRange(childDecls.OfType<TypeDecl>());
                decl.Operators.AddRange(childDecls.OfType<OperatorDecl>());
                decl.Subscripts.AddRange(childDecls.OfType<SubscriptDecl>());
                decl.GenericParameters = genericParameters;

                // Collect enum cases if this is an EnumDecl
                if (decl is EnumDecl enumDecl)
                {
                    enumDecl.Cases.AddRange(childDecls.OfType<EnumCaseDecl>());
                }

                // Detect missing protocol requirements: count ABI JSON Function/Constructor
                // children and compare against successfully parsed methods. A mismatch means
                // some children failed parsing (e.g., `some` parameter causing
                // GenericSignatureParser count mismatch). We count ALL Function/Constructor
                // children (not just protocolReq=true) because extension defaults also need
                // to parse successfully for the method count to match.
                if (decl is ProtocolDecl protocolDecl2)
                {
                    int expectedFuncChildren = node.Children
                        .Count(c => c.Kind == "Function" || c.Kind == "Constructor");
                    if (decl.Methods.Count < expectedFuncChildren)
                    {
                        protocolDecl2.HasMissingRequirements = true;
                        _logger.LogDebug("Protocol {Name}: {Missing} method(s) failed ABI parsing ({Parsed}/{Expected})",
                            decl.Name, expectedFuncChildren - decl.Methods.Count, decl.Methods.Count, expectedFuncChildren);
                    }
                }

                foreach (var type in decl.Types)
                {
                    _moduleTypes.TryAdd(new NamedTypeSpec(type.SwiftTypeName.ModuleQualifiedName), type);
                }
            }

            return decl;
        }

        private TypeConformance HandleConformance(Node node, SwiftTypeName typeName)
        {
            var reduction = demangler.Run(node.MangledName) as TypeSpecReduction ?? throw new InvalidOperationException($"Invalid demangling result for '{node.MangledName}'.");
            var protocolTypeSpec = reduction.TypeSpec as NamedTypeSpec ?? throw new InvalidOperationException($"TypeSpec '{reduction.TypeSpec}' is not a NamedTypeSpec");
            SwiftTypeName protocolName = SwiftTypeName.FromTypeSpec(protocolTypeSpec);
            string protocolConformanceDescriptor = string.Empty;

            try
            {
                protocolConformanceDescriptor = _demangledTbd.GetProtocolConformanceDescriptor(typeName, protocolName);
            }
            catch (Exception e)
            {
                // TODO: Some types conform to protocols inherently, i.e., they are not explicitly declared.
                // These conformances are specified in the ABI.json but the descriptors are not present in the TBD.
                _logger.LogWarning($"Error while getting protocol conformance descriptor for '{typeName}' and protocol '{protocolName}': {e.Message}");
            }

            var conformance = new TypeConformance(typeName, protocolName, protocolConformanceDescriptor);

            // Extract TypeWitness entries from conformance children.
            // These map associated types to concrete types for this conformance.
            foreach (var child in node.Children)
            {
                if (child.Kind != "TypeWitness") continue;
                if (!child.Children.Any()) continue;
                try
                {
                    var resolvedType = CreateTypeSpec(child.Children.First());
                    _conformanceGraph.AddWitness(
                        typeName.ModuleQualifiedName,
                        protocolName.ModuleQualifiedName,
                        child.Name,  // e.g., "Element"
                        resolvedType);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to parse TypeWitness {typeName}.{child.Name}: {ex.Message}");
                }
            }

            return conformance;
        }

        /// <summary>
        /// Creates a struct declaration from a node.
        /// </summary>
        /// <param name="node">The node representing the struct declaration.</param>
        /// <param name="parentDecl">The parent declaration.</param>
        /// <param name="moduleDecl">The module declaration.</param>
        /// <param name="genericParameters">The generic parameters for this type.</param>
        /// <returns>The struct declaration.</returns>
        private StructDecl CreateStructDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl, List<GenericArgumentDecl> genericParameters, string? moduleNameOverride = null)
        {
            var swiftTypeName = GetSwiftTypeName(parentDecl, node.Name, moduleNameOverride);
            var hasFrozenAttribute = node.DeclAttributes is not null && Array.IndexOf(node.DeclAttributes, "Frozen") != -1;

            var decl = new StructDecl
            {
                Name = ExtractUniqueName(node.Name),
                SwiftTypeName = swiftTypeName,
                MangledName = node.MangledName,
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Types = new List<TypeDecl>(),
                Operators = new List<OperatorDecl>(),
                GenericParameters = genericParameters,
                Conformances = [.. node.Conformances.Select(x => HandleConformance(x, swiftTypeName))],
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                IsFrozen = hasFrozenAttribute,
                MetadataAccessor = _demangledTbd.GetMetadataAccessor(swiftTypeName),
                IsModuleInternal = IsNodeModuleInternal(node),
                IsSpiProtected = IsNodeSpiProtected(node)
            };
            if (!decl.IsModuleInternal)
                decl.IsModuleInternal = IsInternalFromPublicTypeNames(decl);
            if (!decl.IsModuleInternal && IsTypeUnavailableFromSwiftInterface(decl))
                decl.IsModuleInternal = true;
            ApplyActorIsolation(decl);
            ApplyAvailability(decl);
            PopulateDocumentation(decl, node);
            return decl;
        }

        /// <summary>
        /// Creates an enum declaration from a node.
        /// </summary>
        /// <param name="node">The node representing the enum declaration.</param>
        /// <param name="parentDecl">The parent declaration.</param>
        /// <param name="moduleDecl">The module declaration.</param>
        /// <param name="genericParameters">The generic parameters for this type.</param>
        /// <returns>The enum declaration.</returns>
        private EnumDecl CreateEnumDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl, List<GenericArgumentDecl> genericParameters, string? moduleNameOverride = null)
        {
            var swiftTypeName = GetSwiftTypeName(parentDecl, node.Name, moduleNameOverride);
            var hasFrozenAttribute = node.DeclAttributes is not null && Array.IndexOf(node.DeclAttributes, "Frozen") != -1;

            var decl = new EnumDecl
            {
                Name = ExtractUniqueName(node.Name),
                SwiftTypeName = swiftTypeName,
                MangledName = node.MangledName,
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Types = new List<TypeDecl>(),
                Operators = new List<OperatorDecl>(),
                Cases = new List<EnumCaseDecl>(),
                GenericParameters = genericParameters,
                Conformances = [.. node.Conformances.Select(x => HandleConformance(x, swiftTypeName))],
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                IsFrozen = hasFrozenAttribute,
                MetadataAccessor = _demangledTbd.GetMetadataAccessor(swiftTypeName),
                RawValueTypeName = node.EnumRawTypeName,
                IsModuleInternal = IsNodeModuleInternal(node),
                IsSpiProtected = IsNodeSpiProtected(node)
            };
            if (!decl.IsModuleInternal)
                decl.IsModuleInternal = IsInternalFromPublicTypeNames(decl);
            if (!decl.IsModuleInternal && IsTypeUnavailableFromSwiftInterface(decl))
                decl.IsModuleInternal = true;
            ApplyActorIsolation(decl);
            ApplyAvailability(decl);
            PopulateDocumentation(decl, node);
            return decl;
        }

        /// <summary>
        /// Creates an enum case declaration from a node.
        /// </summary>
        /// <param name="node">The node representing the enum case.</param>
        /// <param name="parentDecl">The parent declaration.</param>
        /// <param name="moduleDecl">The module declaration.</param>
        /// <returns>The enum case declaration.</returns>
        private EnumCaseDecl CreateEnumCaseDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            // Strip Swift backtick escaping (e.g., `subscript` → subscript).
            var caseName = node.Name;
            if (caseName.Length >= 2 && caseName[0] == '`' && caseName[caseName.Length - 1] == '`')
                caseName = caseName.Substring(1, caseName.Length - 2);

            var enumCaseDecl = new EnumCaseDecl
            {
                Name = caseName,
                MangledName = node.MangledName,
                AssociatedValues = new List<TypeSpec>(),
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                IsSpiProtected = IsNodeSpiProtected(node),
            };
            PopulateDocumentation(enumCaseDecl, node);

            // Parse associated values from the type signature if present
            // The type signature for enum cases looks like:
            // - Simple case: (EnumType.Type) -> EnumType
            // - Case with associated values: (EnumType.Type) -> (AssocValue1, AssocValue2) -> EnumType
            var children = node.Children.ToList();
            if (children.Count > 0 && children[0].Kind == kFunc)
            {
                var funcChildren = children[0].Children.ToList();
                // For associated values, there will be a tuple in the function type
                // The structure is: Function -> [ReturnType, Metatype] for simple
                // or Function -> [Function -> [ReturnType, TupleOfAssocValues], Metatype] for associated values
                if (funcChildren.Count >= 2)
                {
                    var returnPart = funcChildren[0];
                    // Check if returnPart is another function (indicating associated values)
                    if (returnPart.Kind == kFunc)
                    {
                        var innerFuncChildren = returnPart.Children.ToList();
                        if (innerFuncChildren.Count >= 2)
                        {
                            // The second child should be the associated values
                            var assocValuesNode = innerFuncChildren[1];
                            if (assocValuesNode.Kind == kTuple || assocValuesNode.Name == kTuple)
                            {
                                // Parse the full tuple printedName to preserve associated value labels.
                                // e.g., "(radius: Swift.Double)" → TypeSpec with TypeLabel = "radius"
                                // TypeSpecParser.Parse() throws on malformed input, so wrap in try/catch
                                // with fallback to the old child-by-child approach.
                                bool parsedFromTuplePrintedName = false;
                                try
                                {
                                    var tuplePrintedName = assocValuesNode.PrintedName;
                                    var parsedTuple = TypeSpecParser.Parse(tuplePrintedName);
                                    if (parsedTuple is TupleTypeSpec tupleSpec)
                                    {
                                        foreach (var element in tupleSpec.Elements)
                                            enumCaseDecl.AssociatedValues.Add(element);
                                        parsedFromTuplePrintedName = true;
                                    }
                                    else if (parsedTuple != null)
                                    {
                                        // Single-element tuple unwrapped by TypeSpecParser
                                        enumCaseDecl.AssociatedValues.Add(parsedTuple);
                                        parsedFromTuplePrintedName = true;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    // TypeSpecParser throws on parse errors — fall through to child iteration
                                    _logger.LogDebug($"Failed to parse tuple printedName '{assocValuesNode.PrintedName}' for enum case '{enumCaseDecl.Name}': {ex.Message}");
                                }

                                if (!parsedFromTuplePrintedName)
                                {
                                    // Fallback: parse individual children (previous behavior, no labels)
                                    foreach (var tupleElement in assocValuesNode.Children)
                                    {
                                        if (tupleElement.Kind == kNominal)
                                        {
                                            var typeSpec = TypeSpecParser.Parse(tupleElement.PrintedName);
                                            if (typeSpec != null)
                                                enumCaseDecl.AssociatedValues.Add(typeSpec);
                                        }
                                    }
                                }
                            }
                            else if (assocValuesNode.Kind == kNominal)
                            {
                                // Single associated value
                                var typeSpec = TypeSpecParser.Parse(assocValuesNode.PrintedName);
                                if (typeSpec != null)
                                {
                                    enumCaseDecl.AssociatedValues.Add(typeSpec);
                                }
                            }
                        }
                    }
                }
            }

            // Apply parameter labels from swiftinterface if available
            if (_enumCaseLabels != null && enumCaseDecl.AssociatedValues.Count > 0 && parentDecl is TypeDecl parentType)
            {
                // Build fully-qualified type path matching the parser's dot-joined key format
                // e.g., "OrderContainer.Status.caseName" for nested enum Status inside OrderContainer
                var typePath = BuildTypeQualifiedPath(parentType);
                var key = $"{typePath}.{enumCaseDecl.Name}";
                if (_enumCaseLabels.TryGetValue(key, out var labels))
                {
                    for (int i = 0; i < Math.Min(labels.Count, enumCaseDecl.AssociatedValues.Count); i++)
                    {
                        // Only apply swiftinterface label when ABI didn't already provide one
                        if (labels[i] != null && string.IsNullOrEmpty(enumCaseDecl.AssociatedValues[i].TypeLabel))
                        {
                            enumCaseDecl.AssociatedValues[i].TypeLabel = labels[i];
                        }
                    }
                }
            }

            // Apply string raw values from swiftinterface if available
            if (_enumCaseRawValues != null && parentDecl is TypeDecl rawValueParent)
            {
                var typePath = BuildTypeQualifiedPath(rawValueParent);
                var rawKey = $"{typePath}.{enumCaseDecl.Name}";
                if (_enumCaseRawValues.TryGetValue(rawKey, out var rawValue))
                {
                    enumCaseDecl.RawValue = rawValue;
                }
            }

            return enumCaseDecl;
        }

        /// <summary>
        /// Builds a fully-qualified type path by walking up the parent chain.
        /// Matches the dot-joined format used by SwiftInterfaceAccessParser's type stack.
        /// e.g., for Status nested inside OrderContainer: "OrderContainer.Status"
        /// </summary>
        private static string BuildTypeQualifiedPath(TypeDecl typeDecl)
        {
            var parts = new List<string>();
            BaseDecl? current = typeDecl;
            while (current is TypeDecl td)
            {
                parts.Add(td.Name);
                current = td.ParentDecl;
            }
            parts.Reverse();
            return string.Join(".", parts);
        }

        /// <summary>
        /// Creates a class declaration from a node.
        /// </summary>
        /// <param name="node">The node representing the class declaration.</param>
        /// <param name="parentDecl">The parent declaration.</param>
        /// <param name="moduleDecl">The module declaration.</param>
        /// <param name="genericParameters">The generic parameters for this type.</param>
        /// <returns>The class declaration.</returns>
        private ClassDecl CreateClassDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl, List<GenericArgumentDecl> genericParameters, string? moduleNameOverride = null)
        {
            var swiftTypeName = GetSwiftTypeName(parentDecl, node.Name, moduleNameOverride);

            // Detect actors by checking for conformance to the Swift Actor protocol.
            // Use the stable mangled name ($sScA) to avoid false positives from user-defined protocols named "Actor".
            var isActor = node.Conformances.Any(c => c.MangledName == "$sScA");

            var decl = new ClassDecl
            {
                Name = ExtractUniqueName(node.Name),
                SwiftTypeName = swiftTypeName,
                MangledName = node.MangledName,
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Types = new List<TypeDecl>(),
                Operators = new List<OperatorDecl>(),
                GenericParameters = genericParameters,
                Conformances = [.. node.Conformances.Select(x => HandleConformance(x, swiftTypeName))],
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                IsActor = isActor,
                IsFinal = node.DeclAttributes?.Contains("Final") == true,
                IsModuleInternal = IsNodeModuleInternal(node),
                IsSpiProtected = IsNodeSpiProtected(node),
                SuperclassUsr = node.superclassUsr,
                SuperclassNames = node.superclassNames?.ToList() ?? new List<string>(),
                InheritsConvenienceInitializers = node.inheritsConvenienceInitializers ?? false,
                HasMissingDesignatedInitializers = node.hasMissingDesignatedInitializers ?? false,
            };
            if (!decl.IsModuleInternal)
                decl.IsModuleInternal = IsInternalFromPublicTypeNames(decl);
            if (!decl.IsModuleInternal && IsTypeUnavailableFromSwiftInterface(decl))
                decl.IsModuleInternal = true;
            ApplyActorIsolation(decl);
            ApplyAvailability(decl);
            PopulateDocumentation(decl, node);
            return decl;
        }

        /// <summary>
        /// Creates a protocol declaration from a node.
        /// </summary>
        /// <param name="node">The node representing the protocol declaration.</param>
        /// <param name="parentDecl">The parent declaration.</param>
        /// <param name="moduleDecl">The module declaration.</param>
        /// <returns>The protocol declaration.</returns>
        private ProtocolDecl CreateProtocolDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl, string? moduleNameOverride = null)
        {
            // Parse associated types from children
            var associatedTypes = new List<AssociatedTypeDecl>();
            foreach (var child in node.Children)
            {
                if (child.DeclKind == "AssociatedType")
                {
                    associatedTypes.Add(new AssociatedTypeDecl
                    {
                        Name = child.Name
                    });
                }
            }

            // Parse inherited protocols from conformances.
            // Protocol conformance entries use Kind == "Conformance" (not "TypeNominal"),
            // so we must accept both kinds. Without this, InheritedProtocols would always
            // be empty for protocols, breaking InheritsCodable, IsClassBoundProtocol,
            // InheritsCaseIterable, and InheritsProtocolWithAssociatedTypes checks.
            // Marker protocols (Sendable, Escapable, Copyable, SendableMetatype) are
            // filtered out — they have no C# representation and would generate ISendable etc.
            var inheritedProtocols = new List<NamedTypeSpec>();
            foreach (var conformance in node.Conformances)
            {
                if (conformance.Kind == kNominal || conformance.Kind == "Conformance")
                {
                    if (string.IsNullOrEmpty(conformance.MangledName))
                        continue;
                    var reduction = demangler.Run(conformance.MangledName);
                    if (reduction is TypeSpecReduction typeSpecReduction &&
                        typeSpecReduction.TypeSpec is NamedTypeSpec namedTypeSpec)
                    {
                        // Skip compiler-internal marker protocols that have no C# binding
                        var simpleName = namedTypeSpec.NameWithoutModule;
                        if (simpleName is "Sendable" or "Escapable" or "Copyable" or "SendableMetatype")
                            continue;
                        inheritedProtocols.Add(namedTypeSpec);
                    }
                }
            }

            // Check for Self requirement in the generic signature
            bool hasSelfRequirement = node.GenericSig?.Contains("Self") == true;

            // Check if class-bound (requires AnyObject).
            // AnyObject may appear in conformances OR in the generic signature
            // (e.g. "<τ_0_0 : AnyObject>" for protocols declared as ": AnyObject").
            // The genericSig check must be precise: only match when Self (τ_0_0) directly
            // conforms to AnyObject, NOT when an associated type does (e.g. "τ_0_0.Element : AnyObject").
            // τ_0_0\s*: matches "τ_0_0 :" but not "τ_0_0.Element :" (dot breaks the \s* match).
            bool isClassBound = inheritedProtocols.Any(p =>
                p.Name == "AnyObject" ||
                p.Name == "Swift.AnyObject") ||
                (node.GenericSig != null &&
                 System.Text.RegularExpressions.Regex.IsMatch(node.GenericSig, @"τ_0_0\s*:[^,]*\bAnyObject\b"));

            var decl = new ProtocolDecl
            {
                Name = ExtractUniqueName(node.Name),
                SwiftTypeName = GetSwiftTypeName(parentDecl, node.Name, moduleNameOverride),
                MangledName = node.MangledName,
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Types = new List<TypeDecl>(),
                Operators = new List<OperatorDecl>(),
                AssociatedTypes = associatedTypes,
                HasSelfRequirement = hasSelfRequirement,
                InheritedProtocols = inheritedProtocols,
                GenericSignature = node.GenericSig,
                IsClassBound = isClassBound,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                IsModuleInternal = IsNodeModuleInternal(node),
                IsSpiProtected = IsNodeSpiProtected(node)
            };
            if (!decl.IsModuleInternal)
                decl.IsModuleInternal = IsInternalFromPublicTypeNames(decl);
            if (!decl.IsModuleInternal && IsTypeUnavailableFromSwiftInterface(decl))
                decl.IsModuleInternal = true;
            ApplyActorIsolation(decl);
            ApplyAvailability(decl);
            PopulateDocumentation(decl, node);

            // Mark protocols whose methods have @convention(c)/@convention(block) closure parameters
            if (_conventionCProtocols != null && _conventionCProtocols.Contains(decl.Name))
                decl.HasConventionCClosureParameters = true;

            return decl;
        }

        /// <summary>
        /// Creates a method declaration from a node.
        /// </summary>
        /// <param name="node">The node representing the method declaration.</param>
        /// <param name="parentDecl">The parent declaration.</param>
        /// <param name="moduleDecl">The module declaration.</param>
        /// <returns>The method declaration.</returns>
        private MethodDecl CreateMethodDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            // Extract parameter names from the signature
            var paramNames = ExtractParameterNames(node.PrintedName);
            string mangledName = node.Kind == "Constructor" ? PatchMangledName(node.MangledName) : node.MangledName;

            IReduction? reduction = null;
            try
            {
                reduction = demangler.Run(mangledName);
            }
            catch (Exception e)
            {
                _logger.LogWarning($"Demangling failed for '{node.Name}' ({mangledName}): {e.Message}");
            }
            FunctionReduction? functionReduction = reduction as FunctionReduction;

            // Detect failable initializer: init? returns Optional<Self>
            // The first child of a Constructor node is the return type.
            // For init?, it will have name == "Optional".
            bool isFailable = node.Kind == "Constructor" &&
                node.Children.Any() &&
                node.Children.First().Name == "Optional";

            var (methodCSharpName, methodOriginalSwiftName) = ExtractUniqueNameWithOriginal(node.Name);
            var methodDecl = new MethodDecl
            {
                Name = methodCSharpName,
                OriginalSwiftName = methodOriginalSwiftName,
                // Constructors for structs are named with a trailing 'C' instead of 'c'
                // because a constructor wrapper is missing in the library.
                MangledName = mangledName,
                MethodType = node.@static ?? false ? MethodType.Static : MethodType.Instance,
                IsConstructor = node.Kind == "Constructor",
                IsFailable = isFailable,
                CSSignature = new List<ArgumentDecl>(),
                GenericParameters = GenericSignatureParser.ParseGenericSignature(node.GenericSig, node.sugared_genericSig),
                RawGenericSig = node.GenericSig,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                Throws = node.throwing ?? false,
                // Primary source: demangler's FunctionReduction. Fallback: check mangled name
                // for the "Ya" (async) marker when the demangler doesn't produce a FunctionReduction.
                IsAsync = functionReduction?.Function?.IsAsync
                    ?? DetectAsyncFromMangledName(mangledName),
                Visibility = Visibility.Public,
                IsMutating = node.funcSelfKind == "Mutating",
                IsFinal = node.DeclAttributes?.Contains("Final") == true,
                IsOverride = node.overriding == true || node.DeclAttributes?.Contains("Override") == true,
                IsImplicit = node.@implicit == true,
                IsModuleInternal = IsNodeModuleInternal(node) ||
                    IsInternalFromSwiftInterface(parentDecl.Name, node.PrintedName),
                IsSpiProtected = IsNodeSpiProtected(node),
                IsObjCOptional = node.DeclAttributes?.Contains("Optional") == true,
                IsExtensionMethod = node.isFromExtension == true,
            };

            // Suppress underscore-prefixed methods without explicit AccessControl.
            // Swift convention: _-prefixed members are internal. The ABI JSON includes them
            // for binary compatibility but they're not callable from external code.
            // Only suppress if no AccessControl attribute (explicitly public _-prefixed APIs
            // like _NIOFileSystem get AccessControl and should be preserved).
            if (!methodDecl.IsModuleInternal && node.Name.StartsWith("_") &&
                (node.DeclAttributes is null || Array.IndexOf(node.DeclAttributes, "AccessControl") == -1))
            {
                methodDecl.IsModuleInternal = true;
            }

            // Suppress unconditionally unavailable methods
            if (!methodDecl.IsModuleInternal && parentDecl is TypeDecl parentTypeForUnavail &&
                IsUnavailableFromSwiftInterface(parentTypeForUnavail, node.PrintedName))
            {
                methodDecl.IsModuleInternal = true;
            }

            // Negative-space detection: if the member is NOT in the public swiftinterface,
            // it's internal. Skip implicit NON-CONSTRUCTOR members (synthesized accessors, etc.)
            // which are public but don't appear in the swiftinterface.
            // Constructors are NOT skipped even if implicit: implicit inits on types with
            // @_hasMissingDesignatedInitializers are internal and MUST be caught.
            // Public implicit inits DO appear in the public swiftinterface (memberwise inits, etc.).
            if (!methodDecl.IsModuleInternal &&
                (node.@implicit != true || methodDecl.IsConstructor))
            {
                if (IsInternalFromPublicMemberNames(parentDecl, node.PrintedName))
                    methodDecl.IsModuleInternal = true;
            }

            // Suppress implicit inherited constructors that are not callable from external code.
            // Swift's initialization safety rules: when a class defines its own designated inits
            // (inheritsConvenienceInitializers=false) and all designated inits are visible
            // (hasMissingDesignatedInitializers=false), implicit inherited constructors from
            // the superclass are NOT available. Emitting wrappers for them causes compilation errors
            // like "missing argument for parameter 'name'" because the implicit init doesn't exist.
            if (!methodDecl.IsModuleInternal && methodDecl.IsImplicit && methodDecl.IsOverride &&
                methodDecl.IsConstructor && parentDecl is ClassDecl classParentForImplicit &&
                !classParentForImplicit.InheritsConvenienceInitializers &&
                !classParentForImplicit.HasMissingDesignatedInitializers)
            {
                methodDecl.IsModuleInternal = true;
            }

            // Look up typed throws error type from swiftinterface data
            if (methodDecl.Throws && _typedThrowsErrors != null)
            {
                // Try type-scoped key first (e.g., "TypedThrowingParser.parse(_:)")
                var throwsScopedKey = $"{parentDecl.Name}.{node.PrintedName}";
                if (!_typedThrowsErrors.TryGetValue(throwsScopedKey, out var errorTypeName))
                {
                    // Try module-level key (free functions, e.g., "parseNumber(_:)")
                    _typedThrowsErrors.TryGetValue(node.PrintedName, out errorTypeName);
                }

                if (errorTypeName != null)
                {
                    methodDecl.ThrownErrorType = TypeSpecParser.Parse(errorTypeName);
                }
            }

            // Apply member-level actor isolation from swiftinterface data
            if (parentDecl is TypeDecl parentType)
            {
                ApplyMemberActorIsolation(methodDecl, parentType, node.PrintedName);
                ApplyMemberAvailability(methodDecl, parentType, node.PrintedName);
            }
            else if (parentDecl is ModuleDecl)
            {
                // Free functions: check if the function itself is actor-isolated.
                // The actorIsolatedMembers set uses bare printedName for free functions.
                if (_actorIsolatedMembers != null && _actorIsolatedMembers.Contains(node.PrintedName))
                    methodDecl.IsActorIsolated = true;
                if (_mainActorIsolatedMembers != null && _mainActorIsolatedMembers.Contains(node.PrintedName))
                    methodDecl.IsMainActorIsolated = true;
                // Free function availability: keyed by bare printedName in swiftinterface
                if (_availabilityAnnotations != null &&
                    _availabilityAnnotations.TryGetValue(node.PrintedName, out var freeFuncAnnotations))
                    methodDecl.AvailabilityAnnotations = freeFuncAnnotations;
            }

            PopulateDocumentation(methodDecl, node);

            // Look up internal parameter names from swiftinterface data
            List<string>? internalParamNames = null;
            if (_parameterNames != null)
            {
                // Try type-scoped key first (e.g., "Dog.speak(_:_:)")
                var scopedKey = $"{parentDecl.Name}.{node.PrintedName}";
                if (!_parameterNames.TryGetValue(scopedKey, out internalParamNames))
                {
                    // Try module-level key (free functions, e.g., "sumTwo(_:_:)")
                    _parameterNames.TryGetValue(node.PrintedName, out internalParamNames);
                }
            }

            for (int i = 0; i < node.Children.Count(); i++)
            {
                var typeSpec = CreateTypeSpec(node.Children.ElementAt(i));

                var childNode = node.Children.ElementAt(i);

                // Populate PrivateName from swiftinterface data.
                // i=0 is the return type in paramNames (no corresponding internal name).
                // i>=1 are actual parameters; internalParamNames index is (i-1).
                var privateName = string.Empty;
                if (internalParamNames != null && i >= 1 && (i - 1) < internalParamNames.Count)
                {
                    privateName = internalParamNames[i - 1];
                }

                methodDecl.CSSignature.Add(new ArgumentDecl
                {
                    SwiftTypeSpec = typeSpec,
                    Name = paramNames[i],
                    PrivateName = privateName,
                    IsInOut = childNode.paramValueOwnership == "InOut",
                    IsGeneric = childNode.Name == "GenericTypeParam",
                    HasDefaultArg = childNode.hasDefaultArg == true,
                    ParentDecl = methodDecl,
                    ModuleDecl = moduleDecl
                });
            }

            // Detect variadic parameters from swiftinterface data.
            // Swift variadic params (T...) appear as Array<T> in ABI JSON, making them
            // indistinguishable from regular array params. The swiftinterface shows the actual
            // "..." syntax. @_cdecl wrappers can't call variadic methods correctly — passing
            // [T] where T... is expected causes a compilation error.
            // Primary source: swiftinterface _variadicMembers set.
            // Fallback: demangler's FunctionReduction (when the demangler succeeds).
            if (_variadicMembers != null)
            {
                // Use BuildTypeQualifiedPath for nested types (e.g., "DisposeBag.DisposableBuilder")
                var varScopedKey = parentDecl is TypeDecl varParentType
                    ? $"{BuildTypeQualifiedPath(varParentType)}.{node.PrintedName}"
                    : node.PrintedName;
                if (_variadicMembers.Contains(varScopedKey) || _variadicMembers.Contains(node.PrintedName))
                {
                    methodDecl.HasVariadicParameter = true;
                }
            }
            if (!methodDecl.HasVariadicParameter &&
                functionReduction?.Function?.ParameterList is TupleTypeSpec paramTuple)
            {
                methodDecl.HasVariadicParameter = HasVariadicElement(paramTuple);
            }

            // Apply default parameter value expressions from swiftinterface data.
            // Must happen after the argument-construction loop since it mutates CSSignature entries.
            if (parentDecl is TypeDecl parentTypeForDefaults)
            {
                ApplyMemberDefaultValues(methodDecl, parentTypeForDefaults, node.PrintedName);
                ApplyMemberAutoclosureFlags(methodDecl, parentTypeForDefaults, node.PrintedName);
            }
            else
            {
                ApplyFreeFunctionDefaultValues(methodDecl, node.PrintedName);
                ApplyFreeFunctionAutoclosureFlags(methodDecl, node.PrintedName);
            }

            return methodDecl;
        }

        /// <summary>
        /// Fallback async detection from the mangled name when the demangler
        /// doesn't produce a FunctionReduction (e.g. for some constructors).
        /// Checks for the "Ya" async marker in Swift's mangling scheme.
        /// To avoid false positives from identifiers containing "Ya" (e.g. "Yak"),
        /// we require "Ya" to NOT be preceded by a digit (identifier length prefix).
        /// </summary>
        internal static bool DetectAsyncFromMangledName(string mangledName)
        {
            int idx = 0;
            while ((idx = mangledName.IndexOf("Ya", idx, StringComparison.Ordinal)) >= 0)
            {
                // If preceded by a digit, it's part of an identifier (e.g. "3Yak") — skip
                if (idx > 0 && char.IsAsciiDigit(mangledName[idx - 1]))
                {
                    idx += 2;
                    continue;
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Checks whether any element in a demangled parameter list is a variadic parameter.
        /// Variadic params (T...) are demangled as Array&lt;T&gt; where the inner T has IsVariadic=true.
        /// </summary>
        internal static bool HasVariadicElement(TupleTypeSpec paramTuple)
        {
            foreach (var element in paramTuple.Elements)
            {
                if (element is NamedTypeSpec named &&
                    (named.Name == "Swift.Array" || named.Name == "Array") &&
                    named.GenericParameters.Count > 0 &&
                    named.GenericParameters[0].IsVariadic)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Creates an operator declaration from a node.
        /// </summary>
        /// <param name="node">The node representing the operator declaration.</param>
        /// <param name="parentDecl">The parent declaration.</param>
        /// <param name="moduleDecl">The module declaration.</param>
        /// <returns>The operator declaration, or null if the underlying method cannot be created.</returns>
        private OperatorDecl? CreateOperatorDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            var methodDecl = CreateMethodDecl(node, parentDecl, moduleDecl);
            if (methodDecl == null) return null;

            // CSSignature[0] is return type, remaining are parameters
            // For operators, Swift static func operators have the operands as parameters
            var paramCount = methodDecl.CSSignature.Count - 1;
            var isUnary = paramCount == 1;

            // Detect prefix vs postfix for unary operators
            // Swift prefix operators have 'prefix' in DeclAttributes
            bool isPrefix = true; // Default to prefix for unary operators
            if (node.DeclAttributes is not null && Array.IndexOf(node.DeclAttributes, "postfix") != -1)
            {
                isPrefix = false;
            }

            var operatorDecl = new OperatorDecl
            {
                Name = node.Name,
                OperatorSymbol = node.Name,
                Kind = isUnary ? OperatorKind.Unary : OperatorKind.Binary,
                IsPrefix = isPrefix,
                UnderlyingMethod = methodDecl,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                AvailabilityAnnotations = methodDecl.AvailabilityAnnotations
            };
            PopulateDocumentation(operatorDecl, node);
            return operatorDecl;
        }

        private List<AccessorDecl> HandleAccessors(IEnumerable<Node> accessors, string fieldName, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            var result = new List<AccessorDecl>();

            // Sanitize property wrapper projected value names ($volume -> projectedVolume)
            // The $ prefix is valid in Swift but not in C# identifiers
            var sanitizedFieldName = NameProvider.SanitizePropertyWrapperName(fieldName);

            foreach (var accessor in accessors)
            {
                switch (accessor.AccessorKind)
                {
                    case "get":
                        result.Add(CreateGetAccessor(accessor, sanitizedFieldName, parentDecl, moduleDecl));
                        break;
                    case "set":
                        result.Add(CreateSetAccessor(accessor, sanitizedFieldName, parentDecl, moduleDecl));
                        break;
                    case "_modify":
                        // Optimization accessor, not needed for correctness
                        break;
                    default:
                        _logger.LogWarning($"Unsupported accessor kind '{accessor.AccessorKind}' encountered.");
                        break;
                }
            }

            return result;
        }

        private GetAccessorDecl CreateGetAccessor(Node accessor, string fieldName, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            // Detect async getters by checking if the TBD contains the "Tu" (async function pointer) suffix
            // for this accessor's mangled name. The ABI JSON doesn't mark accessors as async directly.
            // For class properties, the exported symbol uses a dispatch thunk (Tj suffix), so the async
            // marker appears as "TjTu" rather than bare "Tu". Check both variants.
            var isAsync = _demangledTbd.AllSymbols.Contains(accessor.MangledName + "Tu")
                || _demangledTbd.AllSymbols.Contains(accessor.MangledName + "TjTu");

            // Build generic parameters for the accessor method.
            // If the accessor has its own GenericSig, parse it. Otherwise, if the parent type is generic,
            // copy the type's generic parameters so the accessor method has the correct generic context.
            var genericParameters = new List<GenericArgumentDecl>();
            if (!string.IsNullOrEmpty(accessor.GenericSig))
            {
                genericParameters = GenericSignatureParser.ParseGenericSignature(accessor.GenericSig, accessor.sugared_genericSig);
            }
            else if (parentDecl is TypeDecl typeDecl && typeDecl.IsGeneric)
            {
                genericParameters = new List<GenericArgumentDecl>(typeDecl.GenericParameters);
            }

            var returnTypeSpec = CreateTypeSpec(accessor.Children.ElementAt(0));

            var methodDecl = new MethodDecl
            {
                Name = $"{fieldName}_Get",
                MangledName = accessor.MangledName,
                MethodType = accessor.@static ?? false ? MethodType.Static : MethodType.Instance,
                IsConstructor = false,
                CSSignature = new List<ArgumentDecl>
                {
                    new ArgumentDecl
                    {
                        SwiftTypeSpec = returnTypeSpec,
                        Name = string.Empty,
                        PrivateName = string.Empty,
                        IsInOut = false,
                        IsGeneric = TypeSpecHelpers.IsGenericTypeParameter(returnTypeSpec),
                        ParentDecl = parentDecl,
                        ModuleDecl = moduleDecl
                    }
                },
                GenericParameters = genericParameters,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                Throws = accessor.throwing ?? false,
                IsAsync = isAsync,
                Visibility = Visibility.Private,
                IsFinal = accessor.DeclAttributes?.Contains("Final") == true,
            };

            // Apply member-level actor isolation to accessor methods
            if (parentDecl is TypeDecl getParentType)
                ApplyMemberActorIsolation(methodDecl, getParentType, fieldName);

            return new GetAccessorDecl { Method = methodDecl };
        }

        private SetAccessorDecl CreateSetAccessor(Node accessor, string fieldName, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            // Build generic parameters for the accessor method (same logic as CreateGetAccessor).
            var genericParameters = new List<GenericArgumentDecl>();
            if (!string.IsNullOrEmpty(accessor.GenericSig))
            {
                genericParameters = GenericSignatureParser.ParseGenericSignature(accessor.GenericSig, accessor.sugared_genericSig);
            }
            else if (parentDecl is TypeDecl typeDecl && typeDecl.IsGeneric)
            {
                genericParameters = new List<GenericArgumentDecl>(typeDecl.GenericParameters);
            }

            // The setter has two children:
            // - Index 0: Void (return type)
            // - Index 1: The parameter type (value to set)
            var valueTypeSpec = CreateTypeSpec(accessor.Children.ElementAt(1));

            var methodDecl = new MethodDecl
            {
                Name = $"{fieldName}_Set",
                MangledName = accessor.MangledName,
                MethodType = accessor.@static ?? false ? MethodType.Static : MethodType.Instance,
                IsConstructor = false,
                CSSignature = new List<ArgumentDecl>
                {
                    // Return type (void for setters - empty tuple)
                    new ArgumentDecl
                    {
                        SwiftTypeSpec = TupleTypeSpec.Empty,
                        Name = string.Empty,
                        PrivateName = string.Empty,
                        IsInOut = false,
                        IsGeneric = false,
                        ParentDecl = parentDecl,
                        ModuleDecl = moduleDecl
                    },
                    // Parameter (value) - at index 1, after the void return type
                    new ArgumentDecl
                    {
                        SwiftTypeSpec = valueTypeSpec,
                        Name = "value",
                        PrivateName = string.Empty,
                        IsInOut = false,
                        IsGeneric = TypeSpecHelpers.IsGenericTypeParameter(valueTypeSpec),
                        ParentDecl = parentDecl,
                        ModuleDecl = moduleDecl
                    }
                },
                GenericParameters = genericParameters,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                Throws = false,
                IsAsync = false,
                Visibility = Visibility.Private,
                IsFinal = accessor.DeclAttributes?.Contains("Final") == true,
            };

            // Apply member-level actor isolation to accessor methods
            if (parentDecl is TypeDecl setParentType)
                ApplyMemberActorIsolation(methodDecl, setParentType, fieldName);

            return new SetAccessorDecl { Method = methodDecl };
        }

        private PropertyDecl CreatePropertyDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            var typeSpec = CreateTypeSpec(node.Children.ElementAt(0));

            // Strip Swift backtick escaping (e.g., `subscript` → subscript).
            // Methods already do this via ExtractUniqueNameWithOriginal.
            var rawName = node.Name;
            if (rawName.Length >= 2 && rawName[0] == '`' && rawName[rawName.Length - 1] == '`')
                rawName = rawName.Substring(1, rawName.Length - 2);

            // Sanitize property wrapper projected value names ($volume -> projectedVolume)
            var sanitizedName = NameProvider.SanitizePropertyWrapperName(rawName);

            var decl = new PropertyDecl
            {
                SwiftTypeSpec = typeSpec,
                Name = sanitizedName,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                IsStatic = node.@static ?? false,
                HasStorage = node.DeclAttributes is not null && Array.IndexOf(node.DeclAttributes, "HasStorage") != -1,
                IsOverride = node.overriding == true || node.DeclAttributes?.Contains("Override") == true,
                IsFinal = node.DeclAttributes?.Contains("Final") == true,
                IsSpiProtected = IsNodeSpiProtected(node),
                IsModuleInternal = IsNodeModuleInternal(node),
                IsObjCOptional = node.DeclAttributes?.Contains("Optional") == true,
                Accessors = HandleAccessors(node.Accessors, sanitizedName, parentDecl, moduleDecl)
            };
            // Propagate extension flag to accessor MethodDecls. Extension methods use static
            // dispatch — accessor P/Invokes must not get Tj dispatch thunk suffix.
            if (node.isFromExtension == true)
            {
                foreach (var accessor in decl.Accessors)
                    accessor.Method.IsExtensionMethod = true;
            }

            // Suppress underscore-prefixed properties without explicit AccessControl.
            if (!decl.IsModuleInternal && rawName.StartsWith("_") &&
                (node.DeclAttributes is null || Array.IndexOf(node.DeclAttributes, "AccessControl") == -1))
            {
                decl.IsModuleInternal = true;
            }

            // Cross-reference with swiftinterface: if property doesn't appear in the public
            // interface, it's internal even if not explicitly flagged in the ABI JSON.
            // Uses unqualified Name (consistent with method path at CreateMethodDecl and with
            // swiftinterface key format — see IsInternalFromSwiftInterface doc comment).
            if (!decl.IsModuleInternal && parentDecl is TypeDecl propParentForInternal)
            {
                decl.IsModuleInternal = IsInternalFromSwiftInterface(propParentForInternal.Name, sanitizedName);
            }
            // Negative-space detection: property not in public swiftinterface is internal.
            if (!decl.IsModuleInternal)
            {
                decl.IsModuleInternal = IsInternalFromPublicMemberNames(parentDecl, sanitizedName);
            }
            if (parentDecl is TypeDecl propParentType)
            {
                ApplyPropertyActorIsolation(decl, propParentType);
                ApplyMemberAvailability(decl, propParentType, sanitizedName);
            }
            PopulateDocumentation(decl, node);
            return decl;
        }

        /// <summary>
        /// Creates a subscript declaration from a node.
        /// Subscripts have children where:
        /// - Child[0] is the return type
        /// - Child[1..n] are the index parameters
        /// </summary>
        /// <param name="node">The node representing the subscript declaration.</param>
        /// <param name="parentDecl">The parent declaration.</param>
        /// <param name="moduleDecl">The module declaration.</param>
        /// <returns>The subscript declaration.</returns>
        private SubscriptDecl CreateSubscriptDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            var children = node.Children.ToList();
            if (children.Count < 2)
            {
                throw new InvalidOperationException($"Subscript '{node.Name}' has insufficient children (expected at least 2).");
            }

            // First child is the return type
            var returnTypeSpec = CreateTypeSpec(children[0]);

            // Remaining children are index parameters
            var indexParameters = new List<ArgumentDecl>();
            var paramNames = ExtractSubscriptParameterNames(node.PrintedName);

            for (int i = 1; i < children.Count; i++)
            {
                var paramName = i - 1 < paramNames.Count ? paramNames[i - 1] : $"index{i - 1}";
                indexParameters.Add(new ArgumentDecl
                {
                    SwiftTypeSpec = CreateTypeSpec(children[i]),
                    Name = paramName,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parentDecl,
                    ModuleDecl = moduleDecl
                });
            }

            var decl = new SubscriptDecl
            {
                Name = "subscript",
                MangledName = node.MangledName,
                ReturnTypeSpec = returnTypeSpec,
                IndexParameters = indexParameters,
                IsStatic = node.@static ?? false,
                Accessors = HandleSubscriptAccessors(node.Accessors, indexParameters, returnTypeSpec, parentDecl, moduleDecl),
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            };
            if (parentDecl is TypeDecl subscriptParentType)
            {
                ApplyMemberAvailability(decl, subscriptParentType, node.PrintedName);
            }

            // Apply parameter labels from swiftinterface if available.
            // ABI JSON may not encode all label variations for subscripts (e.g., "subscript(_:)"
            // when the actual declaration is "subscript(bitAt:)"). Cross-reference labels from
            // the swiftinterface to fix the parameter names.
            if (_subscriptLabels != null && indexParameters.Count > 0 && parentDecl is TypeDecl subscriptLabelParentType)
            {
                var typePath = BuildTypeQualifiedPath(subscriptLabelParentType);

                // Try matching by ABI printed name first (may be correct for some subscripts)
                var abiKey = $"{typePath}.{node.PrintedName}";
                if (!_subscriptLabels.TryGetValue(abiKey, out var labels))
                {
                    // ABI key didn't match — search for a subscript with matching parameter count.
                    // For ambiguous cases (multiple subscripts with the same param count),
                    // we can't definitively match, so we only apply when there's exactly one match.
                    var prefix = $"{typePath}.subscript(";
                    var candidates = _subscriptLabels
                        .Where(kv => kv.Key.StartsWith(prefix) && kv.Value.Count == indexParameters.Count)
                        .ToList();

                    if (candidates.Count == 1)
                    {
                        labels = candidates[0].Value;
                    }
                }

                if (labels != null)
                {
                    for (int i = 0; i < Math.Min(labels.Count, indexParameters.Count); i++)
                    {
                        var label = labels[i];
                        if (label == "_")
                        {
                            // No argument label — force the "indexN" name pattern so
                            // FixSubscriptCallArg strips the label from bracket syntax.
                            // The ABI JSON may have a param name (e.g., "key" from subscript(key:))
                            // that looks like a label but isn't — subscripts with single-name params
                            // have no argument label in Swift.
                            if (!indexParameters[i].Name.StartsWith("index"))
                                indexParameters[i].Name = $"index{i}";
                        }
                        else
                        {
                            indexParameters[i].Name = label;
                        }
                    }
                }
            }

            // Propagate extension flag to subscript accessor MethodDecls.
            if (node.isFromExtension == true)
            {
                foreach (var accessor in decl.Accessors)
                    accessor.Method.IsExtensionMethod = true;
            }

            PopulateDocumentation(decl, node);
            return decl;
        }

        /// <summary>
        /// Extracts parameter names from a subscript's printed name.
        /// Examples: "subscript(_:)" -> ["_"], "subscript(row:column:)" -> ["row", "column"]
        /// </summary>
        private List<string> ExtractSubscriptParameterNames(string printedName)
        {
            var result = new List<string>();
            var start = printedName.IndexOf('(');
            var end = printedName.LastIndexOf(')');

            if (start < 0 || end < 0 || start >= end)
                return result;

            var paramPart = printedName.Substring(start + 1, end - start - 1);
            var paramNames = paramPart.Split(':').Where(s => !string.IsNullOrEmpty(s)).ToList();

            for (int i = 0; i < paramNames.Count; i++)
            {
                var name = paramNames[i].Trim();
                // If the parameter name is just "_", generate a unique name
                if (name == "_")
                {
                    result.Add($"index{i}");
                }
                else
                {
                    result.Add(ExtractUniqueName(name));
                }
            }

            return result;
        }

        /// <summary>
        /// Handles accessors for a subscript declaration.
        /// Similar to HandleAccessors but subscript accessors have index parameters.
        /// </summary>
        private List<AccessorDecl> HandleSubscriptAccessors(
            IEnumerable<Node> accessors,
            IReadOnlyList<ArgumentDecl> indexParameters,
            TypeSpec returnTypeSpec,
            BaseDecl parentDecl,
            ModuleDecl moduleDecl)
        {
            var result = new List<AccessorDecl>();

            foreach (var accessor in accessors)
            {
                switch (accessor.AccessorKind)
                {
                    case "get":
                        result.Add(CreateSubscriptGetAccessor(accessor, indexParameters, returnTypeSpec, parentDecl, moduleDecl));
                        break;
                    case "set":
                        result.Add(CreateSubscriptSetAccessor(accessor, indexParameters, returnTypeSpec, parentDecl, moduleDecl));
                        break;
                    case "_modify":
                    case "_read":
                        // Coroutine accessors - skip these for now
                        break;
                    default:
                        _logger.LogWarning($"Unsupported subscript accessor kind '{accessor.AccessorKind}' encountered.");
                        break;
                }
            }

            return result;
        }

        /// <summary>
        /// Creates a getter accessor for a subscript.
        /// </summary>
        private GetAccessorDecl CreateSubscriptGetAccessor(
            Node accessor,
            IReadOnlyList<ArgumentDecl> indexParameters,
            TypeSpec returnTypeSpec,
            BaseDecl parentDecl,
            ModuleDecl moduleDecl)
        {
            // Build signature: [0] = return type, [1..n] = index parameters
            var signature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = returnTypeSpec,
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parentDecl,
                    ModuleDecl = moduleDecl
                }
            };
            signature.AddRange(indexParameters);

            var methodDecl = new MethodDecl
            {
                Name = "subscript_Get",
                MangledName = accessor.MangledName,
                MethodType = accessor.@static ?? false ? MethodType.Static : MethodType.Instance,
                IsConstructor = false,
                CSSignature = signature,
                GenericParameters = new List<GenericArgumentDecl>(),
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                Throws = false,
                IsAsync = false,
                Visibility = Visibility.Private,
                IsAccessor = true,
                IsFinal = accessor.DeclAttributes?.Contains("Final") == true,
            };

            return new GetAccessorDecl { Method = methodDecl };
        }

        /// <summary>
        /// Creates a setter accessor for a subscript.
        /// </summary>
        private SetAccessorDecl CreateSubscriptSetAccessor(
            Node accessor,
            IReadOnlyList<ArgumentDecl> indexParameters,
            TypeSpec returnTypeSpec,
            BaseDecl parentDecl,
            ModuleDecl moduleDecl)
        {
            // Build signature: [0] = void (return), [1] = newValue, [2..n] = index parameters
            var signature = new List<ArgumentDecl>
            {
                // Return type (void for setters)
                new ArgumentDecl
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parentDecl,
                    ModuleDecl = moduleDecl
                },
                // The new value parameter
                new ArgumentDecl
                {
                    SwiftTypeSpec = returnTypeSpec,
                    Name = "newValue",
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parentDecl,
                    ModuleDecl = moduleDecl
                }
            };
            signature.AddRange(indexParameters);

            var methodDecl = new MethodDecl
            {
                Name = "subscript_Set",
                MangledName = accessor.MangledName,
                MethodType = accessor.@static ?? false ? MethodType.Static : MethodType.Instance,
                IsConstructor = false,
                CSSignature = signature,
                GenericParameters = new List<GenericArgumentDecl>(),
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                Throws = false,
                IsAsync = false,
                Visibility = Visibility.Private,
                IsAccessor = true,
                IsFinal = accessor.DeclAttributes?.Contains("Final") == true,
            };

            return new SetAccessorDecl { Method = methodDecl };
        }

        /// <summary>
        /// Creates a type spec from a given node parsing the printed name
        /// </summary>
        internal TypeSpec CreateTypeSpec(Node node)
        {
            switch (node.Kind)
            {
                case kNominal:
                case kFunc:
                    // Handle ProtocolComposition (existential types like 'Any', 'any P1 & P2')
                    // In ABI JSON, 'Any' appears as TypeNominal with name="ProtocolComposition", printedName="Any"
                    if (node.Name == "ProtocolComposition")
                    {
                        return CreateProtocolCompositionTypeSpec(node);
                    }
                    // Handle OpaqueTypeArchetype (opaque return types like 'some Protocol')
                    // In ABI JSON, these appear as TypeNominal with name="OpaqueTypeArchetype",
                    // printedName="some ModuleName.ProtocolName", with children listing the protocol constraints.
                    // We represent them as a ProtocolListTypeSpec with IsOpaque=true, and generate a Swift
                    // wrapper that boxes the concrete return value into an existential container (any Protocol).
                    if (node.Name == "OpaqueTypeArchetype")
                    {
                        return CreateOpaqueReturnTypeSpec(node);
                    }
                    // Handle DependentMember (associated type references like "τ_0_0.Element").
                    // In ABI JSON, these appear as TypeNominal with name="DependentMember",
                    // so they match the kNominal case — the separate case "DependentMember"
                    // branch below was dead code.
                    if (node.Name == "DependentMember")
                    {
                        return new AssociatedTypeReferenceSpec(node.PrintedName);
                    }
                    var spec = TypeSpecParser.Parse(node.PrintedName);
                    if (spec is null)
                    {
                        throw new Exception($"Error parsing type from \"{node.PrintedName}\"");
                    }
                    // Propagate escaping attribute from ABI JSON typeAttributes.
                    // Swift public API convention: closures are @escaping unless
                    // explicitly marked noescape. TypeSpecParser doesn't parse
                    // @escaping from PrintedName, so we set it from ABI data.
                    if (spec is ClosureTypeSpec closureSpec)
                    {
                        bool isNoescape = node.typeAttributes?.Contains("noescape") == true;
                        if (!isNoescape && !closureSpec.IsEscaping)
                        {
                            closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
                        }
                    }
                    return spec;
                case kGenericTypeParam:
                    // Generic type parameter - parse the PrintedName (e.g., "T", "τ_0_0")
                    // which will create a NamedTypeSpec that can be matched in GenericTypeMapping
                    var genericSpec = TypeSpecParser.Parse(node.PrintedName);
                    if (genericSpec is null)
                    {
                        throw new Exception($"Error parsing generic type param from \"{node.PrintedName}\"");
                    }
                    return genericSpec;
                default:
                    throw new NotImplementedException($"Can't handle node type {node.Kind} yet.");
            }
        }

        /// <summary>
        /// Creates a ProtocolListTypeSpec from a ProtocolComposition node.
        /// The node's children represent the protocols in the composition.
        /// An empty composition (no children with printedName "Any") represents 'Any'.
        /// In practice, ABI JSON ProtocolComposition nodes have no children —
        /// the protocol list is encoded in the printedName (e.g., "any CryptoSwift.Cryptor &amp; CryptoSwift.Updatable").
        /// </summary>
        private TypeSpec CreateProtocolCompositionTypeSpec(Node node)
        {
            var protocols = new List<NamedTypeSpec>();
            foreach (var child in node.Children)
            {
                if (child.Kind == kNominal)
                {
                    // Parse the protocol name
                    var childSpec = TypeSpecParser.Parse(child.PrintedName) as NamedTypeSpec;
                    if (childSpec != null)
                    {
                        protocols.Add(childSpec);
                    }
                }
            }

            // ABI JSON ProtocolComposition nodes typically have no children.
            // The protocol list is encoded in printedName: "any P1 & P2" or just "Any".
            if (protocols.Count == 0 && !string.IsNullOrEmpty(node.PrintedName))
            {
                var printedName = node.PrintedName;
                if (printedName.StartsWith("any "))
                    printedName = printedName.Substring(4);

                if (printedName != "Any")
                {
                    var parts = printedName.Split(new[] { " & " }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        var spec = TypeSpecParser.Parse(part.Trim()) as NamedTypeSpec;
                        if (spec != null)
                        {
                            protocols.Add(spec);
                        }
                    }
                }
            }

            return new ProtocolListTypeSpec(protocols);
        }

        /// <summary>
        /// Creates a ProtocolListTypeSpec from an OpaqueTypeArchetype node (some Protocol).
        /// The node's children represent the protocol constraints of the opaque return type.
        /// Marked as IsOpaque=true to indicate a Swift wrapper is needed.
        /// </summary>
        private TypeSpec CreateOpaqueReturnTypeSpec(Node node)
        {
            var protocols = new List<NamedTypeSpec>();
            foreach (var child in node.Children)
            {
                if (child.Kind == kNominal)
                {
                    var childSpec = TypeSpecParser.Parse(child.PrintedName) as NamedTypeSpec;
                    if (childSpec != null)
                    {
                        protocols.Add(childSpec);
                    }
                }
            }
            return new ProtocolListTypeSpec(protocols) { IsOpaque = true };
        }

        /// <summary>
        /// Extracts and processes parameter names from a method signature.
        /// </summary>
        /// <param name="signature">The method signature string.</param>
        /// <returns>A list of processed parameter names.</returns>
        private List<string> ExtractParameterNames(string signature)
        {
            // Split the signature to get parameter names part and process it.
            var paramNames = signature.Split('(', ')')[1]
                                    .Split(new[] { ":" }, StringSplitOptions.RemoveEmptyEntries)
                                    .ToList();

            for (int i = 0; i < paramNames.Count; i++)
            {
                paramNames[i] = ExtractUniqueName(paramNames[i]);
                // If the parameter name is just "_", generate a unique generic name
                if (paramNames[i] == "_")
                {
                    paramNames[i] = $"arg{i}";
                }
            }

            // Return type is the first element in the signature
            paramNames.Insert(0, string.Empty);

            return paramNames;
        }

        /// <summary>
        /// Check if the name is a keyword and prefix it with "_".
        /// Returns a tuple: (csharpSafeName, originalSwiftName).
        /// originalSwiftName is non-null only when the name was modified (C# keyword prefix added).
        /// </summary>
        private static (string CSharpName, string? OriginalSwiftName) ExtractUniqueNameWithOriginal(string name)
        {
            // Strip Swift backtick escaping (e.g., `default` → default).
            // Backticks are used in Swift to escape keywords as identifiers;
            // they are not part of the identifier itself.
            if (name.Length >= 2 && name[0] == '`' && name[name.Length - 1] == '`')
                name = name.Substring(1, name.Length - 2);

            if (SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None)
            {
                return ($"_{name}", name);
            }

            return (name, null);
        }

        /// <summary>
        /// Check if the name is a keyword and prefix it with "_".
        /// </summary>
        /// <param name="name">The name to check.</param>
        /// <returns>The processed name.</returns>
        private static string ExtractUniqueName(string name)
        {
            return ExtractUniqueNameWithOriginal(name).CSharpName;
        }

        /// <summary>
        /// Populates the Documentation property on a declaration from the symbol graph, if available.
        /// Join key: node.usr matches symbol identifier.precise.
        /// </summary>
        private void PopulateDocumentation(BaseDecl decl, Node node)
        {
            if (_docComments != null && node.usr != null && _docComments.TryGetValue(node.usr, out var doc))
            {
                decl.Documentation = doc;
            }
        }

        private static SwiftTypeName GetSwiftTypeName(BaseDecl parentDecl, string name, string? moduleNameOverride = null)
            => parentDecl switch
            {
                ModuleDecl moduleDecl => SwiftTypeName.FromModuleQualifiedName($"{moduleNameOverride ?? moduleDecl.Name}.{name}"),
                TypeDecl typeDecl => SwiftTypeName.FromModuleQualifiedName($"{typeDecl.SwiftTypeName.ModuleQualifiedName}.{name}"),
                _ => throw new InvalidOperationException("Parent declaration is not a module or type.")
            };

        /// <summary>
        /// Check if the name is an operator.
        /// </summary>
        /// <param name="name">The name to check.</param>
        /// <returns>True if the name is an operator, false otherwise.</returns>
        private static bool IsOperator(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            return name.All(c => _operatorChars.Contains(c));
        }

        /// <summary>
        /// Patches the mangled name of a constructor.
        /// </summary>
        /// <param name="mangledName">The mangled name to patch.</param>
        /// <returns>The patched mangled name.</returns>
        private string PatchMangledName(string mangledName)
        {
            if (mangledName.Last() == 'c')
            {
                return mangledName.Substring(0, mangledName.Length - 1) + "C";
            }
            return mangledName;
        }
    }
}
