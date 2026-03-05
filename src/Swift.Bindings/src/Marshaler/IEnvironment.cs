// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift.Runtime;

namespace BindingsGeneration
{
    /// <summary>
    /// Represents an environment interface. It should contain data required to emit C# code.
    /// </summary>
    public interface IEnvironment
    {
        /// <summary>
        /// Gets the TypeDatabase
        /// </summary>
        public ITypeDatabase TypeDatabase { get; }
    }

    /// <summary>
    /// Represents a module environment.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the ModuleEnvironment class.
    /// </remarks>
    /// <param name="moduleDecl">The module declaration.</param>
    /// <param name="typeDatabase">The type database instance.</param>
    public class ModuleEnvironment(ModuleDecl moduleDecl, ITypeDatabase typeDatabase) : IEnvironment
    {
        /// <summary>
        /// Gets the module declaration.
        /// </summary>
        public ModuleDecl ModuleDecl { get; private set; } = moduleDecl;

        /// <summary>
        /// Gets the TypeDatabase
        /// </summary>
        public ITypeDatabase TypeDatabase { get; } = typeDatabase;
    }

    /// <summary>
    /// Represents a type environment.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the TypeEnvironment class.
    /// </remarks>
    /// <param name="typeDecl">The type declaration.</param>
    /// <param name="typeDatabase">The type database instance.</param>
    public class TypeEnvironment(TypeDecl typeDecl, ITypeDatabase typeDatabase) : IEnvironment
    {
        /// <summary>
        /// Gets the type declaration.
        /// </summary>
        public TypeDecl TypeDecl { get; private set; } = typeDecl;

        /// <summary>
        /// Gets the TypeDatabase
        /// </summary>
        public ITypeDatabase TypeDatabase { get; } = typeDatabase;

        /// <summary>
        /// Mapping of Swift generic type names to C# generic type names.
        /// </summary>
        public Dictionary<string, GenericParameterCSName> GenericTypeMapping { get; } = NameProvider.GetGenericTypeMappingForType(typeDecl);
    }

    /// <summary>
    /// Represents a method environment.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the MethodEnvironment class.
    /// </remarks>
    /// <param name="methodDecl">The method declaration.</param>
    /// <param name="typeDatabase">The type database instance.</param>
    /// <param name="siblingPropertyNames">Optional set of property names in the same type, used for collision detection.</param>
    /// <param name="pinvokeHelperContext">Optional P/Invoke helper context for generic types (to avoid CS7042).</param>
    public class MethodEnvironment(MethodDecl methodDecl, ITypeDatabase typeDatabase, IReadOnlySet<string>? siblingPropertyNames = null, PInvokeHelperContext? pinvokeHelperContext = null, SortedDictionary<string, List<string>>? compositionCollector = null) : IEnvironment
    {
        /// <summary>
        /// Gets the method declaration.
        /// </summary>
        public MethodDecl MethodDecl { get; private set; } = methodDecl;

        /// <summary>
        /// Gets the parent declaration.
        /// </summary>
        public BaseDecl ParentDecl { get; } = methodDecl.ParentDecl ?? throw new ArgumentNullException($"Parent declaration on method {methodDecl.Name} is null.");

        /// <summary>
        /// Gets the TypeDatabase
        /// </summary>
        public ITypeDatabase TypeDatabase { get; } = typeDatabase;

        /// <summary>
        /// Mapping of Swift generic type names to C# generic type names.
        /// </summary>
        public Dictionary<string, GenericParameterCSName> GenericTypeMapping { get; } = NameProvider.GetGenericTypeMapping(methodDecl);

        /// <summary>
        /// Bound generic helper instance.
        /// </summary>
        public BoundGenericsHandler BoundGenericsHandler { get; } = new BoundGenericsHandler(typeDatabase,
            (methodDecl.ModuleDecl as ModuleDecl)?.ConformanceGraph);

        /// <summary>
        /// Closure handler instance.
        /// </summary>
        public ClosureHandler ClosureHandler { get; } = new ClosureHandler(typeDatabase);

        /// <summary>
        /// Tuple handler instance.
        /// </summary>
        public TupleHandler TupleHandler { get; } = new TupleHandler(typeDatabase);

        /// <summary>
        /// Type conversion handler instance for automatic .NET type conversions.
        /// </summary>
        public TypeConversionHandler TypeConversionHandler { get; } = new TypeConversionHandler(typeDatabase);

        /// <summary>
        /// Existential handler instance for handling protocol existential types.
        /// </summary>
        public ExistentialHandler ExistentialHandler { get; } = new ExistentialHandler(typeDatabase, compositionCollector)
        {
            CurrentModuleName = (methodDecl.ModuleDecl as ModuleDecl)?.Name
        };

        /// <summary>
        /// Gets the set of property names in the same parent type.
        /// Used to detect and resolve method/property name collisions.
        /// </summary>
        public IReadOnlySet<string>? SiblingPropertyNames { get; } = siblingPropertyNames;

        /// <summary>
        /// Gets the C# method name, resolving any collisions with property names.
        /// </summary>
        public string CSharpMethodName => NameProvider.GetPublicMethodName(
            MethodDecl.Name, MethodDecl.IsAsync,
            hasReturnValue: !MethodDecl.IsAccessor && MethodDecl.CSSignature.Count > 0 && !MethodDecl.CSSignature.First().SwiftTypeSpec.IsEmptyTuple,
            SiblingPropertyNames,
            isSelfReturning: IsSelfReturning,
            parentTypeName: (MethodDecl.ParentDecl as TypeDecl)?.Name,
            parameterCount: MethodDecl.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple));

        /// <summary>
        /// Returns true if the method returns its declaring type (fluent/builder pattern).
        /// Self-returning methods skip the "Get" prefix (e.g., "equalTo" → "EqualTo", not "GetEqualTo").
        /// Only applies to non-constructor, non-accessor instance methods.
        /// </summary>
        internal bool IsSelfReturning => IsSelfReturningMethod(MethodDecl);

        /// <summary>
        /// Static helper for detecting self-returning methods.
        /// Reused by dedup key builders that don't have a MethodEnvironment.
        /// </summary>
        internal static bool IsSelfReturningMethod(MethodDecl methodDecl)
        {
            // Only instance methods can be "self-returning" (fluent/builder pattern).
            // Static methods returning Self are factories/singletons where Get prefix IS appropriate.
            if (methodDecl.IsConstructor || methodDecl.IsAccessor || methodDecl.IsAsync)
                return false;
            if (methodDecl.MethodType == MethodType.Static)
                return false;
            if (methodDecl.CSSignature.Count == 0)
                return false;

            var returnTypeSpec = methodDecl.CSSignature[0].SwiftTypeSpec;
            if (returnTypeSpec.IsEmptyTuple)
                return false;

            // Check for literal Self returns (protocol extension methods)
            if (returnTypeSpec.IsDynamicSelf)
                return true;

            // Check for concrete type matching the parent type
            if (methodDecl.ParentDecl is TypeDecl parentTypeDecl &&
                returnTypeSpec is NamedTypeSpec named &&
                named.Name == parentTypeDecl.SwiftTypeName.ModuleQualifiedName)
                return true;

            return false;
        }

        /// <summary>
        /// Gets the P/Invoke helper context for collecting P/Invoke declarations in generic types.
        /// When non-null, P/Invoke declarations are collected here instead of emitted inline (to avoid CS7042).
        /// </summary>
        public PInvokeHelperContext? PInvokeHelperContext { get; } = pinvokeHelperContext;

        /// <summary>
        /// Indicates whether the containing type is generic and P/Invoke must be emitted in a helper class.
        /// </summary>
        public bool IsContainingTypeGeneric => PInvokeHelperContext != null;

        /// <summary>
        /// Composition collector for multi-protocol existential interfaces.
        /// Threaded from TypeHandlerContext to ExistentialHandler during emission.
        /// </summary>
        public SortedDictionary<string, List<string>>? CompositionCollector { get; } = compositionCollector;

        /// <summary>
        /// Shared set of projected C# method signatures already emitted, used to deduplicate
        /// default parameter overloads against the main emission pass. (C6/C7)
        /// Set by HandleBaseDecl before method emission; null if not available.
        /// </summary>
        public HashSet<string>? EmittedProjectedSignatures { get; set; }
    }

    /// <summary>
    /// Represents a property environment.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the PropertyEnvironment class.
    /// </remarks>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <param name="typeDatabase">The type database instance.</param>
    /// <param name="siblingNestedTypeNames">Optional set of nested type names in the same parent type, used for collision detection.</param>
    public class PropertyEnvironment(PropertyDecl propertyDecl, ITypeDatabase typeDatabase, IReadOnlySet<string>? siblingNestedTypeNames = null, SortedDictionary<string, List<string>>? compositionCollector = null) : IEnvironment
    {
        /// <summary>
        /// Gets the property declaration.
        /// </summary>
        public PropertyDecl PropertyDecl { get; private set; } = propertyDecl;

        /// <summary>
        /// Gets the TypeDatabase
        /// </summary>
        public ITypeDatabase TypeDatabase { get; } = typeDatabase;

        /// <summary>
        /// Gets the sibling nested type names for collision detection.
        /// </summary>
        public IReadOnlySet<string>? SiblingNestedTypeNames { get; } = siblingNestedTypeNames;

        /// <summary>
        /// Bound generic helper instance.
        /// </summary>
        public BoundGenericsHandler BoundGenericsHandler { get; } = new BoundGenericsHandler(typeDatabase,
            (propertyDecl.ModuleDecl as ModuleDecl)?.ConformanceGraph);

        /// <summary>
        /// Tuple handler instance.
        /// </summary>
        public TupleHandler TupleHandler { get; } = new TupleHandler(typeDatabase);

        /// <summary>
        /// Type conversion handler instance for automatic .NET type conversions.
        /// </summary>
        public TypeConversionHandler TypeConversionHandler { get; } = new TypeConversionHandler(typeDatabase);

        /// <summary>
        /// Existential handler instance for handling protocol existential types.
        /// </summary>
        public ExistentialHandler ExistentialHandler { get; } = new ExistentialHandler(typeDatabase, compositionCollector)
        {
            CurrentModuleName = (propertyDecl.ModuleDecl as ModuleDecl)?.Name
        };

        /// <summary>
        /// Composition collector for multi-protocol existential interfaces.
        /// </summary>
        public SortedDictionary<string, List<string>>? CompositionCollector { get; } = compositionCollector;

        /// <summary>
        /// Closure handler instance for handling closure (function) types.
        /// </summary>
        public ClosureHandler ClosureHandler { get; } = new ClosureHandler(typeDatabase);

        /// <summary>
        /// AsyncStream handler instance for handling Swift AsyncStream types.
        /// </summary>
        public AsyncStreamHandler AsyncStreamHandler { get; } = new AsyncStreamHandler(typeDatabase);
    }
}
