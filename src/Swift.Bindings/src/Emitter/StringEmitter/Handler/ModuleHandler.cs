// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
using Microsoft.Extensions.Logging;
using Swift.Runtime;

namespace BindingsGeneration
{
    /// <summary>
    /// Factory class for creating instances of ModuleHandler.
    /// </summary>
    public class ModuleHandlerFactory : HandlerFactory, IFactory<BaseDecl, IModuleHandler>
    {
        private readonly NamespacePatternResolver _namespacePatternResolver;

        /// <summary>
        /// Initializes a new instance of the <see cref="ModuleHandlerFactory"/> class.
        /// </summary>
        /// <param name="loggerFactory">The logger factory instance.</param>
        public ModuleHandlerFactory(ILoggerFactory loggerFactory, NamespacePatternResolver? namespacePatternResolver = null) : base(loggerFactory.CreateLogger<ModuleHandler>())
        {
            _namespacePatternResolver = namespacePatternResolver ?? new NamespacePatternResolver();
        }

        /// <summary>
        /// Determines if the factory handles the specified declaration.
        /// </summary>
        /// <param name="decl">The base declaration.</param>
        public bool Handles(BaseDecl decl)
        {
            return decl is ModuleDecl;
        }

        /// <summary>
        /// Constructs a new instance of ModuleHandler.
        /// </summary>
        public IModuleHandler Construct()
        {
            return new ModuleHandler(_handlerLogger, _namespacePatternResolver);
        }
    }

    /// <summary>
    /// Handler class for module declarations.
    /// </summary>
    public class ModuleHandler : BaseHandler, IModuleHandler
    {
        /// <summary>
        /// Runtime-dispatch contract <em>epoch</em> for the generator's baked default runtime
        /// version. Derived — not a hand-maintained literal — from the same single-sourced package
        /// version (<c>major*1000 + minor</c>) that the runtime's <c>RuntimeContract.Version</c>
        /// derives from and that the bounded <c>SwiftBindings.Runtime</c> NuGet range fractures on,
        /// so the binding's load-time handshake epoch, the runtime's epoch, and the restore-time
        /// range can no longer silently drift apart. A dev/in-tree build (<c>0.0.0-dev</c>) yields
        /// epoch 0, which the handshake treats as always-compatible. See RuntimeContract.cs for the
        /// gate.
        /// <para>
        /// This is the <em>no-override</em> epoch. When a binding pins a specific runtime via
        /// <c>--swift-runtime-version</c>, the emission instead uses the epoch of that targeted
        /// version (carried on <see cref="ModuleEmissionContext.RuntimeContractEpoch"/>), so the
        /// asserted epoch and the emitted package range agree on the same minor — see
        /// <see cref="ResolveEmittedContractEpoch"/>.
        /// </para>
        /// </summary>
        internal static int EmittedRuntimeContractVersion =>
            RuntimeVersionRange.Epoch(BindingProjectEmitter.DefaultSwiftRuntimeVersion);

        /// <summary>
        /// The contract epoch to emit for THIS module: the targeted runtime's epoch when the
        /// emission context carries one (the <c>--swift-runtime-version</c> path, which also drives
        /// the bounded package range), else the baked-default epoch. Keeping the asserted epoch tied
        /// to the same resolved runtime version the <c>PackageReference</c> targets is what stops a
        /// pinned-older-runtime binding from restoring cleanly and then hard-aborting at module load.
        /// </summary>
        internal static int ResolveEmittedContractEpoch(ModuleEmissionContext? emissionCtx) =>
            emissionCtx?.RuntimeContractEpoch ?? EmittedRuntimeContractVersion;

        private readonly NamespacePatternResolver _namespacePatternResolver;

        public ModuleHandler(ILogger logger, NamespacePatternResolver? namespacePatternResolver = null) : base(logger)
        {
            _namespacePatternResolver = namespacePatternResolver ?? new NamespacePatternResolver();
        }

        /// <inheritdoc/>
        public IEnvironment Marshal(BaseDecl baseDecl, ITypeDatabase typeDatabase)
        {
            if (baseDecl is not ModuleDecl moduleDecl)
            {
                throw new ArgumentException("The provided decl must be a ModuleDecl.", nameof(baseDecl));
            }
            return new ModuleEnvironment(moduleDecl, typeDatabase);
        }

        /// <inheritdoc/>
        public void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor, TypeHandlerContext context)
        {
            var moduleEnv = (ModuleEnvironment)env;
            var moduleDecl = moduleEnv.ModuleDecl;

            // Per-module state is now on ModuleEmissionContext (no more static resets needed).

            // Seed ReportCollector with the set of types that type handlers will skip.
            // Member gates (ValidationRuleSet.ReferencesUnsupportedModule) consult this
            // set via ReportCollector.IsTypeSkipped so signatures referencing a skipped
            // generic (e.g., MusicKit.MusicRelationshipProperty<_,_>) get pruned in the
            // same pass they're emitted, instead of producing a dangling reference that
            // fails C# compilation with CS0234.
            TypeSkipPrePass.Run(moduleDecl, env.TypeDatabase);

            // Emit Swift imports at the top of the Swift wrapper file
            EmitSwiftImports(swiftWriter, moduleDecl, context.GetEmissionContext());

            // Emit EveryProtocol class and protocol conformances for Swift side
            EmitEveryProtocolConformances(swiftWriter, moduleDecl, env.TypeDatabase, context.GetEmissionContext());

            // Pre-pass: front-load the suppressed-proxy-name set now that conformance decisions and
            // read-only-proxy marks are populated (EmitEveryProtocolConformances above), and BEFORE
            // any C# member is emitted. Emit-time proxy-reference gates (which replaced the
            // retired whole-file generate-then-strip post-pass) consult this set; free functions and
            // earlier-declared types would otherwise see an incomplete set. See SuppressedProxyPrecomputer.
            SuppressedProxyPrecomputer.Precompute(moduleDecl, env.TypeDatabase, context.GetEmissionContext());

            var generatedNamespace = _namespacePatternResolver.ResolveNamespace(moduleDecl.Name);
            context.GetEmissionContext().ResolvedNamespace = generatedNamespace;
            context.GetEmissionContext().NamespaceResolver = _namespacePatternResolver;

            csWriter.WriteLine("#nullable enable");
            // The reverse-dispatch marker exists for CONSUMERS of this binding — a hand-written C# type
            // that declares `: I{Protocol}` and would never be called back. Every reference the binding
            // makes to such an interface is one the marker itself calls unaffected: the proxy that
            // implements it, the projected Swift-vended conformers (called through their own witness
            // tables, not our vtable), and forward-use signatures that merely carry the value. Left
            // unsuppressed, the binding cannot compile itself. Consumers compile against the produced
            // assembly, where the attribute still fires from metadata, so this cannot under-warn them.
            csWriter.WriteLine("#pragma warning disable SB0010 // internal references; the marker is for consumers implementing the interface");
            csWriter.WriteLine();
            csWriter.WriteLine($"using System;");
            csWriter.WriteLine($"using System.Collections.Generic;");
            csWriter.WriteLine($"using System.Diagnostics;");
            csWriter.WriteLine($"using System.Diagnostics.CodeAnalysis;");
            csWriter.WriteLine($"using System.Linq;");
            csWriter.WriteLine($"using System.Runtime.CompilerServices;");
            csWriter.WriteLine($"using System.Runtime.InteropServices;");
            csWriter.WriteLine($"using System.Runtime.InteropServices.Swift;");
            csWriter.WriteLine($"using System.Threading.Tasks;");
            csWriter.WriteLine($"using Swift;");
            csWriter.WriteLine($"using Swift.Runtime;");
            csWriter.WriteLine($"using Swift.Runtime.InteropServices;");
            csWriter.WriteLine($"using System.ComponentModel;");
            csWriter.WriteLine($"using {generatedNamespace}.SwiftInterop;");
            // Alias the runtime Utf8Slice type so generated code can reference it unqualified
            csWriter.WriteLine("using Utf8Slice = global::Swift.Runtime.Utf8Slice;");

            // (RealityKit-bug-13: The maccatalyst-only "missing `using ARKit;`" problem will need
            // a per-project SwiftFrameworkDependency-aware emit — emitting `using` for every
            // referenced Apple framework breaks consumer projects that don't reference those
            // packages, e.g. LiveCommunicationKit which references AVFAudio types without
            // pulling AVFAudio into its csproj. Tracked in roadmap.)
            csWriter.WriteLine();
            csWriter.WriteLine($"namespace {generatedNamespace}");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // File-per-type split: the namespace body starts here. Everything before this
            // point (usings + `namespace X {`) is the shared header prepended to every
            // per-type file. See ModuleEmitter.WriteModuleFiles.
            context.GetEmissionContext().EmissionNamespaceBodyStart = csWriter.CurrentOffset;

            // Scope composition interface collection across BOTH top-level methods and types.
            // Free functions can reference composition existentials (e.g., any Describable & TestIdentifiable),
            // so the collector must be active before emitting top-level methods.
            // Populate composition collector on the context — threaded through
            // MethodEnvironment/PropertyEnvironment → ExistentialHandler during emission.
            conductor.CompositionInterfaces.Clear();
            context = context with { CompositionCollector = conductor.CompositionInterfaces };
            // Inject the single per-module composition collector onto the shared MarshalingContext
            // existential handler — the one instance every MethodEnvironment/PropertyEnvironment delegates
            // to during emission. The per-env SetCompositionCollector calls (MethodHandler/PropertyHandler)
            // run BEFORE EmissionContext is attached and so land on a local fallback; doing it here, once,
            // is what actually gives the shared handler the module collector for the whole emission. Same
            // single late-injection point the per-env path always used, just on the shared instance.
            context.GetEmissionContext().Marshaling?.Existential.SetCompositionCollector(conductor.CompositionInterfaces);
            {
                // Emit top-level methods
                if (moduleDecl.Methods.Any())
                {
                    var wrapperClassName = moduleDecl.Name;
                    bool stutters = generatedNamespace.EndsWith($".{moduleDecl.Name}") || generatedNamespace == moduleDecl.Name;
                    if (stutters)
                    {
                        wrapperClassName = "Functions";
                        // Check if a top-level type or the module itself already uses the chosen name
                        var typeNames = new HashSet<string>(moduleDecl.Types.Select(t => t.Name));
                        if (wrapperClassName == moduleDecl.Name || typeNames.Contains(wrapperClassName))
                            wrapperClassName = "GlobalFunctions";
                        if (wrapperClassName == moduleDecl.Name || typeNames.Contains(wrapperClassName))
                        {
                            // Ultimate fallback: append suffix until unique
                            var candidate = $"{moduleDecl.Name}Functions";
                            int suffix = 2;
                            while (typeNames.Contains(candidate))
                                candidate = $"Functions{suffix++}";
                            wrapperClassName = candidate;
                        }
                    }
                    csWriter.WriteLine($"public partial class {wrapperClassName}");
                    csWriter.WriteLine("{");
                    csWriter.Indent++;
                    csWriter.WriteLine();
                    // Track emitted signatures to avoid duplicate free function overloads
                    // (e.g., Swift count(_:) vs count(distinct:) which both project to GetCount<T0>(T0))
                    var emittedMethodSignatures = new HashSet<string>();
                    var emittedProjectedSignatures = new HashSet<string>(StringComparer.Ordinal);
                    // Companion shape table (see the type-body sibling in HandleBaseDecl): reserved
                    // key → how many parameters a caller must supply, so a synthesized overload can
                    // tell whether it would be CS0121-ambiguous with an already-emitted free function.
                    var reservedOverloadShapes = new Dictionary<string, int>(StringComparer.Ordinal);
                    var pipeline = new MemberValidationPipeline(env.TypeDatabase);

                    // Overload names for free functions (mirrors HandleBaseDecl). Built over the same
                    // emitting partition the loop below uses — validation-passed then primary-signature-
                    // deduped (free functions are never constructors) — so a colliding overload's name comes
                    // from its own labels/types rather than its position in the file. Members in no collision
                    // family are absent → read as Natural.
                    var freeFunctionOverloadNames = BuildFreeFunctionOverloadNames(moduleDecl.Methods, pipeline, env.TypeDatabase);

                    foreach (MethodDecl methodDecl in moduleDecl.Methods)
                    {
                        // Attribute everything this free-function iteration writes to the MethodDecl.
                        // `using` declarations so every `continue` path closes the scope without re-indent.
                        var fnOwner = FragmentOwners.ForDecl(methodDecl);
                        using var fnCsScope = csWriter.BeginFragment(fnOwner);
                        using var fnSwiftScope = swiftWriter.BeginFragment(fnOwner);
                        // Pipeline: unified emission validation (SPI, internal, synthesized, closures, modules)
                        var validationResult = pipeline.ValidateMethodEmission(methodDecl, null);
                        if (!validationResult.ShouldEmit)
                        {
                            // Closure-param tombstone (Layer A) — same routing as HandleBaseDecl.
                            if (validationResult.Reason == SkipReason.UnsupportedClosure
                                && !methodDecl.IsAccessor
                                && ClosureParamTombstoneEmitter.IsEligible(methodDecl, env.TypeDatabase))
                            {
                                methodDecl.IsClosureParamTombstone = true;
                                // Fall through — dedup + handler.Emit run normally below.
                            }
                            else
                            {
                                if (validationResult.IsRoutedElsewhere)
                                {
                                    // Concrete specializations elsewhere provide the public surface;
                                    // do not emit `// Unsupported:` or record as skipped. (Today the
                                    // CSM-routing paths require a TypeDecl parent, so free functions
                                    // never reach this branch — defensive parity with the type-member
                                    // consumer in IHandler.cs.)
                                    csWriter.WriteLine();
                                    continue;
                                }
                                ReportCollector.RecordMemberSkipped(methodDecl,
                                    validationResult.Reason ?? SkipReason.ModuleInternal, validationResult.Details ?? "");
                                UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, methodDecl.Name, BindingItemKind.Method, validationResult.Reason ?? SkipReason.ModuleInternal, validationResult.Details, containingDecl: methodDecl.ParentDecl);
                                csWriter.WriteLine();
                                continue;
                            }
                        }

                        // Ahead of the signature and projected-key reservations below, so a denied free
                        // function does not hold a C# name against a sibling that projects to the same
                        // one and could still emit.
                        if (EmissionSeam.TryDenyUpFront(methodDecl, csWriter))
                            continue;

                        // Primary dedup: Swift-level signature
                        var signatureKey = GetMethodSignatureKey(methodDecl, env.TypeDatabase, _logger);
                        if (emittedMethodSignatures.Contains(signatureKey))
                        {
                            _logger.LogDebug($"Skipping duplicate free function '{methodDecl.Name}' with signature: {signatureKey}");
                            ReportCollector.RecordMemberSkipped(methodDecl, SkipReason.DuplicateSignature, signatureKey);
                            UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, methodDecl.Name, BindingItemKind.Method, SkipReason.DuplicateSignature, containingDecl: methodDecl.ParentDecl);
                            csWriter.WriteLine();
                            continue;
                        }
                        emittedMethodSignatures.Add(signatureKey);

                        // Secondary dedup: projected C# public signature. Colliding overloads are
                        // disambiguated by their own Swift argument labels or parameter types.
                        var projectedKey = GetProjectedCSharpMethodKey(methodDecl, env.TypeDatabase, _logger);
                        var overloadName = freeFunctionOverloadNames.GetValueOrDefault(methodDecl, OverloadNameAssignment.Natural);
                        if (overloadName.IsRefused)
                        {
                            _logger.LogDebug($"Skipping free function '{methodDecl.Name}' — no argument label or parameter type distinguishes it from an already-emitted overload: {projectedKey}");
                            ReportCollector.RecordMemberSkipped(methodDecl, SkipReason.DuplicateSignature, overloadName.Detail);
                            UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, methodDecl.Name, BindingItemKind.Method, SkipReason.DuplicateSignature, overloadName.Detail, containingDecl: methodDecl.ParentDecl);
                            csWriter.WriteLine();
                            continue;
                        }
                        var disambiguatedNameInput = overloadName.NameInput;
                        var reservedKey = disambiguatedNameInput == null
                            ? projectedKey
                            : GetProjectedCSharpMethodKey(methodDecl, env.TypeDatabase, _logger, siblingPropertyNames: null, nameOverride: disambiguatedNameInput);
                        if (!emittedProjectedSignatures.Add(reservedKey))
                        {
                            // Occupancy escalation: the resolver's slot is already taken by something it did
                            // not see (a post-processor-emitted overload, an earlier module partition). Walk
                            // the same ladder — labels, then parameter types — against live occupancy.
                            var escalated = OverloadNameDisambiguator.Escalate(
                                methodDecl,
                                input => GetProjectedCSharpMethodKey(methodDecl, env.TypeDatabase, _logger, siblingPropertyNames: null, nameOverride: input),
                                emittedProjectedSignatures.Add);
                            if (escalated == null)
                            {
                                _logger.LogDebug($"Skipping free function '{methodDecl.Name}' — projected C# signature is already taken and no argument label or parameter type frees it: {projectedKey}");
                                ReportCollector.RecordMemberSkipped(methodDecl, SkipReason.DuplicateSignature,
                                    $"Projects to an already-emitted C# signature ({projectedKey}) and no argument label or parameter type distinguishes it.");
                                UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, methodDecl.Name, BindingItemKind.Method, SkipReason.DuplicateSignature, containingDecl: methodDecl.ParentDecl);
                                csWriter.WriteLine();
                                continue;
                            }
                            disambiguatedNameInput = escalated;
                            _logger.LogDebug($"Disambiguating free function '{methodDecl.Name}' — projected key {projectedKey} was taken → base name '{escalated}'");
                        }

                        // Record what this free function reserved, in resolution terms (mirrors the
                        // type-body sibling). ONLY the key this member actually claimed: when the
                        // natural key was already taken and this one escalated to a different name,
                        // that key belongs to an EARLIER member, and writing this member's parameter
                        // list under it would answer a later ambiguity question about the wrong
                        // signature — fail-open if the count comes out higher, fail-closed (a valid
                        // overload declined) if lower.
                        int reservedRequiredCount = OverloadAmbiguityGuard.RequiredCountFor(methodDecl, env.TypeDatabase, projectedKey);
                        var claimedKey = disambiguatedNameInput == null
                            ? projectedKey
                            : GetProjectedCSharpMethodKey(methodDecl, env.TypeDatabase, _logger, siblingPropertyNames: null, nameOverride: disambiguatedNameInput);
                        OverloadAmbiguityGuard.RecordReservation(reservedOverloadShapes, claimedKey, reservedRequiredCount);

                        if (conductor.TryGetMethodHandler(methodDecl, out var methodHandler))
                        {
                            var methodEnv = new MethodEnvironment(methodDecl, env.TypeDatabase, compositionCollector: context.CompositionCollector);
                            methodEnv.DisambiguatedNameInput = disambiguatedNameInput;
                            methodEnv.EmittedProjectedSignatures = emittedProjectedSignatures;
                            methodEnv.ReservedOverloadShapes = reservedOverloadShapes;
                            // Containment seam for a free function. No escalation rung: a free
                            // function's enclosing unit is the module, and denying the module is the
                            // failure this whole mechanism exists to avoid.
                            EmissionSeam.Guard(
                                methodDecl,
                                RecoveryScope.LeafApi,
                                null,
                                () => methodHandler.Emit(csWriter, swiftWriter, methodEnv, conductor, context));
                            // Record the consumer-visible contract for this emitted free function —
                            // its post-collision C# signature → the entry symbol the P/Invoke binds. Mirrors
                            // the type-body chokepoint in IHandler; the module is the implicit parent so the
                            // key is the bare C# name (a free function can't collide with a type member's key).
                            // As at the type-body chokepoint, a declaration writer that reshaped the
                            // emitted name or parameter list has already recorded what it wrote.
                            if (methodDecl.WasEmitted && context.GetEmissionContext() is { } freeFnCtx)
                                freeFnCtx.RecordApiManifestEntry(
                                    ModuleEmissionContext.BuildApiManifestKey(methodDecl.ParentDecl, methodEnv.CSharpMethodName, projectedKey, env.TypeDatabase,
                                        freeFnCtx.GetEmittedApiShape(methodDecl)),
                                    methodEnv.EmissionSymbol);
                        }
                        else
                        {
                            _logger.LogWarning($"No handler found for method {methodDecl.Name}");
                            ReportCollector.RecordMemberSkipped(methodDecl, SkipReason.MissingHandler, "No method handler found for top-level method.");
                            UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, methodDecl.Name, BindingItemKind.Method, SkipReason.MissingHandler, containingDecl: methodDecl.ParentDecl);
                        }
                        csWriter.WriteLine();
                    }
                    csWriter.Indent--;
                    csWriter.WriteLine("}");
                    csWriter.WriteLine();
                }

                base.HandleBaseDecl(csWriter, swiftWriter, moduleDecl.Types, conductor, env.TypeDatabase, context,
                    topLevelSpanSink: context.GetEmissionContext().TopLevelTypeSpans);

                // Emit protocol extension method Swift wrappers (accumulated during InjectExtensionMethods)
                var emissionCtx = context.GetEmissionContext();
                ProtocolExtensionEmitter.EmitSwiftWrappers(swiftWriter, emissionCtx);

                // Emit foreign type extension Swift wrappers and C# extension classes
                ForeignTypeExtensionEmitter.EmitSwiftWrappers(swiftWriter, emissionCtx);
                ForeignTypeExtensionEmitter.EmitCSharpExtensionClasses(csWriter, env.TypeDatabase, moduleDecl.Name, emissionCtx);

                // Emit typed KeyPath singletons rooted on this module's
                // closed AppIntents.AppEntity conformers (KeyPath roots for the
                // EntityProperty / IntentParameter convenience-init family). Driven by
                // conformer enumeration over the current module — see
                // AppEntityKeyPathSingletonEmitter.
                AppEntityKeyPathSingletonEmitter.EmitForModule(
                    csWriter, swiftWriter, moduleDecl, env.TypeDatabase, emissionCtx,
                    emissionCtx.SpecializationEngine, _logger);

                // Emit consumer-side factories that construct a framework
                // dependency's generic type via its method-own-generic KeyPath init,
                // closing the method generic to a local conformer and consuming the
                // KeyPath singletons emitted just above. See
                // ConformerKeyPathInitFactoryEmitter.
                ConformerKeyPathInitFactoryEmitter.EmitForModule(
                    csWriter, swiftWriter, moduleDecl, env.TypeDatabase, emissionCtx,
                    emissionCtx.SpecializationEngine, _logger);

                // Emit deferred enum extension classes (from nested simple enums).
                // C# requires extension methods to be in top-level static classes, so nested
                // enums (e.g., ImageProcessingOptions.Unit) defer their extension classes here.
                foreach (var extensionSource in emissionCtx.DeferredEnumExtensionClasses)
                {
                    csWriter.InnerWriter.Write(extensionSource);
                }

                // Emit composition interfaces (e.g., IAgeableAndNameable : IAgeable, INameable)
                // These are collected during method/property emission when multi-protocol existentials are encountered.
                // SortedDictionary ensures deterministic emission order regardless of encounter order.
                foreach (var (compositionName, parentInterfaces) in conductor.CompositionInterfaces)
                {
                    csWriter.WriteLine();
                    csWriter.WriteLine($"public interface {compositionName} : {string.Join(", ", parentInterfaces)}");
                    csWriter.WriteLine("{");
                    csWriter.WriteLine("}");
                }

                // Emit wrap-only proxy classes for each composition interface
                foreach (var (compositionName, parentInterfaces) in conductor.CompositionInterfaces)
                {
                    EmitCompositionProxy(csWriter, compositionName, parentInterfaces, moduleDecl, env.TypeDatabase);
                }
            }

            // Plain-throws → SwiftException<TError> bridge: emit the per-module
            // C# typed-exception dispatcher class (consumed by 6-param error callbacks
            // emitted from plain-throws async wrappers) and the Swift cascade dispatcher
            // (called from the `} catch { ... }` blocks of those wrappers). Both no-op
            // when ErrorTypeOrder is empty for the module. See ErrorRegistryHelperEmitter.
            {
                var errorRegistryEmissionCtx = context.GetEmissionContext();
                var errorRegistryModuleLibPath = env.TypeDatabase.GetLibraryPath(moduleDecl.Name);
                var errorRegistryWrapperLibPath = env.TypeDatabase.AsyncLibraryName ?? errorRegistryModuleLibPath;
                ErrorRegistryHelperEmitter.EmitCSharpRegistryIfNeeded(
                    csWriter, moduleDecl.Name, errorRegistryWrapperLibPath, errorRegistryEmissionCtx, env.TypeDatabase);
                ErrorRegistryHelperEmitter.EmitSwiftCascadeIfNeeded(
                    swiftWriter, moduleDecl.Name, errorRegistryEmissionCtx, env.TypeDatabase);
            }

            // Emit DllImport framework resolver + NativeAOT factory registration with [ModuleInitializer]
            EmitFrameworkResolver(csWriter, moduleDecl.Name, context.GetEmissionContext());

            // File-per-type split: everything from the namespace body start to here that is
            // NOT inside a recorded top-level-type span (free functions, foreign-type
            // extensions, composition interfaces, error registry, framework resolver) is
            // module-level scaffolding that stays in the prelude file.
            context.GetEmissionContext().EmissionNamespaceBodyEnd = csWriter.CurrentOffset;
            csWriter.Indent--;
            csWriter.WriteLine("}");
            // The namespace-closing brace is replayed verbatim onto every per-type file.
            context.GetEmissionContext().EmissionNamespaceCloseEnd = csWriter.CurrentOffset;

            // Emit SwiftInterop sub-namespace for protocol proxy classes.
            // Always emitted so the 'using' directive at the top resolves even when empty.
            csWriter.WriteLine();
            csWriter.WriteLine($"namespace {generatedNamespace}.SwiftInterop");
            csWriter.WriteLine("{");
            foreach (var proxySource in context.GetEmissionContext().DeferredProxyClasses)
                csWriter.InnerWriter.Write(proxySource);
            csWriter.WriteLine("}");

        }

        /// <summary>
        /// Resolves overload names for top-level free functions. Walks <paramref name="methods"/> in source
        /// order through the same filter chain the emission loop uses — validation
        /// (<see cref="BaseHandler.ClassifyOverridePrePassEmission"/> with the loop's null
        /// <c>ValidationContext</c>) then primary-signature dedup (first-wins on
        /// <see cref="BaseHandler.GetMethodSignatureKey"/>) — and hands the survivors, tagged with their
        /// tombstone-view projected key, to <see cref="OverloadNameDisambiguator.Resolve"/>. Free functions
        /// are never constructors, so none are excluded on that axis.
        ///
        /// Unlike the type-body lane this is NOT memoized: a module's free functions have no second
        /// consumer that has to predict their emitted names (there is no conformance to satisfy), so the
        /// map is built once per emission walk over the exact emitting partition.
        /// </summary>
        private Dictionary<MethodDecl, OverloadNameAssignment> BuildFreeFunctionOverloadNames(
            IEnumerable<MethodDecl> methods, MemberValidationPipeline pipeline, ITypeDatabase typeDatabase)
        {
            var primarySeen = new HashSet<string>(StringComparer.Ordinal);
            var emitting = new List<(MethodDecl, string)>();
            var tombstoneView = new Dictionary<MethodDecl, bool>(ReferenceEqualityComparer.Instance);
            foreach (var m in methods)
            {
                var (willEmit, isTombstone) = ClassifyOverridePrePassEmission(m, pipeline, validationCtx: null, typeDatabase);
                if (!willEmit) continue;
                var signatureKey = GetMethodSignatureKey(m, typeDatabase, _logger);
                if (!primarySeen.Add(signatureKey)) continue;
                tombstoneView[m] = isTombstone;
                var projectedKey = GetProjectedCSharpMethodKey(m, typeDatabase, _logger,
                    siblingPropertyNames: null, treatAsClosureTombstone: isTombstone);
                emitting.Add((m, projectedKey));
            }
            return OverloadNameDisambiguator.Resolve(
                emitting,
                (decl, nameOverride) => GetProjectedCSharpMethodKey(decl, typeDatabase, _logger,
                    siblingPropertyNames: null,
                    treatAsClosureTombstone: tombstoneView.GetValueOrDefault(decl),
                    nameOverride: nameOverride),
                // A free function has no sibling properties and no enclosing type, so nothing can rename it
                // off its natural name; the un-renamed name IS the shaped name.
                decl => NameProvider.GetPublicMethodName(PublicMethodNameContext.ForMethod(decl, siblingPropertyNames: null)));
        }

        /// <summary>
        /// Emits a static class with [ModuleInitializer] that:
        /// 1. Registers a NativeLibrary.SetDllImportResolver for iOS framework loading
        /// 2. Pre-registers NewFromPayload factories for NativeAOT (avoids reflection trimming)
        /// </summary>
        private static void EmitFrameworkResolver(CSharpWriter csWriter, string moduleName, ModuleEmissionContext? emissionCtx)
        {
            var factoryTypes = emissionCtx?.EmittedSwiftObjectTypes ?? Array.Empty<string>();
            var payloadSemantics = emissionCtx?.PayloadSemantics
                ?? Array.Empty<(string TypeofExpr, PayloadConstructionSemantics Semantics)>();
            var conformances = emissionCtx?.EmittedConformances ?? Array.Empty<(string, string)>();
            var simpleEnumRegistrations = emissionCtx?.SimpleEnumMetadataRegistrations
                ?? Array.Empty<(string, string, string)>();
            var classBoundExistentialRegistrations = emissionCtx?.ClassBoundExistentialRegistrations
                ?? Array.Empty<(string, string)>();

            // Emit a single [ModuleInitializer] class that:
            // 1. Registers the DllImport framework resolver (must be first — metadata lookups P/Invoke into native libs)
            // 2. Pre-registers NewFromPayload factories for NativeAOT
            // 3. Pre-registers type metadata in the cache for NativeAOT (avoids trimmed reflection path)
            // 4. Pre-registers protocol conformance factories for NativeAOT
            // 5. Registers simple enum metadata via P/Invoke (correct SwiftOptional<T> layout)
            // All in one initializer to guarantee ordering (framework resolver before any P/Invoke).
            csWriter.WriteLine();
            csWriter.WriteLine("#pragma warning disable CA2255 // ModuleInitializer is intentional in generated binding code");
            // Eager type-metadata / factory registrations touch types that may carry
            // [SupportedOSPlatform] annotations stricter than the callsite's floor. The
            // initializer runs at module load from an arbitrary OS context and every
            // call is wrapped in try/catch, so a CA1416 at this site is a false positive.
            csWriter.WriteLine("#pragma warning disable CA1416 // ModuleInitializer registrations are best-effort across OS versions");
            csWriter.WriteLines($$"""
                internal static class __SwiftFrameworkResolver_{{moduleName}}
                {
                    [global::System.Runtime.CompilerServices.ModuleInitializer]
                    internal static void Initialize()
                    {
                        // Runtime-contract handshake (Finding 32): a single unconditional check
                        // before the best-effort (try/catch) registrations below. If this binding
                        // was generated against a different runtime dispatch contract than the
                        // loaded SwiftBindings.Runtime, fail loudly at module load rather than let
                        // an incompatible binding silently fall through to a later dispatch failure.
                        global::Swift.Runtime.RuntimeContract.AssertCompatible({{ResolveEmittedContractEpoch(emissionCtx)}});
                        global::Swift.Runtime.SwiftFrameworkResolver.RegisterForAssembly(typeof(__SwiftFrameworkResolver_{{moduleName}}).Assembly);
                """);

            // Eager generic registration / metadata warmup of an availability-gated type aborts
            // uncatchably on Mono when the running OS is below the type's floor: forcing the closed
            // generic method context (mini_init_method_rgctx → mini_instantiate_gshared_info) or the
            // trailing metadata accessor for a type whose Swift @available exceeds the host OS is a
            // native process abort — the surrounding try/catch only intercepts MANAGED exceptions.
            // So wrap each per-type eager registration in a POSITIVE availability check: run it only
            // when the type's effective floor is satisfied here. The member-level guards already
            // convert a below-floor call into a catchable PlatformNotSupportedException, so an
            // un-warmed gated type still resolves correctly (just lazily) on a new-enough OS.
            void EmitGuardedRegistration(string typeName, string body)
            {
                var guard = AvailabilityAttributeEmitter.BuildIsAvailableCondition(
                    emissionCtx?.GetTypeEffectiveAvailability(typeName));
                if (guard != null)
                    csWriter.WriteLines($"        if ({guard}) {{ {body} }}");
                else
                    csWriter.WriteLines($"        {body}");
            }

            foreach (var typeName in factoryTypes)
            {
                // Wrap each registration in try-catch so one failing type doesn't crash the
                // entire app during module initialization. On NativeAOT device, some types
                // (e.g., types depending on framework initialization order) may fail during
                // early startup. The factory and metadata are best-effort — types that fail
                // here will fall back to the reflection path at call time.
                //
                // Generic types (name contains '<') get factory registration only; their metadata
                // accessor can SIGSEGV in the Swift runtime during module init (not catchable in C#
                // try/catch) because the Swift class isn't fully initialized yet. On-demand lookup
                // via SwiftObjectHelper<T>.GetTypeMetadata() at actual call time works fine.
                if (typeName.Contains('<'))
                {
                    EmitGuardedRegistration(typeName,
                        $"try {{ global::Swift.Runtime.InteropServices.SwiftMarshal.RegisterSwiftObjectFactory<{typeName}>(); }} catch {{ }}");
                }
                else
                {
                    EmitGuardedRegistration(typeName,
                        $"try {{ global::Swift.Runtime.InteropServices.SwiftMarshal.RegisterSwiftObjectFactory<{typeName}>(); global::Swift.Runtime.SwiftObjectHelper<{typeName}>.GetTypeMetadata(); }} catch {{ }}");
                }
            }
            // Pre-register each emitted type's declared payload-construction semantics (Finding 11).
            // The unconstrained marshal seam reads this by-Type contract to balance Swift ARC and free
            // the wire temporary correctly; a literal enum value here avoids the static-virtual property
            // read that would assert on Mono. Generic definitions register their open form once and the
            // dispatcher resolves closed instantiations via the open-generic fallback. Best-effort like
            // the factory loop — an unregistered type falls back to the runtime reflection backstop.
            foreach (var (typeofExpr, semantics) in payloadSemantics)
            {
                csWriter.WriteLines($"        try {{ global::Swift.Runtime.InteropServices.SwiftMarshal.RegisterPayloadSemantics(typeof({typeofExpr}), global::Swift.Runtime.PayloadConstructionSemantics.{semantics}); }} catch {{ }}");
            }
            // Fail-closed Mono-safety invariant. BOTH conformance loops below invoke a
            // static-virtual on the TType operand — RegisterConformanceFactory resolves
            // TType.GetProtocolConformanceDescriptor and RegisterWitnessTable resolves
            // GetOrThrowDirect. On Mono an OPEN-generic TType crashes the JIT instead of
            // dispatching, so the conformance recorder (ModuleEmissionContext.RecordConformance)
            // already skips any type under an open-generic ancestor — every pair reaching here is
            // a concrete literal today. Assert that here so a future recorder regression surfaces
            // as a generation-time error rather than as a Mono/NativeAOT runtime crash in a
            // consumer app. Only the TType (left) operand is gated; the protocol operand may
            // legally be a CLOSED generic interface (e.g. IEquatable<Codec.Encoding>).
            foreach (var (typeName, protocolName) in conformances)
            {
                if (typeName.Contains('<'))
                {
                    throw new InvalidOperationException(
                        $"SWIFTBIND047: conformance registration requires a concrete-literal type, but '{typeName}' "
                        + $"(conforming to '{protocolName}') is an open generic. Emitting RegisterConformanceFactory/"
                        + "RegisterWitnessTable for it would produce a Mono-unsafe static-virtual dispatch; the "
                        + "conformance recorder must skip open-generic types.");
                }
            }
            foreach (var (typeName, protocolName) in conformances)
            {
                EmitGuardedRegistration(typeName,
                    $"try {{ global::Swift.Runtime.InteropServices.SwiftMarshal.RegisterConformanceFactory<{typeName}, {protocolName}>(); }} catch {{ }}");
            }
            // Pre-register witness tables for ALL protocol conformances.
            // This eagerly computes and caches the witness table during module initialization
            // via GetOrThrowDirect (static virtual dispatch). On NativeAOT device,
            // LoadFromSymbol → swift_getWitnessTable can crash when called later at runtime
            // (likely due to library handle lifecycle issues). Pre-registering during init
            // ensures the witness table is cached and the runtime path uses the cache.
            foreach (var (typeName, protocolName) in conformances)
            {
                EmitGuardedRegistration(typeName,
                    $"try {{ global::Swift.Runtime.InteropServices.SwiftMarshal.RegisterWitnessTable<{typeName}, {protocolName}>(); }} catch {{ }}");
            }
            // Register simple enum metadata via P/Invoke to @_cdecl Swift wrappers.
            // Simple C# enums can't implement ISwiftObject, so their Swift metadata must be
            // registered explicitly. Without this, SwiftOptional<T> gets the wrong Optional
            // layout (tag-byte encoding from the underlying integer type instead of
            // extra-inhabitant encoding from the actual Swift enum).
            foreach (var (typeName, _, _) in simpleEnumRegistrations)
            {
                var safeName = typeName.Replace(".", "_");
                csWriter.WriteLines($"        try {{ global::Swift.Runtime.TypeMetadata.RegisterMetadata(typeof({typeName}), global::Swift.Runtime.TypeMetadata.FromHandle(__GetEnumMetadata_{safeName}())); }} catch {{ }}");
            }
            // Register the shared class-existential value-witness metadata for the
            // ClassExistentialContainer1 carrier (16-byte [classRef][witnessTable] stride).
            // Required so SwiftArray<ClassExistentialContainer1> derives the correct element
            // stride from Swift metadata; the registration is idempotent and protocol-agnostic
            // for the arity, so the first class-bound protocol's descriptor wins.
            foreach (var (libraryName, descriptorSymbol) in classBoundExistentialRegistrations)
            {
                csWriter.WriteLines($"        try {{ global::Swift.Runtime.TypeMetadata.RegisterClassBoundExistentialMetadata(\"{libraryName}\", \"{descriptorSymbol}\"); }} catch {{ }}");
            }
            csWriter.WriteLines($$"""
                    }
                """);
            // Emit DllImport P/Invoke declarations for simple enum metadata accessors.
            foreach (var (typeName, metadataSymbol, wrapperLibName) in simpleEnumRegistrations)
            {
                var safeName = typeName.Replace(".", "_");
                csWriter.WriteLines($"    [global::System.Runtime.InteropServices.DllImport(\"{wrapperLibName}\", CallingConvention = global::System.Runtime.InteropServices.CallingConvention.Cdecl, EntryPoint = \"{metadataSymbol}\")]");
                csWriter.WriteLines($"    private static extern IntPtr __GetEnumMetadata_{safeName}();");
            }
            csWriter.WriteLines($$"""
                }
                """);

            csWriter.WriteLine("#pragma warning restore CA1416");
            csWriter.WriteLine("#pragma warning restore CA2255");
        }

        /// <summary>
        /// Collects the set of Apple framework module names referenced by a module's bound
        /// surface (declared dependencies + scanned method/property/protocol/subscript signatures).
        /// Filtering of implicit / self modules (Swift, Foundation, the module being bound) is
        /// applied here so callers receive a ready-to-emit set. The wrapper-import gate is
        /// data-driven via <see cref="AppleFrameworkRegistry.IsWrapperImportableModule"/>
        /// (backed by the <c>wrapperImportable</c> field in apple-frameworks.json) so adding
        /// a new Apple framework binding is a one-line JSON edit instead of a code change.
        /// </summary>
        private HashSet<string> CollectFrameworkImports(ModuleDecl moduleDecl)
        {
            var neededImports = new HashSet<string>();

            // Add platform UI frameworks if present in dependencies
            foreach (var dep in moduleDecl.Dependencies)
            {
                if (AppleFrameworkRegistry.IsWrapperImportableModule(dep))
                {
                    neededImports.Add(dep);
                }
            }

            // Scan for types used in methods that need corresponding imports
            ScanTypesForFrameworkImports(moduleDecl.Types, neededImports);

            // Scan protocols for types used in method/property signatures
            if (moduleDecl.Protocols != null)
            {
                ScanProtocolsForFrameworkImports(moduleDecl.Protocols, neededImports);
            }

            // Scan top-level free functions and global properties. These live directly on
            // the module (not inside moduleDecl.Types), so the type scan above never reaches
            // them — yet their @_cdecl wrappers reference parameter/return/value types just
            // like type members do. A free function taking/returning an Apple-framework type
            // (e.g. roundTripOptionalASCredentialParameters(_: ASAuthorizationPublicKeyCredentialParameters?))
            // otherwise emits a wrapper that references the framework without importing it,
            // which fails swiftc with "cannot find type 'X' in scope". The declared-imports
            // path can't cover this: it deliberately skips Apple frameworks and relies on this
            // scan to add them only when their types appear in the wrapper's public surface.
            ScanModuleMembersForFrameworkImports(moduleDecl, neededImports);

            // Drop implicit / already-imported / self modules discovered during the scan.
            // Swift stdlib is implicit; Foundation is imported unconditionally on the wrapper
            // side and lives in System on the C# side; the module being bound is its own
            // namespace.
            neededImports.Remove("Swift");
            neededImports.Remove("Foundation");
            neededImports.Remove(moduleDecl.Name);

            return neededImports;
        }

        /// <summary>
        /// Emits Swift import statements to the wrapper file.
        /// </summary>
        private void EmitSwiftImports(SwiftWriter swiftWriter, ModuleDecl moduleDecl, ModuleEmissionContext? emissionCtx = null)
        {
            // Always import the module being bound. Some Apple modules (e.g. RealityFoundation)
            // are marked @_implementationOnly by their umbrella (RealityKit) and must be imported
            // through the umbrella instead. Type qualifications and the .NET namespace continue
            // to use moduleDecl.Name — only this literal import line is rewritten.
            var compileImport = AppleFrameworkRegistry.MapModuleToCompileImport(moduleDecl.Name);
            swiftWriter.WriteLine($"import {compileImport}");
            swiftWriter.WriteLine("import Foundation");

            // Build the additional-imports set with every candidate normalized through the
            // compile-import remap, then dedupe on the *normalized* name. This covers scanned
            // imports (CollectFrameworkImports), the bound module's own `import` declarations,
            // and --framework-dependency entries equally, so a sibling module that pulls in
            // @_implementationOnly RealityFoundation either way still emits `import RealityKit`.
            //
            // The bound module's swiftinterface is the authoritative source for what the
            // wrapper needs to import — every cross-module type the wrapper references shows
            // up in the bound module's public surface, which means its defining module is
            // either declared as `import X` in the swiftinterface OR appears as a type
            // qualifier (`X.SomeType`) inline. We emit declared imports directly (so static-
            // archive static-cloud-sdk libs whose otool-based auto-detection finds nothing still get
            // their sibling imports). DependencyModuleNames adds qualifier-only references
            // and is filtered against declared/scanned references so we drop C++-only
            // siblings (absl/grpc/leveldb/openssl_grpc/grpcpp) whose Clang umbrella headers
            // can't compile in swiftc without `-Xcc -std=c++17` flags.
            //
            // When the swiftinterface is unavailable (apple-framework-mode unit tests,
            // direct-mode runs without `-s/--swiftinterface`), declaredImports stays null
            // and we fall back to legacy emit-all behavior so existing test fixtures and
            // out-of-tree consumers continue to work unchanged.
            var scannedImports = CollectFrameworkImports(moduleDecl);
            HashSet<string>? declaredImports = null;
            HashSet<string>? nonPublicImports = null;
            string? interfaceText = null;
            if (!string.IsNullOrEmpty(moduleDecl.SwiftInterfacePath) && File.Exists(moduleDecl.SwiftInterfacePath))
            {
                interfaceText = File.ReadAllText(moduleDecl.SwiftInterfacePath);
                declaredImports = new HashSet<string>(AppleFrameworkImportDetector.ExtractImports(interfaceText), StringComparer.Ordinal);
                nonPublicImports = AppleFrameworkImportDetector.ExtractNonPublicImports(interfaceText);
            }

            var additionalImports = new HashSet<string>();
            foreach (var scanned in scannedImports)
            {
                additionalImports.Add(AppleFrameworkRegistry.MapModuleToCompileImport(scanned));
            }
            // Emit the bound module's own declared `import` lines for SIBLING modules
            // (third-party deps the bound source depends on). Apple frameworks and Swift
            // system modules are deliberately skipped here — `scannedImports` already
            // imports them only when their types appear in the wrapper's public surface,
            // and indiscriminately re-importing them can cause "ambiguous type lookup"
            // errors when the bound module exposes a type name that also exists in an
            // Apple framework (e.g., a type that collides with an Apple framework type of the same name).
            //
            // This catches sibling deps for libraries whose binary is a static archive
            // (static-archive cloud SDKs) so otool-based auto-detection produces nothing — the swiftinterface
            // still tells us exactly which siblings the public surface needs.
            if (declaredImports != null)
            {
                foreach (var declared in declaredImports)
                {
                    if (declared.Length > 0 && declared[0] == '_')
                        continue;
                    if (AppleFrameworkRegistry.ShouldSuppressDeclaredWrapperImport(declared))
                        continue;
                    // Skip imports the bound module marked as non-public (`@_implementationOnly`,
                    // `private`, `internal`, `fileprivate`). They aren't part of the public surface
                    // a wrapper sees, and re-emitting them forces swiftc to load C++-only siblings
                    // (absl/grpc/leveldb) that the wrapper has no reason to depend on.
                    if (nonPublicImports != null && nonPublicImports.Contains(declared))
                        continue;
                    additionalImports.Add(AppleFrameworkRegistry.MapModuleToCompileImport(declared));
                }
            }
            foreach (var depModule in moduleDecl.DependencyModuleNames)
            {
                // Non-public imports (`@_implementationOnly`, `private`, `internal`,
                // `fileprivate`, `package`) don't carry to the wrapper either way — declared
                // depModule entries derived from them must NOT short-circuit the public-import
                // checks below, otherwise the wrapper still ends up with `import absl` and the
                // C++-only sibling fails to load.
                if (nonPublicImports != null && nonPublicImports.Contains(depModule))
                    continue;
                var mapped = AppleFrameworkRegistry.MapModuleToCompileImport(depModule);
                if (declaredImports == null
                    || declaredImports.Contains(depModule)
                    || declaredImports.Contains(mapped)
                    || scannedImports.Contains(depModule)
                    // Cross-module sibling deps can appear in the
                    // bound module's swiftinterface ONLY as a type qualifier without an
                    // explicit `import` line. The Apple-framework-only `scannedImports`
                    // doesn't see these. Qualified-reference match against the swiftinterface
                    // text catches `<Module>.<Type>` references while still rejecting C++-only
                    // siblings (absl/grpc/leveldb/openssl_grpc/grpcpp) that the bound module
                    // never mentions.
                    || (interfaceText != null && SwiftInterfaceReferencesModule(interfaceText, depModule)))
                {
                    additionalImports.Add(mapped);
                }
            }
            additionalImports.Remove("Swift");
            additionalImports.Remove("Foundation");
            additionalImports.Remove(compileImport);
            additionalImports.Remove(moduleDecl.Name);

            foreach (var import in additionalImports.OrderBy(s => s))
            {
                swiftWriter.WriteLine($"import {import}");
            }

            // A module whose own name is shadowed by a type of that name can't be qualified
            // through — every `Module.X` reads as member lookup into the shadowing type — so the
            // wrapper strips those qualifiers and names the module's types bare. Bare names then
            // resolve against every wildcard import at once, which is ambiguous for any name an
            // imported module also declares. Scoped imports bind each of the bound module's own
            // names to its own declaration and outrank the wildcards, restoring the unambiguous
            // reference the stripped qualifier can no longer provide.
            if (emissionCtx?.ModuleNameForCollision == moduleDecl.Name)
            {
                foreach (var scopedImport in CollisionScopedImportPlanner.Plan(moduleDecl, compileImport))
                {
                    swiftWriter.WriteLine(scopedImport);
                }
            }

            swiftWriter.WriteLine();

            // Emit SBW_Utf8Slice and SBW_Free at module level (before any functions)
            // These are needed for async String returns and may be used elsewhere.
            // Emitting unconditionally is safe - small structs/functions that do no harm if unused.
            // SBW_Free uses module-specific symbol name to avoid collisions if multiple modules
            // are linked into the same wrapper library.
            Utf8SliceEmitter.EmitIfNeeded(swiftWriter, emissionCtx);
            Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter, moduleDecl.Name, emissionCtx);

            // Emit Swift Task cancellation infrastructure (cancel function + task dictionary)
            CancellationTaskEmitter.EmitIfNeeded(swiftWriter, moduleDecl.Name, emissionCtx);

            // Emit Swift error description extraction infrastructure (for sync throwing methods)
            ErrorDescriptionEmitter.EmitIfNeeded(swiftWriter, moduleDecl.Name, emissionCtx);
        }

        // Detects a qualified `<Module>.<Identifier>` reference in a swiftinterface body.
        // Used to discover dependency module references that are qualified inline but never appear as an explicit
        // `import` line. Requires the trailing `.` so a bare whole-word occurrence (parameter
        // name, type leaf, identifier substring, comment, or string literal) doesn't trigger a
        // spurious `import` for short C++-only sibling names like `grpc` / `absl`. Also requires
        // an identifier-start character after the dot to reject decimal literals
        // (`3.absl` is impossible Swift but `absl.0` could occur in numeric debug output).
        // Returns false when the name doesn't appear in qualifier shape, or appears only inside
        // a larger identifier (e.g. `FooBar` for module `Foo`).
        private static bool SwiftInterfaceReferencesModule(string interfaceText, string moduleName)
        {
            if (string.IsNullOrEmpty(interfaceText) || string.IsNullOrEmpty(moduleName))
                return false;
            int start = 0;
            while (true)
            {
                var idx = interfaceText.IndexOf(moduleName, start, StringComparison.Ordinal);
                if (idx < 0) return false;
                var before = idx == 0 ? '\0' : interfaceText[idx - 1];
                var afterIdx = idx + moduleName.Length;
                var after = afterIdx >= interfaceText.Length ? '\0' : interfaceText[afterIdx];
                // `before` must not be an identifier character (substring of larger identifier)
                // and not '.' (nested component, e.g. `Foo.Bar.Baz` is NOT a top-level reference
                // to module `Bar`). Top-level module qualifiers appear at the start of a dotted
                // type path, never in the middle.
                bool beforeOk = !(char.IsLetterOrDigit(before) || before == '_' || before == '.');
                bool isQualifierShape = false;
                if (after == '.' && afterIdx + 1 < interfaceText.Length)
                {
                    var afterDot = interfaceText[afterIdx + 1];
                    isQualifierShape = char.IsLetter(afterDot) || afterDot == '_';
                }
                if (beforeOk && isQualifierShape)
                    return true;
                start = idx + 1;
            }
        }

        /// <summary>
        /// Recursively scans types for async methods that return framework types.
        /// </summary>
        private void ScanTypesForFrameworkImports(IEnumerable<TypeDecl> types, HashSet<string> neededImports)
        {
            foreach (var type in types)
            {
                // Methods/Properties live on the TypeDecl base, so EnumDecl carries them too —
                // a previous switch-on-subtype here silently dropped enum properties whose
                // types belonged to a non-imported Apple framework. The constrained-extension
                // emitter on a generic enum (StoreKit2 `VerificationResult<SignedType>` →
                // `signature: P256.Signing.ECDSASignature`) hit this: the `@_cdecl` wrapper
                // referenced `CryptoKit.P256.…` but `import CryptoKit` was never added.

                // Check methods — @_cdecl wrappers reference parameter and return types from ALL methods,
                // not just async ones. Missing imports cause "cannot find type 'X' in scope" errors.
                foreach (var method in type.Methods)
                {
                    foreach (var sig in method.CSSignature)
                    {
                        ScanTypeSpecForImports(sig.SwiftTypeSpec, neededImports);
                    }
                }

                // Check properties — @_cdecl wrappers also reference property types
                foreach (var property in type.Properties)
                {
                    ScanTypeSpecForImports(property.SwiftTypeSpec, neededImports);
                }

                // Check operators — emitted wrappers reference the underlying method's
                // parameter and return types just like ordinary methods. Skipping these
                // lets a concrete type's `static func == (lhs:rhs:)` reference a framework
                // type (e.g. CryptoKit.Curve25519 marker) without `import CryptoKit` in
                // the wrapper, producing "cannot find type in scope" at swiftc.
                foreach (var op in type.Operators)
                {
                    foreach (var sig in op.UnderlyingMethod.CSSignature)
                    {
                        ScanTypeSpecForImports(sig.SwiftTypeSpec, neededImports);
                    }
                }

                // Check subscripts — subscript return types and index parameter types
                // appear in the @_cdecl wrapper and need their framework imports too.
                // Protocol subscripts are handled separately in ScanProtocolsForFrameworkImports.
                foreach (var subscript in type.Subscripts)
                {
                    ScanTypeSpecForImports(subscript.ReturnTypeSpec, neededImports);
                    foreach (var param in subscript.IndexParameters)
                    {
                        ScanTypeSpecForImports(param.SwiftTypeSpec, neededImports);
                    }
                }

                // Recursively check nested types
                if (type.Types.Any())
                {
                    ScanTypesForFrameworkImports(type.Types, neededImports);
                }
            }
        }

        /// <summary>
        /// Scans a module's top-level free functions and global properties for framework
        /// types. Unlike type members (covered by <see cref="ScanTypesForFrameworkImports"/>),
        /// these hang directly off the <see cref="ModuleDecl"/>, so without this pass an
        /// Apple-framework type used only by a free function or global never triggers its
        /// `import` and the wrapper fails to compile.
        /// </summary>
        private void ScanModuleMembersForFrameworkImports(ModuleDecl moduleDecl, HashSet<string> neededImports)
        {
            foreach (var method in moduleDecl.Methods)
            {
                foreach (var sig in method.CSSignature)
                {
                    ScanTypeSpecForImports(sig.SwiftTypeSpec, neededImports);
                }
            }

            foreach (var property in moduleDecl.Properties)
            {
                ScanTypeSpecForImports(property.SwiftTypeSpec, neededImports);
            }
        }

        /// <summary>
        /// Scans protocols for types used in method parameters, return types, and properties.
        /// These types appear in EveryProtocol conformance code and need corresponding imports.
        /// </summary>
        private void ScanProtocolsForFrameworkImports(IEnumerable<ProtocolDecl> protocols, HashSet<string> neededImports)
        {
            foreach (var protocol in protocols)
            {
                // Scan properties
                foreach (var property in protocol.Properties)
                {
                    ScanTypeSpecForImports(property.SwiftTypeSpec, neededImports);
                }

                // Scan methods
                foreach (var method in protocol.Methods)
                {
                    // Scan return type
                    if (method.CSSignature.Count > 0)
                    {
                        ScanTypeSpecForImports(method.CSSignature[0].SwiftTypeSpec, neededImports);
                    }

                    // Scan parameter types
                    for (int i = 1; i < method.CSSignature.Count; i++)
                    {
                        ScanTypeSpecForImports(method.CSSignature[i].SwiftTypeSpec, neededImports);
                    }
                }

                // Scan subscripts
                foreach (var subscript in protocol.Subscripts)
                {
                    ScanTypeSpecForImports(subscript.ReturnTypeSpec, neededImports);
                    foreach (var param in subscript.IndexParameters)
                    {
                        ScanTypeSpecForImports(param.SwiftTypeSpec, neededImports);
                    }
                }
            }
        }

        /// <summary>
        /// Recursively scans a TypeSpec for framework types and adds needed imports.
        /// </summary>
        private void ScanTypeSpecForImports(TypeSpec? typeSpec, HashSet<string> neededImports)
        {
            if (typeSpec == null)
                return;

            if (typeSpec is NamedTypeSpec namedType)
            {
                // Check if the type name starts with a known framework
                CheckTypeNameForFrameworkImport(namedType.Name, neededImports);

                // Recursively check generic parameters
                foreach (var genericParam in namedType.GenericParameters)
                {
                    ScanTypeSpecForImports(genericParam, neededImports);
                }
            }
            else if (typeSpec is TupleTypeSpec tupleType)
            {
                foreach (var element in tupleType.Elements)
                {
                    ScanTypeSpecForImports(element, neededImports);
                }
            }
            else if (typeSpec is ClosureTypeSpec closureType)
            {
                ScanTypeSpecForImports(closureType.Arguments, neededImports);
                ScanTypeSpecForImports(closureType.ReturnType, neededImports);
            }
            else if (typeSpec is ProtocolListTypeSpec protocolList)
            {
                foreach (var proto in protocolList.Protocols.Keys)
                {
                    CheckTypeNameForFrameworkImport(proto.Name, neededImports);
                }
            }
        }

        /// <summary>
        /// Checks if a type name requires a framework import. Adds the module portion of a
        /// qualified name to <paramref name="neededImports"/> when the module is opted into
        /// wrapper imports via apple-frameworks.json's <c>wrapperImportable</c> field.
        /// Filtering of implicit modules (Swift, Foundation, self) is done by the caller
        /// after the full scan.
        /// </summary>
        private void CheckTypeNameForFrameworkImport(string? typeName, HashSet<string> neededImports)
        {
            if (string.IsNullOrEmpty(typeName))
                return;

            var dotIndex = typeName.IndexOf('.');
            if (dotIndex <= 0)
                return;

            var moduleName = typeName.Substring(0, dotIndex);

            // Underscore-prefixed Apple SPI modules (e.g. _LocationEssentials) are not public
            // and cannot be imported directly. Remap to the public counterpart when known;
            // drop silently when unknown (better to skip than to emit a broken import).
            if (moduleName.StartsWith("_", StringComparison.Ordinal))
            {
                if (!AppleFrameworkRegistry.TryMapSpiModuleToPublic(moduleName, out var publicModule))
                    return;
                moduleName = publicModule;
            }

            // Opt-in per module via apple-frameworks.json's `wrapperImportable` field.
            // Unconditional add broke the validation corpus in two ways: (1) Swift generic
            // placeholders like `τ_0_0` leaked in as bogus modules, (2) ambient modules got
            // auto-imported and collided with same-named types in the bound module. The data-driven predicate
            // keeps both exclusions explicit and centralised.
            if (AppleFrameworkRegistry.IsWrapperImportableModule(moduleName))
                neededImports.Add(moduleName);
        }

        /// <summary>
        /// Emits the EveryProtocol class and protocol conformances for Swift side.
        /// This enables C# code to implement Swift protocols by providing vtable callbacks.
        /// </summary>
        private void EmitEveryProtocolConformances(SwiftWriter swiftWriter, ModuleDecl moduleDecl, ITypeDatabase typeDatabase, ModuleEmissionContext? emissionCtx = null)
        {
            // Skip if there are no protocols to conform to
            var protocols = moduleDecl.Protocols;
            if (protocols == null || !protocols.Any())
                return;

            // Check if any protocols are suitable for EveryProtocol conformance
            var suitableProtocols = protocols
                // Gate 0: this Swift-side pass runs before the C# interface dispatch, so a protocol
                // denied after faulting would otherwise keep its EveryProtocol conformance and ship a
                // Swift witness for an interface the retry never emits.
                .Where(p => !EmitterFaultGate.IsDenied(DeclIdFactory.ForType(p), out _))
                .Where(p => !p.HasSelfRequirement && p.AssociatedTypes.Count == 0)
                // Skip internal, @_spi, and @usableFromInline protocols — EveryProtocol can only
                // conform to protocols whose members are all publicly accessible.
                .Where(p => !p.IsModuleInternal)
                // EveryProtocolEmitter handles all member-level decisions:
                // constructor requirements, static method requirements, empty marker protocols, etc.
                // All protocols pass through here; the emitter records proper skip reasons.
                // Bug #14: Filter out protocols not actually defined in this module.
                // When a module extends stdlib protocols, the parser creates ProtocolDecl entries
                // with the module's name (e.g., Module.Collection), but these are stdlib protocols
                // re-exported by the module — not defined in it.
                // Check the mangled name: module-defined protocols encode the module name as
                // $s{length}{moduleName}..., while stdlib protocols
                // use abbreviated forms ($sSl, $sSB, $ss17...).
                //
                // @objc protocols have an empty mangled name in the ABI JSON (Swift omits
                // the mangling for protocols visible through the Objective-C runtime). Fall
                // back to the SwiftTypeName.Module check so NSObjectProtocol-only @objc protocols
                // (e.g., our NumberProvider fixture) aren't
                // categorically dropped by the mangled-name check before the routing gate
                // downstream can promote them to EveryObjCProtocol.
                .Where(p => IsMangledNameFromModule(p.MangledName, moduleDecl.Name)
                            || (string.IsNullOrEmpty(p.MangledName)
                                && p.SwiftTypeName != null
                                && string.Equals(p.SwiftTypeName.Module, moduleDecl.Name, StringComparison.Ordinal)))
                .Where(p => !HasMembersReferencingUnsupportedModule(p, typeDatabase))
                // Note: InheritsCodable filter removed — EveryProtocol now emits Codable/Error
                // stub conformances so protocols that inherit Decodable/Encodable are supported.
                // Skip protocols requiring NSObjectProtocol identity semantics —
                // EveryProtocol can't provide NSObject methods (isEqual:, hash, etc.).
                // Pure AnyObject class-bound protocols are allowed (EveryProtocol is a class).
                // Protocols whose only ObjC-rooted requirement is NSObjectProtocol (or NSCoding,
                // which implies it and is witnessed by a no-op carrier stub) are routed through
                // the NSObject-rooted EveryObjCProtocol helper class downstream, so they remain
                // "suitable" here. NSSecureCoding / NSCopying / NSMutableCopying still drop out
                // because their encoding / copying surfaces cannot be synthesized.
                .Where(p => !EveryProtocolEmitter.IsClassBoundProtocol(p, protocols)
                            || EveryProtocolEmitter.IsNSObjectProtocolOnly(p, protocols))
                // Skip protocols whose inheritance names a concrete class (e.g.
                // `protocol P : UIGestureRecognizer`). EveryProtocol is a plain
                // Swift class and cannot satisfy a class superclass constraint.
                // Exception: Entity-rooted protocols (`protocol HasAnchoring : Entity`)
                // are routed downstream through the EveryEntityProtocol helper class,
                // so they remain "suitable" here. WillSkipConformance /
                // EmitProtocolConformance perform the same routing check per-protocol.
                .Where(p => !EveryProtocolEmitter.HasClassSuperclassRequirement(p, typeDatabase, protocols)
                            || EveryProtocolEmitter.IsEntityRootedProtocol(p, typeDatabase, protocols))
                // Skip CaseIterable — requires compiler-synthesized allCases. Transitive check.
                .Where(p => !EveryProtocolEmitter.InheritsCaseIterable(p, protocols))
                // Skip protocols that inherit from protocols with associated types or Self requirements.
                // EveryProtocol can't provide concrete associated types for inherited PATs.
                .Where(p => !InheritsProtocolWithAssociatedTypes(p, protocols, typeDatabase))
                // Skip protocols that inherit from stdlib protocols with requirements
                // EveryProtocol can't satisfy (CustomStringConvertible, CodingKey, etc.).
                // The vtable only includes the protocol's own members, not inherited ones.
                .Where(p => !EveryProtocolEmitter.InheritsUnsatisfiedStdlibProtocol(p, protocols))
                // Skip protocols that will instead be admitted as a FORWARD-ONLY (read-only)
                // proxy for a forward-SAFE reverse-impossible reason. A protocol blocked only by
                // a stripped `__`-prefixed hidden requirement (the RealityFoundation `Material`
                // shape) is structurally suitable here — it has no Self/associated-type/class
                // requirement — so absent this filter it lands in `suitableNames`, its reverse
                // conformance is then skipped downstream (`WillSkipConformance` /
                // `EmitProtocolConformance` fire on `HasUnsatisfiedHiddenRequirements`), and the
                // read-only admission below excludes it via its `!suitableNames` guard — leaving
                // the proxy fully suppressed and every `any P` / `[any P]` / `(any P)?` getter a
                // throwing stub. Dropping it here (the same way the stdlib-inheritance arm is
                // dropped by the filter above) lets it fall into the read-only admission, which
                // emits a forward-only proxy so the existential reads through its own witness
                // table. `HasForwardSafeReverseImpossibleReason` is the exact set the read-only
                // admission accepts, so forward-UNSAFE hidden-requirement protocols (missing
                // requirements, absent TBD descriptors, …) return false here and stay suitable —
                // preserving their current full-suppression classification.
                .Where(p => !EveryProtocolEmitter.HasForwardSafeReverseImpossibleReason(p, typeDatabase, protocols))
                // Skip protocols whose member signatures reference types from this module
                // that are not in the type database (module-internal types). EveryProtocol
                // can't implement methods requiring internal types.
                .Where(p => !HasMembersReferencingInternalTypes(p, typeDatabase, moduleDecl.Name))
                .ToList();

            // Cross-module parents are collected here (rather than just before the
            // conformance loop) so the property-type-count conflict gate below can
            // see them. Without this, a same-name+different-type property collision
            // between a local protocol and a cross-module parent slips past the gate
            // and reaches swiftc as a redeclaration error on EveryProtocol — the
            // ownership map keys (name + typeKey) treat the two types as separate
            // entries, both emit bodies, and Swift rejects the redeclared var.
            var crossModuleParents = CollectCrossModuleParentDecls(suitableProtocols, moduleDecl);

            // Global dedup of EveryProtocol stubs is keyed by property name, so two protocols
            // that each require a property with the SAME name but DIFFERENT types produce one
            // successful conformance and one whose required member gets skipped (breaking the
            // extension with "type 'EveryProtocol' does not conform"). Pre-scan to find such
            // conflicting names and drop every protocol that participates, so the remaining
            // conformances compile cleanly. Example: MusicKit.LibraryAlbumFilter.artistName is
            // `String` while MusicKit.LibraryMusicVideoFilter.artistName is `String?`.
            // Only protocol *requirements* contribute witnesses to EveryProtocol's conformance —
            // default implementations from same-protocol extensions are extension methods on the
            // existential, not witness-table entries. Including them in the conflict scan turned
            // RealityFoundation umbrella-prefixed protocols (e.g. RealityKit.Material's
            // `name: String?` extension default) into false positives that dropped the canonical
            // BlendTreeNode/AnimationDefinition/MaterialFunction protocols whose `name: String`
            // is genuinely required.
            //
            // Scans the union of local protocols + cross-module parents — both contribute
            // EveryProtocol conformances and must agree on the property type or both get
            // dropped from their respective lists.
            var propertyTypeCounts = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var p in suitableProtocols.Concat(crossModuleParents))
            {
                foreach (var prop in p.Properties)
                {
                    if (prop.IsStatic || prop.IsObjCOptional || !prop.IsProtocolRequirement)
                        continue;
                    if (!propertyTypeCounts.TryGetValue(prop.Name, out var types))
                    {
                        types = new HashSet<string>(StringComparer.Ordinal);
                        propertyTypeCounts[prop.Name] = types;
                    }
                    types.Add(prop.SwiftTypeSpec.ToString());
                }
            }
            var conflictingPropertyNames = propertyTypeCounts
                .Where(kvp => kvp.Value.Count > 1)
                .Select(kvp => kvp.Key)
                .ToHashSet(StringComparer.Ordinal);
            if (conflictingPropertyNames.Count > 0)
            {
                bool ContributesConflict(ProtocolDecl p) => p.Properties.Any(prop =>
                    !prop.IsStatic && !prop.IsObjCOptional && prop.IsProtocolRequirement &&
                    conflictingPropertyNames.Contains(prop.Name));
                // Drop conflicting locals first. This typically resolves the union conflict
                // (one side gone), so cross-module parents stay intact — any other local
                // protocol that inherits from a parent keeps its inherited witness body.
                suitableProtocols = suitableProtocols.Where(p => !ContributesConflict(p)).ToList();
                // Residual conflicts after the local-drop pass mean two or more cross-module
                // parents collide with each other (different dependency modules contributing
                // the same property name with different types). Drop those parents too — any
                // local protocol that inherited from them will fail conformance at swiftc
                // time and be removed by the strip-salvage path, same as before this gate
                // existed for the local case.
                var postLocalTypes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                foreach (var p in suitableProtocols.Concat(crossModuleParents))
                {
                    foreach (var prop in p.Properties)
                    {
                        if (prop.IsStatic || prop.IsObjCOptional || !prop.IsProtocolRequirement)
                            continue;
                        if (!postLocalTypes.TryGetValue(prop.Name, out var types))
                        {
                            types = new HashSet<string>(StringComparer.Ordinal);
                            postLocalTypes[prop.Name] = types;
                        }
                        types.Add(prop.SwiftTypeSpec.ToString());
                    }
                }
                var residualConflictNames = postLocalTypes
                    .Where(kvp => kvp.Value.Count > 1)
                    .Select(kvp => kvp.Key)
                    .ToHashSet(StringComparer.Ordinal);
                if (residualConflictNames.Count > 0)
                {
                    bool ContributesResidual(ProtocolDecl p) => p.Properties.Any(prop =>
                        !prop.IsStatic && !prop.IsObjCOptional && prop.IsProtocolRequirement &&
                        residualConflictNames.Contains(prop.Name));
                    var droppedParents = crossModuleParents.Where(ContributesResidual).ToList();
                    crossModuleParents = crossModuleParents.Where(p => !ContributesResidual(p)).ToList();
                    if (droppedParents.Count > 0)
                    {
                        // Cascade-drop any local whose inheritance chain reaches a dropped parent.
                        // Without this, the local's `extension EveryProtocol: L` would emit but the
                        // parent body it depends on for the inherited witness would not — leaving
                        // strip-salvage to clean up the wrapper, which can leave the C# proxy and
                        // P/Invoke surface out of sync with the stripped Swift.
                        var droppedParentKeys = droppedParents
                            .Select(p => $"{p.ModuleDecl?.Name}.{p.Name}")
                            .ToHashSet(StringComparer.Ordinal);
                        suitableProtocols = suitableProtocols
                            .Where(local => !TransitivelyInheritsCrossModuleParent(local, moduleDecl, droppedParentKeys))
                            .ToList();
                    }
                }
            }

            // Accessor-set conflict is no longer resolved by dropping protocols. Instead the
            // emitter computes a property-emission-ownership map below (over the union of
            // local + cross-module-parent protocols) — the protocol with the fattest accessor
            // set owns the body, siblings emit empty extensions, and Swift's cross-extension
            // witness resolution stitches them together.
            //
            // See EveryProtocolEmitter.ComputePropertyEmissionPlans and the matching property
            // dedup + fan-out branches in EmitProtocolExtension / EmitPropertyImplementation.

            // Member-kind conflict: protocol A requires `var label: T { get }` while protocol B
            // requires `func label() -> T`. Swift rejects `var label` and `func label()` on the
            // same class as "invalid redeclaration of 'label()'". Drop the function-side
            // protocols (property-side keeps the more common shape).
            //
            // Scans the union of local protocols + cross-module parents. Property names
            // contributed by EITHER side preempt method names on EITHER side — a parent
            // declaring `var label` plus a local declaring `func label() -> T` (or the
            // inverse) would otherwise emit both shapes on EveryProtocol and trigger the
            // same swiftc redeclaration. Drop the method-side protocol from whichever
            // list (`suitableProtocols` or `crossModuleParents`) it lives in.
            var propertyNamesAsRequirements = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in suitableProtocols.Concat(crossModuleParents))
            {
                foreach (var prop in p.Properties)
                {
                    if (prop.IsStatic || prop.IsObjCOptional || !prop.IsProtocolRequirement)
                        continue;
                    propertyNamesAsRequirements.Add(prop.Name);
                }
            }
            var methodNamesCollidingWithProperties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in suitableProtocols.Concat(crossModuleParents))
            {
                foreach (var m in p.Methods)
                {
                    if (m.IsConstructor || m.MethodType == MethodType.Static || m.IsObjCOptional)
                        continue;
                    // Only zero-parameter methods can collide with a property base name.
                    if (m.CSSignature.Count > 1)
                        continue;
                    if (propertyNamesAsRequirements.Contains(m.Name))
                        methodNamesCollidingWithProperties.Add(m.Name);
                }
            }
            if (methodNamesCollidingWithProperties.Count > 0)
            {
                bool ContributesMemberKindConflict(ProtocolDecl p) => p.Methods.Any(m =>
                    !m.IsConstructor && m.MethodType != MethodType.Static && !m.IsObjCOptional &&
                    m.CSSignature.Count == 1 &&
                    methodNamesCollidingWithProperties.Contains(m.Name));
                var droppedMemberKindParents = crossModuleParents.Where(ContributesMemberKindConflict).ToList();
                suitableProtocols = suitableProtocols.Where(p => !ContributesMemberKindConflict(p)).ToList();
                crossModuleParents = crossModuleParents.Where(p => !ContributesMemberKindConflict(p)).ToList();
                if (droppedMemberKindParents.Count > 0)
                {
                    // Cascade-drop locals that inherit a method-side cross-module parent we just
                    // dropped. Mirrors the property-type-count residual cascade above — without
                    // it, the local's `extension EveryProtocol: L` would emit, swiftc would
                    // accept it (the parent body is no longer there to redeclare), but the
                    // inherited witness body the local depends on for dispatch is gone, leaving
                    // strip-salvage to clean up the wrapper while the C# proxy/P/Invoke surface
                    // remains out of sync.
                    var droppedMemberKindParentKeys = droppedMemberKindParents
                        .Select(p => $"{p.ModuleDecl?.Name}.{p.Name}")
                        .ToHashSet(StringComparer.Ordinal);
                    suitableProtocols = suitableProtocols
                        .Where(local => !TransitivelyInheritsCrossModuleParent(local, moduleDecl, droppedMemberKindParentKeys))
                        .ToList();
                }
            }

            // Read-only (Swift-vended-only) proxy protocols: those whose reverse EveryProtocol
            // conformance can't be synthesized for a FORWARD-SAFE reason, so no conformance is
            // emitted and the suitableProtocols filter chain dropped them — yet `any P` is still
            // a valid READ target through its own witness table. The C# proxy is still emitted so
            // Swift-vended `any P` returns and `[any P]` array elements can be wrapped and
            // dispatched through the existential's OWN witness table — the witness-dispatch
            // accessors reconstruct `any P` via its static type (`load(as: (any P).self)`),
            // which needs no EveryProtocol conformance. Two disjoint admission reasons (see the
            // .Where below): a non-Entity-rooted class-superclass requirement, OR a non-class
            // protocol blocked only by a stripped hidden requirement / an inherited unsatisfiable
            // stdlib protocol. Mirrors the suitableProtocols filter chain but KEEPS the protocols
            // it drops for those reasons, and excludes any that already made it into
            // suitableProtocols.
            var suitableNames = new HashSet<string>(suitableProtocols.Select(p => p.Name), StringComparer.Ordinal);
            var readOnlyProxyProtocols = protocols
                .Where(p => !p.HasSelfRequirement && p.AssociatedTypes.Count == 0)
                .Where(p => !p.IsModuleInternal)
                .Where(p => IsMangledNameFromModule(p.MangledName, moduleDecl.Name)
                            || (string.IsNullOrEmpty(p.MangledName)
                                && p.SwiftTypeName != null
                                && string.Equals(p.SwiftTypeName.Module, moduleDecl.Name, StringComparison.Ordinal)))
                .Where(p => !HasMembersReferencingUnsupportedModule(p, typeDatabase))
                .Where(p => !EveryProtocolEmitter.IsClassBoundProtocol(p, protocols)
                            || EveryProtocolEmitter.IsNSObjectProtocolOnly(p, protocols))
                // Admit a protocol as a forward-only proxy under EITHER of two disjoint,
                // forward-readable reasons its reverse conformance can't be synthesized:
                //   (1) a class-superclass requirement that is NOT Entity-rooted — the
                //       original read-only population (`extension EveryProtocol: P` is
                //       unsatisfiable because EveryProtocol has no class lineage, yet the
                //       superclass-constrained `any P` reads fine). This arm additionally
                //       excludes a stdlib-inheriting superclass protocol, matching the
                //       prior standalone filter exactly.
                //   (2) a non-class-superclass protocol that still can't host the reverse
                //       witness for a forward-SAFE structural reason — a stripped
                //       `__`-prefixed hidden requirement, or an inherited stdlib protocol
                //       EveryProtocol can't witness. Without this the suppressed proxy turned
                //       a getter returning `any P` / `[any P]` / `(any P)?` into a throwing
                //       NotSupportedException stub. The forward read (`load(as: (any P).self)`
                //       + witness-table dispatch) is identical for both arms.
                .Where(p => (EveryProtocolEmitter.HasClassSuperclassRequirement(p, typeDatabase, protocols)
                                && !EveryProtocolEmitter.IsEntityRootedProtocol(p, typeDatabase, protocols)
                                && !EveryProtocolEmitter.InheritsUnsatisfiedStdlibProtocol(p, protocols))
                            || EveryProtocolEmitter.HasForwardSafeReverseImpossibleReason(p, typeDatabase, protocols))
                .Where(p => !EveryProtocolEmitter.InheritsCaseIterable(p, protocols))
                // A mixed-generic protocol (a method-level-generic requirement coexisting with a
                // non-generic instance member) emits NO Swift witness-dispatch accessors at all —
                // EmitWitnessDispatchFunctions is gated protocol-wide on !IsMixedGenericProtocol
                // (below and at the suitable-protocol loop), so even a plain dispatchable property
                // gets no @_cdecl accessor. The C# forward-read proxy gates per-member, so it would
                // still emit that property's NativeMethods P/Invoke -> dangling symbol ->
                // EntryPointNotFoundException at runtime. Fail closed (keep the throwing stub).
                // This standalone filter also covers the class-superclass admission arm above,
                // which bypasses HasForwardSafeReverseImpossibleReason's matching exclusion.
                .Where(p => !EveryProtocolEmitter.IsMixedGenericProtocol(p))
                // selfRequirementBlocks:false — a forward-only proxy reads `any P` through P's OWN
                // witness table and never dispatches an inherited Self-typed requirement, so an
                // inherited Self-requirement-ONLY stdlib protocol (Equatable/Hashable/Comparable —
                // no associated types) is forward-safe and admitted. Genuine associated types,
                // where `any P` would be an invalid existential, still block.
                .Where(p => !InheritsProtocolWithAssociatedTypes(p, protocols, typeDatabase, selfRequirementBlocks: false))
                .Where(p => !HasMembersReferencingInternalTypes(p, typeDatabase, moduleDecl.Name))
                .Where(p => !suitableNames.Contains(p.Name))
                .ToList();
            foreach (var p in readOnlyProxyProtocols)
                emissionCtx?.MarkReadOnlyProxy(p.Name);

            // Attribute every protocol dropped from EveryProtocol candidacy (mechanism D:
            // "dropped-candidacy"). Such a protocol left `suitableProtocols` via one of the .Where
            // filters or a conflict pass ABOVE without any RecordConformanceDecision call, so when
            // ProtocolHandler later classifies its proxy SuppressedByConformance, GetConformanceSkipReason
            // returns null and it falls back to ForDroppedProtocol → "no decision recorded" (the Review-tier
            // noise this pass eliminates). Record a `false` decision carrying the specific structural cause
            // so the persisted skip classifies ExpectedStructural.
            //
            // This runs on BOTH the non-empty and the empty early-return path below. When the module's
            // suitable-protocol set is empty, no EveryProtocol carrier is emitted, so every non-read-only
            // protocol's proxy is suppressed by the carrier check in ProtocolProxyEmissionPolicy.Decide —
            // and those suppressed proxies would otherwise report "no decision recorded". Attributing them
            // here gives an honest structural disposition. It no longer risks flipping Decide's answer:
            // Decide keys suppression on the carrier flag (WasEveryProtocolCarrierEmitted), not on the
            // ConformanceDecisions count, so pushing the count 0→>0 here is inert for emission — a
            // carrier-less module suppresses every non-read-only proxy regardless of the count, and
            // read-only protocols are excluded below and keep emitting. So this only enriches the report;
            // the emitted C#/Swift is unchanged.
            if (emissionCtx != null)
            {
                var suitableNamesForDrop = new HashSet<string>(suitableProtocols.Select(p => p.Name), StringComparer.Ordinal);
                var readOnlyNamesForDrop = new HashSet<string>(readOnlyProxyProtocols.Select(p => p.Name), StringComparer.Ordinal);
                foreach (var p in protocols)
                {
                    // Self-/associated-type and module-internal protocols are already classified
                    // correctly by ForDroppedProtocol (ExpectedStructural / ExpectedNonPublic); leave them.
                    if (p.HasSelfRequirement || p.AssociatedTypes.Count > 0 || p.IsModuleInternal)
                        continue;
                    // A protocol that will emit (suitable) or reads via its own witness table (read-only)
                    // is not a dropped candidacy.
                    if (suitableNamesForDrop.Contains(p.Name) || readOnlyNamesForDrop.Contains(p.Name))
                        continue;
                    // Unsupported-module protocols are classified on the SkippedUnsupportedModule channel,
                    // not SuppressedByConformance — don't double-attribute them here.
                    if (HasMembersReferencingUnsupportedModule(p, typeDatabase))
                        continue;
                    var dropKey = p.SwiftTypeName?.ModuleQualifiedName ?? p.Name;
                    // Already emitted or already carries an emit-time decline reason — don't overwrite.
                    if (emissionCtx.WasConformanceEmitted(dropKey) || emissionCtx.GetConformanceSkipReason(dropKey) != null)
                        continue;
                    var cause = ClassifyDroppedCandidacy(
                        p, protocols, typeDatabase, moduleDecl.Name,
                        conflictingPropertyNames, methodNamesCollidingWithProperties);
                    emissionCtx.RecordConformanceDecision(dropKey, emitted: false, cause);
                }
            }

            if (!suitableProtocols.Any())
            {
                // No EveryProtocol-backed conformances — but read-only proxies still need their
                // Swift witness-dispatch accessors emitted (the C# proxy's read path P/Invokes
                // into them). The EveryProtocol-class scaffolding is skipped (nothing conforms).
                if (readOnlyProxyProtocols.Count > 0)
                {
                    var readOnlyDispatchEmitter = new WitnessDispatchEmitter(typeDatabase, _logger, moduleDecl.Name, emissionCtx);
                    foreach (var protocolDecl in readOnlyProxyProtocols)
                        if (!EveryProtocolEmitter.IsMixedGenericProtocol(protocolDecl))
                            readOnlyDispatchEmitter.EmitWitnessDispatchFunctions(swiftWriter, protocolDecl);
                }
                return;
            }

            var emitter = new EveryProtocolEmitter(typeDatabase, _logger, moduleDecl.Name, emissionCtx);
            var dispatchEmitter = new WitnessDispatchEmitter(typeDatabase, _logger, moduleDecl.Name, emissionCtx);

            // Emit the EveryProtocol class once. Passing suitableProtocols lets the
            // emitter pre-scan for Entity-rooted protocols (Failure B) and conditionally
            // emit the EveryEntityProtocol Swift class before the per-protocol
            // EmitProtocolConformance loop below opens any `extension EveryEntityProtocol: ...`
            // blocks.
            emitter.EmitEveryProtocolClass(swiftWriter, suitableProtocols);

            // Emit Codable/Error stub conformances on EveryProtocol if any suitable protocol
            // requires them. These stubs let EveryProtocol satisfy the inherited Codable/Error
            // requirements when conforming to protocols that inherit Decodable/Encodable/Error.
            emitter.EmitCodableStubsIfNeeded(swiftWriter, suitableProtocols, protocols, typeDatabase);

            // Emit a no-op NSCoding stub on the ObjC-rooted carrier when a suitable protocol
            // transitively inherits NSCoding (e.g. RoomPlan.RoomCaptureViewDelegate). Without it
            // the synthesised `extension EveryObjCProtocol: X` can't satisfy X's inherited NSCoding
            // requirement. The stub is no-op — the synthetic carrier never archives.
            emitter.EmitObjCCodingStubIfNeeded(swiftWriter, suitableProtocols, protocols);

            // Every module-local protocol that lost the candidacy filter above. None of these get an
            // `extension {carrier}: {P}` — so a surviving protocol that INHERITS one would emit a
            // conformance Swift can't satisfy (the extension body witnesses only the child's own
            // members, and the parent's requirements go unwitnessed → "type 'EveryProtocol' does not
            // conform to protocol '{parent}'" at wrapper compile). The pre-scan can't discover this
            // on its own: it only iterates the survivors, so a dropped parent is invisible to it.
            // Both spellings are seeded because a constraint may name either.
            var droppedCandidates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dropped in protocols.Except(suitableProtocols))
            {
                droppedCandidates.Add(dropped.Name);
                if (dropped.SwiftTypeName != null)
                    droppedCandidates.Add(dropped.SwiftTypeName.ModuleQualifiedName);
            }

            // Pre-scan: identify protocols that will be skipped by structural gates.
            // This makes inherited-conformance checks order-independent. Pass the cross-module
            // parents so the cross-carrier suppression gate can resolve a child that inherits a
            // parent in a --framework-dependency module (which emits its own EveryProtocol
            // conformance below) and detect a carrier split across the module boundary. Cross-module
            // parents are never in moduleDecl.Protocols, so droppedCandidates can't shadow one.
            emitter.PreScanProtocols(suitableProtocols, crossModuleParents, droppedCandidates);

            // Drop cross-module parents that no LONGER have a live local child after the pre-scan.
            // A cross-module parent's Swift EveryProtocol scaffolding (vtable struct + setter
            // trampoline + extension, emitted at the crossModuleParents loop below) is only wired
            // on the C# side by an inheriting LOCAL child proxy's static cctor
            // (EmitCrossModuleParentVtableInit). If every local child that reaches this parent is
            // suppressed — e.g. the cross-carrier gate drops a `Child : Dep.Parent, NSObjectProtocol`
            // whose carrier splits from the parent's — no C# mirror is emitted, and the parent's
            // Swift scaffolding is left orphaned: Swift declares the `_vtable` struct + `Set…_vtable`
            // setter but C# never mirrors it or P/Invokes the setter (an artifact-parity divergence
            // and a latent EntryPointNotFound). Keep a parent iff some EMITTABLE local child
            // transitively inherits it; otherwise neither side emits.
            crossModuleParents = crossModuleParents
                .Where(parent => suitableProtocols.Any(local =>
                    !emitter.IsConformanceSkipped(local)
                    && TransitivelyInheritsCrossModuleParent(
                        local, moduleDecl,
                        new HashSet<string>(StringComparer.Ordinal)
                        {
                            $"{parent.ModuleDecl?.Name}.{parent.Name}",
                        })))
                .ToList();

            // Track emitted method signatures globally to detect conflicts across protocols
            // Key is the Swift method signature (e.g., "removeAll()")
            var globalEmittedSignatures = new HashSet<string>();

            // Cross-module inherited-delegate parents
            // (justinwojo/swift-dotnet-bindings#40 cross-module variant):
            // When a local protocol inherits from a parent in a --framework-dependency
            // module, Swift's witness dispatch for the inherited requirement routes
            // through the LOCAL module's EveryProtocol — which must therefore conform
            // to the parent and supply the inherited witness body. The parent's full
            // EveryProtocol scaffolding (vtable struct + setter + extension) is emitted
            // here once per unique cross-module parent; vtable population is wired by
            // each local child proxy's static cctor in ProtocolProxyEmitter
            // (`EmitCrossModuleParentVtableInit`).
            //
            // No witness-dispatch wrappers — those are the C# → Swift direction and
            // already live in the dependency module's bindings. We only need Swift → C#
            // (callback) for the inherited requirement.
            //
            // `crossModuleParents` is collected earlier (above the property-type-count
            // gate) so the gate operates on the union of local + parent protocols.

            // Pre-pass: determine which method signatures must be emitted non-throwing.
            // In Swift, a non-throwing method satisfies both throwing and non-throwing protocol
            // requirements, but a throwing method does NOT satisfy a non-throwing requirement.
            // If two protocols share the same method signature but differ in throws-ness,
            // we must emit the non-throwing variant to satisfy both conformances. Computed
            // over the union so a throwing local + non-throwing parent (or vice-versa) still
            // resolves to the non-throwing form.
            var nonThrowingOverrides = ComputeNonThrowingOverrides(
                suitableProtocols.Concat(crossModuleParents), emitter);

            // Plan input must exclude protocols whose EveryProtocol conformance will be
            // skipped at emission time, or that will emit fatalError() stubs instead of
            // real vtable dispatch bodies, but which are NOT yet filtered out of
            // suitableProtocols / crossModuleParents. Without this guard the owner-resolution
            // can pick a never-emitted (or stub-only) protocol because HasSetter wins
            // ownership, the owner's real body never lands in the Swift wrapper, and the
            // other siblings skip their own bodies in deference to a phantom owner.
            //
            // Three filters compose:
            //   1. HasNoncopyableMember — the wrapper's inout trampoline rejects ~Copyable;
            //      conformance is dropped entirely.
            //   2. IsConformanceSkipped — already classified un-emittable by PreScanProtocols
            //      (Self / missing requirements / convention-c closures / suppressed members /
            //      hidden requirements / missing TBD descriptors / subscript-bound generics
            //      / transitive genericSig propagation). Cross-module parents aren't
            //      pre-scanned, so they pass through this filter — sibling-plan failures
            //      for cross-module-parent owners are tracked separately.
            //   3. IsMixedGenericProtocol — protocols with both method-level generic and
            //      non-generic instance members emit fatalError() stubs for ALL properties
            //      and subscripts (the type projection pipeline can't render the non-generic
            //      members correctly when method-level generics are in scope), so they can
            //      never serve as a real dispatch owner. Member-level stub shapes
            //      (self-typed properties, non-dispatchable closure properties) are NOT
            //      filtered here because they're per-property — owner selection would need
            //      a member-level predicate, which the current plan-compute interface
            //      doesn't accept. If a real-world fixture surfaces, lift the per-property
            //      check into ComputePropertyEmissionPlans rather than overconstraining
            //      the per-protocol filter here.
            bool IsEmittable(ProtocolDecl p)
                => !EveryProtocolEmitter.HasNoncopyableMember(p, typeDatabase)
                   && !emitter.IsConformanceSkipped(p)
                   && !EveryProtocolEmitter.IsMixedGenericProtocol(p);
            var allCandidateProtocols = suitableProtocols.Concat(crossModuleParents).ToList();
            var planInputProtocols = allCandidateProtocols.Where(IsEmittable).ToList();
            // Protocols dropped by IsEmittable but which still participate in the same
            // (propertyName, propertyType) / subscript-signature group with an emittable owner.
            // Swift cross-extension witness resolution can still route a filtered protocol's
            // existential dispatch through the emittable owner's body, so the plans must know
            // the group "had filtered peers" to switch the owner body into the nil-check
            // fan-out shape and avoid a force-unwrap SIGSEGV when only the filtered side has
            // been registered. See HasFilteredPeers on PropertyEmissionPlan/SubscriptEmissionPlan.
            var filteredPeers = allCandidateProtocols.Where(p => !IsEmittable(p)).ToList();

            // Pre-pass: compute the per-property emission plan across the union of local +
            // parent protocols. The owner of a shared (propertyName, propertyType) emits the
            // body; siblings emit empty extensions and rely on Swift's cross-extension
            // witness resolution. The owner's body fans out across every sibling vtable so
            // dispatch through any sibling existential finds the populated vtable. Has-setter
            // wins over get-only; lex tie-break for determinism.
            // See EveryProtocolEmitter.ComputePropertyEmissionPlans. Instance call: the grouping
            // is partitioned by carrier class, which needs the emitter's pre-scanned protocol/type
            // state.
            var propertyPlans = emitter.ComputePropertyEmissionPlans(planInputProtocols, filteredPeers);

            // Sibling fallback map: when ProtocolProxyEmitter emits a property receiver for
            // a property in a sibling group, the receiver tries its own interface first and
            // then falls back to the sibling interfaces recorded here. The Swift fan-out can
            // pick any populated sibling vtable; whichever receiver runs locates the proxy
            // via per-instance SwiftObjectRegistry lookups across all sibling interfaces.
            // Without this fallback the receiver in the chosen vtable's proxy class cannot
            // see a handle registered as a different sibling's proxy and returns empty /
            // no-ops, producing order-dependent dispatch bugs.
            emissionCtx?.SetSiblingPropertyFallbacks(
                emitter.ComputeSiblingPropertyFallbacks(planInputProtocols));

            // Sibling-subscript plan + fallback map: mirror of the property sibling pipeline.
            // The owner of a shared subscript signature emits the body with fan-out across
            // every sibling's per-protocol vtable index; siblings emit empty extensions.
            var subscriptPlans = emitter.ComputeSubscriptEmissionPlans(planInputProtocols, filteredPeers);
            emissionCtx?.SetSiblingSubscriptFallbacks(
                emitter.ComputeSiblingSubscriptFallbacks(planInputProtocols));

            // Sibling-method plan + fallback map: same-signature-method counterpart of the property
            // and subscript sibling pipelines. The owner of a shared method signature emits the body
            // with fan-out across every sibling's per-protocol vtable index; siblings emit empty
            // extensions. Without this, a C# impl conforming to ONLY a non-owner protocol would
            // dispatch through the owner's nil global vtable and SIGSEGV on the force-unwrap.
            // These are instance methods (not static) because the grouping key is the projected
            // Swift signature. See EveryProtocolEmitter.ComputeMethodEmissionPlans.
            var methodPlans = emitter.ComputeMethodEmissionPlans(planInputProtocols, filteredPeers);
            emissionCtx?.SetSiblingMethodFallbacks(
                emitter.ComputeSiblingMethodFallbacks(planInputProtocols));

            // Emit conformances and witness dispatch accessors for each suitable protocol
            foreach (var protocolDecl in suitableProtocols)
            {
                _logger.LogDebug($"Emitting EveryProtocol conformance for {protocolDecl.Name}");
                emitter.EmitProtocolConformance(swiftWriter, protocolDecl, globalEmittedSignatures, nonThrowingOverrides, propertyPlans, subscriptPlans, methodPlans);
                // Skip witness dispatch for mixed-generic protocols — the type projection
                // pipeline generates incorrect types when method-level generic parameters
                // are in scope (e.g., RxTime→Double instead of Date).
                if (!EveryProtocolEmitter.IsMixedGenericProtocol(protocolDecl))
                    dispatchEmitter.EmitWitnessDispatchFunctions(swiftWriter, protocolDecl);
            }

            // Read-only proxy protocols: emit ONLY their Swift witness-dispatch accessors (no
            // EveryProtocol conformance). The C# proxy classes are emitted by ProtocolHandler;
            // these accessors are the @_cdecl read-path entry points the proxy's NativeMethods
            // P/Invoke into for `any P` member reads.
            foreach (var protocolDecl in readOnlyProxyProtocols)
            {
                _logger.LogDebug($"Emitting read-only witness dispatch for {protocolDecl.Name} (Swift-vended-only proxy)");
                if (!EveryProtocolEmitter.IsMixedGenericProtocol(protocolDecl))
                    dispatchEmitter.EmitWitnessDispatchFunctions(swiftWriter, protocolDecl);
            }

            foreach (var parentDecl in crossModuleParents)
            {
                _logger.LogDebug($"Emitting cross-module parent EveryProtocol conformance for {parentDecl.ModuleDecl?.Name}.{parentDecl.Name}");
                emitter.EmitProtocolConformance(swiftWriter, parentDecl, globalEmittedSignatures, nonThrowingOverrides, propertyPlans, subscriptPlans, methodPlans);
            }
        }

        /// <summary>
        /// Collects unique parent <see cref="ProtocolDecl"/>s from <c>--framework-dependency</c>
        /// modules that any local child protocol inherits across the module boundary.
        /// Walks transitively: a local child inheriting <c>B.Parent</c> which itself inherits
        /// <c>C.Grandparent</c> yields BOTH parent and grandparent so the local wrapper emits
        /// per-ancestor companion conformance + setter trampoline, and the child proxy's cctor
        /// can populate every ancestor's vtable storage in the local wrapper.
        ///
        /// Returned decls have <c>ModuleDecl.Name</c> pointing to the dependency module;
        /// callers drive <see cref="EveryProtocolEmitter.EmitProtocolConformance"/> with them
        /// so the LOCAL module's EveryProtocol conforms to each ancestor in the chain.
        /// Dedup keyed by <c>{module}.{name}</c> so multiple children sharing an ancestor
        /// emit one set of vtable+setter+extension in the local wrapper. Resolution stops
        /// gracefully at ancestors whose module isn't loaded as a dependency (the parser
        /// invocation didn't pass <c>--framework-dependency</c> for that module).
        /// </summary>
        private static List<ProtocolDecl> CollectCrossModuleParentDecls(
            IReadOnlyList<ProtocolDecl> localProtocols, ModuleDecl moduleDecl)
        {
            if (moduleDecl.DependencyProtocols.Count == 0)
                return new List<ProtocolDecl>();

            var currentModule = moduleDecl.Name;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var collected = new List<ProtocolDecl>();
            var pending = new Queue<ProtocolDecl>();

            // Seed the queue with direct cross-module parents of the local protocols.
            foreach (var local in localProtocols)
            {
                EnqueueCrossModuleAncestors(local.InheritedProtocols, moduleDecl, currentModule, seen, pending);
            }

            // Drain transitively — each parent's own InheritedProtocols may reach
            // a different dependency module's grandparent.
            while (pending.Count > 0)
            {
                var ancestor = pending.Dequeue();
                collected.Add(ancestor);
                EnqueueCrossModuleAncestors(ancestor.InheritedProtocols, moduleDecl, currentModule, seen, pending);
            }
            return collected;
        }

        /// <summary>
        /// Returns true if <paramref name="local"/>'s inheritance chain reaches a cross-module
        /// protocol whose `{module}.{name}` key is in <paramref name="droppedParentKeys"/>. Used
        /// to cascade-drop locals whose inherited witness body would no longer be emitted.
        /// Walks BOTH same-module intermediate protocols (resolved via <c>moduleDecl.Protocols</c>)
        /// AND cross-module ancestors (resolved via <c>moduleDecl.DependencyProtocols</c>) so a
        /// chain like <c>LocalGrandchild : LocalChild : DroppedDepParent</c> still detects the
        /// dropped ancestor.
        /// </summary>
        private static bool TransitivelyInheritsCrossModuleParent(
            ProtocolDecl local, ModuleDecl moduleDecl, HashSet<string> droppedParentKeys)
        {
            var currentModule = moduleDecl.Name;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<NamedTypeSpec>(local.InheritedProtocols);
            while (pending.Count > 0)
            {
                var inherited = pending.Dequeue();
                if (inherited.NameWithoutModule is "Sendable" or "Escapable" or "Copyable" or "SendableMetatype" or "AnyObject")
                    continue;
                var inhModule = inherited.Module;
                if (string.IsNullOrEmpty(inhModule) || inhModule == currentModule)
                {
                    // Same-module intermediate. Resolve to its ProtocolDecl and walk its own
                    // InheritedProtocols — the dropped ancestor may sit two levels up via a
                    // local hop (`LocalGrandchild : LocalChild : DroppedDepParent`).
                    var localKey = $"{currentModule}.{inherited.NameWithoutModule}";
                    if (!seen.Add(localKey))
                        continue;
                    var localDecl = moduleDecl.Protocols.FirstOrDefault(p => p.Name == inherited.NameWithoutModule);
                    if (localDecl != null)
                    {
                        foreach (var parent in localDecl.InheritedProtocols)
                            pending.Enqueue(parent);
                    }
                    continue;
                }
                var key = $"{inhModule}.{inherited.NameWithoutModule}";
                if (!seen.Add(key))
                    continue;
                if (droppedParentKeys.Contains(key))
                    return true;
                // Walk transitively — a dropped grandparent two levels up still breaks the
                // child's inherited witness dispatch.
                if (moduleDecl.DependencyProtocols.TryGetValue(inhModule, out var depProtos))
                {
                    var parentDecl = depProtos.FirstOrDefault(dp => dp.Name == inherited.NameWithoutModule);
                    if (parentDecl != null)
                    {
                        foreach (var grandparent in parentDecl.InheritedProtocols)
                            pending.Enqueue(grandparent);
                    }
                }
            }
            return false;
        }

        private static void EnqueueCrossModuleAncestors(
            IEnumerable<NamedTypeSpec> inheritedProtocols,
            ModuleDecl moduleDecl,
            string currentModule,
            HashSet<string> seen,
            Queue<ProtocolDecl> pending)
        {
            foreach (var inherited in inheritedProtocols)
            {
                var inhModule = inherited.Module;
                if (string.IsNullOrEmpty(inhModule) || inhModule == currentModule)
                    continue;
                if (inherited.NameWithoutModule is "Sendable" or "Escapable" or "Copyable" or "SendableMetatype" or "AnyObject")
                    continue;
                if (!moduleDecl.DependencyProtocols.TryGetValue(inhModule, out var depProtos))
                    continue;
                var ancestorDecl = depProtos.FirstOrDefault(dp => dp.Name == inherited.NameWithoutModule);
                if (ancestorDecl == null)
                    continue;
                var key = $"{inhModule}.{ancestorDecl.Name}";
                if (seen.Add(key))
                    pending.Enqueue(ancestorDecl);
            }
        }

        /// <summary>
        /// Pre-computes the set of method full signatures that must be emitted non-throwing.
        /// A signature is included if it appears as both throwing (in at least one protocol)
        /// and non-throwing (in at least one other protocol). The non-throwing variant must
        /// win because it satisfies both requirements.
        /// Uses full signatures (name + param types + return type) so that overloads with
        /// different parameter types are tracked independently — e.g., a non-throwing
        /// validate(input: String) won't suppress throws on validate(input: Int32) throws.
        /// </summary>
        private static HashSet<string> ComputeNonThrowingOverrides(
            IEnumerable<ProtocolDecl> protocols, EveryProtocolEmitter emitter)
        {
            var throwingSignatures = new HashSet<string>();
            var nonThrowingSignatures = new HashSet<string>();

            foreach (var protocol in protocols)
            {
                foreach (var method in protocol.Methods)
                {
                    if (method.IsConstructor || method.MethodType == MethodType.Static)
                        continue;

                    var sig = emitter.GetSwiftMethodFullSignature(method);
                    if (method.Throws)
                        throwingSignatures.Add(sig);
                    else
                        nonThrowingSignatures.Add(sig);
                }
            }

            // Only override signatures that appear in BOTH sets (i.e., a real conflict exists)
            nonThrowingSignatures.IntersectWith(throwingSignatures);
            return nonThrowingSignatures;
        }

        /// <summary>
        /// Checks if a protocol inherits from Decodable, Encodable, or Codable,
        /// either directly or transitively through inherited protocols.
        /// EveryProtocol's handle: UnsafeRawPointer? property cannot synthesize Codable
        /// conformance, so protocols requiring it must be skipped.
        /// </summary>
        /// <param name="protocolDecl">The protocol to check.</param>
        /// <param name="allProtocols">All protocols in the module for intra-module transitive lookup.
        /// If null, only direct inheritance is checked.</param>
        /// <param name="typeDatabase">Type database for cross-module transitive lookup via
        /// TypeRecordFlags.InheritsCodable. If null, only intra-module lookup is used.</param>
        internal static bool InheritsCodable(ProtocolDecl protocolDecl, IReadOnlyList<ProtocolDecl>? allProtocols = null, ITypeDatabase? typeDatabase = null)
        {
            return InheritsCodableRecursive(protocolDecl, allProtocols, typeDatabase, new HashSet<string>(StringComparer.Ordinal));
        }

        private static bool InheritsCodableRecursive(ProtocolDecl protocolDecl, IReadOnlyList<ProtocolDecl>? allProtocols, ITypeDatabase? typeDatabase, HashSet<string> visited)
        {
            // Prevent infinite loops in circular inheritance chains
            var qualifiedName = protocolDecl.SwiftTypeName?.ToString() ?? protocolDecl.Name;
            if (!visited.Add(qualifiedName))
                return false;

            foreach (var inherited in protocolDecl.InheritedProtocols)
            {
                var name = inherited.Name;
                // Strip module prefix if present (e.g., "Swift.Decodable" → "Decodable")
                var dotIndex = name.LastIndexOf('.');
                var simpleName = dotIndex >= 0 ? name.Substring(dotIndex + 1) : name;

                if (simpleName is "Decodable" or "Encodable" or "Codable")
                    return true;

                // Intra-module transitive check: look up the inherited protocol in the module
                if (allProtocols != null)
                {
                    var inheritedDecl = allProtocols.FirstOrDefault(p =>
                        p.Name == simpleName || p.Name == name ||
                        p.SwiftTypeName?.ToString() == name);
                    if (inheritedDecl != null && InheritsCodableRecursive(inheritedDecl, allProtocols, typeDatabase, visited))
                        return true;
                }

                // Cross-module transitive check: look up the inherited protocol's TypeRecord
                // in the type database. Dependency modules are processed before the main module,
                // so their InheritsCodable flags are already set.
                if (typeDatabase != null)
                {
                    var inheritedSwiftName = SwiftTypeName.FromModuleQualifiedName(name);
                    if (typeDatabase.TryGetTypeRecord(inheritedSwiftName, out var record) &&
                        record.Kind == TypeRecordKind.Protocol &&
                        record.Flags.HasFlag(TypeRecordFlags.InheritsCodable))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Known cross-module protocols with associated types or Self requirements that
        /// may not be present in the type database (e.g., Foundation types without .NET
        /// bindings). Any protocol inheriting from one of these cannot receive an
        /// EveryProtocol conformance.
        /// </summary>
        private static readonly HashSet<string> KnownCrossModuleProtocolsWithAssociatedTypes = new(StringComparer.Ordinal)
        {
            // Foundation predicate expression DSL (iOS 17+). Has associated type Output
            // and is implemented by a closed set of compiler-known expression structs.
            "Foundation.PredicateExpression",
            "Foundation.StandardPredicateExpression",
            // Swift concurrency clock protocol. Carries associated types Duration and Instant
            // (Instant itself constrained to InstantProtocol), so a witness would have to pin
            // concrete types the reverse-dispatch vtable has no way to choose.
            "_Concurrency.Clock",
        };

        /// <summary>
        /// Checks if a protocol transitively inherits from any protocol with associated types
        /// or (when <paramref name="selfRequirementBlocks"/> is true) Self requirements. These
        /// protocols cannot get a reverse EveryProtocol conformance because the associated type
        /// cannot be determined / the Self requirement cannot be witnessed.
        /// </summary>
        /// <param name="selfRequirementBlocks">
        /// When true (default, the reverse-conformance suitableProtocols path), an inherited
        /// protocol that carries ONLY a Self requirement (e.g. <c>Equatable</c>/<c>Hashable</c>/
        /// <c>Comparable</c>, which have no associated types but a Self-typed <c>==</c>/<c>&lt;</c>)
        /// still blocks. When false (the forward-only READ proxy path), it does NOT: a
        /// Self-requirement-only inherited stdlib protocol is the Population-B forward-safe case —
        /// <c>any P</c> remains a valid existential and the inherited Self requirement is never
        /// dispatched through the forward proxy, only <c>P</c>'s own members are. Genuine
        /// associated types (where <c>any P</c> would be invalid) still block in BOTH modes.
        /// </param>
        internal static bool InheritsProtocolWithAssociatedTypes(ProtocolDecl protocolDecl, IReadOnlyList<ProtocolDecl>? allProtocols = null, ITypeDatabase? typeDatabase = null, bool selfRequirementBlocks = true)
        {
            return InheritsProtocolWithAssociatedTypesRecursive(protocolDecl, allProtocols, typeDatabase, new HashSet<string>(StringComparer.Ordinal), selfRequirementBlocks);
        }

        private static bool InheritsProtocolWithAssociatedTypesRecursive(ProtocolDecl protocolDecl, IReadOnlyList<ProtocolDecl>? allProtocols, ITypeDatabase? typeDatabase, HashSet<string> visited, bool selfRequirementBlocks)
        {
            var qualifiedName = protocolDecl.SwiftTypeName?.ToString() ?? protocolDecl.Name;
            if (!visited.Add(qualifiedName))
                return false;

            // InheritedProtocols may be empty due to ABI JSON conformance parsing (Kind mismatch).
            // Use GenericSignature as a fallback to extract parent protocol names.
            // GenericSignature format: "<Self : Module.ParentProtocol1, Self : Module.ParentProtocol2>"
            var parentNames = new List<string>();

            // Collect from InheritedProtocols (populated when conformance Kind matches)
            foreach (var inherited in protocolDecl.InheritedProtocols)
                parentNames.Add(inherited.Name);

            // Fallback (Finding 19): when InheritedProtocols is empty, derive parent protocol names
            // from the parsed signature's conformance targets. The legacy scan collected the target
            // after every " : " regardless of subject (Self / τ_0_0 / associated-type member); the
            // parsed equivalent is every conformance-requirement target.
            if (parentNames.Count == 0)
            {
                foreach (var r in protocolDecl.ParsedGenericSignature.Requirements)
                {
                    if (r.Kind == GenericRequirementKind.Conformance)
                        parentNames.Add(r.Target);
                }
            }

            foreach (var name in parentNames)
            {
                var dotIndex = name.LastIndexOf('.');
                var simpleName = dotIndex >= 0 ? name.Substring(dotIndex + 1) : name;

                // Hardcoded cross-module list: catches Foundation.PredicateExpression and
                // similar protocols that may not be registered in the type database but are
                // known to carry associated-type requirements EveryProtocol cannot satisfy.
                if (KnownCrossModuleProtocolsWithAssociatedTypes.Contains(name))
                    return true;

                // Intra-module check: look up the inherited protocol in the module
                if (allProtocols != null)
                {
                    var inheritedDecl = allProtocols.FirstOrDefault(p =>
                        p.Name == simpleName || p.Name == name ||
                        p.SwiftTypeName?.ToString() == name);
                    if (inheritedDecl != null)
                    {
                        if (inheritedDecl.AssociatedTypes.Count > 0 || (selfRequirementBlocks && inheritedDecl.HasSelfRequirement))
                            return true;
                        if (InheritsProtocolWithAssociatedTypesRecursive(inheritedDecl, allProtocols, typeDatabase, visited, selfRequirementBlocks))
                            return true;
                    }
                }

                // Cross-module check: look up in type database
                // Only for module-qualified names (contains a dot)
                if (typeDatabase != null && dotIndex >= 0)
                {
                    var inheritedSwiftName = SwiftTypeName.FromModuleQualifiedName(name);
                    if (typeDatabase.TryGetTypeRecord(inheritedSwiftName, out var record) &&
                        record.Kind == TypeRecordKind.Protocol &&
                        (record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes) ||
                         (selfRequirementBlocks && record.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement))))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Attributes a protocol dropped from EveryProtocol candidacy to the specific structural
        /// filter that removed it, mirroring the <c>suitableProtocols</c> .Where chain in order.
        /// Returns the matching <see cref="EveryProtocolSkipCause"/> token; the caller records it as
        /// the conformance skip reason so the persisted diagnostic classifies ExpectedStructural
        /// instead of "no decision recorded" (Review). Callers pre-exclude Self/associated-type,
        /// module-internal, and unsupported-module protocols (classified on other channels), so those
        /// arms are not repeated here. The two conflict passes run AFTER all .Where filters, so their
        /// checks come last — a protocol removed by an earlier filter never reached the conflict scan.
        /// </summary>
        private static string ClassifyDroppedCandidacy(
            ProtocolDecl protocolDecl,
            IReadOnlyList<ProtocolDecl> protocols,
            ITypeDatabase typeDatabase,
            string moduleName,
            HashSet<string> conflictingPropertyNames,
            HashSet<string> methodNamesCollidingWithProperties)
        {
            // Bug #14: a protocol re-exported from another module (mangled name / SwiftTypeName.Module
            // not this module's) is not locally defined and carries no synthesizable requirements here.
            bool localMangled = IsMangledNameFromModule(protocolDecl.MangledName, moduleName)
                || (string.IsNullOrEmpty(protocolDecl.MangledName)
                    && protocolDecl.SwiftTypeName != null
                    && string.Equals(protocolDecl.SwiftTypeName.Module, moduleName, StringComparison.Ordinal));
            if (!localMangled)
                return EveryProtocolSkipCause.DroppedForeignProtocol;

            if (EveryProtocolEmitter.IsClassBoundProtocol(protocolDecl, protocols)
                && !EveryProtocolEmitter.IsNSObjectProtocolOnly(protocolDecl, protocols))
                return EveryProtocolSkipCause.DroppedClassIdentity;

            if (EveryProtocolEmitter.HasClassSuperclassRequirement(protocolDecl, typeDatabase, protocols)
                && !EveryProtocolEmitter.IsEntityRootedProtocol(protocolDecl, typeDatabase, protocols))
                return EveryProtocolSkipCause.DroppedClassSuperclass;

            if (EveryProtocolEmitter.InheritsCaseIterable(protocolDecl, protocols)
                || InheritsProtocolWithAssociatedTypes(protocolDecl, protocols, typeDatabase)
                || EveryProtocolEmitter.InheritsUnsatisfiedStdlibProtocol(protocolDecl, protocols))
                return EveryProtocolSkipCause.DroppedInheritsUnsatisfiable;

            if (HasMembersReferencingInternalTypes(protocolDecl, typeDatabase, moduleName))
                return EveryProtocolSkipCause.DroppedInternalTypeReach;

            if (conflictingPropertyNames.Count > 0 && protocolDecl.Properties.Any(prop =>
                    !prop.IsStatic && !prop.IsObjCOptional && prop.IsProtocolRequirement
                    && conflictingPropertyNames.Contains(prop.Name)))
                return EveryProtocolSkipCause.DroppedPropertyTypeConflict;

            if (methodNamesCollidingWithProperties.Count > 0 && protocolDecl.Methods.Any(m =>
                    !m.IsConstructor && m.MethodType != MethodType.Static && !m.IsObjCOptional
                    && m.CSSignature.Count == 1
                    && methodNamesCollidingWithProperties.Contains(m.Name)))
                return EveryProtocolSkipCause.DroppedMemberKindConflict;

            return EveryProtocolSkipCause.DroppedCandidacyStructural;
        }

        /// <summary>
        /// Checks if a Swift mangled name belongs to the given module.
        /// Swift encodes module names in mangled symbols as $s{length}{moduleName}...
        /// Stdlib protocols use abbreviated forms ($sSl, $sSB, $ss...).
        /// Also accepts the umbrella prefix when the source module is exposed through Apple's
        /// `@_implementationOnly` re-export (e.g., RealityFoundation protocols carry
        /// $s10RealityKit... mangling because RealityKit is the umbrella declared in
        /// apple-frameworks.json's compileImportModule). Without the umbrella branch, those
        /// protocols would be silently filtered out of the source module's emission pass.
        /// </summary>
        internal static bool IsMangledNameFromModule(string mangledName, string moduleName)
        {
            if (string.IsNullOrEmpty(mangledName) || string.IsNullOrEmpty(moduleName))
                return false;

            // The expected mangled prefix is "$s" + length + moduleName
            var expectedPrefix = ManglingProbes.ModulePrefix(moduleName);
            if (mangledName.StartsWith(expectedPrefix, StringComparison.Ordinal))
                return true;

            var umbrella = AppleFrameworkRegistry.MapModuleToCompileImport(moduleName);
            if (!string.IsNullOrEmpty(umbrella) && !string.Equals(umbrella, moduleName, StringComparison.Ordinal))
            {
                var umbrellaPrefix = ManglingProbes.ModulePrefix(umbrella);
                if (mangledName.StartsWith(umbrellaPrefix, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Returns true if the protocol has any non-static member whose type references an
        /// unsupported module (SwiftUI, Combine) that is not registered in the type database.
        /// Used to skip EveryProtocol conformance and C# proxy emission for protocols whose
        /// requirements can't be satisfied.
        /// </summary>
        internal static bool HasMembersReferencingUnsupportedModule(ProtocolDecl protocolDecl, ITypeDatabase? typeDatabase = null)
        {
            foreach (var property in protocolDecl.Properties)
            {
                if (property.IsStatic) continue;
                if (MemberEmissionValidator.ReferencesUnsupportedModule(property.SwiftTypeSpec, typeDatabase))
                    return true;
            }
            foreach (var method in protocolDecl.Methods)
            {
                if (method.IsConstructor || method.MethodType == MethodType.Static) continue;
                foreach (var arg in method.CSSignature)
                {
                    if (MemberEmissionValidator.ReferencesUnsupportedModule(arg.SwiftTypeSpec, typeDatabase))
                        return true;
                }
            }
            foreach (var subscript in protocolDecl.Subscripts)
            {
                if (subscript.IsStatic) continue;
                if (MemberEmissionValidator.ReferencesUnsupportedModule(subscript.ReturnTypeSpec, typeDatabase))
                    return true;
                foreach (var param in subscript.IndexParameters)
                {
                    if (MemberEmissionValidator.ReferencesUnsupportedModule(param.SwiftTypeSpec, typeDatabase))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Returns true if the protocol has any non-static member whose type references a type
        /// from the current module that is not registered in the type database. Such types are
        /// likely module-internal and will cause compilation errors when EveryProtocol tries to
        /// conform to the protocol (the wrapper module cannot access internal types).
        /// </summary>
        internal static bool HasMembersReferencingInternalTypes(ProtocolDecl protocolDecl, ITypeDatabase typeDatabase, string moduleName)
        {
            foreach (var property in protocolDecl.Properties)
            {
                if (property.IsStatic) continue;
                if (ReferencesInternalModuleType(property.SwiftTypeSpec, typeDatabase, moduleName))
                    return true;
            }
            foreach (var method in protocolDecl.Methods)
            {
                if (method.IsConstructor || method.MethodType == MethodType.Static) continue;
                foreach (var arg in method.CSSignature)
                {
                    if (ReferencesInternalModuleType(arg.SwiftTypeSpec, typeDatabase, moduleName))
                        return true;
                }
            }
            foreach (var subscript in protocolDecl.Subscripts)
            {
                if (subscript.IsStatic) continue;
                if (ReferencesInternalModuleType(subscript.ReturnTypeSpec, typeDatabase, moduleName))
                    return true;
                foreach (var param in subscript.IndexParameters)
                {
                    if (ReferencesInternalModuleType(param.SwiftTypeSpec, typeDatabase, moduleName))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Recursively checks if a TypeSpec references a type from the specified module that
        /// is not registered in the type database (indicating it is module-internal).
        /// </summary>
        private static bool ReferencesInternalModuleType(TypeSpec? typeSpec, ITypeDatabase typeDatabase, string moduleName)
        {
            if (typeSpec == null)
                return false;

            switch (typeSpec)
            {
                case NamedTypeSpec namedType:
                    // Generic type parameters (τ_0_0, τ_1_0, T, etc.) are not concrete types —
                    // they don't need to be in the type database and are never internal.
                    if (TypeSpecHelpers.IsGenericTypeParameter(namedType.Name))
                        return false;
                    if (namedType.HasModule())
                    {
                        var typeModule = namedType.Module;
                        // Only check types from the current module — types from other modules
                        // are either imported or from the standard library.
                        if (typeModule == moduleName)
                        {
                            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
                            if (!typeDatabase.TryGetTypeRecord(swiftTypeName, out _))
                                return true; // From this module but not in DB → likely internal
                        }
                    }
                    else
                    {
                        // Unqualified type name — ABI JSON sometimes omits the module prefix
                        // for types in the current module. Try resolving with the module name.
                        var qualifiedName = $"{moduleName}.{namedType.Name}";
                        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(qualifiedName);
                        if (!typeDatabase.TryGetTypeRecord(swiftTypeName, out _))
                        {
                            // Not found with module prefix either — could be a stdlib type
                            // (e.g., "Int", "String") or genuinely internal. Only flag as
                            // internal if it's not a well-known Swift/stdlib type.
                            var stdlibName = SwiftTypeName.FromModuleQualifiedName($"Swift.{namedType.Name}");
                            if (!typeDatabase.TryGetTypeRecord(stdlibName, out _))
                                return true; // Not in module DB or stdlib → likely internal
                        }
                    }
                    foreach (var genericParam in namedType.GenericParameters)
                    {
                        if (ReferencesInternalModuleType(genericParam, typeDatabase, moduleName))
                            return true;
                    }
                    return false;

                case TupleTypeSpec tupleType:
                    foreach (var element in tupleType.Elements)
                    {
                        if (ReferencesInternalModuleType(element, typeDatabase, moduleName))
                            return true;
                    }
                    return false;

                case ClosureTypeSpec closureType:
                    if (ReferencesInternalModuleType(closureType.Arguments, typeDatabase, moduleName))
                        return true;
                    if (ReferencesInternalModuleType(closureType.ReturnType, typeDatabase, moduleName))
                        return true;
                    return false;

                case ProtocolListTypeSpec protocolList:
                    foreach (var protocol in protocolList.Protocols.Keys)
                    {
                        if (ReferencesInternalModuleType(protocol, typeDatabase, moduleName))
                            return true;
                    }
                    return false;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Emits a wrap-only proxy class for a composition interface.
        /// The proxy wraps a Swift existential container; member access throws NotSupportedException.
        /// </summary>
        /// <remarks>
        /// Only reached for PURE-PROTOCOL compositions, whose ABI is the opaque
        /// <c>ExistentialContainerN</c> layout (N witness-table words + 3-word inline value buffer +
        /// metadata word). Class-bound / ObjC compositions (<c>any SomeClass &amp; P</c>) are degraded to
        /// <c>object</c> upstream by <see cref="ExistentialHandler"/> and never produce a composition
        /// proxy, so the opaque container deref in <c>NewFromPayload</c> and the value-witness Destroy in
        /// the ownership path are always layout-correct here. A class-bound composition would need a
        /// distinct 2-word class-existential release shape (cf. the EC1 <c>useClassBoundContainerLayout</c>
        /// split) — out of scope until that path is supported.
        /// </remarks>
        private void EmitCompositionProxy(CSharpWriter csWriter, string compositionName, List<string> parentInterfaces,
            ModuleDecl moduleDecl, ITypeDatabase typeDatabase)
        {
            var proxyClassName = compositionName.Substring(1) + "Proxy"; // Strip leading "I", add "Proxy"
            var protocolCount = parentInterfaces.Count;
            var containerType = $"Swift.Runtime.ExistentialContainer{protocolCount}";

            csWriter.WriteLine();
            csWriter.WriteLine($"/// <summary>");
            csWriter.WriteLine($"/// Wrap-only proxy for the {compositionName} composition existential.");
            csWriter.WriteLine($"/// Wraps a Swift existential container; member access is not supported.");
            csWriter.WriteLine($"/// </summary>");
            csWriter.WriteLine("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
            csWriter.WriteLine($"public unsafe class {proxyClassName} : {compositionName}, ISwiftObject, IDisposable, Swift.Runtime.ISwiftExistentialConvertible<{containerType}>");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // Fields
            csWriter.WriteLine($"private readonly {containerType} _swiftContainer;");
            csWriter.WriteLine("private bool _disposed;");
            csWriter.WriteLine();

            // True only for a composition proxy that ADOPTED a Swift-returned `any A & B & …`
            // existential at +1 (the owned-return marshalling paths construct with
            // `ownsContainer: true`). Such a proxy owns the container's value-witness retains
            // and must release them on Dispose/finalize. False for every other construction —
            // borrowed parameter wraps and payload-pointer reads (NewFromPayload) do NOT own a
            // +1, so destroying their (borrowed) container would be a use-after-free.
            csWriter.WriteLine("private readonly bool _ownsContainer;");
            csWriter.WriteLine();

            // Constructor from container. `ownsContainer` mirrors the single-protocol (EC1) proxy:
            // an owned return adopts the +1 and releases it on Dispose/finalize. A composition
            // container holds exactly ONE conforming value regardless of protocol count, so its
            // value-witness Destroy (via the existential's own metadata) releases that one value.
            csWriter.WriteLine($"public {proxyClassName}({containerType} container, bool ownsContainer = false)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("_swiftContainer = container;");
            csWriter.WriteLine("_ownsContainer = ownsContainer;");
            csWriter.WriteLine("// Only an owning proxy has anything to release; suppress the finalizer for");
            csWriter.WriteLine("// borrowed/synthetic containers so they never run a value-witness Destroy on");
            csWriter.WriteLine("// a container they don't own.");
            csWriter.WriteLine("if (!ownsContainer)");
            csWriter.Indent++;
            csWriter.WriteLine("GC.SuppressFinalize(this);");
            csWriter.Indent--;
            csWriter.WriteLine("// Register with the ambient dispose scope (if any) so an owned return is");
            csWriter.WriteLine("// deterministically released at scope exit instead of waiting on the finalizer,");
            csWriter.WriteLine("// mirroring the single-protocol (EC1) proxy.");
            csWriter.WriteLine("Swift.Runtime.SwiftDisposeScope.TryRegister(this);");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            // ISwiftExistentialConvertible (explicit interface implementation to hide from public API)
            csWriter.WriteLine($"{containerType} ISwiftExistentialConvertible<{containerType}>.GetExistentialContainer()");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("if (_disposed) throw new ObjectDisposedException(GetType().Name);");
            csWriter.WriteLine("return _swiftContainer;");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            // ISwiftObject implementation
            csWriter.WriteLines($$"""
                IntPtr ISwiftObject.SwiftHandle => _swiftContainer.Payload0;

                public static TypeMetadata GetTypeMetadata()
                {
                    throw new NotSupportedException("Composition proxy has no single EveryProtocol metadata.");
                }

                public static ISwiftObject NewFromPayload(IntPtr payload)
                {
                    return new {{proxyClassName}}(*({{containerType}}*)payload);
                }

                // Composition proxy reads its container by value; the seam frees the wire temporary
                // and never touches SwiftHandle. Public (not explicit) and not module-init registered,
                // so the runtime reflection backstop finds it on Mono and NativeAOT.
                public static global::Swift.Runtime.PayloadConstructionSemantics PayloadConstructionSemantics => global::Swift.Runtime.PayloadConstructionSemantics.Inline;

                public int MarshalToSwift(ref Span<byte> swiftDestSpan)
                {
                    if (_disposed) throw new ObjectDisposedException(GetType().Name);
                    var size = _swiftContainer.SizeOf;
                    if (swiftDestSpan.Length < size)
                        throw new ArgumentException("Destination span too small", nameof(swiftDestSpan));
                    fixed ({{containerType}}* containerPtr = &_swiftContainer)
                    {
                        new Span<byte>(containerPtr, size).CopyTo(swiftDestSpan);
                    }
                    return size;
                }

                public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
                {
                    throw new NotSupportedException("Composition proxy does not support protocol conformance descriptors.");
                }

                public void Dispose()
                {
                    if (_disposed) return;
                    _disposed = true;
                    GC.SuppressFinalize(this);
                    ReleaseAdoptedSwiftContainer();
                }

                /// <summary>
                /// Finalizer — releases an adopted (<c>_ownsContainer</c>) composition existential
                /// container if the consumer never called <see cref="Dispose"/>. Non-owning proxies
                /// suppress finalization in their constructor, so this only runs for owners.
                /// </summary>
                ~{{proxyClassName}}()
                {
                    if (_disposed) return;
                    _disposed = true;
                    ReleaseAdoptedSwiftContainer();
                }

                // Releases the value-witness retains of an ADOPTED Swift-returned composition
                // existential container. Gated to proxies that actually own a +1 (_ownsContainer,
                // set only by the owned-return marshalling paths). Borrowed parameter wraps and
                // payload-pointer reads (NewFromPayload) do NOT own a +1 — destroying their
                // (borrowed) container would be a use-after-free. A composition container holds
                // ONE conforming value (3-word inline buffer or heap box), so destroying through
                // the existential's own value-witness table — resolved by protocol count, which
                // for EC2+ skips the leading witness-table words to the payload — releases that
                // one value (inline class reference or boxed value payload alike).
                private void ReleaseAdoptedSwiftContainer()
                {
                    if (!_ownsContainer)
                        return;
                    try
                    {
                        fixed ({{containerType}}* containerPtr = &_swiftContainer)
                        {
                            var existentialMetadata = Swift.Runtime.TypeMetadata.GetExistentialTypeMetadata(_swiftContainer.Count);
                            // This body runs from BOTH Dispose (user thread) and the GC finalizer
                            // (~Proxy). A direct VWT Destroy (CallConvSwift) from the finalizer
                            // thread crashes Mono with the !ji->async assertion after CallConvSwift
                            // JIT contamination — the same hazard the single-protocol (EC1) proxy
                            // dodges (ProtocolProxyEmitter.SwiftObject.cs) and the class-bound proxy
                            // dodges via Arc.UnknownObjectReleaseFinalizerSafe. Route through the
                            // SBW_VWTDestroy @_cdecl trampoline, which is safe from either thread.
                            Swift.Runtime.InteropServices.SwiftMarshal.DestroyWireBufferRetainsFinalizerSafe((IntPtr)containerPtr, existentialMetadata);
                        }
                    }
                    catch
                    {
                        // Existential metadata unavailable (e.g. SwiftBindingsRuntime not loaded
                        // under unit tests) — skip the destroy rather than throw from Dispose/finalize.
                    }
                }
                """);
            csWriter.WriteLine();

            // Emit stub implementations for all interface members
            EmitCompositionMemberStubs(csWriter, parentInterfaces, moduleDecl, typeDatabase);

            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        /// <summary>
        /// Emits NotSupportedException stub implementations for all inherited interface members.
        /// </summary>
        private void EmitCompositionMemberStubs(CSharpWriter csWriter, List<string> parentInterfaces,
            ModuleDecl moduleDecl, ITypeDatabase typeDatabase)
        {
            var emittedProperties = new HashSet<string>();
            var emittedMethods = new HashSet<string>();

            foreach (var interfaceName in parentInterfaces)
            {
                // Resolve interface name (e.g., "ICryptor") → protocol name (e.g., "Cryptor")
                var protocolName = interfaceName.StartsWith("I") ? interfaceName.Substring(1) : interfaceName;
                var protocolDecl = moduleDecl.Protocols?.FirstOrDefault(p => p.Name == protocolName);
                if (protocolDecl == null)
                    continue;

                // Properties
                foreach (var property in protocolDecl.Properties)
                {
                    if (property.IsStatic)
                        continue;
                    if (!emittedProperties.Add(property.Name))
                        continue;

                    var csharpType = ResolvePropertyType(property, typeDatabase, moduleDecl.Name);
                    var propertyName = NameProvider.GetPropertyName(property);
                    var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
                    var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();

                    csWriter.WriteLine($"public {csharpType} {propertyName}");
                    csWriter.WriteLine("{");
                    csWriter.Indent++;
                    if (hasGetter)
                        csWriter.WriteLine($"get => throw new NotSupportedException(\"Cannot access member on Swift-backed composition existential.\");");
                    if (hasSetter)
                        csWriter.WriteLine($"set => throw new NotSupportedException(\"Cannot access member on Swift-backed composition existential.\");");
                    csWriter.Indent--;
                    csWriter.WriteLine("}");
                    csWriter.WriteLine();
                }

                // Methods
                foreach (var method in protocolDecl.Methods)
                {
                    if (method.IsConstructor || method.MethodType == MethodType.Static)
                        continue;

                    // Effective* keeps these NotSupported stubs in lockstep with the disambiguated
                    // interface: a label-only-overload pair emits two distinct stubs under their
                    // label-derived names instead of collapsing onto one (which would leave the
                    // sibling interface member unimplemented → CS0535).
                    var methodKey = ProtocolMethodDisambiguator.EffectiveRawKey(method, protocolDecl, typeDatabase);
                    if (!emittedMethods.Add(methodKey))
                        continue;

                    var returnType = ResolveMethodReturnType(method, typeDatabase, moduleDecl.Name);
                    var parameters = ResolveMethodParameters(method, typeDatabase, moduleDecl.Name);
                    bool hasReturnValue = method.CSSignature.Count > 0 && !method.CSSignature.First().SwiftTypeSpec.IsEmptyTuple;
                    var methodName = NameProvider.GetPublicMethodName(ProtocolMethodDisambiguator.EffectiveNameInput(method, protocolDecl, typeDatabase), method.IsAsync, hasReturnValue,
                        parameterCount: method.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple),
                        isMutating: method.IsMutating);

                    if (method.IsAsync)
                    {
                        returnType = returnType == "void" ? "Task" : $"Task<{returnType}>";
                    }

                    csWriter.WriteLine($"public {returnType} {methodName}({string.Join(", ", parameters)})");
                    csWriter.Indent++;
                    csWriter.WriteLine($"=> throw new NotSupportedException(\"Cannot call method on Swift-backed composition existential.\");");
                    csWriter.Indent--;
                    csWriter.WriteLine();
                }
            }
        }

        /// <summary>
        /// Resolves a property's C# type using the same chain as ProtocolHandler.EmitInterfaceProperty.
        /// </summary>
        private static string ResolvePropertyType(PropertyDecl property, ITypeDatabase typeDatabase, string? currentModuleName)
        {
            return ResolveCSharpTypeName(property.SwiftTypeSpec, typeDatabase, currentModuleName, isParameter: false);
        }

        /// <summary>
        /// Resolves a method's return type using the same chain as ProtocolHandler.EmitInterfaceMethod.
        /// </summary>
        private static string ResolveMethodReturnType(MethodDecl method, ITypeDatabase typeDatabase, string? currentModuleName)
        {
            if (method.CSSignature.Count == 0) return "void";
            var returnArg = method.CSSignature[0];
            if (returnArg.SwiftTypeSpec is TupleTypeSpec tuple && tuple.IsEmptyTuple) return "void";
            return ResolveCSharpTypeName(returnArg.SwiftTypeSpec, typeDatabase, currentModuleName, isParameter: false);
        }

        /// <summary>
        /// Resolves method parameters to C# parameter declarations.
        /// </summary>
        private static List<string> ResolveMethodParameters(MethodDecl method, ITypeDatabase typeDatabase, string? currentModuleName)
        {
            var parameters = new List<string>();
            for (int i = 1; i < method.CSSignature.Count; i++)
            {
                var arg = method.CSSignature[i];
                // Skip debug params and empty tuple () params (zero-sized Void)
                if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                    continue;
                if (arg.SwiftTypeSpec.IsEmptyTuple)
                    continue;
                var paramTypeName = ResolveCSharpTypeName(arg.SwiftTypeSpec, typeDatabase, currentModuleName);
                var paramName = NameProvider.GetCSharpParameterName(arg);
                parameters.Add($"{paramTypeName} {paramName}");
            }
            return parameters;
        }

        /// <summary>
        /// Resolves a TypeSpec to its C# type name, handling closures, tuples, existentials, bound generics,
        /// and standard types. Mirrors ProtocolHandler.GetCSharpTypeName() for composition proxy stubs.
        /// </summary>
        private static string ResolveCSharpTypeName(TypeSpec typeSpec, ITypeDatabase typeDatabase, string? currentModuleName, bool isParameter = true)
        {
            // Factory-first with GenericContext: handles all types including bound generics.
            // The module being emitted has to travel with the projection: an existential owned by
            // a sibling module must name that module here, because the generated file emits no
            // using for the sibling's namespace and a bare interface name resolves to nothing.
            var factory = new TypeProjectionFactory();
            var projection = factory.Project(typeSpec, new ProjectionContext
            {
                TypeDatabase = typeDatabase,
                IsParameter = isParameter,
                GenericContext = GenericContext.Empty,
                CurrentModuleName = currentModuleName
            });
            if (projection != null)
                return projection.PublicType;

            // Bound generic fallback: produce raw ABI type name with generic args
            if (typeSpec is NamedTypeSpec boundGeneric && boundGeneric.ContainsGenericParameters)
            {
                var bgh = new BoundGenericsHandler(typeDatabase, conformanceGraph: null,
                    currentModuleName: currentModuleName);
                return bgh.TranslateBoundGenericTypeToCSharp(typeSpec, GenericContext.Empty);
            }

            // Standard type lookup
            return typeDatabase.GetTypeRecordOrAnyType(typeSpec).CSharpTypeName.FullyQualifiedName;
        }

    }
}
