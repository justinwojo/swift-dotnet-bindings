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
            // Track collision counts per projected key for disambiguation suffix generation
            var projectedKeyCollisionCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            var sortedDecl = TopologicallySortTypes(decl);
            var emissionCtx = context.GetEmissionContext();
            var pipeline = new MemberValidationPipeline(typeDatabase);
            var validationCtx = new ValidationContext(
                typeDatabase, context.PInvokeHelperContext, emissionCtx,
                parentType: null, moduleDecl: null, siblingPropertyNames, conductor);
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
                        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, typeDecl.Name, SkipReason.UnderscorePrefixInternal);
                        continue;
                    }

                    // Suppress @_spi types — they are only visible to SPI consumers
                    // (e.g., other Stripe modules) and not part of the public API.
                    // NOTE: We specifically check IsSpiProtected, NOT IsModuleInternal,
                    // because IsModuleInternal is also set for @usableFromInline types
                    // which may still need bindings (they appear in public API signatures
                    // of @inlinable functions).
                    if (typeDecl.IsSpiProtected)
                    {
                        ReportCollector.RecordTypeSkipped(typeDecl, SkipReason.ModuleInternal,
                            "@_spi type suppressed from bindings (not part of public API).");
                        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, typeDecl.Name, SkipReason.ModuleInternal, "@_spi type");
                        continue;
                    }

                    // Suppress types that the Apple supplement (SwiftBindings.Apple) already
                    // owns. Without this gate, framework packages (CryptoKit, Foundation, etc.)
                    // re-emit parallel copies of supplement-owned types (e.g.
                    // CryptoKit.P256.Signing.ECDSASignature) alongside the supplement's
                    // canonical Swift.CryptoKit.* projection, breaking cross-module identity.
                    // AppleSupplementResolver.TryResolve only succeeds when the identity is in
                    // the Apple types manifest AND the registry resolves it to the supplement,
                    // so types outside the manifest (e.g. P256 namespace containers) still emit
                    // locally. The supplement's own emission path (AppleTypesCsEmitter) does
                    // NOT go through HandleBaseDecl, so this gate never affects supplement builds.
                    if (typeDecl.SwiftTypeName != null &&
                        AppleSupplementResolver.TryResolve(
                            typeDecl.SwiftTypeName,
                            typeDecl.SwiftTypeName.Module,
                            out _))
                    {
                        ReportCollector.RecordTypeSkipped(typeDecl, SkipReason.OwnedByAppleSupplement,
                            $"Type '{typeDecl.SwiftTypeName.ModuleQualifiedName}' is owned by SwiftBindings.Apple; consume the supplement projection instead.");
                        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, typeDecl.Name, SkipReason.OwnedByAppleSupplement,
                            "owned by SwiftBindings.Apple");
                        continue;
                    }
                }

                if (baseDecl is StructDecl structDecl)
                {
                    if (SwiftUIViewDetector.IsSwiftUIView(structDecl))
                    {
                        ReportCollector.RecordTypeSkipped(structDecl, SkipReason.SwiftUIView,
                            "Type conforms to SwiftUI.View. Bridge generation available.");
                        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, structDecl.Name, SkipReason.SwiftUIView);
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
                        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, structDecl.Name, SkipReason.MissingHandler);
                    }
                }
                else if (baseDecl is ClassDecl classDecl)
                {
                    if (SwiftUIViewDetector.IsSwiftUIView(classDecl))
                    {
                        ReportCollector.RecordTypeSkipped(classDecl, SkipReason.SwiftUIView,
                            "Type conforms to SwiftUI.View. Bridge generation available.");
                        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, classDecl.Name, SkipReason.SwiftUIView);
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
                        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, classDecl.Name, SkipReason.MissingHandler);
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
                        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, protocolDecl.Name, SkipReason.MissingHandler);
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
                        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, enumDecl.Name, SkipReason.MissingHandler);
                    }
                }
                else if (baseDecl is MethodDecl methodDecl)
                {
                    // Pipeline: unified emission validation (replaces inline SPI, implicit+overriding,
                    // synthesized protocol, ShouldSkipMethodEmission, hard gates, and constraint checks).
                    // Runs BEFORE dedup to match original behavior — skipped methods must not
                    // reserve dedup keys (an SPI method shouldn't block a non-SPI method with
                    // the same signature).
                    var validationResult = pipeline.ValidateMethodEmission(methodDecl, validationCtx);
                    if (!validationResult.ShouldEmit)
                    {
                        if (!methodDecl.IsAccessor)
                        {
                            if (validationResult.IsSynthesized)
                                ReportCollector.RecordMemberSynthesized(methodDecl);
                            else
                            {
                                ReportCollector.RecordMemberSkipped(methodDecl, validationResult.Reason ?? SkipReason.Unknown, validationResult.Details ?? "");
                                UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, methodDecl.Name, BindingItemKind.Method, validationResult.Reason ?? SkipReason.Unknown, validationResult.Details);
                            }
                        }
                        continue;
                    }

                    // Dedup: primary signature dedup (stays in HandleBaseDecl — stateful, shared with post-processors)
                    var signatureKey = GetMethodSignatureKey(methodDecl, typeDatabase, _logger);
                    if (emittedMethodSignatures.Contains(signatureKey))
                    {
                        _logger.LogDebug($"Skipping duplicate method '{methodDecl.Name}' with signature: {signatureKey}");
                        if (!methodDecl.IsAccessor)
                        {
                            ReportCollector.RecordMemberSkipped(methodDecl, SkipReason.DuplicateSignature, signatureKey);
                            UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, methodDecl.Name, BindingItemKind.Method, SkipReason.DuplicateSignature);
                        }
                        continue;
                    }
                    emittedMethodSignatures.Add(signatureKey);

                    // Empty-tuple constructor collision (ordering-dependent, dedup-adjacent)
                    if (methodDecl.IsConstructor &&
                        ConstructorHandler.HasOnlyEmptyTupleParams(methodDecl) &&
                        ConstructorHandler.HasParameterlessConstructorSibling(methodDecl))
                    {
                        _logger.LogDebug($"Skipping constructor '{methodDecl.Name}': becomes parameterless after empty tuple removal, collides with existing constructor.");
                        ReportCollector.RecordMemberSkipped(methodDecl,
                            SkipReason.UnsupportedSignature, "Constructor has only empty tuple () parameters; would duplicate existing parameterless constructor.");
                        UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, methodDecl.Name, BindingItemKind.Method, SkipReason.UnsupportedSignature, "empty tuple constructor collision");
                        continue;
                    }

                    // B15: Secondary dedup based on projected C# public method signature.
                    // For non-constructor methods, collisions are disambiguated with numeric suffix
                    // (e.g., HandleNextAction, HandleNextAction2). Constructors can't be renamed in C#,
                    // so constructor collisions are still skipped.
                    var projectedKey = GetProjectedCSharpMethodKey(methodDecl, typeDatabase, _logger);
                    int collisionIndex = 0;
                    if (!emittedProjectedSignatures.Add(projectedKey))
                    {
                        if (methodDecl.IsConstructor)
                        {
                            // Constructors can't be renamed — skip as before
                            _logger.LogDebug($"Skipping constructor '{methodDecl.Name}' - projected C# signature collides: {projectedKey}");
                            ReportCollector.RecordMemberSkipped(methodDecl, SkipReason.DuplicateSignature, $"Projected C# constructor signature collides: {projectedKey}");
                            UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, methodDecl.Name, BindingItemKind.Method, SkipReason.DuplicateSignature);
                            continue;
                        }

                        // Disambiguate non-constructor methods with numeric suffix.
                        // Loop until a free suffix is found — a natural method name like "Process2"
                        // could already occupy the suffixed slot.
                        if (!projectedKeyCollisionCounts.TryGetValue(projectedKey, out var count))
                            count = 0;
                        string disambiguatedKey;
                        do
                        {
                            collisionIndex = ++count;
                            disambiguatedKey = ApplyCollisionSuffixToKey(projectedKey, collisionIndex);
                        } while (!emittedProjectedSignatures.Add(disambiguatedKey));
                        projectedKeyCollisionCounts[projectedKey] = collisionIndex;

                        _logger.LogDebug($"Disambiguating method '{methodDecl.Name}' — collision #{collisionIndex + 1} for projected key: {projectedKey} → {disambiguatedKey}");
                    }

                    if (conductor.TryGetMethodHandler(methodDecl, out var handler))
                    {
                        // Pass property names and P/Invoke helper context to the method environment
                        var env = new MethodEnvironment(methodDecl, typeDatabase, siblingPropertyNames, context.PInvokeHelperContext, context.CompositionCollector);
                        env.CollisionIndex = collisionIndex;
                        // C6/C7: Share projected signature set so DefaultParameterOverloadEmitter
                        // can dedup against methods already emitted from the main pass
                        env.EmittedProjectedSignatures = emittedProjectedSignatures;
                        handler.Emit(csWriter, swiftWriter, env, conductor, context);
                        // Stamp the actual emitted C# name on the decl while the env is still
                        // alive (CollisionIndex is set here and nowhere else). This is the only
                        // single source of truth for the post-disambiguation name — recomputing
                        // later via NameProvider misses the collision suffix. Read by
                        // ClassHandler.PopulateEmittedClassMethods for the cross-module override
                        // verifier so a parent emitted as `Foo2` is recorded as `Foo2`, not `Foo`.
                        if (methodDecl.WasEmitted)
                            methodDecl.EmittedCSharpName = env.CSharpMethodName;
                    }
                    else
                    {
                        _logger.LogWarning($"No handler found for method {methodDecl.Name}");
                        if (!methodDecl.IsAccessor)
                        {
                            ReportCollector.RecordMemberSkipped(methodDecl, SkipReason.MissingHandler, "No method handler found.");
                            UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, methodDecl.Name, BindingItemKind.Method, SkipReason.MissingHandler);
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

            // Build the set of generic parameter names visible in this method's scope —
            // both the parent type's params (e.g. `Value` from `class FromToByAction<Value>`)
            // and the method's own params. swift-api-digester emits the source-level name
            // ("Value") in kGenericTypeParam.printedName for compiled .swiftmodules, NOT the
            // ABI-canonical `τ_0_0` form, so IsGenericTypeParameter alone misses these. The
            // set is used to collapse `Optional<Value>` onto bare `Value` for overload-key
            // dedup (RealityFoundation FromToByAction CS0111 trigger).
            var parentGenericNames = CollectVisibleGenericParamNames(methodDecl);

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
                // Optional<GenericParam> and bare GenericParam collapse to the same overload
                // for reference-constrained T (and the compiler emits CS0111 for unconstrained
                // T too — `Foo<T>(T)` and `Foo<T>(T?)` would conflict for `T = string`). Unwrap
                // the Optional layer when its element is a generic-param reference visible in
                // this method's scope.
                if (typeSpecForKey is NamedTypeSpec optGenericSpec &&
                    optGenericSpec.Name == "Swift.Optional" &&
                    optGenericSpec.GenericParameters.Count == 1 &&
                    IsGenericParamReference(optGenericSpec.GenericParameters[0], parentGenericNames))
                {
                    typeSpecForKey = optGenericSpec.GenericParameters[0];
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
        /// Collects the names of every generic parameter visible inside <paramref name="methodDecl"/> —
        /// both the method's own generic parameters and any walked-up parent type parameters
        /// (struct/class generics + their enclosing nested-type chain). Both the ABI-canonical
        /// (<c>τ_0_0</c>) and source-level sugared (<c>Value</c>, <c>Element</c>) names are
        /// included, since swift-api-digester emits either depending on the surrounding
        /// declaration shape. Used to recognise <c>Optional&lt;GenericParam&gt;</c> for the
        /// overload-identity unwrap in <see cref="GetProjectedCSharpMethodKey"/>.
        /// </summary>
        internal static HashSet<string> CollectVisibleGenericParamNames(MethodDecl methodDecl)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            void Add(GenericArgumentDecl g)
            {
                if (!string.IsNullOrEmpty(g.TypeName)) names.Add(g.TypeName);
                if (!string.IsNullOrEmpty(g.SugaredTypeName)) names.Add(g.SugaredTypeName);
            }

            foreach (var g in methodDecl.GenericParameters)
                Add(g);

            // Walk every enclosing TypeDecl — nested generic types contribute their parameters
            // (e.g. `Outer<A>.Inner<B>` exposes both A and B inside Inner's methods).
            BaseDecl? cursor = methodDecl.ParentDecl;
            while (cursor is TypeDecl td)
            {
                foreach (var g in td.GenericParameters)
                    Add(g);
                cursor = td.ParentDecl;
            }
            return names;
        }

        /// <summary>
        /// Returns true when <paramref name="typeSpec"/> is a NamedTypeSpec whose name refers to
        /// a generic parameter visible in the method's scope. Combines the explicit
        /// <paramref name="visibleGenericNames"/> set (collected from parent + method generic
        /// parameters) with the heuristic <see cref="TypeSpecHelpers.IsGenericTypeParameter(string)"/>
        /// recogniser (catches τ_*_* even when the parent decl wasn't fully populated, e.g. for
        /// detached test fixtures).
        /// </summary>
        private static bool IsGenericParamReference(TypeSpec typeSpec, HashSet<string> visibleGenericNames)
        {
            if (typeSpec is not NamedTypeSpec named)
                return false;
            if (visibleGenericNames.Contains(named.Name))
                return true;
            return TypeSpecHelpers.IsGenericTypeParameter(named.Name);
        }

        /// <summary>
        /// Applies a collision disambiguation suffix to a projected C# method key.
        /// The key format is "MethodName(type1,type2,...)" — the suffix is inserted
        /// before the opening parenthesis (e.g., "Foo(int)" → "Foo2(int)").
        /// </summary>
        /// <param name="projectedKey">The base projected key without suffix.</param>
        /// <param name="collisionIndex">The collision index (1-based: 1 → suffix "2", 2 → suffix "3", etc.).</param>
        /// <returns>The disambiguated key, or the original key if collisionIndex is 0.</returns>
        internal static string ApplyCollisionSuffixToKey(string projectedKey, int collisionIndex)
        {
            if (collisionIndex <= 0) return projectedKey;
            var parenIndex = projectedKey.IndexOf('(');
            if (parenIndex < 0) return $"{projectedKey}{collisionIndex + 1}";
            return $"{projectedKey[..parenIndex]}{collisionIndex + 1}{projectedKey[parenIndex..]}";
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
                // Optional<GenericParam> and bare GenericParam produce the same dedup key.
                // Reference-constrained generics treat T? and T as the same overload (CS0111).
                // TypeProjectionFactory returns null for unresolved generic params, so the DB
                // fallback yields different names (SwiftOptional vs AnyType) without this branch.
                if (namedSpec.Name == "Swift.Optional" && namedSpec.GenericParameters.Count == 1 &&
                    TypeSpecHelpers.IsGenericTypeParameter(namedSpec.GenericParameters[0]))
                {
                    return NormalizeContainerForOverloadKey(namedSpec.GenericParameters[0], typeDatabase);
                }
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
