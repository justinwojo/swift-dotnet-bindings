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
            var opaqueDerefLines = new List<string>();
            var methodParams = _env.MethodDecl.CSSignature
                .Skip(1)
                .Select(p =>
                {
                    if (OptionalPointerWrapperEmitter.ShouldWidenParam(p, _env.BoundGenericsHandler))
                    {
                        opaqueDerefLines.Add(OptionalPointerWrapperEmitter.GetDerefCode(p, p.Name, p.Name));
                        return $"{p.Name}: UnsafeRawPointer";
                    }
                    return $"{p.Name}: {(p.IsGeneric ? _env.MethodDecl.GenericParameters.Find(g => g.TypeName == p.SwiftTypeSpec.ToString())!.SugaredTypeName : p.SwiftTypeSpec)}";
                });

            string parameters = string.Join(", ", methodParams);

            // Build the argument forwarding list
            var methodCallArgs = string.Join(", ", _env.MethodDecl.CSSignature.Skip(1)
                .Select(p =>
                {
                    var valueRef = OptionalPointerWrapperEmitter.ShouldWidenParam(p, _env.BoundGenericsHandler)
                        ? $"{p.Name}Val" : p.Name;
                    return p.Name switch
                    {
                        var n when n.StartsWith("arg") => valueRef,
                        var n when n.StartsWith("_") => $"{n.Substring(1)}: {valueRef}",
                        _ => $"{p.Name}: {valueRef}"
                    };
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

            // Pre-format deref lines for insertion into raw string templates
            var extDerefCode = opaqueDerefLines.Count > 0
                ? string.Join("\n                    ", opaqueDerefLines) + "\n                    "
                : "";
            var freeDerefCode = opaqueDerefLines.Count > 0
                ? string.Join("\n                ", opaqueDerefLines) + "\n                "
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
                    {{extDerefCode}}return {{callPrefix}}{{_env.MethodDecl.Name}}({{methodCallArgs}})
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
                {{freeDerefCode}}return {{(moduleName.Length > 0 ? moduleName + "." : "")}}{{_env.MethodDecl.Name}}({{methodCallArgs}})
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

                // Skip Optional<existential> — handled by dedicated existential marshalling path
                if (_env.ExistentialHandler.IsOptionalExistential(argumentDecl.SwiftTypeSpec))
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
        /// Uses projection-first approach: tries TypeProjectionFactory, falls back to inline code.
        /// </summary>
        private void EmitTypeConversions(CSharpWriter csWriter)
        {
            // Skip type conversions for property accessors — property wrapper handles conversion
            // EXCEPT Optional-existential parameters which need container extraction here
            if (_env.MethodDecl.IsAccessor)
            {
                foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1))
                {
                    if (_env.ExistentialHandler.IsOptionalExistential(argumentDecl.SwiftTypeSpec) &&
                        !_env.ClosureHandler.IsOptionalClosure(argumentDecl.SwiftTypeSpec))
                    {
                        TryEmitParameterConversionViaProjection(csWriter, argumentDecl);
                    }
                }
                return;
            }

            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1))
            {
                if (!_env.TypeConversionHandler.IsConvertibleType(argumentDecl.SwiftTypeSpec) &&
                    !_env.ExistentialHandler.IsOptionalExistential(argumentDecl.SwiftTypeSpec) &&
                    !_env.TypeConversionHandler.HasNativeTypeRemapping(argumentDecl.SwiftTypeSpec))
                    continue;
                if (_env.ClosureHandler.IsOptionalClosure(argumentDecl.SwiftTypeSpec))
                    continue;

                if (!TryEmitParameterConversionViaProjection(csWriter, argumentDecl))
                {
                    // Fallback for types the projection factory can't handle:
                    // - B12 ObjC optional inner (Handle extraction)
                    // - Containers with user-defined bound generic inner types
                    EmitLegacyParameterConversion(csWriter, argumentDecl);
                }
            }
        }

        /// <summary>
        /// Tries to emit parameter conversion via the projection factory.
        /// Returns true if the projection handled the parameter, false if fallback is needed.
        /// </summary>
        private bool TryEmitParameterConversionViaProjection(CSharpWriter csWriter, ArgumentDecl argumentDecl)
        {
            var projection = s_projectionFactory.Project(argumentDecl.SwiftTypeSpec,
                new ProjectionContext { TypeDatabase = _env.TypeDatabase, IsParameter = true, GenericContext = _genericContext });
            if (projection == null)
                return false;

            // B12: ObjC optional inner — factory returns OptionalProjection but we need handle extraction
            if (projection is OptionalProjection optProj)
            {
                var optNamed = argumentDecl.SwiftTypeSpec as NamedTypeSpec;
                var innerElement = optNamed?.GenericParameters.FirstOrDefault();
                if (innerElement is NamedTypeSpec innerNamed && innerNamed.HasModule() &&
                    TypeDatabaseExtensions.IsObjCModuleType(innerNamed))
                    return false; // Fall back to inline ObjC handle extraction
            }

            var csName = NameProvider.GetCSharpParameterName(argumentDecl);

            // Check if large Optional param needs DangerousGetHandle override
            if (projection is OptionalProjection optProjForHandle)
            {
                bool needsLargeOptOverride = _env.BoundGenericsHandler.IsLargeOptionalParam(argumentDecl.SwiftTypeSpec) &&
                    (_env.MethodDecl.HasOptionalPointerWrapper || _env.MethodDecl.UsesWrapperLibrary ||
                     _env.MethodDecl.IsAsync || _requiresOpaqueReturnWrapper);
                if (needsLargeOptOverride)
                {
                    projection = new OptionalProjection(optProjForHandle.InnerProjection, optProjForHandle.IsExistentialInner, useDangerousGetHandle: true);
                }
            }

            var plan = projection.GetParameterPlan(csName);
            MarshalPlanRenderer.RenderStatements(csWriter, plan.SetupStatements);
            return true;
        }

        /// <summary>
        /// Legacy fallback for parameter conversion when the projection factory returns null.
        /// Handles containers with user-defined bound generic inner types and B12 ObjC optional inner.
        /// </summary>
        private void EmitLegacyParameterConversion(CSharpWriter csWriter, ArgumentDecl argumentDecl)
        {
            var csName = NameProvider.GetCSharpParameterName(argumentDecl);

            if (_env.TypeConversionHandler.IsSwiftArray(argumentDecl.SwiftTypeSpec))
            {
                var swiftType = _env.TypeConversionHandler.GetSwiftWrapperType(
                    argumentDecl.SwiftTypeSpec,
                    typeSpec => TranslateTypeSpecForConversion(typeSpec));
                var elementTypeSpec = (argumentDecl.SwiftTypeSpec as NamedTypeSpec)?.GenericParameters.FirstOrDefault();
                if (elementTypeSpec != null && _env.TypeConversionHandler.IsSwiftString(elementTypeSpec))
                {
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
                csWriter.WriteLine($"using var {csName}Disposable = {csName}Swift.PayloadBuffer;");
                var bufferName = NameProvider.GetBoundGenericBufferName(csName);
                csWriter.WriteLine($"IntPtr {bufferName} = {csName}Disposable.Buffer;");
            }
            else if (_env.TypeConversionHandler.IsSwiftDictionary(argumentDecl.SwiftTypeSpec))
            {
                var swiftType = _env.TypeConversionHandler.GetSwiftWrapperType(
                    argumentDecl.SwiftTypeSpec,
                    typeSpec => TranslateTypeSpecForConversion(typeSpec));
                var dictTypeSpec = argumentDecl.SwiftTypeSpec as NamedTypeSpec;
                var keyTypeSpec = dictTypeSpec?.GenericParameters.FirstOrDefault();
                var valueTypeSpec = dictTypeSpec?.GenericParameters.Count > 1 ? dictTypeSpec.GenericParameters[1] : null;
                bool keyIsString = keyTypeSpec != null && _env.TypeConversionHandler.IsSwiftString(keyTypeSpec);
                bool valueIsString = valueTypeSpec != null && _env.TypeConversionHandler.IsSwiftString(valueTypeSpec);
                bool valueIsArray = valueTypeSpec is NamedTypeSpec valArraySpec && _env.TypeConversionHandler.IsSwiftArray(valArraySpec);
                bool keyConverted = keyTypeSpec != null && _env.TypeConversionHandler.IsDictionaryKeyTypeConverted(dictTypeSpec!,
                    typeSpec => TranslateTypeSpecForConversion(typeSpec));
                bool valueConverted = valueTypeSpec != null && _env.TypeConversionHandler.IsDictionaryValueTypeConverted(dictTypeSpec!,
                    typeSpec => TranslateTypeSpecForConversion(typeSpec));

                if (keyConverted || valueConverted)
                {
                    var keyExpr = keyIsString ? $"new SwiftString(kvp.Key)" : "kvp.Key";
                    string valueExpr;
                    if (valueIsString) valueExpr = "new SwiftString(kvp.Value)";
                    else if (valueIsArray) valueExpr = GetDictValueArrayConversion("kvp.Value", (NamedTypeSpec)valueTypeSpec!);
                    else valueExpr = "kvp.Value";
                    var rawKeyType = _env.TypeConversionHandler.GetRawDictionaryKeyType(dictTypeSpec!,
                        typeSpec => TranslateTypeSpecForConversion(typeSpec));
                    var rawValueType = _env.TypeConversionHandler.GetRawDictionaryValueType(dictTypeSpec!,
                        typeSpec => TranslateTypeSpecForConversion(typeSpec));
                    csWriter.WriteLine($"var {csName}Converted = {csName}.Select(kvp => new KeyValuePair<{rawKeyType}, {rawValueType}>({keyExpr}, {valueExpr})).ToList();");
                    csWriter.WriteLine($"{swiftType} {csName}SwiftInner;");
                    var disposeStatements = new List<string>();
                    if (keyIsString) disposeStatements.Add($"_item.Key.Dispose()");
                    if (valueIsString || valueIsArray) disposeStatements.Add($"_item.Value.Dispose()");
                    var disposeExpr = string.Join("; ", disposeStatements);
                    csWriter.WriteLine($"try {{ {csName}SwiftInner = {swiftType}.FromDictionary({csName}Converted); }}");
                    csWriter.WriteLine($"finally {{ foreach (var _item in {csName}Converted) {{ {disposeExpr}; }} }}");
                    csWriter.WriteLine($"using var {csName}Swift = {csName}SwiftInner;");
                }
                else
                {
                    csWriter.WriteLine($"using var {csName}Swift = {swiftType}.FromDictionary({csName});");
                }
                csWriter.WriteLine($"using var {csName}Disposable = {csName}Swift.PayloadBuffer;");
                var dictBufName = NameProvider.GetBoundGenericBufferName(csName);
                csWriter.WriteLine($"IntPtr {dictBufName} = {csName}Disposable.Buffer;");
            }
            else if (_env.TypeConversionHandler.IsSwiftOptional(argumentDecl.SwiftTypeSpec))
            {
                var optNamedType = argumentDecl.SwiftTypeSpec as NamedTypeSpec;
                var innerElement = optNamedType?.GenericParameters.FirstOrDefault();

                // B12 ObjC optional inner
                if (innerElement is NamedTypeSpec innerNamed && innerNamed.HasModule() &&
                    TypeDatabaseExtensions.IsObjCModuleType(innerNamed))
                {
                    var bufferName = NameProvider.GetBoundGenericBufferName(csName);
                    csWriter.WriteLine($"IntPtr {bufferName} = {csName}?.Handle ?? IntPtr.Zero;");
                }
                else
                {
                    // Generic Optional with unsupported inner type — use TranslateBound fallback
                    var swiftType = _env.TypeConversionHandler.GetSwiftWrapperType(
                        argumentDecl.SwiftTypeSpec,
                        typeSpec => TranslateTypeSpecForConversion(typeSpec));

                    if (innerElement is NamedTypeSpec innerArrayNamed && _env.TypeConversionHandler.IsSwiftArray(innerArrayNamed))
                    {
                        var rawArrayElement = _env.TypeConversionHandler.GetRawArrayElementType(innerArrayNamed);
                        string arrayConversion;
                        if (rawArrayElement != null)
                        {
                            var innerArrayElementSpec = innerArrayNamed.GenericParameters.FirstOrDefault();
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
                    else if (innerElement is NamedTypeSpec innerDictNamed && _env.TypeConversionHandler.IsSwiftDictionary(innerDictNamed))
                    {
                        var rawDictKey = _env.TypeConversionHandler.GetRawDictionaryKeyType(innerDictNamed,
                            typeSpec => TranslateTypeSpecForConversion(typeSpec));
                        var rawDictValue = _env.TypeConversionHandler.GetRawDictionaryValueType(innerDictNamed,
                            typeSpec => TranslateTypeSpecForConversion(typeSpec));
                        if (rawDictKey != null && rawDictValue != null)
                        {
                            var innerDictKeySpec = innerDictNamed.GenericParameters.FirstOrDefault();
                            var innerDictValueSpec = innerDictNamed.GenericParameters.Count > 1 ? innerDictNamed.GenericParameters[1] : null;
                            bool dictKeyIsString = innerDictKeySpec != null && _env.TypeConversionHandler.IsSwiftString(innerDictKeySpec);
                            bool dictValueIsString = innerDictValueSpec != null && _env.TypeConversionHandler.IsSwiftString(innerDictValueSpec);
                            bool dictValueIsArray = innerDictValueSpec is NamedTypeSpec dictValArraySpec && _env.TypeConversionHandler.IsSwiftArray(dictValArraySpec);
                            bool dictKeyConverted = innerDictKeySpec != null && _env.TypeConversionHandler.IsDictionaryKeyTypeConverted(innerDictNamed,
                                typeSpec => TranslateTypeSpecForConversion(typeSpec));
                            bool dictValueConverted = innerDictValueSpec != null && _env.TypeConversionHandler.IsDictionaryValueTypeConverted(innerDictNamed,
                                typeSpec => TranslateTypeSpecForConversion(typeSpec));
                            if (dictKeyConverted || dictValueConverted)
                            {
                                var kExpr = dictKeyIsString ? $"new SwiftString(kvp.Key)" : "kvp.Key";
                                string vExpr;
                                if (dictValueIsString) vExpr = "new SwiftString(kvp.Value)";
                                else if (dictValueIsArray) vExpr = GetDictValueArrayConversion("kvp.Value", (NamedTypeSpec)innerDictValueSpec!);
                                else vExpr = "kvp.Value";
                                var swiftDictType = $"SwiftDictionary<{rawDictKey}, {rawDictValue}>";
                                var disposeStatements = new List<string>();
                                if (dictKeyIsString) disposeStatements.Add($"_item.Key.Dispose()");
                                if (dictValueIsString || dictValueIsArray) disposeStatements.Add($"_item.Value.Dispose()");
                                var disposeExpr = string.Join("; ", disposeStatements);

                                csWriter.WriteLine($"{swiftType} {csName}SwiftInner;");
                                csWriter.WriteLine($"if ({csName} is {{}} {csName}Value)");
                                csWriter.WriteLine($"{{");
                                csWriter.WriteLine($"    var {csName}Converted = {csName}Value.Select(kvp => new KeyValuePair<{rawDictKey}, {rawDictValue}>({kExpr}, {vExpr})).ToList();");
                                csWriter.WriteLine($"    {swiftDictType} {csName}DictInner;");
                                csWriter.WriteLine($"    try {{ {csName}DictInner = {swiftDictType}.FromDictionary({csName}Converted); }}");
                                csWriter.WriteLine($"    finally {{ foreach (var _item in {csName}Converted) {{ {disposeExpr}; }} }}");
                                csWriter.WriteLine($"    try {{ {csName}SwiftInner = {swiftType}.NewSome({csName}DictInner); }}");
                                csWriter.WriteLine($"    finally {{ {csName}DictInner.Dispose(); }}");
                                csWriter.WriteLine($"}}");
                                csWriter.WriteLine($"else {{ {csName}SwiftInner = {swiftType}.NewNone(); }}");
                                csWriter.WriteLine($"using var {csName}Swift = {csName}SwiftInner;");
                            }
                            else
                            {
                                var swiftDictType = $"SwiftDictionary<{rawDictKey}, {rawDictValue}>";
                                csWriter.WriteLine($"{swiftType} {csName}SwiftInner;");
                                csWriter.WriteLine($"if ({csName} is {{}} {csName}Value)");
                                csWriter.WriteLine($"{{");
                                csWriter.WriteLine($"    using var {csName}Dict = {swiftDictType}.FromDictionary({csName}Value);");
                                csWriter.WriteLine($"    {csName}SwiftInner = {swiftType}.NewSome({csName}Dict);");
                                csWriter.WriteLine($"}}");
                                csWriter.WriteLine($"else {{ {csName}SwiftInner = {swiftType}.NewNone(); }}");
                                csWriter.WriteLine($"using var {csName}Swift = {csName}SwiftInner;");
                            }
                        }
                        else
                        {
                            csWriter.WriteLine($"using var {csName}Swift = {csName} is {{}} {csName}Value ? {swiftType}.NewSome({csName}Value) : {swiftType}.NewNone();");
                        }
                    }
                    else
                    {
                        csWriter.WriteLine($"using var {csName}Swift = {csName} is {{}} {csName}Value ? {swiftType}.NewSome({csName}Value) : {swiftType}.NewNone();");
                    }

                    // Create payload for P/Invoke
                    if (_env.BoundGenericsHandler.IsLargeOptionalParam(argumentDecl.SwiftTypeSpec) &&
                        (_env.MethodDecl.HasOptionalPointerWrapper || _env.MethodDecl.UsesWrapperLibrary ||
                         _env.MethodDecl.IsAsync || _requiresOpaqueReturnWrapper))
                    {
                        var bufferName = NameProvider.GetBoundGenericBufferName(csName);
                        csWriter.WriteLine($"IntPtr {bufferName} = {csName}Swift.Payload.DangerousGetHandle();");
                    }
                    else
                    {
                        csWriter.WriteLine($"using var {csName}Disposable = {csName}Swift.PayloadBuffer;");
                        var bufferName = NameProvider.GetBoundGenericBufferName(csName);
                        csWriter.WriteLine($"IntPtr {bufferName} = {csName}Disposable.Buffer;");
                    }
                }
            }
            else if (_env.TypeConversionHandler.HasNativeTypeRemapping(argumentDecl.SwiftTypeSpec))
            {
                var conversion = _env.TypeConversionHandler.GetNativeParameterConversion(csName, argumentDecl.SwiftTypeSpec);
                if (conversion != null)
                {
                    if (_env.TypeConversionHandler.IsFoundationURL(argumentDecl.SwiftTypeSpec))
                        csWriter.WriteLine($"using var {csName}Swift = {conversion};");
                    else
                        csWriter.WriteLine($"var {csName}Swift = {conversion};");
                }
            }
        }

        /// <summary>
        /// Builds a conversion expression for a dictionary value that is a SwiftArray.
        /// Legacy helper for bound generic fallback paths.
        /// </summary>
        private string GetDictValueArrayConversion(string expr, NamedTypeSpec arraySpec)
        {
            var rawElem = _env.TypeConversionHandler.GetRawArrayElementType(arraySpec,
                typeSpec => TranslateTypeSpecForConversion(typeSpec));
            if (rawElem == null)
                return expr;

            var innerElemSpec = arraySpec.GenericParameters.FirstOrDefault();
            if (innerElemSpec != null && _env.TypeConversionHandler.IsSwiftString(innerElemSpec))
                return $"SwiftArray<{rawElem}>.FromEnumerable({expr}.Select(e => new SwiftString(e)))";
            return $"SwiftArray<{rawElem}>.FromEnumerable({expr})";
        }

        /// <summary>
        /// Translates a TypeSpec to C# type name for use in type conversion handlers.
        /// Legacy helper for bound generic fallback paths.
        /// </summary>
        private string TranslateTypeSpecForConversion(TypeSpec typeSpec)
        {
            if (_env.ExistentialHandler.IsExistential(typeSpec))
            {
                var protocolList = _env.ExistentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null && _env.ExistentialHandler.IsSupportedExistential(protocolList))
                    return _env.ExistentialHandler.GetCSharpExistentialType(protocolList);
                return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
            }

            if (typeSpec is NamedTypeSpec namedTypeSpec)
            {
                if (TypeSpecHelpers.IsGenericTypeParameter(namedTypeSpec.Name) &&
                    _genericContext.TryResolve(namedTypeSpec.Name, out var csName))
                {
                    return csName;
                }

                var typeRecord = _env.TypeDatabase.GetTypeRecordOrAnyType(namedTypeSpec);
                if (typeRecord == TypeDatabaseExtensions.AnyType ||
                    typeRecord == TypeDatabaseExtensions.IntPtrType)
                {
                    return typeRecord.CSharpTypeName.FullyQualifiedName;
                }

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
            foreach (var line in _syncPlan.GenericArgumentMarshallingLines)
                csWriter.WriteLine(line);
            csWriter.WriteLine();
        }

        /// <summary>
        /// After a P/Invoke call, writes back modified generic inout payloads to the caller's ref parameters.
        /// Without this, mutations made by Swift to ref generic parameters would be lost.
        /// </summary>
        private void EmitGenericInoutWriteback(CSharpWriter csWriter)
        {
            foreach (var line in _syncPlan.GenericInoutWritebackLines)
                csWriter.WriteLine(line);
        }

        private void EmitProtocolWitnessTables(CSharpWriter csWriter)
        {
            foreach (var line in _syncPlan.WitnessTableStatements)
                csWriter.WriteLine(line);
            csWriter.WriteLine();
        }
    }
}
