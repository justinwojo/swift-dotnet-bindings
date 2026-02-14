// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;

namespace BindingsGeneration
{
    internal partial class WrapperEmitter
    {
        /// <summary>
        /// Emits a Swift wrapper for methods returning opaque types (some Protocol).
        /// The wrapper calls the original function and boxes the return value into an
        /// existential container (any Protocol) that matches the C# ExistentialContainer type.
        /// </summary>
        private void EmitOpaqueReturnWrapper(SwiftWriter swiftWriter)
        {
            if (!_requiresOpaqueReturnWrapper)
                return;

            var returnTypeSpec = _env.MethodDecl.CSSignature.First().SwiftTypeSpec as ProtocolListTypeSpec;
            if (returnTypeSpec == null)
                return;

            // Build the "any Protocol1 & Protocol2" return type string
            var anyReturnType = "any " + string.Join(" & ", returnTypeSpec.Protocols.Keys.Select(p => p.Name));

            var parentTypeName = (_env.ParentDecl as TypeDecl)?.SwiftTypeName;
            bool isInstanceMethod = _env.MethodDecl.MethodType != MethodType.Static;
            bool isAccessor = _env.MethodDecl.IsAccessor;

            // Build Swift parameter list (matching the original function's signature)
            var methodParams = _env.MethodDecl.CSSignature
                .Skip(1)
                .Select(p => $"{p.Name}: {(p.IsGeneric ? _env.MethodDecl.GenericParameters.Find(g => g.TypeName == p.SwiftTypeSpec.ToString())!.SugaredTypeName : p.SwiftTypeSpec)}");

            string parameters = string.Join(", ", methodParams);

            // Build the argument forwarding list
            var methodCallArgs = string.Join(", ", _env.MethodDecl.CSSignature.Skip(1)
                .Select(p => p.Name switch
                {
                    var n when n.StartsWith("arg") => n,
                    var n when n.StartsWith("_") => $"{n.Substring(1)}: {n}",
                    var n => $"{n}: {n}"
                }));

            var genericParams = _env.MethodDecl.IsGeneric
                ? $"<{string.Join(", ", _env.MethodDecl.GenericParameters.Select(p => p.SugaredTypeName))}>"
                : "";

            var whereClause = (_env.MethodDecl.IsGeneric && _env.MethodDecl.GenericParameters.Any(p => p.GenericConformances.Any() || p.AssosiatedTypeConformances.Any()))
                ? " where " + string.Join(", ", _env.MethodDecl.GenericParameters.Select(p =>
                {
                    var genericConformances = p.GenericConformances
                        .Select(gc => $"{p.SugaredTypeName} : {gc.ConformanceTarget.Name}");
                    var typeConformances = p.AssosiatedTypeConformances
                        .Select(tc => $"{p.SugaredTypeName}.{string.Join(".", tc.Path.Skip(1))} == {tc.ConformanceTarget.Name}");
                    return string.Join(", ", genericConformances.Concat(typeConformances));
                }))
                : "";

            if (parentTypeName != null)
            {
                if (isAccessor)
                {
                    // Property getter wrapper - strip the _Get/_Set suffix to get the Swift property name
                    var propertyName = _env.MethodDecl.Name;
                    if (propertyName.EndsWith("_Get")) propertyName = propertyName.Substring(0, propertyName.Length - 4);
                    else if (propertyName.EndsWith("_Set")) propertyName = propertyName.Substring(0, propertyName.Length - 4);
                    var staticModifier = !isInstanceMethod ? "static " : "";
                    swiftWriter.WriteLine($$"""
            extension {{parentTypeName.ModuleQualifiedName}} {
                @_silgen_name("{{NameProvider.GetMangledName(_env.MethodDecl)}}")
                public {{staticModifier}}var _sb_{{propertyName}}: {{anyReturnType}} {
                    return {{(!isInstanceMethod ? parentTypeName.ModuleQualifiedName + "." : "self.")}}{{propertyName}}
                }
            }
            """);
                }
                else
                {
                    // Method wrapper
                    var staticModifier = !isInstanceMethod ? "static " : "";
                    var callPrefix = !isInstanceMethod ? $"{parentTypeName.ModuleQualifiedName}." : "self.";
                    swiftWriter.WriteLine($$"""
            extension {{parentTypeName.ModuleQualifiedName}} {
                @_silgen_name("{{NameProvider.GetMangledName(_env.MethodDecl)}}")
                public {{staticModifier}}func {{NameProvider.GetPInvokeName(_env.MethodDecl)}}{{genericParams}}({{parameters}}) -> {{anyReturnType}}{{whereClause}} {
                    return {{callPrefix}}{{_env.MethodDecl.Name}}({{methodCallArgs}})
                }
            }
            """);
                }
            }
            else
            {
                // Free function wrapper (module-level)
                var moduleName = _env.MethodDecl.ModuleDecl?.Name ?? "";
                swiftWriter.WriteLine($$"""
            @_silgen_name("{{NameProvider.GetMangledName(_env.MethodDecl)}}")
            public func {{NameProvider.GetPInvokeName(_env.MethodDecl)}}{{genericParams}}({{parameters}}) -> {{anyReturnType}}{{whereClause}} {
                return {{(moduleName.Length > 0 ? moduleName + "." : "")}}{{_env.MethodDecl.Name}}({{methodCallArgs}})
            }
            """);
            }
        }

        /// <summary>
        /// Emits bound generic argument marshalling.
        /// Skips arguments that have type conversion (those are handled by EmitTypeConversions).
        /// </summary>
        private void EmitBoundGenericArguments(CSharpWriter csWriter)
        {
            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1).Where(_env.BoundGenericsHandler.IsBoundGeneric))
            {
                // Skip if this argument uses type conversion (already handled in EmitTypeConversions)
                if (!_env.MethodDecl.IsAccessor && _env.TypeConversionHandler.IsConvertibleType(argumentDecl.SwiftTypeSpec))
                    continue;

                if (_env.BoundGenericsHandler.RequiresBoundGenericMarshalling(argumentDecl))
                {
                    var csName = NameProvider.GetCSharpParameterName(argumentDecl);
                    var bufferName = NameProvider.GetBoundGenericBufferName(csName);

                    // Bug #8: Check if the bound generic's root type is a frozen struct projected as class
                    // (has PayloadBuffer). Non-frozen generic types (like BatchedCollectionIndex<T0>)
                    // use SwiftSafeHandle and should be marshalled via .Payload.DangerousGetHandle().
                    var rootTypeName = SwiftTypeName.FromTypeSpec((NamedTypeSpec)argumentDecl.SwiftTypeSpec);
                    if (_env.TypeDatabase.TryGetTypeRecord(rootTypeName, out var argTypeRecord) &&
                        MarshallingHelpers.IsFrozenStructProjectedAsClass(argTypeRecord))
                    {
                        csWriter.WriteLine($"using PayloadBuffer<IntPtr> {csName}Disposable = {csName}.PayloadBuffer;");
                        csWriter.WriteLine($"IntPtr {bufferName} = {csName}Disposable.Buffer;");
                    }
                    else
                    {
                        // Non-frozen type: use handle-based marshalling
                        csWriter.WriteLine($"IntPtr {bufferName} = {csName}.Payload.DangerousGetHandle();");
                    }
                }
            }
        }

        /// <summary>
        /// Emits closure argument marshalling.
        /// For @convention(c) closures, converts C# delegates to unmanaged function pointers.
        /// For escaping closures, creates closure data with a thunk and GCHandle context.
        /// For optional closures, handles null by creating a zero-initialized SwiftClosureData.
        /// </summary>
        private void EmitClosureMarshalling(CSharpWriter csWriter)
        {
            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1).Where(_env.ClosureHandler.IsClosure))
            {
                var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argumentDecl)!;
                if (!_env.ClosureHandler.IsSupportedClosure(closureTypeSpec))
                    continue;

                var csName = NameProvider.GetCSharpParameterName(argumentDecl);
                bool isOptional = _env.ClosureHandler.IsOptionalClosure(argumentDecl.SwiftTypeSpec);

                if (_env.ClosureHandler.IsConventionC(closureTypeSpec))
                {
                    // For @convention(c) closures, convert delegate to function pointer
                    var funcPtrType = _env.ClosureHandler.GetPInvokeFunctionPointerType(closureTypeSpec);

                    if (isOptional)
                    {
                        // Optional @convention(c) closure - handle null case
                        csWriter.WriteLines($"""
                            var {csName}FuncPtr = {csName} != null
                                ? ({funcPtrType})Marshal.GetFunctionPointerForDelegate({csName})
                                : ({funcPtrType})IntPtr.Zero;
                            """);
                    }
                    else
                    {
                        // Marshal.GetFunctionPointerForDelegate returns IntPtr, cast to the proper function pointer type
                        csWriter.WriteLine($"var {csName}FuncPtr = ({funcPtrType})Marshal.GetFunctionPointerForDelegate({csName});");
                    }
                }
                else if (_env.ClosureHandler.IsAsyncThrowingClosure(closureTypeSpec))
                {
                    // Async+throwing closures use a special pattern with AsyncThrowingClosureState
                    // The state holds the user's async delegate, and we pass context + start function to Swift
                    ClosureEmitter.EmitAsyncThrowingClosureMarshallingSetup(
                        csWriter,
                        _env.MethodDecl.Name,
                        csName,
                        closureTypeSpec,
                        _env.ClosureHandler,
                        _env.MethodDecl.MangledName);
                }
                else if (_env.ClosureHandler.RequiresThunk(closureTypeSpec))
                {
                    if (_env.MethodDecl.HasClosureCdeclWrapper)
                    {
                        // Cdecl wrapper: just allocate the GCHandle if closure is non-null.
                        // The call-argument mapping (MethodSignature) handles passing func ptr and context.
                        if (isOptional)
                        {
                            csWriter.WriteLine($"if ({csName} != null)");
                            csWriter.Indent++;
                            csWriter.WriteLine($"{csName}Handle = GCHandle.Alloc({csName});");
                            csWriter.Indent--;
                        }
                        else
                        {
                            csWriter.WriteLine($"{csName}Handle = GCHandle.Alloc({csName});");
                        }
                    }
                    else
                    {
                        // Legacy SwiftClosureData path (for async methods with non-async closures)
                        var callbackName = ClosureHandler.GetCallbackFunctionName(_env.MethodDecl.Name, argumentDecl.Name, _env.MethodDecl.MangledName);

                        if (isOptional)
                        {
                            // Optional escaping closure - handle null case with zero-initialized SwiftClosureData
                            csWriter.WriteLine($"SwiftClosureData {csName}Closure;");
                            csWriter.WriteLine($"if ({csName} != null)");
                            csWriter.WriteLine("{");
                            csWriter.Indent++;
                            csWriter.WriteLine($"{csName}Handle = GCHandle.Alloc({csName});");
                            csWriter.WriteLine($"{csName}Closure = new SwiftClosureData((IntPtr)s_{callbackName}, GCHandle.ToIntPtr({csName}Handle));");
                            csWriter.Indent--;
                            csWriter.WriteLine("}");
                            csWriter.WriteLine("else");
                            csWriter.WriteLine("{");
                            csWriter.Indent++;
                            csWriter.WriteLine($"{csName}Closure = default; // Zero-initialized = nil in Swift");
                            csWriter.Indent--;
                            csWriter.WriteLine("}");
                        }
                        else
                        {
                            csWriter.WriteLines($"""
                                {csName}Handle = GCHandle.Alloc({csName});
                                var {csName}Closure = new SwiftClosureData((IntPtr)s_{callbackName}, GCHandle.ToIntPtr({csName}Handle));
                                """);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Emits type conversions for parameters that use idiomatic .NET types.
        /// Converts string -> SwiftString, IEnumerable&lt;T&gt; -> SwiftArray&lt;T&gt;, T? -> SwiftOptional&lt;T&gt;.
        /// Also handles payload buffer creation for bound generic types that have been type-converted.
        /// </summary>
        private void EmitTypeConversions(CSharpWriter csWriter)
        {
            // Skip type conversions for property accessors — property wrapper handles conversion
            if (_env.MethodDecl.IsAccessor)
                return;

            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1))
            {
                var csName = NameProvider.GetCSharpParameterName(argumentDecl);

                if (_env.TypeConversionHandler.IsSwiftString(argumentDecl.SwiftTypeSpec))
                {
                    // string -> SwiftString (using pattern for automatic disposal)
                    csWriter.WriteLine($"using var {csName}Swift = new SwiftString({csName});");
                    csWriter.WriteLine($"using PayloadBuffer<SwiftString.Buffer> {csName}Disposable = {csName}Swift.PayloadBuffer;");
                }
                else if (_env.TypeConversionHandler.IsSwiftArray(argumentDecl.SwiftTypeSpec))
                {
                    // IEnumerable<T> -> SwiftArray<T>
                    var swiftType = _env.TypeConversionHandler.GetSwiftWrapperType(
                        argumentDecl.SwiftTypeSpec,
                        typeSpec => TranslateTypeSpecForConversion(typeSpec));
                    // Check if array element is an existential (public API uses IEnumerable<IProtocol>,
                    // but SwiftArray needs ExistentialContainer elements)
                    var elementTypeSpec = (argumentDecl.SwiftTypeSpec as NamedTypeSpec)?.GenericParameters.FirstOrDefault();
                    if (elementTypeSpec != null && _env.ExistentialHandler.IsExistential(elementTypeSpec))
                    {
                        var protocolList = _env.ExistentialHandler.ToProtocolListTypeSpec(elementTypeSpec);
                        if (protocolList != null && _env.ExistentialHandler.IsSupportedExistential(protocolList))
                        {
                            var containerType = _env.ExistentialHandler.GetCSharpExistentialType(protocolList);
                            csWriter.WriteLine($"var {csName}Containers = {csName}.Select(i => ((Swift.Runtime.ISwiftExistentialConvertible<{containerType}>)i).GetExistentialContainer());");
                            csWriter.WriteLine($"using var {csName}Swift = {swiftType}.FromEnumerable({csName}Containers);");
                        }
                        else
                        {
                            csWriter.WriteLine($"using var {csName}Swift = {swiftType}.FromEnumerable({csName});");
                        }
                    }
                    else if (elementTypeSpec != null && _env.TypeConversionHandler.IsSwiftString(elementTypeSpec))
                    {
                        // Element type converted: public API is IEnumerable<string>,
                        // but SwiftArray<SwiftString>.FromEnumerable needs IEnumerable<SwiftString>
                        // try/finally ensures temporary SwiftStrings are disposed even if FromEnumerable throws
                        csWriter.WriteLine($"var {csName}Converted = {csName}.Select(e => new SwiftString(e)).ToList();");
                        csWriter.WriteLine($"{swiftType} {csName}SwiftInner;");
                        csWriter.WriteLine($"try {{ {csName}SwiftInner = {swiftType}.FromEnumerable({csName}Converted); }}");
                        csWriter.WriteLine($"finally {{ foreach (var _item in {csName}Converted) _item.Dispose(); }}");
                        csWriter.WriteLine($"using var {csName}Swift = {csName}SwiftInner;");
                    }
                    else
                    {
                        csWriter.WriteLine($"using var {csName}Swift = {swiftType}.FromEnumerable({csName});");
                    }
                    // Create payload buffer for P/Invoke (same as bound generic handling)
                    csWriter.WriteLine($"using PayloadBuffer<IntPtr> {csName}Disposable = {csName}Swift.PayloadBuffer;");
                    var bufferName = NameProvider.GetBoundGenericBufferName(csName);
                    csWriter.WriteLine($"IntPtr {bufferName} = {csName}Disposable.Buffer;");
                }
                else if (_env.ExistentialHandler.IsOptionalExistential(argumentDecl.SwiftTypeSpec) &&
                         !_env.ClosureHandler.IsOptionalClosure(argumentDecl.SwiftTypeSpec))
                {
                    // (any Protocol)? -> SwiftOptional<ExistentialContainer> with container extraction
                    // Must extract container from interface before NewSome() since SwiftOptional expects the container type
                    var innerProtocolList = _env.ExistentialHandler.UnwrapOptionalExistential(argumentDecl.SwiftTypeSpec);
                    if (innerProtocolList != null && _env.ExistentialHandler.IsSupportedExistential(innerProtocolList))
                    {
                        var containerType = _env.ExistentialHandler.GetCSharpExistentialType(innerProtocolList);
                        var swiftType = _env.TypeConversionHandler.GetSwiftWrapperType(
                            argumentDecl.SwiftTypeSpec,
                            typeSpec => TranslateTypeSpecForConversion(typeSpec));
                        csWriter.WriteLine($"using var {csName}Swift = {csName} is {{}} {csName}Value");
                        csWriter.WriteLine($"    ? {swiftType}.NewSome(((Swift.Runtime.ISwiftExistentialConvertible<{containerType}>){csName}Value).GetExistentialContainer())");
                        csWriter.WriteLine($"    : {swiftType}.NewNone();");
                        csWriter.WriteLine($"using PayloadBuffer<IntPtr> {csName}Disposable = {csName}Swift.PayloadBuffer;");
                        var bufferName = NameProvider.GetBoundGenericBufferName(csName);
                        csWriter.WriteLine($"IntPtr {bufferName} = {csName}Disposable.Buffer;");
                    }
                }
                else if (_env.TypeConversionHandler.IsSwiftOptional(argumentDecl.SwiftTypeSpec) &&
                         !_env.ClosureHandler.IsOptionalClosure(argumentDecl.SwiftTypeSpec))
                {
                    // B12: Check if inner element is an ObjC-bridged type (e.g., UIViewController)
                    // ObjC types have .Handle property but not ISwiftObject, so SwiftOptional<T> would be invalid.
                    // Emit IntPtr fallback using the ObjC .Handle property.
                    var optNamedTypeB12 = argumentDecl.SwiftTypeSpec as NamedTypeSpec;
                    var innerElementB12 = optNamedTypeB12?.GenericParameters.FirstOrDefault();
                    if (innerElementB12 is NamedTypeSpec innerNamedB12 && innerNamedB12.HasModule() &&
                        TypeDatabaseExtensions.IsObjCModuleType(innerNamedB12))
                    {
                        var bufferName = NameProvider.GetBoundGenericBufferName(csName);
                        csWriter.WriteLine($"IntPtr {bufferName} = {csName}?.Handle ?? IntPtr.Zero;");
                    }
                    else
                    {
                    // T? -> SwiftOptional<T> (but not for optional closures - those are handled by EmitClosureSetup)
                    // Use pattern matching which works for both nullable value types and reference types
                    var swiftType = _env.TypeConversionHandler.GetSwiftWrapperType(
                        argumentDecl.SwiftTypeSpec,
                        typeSpec => TranslateTypeSpecForConversion(typeSpec));
                    // Check if inner element type was converted (e.g., string → SwiftString, IReadOnlyList → SwiftArray)
                    var optNamedType = argumentDecl.SwiftTypeSpec as NamedTypeSpec;
                    var innerElementSpec = optNamedType?.GenericParameters.FirstOrDefault();
                    if (innerElementSpec != null && _env.TypeConversionHandler.IsSwiftString(innerElementSpec))
                    {
                        // Public API is string?, but SwiftOptional<SwiftString>.NewSome needs SwiftString
                        // Use named intermediate so the temporary SwiftString is deterministically disposed
                        csWriter.WriteLine($"using var {csName}Str = {csName} is {{}} {csName}Value ? new SwiftString({csName}Value) : null;");
                        csWriter.WriteLine($"using var {csName}Swift = {csName}Str != null ? {swiftType}.NewSome({csName}Str) : {swiftType}.NewNone();");
                    }
                    else if (innerElementSpec is NamedTypeSpec innerNamed && _env.TypeConversionHandler.IsSwiftArray(innerNamed))
                    {
                        // Public API is IReadOnlyList<T>?, but SwiftOptional<SwiftArray<T>>.NewSome needs SwiftArray<T>
                        // Convert using FromEnumerable before wrapping in Optional
                        var rawArrayElement = _env.TypeConversionHandler.GetRawArrayElementType(innerNamed);
                        string arrayConversion;
                        if (rawArrayElement != null)
                        {
                            // Check if element type needs conversion (e.g., string → SwiftString)
                            var innerArrayElementSpec = innerNamed.GenericParameters.FirstOrDefault();
                            if (innerArrayElementSpec != null && _env.TypeConversionHandler.IsSwiftString(innerArrayElementSpec))
                                arrayConversion = $"SwiftArray<{rawArrayElement}>.FromEnumerable({csName}Value.Select(e => new SwiftString(e)))";
                            else
                                arrayConversion = $"SwiftArray<{rawArrayElement}>.FromEnumerable({csName}Value)";
                        }
                        else
                        {
                            arrayConversion = $"{csName}Value";
                        }
                        csWriter.WriteLine($"using var {csName}Swift = {csName} is {{}} {csName}Value ? {swiftType}.NewSome({arrayConversion}) : {swiftType}.NewNone();");
                    }
                    else
                    {
                        csWriter.WriteLine($"using var {csName}Swift = {csName} is {{}} {csName}Value ? {swiftType}.NewSome({csName}Value) : {swiftType}.NewNone();");
                    }
                    // Create payload for P/Invoke
                    if (_env.MethodDecl.HasOptionalPointerWrapper &&
                        _env.BoundGenericsHandler.IsLargeOptionalParam(argumentDecl.SwiftTypeSpec))
                    {
                        // Pass pointer to the full Optional buffer — Swift wrapper dereferences via .pointee
                        var bufferName = NameProvider.GetBoundGenericBufferName(csName);
                        csWriter.WriteLine($"IntPtr {bufferName} = {csName}Swift.Payload.DangerousGetHandle();");
                    }
                    else
                    {
                        // Original path for small Optionals (e.g., Optional<Int32>)
                        csWriter.WriteLine($"using PayloadBuffer<IntPtr> {csName}Disposable = {csName}Swift.PayloadBuffer;");
                        var bufferName = NameProvider.GetBoundGenericBufferName(csName);
                        csWriter.WriteLine($"IntPtr {bufferName} = {csName}Disposable.Buffer;");
                    }
                    } // end else (non-ObjC optional element)
                }
                else if (_env.TypeConversionHandler.HasNativeTypeRemapping(argumentDecl.SwiftTypeSpec))
                {
                    // Native type remapping: Foundation.NSUrl -> Swift.URL, Foundation.NSData -> Swift.Data
                    var conversion = _env.TypeConversionHandler.GetNativeParameterConversion(csName, argumentDecl.SwiftTypeSpec);
                    if (conversion != null)
                    {
                        if (_env.TypeConversionHandler.IsFoundationURL(argumentDecl.SwiftTypeSpec))
                        {
                            // URL is non-frozen and requires disposal
                            csWriter.WriteLine($"using var {csName}Swift = {conversion};");
                        }
                        else
                        {
                            // Data is a frozen struct
                            csWriter.WriteLine($"var {csName}Swift = {conversion};");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Translates a TypeSpec to C# type name for use in type conversion handlers.
        /// Handles generic types by translating their type parameters.
        /// </summary>
        private string TranslateTypeSpecForConversion(TypeSpec typeSpec)
        {
            // Handle existential types (ProtocolListTypeSpec and NamedTypeSpec with IsAny)
            if (_env.ExistentialHandler.IsExistential(typeSpec))
            {
                var protocolList = _env.ExistentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null && _env.ExistentialHandler.IsSupportedExistential(protocolList))
                    return _env.ExistentialHandler.GetCSharpExistentialType(protocolList);
                return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
            }

            if (typeSpec is NamedTypeSpec namedTypeSpec)
            {
                // Check if this is a generic type parameter that can be resolved
                if (TypeSpecHelpers.IsGenericTypeParameter(namedTypeSpec.Name) &&
                    _genericContext.TryResolve(namedTypeSpec.Name, out var csName))
                {
                    return csName;
                }

                var typeRecord = _env.TypeDatabase.GetTypeRecordOrAnyType(namedTypeSpec);

                // If the type falls back to AnyType or is IntPtr (pointer types), don't append generic parameters
                // Pointer types like UnsafeMutablePointer<T> resolve to IntPtr which doesn't support generics
                if (typeRecord == TypeDatabaseExtensions.AnyType ||
                    typeRecord == TypeDatabaseExtensions.IntPtrType)
                {
                    return typeRecord.CSharpTypeName.FullyQualifiedName;
                }

                // Handle generic parameters
                if (namedTypeSpec.GenericParameters.Count > 0)
                {
                    var translatedParams = namedTypeSpec.GenericParameters
                        .Select(p => TranslateTypeSpecForConversion(p))
                        .ToList();
                    return $"{typeRecord.CSharpTypeName.FullyQualifiedName}<{string.Join(", ", translatedParams)}>";
                }

                return typeRecord.CSharpTypeName.FullyQualifiedName;
            }
            return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        }

        /// <summary>
        /// Emits callback functions and pointers for escaping closures.
        /// When HasClosureCdeclWrapper is set, non-async closure callbacks use CallConvCdecl
        /// instead of CallConvSwift to avoid Mono JIT assertion crashes.
        /// </summary>
        private void EmitClosureCallbacks(CSharpWriter csWriter)
        {
            // Determine if callbacks should use Cdecl calling convention.
            // Async+throwing closures always use their own Cdecl pattern regardless.
            var useCdecl = _env.MethodDecl.HasClosureCdeclWrapper;

            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1).Where(_env.ClosureHandler.IsClosure))
            {
                var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argumentDecl)!;
                if (!_env.ClosureHandler.IsSupportedClosure(closureTypeSpec))
                    continue;

                if (_env.ClosureHandler.RequiresThunk(closureTypeSpec))
                {
                    // Check if this is an async+throwing closure (must check before throwing-only)
                    if (_env.ClosureHandler.IsAsyncThrowingClosure(closureTypeSpec))
                    {
                        // Async+throwing closures use a special "start" callback pattern
                        // The start function is synchronous and spawns Task.Run
                        // These always use their own Cdecl pattern, not gated by useCdecl
                        ClosureEmitter.EmitAsyncThrowingClosureCallbackPointer(csWriter, _env.MethodDecl.Name, argumentDecl.Name, _env.MethodDecl.MangledName);
                        ClosureEmitter.EmitAsyncThrowingClosureCallback(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.MethodDecl.MangledName);
                    }
                    // Check if this is a throwing closure (but not async+throwing)
                    else if (_env.ClosureHandler.IsThrowingClosure(closureTypeSpec))
                    {
                        // Throwing closures need special callback that handles SwiftError
                        ClosureEmitter.EmitThrowingClosureCallbackPointer(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.MethodDecl.MangledName, useCdecl);
                        ClosureEmitter.EmitThrowingClosureCallback(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.MethodDecl.MangledName, useCdecl);
                    }
                    // Check if this closure needs indirect return marshalling
                    else if (_env.ClosureHandler.RequiresIndirectReturnMarshalling(closureTypeSpec))
                    {
                        ClosureEmitter.EmitIndirectReturnCallbackPointer(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.MethodDecl.MangledName, useCdecl);
                        ClosureEmitter.EmitIndirectReturnCallback(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.MethodDecl.MangledName, useCdecl);
                    }
                    else
                    {
                        ClosureEmitter.EmitClosureCallbackPointer(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.MethodDecl.MangledName, useCdecl);
                        ClosureEmitter.EmitEscapingClosureCallback(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.MethodDecl.MangledName, useCdecl);
                    }
                    csWriter.WriteLine();
                }
            }
        }

        /// <summary>
        /// Emits the SafeHandle add reference.
        /// Frozen structs are passed as lowered buffers, so explicit retain is needed.
        /// Non-frozen structs are passed as SafeHandle, so reference counting is managed automatically.
        /// Generics are copied prior to the call via MarshalToSwift, no ref counting is needed on a copy. InitWithCopy is called to create a copy.
        /// </summary>
        private void EmitSafeHandleAddRef(CSharpWriter csWriter)
        {
            if (_env.MethodDecl.MethodType != MethodType.Static && !_env.MethodDecl.IsConstructor)
            {
                if (_env.ParentDecl is StructDecl structDecl)
                {
                    var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
                    if (MarshallingHelpers.RequiresMemoryManagement(typeRecord) || !MarshallingHelpers.IsTypeFrozen(typeRecord))
                    {
                        csWriter.WriteLine($"var success = false;");
                        csWriter.WriteLine($"_payload.DangerousAddRef(ref success);");
                    }
                }
                else if (_env.ParentDecl is ClassDecl)
                {
                    // Swift classes always need ref counting - they use _payload SafeHandle
                    csWriter.WriteLine($"var success = false;");
                    csWriter.WriteLine($"_payload.DangerousAddRef(ref success);");
                }
            }

            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1).Where(a => !a.IsGeneric && !_env.BoundGenericsHandler.IsBoundGeneric(a) && !_env.ClosureHandler.IsClosure(a) && !_env.TupleHandler.IsTuple(a) && !_env.ExistentialHandler.IsExistential(a) && (_env.MethodDecl.IsAccessor || !_env.TypeConversionHandler.IsConvertibleType(a.SwiftTypeSpec))))
            {
                TypeRecord typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(argumentDecl.SwiftTypeSpec);
                var csName = NameProvider.GetCSharpParameterName(argumentDecl);

                // ObjC bridged types: extract Handle from .NET iOS binding object
                if (MarshallingHelpers.IsObjCBridged(typeRecord))
                {
                    csWriter.WriteLine($"IntPtr {csName}Handle = {csName}?.Handle ?? IntPtr.Zero;");
                    continue;
                }

                if (MarshallingHelpers.IsFrozenStructProjectedAsClass(typeRecord))
                {
                    csWriter.WriteLine($"using PayloadBuffer<{typeRecord.CSharpTypeName}.Buffer> {csName}Disposable = {csName}.PayloadBuffer;");
                }
            }

            // NOTE: For async methods, non-frozen parameter copy buffers are created in EmitAsync
            // (before the GCHandle holder) using InitializeWithCopy. The {param}Handle and
            // {param}CopyBuffer variables are already declared there. Nothing more to do here.
        }

        /// <summary>
        /// Emits the SafeHandle release.
        /// Frozen structs are passed as lowered buffers, so explicit release is needed.
        /// Non-frozen structs are passed as SafeHandle, so reference counting is managed automatically.
        /// Generics are copied prior to the call via MarshalToSwift, no ref counting is needed on a copy; Destroy is called on the copy.
        ///
        /// For async instance methods, DangerousRelease is deferred until the async callback fires.
        /// This prevents the SafeHandle from being released while the Swift async Task is still running.
        /// </summary>
        private void EmitSafeHandleRelease(CSharpWriter csWriter)
        {
            // For async instance methods, skip immediate release - the callback will handle it
            // via DeferredSafeHandleRelease stored in the async holder
            if (_env.MethodDecl.IsAsync && _env.MethodDecl.MethodType != MethodType.Static && !_env.MethodDecl.IsConstructor)
            {
                // Async instance methods defer release to callback
                return;
            }

            if (_env.MethodDecl.MethodType != MethodType.Static && !_env.MethodDecl.IsConstructor)
            {
                if (_env.ParentDecl is StructDecl structDecl)
                {
                    var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
                    if (MarshallingHelpers.RequiresMemoryManagement(typeRecord) || !MarshallingHelpers.IsTypeFrozen(typeRecord))
                    {
                        csWriter.WriteLine($"if (success)");
                        csWriter.WriteLine($"   _payload.DangerousRelease();");
                    }
                }
                else if (_env.ParentDecl is ClassDecl)
                {
                    // Swift classes always need ref counting - they use _payload SafeHandle
                    csWriter.WriteLine($"if (success)");
                    csWriter.WriteLine($"   _payload.DangerousRelease();");
                }
            }

            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1))
            {
                var csName = NameProvider.GetCSharpParameterName(argumentDecl);

                if (argumentDecl.IsGeneric)
                {
                    var csTypeParamName = _env.GenericTypeMapping[argumentDecl.SwiftTypeSpec.ToString()].TypeParameter;
                    var metadataName = NameProvider.GetMetadataName(csTypeParamName);
                    var payloadName = NameProvider.GetPayloadName(csName);
                    csWriter.WriteLine($"{metadataName}.ValueWitnessTable->Destroy((void *){payloadName}, {metadataName});");
                    continue;
                }

                // Free GCHandle for escaping closures
                // Note: Async+throwing closures free their GCHandle inside Task.Run's finally block
                if (_env.ClosureHandler.IsClosure(argumentDecl))
                {
                    var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argumentDecl)!;
                    if (_env.ClosureHandler.IsSupportedClosure(closureTypeSpec) &&
                        _env.ClosureHandler.RequiresThunk(closureTypeSpec) &&
                        !_env.ClosureHandler.IsAsyncThrowingClosure(closureTypeSpec))
                    {
                        csWriter.WriteLine($"if ({csName}Handle.IsAllocated) {csName}Handle.Free();");
                    }
                }
            }

            // NOTE: Async non-frozen parameters are NOT released here.
            // They are kept alive by the GCHandle (in the object[] holder) until the callback fires.
            // This prevents SIGSEGV crashes caused by GC finalizing the parameter while Swift's
            // async Task is still pending and may access copy-on-write shared storage.
        }

        /// <summary>
        /// Emits the generic arguments setup.
        /// </summary>
        private void EmitGenericArguments(CSharpWriter csWriter)
        {
            foreach (var argument in _env.MethodDecl.CSSignature.Skip(1).Where(a => a.IsGeneric))
            {
                var csName = NameProvider.GetCSharpParameterName(argument);
                var csTypeParamName = _env.GenericTypeMapping[argument.SwiftTypeSpec.ToString()].TypeParameter;
                var metadataName = NameProvider.GetMetadataName(csTypeParamName);
                var payloadName = NameProvider.GetPayloadName(csName);

                var text = $$"""
                Span<byte> {{payloadName}}Span = stackalloc byte[(int){{metadataName}}.Size];
                {{payloadName}} = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference({{payloadName}}Span));
                SwiftMarshal.MarshalToSwift({{csName}}, ref {{payloadName}}Span);
                """;
                csWriter.WriteLines(text);
            }
            csWriter.WriteLine();
        }

        /// <summary>
        /// After a P/Invoke call, writes back modified generic inout payloads to the caller's ref parameters.
        /// Without this, mutations made by Swift to ref generic parameters would be lost.
        /// </summary>
        private void EmitGenericInoutWriteback(CSharpWriter csWriter)
        {
            foreach (var argument in _env.MethodDecl.CSSignature.Skip(1).Where(a => a.IsGeneric && a.IsInOut))
            {
                var csName = NameProvider.GetCSharpParameterName(argument);
                var csTypeParamName = _env.GenericTypeMapping[argument.SwiftTypeSpec.ToString()].TypeParameter;
                var payloadName = NameProvider.GetPayloadName(csName);

                csWriter.WriteLine($"// Write back modified inout generic parameter");
                csWriter.WriteLine($"{csName} = SwiftMarshal.MarshalFromSwift<{csTypeParamName}>({payloadName});");
            }
        }

        private void EmitProtocolWitnessTables(CSharpWriter csWriter)
        {
            foreach (var genericParameter in _env.MethodDecl.GenericParameters)
            {
                var csTypeParamName = _env.GenericTypeMapping[genericParameter.TypeName].TypeParameter;
                var conformances = genericParameter.GenericConformances.OrderBy(c => c.ConformanceTarget.ModuleQualifiedName);
                foreach (var conformance in conformances)
                {
                    // Skip unknown protocols and protocols with associated types
                    // (protocols with associated types generate generic interfaces which can't be used here)
                    if (!IsProtocolAvailableForConstraint(conformance.ConformanceTarget))
                        continue;

                    var pwtName = NameProvider.GetProtocolWitnessTableName(csTypeParamName, conformance.ConformanceTarget.Name);
                    var protocolName = NameProvider.GetInterfaceName(conformance.ConformanceTarget.Name, moduleName: conformance.ConformanceTarget.Module);
                    csWriter.WriteLine($"var {pwtName} = ProtocolWitnessTable.GetOrThrow<{csTypeParamName}, {protocolName}>();");
                }
            }
            csWriter.WriteLine();
        }
    }
}
