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

        protected virtual void HandleBaseDecl(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnumerable<BaseDecl> decl, Conductor conductor, ITypeDatabase typeDatabase, TypeHandlerContext context, IReadOnlySet<string>? siblingPropertyNames = null, List<(string TypeName, int Start, int End)>? topLevelSpanSink = null)
        {
            // File-per-type split: record each emitted top-level type's character span in
            // the shared buffer so ModuleEmitter can slice one file per type. Populated only
            // for the outermost walk (ModuleHandler passes the sink); nested/facade recursion
            // passes null, so nested types stay inside their parent's span.
            void RecordTopLevelSpan(BaseDecl emitted, int start)
            {
                if (topLevelSpanSink == null)
                    return;
                topLevelSpanSink.Add((SplitFileNaming.LeafFor(emitted, typeDatabase), start, csWriter.CurrentOffset));
            }

            // Track emitted method signatures to avoid duplicates
            var emittedMethodSignatures = new HashSet<string>();
            // B15: Secondary dedup based on projected C# public signature
            var emittedProjectedSignatures = new HashSet<string>(StringComparer.Ordinal);
            // Track collision counts per projected key for disambiguation suffix generation
            var projectedKeyCollisionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            // FB-1b: the first-declared failable init (init?/init!) to own each projected C# key. Later
            // siblings that erase to the same TryCreate signature recover under a label-suffixed factory
            // name (TryCreateWith{DistinguishingLabel}) computed against this winner, instead of being
            // dropped as DuplicateSignature. Keyed by the label-free projected ctor key.
            var firstFailableInitByProjectedKey = new Dictionary<string, MethodDecl>(StringComparer.Ordinal);

            var sortedDecl = TopologicallySortTypes(decl);
            var emissionCtx = context.GetEmissionContext();
            var pipeline = new MemberValidationPipeline(typeDatabase);
            var validationCtx = new ValidationContext(
                typeDatabase, context.PInvokeHelperContext, emissionCtx,
                parentType: null, moduleDecl: null, siblingPropertyNames, conductor);

            // Reserve collision-suffix overrides' adopted ancestor names up front so the
            // disambiguation in the main loop is declaration-order independent (see method doc).
            PreReserveAdoptedOverrideNames(
                sortedDecl, pipeline, validationCtx, typeDatabase, siblingPropertyNames,
                context, emittedProjectedSignatures);

            // Maps each member of a same-projected-key overload group to a rank (0..n-1) in declaration order,
            // so the first-declared overload keeps the bare name and later siblings take ascending suffixes.
            // Members outside any group are absent → read as rank 0 (natural name).
            var collisionRankMap = BuildClassBodyCollisionRankMap(
                sortedDecl, pipeline, validationCtx, typeDatabase, siblingPropertyNames);

            foreach (var baseDecl in sortedDecl)
            {
                // Span start for the file-per-type split: skipped decls (which only emit an
                // `// Unsupported:` comment and `continue`) never call RecordTopLevelSpan, so
                // their bytes fall outside every type span and land in the prelude file.
                var spanStart = topLevelSpanSink != null ? csWriter.CurrentOffset : 0;

                // Attribute everything this iteration writes — into either plane — to the
                // declaration being dispatched. `using` declarations rather than blocks because the
                // body leaves through some fifteen `continue` paths; a declaration closes on every
                // one of them without re-indenting four hundred lines. The scope deliberately spans
                // the skip paths too: an `// Unsupported:` comment belongs to the declaration it
                // describes, and losing that is losing the one thing a skip diagnostic needs to say.
                var declOwner = FragmentOwners.ForDecl(baseDecl);
                using var declCsScope = csWriter.BeginFragment(declOwner);
                using var declSwiftScope = swiftWriter.BeginFragment(FragmentOwners.ForDeclWrapper(baseDecl));
                if (baseDecl is TypeDecl typeDecl)
                {
                    // Suppress underscore-prefixed types that are not structurally required
                    if (typeDecl.SwiftTypeName != null &&
                        emissionCtx.IsUnderscoreSuppressed(typeDecl.SwiftTypeName.ToString()))
                    {
                        ReportCollector.RecordTypeSkipped(typeDecl, SkipReason.UnderscorePrefixInternal,
                            "Underscore-prefixed type suppressed from public API.");
                        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, typeDecl.Name, SkipReason.UnderscorePrefixInternal,
                            declId: DeclIdFactory.ForType(typeDecl));
                        continue;
                    }

                    // Suppress @_spi types — they are only visible to SPI consumers
                    // (e.g., other SPI modules) and not part of the public API.
                    // NOTE: We specifically check IsSpiProtected, NOT IsModuleInternal,
                    // because IsModuleInternal is also set for @usableFromInline types
                    // which may still need bindings (they appear in public API signatures
                    // of @inlinable functions).
                    if (typeDecl.IsSpiProtected)
                    {
                        ReportCollector.RecordTypeSkipped(typeDecl, SkipReason.ModuleInternal,
                            "@_spi type suppressed from bindings (not part of public API).");
                        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, typeDecl.Name, SkipReason.ModuleInternal, "@_spi type",
                            DeclIdFactory.ForType(typeDecl));
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
                            "owned by SwiftBindings.Apple", DeclIdFactory.ForType(typeDecl));
                        continue;
                    }
                }

                if (baseDecl is StructDecl structDecl)
                {
                    if (SwiftUIViewDetector.IsSwiftUIView(structDecl))
                    {
                        ReportCollector.RecordTypeSkipped(structDecl, SkipReason.SwiftUIView,
                            "Type conforms to SwiftUI.View. Bridge generation available.");
                        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, structDecl.Name, SkipReason.SwiftUIView,
                            declId: DeclIdFactory.ForType(structDecl));
                        SwiftUIBridgeCollector.Collect(structDecl, context.GetEmissionContext());
                        continue;
                    }

                    // Namespace-facade short-circuit: a top-level public struct
                    // with no member surface (no properties, methods, inits,
                    // operators, subscripts, conformances) and at least one
                    // nested type is the canonical Swift "uninhabited type as
                    // namespace" idiom. Emit it as a real C# nested namespace
                    // so consumers can write `using Module.FacadeType;` instead
                    // of `using static Module.FacadeType;` or fully-qualifying
                    // every type — a namespace facade must emit as a namespace, not a class.
                    if (NamespaceFacadeDetector.IsNamespaceFacade(structDecl))
                    {
                        NamespaceFacadeEmitter.Emit(
                            csWriter, swiftWriter, structDecl, conductor, typeDatabase, context,
                            (decls, ctx) => HandleBaseDecl(csWriter, swiftWriter, decls, conductor, typeDatabase, ctx));
                        RecordTopLevelSpan(structDecl, spanStart);
                        continue;
                    }

                    if (conductor.TryGetTypeHandler(structDecl, out var handler))
                    {
                        var env = handler.Marshal(structDecl, typeDatabase);
                        handler.Emit(csWriter, swiftWriter, env, conductor, context);
                        RecordTopLevelSpan(structDecl, spanStart);
                    }
                    else
                    {
                        _logger.LogWarning($"No handler found for method {structDecl.Name}");
                        ReportCollector.RecordTypeSkipped(structDecl, SkipReason.MissingHandler, "No type handler found for struct.");
                        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, structDecl.Name, SkipReason.MissingHandler,
                            declId: DeclIdFactory.ForType(structDecl));
                    }
                }
                else if (baseDecl is ClassDecl classDecl)
                {
                    if (SwiftUIViewDetector.IsSwiftUIView(classDecl))
                    {
                        ReportCollector.RecordTypeSkipped(classDecl, SkipReason.SwiftUIView,
                            "Type conforms to SwiftUI.View. Bridge generation available.");
                        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, classDecl.Name, SkipReason.SwiftUIView,
                            declId: DeclIdFactory.ForType(classDecl));
                        SwiftUIBridgeCollector.Collect(classDecl, context.GetEmissionContext());
                        continue;
                    }

                    if (conductor.TryGetTypeHandler(classDecl, out var handler))
                    {
                        var env = handler.Marshal(classDecl, typeDatabase);
                        handler.Emit(csWriter, swiftWriter, env, conductor, context);
                        RecordTopLevelSpan(classDecl, spanStart);
                    }
                    else
                    {
                        _logger.LogWarning($"No handler found for method {classDecl.Name}");
                        ReportCollector.RecordTypeSkipped(classDecl, SkipReason.MissingHandler, "No type handler found for class.");
                        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, classDecl.Name, SkipReason.MissingHandler,
                            declId: DeclIdFactory.ForType(classDecl));
                    }
                }
                else if (baseDecl is ProtocolDecl protocolDecl)
                {
                    if (conductor.TryGetTypeHandler(protocolDecl, out var handler))
                    {
                        var env = handler.Marshal(protocolDecl, typeDatabase);
                        handler.Emit(csWriter, swiftWriter, env, conductor, context);
                        RecordTopLevelSpan(protocolDecl, spanStart);
                    }
                    else
                    {
                        _logger.LogWarning($"No handler found for method {protocolDecl.Name}");
                        ReportCollector.RecordTypeSkipped(protocolDecl, SkipReason.MissingHandler, "No type handler found for protocol.");
                        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, protocolDecl.Name, SkipReason.MissingHandler,
                            declId: DeclIdFactory.ForType(protocolDecl));
                    }
                }
                else if (baseDecl is EnumDecl enumDecl)
                {
                    // Namespace-facade short-circuit: a caseless public enum
                    // with no member surface beyond nested types is the
                    // canonical Swift "uninhabited enum as namespace" idiom
                    // (e.g., `public enum Constants { struct Foo { … } }`).
                    // Emit as a real C# nested namespace instead of the
                    // default `static partial class` so consumers see a
                    // first-class namespace rather than a member-access
                    // container — a namespace facade must emit as a namespace, not a class.
                    if (NamespaceFacadeDetector.IsNamespaceFacade(enumDecl))
                    {
                        NamespaceFacadeEmitter.Emit(
                            csWriter, swiftWriter, enumDecl, conductor, typeDatabase, context,
                            (decls, ctx) => HandleBaseDecl(csWriter, swiftWriter, decls, conductor, typeDatabase, ctx));
                        RecordTopLevelSpan(enumDecl, spanStart);
                        continue;
                    }

                    if (conductor.TryGetTypeHandler(enumDecl, out var handler))
                    {
                        var env = handler.Marshal(enumDecl, typeDatabase);
                        handler.Emit(csWriter, swiftWriter, env, conductor, context);
                        RecordTopLevelSpan(enumDecl, spanStart);
                    }
                    else
                    {
                        _logger.LogWarning($"No handler found for enum {enumDecl.Name}");
                        ReportCollector.RecordTypeSkipped(enumDecl, SkipReason.MissingHandler, "No type handler found for enum.");
                        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, enumDecl.Name, SkipReason.MissingHandler,
                            declId: DeclIdFactory.ForType(enumDecl));
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
                        // Closure-param tombstone (Layer A): when the only blocker is an
                        // unsupported closure parameter shape, emit a tombstoned-but-reachable
                        // surface so consumers see the API exists. Falls through to the regular
                        // dedup + handler.Emit pipeline; the handler routes IsClosureParamTombstone
                        // members to ClosureParamTombstoneEmitter at the top of Emit().
                        if (validationResult.Reason == SkipReason.UnsupportedClosure
                            && !methodDecl.IsAccessor
                            && ClosureParamTombstoneEmitter.IsEligible(methodDecl, typeDatabase))
                        {
                            methodDecl.IsClosureParamTombstone = true;
                            // Fall through — no `continue`. Dedup + handler.Emit run normally.
                        }
                        else if (TryBuildTrailingDefaultGateReduction(
                                     methodDecl, pipeline, validationCtx, typeDatabase,
                                     sortedDecl, siblingPropertyNames, context, out var reducedDecl))
                        {
                            // The full member is dropped solely because a trailing default-valued
                            // parameter has an unbindable type (e.g. `arrowEdge: SwiftUI.Edge = .top`).
                            // Swift lets callers omit it, so emit the reduced overload that drops the
                            // offending trailing defaults — the @_cdecl wrapper calls the Swift
                            // declaration with the kept arguments and Swift supplies the defaults.
                            // Substitute the reduced decl and fall through to the normal dedup +
                            // handler path (which emits a real C# constructor/method for it).
                            methodDecl = reducedDecl;
                            // Fall through — no `continue`.
                        }
                        else
                        {
                            if (!methodDecl.IsAccessor)
                            {
                                if (validationResult.IsSynthesized)
                                    ReportCollector.RecordMemberSynthesized(methodDecl);
                                else if (validationResult.IsRoutedElsewhere)
                                {
                                    // The open-form member is suppressed because concrete
                                    // specializations (CSM-async per-conformer overloads, or
                                    // CSM-sync generic-parent extensions) provide the public
                                    // surface. Do not emit a `// Unsupported:` comment (it would
                                    // mislead consumers reading the generated source — the API
                                    // IS callable via the alternate overloads) and do not record
                                    // as a skipped member.
                                }
                                else
                                {
                                    ReportCollector.RecordMemberSkipped(methodDecl, validationResult.Reason ?? SkipReason.Unknown, validationResult.Details ?? "");
                                    UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, methodDecl.Name, BindingItemKind.Method, validationResult.Reason ?? SkipReason.Unknown, validationResult.Details, containingDecl: methodDecl.ParentDecl);
                                }
                            }
                            continue;
                        }
                    }

                    // Dedup: primary signature dedup (stays in HandleBaseDecl — stateful, shared with post-processors)
                    var signatureKey = GetMethodSignatureKey(methodDecl, typeDatabase, _logger);
                    if (emittedMethodSignatures.Contains(signatureKey))
                    {
                        _logger.LogDebug($"Skipping duplicate method '{methodDecl.Name}' with signature: {signatureKey}");
                        if (!methodDecl.IsAccessor)
                        {
                            ReportCollector.RecordMemberSkipped(methodDecl, SkipReason.DuplicateSignature, signatureKey);
                            UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, methodDecl.Name, BindingItemKind.Method, SkipReason.DuplicateSignature, containingDecl: methodDecl.ParentDecl);
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
                        UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, methodDecl.Name, BindingItemKind.Method, SkipReason.UnsupportedSignature, "empty tuple constructor collision", containingDecl: methodDecl.ParentDecl);
                        continue;
                    }

                    // B15: Secondary dedup based on projected C# public method signature.
                    // For non-constructor methods, collisions are disambiguated with numeric suffix
                    // (e.g., HandleNextAction, HandleNextAction2). Constructors can't be renamed in C#,
                    // so constructor collisions are still skipped.
                    var projectedKey = GetProjectedCSharpMethodKey(methodDecl, typeDatabase, _logger, siblingPropertyNames);
                    // The disambiguation suffix follows the member's rank within its same-projected-key overload
                    // group (BuildCollisionRankMap, in declaration order). Rank 0 (also the default for members
                    // in no collision group) keeps the natural name; the first-declared overload owns it and
                    // later siblings take the suffixed slot — matching the C# surface earlier releases shipped.
                    int collisionIndex = collisionRankMap.GetValueOrDefault(methodDecl, 0);
                    var reservedKey = ApplyCollisionSuffixToKey(projectedKey, collisionIndex);
                    // FB-1b: recovered static-factory name for a colliding failable init; null keeps the
                    // default "TryCreate" (winner / no-collision).
                    string? failableFactoryName = null;
                    if (!emittedProjectedSignatures.Add(reservedKey))
                    {
                        if (methodDecl.IsConstructor)
                        {
                            if (methodDecl.IsFailable)
                            {
                                // FB-1b: a failable init (init?/init!) is emitted as a static `TryCreate`
                                // factory, which — unlike a real constructor whose name is the type name —
                                // CAN be renamed. Recover the otherwise-dropped overload by suffixing its
                                // distinguishing Swift argument label(s) versus the winning sibling
                                // (e.g. TryCreateWithMessengerPageId), rather than skipping it as a
                                // DuplicateSignature. The first-declared init keeps the plain `TryCreate`
                                // name (nothing already emitted is renamed).
                                firstFailableInitByProjectedKey.TryGetValue(projectedKey, out var winner);
                                failableFactoryName = BuildFailableFactoryName(methodDecl, winner, projectedKey, projectedKeyCollisionCounts);
                                // The Add(reservedKey) above reserved the CONSTRUCTOR key namespace, but the
                                // recovered factory emits under `failableFactoryName` — an ordinary static method.
                                // Its true C# signature is `{name}({inputs}, out {Self})`: the trailing `out`
                                // gives it a DIFFERENT arity than any same-named natural method (which never emits
                                // an `out`), so those never collide in C#. The only member that CAN duplicate a
                                // factory's signature is ANOTHER failable factory with the same name + input types.
                                // Reserve the factory in its own namespace (so a natural same-name method can't
                                // false-trigger an escalation) and, on a genuine factory-vs-factory clash, walk to
                                // the next free numeric suffix. Defensive: the primary label-dedup above already
                                // collapses same-label siblings and BuildFailableFactoryName's numeric fallback
                                // counter keeps unlabeled siblings distinct, so no in-tree input reaches the
                                // escalation — but the reservation closes the structural fail-open (an untracked
                                // emitted signature) so a future change to name rendering can't silently emit CS0111.
                                int factoryParen = projectedKey.IndexOf('(');
                                if (factoryParen > 0)
                                {
                                    string factoryParams = projectedKey.Substring(factoryParen);
                                    const string factoryNamespace = "failable-factory:";
                                    if (!emittedProjectedSignatures.Add(factoryNamespace + failableFactoryName + factoryParams))
                                    {
                                        int factorySuffix = 1;
                                        string escalatedName;
                                        do
                                        {
                                            escalatedName = $"{failableFactoryName}{++factorySuffix}";
                                        } while (!emittedProjectedSignatures.Add(factoryNamespace + escalatedName + factoryParams));
                                        failableFactoryName = escalatedName;
                                    }
                                }
                                _logger.LogDebug($"Recovering failable init '{methodDecl.Name}' — projected C# signature collides: {projectedKey} → {failableFactoryName}");
                                // fall through: emit the factory under the disambiguated name
                            }
                            else
                            {
                                // Real constructors can't be renamed — skip as before. The other
                                // overload that owns this projected key was already emitted in
                                // the same class body, so writing a `// Unsupported: method
                                // 'init' — C# signature collides…` comment into csWriter here
                                // would land directly above whatever is emitted next and read
                                // as if it applied to that working member. Record the skip in
                                // report.json (the audit trail) but suppress the source-level
                                // comment — a collision-suppressed unsupported annotation landing above a
                                // working member would
                                // mislead readers.
                                _logger.LogDebug($"Skipping constructor '{methodDecl.Name}' - projected C# signature collides: {projectedKey}");
                                ReportCollector.RecordMemberSkipped(methodDecl, SkipReason.DuplicateSignature, $"Projected C# constructor signature collides: {projectedKey}");
                                continue;
                            }
                        }
                        else
                        {
                            // Occupancy escalation: the rank-derived slot is already taken by an UNRELATED natural
                            // name (a method literally named to match the suffixed form). Walk to the next free
                            // suffix. Seed from the rank so an in-group member never collapses onto a lower-ranked
                            // sibling's already-reserved slot.
                            var count = Math.Max(collisionIndex,
                                projectedKeyCollisionCounts.TryGetValue(projectedKey, out var seeded) ? seeded : 0);
                            string disambiguatedKey;
                            do
                            {
                                collisionIndex = ++count;
                                disambiguatedKey = ApplyCollisionSuffixToKey(projectedKey, collisionIndex);
                            } while (!emittedProjectedSignatures.Add(disambiguatedKey));
                            projectedKeyCollisionCounts[projectedKey] = collisionIndex;

                            _logger.LogDebug($"Disambiguating method '{methodDecl.Name}' — collision #{collisionIndex + 1} for projected key: {projectedKey} → {disambiguatedKey}");
                        }
                    }
                    else
                    {
                        if (collisionIndex > 0)
                        {
                            // In-group member that claimed its rank-derived suffixed slot directly (no occupancy
                            // clash). Record the high-water mark so a later unrelated natural-name collision on the
                            // same projected key escalates ABOVE this slot rather than re-issuing it.
                            if (!projectedKeyCollisionCounts.TryGetValue(projectedKey, out var seeded) || seeded < collisionIndex)
                                projectedKeyCollisionCounts[projectedKey] = collisionIndex;
                        }
                        // FB-1b: the first failable init to claim a projected key is the winner — it keeps the
                        // plain `TryCreate` name and later same-key failable siblings disambiguate against it.
                        if (methodDecl.IsConstructor && methodDecl.IsFailable)
                            firstFailableInitByProjectedKey.TryAdd(projectedKey, methodDecl);
                    }

                    if (conductor.TryGetMethodHandler(methodDecl, out var handler))
                    {
                        // Pass property names and P/Invoke helper context to the method environment
                        var env = new MethodEnvironment(methodDecl, typeDatabase, siblingPropertyNames, context.PInvokeHelperContext, context.CompositionCollector);
                        env.CollisionIndex = collisionIndex;
                        // FB-1b: a recovered colliding failable init emits under a label-disambiguated
                        // static-factory name; null leaves the emitter's default "TryCreate".
                        env.FailableFactoryName = failableFactoryName;
                        // Scenario A: a derived override of one collision-suffixed base overload
                        // must adopt the ancestor slot's emitted name (resolved by full Swift selector,
                        // labels included) — otherwise it recomputes a suffix-free name from its own
                        // single-method class body and binds to the WRONG base slot (silent mis-dispatch).
                        // Set before Emit so every CSharpMethodName reader (signature, override modifier,
                        // forwarding overloads/bridges) and the EmittedCSharpName stamp below agree.
                        // No-op for non-overrides and for overrides whose names already match the slot.
                        env.AdoptedOverrideCSharpName = TryResolveAdoptedOverrideName(env);

                        // Scenario C (dedup parity — in-loop fallback): the override added only
                        // its LOCALLY computed key (`Process`) to emittedProjectedSignatures at the Add()
                        // above, but it EMITS under the adopted name (`Process2`). A sibling that projects
                        // to the adopted name (e.g. `process2(_:)` → `Process2`) must disambiguate to the
                        // next free suffix (`Process22`) rather than emit a duplicate `Process2` (CS0111).
                        // PreReserveAdoptedOverrideNames already reserved this key BEFORE the loop, so the
                        // outcome is declaration-order independent (a `process2(_:)` declared ahead of the
                        // override still loses the race). This in-loop Add is the fallback for the cases
                        // the pre-pass conservatively skips — the override's local key was non-unique
                        // because a validation-SKIPPED sibling shared it, so the pre-pass could not
                        // distinguish it from a self-suffixing override — and is a harmless no-op when the
                        // pre-pass already reserved the key. Uses the real CollisionIndex, so a self-
                        // suffixing override (whose own body already yields the suffixed name) resolves
                        // AdoptedOverrideCSharpName to null and reserves nothing.
                        if (env.AdoptedOverrideCSharpName != null)
                        {
                            int adoptedParen = projectedKey.IndexOf('(');
                            if (adoptedParen > 0)
                                emittedProjectedSignatures.Add(
                                    env.AdoptedOverrideCSharpName + projectedKey.Substring(adoptedParen));
                        }
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
                        {
                            methodDecl.EmittedCSharpName = env.CSharpMethodName;
                            // Record the consumer-visible contract for this emitted member —
                            // its post-collision C# signature → the entry symbol the P/Invoke binds.
                            // Recorded here (not in a later model walk) because env.CSharpMethodName's
                            // collision suffix is only known inside this disambiguation loop.
                            emissionCtx.RecordApiManifestEntry(
                                ModuleEmissionContext.BuildApiManifestKey(methodDecl.ParentDecl, env.CSharpMethodName, projectedKey, env.TypeDatabase),
                                env.EmissionSymbol);
                        }
                        // AF13: stash the emission-time symbol this method settled on, keyed by
                        // decl identity, so a later env-less emitter (e.g. the concrete-protocol
                        // specialization emitter, which historically read a sibling constructor's
                        // promoted MangledName off the shared decl) recovers it from the
                        // emission-scoped side table instead of in-place model mutation.
                        emissionCtx.RecordMethodEmissionSymbol(methodDecl, env.EmissionSymbol);
                    }
                    else
                    {
                        _logger.LogWarning($"No handler found for method {methodDecl.Name}");
                        if (!methodDecl.IsAccessor)
                        {
                            ReportCollector.RecordMemberSkipped(methodDecl, SkipReason.MissingHandler, "No method handler found.");
                            UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, methodDecl.Name, BindingItemKind.Method, SkipReason.MissingHandler, containingDecl: methodDecl.ParentDecl);
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
        /// Pre-gate trailing-default rescue. When a constructor or method is dropped by the
        /// emission gate, tries to build a reduced overload that drops the smallest suffix of
        /// trailing default-valued parameters needed to clear the gate (keeping the most
        /// parameters). Returns true with <paramref name="reducedDecl"/> only when (a) a reduced
        /// form passes the full validation pipeline and (b) no emittable sibling already projects
        /// to the same C# signature. The full member's drop is the trigger, so the rescue is
        /// purely additive — it can only turn a drop into an emit.
        ///
        /// This recovers members dropped solely because a trailing default-valued parameter has an
        /// unbindable type (e.g. a `SwiftUI.Edge arrowEdge = .top` on an otherwise bindable init):
        /// Swift lets callers omit such parameters, and the reduced overload's @_cdecl wrapper calls
        /// the Swift declaration with the kept arguments, letting Swift supply the dropped defaults.
        /// </summary>
        private bool TryBuildTrailingDefaultGateReduction(
            MethodDecl methodDecl,
            MemberValidationPipeline pipeline,
            ValidationContext validationCtx,
            ITypeDatabase typeDatabase,
            IReadOnlyList<BaseDecl> siblings,
            IReadOnlySet<string>? siblingPropertyNames,
            TypeHandlerContext context,
            out MethodDecl reducedDecl)
        {
            reducedDecl = null!;

            if (methodDecl.IsAccessor)
                return false;

            // Only rescue a member the gate dropped because of a parameter's TYPE. A module-internal
            // or compiler-synthesized (implicit inherited) constructor is dropped for a different
            // reason: the Swift declaration itself is not externally callable. Swift omits the
            // inherited initializer when a subclass declares its own designated inits, so a reduced
            // overload's @_cdecl wrapper would emit an uncompilable call (e.g. "extra argument" /
            // "missing argument") into a constructor that does not exist on the subclass.
            if (methodDecl.IsModuleInternal || methodDecl.IsImplicit)
                return false;

            int trailingDefaults = DefaultParameterOverloadEmitter.CountTrailingDefaults(methodDecl);
            if (trailingDefaults == 0)
                return false;

            // Bound by the post-processor's own overload cap to avoid pathological fan-out, and
            // walk smallest-drop first so the most parameters are kept.
            int maxDrop = Math.Min(trailingDefaults, 4);
            for (int drop = 1; drop <= maxDrop; drop++)
            {
                var candidate = DefaultParameterOverloadEmitter.BuildGateReducedDecl(methodDecl, drop);
                if (!pipeline.ValidateMethodEmission(candidate, validationCtx).ShouldEmit)
                    continue;

                // The reduced clone keeps the FULL ABI symbol (MangledName) but emits FEWER
                // arguments — correct ONLY when the candidate routes through a @_cdecl wrapper whose
                // Swift body calls the declaration with the kept args and lets Swift supply the
                // dropped trailing defaults. A candidate that passes the emission gate but CANNOT be
                // wrapped (a public member on a @usableFromInline-internal parent, a custom-actor-
                // isolated member, non-XCFramework mode, and other CannotWrap shapes) instead falls
                // back to a direct CallConvSwift P/Invoke against that full-ABI symbol; the reduced
                // arg list then mismatches the symbol's ABI → runtime crash, which no compile gate
                // catches. Decline unless a wrapper is actually emitted. A larger drop can flip a
                // CannotWrap shape (e.g. by removing a closure param), so `continue` rather than abort.
                var candidateEnv = new MethodEnvironment(
                    candidate, typeDatabase, siblingPropertyNames,
                    context.PInvokeHelperContext, context.CompositionCollector);
                var wrapperDecision = candidate.IsConstructor
                    ? WrapperValidation.DetermineConstructorWrapperDecision(candidateEnv)
                    : WrapperValidation.DetermineMethodWrapperDecision(candidateEnv);
                if (wrapperDecision != WrapperDecision.WrapperRequired)
                    continue;

                // Redundancy/collision guard: skip the rescue when an emittable sibling already
                // projects to the same C# signature. A constructor sibling would win the dedup slot
                // anyway (constructor collisions are dropped, not renamed); for both kinds the rescue
                // would otherwise duplicate a member the consumer can already call.
                var candidateKey = GetProjectedCSharpMethodKey(candidate, typeDatabase, _logger, siblingPropertyNames);
                bool siblingProvides = siblings.OfType<MethodDecl>().Any(sib =>
                    !ReferenceEquals(sib, methodDecl) &&
                    GetProjectedCSharpMethodKey(sib, typeDatabase, _logger, siblingPropertyNames) == candidateKey &&
                    pipeline.ValidateMethodEmission(sib, validationCtx).ShouldEmit);
                if (siblingProvides)
                    return false;

                reducedDecl = candidate;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Creates a projected C# method signature key for class/module/extension dedup.
        /// Uses the public method name and projected C# parameter types, so different Swift overloads
        /// that produce identical C# signatures are deduplicated. Thin shim over
        /// <see cref="ProtocolSignatureHelper.BuildProjectedMethodKey"/> on the class path.
        ///
        /// The closure-tombstone view folds the decl's own <c>IsClosureParamTombstone</c> in with
        /// <paramref name="treatAsClosureTombstone"/>: the main loop sets that flag only AT the dedup
        /// site, so a pre-pass caller (PreReserveAdoptedOverrideNames) requests the view it KNOWS the
        /// loop will take, keeping the pre-pass and main-loop keys in agreement. Computing the effective
        /// tombstone HERE (not in the core) keeps the default-overload path — which never consults the
        /// flag — byte-identical.
        /// </summary>
        internal static string GetProjectedCSharpMethodKey(MethodDecl methodDecl, ITypeDatabase typeDatabase, ILogger? logger = null, IReadOnlySet<string>? siblingPropertyNames = null, bool treatAsClosureTombstone = false)
            => ProtocolSignatureHelper.BuildProjectedMethodKey(methodDecl, typeDatabase, new ProtocolSignatureHelper.ProjectedKeyOptions
            {
                PropertyNames = siblingPropertyNames,
                TreatAsClosureTombstone = methodDecl.IsClosureParamTombstone || treatAsClosureTombstone,
                IncludeParentTypeName = true,
                Logger = logger,
            });

        /// <summary>
        /// Classifies whether <paramref name="method"/> will reach the dedup block in the main emission
        /// loop and, if so, whether it does so as a closure-param tombstone (every unsupported closure
        /// param collapsed to <c>object?</c>). Single source of truth for the pre-pass, mirroring the
        /// main loop's predicate at the dedup site so the two agree on BOTH axes:
        /// <list type="bullet">
        /// <item><description><b>Emitting partition</b> — the main loop's collision counter and projected-
        /// signature set count ONLY emitting methods (a validation-skipped non-tombstone method
        /// <c>continue</c>s before the dedup <c>Add</c>); the pre-pass must apply the same filter or a
        /// skipped sibling inflates an override's local key count and suppresses a valid pre-reservation.</description></item>
        /// <item><description><b>Tombstone view</b> — the loop sets <see cref="MethodDecl.IsClosureParamTombstone"/>
        /// AFTER this pre-pass runs, so the pre-pass predicts it here to key off the same object?-collapsed
        /// param shape the loop will dedup on.</description></item>
        /// </list>
        /// <see cref="MemberValidationPipeline.ValidateMethodEmission"/> is a pure predicate, so evaluating
        /// it here (and again in the loop) is side-effect-free.
        /// </summary>
        internal static (bool WillEmit, bool IsClosureTombstone) ClassifyOverridePrePassEmission(
            MethodDecl method, MemberValidationPipeline pipeline, ValidationContext? validationCtx, ITypeDatabase typeDatabase)
        {
            var vr = pipeline.ValidateMethodEmission(method, validationCtx);
            bool isTombstone = !vr.ShouldEmit && vr.Reason == SkipReason.UnsupportedClosure
                && !method.IsAccessor && ClosureParamTombstoneEmitter.IsEligible(method, typeDatabase);
            return (vr.ShouldEmit || isTombstone, isTombstone);
        }

        /// <summary>
        /// Declaration-order independence: reserve the adopted ancestor-slot names of same-module
        /// collision-suffix overrides BEFORE the main emission loop, so that a natural sibling projecting
        /// to the same adopted name (e.g. <c>process2(_:)</c> → <c>Process2</c>) disambiguates to the next
        /// free suffix (<c>Process22</c>) regardless of which is declared first. Without this, an override
        /// declared AFTER such a sibling finds its adopted slot already taken and silently emits a
        /// duplicate C# name → CS0111 (the in-loop reservation at the <c>Emit</c> site fires too late to
        /// stop an earlier sibling).
        ///
        /// Two guards keep this from over-reserving — both essential:
        /// <list type="bullet">
        /// <item>Validation gate — only an override that would ACTUALLY emit reserves a name, so a
        /// validation-skipped (<c>@_spi</c>/internal/unsupported) override never blocks a sibling from its
        /// natural name. (Reserving for skipped methods was the defect that retired the earlier
        /// whole-body pre-pass; here the reservation, not just the count, is validation-gated.)</item>
        /// <item>Uniqueness gate — only an override whose LOCAL projected key is unique among the body's
        /// EMITTING methods reserves. A self-suffixing override (one that overrides BOTH label-overloads, so
        /// two emitting methods share the local key) takes its suffix from the main loop's collision counter,
        /// not adoption; pre-reserving its "adopted" name would push the real suffixed slot to the next index
        /// (<c>Process3</c>) and mis-bind. The count mirrors the main loop's emitting partition
        /// (<see cref="ClassifyOverridePrePassEmission"/>): a validation-skipped non-tombstone sibling that
        /// happens to share the key is EXCLUDED from the count, so it cannot suppress an otherwise-valid
        /// pre-reservation — the defect that let an earlier-declared natural sibling steal the adopted slot
        /// (CS0111). The projected key carries name + param types only (no return type), so a sibling skipped
        /// solely for an unsupported RETURN still shares the key and was exactly such a suppressor.</item>
        /// </list>
        /// A unique local key guarantees the main loop assigns <c>CollisionIndex 0</c>, so the adopted
        /// name resolved here with a <c>CollisionIndex 0</c> environment matches what the loop computes.
        /// The throwaway <see cref="MethodEnvironment"/> is side-effect-free (it only constructs helper
        /// instances and reads the decl); <see cref="MemberValidationPipeline.ValidateMethodEmission"/> is
        /// a pure predicate (the loop records skips separately), so both are safe to evaluate twice.
        /// </summary>
        private void PreReserveAdoptedOverrideNames(
            IEnumerable<BaseDecl> sortedDecl, MemberValidationPipeline pipeline, ValidationContext validationCtx,
            ITypeDatabase typeDatabase, IReadOnlySet<string>? siblingPropertyNames, TypeHandlerContext context,
            HashSet<string> emittedProjectedSignatures)
        {
            // Conservative local-key multiset over every method in this type body that WILL EMIT.
            // Validation-skipped non-tombstone methods are EXCLUDED: the main loop's collision counter and
            // emittedProjectedSignatures set count only emitting methods (a skipped method `continue`s
            // before the dedup Add), so a skipped sibling sharing an override's projected key must NOT
            // inflate the override's local count — otherwise the count!=1 gate below suppresses a valid
            // pre-reservation and a natural adopted-name sibling (e.g. `process2(_:)`) declared earlier wins
            // the slot in the main loop, emitting a duplicate of the override's adopted name (CS0111 /
            // mis-dispatch). Each surviving key uses the SAME closure-tombstone view the main loop will take
            // (the flag is set AFTER this pre-pass runs — see ClassifyOverridePrePassEmission).
            var localKeyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var d in sortedDecl)
            {
                if (d is MethodDecl m && !m.IsConstructor)
                {
                    var (willEmit, isTomb) = ClassifyOverridePrePassEmission(m, pipeline, validationCtx, typeDatabase);
                    if (!willEmit) continue;
                    var k = GetProjectedCSharpMethodKey(m, typeDatabase, _logger, siblingPropertyNames,
                        treatAsClosureTombstone: isTomb);
                    localKeyCounts[k] = localKeyCounts.TryGetValue(k, out var c) ? c + 1 : 1;
                }
            }

            foreach (var d in sortedDecl)
            {
                if (d is not MethodDecl method) continue;
                if (!method.IsOverride || method.IsConstructor || method.IsAccessor) continue;
                if (method.ParentDecl is not ClassDecl) continue;

                // Validation gate: reserve only for an override that will actually emit (ShouldEmit, or a
                // closure-param tombstone, which still reaches the dedup block and emits a stub).
                var (willEmit, isTombstone) = ClassifyOverridePrePassEmission(method, pipeline, validationCtx, typeDatabase);
                if (!willEmit) continue;

                // Key with the loop's tombstone view so the uniqueness gate below compares against the
                // same key namespace the main loop will dedup on.
                var key = GetProjectedCSharpMethodKey(method, typeDatabase, _logger, siblingPropertyNames,
                    treatAsClosureTombstone: isTombstone);
                // Not unique → either a self-suffixing override (main loop's counter owns the suffix) or a
                // skipped-sibling collision (in-loop reservation owns it). Either way, do not pre-reserve.
                if (!localKeyCounts.TryGetValue(key, out var count) || count != 1) continue;

                // Unique local key ⇒ CollisionIndex 0 in the main loop ⇒ adoption resolves identically.
                var env = new MethodEnvironment(method, typeDatabase, siblingPropertyNames, context.PInvokeHelperContext, context.CompositionCollector);
                env.CollisionIndex = 0;
                var adopted = TryResolveAdoptedOverrideName(env);
                if (adopted == null) continue;

                int paren = key.IndexOf('(');
                if (paren > 0)
                    emittedProjectedSignatures.Add(adopted + key.Substring(paren));
            }
        }

        /// <summary>
        /// Builds the declaration-order collision-rank map for one type body. Walks <paramref name="sortedDecl"/>
        /// in the SAME order and through the SAME filter chain the main emission loop uses — validation
        /// (<see cref="ClassifyOverridePrePassEmission"/>), primary-signature dedup (first-wins on
        /// <see cref="GetMethodSignatureKey"/>), and constructor exclusion (constructors skip on collision and
        /// never take a suffix) — so each rank lines up with a method that actually reaches the dedup block.
        /// Non-constructor survivors are tagged with their tombstone-view projected key (the flag is set by the
        /// loop AFTER this runs, so the view is predicted here, matching <see cref="PreReserveAdoptedOverrideNames"/>)
        /// and handed to <see cref="BuildCollisionRankMap"/>.
        /// </summary>
        private Dictionary<MethodDecl, int> BuildClassBodyCollisionRankMap(
            IEnumerable<BaseDecl> sortedDecl, MemberValidationPipeline pipeline, ValidationContext validationCtx,
            ITypeDatabase typeDatabase, IReadOnlySet<string>? siblingPropertyNames)
        {
            var primarySeen = new HashSet<string>(StringComparer.Ordinal);
            var emitting = new List<(MethodDecl, string)>();
            foreach (var d in sortedDecl)
            {
                if (d is not MethodDecl m) continue;
                var (willEmit, isTombstone) = ClassifyOverridePrePassEmission(m, pipeline, validationCtx, typeDatabase);
                if (!willEmit) continue;
                // Primary-signature dedup (first-wins) mirrors HandleBaseDecl's emittedMethodSignatures gate —
                // a primary-duplicate sibling never reaches the projected-collision block, so it must not pad a
                // collision group with a method that does not emit.
                var signatureKey = GetMethodSignatureKey(m, typeDatabase, _logger);
                if (!primarySeen.Add(signatureKey)) continue;
                // Constructors are skipped on a projected collision (they can't be renamed) — never ranked.
                if (m.IsConstructor) continue;
                var projectedKey = GetProjectedCSharpMethodKey(m, typeDatabase, _logger, siblingPropertyNames,
                    treatAsClosureTombstone: isTombstone);
                emitting.Add((m, projectedKey));
            }
            return BuildCollisionRankMap(emitting);
        }

        /// <summary>
        /// For a same-module override of a B15 collision-suffixed base overload, returns the ancestor
        /// slot's emitted C# name so the derived override can adopt it (see
        /// <see cref="MethodEnvironment.AdoptedOverrideCSharpName"/>). The base may disambiguate two
        /// same-name/same-type overloads that differ only by Swift external argument label (e.g.
        /// <c>process(first:)</c> → <c>Process</c>, <c>process(second:)</c> → <c>Process2</c>). A derived
        /// class overriding only the suffixed one has a single method in its own body, so its local
        /// collision index is 0 and it naively computes the suffix-free name — binding to the WRONG base
        /// slot and silently mis-dispatching. We resolve the precise overridden slot by FULL Swift
        /// selector (method name + external argument labels + parameter Swift types; labels are required
        /// because the colliding overloads are identical by type alone) and return its
        /// <see cref="MethodDecl.EmittedCSharpName"/>.
        ///
        /// Returns null — leaving the derived's own computed name in place — when the method is not an
        /// override, its parent is not a class, no same-module ancestor slot matches, the matched
        /// ancestor was not emitted, or the ancestor name is NOT a pure collision-suffix variant of the
        /// derived's own computed name. The last guard keeps adoption surgical: it never rewrites a
        /// property-collision rename (e.g. base <c>Foo</c> vs derived <c>FooMethod</c>), which could
        /// introduce a CS0102 clash in the derived. The cross-module variant of this bug is a tracked
        /// residual — <see cref="TypeRecord.EmittedClassMethods"/> persists no argument labels, so a
        /// cross-module ancestor's two same-type slots are indistinguishable.
        /// </summary>
        private static string? TryResolveAdoptedOverrideName(MethodEnvironment env)
        {
            var method = env.MethodDecl;
            if (!method.IsOverride) return null;
            if (method.ParentDecl is not ClassDecl classDecl) return null;

            // Derived's own computed name BEFORE adoption (AdoptedOverrideCSharpName is still null here).
            var derivedName = env.CSharpMethodName;
            int paramCount = method.CSSignature.Count - 1;

            // Walk the resolved superclass chain; the nearest ancestor that actually emitted the
            // matching selector owns the C# slot this override binds to (Swift vtable rule).
            for (var ancestor = classDecl.ResolvedSuperclass; ancestor != null; ancestor = ancestor.ResolvedSuperclass)
            {
                foreach (var candidate in ancestor.Methods)
                {
                    if (!candidate.WasEmitted || candidate.IsAccessor || candidate.IsConstructor) continue;
                    if (candidate.Name != method.Name) continue;
                    if (candidate.CSSignature.Count - 1 != paramCount) continue;
                    if (!OverrideSelectorMatches(candidate, method, paramCount)) continue;

                    var ancestorName = candidate.EmittedCSharpName;
                    if (string.IsNullOrEmpty(ancestorName) || ancestorName == derivedName) return null;
                    // Adopt only a pure collision-suffix variant (derivedName + digits, e.g. "Process2"
                    // for "Process") — never a different rename, which could collide in the derived.
                    return IsCollisionSuffixVariant(derivedName, ancestorName) ? ancestorName : null;
                }
            }
            return null;
        }

        /// <summary>
        /// True when <paramref name="ancestor"/> and <paramref name="derived"/> are the same Swift
        /// selector: every parameter position agrees on both its Swift type spec AND its external
        /// argument label (<see cref="BaseDecl.GetSwiftName"/>, which resolves keyword escaping).
        /// Parameter counts are assumed already equal. Labels are what distinguish two overloads that
        /// share a projected C# type.
        /// </summary>
        private static bool OverrideSelectorMatches(MethodDecl ancestor, MethodDecl derived, int paramCount)
        {
            for (int i = 1; i <= paramCount; i++)
            {
                var a = ancestor.CSSignature[i];
                var d = derived.CSSignature[i];
                if (a.SwiftTypeSpec.ToString() != d.SwiftTypeSpec.ToString()) return false;
                if (a.GetSwiftName() != d.GetSwiftName()) return false;
            }
            return true;
        }

        /// <summary>
        /// True when <paramref name="candidate"/> is <paramref name="baseName"/> followed by one or
        /// more digits — the exact shape of a B15 collision suffix (<c>Process</c> → <c>Process2</c>).
        /// Confines override-name adoption to the collision-suffix case and excludes any other rename.
        /// </summary>
        private static bool IsCollisionSuffixVariant(string baseName, string candidate)
        {
            if (candidate.Length <= baseName.Length) return false;
            if (!candidate.StartsWith(baseName, StringComparison.Ordinal)) return false;
            for (int i = baseName.Length; i < candidate.Length; i++)
                if (!char.IsDigit(candidate[i])) return false;
            return true;
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
        /// FB-1b: computes the disambiguated static-factory name for a failable init (<c>init?</c>/
        /// <c>init!</c>) whose projected C# <c>TryCreate</c> signature collides with an earlier-declared
        /// sibling. The first-declared init keeps the plain <c>TryCreate</c> name; each colliding sibling
        /// is suffixed by its <b>distinguishing</b> Swift argument label(s) — the labels that differ from
        /// the <paramref name="winner"/> at the same position — producing e.g. <c>TryCreateWithMessengerPageId</c>.
        /// The colliding siblings share the same projected parameter types and arity by construction (that
        /// is what makes their keys collide), so a position-wise label comparison is well-defined; and
        /// because <see cref="GetMethodSignatureKey"/> (the primary dedup) already includes labels, any
        /// sibling reaching this projected-collision path differs from the winner in at least one usable
        /// label, so the distinguishing set is non-empty in practice. A defensive numeric fallback keeps the
        /// name unique in the pathological all-unlabeled case (which primary dedup would already have
        /// collapsed). Purely additive to the emitted surface: nothing already emitted is renamed.
        /// </summary>
        /// <param name="failableInit">The colliding failable init to name.</param>
        /// <param name="winner">The first-declared failable init that owns the plain <c>TryCreate</c> slot,
        /// or null if the slot was claimed by a non-failable constructor (then all usable labels distinguish).</param>
        /// <param name="projectedKey">The label-free projected ctor key, used only for the numeric fallback counter.</param>
        /// <param name="projectedKeyCollisionCounts">Shared per-body counter reused for the numeric fallback.</param>
        internal static string BuildFailableFactoryName(
            MethodDecl failableInit, MethodDecl? winner, string projectedKey,
            Dictionary<string, int> projectedKeyCollisionCounts)
        {
            var sb = new System.Text.StringBuilder();
            var winnerArgs = winner?.CSSignature;
            // CSSignature[0] is the return type; real parameters start at index 1.
            for (int i = 1; i < failableInit.CSSignature.Count; i++)
            {
                var arg = failableInit.CSSignature[i];
                if (arg.SwiftTypeSpec == null || arg.SwiftTypeSpec.IsEmptyTuple)
                    continue;
                var label = arg.GetSwiftName();
                if (string.IsNullOrEmpty(label) || label == "_" || SwiftBuilder.IsAutoGeneratedArgName(label))
                    continue;
                // A label shared with the winner at the same position does not distinguish this overload.
                if (winnerArgs != null && i < winnerArgs.Count && winnerArgs[i].GetSwiftName() == label)
                    continue;
                sb.Append(char.ToUpperInvariant(label[0]));
                if (label.Length > 1)
                    sb.Append(label.Substring(1));
            }

            if (sb.Length > 0)
                return $"TryCreateWith{sb}";

            // Pathological fallback: no usable distinguishing label (e.g. all positional/synthesized).
            // Take the next free numeric suffix on the shared per-body counter so the name is still unique
            // and never collapses onto the winner's "TryCreate".
            var next = (projectedKeyCollisionCounts.TryGetValue(projectedKey, out var seeded) ? seeded : 0) + 1;
            projectedKeyCollisionCounts[projectedKey] = next;
            return $"TryCreate{next + 1}";
        }

        /// <summary>
        /// Assigns the numeric disambiguation suffix for same-projected-C#-key overload collision groups in
        /// SOURCE/DECLARATION order: within a group, the first overload the emission walk reaches keeps the
        /// natural (unsuffixed) name and later siblings take ascending suffixes (rank 1 → <c>…2</c>, etc.).
        /// Each caller passes the methods that will actually emit (its own emitting partition:
        /// validation-passed, primary-signature-deduped, constructors excluded), tagged with the projected
        /// C# key they dedup on, IN the order the caller's topo-sorted emission loop visits them — so a
        /// group list's index IS that declaration order.
        ///
        /// Why declaration order and not a content-derived rule (e.g. alphabetical by Swift signature): the
        /// binding's emitted C# surface is its consumer contract, and the first-declared overload is the
        /// least-surprising owner of the bare name. A content-derived rank was prototyped and rejected — it
        /// renamed overloads already shipped in a prior release (a consumer's named-argument call against the
        /// bare name silently retargeted to a different parameter set, e.g. <c>GeneratePlane(width:height:)</c>
        /// → <c>(width:depth:)</c>) to buy invariance under source reordering. That trades a break to the
        /// published surface for protection against a reorder the generator itself controls. Genuine
        /// name↔symbol retargets are caught instead by the api-manifest ratchet, which fires precisely when a
        /// stable C# signature rebinds to a different native symbol — the consumer-visible event worth
        /// surfacing.
        ///
        /// Methods NOT in any multi-member group are absent from the returned map; the caller reads them as
        /// rank 0 and they keep the natural-name-first behavior. The returned map is keyed by reference
        /// identity (<see cref="MethodDecl"/> is a record, so value equality would conflate distinct
        /// same-signature siblings).
        /// </summary>
        internal static Dictionary<MethodDecl, int> BuildCollisionRankMap(
            IReadOnlyList<(MethodDecl Method, string ProjectedKey)> emittingMethods)
        {
            var groups = new Dictionary<string, List<MethodDecl>>(StringComparer.Ordinal);
            foreach (var (method, projectedKey) in emittingMethods)
            {
                if (!groups.TryGetValue(projectedKey, out var list))
                    groups[projectedKey] = list = new List<MethodDecl>();
                list.Add(method);
            }

            var rankMap = new Dictionary<MethodDecl, int>(ReferenceEqualityComparer.Instance);
            foreach (var list in groups.Values)
            {
                if (list.Count < 2) continue; // no collision → natural name, absent from the map (rank 0)
                // `list` is populated in the caller's declaration/topo-sort walk order, so its index IS that
                // source order: the first-declared overload takes rank 0 (bare name), later siblings ascend.
                for (int rank = 0; rank < list.Count; rank++)
                    rankMap[list[rank]] = rank;
            }
            return rankMap;
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
                // A container-shaped metatype ([T].Type) is not a collection. Skip the
                // IEnumerable<T> normalization below so the overload key routes it through the
                // SAME GetTypeRecordOrAnyType resolution the signature path uses (TypeProjectionFactory
                // returns null for a metatype, then the DB fallback yields AnyType), keeping key and
                // signature consistent by construction for this shape. In practice a metatype param
                // resolves to an AnyType placeholder and the method is skipped before it is keyed, so
                // this is a consistency guard, not the fix for an observable collision.
                if (WrapperValidation.IsMetatypeType(namedSpec))
                    return typeDatabase.GetTypeRecordOrAnyType(typeSpecForKey).CSharpTypeName.FullyQualifiedName;

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
        /// Creates a unique signature key for a method based on Swift-level identity:
        /// constructor status, name, async/throws qualifiers, and parameter labels+types.
        ///
        /// Including labels distinguishes Swift overloads that differ only in argument labels
        /// (e.g. `request(_:didCreateTask:)` vs `request(_:didReceiveTask:)`) — both have
        /// identical positional types but represent different methods. Async and throws are
        /// included so that `f()` and `f() async` (or `f() throws`) flow through to secondary
        /// (projected C#) dedup, which renames the second via numeric suffix instead of
        /// silently dropping it.
        ///
        /// Used as a stateful HashSet key by both <see cref="HandleBaseDecl"/> (class/struct
        /// methods) and <see cref="ModuleHandler"/> (free functions). The
        /// <see cref="ProtocolSignatureHelper.GetMethodSignatureKey"/> variant intentionally
        /// stays label-free — it doubles as the witness-matching key on the protocol side
        /// and matching is positional, not by label.
        /// </summary>
        protected static string GetMethodSignatureKey(MethodDecl methodDecl, ITypeDatabase typeDatabase, ILogger? logger = null)
        {
            var paramEntries = new List<string>();
            // Skip first element (return type) in CSSignature
            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var arg = methodDecl.CSSignature[i];
                string paramType = BuildPrimaryParamKey(arg.SwiftTypeSpec, typeDatabase, methodDecl.Name, logger);
                // Label is the external Swift argument label (or "argN" for `_`-prefixed
                // positional labels — synthesized by SwiftABIParser.ExtractParameterNames).
                paramEntries.Add($"{arg.Name}:{paramType}");
            }
            var prefix = methodDecl.IsConstructor ? "ctor:" : "method:";
            var qualifiers = new System.Text.StringBuilder();
            if (methodDecl.IsAsync) qualifiers.Append("|async");
            if (methodDecl.Throws) qualifiers.Append("|throws");
            return $"{prefix}{methodDecl.Name}{qualifiers}({string.Join(",", paramEntries)})";
        }

        /// <summary>
        /// Builds the per-parameter contribution to <see cref="GetMethodSignatureKey"/>.
        /// Recursively encodes generic-argument types so that e.g. <c>Array&lt;URL&gt;</c>
        /// and <c>Array&lt;ImageRequest&gt;</c> produce distinct keys — the previous
        /// implementation collapsed both to bare <c>Swift.SwiftArray</c> (the container's
        /// resolved C# name without generic args), causing primary dedup to silently drop
        /// every container-typed overload past the first one.
        /// </summary>
        private static string BuildPrimaryParamKey(TypeSpec? typeSpec, ITypeDatabase typeDatabase, string methodName, ILogger? logger)
        {
            if (typeSpec is null) return "unknown";
            if (typeSpec is NamedTypeSpec named)
            {
                string baseName;
                try
                {
                    var typeRecord = typeDatabase.GetTypeRecordOrAnyType(named);
                    baseName = typeRecord.CSharpTypeName.FullyQualifiedName;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning($"GetMethodSignatureKey: Failed to resolve type '{named}' for method '{methodName}', using string fallback: {ex.Message}");
                    baseName = named.Name;
                }
                if (named.GenericParameters.Count == 0)
                    return baseName;
                var args = string.Join(",", named.GenericParameters.Select(g => BuildPrimaryParamKey(g, typeDatabase, methodName, logger)));
                return $"{baseName}<{args}>";
            }
            if (typeSpec is TupleTypeSpec tuple)
            {
                if (tuple.IsEmptyTuple) return "Swift.Void";
                var elems = string.Join(",", tuple.Elements.Select(e => BuildPrimaryParamKey(e, typeDatabase, methodName, logger)));
                return $"({elems})";
            }
            try
            {
                var typeRecord = typeDatabase.GetTypeRecordOrAnyType(typeSpec);
                return typeRecord.CSharpTypeName.FullyQualifiedName;
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"GetMethodSignatureKey: Failed to resolve type '{typeSpec}' for method '{methodName}', using string fallback: {ex.Message}");
                return typeSpec.ToString() ?? "unknown";
            }
        }
    }
}
