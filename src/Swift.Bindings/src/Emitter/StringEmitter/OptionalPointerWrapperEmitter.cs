// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Generates Swift wrapper functions that accept UnsafeRawPointer for large Optional
/// parameters (e.g., Optional&lt;String&gt; which is 16 bytes). The wrapper dereferences
/// the pointer via .assumingMemoryBound(to:).pointee before calling the original method.
/// This avoids the IntPtr truncation bug where PayloadBuffer&lt;IntPtr&gt; only reads 8 bytes.
/// </summary>
public static class OptionalPointerWrapperEmitter
{
    /// <summary>
    /// Emits a Swift wrapper function with @_silgen_name that adapts large Optional
    /// parameters from UnsafeRawPointer to their native Optional types.
    /// Follows the same pattern as <see cref="ClosureEmitter.EmitClosureCdeclSwiftWrapper"/>.
    /// </summary>
    public static void EmitSwiftWrapper(
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        TypeDecl? parentDecl)
    {
        var methodDecl = env.MethodDecl;
        var wrapperSymbol = NameProvider.GetMangledName(methodDecl);

        bool isSetter = methodDecl.IsAccessor && MarshallingHelpers.MethodIsSetter(methodDecl);
        bool isGetter = methodDecl.IsAccessor && !isSetter;

        // Build Swift parameter list
        var swiftParams = new List<string>();
        var callArgs = new List<string>();
        var valueArgs = new List<string>(); // Unlabeled values for setter assignment RHS
        var derefCode = new List<string>();

        foreach (var arg in methodDecl.CSSignature.Skip(1))
        {
            var csName = NameProvider.GetCSharpParameterName(arg);
            // Escape Swift keywords with backticks for use in generated Swift code
            var swiftName = NameProvider.EscapeSwiftKeyword(csName);

            if (env.BoundGenericsHandler.IsLargeOptionalParam(arg.SwiftTypeSpec))
            {
                // Large Optional: accept UnsafeRawPointer, dereference in body
                swiftParams.Add($"_ {swiftName}: UnsafeRawPointer");

                var swiftType = SwiftTypeNameHelper.GetSwiftTypeNameForMetatype(arg.SwiftTypeSpec);
                derefCode.Add($"let {csName}Val = {swiftName}.assumingMemoryBound(to: {swiftType}.self).pointee");

                var label = GetSwiftArgLabel(arg);
                callArgs.Add($"{label}{csName}Val");
                valueArgs.Add($"{csName}Val");
            }
            else
            {
                // Non-large param: pass through with original Swift type
                var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec);
                swiftParams.Add($"_ {swiftName}: {swiftType}");
                var label = GetSwiftArgLabel(arg);
                callArgs.Add($"{label}{swiftName}");
                valueArgs.Add(swiftName);
            }
        }

        // Check if the return type is a large Optional that needs an out-buffer
        bool hasLargeOptionalReturn = env.BoundGenericsHandler.IsLargeOptionalReturn(methodDecl);

        // Add result buffer parameter before self (if large Optional return)
        if (hasLargeOptionalReturn)
        {
            swiftParams.Add("_ _resultBuf: UnsafeMutableRawPointer");
        }

        // For instance methods, add self as last param
        bool isInstance = methodDecl.MethodType != MethodType.Static && parentDecl != null && !methodDecl.IsConstructor;
        if (isInstance)
        {
            swiftParams.Add("_ _self: UnsafeMutableRawPointer");
        }

        var paramsStr = string.Join(",\n    ", swiftParams);
        var callArgsStr = string.Join(", ", callArgs);
        // Setter RHS: typically one value, no labels needed
        var setterValueStr = string.Join(", ", valueArgs);

        // Build return type — void when using result buffer
        var returnTypeSpec = methodDecl.CSSignature[0].SwiftTypeSpec;
        var hasReturn = !returnTypeSpec.IsEmptyTuple && !hasLargeOptionalReturn;
        var returnSwiftTypeName = ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec);
        var returnTypeStr = hasReturn ? $" -> {returnSwiftTypeName}" : "";
        var throwsStr = methodDecl.Throws ? " throws" : "";

        // Determine whether we need through-pointer self access (mutations preserved)
        // vs copy-based access (simpler but mutations lost).
        bool isClass = parentDecl is ClassDecl;
        bool needsThroughPointer = !isClass && (methodDecl.IsMutating || isSetter);
        var typeName = parentDecl?.SwiftTypeName?.ModuleQualifiedName ?? parentDecl?.Name ?? "";

        // Detect subscript accessors — subscript methods are named "subscript_Get" / "subscript_Set"
        bool isSubscriptAccessor = methodDecl.Name.StartsWith("subscript_");

        // Build subscript-specific args with correct label conventions
        // (unlabeled "indexN" params → no label, labeled params → "label: value")
        string subscriptArgsStr = "";
        string subscriptSetterValue = "";
        if (isSubscriptAccessor)
        {
            var subscriptArgs = new List<string>();
            bool isFirst = true;
            foreach (var arg in methodDecl.CSSignature.Skip(1))
            {
                var csName = NameProvider.GetCSharpParameterName(arg);
                var swiftName = NameProvider.EscapeSwiftKeyword(csName);
                var valName = env.BoundGenericsHandler.IsLargeOptionalParam(arg.SwiftTypeSpec)
                    ? $"{csName}Val" : swiftName;

                // For setter, first param is newValue — capture separately for assignment RHS
                if (isSetter && isFirst)
                {
                    subscriptSetterValue = valName;
                    isFirst = false;
                    continue;
                }
                isFirst = false;

                var label = GetSubscriptArgLabel(arg);
                subscriptArgs.Add($"{label}{valName}");
            }
            subscriptArgsStr = string.Join(", ", subscriptArgs);
        }

        // Build the call expression and self conversion
        string callLine;
        string selfConversion = "";

        if (methodDecl.IsConstructor)
        {
            callLine = $"{typeName}({callArgsStr})";
        }
        else if (isSubscriptAccessor && isSetter && isInstance)
        {
            // Subscript setter: __self[index] = value
            if (isClass)
            {
                selfConversion = $"let __self = unsafeBitCast(OpaquePointer(_self), to: {typeName}.self)";
                callLine = $"__self[{subscriptArgsStr}] = {subscriptSetterValue}";
            }
            else
            {
                callLine = $"_self.assumingMemoryBound(to: {typeName}.self).pointee[{subscriptArgsStr}] = {subscriptSetterValue}";
            }
        }
        else if (isSubscriptAccessor && isGetter && isInstance)
        {
            // Subscript getter: __self[index]
            if (isClass)
            {
                selfConversion = $"let __self = unsafeBitCast(OpaquePointer(_self), to: {typeName}.self)";
                callLine = $"__self[{subscriptArgsStr}]";
            }
            else
            {
                selfConversion = $"let __self = _self.assumingMemoryBound(to: {typeName}.self).pointee";
                callLine = $"__self[{subscriptArgsStr}]";
            }
        }
        else if (isSetter && isInstance)
        {
            // Property setter: emit assignment syntax (no argument labels on RHS)
            var propertyName = GetPropertyNameFromAccessor(methodDecl.Name);
            if (isClass)
            {
                selfConversion = $"let __self = unsafeBitCast(OpaquePointer(_self), to: {typeName}.self)";
                callLine = $"__self.{propertyName} = {setterValueStr}";
            }
            else
            {
                // Value type setter: through-pointer assignment
                callLine = $"_self.assumingMemoryBound(to: {typeName}.self).pointee.{propertyName} = {setterValueStr}";
            }
        }
        else if (isSetter && !isInstance && parentDecl != null)
        {
            // Static setter
            var propertyName = GetPropertyNameFromAccessor(methodDecl.Name);
            callLine = $"{typeName}.{propertyName} = {setterValueStr}";
        }
        else if (isGetter && isInstance)
        {
            // Property getter: access as property (no parentheses)
            var propertyName = GetPropertyNameFromAccessor(methodDecl.Name);
            if (isClass)
            {
                selfConversion = $"let __self = unsafeBitCast(OpaquePointer(_self), to: {typeName}.self)";
                callLine = $"__self.{propertyName}";
            }
            else
            {
                selfConversion = $"let __self = _self.assumingMemoryBound(to: {typeName}.self).pointee";
                callLine = $"__self.{propertyName}";
            }
        }
        else if (isGetter && !isInstance && parentDecl != null)
        {
            // Static getter
            var propertyName = GetPropertyNameFromAccessor(methodDecl.Name);
            callLine = $"{typeName}.{propertyName}";
        }
        else if (isInstance)
        {
            if (isClass)
            {
                selfConversion = $"let __self = unsafeBitCast(OpaquePointer(_self), to: {typeName}.self)";
                callLine = $"__self.{methodDecl.Name}({callArgsStr})";
            }
            else if (needsThroughPointer)
            {
                // Mutating value type: through-pointer to preserve mutations
                callLine = $"_self.assumingMemoryBound(to: {typeName}.self).pointee.{methodDecl.Name}({callArgsStr})";
            }
            else
            {
                // Non-mutating value type: copy is safe
                selfConversion = $"let __self = _self.assumingMemoryBound(to: {typeName}.self).pointee";
                callLine = $"__self.{methodDecl.Name}({callArgsStr})";
            }
        }
        else if (parentDecl != null)
        {
            callLine = $"{typeName}.{methodDecl.Name}({callArgsStr})";
        }
        else
        {
            var moduleName = methodDecl.ModuleDecl?.Name ?? "";
            var prefix = moduleName.Length > 0 ? $"{moduleName}." : "";
            callLine = $"{prefix}{methodDecl.Name}({callArgsStr})";
        }

        var tryPrefix = methodDecl.Throws ? "try " : "";

        // Emit the wrapper
        swiftWriter.WriteLine($"@_silgen_name(\"{wrapperSymbol}\")");
        swiftWriter.WriteLine($"public func {NameProvider.GetPInvokeName(methodDecl)}(");
        swiftWriter.WriteLine($"    {paramsStr}");
        swiftWriter.WriteLine($"){throwsStr}{returnTypeStr} {{");

        // Emit self conversion for instance methods (when not using through-pointer)
        if (!string.IsNullOrEmpty(selfConversion))
        {
            swiftWriter.WriteLine($"    {selfConversion}");
        }

        // Emit pointer dereferences for large Optional params
        foreach (var line in derefCode)
        {
            swiftWriter.WriteLine($"    {line}");
        }

        // Emit the call — write to result buffer for large Optional returns
        if (hasLargeOptionalReturn)
        {
            var bufferLines = GetReturnBufferCode($"{tryPrefix}{callLine}", returnSwiftTypeName);
            foreach (var line in bufferLines)
            {
                swiftWriter.WriteLine($"    {line}");
            }
        }
        else
        {
            var returnPrefix = hasReturn || methodDecl.IsConstructor ? "return " : "";
            swiftWriter.WriteLine($"    {returnPrefix}{tryPrefix}{callLine}");
        }
        swiftWriter.WriteLine("}");
        swiftWriter.WriteLine();
    }

    /// <summary>
    /// Returns true if the argument is a large Optional parameter that needs UnsafeRawPointer widening.
    /// Shared by all Swift wrapper emitters (ArraySlice, DefaultParam, ClosureCdecl, opaque return, async).
    /// </summary>
    public static bool ShouldWidenParam(ArgumentDecl arg, BoundGenericsHandler bgHandler)
        => bgHandler.IsLargeOptionalParam(arg.SwiftTypeSpec);

    /// <summary>
    /// Returns the Swift code to dereference an UnsafeRawPointer parameter to its original Optional type.
    /// </summary>
    public static string GetDerefCode(ArgumentDecl arg, string csName, string swiftName)
    {
        var swiftType = SwiftTypeNameHelper.GetSwiftTypeNameForMetatype(arg.SwiftTypeSpec);
        return $"let {csName}Val = {swiftName}.assumingMemoryBound(to: {swiftType}.self).pointee";
    }

    /// <summary>
    /// Returns the Swift code to write a result value to an UnsafeMutableRawPointer result buffer.
    /// Used when the return type is a large Optional (e.g., Optional&lt;String&gt; which is 16 bytes).
    /// </summary>
    public static List<string> GetReturnBufferCode(string callLine, string returnSwiftType)
    {
        return new List<string>
        {
            $"let _result = {callLine}",
            $"withUnsafePointer(to: _result) {{ _srcPtr in",
            $"    _resultBuf.copyMemory(from: UnsafeRawPointer(_srcPtr),",
            $"        byteCount: MemoryLayout<{returnSwiftType}>.size)",
            $"}}"
        };
    }

    /// <summary>
    /// Gets the Swift property name from an accessor method name by stripping the _Get or _Set suffix.
    /// </summary>
    private static string GetPropertyNameFromAccessor(string methodName)
    {
        if (methodName.EndsWith("_Set"))
            return methodName.Substring(0, methodName.Length - 4);
        if (methodName.EndsWith("_Get"))
            return methodName.Substring(0, methodName.Length - 4);
        return methodName;
    }

    /// <summary>
    /// Gets the Swift argument label for a parameter.
    /// Reconstructs the original Swift label from the argument name.
    /// Same logic as <see cref="ClosureEmitter"/>'s GetSwiftArgLabel.
    /// </summary>
    private static string GetSwiftArgLabel(ArgumentDecl arg)
    {
        var name = arg.Name;
        if (name.StartsWith("arg"))
            return ""; // Unlabeled
        if (name.StartsWith("_"))
            return $"{name.Substring(1)}: "; // Strip leading underscore
        return $"{name}: ";
    }

    /// <summary>
    /// Gets the Swift subscript label for an index parameter.
    /// Unlabeled subscript params are named "indexN" by the parser — these become unlabeled.
    /// Labeled subscript params (e.g., "string", "data") keep their label.
    /// </summary>
    private static string GetSubscriptArgLabel(ArgumentDecl arg)
    {
        var name = arg.Name;
        // Parser generates "index0", "index1" etc. for unlabeled subscript params
        if (name.StartsWith("index") && name.Length > 5 && char.IsDigit(name[5]))
            return "";
        return $"{name}: ";
    }
}
