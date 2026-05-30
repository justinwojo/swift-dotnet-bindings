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
    /// Checks if a method with optional pointer wrapper can be converted to @_cdecl.
    /// Uses shared function-level gates plus per-param checks on non-large params.
    /// </summary>
    public static bool CanConvertToCdecl(MethodEnvironment env)
    {
        if (!MethodWrapperEmitter.HasCdeclCompatibleFunctionShape(env))
            return false;
        // Per-param checks on NON-LARGE params only (large params are already UnsafeRawPointer)
        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            // Metatype check runs BEFORE the large-optional skip: AnyClass.Type? would otherwise
            // be marshalled as UnsafeRawPointer and the wrapper body would still try to render
            // the bare metatype token. Reject upfront — same boundary as the primary wrapper gate.
            if (WrapperValidation.IsMetatypeTypeIncludingOptional(arg.SwiftTypeSpec))
                return false;
            if (env.BoundGenericsHandler.IsLargeOptionalParam(arg.SwiftTypeSpec))
                continue;
            if (arg.IsGeneric) return false;
            // Closure params (including Optional<Closure>) need funcPtr+context decomposition
            // that @_cdecl GetCdeclParamMapping doesn't support. Fall back to @_silgen_name
            // where closures pass as native Swift types.
            if (env.ClosureHandler.IsClosure(arg)) return false;
            if (CdeclParamMapper.IsProtocolExistentialType(arg.SwiftTypeSpec, env.TypeDatabase))
                return false;
            if (MethodWrapperEmitter.IsNestedFrozenStructParam(arg, env.TypeDatabase))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Emits a Swift wrapper function that adapts large Optional
    /// parameters from UnsafeRawPointer to their native Optional types.
    /// When useCdecl=true, emits @_cdecl with C-compatible params via GetCdeclParamMapping.
    /// When useCdecl=false, emits @_silgen_name with native Swift types.
    /// </summary>
    public static void EmitSwiftWrapper(
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        TypeDecl? parentDecl,
        bool useCdecl = false,
        ModuleEmissionContext? emissionContext = null)
    {
        var methodDecl = env.MethodDecl;
        var wrapperSymbol = NameProvider.GetMangledName(methodDecl);

        bool isSetter = methodDecl.IsAccessor && MarshallingHelpers.MethodIsSetter(methodDecl);
        bool isGetter = methodDecl.IsAccessor && !isSetter;

        // Compute return type info BEFORE building params so we know the ordering.
        // C# PInvokeEmitter puts resultPtr first (via HandleReturnType), so we must match.
        var returnTypeSpec = methodDecl.CSSignature[0].SwiftTypeSpec;
        bool hasLargeOptionalReturn = env.BoundGenericsHandler.IsLargeOptionalReturn(methodDecl);
        var hasReturn = !returnTypeSpec.IsEmptyTuple && !hasLargeOptionalReturn;
        var returnSwiftTypeName = ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec);
        string returnTypeStr;
        string throwsStr;
        bool cdeclNeedsResultPtr = false;
        bool cdeclIsStringReturn = false;
        CdeclReturnMapping? cdeclReturnMapping = null;
        if (useCdecl && hasReturn && !hasLargeOptionalReturn)
        {
            var (returnMapping, needsResultPtr) = CdeclReturnMapping.Classify(returnTypeSpec, env.TypeDatabase);
            cdeclReturnMapping = returnMapping;
            cdeclIsStringReturn = WitnessDispatchEmitter.IsStringType(returnTypeSpec);
            if (cdeclIsStringReturn) needsResultPtr = true;
            cdeclNeedsResultPtr = needsResultPtr;
            returnTypeStr = needsResultPtr ? "" : $" -> {returnMapping.CdeclReturnType}";
            if (cdeclIsStringReturn)
                Utf8SliceEmitter.EmitIfNeeded(swiftWriter, emissionContext);
        }
        else
        {
            returnTypeStr = hasReturn ? $" -> {returnSwiftTypeName}" : "";
        }
        throwsStr = (useCdecl && methodDecl.Throws) ? "" : (methodDecl.Throws ? " throws" : "");

        // Build Swift parameter list.
        // Order must match C# PInvokeSignatureBuilder:
        //   [resultPtr] [args...] [_resultBuf] [self] [errorOut]
        // resultPtr is FIRST (matches HandleReturnType at position 0).
        // _resultBuf position depends on the wrapper style:
        //   - @_cdecl: FIRST (position 0) — C# HandleReturnType routes large Optional returns through
        //     MethodRequiresIndirectResult, emitting resultPtr at position 0. HandleArguments skips
        //     _optRetPtr because !MethodRequiresIndirectResult is false.
        //   - @_silgen_name: AFTER args — C# HandleArguments adds _optRetPtr at end.
        // errorOut is AFTER self (matches HandleSwiftError for non-constructor methods).
        var swiftParams = new List<string>();
        var callArgs = new List<string>();
        var valueArgs = new List<string>(); // Unlabeled values for setter assignment RHS
        var derefCode = new List<string>();

        // 1. Result ptr parameter (first, for indirect returns — matches C# HandleReturnType)
        if (cdeclNeedsResultPtr)
        {
            swiftParams.Add("_ resultPtr: UnsafeMutableRawPointer");
        }
        // 1b. @_cdecl large Optional return buffer at position 0.
        // C# PInvokeEmitter.HandleReturnType routes this through MethodRequiresIndirectResult,
        // adding resultPtr at position 0 (not _optRetPtr at end of HandleArguments).
        else if (useCdecl && hasLargeOptionalReturn)
        {
            swiftParams.Add("_ _resultBuf: UnsafeMutableRawPointer");
        }

        // 2. Method arguments
        int argIndex = 0;
        foreach (var arg in methodDecl.CSSignature.Skip(1))
        {
            var csName = NameProvider.StripVerbatimPrefix(NameProvider.GetCSharpParameterName(arg));
            // Escape Swift keywords with backticks for use in generated Swift code
            var swiftName = NameProvider.EscapeSwiftKeyword(csName);

            if (env.BoundGenericsHandler.IsLargeOptionalParam(arg.SwiftTypeSpec))
            {
                // Large Optional: accept UnsafeRawPointer, dereference in body. Route through
                // GetDerefCode so Optional<NonFrozenStruct> / Optional<ComplexEnum> (projected
                // as C# SwiftOptional<IntPtr>) gets the opaque-aware decoding — matches the DBW
                // path's treatment. Regression: without this, a non-DBW full wrapper for
                // Optional<NonFrozenStruct> read the native Optional<T> layout while C# passed
                // an 8-byte pointer buffer, causing layout mismatch.
                swiftParams.Add($"_ {swiftName}: UnsafeRawPointer");
                derefCode.Add(GetDerefCode(arg, csName, swiftName, env.TypeDatabase));

                var label = GetSwiftArgLabel(arg);
                callArgs.Add($"{label}{csName}Val");
                valueArgs.Add($"{csName}Val");
            }
            else if (useCdecl)
            {
                // Non-large param in @_cdecl mode: convert to C-compatible type
                var label_ = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
                // Swift's `_` is a discard pattern — cannot be used as a variable name in the body.
                if (label_ == "_")
                    label_ = $"arg{argIndex}";
                var (cdeclParam, reconstruction, callArg) =
                    CdeclParamMapper.Map(arg, label_, env, omitLabels: true);
                swiftParams.Add(cdeclParam);
                if (reconstruction != null) derefCode.Add(reconstruction);
                var swiftArgLabel = GetSwiftArgLabel(arg);
                var valueRef = reconstruction != null ? $"{label_}Val" : csName;
                callArgs.Add($"{swiftArgLabel}{valueRef}");
                valueArgs.Add(valueRef);
            }
            else
            {
                // Non-large param: pass through with original Swift type
                var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec);
                swiftParams.Add($"_ {swiftName}: {swiftType}");
                var label = GetSwiftArgLabel(arg);
                // @autoclosure parameters: the wrapper receives the closure as () -> T,
                // but the original method expects T with @autoclosure wrapping.
                // Invoke the closure with () to produce the value for @autoclosure re-wrapping.
                var autoClosureSuffix = arg.SwiftTypeSpec is ClosureTypeSpec cls && cls.IsAutoClosure ? "()" : "";
                callArgs.Add($"{label}{swiftName}{autoClosureSuffix}");
                valueArgs.Add($"{swiftName}{autoClosureSuffix}");
            }
            argIndex++;
        }

        // 3. Large Optional result buffer (after args — matches C# _optRetPtr at end of HandleArguments)
        // Skip for @_cdecl: already added at position 0 (step 1b) to match C# HandleReturnType.
        if (hasLargeOptionalReturn && !useCdecl)
        {
            swiftParams.Add("_ _resultBuf: UnsafeMutableRawPointer");
        }

        // 4. Self parameter (instance methods only)
        bool isInstance = methodDecl.MethodType != MethodType.Static && parentDecl != null && !methodDecl.IsConstructor;
        if (isInstance)
        {
            swiftParams.Add("_ _self: UnsafeMutableRawPointer");
        }

        // 5. Error out-pointer (after self — matches C# HandleSwiftError for non-constructor methods)
        if (useCdecl && methodDecl.Throws)
        {
            swiftParams.Add("_ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>");
        }

        var callArgsStr = string.Join(", ", callArgs);
        // Setter RHS: typically one value, no labels needed
        var setterValueStr = string.Join(", ", valueArgs);

        // Build paramsStr AFTER all params have been added
        var paramsStr = string.Join(",\n    ", swiftParams);

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
            var escapedName = NameProvider.ParserNameToSwift(methodDecl);
            if (isClass)
            {
                selfConversion = $"let __self = unsafeBitCast(OpaquePointer(_self), to: {typeName}.self)";
                callLine = $"__self.{escapedName}({callArgsStr})";
            }
            else if (needsThroughPointer)
            {
                // Mutating value type: through-pointer to preserve mutations
                callLine = $"_self.assumingMemoryBound(to: {typeName}.self).pointee.{escapedName}({callArgsStr})";
            }
            else
            {
                // Non-mutating value type: copy is safe
                selfConversion = $"let __self = _self.assumingMemoryBound(to: {typeName}.self).pointee";
                callLine = $"__self.{escapedName}({callArgsStr})";
            }
        }
        else if (parentDecl != null)
        {
            callLine = $"{typeName}.{NameProvider.ParserNameToSwift(methodDecl)}({callArgsStr})";
        }
        else
        {
            var moduleName = methodDecl.ModuleDecl?.Name ?? "";
            var prefix = moduleName.Length > 0 ? $"{moduleName}." : "";
            callLine = $"{prefix}{NameProvider.ParserNameToSwift(methodDecl)}({callArgsStr})";
        }

        var tryPrefix = methodDecl.Throws ? "try " : "";

        // Determine if wrapper needs @MainActor annotation (only for @MainActor, not custom actors)
        bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
            parentDecl, methodDecl.IsMainActorIsolated, methodDecl.IsNonisolated);

        // @_silgen_name and @_cdecl wrappers are top-level Swift functions and do NOT inherit
        // their parent type's @available; both must be re-applied or the wrapper compiles
        // unconditionally and crashes on devices below the wrapped API's introduced version.
        var availability = WrapperEmitterHelpers.MergeAvailability(methodDecl.AvailabilityAnnotations, parentDecl);
        WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);

        // Emit the wrapper
        if (needsMainActor)
            swiftWriter.WriteLine("@MainActor");
        var annotation = useCdecl ? "@_cdecl" : "@_silgen_name";
        // Register the @_cdecl symbol for the wrapper-symbol contract — the
        // @_silgen_name branch isn't an SBW_… cdecl wrapper so the contract
        // check (which only fires for SBW_-shaped entry points) wouldn't see it.
        // S5 audited (Tier B): the optional-pointer wrapper path is mutually exclusive
        // with MethodWrapperEmitter for the same method (handler-pipeline gates ensure
        // only one fires); the method's mangled name is unique per overload, so the
        // per-kind method bucket is collision-safe.
        if (useCdecl)
            emissionContext?.TryAddMethodWrapperSymbol(wrapperSymbol);
        swiftWriter.WriteLine($"{annotation}(\"{wrapperSymbol}\")");
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

        // Emit the call — with error handling for @_cdecl throwing methods
        if (useCdecl && methodDecl.Throws)
        {
            swiftWriter.WriteLine("    do {");
            if (hasLargeOptionalReturn)
            {
                var bufferLines = GetReturnBufferCode($"try {callLine}", returnSwiftTypeName);
                foreach (var line in bufferLines)
                    swiftWriter.WriteLine($"        {line}");
            }
            else if (cdeclIsStringReturn)
            {
                EmitStringReturnBody(swiftWriter, $"try {callLine}", indent: "        ");
            }
            else if (cdeclNeedsResultPtr)
            {
                var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec);
                // Protocol existential returns need `(any P).self`, not `any P.self`.
                var metatype = swiftType.StartsWith("any ") ? $"({swiftType}).self" : $"{swiftType}.self";
                swiftWriter.WriteLine($"        let result = try {callLine}");
                swiftWriter.WriteLine($"        resultPtr.initializeMemory(as: {metatype}, repeating: result, count: 1)");
            }
            else if (hasReturn || methodDecl.IsConstructor)
            {
                EmitCdeclDirectReturn(swiftWriter, $"try {callLine}", returnTypeSpec, env.TypeDatabase, cdeclReturnMapping, indent: "        ");
            }
            else
            {
                swiftWriter.WriteLine($"        try {callLine}");
            }
            swiftWriter.WriteLines("""
                    } catch {
                        errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
                    """);
            // Sentinel return for non-void direct returns on error path
            if (hasReturn && !cdeclNeedsResultPtr && !hasLargeOptionalReturn)
                EmitCdeclSentinelReturn(swiftWriter, cdeclReturnMapping, indent: "        ");
            swiftWriter.WriteLine("    }");
        }
        else if (hasLargeOptionalReturn)
        {
            var bufferLines = GetReturnBufferCode($"{tryPrefix}{callLine}", returnSwiftTypeName);
            foreach (var line in bufferLines)
            {
                swiftWriter.WriteLine($"    {line}");
            }
        }
        else if (cdeclIsStringReturn)
        {
            EmitStringReturnBody(swiftWriter, $"{tryPrefix}{callLine}", indent: "    ");
        }
        else if (cdeclNeedsResultPtr)
        {
            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec);
            // Protocol existential returns need `(any P).self`, not `any P.self`.
            var metatype = swiftType.StartsWith("any ") ? $"({swiftType}).self" : $"{swiftType}.self";
            swiftWriter.WriteLine($"    let result = {tryPrefix}{callLine}");
            swiftWriter.WriteLine($"    resultPtr.initializeMemory(as: {metatype}, repeating: result, count: 1)");
        }
        else if (useCdecl && hasReturn)
        {
            EmitCdeclDirectReturn(swiftWriter, $"{tryPrefix}{callLine}", returnTypeSpec, env.TypeDatabase, cdeclReturnMapping, indent: "    ");
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
    /// For Optional&lt;OpaqueType&gt; (non-frozen struct or complex enum projected as class-with-opaque-payload),
    /// C# passes a SwiftOptional&lt;IntPtr&gt; buffer which uses Swift's extra-inhabitant Optional&lt;UnsafePointer&gt;
    /// layout (8 bytes, 0x0 = nil). Reading as Optional&lt;T&gt; directly would misinterpret the layout and
    /// read past the 8-byte buffer. Mirrors the pattern in CdeclParamMapper for the full @_cdecl path.
    /// </summary>
    public static string GetDerefCode(ArgumentDecl arg, string csName, string swiftName, ITypeDatabase? typeDatabase = null)
    {
        if (typeDatabase != null && TryGetOptionalOpaqueInnerType(arg.SwiftTypeSpec, typeDatabase, out var innerSpec))
        {
            var innerSwiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(innerSpec!);
            return $"let {csName}Val: {innerSwiftType}? = {swiftName}.assumingMemoryBound(to: UnsafeMutableRawPointer?.self).pointee.map {{ $0.assumingMemoryBound(to: {innerSwiftType}.self).pointee }}";
        }
        var swiftType = SwiftTypeNameHelper.GetSwiftTypeNameForMetatype(arg.SwiftTypeSpec);
        return $"let {csName}Val = {swiftName}.assumingMemoryBound(to: {swiftType}.self).pointee";
    }

    /// <summary>
    /// Returns true when the given TypeSpec is Optional&lt;T&gt; where T is projected as an
    /// opaque (class-with-opaque-payload) type in C# — i.e., a non-frozen struct or a
    /// non-simple enum. These use SwiftOptional&lt;IntPtr&gt; on the C# side and require
    /// UnsafeMutableRawPointer? decoding in Swift wrappers.
    /// </summary>
    private static bool TryGetOptionalOpaqueInnerType(TypeSpec spec, ITypeDatabase typeDatabase, out TypeSpec? innerSpec)
    {
        innerSpec = null;
        if (spec is not NamedTypeSpec optSpec ||
            optSpec.Name != "Swift.Optional" ||
            optSpec.GenericParameters.Count != 1)
            return false;

        var inner = optSpec.GenericParameters[0];
        if (inner is not NamedTypeSpec innerNamed)
            return false;

        if (!typeDatabase.TryGetTypeRecord(innerNamed, out var innerRecord))
            return false;

        // NativeRemapped types (URL, Data, etc.) use their own marshalling, not SwiftOptional<IntPtr>.
        if (innerRecord.NativeTypeName != null)
            return false;

        bool isOpaque = (innerRecord.Kind == TypeRecordKind.Enum && !innerRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
                     || (innerRecord.Kind == TypeRecordKind.Struct && !MarshallingHelpers.IsTypeFrozen(innerRecord));

        if (!isOpaque) return false;
        innerSpec = inner;
        return true;
    }

    /// <summary>
    /// Returns the Swift code to write a result value to an UnsafeMutableRawPointer result buffer.
    /// Used when the return type is a large Optional (e.g., Optional&lt;String&gt; which is 16 bytes).
    /// </summary>
    public static List<string> GetReturnBufferCode(string callLine, string returnSwiftType)
    {
        // Use initializeMemory instead of copyMemory to properly handle ARC retain.
        // copyMemory is a raw memcpy that doesn't retain reference types (String, classes, etc.).
        // When the Swift wrapper returns, _result is destroyed and its references are released.
        // If copyMemory was used, the bytes in _resultBuf would contain dangling pointers
        // because the reference count was never incremented for the copy.
        // initializeMemory properly initializes the memory with a copy that retains ARC references,
        // keeping the value alive after the function returns so C# can safely read it.
        return new List<string>
        {
            $"let _result = {callLine}",
            $"_resultBuf.initializeMemory(as: {returnSwiftType}.self, repeating: _result, count: 1)"
        };
    }

    /// <summary>
    /// Gets the Swift property name from an accessor method name by stripping the _Get or _Set suffix.
    /// Accessor field names come from SanitizePropertyWrapperName (raw Swift names, NOT parser-escaped),
    /// so we use EscapeSwiftKeyword directly — not ParserNameToSwift which would wrongly strip
    /// leading underscores from genuine Swift identifiers like _class.
    /// </summary>
    private static string GetPropertyNameFromAccessor(string methodName)
    {
        string baseName;
        if (methodName.EndsWith("_Set"))
            baseName = methodName.Substring(0, methodName.Length - 4);
        else if (methodName.EndsWith("_Get"))
            baseName = methodName.Substring(0, methodName.Length - 4);
        else
            baseName = methodName;
        return NameProvider.EscapeSwiftKeyword(baseName);
    }

    /// <summary>
    /// Gets the Swift argument label for a parameter.
    /// Reconstructs the original Swift label from the argument name.
    /// Same logic as <see cref="ClosureEmitter"/>'s GetSwiftArgLabel.
    /// </summary>
    private static string GetSwiftArgLabel(ArgumentDecl arg)
    {
        var name = arg.Name;
        if (SwiftBuilder.IsAutoGeneratedArgName(name))
            return ""; // Unlabeled
        if (name.StartsWith("_"))
            return $"{name.Substring(1)}: "; // Strip leading underscore
        return $"{name}: ";
    }

    /// <summary>
    /// Gets the Swift bracket-call label for a subscript index parameter.
    /// Returns "" for unlabeled positions (parser-set <see cref="ArgumentDecl.IsUnlabeledSubscriptIndex"/>),
    /// otherwise the keyword-safe Swift label followed by ": ". Routes through
    /// <see cref="NameProvider.GetSubscriptExternalLabel"/> so keyword labels (<c>default</c>,
    /// <c>in</c>, ...) are backtick-escaped and user labels spelling <c>indexN</c> are preserved
    /// instead of being mis-classified as the synthetic placeholder.
    /// </summary>
    private static string GetSubscriptArgLabel(ArgumentDecl arg)
    {
        var label = NameProvider.GetSubscriptExternalLabel(arg);
        return label == "_" ? "" : $"{label}: ";
    }

    /// <summary>
    /// Emits string return body (SBW_Utf8Slice via resultPtr) at the given indent level.
    /// Shared between throwing and non-throwing @_cdecl paths.
    /// <paramref name="postCallStatement"/> is emitted immediately after the call result
    /// is bound but before the string is serialized — used by CSM mutating-self + string
    /// return to propagate the mutated `var __self` back to the caller's payload memory
    /// (otherwise the mutation lives only on a local copy).
    /// </summary>
    internal static void EmitStringReturnBody(SwiftWriter swiftWriter, string callExpr, string indent, string? postCallStatement = null)
    {
        swiftWriter.WriteLine($"{indent}let result = {callExpr}");
        if (!string.IsNullOrEmpty(postCallStatement))
            swiftWriter.WriteLine($"{indent}{postCallStatement}");
        swiftWriter.WriteLine($"{indent}let utf8 = Array(result.utf8)");
        swiftWriter.WriteLine($"{indent}if utf8.isEmpty {{");
        swiftWriter.WriteLine($"{indent}    resultPtr.storeBytes(of: SBW_Utf8Slice(ptr: &_sbw_emptyBuffer, len: 0), as: SBW_Utf8Slice.self)");
        swiftWriter.WriteLine($"{indent}    return");
        swiftWriter.WriteLine($"{indent}}}");
        swiftWriter.WriteLine($"{indent}let ptr = UnsafeMutablePointer<UInt8>.allocate(capacity: utf8.count)");
        swiftWriter.WriteLine($"{indent}ptr.initialize(from: utf8, count: utf8.count)");
        swiftWriter.WriteLine($"{indent}resultPtr.storeBytes(of: SBW_Utf8Slice(ptr: ptr, len: utf8.count), as: SBW_Utf8Slice.self)");
    }

    /// <summary>
    /// Emits a direct return with cdecl type conversion (Bool→Int8, Class→Unmanaged, etc.).
    /// </summary>
    internal static void EmitCdeclDirectReturn(SwiftWriter swiftWriter, string callExpr,
        TypeSpec returnTypeSpec, ITypeDatabase typeDatabase,
        CdeclReturnMapping? mapping, string indent)
    {
        var kind = mapping?.Kind ?? CdeclReturnKind.Direct;
        switch (kind)
        {
            case CdeclReturnKind.Bool:
                swiftWriter.WriteLine($"{indent}return ({callExpr}) ? 1 : 0");
                break;
            case CdeclReturnKind.SimpleEnum:
                if (typeDatabase.TryGetTypeRecord(returnTypeSpec, out var enumRecord) &&
                    !string.IsNullOrEmpty(enumRecord.RawValueTypeName))
                {
                    swiftWriter.WriteLine($"{indent}return {mapping!.CdeclReturnType}(({callExpr}).rawValue)");
                }
                else
                {
                    // Tag-only enum: zero-initialize and copyMemory to avoid reading past
                    // the enum's 1-byte allocation (load(as: Int.self) reads 8 bytes → crash).
                    // Compute size before closures to avoid Swift exclusivity checker error.
                    // EmitTagOnlyEnumReturn doesn't support indent, so emit inline.
                    swiftWriter.WriteLine($"{indent}var result = {callExpr}");
                    swiftWriter.WriteLine($"{indent}let resultSize = MemoryLayout.size(ofValue: result)");
                    swiftWriter.WriteLine($"{indent}var tag: {mapping!.CdeclReturnType} = 0");
                    swiftWriter.WriteLine($"{indent}withUnsafeMutablePointer(to: &tag) {{ tagPtr in withUnsafePointer(to: &result) {{ resultPtr in UnsafeMutableRawPointer(tagPtr).copyMemory(from: UnsafeRawPointer(resultPtr), byteCount: resultSize) }} }}");
                    swiftWriter.WriteLine($"{indent}return tag");
                }
                break;
            case CdeclReturnKind.ClassPointer:
                // Use `as AnyObject` for safety — handles both true classes and ObjC-bridged structs.
                swiftWriter.WriteLine($"{indent}return Unmanaged.passRetained({callExpr} as AnyObject).toOpaque()");
                break;
            case CdeclReturnKind.OptionalClassPointer:
                // Use `as AnyObject` — ObjC-bridged structs (e.g., NSZone, IndexPath) need bridge cast.
                swiftWriter.WriteLine($"{indent}if let result = {callExpr} {{ return Unmanaged.passRetained(result as AnyObject).toOpaque() }}");
                swiftWriter.WriteLine($"{indent}return nil");
                break;
            default:
                swiftWriter.WriteLine($"{indent}return {callExpr}");
                break;
        }
    }

    /// <summary>
    /// Emits a sentinel return value in the catch block for non-void direct @_cdecl returns.
    /// </summary>
    internal static void EmitCdeclSentinelReturn(SwiftWriter swiftWriter,
        CdeclReturnMapping? mapping, string indent)
    {
        var kind = mapping?.Kind ?? CdeclReturnKind.Direct;
        switch (kind)
        {
            case CdeclReturnKind.Bool:
            case CdeclReturnKind.SimpleEnum:
            case CdeclReturnKind.Direct:
                swiftWriter.WriteLine($"{indent}return 0");
                break;
            case CdeclReturnKind.ClassPointer:
                swiftWriter.WriteLine($"{indent}return UnsafeMutableRawPointer(bitPattern: 1)!");
                break;
            case CdeclReturnKind.OptionalClassPointer:
                swiftWriter.WriteLine($"{indent}return nil");
                break;
        }
    }

}
