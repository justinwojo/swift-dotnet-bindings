// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Swift.Runtime;

[assembly: InternalsVisibleTo("Swift.Bindings.Unit.Tests")]

namespace BindingsGeneration
{
    /// <summary>
    /// Factory class for creating instances of ProtocolHandler.
    /// </summary>
    public class ProtocolHandlerFactory : HandlerFactory, IFactory<BaseDecl, ITypeHandler>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ProtocolHandlerFactory"/> class.
        /// </summary>
        /// <param name="loggerFactory">The logger factory instance.</param>
        public ProtocolHandlerFactory(ILoggerFactory loggerFactory) : base(loggerFactory.CreateLogger<ProtocolHandler>())
        {
        }

        /// <summary>
        /// Determines if the factory handles the specified declaration.
        /// </summary>
        /// <param name="decl">The base declaration.</param>
        public bool Handles(BaseDecl decl)
        {
            return decl is ProtocolDecl;
        }

        /// <summary>
        /// Constructs a new instance of ProtocolHandler.
        /// </summary>
        public ITypeHandler Construct()
        {
            return new ProtocolHandler(_handlerLogger);
        }
    }

    /// <summary>
    /// Handler class for protocol declarations.
    /// </summary>
    public class ProtocolHandler : BaseHandler, ITypeHandler
    {
        private const string ExtensionDefaultPropertyMessage = "This property uses a Swift protocol extension default. Access it on the concrete type instead.";
        private const string ExtensionDefaultMethodMessage = "This method uses a Swift protocol extension default. Call it on the concrete type instead.";

        // ObjC `@objc optional` lowering: emit a DIM whose body silently no-ops (returns
        // `default` for value-bearing members, an empty block for `void`). Consumers of
        // the C# interface only have to implement the optional member when they care —
        // matching the Swift / ObjC contract where the framework no-ops or substitutes
        // a default when the conformer hasn't supplied one.

        private SortedDictionary<string, List<string>>? _compositionCollector;

        /// <summary>
        /// Initializes a new instance of the <see cref="ProtocolHandler"/> class.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        public ProtocolHandler(ILogger logger) : base(logger)
        {
        }

        /// <inheritdoc/>
        public IEnvironment Marshal(BaseDecl baseDecl, ITypeDatabase typeDatabase)
        {
            if (baseDecl is not ProtocolDecl protocolDecl)
            {
                throw new ArgumentException("The provided decl must be a ProtocolDecl.", nameof(baseDecl));
            }
            return new TypeEnvironment(protocolDecl, typeDatabase);
        }

        /// <inheritdoc/>
        public void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor, TypeHandlerContext context)
        {
            _compositionCollector = context.CompositionCollector;
            var protocolEnv = (TypeEnvironment)env;
            var protocolDecl = (ProtocolDecl)protocolEnv.TypeDecl;

            ReportCollector.RecordTypeEmitted(protocolDecl);

            var interfaceName = GetInterfaceNameWithGenerics(protocolDecl);
            var emissionCtx = context.GetEmissionContext();
            var inheritedInterfaces = GetInheritedInterfaceList(protocolDecl, env.TypeDatabase, emissionCtx);

            // Count total declared members that are candidates for interface emission.
            // Includes static properties and static methods (emitted as static abstract).
            // Excludes: constructors (need factory synthesis), static subscripts (no C# mapping), operators.
            int totalDeclaredMembers =
                protocolDecl.Properties.Count +
                protocolDecl.Subscripts.Count(s => !s.IsStatic) +
                protocolDecl.Methods.Count(m => !m.IsConstructor);

            // Buffer the interface body so we can decide whether to emit SB0004
            // (empty interface with skipped members) before the declaration.
            var bodyStringWriter = new System.IO.StringWriter();
            var bodyWriter = new CSharpWriter(bodyStringWriter);
            bodyWriter.Indent = csWriter.Indent + 1;

            // Track emitted members to avoid duplicates
            int emittedInterfaceMemberCount = 0;
            var emittedProperties = new HashSet<string>();
            var emittedMethods = new HashSet<string>();
            var emittedCSharpKeys = new HashSet<string>();
            var emittedResolvedSignatures = new HashSet<string>(StringComparer.Ordinal);
            var emittedSubscripts = new HashSet<string>();
            var closureHandler = new ClosureHandler(env.TypeDatabase);

            // Pre-compute extension default lookup values (loop-invariant)
            var extensionDefaultsIndex = emissionCtx?.ExtensionDefaultsIndex;
            var protoQualifiedName = protocolDecl.SwiftTypeName?.ModuleQualifiedName
                                   ?? $"{protocolDecl.ModuleDecl?.Name ?? "Unknown"}.{protocolDecl.Name}";

            // Emit properties as interface members
            var skippedPropertyNames = new HashSet<string>();
            var closureSkippedPropertyNames = new HashSet<string>(); // Closure properties: in interface, proxy needs stub
            var optionalDimPropertyNames = new HashSet<string>(); // @objc optional properties: in interface as DIM, proxy skips entirely
            var staticAbstractPropertyNames = new HashSet<string>(); // Static properties emitted as static abstract
            foreach (var propertyDecl in protocolDecl.Properties)
            {
                // Attribute everything this property iteration writes to the PropertyDecl.
                // Interface body is buffered on bodyWriter only (no Swift surface here).
                // `using` declarations so every `continue` path closes the scope without re-indent.
                var propOwner = FragmentOwners.ForDecl(propertyDecl);
                using var propCsScope = bodyWriter.BeginFragment(propOwner);
                // Static properties: evaluate gates, emit as static abstract if passes
                if (propertyDecl.IsStatic)
                {
                    var staticPropertyKey = propertyDecl.Name;
                    if (emittedProperties.Contains(staticPropertyKey))
                    {
                        ReportCollector.RecordMemberSkipped(propertyDecl, SkipReason.DuplicateSignature, "Duplicate protocol property signature.");
                        continue;
                    }
                    emittedProperties.Add(staticPropertyKey);
                    // Denied AFTER the reservation, not before it. On a protocol the reservation and
                    // the reverse-dispatch skip sets are keyed the same way, and every downstream
                    // consumer keys off the name rather than the declaration: releasing the name here
                    // would let a same-named sibling become the emitted requirement while the
                    // name-wide skip still suppresses its proxy side. Holding the name keeps a
                    // denial indistinguishable from the gate skip just below it.
                    if (EmissionSeam.TryDenyUpFront(propertyDecl))
                        continue;

                    var staticGateEvaluator = new MemberGateEvaluator(env.TypeDatabase);
                    var staticPropertyGate = staticGateEvaluator.EvaluateProperty(propertyDecl, protocolDecl.ModuleDecl, protocolDecl);
                    if (staticPropertyGate.IsSkipped)
                    {
                        _logger.LogDebug($"Skipping static property '{propertyDecl.Name}' in interface {protocolDecl.Name} - {staticPropertyGate.Details}");
                        ReportCollector.RecordMemberSkipped(propertyDecl, staticPropertyGate.Reason!.Value, staticPropertyGate.DetailsForReport!);
                        continue;
                    }

                    // Emit as static abstract (no DIM — static abstract members can't have default implementations)
                    // Contain the static abstract property surface. Escalates to the protocol when
                    // denying this leaf still faults on shared interface emission.
                    // A denial returns from the seam normally, so the "it emitted" bookkeeping below
                    // has to be gated: recording the name, counting the member and filing an emitted
                    // row for a property that wrote nothing would contradict the skip row the seam
                    // just recorded for it.
                    if (EmissionSeam.Guard(
                        propertyDecl,
                        RecoveryScope.LeafApi,
                        protocolDecl,
                        () => EmitInterfaceProperty(bodyWriter, propertyDecl, env.TypeDatabase, closureHandler, protocolDecl, isExtensionDefault: false, isStaticAbstract: true, emissionCtx: emissionCtx)))
                    {
                        staticAbstractPropertyNames.Add(propertyDecl.Name);
                        emittedInterfaceMemberCount++;
                        ReportCollector.RecordMemberEmitted(propertyDecl);
                    }
                    continue;
                }

                // Create a unique key for the property (name is sufficient since properties can't be overloaded)
                var propertyKey = propertyDecl.Name;
                if (emittedProperties.Contains(propertyKey))
                {
                    _logger.LogDebug($"Skipping duplicate property '{propertyDecl.Name}' in interface {protocolDecl.Name}");
                    ReportCollector.RecordMemberSkipped(propertyDecl, SkipReason.DuplicateSignature, "Duplicate protocol property signature.");
                    continue;
                }
                emittedProperties.Add(propertyKey);
                // Same door the gate skip below takes: hold the reserved name and record the
                // requirement as skipped. The proxy and the static-init vtable fill both decide
                // what to implement from `skippedPropertyNames`, so a denial that leaves the set
                // untouched drops the property from the interface while the proxy still emits
                // `impl.Property` against it.
                if (EmissionSeam.TryDenyUpFront(propertyDecl))
                {
                    skippedPropertyNames.Add(propertyDecl.Name);
                    continue;
                }

                // Evaluate property gates via centralized evaluator (P3-P7)
                var gateEvaluator = new MemberGateEvaluator(env.TypeDatabase);
                var propertyGate = gateEvaluator.EvaluateProperty(propertyDecl, protocolDecl.ModuleDecl, protocolDecl);
                if (propertyGate.IsSkipped)
                {
                    skippedPropertyNames.Add(propertyDecl.Name);
                    _logger.LogDebug($"Skipping property '{propertyDecl.Name}' in interface {protocolDecl.Name} - {propertyGate.Details}");
                    ReportCollector.RecordMemberSkipped(propertyDecl, propertyGate.Reason!.Value, propertyGate.DetailsForReport!);
                    continue;
                }
                if (propertyGate.IsInterfaceOnly)
                {
                    bool hasClosure = propertyGate.SoftFlags.HasFlag(SoftGateFlags.HasClosureProperty);
                    bool isDispatchableClosure = hasClosure
                        && EveryProtocolEmitter.IsDispatchableClosureProperty(propertyDecl, closureHandler);
                    if (!isDispatchableClosure)
                    {
                        // Closure property (non-dispatchable shape) or other interface-only soft skip:
                        // emit in interface, track for proxy NotSupportedException stub.
                        skippedPropertyNames.Add(propertyDecl.Name);
                        if (hasClosure)
                            closureSkippedPropertyNames.Add(propertyDecl.Name);
                        _logger.LogDebug($"Property '{propertyDecl.Name}' in interface {protocolDecl.Name} has closure type - proxy will use NotSupportedException stub.");
                        // The requirement still emits on the interface; only the proxy's
                        // implementation degrades to a throwing SB0003 stub.
                        ReportCollector.RecordMemberDegraded(
                            propertyDecl, protocolDecl, SkipReason.ProtocolWitnessNotDispatchable,
                            hasClosure
                                ? "closure-typed property cannot be marshalled through a witness table"
                                : $"interface-only property shape is not dispatchable via witness table ({propertyGate.SoftFlags})");
                    }
                    else
                    {
                        // Dispatchable closure property: falls through to the
                        // real interface emission AND real proxy emission. Both directions
                        // (setter via vtable, getter via cdecl thunk + _SBClosureCtx box) are
                        // covered by EveryProtocolEmitter.EmitDispatchableClosurePropertyImplementation
                        // and ProtocolProxyEmitter receiver / static-init pair.
                        _logger.LogDebug($"Property '{propertyDecl.Name}' in interface {protocolDecl.Name} is a dispatchable closure property — emitting real proxy dispatch.");
                    }
                    // Fall through to emit in interface — concrete types can implement it
                }

                // Check if this property has an extension default (direct or from sub-protocol).
                // When interface inheritance is enabled, sub-protocol defaults must also produce DIMs
                // on parent interfaces — otherwise conforming types get CS0535 for inherited requirements
                // that are satisfied by a sub-protocol default in Swift.
                // Setter-aware: a getter-only default does NOT DIM-relax a { get set } requirement
                bool isPropertyExtDefault = false;
                if (extensionDefaultsIndex != null)
                {
                    var protoHasSetter = propertyDecl.Accessors.OfType<SetAccessorDecl>().Any();
                    isPropertyExtDefault = extensionDefaultsIndex.HasPropertyDefault(
                        protoQualifiedName, propertyDecl.Name, requiresSetter: protoHasSetter);
                }

                // ObjC `@objc optional` properties: emit as DIM with a no-op default body
                // so consumers don't have to implement them. The DIM lives on the interface,
                // so the proxy must skip emitting an implementation (let the DIM run); track
                // both in `skippedPropertyNames` (proxy skip) and `optionalDimPropertyNames`
                // (so the canonical property-name set re-includes the DIM, mirroring how
                // closure-skipped properties are added back below).
                if (propertyDecl.IsObjCOptional)
                {
                    skippedPropertyNames.Add(propertyDecl.Name);
                    optionalDimPropertyNames.Add(propertyDecl.Name);
                    // Contain @objc optional property DIM emission. Escalates to the protocol
                    // type if the leaf denial does not clear the fault.
                    // The pre-check above already diverts a poisoned requirement, so this branch is
                    // the backstop for a denial that arrives at the seam itself. It returns normally
                    // rather than unwinding, so the bookkeeping has to be gated: counting the member
                    // and filing an emitted row for a property that wrote nothing would contradict
                    // the skip row the seam just recorded, and the skip set has to pick it up or the
                    // proxy implements a requirement absent from the interface.
                    if (EmissionSeam.Guard(
                        propertyDecl,
                        RecoveryScope.LeafApi,
                        protocolDecl,
                        () => EmitInterfaceProperty(bodyWriter, propertyDecl, env.TypeDatabase, closureHandler, protocolDecl, isExtensionDefault: false, isStaticAbstract: false, isObjCOptional: true, emissionCtx: emissionCtx)))
                    {
                        emittedInterfaceMemberCount++;
                        ReportCollector.RecordMemberEmitted(propertyDecl);
                    }
                    else
                    {
                        skippedPropertyNames.Add(propertyDecl.Name);
                    }
                    continue;
                }

                // Contain the ordinary protocol property member. Escalates to the enclosing
                // protocol when the fault is not isolated to this property.
                // Seam-denial backstop, gated for the same reason as the ObjC-optional branch above.
                if (EmissionSeam.Guard(
                    propertyDecl,
                    RecoveryScope.LeafApi,
                    protocolDecl,
                    () => EmitInterfaceProperty(bodyWriter, propertyDecl, env.TypeDatabase, closureHandler, protocolDecl, isPropertyExtDefault, emissionCtx: emissionCtx)))
                {
                    emittedInterfaceMemberCount++;
                    ReportCollector.RecordMemberEmitted(propertyDecl);
                }
                else
                {
                    skippedPropertyNames.Add(propertyDecl.Name);
                }
            }

            // Collect actually-emitted C# property names for method/property collision detection.
            // Only include properties that passed all gates and were emitted in the interface
            // (not properties that were seen for dedup but gate-skipped).
            var emittedCSharpPropertyNames = new HashSet<string>();
            foreach (var propKey in emittedProperties)
            {
                if (!skippedPropertyNames.Contains(propKey))
                    emittedCSharpPropertyNames.Add(NameProvider.GetPropertyName(propKey));
            }
            // Also include closure properties that ARE emitted in the interface
            // (they're in skippedPropertyNames for proxy tracking, but they're still
            // interface members that can collide with method names).
            foreach (var closurePropName in closureSkippedPropertyNames)
                emittedCSharpPropertyNames.Add(NameProvider.GetPropertyName(closurePropName));
            // Same for `@objc optional` properties: the DIM is in the interface, so its
            // C# name participates in method/property collision detection.
            foreach (var optionalPropName in optionalDimPropertyNames)
                emittedCSharpPropertyNames.Add(NameProvider.GetPropertyName(optionalPropName));

            // Publish the actually-emitted property-name set so downstream emitters that
            // need to compute this protocol's exact C# member names (proxy explicit-interface
            // forwarders, BFS shadow detection) can read it instead of approximating from
            // protocolDecl.Properties (which over-includes gate-skipped properties).
            emissionCtx?.RecordInterfacePropertyNames(protoQualifiedName, emittedCSharpPropertyNames);

            // Emit subscripts as interface indexers
            var skippedSubscriptIndices = new HashSet<int>();
            int subscriptIndex = 0;
            foreach (var subscriptDecl in protocolDecl.Subscripts)
            {
                // Attribute everything this subscript iteration writes to the SubscriptDecl.
                // Interface body is buffered on bodyWriter only (no Swift surface here).
                // `using` declarations so every `continue` path closes the scope without re-indent.
                var subOwner = FragmentOwners.ForDecl(subscriptDecl);
                using var subCsScope = bodyWriter.BeginFragment(subOwner);
                // Skip static subscripts - C# interfaces cannot have static members as requirements
                // Note: Static subscripts are still emitted on conforming types, just not in the interface
                if (subscriptDecl.IsStatic)
                {
                    _logger.LogDebug($"Skipping static subscript in interface {protocolDecl.Name} - static interface members are not supported.");
                    ReportCollector.RecordMemberSkipped(subscriptDecl, SkipReason.StaticProtocolMember, "Static protocol members cannot be declared in C# interfaces.");
                    continue;
                }

                // Create a unique key for the subscript based on index parameter types
                var subscriptKey = ProtocolSignatureHelper.GetSubscriptSignatureKey(subscriptDecl, env.TypeDatabase, protocolDecl);
                if (emittedSubscripts.Contains(subscriptKey))
                {
                    _logger.LogDebug($"Skipping duplicate subscript in interface {protocolDecl.Name}");
                    ReportCollector.RecordMemberSkipped(subscriptDecl, SkipReason.DuplicateSignature, "Duplicate protocol subscript signature.");
                    subscriptIndex++;
                    continue;
                }
                emittedSubscripts.Add(subscriptKey);
                // Leaving through the same door the gate skip below uses, and from the same place:
                // after the key is reserved, so a same-key sibling still resolves as a duplicate
                // rather than being promoted into the requirement a denied declaration vacated.
                // The index is consumed and recorded as skipped so the reverse-dispatch slot stays
                // allocated-but-unfilled — shrinking the vtable would shift every later slot.
                if (EmissionSeam.TryDenyUpFront(subscriptDecl))
                {
                    skippedSubscriptIndices.Add(subscriptIndex);
                    subscriptIndex++;
                    continue;
                }

                // Evaluate subscript gates via centralized evaluator (S3-S5)
                var subscriptGateEvaluator = new MemberGateEvaluator(env.TypeDatabase);
                var subscriptGate = subscriptGateEvaluator.EvaluateSubscript(subscriptDecl, protocolDecl.ModuleDecl, protocolDecl);
                if (subscriptGate.IsSkipped)
                {
                    skippedSubscriptIndices.Add(subscriptIndex);
                    _logger.LogDebug($"Skipping subscript in interface {protocolDecl.Name} - {subscriptGate.Details}");
                    ReportCollector.RecordMemberSkipped(subscriptDecl, subscriptGate.Reason!.Value, subscriptGate.DetailsForReport!);
                    subscriptIndex++;
                    continue;
                }

                // Contain one protocol subscript/indexer requirement. Escalates to the protocol
                // so a sticky subscript fault can withdraw the interface type.
                // Seam-denial backstop. The index is consumed either way — the slot exists in the
                // Swift vtable regardless of whether this requirement filled it.
                if (EmissionSeam.Guard(
                    subscriptDecl,
                    RecoveryScope.LeafApi,
                    protocolDecl,
                    () => EmitInterfaceSubscript(bodyWriter, subscriptDecl, env.TypeDatabase, closureHandler, protocolDecl, emissionCtx: emissionCtx)))
                {
                    emittedInterfaceMemberCount++;
                    ReportCollector.RecordMemberEmitted(subscriptDecl);
                }
                else
                {
                    skippedSubscriptIndices.Add(subscriptIndex);
                }
                subscriptIndex++;
            }

            // Emit methods as interface members
            var skippedMethodKeys = new HashSet<string>();
            var closureSkippedMethodKeys = new HashSet<string>(); // Closure methods: in interface, proxy needs stub
            var staticAbstractMethodKeys = new HashSet<string>(); // Static methods emitted as static abstract
            foreach (var methodDecl in protocolDecl.Methods)
            {
                // Attribute everything this method iteration writes to the MethodDecl.
                // Interface body is buffered on bodyWriter only (no Swift surface here).
                // `using` declarations so every `continue` path closes the scope without re-indent.
                var methodOwner = FragmentOwners.ForDecl(methodDecl);
                using var methodCsScope = bodyWriter.BeginFragment(methodOwner);
                // Constructors: still skipped (would need factory method synthesis on conforming types)
                if (methodDecl.IsConstructor)
                {
                    ReportCollector.RecordMemberSkipped(methodDecl, SkipReason.StaticProtocolMember, "Protocol constructor requirements cannot be declared in C# interfaces.");
                    continue;
                }

                // Static methods: evaluate gates, emit as static abstract if passes.
                //
                // NOTE: unlike instance methods (below), static requirements are NOT run through
                // ProtocolMethodDisambiguator — they intentionally keep the label-blind
                // GetMethodSignatureKey, so two static requirements differing only by argument label
                // collapse to one member (the second is dropped as DuplicateSignature). This is a
                // deliberate scope boundary, not an oversight:
                //   (1) Static requirements have NO reverse-dispatch path — they get no vtable slot,
                //       no witness, and any protocol with even one static method requirement has its
                //       ENTIRE EveryProtocol conformance skipped; the surviving interface member and
                //       its proxy stub both throw NotSupportedException. So the label-only static that
                //       collapses is dropped outright (skipped as DuplicateSignature — no member emitted
                //       at all), and the requirement it collapses onto is itself a non-dispatchable
                //       throwing stub. Either way there is no working dispatch to lose — no silent
                //       MIS-dispatch of a live call, only a metadata-fidelity gap.
                //   (2) A selector-style rename on the INTERFACE alone would REGRESS conformance: a
                //       concrete conforming type disambiguates its own label-only statics via the
                //       class path's NUMERIC suffix (Configure/Configure2), so the conformance
                //       validator's static name-parity gate would see interface names that no longer
                //       match the concrete member names and drop an otherwise-valid conformance. A
                //       faithful fix must reconcile interface and concrete static naming under one
                //       policy (naming-policy work), not a disambiguator one-liner. Revisit only when
                //       a real library needs both label-only static requirements AND working static
                //       dispatch.
                if (methodDecl.MethodType == MethodType.Static)
                {
                    var staticMethodKey = ProtocolSignatureHelper.GetMethodSignatureKey(methodDecl, env.TypeDatabase, protocolDecl);
                    if (emittedMethods.Contains(staticMethodKey))
                    {
                        ReportCollector.RecordMemberSkipped(methodDecl, SkipReason.DuplicateSignature, "Duplicate protocol method signature.");
                        continue;
                    }
                    emittedMethods.Add(staticMethodKey);

                    var staticProjectedKey = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(methodDecl, env.TypeDatabase, protocolDecl);
                    if (!emittedCSharpKeys.Add(staticProjectedKey))
                    {
                        ReportCollector.RecordMemberSkipped(methodDecl, SkipReason.DuplicateSignature, "Projected C# method signature collides with already-emitted method.");
                        continue;
                    }

                    // After both reservations, in the slot the gate check below occupies: a denied
                    // requirement keeps its keys so a sibling sharing either one still resolves as a
                    // duplicate. Statics own no reverse-dispatch slot, so there is nothing to record.
                    if (EmissionSeam.TryDenyUpFront(methodDecl))
                        continue;

                    var staticMethodGateEvaluator = new MemberGateEvaluator(env.TypeDatabase);
                    var staticMethodGate = staticMethodGateEvaluator.EvaluateMethod(methodDecl, protocolDecl.ModuleDecl, protocolDecl);
                    if (staticMethodGate.IsSkipped)
                    {
                        _logger.LogDebug($"Skipping static method '{methodDecl.Name}' in interface {protocolDecl.Name} - {staticMethodGate.Details}");
                        ReportCollector.RecordMemberSkipped(methodDecl, staticMethodGate.Reason!.Value, staticMethodGate.DetailsForReport!);
                        continue;
                    }

                    // Emit as static abstract (no DIM, no nint overload — static abstract members can't have default implementations)
                    // Contain static abstract method interface emission. Escalates to the
                    // protocol when leaf denial is insufficient.
                    // Seam-denial backstop. `staticAbstractMethodKeys` drives the conformance
                    // validator's static name-parity gate, so claiming membership for a requirement
                    // that wrote nothing would fail an otherwise-valid conformance.
                    if (EmissionSeam.Guard(
                        methodDecl,
                        RecoveryScope.LeafApi,
                        protocolDecl,
                        () => EmitInterfaceMethod(bodyWriter, methodDecl, env.TypeDatabase, closureHandler, protocolDecl, emittedCSharpPropertyNames, isExtensionDefault: false, isStaticAbstract: true, emissionCtx: emissionCtx)))
                    {
                        staticAbstractMethodKeys.Add(staticMethodKey);
                        emittedInterfaceMemberCount++;
                        ReportCollector.RecordMemberEmitted(methodDecl);
                    }
                    continue;
                }

                // Create a unique key for the method (name + parameter types).
                // EffectiveRawKey: for a label-only-overload sibling (e.g. delegate callbacks
                // conversationManager(_:didActivate:) / (_:didDeactivate:)) this is the label-INCLUSIVE
                // slot key, so the siblings stay distinct here and BOTH emit; for every other method it is
                // the unchanged label-erased signature key.
                var methodKey = ProtocolMethodDisambiguator.EffectiveRawKey(methodDecl, protocolDecl, env.TypeDatabase);
                if (emittedMethods.Contains(methodKey))
                {
                    _logger.LogDebug($"Skipping duplicate method '{methodDecl.Name}' in interface {protocolDecl.Name}");
                    ReportCollector.RecordMemberSkipped(methodDecl, SkipReason.DuplicateSignature, "Duplicate protocol method signature.");
                    continue;
                }
                emittedMethods.Add(methodKey);

                // Secondary dedup: different Swift types can project to the same C# type.
                // When two requirements share a base name + projected param types but differ only by argument
                // LABELS, ProtocolMethodDisambiguator gives each a label-derived name (built ObjC-selector
                // style), and EffectiveProjectedKey computes the projected key under that name — so the
                // siblings produce DISTINCT projected keys and both survive instead of all-but-one being
                // dropped. Pure type-erasure collisions (same labels, types that project alike) still collapse
                // here, exactly as before. The name is threaded through every proxy/receiver/validator site
                // below via the same disambiguator so the interface contract stays CS0535-clean.
                var projectedKey = ProtocolMethodDisambiguator.EffectiveProjectedKey(methodDecl, protocolDecl, env.TypeDatabase, propertyNames: null);
                if (!emittedCSharpKeys.Add(projectedKey))
                {
                    skippedMethodKeys.Add(methodKey);
                    _logger.LogDebug($"Skipping method '{methodDecl.Name}' - projected C# signature collides with already-emitted method.");
                    ReportCollector.RecordMemberSkipped(methodDecl, SkipReason.DuplicateSignature, "Projected C# method signature collides with already-emitted method.");
                    continue;
                }

                // Denied out through the same door the gate skip below uses, and from the same place:
                // after both keys are reserved. `skippedMethodKeys` is key-wide and the reverse-dispatch
                // walks consume it by key, so releasing `methodKey` here would let a sibling sharing it
                // become the emitted requirement while the key-wide skip still suppressed its proxy
                // implementation (CS0535). Holding both keys makes a denial indistinguishable from the
                // gate skip, which is the behaviour the whole retry is defined against.
                if (EmissionSeam.TryDenyUpFront(methodDecl))
                {
                    skippedMethodKeys.Add(methodKey);
                    continue;
                }

                // Evaluate method gates via centralized evaluator (M5-M10)
                var methodGateEvaluator = new MemberGateEvaluator(env.TypeDatabase);
                var methodGate = methodGateEvaluator.EvaluateMethod(methodDecl, protocolDecl.ModuleDecl, protocolDecl);
                if (methodGate.IsSkipped)
                {
                    skippedMethodKeys.Add(methodKey);
                    _logger.LogDebug($"Skipping method '{methodDecl.Name}' in interface {protocolDecl.Name} - {methodGate.Details}");
                    ReportCollector.RecordMemberSkipped(methodDecl, methodGate.Reason!.Value, methodGate.DetailsForReport!);
                    continue;
                }

                // Emitted signature collision (stays inline — uses stateful HashSet)
                var emittedSignature = BuildEmittedSignature(methodDecl, env.TypeDatabase, protocolDecl, emittedCSharpPropertyNames);
                if (!emittedResolvedSignatures.Add(emittedSignature))
                {
                    skippedMethodKeys.Add(methodKey);
                    _logger.LogDebug($"Skipping method '{methodDecl.Name}' - emitted C# signature collides with already-emitted method.");
                    ReportCollector.RecordMemberSkipped(methodDecl, SkipReason.DuplicateSignature, "Emitted C# method signature collides with already-emitted method.");
                    continue;
                }

                // Track closure methods that passed all gates and are emitted in interface.
                // Closure methods get NotSupportedException stubs in the proxy unless they are
                // on the dispatchable closure-param surface — those flow through the normal
                // emission path so the proxy receiver and EveryProtocol extension implement
                // real Swift→C# forward dispatch.
                // Existential-only methods flow through normal emission — receivers already handle
                // ExistentialContainer marshalling via GetReceiverExistentialSetterConversion.
                if (methodGate.IsInterfaceOnly)
                {
                    bool hasClosure = methodGate.SoftFlags.HasFlag(SoftGateFlags.HasClosureParam);
                    // Async closure-param methods are lifted into dispatch alongside the
                    // regular closure-param shape, so the proxy gets a real receiver instead
                    // of the NotSupportedException stub.
                    if (hasClosure
                        && !EveryProtocolEmitter.IsDispatchableClosureMethod(methodDecl, closureHandler)
                        && !EveryProtocolEmitter.IsDispatchableAsyncClosureMethod(methodDecl, closureHandler))
                    {
                        skippedMethodKeys.Add(methodKey);
                        closureSkippedMethodKeys.Add(methodKey);
                        // The requirement still emits on the interface; only the proxy's
                        // implementation degrades to a throwing SB0003 stub.
                        ReportCollector.RecordMemberDegraded(
                            methodDecl, protocolDecl, SkipReason.ProtocolWitnessNotDispatchable,
                            "closure parameters cannot be marshalled through a witness table");
                    }
                }

                // Check if this method has an extension default (direct or from sub-protocol).
                // When interface inheritance is enabled, sub-protocol defaults must also produce DIMs
                // on parent interfaces — otherwise conforming types get CS0535 for inherited requirements
                // that are satisfied by a sub-protocol default in Swift.
                bool isExtensionDefault = false;
                if (extensionDefaultsIndex != null)
                {
                    var extMethodKey = ProtocolExtensionEmitter.BuildMethodKey(methodDecl);
                    isExtensionDefault = extensionDefaultsIndex.HasMethodDefault(
                        protoQualifiedName, extMethodKey);
                }

                // ObjC `@objc optional` methods: emit as DIM with a no-op default body
                // so consumers don't have to implement them. Track for proxy skip.
                if (methodDecl.IsObjCOptional)
                {
                    skippedMethodKeys.Add(methodKey);
                    // Contain @objc optional method DIM emission. Escalates to the protocol
                    // when the fault sticks after denying this method.
                    // Seam-denial backstop; the skip set has to pick up a denial or the proxy
                    // implements a requirement the interface no longer declares.
                    if (EmissionSeam.Guard(
                        methodDecl,
                        RecoveryScope.LeafApi,
                        protocolDecl,
                        () => EmitInterfaceMethod(bodyWriter, methodDecl, env.TypeDatabase, closureHandler, protocolDecl, emittedCSharpPropertyNames, isExtensionDefault: false, isStaticAbstract: false, emissionCtx: emissionCtx, isObjCOptional: true)))
                    {
                        emittedInterfaceMemberCount++;
                        ReportCollector.RecordMemberEmitted(methodDecl);
                    }
                    else
                    {
                        skippedMethodKeys.Add(methodKey);
                    }
                    // Skip the nint→int DIM overload for optional members; the no-op DIM
                    // already covers the convenience-overload role and a second DIM with
                    // the same projected name would be a duplicate.
                    continue;
                }

                // Contain the ordinary protocol method requirement. Escalates to the protocol
                // type rather than the module if leaf denial does not clear the fault.
                // Seam-denial backstop, gated for the same reason as the ObjC-optional branch above.
                // A denial also skips the nint→int DIM below: an overload of a requirement that was
                // never declared has nothing to narrow.
                if (!EmissionSeam.Guard(
                    methodDecl,
                    RecoveryScope.LeafApi,
                    protocolDecl,
                    () => EmitInterfaceMethod(bodyWriter, methodDecl, env.TypeDatabase, closureHandler, protocolDecl, emittedCSharpPropertyNames, isExtensionDefault, emissionCtx: emissionCtx)))
                {
                    skippedMethodKeys.Add(methodKey);
                    continue;
                }
                emittedInterfaceMemberCount++;
                ReportCollector.RecordMemberEmitted(methodDecl);

                // F1: Emit DIM (Default Interface Method) overload with narrowed nint→int params.
                // Proxy classes inherit DIMs automatically — no changes needed in ProtocolProxyEmitter.
                // Skip nint DIM overload for extension-defaulted methods (a DIM that throws shouldn't also get a convenience overload).
                if (!isExtensionDefault)
                {
                    // Contain the nint→int convenience overload for the same method leaf.
                    // Escalates to the protocol on a sticky overload-emission fault.
                    EmissionSeam.Guard(
                        methodDecl,
                        RecoveryScope.LeafApi,
                        protocolDecl,
                        () => TryEmitInterfaceMethodNintOverload(bodyWriter, methodDecl, env.TypeDatabase, protocolDecl, emittedCSharpKeys, emittedCSharpPropertyNames));
                }
            }

            // Record operators as skipped - C# interfaces cannot have operator overloads
            foreach (var operatorDecl in protocolDecl.Operators)
            {
                ReportCollector.RecordMemberSkipped(operatorDecl, SkipReason.StaticProtocolMember, "Protocol operator requirements cannot be declared in C# interfaces.");
            }

            // Now emit the interface declaration with optional SB0004 diagnostic.
            // We deferred writing the declaration until after the body was buffered
            // so we know whether any members were emitted.
            XmlDocCommentEmitter.EmitDocComment(csWriter, protocolDecl);
            AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, protocolDecl, emitObsolete: true);
            if (emittedInterfaceMemberCount == 0 && totalDeclaredMembers > 0 && inheritedInterfaces.Count == 0)
            {
                csWriter.WriteLine($"[Obsolete(\"All {totalDeclaredMembers} protocol member(s) were skipped during binding generation (SB0004). \" +");
                csWriter.WriteLine("    \"This interface is empty because no members could be projected to C#.\",");
                csWriter.WriteLine("    DiagnosticId = \"SB0004\",");
                csWriter.WriteLine("    UrlFormat = \"https://github.com/justinwojo/swift-dotnet-bindings/wiki/Troubleshooting\")]");
            }
            if (protocolDecl.Name.StartsWith("_"))
                csWriter.WriteLine("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
            var whereClause = GetInterfaceWhereClause(protocolDecl);
            if (inheritedInterfaces.Count > 0)
            {
                csWriter.WriteLine($"public interface {interfaceName} : {string.Join(", ", inheritedInterfaces)}{whereClause}");
            }
            else
            {
                csWriter.WriteLine($"public interface {interfaceName}{whereClause}");
            }
            csWriter.WriteLine("{");
            // Flush the buffered body (already indented by bodyWriter), carrying across the fragment
            // boundaries bodyWriter recorded. A plain InnerWriter.Write would land the whole interface
            // body as one opaque run, collapsing every member's provenance onto the protocol itself.
            csWriter.WriteAbsorbing(bodyStringWriter.ToString(), bodyWriter);
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            // Record the direct emitted member count on the protocol's TypeRecord.
            // This is only the count of members declared directly on this interface.
            // Inherited requirements are added in a post-emission fixup pass
            // (FixupProtocolInheritedRequirements) to avoid order-dependent miscounting
            // when a child protocol is emitted before its parent in the same module.
            if (env.TypeDatabase.TryGetTypeRecord(protocolDecl.SwiftTypeName!, out _))
            {
                // Finding 47: stamp the direct member count via the emission-result path.
                env.TypeDatabase.ApplyEmissionResult(protocolDecl.SwiftTypeName!,
                    new TypeEmissionResult { EmittedMemberCount = emittedInterfaceMemberCount });
            }

            // The proxy-emission decision (emit / suppress-by-conformance / skip-unsupported-module)
            // lives in ProtocolProxyEmissionPolicy.Decide so the order-independent
            // SuppressedProxyPrecomputer pre-pass and this emit-time path reach an identical verdict
            // from one predicate. The pre-pass front-loads the suppressed-name set so emit-time
            // reference gates (which replaced the retired whole-file generate-then-strip post-pass) see a
            // complete set even for free functions / earlier-declared types. RecordSuppressedProxy
            // here is now an idempotent re-record of what the pre-pass already set.
            switch (ProtocolProxyEmissionPolicy.Decide(protocolDecl, env.TypeDatabase, context.EmissionContext))
            {
                // Skip proxy class if a required member references an unsupported module (SwiftUI,
                // Combine). The Swift EveryProtocol conformance is also skipped (in ModuleHandler),
                // so emitting the proxy would call non-existent Swift symbols (SetVtable,
                // WitnessTableGetter). RecordMemberSkipped (not RecordTypeSkipped) because
                // RecordTypeEmitted was already called for the interface — the proxy is a
                // sub-artifact of the type.
                case ProxyEmissionDecision.SkippedUnsupportedModule:
                    // Record the suppression so a retained consumer that projects `any P` downgrades its
                    // reference instead of emitting a dangling `new {P}Proxy(…)`. The SwiftUI/Combine case
                    // usually has no such consumer, but the ingestion-quarantine case (SWIFTBIND046
                    // withdraws the protocol's methods yet keeps the protocol + a `consume(base: any P)`)
                    // does — the SwiftRichString StyleProtocol CS0246. `?.` because this arm can be reached
                    // with a null EmissionContext (unlike SuppressedByConformance, which requires ctx);
                    // the precompute pass records the same name up front on the real emission path.
                    context.EmissionContext?.RecordSuppressedProxy(ProtocolProxyEmissionPolicy.ProxyClassName(protocolDecl));
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Type, ProtocolProxyEmissionPolicy.ProxyClassName(protocolDecl),
                        protocolDecl, SkipReason.SwiftUIConstraint,
                        "Protocol proxy skipped: required members reference unsupported module types.");
                    break;

                // EveryProtocol conformance was not emitted (class-bound, genericSig constraint,
                // method type conflict, static methods, constructor requirement, etc.). Without the
                // conformance the proxy's NativeMethods would reference non-existent Swift symbols
                // (SetVtable, GetWitnessTable) → TypeInitializationException at runtime. Member
                // bodies in other types that reference the proxy are gated at emit time (CONSUME
                // sites drop the wrap fallback; PRODUCE sites stub the whole member). Read-only
                // (Swift-vended-only) proxies are exempt inside Decide() — their proxy IS emitted.
                case ProxyEmissionDecision.SuppressedByConformance:
                    var suppressedProxyClassName = ProtocolProxyEmissionPolicy.ProxyClassName(protocolDecl);
                    context.EmissionContext!.RecordSuppressedProxy(suppressedProxyClassName);
                    // A protocol dropped from suitableProtocols before any conformance decision was
                    // recorded has a null GetConformanceSkipReason. Rather than collapse every such
                    // pre-filter drop to the opaque "no decision recorded", attribute the drop from the
                    // protocol's shape (internal / associated-type-or-Self / genuinely unexplained) so
                    // the skip triage can bucket it — internal is expected, an unexplained public one is
                    // worth a look. EveryProtocolSkipCause owns this vocabulary end to end.
                    var conformanceSkipCause = context.EmissionContext.GetConformanceSkipReason(
                        protocolDecl.SwiftTypeName?.ModuleQualifiedName ?? protocolDecl.Name)
                        ?? EveryProtocolSkipCause.ForDroppedProtocol(protocolDecl);
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Type, suppressedProxyClassName,
                        protocolDecl, SkipReason.EveryProtocolConformanceSkipped,
                        $"Protocol proxy skipped: EveryProtocol conformance was not emitted ({conformanceSkipCause}).");
                    break;

                default:
                    // context.EmissionContext is intentionally nullable — null triggers the
                    // direct-emit fallback in EmitProtocolProxy (unit-test path without
                    // ModuleEmissionContext). GetEmissionContext() would always be non-null and
                    // route all proxies through the deferred path.
                    EmitProtocolProxy(csWriter, protocolDecl, env.TypeDatabase, skippedMethodKeys, skippedPropertyNames, skippedSubscriptIndices,
                        closureSkippedMethodKeys, closureSkippedPropertyNames, staticAbstractPropertyNames, staticAbstractMethodKeys, context.EmissionContext);
                    break;
            }
        }

        /// <summary>
        /// Emits a proxy class that enables C# code to implement this protocol.
        /// The proxy wraps either a C# implementation or a Swift existential container.
        /// </summary>
        private void EmitProtocolProxy(CSharpWriter csWriter, ProtocolDecl protocolDecl, ITypeDatabase typeDatabase,
            HashSet<string> skippedMethodKeys, HashSet<string> skippedPropertyNames, HashSet<int> skippedSubscriptIndices,
            HashSet<string> closureSkippedMethodKeys, HashSet<string> closureSkippedPropertyNames,
            HashSet<string> staticAbstractPropertyNames, HashSet<string> staticAbstractMethodKeys,
            ModuleEmissionContext? emissionCtx = null)
        {
            var moduleName = protocolDecl.ModuleDecl?.Name ?? "Swift";
            var proxyEmitter = new ProtocolProxyEmitter(typeDatabase, _logger, moduleName, emissionCtx);

            // Buffer proxy output for deferred emission in SwiftInterop sub-namespace
            if (emissionCtx != null)
            {
                var proxyStringWriter = new System.IO.StringWriter();
                var proxyWriter = new CSharpWriter(proxyStringWriter);
                proxyWriter.Indent = 1; // One level of indent inside the sub-namespace
                proxyEmitter.EmitProxyClass(proxyWriter, protocolDecl, skippedMethodKeys, skippedPropertyNames, skippedSubscriptIndices,
                    closureSkippedMethodKeys, closureSkippedPropertyNames, staticAbstractPropertyNames, staticAbstractMethodKeys);
                emissionCtx.AddDeferredProxyClass(proxyStringWriter.ToString());
            }
            else
            {
                // Fallback: emit directly (e.g., unit tests without ModuleEmissionContext)
                proxyEmitter.EmitProxyClass(csWriter, protocolDecl, skippedMethodKeys, skippedPropertyNames, skippedSubscriptIndices,
                    closureSkippedMethodKeys, closureSkippedPropertyNames, staticAbstractPropertyNames, staticAbstractMethodKeys);
            }
        }

        /// <summary>
        /// Gets the interface name, including generic parameters for protocols with associated types.
        /// </summary>
        private static string GetInterfaceNameWithGenerics(ProtocolDecl protocolDecl)
        {
            var baseName = NameProvider.GetInterfaceName(protocolDecl.Name, moduleName: protocolDecl.ModuleDecl?.Name ?? "");

            // If the protocol has associated types or Self requirement, make it generic
            if (protocolDecl.HasSelfRequirement)
            {
                return $"{baseName}<TSelf>";
            }

            if (protocolDecl.AssociatedTypes.Count > 0)
            {
                var typeParams = protocolDecl.AssociatedTypes.Select(at => $"T{at.Name}");
                return $"{baseName}<{string.Join(", ", typeParams)}>";
            }

            return baseName;
        }

        /// <summary>
        /// Returns the generic constraint suffix (leading space + "where ...") for the
        /// interface declaration, or an empty string if no constraints apply. Must be
        /// appended AFTER the base interface list — placing the where clause before
        /// the base list is a C# syntax error (CS1003).
        /// </summary>
        private static string GetInterfaceWhereClause(ProtocolDecl protocolDecl)
        {
            if (protocolDecl.HasSelfRequirement)
            {
                var baseName = NameProvider.GetInterfaceName(
                    protocolDecl.Name,
                    moduleName: protocolDecl.ModuleDecl?.Name ?? "");
                return $" where TSelf : {baseName}<TSelf>";
            }
            return string.Empty;
        }

        /// <summary>
        /// Gets the list of inherited C# interfaces for the protocol.
        /// Resolves each inherited Swift protocol to its C# interface name via the type database.
        /// Skips protocols that aren't in the type database (e.g., stdlib protocols without
        /// runtime stubs, or cross-module protocols not yet processed).
        /// </summary>
        private List<string> GetInheritedInterfaceList(ProtocolDecl protocolDecl, ITypeDatabase typeDatabase,
            ModuleEmissionContext? emissionCtx = null)
        {
            var currentModule = protocolDecl.ModuleDecl?.Name;
            var result = new List<string>();
            var seen = new HashSet<string>();
            foreach (var inherited in protocolDecl.InheritedProtocols)
            {
                // Skip AnyObject — it's a class-bound constraint, not a real interface
                if (inherited.Name is "Swift.AnyObject" or "AnyObject")
                    continue;

                // Skip marker protocols that have no C# representation
                if (inherited.NameWithoutModule is "Sendable" or "Escapable" or "Copyable" or "SendableMetatype")
                    continue;

                // Look up the inherited protocol in the type database.
                // Cross-module inherited protocols are included when the type database has
                // the parent's TypeRecord (loaded via the dependency module's emitted XML);
                // GetInterfaceName qualifies the reference with the parent's namespace.
                // Without this, IProtocolProxyImpl<IChild> can't resolve covariantly to
                // IProtocolProxyImpl<IParent> for cross-module inherited-delegate dispatch
                // (justinwojo/swift-dotnet-bindings#40 cross-module variant). A missing
                // TypeRecord still skips, so dependencies not loaded into the DB fall back
                // safely to the original "skip cross-module" behavior.
                var swiftTypeName = SwiftTypeName.FromTypeSpec(inherited);
                if (!typeDatabase.TryGetTypeRecord(swiftTypeName, out var inheritedRecord))
                    continue;

                // Only include protocols (not classes/structs that happen to share a name)
                if (inheritedRecord.Kind != TypeRecordKind.Protocol)
                    continue;

                // Skip protocols with associated types or Self requirements — their interfaces
                // are generic (e.g., ICollection<TElement>) and we can't know the type args here
                if (inheritedRecord.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes) ||
                    inheritedRecord.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement))
                    continue;

                // Skip underscore-suppressed protocols — their interfaces are not emitted,
                // so referencing them as a parent interface would cause CS0246
                if (emissionCtx != null && swiftTypeName != null &&
                    emissionCtx.IsUnderscoreSuppressed(swiftTypeName.ToString()))
                    continue;

                var interfaceName = NameProvider.GetInterfaceName(
                    inherited.NameWithoutModule,
                    moduleName: inherited.Module,
                    currentModuleName: currentModule ?? string.Empty);
                if (seen.Add(interfaceName))
                    result.Add(interfaceName);
            }
            return result;
        }

        /// <summary>
        /// Emits a property declaration for an interface.
        /// </summary>
        private void EmitInterfaceProperty(CSharpWriter csWriter, PropertyDecl propertyDecl, ITypeDatabase typeDatabase, ClosureHandler closureHandler, ProtocolDecl? protocolContext = null, bool isExtensionDefault = false, bool isStaticAbstract = false, bool isObjCOptional = false, ModuleEmissionContext? emissionCtx = null)
        {
            // Same module context as the factory projection this feeds: a bound-generic
            // argument that is a closure renders its delegate signature through this handler,
            // and an existential in that signature has to name the module owning the protocol
            // or the interface declaration emits a name the consuming assembly can't resolve.
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase, conformanceGraph: null,
                currentModuleName: protocolContext?.ModuleDecl?.Name);

            // Resolve property type using factory-first projection
            var csharpTypeName = GetCSharpTypeName(propertyDecl.SwiftTypeSpec, typeDatabase, boundGenericsHandler, protocolContext, isParameter: false);

            // F1: Narrow nint/nuint property types to int/uint for idiomatic C#.
            csharpTypeName = NativeIntOverloadEmitter.NarrowNativeIntType(csharpTypeName);

            // Determine accessors
            var hasGetter = propertyDecl.Accessors.OfType<GetAccessorDecl>().Any();
            var hasSetter = propertyDecl.Accessors.OfType<SetAccessorDecl>().Any();

            string accessors;
            if (hasGetter && hasSetter)
            {
                accessors = "{ get; set; }";
            }
            else if (hasGetter)
            {
                accessors = "{ get; }";
            }
            else if (hasSetter)
            {
                accessors = "{ set; }";
            }
            else
            {
                // Default to get-only if no accessors found
                accessors = "{ get; }";
            }

            var propertyName = NameProvider.GetPropertyName(propertyDecl.Name);

            // Emit [UnsupportedSwiftType] if the property type falls back to AnyType
            if (UnsupportedSwiftTypeSupport.TryFindFallbackInfo(typeDatabase, closureHandler, propertyDecl.SwiftTypeSpec, out var fallbackInfo))
            {
                UnsupportedSwiftTypeSupport.EmitAttribute(csWriter, fallbackInfo, emissionCtx);
            }

            // The single flag above names only the first degraded existential; record the whole
            // property type so every DISTINCT degraded existential (e.g. `(any P, any Q)`) raises its
            // own loud SWIFTBIND023 instead of being silently degraded to object. Dedup makes the
            // overlap with the flag above harmless.
            UnsupportedSwiftTypeSupport.RecordExistentialDegradations(
                emissionCtx, typeDatabase, closureHandler, new[] { propertyDecl.SwiftTypeSpec });

            XmlDocCommentEmitter.EmitDocComment(csWriter, propertyDecl);
            AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, propertyDecl, protocolContext, emitObsolete: true);
            if (isStaticAbstract)
            {
                // Static virtual with throw body: provides interface-level default so the
                // interface can be used as a type argument (avoids CS8920), while conforming
                // types override with actual implementations. Our conformance validator
                // ensures types have matching static members before emitting conformances.
                //
                // The throwing body is DELIBERATELY left bare — no [Obsolete] poison. This is
                // a partial failure, not a total one: `T.Member` through a generic constraint
                // dispatches to an overriding conformer's real static at runtime, and C# binds
                // the reference against the interface member, so any [Obsolete] here (even a
                // warning) would flag every legitimate override-dispatch call site. Poison is
                // reserved for shapes that throw for EVERY receiver — the suppressed-proxy reads
                // and the proxy's own static stub (a proxy can never dispatch a static, so that
                // stub IS [Obsolete]-poisoned in ProtocolProxyEmitter). This overridable interface
                // default is not one of them.
                if (hasGetter && hasSetter)
                {
                    csWriter.WriteLine($"static virtual {csharpTypeName} {propertyName}");
                    csWriter.WriteLine("{");
                    csWriter.Indent++;
                    csWriter.WriteLine("get => throw new global::System.NotSupportedException(\"Static protocol members must be accessed on concrete types, not through the protocol interface.\");");
                    csWriter.WriteLine("set => throw new global::System.NotSupportedException(\"Static protocol members must be accessed on concrete types, not through the protocol interface.\");");
                    csWriter.Indent--;
                    csWriter.WriteLine("}");
                }
                else if (hasGetter)
                {
                    csWriter.WriteLine($"static virtual {csharpTypeName} {propertyName}");
                    csWriter.Indent++;
                    csWriter.WriteLine("=> throw new global::System.NotSupportedException(\"Static protocol members must be accessed on concrete types, not through the protocol interface.\");");
                    csWriter.Indent--;
                }
                else
                {
                    csWriter.WriteLine($"static virtual {csharpTypeName} {propertyName}");
                    csWriter.WriteLine("{");
                    csWriter.Indent++;
                    csWriter.WriteLine("set => throw new global::System.NotSupportedException(\"Static protocol members must be accessed on concrete types, not through the protocol interface.\");");
                    csWriter.Indent--;
                    csWriter.WriteLine("}");
                }
                return;
            }
            if (isExtensionDefault)
            {
                // Emit as DIM (Default Interface Method) with NotSupportedException body.
                // Matches method DIM pattern — types with direct implementation override the DIM.
                //
                // The throwing body is DELIBERATELY left bare — no [Obsolete] poison. An
                // overriding conformer (including the Swift extension default the generator
                // injects onto each conformer) succeeds at runtime through this same interface
                // slot, so poisoning the member would flag every legitimate `x.Member`/`T.Member`
                // dispatch that C# binds against the interface. Only suppressed-proxy reads —
                // which throw for every receiver — earn the compile-visible poison.
                if (hasGetter && hasSetter)
                {
                    csWriter.WriteLine($"{csharpTypeName} {propertyName}");
                    csWriter.WriteLine("{");
                    csWriter.Indent++;
                    csWriter.WriteLine($"get => throw new global::System.NotSupportedException(\"{ExtensionDefaultPropertyMessage}\");");
                    csWriter.WriteLine($"set => throw new global::System.NotSupportedException(\"{ExtensionDefaultPropertyMessage}\");");
                    csWriter.Indent--;
                    csWriter.WriteLine("}");
                }
                else if (hasGetter)
                {
                    csWriter.WriteLine($"{csharpTypeName} {propertyName}");
                    csWriter.Indent++;
                    csWriter.WriteLine($"=> throw new global::System.NotSupportedException(\"{ExtensionDefaultPropertyMessage}\");");
                    csWriter.Indent--;
                }
                else
                {
                    // set-only or no accessors: emit with throw body
                    csWriter.WriteLine($"{csharpTypeName} {propertyName}");
                    csWriter.WriteLine("{");
                    csWriter.Indent++;
                    csWriter.WriteLine($"set => throw new global::System.NotSupportedException(\"{ExtensionDefaultPropertyMessage}\");");
                    csWriter.Indent--;
                    csWriter.WriteLine("}");
                }
            }
            else if (isObjCOptional)
            {
                // `@objc optional` lowering: DIM with `default` getter and no-op setter so
                // consumers can leave the optional unimplemented without CS0535.
                if (hasGetter && hasSetter)
                {
                    csWriter.WriteLine($"{csharpTypeName} {propertyName}");
                    csWriter.WriteLine("{");
                    csWriter.Indent++;
                    csWriter.WriteLine("get => default!;");
                    csWriter.WriteLine("set { }");
                    csWriter.Indent--;
                    csWriter.WriteLine("}");
                }
                else if (hasGetter)
                {
                    csWriter.WriteLine($"{csharpTypeName} {propertyName} => default!;");
                }
                else
                {
                    csWriter.WriteLine($"{csharpTypeName} {propertyName}");
                    csWriter.WriteLine("{");
                    csWriter.Indent++;
                    csWriter.WriteLine("set { }");
                    csWriter.Indent--;
                    csWriter.WriteLine("}");
                }
            }
            else
            {
                // When a child protocol refines an inherited get-only requirement into
                // get+set (or otherwise redeclares an inherited member with the same C#
                // name), C# requires the `new` keyword to suppress CS0108 "hides
                // inherited member". The sibling-property dispatch fan-out already routes
                // through the child's body via Swift cross-extension witness resolution;
                // this just gates the C# interface declaration so it compiles.
                var newModifier = ChildRefinesInheritedProperty(propertyDecl, protocolContext) ? "new " : "";
                csWriter.WriteLine($"{newModifier}{csharpTypeName} {propertyName} {accessors}");
            }
        }

        /// <summary>
        /// True when this property name shadows a same-named property declared on any
        /// inherited (parent) protocol. Used to add the C# <c>new</c> modifier so
        /// <c>CS0108: hides inherited member</c> doesn't fail compilation. Walks one
        /// level of <see cref="ProtocolDecl.InheritedProtocols"/> by name — the
        /// inherited protocol's full transitive set is irrelevant for shadowing.
        /// </summary>
        private static bool ChildRefinesInheritedProperty(PropertyDecl propertyDecl, ProtocolDecl? protocolContext)
        {
            if (protocolContext is null || protocolContext.InheritedProtocols.Count == 0)
                return false;
            var moduleDecl = protocolContext.ModuleDecl;
            if (moduleDecl is null)
                return false;
            // Inherited protocol names are module-qualified; compare against unqualified short names.
            var candidateNames = protocolContext.InheritedProtocols
                .Select(t => t.NameWithoutModule)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var parent in moduleDecl.Protocols)
            {
                if (!candidateNames.Contains(parent.Name))
                    continue;
                if (parent.Properties.Any(p => string.Equals(p.Name, propertyDecl.Name, StringComparison.Ordinal)))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Emits a subscript declaration as a C# indexer for an interface.
        /// Swift: subscript(key: ImageCacheKey) -> ImageContainer? { get set }
        /// C#:   SwiftOptional<ImageContainer> this[ImageCacheKey key] { get; set; }
        /// </summary>
        private void EmitInterfaceSubscript(CSharpWriter csWriter, SubscriptDecl subscriptDecl, ITypeDatabase typeDatabase, ClosureHandler closureHandler, ProtocolDecl? protocolContext = null, ModuleEmissionContext? emissionCtx = null)
        {
            // Module context so an existential nested in a bound-generic argument's closure
            // signature names the module owning its protocol (see EmitInterfaceProperty).
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase, conformanceGraph: null,
                currentModuleName: protocolContext?.ModuleDecl?.Name);
            NameProvider.DeduplicateParameterNamesForParameterList(subscriptDecl.IndexParameters);

            // Resolve return type using factory-first projection
            var returnTypeName = GetCSharpTypeName(subscriptDecl.ReturnTypeSpec, typeDatabase, boundGenericsHandler, protocolContext, isParameter: false);

            // Build index parameters
            var parameters = new List<string>();
            foreach (var param in subscriptDecl.IndexParameters)
            {
                var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec, typeDatabase, boundGenericsHandler, protocolContext);
                var paramName = NameProvider.GetCSharpParameterName(param);
                parameters.Add($"{paramTypeName} {paramName}");
            }

            // Determine accessors
            var hasGetter = subscriptDecl.HasGetter;
            var hasSetter = subscriptDecl.HasSetter;

            string accessors;
            if (hasGetter && hasSetter)
            {
                accessors = "{ get; set; }";
            }
            else if (hasGetter)
            {
                accessors = "{ get; }";
            }
            else if (hasSetter)
            {
                accessors = "{ set; }";
            }
            else
            {
                // Default to get-only if no accessors found
                accessors = "{ get; }";
            }

            // Emit [UnsupportedSwiftType] if the return type or any parameter falls back to AnyType
            if (UnsupportedSwiftTypeSupport.TryFindFallbackInfo(typeDatabase, closureHandler, subscriptDecl.ReturnTypeSpec, out var subscriptFallbackInfo))
            {
                UnsupportedSwiftTypeSupport.EmitAttribute(csWriter, subscriptFallbackInfo, emissionCtx);
            }
            else
            {
                foreach (var param in subscriptDecl.IndexParameters)
                {
                    if (UnsupportedSwiftTypeSupport.TryFindFallbackInfo(typeDatabase, closureHandler, param.SwiftTypeSpec, out var paramFallbackInfo))
                    {
                        UnsupportedSwiftTypeSupport.EmitAttribute(csWriter, paramFallbackInfo, emissionCtx);
                        break; // One attribute is enough to flag the subscript
                    }
                }
            }

            // Record EVERY distinct degraded existential (return + each index parameter), not just the
            // one the single flag names, so SWIFTBIND023 fires per distinct type.
            UnsupportedSwiftTypeSupport.RecordExistentialDegradations(
                emissionCtx, typeDatabase, closureHandler,
                new[] { subscriptDecl.ReturnTypeSpec }.Concat(subscriptDecl.IndexParameters.Select(p => p.SwiftTypeSpec)));

            AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, subscriptDecl, protocolContext, emitObsolete: true);
            csWriter.WriteLine($"{returnTypeName} this[{string.Join(", ", parameters)}] {accessors}");
        }

        /// <summary>
        /// Emits a method declaration for an interface.
        /// </summary>
        private void EmitInterfaceMethod(CSharpWriter csWriter, MethodDecl methodDecl, ITypeDatabase typeDatabase, ClosureHandler closureHandler, ProtocolDecl? protocolContext = null, IReadOnlySet<string>? propertyNames = null, bool isExtensionDefault = false, bool isStaticAbstract = false, ModuleEmissionContext? emissionCtx = null, bool isObjCOptional = false)
        {
            // Note: Constructor, static, duplicate, and AnyType generic arg checks
            // are handled at the loop level in Emit(). This method is only called
            // for methods that pass all pre-checks.

            // Module context so an existential nested in a bound-generic argument's closure
            // signature names the module owning its protocol (see EmitInterfaceProperty).
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase, conformanceGraph: null,
                currentModuleName: protocolContext?.ModuleDecl?.Name);
            NameProvider.DeduplicateParameterNames(methodDecl.CSSignature);

            // Get return type using factory-first projection
            var returnType = "void";
            if (methodDecl.CSSignature.Count > 0)
            {
                var returnArg = methodDecl.CSSignature[0];
                if (returnArg.SwiftTypeSpec is not TupleTypeSpec tuple || !tuple.IsEmptyTuple)
                {
                    returnType = GetCSharpTypeName(returnArg.SwiftTypeSpec, typeDatabase, boundGenericsHandler, protocolContext, isParameter: false);
                }
            }

            // Build parameters (skip first which is return type)
            var parameters = new List<string>();
            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var arg = methodDecl.CSSignature[i];
                // Skip debug params (#file, #line, etc.) and empty tuple () params (zero-sized Void)
                if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                    continue;
                if (arg.SwiftTypeSpec.IsEmptyTuple)
                    continue;
                // inout maps to a C# `ref` parameter. The concrete conforming class emits `ref`
                // (MethodSignature.HandleArguments), so the interface declaration MUST match or
                // every conformer fails CS0535. Keep this in lockstep with the proxy + receiver sites.
                var inoutModifier = arg.IsInOut ? "ref " : "";
                var argTypeName = GetCSharpTypeName(arg.SwiftTypeSpec, typeDatabase, boundGenericsHandler, protocolContext);
                var argName = NameProvider.GetCSharpParameterName(arg);
                parameters.Add($"{inoutModifier}{argTypeName} {argName}");
            }

            // Capture hasReturnValue BEFORE async conversion turns void → Task
            var hasReturnValue = returnType != "void";

            // Handle async methods
            if (methodDecl.IsAsync)
            {
                if (returnType == "void")
                {
                    returnType = "Task";
                }
                else
                {
                    returnType = $"Task<{returnType}>";
                }
            }

            // Add CancellationToken to async interface methods (matches WrapperEmitter emission)
            if (methodDecl.IsAsync)
            {
                parameters.Add("global::System.Threading.CancellationToken cancellationToken = default");
            }

            // Emit [UnsupportedSwiftType] if the return type or any parameter falls back to AnyType
            bool emittedAttribute = false;
            if (methodDecl.CSSignature.Count > 0)
            {
                var returnArg = methodDecl.CSSignature[0];
                if (returnArg.SwiftTypeSpec is not TupleTypeSpec tuple || !tuple.IsEmptyTuple)
                {
                    if (UnsupportedSwiftTypeSupport.TryFindFallbackInfo(typeDatabase, closureHandler, returnArg.SwiftTypeSpec, out var returnFallbackInfo))
                    {
                        UnsupportedSwiftTypeSupport.EmitAttribute(csWriter, returnFallbackInfo, emissionCtx);
                        emittedAttribute = true;
                    }
                }
            }
            if (!emittedAttribute)
            {
                for (int j = 1; j < methodDecl.CSSignature.Count; j++)
                {
                    if (UnsupportedSwiftTypeSupport.TryFindFallbackInfo(typeDatabase, closureHandler, methodDecl.CSSignature[j].SwiftTypeSpec, out var paramFallbackInfo))
                    {
                        UnsupportedSwiftTypeSupport.EmitAttribute(csWriter, paramFallbackInfo, emissionCtx);
                        break; // One attribute is enough to flag the method
                    }
                }
            }

            // Record EVERY distinct degraded existential across the signature (return + every param),
            // not just the one the single flag names, so SWIFTBIND023 fires per distinct type.
            // CSSignature[0] is the return.
            UnsupportedSwiftTypeSupport.RecordExistentialDegradations(
                emissionCtx, typeDatabase, closureHandler,
                methodDecl.CSSignature.Select(a => a.SwiftTypeSpec));

            var isSelfReturning = MethodEnvironment.IsSelfReturningMethod(methodDecl);
            // Disambiguator override: a label-only-overload sibling emits under its label-derived name
            // (e.g. ConversationManagerDidActivate) instead of the bare PascalCased method name; identity
            // for every other method (returns methodDecl.Name).
            var methodName = NameProvider.GetPublicMethodName(ProtocolMethodDisambiguator.EffectiveNameInput(methodDecl, protocolContext, typeDatabase), methodDecl.IsAsync, hasReturnValue: hasReturnValue,
                propertyNames: propertyNames, isSelfReturning: isSelfReturning,
                parameterCount: methodDecl.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple),
                isMutating: methodDecl.IsMutating);
            XmlDocCommentEmitter.EmitMethodDocComment(csWriter, methodDecl);
            AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, methodDecl, protocolContext, emitObsolete: true);
            if (isStaticAbstract)
            {
                // Static virtual with throw body: provides interface-level default so the
                // interface can be used as a type argument (avoids CS8920).
                //
                // Left bare by design — no [Obsolete] poison: `T.Method()` dispatches to an
                // overriding conformer's real static at runtime, so poisoning the interface
                // slot would break legitimate generic-constraint dispatch (unlike a
                // suppressed-proxy read, which fails for every receiver).
                csWriter.WriteLine($"static virtual {returnType} {methodName}({string.Join(", ", parameters)})");
                csWriter.Indent++;
                csWriter.WriteLine("=> throw new global::System.NotSupportedException(\"Static protocol members must be called on concrete types, not through the protocol interface.\");");
                csWriter.Indent--;
            }
            else if (isExtensionDefault)
            {
                // Emit as DIM (Default Interface Method) with NotSupportedException body.
                // Types that implement directly → their implementation overrides the DIM.
                // Types relying on the default → inherit the DIM; generic constraints compile.
                //
                // Left bare by design — no [Obsolete] poison: an overriding conformer succeeds
                // at runtime through this same interface slot, so a poison (even warning-level)
                // would flag every legitimate override-dispatch call site. Only suppressed-proxy
                // reads, which fail for every receiver, earn the compile-visible poison.
                csWriter.WriteLine($"{returnType} {methodName}({string.Join(", ", parameters)})");
                csWriter.Indent++;
                csWriter.WriteLine($"=> throw new global::System.NotSupportedException(\"{ExtensionDefaultMethodMessage}\");");
                csWriter.Indent--;
            }
            else if (isObjCOptional)
            {
                // `@objc optional` lowering: DIM whose body silently no-ops. Three shapes:
                //   * void              → empty block `{ }`
                //   * bare Task         → `=> Task.CompletedTask;` (awaiting null Task NREs)
                //   * Task<T>           → `=> Task.FromResult<T>(default!);` (same reason)
                //   * everything else   → `=> default!;`
                // Consumers override only when they care; ignoring matches ObjC semantics.
                csWriter.WriteLine($"{returnType} {methodName}({string.Join(", ", parameters)})");
                if (returnType == "void")
                {
                    csWriter.WriteLine("{");
                    csWriter.WriteLine("}");
                }
                else if (returnType == "Task")
                {
                    csWriter.Indent++;
                    csWriter.WriteLine("=> global::System.Threading.Tasks.Task.CompletedTask;");
                    csWriter.Indent--;
                }
                else if (returnType.StartsWith("Task<", StringComparison.Ordinal) && returnType.EndsWith(">", StringComparison.Ordinal))
                {
                    // Extract the inner generic argument; `Task<long>` → `long`. The DIM must
                    // return a non-null Task so callers that `await` it observe `default(T)`
                    // instead of NRE'ing on a null reference.
                    var inner = returnType.Substring("Task<".Length, returnType.Length - "Task<".Length - 1);
                    csWriter.Indent++;
                    csWriter.WriteLine($"=> global::System.Threading.Tasks.Task.FromResult<{inner}>(default!);");
                    csWriter.Indent--;
                }
                else
                {
                    csWriter.Indent++;
                    csWriter.WriteLine("=> default!;");
                    csWriter.Indent--;
                }
            }
            else
            {
                // `new` modifier when this method shadows an inherited interface's same-name
                // method — silences CS0108. The shadowing usually arises from refined-return
                // covariance (e.g. WCDB's PropertyConvertible refines ColumnConvertible's
                // `_in(string) -> Column` to return `Property`). Same projected key (name +
                // params, no return) means C# treats this as a hide.
                var newModifier = ShadowsInheritedInterfaceMethod(methodDecl, protocolContext, typeDatabase, emissionCtx) ? "new " : "";
                csWriter.WriteLine($"{newModifier}{returnType} {methodName}({string.Join(", ", parameters)});");
            }
        }

        /// <summary>
        /// Returns the C# property-name set the interface emitter actually used for
        /// <paramref name="protocolDecl"/>, or a conservative fallback derived from
        /// <c>protocolDecl.Properties</c> when the cache hasn't been populated. The
        /// fallback is used for ancestors whose interface emission ran in a different
        /// module pass (cross-module inheritance is filtered out elsewhere, but the
        /// helper stays defensive).
        /// </summary>
        private static IReadOnlySet<string>? GetEmittedInterfacePropertyNames(
            ProtocolDecl protocolDecl, ModuleEmissionContext? emissionCtx)
        {
            if (emissionCtx != null)
            {
                var qualifiedName = protocolDecl.SwiftTypeName?.ModuleQualifiedName
                                  ?? $"{protocolDecl.ModuleDecl?.Name ?? "Unknown"}.{protocolDecl.Name}";
                var cached = emissionCtx.GetInterfacePropertyNames(qualifiedName);
                if (cached != null)
                    return cached;
            }
            return new HashSet<string>(protocolDecl.Properties.Select(p => NameProvider.GetPropertyName(p.Name)));
        }

        /// <summary>
        /// Returns true when <paramref name="methodDecl"/> declared on
        /// <paramref name="protocolDecl"/> projects to the same C# overload key
        /// (method name + parameter types, ignoring return type) as a method declared on
        /// any same-module ancestor protocol reachable via <c>InheritedProtocols</c>.
        ///
        /// C# emits CS0108 when an interface method hides an inherited interface's method
        /// without the <c>new</c> modifier. Refined-return covariance (Swift's protocol
        /// witness tables permit it; C# interface contracts do not) is the most common
        /// trigger — see <c>ProtocolProxyEmitter.InterfaceImpl.cs</c> for the matching
        /// proxy-side fix that emits an explicit-interface forwarder for the base slot.
        ///
        /// BFS filter mirrors <see cref="GetInheritedInterfaceList"/>: skip AnyObject,
        /// Sendable/Copyable/Escapable, cross-module, PAT/Self-requirement, and
        /// underscore-suppressed protocols. Stops at the first match — emitting <c>new</c>
        /// once is sufficient regardless of how many ancestors share the slot.
        /// </summary>
        private static bool ShadowsInheritedInterfaceMethod(
            MethodDecl methodDecl, ProtocolDecl? protocolDecl, ITypeDatabase typeDatabase,
            ModuleEmissionContext? emissionCtx = null)
        {
            if (protocolDecl == null || protocolDecl.InheritedProtocols.Count == 0)
                return false;

            var moduleDecl = protocolDecl.ModuleDecl;
            if (moduleDecl == null)
                return false;

            // Compute keys with each protocol's own emitted property-name set so a method
            // renamed `Foo` -> `FooMethod` in one protocol (because that protocol has a
            // property `Foo`) doesn't collide with a method that emits as plain `Foo`
            // elsewhere. Falls back to a conservative approximation when the cache hasn't
            // been populated (e.g. cross-module ancestor whose interface ran in a different
            // module's emission pass).
            var ownPropNames = GetEmittedInterfacePropertyNames(protocolDecl, emissionCtx);
            var ownProjectedKey = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(methodDecl, typeDatabase, protocolDecl, ownPropNames);
            var currentModule = protocolDecl.ModuleDecl?.Name;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<NamedTypeSpec>();
            foreach (var inherited in protocolDecl.InheritedProtocols)
                queue.Enqueue(inherited);

            while (queue.Count > 0)
            {
                var inherited = queue.Dequeue();
                if (inherited.Name is "Swift.AnyObject" or "AnyObject")
                    continue;
                if (inherited.NameWithoutModule is "Sendable" or "Escapable" or "Copyable" or "SendableMetatype")
                    continue;

                var inheritedModule = inherited.Module;
                if (!string.IsNullOrEmpty(inheritedModule) && !string.IsNullOrEmpty(currentModule) &&
                    inheritedModule != currentModule)
                    continue;

                var swiftTypeName = SwiftTypeName.FromTypeSpec(inherited);
                if (!typeDatabase.TryGetTypeRecord(swiftTypeName, out var inheritedRecord))
                    continue;
                if (inheritedRecord.Kind != TypeRecordKind.Protocol)
                    continue;
                if (inheritedRecord.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes) ||
                    inheritedRecord.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement))
                    continue;

                // Skip underscore-suppressed protocols — their interfaces aren't emitted, so the
                // child protocol can't actually inherit (or shadow) anything from them. Without
                // this filter, we'd wrongly add a `new` modifier when the parent interface was
                // never produced, making the modifier itself a CS0109 "new keyword not required"
                // warning — the exact noise this whole pass is trying to prevent.
                if (emissionCtx != null && swiftTypeName != null &&
                    emissionCtx.IsUnderscoreSuppressed(swiftTypeName.ToString()))
                    continue;

                var visitKey = swiftTypeName?.ToString() ?? inherited.NameWithoutModule;
                if (!visited.Add(visitKey))
                    continue;

                var inheritedDecl = moduleDecl.Protocols.FirstOrDefault(p => p.Name == inherited.NameWithoutModule);
                if (inheritedDecl == null)
                    continue;

                var ancestorPropNames = GetEmittedInterfacePropertyNames(inheritedDecl, emissionCtx);
                foreach (var ancestorMethod in inheritedDecl.Methods)
                {
                    if (ancestorMethod.IsConstructor || ancestorMethod.MethodType == MethodType.Static)
                        continue;
                    var ancestorKey = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(ancestorMethod, typeDatabase, inheritedDecl, ancestorPropNames);
                    if (ancestorKey == ownProjectedKey)
                        return true;
                }

                foreach (var grandparent in inheritedDecl.InheritedProtocols)
                    queue.Enqueue(grandparent);
            }
            return false;
        }

        /// <summary>
        /// Gets the C# type name for a Swift type specification, handling bound generics and associated types.
        /// For protocol interfaces, this also handles closures, tuples, and existentials with relaxed requirements
        /// since we're just emitting signatures, not PInvoke implementations.
        /// </summary>
        private string GetCSharpTypeName(TypeSpec typeSpec, ITypeDatabase typeDatabase, BoundGenericsHandler boundGenericsHandler, ProtocolDecl? protocolContext = null, bool isParameter = true)
        {
            // Handle associated type references (e.g., Self.Element, τ_0_0.Element)
            if (typeSpec is AssociatedTypeReferenceSpec assocRef)
            {
                return ProtocolSignatureHelper.MapAssociatedTypeToGenericParam(assocRef, protocolContext);
            }

            // Factory-first with GenericContext: handles all types
            // For Self-requirement protocols, map τ_0_0 → TSelf
            var genericContext = protocolContext?.HasSelfRequirement == true
                ? GenericContext.ForProtocolSelf()
                : GenericContext.Empty;

            var factory = new TypeProjectionFactory();
            // Pass CurrentModuleName so cross-module existential projections (e.g. an
            // umbrella-collapsed `any RealityKit.HasCollision` whose record actually
            // lives in `RealityFoundation`) get namespace-qualified
            // (`RealityFoundation.IHasCollision?`). Without this, the interface
            // declaration emits a bare `IHasCollision?` while the concrete
            // implementation emits the qualified form, producing CS0246 + CS0738
            // when more existentials survive ObjC filtering after the per-module
            // prefix gate.
            var factory_currentModuleName = protocolContext?.ModuleDecl?.Name;
            var projection = factory.Project(typeSpec, new ProjectionContext
            {
                TypeDatabase = typeDatabase,
                IsParameter = isParameter,
                GenericContext = genericContext,
                CompositionCollector = _compositionCollector,
                CurrentModuleName = factory_currentModuleName
            });
            if (projection != null)
                return projection.PublicType;

            // Closure fallback when factory can't fully resolve (e.g., inner types not in TypeDatabase)
            if (typeSpec is ClosureTypeSpec closureType)
                return GetClosureCSharpType(closureType, typeDatabase, protocolContext);

            // Tuple fallback
            if (typeSpec is TupleTypeSpec tupleType)
            {
                if (tupleType.IsEmptyTuple) return "void";
                var elements = tupleType.Elements.Select(e => GetCSharpTypeName(e, typeDatabase, boundGenericsHandler, protocolContext, isParameter)).ToList();
                return $"({string.Join(", ", elements)})";
            }

            // Bound generic fallback: produce full type name with generic args
            // (e.g., BatchedCollection<Swift.AnyType> for unknown inner types).
            // Factory returns null when inner types can't be projected (existentials, unknown types).
            if (typeSpec is NamedTypeSpec boundGeneric && boundGeneric.ContainsGenericParameters)
                return boundGenericsHandler.TranslateBoundGenericTypeToCSharp(typeSpec, GenericContext.Empty);

            // Type record fallback
            return typeDatabase.GetTypeRecordOrAnyType(typeSpec).CSharpTypeName.FullyQualifiedName;
        }

        /// <summary>
        /// Translates a Swift closure type to a C# delegate type for protocol interface emission.
        /// This is less restrictive than the full closure handler since we're just emitting signatures.
        /// </summary>
        private string GetClosureCSharpType(ClosureTypeSpec closureTypeSpec, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext)
        {
            // Module context so an existential nested in a bound-generic argument's closure
            // signature names the module owning its protocol (see EmitInterfaceProperty).
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase, conformanceGraph: null,
                currentModuleName: protocolContext?.ModuleDecl?.Name);

            // Build parameter types
            var paramTypes = new List<string>();
            foreach (var arg in closureTypeSpec.EachArgument())
            {
                paramTypes.Add(GetCSharpTypeName(arg, typeDatabase, boundGenericsHandler, protocolContext));
            }

            // Get return type
            var returnType = closureTypeSpec.ReturnType;
            bool hasReturn = !returnType.IsEmptyTuple;

            if (!hasReturn)
            {
                // Action delegate
                if (paramTypes.Count == 0)
                    return "Action";
                return $"Action<{string.Join(", ", paramTypes)}>";
            }
            else
            {
                // Func delegate — closure return types use isParameter:false (return position)
                // to match ProtocolSignatureHelper's proxy projection. Without this, arrays in
                // closure returns project as IEnumerable<T> here but IReadOnlyList<T> in the
                // proxy, causing the proxy to not implement the interface (compile error).
                var returnTypeName = GetCSharpTypeName(returnType, typeDatabase, boundGenericsHandler, protocolContext, isParameter: false);
                if (paramTypes.Count == 0)
                    return $"Func<{returnTypeName}>";
                return $"Func<{string.Join(", ", paramTypes)}, {returnTypeName}>";
            }
        }

        /// <summary>
        /// Translates a Swift tuple type to a C# ValueTuple type for protocol interface emission.
        /// </summary>
        private string GetTupleCSharpType(TupleTypeSpec tupleTypeSpec, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext)
        {
            // Module context so an existential nested in a bound-generic argument's closure
            // signature names the module owning its protocol (see EmitInterfaceProperty).
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase, conformanceGraph: null,
                currentModuleName: protocolContext?.ModuleDecl?.Name);
            var elements = new List<string>();

            foreach (var element in tupleTypeSpec.Elements)
            {
                var typeName = GetCSharpTypeName(element, typeDatabase, boundGenericsHandler, protocolContext);

                // Include label if present
                if (!string.IsNullOrEmpty(element.TypeLabel))
                {
                    elements.Add($"{typeName} {element.TypeLabel}");
                }
                else
                {
                    elements.Add(typeName);
                }
            }

            return $"({string.Join(", ", elements)})";
        }

        private string BuildEmittedSignature(MethodDecl methodDecl, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext, IReadOnlySet<string>? propertyNames = null)
        {
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            var returnTypeSpec = methodDecl.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
            bool hasReturnValue = returnTypeSpec != null && !returnTypeSpec.IsEmptyTuple;
            var isSelfReturning = MethodEnvironment.IsSelfReturningMethod(methodDecl);
            // Disambiguator override: keeps the emitted-signature dedup key in step with the label-derived
            // name the interface declares, so two label-only siblings produce DISTINCT emitted signatures.
            var methodName = NameProvider.GetPublicMethodName(ProtocolMethodDisambiguator.EffectiveNameInput(methodDecl, protocolContext, typeDatabase), methodDecl.IsAsync, hasReturnValue: hasReturnValue,
                propertyNames: propertyNames, isSelfReturning: isSelfReturning,
                parameterCount: methodDecl.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple),
                isMutating: methodDecl.IsMutating);

            var visibleGenericNames = BaseHandler.CollectVisibleGenericParamNames(methodDecl);
            var paramTypes = new List<string>();
            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var arg = methodDecl.CSSignature[i];
                if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                    continue;
                if (arg.SwiftTypeSpec.IsEmptyTuple)
                    continue;
                var typeSpecForKey = ProtocolSignatureHelper.StripOptionalClassLikeForOverloadIdentity(
                    arg.SwiftTypeSpec, typeDatabase, visibleGenericNames);
                var paramType = GetCSharpTypeName(typeSpecForKey, typeDatabase, boundGenericsHandler, protocolContext, isParameter: true);
                paramType = ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity(paramType, arg.SwiftTypeSpec, typeDatabase);
                paramTypes.Add(paramType);
            }

            // Async methods emit a trailing CancellationToken parameter, so their emitted C# signature
            // carries one more arg than a sync namesake. Mirror the projected-key builder (AF05 ruling b):
            // without this, `func foo() async` and a sibling `func fooAsync()` BOTH render "FooAsync(int)"
            // here and this emitted-signature dedup (emittedResolvedSignatures) silently drops the second
            // — re-collapsing the very async/sync pair the projected-key CancellationToken axis split apart,
            // so only one FooAsync member would reach the interface + proxy. The KeyBuilderAsyncOverloadProtocol
            // fixture proves both members emit; this append is what makes the two emitted signatures diverge.
            if (methodDecl.IsAsync)
            {
                paramTypes.Add("System.Threading.CancellationToken");
            }

            return $"{methodName}({string.Join(",", paramTypes)})";
        }

        /// <summary>
        /// Emits a DIM (Default Interface Method) overload with narrowed nint→int params.
        /// E.g.: nint Skip(nint count); → DIM: int Skip(int count) => (int)Skip((nint)count);
        /// Proxy classes inherit DIMs automatically.
        /// </summary>
        internal void TryEmitInterfaceMethodNintOverload(
            CSharpWriter csWriter, MethodDecl methodDecl, ITypeDatabase typeDatabase,
            ProtocolDecl? protocolContext, HashSet<string> emittedCSharpKeys,
            IReadOnlySet<string>? propertyNames = null)
        {
            // Skip async methods — async interface methods are reshaped to Task<T> with CancellationToken.
            // Generating correct DIM for these requires mirroring the Task wrapping + await + token forwarding.
            if (methodDecl.IsAsync)
                return;

            // Module context so an existential nested in a bound-generic argument's closure
            // signature names the module owning its protocol (see EmitInterfaceProperty).
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase, conformanceGraph: null,
                currentModuleName: protocolContext?.ModuleDecl?.Name);
            var csSignature = methodDecl.CSSignature;
            if (csSignature.Count < 2)
                return;

            // Detect nint/nuint params (skip return type at index 0), including Optional<Swift.Int> → int?
            var conversions = new List<(int index, string nativeType, string convType, bool isOptional)>();
            for (int i = 1; i < csSignature.Count; i++)
            {
                var arg = csSignature[i];
                if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                    continue;
                if (arg.SwiftTypeSpec.IsEmptyTuple)
                    continue;
                if (arg.SwiftTypeSpec is NamedTypeSpec ns && NativeIntOverloadEmitter.TryGetAbiWideningType(ns, out var nativeType))
                {
                    var isUnsigned = nativeType == "nuint";
                    conversions.Add((i, isUnsigned ? "nuint" : "nint", isUnsigned ? "uint" : "int", isOptional: false));
                }
                else if (arg.SwiftTypeSpec is NamedTypeSpec optNs &&
                         optNs.Name == "Swift.Optional" &&
                         optNs.GenericParameters.Count == 1 &&
                         optNs.GenericParameters[0] is NamedTypeSpec innerNs &&
                         NativeIntOverloadEmitter.TryGetAbiWideningType(innerNs, out var optNativeType))
                {
                    var isUnsigned = optNativeType == "nuint";
                    conversions.Add((i, isUnsigned ? "nuint" : "nint", isUnsigned ? "uint" : "int", isOptional: true));
                }
            }

            if (conversions.Count == 0)
                return;

            // Return type stays as-is (nint/nuint) — same overload resolution safety as class method overloads.
            var returnTypeSpec = csSignature[0].SwiftTypeSpec;
            bool hasReturn = !returnTypeSpec.IsEmptyTuple;

            // Dedup: build projected key with narrowed types
            var dimVisibleGenericNames = BaseHandler.CollectVisibleGenericParamNames(methodDecl);
            var dimParamTypes = new List<string>();
            for (int i = 1; i < csSignature.Count; i++)
            {
                var arg = csSignature[i];
                if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                    continue;
                if (arg.SwiftTypeSpec.IsEmptyTuple)
                    continue;
                var conv = conversions.Find(c => c.index == i);
                if (conv != default)
                    dimParamTypes.Add(conv.isOptional ? $"{conv.convType}?" : conv.convType);
                else
                {
                    var typeSpecForKey = ProtocolSignatureHelper.StripOptionalClassLikeForOverloadIdentity(
                        arg.SwiftTypeSpec, typeDatabase, dimVisibleGenericNames);
                    var paramType = GetCSharpTypeName(typeSpecForKey, typeDatabase, boundGenericsHandler, protocolContext, isParameter: true);
                    paramType = ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity(paramType, arg.SwiftTypeSpec, typeDatabase);
                    dimParamTypes.Add(paramType);
                }
            }

            var isSelfReturning = MethodEnvironment.IsSelfReturningMethod(methodDecl);
            // Disambiguator override: the nint→int convenience DIM must carry the same label-derived name
            // as its full-width sibling, or the two label-only overloads' DIMs would collide.
            var methodName = NameProvider.GetPublicMethodName(ProtocolMethodDisambiguator.EffectiveNameInput(methodDecl, protocolContext, typeDatabase), methodDecl.IsAsync, hasReturnValue: hasReturn,
                propertyNames: propertyNames, isSelfReturning: isSelfReturning,
                parameterCount: methodDecl.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple),
                isMutating: methodDecl.IsMutating);

            var dimKey = $"{methodName}({string.Join(",", dimParamTypes)})";
            if (!emittedCSharpKeys.Add(dimKey))
                return;

            // Build parameter list and call arguments
            var paramParts = new List<string>();
            var callArgs = new List<string>();
            for (int i = 1; i < csSignature.Count; i++)
            {
                var arg = csSignature[i];
                if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                    continue;
                if (arg.SwiftTypeSpec.IsEmptyTuple)
                    continue;

                var paramName = NameProvider.GetCSharpParameterName(arg);
                var conv = conversions.Find(c => c.index == i);
                if (conv != default)
                {
                    if (conv.isOptional)
                    {
                        paramParts.Add($"{conv.convType}? {paramName}");
                        callArgs.Add($"({conv.nativeType}?){paramName}");
                    }
                    else
                    {
                        paramParts.Add($"{conv.convType} {paramName}");
                        callArgs.Add($"({conv.nativeType}){paramName}");
                    }
                }
                else
                {
                    // inout params are never nint-narrowed (NativeIntOverloadEmitter skips them),
                    // so they fall here — preserve `ref` on both the DIM signature and its forward
                    // to the primary interface method, which is also `ref`.
                    var inoutModifier = arg.IsInOut ? "ref " : "";
                    var typeName = GetCSharpTypeName(arg.SwiftTypeSpec, typeDatabase, boundGenericsHandler, protocolContext, isParameter: true);
                    paramParts.Add($"{inoutModifier}{typeName} {paramName}");
                    callArgs.Add($"{inoutModifier}{paramName}");
                }
            }

            var paramStr = string.Join(", ", paramParts);
            var argsStr = string.Join(", ", callArgs);

            // Determine return type — keep nint/nuint, don't narrow
            string returnType = hasReturn
                ? GetCSharpTypeName(returnTypeSpec, typeDatabase, boundGenericsHandler, protocolContext, isParameter: false)
                : "void";

            // Emit DIM — no access modifier (interface members are implicitly public)
            if (hasReturn)
            {
                csWriter.WriteLine($"{returnType} {methodName}({paramStr}) => {methodName}({argsStr});");
            }
            else
            {
                csWriter.WriteLine($"void {methodName}({paramStr}) => {methodName}({argsStr});");
            }
        }

        /// <summary>
        /// Post-emission fixup: recomputes EmittedMemberCount for all protocol TypeRecords
        /// to include inherited protocol requirements. Must be called after all protocols in
        /// the module have been emitted (so all direct member counts are set), but before
        /// the module database is serialized.
        /// </summary>
        /// <remarks>
        /// During emission, ProtocolHandler.Emit stores only the direct member count to avoid
        /// order-dependent miscounting (a child protocol emitted before its parent would see
        /// null for the parent's count). This fixup iterates to a fixed point so that
        /// transitive inheritance chains (Child → Parent → Grandparent) propagate correctly
        /// regardless of declaration order.
        /// </remarks>
        public static void FixupProtocolInheritedRequirements(ModuleDecl moduleDecl, ITypeDatabase typeDatabase)
        {
            // Recursively collect ALL protocol decls (including nested types) and
            // snapshot their direct member counts before any fixup.
            var protocolDecls = new List<(ProtocolDecl decl, int directCount)>();
            CollectProtocolDecls(moduleDecl.Types, protocolDecls, typeDatabase);

            // Iterate to a fixed point: each pass recomputes total = directCount + inherited.
            // A parent updated in one pass may cause its child to update in the next.
            // Worst case is O(depth) passes for a linear chain; typical modules converge in 1-2.
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var (protocolDecl, directCount) in protocolDecls)
                {
                    int inheritedRequirementCount = 0;
                    foreach (var inherited in protocolDecl.InheritedProtocols)
                    {
                        var inheritedTypeName = SwiftTypeName.FromTypeSpec(inherited);
                        if (typeDatabase.TryGetTypeRecord(inheritedTypeName, out var parentRecord)
                            && parentRecord.EmittedMemberCount.HasValue)
                        {
                            inheritedRequirementCount += parentRecord.EmittedMemberCount.Value;
                        }
                    }

                    int totalRequirements = directCount + inheritedRequirementCount;
                    if (typeDatabase.TryGetTypeRecord(protocolDecl.SwiftTypeName, out var currentRecord)
                        && currentRecord.EmittedMemberCount != totalRequirements)
                    {
                        // Finding 47: stamp the inherited-inclusive count via the emission-result path.
                        typeDatabase.ApplyEmissionResult(protocolDecl.SwiftTypeName,
                            new TypeEmissionResult { EmittedMemberCount = totalRequirements });
                        changed = true;
                    }
                }
            }
        }

        /// <summary>
        /// Recursively collects all ProtocolDecl instances from a type hierarchy,
        /// including protocols nested inside structs, classes, and enums.
        /// </summary>
        private static void CollectProtocolDecls(
            IEnumerable<TypeDecl> types,
            List<(ProtocolDecl decl, int directCount)> result,
            ITypeDatabase typeDatabase)
        {
            foreach (var typeDecl in types)
            {
                if (typeDecl is ProtocolDecl protocolDecl)
                {
                    if (typeDatabase.TryGetTypeRecord(protocolDecl.SwiftTypeName, out var record)
                        && record.Kind == TypeRecordKind.Protocol
                        && record.EmittedMemberCount != null)
                    {
                        result.Add((protocolDecl, record.EmittedMemberCount.Value));
                    }
                }

                // Recurse into nested types (structs, classes, enums can all contain protocols)
                if (typeDecl.Types.Count > 0)
                {
                    CollectProtocolDecls(typeDecl.Types, result, typeDatabase);
                }
            }
        }

    }
}
