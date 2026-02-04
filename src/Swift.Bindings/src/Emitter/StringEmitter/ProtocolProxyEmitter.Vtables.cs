// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

public partial class ProtocolProxyEmitter
{
    /// <summary>
    /// Emits the struct that matches the Swift vtable layout.
    /// This is passed to Swift's SetVtable function.
    /// </summary>
    private void EmitSwiftVtableStruct(CSharpWriter writer, ProtocolDecl protocolDecl)
    {
        var structName = GetSwiftVtableStructName(protocolDecl);

        writer.WriteLine($"/// <summary>Matches Swift {protocolDecl.Name}_vtable layout</summary>");
        writer.WriteLine("[StructLayout(LayoutKind.Sequential)]");
        writer.WriteLine($"private struct {structName}");
        writer.WriteLine("{");
        writer.Indent++;

        writer.WriteLine("public IntPtr csVTHandle;");

        // Track emitted fields to avoid duplicates
        var emittedFields = new HashSet<string>();

        // Property fields (skip static properties - they're not part of the vtable)
        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic)
                continue;
            EmitPropertyVtableSwiftFields(writer, property, emittedFields);
        }

        // Subscript fields (skip static subscripts - they're not part of the vtable)
        int subscriptIndex = 0;
        foreach (var subscript in protocolDecl.Subscripts)
        {
            if (subscript.IsStatic)
                continue;
            EmitSubscriptVtableSwiftFields(writer, subscript, subscriptIndex, emittedFields);
            subscriptIndex++;
        }

        // Method fields
        int methodIndex = 0;
        var methodIndices = new Dictionary<string, int>();
        foreach (var method in protocolDecl.Methods)
        {
            if (method.IsConstructor || method.MethodType == MethodType.Static)
                continue;

            var methodKey = GetMethodKey(method);
            if (!methodIndices.TryGetValue(methodKey, out var idx))
            {
                idx = methodIndex++;
                methodIndices[methodKey] = idx;
                // Only emit the field for new methods
                EmitMethodVtableSwiftField(writer, method, idx, emittedFields);
            }
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits the local vtable struct that holds managed delegates.
    /// </summary>
    private void EmitLocalVtableStruct(CSharpWriter writer, ProtocolDecl protocolDecl)
    {
        var structName = GetLocalVtableStructName(protocolDecl);

        writer.WriteLine($"/// <summary>Local vtable holding managed delegates</summary>");
        writer.WriteLine($"private struct {structName}");
        writer.WriteLine("{");
        writer.Indent++;

        // Property delegates - track emitted to avoid duplicates (skip static properties)
        var emittedFields = new HashSet<string>();
        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic)
                continue;
            EmitPropertyLocalVtableFields(writer, property, protocolDecl, emittedFields);
        }

        // Subscript delegates (skip static subscripts)
        int subscriptIndex = 0;
        foreach (var subscript in protocolDecl.Subscripts)
        {
            if (subscript.IsStatic)
                continue;
            EmitSubscriptLocalVtableFields(writer, subscript, protocolDecl, subscriptIndex, emittedFields);
            subscriptIndex++;
        }

        // Method delegates
        int methodIndex = 0;
        var methodIndices = new Dictionary<string, int>();
        foreach (var method in protocolDecl.Methods)
        {
            if (method.IsConstructor || method.MethodType == MethodType.Static)
                continue;

            var methodKey = GetMethodKey(method);
            if (!methodIndices.TryGetValue(methodKey, out var idx))
            {
                idx = methodIndex++;
                methodIndices[methodKey] = idx;
                // Only emit the field for new methods
                EmitMethodLocalVtableField(writer, method, protocolDecl, idx, emittedFields);
            }
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    private void EmitPropertyVtableSwiftFields(CSharpWriter writer, PropertyDecl property, HashSet<string> emittedFields)
    {
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();

        if (hasGetter)
        {
            var fieldName = $"func_{property.Name}_get";
            if (emittedFields.Add(fieldName))
            {
                writer.WriteLine($"public IntPtr {fieldName};");
            }
        }
        if (hasSetter)
        {
            var fieldName = $"func_{property.Name}_set";
            if (emittedFields.Add(fieldName))
            {
                writer.WriteLine($"public IntPtr {fieldName};");
            }
        }
    }

    private void EmitPropertyLocalVtableFields(CSharpWriter writer, PropertyDecl property, ProtocolDecl protocolDecl, HashSet<string> emittedFields)
    {
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();

        if (hasGetter)
        {
            var fieldName = $"Func_{property.Name}_get";
            if (emittedFields.Add(fieldName))
            {
                writer.WriteLine($"public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr> {fieldName};");
            }
        }
        if (hasSetter)
        {
            var fieldName = $"Func_{property.Name}_set";
            if (emittedFields.Add(fieldName))
            {
                writer.WriteLine($"public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void> {fieldName};");
            }
        }
    }

    private void EmitSubscriptVtableSwiftFields(CSharpWriter writer, SubscriptDecl subscript, int index, HashSet<string> emittedFields)
    {
        if (subscript.HasGetter)
        {
            var fieldName = $"func_subscript_{index}_get";
            if (emittedFields.Add(fieldName))
            {
                writer.WriteLine($"public IntPtr {fieldName};");
            }
        }
        if (subscript.HasSetter)
        {
            var fieldName = $"func_subscript_{index}_set";
            if (emittedFields.Add(fieldName))
            {
                writer.WriteLine($"public IntPtr {fieldName};");
            }
        }
    }

    private void EmitSubscriptLocalVtableFields(CSharpWriter writer, SubscriptDecl subscript, ProtocolDecl protocolDecl, int index, HashSet<string> emittedFields)
    {
        var paramCount = subscript.IndexParameters.Count;
        // Getter: IntPtr (vtable), IntPtr (self), IntPtr[] (indices) -> IntPtr (result)
        // Setter: IntPtr (vtable), IntPtr (self), IntPtr (value), IntPtr[] (indices) -> void

        if (subscript.HasGetter)
        {
            var fieldName = $"Func_subscript_{index}_get";
            if (emittedFields.Add(fieldName))
            {
                var paramTypes = "IntPtr, IntPtr" + string.Concat(Enumerable.Repeat(", IntPtr", paramCount));
                writer.WriteLine($"public delegate* unmanaged[Cdecl]<{paramTypes}, IntPtr> {fieldName};");
            }
        }
        if (subscript.HasSetter)
        {
            var fieldName = $"Func_subscript_{index}_set";
            if (emittedFields.Add(fieldName))
            {
                var paramTypes = "IntPtr, IntPtr, IntPtr" + string.Concat(Enumerable.Repeat(", IntPtr", paramCount));
                writer.WriteLine($"public delegate* unmanaged[Cdecl]<{paramTypes}, void> {fieldName};");
            }
        }
    }

    private void EmitMethodVtableSwiftField(CSharpWriter writer, MethodDecl method, int index, HashSet<string> emittedFields)
    {
        var fieldName = $"func_{method.Name}_{index}";
        if (emittedFields.Add(fieldName))
        {
            writer.WriteLine($"public IntPtr {fieldName};");
        }
    }

    private void EmitMethodLocalVtableField(CSharpWriter writer, MethodDecl method, ProtocolDecl protocolDecl, int index, HashSet<string> emittedFields)
    {
        var fieldName = $"Func_{method.Name}_{index}";
        if (!emittedFields.Add(fieldName))
            return;

        var paramCount = method.CSSignature.Count - 1; // Exclude return type
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;

        // Parameters: IntPtr (vtable), IntPtr (self), IntPtr[] (method params)
        var paramTypes = "IntPtr, IntPtr" + string.Concat(Enumerable.Repeat(", IntPtr", paramCount));
        var returnTypeStr = hasReturn ? "IntPtr" : "void";

        writer.WriteLine($"public delegate* unmanaged[Cdecl]<{paramTypes}, {returnTypeStr}> {fieldName};");
    }
}
