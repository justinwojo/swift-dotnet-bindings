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

        // When EveryProtocolEmitter did not emit a Set{Protocol}_vtable Swift trampoline
        // (e.g., empty marker / static-only-requirement / noncopyable protocols), calling
        // it would throw EntryPointNotFoundException. Skip the entire vtable population:
        // for these protocols the proxy is only used to wrap Swift-side existential
        // containers (read-only), and instance dispatch goes through _swiftContainer's
        // witness table, not the local vtable. C# impls of the protocol won't dispatch
        // back into Swift, but those are not produced for the protocol shapes that fall
        // into this gate. See bug-0.10.0-proxy-vtable-setters-not-exported.md.
        if (!_setVtableEmitted)
        {
            writer.WriteLine("// No Set" + protocolDecl.Name + "_vtable Swift trampoline was emitted for this protocol;");
            writer.WriteLine("// the proxy is read-only (Swift→C# wrap path only). Skip vtable initialisation.");
            writer.WriteLine("_vtableInitialized = true;");
            writer.Indent--;
            writer.WriteLine("}");
            writer.Indent--;
            writer.WriteLine("}");
            writer.WriteLine();
            return;
        }

        // Build local vtable with receiver function pointers
        writer.WriteLine($"_localVTable = new {localVtableName}");
        writer.WriteLine("{");
        writer.Indent++;

        // Track emitted assignments to prevent duplicates
        var emittedLocalAssignments = new HashSet<string>();

        // Property receivers (skip static properties)
        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic)
                continue;
            // Skip assignment for properties that the interface skipped (field stays default/null)
            if (_skippedPropertyNames.Contains(property.Name))
                continue;
            EmitLocalVtablePropertyAssignment(writer, property, emittedLocalAssignments);
        }

        // Subscript receivers (skip static subscripts)
        int subscriptIndex = 0;
        var emittedSubscripts = new HashSet<string>();
        foreach (var subscript in protocolDecl.Subscripts)
        {
            if (subscript.IsStatic)
                continue;
            var subscriptKey = $"subscript_{subscriptIndex}";
            if (emittedSubscripts.Add(subscriptKey))
            {
                // Skip assignment for subscripts that the interface skipped (field stays default/null)
                if (!_skippedSubscriptIndices.Contains(subscriptIndex))
                    EmitLocalVtableSubscriptAssignment(writer, subscript, subscriptIndex);
            }
            subscriptIndex++;
        }

        // Method receivers
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
                {
                    // Closure-skipped methods have no receiver and no local-vtable field,
                    // so there's nothing to assign here. See ProtocolProxyEmitter.Vtables.cs
                    // (EmitLocalVtableStruct + EmitSwiftVtableStruct) for the matching omissions.
                    continue;
                }
                var projectedKey = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(method, _typeDatabase, protocolDecl);
                if (!emittedCSharpKeys.Add(projectedKey))
                    continue;
                EmitLocalVtableMethodAssignment(writer, method, idx);
            }
        }

        writer.Indent--;
        writer.WriteLine("};");
        writer.WriteLine();

        // Pin the local vtable
        writer.WriteLine("_localVTableHandle = GCHandle.Alloc(_localVTable, GCHandleType.Pinned);");
        writer.WriteLine();

        // Build Swift vtable
        writer.WriteLine($"_swiftVTable = new {swiftVtableName}");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine("csVTHandle = GCHandle.ToIntPtr(_localVTableHandle),");

        // Track emitted Swift vtable assignments
        var emittedSwiftAssignments = new HashSet<string>();

        // Property function pointers (skip static properties)
        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic)
                continue;
            // Skip assignment for properties that the interface skipped (field stays IntPtr.Zero)
            if (_skippedPropertyNames.Contains(property.Name))
                continue;
            EmitSwiftVtablePropertyAssignment(writer, property, emittedSwiftAssignments);
        }

        // Subscript function pointers (skip static subscripts)
        subscriptIndex = 0;
        emittedSubscripts.Clear();
        foreach (var subscript in protocolDecl.Subscripts)
        {
            if (subscript.IsStatic)
                continue;
            var subscriptKey = $"subscript_{subscriptIndex}";
            if (emittedSubscripts.Add(subscriptKey))
            {
                // Skip assignment for subscripts that the interface skipped (field stays IntPtr.Zero)
                if (!_skippedSubscriptIndices.Contains(subscriptIndex))
                    EmitSwiftVtableSubscriptAssignment(writer, subscript, subscriptIndex);
            }
            subscriptIndex++;
        }

        // Method function pointers
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
                {
                    // Non-dispatchable closure methods get fatalError stubs in Swift's
                    // EveryProtocol extension; their slot is omitted from Swift's vtable
                    // struct entirely (see EveryProtocolEmitter.EmitProtocolVtableStruct).
                    // Writing an assignment here would target a slot that doesn't exist
                    // on the Swift side and corrupt the adjacent function pointer.
                    continue;
                }
                var projectedKey = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(method, _typeDatabase, protocolDecl);
                if (!emittedCSharpKeys.Add(projectedKey))
                    continue;
                EmitSwiftVtableMethodAssignment(writer, method, idx);
            }
        }

        writer.Indent--;
        writer.WriteLine("};");
        writer.WriteLine();

        // Call Swift's SetVtable
        writer.WriteLines($$"""
            fixed ({{swiftVtableName}}* vtPtr = &_swiftVTable)
            {
                NativeMethods.{{setVtableName}}((IntPtr)vtPtr);
            }

            _vtableInitialized = true;
            """);

        writer.Indent--;
        writer.WriteLine("}");

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
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

    private void EmitSwiftVtablePropertyAssignment(CSharpWriter writer, PropertyDecl property, HashSet<string> emitted)
    {
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();

        if (hasGetter)
        {
            var key = $"func_{property.Name}_get";
            if (emitted.Add(key))
                writer.WriteLine($"{key} = (IntPtr)_localVTable.Func_{property.Name}_get,");
        }
        if (hasSetter)
        {
            var key = $"func_{property.Name}_set";
            if (emitted.Add(key))
                writer.WriteLine($"{key} = (IntPtr)_localVTable.Func_{property.Name}_set,");
        }
    }

    private void EmitSwiftVtableSubscriptAssignment(CSharpWriter writer, SubscriptDecl subscript, int index)
    {
        if (subscript.HasGetter)
        {
            writer.WriteLine($"func_subscript_{index}_get = (IntPtr)_localVTable.Func_subscript_{index}_get,");
        }
        if (subscript.HasSetter)
        {
            writer.WriteLine($"func_subscript_{index}_set = (IntPtr)_localVTable.Func_subscript_{index}_set,");
        }
    }

    private void EmitSwiftVtableMethodAssignment(CSharpWriter writer, MethodDecl method, int index)
    {
        writer.WriteLine($"func_{method.Name}_{index} = (IntPtr)_localVTable.Func_{method.Name}_{index},");
    }
}
