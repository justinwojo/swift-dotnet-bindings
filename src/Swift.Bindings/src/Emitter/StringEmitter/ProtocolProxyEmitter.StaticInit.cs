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
            private static {swiftVtableName} _swiftVTable;
            private static {localVtableName} _localVTable;
            // Application-lifetime: vtable must outlive all proxy instances. Never disposed.
            private static GCHandle _localVTableHandle;
            private static bool _vtableInitialized;
            private static readonly object _vtableLock = new object();

            """);
    }

    private void EmitInstanceFields(CSharpWriter writer, ProtocolDecl protocolDecl, string interfaceName)
    {
        // _everyProtocolHandle is a plain IntPtr — the Swift +1 retain is owned by
        // ProxyLifetimeTracker (anchored to the user's impl via a
        // ConditionalWeakTable), not by this proxy. The tracker releases the +1
        // when the impl is garbage-collected; the Swift-side deinit callback
        // (OnEveryProtocolDeinit) drops the strong registry root when Swift's
        // last retain is released. Zero means the proxy was built from an
        // existing Swift-owned container (the Swift-backed ctor).
        //
        // _csharpImpl is a WEAK reference to the user's impl. This is load-bearing:
        // the proxy is strongly rooted by SwiftObjectRegistry for the lifetime of
        // the Swift existential container, so a strong _csharpImpl would root the
        // impl transitively and the tracker's impl-GC trigger would never fire.
        // The _csharpImpl property unwraps the weak ref on each access; all the
        // generator's receiver/interface-impl emit sites continue to use the
        // member access syntax unchanged.
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

            // Common entry-point for receivers to access the user-supplied C# impl across
            // sibling proxy types. Receivers look up by `IProtocolProxyImpl<TInterface>`
            // instead of the specific proxy class, so an inherited-protocol callback
            // (e.g. KidozInitDelegate inheriting onInitSuccess() from SDKInitDelegate)
            // reaches the registered child proxy's user impl through covariance.
            {{interfaceName}}? Swift.Runtime.IProtocolProxyImpl<{{interfaceName}}>.UserImpl => _csharpImpl;

            private readonly IntPtr _everyProtocolHandle;
            private ExistentialContainer1 _swiftContainer;
            private bool _disposed;

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
        // protocols like `protocol KidozInitDelegate: SDKInitDelegate {}` — only the
        // child cctor fires; the ancestor cctor never runs and its vtable stays nil.
        // Swift's witness dispatch then forwards inherited requirements through the
        // ancestor's extension body, force-unwrapping the nil function pointer
        // (Kidoz crash repro: `_sDKInitDelegate_vtable.func_onInitSuccess_0!`).
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
        // See bug-0.10.0-proxy-vtable-setters-not-exported.md.
        var emitChildVtablePopulation = _setVtableEmitted;
        if (!emitChildVtablePopulation)
        {
            writer.WriteLine("// No Set" + protocolDecl.Name + "_vtable Swift trampoline was emitted for this protocol;");
            writer.WriteLine("// the proxy is read-only for its own surface (Swift→C# wrap path only).");
            writer.WriteLine("// Cross-module parent vtable init below still runs so inherited dispatch works.");
        }

        if (emitChildVtablePopulation)
        {
            EmitChildVtablePopulation(writer, protocolDecl, swiftVtableName, localVtableName, setVtableName);
        }

        // After registering the child's own vtable, populate each cross-module
        // parent's local vtable in the bound module's wrapper. Swift's witness
        // dispatch for the inherited requirement reads from THIS vtable (not the
        // parent module's), so it must be non-nil before any inherited method
        // can be dispatched on a C# impl of the child interface. Runs even when
        // the child has no own members (empty child inheriting a cross-module
        // parent — the Kidoz repro's cross-module variant).
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
        ProtocolDecl protocolDecl,
        string swiftVtableName,
        string localVtableName,
        string setVtableName)
    {
        // Build local vtable with receiver function pointers
        writer.WriteLine($"_localVTable = new {localVtableName}");
        writer.WriteLine("{");
        writer.Indent++;

        var emittedLocalAssignments = new HashSet<string>();

        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic)
                continue;
            if (_skippedPropertyNames.Contains(property.Name))
                continue;
            EmitLocalVtablePropertyAssignment(writer, property, emittedLocalAssignments);
        }

        int subscriptIndex = 0;
        var emittedSubscripts = new HashSet<string>();
        foreach (var subscript in protocolDecl.Subscripts)
        {
            if (subscript.IsStatic)
                continue;
            var subscriptKey = $"subscript_{subscriptIndex}";
            if (emittedSubscripts.Add(subscriptKey))
            {
                if (!_skippedSubscriptIndices.Contains(subscriptIndex))
                    EmitLocalVtableSubscriptAssignment(writer, subscript, subscriptIndex);
            }
            subscriptIndex++;
        }

        int methodIndex = 0;
        var methodIndices = new Dictionary<string, int>();
        var emittedCSharpKeys = new HashSet<string>();
        foreach (var method in protocolDecl.Methods)
        {
            if (method.IsConstructor || method.MethodType == MethodType.Static)
                continue;

            var methodKey = ProtocolSignatureHelper.GetMethodSignatureKey(method, _typeDatabase, protocolDecl);
            if (!methodIndices.ContainsKey(methodKey))
            {
                var idx = methodIndex++;
                methodIndices[methodKey] = idx;
                if (_skippedMethodKeys.Contains(methodKey))
                    continue;
                var projectedKey = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(method, _typeDatabase, protocolDecl);
                if (!emittedCSharpKeys.Add(projectedKey))
                    continue;
                EmitLocalVtableMethodAssignment(writer, method, idx);
            }
        }

        writer.Indent--;
        writer.WriteLine("};");
        writer.WriteLine();

        writer.WriteLine("_localVTableHandle = GCHandle.Alloc(_localVTable, GCHandleType.Pinned);");
        writer.WriteLine();

        writer.WriteLine($"_swiftVTable = new {swiftVtableName}");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine("csVTHandle = GCHandle.ToIntPtr(_localVTableHandle),");

        var emittedSwiftAssignments = new HashSet<string>();

        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic)
                continue;
            if (_skippedPropertyNames.Contains(property.Name))
                continue;
            EmitSwiftVtablePropertyAssignment(writer, property, emittedSwiftAssignments);
        }

        subscriptIndex = 0;
        emittedSubscripts.Clear();
        foreach (var subscript in protocolDecl.Subscripts)
        {
            if (subscript.IsStatic)
                continue;
            var subscriptKey = $"subscript_{subscriptIndex}";
            if (emittedSubscripts.Add(subscriptKey))
            {
                if (!_skippedSubscriptIndices.Contains(subscriptIndex))
                    EmitSwiftVtableSubscriptAssignment(writer, subscript, subscriptIndex);
            }
            subscriptIndex++;
        }

        methodIndex = 0;
        methodIndices.Clear();
        emittedCSharpKeys.Clear();
        foreach (var method in protocolDecl.Methods)
        {
            if (method.IsConstructor || method.MethodType == MethodType.Static)
                continue;

            var methodKey = ProtocolSignatureHelper.GetMethodSignatureKey(method, _typeDatabase, protocolDecl);
            if (!methodIndices.ContainsKey(methodKey))
            {
                var idx = methodIndex++;
                methodIndices[methodKey] = idx;
                if (_skippedMethodKeys.Contains(methodKey))
                    continue;
                var projectedKey = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(method, _typeDatabase, protocolDecl);
                if (!emittedCSharpKeys.Add(projectedKey))
                    continue;
                EmitSwiftVtableMethodAssignment(writer, method, idx);
            }
        }

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
                if (_emissionContext.ConformanceDecisions.Count > 0 &&
                    !_emissionContext.WasConformanceEmitted(ancestorDecl.Name))
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
            if (!ProtocolVtableMembers.IncludesSubscript(subscript, parentDecl)) { subscriptIndex++; continue; }
            EmitLocalVtableSubscriptAssignment(writer, subscript, subscriptIndex);
            subscriptIndex++;
        }
        int methodIndex = 0;
        var methodIndices = new Dictionary<string, int>();
        var emittedCSharpKeys = new HashSet<string>();
        foreach (var method in parentDecl.Methods)
        {
            // Mirror Vtables.cs / Receivers.cs: constructors and statics
            // skip BEFORE the index/key block so they don't consume a slot.
            // ObjC-optional and the filter-excluded dispatchability cases
            // (closure, generic, Self-typed, mixed-generic protocol) still
            // consume the index — the C# vtable struct emitter does the
            // same so the dispatchable method that follows lands at the
            // slot the struct field carries.
            if (method.IsConstructor || method.MethodType == MethodType.Static) continue;
            var methodKey = ProtocolSignatureHelper.GetMethodSignatureKey(method, _typeDatabase, parentDecl);
            if (!methodIndices.ContainsKey(methodKey))
            {
                var idx = methodIndex++;
                methodIndices[methodKey] = idx;
                if (!ProtocolVtableMembers.IncludesMethod(method, parentDecl, vtableClosureHandler)) continue;
                var projectedKey = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(method, _typeDatabase, parentDecl);
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
            if (!ProtocolVtableMembers.IncludesSubscript(subscript, parentDecl)) { subscriptIndex++; continue; }
            EmitSwiftVtableSubscriptAssignment(writer, subscript, subscriptIndex, localVtableField);
            subscriptIndex++;
        }
        methodIndex = 0;
        methodIndices.Clear();
        emittedCSharpKeys.Clear();
        foreach (var method in parentDecl.Methods)
        {
            // See the local-vtable loop above for the early-continue
            // rationale: constructor/static/ObjC-optional methods are
            // dropped without consuming a slot, matching Vtables.cs /
            // Receivers.cs / EveryProtocolEmitter.
            if (method.IsConstructor || method.MethodType == MethodType.Static) continue;
            var methodKey = ProtocolSignatureHelper.GetMethodSignatureKey(method, _typeDatabase, parentDecl);
            if (!methodIndices.ContainsKey(methodKey))
            {
                var idx = methodIndex++;
                methodIndices[methodKey] = idx;
                if (!ProtocolVtableMembers.IncludesMethod(method, parentDecl, vtableClosureHandler)) continue;
                var projectedKey = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(method, _typeDatabase, parentDecl);
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
