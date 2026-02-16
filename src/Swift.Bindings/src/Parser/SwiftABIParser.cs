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
        public string? funcSelfKind { get; set; }
        public string? usr { get; set; }
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

        /// <summary>
        /// The set of operators.
        /// </summary>
        private static readonly HashSet<string> _operators = new()
        {
            // Arithmetic
            "+", "-", "*", "/", "%",
            // Relational
            "<", ">", "<=", ">=", "==", "!=",
            // Logical
            "&&", "||", "!",
            // Bitwise
            "&", "|", "^", "~", "<<", ">>",
            // Overflow (Swift-specific wrapping operators)
            "&+", "&-", "&*", "&<<", "&>>", "&<<=", "&>>=",
            // Assignment
            "=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>=",
            // Other
            "??", "?.", "=>"
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
        /// 4. Supplementary swiftinterface data for @inlinable internal WITH AccessControl
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

            return false;
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

        public SwiftABIParser(
            string filePath,
            ITypeDatabase typeDatabase,
            DemanglingResults demangledTbd,
            ILogger logger,
            HashSet<string>? internalMemberKeys = null,
            Dictionary<string, List<string>>? parameterNames = null,
            Dictionary<string, DocComment>? docComments = null,
            Dictionary<string, string>? typedThrowsErrors = null,
            Dictionary<string, List<string?>>? enumCaseLabels = null)
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
                _moduleTypes.Add(new NamedTypeSpec(type.SwiftTypeName.ModuleQualifiedName), type);
            }

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
            var typeName = GetSwiftTypeName(parentDecl, node.Name);
            if (_typeDatabase.IsTypeProcessed(typeName))
            {
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
                    decl = CreateStructDecl(node, parentDecl, moduleDecl, genericParameters);
                    break;

                case "Enum":
                    decl = CreateEnumDecl(node, parentDecl, moduleDecl, genericParameters);
                    break;

                case "Class":
                    decl = CreateClassDecl(node, parentDecl, moduleDecl, genericParameters);
                    break;

                case "Protocol":
                    decl = CreateProtocolDecl(node, parentDecl, moduleDecl);
                    break;

                default:
                    _logger.LogWarning($"Unsupported declaration type '{node.DeclKind} {node.Name}' encountered.");
                    return null;
            }

            if (decl is not null)
            {
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

                foreach (var type in decl.Types)
                {
                    _moduleTypes.Add(new NamedTypeSpec(type.SwiftTypeName.ModuleQualifiedName), type);
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
        private StructDecl CreateStructDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl, List<GenericArgumentDecl> genericParameters)
        {
            var swiftTypeName = GetSwiftTypeName(parentDecl, node.Name);
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
                IsModuleInternal = IsNodeModuleInternal(node)
            };
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
        private EnumDecl CreateEnumDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl, List<GenericArgumentDecl> genericParameters)
        {
            var swiftTypeName = GetSwiftTypeName(parentDecl, node.Name);
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
                IsModuleInternal = IsNodeModuleInternal(node)
            };
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
            var enumCaseDecl = new EnumCaseDecl
            {
                Name = node.Name,
                MangledName = node.MangledName,
                AssociatedValues = new List<TypeSpec>(),
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
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
                            if (assocValuesNode.Kind == kTuple)
                            {
                                // Parse tuple elements as associated values
                                foreach (var tupleElement in assocValuesNode.Children)
                                {
                                    if (tupleElement.Kind == kNominal)
                                    {
                                        var typeSpec = TypeSpecParser.Parse(tupleElement.PrintedName);
                                        if (typeSpec != null)
                                        {
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
                        if (labels[i] != null)
                        {
                            enumCaseDecl.AssociatedValues[i].TypeLabel = labels[i];
                        }
                    }
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
        private ClassDecl CreateClassDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl, List<GenericArgumentDecl> genericParameters)
        {
            var swiftTypeName = GetSwiftTypeName(parentDecl, node.Name);

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
                IsModuleInternal = IsNodeModuleInternal(node)
            };
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
        private ProtocolDecl CreateProtocolDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl)
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

            // Parse inherited protocols from conformances
            var inheritedProtocols = new List<NamedTypeSpec>();
            foreach (var conformance in node.Conformances)
            {
                if (conformance.Kind == kNominal)
                {
                    var reduction = demangler.Run(conformance.MangledName);
                    if (reduction is TypeSpecReduction typeSpecReduction &&
                        typeSpecReduction.TypeSpec is NamedTypeSpec namedTypeSpec)
                    {
                        inheritedProtocols.Add(namedTypeSpec);
                    }
                }
            }

            // Check for Self requirement in the generic signature
            bool hasSelfRequirement = node.GenericSig?.Contains("Self") == true;

            // Check if class-bound (requires AnyObject)
            bool isClassBound = inheritedProtocols.Any(p =>
                p.Name == "AnyObject" ||
                p.Name == "Swift.AnyObject");

            var decl = new ProtocolDecl
            {
                Name = ExtractUniqueName(node.Name),
                SwiftTypeName = GetSwiftTypeName(parentDecl, node.Name),
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
                ModuleDecl = moduleDecl
            };
            PopulateDocumentation(decl, node);
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

            var reduction = demangler.Run(mangledName);
            FunctionReduction? functionReduction = reduction as FunctionReduction;

            // Detect failable initializer: init? returns Optional<Self>
            // The first child of a Constructor node is the return type.
            // For init?, it will have name == "Optional".
            bool isFailable = node.Kind == "Constructor" &&
                node.Children.Any() &&
                node.Children.First().Name == "Optional";

            var methodDecl = new MethodDecl
            {
                Name = ExtractUniqueName(node.Name),
                // Constructors for structs are named with a trailing 'C' instead of 'c'
                // because a constructor wrapper is missing in the library.
                MangledName = mangledName,
                MethodType = node.@static ?? false ? MethodType.Static : MethodType.Instance,
                IsConstructor = node.Kind == "Constructor",
                IsFailable = isFailable,
                CSSignature = new List<ArgumentDecl>(),
                GenericParameters = GenericSignatureParser.ParseGenericSignature(node.GenericSig, node.sugared_genericSig),
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
                IsModuleInternal = IsNodeModuleInternal(node) ||
                    IsInternalFromSwiftInterface(parentDecl.Name, node.PrintedName),
            };

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
                ModuleDecl = moduleDecl
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
                Throws = false,
                IsAsync = isAsync,
                Visibility = Visibility.Private,
                IsFinal = accessor.DeclAttributes?.Contains("Final") == true,
            };

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

            return new SetAccessorDecl { Method = methodDecl };
        }

        private PropertyDecl CreatePropertyDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            var typeSpec = CreateTypeSpec(node.Children.ElementAt(0));

            // Sanitize property wrapper projected value names ($volume -> projectedVolume)
            var sanitizedName = NameProvider.SanitizePropertyWrapperName(node.Name);

            var decl = new PropertyDecl
            {
                SwiftTypeSpec = typeSpec,
                Name = sanitizedName,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                IsStatic = node.@static ?? false,
                HasStorage = node.DeclAttributes is not null && Array.IndexOf(node.DeclAttributes, "HasStorage") != -1,
                Accessors = HandleAccessors(node.Accessors, sanitizedName, parentDecl, moduleDecl)
            };
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
        TypeSpec CreateTypeSpec(Node node)
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
                    var spec = TypeSpecParser.Parse(node.PrintedName);
                    if (spec is null)
                    {
                        throw new Exception($"Error parsing type from \"{node.PrintedName}\"");
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
                case "DependentMember":
                    // Dependent member type - represents a reference to a protocol's associated type
                    // For example, "Self.Element" or "τ_0_0.Element"
                    return new AssociatedTypeReferenceSpec(node.PrintedName);
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
        /// </summary>
        /// <param name="name">The name to check.</param>
        /// <returns>The processed name.</returns>
        private static string ExtractUniqueName(string name)
        {
            if (SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None)
            {
                return $"_{name}";
            }

            return name;
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

        private static SwiftTypeName GetSwiftTypeName(BaseDecl parentDecl, string name)
            => parentDecl switch
            {
                ModuleDecl moduleDecl => SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
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
            return _operators.Contains(name);
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
