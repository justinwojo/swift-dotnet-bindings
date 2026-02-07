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
            private static GCHandle _localVTableHandle;
            private static bool _vtableInitialized;
            private static readonly object _vtableLock = new object();

            """);
    }

    private void EmitInstanceFields(CSharpWriter writer, ProtocolDecl protocolDecl, string interfaceName)
    {
        writer.WriteLines($"""
            private readonly {interfaceName}? _csharpImpl;
            private readonly EveryProtocol? _everyProtocol;
            private ExistentialContainer1 _swiftContainer;

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
                EmitLocalVtableSubscriptAssignment(writer, subscript, subscriptIndex);
            }
            subscriptIndex++;
        }

        // Method receivers
        int methodIndex = 0;
        var methodIndices = new Dictionary<string, int>();
        foreach (var method in protocolDecl.Methods)
        {
            if (method.IsConstructor || method.MethodType == MethodType.Static)
                continue;

            var methodKey = GetMethodKey(method);
            if (!methodIndices.ContainsKey(methodKey))
            {
                var idx = methodIndex++;
                methodIndices[methodKey] = idx;
                // Skip assignment for methods that the interface skipped (field stays default/null)
                if (_skippedMethodKeys.Contains(methodKey))
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
                EmitSwiftVtableSubscriptAssignment(writer, subscript, subscriptIndex);
            }
            subscriptIndex++;
        }

        // Method function pointers
        methodIndex = 0;
        methodIndices.Clear();
        foreach (var method in protocolDecl.Methods)
        {
            if (method.IsConstructor || method.MethodType == MethodType.Static)
                continue;

            var methodKey = GetMethodKey(method);
            if (!methodIndices.ContainsKey(methodKey))
            {
                var idx = methodIndex++;
                methodIndices[methodKey] = idx;
                // Skip assignment for methods that the interface skipped (field stays IntPtr.Zero)
                if (_skippedMethodKeys.Contains(methodKey))
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
