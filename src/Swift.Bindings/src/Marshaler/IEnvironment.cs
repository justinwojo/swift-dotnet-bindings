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
    public class MethodEnvironment(MethodDecl methodDecl, ITypeDatabase typeDatabase, IReadOnlySet<string>? siblingPropertyNames = null) : IEnvironment
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
        public BoundGenericsHandler BoundGenericsHandler { get; } = new BoundGenericsHandler(typeDatabase);

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
        /// Gets the set of property names in the same parent type.
        /// Used to detect and resolve method/property name collisions.
        /// </summary>
        public IReadOnlySet<string>? SiblingPropertyNames { get; } = siblingPropertyNames;

        /// <summary>
        /// Gets the C# method name, resolving any collisions with property names.
        /// </summary>
        public string CSharpMethodName => NameProvider.GetMethodName(MethodDecl.Name, SiblingPropertyNames);
    }

    /// <summary>
    /// Represents a property environment.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the PropertyEnvironment class.
    /// </remarks>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <param name="typeDatabase">The type database instance.</param>
    public class PropertyEnvironment(PropertyDecl propertyDecl, ITypeDatabase typeDatabase) : IEnvironment
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
        /// Bound generic helper instance.
        /// </summary>
        public BoundGenericsHandler BoundGenericsHandler { get; } = new BoundGenericsHandler(typeDatabase);

        /// <summary>
        /// Tuple handler instance.
        /// </summary>
        public TupleHandler TupleHandler { get; } = new TupleHandler(typeDatabase);

        /// <summary>
        /// Type conversion handler instance for automatic .NET type conversions.
        /// </summary>
        public TypeConversionHandler TypeConversionHandler { get; } = new TypeConversionHandler(typeDatabase);
    }
}
