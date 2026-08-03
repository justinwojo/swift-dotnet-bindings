// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

public partial class ProtocolProxyEmitter
{
    private void EmitStaticFields(CSharpWriter writer, ProtocolDecl protocolDecl)
    {
        var swiftVtableName = GetSwiftVtableStructName(protocolDecl);
        var localVtableName = GetLocalVtableStructName(protocolDecl);

        writer.WriteLines($"""
            private static IntPtr _protocolWitnessTable;
            private static bool _vtableInitialized;
            private static readonly object _vtableLock = new object();

            """);

        // The child's own reverse-dispatch vtable fields are referenced ONLY by
        // EmitChildVtablePopulation (gated on _setVtableEmitted) and are typed by the
        // `{P}SwiftVTable`/`{P}LocalVTable` structs. A read-only proxy emits neither struct
        // (see EmitProxyClass) and never populates these fields, so emitting them would be a
        // dangling reference to a suppressed type. Suppress them in lock-step. The cross-module
        // parent vtable fields are emitted separately (EmitCrossModuleParentScaffolding) and are
        // unaffected.
        if (!_isReadOnlyProxy)
        {
            writer.WriteLines($"""
                private static {swiftVtableName} _swiftVTable;
                private static {localVtableName} _localVTable;
                // Application-lifetime: vtable must outlive all proxy instances. Never disposed.
                private static GCHandle _localVTableHandle;

                """);
        }

        // Finding 33: per-proxy (per-module) EveryProtocol metadata. The opaque existential
        // layout stamps this into ObjectMetadata, and GetTypeMetadata() returns it. Sourced
        // from THIS module's own metadata accessor so that, in a multi-binding app, module B's
        // opaque proxies never read module A's metadata — the failure mode of the old
        // process-global first-wins EveryProtocol latch. Class-bound (EveryObjCProtocol)
        // carriers use a 2-word layout that never consults ObjectMetadata, so they emit none.
        //
        // Read-only (Swift-vended-only) proxies emit none either: their module may export no
        // EveryProtocol scaffolding (zero suitable protocols), so SBW_GetMetadata_EveryProtocol
        // does not exist. An eager field initializer would P/Invoke that missing symbol and throw
        // TypeInitializationException the first time the proxy type is touched — even on the
        // wrap-only path that never needs the helper metadata. GetTypeMetadata() fails clean for
        // these proxies instead (see EmitISwiftObjectImplementation), mirroring GetWitnessTableFromSwift.
        if (!_useObjCBase && UsesEveryProtocolCarrier)
        {
            writer.WriteLines($$"""
                private static readonly TypeMetadata s_everyProtocolMetadata = TypeMetadata.FromHandle(NativeMethods.{{GetMetadataMethodName}}());

                """);
        }
    }

    private void EmitInstanceFields(CSharpWriter writer, ProtocolDecl protocolDecl, string interfaceName)
    {
        // _everyProtocolHandle is a plain IntPtr carrying the Swift EveryProtocol the
        // factory created with a +1 (R0, the construction retain). Under Design B2 that
        // +1 is owned by THIS proxy and released on the proxy's finalizer/Dispose via
        // ProxyLifetimeTracker.ReleaseHandle (gated by _ownsEveryProtocolR0). The impl
        // itself is rooted independently — by a strong GCHandle in ProxyLifetimeTracker
        // keyed on this handle — so reverse dispatch resolves it via ResolveImpl without
        // depending on a live proxy. Swift's deinit callback (OnEveryProtocolDeinit) frees
        // that strong root and drops the (weak) registry entry once Swift's last retain is
        // released. Zero means the proxy was built from an existing Swift-owned container
        // (the Swift-backed ctor), which owns no R0.
        //
        // _csharpImplRef is a WEAK reference to the user's impl, retained only to satisfy
        // the IProtocolProxyImpl<TInterface>.UserImpl contract (covariant cross-module
        // registry lookups). It is NOT the reverse-dispatch resolution path under Design
        // B2 — receivers resolve through ProxyLifetimeTracker.ResolveImpl, whose strong
        // root keeps the impl alive for exactly as long as Swift references the proxy.
        writer.WriteLines($$"""
            private readonly WeakReference<{{interfaceName}}>? _csharpImplRef;

            private {{interfaceName}}? _csharpImpl
            {
                get
                {
                    var weakRef = _csharpImplRef;
                    if (weakRef != null && weakRef.TryGetTarget(out var impl))
                        return impl;
                    return null;
                }
            }

            // Satisfies the IProtocolProxyImpl<TInterface> contract for covariant
            // cross-module registry lookups. Reverse dispatch itself no longer reads this
            // (it resolves through ProxyLifetimeTracker.ResolveImpl); see the field
            // comment above.
            {{interfaceName}}? Swift.Runtime.IProtocolProxyImpl<{{interfaceName}}>.UserImpl => _csharpImpl;

            private readonly IntPtr _everyProtocolHandle;
            private ExistentialContainer1 _swiftContainer;
            private bool _disposed;

            // True only for C#-impl-backed proxies (built by the public {{interfaceName}}-taking
            // ctor): they own the EveryProtocol construction +1 (R0) and release it on
            // Dispose/finalize via ProxyLifetimeTracker.ReleaseHandle. False for Swift-backed
            // proxies (the ExistentialContainer1 ctor), which never created an R0.
            private readonly bool _ownsEveryProtocolR0;

            // True only for proxies that ADOPTED a Swift-returned existential at +1 (the
            // owned-return marshalling paths construct with `ownsContainer: true`). Such a
            // proxy owns the container's value-witness retains and must release them on
            // Dispose/finalize. False for every other construction — C#-impl-backed
            // proxies (R0 owned via ProxyLifetimeTracker), borrowed parameter wraps
            // (ExistentialContainerFactory.GetOrCreate), payload-pointer reads
            // (NewFromPayload), and externally-constructed/synthetic containers — none of
            // which own a value-witness +1, so releasing their (often borrowed or zeroed)
            // container would be a use-after-free / null-metadata crash.
            private readonly bool _ownsContainer;

            """);
    }

    private void EmitStaticConstructor(CSharpWriter writer, ProtocolDecl protocolDecl)
    {
        var proxyClassName = GetProxyClassName(protocolDecl);
        var swiftVtableName = GetSwiftVtableStructName(protocolDecl);
        var localVtableName = GetLocalVtableStructName(protocolDecl);
        var setVtableName = GetSetVtablePInvokeName(protocolDecl);

        writer.WriteLine($"static {proxyClassName}()");
        writer.WriteLine("{");
        writer.Indent++;

        writer.WriteLine("InitializeVtable();");

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();

        // Emit InitializeVtable method
        writer.WriteLine("private static void InitializeVtable()");
        writer.WriteLine("{");
        writer.Indent++;

        writer.WriteLine("lock (_vtableLock)");
        writer.WriteLine("{");
        writer.Indent++;

        writer.WriteLine("if (_vtableInitialized) return;");
        writer.WriteLine();

        // Force ancestor proxy cctors to fire before our own vtable setup. The Swift
        // wrapper's per-protocol `_p_vtable` module-globals are populated only by the
        // ancestor proxy's static ctor (which calls SetP_vtable). When a child protocol
        // is constructed via `new ChildProxy(impl)` — common for inheritance-only
        // protocols like `protocol AdNetworkInitDelegate: SDKInitDelegate {}` — only the
        // child cctor fires; the ancestor cctor never runs and its vtable stays nil.
        // Swift's witness dispatch then forwards inherited requirements through the
        // ancestor's extension body, force-unwrapping the nil function pointer
        // (@objc:NSObject reverse-dispatch crash repro: `_sDKInitDelegate_vtable.func_onInitSuccess_0!`).
        // RunClassConstructor is idempotent and composes transitively because every
        // ancestor proxy's own InitializeVtable also walks its ancestors.
        EmitAncestorProxyCctorInit(writer, protocolDecl);

        // When EveryProtocolEmitter did not emit a Set{Protocol}_vtable Swift trampoline
        // (e.g., empty marker / static-only-requirement / noncopyable protocols), calling
        // it would throw EntryPointNotFoundException. Skip the child's own vtable
        // population — but the cross-module parent vtable init below still runs so an
        // empty child that inherits a cross-module parent (e.g. `protocol Child:
        // OtherModule.Parent {}`) can still receive inherited dispatch through the
        // parent's `_p_vtable` global in the bound module's wrapper.
        // A hollow vtable — every declared requirement present but not one of them reverse-dispatchable
        // — is registered with Swift as an all-null table, and a C# type implementing the interface is
        // then never called. Registering it advertises a readiness that does not exist and pins a
        // GCHandle for a table nothing can dispatch through, so suppress the population and the
        // registration together. Strictly gated on ZERO filled callbacks: one live slot means the
        // interface IS honestly implementable for that member and the table must be registered.
        var fillPlan = ProtocolVtableFillPlanBuilder.Build(
            protocolDecl, _typeDatabase, _skippedMethodKeys, _skippedPropertyNames, _skippedSubscriptIndices);
        var emitChildVtablePopulation = _setVtableEmitted && !fillPlan.IsHollow;
        if (_setVtableEmitted && fillPlan.IsHollow)
        {
            writer.WriteLine($"// No requirement of {protocolDecl.Name} is reverse-dispatchable ({fillPlan.ObligationCount} declared,");
            writer.WriteLine("// 0 callback slots filled), so no vtable is registered with Swift: a C# implementation of");
            writer.WriteLine("// this interface has nothing to be called back through. The Swift→C# wrap path is unaffected.");
        }
        else if (!emitChildVtablePopulation)
        {
            writer.WriteLine("// No Set" + protocolDecl.Name + "_vtable Swift trampoline was emitted for this protocol;");
            writer.WriteLine("// the proxy is read-only for its own surface (Swift→C# wrap path only).");
            // A read-only proxy never reverse-dispatches, so CollectCrossModuleParents returns empty
            // for it and the parent-init loop below emits nothing. A non-read-only proxy that merely
            // lacks its own Set{Protocol}_vtable (empty marker / static-only / noncopyable) still
            // populates its cross-module parents' vtables below for inherited dispatch.
            if (_isReadOnlyProxy)
                writer.WriteLine("// Read-only proxy: cross-module parent vtable init is suppressed too.");
            else
                writer.WriteLine("// Cross-module parent vtable init below still runs so inherited dispatch works.");
        }

        if (emitChildVtablePopulation)
        {
            EmitChildVtablePopulation(writer, fillPlan, swiftVtableName, localVtableName, setVtableName);
        }

        // After registering the child's own vtable, populate each cross-module
        // parent's local vtable in the bound module's wrapper. Swift's witness
        // dispatch for the inherited requirement reads from THIS vtable (not the
        // parent module's), so it must be non-nil before any inherited method
        // can be dispatched on a C# impl of the child interface. Runs even when
        // the child has no own members (empty child inheriting a cross-module
        // parent — the cross-module variant of the issue #40 repro).
        var crossModuleParentsForInit = CollectCrossModuleParents(protocolDecl);
        foreach (var parentDecl in crossModuleParentsForInit)
        {
            EmitCrossModuleParentVtableInit(writer, parentDecl);
        }

        writer.WriteLine("_vtableInitialized = true;");

        writer.Indent--;
        writer.WriteLine("}");

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits the child protocol's OWN local + Swift vtable population and the
    /// SetVtable P/Invoke call. Extracted from <see cref="EmitStaticConstructor"/>
    /// so empty children that exist only to inherit a cross-module parent can skip
    /// this block (no Set{Child}_vtable trampoline is exported) while still running
    /// the cross-module-parent vtable init that follows.
    /// </summary>
    private void EmitChildVtablePopulation(
        CSharpWriter writer,
        ProtocolVtableFillPlan fillPlan,
        string swiftVtableName,
        string localVtableName,
        string setVtableName)
    {
        // Both tables render ONE sequence of filled slot suffixes, computed by
        // ProtocolVtableFillPlanBuilder from the same two gates these loops used to apply inline:
        //   1. LAYOUT — VtableLayoutBuilder.Classify*. Never assign into a slot the struct does not
        //      declare (would reference a missing field), and take the slot INDEX from the shared
        //      model so an assignment can never drift from its struct field (Bug #21).
        //   2. FILLABILITY — the interface-emission skip sets plus the raw/projected-key dedup. A
        //      slot Swift KEEPS but C# can't project (e.g. AnyType-unprojectable) has no Receive_
        //      trampoline, so it is left null; the struct still carries the field.
        // Driving both from one sequence is what makes the Swift-facing mirror unable to claim a
        // pointer the local table left null — it previously omitted the raw-key dedup and would copy
        // an unassigned local field, which reads as a wired slot right up until the null deref.
        writer.WriteLine($"_localVTable = new {localVtableName}");
        writer.WriteLine("{");
        writer.Indent++;
        foreach (var suffix in fillPlan.FilledSlotSuffixes)
            writer.WriteLine($"Func_{suffix} = &Receive_{suffix},");
        writer.Indent--;
        writer.WriteLine("};");
        writer.WriteLine();

        writer.WriteLine("_localVTableHandle = GCHandle.Alloc(_localVTable, GCHandleType.Pinned);");
        writer.WriteLine();

        writer.WriteLine($"_swiftVTable = new {swiftVtableName}");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine("csVTHandle = GCHandle.ToIntPtr(_localVTableHandle),");
        foreach (var suffix in fillPlan.FilledSlotSuffixes)
            writer.WriteLine($"func_{suffix} = (IntPtr)_localVTable.Func_{suffix},");
        writer.Indent--;
        writer.WriteLine("};");
        writer.WriteLine();

        writer.WriteLines($$"""
            fixed ({{swiftVtableName}}* vtPtr = &_swiftVTable)
            {
                NativeMethods.{{setVtableName}}((IntPtr)vtPtr);
            }

            """);
    }

    /// <summary>
    /// Emits RuntimeHelpers.RunClassConstructor calls for every transitively-inherited
    /// same-module ancestor protocol that has its own proxy class. This ensures the
    /// ancestor's static cctor — which calls SetP_vtable to populate the Swift-side
    /// per-protocol module-global vtable — fires before any dispatch path can land
    /// on the ancestor's extension body with a nil function pointer.
    ///
    /// Filtering mirrors <see cref="EmitInheritedInterfaceImplementations"/>:
    /// AnyObject / Sendable / Escapable / Copyable / SendableMetatype are excluded;
    /// PAT / Self-requirement / underscore-suppressed ancestors have no proxy class to
    /// reference; cross-module ancestors are currently skipped (their proxy lives in a
    /// different assembly with no compile-time `typeof` reference). The cross-module
    /// case is a known gap, tracked alongside the broader per-instance-vtable redesign.
    /// </summary>
    private void EmitAncestorProxyCctorInit(CSharpWriter writer, ProtocolDecl protocolDecl)
    {
        if (protocolDecl.InheritedProtocols.Count == 0)
            return;

        var moduleDecl = protocolDecl.ModuleDecl;
        if (moduleDecl == null)
            return;

        var ancestors = CollectAncestorProxiesForCctorInit(protocolDecl);
        if (ancestors.Count == 0)
            return;

        writer.WriteLine("// Ancestor proxy cctors must fire before our own vtable setup so that");
        writer.WriteLine("// the Swift-side per-protocol vtable globals (populated by each ancestor's");
        writer.WriteLine("// SetP_vtable call) are non-nil when inherited requirements dispatch through");
        writer.WriteLine("// the ancestor's EveryProtocol extension body. RunClassConstructor is");
        writer.WriteLine("// idempotent; the chain composes transitively via each ancestor's own");
        writer.WriteLine("// InitializeVtable walking its ancestors.");
        foreach (var ancestor in ancestors)
        {
            var ancestorProxy = GetProxyClassName(ancestor);
            writer.WriteLine(
                $"global::System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof({ancestorProxy}).TypeHandle);");
        }
        writer.WriteLine();
    }

    /// <summary>
    /// Collects same-module ancestor protocols that have an emitted proxy class.
    /// Walks transitively so an ancestor that itself is filtered out (e.g., underscore-
    /// suppressed) doesn't break the chain — grandparents below it are still collected.
    /// </summary>
    private List<ProtocolDecl> CollectAncestorProxiesForCctorInit(ProtocolDecl protocolDecl)
    {
        var moduleDecl = protocolDecl.ModuleDecl!;
        var currentModule = moduleDecl.Name;
        var collected = new List<ProtocolDecl>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<ProtocolDecl>();
        queue.Enqueue(protocolDecl);
        // Mark the starting protocol as visited so its own emitted name isn't re-added.
        visited.Add(protocolDecl.SwiftTypeName?.ModuleQualifiedName ?? protocolDecl.Name);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var inherited in current.InheritedProtocols)
            {
                if (inherited.Name is "Swift.AnyObject" or "AnyObject")
                    continue;
                if (inherited.NameWithoutModule is "Sendable" or "Escapable" or "Copyable" or "SendableMetatype")
                    continue;

                // Cross-module ancestors: their proxy class lives in a different binding
                // assembly. We can't reference it via `typeof` at compile time without
                // cross-assembly using declarations / strong-named refs that the generator
                // does not currently produce. Document as a known gap (see method summary).
                var inheritedModule = inherited.Module;
                if (!string.IsNullOrEmpty(inheritedModule) && !string.IsNullOrEmpty(currentModule) &&
                    inheritedModule != currentModule)
                    continue;

                var key = inherited switch
                {
                    NamedTypeSpec nts => nts.ToString(),
                    _ => inherited.Name,
                };
                if (!visited.Add(key))
                    continue;

                var ancestorDecl = moduleDecl.Protocols.FirstOrDefault(p => p.Name == inherited.NameWithoutModule);
                if (ancestorDecl == null)
                    continue;

                // PAT / Self / underscore-suppressed ancestors do not get a proxy class
                // emitted, so there is nothing to call RunClassConstructor on. Continue
                // walking transitively in case their grandparents do have proxies.
                var ancestorSwiftName = SwiftTypeName.FromTypeSpec(inherited);
                if (_typeDatabase.TryGetTypeRecord(ancestorSwiftName, out var ancestorRecord))
                {
                    if (ancestorRecord.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes) ||
                        ancestorRecord.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement))
                    {
                        queue.Enqueue(ancestorDecl);
                        continue;
                    }
                }
                if (ancestorSwiftName != null && _emissionContext.IsUnderscoreSuppressed(ancestorSwiftName.ToString()))
                {
                    queue.Enqueue(ancestorDecl);
                    continue;
                }

                // Ancestor conformance was skipped (e.g. static-only requirements, hidden
                // requirements, class-bound + non-NSObjectProtocol-only, etc.) so the
                // ancestor's C# proxy class was suppressed by ProtocolHandler — there is no
                // `XProxy` type to reference via `typeof`. ConformanceDecisions is populated
                // by EveryProtocolEmitter BEFORE any proxy emission begins (ModuleHandler.
                // EmitEveryProtocolConformances precedes ProtocolHandler), so this check is
                // order-independent even when the ancestor's proxy is processed AFTER ours.
                // Walk transitively so grandparents whose proxies WERE emitted still get a
                // cctor call.
                // Keyed on the module-qualified name (matching RecordConformanceDecision). The
                // ancestor is always resolved from the LOCAL module here (cross-module ancestors
                // are skipped above), so its qualified key matches what the recorder used.
                //
                // Mirror ProtocolProxyEmissionPolicy.Decide's non-read-only arm exactly so this cctor
                // never references an ancestor proxy the policy suppressed: when no EveryProtocol carrier
                // was emitted, every non-read-only proxy (including this ancestor's) is suppressed, so
                // there is no `{Ancestor}Proxy` type to RunClassConstructor on; and even with a carrier,
                // skip an ancestor whose own conformance was not emitted. Walk transitively. No
                // ConformanceDecisions.Count term is needed — when the carrier IS emitted the count is
                // always non-zero, so it was redundant, and dropping it keeps this predicate byte-for-byte
                // identical to Decide's (the four decision sites share one signal). This guard is
                // load-bearing, NOT pure defence-in-depth: a read-only proxy DOES emit its own static
                // init with no carrier and still walks its ancestors here, so the `!carrier` arm is what
                // stops it RunClassConstructor-ing an ancestor proxy that was suppressed.
                if (!_emissionContext.WasEveryProtocolCarrierEmitted ||
                    !_emissionContext.WasConformanceEmitted(ancestorDecl.SwiftTypeName?.ModuleQualifiedName ?? ancestorDecl.Name))
                {
                    queue.Enqueue(ancestorDecl);
                    continue;
                }

                collected.Add(ancestorDecl);
                queue.Enqueue(ancestorDecl);
            }
        }
        return collected;
    }

    private void EmitLocalVtablePropertyAssignment(CSharpWriter writer, PropertyDecl property, HashSet<string> emitted)
    {
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();

        if (hasGetter)
        {
            var key = $"Func_{property.Name}_get";
            if (emitted.Add(key))
                writer.WriteLine($"{key} = &Receive_{property.Name}_get,");
        }
        if (hasSetter)
        {
            var key = $"Func_{property.Name}_set";
            if (emitted.Add(key))
                writer.WriteLine($"{key} = &Receive_{property.Name}_set,");
        }
    }

    private void EmitLocalVtableSubscriptAssignment(CSharpWriter writer, SubscriptDecl subscript, int index)
    {
        if (subscript.HasGetter)
        {
            writer.WriteLine($"Func_subscript_{index}_get = &Receive_subscript_{index}_get,");
        }
        if (subscript.HasSetter)
        {
            writer.WriteLine($"Func_subscript_{index}_set = &Receive_subscript_{index}_set,");
        }
    }

    private void EmitLocalVtableMethodAssignment(CSharpWriter writer, MethodDecl method, int index)
    {
        writer.WriteLine($"Func_{method.Name}_{index} = &Receive_{method.Name}_{index},");
    }

    private void EmitSwiftVtablePropertyAssignment(CSharpWriter writer, PropertyDecl property, HashSet<string> emitted, string localVtableFieldName = "_localVTable")
    {
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();

        if (hasGetter)
        {
            var key = $"func_{property.Name}_get";
            if (emitted.Add(key))
                writer.WriteLine($"{key} = (IntPtr){localVtableFieldName}.Func_{property.Name}_get,");
        }
        if (hasSetter)
        {
            var key = $"func_{property.Name}_set";
            if (emitted.Add(key))
                writer.WriteLine($"{key} = (IntPtr){localVtableFieldName}.Func_{property.Name}_set,");
        }
    }

    private void EmitSwiftVtableSubscriptAssignment(CSharpWriter writer, SubscriptDecl subscript, int index, string localVtableFieldName = "_localVTable")
    {
        if (subscript.HasGetter)
        {
            writer.WriteLine($"func_subscript_{index}_get = (IntPtr){localVtableFieldName}.Func_subscript_{index}_get,");
        }
        if (subscript.HasSetter)
        {
            writer.WriteLine($"func_subscript_{index}_set = (IntPtr){localVtableFieldName}.Func_subscript_{index}_set,");
        }
    }

    private void EmitSwiftVtableMethodAssignment(CSharpWriter writer, MethodDecl method, int index, string localVtableFieldName = "_localVTable")
    {
        writer.WriteLine($"func_{method.Name}_{index} = (IntPtr){localVtableFieldName}.Func_{method.Name}_{index},");
    }

    /// <summary>
    /// Emits the population block for a cross-module parent's vtable inside the
    /// child proxy's <c>InitializeVtable()</c>. Mirrors the child's own vtable
    /// population: build local vtable holding receiver function pointers, pin it,
    /// build Swift-side vtable referencing the pinned local, and call the bound
    /// module's <c>Set{Parent}_vtable</c> P/Invoke to register it with the parent's
    /// per-module <c>_p_vtable</c> global in module B's wrapper.
    /// </summary>
    private void EmitCrossModuleParentVtableInit(CSharpWriter writer, ProtocolDecl parentDecl)
    {
        var localVtableField = GetCrossModuleParentLocalVtableFieldName(parentDecl);
        var swiftVtableField = GetCrossModuleParentSwiftVtableFieldName(parentDecl);
        var localVtableHandleField = GetCrossModuleParentLocalVtableHandleFieldName(parentDecl);
        var localVtableStruct = GetLocalVtableStructName(parentDecl);
        var swiftVtableStruct = GetSwiftVtableStructName(parentDecl);
        var nativeMethodsClass = GetCrossModuleParentNativeMethodsClassName(parentDecl);
        var setVtableName = GetSetVtablePInvokeName(parentDecl);

        writer.WriteLine($"// Cross-module parent vtable: {parentDecl.ModuleDecl?.Name}.{parentDecl.Name}");
        writer.WriteLine($"{localVtableField} = new {localVtableStruct}");
        writer.WriteLine("{");
        writer.Indent++;

        // ProtocolVtableMembers is the single source of truth for "is this slot present?";
        // matches the predicates EveryProtocolEmitter applies on the Swift side so the C#
        // assignment list lines up with the struct definition emitted by
        // EmitSwiftVtableStruct/EmitLocalVtableStruct (called with applyVtableMembershipFilter: true).
        var vtableClosureHandler = new ClosureHandler(_typeDatabase);

        var emittedLocalAssignments = new HashSet<string>();
        foreach (var property in parentDecl.Properties)
        {
            if (!ProtocolVtableMembers.IncludesProperty(property, parentDecl, vtableClosureHandler)) continue;
            EmitLocalVtablePropertyAssignment(writer, property, emittedLocalAssignments);
        }
        int subscriptIndex = 0;
        foreach (var subscript in parentDecl.Subscripts)
        {
            // Mirror Vtables.cs / Receivers.cs: static subscripts are dropped
            // entirely without consuming a slot index. Filter-excluded
            // instance subscripts still consume the index so the next
            // dispatchable instance subscript lands at the slot the struct
            // emitter assigned to it.
            if (subscript.IsStatic) continue;
            if (!ProtocolVtableMembers.IncludesSubscript(subscript, parentDecl, vtableClosureHandler)) { subscriptIndex++; continue; }
            EmitLocalVtableSubscriptAssignment(writer, subscript, subscriptIndex);
            subscriptIndex++;
        }
        // Slot INDEX comes from the shared VtableLayout model (the SAME ordered list the struct in
        // Vtables.cs renders), so an assignment's index can never drift from its struct field (Bug #21).
        // Cross-module parents have empty skip sets, so IncludesMethod alone drives layout here; both
        // the local and swift-facing loops reuse this one map (identical indices by construction).
        var methodSlotIndices = new VtableLayoutBuilder(_typeDatabase).Build(parentDecl).MethodSlotIndexByKey;
        var methodIndices = new Dictionary<string, int>();
        var emittedRawKeys = new HashSet<string>();
        var emittedCSharpKeys = new HashSet<string>();
        foreach (var method in parentDecl.Methods)
        {
            // Mirror Vtables.cs / Receivers.cs / EveryProtocolEmitter: constructors,
            // statics, and @objc-optional methods skip BEFORE the index/key block so they
            // do NOT consume a slot (the Swift producer skips optional before its increment).
            // The filter-excluded dispatchability cases (closure, generic, Self-typed,
            // mixed-generic protocol) DO consume the index — IncludesMethod drops their
            // emission AFTER the increment, matching the producer, so the dispatchable
            // method that follows lands at the slot the struct field carries.
            if (method.IsConstructor || method.MethodType == MethodType.Static) continue;
            if (method.IsObjCOptional) continue;
            // Index on the RAW producer key (matches Vtables.cs / EveryProtocolEmitter); the
            // projected key is fillability-only. Cross-module parents have empty skip sets
            // (reset in EmitCrossModuleParentScaffolding), so IncludesMethod alone drives layout.
            var slotKey = EveryProtocolEmitter.GetMethodKey(method);
            if (!methodIndices.ContainsKey(slotKey))
            {
                var idx = methodSlotIndices[slotKey];
                methodIndices[slotKey] = idx;
                if (!ProtocolVtableMembers.IncludesMethod(method, parentDecl, vtableClosureHandler)) continue;
                // RAW-SIGNATURE DEDUP: mirror the receiver loop (EmitReceiverMethods, shared with
                // this cross-module path via applyVtableMembershipFilter) and the child local-vtable
                // loop. An existential-overload pair that collapses to one raw key but projects to
                // distinct C# keys gets ONE receiver (the first survivor); without this guard the
                // local-vtable initializer would emit `Func_X_{idx} = &Receive_X_{idx}` for the
                // second overload — an orphan reference to a receiver the guarded loop never emitted
                // (CS0103). The slot's struct field still exists (keyed on the raw producer key) and
                // is left at IntPtr.Zero, matching the documented fillability model.
                var collapsingKey = ProtocolMethodDisambiguator.EffectiveRawKey(method, parentDecl, _typeDatabase);
                if (!emittedRawKeys.Add(collapsingKey)) continue;
                var projectedKey = ProtocolMethodDisambiguator.EffectiveProjectedKey(method, parentDecl, _typeDatabase, propertyNames: null);
                if (!emittedCSharpKeys.Add(projectedKey)) continue;
                EmitLocalVtableMethodAssignment(writer, method, idx);
            }
        }

        writer.Indent--;
        writer.WriteLine("};");
        writer.WriteLine($"{localVtableHandleField} = GCHandle.Alloc({localVtableField}, GCHandleType.Pinned);");
        writer.WriteLine();

        writer.WriteLine($"{swiftVtableField} = new {swiftVtableStruct}");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine($"csVTHandle = GCHandle.ToIntPtr({localVtableHandleField}),");

        var emittedSwiftAssignments = new HashSet<string>();
        foreach (var property in parentDecl.Properties)
        {
            if (!ProtocolVtableMembers.IncludesProperty(property, parentDecl, vtableClosureHandler)) continue;
            EmitSwiftVtablePropertyAssignment(writer, property, emittedSwiftAssignments, localVtableField);
        }
        subscriptIndex = 0;
        foreach (var subscript in parentDecl.Subscripts)
        {
            // See the local-vtable loop above for the static-vs-filtered
            // index-consumption rationale.
            if (subscript.IsStatic) continue;
            if (!ProtocolVtableMembers.IncludesSubscript(subscript, parentDecl, vtableClosureHandler)) { subscriptIndex++; continue; }
            EmitSwiftVtableSubscriptAssignment(writer, subscript, subscriptIndex, localVtableField);
            subscriptIndex++;
        }
        methodIndices.Clear();
        emittedCSharpKeys.Clear();
        foreach (var method in parentDecl.Methods)
        {
            // See the local-vtable loop above for the early-continue
            // rationale: constructor/static/@objc-optional methods are
            // dropped without consuming a slot, matching Vtables.cs /
            // Receivers.cs / EveryProtocolEmitter.
            if (method.IsConstructor || method.MethodType == MethodType.Static) continue;
            if (method.IsObjCOptional) continue;
            // Index on the RAW producer key (matches Vtables.cs / EveryProtocolEmitter); see the
            // local-vtable loop above.
            var slotKey = EveryProtocolEmitter.GetMethodKey(method);
            if (!methodIndices.ContainsKey(slotKey))
            {
                var idx = methodSlotIndices[slotKey];
                methodIndices[slotKey] = idx;
                if (!ProtocolVtableMembers.IncludesMethod(method, parentDecl, vtableClosureHandler)) continue;
                var projectedKey = ProtocolMethodDisambiguator.EffectiveProjectedKey(method, parentDecl, _typeDatabase, propertyNames: null);
                if (!emittedCSharpKeys.Add(projectedKey)) continue;
                EmitSwiftVtableMethodAssignment(writer, method, idx, localVtableField);
            }
        }

        writer.Indent--;
        writer.WriteLine("};");
        writer.WriteLine();

        writer.WriteLines($$"""
            fixed ({{swiftVtableStruct}}* vtPtr = &{{swiftVtableField}})
            {
                {{nativeMethodsClass}}.{{setVtableName}}((IntPtr)vtPtr);
            }

            """);
    }
}
