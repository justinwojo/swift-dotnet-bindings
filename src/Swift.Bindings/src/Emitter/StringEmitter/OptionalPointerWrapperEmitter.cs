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

        // Build Swift parameter list
        var swiftParams = new List<string>();
        var callArgs = new List<string>();
        var valueArgs = new List<string>(); // Unlabeled values for setter assignment RHS
        var derefCode = new List<string>();

        foreach (var arg in methodDecl.CSSignature.Skip(1))
        {
            var csName = NameProvider.GetCSharpParameterName(arg);

            if (env.BoundGenericsHandler.IsLargeOptionalParam(arg.SwiftTypeSpec))
            {
                // Large Optional: accept UnsafeRawPointer, dereference in body
                swiftParams.Add($"_ {csName}: UnsafeRawPointer");

                var swiftType = SwiftTypeNameHelper.GetSwiftTypeNameForMetatype(arg.SwiftTypeSpec);
                derefCode.Add($"let {csName}Val = {csName}.assumingMemoryBound(to: {swiftType}.self).pointee");

                var label = GetSwiftArgLabel(arg);
                callArgs.Add($"{label}{csName}Val");
                valueArgs.Add($"{csName}Val");
            }
            else
            {
                // Non-large param: pass through with original Swift type
                var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec);
                swiftParams.Add($"_ {csName}: {swiftType}");
                var label = GetSwiftArgLabel(arg);
                callArgs.Add($"{label}{csName}");
                valueArgs.Add(csName);
            }
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

        // Build return type
        var returnTypeSpec = methodDecl.CSSignature[0].SwiftTypeSpec;
        var hasReturn = !returnTypeSpec.IsEmptyTuple;
        var returnTypeStr = hasReturn ? $" -> {ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec)}" : "";
        var throwsStr = methodDecl.Throws ? " throws" : "";

        // Determine whether we need through-pointer self access (mutations preserved)
        // vs copy-based access (simpler but mutations lost).
        bool isClass = parentDecl is ClassDecl;
        bool needsThroughPointer = !isClass && (methodDecl.IsMutating || isSetter);
        var typeName = parentDecl?.SwiftTypeName?.ModuleQualifiedName ?? parentDecl?.Name ?? "";

        // Build the call expression and self conversion
        string callLine;
        string selfConversion = "";

        if (methodDecl.IsConstructor)
        {
            callLine = $"{typeName}({callArgsStr})";
        }
        else if (isSetter && isInstance)
        {
            // Property setter: emit assignment syntax (no argument labels on RHS)
            var propertyName = GetPropertyNameFromSetter(methodDecl.Name);
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
            var propertyName = GetPropertyNameFromSetter(methodDecl.Name);
            callLine = $"{typeName}.{propertyName} = {setterValueStr}";
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

        // Emit the call
        var returnPrefix = hasReturn || methodDecl.IsConstructor ? "return " : "";
        swiftWriter.WriteLine($"    {returnPrefix}{tryPrefix}{callLine}");
        swiftWriter.WriteLine("}");
        swiftWriter.WriteLine();
    }

    /// <summary>
    /// Gets the Swift property name from a setter method name by stripping the _Set suffix.
    /// </summary>
    private static string GetPropertyNameFromSetter(string methodName)
    {
        const string suffix = "_Set";
        if (methodName.EndsWith(suffix))
            return methodName.Substring(0, methodName.Length - suffix.Length);
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
}
