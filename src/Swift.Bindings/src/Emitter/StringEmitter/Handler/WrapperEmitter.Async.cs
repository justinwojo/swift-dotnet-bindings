// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;

namespace BindingsGeneration
{
    internal partial class WrapperEmitter
    {
        /// <summary>
        /// Emits the Async task.
        /// </summary>
        /// <param name="csWriter">The IndentedTextWriter instance.</param>
        private void EmitAsync(CSharpWriter csWriter, SwiftWriter swiftWriter)
        {
            if (!_requiresSwiftAsync)
                return;

            bool isEmptyTuple = _env.MethodDecl.CSSignature.First().SwiftTypeSpec.IsEmptyTuple;
            // Async constructors are emitted as static CreateAsync() factories — no self retention needed
            bool isAsyncConstructor = _env.MethodDecl.IsConstructor;
            bool isInstanceMethod = _env.MethodDecl.MethodType != MethodType.Static && !isAsyncConstructor;
            bool isSwiftClass = _env.ParentDecl is ClassDecl;

            // Detect String return type - requires UTF-8 marshalling via SBW_Utf8Slice
            // SBW_Utf8Slice and SBW_Free are emitted at module level (ModuleHandler.EmitSwiftImports)
            var returnTypeSpec = _env.MethodDecl.CSSignature.First().SwiftTypeSpec;
            bool isStringReturn = !isEmptyTuple && returnTypeSpec.ToString() == "Swift.String";

            // Detect Array<String> return type - requires flat buffer marshalling
            bool isArrayStringReturn = !isEmptyTuple && IsArrayOfString(returnTypeSpec);

            // Identify parameters that need copy-buffer treatment for async safety.
            // Non-frozen types always need this. Enum types also need it regardless of
            // frozen status because they're projected as managed C# classes with SafeHandle
            // payload, which can't be passed directly through P/Invoke with Swift calling convention.
            var nonFrozenParams = _env.MethodDecl.CSSignature
                .Skip(1)
                .Where(p => !p.IsGeneric && !_env.BoundGenericsHandler.IsBoundGeneric(p) && !_env.ClosureHandler.IsClosure(p) && !_env.TupleHandler.IsTuple(p) && !_env.ExistentialHandler.IsExistential(p.SwiftTypeSpec))
                .Where(p =>
                {
                    var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(p.SwiftTypeSpec);
                    return !MarshallingHelpers.IsTypeFrozen(typeRecord) || typeRecord.Kind == TypeRecordKind.Enum;
                })
                .ToList();

            // For non-frozen parameters, create proper copies using InitializeWithCopy FIRST
            // (before the holder is created), so the copy buffer pointers can be stored in the holder.
            // Swift reads via .pointee (bitwise copy). Original params kept alive to maintain ref count.
            if (nonFrozenParams.Count > 0)
            {
                // Create copy buffers for non-frozen parameters
                foreach (var p in nonFrozenParams)
                {
                    var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(p.SwiftTypeSpec);
                    var typeName = typeRecord.CSharpTypeName.FullyQualifiedName;

                    // For native-remapped types (e.g., Foundation.NSUrl -> Swift.URL), we need to
                    // convert to the Swift type first before copying. The wrapper signature uses the
                    // native type but the underlying Swift type is what we need to copy.
                    if (!_env.MethodDecl.IsAccessor && _env.TypeConversionHandler.HasNativeTypeRemapping(p.SwiftTypeSpec))
                    {
                        // Convert native type to Swift type, then copy from Swift type
                        var conversion = _env.TypeConversionHandler.GetNativeParameterConversion(p.Name, p.SwiftTypeSpec);
                        csWriter.WriteLines($"""
                            var {p.Name}Metadata = SwiftObjectHelper<{typeName}>.GetTypeMetadata();
                            IntPtr {p.Name}CopyBuffer = (IntPtr)NativeMemory.Alloc({p.Name}Metadata.Size);
                            using var {p.Name}SwiftTemp = {conversion};
                            {p.Name}Metadata.ValueWitnessTable->InitializeWithCopy(
                                (void*){p.Name}CopyBuffer,
                                (void*){p.Name}SwiftTemp.Payload.DangerousGetHandle(),
                                {p.Name}Metadata);
                            IntPtr {p.Name}Handle = {p.Name}CopyBuffer;
                            var {p.Name}CopyBufferWrapper = new CopyBufferWithType({p.Name}CopyBuffer, {p.Name}Metadata);
                            """);
                    }
                    else
                    {
                        csWriter.WriteLines($"""
                            var {p.Name}Metadata = SwiftObjectHelper<{typeName}>.GetTypeMetadata();
                            IntPtr {p.Name}CopyBuffer = (IntPtr)NativeMemory.Alloc({p.Name}Metadata.Size);
                            {p.Name}Metadata.ValueWitnessTable->InitializeWithCopy(
                                (void*){p.Name}CopyBuffer,
                                (void*){p.Name}.Payload.DangerousGetHandle(),
                                {p.Name}Metadata);
                            IntPtr {p.Name}Handle = {p.Name}CopyBuffer;
                            var {p.Name}CopyBufferWrapper = new CopyBufferWithType({p.Name}CopyBuffer, {p.Name}Metadata);
                            """);
                    }
                }

                // Now create the holder with copy buffer pointers AND original parameters AND self (for instance methods)
                // Original parameters must be kept alive to prevent GC from calling Destroy on them
                // while the async task is still running (InitializeWithCopy increments ref count,
                // but if original is destroyed, the internal storage could be freed prematurely)
                // Also keep 'this' alive for instance methods since SwiftSelf doesn't prevent GC
                var copyBufferList = string.Join(", ", nonFrozenParams.Select(p => $"{p.Name}CopyBufferWrapper"));
                var originalParamList = string.Join(", ", nonFrozenParams.Select(p => $"(object){p.Name}"));

                // For Swift classes, Arc.Retain the self pointer before async call.
                // SwiftSelf passes a raw pointer - no ARC semantics. By the time Swift's Task{}
                // closure runs, 'self' may be deallocated. Retain ensures Swift ARC tracks it.
                string selfInHolder;
                if (isInstanceMethod && isSwiftClass)
                {
                    // For Swift classes, retain self and store a RetainedSelfPtr marker
                    // The payload buffer contains a pointer to the class instance - we need to dereference it
                    csWriter.WriteLines($$"""
            IntPtr _selfPtr = *(IntPtr*)_payload.DangerousGetHandle();
            Arc.Retain(_selfPtr);
            """);
                    selfInHolder = ", new RetainedSelfPtr(_selfPtr), (object)this";
                }
                else if (isInstanceMethod)
                {
                    // For structs, keep 'this' alive and defer SafeHandle release until callback
                    selfInHolder = ", new DeferredSafeHandleRelease(_payload), (object)this";
                }
                else
                {
                    selfInHolder = "";
                }

                csWriter.WriteLines($$"""
            TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}} task = new TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}}();
            object[] _asyncCallHolder = new object[] { task, {{copyBufferList}}, {{originalParamList}}{{selfInHolder}} };
            GCHandle handle = GCHandle.Alloc(_asyncCallHolder, GCHandleType.Normal);
            """);
            }
            else if (isInstanceMethod)
            {
                // No non-frozen parameters, but still need to keep 'this' alive for instance methods
                // For Swift classes, also retain self to prevent deallocation during async execution
                if (isSwiftClass)
                {
                    // The payload buffer contains a pointer to the class instance - we need to dereference it
                    csWriter.WriteLines($$"""
            TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}} task = new TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}}();
            IntPtr _selfPtr = *(IntPtr*)_payload.DangerousGetHandle();
            Arc.Retain(_selfPtr);
            object[] _asyncCallHolder = new object[] { task, new RetainedSelfPtr(_selfPtr), (object)this };
            GCHandle handle = GCHandle.Alloc(_asyncCallHolder, GCHandleType.Normal);
            """);
                }
                else
                {
                    // For structs, keep 'this' alive and defer SafeHandle release until callback
                    csWriter.WriteLines($$"""
            TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}} task = new TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}}();
            object[] _asyncCallHolder = new object[] { task, new DeferredSafeHandleRelease(_payload), (object)this };
            GCHandle handle = GCHandle.Alloc(_asyncCallHolder, GCHandleType.Normal);
            """);
                }
            }
            else
            {
                // Static method with no non-frozen parameters - no holder needed
                csWriter.WriteLines($$"""
            TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}} task = new TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}}();
            GCHandle handle = GCHandle.Alloc(task, GCHandleType.Normal);
            """);
            }

            // Build parameter string - non-frozen types use UnsafeRawPointer in Swift wrapper
            // For tuple returns, flatten the tuple elements into separate callback parameters
            // because @convention(c) doesn't support Swift tuples
            var returnTypeArg = _env.MethodDecl.CSSignature.First();
            var isTupleReturn = _env.TupleHandler.IsTuple(returnTypeArg.SwiftTypeSpec) &&
                                _env.TupleHandler.IsSupportedTuple((TupleTypeSpec)returnTypeArg.SwiftTypeSpec);

            string callbackParams;
            string callbackResultArgs;
            string stringMarshalCode = "";  // UTF-8 marshalling for String returns
            if (isEmptyTuple)
            {
                callbackParams = "";
                callbackResultArgs = "";
            }
            else if (isTupleReturn)
            {
                // Flatten tuple elements for @convention(c) compatibility
                var tupleTypeSpec = (TupleTypeSpec)returnTypeArg.SwiftTypeSpec;
                var elementTypes = tupleTypeSpec.Elements.Select(e => e.ToString()).ToList();
                callbackParams = string.Join(", ", elementTypes) + ", ";
                // For callback invocation, access tuple elements with .0, .1, etc.
                callbackResultArgs = string.Join(", ", Enumerable.Range(0, tupleTypeSpec.Elements.Count).Select(i => $"result{_env.MethodDecl.Name}.{i}")) + ", ";
            }
            else if (isStringReturn)
            {
                // String return: pass UTF-8 (ptr, len) directly for @convention(c) compatibility
                // Custom structs aren't allowed in @convention(c) params, only primitives and pointers
                // Swift allocates UTF-8 buffer, C# copies and frees via SBW_Free
                callbackParams = "UnsafeMutablePointer<UInt8>, Int, ";
                callbackResultArgs = "_slicePtr, _sliceLen, ";
                var resultVar = $"result{_env.MethodDecl.Name}";
                // Always allocate (even empty strings get 1 byte) for simplicity.
                // C# always frees via SBW_Free - even 1-byte empty allocations are harmless.
                stringMarshalCode =
                    $"// Marshal String to UTF-8 (C# will free via SBW_Free)\n" +
                    $"                        var _utf8 = Array({resultVar}.utf8)\n" +
                    $"                        let _sliceLen = _utf8.count\n" +
                    $"                        let _slicePtr = UnsafeMutablePointer<UInt8>.allocate(capacity: max(_sliceLen, 1))\n" +
                    $"                        if _sliceLen > 0 {{\n" +
                    $"                            _slicePtr.initialize(from: &_utf8, count: _sliceLen)\n" +
                    $"                        }}";
            }
            else if (isArrayStringReturn)
            {
                // Array<String> return: serialize to flat buffer [count][lengths...][data...]
                // Same callback signature as String - just (ptr, len)
                callbackParams = "UnsafeMutablePointer<UInt8>, Int, ";
                callbackResultArgs = "_bufferPtr, _bufferLen, ";
                var resultVar = $"result{_env.MethodDecl.Name}";
                // Serialize array to flat buffer using explicit Int64 for wire format consistency
                // (avoids platform-sized Int ambiguity between Swift and C#)
                stringMarshalCode =
                    $"// Marshal Array<String> to flat buffer (C# will free via SBW_Free)\n" +
                    $"                        let _count = Int64({resultVar}.count)\n" +
                    $"                        var _lengths = [Int64]()\n" +
                    $"                        var _totalDataLen = 0\n" +
                    $"                        for _s in {resultVar} {{\n" +
                    $"                            let _utf8 = Array(_s.utf8)\n" +
                    $"                            _lengths.append(Int64(_utf8.count))\n" +
                    $"                            _totalDataLen += _utf8.count\n" +
                    $"                        }}\n" +
                    $"                        let _headerSize = MemoryLayout<Int64>.size * (1 + Int(_count))\n" +
                    $"                        let _bufferLen = _headerSize + _totalDataLen\n" +
                    $"                        let _bufferPtr = UnsafeMutablePointer<UInt8>.allocate(capacity: max(_bufferLen, 1))\n" +
                    $"                        // Write header: count followed by lengths (all Int64)\n" +
                    $"                        _bufferPtr.withMemoryRebound(to: Int64.self, capacity: 1 + Int(_count)) {{ _intPtr in\n" +
                    $"                            _intPtr[0] = _count\n" +
                    $"                            for _i in 0..<Int(_count) {{\n" +
                    $"                                _intPtr[1 + _i] = _lengths[_i]\n" +
                    $"                            }}\n" +
                    $"                        }}\n" +
                    $"                        // Write string data after header\n" +
                    $"                        var _dataOffset = _headerSize\n" +
                    $"                        for _s in {resultVar} {{\n" +
                    $"                            var _utf8 = Array(_s.utf8)\n" +
                    $"                            if !_utf8.isEmpty {{\n" +
                    $"                                (_bufferPtr + _dataOffset).initialize(from: &_utf8, count: _utf8.count)\n" +
                    $"                            }}\n" +
                    $"                            _dataOffset += _utf8.count\n" +
                    $"                        }}";
            }
            else if (returnTypeArg.IsGeneric)
            {
                callbackParams = _env.MethodDecl.GenericParameters[0].SugaredTypeName + ", ";
                callbackResultArgs = $"result{_env.MethodDecl.Name} as! {_env.MethodDecl.GenericParameters[0].SugaredTypeName}, ";
            }
            else
            {
                // For non-primitive types (classes, enums, structs), @convention(c) doesn't allow
                // passing Swift types directly. We must use OpaquePointer and allocate memory.
                var returnTypeName = returnTypeSpec.ToString();
                bool isComplexType = !IsSwiftPrimitive(returnTypeName);

                if (isComplexType)
                {
                    // Complex types: allocate memory, store result, pass pointer
                    callbackParams = "OpaquePointer, ";
                    callbackResultArgs = "_resultPtr, ";
                    var resultVar = $"result{_env.MethodDecl.Name}";
                    var swiftReturnType = returnTypeSpec.ToString();

                    // Check if this is a class type (needs retain to prevent ARC deallocation)
                    _env.TypeDatabase.TryGetTypeRecord(returnTypeSpec, out var complexReturnTypeRecord);
                    bool isClassType = complexReturnTypeRecord?.Kind == TypeRecordKind.Class;

                    // Allocate memory, copy result value, pass pointer through callback
                    // For classes: the "value" is a pointer, so we're storing the pointer value
                    // For enums/structs: we're storing the inline data
                    // C# will read from this memory and call SBW_Free to release it
                    stringMarshalCode =
                        $"// Marshal complex type to pointer (C# will free via SBW_Free)\n" +
                        $"                        let _resultPtr: OpaquePointer\n" +
                        $"                        do {{\n" +
                        $"                            let _rawPtr = UnsafeMutableRawPointer.allocate(\n" +
                        $"                                byteCount: MemoryLayout<{swiftReturnType}>.size,\n" +
                        $"                                alignment: MemoryLayout<{swiftReturnType}>.alignment)\n" +
                        $"                            _rawPtr.storeBytes(of: {resultVar}, as: {swiftReturnType}.self)\n" +
                        (isClassType
                            ? $"                            // Retain class to prevent ARC deallocation before C# processes it\n" +
                              $"                            _ = Unmanaged.passRetained({resultVar} as AnyObject)\n"
                            : "") +
                        $"                            _resultPtr = OpaquePointer(_rawPtr)\n" +
                        $"                        }}";
                }
                else
                {
                    // Primitive types can be passed directly through @convention(c)
                    callbackParams = returnTypeArg.SwiftTypeSpec + ", ";
                    callbackResultArgs = $"result{_env.MethodDecl.Name}, ";
                }
            }

            var baseParams = new[]
            {
                $"callback: @escaping @convention(c) ({callbackParams}Int64) -> Void",
                "errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int64) -> Void",
                "task: Int64"
            };

            var methodParams = _env.MethodDecl.CSSignature
                .Skip(1)
                .Select(p =>
                {
                    // Check if this is a non-frozen parameter that needs UnsafeRawPointer
                    if (nonFrozenParams.Any(nfp => nfp.Name == p.Name))
                    {
                        return $"{p.Name}: UnsafeRawPointer";
                    }
                    if (p.IsGeneric)
                    {
                        return $"{p.Name}: {_env.MethodDecl.GenericParameters.Find(g => g.TypeName == p.SwiftTypeSpec.ToString())!.SugaredTypeName}";
                    }
                    // For closure parameters in async wrappers, closures are captured by Task {}
                    // which requires @escaping (outlives function) and @Sendable (concurrency safety).
                    if (p.SwiftTypeSpec is ClosureTypeSpec)
                    {
                        var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(p.SwiftTypeSpec);
                        if (!swiftType.StartsWith("@escaping"))
                        {
                            swiftType = !swiftType.Contains("@Sendable")
                                ? $"@escaping @Sendable {swiftType}"
                                : $"@escaping {swiftType}";
                        }
                        else if (!swiftType.Contains("@Sendable"))
                        {
                            swiftType = swiftType.Replace("@escaping ", "@escaping @Sendable ");
                        }
                        return $"{p.Name}: {swiftType}";
                    }
                    return $"{p.Name}: {p.SwiftTypeSpec}";
                });

            // For async instance methods on non-singleton classes, add _self: OpaquePointer as explicit parameter
            // Singleton classes use ClassName.shared workaround and don't need _self
            var hasSingletonForParams = (_env.ParentDecl as TypeDecl)?.HasSingletonPattern ?? false;
            var needsSelfParam = _env.ParentDecl is TypeDecl && isInstanceMethod && _env.MethodDecl.MethodType != MethodType.Static && !hasSingletonForParams;
            var selfParam = needsSelfParam
                ? new[] { "_self: OpaquePointer" }
                : Array.Empty<string>();

            string parameters = string.Join(", ", baseParams.Concat(methodParams).Concat(selfParam));

            var genericParams = _env.MethodDecl.IsGeneric switch
            {
                true => $"<{string.Join(", ", _env.MethodDecl.GenericParameters.Select(p => p.SugaredTypeName))}>",
                false => ""
            };

            var whereClause = (_env.MethodDecl.IsGeneric && _env.MethodDecl.GenericParameters.Any(p => p.GenericConformances.Any() || p.AssosiatedTypeConformances.Any())) switch
            {
                true => " where " + string.Join(
                    ", ",
                    _env.MethodDecl.GenericParameters.Select(p =>
                    {
                        // Build conformances of the form "T : ProtocolName"
                        var genericConformances = p.GenericConformances
                            .Select(gc => $"{p.SugaredTypeName} : {gc.ConformanceTarget.Name}");

                        // Build type conformances of the form "T.AssociatedType == ProtocolName"
                        var typeConformances = p.AssosiatedTypeConformances
                            .Select(tc =>
                                $"{p.SugaredTypeName}.{string.Join(".", tc.Path.Skip(1))} == {tc.ConformanceTarget.Name}"
                            );

                        return string.Join(", ", genericConformances.Concat(typeConformances));
                    })
                ),
                false => ""
            };

            // Generate code to read non-frozen parameters via .pointee
            // C# created proper copies using InitializeWithCopy (handles reference counting).
            // Read non-frozen parameters via .pointee (bitwise copy, doesn't affect ref count).
            // The copy buffer created by C#'s InitializeWithCopy owns a proper reference.
            // C# will call Destroy on the copy buffer after callback completes.
            var readCode = nonFrozenParams.Count > 0
                ? string.Join("\n        ", nonFrozenParams.Select(p =>
                {
                    var swiftTypeName = SwiftTypeNameHelper.GetSwiftTypeNameForMetatype(p.SwiftTypeSpec);
                    var isExistential = _env.ExistentialHandler.IsExistential(p.SwiftTypeSpec);
                    return isExistential
                        ? $"let {p.Name}Value = {p.Name}.load(as: {swiftTypeName}.self)"
                        : $"let {p.Name}Value = {p.Name}.assumingMemoryBound(to: {swiftTypeName}.self).pointee";
                }))
                : "";

            // Generate argument list for the actual Swift method call
            var methodCallArgs = string.Join(", ", _env.MethodDecl.CSSignature.Skip(1)
                .Select(p =>
                {
                    var argName = p.Name switch
                    {
                        var n when n.StartsWith("arg") => n,
                        var n when n.StartsWith("_") => $"{n.Substring(1)}: {n}",
                        var n => $"{n}: {n}"
                    };

                    // For non-frozen params, use the captured value
                    if (nonFrozenParams.Any(nfp => nfp.Name == p.Name))
                    {
                        var label = p.Name switch
                        {
                            var n when n.StartsWith("arg") => "",
                            var n when n.StartsWith("_") => $"{n.Substring(1)}: ",
                            var n => $"{n}: "
                        };
                        return $"{label}{p.Name}Value";
                    }
                    return argName;
                }));

            var parentTypeName = (_env.ParentDecl as TypeDecl)?.SwiftTypeName;

            // For async instance methods on Swift classes, C# calls Arc.Retain on self before
            // invoking this wrapper, ensuring Swift ARC keeps self alive through the Task closure.
            // The matching Arc.Release is called in the C# callback after async completion.
            var selfComment = (isInstanceMethod && isSwiftClass)
                ? "// selfInstance is safe - C# called Arc.Retain before invoking this method"
                : "";

            // For async instance methods:
            // - If the parent class has a singleton pattern (static 'shared' property), use that
            // - Otherwise, use a free function that receives _self as OpaquePointer
            var isAsyncInstanceMethod = parentTypeName != null && isInstanceMethod && _env.MethodDecl.MethodType != MethodType.Static;
            var hasSingleton = (_env.ParentDecl as TypeDecl)?.HasSingletonPattern ?? false;

            // Determine how to call the method:
            // - Static methods: ClassName.method()
            // - Async instance methods on singleton classes: ClassName.shared.method() (workaround)
            // - Async instance methods on non-singleton classes: __self.method() (convert _self pointer)
            // - Regular instance methods: self.method()
            string selfConversion;
            string methodCallPrefix;
            if (_env.MethodDecl.MethodType == MethodType.Static || isAsyncConstructor)
            {
                selfConversion = "";
                methodCallPrefix = parentTypeName != null ? $"{parentTypeName.ModuleQualifiedName}." : "";
            }
            else if (isAsyncInstanceMethod && hasSingleton)
            {
                // Singleton workaround: use ClassName.shared instead of passing self
                // This avoids the SwiftSelf binding issue with @_silgen_name and Task closures
                selfConversion = "";
                methodCallPrefix = parentTypeName != null ? $"{parentTypeName.ModuleQualifiedName}.shared." : "";
            }
            else if (isAsyncInstanceMethod)
            {
                // Non-singleton async instance method: convert _self pointer to type reference
                if (isSwiftClass)
                {
                    // For classes: the pointer IS the object reference, use unsafeBitCast
                    selfConversion = $"let __self = unsafeBitCast(_self, to: {parentTypeName!.ModuleQualifiedName}.self)";
                }
                else
                {
                    // For structs: the pointer points TO the struct data, dereference it
                    selfConversion = $"let __self = UnsafePointer<{parentTypeName!.ModuleQualifiedName}>(_self).pointee";
                }
                methodCallPrefix = "__self.";
            }
            else
            {
                selfConversion = "";
                methodCallPrefix = "self.";
            }

            // Generate the Swift wrapper
            // For async instance methods, we use a free function to avoid SwiftSelf binding issues
            // For all other methods, we use extension methods
            if (isAsyncInstanceMethod)
            {
                // Free function for async instance methods (marked public to ensure export)
                if (nonFrozenParams.Count > 0)
                {
                    swiftWriter.WriteLine($$"""
            @_silgen_name("{{NameProvider.GetMangledName(_env.MethodDecl)}}")
            public func {{NameProvider.GetPInvokeName(_env.MethodDecl)}}{{genericParams}}({{parameters}}){{whereClause}}{
                // Read non-frozen parameters via .pointee (bitwise copy)
                // C# created copies using InitializeWithCopy (owns a proper reference)
                {{readCode}}
                {{selfConversion}}
                {{selfComment}}

                Task {
                    do {
                        {{(isEmptyTuple ? "" : $"let result{_env.MethodDecl.Name} = ")}}try await {{methodCallPrefix}}{{_env.MethodDecl.Name}}(
                            {{methodCallArgs}}
                        )
                        {{stringMarshalCode}}
                        callback({{callbackResultArgs}}task)
                    } catch {
                        let errorMessage = String(describing: error)
                        errorMessage.withCString { errorCallback($0, task) }
                    }
                }
            }
            """);
                }
                else
                {
                    swiftWriter.WriteLine($$"""
            @_silgen_name("{{NameProvider.GetMangledName(_env.MethodDecl)}}")
            public func {{NameProvider.GetPInvokeName(_env.MethodDecl)}}{{genericParams}}({{parameters}}){{whereClause}}{
                {{selfConversion}}
                {{selfComment}}
                Task {
                    do {
                        {{(isEmptyTuple ? "" : $"let result{_env.MethodDecl.Name} = ")}}try await {{methodCallPrefix}}{{_env.MethodDecl.Name}}(
                            {{methodCallArgs}}
                        )
                        {{stringMarshalCode}}
                        callback({{callbackResultArgs}}task)
                    } catch {
                        let errorMessage = String(describing: error)
                        errorMessage.withCString { errorCallback($0, task) }
                    }
                }
            }
            """);
                }
            }
            else if (parentTypeName != null)
            {
                // Extension method for static methods, async constructors, and non-async methods on types
                var staticModifier = (_env.MethodDecl.MethodType == MethodType.Static || isAsyncConstructor) ? "static " : "";
                if (nonFrozenParams.Count > 0)
                {
                    swiftWriter.WriteLine($$"""
            extension {{parentTypeName.ModuleQualifiedName}} {
                @_silgen_name("{{NameProvider.GetMangledName(_env.MethodDecl)}}")
                public {{staticModifier}}func {{NameProvider.GetPInvokeName(_env.MethodDecl)}}{{genericParams}}({{parameters}}){{whereClause}}{
                    // Read non-frozen parameters via .pointee (bitwise copy)
                    // C# created copies using InitializeWithCopy (owns a proper reference)
                    {{readCode}}
                    {{selfComment}}

                    Task {
                        do {
                            {{(isEmptyTuple ? "" : $"let result{_env.MethodDecl.Name} = ")}}try await {{methodCallPrefix}}{{_env.MethodDecl.Name}}(
                                {{methodCallArgs}}
                            )
                            {{stringMarshalCode}}
                            callback({{callbackResultArgs}}task)
                        } catch {
                            let errorMessage = String(describing: error)
                            errorMessage.withCString { errorCallback($0, task) }
                        }
                    }
                }
            }
            """);
                }
                else
                {
                    swiftWriter.WriteLine($$"""
            extension {{parentTypeName.ModuleQualifiedName}} {
                @_silgen_name("{{NameProvider.GetMangledName(_env.MethodDecl)}}")
                public {{staticModifier}}func {{NameProvider.GetPInvokeName(_env.MethodDecl)}}{{genericParams}}({{parameters}}){{whereClause}}{
                    {{selfComment}}
                    Task {
                        do {
                            {{(isEmptyTuple ? "" : $"let result{_env.MethodDecl.Name} = ")}}try await {{methodCallPrefix}}{{_env.MethodDecl.Name}}(
                                {{methodCallArgs}}
                            )
                            {{stringMarshalCode}}
                            callback({{callbackResultArgs}}task)
                        } catch {
                            let errorMessage = String(describing: error)
                            errorMessage.withCString { errorCallback($0, task) }
                        }
                    }
                }
            }
            """);
                }
            }
            else
            {
                // Free function for top-level async functions (no parent type to extend)
                if (nonFrozenParams.Count > 0)
                {
                    swiftWriter.WriteLine($$"""
            @_silgen_name("{{NameProvider.GetMangledName(_env.MethodDecl)}}")
            public func {{NameProvider.GetPInvokeName(_env.MethodDecl)}}{{genericParams}}({{parameters}}){{whereClause}}{
                // Read non-frozen parameters via .pointee (bitwise copy)
                // C# created copies using InitializeWithCopy (owns a proper reference)
                {{readCode}}

                Task {
                    do {
                        {{(isEmptyTuple ? "" : $"let result{_env.MethodDecl.Name} = ")}}try await {{methodCallPrefix}}{{_env.MethodDecl.Name}}(
                            {{methodCallArgs}}
                        )
                        {{stringMarshalCode}}
                        callback({{callbackResultArgs}}task)
                    } catch {
                        let errorMessage = String(describing: error)
                        errorMessage.withCString { errorCallback($0, task) }
                    }
                }
            }
            """);
                }
                else
                {
                    swiftWriter.WriteLine($$"""
            @_silgen_name("{{NameProvider.GetMangledName(_env.MethodDecl)}}")
            public func {{NameProvider.GetPInvokeName(_env.MethodDecl)}}{{genericParams}}({{parameters}}){{whereClause}}{
                Task {
                    do {
                        {{(isEmptyTuple ? "" : $"let result{_env.MethodDecl.Name} = ")}}try await {{methodCallPrefix}}{{_env.MethodDecl.Name}}(
                            {{methodCallArgs}}
                        )
                        {{stringMarshalCode}}
                        callback({{callbackResultArgs}}task)
                    } catch {
                        let errorMessage = String(describing: error)
                        errorMessage.withCString { errorCallback($0, task) }
                    }
                }
            }
            """);
                }
            }
        }

        /// <summary>
        /// Emits a wrapper for Swift async method.
        /// </summary>
        /// <param name="writer">The IndentedTextWriter instance.</param>
        private void EmitAsyncWrapper(CSharpWriter csWriter)
        {
            if (!_requiresSwiftAsync)
                return;

            var returnType = _env.MethodDecl.CSSignature.First();
            var voidReturn = returnType.SwiftTypeSpec.IsEmptyTuple;
            var isTupleReturn = _env.TupleHandler.IsTuple(returnType.SwiftTypeSpec) &&
                                _env.TupleHandler.IsSupportedTuple((TupleTypeSpec)returnType.SwiftTypeSpec);

            var callbackFieldName = NameProvider.GetAsyncCallbackFieldName(_env.MethodDecl);
            var callbackMethodName = NameProvider.GetAsyncCallbackMethodName(_env.MethodDecl);
            var errorCallbackFieldName = NameProvider.GetAsyncErrorCallbackFieldName(_env.MethodDecl);
            var errorCallbackMethodName = NameProvider.GetAsyncErrorCallbackMethodName(_env.MethodDecl);

            // For tuple returns, we need to marshal each element individually
            if (isTupleReturn)
            {
                EmitAsyncWrapperForTuple(csWriter, returnType, callbackFieldName, callbackMethodName, errorCallbackFieldName, errorCallbackMethodName);
                return;
            }

            // Detect String return - requires UTF-8 unmarshalling from SBW_Utf8Slice
            bool isStringReturn = !voidReturn && returnType.SwiftTypeSpec.ToString() == "Swift.String";
            if (isStringReturn)
            {
                EmitAsyncWrapperForString(csWriter, callbackFieldName, callbackMethodName, errorCallbackFieldName, errorCallbackMethodName);
                return;
            }

            // Detect Array<String> return - requires flat buffer unmarshalling
            bool isArrayStringReturn = !voidReturn && IsArrayOfString(returnType.SwiftTypeSpec);
            if (isArrayStringReturn)
            {
                EmitAsyncWrapperForArrayString(csWriter, callbackFieldName, callbackMethodName, errorCallbackFieldName, errorCallbackMethodName);
                return;
            }

            // Detect complex type returns (classes, enums, structs) that need OpaquePointer marshalling
            // These types can't be passed directly through @convention(c) callbacks
            var returnTypeName = returnType.SwiftTypeSpec.ToString();
            bool isComplexTypeReturn = !voidReturn && !returnType.IsGeneric && !IsSwiftPrimitive(returnTypeName);
            if (isComplexTypeReturn)
            {
                _env.TypeDatabase.TryGetTypeRecord(returnType.SwiftTypeSpec, out var complexTypeRecord);
                bool isClassType = complexTypeRecord?.Kind == TypeRecordKind.Class;
                EmitAsyncWrapperForComplexType(csWriter, callbackFieldName, callbackMethodName, errorCallbackFieldName, errorCallbackMethodName, isClassType);
                return;
            }

            // Non-tuple return handling
            TypeRecord returnTypeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(returnType.SwiftTypeSpec);
            var isObjCBridged = !voidReturn && MarshallingHelpers.IsObjCBridged(returnTypeRecord);

            // Convertible types (SwiftString -> string, SwiftArray -> IReadOnlyList, etc.) are already
            // properly marshalled and don't need InitWithCopy. Using SwiftObjectHelper with their projected
            // types (string, IReadOnlyList<T>) would fail since those types don't implement ISwiftObject.
            var isConvertibleType = _env.TypeConversionHandler.IsConvertibleType(returnType.SwiftTypeSpec);

            // ObjC bridged types and convertible types don't need InitWithCopy
            var requiresInitWithCopy = !voidReturn && !isObjCBridged && !isConvertibleType && (MarshallingHelpers.RequiresMemoryManagement(returnTypeRecord) || returnType.IsGeneric);

            // For ObjC bridged types, the rawResult is the ObjC object pointer directly
            // For Swift types, we need to marshal from Swift memory layout
            string marshalResultCode;
            if (isObjCBridged)
            {
                // ObjC types: rawResult is the ObjC object pointer, wrap with GetNSObject<T>
                marshalResultCode = $"var result = ObjCRuntime.Runtime.GetNSObject<{_wrapperSignature.ReturnType}>(rawResult);";
            }
            else
            {
                marshalResultCode = $"var result = SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>(new IntPtr(&rawResult));";
            }

            var text = $$"""
                        private static unsafe delegate* unmanaged[Cdecl]<{{(voidReturn ? "" : $"{_pInvokeSignature.ReturnType}, ")}}IntPtr, void> {{callbackFieldName}} = &{{callbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
                        private static void {{callbackMethodName}}({{(voidReturn ? "" : $"{_pInvokeSignature.ReturnType} rawResult, ")}}IntPtr task)
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);
                            try
                            {
                                {{(voidReturn ? "" : marshalResultCode)}}
                                {{(requiresInitWithCopy ? $"var metadata = SwiftObjectHelper<{_wrapperSignature.ReturnType}>.GetTypeMetadata();" : "")}}
                                {{(requiresInitWithCopy ? $"Span<byte> payloadSpan = stackalloc byte[(int)metadata.Size];" : "")}}
                                {{(requiresInitWithCopy ? $"IntPtr payload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(payloadSpan));" : "")}}
                                {{(requiresInitWithCopy ? $"SwiftMarshal.MarshalToSwift(result, ref payloadSpan);" : "")}}
                                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource{{(voidReturn ? "" : $"<{_wrapperSignature.ReturnType}>")}} holderTcs)
                                {
                                    // Free copy buffer memory for non-frozen params and release retained self
                                    // Note: Original params in holder keep internal storage alive
                                    for (int i = 1; i < holder.Length; i++)
                                    {
                                        if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                                        {
                                            // Release the extra retain added for async safety
                                            Arc.Release(retained.Ptr);
                                        }
                                        else if (holder[i] is DeferredSafeHandleRelease deferred)
                                        {
                                            // Release the SafeHandle that was kept alive for async safety
                                            deferred.Handle.DangerousRelease();
                                        }
                                        else if (holder[i] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                                        {
                                            // Call Destroy to release Swift references, then free buffer
                                            copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                                            NativeMemory.Free((void*)copyBuffer.Buffer);
                                        }
                                    }
                                    holderTcs.TrySetResult({{(voidReturn ? "" : "result")}});
                                }
                                else if (handle.Target is TaskCompletionSource{{(voidReturn ? "" : $"<{_wrapperSignature.ReturnType}>")}} directTcs)
                                {
                                    directTcs.TrySetResult({{(voidReturn ? "" : "result")}});
                                }
                            }
                            finally
                            {
                                handle.Free();
                            }
                        }

                        private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> {{errorCallbackFieldName}} = &{{errorCallbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
                        private static void {{errorCallbackMethodName}}(IntPtr errorMessagePtr, IntPtr task)
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);
                            try
                            {
                                var errorMessage = Marshal.PtrToStringUTF8(errorMessagePtr) ?? "Unknown Swift error";
                                var exception = new SwiftException(errorMessage);

                                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource{{(voidReturn ? "" : $"<{_wrapperSignature.ReturnType}>")}} holderTcs)
                                {
                                    // Free copy buffer memory for non-frozen params and release retained self
                                    for (int i = 1; i < holder.Length; i++)
                                    {
                                        if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                                        {
                                            Arc.Release(retained.Ptr);
                                        }
                                        else if (holder[i] is DeferredSafeHandleRelease deferred)
                                        {
                                            // Release the SafeHandle that was kept alive for async safety
                                            deferred.Handle.DangerousRelease();
                                        }
                                        else if (holder[i] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                                        {
                                            // Call Destroy to release Swift references, then free buffer
                                            copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                                            NativeMemory.Free((void*)copyBuffer.Buffer);
                                        }
                                    }
                                    holderTcs.TrySetException(exception);
                                }
                                else if (handle.Target is TaskCompletionSource{{(voidReturn ? "" : $"<{_wrapperSignature.ReturnType}>")}} directTcs)
                                {
                                    directTcs.TrySetException(exception);
                                }
                            }
                            finally
                            {
                                handle.Free();
                            }
                        }
                """;
            csWriter.WriteLine(text);
        }

        /// <summary>
        /// Emits async wrapper for methods returning tuples.
        /// Handles marshalling each tuple element individually.
        /// For @convention(c) compatibility, tuple elements are flattened into separate callback parameters.
        /// </summary>
        private void EmitAsyncWrapperForTuple(CSharpWriter csWriter, ArgumentDecl returnType, string callbackFieldName, string callbackMethodName, string errorCallbackFieldName, string errorCallbackMethodName)
        {
            var tupleTypeSpec = (TupleTypeSpec)returnType.SwiftTypeSpec;
            var elements = tupleTypeSpec.Elements;

            // Build flattened callback parameter lists
            var delegateParams = new List<string>();  // For delegate* signature
            var methodParams = new List<string>();    // For method signature
            var marshalLines = new List<string>();
            var resultElements = new List<string>();

            for (int i = 0; i < elements.Count; i++)
            {
                var element = elements[i];
                var rawParamName = $"rawItem{i}";
                var resultName = $"item{i}";
                var pInvokeType = GetPInvokeTypeForTupleElement(element);
                var csharpType = GetCSharpTypeForTupleElement(element);

                delegateParams.Add(pInvokeType);
                methodParams.Add($"{pInvokeType} {rawParamName}");

                // Determine how to marshal this element
                var marshalCode = GetTupleElementMarshalCode(element, rawParamName, resultName, csharpType);
                if (marshalCode != null)
                {
                    marshalLines.Add(marshalCode);
                }

                // Build the result element (with label if present)
                if (!string.IsNullOrEmpty(element.TypeLabel))
                {
                    resultElements.Add($"{element.TypeLabel}: {resultName}");
                }
                else
                {
                    resultElements.Add(resultName);
                }
            }

            var delegateTypeParams = string.Join(", ", delegateParams) + ", IntPtr, void";
            var methodParamList = string.Join(", ", methodParams) + ", IntPtr task";
            var marshalResultCode = string.Join("\n                    ", marshalLines);
            var tupleConstruction = $"var result = ({string.Join(", ", resultElements)});";

            var text = $$"""
                        private static unsafe delegate* unmanaged[Cdecl]<{{delegateTypeParams}}> {{callbackFieldName}} = &{{callbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
                        private static void {{callbackMethodName}}({{methodParamList}})
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);
                            try
                            {
                                {{marshalResultCode}}
                                {{tupleConstruction}}
                                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> holderTcs)
                                {
                                    // Free copy buffer memory for non-frozen params and release retained self
                                    for (int i = 1; i < holder.Length; i++)
                                    {
                                        if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                                        {
                                            Arc.Release(retained.Ptr);
                                        }
                                        else if (holder[i] is DeferredSafeHandleRelease deferred)
                                        {
                                            // Release the SafeHandle that was kept alive for async safety
                                            deferred.Handle.DangerousRelease();
                                        }
                                        else if (holder[i] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                                        {
                                            // Call Destroy to release Swift references, then free buffer
                                            copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                                            NativeMemory.Free((void*)copyBuffer.Buffer);
                                        }
                                    }
                                    holderTcs.TrySetResult(result);
                                }
                                else if (handle.Target is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> directTcs)
                                {
                                    directTcs.TrySetResult(result);
                                }
                            }
                            finally
                            {
                                handle.Free();
                            }
                        }

                        private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> {{errorCallbackFieldName}} = &{{errorCallbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
                        private static void {{errorCallbackMethodName}}(IntPtr errorMessagePtr, IntPtr task)
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);
                            try
                            {
                                var errorMessage = Marshal.PtrToStringUTF8(errorMessagePtr) ?? "Unknown Swift error";
                                var exception = new SwiftException(errorMessage);

                                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> holderTcs)
                                {
                                    // Free copy buffer memory for non-frozen params and release retained self
                                    for (int i = 1; i < holder.Length; i++)
                                    {
                                        if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                                        {
                                            Arc.Release(retained.Ptr);
                                        }
                                        else if (holder[i] is DeferredSafeHandleRelease deferred)
                                        {
                                            // Release the SafeHandle that was kept alive for async safety
                                            deferred.Handle.DangerousRelease();
                                        }
                                        else if (holder[i] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                                        {
                                            // Call Destroy to release Swift references, then free buffer
                                            copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                                            NativeMemory.Free((void*)copyBuffer.Buffer);
                                        }
                                    }
                                    holderTcs.TrySetException(exception);
                                }
                                else if (handle.Target is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> directTcs)
                                {
                                    directTcs.TrySetException(exception);
                                }
                            }
                            finally
                            {
                                handle.Free();
                            }
                        }
                """;
            csWriter.WriteLine(text);
        }

        /// <summary>
        /// Emits async wrapper for methods returning String.
        /// Uses SBW_Utf8Slice for @convention(c) compatibility.
        /// Swift allocates UTF-8 buffer, C# copies via Marshal.PtrToStringUTF8 and frees via SBW_Free.
        /// </summary>
        private void EmitAsyncWrapperForString(CSharpWriter csWriter, string callbackFieldName, string callbackMethodName, string errorCallbackFieldName, string errorCallbackMethodName)
        {
            // Determine the wrapper library path - SBW_Free is emitted in the Swift wrapper library
            var moduleDecl = _env.MethodDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(_env.MethodDecl.ModuleDecl));
            var moduleLibPath = _env.TypeDatabase.GetLibraryPath(moduleDecl.Name);
            var wrapperLibPath = _env.TypeDatabase.AsyncLibraryName ?? moduleLibPath;

            // Get the module-specific symbol name for SBW_Free
            var freeSymbolName = Utf8SliceEmitter.GetFreeSymbolName(moduleDecl.Name);

            // Determine fully-qualified C# type key for deduplication (types with multiple async
            // string methods should only emit SBW_Free P/Invoke once). Use ModuleQualifiedName
            // to avoid collisions between nested types with the same simple name in different
            // containing types (e.g., OuterA.ErrorType vs OuterB.ErrorType).
            var typeKey = (_env.ParentDecl as TypeDecl)?.SwiftTypeName.ModuleQualifiedName ?? moduleDecl.Name;
            var needsFreePInvoke = !Utf8SliceEmitter.HasFreePInvokeForType(typeKey);
            if (needsFreePInvoke)
            {
                Utf8SliceEmitter.MarkFreePInvokeEmittedForType(typeKey);
            }

            // For string returns, the callback receives (ptr, len) directly - custom structs not allowed in @convention(c)
            // We unmarshal to string and then call SBW_Free in finally block
            var freePInvokeDecl = needsFreePInvoke
                ? $"""
                        [System.Runtime.InteropServices.DllImport("{wrapperLibPath}", EntryPoint = "{freeSymbolName}")]
                        private static extern void SBW_Free(IntPtr ptr);

                """
                : "";

            var text = $$"""
                        {{freePInvokeDecl}}private static unsafe delegate* unmanaged[Cdecl]<IntPtr, nint, IntPtr, void> {{callbackFieldName}} = &{{callbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
                        private static void {{callbackMethodName}}(IntPtr slicePtr, nint sliceLen, IntPtr task)
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);
                            try
                            {
                                // Unmarshal UTF-8 to string
                                string result;
                                if (sliceLen == 0)
                                {
                                    result = string.Empty;
                                }
                                else
                                {
                                    result = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(slicePtr, (int)sliceLen)!;
                                }

                                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> holderTcs)
                                {
                                    // Free copy buffer memory for non-frozen params and release retained self
                                    for (int i = 1; i < holder.Length; i++)
                                    {
                                        if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                                        {
                                            Arc.Release(retained.Ptr);
                                        }
                                        else if (holder[i] is DeferredSafeHandleRelease deferred)
                                        {
                                            deferred.Handle.DangerousRelease();
                                        }
                                        else if (holder[i] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                                        {
                                            copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                                            NativeMemory.Free((void*)copyBuffer.Buffer);
                                        }
                                    }
                                    holderTcs.TrySetResult(result);
                                }
                                else if (handle.Target is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> directTcs)
                                {
                                    directTcs.TrySetResult(result);
                                }
                            }
                            finally
                            {
                                // Always free Swift-allocated memory (even empty strings allocate 1 byte)
                                SBW_Free(slicePtr);
                                handle.Free();
                            }
                        }

                        private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> {{errorCallbackFieldName}} = &{{errorCallbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
                        private static void {{errorCallbackMethodName}}(IntPtr errorMessagePtr, IntPtr task)
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);
                            try
                            {
                                var errorMessage = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(errorMessagePtr) ?? "Unknown Swift error";
                                var exception = new SwiftException(errorMessage);

                                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> holderTcs)
                                {
                                    // Free copy buffer memory for non-frozen params and release retained self
                                    for (int i = 1; i < holder.Length; i++)
                                    {
                                        if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                                        {
                                            Arc.Release(retained.Ptr);
                                        }
                                        else if (holder[i] is DeferredSafeHandleRelease deferred)
                                        {
                                            deferred.Handle.DangerousRelease();
                                        }
                                        else if (holder[i] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                                        {
                                            copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                                            NativeMemory.Free((void*)copyBuffer.Buffer);
                                        }
                                    }
                                    holderTcs.TrySetException(exception);
                                }
                                else if (handle.Target is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> directTcs)
                                {
                                    directTcs.TrySetException(exception);
                                }
                            }
                            finally
                            {
                                handle.Free();
                            }
                        }
                """;
            csWriter.WriteLine(text);
        }

        /// <summary>
        /// Emits async wrapper for methods returning Array&lt;String&gt;.
        /// Deserializes from flat buffer format: [count][lengths...][data...].
        /// Returns IReadOnlyList&lt;string&gt; for idiomatic C# usage.
        /// </summary>
        private void EmitAsyncWrapperForArrayString(CSharpWriter csWriter, string callbackFieldName, string callbackMethodName, string errorCallbackFieldName, string errorCallbackMethodName)
        {
            // Determine the wrapper library path - SBW_Free is emitted in the Swift wrapper library
            var moduleDecl = _env.MethodDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(_env.MethodDecl.ModuleDecl));
            var moduleLibPath = _env.TypeDatabase.GetLibraryPath(moduleDecl.Name);
            var wrapperLibPath = _env.TypeDatabase.AsyncLibraryName ?? moduleLibPath;

            // Get the module-specific symbol name for SBW_Free
            var freeSymbolName = Utf8SliceEmitter.GetFreeSymbolName(moduleDecl.Name);

            // Determine fully-qualified C# type key for deduplication
            var typeKey = (_env.ParentDecl as TypeDecl)?.SwiftTypeName.ModuleQualifiedName ?? moduleDecl.Name;
            var needsFreePInvoke = !Utf8SliceEmitter.HasFreePInvokeForType(typeKey);
            if (needsFreePInvoke)
            {
                Utf8SliceEmitter.MarkFreePInvokeEmittedForType(typeKey);
            }

            var freePInvokeDecl = needsFreePInvoke
                ? $"""
                        [System.Runtime.InteropServices.DllImport("{wrapperLibPath}", EntryPoint = "{freeSymbolName}")]
                        private static extern void SBW_Free(IntPtr ptr);

                """
                : "";

            // The wrapper return type is IReadOnlyList<SwiftString> (matches non-async Array<String> return type)
            var text = $$"""
                        {{freePInvokeDecl}}private static unsafe delegate* unmanaged[Cdecl]<IntPtr, nint, IntPtr, void> {{callbackFieldName}} = &{{callbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
                        private static void {{callbackMethodName}}(IntPtr bufferPtr, nint bufferLen, IntPtr task)
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);
                            System.Exception? deserializationError = null;
                            System.Collections.Generic.List<SwiftString>? result = null;

                            try
                            {
                                // Deserialize Array<String> from flat buffer into SwiftString instances
                                // Buffer format: [count: Int64][len0: Int64]...[lenN-1: Int64][str0 bytes]...[strN-1 bytes]
                                if (bufferLen <= sizeof(long))
                                {
                                    // Empty array or just count field
                                    result = new System.Collections.Generic.List<SwiftString>();
                                }
                                else
                                {
                                    long count = *(long*)bufferPtr;

                                    // Validate count is in valid range for int cast
                                    if (count < 0 || count > int.MaxValue)
                                        throw new InvalidOperationException($"Invalid array count in async callback buffer: {count}");

                                    if (count == 0)
                                    {
                                        result = new System.Collections.Generic.List<SwiftString>();
                                    }
                                    else
                                    {
                                        // Calculate and validate header size (count already validated <= int.MaxValue)
                                        int headerSize = sizeof(long) * (1 + (int)count);
                                        if (headerSize > bufferLen)
                                            throw new InvalidOperationException($"Buffer too small for array header: need {headerSize}, have {bufferLen}");

                                        // Read lengths from header
                                        long* lengthsPtr = (long*)bufferPtr + 1;

                                        // Validate all lengths are in valid range and total doesn't exceed buffer
                                        long totalDataLen = 0;
                                        for (int i = 0; i < count; i++)
                                        {
                                            long len = lengthsPtr[i];
                                            if (len < 0 || len > int.MaxValue)
                                                throw new InvalidOperationException($"Invalid string length at index {i}: {len}");
                                            totalDataLen += len;
                                        }
                                        if (headerSize + totalDataLen > bufferLen)
                                            throw new InvalidOperationException($"Buffer too small for array data: need {headerSize + totalDataLen}, have {bufferLen}");

                                        // Read strings and wrap in SwiftString (casts are safe after validation)
                                        result = new System.Collections.Generic.List<SwiftString>((int)count);
                                        int dataOffset = headerSize;
                                        for (int i = 0; i < count; i++)
                                        {
                                            int strLen = (int)lengthsPtr[i];
                                            string s = strLen == 0
                                                ? string.Empty
                                                : System.Runtime.InteropServices.Marshal.PtrToStringUTF8(bufferPtr + dataOffset, strLen)!;
                                            result.Add(new SwiftString(s));
                                            dataOffset += strLen;
                                        }
                                    }
                                }
                            }
                            catch (System.Exception ex)
                            {
                                // Capture deserialization errors to report via TCS (can't throw from UnmanagedCallersOnly)
                                deserializationError = ex;
                            }

                            try
                            {
                                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> holderTcs)
                                {
                                    // Free copy buffer memory for non-frozen params and release retained self
                                    for (int i = 1; i < holder.Length; i++)
                                    {
                                        if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                                        {
                                            Arc.Release(retained.Ptr);
                                        }
                                        else if (holder[i] is DeferredSafeHandleRelease deferred)
                                        {
                                            deferred.Handle.DangerousRelease();
                                        }
                                        else if (holder[i] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                                        {
                                            copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                                            NativeMemory.Free((void*)copyBuffer.Buffer);
                                        }
                                    }
                                    if (deserializationError != null)
                                        holderTcs.TrySetException(deserializationError);
                                    else
                                        holderTcs.TrySetResult(result!);
                                }
                                else if (handle.Target is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> directTcs)
                                {
                                    if (deserializationError != null)
                                        directTcs.TrySetException(deserializationError);
                                    else
                                        directTcs.TrySetResult(result!);
                                }
                            }
                            finally
                            {
                                // Always free Swift-allocated memory (even empty arrays allocate 1 byte)
                                SBW_Free(bufferPtr);
                                handle.Free();
                            }
                        }

                        private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> {{errorCallbackFieldName}} = &{{errorCallbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
                        private static void {{errorCallbackMethodName}}(IntPtr errorMessagePtr, IntPtr task)
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);
                            try
                            {
                                var errorMessage = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(errorMessagePtr) ?? "Unknown Swift error";
                                var exception = new SwiftException(errorMessage);

                                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> holderTcs)
                                {
                                    // Free copy buffer memory for non-frozen params and release retained self
                                    for (int i = 1; i < holder.Length; i++)
                                    {
                                        if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                                        {
                                            Arc.Release(retained.Ptr);
                                        }
                                        else if (holder[i] is DeferredSafeHandleRelease deferred)
                                        {
                                            deferred.Handle.DangerousRelease();
                                        }
                                        else if (holder[i] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                                        {
                                            copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                                            NativeMemory.Free((void*)copyBuffer.Buffer);
                                        }
                                    }
                                    holderTcs.TrySetException(exception);
                                }
                                else if (handle.Target is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> directTcs)
                                {
                                    directTcs.TrySetException(exception);
                                }
                            }
                            finally
                            {
                                handle.Free();
                            }
                        }
                """;
            csWriter.WriteLine(text);
        }

        /// <summary>
        /// Emits async wrapper for methods returning complex types (classes, enums, structs).
        /// These types can't be passed directly through @convention(c) callbacks, so Swift
        /// allocates memory, stores the result, and passes an OpaquePointer.
        /// C# receives IntPtr, reads the value, and frees the memory.
        /// </summary>
        private void EmitAsyncWrapperForComplexType(CSharpWriter csWriter, string callbackFieldName, string callbackMethodName, string errorCallbackFieldName, string errorCallbackMethodName, bool isClassType)
        {
            // Determine the wrapper library path - SBW_Free is emitted in the Swift wrapper library
            var moduleDecl = _env.MethodDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(_env.MethodDecl.ModuleDecl));
            var moduleLibPath = _env.TypeDatabase.GetLibraryPath(moduleDecl.Name);
            var wrapperLibPath = _env.TypeDatabase.AsyncLibraryName ?? moduleLibPath;

            // Get the module-specific symbol name for SBW_Free
            var freeSymbolName = Utf8SliceEmitter.GetFreeSymbolName(moduleDecl.Name);

            // Determine fully-qualified C# type key for deduplication
            var typeKey = (_env.ParentDecl as TypeDecl)?.SwiftTypeName.ModuleQualifiedName ?? moduleDecl.Name;
            var needsFreePInvoke = !Utf8SliceEmitter.HasFreePInvokeForType(typeKey);
            if (needsFreePInvoke)
            {
                Utf8SliceEmitter.MarkFreePInvokeEmittedForType(typeKey);
            }

            var freePInvokeDecl = needsFreePInvoke
                ? $"""
                        [System.Runtime.InteropServices.DllImport("{wrapperLibPath}", EntryPoint = "{freeSymbolName}")]
                        private static extern void SBW_Free(IntPtr ptr);

                """
                : "";

            // For class types, Swift retained the object before passing through callback.
            // We must read the object pointer from the buffer (resultPtr points to buffer containing the pointer)
            // and release it in finally to avoid leaking the retain even if marshalling throws.
            var readObjPtrCode = isClassType
                ? "\n                            // Read object pointer from buffer (for class types, buffer contains the object reference)\n                            IntPtr _retainedObjPtr = *(IntPtr*)resultPtr;"
                : "";
            var releaseObjCode = isClassType
                ? "\n                                // Release the retain added by Swift (must use dereferenced object pointer, not buffer pointer)\n                                Arc.Release(_retainedObjPtr);"
                : "";

            var text = $$"""
                        {{freePInvokeDecl}}private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> {{callbackFieldName}} = &{{callbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
                        private static void {{callbackMethodName}}(IntPtr resultPtr, IntPtr task)
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);{{readObjPtrCode}}
                            try
                            {
                                // Read result from pointer (Swift allocated memory and stored the value)
                                var result = SwiftMarshal.MarshalFromSwift<{{_wrapperSignature.ReturnType}}>(resultPtr);

                                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> holderTcs)
                                {
                                    // Free copy buffer memory for non-frozen params and release retained self
                                    for (int i = 1; i < holder.Length; i++)
                                    {
                                        if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                                        {
                                            Arc.Release(retained.Ptr);
                                        }
                                        else if (holder[i] is DeferredSafeHandleRelease deferred)
                                        {
                                            deferred.Handle.DangerousRelease();
                                        }
                                        else if (holder[i] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                                        {
                                            copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                                            NativeMemory.Free((void*)copyBuffer.Buffer);
                                        }
                                    }
                                    holderTcs.TrySetResult(result);
                                }
                                else if (handle.Target is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> directTcs)
                                {
                                    directTcs.TrySetResult(result);
                                }
                            }
                            finally
                            {{{releaseObjCode}}
                                // Free Swift-allocated memory
                                SBW_Free(resultPtr);
                                handle.Free();
                            }
                        }

                        private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> {{errorCallbackFieldName}} = &{{errorCallbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
                        private static void {{errorCallbackMethodName}}(IntPtr errorMessagePtr, IntPtr task)
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);
                            try
                            {
                                var errorMessage = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(errorMessagePtr) ?? "Unknown Swift error";
                                var exception = new SwiftException(errorMessage);

                                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> holderTcs)
                                {
                                    // Free copy buffer memory for non-frozen params and release retained self
                                    for (int i = 1; i < holder.Length; i++)
                                    {
                                        if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                                        {
                                            Arc.Release(retained.Ptr);
                                        }
                                        else if (holder[i] is DeferredSafeHandleRelease deferred)
                                        {
                                            deferred.Handle.DangerousRelease();
                                        }
                                        else if (holder[i] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                                        {
                                            copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                                            NativeMemory.Free((void*)copyBuffer.Buffer);
                                        }
                                    }
                                    holderTcs.TrySetException(exception);
                                }
                                else if (handle.Target is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> directTcs)
                                {
                                    directTcs.TrySetException(exception);
                                }
                            }
                            finally
                            {
                                handle.Free();
                            }
                        }
                """;
            csWriter.WriteLine(text);
        }

        /// <summary>
        /// Determines if a TypeSpec represents Swift.Array&lt;Swift.String&gt;.
        /// Used to detect array-of-string returns that need flat buffer serialization in async callbacks.
        /// </summary>
        private bool IsArrayOfString(TypeSpec typeSpec)
        {
            if (typeSpec is not NamedTypeSpec namedType)
                return false;

            // Check if it's Swift.Array
            var typeName = SwiftTypeName.FromTypeSpec(namedType);
            if (typeName.ModuleQualifiedName != "Swift.Array")
                return false;

            // Check if it has exactly one generic parameter
            if (namedType.GenericParameters.Count != 1)
                return false;

            // Check if the generic parameter is Swift.String
            var elementType = namedType.GenericParameters[0];
            return elementType.ToString() == "Swift.String";
        }

        /// <summary>
        /// Checks if a Swift type name is a primitive type that can be passed directly
        /// through @convention(c) callbacks without pointer indirection.
        /// </summary>
        private static bool IsSwiftPrimitive(string swiftTypeName)
        {
            return swiftTypeName switch
            {
                "Swift.Int" => true,
                "Swift.UInt" => true,
                "Swift.Int8" => true,
                "Swift.UInt8" => true,
                "Swift.Int16" => true,
                "Swift.UInt16" => true,
                "Swift.Int32" => true,
                "Swift.UInt32" => true,
                "Swift.Int64" => true,
                "Swift.UInt64" => true,
                "Swift.Float" => true,
                "Swift.Double" => true,
                "Swift.Bool" => true,
                _ => false
            };
        }
    }
}
