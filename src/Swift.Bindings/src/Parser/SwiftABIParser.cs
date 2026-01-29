// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.Demangling;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

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

        public SwiftABIParser(
            string filePath,
            ITypeDatabase typeDatabase,
            DemanglingResults demangledTbd,
            ILogger logger)
        {
            _filePath = filePath;
            _typeDatabase = typeDatabase;
            _demangledTbd = demangledTbd;
            _logger = logger;

            string jsonContent = File.ReadAllText(_filePath);
            _moduleRoot = JsonConvert.DeserializeObject<ABIRootNode>(jsonContent) ?? throw new InvalidOperationException("Invalid ABI structure.");
        }

        /// <summary>
        /// Gets the module name.
        /// </summary>
        /// <returns>The module name.</returns>
        public string GetModuleName()
        {
            return _moduleRoot.ABIRoot.Children.FirstOrDefault()?.ModuleName ?? string.Empty;
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

            // Skip generic types, except for protocols which can have generic requirements
            // Protocols with generic requirements (like 'where' clauses) should still be processed
            if (node.GenericSig is not null && node.DeclKind != "Protocol")
            {
                _logger.LogWarning($"Generic type '{node.Name}' not supported. Skipping.");
                return null;
            }

            switch (node.DeclKind)
            {
                case "Struct":
                    decl = CreateStructDecl(node, parentDecl, moduleDecl);
                    break;

                case "Enum":
                    decl = CreateEnumDecl(node, parentDecl, moduleDecl);
                    break;

                case "Class":
                    decl = CreateClassDecl(node, parentDecl, moduleDecl);
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
        /// <returns>The struct declaration.</returns>
        private StructDecl CreateStructDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            var swiftTypeName = GetSwiftTypeName(parentDecl, node.Name);
            var hasFrozenAttribute = node.DeclAttributes is not null && Array.IndexOf(node.DeclAttributes, "Frozen") != -1;

            return new StructDecl
            {
                Name = ExtractUniqueName(node.Name),
                SwiftTypeName = swiftTypeName,
                MangledName = node.MangledName,
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Types = new List<TypeDecl>(),
                Operators = new List<OperatorDecl>(),
                Conformances = [.. node.Conformances.Select(x => HandleConformance(x, swiftTypeName))],
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                IsFrozen = hasFrozenAttribute,
                MetadataAccessor = _demangledTbd.GetMetadataAccessor(swiftTypeName)
            };
        }

        /// <summary>
        /// Creates an enum declaration from a node.
        /// </summary>
        /// <param name="node">The node representing the enum declaration.</param>
        /// <param name="parentDecl">The parent declaration.</param>
        /// <param name="moduleDecl">The module declaration.</param>
        /// <returns>The enum declaration.</returns>
        private EnumDecl CreateEnumDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            var swiftTypeName = GetSwiftTypeName(parentDecl, node.Name);
            var hasFrozenAttribute = node.DeclAttributes is not null && Array.IndexOf(node.DeclAttributes, "Frozen") != -1;

            return new EnumDecl
            {
                Name = ExtractUniqueName(node.Name),
                SwiftTypeName = swiftTypeName,
                MangledName = node.MangledName,
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Types = new List<TypeDecl>(),
                Operators = new List<OperatorDecl>(),
                Cases = new List<EnumCaseDecl>(),
                Conformances = [.. node.Conformances.Select(x => HandleConformance(x, swiftTypeName))],
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                IsFrozen = hasFrozenAttribute,
                MetadataAccessor = _demangledTbd.GetMetadataAccessor(swiftTypeName)
            };
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

            return enumCaseDecl;
        }

        /// <summary>
        /// Creates a class declaration from a node.
        /// </summary>
        /// <param name="node">The node representing the class declaration.</param>
        /// <param name="parentDecl">The parent declaration.</param>
        /// <param name="moduleDecl">The module declaration.</param>
        /// <returns>The class declaration.</returns>
        private ClassDecl CreateClassDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            var swiftTypeName = GetSwiftTypeName(parentDecl, node.Name);
            return new ClassDecl
            {
                Name = ExtractUniqueName(node.Name),
                SwiftTypeName = swiftTypeName,
                MangledName = node.MangledName,
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Types = new List<TypeDecl>(),
                Operators = new List<OperatorDecl>(),
                Conformances = [.. node.Conformances.Select(x => HandleConformance(x, swiftTypeName))],
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            };
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

            return new ProtocolDecl
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

            // TODO: https://github.com/dotnet/runtimelab/issues/2954
            var reduction = demangler.Run(mangledName);
            FunctionReduction? functionReduction = reduction as FunctionReduction;

            var methodDecl = new MethodDecl
            {
                Name = ExtractUniqueName(node.Name),
                // Constructors for structs are named with a trailing 'C' instead of 'c'
                // because a constructor wrapper is missing in the library.
                MangledName = mangledName,
                MethodType = node.@static ?? false ? MethodType.Static : MethodType.Instance,
                IsConstructor = node.Kind == "Constructor",
                CSSignature = new List<ArgumentDecl>(),
                GenericParameters = GenericSignatureParser.ParseGenericSignature(node.GenericSig, node.sugared_genericSig),
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                Throws = node.throwing ?? false,
                IsAsync = functionReduction?.Function?.IsAsync ?? false,
                Visibility = Visibility.Public,
            };

            for (int i = 0; i < node.Children.Count(); i++)
            {
                var typeSpec = CreateTypeSpec(node.Children.ElementAt(i));

                methodDecl.CSSignature.Add(new ArgumentDecl
                {
                    SwiftTypeSpec = typeSpec,
                    Name = paramNames[i],
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = node.Children.ElementAt(i).Name == "GenericTypeParam",
                    ParentDecl = methodDecl,
                    ModuleDecl = moduleDecl
                });
            }

            return methodDecl;
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

            return new OperatorDecl
            {
                Name = node.Name,
                OperatorSymbol = node.Name,
                Kind = isUnary ? OperatorKind.Unary : OperatorKind.Binary,
                IsPrefix = isPrefix,
                UnderlyingMethod = methodDecl,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            };
        }

        private List<AccessorDecl> HandleAccessors(IEnumerable<Node> accessors, string fieldName, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            var result = new List<AccessorDecl>();

            foreach (var accessor in accessors)
            {
                switch (accessor.AccessorKind)
                {
                    case "get":
                        result.Add(CreateGetAccessor(accessor, fieldName, parentDecl, moduleDecl));
                        break;
                    case "set":
                        result.Add(CreateSetAccessor(accessor, fieldName, parentDecl, moduleDecl));
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
                        SwiftTypeSpec = CreateTypeSpec(accessor.Children.ElementAt(0)),
                        Name = string.Empty,
                        PrivateName = string.Empty,
                        IsInOut = false,
                        IsGeneric = false,
                        ParentDecl = parentDecl,
                        ModuleDecl = moduleDecl
                    }
                },
                GenericParameters = new List<GenericArgumentDecl>(),
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                Throws = false,
                IsAsync = false,
                Visibility = Visibility.Private,
            };

            return new GetAccessorDecl { Method = methodDecl };
        }

        private SetAccessorDecl CreateSetAccessor(Node accessor, string fieldName, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            // The setter has two children:
            // - Index 0: Void (return type)
            // - Index 1: The parameter type (value to set)
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
                        SwiftTypeSpec = CreateTypeSpec(accessor.Children.ElementAt(1)),
                        Name = "value",
                        PrivateName = string.Empty,
                        IsInOut = false,
                        IsGeneric = false,
                        ParentDecl = parentDecl,
                        ModuleDecl = moduleDecl
                    }
                },
                GenericParameters = new List<GenericArgumentDecl>(),
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                Throws = false,
                IsAsync = false,
                Visibility = Visibility.Private,
            };

            return new SetAccessorDecl { Method = methodDecl };
        }

        private PropertyDecl CreatePropertyDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            var typeSpec = CreateTypeSpec(node.Children.ElementAt(0));

            return new PropertyDecl
            {
                SwiftTypeSpec = typeSpec,
                Name = node.Name,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                IsStatic = node.@static ?? false,
                HasStorage = node.DeclAttributes is not null && Array.IndexOf(node.DeclAttributes, "HasStorage") != -1,
                Accessors = HandleAccessors(node.Accessors, node.Name, parentDecl, moduleDecl)
            };
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
                    var spec = TypeSpecParser.Parse(node.PrintedName);
                    if (spec is null)
                    {
                        throw new Exception($"Error parsing type from \"{node.PrintedName}\"");
                    }
                    return spec;
                default:
                    throw new NotImplementedException($"Can't handle node type {node.Kind} yet.");
            }
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
