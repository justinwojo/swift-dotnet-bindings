// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
using Microsoft.Extensions.Logging;
using Swift.Runtime;

namespace BindingsGeneration
{
    /// <summary>
    /// Interface for handling various types of declarations.
    /// </summary>
    public interface IHandler
    {
        /// <summary>
        /// Marshals the specified base declaration.
        /// </summary>
        /// <param name="baseDecl">The base declaration.</param>
        /// <param name="typeDatabase">The type database instance.</param>
        /// <returns>The environment corresponding to the base declaration.</returns>
        IEnvironment Marshal(BaseDecl baseDecl, ITypeDatabase typeDatabase);

        /// <summary>
        /// Emits the necessary code for the specified environment.
        /// </summary>
        /// <param name="csWriter">The csWriter instance.</param>
        /// <param name="swiftWriter">The swiftWriter instance.</param>
        /// <param name="env">The environment.</param>
        /// <param name="conductor">The conductor instance.</param>
        void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor);
    }

    /// <summary>
    /// Interface for handling module declarations.
    /// </summary>
    public interface IModuleHandler : IHandler
    {
    }

    /// <summary>
    /// Interface for handling type declarations.
    /// </summary>
    public interface ITypeHandler : IHandler
    {
    }

    /// <summary>
    /// Interface for handling method declarations.
    /// </summary>
    public interface IMethodHandler : IHandler
    {
    }

    /// <summary>
    /// Interface for handling argument declarations.
    /// </summary>
    public interface IArgumentHandler : IHandler
    {
    }

    /// <summary>
    /// Interface for handling property declarations.
    /// </summary>
    public interface IPropertyHandler : IHandler
    {
    }

    /// <summary>
    /// Base class for handling declarations.
    /// </summary>
    public class BaseHandler
    {
        protected readonly ILogger _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="BaseHandler"/> class.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        public BaseHandler(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Handles a base declaration.
        /// </summary>
        /// <param name="csWriter">The CSharpWriter instance.</param>
        /// <param name="swiftWriter">The SwiftWriter instance.</param>
        /// <param name="decl">The list of base declarations.</param>
        /// <param name="conductor">The conductor instance.</param>
        /// <param name="typeDatabase">The type database instance.</param>
        /// <param name="siblingPropertyNames">Optional set of property names for detecting method/property collisions.</param>
        /// <param name="pinvokeHelperContext">Optional P/Invoke helper context for generic types (to avoid CS7042).</param>
        protected virtual void HandleBaseDecl(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnumerable<BaseDecl> decl, Conductor conductor, ITypeDatabase typeDatabase, IReadOnlySet<string>? siblingPropertyNames = null, PInvokeHelperContext? pinvokeHelperContext = null)
        {
            // Track emitted method signatures to avoid duplicates
            var emittedMethodSignatures = new HashSet<string>();
            // B15: Secondary dedup based on projected C# public signature
            var emittedProjectedSignatures = new HashSet<string>(StringComparer.Ordinal);

            foreach (var baseDecl in decl)
            {
                if (baseDecl is StructDecl structDecl)
                {
                    if (SwiftUIViewDetector.IsSwiftUIView(structDecl))
                    {
                        ReportCollector.RecordTypeSkipped(structDecl, SkipReason.SwiftUIView,
                            "Type conforms to SwiftUI.View. Bridge generation available.");
                        SwiftUIBridgeCollector.Collect(structDecl);
                        continue;
                    }

                    if (conductor.TryGetTypeHandler(structDecl, out var handler))
                    {
                        var env = handler.Marshal(structDecl, typeDatabase);
                        handler.Emit(csWriter, swiftWriter, env, conductor);
                    }
                    else
                    {
                        _logger.LogWarning($"No handler found for method {structDecl.Name}");
                        ReportCollector.RecordTypeSkipped(structDecl, SkipReason.MissingHandler, "No type handler found for struct.");
                    }
                }
                else if (baseDecl is ClassDecl classDecl)
                {
                    if (SwiftUIViewDetector.IsSwiftUIView(classDecl))
                    {
                        ReportCollector.RecordTypeSkipped(classDecl, SkipReason.SwiftUIView,
                            "Type conforms to SwiftUI.View. Bridge generation available.");
                        SwiftUIBridgeCollector.Collect(classDecl);
                        continue;
                    }

                    if (conductor.TryGetTypeHandler(classDecl, out var handler))
                    {
                        var env = handler.Marshal(classDecl, typeDatabase);
                        handler.Emit(csWriter, swiftWriter, env, conductor);
                    }
                    else
                    {
                        _logger.LogWarning($"No handler found for method {classDecl.Name}");
                        ReportCollector.RecordTypeSkipped(classDecl, SkipReason.MissingHandler, "No type handler found for class.");
                    }
                }
                else if (baseDecl is ProtocolDecl protocolDecl)
                {
                    if (conductor.TryGetTypeHandler(protocolDecl, out var handler))
                    {
                        var env = handler.Marshal(protocolDecl, typeDatabase);
                        handler.Emit(csWriter, swiftWriter, env, conductor);
                    }
                    else
                    {
                        _logger.LogWarning($"No handler found for method {protocolDecl.Name}");
                        ReportCollector.RecordTypeSkipped(protocolDecl, SkipReason.MissingHandler, "No type handler found for protocol.");
                    }
                }
                else if (baseDecl is EnumDecl enumDecl)
                {
                    if (conductor.TryGetTypeHandler(enumDecl, out var handler))
                    {
                        var env = handler.Marshal(enumDecl, typeDatabase);
                        handler.Emit(csWriter, swiftWriter, env, conductor);
                    }
                    else
                    {
                        _logger.LogWarning($"No handler found for enum {enumDecl.Name}");
                        ReportCollector.RecordTypeSkipped(enumDecl, SkipReason.MissingHandler, "No type handler found for enum.");
                    }
                }
                else if (baseDecl is MethodDecl methodDecl)
                {
                    // Create unique signature key to detect duplicates
                    var signatureKey = GetMethodSignatureKey(methodDecl, typeDatabase);
                    if (emittedMethodSignatures.Contains(signatureKey))
                    {
                        _logger.LogDebug($"Skipping duplicate method '{methodDecl.Name}' with signature: {signatureKey}");
                        if (!methodDecl.IsAccessor)
                        {
                            ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodDecl.Name, methodDecl.ParentDecl, SkipReason.DuplicateSignature, signatureKey);
                        }
                        continue;
                    }
                    emittedMethodSignatures.Add(signatureKey);

                    // B15: Secondary dedup based on projected C# public method signature.
                    // Different Swift overloads (e.g., secret: vs clientSecret:) can produce
                    // identical C# method names after async normalization and parameter projection.
                    var projectedKey = GetProjectedCSharpMethodKey(methodDecl, typeDatabase);
                    if (!emittedProjectedSignatures.Add(projectedKey))
                    {
                        _logger.LogDebug($"Skipping method '{methodDecl.Name}' - projected C# signature collides: {projectedKey}");
                        if (!methodDecl.IsAccessor)
                        {
                            ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodDecl.Name, methodDecl.ParentDecl, SkipReason.DuplicateSignature, $"Projected C# method signature collides: {projectedKey}");
                        }
                        continue;
                    }

                    // Annotate with Mono JIT risk patterns (informational, does not affect routing)
                    MonoJitRiskDetector.ApplyRiskDetection(methodDecl);

                    if (conductor.TryGetMethodHandler(methodDecl, out var handler))
                    {
                        // Pass property names and P/Invoke helper context to the method environment
                        var env = new MethodEnvironment(methodDecl, typeDatabase, siblingPropertyNames, pinvokeHelperContext);
                        handler.Emit(csWriter, swiftWriter, env, conductor);
                    }
                    else
                    {
                        _logger.LogWarning($"No handler found for method {methodDecl.Name}");
                        if (!methodDecl.IsAccessor)
                        {
                            ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodDecl.Name, methodDecl.ParentDecl, SkipReason.MissingHandler, "No method handler found.");
                        }
                    }
                }
                else
                {
                    var declType = baseDecl?.GetType() ?? throw new ArgumentNullException(nameof(baseDecl));
                    throw new NotImplementedException($"Unsupported declaration type: {declType}");
                }

                csWriter.WriteLine();
            }
        }

        /// <summary>
        /// Creates a projected C# method signature key for dedup.
        /// Uses the public method name and projected C# parameter types,
        /// so different Swift overloads that produce identical C# signatures are deduplicated.
        /// </summary>
        private static string GetProjectedCSharpMethodKey(MethodDecl methodDecl, ITypeDatabase typeDatabase)
        {
            var returnTypeSpec = methodDecl.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
            bool hasReturnValue = returnTypeSpec != null && !returnTypeSpec.IsEmptyTuple;
            var methodName = methodDecl.IsConstructor
                ? "ctor"
                : NameProvider.GetPublicMethodName(methodDecl.Name, methodDecl.IsAsync, hasReturnValue: hasReturnValue);

            var typeConversionHandler = new TypeConversionHandler(typeDatabase);
            var paramTypes = new List<string>();
            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var arg = methodDecl.CSSignature[i];
                var idiomaticType = typeConversionHandler.GetIdiomaticCSharpType(arg.SwiftTypeSpec, isParameter: true);
                if (idiomaticType != null)
                {
                    paramTypes.Add(idiomaticType);
                }
                else
                {
                    try
                    {
                        var typeRecord = typeDatabase.GetTypeRecordOrAnyType(arg.SwiftTypeSpec);
                        paramTypes.Add(typeRecord.CSharpTypeName.FullyQualifiedName);
                    }
                    catch
                    {
                        paramTypes.Add(arg.SwiftTypeSpec?.ToString() ?? "unknown");
                    }
                }
            }
            return $"{methodName}({string.Join(",", paramTypes)})";
        }

        /// <summary>
        /// Creates a unique signature key for a method based on name, constructor status, and parameter types.
        /// </summary>
        private static string GetMethodSignatureKey(MethodDecl methodDecl, ITypeDatabase typeDatabase)
        {
            var paramTypes = new List<string>();
            // Skip first element (return type) in CSSignature
            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var arg = methodDecl.CSSignature[i];
                try
                {
                    var typeRecord = typeDatabase.GetTypeRecordOrAnyType(arg.SwiftTypeSpec);
                    paramTypes.Add(typeRecord.CSharpTypeName.FullyQualifiedName);
                }
                catch
                {
                    // For generic type parameters or other unsupported types,
                    // use the string representation of the type spec
                    paramTypes.Add(arg.SwiftTypeSpec?.ToString() ?? "unknown");
                }
            }
            var prefix = methodDecl.IsConstructor ? "ctor:" : "method:";
            return $"{prefix}{methodDecl.Name}({string.Join(",", paramTypes)})";
        }
    }
}
