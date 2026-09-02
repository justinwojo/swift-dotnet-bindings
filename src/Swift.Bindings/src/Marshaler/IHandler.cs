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
            // Companion shape table for the set above: reserved key → how many of its parameters a
            // caller MUST supply. The key alone cannot answer that (it carries types, not
            // defaultedness), and without it a synthesized overload cannot tell whether a consumer
            // call site would bind it and an already-emitted member equally well (CS0121).
            var reservedOverloadShapes = new Dictionary<string, int>(StringComparer.Ordinal);
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

            // Reserve disambiguating overrides' adopted ancestor names up front so the resolution in
            // the main loop is declaration-order independent (see method doc).
            PreReserveAdoptedOverrideNames(
                sortedDecl, pipeline, validationCtx, typeDatabase, siblingPropertyNames,
                context, emittedProjectedSignatures);

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
                    // Gate 0: a type a previous attempt threw while lowering. The containment seam
                    // below would refuse to dispatch it in any case; denying it here as well is what
                    // makes the omission visible, so the re-emitted binding carries the same
                    // tombstone as any other refused type instead of the type simply vanishing.
                    if (EmitterFaultGate.IsDenied(DeclIdFactory.ForType(typeDecl), out var typeFaultDetails))
                    {
                        ReportCollector.RecordTypeSkipped(typeDecl, SkipReason.EmitterFault, typeFaultDetails);
                        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, typeDecl.Name, SkipReason.EmitterFault,
                            typeFaultDetails, typeDecl.ParentDecl, DeclIdFactory.ForType(typeDecl));
                        continue;
                    }

                    // Suppress underscore-prefixed types that are not structurally required
                    if (typeDecl.SwiftTypeName != null &&
                        emissionCtx.IsUnderscoreSuppressed(typeDecl.SwiftTypeName.ToString()))
                    {
                        ReportCollector.RecordTypeSkipped(typeDecl, SkipReason.UnderscorePrefixInternal,
                            "Underscore-prefixed type suppressed from public API.");
                        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, typeDecl.Name, SkipReason.UnderscorePrefixInternal,
                            containingDecl: typeDecl.ParentDecl, declId: DeclIdFactory.ForType(typeDecl));
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
                            typeDecl.ParentDecl, DeclIdFactory.ForType(typeDecl));
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
                            "owned by SwiftBindings.Apple", typeDecl.ParentDecl, DeclIdFactory.ForType(typeDecl));
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
                            containingDecl: structDecl.ParentDecl, declId: DeclIdFactory.ForType(structDecl));
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
                        // Containment seam: an exception anywhere under this type's lowering
                        // denies the type and re-emits the module without it, rather than taking
                        // the whole binding down. Identity is computed before dispatch because
                        // emission mutates the declaration it is handed.
                        EmissionSeam.Guard(structDecl, RecoveryScope.TypeRepresentation, null, () =>
                        {
                            var env = handler.Marshal(structDecl, typeDatabase);
                            handler.Emit(csWriter, swiftWriter, env, conductor, context);
                        });
                        RecordTopLevelSpan(structDecl, spanStart);
                    }
                    else
                    {
                        _logger.LogWarning($"No handler found for method {structDecl.Name}");
                        ReportCollector.RecordTypeSkipped(structDecl, SkipReason.MissingHandler, "No type handler found for struct.");
                        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, structDecl.Name, SkipReason.MissingHandler,
                            containingDecl: structDecl.ParentDecl, declId: DeclIdFactory.ForType(structDecl));
                    }
                }
                else if (baseDecl is ClassDecl classDecl)
                {
                    if (SwiftUIViewDetector.IsSwiftUIView(classDecl))
                    {
                        ReportCollector.RecordTypeSkipped(classDecl, SkipReason.SwiftUIView,
                            "Type conforms to SwiftUI.View. Bridge generation available.");
                        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, classDecl.Name, SkipReason.SwiftUIView,
                            containingDecl: classDecl.ParentDecl, declId: DeclIdFactory.ForType(classDecl));
                        SwiftUIBridgeCollector.Collect(classDecl, context.GetEmissionContext());
                        continue;
                    }

                    if (conductor.TryGetTypeHandler(classDecl, out var handler))
                    {
                        // Containment seam: an exception anywhere under this type's lowering
                        // denies the type and re-emits the module without it, rather than taking
                        // the whole binding down. Identity is computed before dispatch because
                        // emission mutates the declaration it is handed.
                        EmissionSeam.Guard(classDecl, RecoveryScope.TypeRepresentation, null, () =>
                        {
                            var env = handler.Marshal(classDecl, typeDatabase);
                            handler.Emit(csWriter, swiftWriter, env, conductor, context);
                        });
                        RecordTopLevelSpan(classDecl, spanStart);
                    }
                    else
                    {
                        _logger.LogWarning($"No handler found for method {classDecl.Name}");
                        ReportCollector.RecordTypeSkipped(classDecl, SkipReason.MissingHandler, "No type handler found for class.");
                        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, classDecl.Name, SkipReason.MissingHandler,
                            containingDecl: classDecl.ParentDecl, declId: DeclIdFactory.ForType(classDecl));
                    }
                }
                else if (baseDecl is ProtocolDecl protocolDecl)
                {
                    if (conductor.TryGetTypeHandler(protocolDecl, out var handler))
                    {
                        // Containment seam: an exception anywhere under this type's lowering
                        // denies the type and re-emits the module without it, rather than taking
                        // the whole binding down. Identity is computed before dispatch because
                        // emission mutates the declaration it is handed.
                        EmissionSeam.Guard(protocolDecl, RecoveryScope.TypeRepresentation, null, () =>
                        {
                            var env = handler.Marshal(protocolDecl, typeDatabase);
                            handler.Emit(csWriter, swiftWriter, env, conductor, context);
                        });
                        RecordTopLevelSpan(protocolDecl, spanStart);
                    }
                    else
                    {
                        _logger.LogWarning($"No handler found for method {protocolDecl.Name}");
                        ReportCollector.RecordTypeSkipped(protocolDecl, SkipReason.MissingHandler, "No type handler found for protocol.");
                        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, protocolDecl.Name, SkipReason.MissingHandler,
                            containingDecl: protocolDecl.ParentDecl, declId: DeclIdFactory.ForType(protocolDecl));
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
                        // Containment seam: an exception anywhere under this type's lowering
                        // denies the type and re-emits the module without it, rather than taking
                        // the whole binding down. Identity is computed before dispatch because
                        // emission mutates the declaration it is handed.
                        EmissionSeam.Guard(enumDecl, RecoveryScope.TypeRepresentation, null, () =>
                        {
                            var env = handler.Marshal(enumDecl, typeDatabase);
                            handler.Emit(csWriter, swiftWriter, env, conductor, context);
                        });
                        RecordTopLevelSpan(enumDecl, spanStart);
                    }
                    else
                    {
                        _logger.LogWarning($"No handler found for enum {enumDecl.Name}");
                        ReportCollector.RecordTypeSkipped(enumDecl, SkipReason.MissingHandler, "No type handler found for enum.");
                        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, enumDecl.Name, SkipReason.MissingHandler,
                            containingDecl: enumDecl.ParentDecl, declId: DeclIdFactory.ForType(enumDecl));
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
                                     methodDecl, validationResult.Reason, pipeline, validationCtx,
                                     typeDatabase, sortedDecl, siblingPropertyNames, context,
                                     out var reducedDecl))
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
                                    // Report-only enrichment; the comment emitter below writes the
                                    // unenriched string into generated source, which is compared.
                                    ReportCollector.RecordMemberSkipped(
                                        methodDecl, validationResult.Reason ?? SkipReason.Unknown,
                                        (validationResult.Details ?? "") + UnresolvedAppleTypes.DescribeSuffix(
                                            methodDecl, typeDatabase, methodDecl.ModuleDecl?.Name));
                                    UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, methodDecl.Name, BindingItemKind.Method, validationResult.Reason ?? SkipReason.Unknown, validationResult.Details, containingDecl: methodDecl.ParentDecl);
                                }
                            }
                            continue;
                        }
                    }

                    // Ahead of every dedup reservation below — the signature key, the projected key and
                    // the adopted-override key. A denied method that reserved those first would take a
                    // C# name it never emits under, pushing a sibling that projects to the same name
                    // onto a collision suffix or out of the binding entirely, so one faulted member
                    // would cost two.
                    if (EmissionSeam.TryDenyUpFront(methodDecl, csWriter))
                        continue;

                    // The projected names this iteration itself claims below, in claim order. Emit has
                    // internal skip-and-return paths, so a name claimed ahead of it can outlive its
                    // claimant and cost a SECOND member: a sibling projecting to the same name is then
                    // dropped as a duplicate of something that never reached the output. Only keys this
                    // iteration ADDS are tracked, so a key an earlier member owns (which the failable-
                    // factory recovery arm reads without claiming) stays with its owner, and an adopted
                    // override name reserved before the loop stays reserved — that reservation is what
                    // makes the outcome independent of declaration order, so it must survive a claimant
                    // that skips inside Emit.
                    List<string>? claimedProjectedKeys = null;
                    void ClaimProjectedKey(string claimed) => (claimedProjectedKeys ??= new List<string>(2)).Add(claimed);
                    // The failable-init winner slot, when THIS member opened it.
                    string? claimedFailableWinnerKey = null;

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

                    // Secondary dedup based on projected C# public method signature. For non-constructor
                    // methods, colliding overloads are disambiguated by their own Swift argument labels or
                    // parameter types (HandleNextAction / HandleNextActionForUser). Constructors can't be
                    // renamed in C#, so constructor collisions are still skipped.
                    var projectedKey = GetProjectedCSharpMethodKey(methodDecl, typeDatabase, _logger, siblingPropertyNames);
                    // The disambiguated base name comes from the member's own signature, so it is stable
                    // against a sibling being inserted upstream — see OverloadNameDisambiguator. Members in
                    // no collision family are absent from the map and read as Natural (null input).
                    var overloadName = OverloadNameDisambiguator.ForMethod(methodDecl, typeDatabase);
                    if (overloadName.IsRefused)
                    {
                        _logger.LogDebug($"Skipping method '{methodDecl.Name}' — no argument label or parameter type distinguishes it from an already-emitted overload: {projectedKey}");
                        ReportCollector.RecordMemberSkipped(methodDecl, SkipReason.DuplicateSignature, overloadName.Detail);
                        UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, methodDecl.Name, BindingItemKind.Method, SkipReason.DuplicateSignature, overloadName.Detail, containingDecl: methodDecl.ParentDecl);
                        continue;
                    }
                    var disambiguatedNameInput = overloadName.NameInput;
                    var reservedKey = disambiguatedNameInput == null
                        ? projectedKey
                        : GetProjectedCSharpMethodKey(methodDecl, typeDatabase, _logger, siblingPropertyNames, nameOverride: disambiguatedNameInput);
                    // FB-1b: recovered static-factory name for a colliding failable init; null keeps the
                    // default "TryCreate" (winner / no-collision).
                    string? failableFactoryName = null;
                    // The factory-namespace key this member claims, if it takes the recovery path —
                    // carried out to the shape recording below so the guard can read its optional tail.
                    string? reservedFactoryKey = null;
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
                                    reservedFactoryKey = factoryNamespace + failableFactoryName + factoryParams;
                                    ClaimProjectedKey(reservedFactoryKey);
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
                            // Occupancy escalation. The map that assigned this method's name is a pure function
                            // of the type's Swift members, so it does not see two things the loop does: a
                            // property-collision rename that lands an otherwise-uncontested method on a
                            // sibling's name, and an override's adopted ancestor name reserved before the loop.
                            // Walk the same ladder — labels, then parameter types — against live occupancy.
                            var escalated = OverloadNameDisambiguator.Escalate(
                                methodDecl,
                                input => GetProjectedCSharpMethodKey(methodDecl, typeDatabase, _logger, siblingPropertyNames, nameOverride: input),
                                candidate =>
                                {
                                    // Record the key at the point it is taken, rather than recomputing the
                                    // escalated name's key afterwards, so the release can never diverge
                                    // from the reservation.
                                    if (!emittedProjectedSignatures.Add(candidate))
                                        return false;
                                    ClaimProjectedKey(candidate);
                                    return true;
                                });
                            if (escalated == null)
                            {
                                _logger.LogDebug($"Skipping method '{methodDecl.Name}' — projected C# signature is already taken and no argument label or parameter type frees it: {projectedKey}");
                                ReportCollector.RecordMemberSkipped(methodDecl, SkipReason.DuplicateSignature,
                                    $"Projects to an already-emitted C# signature ({projectedKey}) and no argument label or parameter type distinguishes it.");
                                UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, methodDecl.Name, BindingItemKind.Method, SkipReason.DuplicateSignature, containingDecl: methodDecl.ParentDecl);
                                continue;
                            }
                            disambiguatedNameInput = escalated;
                            _logger.LogDebug($"Disambiguating method '{methodDecl.Name}' — projected key {projectedKey} was taken → base name '{escalated}'");
                        }
                    }
                    else
                    {
                        ClaimProjectedKey(reservedKey);
                        // FB-1b: the first failable init to claim a projected key is the winner — it keeps the
                        // plain `TryCreate` name and later same-key failable siblings disambiguate against it.
                        if (methodDecl.IsConstructor && methodDecl.IsFailable &&
                            firstFailableInitByProjectedKey.TryAdd(projectedKey, methodDecl))
                        {
                            claimedFailableWinnerKey = projectedKey;
                        }
                    }

                    // Record what this member reserved, in resolution terms. The required-parameter
                    // count is a property of the parameter list, so every name variant this member
                    // reserved (claimed, factory, adopted below) shares the one value. ONLY keys this
                    // member actually claimed may be recorded: when the natural key was already taken
                    // and this one escalated to a different name, that key belongs to an EARLIER
                    // member, and writing this member's parameter list under it would answer a later
                    // ambiguity question about the wrong signature — fail-open if the count comes out
                    // higher, fail-closed (a valid overload declined) if lower.
                    int reservedRequiredCount = OverloadAmbiguityGuard.RequiredCountFor(methodDecl, typeDatabase, projectedKey);
                    var claimedKey = disambiguatedNameInput == null
                        ? projectedKey
                        : GetProjectedCSharpMethodKey(methodDecl, typeDatabase, _logger, siblingPropertyNames, nameOverride: disambiguatedNameInput);
                    OverloadAmbiguityGuard.RecordReservation(reservedOverloadShapes, claimedKey, reservedRequiredCount);
                    // A recovered failable init claims a SECOND key, in the factory namespace, and the
                    // overload producer re-keys its trims into that same namespace. Leaving it unshaped
                    // reads back as fully-required, which silently switches the ambiguity check off for
                    // exactly the members — async factories with a trailing CancellationToken — it was
                    // built to catch.
                    if (reservedFactoryKey != null)
                        OverloadAmbiguityGuard.RecordReservation(reservedOverloadShapes, reservedFactoryKey, reservedRequiredCount);

                    if (conductor.TryGetMethodHandler(methodDecl, out var handler))
                    {
                        // Pass property names and P/Invoke helper context to the method environment
                        var env = new MethodEnvironment(methodDecl, typeDatabase, siblingPropertyNames, context.PInvokeHelperContext, context.CompositionCollector);
                        env.DisambiguatedNameInput = disambiguatedNameInput;
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
                        // above, but it EMITS under the adopted name (`ProcessSecond`). A sibling that
                        // projects to the adopted name (e.g. `processSecond(_:)` → `ProcessSecond`) must
                        // escalate to a discriminated name of its own rather than emit a duplicate (CS0111).
                        // PreReserveAdoptedOverrideNames already reserved this key BEFORE the loop, so the
                        // outcome is declaration-order independent (a `processSecond(_:)` declared ahead of
                        // the override still loses the race). This in-loop Add is the fallback for the cases
                        // the pre-pass conservatively skips — the override's local key was non-unique
                        // because a validation-SKIPPED sibling shared it, so the pre-pass could not
                        // distinguish it from a self-disambiguating override — and is a harmless no-op when
                        // the pre-pass already reserved the key. Uses the real DisambiguatedNameInput, so an
                        // override whose own body already yields the discriminated name resolves
                        // AdoptedOverrideCSharpName to null and reserves nothing.
                        if (env.AdoptedOverrideCSharpName != null)
                        {
                            int adoptedParen = projectedKey.IndexOf('(');
                            if (adoptedParen > 0)
                            {
                                var adoptedKey = env.AdoptedOverrideCSharpName + projectedKey.Substring(adoptedParen);
                                // Tracked for release only when this Add is the one that took the key:
                                // a key the pre-pass already holds is what makes the adopted-name
                                // resolution declaration-order independent, so it outlives an override
                                // that skips inside Emit.
                                if (emittedProjectedSignatures.Add(adoptedKey))
                                    ClaimProjectedKey(adoptedKey);
                                OverloadAmbiguityGuard.RecordReservation(reservedOverloadShapes, adoptedKey, reservedRequiredCount);
                            }
                        }
                        // C6/C7: Share projected signature set so DefaultParameterOverloadEmitter
                        // can dedup against methods already emitted from the main pass
                        env.EmittedProjectedSignatures = emittedProjectedSignatures;
                        env.ReservedOverloadShapes = reservedOverloadShapes;
                        // Containment seam for a type-body member. Escalates to the enclosing type:
                        // if denying the member alone still faults, the defect is in shared type
                        // infrastructure the member merely triggered, and only withdrawing the type
                        // can make the next attempt differ from this one.
                        EmissionSeam.Guard(
                            methodDecl,
                            RecoveryScope.LeafApi,
                            methodDecl.ParentDecl,
                            () => handler.Emit(csWriter, swiftWriter, env, conductor, context));
                        // Stamp the actual emitted C# name on the decl while the env is still
                        // alive (DisambiguatedNameInput is set here and nowhere else). This is the
                        // only single source of truth for the post-disambiguation name — recomputing
                        // later via NameProvider misses it. Read by
                        // ClassHandler.PopulateEmittedClassMethods for the cross-module override
                        // verifier so a parent emitted as `FooWithInt` is recorded as `FooWithInt`,
                        // not `Foo`. The name INPUT is stamped alongside so a derived override can
                        // tell an overload disambiguation apart from an ordinary rename.
                        if (methodDecl.WasEmitted)
                        {
                            methodDecl.EmittedCSharpName = env.CSharpMethodName;
                            methodDecl.EmittedOverloadNameInput = env.DisambiguatedNameInput;
                            // Record the consumer-visible contract for this emitted member —
                            // its post-collision C# signature → the entry symbol the P/Invoke binds.
                            // Recorded here (not in a later model walk) because env.CSharpMethodName's
                            // collision suffix is only known inside this disambiguation loop.
                            // Emit() ran just above, so any declaration writer that emitted a name or
                            // parameter list other than the declared one (a constructor, a failable or
                            // async init, an existential-bypass or metatype-array bridge) has already
                            // recorded what it wrote; prefer that over the pre-emission shape.
                            emissionCtx.RecordApiManifestEntry(
                                ModuleEmissionContext.BuildApiManifestKey(methodDecl.ParentDecl, env.CSharpMethodName, projectedKey, env.TypeDatabase,
                                    emissionCtx.GetEmittedApiShape(methodDecl)),
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

                    // Settle the reservations on the emission OUTCOME. A name a member never emitted
                    // under belongs to whichever sibling can still use it, so the rule the dedup sets
                    // enforce is "first EMITTING claimant wins": a name held by a member that DID emit
                    // is never released, and a member that produced nothing gives its names back.
                    if (!methodDecl.WasEmitted)
                    {
                        emittedMethodSignatures.Remove(signatureKey);
                        if (claimedFailableWinnerKey != null)
                            firstFailableInitByProjectedKey.Remove(claimedFailableWinnerKey);
                        ReleaseProjectedReservations(emittedProjectedSignatures, reservedOverloadShapes, claimedProjectedKeys);
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
        /// Whether a gate drop for <paramref name="gateReason"/> may be rescued by trimming trailing
        /// default-valued parameters. Every reason qualifies except <see cref="SkipReason.EmitterFault"/>.
        ///
        /// <para>That reason covers both containment origins — a verify-recover withdrawal and a
        /// contained emitter exception replayed as a denial on the next attempt — and both are declined
        /// for the same reason, so the guard deliberately keys on the reason rather than the origin
        /// (which the pipeline never sees anyway: <c>EmitterFaultGate.Denied</c> reports one reason for
        /// all of them).</para>
        ///
        /// <para>Such a drop is not a "this parameter's type is unbindable" drop; it is an instruction
        /// that this unit must not reach the output at all, issued because the surface it produced
        /// failed a real compile or threw on the way out. The reduction would escape that instruction
        /// by exactly one parameter — <c>BuildGateReducedDecl</c> clones the decl with a trimmed <c>CSSignature</c>,
        /// and <c>DeclIdFactory.ForMethod</c> folds parameter labels and types into the canonical id,
        /// so the clone no longer matches the poisoned id and the fault gate waves it through. Two
        /// things then go wrong. The withdrawal leaves no trace: the substituted decl falls through to
        /// the emit path, so neither the <c>RecordMemberSkipped</c>/<c>UnsupportedCommentEmitter</c>
        /// arm nor <c>EmissionSeam.TryDenyUpFront</c> (which by then sees the clone's id) records
        /// anything, and the only evidence left is the loop's own withdrawn-unit list. And the
        /// substitution is not an independent member: the clone preserves <c>MangledName</c>, so the
        /// reduced form re-emits the SAME @_cdecl symbol at a different arity, republishing the entry
        /// point the loop just withdrew — which on the Swift plane can reintroduce the very compile
        /// error the withdrawal was recovering from, costing another round or non-convergence.</para>
        /// </summary>
        internal static bool IsTrailingDefaultRescueEligible(SkipReason? gateReason)
            => gateReason != SkipReason.EmitterFault;

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
        ///
        /// <paramref name="gateReason"/> is what the pipeline dropped the full member for. The rescue
        /// applies only to type-driven drops: a verify-recover withdrawal
        /// (<see cref="SkipReason.EmitterFault"/>) is declined outright, see
        /// <see cref="IsTrailingDefaultRescueEligible"/>.
        /// </summary>
        private bool TryBuildTrailingDefaultGateReduction(
            MethodDecl methodDecl,
            SkipReason? gateReason,
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

            if (!IsTrailingDefaultRescueEligible(gateReason))
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
        /// Gives back the projected-signature names a member reserved before emission when the member
        /// did not emit. Releases EXACTLY the keys the caller recorded as taken by that member — never
        /// a recomputed key, and never a key an earlier member owns — and drops each released key's
        /// companion shape entry with it, so the shape table cannot answer a later ambiguity question
        /// about a signature no longer in the output.
        /// </summary>
        private protected static void ReleaseProjectedReservations(
            HashSet<string> emittedProjectedSignatures,
            IDictionary<string, int>? reservedOverloadShapes,
            List<string>? claimedProjectedKeys)
        {
            if (claimedProjectedKeys == null)
                return;

            foreach (var claimed in claimedProjectedKeys)
            {
                emittedProjectedSignatures.Remove(claimed);
                reservedOverloadShapes?.Remove(claimed);
            }
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
        internal static string GetProjectedCSharpMethodKey(MethodDecl methodDecl, ITypeDatabase typeDatabase, ILogger? logger = null, IReadOnlySet<string>? siblingPropertyNames = null, bool treatAsClosureTombstone = false, string? nameOverride = null)
            => ProtocolSignatureHelper.BuildProjectedMethodKey(methodDecl, typeDatabase, new ProtocolSignatureHelper.ProjectedKeyOptions
            {
                PropertyNames = siblingPropertyNames,
                TreatAsClosureTombstone = methodDecl.IsClosureParamTombstone || treatAsClosureTombstone,
                IncludeParentTypeName = true,
                Logger = logger,
                NameOverride = nameOverride,
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
        /// EMITTING methods reserves. A self-disambiguating override (one that overrides BOTH
        /// label-overloads, so two emitting methods share the local key) gets its discriminated name from the
        /// resolver in its own body, not adoption; pre-reserving its "adopted" name would take the slot the
        /// real member needs and mis-bind. The count mirrors the main loop's emitting partition
        /// (<see cref="ClassifyOverridePrePassEmission"/>): a validation-skipped non-tombstone sibling that
        /// happens to share the key is EXCLUDED from the count, so it cannot suppress an otherwise-valid
        /// pre-reservation — the defect that let an earlier-declared natural sibling steal the adopted slot
        /// (CS0111). The projected key carries name + param types only (no return type), so a sibling skipped
        /// solely for an unsupported RETURN still shares the key and was exactly such a suppressor.</item>
        /// </list>
        /// The pre-adoption name is computed from the SAME resolver the main loop reads
        /// (<see cref="OverloadNameDisambiguator.ForMethod"/>), so the two agree on what the override
        /// would have been called without adoption.
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

                // Seed the name input from the same resolver the main loop reads, so the pre-pass's
                // pre-adoption name is the one the loop will compute. It is normally null here (a
                // locally-unique key means nothing contested the name), but the resolver's view is
                // key-based rather than emitting-partition-based, so the two can legitimately differ.
                var env = new MethodEnvironment(method, typeDatabase, siblingPropertyNames, context.PInvokeHelperContext, context.CompositionCollector);
                env.DisambiguatedNameInput = OverloadNameDisambiguator.ForMethod(method, typeDatabase).NameInput;
                var adopted = TryResolveAdoptedOverrideName(env);
                if (adopted == null) continue;

                int paren = key.IndexOf('(');
                if (paren > 0)
                    emittedProjectedSignatures.Add(adopted + key.Substring(paren));
            }
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
        /// ancestor was not emitted, or the ancestor's name did NOT come from overload disambiguation.
        /// That last guard keeps adoption surgical: it never rewrites a property-collision rename (e.g.
        /// base <c>Foo</c> vs derived <c>FooMethod</c>), which could introduce a CS0102 clash in the
        /// derived. It reads the ancestor's <see cref="MethodDecl.EmittedOverloadNameInput"/> stamp
        /// rather than inspecting the spelling: disambiguated names are ordinary words now, so
        /// "ancestor name is the derived name plus digits" no longer identifies them, and a
        /// spelling-based test would silently stop adopting and mis-dispatch every such override. The
        /// cross-module variant of this bug is a tracked residual —
        /// <see cref="TypeRecord.EmittedClassMethods"/> persists no argument labels, so a cross-module
        /// ancestor's two same-type slots are indistinguishable.
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
                    // Adopt only when the ancestor's name came from OVERLOAD disambiguation — never a
                    // different rename (property collision, CS0542/CS0102), which could collide in the
                    // derived body.
                    return candidate.EmittedOverloadNameInput != null ? ancestorName : null;
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
        protected internal static string GetMethodSignatureKey(MethodDecl methodDecl, ITypeDatabase typeDatabase, ILogger? logger = null)
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
