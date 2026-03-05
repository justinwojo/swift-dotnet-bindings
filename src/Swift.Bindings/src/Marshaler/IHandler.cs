// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
using System.Diagnostics;
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
        /// <param name="context">The type handler context (P/Invoke helper, renames, etc.).</param>
        void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor, TypeHandlerContext context);
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
        /// <param name="context">The type handler context (P/Invoke helper, renames, etc.).</param>
        /// <param name="siblingPropertyNames">Optional set of property names for detecting method/property collisions.</param>
        /// <summary>
        /// Topologically sorts type declarations so that base classes are emitted before derived classes.
        /// Non-class types and root classes maintain their original relative ordering.
        /// Uses Kahn's algorithm with original-index tie-breaking for stability.
        /// </summary>
        protected static List<BaseDecl> TopologicallySortTypes(IEnumerable<BaseDecl> decls)
        {
            var list = decls as List<BaseDecl> ?? decls.ToList();

            // Build edges: derived ClassDecl depends on its ResolvedSuperclass
            var classToIndex = new Dictionary<ClassDecl, int>(ReferenceEqualityComparer.Instance);
            var edges = new List<(int derivedIdx, int baseIdx)>();

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is ClassDecl cd)
                    classToIndex[cd] = i;
            }

            foreach (var (cd, idx) in classToIndex)
            {
                if (cd.HasResolvedSuperclass && classToIndex.TryGetValue(cd.ResolvedSuperclass!, out var baseIdx))
                    edges.Add((idx, baseIdx));
            }

            if (edges.Count == 0) return list;

            // Kahn's algorithm: edges are "derived depends on base".
            // In-degree counts how many types depend on this index (i.e., how many derived classes point to it).
            // Actually, for Kahn's we need: in-degree = number of dependencies that must come before this node.
            // Edge direction: derived → base means "base must come first".
            // So for emission order: in-degree[derived] += 1 for each base it depends on.
            var inDegree = new int[list.Count];
            var dependents = new Dictionary<int, List<int>>(); // baseIdx → list of derivedIdx

            foreach (var (derivedIdx, baseIdx) in edges)
            {
                inDegree[derivedIdx]++;
                if (!dependents.TryGetValue(baseIdx, out var deps))
                {
                    deps = new List<int>();
                    dependents[baseIdx] = deps;
                }
                deps.Add(derivedIdx);
            }

            // Priority queue: nodes with 0 in-degree, ordered by original index for stability
            var ready = new SortedSet<int>();
            for (int i = 0; i < list.Count; i++)
            {
                if (inDegree[i] == 0)
                    ready.Add(i);
            }

            var result = new List<BaseDecl>(list.Count);
            while (ready.Count > 0)
            {
                var idx = ready.Min;
                ready.Remove(idx);
                result.Add(list[idx]);

                if (dependents.TryGetValue(idx, out var deps))
                {
                    foreach (var dep in deps)
                    {
                        inDegree[dep]--;
                        if (inDegree[dep] == 0)
                            ready.Add(dep);
                    }
                }
            }

            // Safety: if the graph has a cycle (or inconsistent hierarchy), some nodes
            // will never reach in-degree 0. Append them in original order rather than
            // silently dropping declarations.
            if (result.Count < list.Count)
            {
                var cycleNames = new List<string>();
                for (int i = 0; i < list.Count; i++)
                {
                    if (inDegree[i] > 0)
                    {
                        result.Add(list[i]);
                        cycleNames.Add(list[i].Name);
                    }
                }
                Debug.WriteLine($"[TopologicallySortTypes] WARNING: Cycle detected in class hierarchy. " +
                    $"The following types have unresolvable dependencies and were appended in original order: " +
                    $"{string.Join(", ", cycleNames)}");
            }

            return result;
        }

        protected virtual void HandleBaseDecl(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnumerable<BaseDecl> decl, Conductor conductor, ITypeDatabase typeDatabase, TypeHandlerContext context, IReadOnlySet<string>? siblingPropertyNames = null)
        {
            // Track emitted method signatures to avoid duplicates
            var emittedMethodSignatures = new HashSet<string>();
            // B15: Secondary dedup based on projected C# public signature
            var emittedProjectedSignatures = new HashSet<string>(StringComparer.Ordinal);

            var sortedDecl = TopologicallySortTypes(decl);
            var emissionCtx = context.GetEmissionContext();
            foreach (var baseDecl in sortedDecl)
            {
                if (baseDecl is TypeDecl typeDecl)
                {
                    // Suppress underscore-prefixed types that are not structurally required
                    if (typeDecl.SwiftTypeName != null &&
                        emissionCtx.IsUnderscoreSuppressed(typeDecl.SwiftTypeName.ToString()))
                    {
                        ReportCollector.RecordTypeSkipped(typeDecl, SkipReason.UnderscorePrefixInternal,
                            "Underscore-prefixed type suppressed from public API.");
                        continue;
                    }
                }

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
                        handler.Emit(csWriter, swiftWriter, env, conductor, context);
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
                        handler.Emit(csWriter, swiftWriter, env, conductor, context);
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
                        handler.Emit(csWriter, swiftWriter, env, conductor, context);
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
                        handler.Emit(csWriter, swiftWriter, env, conductor, context);
                    }
                    else
                    {
                        _logger.LogWarning($"No handler found for enum {enumDecl.Name}");
                        ReportCollector.RecordTypeSkipped(enumDecl, SkipReason.MissingHandler, "No type handler found for enum.");
                    }
                }
                else if (baseDecl is MethodDecl methodDecl)
                {
                    // Suppress synthesized protocol methods (e.g., hash(into:) for Hashable)
                    // whose functionality is provided by .NET equivalents (GetHashCode)
                    if (methodDecl.ParentDecl is TypeDecl parentType &&
                        MemberEmissionValidator.IsSynthesizedProtocolMethod(methodDecl, parentType))
                    {
                        if (!methodDecl.IsAccessor)
                            ReportCollector.RecordMemberSynthesized(BindingItemKind.Method, methodDecl.Name, methodDecl.ParentDecl);
                        continue;
                    }

                    // Create unique signature key to detect duplicates
                    var signatureKey = GetMethodSignatureKey(methodDecl, typeDatabase, _logger);
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

                    // Skip constructors that become parameterless after empty tuple () params are
                    // stripped (e.g., init(nilLiteral: ()) from ExpressibleByNilLiteral) when a
                    // parameterless constructor already exists. Must be checked BEFORE projected key
                    // reservation to avoid the empty-tuple ctor reserving ctor() and blocking the
                    // real parameterless constructor.
                    if (methodDecl.IsConstructor &&
                        ConstructorHandler.HasOnlyEmptyTupleParams(methodDecl) &&
                        ConstructorHandler.HasParameterlessConstructorSibling(methodDecl))
                    {
                        _logger.LogDebug($"Skipping constructor '{methodDecl.Name}': becomes parameterless after empty tuple removal, collides with existing constructor.");
                        ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodDecl.Name, methodDecl.ParentDecl,
                            SkipReason.UnsupportedSignature, "Constructor has only empty tuple () parameters; would duplicate existing parameterless constructor.");
                        continue;
                    }

                    // B15: Secondary dedup based on projected C# public method signature.
                    // Different Swift overloads (e.g., secret: vs clientSecret:) can produce
                    // identical C# method names after async normalization and parameter projection.
                    var projectedKey = GetProjectedCSharpMethodKey(methodDecl, typeDatabase, _logger);
                    if (!emittedProjectedSignatures.Add(projectedKey))
                    {
                        _logger.LogDebug($"Skipping method '{methodDecl.Name}' - projected C# signature collides: {projectedKey}");
                        if (!methodDecl.IsAccessor)
                        {
                            ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodDecl.Name, methodDecl.ParentDecl, SkipReason.DuplicateSignature, $"Projected C# method signature collides: {projectedKey}");
                        }
                        continue;
                    }

                    // Check for specific conditions that cause compilation errors but aren't
                    // caught by the downstream method handler (which has its own UnsupportedSwiftType fallback).
                    // NOTE: CanEmitMethod is too strict for main emission (blocks ContainsPlaceholder which
                    // the handler intentionally emits via [UnsupportedSwiftType]). Only check emission-critical
                    // conditions: B18 non-simple enum .Buffer, B19 SwiftUI refs, C6 async enum tuple.
                    var methodSkipReason = MemberEmissionValidator.ShouldSkipMethodEmission(methodDecl, typeDatabase, out var methodSkipDetails);
                    if (methodSkipReason != null)
                    {
                        if (!methodDecl.IsAccessor)
                        {
                            ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodDecl.Name, methodDecl.ParentDecl, methodSkipReason.Value, methodSkipDetails ?? "");
                        }
                        continue;
                    }

                    // Annotate with Mono JIT risk patterns (informational, does not affect routing)
                    MonoJitRiskDetector.ApplyRiskDetection(methodDecl);

                    if (conductor.TryGetMethodHandler(methodDecl, out var handler))
                    {
                        // Pass property names and P/Invoke helper context to the method environment
                        var env = new MethodEnvironment(methodDecl, typeDatabase, siblingPropertyNames, context.PInvokeHelperContext, context.CompositionCollector);
                        // C6/C7: Share projected signature set so DefaultParameterOverloadEmitter
                        // can dedup against methods already emitted from the main pass
                        env.EmittedProjectedSignatures = emittedProjectedSignatures;
                        handler.Emit(csWriter, swiftWriter, env, conductor, context);
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
        internal static string GetProjectedCSharpMethodKey(MethodDecl methodDecl, ITypeDatabase typeDatabase, ILogger? logger = null)
        {
            var returnTypeSpec = methodDecl.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
            bool hasReturnValue = returnTypeSpec != null && !returnTypeSpec.IsEmptyTuple;
            var isSelfReturning = MethodEnvironment.IsSelfReturningMethod(methodDecl);
            var methodName = methodDecl.IsConstructor
                ? "ctor"
                : NameProvider.GetPublicMethodName(methodDecl.Name, methodDecl.IsAsync, hasReturnValue: hasReturnValue, isSelfReturning: isSelfReturning, parentTypeName: (methodDecl.ParentDecl as TypeDecl)?.Name,
                    parameterCount: methodDecl.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple));

            var paramTypes = new List<string>();
            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var arg = methodDecl.CSSignature[i];
                // Debug params (#file, #line, etc.) are stripped from the public signature
                if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                    continue;
                // Empty tuple () params are stripped from the C# signature (zero-sized Void)
                if (arg.SwiftTypeSpec.IsEmptyTuple)
                    continue;
                // C11: Optional<Closure> and bare Closure are the same overload in C#
                // (nullable reference types don't affect overload resolution).
                // Unwrap Optional<Closure> so both produce the same projected key.
                var typeSpecForKey = arg.SwiftTypeSpec;
                if (typeSpecForKey is NamedTypeSpec optionalClosureSpec &&
                    optionalClosureSpec.Name == "Swift.Optional" &&
                    optionalClosureSpec.GenericParameters.Count == 1 &&
                    optionalClosureSpec.GenericParameters[0] is ClosureTypeSpec)
                {
                    typeSpecForKey = optionalClosureSpec.GenericParameters[0];
                }
                string paramType;
                try
                {
                    var factory = new TypeProjectionFactory();
                    var projection = factory.Project(typeSpecForKey, new ProjectionContext
                    {
                        TypeDatabase = typeDatabase,
                        IsParameter = true
                    });
                    if (projection != null)
                    {
                        paramType = projection.PublicType;
                    }
                    else
                    {
                        // Normalize container types whose element projection failed
                        // (e.g., Array<τ_0_0> where τ_0_0 can't be resolved without GenericContext).
                        // Array and Set both project to IEnumerable<T> as parameters, so their
                        // keys must match to prevent CS0111 collisions.
                        paramType = NormalizeContainerForOverloadKey(typeSpecForKey, typeDatabase);
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning($"GetProjectedCSharpMethodKey: Failed to resolve type '{typeSpecForKey}' for method '{methodDecl.Name}', using string fallback: {ex.Message}");
                    paramType = typeSpecForKey?.ToString() ?? "unknown";
                }
                // Normalize nullable reference types: Optional<Class> and Class produce
                // the same C# overload (nullable annotations are erased at runtime).
                paramType = ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity(paramType, arg.SwiftTypeSpec, typeDatabase);
                paramTypes.Add(paramType);
            }
            // All async methods get CancellationToken at emission time — include it in the
            // projected key so native async methods collide with completion handler overloads.
            if (methodDecl.IsAsync)
            {
                paramTypes.Add("System.Threading.CancellationToken");
            }

            return $"{methodName}({string.Join(",", paramTypes)})";
        }

        /// <summary>
        /// Normalizes container type specs for overload key generation.
        /// Array and Set both project to IEnumerable&lt;T&gt; as parameters, but when the element
        /// type is an unresolved generic parameter (τ_0_0), TypeProjectionFactory returns null
        /// and DB lookup returns different names (SwiftArray vs SwiftSet). This method ensures
        /// both produce the same key by using a canonical container name.
        /// </summary>
        internal static string NormalizeContainerForOverloadKey(TypeSpec typeSpecForKey, ITypeDatabase typeDatabase)
        {
            if (typeSpecForKey is NamedTypeSpec namedSpec)
            {
                // Array<T>, ArraySlice<T>, and Set<T> all project to IEnumerable<T> as parameters.
                // Project the element type so keys match regardless of container
                // (e.g., ArraySlice<UInt8> and Array<UInt8> both → IEnumerable<byte>).
                if (namedSpec.Name is "Swift.Array" or "Swift.ArraySlice" or "Swift.Set" && namedSpec.GenericParameters.Count == 1)
                {
                    var elemSpec = namedSpec.GenericParameters[0];
                    string elemKey;
                    try
                    {
                        var factory = new TypeProjectionFactory();
                        var projection = factory.Project(elemSpec, new ProjectionContext
                        {
                            TypeDatabase = typeDatabase,
                            IsParameter = true
                        });
                        elemKey = projection?.PublicType ?? elemSpec.ToString();
                    }
                    catch
                    {
                        elemKey = elemSpec.ToString();
                    }
                    return $"IEnumerable<{elemKey}>";
                }
                // Dictionary<K,V> projects to IReadOnlyDictionary<K,V> as parameters
                if (namedSpec.Name == "Swift.Dictionary" && namedSpec.GenericParameters.Count == 2)
                {
                    var keyKey = namedSpec.GenericParameters[0].ToString();
                    var valueKey = namedSpec.GenericParameters[1].ToString();
                    return $"IReadOnlyDictionary<{keyKey},{valueKey}>";
                }
            }
            var typeRecord = typeDatabase.GetTypeRecordOrAnyType(typeSpecForKey);
            return typeRecord.CSharpTypeName.FullyQualifiedName;
        }

        /// <summary>
        /// Creates a unique signature key for a method based on name, constructor status, and parameter types.
        /// </summary>
        protected static string GetMethodSignatureKey(MethodDecl methodDecl, ITypeDatabase typeDatabase, ILogger? logger = null)
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
                catch (Exception ex)
                {
                    // For generic type parameters or other unsupported types,
                    // use the string representation of the type spec
                    logger?.LogWarning($"GetMethodSignatureKey: Failed to resolve type '{arg.SwiftTypeSpec}' for method '{methodDecl.Name}', using string fallback: {ex.Message}");
                    paramTypes.Add(arg.SwiftTypeSpec?.ToString() ?? "unknown");
                }
            }
            var prefix = methodDecl.IsConstructor ? "ctor:" : "method:";
            return $"{prefix}{methodDecl.Name}({string.Join(",", paramTypes)})";
        }
    }
}
