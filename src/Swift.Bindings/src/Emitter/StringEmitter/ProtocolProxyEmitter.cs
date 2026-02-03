// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Emits C# proxy classes for Swift protocols.
/// The proxy pattern allows C# code to implement Swift protocols by:
/// 1. Wrapping either a C# implementation or a Swift existential container
/// 2. Providing a vtable of function pointers that Swift can call back into
/// 3. Managing the EveryProtocol instance and protocol witness table
/// </summary>
public class ProtocolProxyEmitter
{
    private readonly ITypeDatabase _typeDatabase;
    private readonly ILogger _logger;
    private readonly string _moduleName;

    public ProtocolProxyEmitter(ITypeDatabase typeDatabase, ILogger logger, string moduleName)
    {
        _typeDatabase = typeDatabase;
        _logger = logger;
        _moduleName = moduleName;
    }

    /// <summary>
    /// Emits the complete proxy class for a protocol.
    /// </summary>
    public void EmitProxyClass(CSharpWriter writer, ProtocolDecl protocolDecl)
    {
        // Skip protocols with Self requirements - these require special handling
        // that can't be done with simple generic parameters
        if (protocolDecl.HasSelfRequirement)
        {
            _logger.LogDebug($"Skipping proxy class for {protocolDecl.Name}: has Self requirement");
            return;
        }

        // Skip protocols with associated types (would create generic proxy classes)
        // C# doesn't allow [UnmanagedCallersOnly] or [DllImport] in generic types,
        // and nested classes inside generic types inherit this restriction.
        // TODO: Implement a more sophisticated approach for generic protocol proxies
        // (e.g., using runtime code generation or non-generic base classes)
        if (protocolDecl.AssociatedTypes.Count > 0)
        {
            _logger.LogWarning($"Skipping proxy class for {protocolDecl.Name}: protocols with associated types are not yet supported for proxy generation (would require [UnmanagedCallersOnly] in generic type)");
            return;
        }

        // Skip protocols with no implementable members
        var hasImplementableMembers = protocolDecl.Properties.Any() ||
                                      protocolDecl.Methods.Any(m => !m.IsConstructor && m.MethodType != MethodType.Static) ||
                                      protocolDecl.Subscripts.Any();
        if (!hasImplementableMembers)
        {
            _logger.LogDebug($"Skipping proxy class for {protocolDecl.Name}: no implementable members");
            return;
        }

        var interfaceName = NameProvider.GetInterfaceName(protocolDecl.Name);
        var proxyClassName = GetProxyClassName(protocolDecl);
        var proxyClassNameWithGenerics = GetProxyClassNameWithGenerics(protocolDecl);
        var interfaceNameWithGenerics = GetInterfaceNameWithGenerics(protocolDecl);
        var constraints = GetProxyClassConstraints(protocolDecl);

        writer.WriteLine($"/// <summary>");
        writer.WriteLine($"/// Proxy class that enables C# implementations of the {protocolDecl.Name} protocol.");
        writer.WriteLine($"/// Can wrap either a C# implementation or receive Swift existential containers.");
        writer.WriteLine($"/// </summary>");
        writer.WriteLine($"public unsafe class {proxyClassNameWithGenerics} : {interfaceNameWithGenerics}, ISwiftObject{constraints}");
        writer.WriteLine("{");
        writer.Indent++;

        // Emit vtable structs
        EmitSwiftVtableStruct(writer, protocolDecl);
        EmitLocalVtableStruct(writer, protocolDecl);

        // Emit static fields
        EmitStaticFields(writer, protocolDecl);

        // Emit instance fields
        EmitInstanceFields(writer, protocolDecl, interfaceNameWithGenerics);

        // Emit static constructor (registers vtable with Swift)
        EmitStaticConstructor(writer, protocolDecl);

        // Emit receiver methods (UnmanagedCallersOnly callbacks)
        EmitReceiverMethods(writer, protocolDecl, interfaceNameWithGenerics);

        // Emit constructors
        EmitConstructors(writer, protocolDecl, interfaceNameWithGenerics);

        // Emit interface implementation
        EmitInterfaceImplementation(writer, protocolDecl, interfaceNameWithGenerics);

        // Emit ISwiftObject implementation
        EmitISwiftObjectImplementation(writer, protocolDecl);

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    #region Vtable Structs

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

        // Property fields
        foreach (var property in protocolDecl.Properties)
        {
            EmitPropertyVtableSwiftFields(writer, property, emittedFields);
        }

        // Subscript fields
        int subscriptIndex = 0;
        foreach (var subscript in protocolDecl.Subscripts)
        {
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

        // Property delegates - track emitted to avoid duplicates
        var emittedFields = new HashSet<string>();
        foreach (var property in protocolDecl.Properties)
        {
            EmitPropertyLocalVtableFields(writer, property, protocolDecl, emittedFields);
        }

        // Subscript delegates
        int subscriptIndex = 0;
        foreach (var subscript in protocolDecl.Subscripts)
        {
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

    #endregion

    #region Static Fields and Constructor

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
            private readonly ExistentialContainer1 _swiftContainer;

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

        // Property receivers
        foreach (var property in protocolDecl.Properties)
        {
            EmitLocalVtablePropertyAssignment(writer, property, emittedLocalAssignments);
        }

        // Subscript receivers
        int subscriptIndex = 0;
        var emittedSubscripts = new HashSet<string>();
        foreach (var subscript in protocolDecl.Subscripts)
        {
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

        // Property function pointers
        foreach (var property in protocolDecl.Properties)
        {
            EmitSwiftVtablePropertyAssignment(writer, property, emittedSwiftAssignments);
        }

        // Subscript function pointers
        subscriptIndex = 0;
        emittedSubscripts.Clear();
        foreach (var subscript in protocolDecl.Subscripts)
        {
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

    #endregion

    #region Receiver Methods

    private void EmitReceiverMethods(CSharpWriter writer, ProtocolDecl protocolDecl, string interfaceName)
    {
        writer.WriteLine("#region Swift Callback Receivers");
        writer.WriteLine();

        // Track emitted receivers to avoid duplicates
        var emittedReceivers = new HashSet<string>();

        // Property receivers
        foreach (var property in protocolDecl.Properties)
        {
            EmitPropertyReceivers(writer, property, protocolDecl, interfaceName, emittedReceivers);
        }

        // Subscript receivers
        int subscriptIndex = 0;
        foreach (var subscript in protocolDecl.Subscripts)
        {
            EmitSubscriptReceivers(writer, subscript, protocolDecl, interfaceName, subscriptIndex, emittedReceivers);
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
            if (!methodIndices.TryGetValue(methodKey, out var idx))
            {
                idx = methodIndex++;
                methodIndices[methodKey] = idx;
                // Only emit receiver for new methods
                EmitMethodReceiver(writer, method, protocolDecl, interfaceName, idx, emittedReceivers);
            }
        }

        writer.WriteLine("#endregion");
        writer.WriteLine();
    }

    private void EmitPropertyReceivers(CSharpWriter writer, PropertyDecl property, ProtocolDecl protocolDecl, string interfaceName, HashSet<string> emittedReceivers)
    {
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();
        var proxyClassName = GetProxyClassName(protocolDecl);
        var csharpTypeName = GetCSharpTypeName(property.SwiftTypeSpec);

        if (hasGetter)
        {
            var receiverName = $"Receive_{property.Name}_get";
            if (emittedReceivers.Add(receiverName))
            {
                writer.WriteLines($$"""
                    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
                    private static IntPtr {{receiverName}}(IntPtr vtHandle, IntPtr selfContainer)
                    {
                        var container = *(ExistentialContainer1*)selfContainer;
                        var proxy = SwiftObjectRegistry.GetProxyFromContainer<{{proxyClassName}}>(container);
                        var result = proxy._csharpImpl!.{{property.Name}};
                        return MarshalToSwiftBuffer(result);
                    }

                    """);
            }
        }

        if (hasSetter)
        {
            var receiverName = $"Receive_{property.Name}_set";
            if (emittedReceivers.Add(receiverName))
            {
                writer.WriteLines($$"""
                    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
                    private static void {{receiverName}}(IntPtr vtHandle, IntPtr selfContainer, IntPtr valuePtr)
                    {
                        var container = *(ExistentialContainer1*)selfContainer;
                        var proxy = SwiftObjectRegistry.GetProxyFromContainer<{{proxyClassName}}>(container);
                        var value = MarshalFromSwift<{{csharpTypeName}}>(valuePtr);
                        proxy._csharpImpl!.{{property.Name}} = value;
                    }

                    """);
            }
        }
    }

    private void EmitSubscriptReceivers(CSharpWriter writer, SubscriptDecl subscript, ProtocolDecl protocolDecl, string interfaceName, int index, HashSet<string> emittedReceivers)
    {
        var proxyClassName = GetProxyClassName(protocolDecl);
        var returnTypeName = GetCSharpTypeName(subscript.ReturnTypeSpec);
        var paramCount = subscript.IndexParameters.Count;

        if (subscript.HasGetter)
        {
            var receiverName = $"Receive_subscript_{index}_get";
            if (emittedReceivers.Add(receiverName))
            {
                // Build parameter list
                var paramTypes = "IntPtr vtHandle, IntPtr selfContainer" + string.Concat(
                    subscript.IndexParameters.Select((p, i) => $", IntPtr arg{i}"));

                writer.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]");
                writer.WriteLine($"private static IntPtr {receiverName}({paramTypes})");
                writer.WriteLine("{");
                writer.Indent++;

                writer.WriteLine("var container = *(ExistentialContainer1*)selfContainer;");
                writer.WriteLine($"var proxy = SwiftObjectRegistry.GetProxyFromContainer<{proxyClassName}>(container);");

                // Unmarshal index parameters
                for (int i = 0; i < subscript.IndexParameters.Count; i++)
                {
                    var param = subscript.IndexParameters[i];
                    var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec);
                    writer.WriteLine($"var index{i} = MarshalFromSwift<{paramTypeName}>(arg{i});");
                }

                var indexArgs = string.Join(", ", Enumerable.Range(0, paramCount).Select(i => $"index{i}"));
                writer.WriteLine($"var result = proxy._csharpImpl![{indexArgs}];");
                writer.WriteLine("return MarshalToSwiftBuffer(result);");

                writer.Indent--;
                writer.WriteLine("}");
                writer.WriteLine();
            }
        }

        if (subscript.HasSetter)
        {
            var receiverName = $"Receive_subscript_{index}_set";
            if (emittedReceivers.Add(receiverName))
            {
                // Build parameter list (includes value after self)
                var paramTypes = "IntPtr vtHandle, IntPtr selfContainer, IntPtr valuePtr" + string.Concat(
                    subscript.IndexParameters.Select((p, i) => $", IntPtr arg{i}"));

                writer.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]");
                writer.WriteLine($"private static void {receiverName}({paramTypes})");
                writer.WriteLine("{");
                writer.Indent++;

                writer.WriteLine("var container = *(ExistentialContainer1*)selfContainer;");
                writer.WriteLine($"var proxy = SwiftObjectRegistry.GetProxyFromContainer<{proxyClassName}>(container);");
                writer.WriteLine($"var value = MarshalFromSwift<{returnTypeName}>(valuePtr);");

                // Unmarshal index parameters
                for (int i = 0; i < subscript.IndexParameters.Count; i++)
                {
                    var param = subscript.IndexParameters[i];
                    var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec);
                    writer.WriteLine($"var index{i} = MarshalFromSwift<{paramTypeName}>(arg{i});");
                }

                var indexArgs = string.Join(", ", Enumerable.Range(0, paramCount).Select(i => $"index{i}"));
                writer.WriteLine($"proxy._csharpImpl![{indexArgs}] = value;");

                writer.Indent--;
                writer.WriteLine("}");
                writer.WriteLine();
            }
        }
    }

    private void EmitMethodReceiver(CSharpWriter writer, MethodDecl method, ProtocolDecl protocolDecl, string interfaceName, int index, HashSet<string> emittedReceivers)
    {
        var receiverName = $"Receive_{method.Name}_{index}";
        if (!emittedReceivers.Add(receiverName))
            return;

        var proxyClassName = GetProxyClassName(protocolDecl);
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var returnTypeName = hasReturn ? GetCSharpTypeName(returnType!) : "void";

        var paramCount = method.CSSignature.Count - 1;
        var paramTypes = "IntPtr vtHandle, IntPtr selfContainer" + string.Concat(
            method.CSSignature.Skip(1).Select((p, i) => $", IntPtr rawArg{i}"));

        var csharpReturnType = hasReturn ? "IntPtr" : "void";

        writer.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]");
        writer.WriteLine($"private static {csharpReturnType} {receiverName}({paramTypes})");
        writer.WriteLine("{");
        writer.Indent++;

        writer.WriteLine("var container = *(ExistentialContainer1*)selfContainer;");
        writer.WriteLine($"var proxy = SwiftObjectRegistry.GetProxyFromContainer<{proxyClassName}>(container);");

        // Unmarshal parameters - use param{i} for local variable names to avoid conflicts with rawArg{i}
        var argNames = new List<string>();
        int argIndex = 0;
        foreach (var param in method.CSSignature.Skip(1))
        {
            var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec);
            var argName = $"param{argIndex}"; // Always use param{i} to avoid conflicts
            writer.WriteLine($"var {argName} = MarshalFromSwift<{paramTypeName}>(rawArg{argIndex});");
            argNames.Add(argName);
            argIndex++;
        }

        var argsString = string.Join(", ", argNames);

        if (hasReturn)
        {
            writer.WriteLine($"var result = proxy._csharpImpl!.{method.Name}({argsString});");
            writer.WriteLine("return MarshalToSwiftBuffer(result);");
        }
        else
        {
            writer.WriteLine($"proxy._csharpImpl!.{method.Name}({argsString});");
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    #endregion

    #region Constructors

    private void EmitConstructors(CSharpWriter writer, ProtocolDecl protocolDecl, string interfaceName)
    {
        var proxyClassName = GetProxyClassName(protocolDecl);

        // Constructor for C# implementation
        writer.WriteLines($$"""
            /// <summary>
            /// Creates a proxy wrapping a C# implementation of {{interfaceName}}.
            /// </summary>
            /// <param name="implementation">The C# implementation of the protocol.</param>
            public {{proxyClassName}}({{interfaceName}} implementation)
            {
                _csharpImpl = implementation ?? throw new ArgumentNullException(nameof(implementation));
                _everyProtocol = new EveryProtocol();

                // Create existential container manually
                // The container holds: payload (EveryProtocol pointer), metadata, and witness table
                _swiftContainer = new ExistentialContainer1();
                _swiftContainer.Payload0 = _everyProtocol.Handle;
                _swiftContainer.ObjectMetadata = EveryProtocol.GetTypeMetadata();
                _swiftContainer[0] = ProtocolWitnessTableHandle;

                // Register this proxy so Swift callbacks can find us
                SwiftObjectRegistry.RegisterStrong(_everyProtocol.Handle, this);
            }

            /// <summary>
            /// Creates a proxy from an existing Swift existential container.
            /// Use this when receiving protocol values from Swift code.
            /// </summary>
            /// <param name="container">The Swift existential container.</param>
            public {{proxyClassName}}(ExistentialContainer1 container)
            {
                _swiftContainer = container;
                _csharpImpl = null;
                _everyProtocol = null;
            }

            """);
    }

    #endregion

    #region Interface Implementation

    private void EmitInterfaceImplementation(CSharpWriter writer, ProtocolDecl protocolDecl, string interfaceName)
    {
        writer.WriteLine("#region Interface Implementation");
        writer.WriteLine();

        // Track emitted members to avoid duplicates
        var emittedMembers = new HashSet<string>();

        // Properties
        foreach (var property in protocolDecl.Properties)
        {
            if (emittedMembers.Add($"property:{property.Name}"))
            {
                EmitPropertyImplementation(writer, property, protocolDecl);
            }
        }

        // Subscripts (as indexers)
        int subscriptIndex = 0;
        foreach (var subscript in protocolDecl.Subscripts)
        {
            var key = $"subscript:{subscriptIndex}";
            if (emittedMembers.Add(key))
            {
                EmitSubscriptImplementation(writer, subscript, protocolDecl, subscriptIndex);
            }
            subscriptIndex++;
        }

        // Methods - track by signature to handle overloads
        var methodIndices = new Dictionary<string, int>();
        foreach (var method in protocolDecl.Methods)
        {
            if (method.IsConstructor || method.MethodType == MethodType.Static)
                continue;

            var methodKey = GetMethodKey(method);
            if (!methodIndices.ContainsKey(methodKey))
            {
                methodIndices[methodKey] = 1;
                EmitMethodImplementation(writer, method, protocolDecl);
            }
        }

        writer.WriteLine("#endregion");
        writer.WriteLine();
    }

    private void EmitPropertyImplementation(CSharpWriter writer, PropertyDecl property, ProtocolDecl protocolDecl)
    {
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();
        var csharpTypeName = GetInterfaceCompatiblePropertyTypeName(property);

        writer.WriteLine($"public {csharpTypeName} {property.Name}");
        writer.WriteLine("{");
        writer.Indent++;

        if (hasGetter)
        {
            writer.WriteLines($$"""
                get
                {
                    if (_csharpImpl != null)
                        return _csharpImpl.{{property.Name}};
                    // TODO: Call Swift via P/Invoke for Swift implementation
                    throw new NotImplementedException("Swift implementation not yet supported");
                }
                """);
        }

        if (hasSetter)
        {
            writer.WriteLines($$"""
                set
                {
                    if (_csharpImpl != null)
                    {
                        _csharpImpl.{{property.Name}} = value;
                        return;
                    }
                    // TODO: Call Swift via P/Invoke for Swift implementation
                    throw new NotImplementedException("Swift implementation not yet supported");
                }
                """);
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    private void EmitSubscriptImplementation(CSharpWriter writer, SubscriptDecl subscript, ProtocolDecl protocolDecl, int index)
    {
        var returnTypeName = GetCSharpTypeName(subscript.ReturnTypeSpec);

        // Build parameter list
        var parameters = new List<string>();
        for (int i = 0; i < subscript.IndexParameters.Count; i++)
        {
            var param = subscript.IndexParameters[i];
            var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec);
            var paramName = string.IsNullOrEmpty(param.Name) ? $"index{i}" : param.Name;
            parameters.Add($"{paramTypeName} {paramName}");
        }
        var parametersString = string.Join(", ", parameters);

        var argNames = subscript.IndexParameters.Select((p, i) =>
            string.IsNullOrEmpty(p.Name) ? $"index{i}" : p.Name).ToList();
        var argsString = string.Join(", ", argNames);

        writer.WriteLine($"public {returnTypeName} this[{parametersString}]");
        writer.WriteLine("{");
        writer.Indent++;

        if (subscript.HasGetter)
        {
            writer.WriteLines($$"""
                get
                {
                    if (_csharpImpl != null)
                        return _csharpImpl[{{argsString}}];
                    // TODO: Call Swift via P/Invoke for Swift implementation
                    throw new NotImplementedException("Swift implementation not yet supported");
                }
                """);
        }

        if (subscript.HasSetter)
        {
            writer.WriteLines($$"""
                set
                {
                    if (_csharpImpl != null)
                    {
                        _csharpImpl[{{argsString}}] = value;
                        return;
                    }
                    // TODO: Call Swift via P/Invoke for Swift implementation
                    throw new NotImplementedException("Swift implementation not yet supported");
                }
                """);
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    private void EmitMethodImplementation(CSharpWriter writer, MethodDecl method, ProtocolDecl protocolDecl)
    {
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var returnTypeName = hasReturn ? GetCSharpTypeName(returnType!) : "void";

        // Build parameter list
        var parameters = new List<string>();
        var argNames = new List<string>();
        int argIndex = 0;
        foreach (var param in method.CSSignature.Skip(1))
        {
            var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec);
            var paramName = string.IsNullOrEmpty(param.Name) ? $"arg{argIndex}" : param.Name;
            parameters.Add($"{paramTypeName} {paramName}");
            argNames.Add(paramName);
            argIndex++;
        }
        var parametersString = string.Join(", ", parameters);
        var argsString = string.Join(", ", argNames);

        writer.WriteLine($"public {returnTypeName} {method.Name}({parametersString})");
        writer.WriteLine("{");
        writer.Indent++;

        if (hasReturn)
        {
            writer.WriteLines($$"""
                if (_csharpImpl != null)
                    return _csharpImpl.{{method.Name}}({{argsString}});
                // TODO: Call Swift via P/Invoke for Swift implementation
                throw new NotImplementedException("Swift implementation not yet supported");
                """);
        }
        else
        {
            writer.WriteLines($$"""
                if (_csharpImpl != null)
                {
                    _csharpImpl.{{method.Name}}({{argsString}});
                    return;
                }
                // TODO: Call Swift via P/Invoke for Swift implementation
                throw new NotImplementedException("Swift implementation not yet supported");
                """);
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    #endregion

    #region ISwiftObject Implementation

    private void EmitISwiftObjectImplementation(CSharpWriter writer, ProtocolDecl protocolDecl)
    {
        var proxyClassName = GetProxyClassName(protocolDecl);
        var witnessTableSymbol = GetWitnessTableSymbol(protocolDecl);

        writer.WriteLines($$"""
            #region ISwiftObject Implementation

            /// <summary>
            /// Gets the protocol witness table handle for EveryProtocol conforming to {{protocolDecl.Name}}.
            /// </summary>
            public static IntPtr ProtocolWitnessTableHandle
            {
                get
                {
                    if (_protocolWitnessTable == IntPtr.Zero)
                    {
                        // The witness table is generated by the Swift compiler
                        // It will be available after the Swift wrapper is loaded
                        // For now, we look it up dynamically
                        _protocolWitnessTable = GetWitnessTableFromSwift();
                    }
                    return _protocolWitnessTable;
                }
            }

            private static IntPtr GetWitnessTableFromSwift()
            {
                // Call the Swift-exported function that returns the witness table pointer
                // This function is generated by EveryProtocolEmitter.EmitWitnessTableGetter()
                return NativeMethods.GetWitnessTable();
            }

            /// <summary>
            /// Gets the existential container that can be passed to Swift code.
            /// </summary>
            public ExistentialContainer1 GetExistentialContainer() => _swiftContainer;

            public static TypeMetadata GetTypeMetadata()
            {
                // Proxy classes don't have their own Swift metadata
                // They use the EveryProtocol metadata
                return EveryProtocol.GetTypeMetadata();
            }

            public static ISwiftObject NewFromPayload(IntPtr payload)
            {
                // Create from existential container
                var container = *(ExistentialContainer1*)payload;
                return new {{proxyClassName}}(container);
            }

            public int MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                // Marshal the existential container
                var size = _swiftContainer.SizeOf;
                if (swiftDestSpan.Length < size)
                    throw new ArgumentException("Destination span too small", nameof(swiftDestSpan));

                fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                {
                    new Span<byte>(containerPtr, size).CopyTo(swiftDestSpan);
                }
                return size;
            }

            public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            {
                throw new NotImplementedException("Protocol conformance descriptor not available for proxy types");
            }

            #endregion

            #region Marshalling Helpers

            private static IntPtr MarshalToSwiftBuffer<T>(T value)
            {
                // Use direct memory operations for all types
                // This works for blittable types (value types, structs with blittable fields)
                var size = Unsafe.SizeOf<T>();
                var ptr = (IntPtr)NativeMemory.Alloc((nuint)size);
                Unsafe.Write((void*)ptr, value);
                return ptr;
            }

            private static T MarshalFromSwift<T>(IntPtr ptr)
            {
                // Use direct memory operations for all types
                return Unsafe.Read<T>((void*)ptr);
            }

            #endregion

            """);

        // Emit NativeMethods for SetVtable
        var setVtableName = GetSetVtablePInvokeName(protocolDecl);
        var mangledName = $"Set{protocolDecl.Name}_vtable";

        // Note: vtable and witness table functions are in the SwiftBindings wrapper, not the original module
        writer.WriteLines($$"""
            private static class NativeMethods
            {
                [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "{{mangledName}}")]
                public static extern void {{setVtableName}}(IntPtr vtable);

                [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "Get_EveryProtocol_{{protocolDecl.Name}}_WitnessTable")]
                public static extern IntPtr GetWitnessTable();
            }
            """);
    }

    #endregion

    #region Helper Methods

    private string GetCSharpTypeName(TypeSpec? typeSpec)
    {
        if (typeSpec == null) return "object";

        // Handle associated type references (e.g., Self.Element -> TElement)
        if (typeSpec is AssociatedTypeReferenceSpec associatedTypeRef)
        {
            // Map associated type references to the generic type parameter
            // Self.Element -> TElement, τ_0_0.Key -> TKey
            return $"T{associatedTypeRef.AssociatedTypeName}";
        }

        // Handle closure types - translate to Action/Func delegates
        if (typeSpec is ClosureTypeSpec closureTypeSpec)
        {
            return GetClosureCSharpType(closureTypeSpec);
        }

        // Handle tuple types - translate to ValueTuple
        if (typeSpec is TupleTypeSpec tupleTypeSpec)
        {
            if (tupleTypeSpec.IsEmptyTuple)
                return "void";
            return GetTupleCSharpType(tupleTypeSpec);
        }

        // Handle existential/protocol types using ExistentialHandler
        var existentialHandler = new ExistentialHandler(_typeDatabase);
        if (existentialHandler.IsExistential(typeSpec))
        {
            var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
            if (protocolList != null && existentialHandler.IsSupportedExistential(protocolList))
            {
                return existentialHandler.GetCSharpExistentialType(protocolList);
            }
            // Keep fallback behavior consistent with ProtocolHandler interface emission.
            // Unsupported existentials flow through to type database fallback (typically Swift.AnyType).
        }

        try
        {
            // Handle generic types by getting base type and building generic arguments
            if (typeSpec is NamedTypeSpec namedType && namedType.GenericParameters.Count > 0)
            {
                // Keep proxy signatures aligned with protocol interface signatures for bound generics.
                // This is especially important for existential generic arguments (Task 7),
                // where BoundGenericsHandler intentionally falls back to AnyType.
                var boundGenericsHandler = new BoundGenericsHandler(_typeDatabase);
                var tempProperty = new PropertyDecl
                {
                    Name = "_temp",
                    SwiftTypeSpec = typeSpec,
                    IsStatic = false,
                    HasStorage = false,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = null
                };
                return boundGenericsHandler.TranslateBoundGenericTypeToCSharp(tempProperty);
            }

            var record = _typeDatabase.GetTypeRecordOrAnyType(typeSpec);
            return record.CSharpTypeName.FullyQualifiedName;
        }
        catch
        {
            if (typeSpec is NamedTypeSpec namedType)
            {
                return namedType.NameWithoutModule;
            }
            return "object";
        }
    }

    /// <summary>
    /// Resolves property types using the same rules as ProtocolHandler.EmitInterfaceProperty
    /// so proxy signatures always match the emitted interface signatures.
    /// </summary>
    private string GetInterfaceCompatiblePropertyTypeName(PropertyDecl property)
    {
        var boundGenericsHandler = new BoundGenericsHandler(_typeDatabase);
        if (boundGenericsHandler.IsBoundGeneric(property))
        {
            return boundGenericsHandler.TranslateBoundGenericTypeToCSharp(property);
        }

        return _typeDatabase.GetTypeRecordOrAnyType(property.SwiftTypeSpec).CSharpTypeName.FullyQualifiedName;
    }

    /// <summary>
    /// Translates a Swift closure type to a C# delegate type (Action or Func).
    /// </summary>
    private string GetClosureCSharpType(ClosureTypeSpec closureTypeSpec)
    {
        // Build parameter types
        var paramTypes = new List<string>();
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            paramTypes.Add(GetCSharpTypeName(arg));
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
            // Func delegate
            var returnTypeName = GetCSharpTypeName(returnType);
            if (paramTypes.Count == 0)
                return $"Func<{returnTypeName}>";
            return $"Func<{string.Join(", ", paramTypes)}, {returnTypeName}>";
        }
    }

    /// <summary>
    /// Translates a Swift tuple type to a C# ValueTuple type.
    /// </summary>
    private string GetTupleCSharpType(TupleTypeSpec tupleTypeSpec)
    {
        var elements = new List<string>();

        foreach (var element in tupleTypeSpec.Elements)
        {
            var typeName = GetCSharpTypeName(element);

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

    private static string GetProxyClassName(ProtocolDecl protocolDecl)
    {
        return $"{protocolDecl.Name}Proxy";
    }

    /// <summary>
    /// Gets the proxy class name with generic type parameters for protocols with associated types.
    /// </summary>
    private static string GetProxyClassNameWithGenerics(ProtocolDecl protocolDecl)
    {
        var baseName = GetProxyClassName(protocolDecl);

        if (protocolDecl.AssociatedTypes.Count > 0)
        {
            var typeParams = protocolDecl.AssociatedTypes.Select(at => $"T{at.Name}");
            return $"{baseName}<{string.Join(", ", typeParams)}>";
        }

        return baseName;
    }

    /// <summary>
    /// Gets the interface name with generic type parameters for protocols with associated types.
    /// </summary>
    private static string GetInterfaceNameWithGenerics(ProtocolDecl protocolDecl)
    {
        var baseName = NameProvider.GetInterfaceName(protocolDecl.Name);

        if (protocolDecl.AssociatedTypes.Count > 0)
        {
            var typeParams = protocolDecl.AssociatedTypes.Select(at => $"T{at.Name}");
            return $"{baseName}<{string.Join(", ", typeParams)}>";
        }

        return baseName;
    }

    /// <summary>
    /// Gets the generic constraints for proxy classes with associated types.
    /// Each associated type parameter is constrained to ISwiftObject.
    /// </summary>
    private static string GetProxyClassConstraints(ProtocolDecl protocolDecl)
    {
        if (protocolDecl.AssociatedTypes.Count == 0)
            return "";

        var constraints = protocolDecl.AssociatedTypes
            .Select(at => $"\n    where T{at.Name} : ISwiftObject");
        return string.Join("", constraints);
    }

    private static string GetSwiftVtableStructName(ProtocolDecl protocolDecl)
    {
        return $"{protocolDecl.Name}SwiftVTable";
    }

    private static string GetLocalVtableStructName(ProtocolDecl protocolDecl)
    {
        return $"{protocolDecl.Name}LocalVTable";
    }

    private static string GetSetVtablePInvokeName(ProtocolDecl protocolDecl)
    {
        return $"Set{protocolDecl.Name}_vtable";
    }

    private static string GetWitnessTableSymbol(ProtocolDecl protocolDecl)
    {
        // This would be the mangled symbol for the witness table
        // The format is: $s<module><type>AA<protocol>WT
        return $"EveryProtocol_{protocolDecl.Name}_WT";
    }

    private static string GetMethodKey(MethodDecl method)
    {
        return method.Name + "(" + string.Join(",", method.CSSignature.Skip(1).Select(p => p.SwiftTypeSpec?.ToString() ?? "")) + ")";
    }

    #endregion
}
