// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Factory class for creating instances of PropertyHandler.
/// </summary>
public class PropertyHandlerFactory : IFactory<BaseDecl, IPropertyHandler>
{
    private readonly ILogger _handlerLogger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PropertyHandlerFactory"/> class.
    /// </summary>
    /// <param name="loggerFactory">The logger factory instance.</param>
    public PropertyHandlerFactory(ILoggerFactory loggerFactory)
    {
        _handlerLogger = loggerFactory.CreateLogger<PropertyHandler>();
    }

    public bool Handles(BaseDecl decl)
    {
        return decl is PropertyDecl;
    }

    public IPropertyHandler Construct()
    {
        return new PropertyHandler(_handlerLogger);
    }
}

/// <summary>
/// Handler class for property declarations that generates the binding code for Swift properties.
/// </summary> 
public class PropertyHandler : BaseHandler, IPropertyHandler
{
    // private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PropertyHandler"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public PropertyHandler(ILogger logger) : base(logger)
    {
    }

    /// <inheritdoc/>
    public IEnvironment Marshal(BaseDecl baseDecl, ITypeDatabase typeDatabase)
    {
        if (baseDecl is not PropertyDecl propertyDecl)
        {
            throw new ArgumentException("The provided decl must be a PropertyDecl.", nameof(baseDecl));
        }
        return new PropertyEnvironment(propertyDecl, typeDatabase);
    }

    /// <inheritdoc/>
    public void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor)
    {
        // This will emit the C# equivalent of the Swift property.
        // To achieve this, the process is divided into the following steps:
        // 1. Check if accessor methods can be emitted (no unsupported types)
        // 2. Emit Accessor Methods: Generate the C# methods that correspond to the Swift property's accessors (getter, setter, etc.).
        // 3. Emit Property Definition: Define the C# property itself, including its type, name, and accessors.
        //    This step utilizes the previously generated accessor methods to implement the property's behavior.

        var propertyEnv = (PropertyEnvironment)env;
        var propertyDecl = propertyEnv.PropertyDecl;

        // Handle AsyncStream properties - emit as IAsyncEnumerable<T>
        bool isAsyncStream = propertyEnv.AsyncStreamHandler.IsAsyncStream(propertyDecl.SwiftTypeSpec);
        if (isAsyncStream)
        {
            if (!propertyEnv.AsyncStreamHandler.IsSupportedAsyncStream(propertyDecl.SwiftTypeSpec))
            {
                _logger.LogWarning($"PropertyHandler: Skipping AsyncStream property {propertyDecl.Name} - element type not supported.");
                return;
            }
            EmitAsyncStreamProperty(csWriter, swiftWriter, propertyEnv, propertyDecl);
            return;
        }

        // Handle existential types (any Protocol) - check if supported (0-8 protocols)
        bool isExistential = propertyEnv.ExistentialHandler.IsExistential(propertyDecl.SwiftTypeSpec);
        if (isExistential)
        {
            var protocolList = propertyEnv.ExistentialHandler.ToProtocolListTypeSpec(propertyDecl.SwiftTypeSpec);
            if (protocolList == null || !propertyEnv.ExistentialHandler.IsSupportedExistential(protocolList))
            {
                _logger.LogWarning($"PropertyHandler: Skipping property {propertyDecl.Name} with unsupported existential (9+ protocols).");
                return;
            }
        }

        // Handle closure properties (property type is a closure/function type)
        bool isClosure = propertyEnv.ClosureHandler.IsClosure(propertyDecl);
        if (isClosure)
        {
            var closureTypeSpec = propertyEnv.ClosureHandler.GetClosureTypeSpec(propertyDecl);
            if (closureTypeSpec == null || !propertyEnv.ClosureHandler.IsSupportedClosure(closureTypeSpec))
            {
                _logger.LogWarning($"PropertyHandler: Skipping closure property {propertyDecl.Name} with unsupported closure type.");
                return;
            }
            // Check if we can invoke this closure from C# (requires primitive parameters)
            if (!propertyEnv.ClosureHandler.CanInvokeFromCSharp(closureTypeSpec))
            {
                _logger.LogWarning($"PropertyHandler: Skipping closure property {propertyDecl.Name} - closure has non-primitive parameters that cannot be marshalled.");
                return;
            }
        }

        bool processed = propertyEnv.TypeDatabase.TryGetTypeRecord(propertyDecl.SwiftTypeSpec, out var typeRecord);

        // Only skip if not an existential and not a closure (these don't have type records in the database)
        if (!processed && !isExistential && !isClosure)
        {
            _logger.LogWarning($"PropertyHandler: Couldn't process property {propertyDecl.Name} of type {propertyDecl.SwiftTypeSpec}. Skipping.");
            return;
        }

        if (propertyDecl.Accessors.Count == 0)
        {
            // No public accessors, so we don't need to emit anything
            return;
        }

        string csTypeName;
        if (isExistential)
        {
            var protocolList = propertyEnv.ExistentialHandler.ToProtocolListTypeSpec(propertyDecl.SwiftTypeSpec)!;
            csTypeName = propertyEnv.ExistentialHandler.GetCSharpExistentialType(protocolList);
        }
        else if (isClosure)
        {
            var closureTypeSpec = propertyEnv.ClosureHandler.GetClosureTypeSpec(propertyDecl)!;
            // Check if it's an optional closure and use nullable delegate type if so
            bool isOptionalClosure = propertyEnv.ClosureHandler.IsOptionalClosure(propertyDecl.SwiftTypeSpec);
            csTypeName = isOptionalClosure
                ? propertyEnv.ClosureHandler.GetCSharpOptionalDelegateType(propertyDecl.SwiftTypeSpec)
                : propertyEnv.ClosureHandler.GetCSharpDelegateType(closureTypeSpec);
        }
        else if (propertyEnv.BoundGenericsHandler.IsBoundGeneric(propertyDecl))
        {
            csTypeName = propertyEnv.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(propertyDecl);
        }
        else
        {
            csTypeName = typeRecord!.CSharpTypeName.FullyQualifiedName;
        }

        // Skip properties with AnyType - the accessor methods will be skipped due to unsupported types
        if (csTypeName.Contains("AnyType"))
        {
            _logger.LogWarning($"PropertyHandler: Skipping property {propertyDecl.Name} with unsupported AnyType in type {csTypeName}.");
            return;
        }

        // TODO Detect and skip / Handle async properties https://github.com/dotnet/runtimelab/issues/2996

        // Get nested type names from parent for collision detection
        // In Swift, a property can have the same name as its type (e.g., cacheType: CacheType)
        // but in C# this causes a collision when both are PascalCase
        IReadOnlySet<string>? nestedTypeNames = null;
        if (propertyDecl.ParentDecl is TypeDecl parentTypeDecl)
        {
            nestedTypeNames = new HashSet<string>(parentTypeDecl.Types.Select(t => t.Name));
        }

        // Get the C# property name, handling reserved keywords, special cases, and nested type collisions
        var propertyName = NameProvider.GetPropertyName(propertyDecl.Name, nestedTypeNames);

        // Check if all accessor methods can be emitted before actually emitting them.
        // If any accessor would be skipped (due to unsupported types like AnyType),
        // skip the entire property to avoid generating a property that references non-existent methods.
        foreach (var accessor in propertyDecl.Accessors)
        {
            if (conductor.TryGetMethodHandler(accessor.Method, out var methodHandler))
            {
                var accessorEnv = (MethodEnvironment)methodHandler.Marshal(accessor.Method, propertyEnv.TypeDatabase);
                var signatureHandler = new SignatureHandler(accessorEnv);
                if (signatureHandler.GetWrapperSignature().ContainsPlaceholder)
                {
                    _logger.LogWarning($"PropertyHandler: Skipping property {propertyDecl.Name} because accessor {accessor.Method.Name} has unsupported signature.");
                    return;
                }
            }
            else
            {
                _logger.LogWarning($"No handler found for property accessor {accessor.Method.Name}. Skipping property {propertyDecl.Name}.");
                return;
            }
        }

        // Now emit the accessor methods using MethodHandler
        foreach (var accessor in propertyDecl.Accessors)
        {
            if (conductor.TryGetMethodHandler(accessor.Method, out var methodHandler))
            {
                // Mark the method as an accessor to prevent type conversions
                // Type conversions would cause a mismatch between property type and accessor return/param types
                accessor.Method.IsAccessor = true;
                var accessorEnv = methodHandler.Marshal(accessor.Method, propertyEnv.TypeDatabase);
                methodHandler.Emit(csWriter, swiftWriter, accessorEnv, conductor);
            }
        }
        var staticModifier = propertyDecl.IsStatic ? "static " : string.Empty;
        // Then emit the property
        csWriter.WriteLine($"public {staticModifier}{csTypeName} {propertyName}");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        var getter = propertyDecl.Accessors.OfType<GetAccessorDecl>().FirstOrDefault();
        if (getter != null)
        {
            EmitGetter(csWriter, getter);
        }

        var setter = propertyDecl.Accessors.OfType<SetAccessorDecl>().FirstOrDefault();
        if (setter != null)
        {
            EmitSetter(csWriter, setter);
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();
    }

    /// <summary>
    /// Emits the getter implementation for a property.
    /// </summary>
    /// <param name="csWriter">The C# code writer to emit to</param>
    /// <param name="getter">The getter accessor declaration</param>
    private void EmitGetter(CSharpWriter csWriter, GetAccessorDecl getter)
    {
        // Use PascalCase method name to match how MethodHandler emits the accessor method
        var methodName = NameProvider.GetMethodName(getter.Method.Name, null);
        csWriter.WriteLine($"get => {methodName}();");
    }

    /// <summary>
    /// Emits the setter implementation for a property.
    /// </summary>
    /// <param name="csWriter">The C# code writer to emit to</param>
    /// <param name="setter">The setter accessor declaration</param>
    private void EmitSetter(CSharpWriter csWriter, SetAccessorDecl setter)
    {
        // Use PascalCase method name to match how MethodHandler emits the accessor method
        var methodName = NameProvider.GetMethodName(setter.Method.Name, null);
        csWriter.WriteLine($"set => {methodName}(value);");
    }

    /// <summary>
    /// Emits an AsyncStream property as IAsyncEnumerable&lt;T&gt;.
    /// AsyncStream properties require a Swift wrapper function to iterate the stream
    /// and call C# callbacks for each element.
    /// </summary>
    /// <param name="csWriter">The C# code writer to emit to</param>
    /// <param name="swiftWriter">The Swift code writer to emit to</param>
    /// <param name="propertyEnv">The property environment</param>
    /// <param name="propertyDecl">The property declaration</param>
    private void EmitAsyncStreamProperty(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        PropertyEnvironment propertyEnv,
        PropertyDecl propertyDecl)
    {
        var asyncStreamHandler = propertyEnv.AsyncStreamHandler;
        var elementType = asyncStreamHandler.GetCSharpElementType(propertyDecl.SwiftTypeSpec);
        var swiftWrapperName = asyncStreamHandler.GetSwiftWrapperFunctionName(propertyDecl);
        var callbackName = $"{propertyDecl.Name}_AsyncStream";

        // Get parent type name for Swift wrapper
        var parentTypeName = propertyDecl.ParentDecl is TypeDecl typeDecl ? typeDecl.Name : "Unknown";

        // Get library path from type database using the parent type's module
        var moduleName = propertyDecl.ParentDecl is TypeDecl td ? td.SwiftTypeName.Module : "Unknown";
        var libraryPath = propertyEnv.TypeDatabase.GetLibraryPath(moduleName);

        // Get nested type names for collision detection
        IReadOnlySet<string>? nestedTypeNames = null;
        if (propertyDecl.ParentDecl is TypeDecl parentTypeDecl)
        {
            nestedTypeNames = new HashSet<string>(parentTypeDecl.Types.Select(t => t.Name));
        }

        // Emit callbacks
        csWriter.WriteLine();
        AsyncStreamEmitter.EmitElementCallback(csWriter, propertyDecl, asyncStreamHandler, callbackName);
        csWriter.WriteLine();
        AsyncStreamEmitter.EmitCompletionCallback(csWriter, callbackName);
        csWriter.WriteLine();

        // Emit P/Invoke
        AsyncStreamEmitter.EmitPInvokeDeclaration(csWriter, swiftWrapperName, libraryPath, propertyDecl.IsStatic);
        csWriter.WriteLine();

        // Emit property with nested type collision detection
        AsyncStreamEmitter.EmitPropertyGetter(csWriter, propertyDecl, asyncStreamHandler, swiftWrapperName, callbackName, nestedTypeNames);
        csWriter.WriteLine();

        // Emit Swift wrapper
        AsyncStreamEmitter.EmitSwiftWrapper(swiftWriter, propertyDecl, asyncStreamHandler, swiftWrapperName, parentTypeName);
    }
}

