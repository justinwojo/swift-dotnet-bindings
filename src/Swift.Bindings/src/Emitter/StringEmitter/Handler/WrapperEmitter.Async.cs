// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;

namespace BindingsGeneration
{
    internal partial class WrapperEmitter
    {
        /// <summary>
        /// Returns the helper class name prefix for referencing hoisted async callbacks.
        /// When async callbacks are hoisted to the PInvokeHelper class (generic parent types),
        /// field/method references must be prefixed with the helper class name.
        /// </summary>
        private string AsyncCallbackPrefix =>
            _env.PInvokeHelperContext != null ? $"{_env.PInvokeHelperContext.HelperClassName}." : "";

        /// <summary>
        /// Visibility for async fields/P/Invokes hoisted to the helper class.
        /// Members accessed from outside the helper class need <c>internal</c>;
        /// inline members (emitted inside the same generic class) use <c>private</c>.
        /// </summary>
        private string AsyncFieldVisibility =>
            _env.PInvokeHelperContext != null ? "internal" : "private";

        /// <summary>
        /// Flushes the async helper writer to PInvokeHelperContext.RawCodeBlocks.
        /// Called at each exit point of EmitAsyncWrapper when callbacks were redirected.
        /// </summary>
        private void FlushAsyncHelperWriter()
        {
            if (_asyncHelperWriter != null && _env.PInvokeHelperContext != null)
            {
                _asyncHelperCsWriter!.Flush();
                var content = _asyncHelperWriter.ToString();
                if (!string.IsNullOrWhiteSpace(content))
                    _env.PInvokeHelperContext.RawCodeBlocks.Add(content);
                _asyncHelperWriter = null;
                _asyncHelperCsWriter = null;
            }
        }

        /// <summary>
        /// Returns generic params string containing only method-own generics (excluding parent-type generics).
        /// Used for async extension wrappers where parent-type generics come from the extension scope.
        /// </summary>
        private static string BuildMethodOwnGenericParams(MethodDecl methodDecl)
        {
            var parentParams = methodDecl.ParentDecl is TypeDecl td && td.IsGeneric
                ? new HashSet<string>(td.GenericParameters.Select(p => p.TypeName))
                : new HashSet<string>();
            var ownParams = methodDecl.GenericParameters
                .Where(p => !parentParams.Contains(p.TypeName))
                .Select(p => p.SugaredTypeName)
                .ToList();
            return ownParams.Count > 0 ? $"<{string.Join(", ", ownParams)}>" : "";
        }

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
            // Free functions (ParentDecl is ModuleDecl, not TypeDecl) have no self — never treat as instance.
            // Global functions may have MethodType != Static in ABI metadata, but they're not instance methods.
            bool isInstanceMethod = _env.MethodDecl.MethodType != MethodType.Static && !isAsyncConstructor && _env.ParentDecl is TypeDecl;
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
                    // C4: ObjC-bridged types (UIViewController, etc.), ObjC-rooted types
                    // (STPAPIClient, etc.), and ObjC-bridgeable value types (URL) are .NET
                    // GC-managed objects — they don't need copy-buffer treatment and emitting
                    // SwiftObjectHelper<T> for them causes CS0311/CS1061/CS0128.
                    if (MarshallingHelpers.IsObjCBridged(typeRecord) || MarshallingHelpers.IsObjCRooted(typeRecord)
                        || MarshallingHelpers.IsObjCBridgeable(typeRecord))
                        return false;
                    // Simple enums are C# value types — they don't need copy-buffer treatment
                    if (typeRecord.Kind == TypeRecordKind.Enum &&
                        (typeRecord.Flags & TypeRecordFlags.SimpleEnum) != 0)
                        return false;
                    return !MarshallingHelpers.IsTypeFrozen(typeRecord) || typeRecord.Kind == TypeRecordKind.Enum;
                })
                .ToList();

            // Identify frozen blittable struct params that need heap allocation for async safety.
            // In sync methods, these use stackalloc (fast, stack-lifetime is sufficient).
            // In async methods, stackalloc is unsafe across await boundaries — use NativeMemory.Alloc.
            // These are params that EmitCdeclFrozenStructMarshalling would normally handle with stackalloc.
            var frozenBlittableAsyncParams = _env.MethodDecl.UsesCdeclWrapper
                ? _env.MethodDecl.CSSignature.Skip(1)
                    .Where(p => WrapperValidation.IsNonPrimitiveFrozenStructParam(p, _env.TypeDatabase))
                    .Where(p =>
                    {
                        // Same skip conditions as EmitCdeclFrozenStructMarshalling
                        if (_env.BoundGenericsHandler.IsBoundGeneric(p)) return false;
                        if (_env.ClosureHandler.IsClosure(p)) return false;
                        if (MarshallingHelpers.IsConvertibleType(p.SwiftTypeSpec)) return false;
                        if (_env.TypeConversionHandler.HasNativeTypeRemapping(p.SwiftTypeSpec)) return false;
                        var typeRecord = _env.TypeDatabase.GetTypeRecordOrAnyType(p.SwiftTypeSpec);
                        // Only blittable frozen structs (not those with RequiresMemoryManagement —
                        // those use DangerousGetHandle() which is already heap-allocated)
                        return !MarshallingHelpers.RequiresMemoryManagement(typeRecord);
                    })
                    .ToList()
                : new List<ArgumentDecl>();

            // Identify large Optional params that need UnsafeRawPointer widening (separate from non-frozen)
            var largeOptionalParams = _env.MethodDecl.CSSignature.Skip(1)
                .Where(p => _env.BoundGenericsHandler.IsLargeOptionalParam(p.SwiftTypeSpec) ||
                            _env.BoundGenericsHandler.IsLargeOptionalProtocolParam(p.SwiftTypeSpec))
                .ToList();
            bool hasReadCode = nonFrozenParams.Count > 0 || largeOptionalParams.Count > 0 || frozenBlittableAsyncParams.Count > 0;

            // For non-frozen parameters, create proper copies using InitializeWithCopy FIRST
            // (before the holder is created), so the copy buffer pointers can be stored in the holder.
            // Swift reads via .pointee (bitwise copy). Original params kept alive to maintain ref count.
            if (nonFrozenParams.Count > 0)
            {
                // Create copy buffers for non-frozen parameters
                foreach (var p in nonFrozenParams)
                {
                    // Use normalized C# name (prefers PrivateName over ABI Name)
                    // to match the method signature emitted by WrapperEmitter.Marshalling
                    var csName = NameProvider.GetCSharpParameterName(p);
                    var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(p.SwiftTypeSpec);
                    var typeName = typeRecord.CSharpTypeName.FullyQualifiedName;

                    // For native-remapped types (e.g., byte[] -> Swift.Foundation.Data), we need to
                    // convert to the Swift type first before copying. The wrapper signature uses the
                    // native type but the underlying Swift type is what we need to copy.
                    if (!_env.MethodDecl.IsAccessor && _env.TypeConversionHandler.HasNativeTypeRemapping(p.SwiftTypeSpec))
                    {
                        // Convert native type to Swift type, then copy from Swift type
                        var conversion = _env.TypeConversionHandler.GetNativeParameterConversion(csName, p.SwiftTypeSpec);
                        csWriter.WriteLines($"""
                            var {csName}Metadata = SwiftObjectHelper<{typeName}>.GetTypeMetadata();
                            IntPtr {csName}CopyBuffer = (IntPtr)NativeMemory.Alloc({csName}Metadata.Size);
                            using var {csName}SwiftTemp = {conversion};
                            {csName}Metadata.ValueWitnessTable->InitializeWithCopy(
                                (void*){csName}CopyBuffer,
                                (void*){csName}SwiftTemp.Payload.DangerousGetHandle(),
                                {csName}Metadata);
                            IntPtr {csName}Handle = {csName}CopyBuffer;
                            var {csName}CopyBufferWrapper = new CopyBufferWithType({csName}CopyBuffer, {csName}Metadata);
                            """);
                    }
                    else
                    {
                        csWriter.WriteLines($"""
                            var {csName}Metadata = SwiftObjectHelper<{typeName}>.GetTypeMetadata();
                            IntPtr {csName}CopyBuffer = (IntPtr)NativeMemory.Alloc({csName}Metadata.Size);
                            {csName}Metadata.ValueWitnessTable->InitializeWithCopy(
                                (void*){csName}CopyBuffer,
                                (void*){csName}.Payload.DangerousGetHandle(),
                                {csName}Metadata);
                            IntPtr {csName}Handle = {csName}CopyBuffer;
                            var {csName}CopyBufferWrapper = new CopyBufferWithType({csName}CopyBuffer, {csName}Metadata);
                            """);
                    }
                }
            }

            // Heap-allocate frozen blittable struct params for async safety.
            // In sync methods, EmitCdeclFrozenStructMarshalling uses stackalloc (fast but stack-lifetime).
            // In async methods, the stack frame may be gone by the time the callback fires.
            // Use NativeMemory.Alloc + MarshalToSwift instead, with cleanup via CopyBufferWithType in holder.
            if (frozenBlittableAsyncParams.Count > 0)
            {
                foreach (var p in frozenBlittableAsyncParams)
                {
                    var csName = NameProvider.GetCSharpParameterName(p);
                    var typeRecord = _env.TypeDatabase.GetTypeRecordOrAnyType(p.SwiftTypeSpec);
                    var csTypeName = typeRecord.CSharpTypeName.FullyQualifiedName;
                    csWriter.WriteLines($"""
                        var {csName}Metadata = TypeMetadata.GetTypeMetadataOrThrow<{csTypeName}>();
                        IntPtr {csName}HeapBuffer = (IntPtr)NativeMemory.Alloc({csName}Metadata.Size);
                        var {csName}Span = new Span<byte>((byte*){csName}HeapBuffer, (int){csName}Metadata.Size);
                        SwiftMarshal.MarshalToSwift({csName}, ref {csName}Span);
                        IntPtr {csName}Ptr = {csName}HeapBuffer;
                        var {csName}CopyBufferWrapper = new CopyBufferWithType({csName}HeapBuffer, {csName}Metadata);
                        """);
                }
            }

            if (nonFrozenParams.Count > 0 || frozenBlittableAsyncParams.Count > 0)
            {
                // Now create the holder with copy buffer pointers AND original parameters AND self (for instance methods)
                // Original parameters must be kept alive to prevent GC from calling Destroy on them
                // while the async task is still running (InitializeWithCopy increments ref count,
                // but if original is destroyed, the internal storage could be freed prematurely)
                // Also keep 'this' alive for instance methods since SwiftSelf doesn't prevent GC
                // Include both non-frozen and frozen blittable copy buffer wrappers for cleanup
                var allCopyBufferWrappers = nonFrozenParams.Select(p => $"{NameProvider.GetCSharpParameterName(p)}CopyBufferWrapper")
                    .Concat(frozenBlittableAsyncParams.Select(p => $"{NameProvider.GetCSharpParameterName(p)}CopyBufferWrapper"));
                var copyBufferList = string.Join(", ", allCopyBufferWrappers);
                var originalParamList = string.Join(", ", nonFrozenParams.Select(p => $"(object){NameProvider.GetCSharpParameterName(p)}"));

                // For Swift classes, Arc.Retain the self pointer before async call.
                // SwiftSelf passes a raw pointer - no ARC semantics. By the time Swift's Task{}
                // closure runs, 'self' may be deallocated. Retain ensures Swift ARC tracks it.
                string selfInHolder;
                bool isObjCRootedClass = isSwiftClass && _env.ParentDecl is ClassDecl asyncClassDecl && asyncClassDecl.IsObjCRooted;
                if (isInstanceMethod && isObjCRootedClass)
                {
                    // ObjC-rooted classes: Handle IS the Swift object pointer (no _payload buffer)
                    csWriter.WriteLines($$"""
            IntPtr _selfPtr = Handle;
            Arc.Retain(_selfPtr);
            """);
                    selfInHolder = ", new RetainedSelfPtr(_selfPtr), (object)this";
                }
                else if (isInstanceMethod && isSwiftClass)
                {
                    // For Swift classes, retain self and store a RetainedSelfPtr marker
                    // SwiftClassHandle: DangerousGetHandle() IS the Swift object pointer (no dereference)
                    // DangerousAddRef prevents concurrent finalizer from releasing handle before Arc.Retain
                    csWriter.WriteLines($$"""
            bool _selfSuccess = false;
            _handle.DangerousAddRef(ref _selfSuccess);
            IntPtr _selfPtr = _handle.DangerousGetHandle();
            Arc.Retain(_selfPtr);
            _handle.DangerousRelease();
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

                // Build the holder elements: originalParamList may be empty when only frozen blittable params exist
                var originalParamSuffix = originalParamList.Length > 0 ? $", {originalParamList}" : "";
                csWriter.WriteLines($$"""
            TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}} _tcs = new TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}}();
            object[] _asyncCallHolder = new object[] { _tcs, {{copyBufferList}}{{originalParamSuffix}}{{selfInHolder}}, null! };
            GCHandle handle = GCHandle.Alloc(_asyncCallHolder, GCHandleType.Normal);
            """);
            }
            else if (isInstanceMethod)
            {
                // No non-frozen parameters, but still need to keep 'this' alive for instance methods
                // For Swift classes, also retain self to prevent deallocation during async execution
                bool isObjCRootedClassNoParams = isSwiftClass && _env.ParentDecl is ClassDecl asyncClassDeclNoParams && asyncClassDeclNoParams.IsObjCRooted;
                if (isObjCRootedClassNoParams)
                {
                    // ObjC-rooted classes: Handle IS the Swift object pointer (no _payload buffer)
                    csWriter.WriteLines($$"""
            TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}} _tcs = new TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}}();
            IntPtr _selfPtr = Handle;
            Arc.Retain(_selfPtr);
            object[] _asyncCallHolder = new object[] { _tcs, new RetainedSelfPtr(_selfPtr), (object)this, null! };
            GCHandle handle = GCHandle.Alloc(_asyncCallHolder, GCHandleType.Normal);
            """);
                }
                else if (isSwiftClass)
                {
                    // SwiftClassHandle: DangerousGetHandle() IS the Swift object pointer (no dereference)
                    // DangerousAddRef prevents concurrent finalizer from releasing handle before Arc.Retain
                    csWriter.WriteLines($$"""
            TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}} _tcs = new TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}}();
            bool _selfSuccess = false;
            _handle.DangerousAddRef(ref _selfSuccess);
            IntPtr _selfPtr = _handle.DangerousGetHandle();
            Arc.Retain(_selfPtr);
            _handle.DangerousRelease();
            object[] _asyncCallHolder = new object[] { _tcs, new RetainedSelfPtr(_selfPtr), (object)this, null! };
            GCHandle handle = GCHandle.Alloc(_asyncCallHolder, GCHandleType.Normal);
            """);
                }
                else
                {
                    // For structs, keep 'this' alive and defer SafeHandle release until callback
                    csWriter.WriteLines($$"""
            TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}} _tcs = new TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}}();
            object[] _asyncCallHolder = new object[] { _tcs, new DeferredSafeHandleRelease(_payload), (object)this, null! };
            GCHandle handle = GCHandle.Alloc(_asyncCallHolder, GCHandleType.Normal);
            """);
                }
            }
            else
            {
                // Static method with no non-frozen parameters
                csWriter.WriteLines($$"""
            TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}} _tcs = new TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}}();
            object[] _asyncCallHolder = new object[] { _tcs, null! };
            GCHandle handle = GCHandle.Alloc(_asyncCallHolder, GCHandleType.Normal);
            """);
            }

            // Pre-cancel check: if token is already cancelled, clean up and return immediately
            var tcsTypeParam = isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>";
            var cancelTaskPrefix = AsyncCallbackPrefix;
            var preCancelCleanup = BuildHolderCleanupCode("_asyncCallHolder", "    ", includeCancellationReg: false);
            csWriter.WriteLines($$"""
            if (cancellationToken.IsCancellationRequested)
            {
                // Clean up resources that were allocated for the async call
            """);
            csWriter.WriteLines(preCancelCleanup);
            csWriter.WriteLines($$"""
                handle.Free();
                return global::System.Threading.Tasks.Task.FromCanceled{{tcsTypeParam}}(cancellationToken);
            }
            if (cancellationToken.CanBeCanceled)
            {
                long taskId = (long)(IntPtr)handle;
                var _cancelRegistration = cancellationToken.Register(
                    static state =>
                    {
                        var (tcs, token, id) = ((TaskCompletionSource{{tcsTypeParam}}, global::System.Threading.CancellationToken, long))state!;
                        {{cancelTaskPrefix}}SBW_CancelTask(id);
                        tcs.TrySetCanceled(token);
                    },
                    (_tcs, cancellationToken, taskId));
                _asyncCallHolder[_asyncCallHolder.Length - 1] = new CancellationRegistrationHolder(_cancelRegistration, cancellationToken);
            }
            """);

            // Build parameter string - non-frozen types use UnsafeRawPointer in Swift wrapper
            // For tuple returns, flatten the tuple elements into separate callback parameters
            // because @convention(c) doesn't support Swift tuples
            var returnTypeArg = _env.MethodDecl.CSSignature.First();
            var tupleTypeSpecForCheck = _env.TupleHandler.IsTuple(returnTypeArg.SwiftTypeSpec)
                ? (TupleTypeSpec)returnTypeArg.SwiftTypeSpec : null;
            var isTupleReturn = tupleTypeSpecForCheck != null &&
                                (_env.TupleHandler.IsSupportedTuple(tupleTypeSpecForCheck) ||
                                 _env.TupleHandler.IsSupportedTuple(tupleTypeSpecForCheck, _genericContext));

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
                var resultVar = $"result{_env.MethodDecl.Name}";

                // Build callback param types and invocation args, handling VALUE TYPES that
                // can't be passed by value in @convention(c) callbacks.
                // Non-primitive value types (Foundation.Data, Swift.String, frozen structs)
                // may have ABI issues: ObjC bridging, Mono JIT struct parameter bugs, or
                // size mismatches. Heap-allocate and pass via pointer to avoid these issues.
                //
                // Class types and Optional<Class> are ALREADY raw pointers in @convention(c)
                // and must NOT be heap-allocated — the C# side unmarshals them as direct
                // object pointers (GetNSObject<T>, etc.).
                var elementTypes = new List<string>();
                var callbackArgParts = new List<string>();
                var heapAllocLines = new List<string>();
                var heapCleanupLines = new List<string>();
                // Build Swift generic param lookup for resolving τ_0_0 → T in tuple elements
                var swiftGenericParamLookup = new Dictionary<string, string>();
                foreach (var gp in _env.MethodDecl.GenericParameters)
                    swiftGenericParamLookup[gp.TypeName] = gp.SugaredTypeName;

                for (int i = 0; i < tupleTypeSpec.Elements.Count; i++)
                {
                    var element = tupleTypeSpec.Elements[i];
                    bool needsHeapAlloc = false;
                    bool isGenericTypeParam = TypeSpecHelpers.IsGenericTypeParameter(element);

                    if (isGenericTypeParam)
                    {
                        // Generic type params are unknown size — always heap-allocate via OpaquePointer
                        needsHeapAlloc = true;
                    }
                    else if (element is NamedTypeSpec elemNamed)
                    {
                        // Skip class types — they're already raw pointers in @convention(c)
                        if (_env.TypeDatabase.TryGetTypeRecord(elemNamed, out var elemRecord) &&
                            elemRecord.Kind == TypeRecordKind.Class)
                        {
                            needsHeapAlloc = false;
                        }
                        // Skip Optional<Class> — uses nil-pointer ABI (pointer or null)
                        else if (elemNamed.ContainsGenericParameters &&
                                 elemNamed.Name == "Swift.Optional" &&
                                 elemNamed.GenericParameters.Count > 0 &&
                                 elemNamed.GenericParameters[0] is NamedTypeSpec innerNamed &&
                                 _env.TypeDatabase.TryGetTypeRecord(innerNamed, out var innerRecord) &&
                                 innerRecord.Kind == TypeRecordKind.Class)
                        {
                            needsHeapAlloc = false;
                        }
                        // Non-primitive value types need heap allocation
                        else if (!IsSwiftPrimitive(elemNamed.Name))
                        {
                            needsHeapAlloc = true;
                        }
                    }

                    if (needsHeapAlloc)
                    {
                        // Non-primitive value type or generic type param: heap-allocate and pass via pointer.
                        // C# reads the struct from the pointer via MarshalFromSwift or direct cast.
                        // Resolve generic type params (τ_0_0) to Swift sugared names (T) for MemoryLayout.
                        var rawName = element.ToString();
                        var swiftTypeName = swiftGenericParamLookup.TryGetValue(rawName, out var sugared)
                            ? sugared : rawName;
                        var ptrVar = $"_tupleBuf{i}";
                        elementTypes.Add("UnsafeMutableRawPointer");
                        callbackArgParts.Add(ptrVar);
                        heapAllocLines.Add(
                            $"let {ptrVar} = UnsafeMutableRawPointer.allocate(byteCount: MemoryLayout<{swiftTypeName}>.size, alignment: MemoryLayout<{swiftTypeName}>.alignment)\n" +
                            $"                        {ptrVar}.initializeMemory(as: {swiftTypeName}.self, repeating: {resultVar}.{i}, count: 1)");
                        heapCleanupLines.Add(
                            $"{ptrVar}.assumingMemoryBound(to: {swiftTypeName}.self).deinitialize(count: 1)\n" +
                            $"                        {ptrVar}.deallocate()");
                    }
                    else
                    {
                        elementTypes.Add(element.ToString());
                        callbackArgParts.Add($"{resultVar}.{i}");
                    }
                }
                callbackParams = string.Join(", ", elementTypes) + ", ";
                callbackResultArgs = string.Join(", ", callbackArgParts) + ", ";

                // Retain ObjC class objects in tuple elements for C# ownership.
                // When an ObjC class is passed through @convention(c), Swift passes the raw pointer
                // WITHOUT adding an extra ARC retain. C#'s GetNSObject<T> takes ownership of one
                // retain count, so we must add it explicitly here.
                // Bridgeable value types (e.g., Foundation.Data → NSData) are automatically bridged
                // with +1 retain by Swift, so they don't need this treatment.
                var retainLines = new List<string>();
                for (int i = 0; i < tupleTypeSpec.Elements.Count; i++)
                {
                    var element = tupleTypeSpec.Elements[i];
                    bool needsRetain = false;
                    bool isOptional = false;

                    if (element is NamedTypeSpec named)
                    {
                        if (named.ContainsGenericParameters)
                        {
                            // Check for Optional<ObjCClass> — inner type needs conditional retain
                            var baseName = SwiftTypeName.FromModuleQualifiedName(named.Name);
                            if (_env.TypeDatabase.TryGetTypeRecord(baseName, out var baseRec) &&
                                baseRec.CSharpTypeName.Name == "SwiftOptional" &&
                                named.GenericParameters.Count > 0 &&
                                named.GenericParameters[0] is NamedTypeSpec innerNamed)
                            {
                                if (_env.TypeDatabase.TryGetTypeRecord(innerNamed, out var innerRec) &&
                                    innerRec.Kind == TypeRecordKind.Class)
                                {
                                    needsRetain = true;
                                    isOptional = true;
                                }
                            }
                        }
                        else if (!IsSwiftPrimitive(named.ToString()))
                        {
                            // Non-primitive, non-generic: retain if it's a class type
                            if (_env.TypeDatabase.TryGetTypeRecord(named, out var rec) &&
                                rec.Kind == TypeRecordKind.Class)
                            {
                                needsRetain = true;
                            }
                        }
                    }

                    if (needsRetain)
                    {
                        var resultAccess = $"{resultVar}.{i}";
                        if (isOptional)
                        {
                            retainLines.Add(
                                $"if let _tupleObj{i} = {resultAccess} {{ _ = Unmanaged<AnyObject>.passRetained(_tupleObj{i} as AnyObject) }}");
                        }
                        else
                        {
                            retainLines.Add(
                                $"_ = Unmanaged<AnyObject>.passRetained({resultAccess} as AnyObject)");
                        }
                    }
                }
                // Build stringMarshalCode: data buffer allocation + ObjC retain lines
                var marshalParts = new List<string>();

                // Non-primitive types: allocate heap buffer before callback, cleanup via defer
                if (heapAllocLines.Count > 0)
                {
                    marshalParts.Add(
                        "// Heap-allocate non-primitive types for @convention(c) callback (avoids ABI issues)\n" +
                        "                        " + string.Join("\n                        ", heapAllocLines) + "\n" +
                        "                        defer {\n" +
                        "                            " + string.Join("\n                            ", heapCleanupLines) + "\n" +
                        "                        }");
                }

                if (retainLines.Count > 0)
                {
                    marshalParts.Add(
                        "// Retain ObjC class objects for C# ownership (GetNSObject takes ownership of this retain)\n" +
                        "                        " + string.Join("\n                        ", retainLines));
                }

                if (marshalParts.Count > 0)
                {
                    stringMarshalCode = string.Join("\n                        ", marshalParts);
                }
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

                    // DynamicSelf (Self return type): resolve to the parent class type.
                    // Async wrappers are emitted as free functions where bare "Self" is invalid.
                    // DynamicSelf is only allowed for class parents (validation gate in WrapperValidation),
                    // so we can safely treat it as a class and use the parent type name.
                    if (returnTypeSpec.IsDynamicSelf && _env.ParentDecl is TypeDecl dynamicSelfParent)
                    {
                        swiftReturnType = dynamicSelfParent.SwiftTypeName.ModuleQualifiedName;
                    }

                    // Check if this is a class type (needs retain to prevent ARC deallocation)
                    _env.TypeDatabase.TryGetTypeRecord(returnTypeSpec, out var complexReturnTypeRecord);
                    bool isClassType = complexReturnTypeRecord?.Kind == TypeRecordKind.Class
                                       || returnTypeSpec.IsDynamicSelf;

                    // Optional<ClassType>: unwrap optional, retain if .some, store nil if .none.
                    // Uses the same nullable pointer ABI as sync @_cdecl wrappers (OptionalClassPointer).
                    // Must check AFTER isClassType since isClassType handles non-optional classes.
                    bool isOptionalClassType = !isClassType &&
                        CdeclParamMapper.IsOptionalWithReferenceInner(returnTypeSpec, _env.TypeDatabase);

                    // Optional<Container<ObjCBridgeable>> (Bug #5): bridges through `as AnyObject` to
                    // NSArray/NSDictionary/NSSet exactly the way sync ObjCBridge container returns
                    // already do. Shares the Swift wrapper code with isOptionalClassType — both produce
                    // a +1 retained pointer or 0 for nil. The C# side branches on TryGetOptionalMarshalType
                    // returning containerBridgeConversion. Without this branch the wrapper would emit a
                    // raw copyMemory of Swift's Array<URL> storage pointer, which is Foundation._SwiftURL
                    // — not an NSObject — so ArrayFromHandle crashes the ObjC registrar at runtime.
                    bool isOptionalObjCContainer = !isClassType && !isOptionalClassType &&
                        IsOptionalObjCBridgeContainerReturn(returnTypeSpec);

                    // Determine whether C#'s NewFromPayload takes ownership of the buffer
                    // (non-frozen structs/enums projected as C# classes with SwiftSafeHandle).
                    // When NewFromPayload takes ownership, we must use initializeMemory to properly
                    // retain internal references — SwiftSafeHandle.Destroy will release them.
                    // When NewFromPayload copies, we use copyMemory (the C# constructor uses
                    // InitializeWithCopy which handles retain, and C# calls SBW_Free on the carrier).
                    // Types that take ownership: non-frozen structs and enums that are projected as
                    // C# classes (i.e., they have RequiresMemoryManagement and are not frozen structs
                    // projected as class with Buffer). This excludes frozen value types, frozen-as-class
                    // types, collections (no type record), and Swift class types.
                    bool newFromPayloadTakesOwnership = false;
                    if (!isClassType && complexReturnTypeRecord != null)
                    {
                        bool isFrozen = MarshallingHelpers.IsTypeFrozen(complexReturnTypeRecord);
                        bool requiresMemMgmt = MarshallingHelpers.RequiresMemoryManagement(complexReturnTypeRecord);
                        bool isFrozenAsClass = MarshallingHelpers.IsFrozenStructProjectedAsClass(complexReturnTypeRecord);
                        // Non-frozen enums and non-frozen structs with memory management → takes ownership
                        // Also non-simple enums (which have RequiresMemoryManagement)
                        newFromPayloadTakesOwnership = requiresMemMgmt && !isFrozenAsClass;
                    }

                    // Allocate memory, copy result value, pass pointer through callback.
                    // Note: storeBytes(of:as:) requires BitwiseCopyable (Swift 6+), so we use
                    // alternative patterns that work with all types including classes like UIImage.
                    string structEnumCopyCode;
                    if (newFromPayloadTakesOwnership)
                    {
                        // Non-frozen structs/enums: initializeMemory properly retains internal refs.
                        // C#'s NewFromPayload takes ownership; SwiftSafeHandle.Destroy releases them.
                        structEnumCopyCode =
                              $"                            let _rawPtr = UnsafeMutableRawPointer.allocate(\n" +
                              $"                                byteCount: MemoryLayout<{swiftReturnType}>.size,\n" +
                              $"                                alignment: MemoryLayout<{swiftReturnType}>.alignment)\n" +
                              $"                            _rawPtr.initializeMemory(as: {swiftReturnType}.self, repeating: {resultVar}, count: 1)\n";
                    }
                    else
                    {
                        // Frozen-as-class / collections / unknown: copyMemory is safe because C#'s
                        // NewFromPayload allocates its own buffer with InitializeWithCopy (properly retains).
                        // C# calls SBW_Free to deallocate this carrier buffer (no retain to release).
                        structEnumCopyCode =
                              $"                            let _rawPtr = UnsafeMutableRawPointer.allocate(\n" +
                              $"                                byteCount: MemoryLayout<{swiftReturnType}>.size,\n" +
                              $"                                alignment: MemoryLayout<{swiftReturnType}>.alignment)\n" +
                              $"                            withUnsafePointer(to: {resultVar}) {{ _srcPtr in\n" +
                              $"                                _rawPtr.copyMemory(from: UnsafeRawPointer(_srcPtr), byteCount: MemoryLayout<{swiftReturnType}>.size)\n" +
                              $"                            }}\n";
                    }

                    stringMarshalCode =
                        $"// Marshal complex type to pointer for C# callback\n" +
                        $"                        let _resultPtr: OpaquePointer\n" +
                        $"                        do {{\n" +
                        (isClassType
                            ? // For classes: retain and store the opaque pointer value directly.
                              // Unmanaged.passRetained increments refcount; toOpaque() returns UnsafeMutableRawPointer
                              // which IS BitwiseCopyable, avoiding the storeBytes restriction.
                              // C# will free the 8-byte carrier buffer via SBW_Free.
                              // SwiftClassHandle takes ownership of the +1 retain (no Arc.Release needed in C#).
                              $"                            let _rawPtr = UnsafeMutableRawPointer.allocate(\n" +
                              $"                                byteCount: MemoryLayout<UnsafeMutableRawPointer>.size,\n" +
                              $"                                alignment: MemoryLayout<UnsafeMutableRawPointer>.alignment)\n" +
                              $"                            _rawPtr.storeBytes(of: Unmanaged.passRetained({resultVar} as AnyObject).toOpaque(), as: UnsafeMutableRawPointer.self)\n"
                            : (isOptionalClassType || isOptionalObjCContainer)
                              ? // Optional<ClassType> and Optional<Container<ObjCBridgeable>> share this shape:
                                // unwrap, retain if .some, store zero (nil) if .none. The `as AnyObject` cast
                                // is the bridging hook — for class inners it's a no-op pointer cast, for
                                // ObjC-bridge containers it dispatches through _ObjectiveCBridgeable to produce
                                // a real NSArray/NSDictionary/NSSet with +1 retain (NOT the raw Swift storage
                                // class). C# reads pointer from buffer, checks for IntPtr.Zero (nil), then
                                // either MarshalFromSwift (class inner) or ArrayFromHandle (container inner).
                                $"                            let _rawPtr = UnsafeMutableRawPointer.allocate(\n" +
                                $"                                byteCount: MemoryLayout<UnsafeMutableRawPointer>.size,\n" +
                                $"                                alignment: MemoryLayout<UnsafeMutableRawPointer>.alignment)\n" +
                                $"                            if let _unwrapped = {resultVar} {{\n" +
                                $"                                _rawPtr.storeBytes(of: Unmanaged.passRetained(_unwrapped as AnyObject).toOpaque(), as: UnsafeMutableRawPointer.self)\n" +
                                $"                            }} else {{\n" +
                                $"                                _rawPtr.storeBytes(of: 0, as: Int.self)\n" +
                                $"                            }}\n"
                              : structEnumCopyCode) +
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

            // For typed throws, the error callback takes 5 params: (errorPtr, errorSize, messagePtr, isCancellation, task)
            // For untyped throws, it takes 3 params: (messagePtr, isCancellation, task)
            // isCancellation (Int32): 1 if error is CancellationError, 0 otherwise
            var errorCallbackSwiftParam = useTypedErrorCallback
                ? "errorCallback: @escaping @convention(c) (UnsafeRawPointer, Int, UnsafePointer<CChar>, Int32, Int64) -> Void"
                : "errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int32, Int64) -> Void";

            // Pre-compute the Swift catch block body for typed vs untyped throws.
            string swiftCatchBody = BuildSwiftCatchBody("                        ");
            string swiftCatchBodyExt = BuildSwiftCatchBody("                            ");

            bool usesCdecl = _env.MethodDecl.UsesCdeclMethodWrapper;
            // For @_cdecl: param labels become `_` (unlabeled), remove @escaping from @convention(c)
            var baseParams = usesCdecl
                ? new[]
                {
                    $"_ callback: @convention(c) ({callbackParams}Int64) -> Void",
                    useTypedErrorCallback
                        ? "_ errorCallback: @convention(c) (UnsafeRawPointer, Int, UnsafePointer<CChar>, Int32, Int64) -> Void"
                        : "_ errorCallback: @convention(c) (UnsafePointer<CChar>, Int32, Int64) -> Void",
                    "_ _sbwTask: Int64"
                }
                : new[]
                {
                    $"callback: @escaping @convention(c) ({callbackParams}Int64) -> Void",
                    errorCallbackSwiftParam,
                    "_sbwTask: Int64"
                };

            // Reconstruction code for @_cdecl converted params (emitted before Task {})
            var cdeclReconstructionLines = new List<string>();

            // Baseline async-throwing closure params: routed through the dedicated
            // Swift-side adapter bridge. Collected here so both the param list and
            // method-call arg list can special-case them, and the outer wrapper can
            // inject the adapter construction before the `try await` line.
            var baselineAsyncClosureParams = usesCdecl
                ? _env.MethodDecl.CSSignature.Skip(1)
                    .Where(p => p.SwiftTypeSpec is ClosureTypeSpec cts
                        && _env.ClosureHandler.IsBaselineAsyncThrowingClosure(cts))
                    .ToList()
                : new List<ArgumentDecl>();

            var methodParams = _env.MethodDecl.CSSignature
                .Skip(1)
                .Select(p =>
                {
                    // Check if this is a non-frozen parameter that needs UnsafeRawPointer
                    if (nonFrozenParams.Any(nfp => nfp.Name == p.Name))
                    {
                        return usesCdecl ? $"_ {p.Name}: UnsafeRawPointer" : $"{p.Name}: UnsafeRawPointer";
                    }
                    if (p.IsGeneric)
                    {
                        return $"{p.Name}: {_env.MethodDecl.GenericParameters.Find(g => g.TypeName == p.SwiftTypeSpec.ToString())!.SugaredTypeName}";
                    }
                    // Baseline async-throwing closure param: widen to (contextPtr, startFunc)
                    // pair matching the C# P/Invoke. The handoff struct (Sendable shim) must
                    // be constructed BEFORE Task {} so the task can capture it — raw pointers
                    // are non-Sendable in Swift 6. The adapter closure itself is built inside
                    // Task {} via adapterSetupCode before the try-await call.
                    if (baselineAsyncClosureParams.Any(bp => bp.Name == p.Name))
                    {
                        cdeclReconstructionLines.Add(ClosureEmitter.BuildAsyncClosureHandoffInit(p.Name));
                        return $"_ {p.Name}ContextPtr: UnsafeMutableRawPointer, "
                             + $"_ {p.Name}StartFunc: @convention(c) "
                             + "(UnsafeMutableRawPointer, UnsafeMutableRawPointer, UnsafeMutableRawPointer, UnsafeMutableRawPointer) -> Void";
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
                    // Large Optional params: accept UnsafeRawPointer, dereference before Task {}
                    if (largeOptionalParams.Any(lop => lop.Name == p.Name))
                    {
                        return usesCdecl ? $"_ {p.Name}: UnsafeRawPointer" : $"{p.Name}: UnsafeRawPointer";
                    }
                    // @_cdecl catchall: convert to C-compatible types via GetCdeclParamMapping
                    if (usesCdecl)
                    {
                        var label = !string.IsNullOrEmpty(p.PrivateName) ? p.PrivateName : p.Name;
                        var (cdeclParam, reconstruction, _) =
                            CdeclParamMapper.Map(p, label, _env, omitLabels: true);
                        if (reconstruction != null) cdeclReconstructionLines.Add(reconstruction);
                        return cdeclParam;
                    }
                    return $"{p.Name}: {p.SwiftTypeSpec}";
                });

            // Compute parent type info early — needed by multiple decisions below.
            var parentTypeName = (_env.ParentDecl as TypeDecl)?.SwiftTypeName;
            var isAsyncInstanceMethod = parentTypeName != null && isInstanceMethod && _env.MethodDecl.MethodType != MethodType.Static;
            bool isGenericParentType = _env.ParentDecl is TypeDecl { IsGeneric: true };
            bool useExtensionForGenericAsync = isAsyncInstanceMethod && isGenericParentType && !usesCdecl;

            // For async instance methods, add _self parameter so the wrapper operates on
            // the correct instance (not hardcoded .shared for singleton classes).
            // @_cdecl uses UnsafeMutableRawPointer; @_silgen_name uses OpaquePointer.
            // Extension methods (generic parent types) don't need _self — self is implicit in ABI.
            var needsSelfParam = _env.ParentDecl is TypeDecl && isInstanceMethod
                && _env.MethodDecl.MethodType != MethodType.Static
                && !useExtensionForGenericAsync;
            var selfParam = needsSelfParam
                ? (usesCdecl ? new[] { "_ _self: UnsafeMutableRawPointer" } : new[] { "_self: OpaquePointer" })
                : Array.Empty<string>();

            string parameters = string.Join(", ", baseParams.Concat(methodParams).Concat(selfParam));

            // For extension methods, generic params come from the extension scope — don't redeclare them.
            // Only add generic params for method-own generics (not parent-type generics).
            var genericParams = _env.MethodDecl.IsGeneric switch
            {
                true when !useExtensionForGenericAsync =>
                    $"<{string.Join(", ", _env.MethodDecl.GenericParameters.Select(p => p.SugaredTypeName))}>",
                true when useExtensionForGenericAsync && WrapperValidation.HasMethodOwnGenericParameters(_env.MethodDecl) =>
                    BuildMethodOwnGenericParams(_env.MethodDecl),
                _ => ""
            };

            var whereClause = (!useExtensionForGenericAsync && _env.MethodDecl.IsGeneric)
                ? WrapperEmitterHelpers.BuildSwiftWhereClause(_env.MethodDecl.GenericParameters)
                : "";

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

            // Append large optional param deref lines (read before Task {} for async safety)
            if (largeOptionalParams.Count > 0)
            {
                var optDeref = string.Join("\n        ", largeOptionalParams.Select(p =>
                    OptionalPointerWrapperEmitter.GetDerefCode(p, p.Name, p.Name)));
                readCode = readCode.Length > 0
                    ? readCode + "\n        " + optDeref
                    : optDeref;
            }

            // Append @_cdecl reconstruction lines (read before Task {} for async safety)
            if (cdeclReconstructionLines.Count > 0)
            {
                var cdeclDeref = string.Join("\n        ", cdeclReconstructionLines);
                readCode = readCode.Length > 0
                    ? readCode + "\n        " + cdeclDeref
                    : cdeclDeref;
                hasReadCode = true;
            }

            // Generate argument list for the actual Swift method call
            var methodCallArgs = string.Join(", ", _env.MethodDecl.CSSignature.Skip(1)
                .Select(p =>
                {
                    // In @_cdecl mode, the function parameter uses PrivateName (Swift internal name)
                    // while Name is the Swift external label. Use the correct variable name.
                    var varName = (usesCdecl && !string.IsNullOrEmpty(p.PrivateName)) ? p.PrivateName : p.Name;
                    var argName = p.Name switch
                    {
                        var n when SwiftBuilder.IsAutoGeneratedArgName(n) => varName,
                        var n when n.StartsWith("_") => $"{n.Substring(1)}: {varName}",
                        var n => $"{n}: {varName}"
                    };

                    // For baseline async-throwing closure params, the adapter closure built
                    // inside Task {} substitutes for the original closure arg at the call site.
                    if (baselineAsyncClosureParams.Any(bp => bp.Name == p.Name))
                    {
                        var label = p.Name switch
                        {
                            var n when SwiftBuilder.IsAutoGeneratedArgName(n) => "",
                            var n when n.StartsWith("_") => $"{n.Substring(1)}: ",
                            var n => $"{n}: "
                        };
                        return $"{label}{ClosureEmitter.GetAdaptedClosureVarName(p.Name)}";
                    }
                    // For non-frozen params, use the captured value
                    if (nonFrozenParams.Any(nfp => nfp.Name == p.Name))
                    {
                        var label = p.Name switch
                        {
                            var n when SwiftBuilder.IsAutoGeneratedArgName(n) => "",
                            var n when n.StartsWith("_") => $"{n.Substring(1)}: ",
                            var n => $"{n}: "
                        };
                        return $"{label}{p.Name}Value";
                    }
                    // For large optional params, use the deref'd value
                    if (largeOptionalParams.Any(lop => lop.Name == p.Name))
                    {
                        var label = p.Name switch
                        {
                            var n when SwiftBuilder.IsAutoGeneratedArgName(n) => "",
                            var n when n.StartsWith("_") => $"{n.Substring(1)}: ",
                            var n => $"{n}: "
                        };
                        return $"{label}{p.Name}Val";
                    }
                    // For @_cdecl-converted params, use the reconstructed value
                    if (usesCdecl && cdeclReconstructionLines.Count > 0)
                    {
                        var label_ = !string.IsNullOrEmpty(p.PrivateName) ? p.PrivateName : p.Name;
                        // Reconstruction lines appear in two shapes: `let NameVal = ...` (no type
                        // annotation) and `let NameVal: Type = ...` (existential path). Match both.
                        if (cdeclReconstructionLines.Any(line =>
                                line.Contains($"let {label_}Val ") || line.Contains($"let {label_}Val:")))
                        {
                            var argLabel = p.Name switch
                            {
                                var n when SwiftBuilder.IsAutoGeneratedArgName(n) => "",
                                var n when n.StartsWith("_") => $"{n.Substring(1)}: ",
                                var n => $"{n}: "
                            };
                            return $"{argLabel}{label_}Val";
                        }
                    }
                    return argName;
                }));

            // For async instance methods on Swift classes, C# calls Arc.Retain on self before
            // invoking this wrapper, ensuring Swift ARC keeps self alive through the Task closure.
            // The matching Arc.Release is called in the C# callback after async completion.
            var selfComment = (isInstanceMethod && isSwiftClass)
                ? "// selfInstance is safe - C# called Arc.Retain before invoking this method"
                : "";

            // Determine how to call the method:
            // - Static methods: ClassName.method()
            // - Async instance methods on generic types (extension): self.method()
            // - Async instance methods (free function): __self.method() (convert _self pointer)
            // - Regular instance methods: self.method()
            string selfConversion;
            string methodCallPrefix;
            if (_env.MethodDecl.MethodType == MethodType.Static || isAsyncConstructor)
            {
                selfConversion = "";
                methodCallPrefix = parentTypeName != null ? $"{parentTypeName.ModuleQualifiedName}." : "";
            }
            else if (useExtensionForGenericAsync)
            {
                // Generic parent type: extension provides generic context via implicit self.
                // @_silgen_name ABI passes self as the last implicit parameter.
                selfConversion = "";
                methodCallPrefix = "self.";
            }
            else if (isAsyncInstanceMethod)
            {
                // Non-singleton async instance method: convert _self pointer to type reference
                if (isSwiftClass)
                {
                    // For classes: @_cdecl uses UnsafeMutableRawPointer, @_silgen_name uses OpaquePointer
                    selfConversion = usesCdecl
                        ? $"let __self = Unmanaged<{parentTypeName!.ModuleQualifiedName}>.fromOpaque(_self).takeUnretainedValue()"
                        : $"let __self = unsafeBitCast(_self, to: {parentTypeName!.ModuleQualifiedName}.self)";
                }
                else if (_env.MethodDecl.IsMutating)
                {
                    // Mutating receiver on a value type: bind to a typed mutable pointer (no .pointee)
                    // and call through __self.pointee so the mutation writes back to the original
                    // storage. Dereferencing into a `let` would copy the struct into the Task closure
                    // and discard the mutation, which is fatal for AsyncIteratorProtocol.next() and
                    // any other `mutating async` method (the iterator would never advance).
                    // UnsafeMutablePointer.pointee uses unsafeMutableAddress, not _modify, so the
                    // call expression does not hold an exclusive borrow across the await boundary.
                    selfConversion = usesCdecl
                        ? $"let __self = _self.assumingMemoryBound(to: {parentTypeName!.ModuleQualifiedName}.self)"
                        : $"let __self = UnsafeMutablePointer<{parentTypeName!.ModuleQualifiedName}>(_self)";
                }
                else
                {
                    // For structs: the pointer points TO the struct data, dereference it
                    selfConversion = usesCdecl
                        ? $"let __self = _self.assumingMemoryBound(to: {parentTypeName!.ModuleQualifiedName}.self).pointee"
                        : $"let __self = UnsafePointer<{parentTypeName!.ModuleQualifiedName}>(_self).pointee";
                }
                methodCallPrefix = (!isSwiftClass && _env.MethodDecl.IsMutating) ? "__self.pointee." : "__self.";
            }
            else if (parentTypeName != null)
            {
                selfConversion = "";
                methodCallPrefix = "self.";
            }
            else
            {
                // Free function — no self
                selfConversion = "";
                methodCallPrefix = "";
            }

            // Generate the Swift wrapper — 3 scope variants (free function, extension, top-level free function)
            // collapsed into a single parameterized template.
            // @_cdecl can't be used in extensions, so force free function for @_cdecl.
            // Async instance methods on generic parent types use extension to inherit generic context.
            bool isExtension = (useExtensionForGenericAsync || (!isAsyncInstanceMethod && parentTypeName != null)) && !usesCdecl;
            var staticModifier = isExtension && (_env.MethodDecl.MethodType == MethodType.Static || isAsyncConstructor) ? "static " : "";
            var catchBody = isExtension ? swiftCatchBodyExt : swiftCatchBody;

            // Skip Swift wrapper for methods with method-own generic parameters.
            // Generic types can't be used in @convention(c) callbacks, and raw type
            // parameter references (τ_0_0) leak into generated code as invalid identifiers.
            // The C# async code above (handle, _tcs, callbacks) is still emitted correctly.
            if (WrapperValidation.HasMethodOwnGenericParameters(_env.MethodDecl))
                return;

            // Determine if wrapper needs @MainActor annotation (only for @MainActor, not custom actors)
            bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
                _env.ParentDecl, _env.MethodDecl.IsMainActorIsolated, _env.MethodDecl.IsNonisolated);

            // Build adapter setup code for any baseline async-throwing closure params, and
            // emit the module-level bridge preamble + per-(module,T) continuation box.
            // The preamble/box are deduped by ModuleEmissionContext across the whole module.
            var adapterSetupCode = "";
            if (baselineAsyncClosureParams.Count > 0)
            {
                // baselineAsyncClosureParams is populated only when usesCdecl is true
                // (see the collection above), and isExtension is forced false whenever
                // usesCdecl is true. The extension branch of the indent ternary was
                // therefore unreachable — use the free-function indent directly.
                var adapterIndent = "            ";
                var moduleName = _env.MethodDecl.ModuleDecl!.Name;
                ClosureEmitter.EmitAsyncClosureBridgePreambleIfNeeded(swiftWriter, _emissionContext);
                var adapterParts = new List<string>();
                foreach (var bp in baselineAsyncClosureParams)
                {
                    var closureSpec = (ClosureTypeSpec)bp.SwiftTypeSpec;
                    var swiftReturnType = SwiftTypeNameHelper.GetSwiftTypeName(closureSpec.ReturnType);
                    ClosureEmitter.EmitAsyncClosureBoxIfNeeded(
                        swiftWriter, moduleName, swiftReturnType, _emissionContext);
                    adapterParts.Add(ClosureEmitter.BuildAsyncClosureAdapter(
                        bp.Name, moduleName, swiftReturnType, adapterIndent));
                }
                adapterSetupCode = string.Join("\n", adapterParts) + "\n";
            }

            swiftWriter.WriteLine(BuildSwiftAsyncWrapperCode(
                isExtension: isExtension,
                parentTypeName: parentTypeName,
                staticModifier: staticModifier,
                genericParams: genericParams,
                parameters: parameters,
                whereClause: whereClause,
                hasReadCode: hasReadCode,
                readCode: readCode,
                selfConversion: selfConversion,
                selfComment: selfComment,
                isEmptyTuple: isEmptyTuple,
                methodCallArgs: methodCallArgs,
                methodCallPrefix: methodCallPrefix,
                stringMarshalCode: stringMarshalCode,
                callbackResultArgs: callbackResultArgs,
                catchBody: catchBody,
                needsMainActor: needsMainActor,
                adapterSetupCode: adapterSetupCode));
        }

        /// <summary>
        /// Emits a wrapper for Swift async method.
        /// </summary>
        /// <param name="writer">The IndentedTextWriter instance.</param>
        private void EmitAsyncWrapper(CSharpWriter csWriter)
        {
            if (!_requiresSwiftAsync)
                return;

            // For generic parent types, [UnmanagedCallersOnly] callbacks must be hoisted to
            // the non-generic PInvokeHelper class to avoid CS7042. Redirect callback output
            // to a StringWriter that gets added to PInvokeHelperContext.RawCodeBlocks.
            var callbackWriter = csWriter;
            if (_env.PInvokeHelperContext != null)
            {
                var helperStringWriter = new System.IO.StringWriter();
                callbackWriter = new CSharpWriter(helperStringWriter) { Indent = 0 };
                // Store the helper writer so we can flush it at the end
                _asyncHelperWriter = helperStringWriter;
                _asyncHelperCsWriter = callbackWriter;
            }

            // Emit SBW_CancelTask P/Invoke once per C# type (for CancellationToken support)
            var moduleDecl = _env.MethodDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(_env.MethodDecl.ModuleDecl));
            var moduleLibPath = _env.TypeDatabase.GetLibraryPath(moduleDecl.Name);
            var wrapperLibPath = _env.TypeDatabase.AsyncLibraryName ?? moduleLibPath;
            var cancelSymbolName = CancellationTaskEmitter.GetCancelSymbolName(moduleDecl.Name);
            var typeKey = (_env.ParentDecl as TypeDecl)?.SwiftTypeName.ModuleQualifiedName ?? moduleDecl.Name;
            if (!CancellationTaskEmitter.HasCancelPInvokeForType(typeKey, _emissionContext))
            {
                CancellationTaskEmitter.MarkCancelPInvokeEmittedForType(typeKey, _emissionContext);
                // SBW_CancelTask P/Invoke: hoist to helper for generic types, emit inline otherwise
                var cancelWriter = _env.PInvokeHelperContext != null ? callbackWriter : csWriter;
                cancelWriter.WriteLines($"""
                    [global::System.Runtime.InteropServices.LibraryImport("{wrapperLibPath}", EntryPoint = "{cancelSymbolName}")]
                    {AsyncFieldVisibility} static partial void SBW_CancelTask(long taskId);

                    """);
            }

            var returnType = _env.MethodDecl.CSSignature.First();
            var voidReturn = returnType.SwiftTypeSpec.IsEmptyTuple;
            var isTupleReturn = _env.TupleHandler.IsTuple(returnType.SwiftTypeSpec) &&
                                (_env.TupleHandler.IsSupportedTuple((TupleTypeSpec)returnType.SwiftTypeSpec) ||
                                 _env.TupleHandler.IsSupportedTuple((TupleTypeSpec)returnType.SwiftTypeSpec, _genericContext));

            var callbackFieldName = NameProvider.GetAsyncCallbackFieldName(_env.MethodDecl);
            var callbackMethodName = NameProvider.GetAsyncCallbackMethodName(_env.MethodDecl);
            var errorCallbackFieldName = NameProvider.GetAsyncErrorCallbackFieldName(_env.MethodDecl);
            var errorCallbackMethodName = NameProvider.GetAsyncErrorCallbackMethodName(_env.MethodDecl);

            // For tuple returns, we need to marshal each element individually
            if (isTupleReturn)
            {
                EmitAsyncWrapperForTuple(callbackWriter, returnType, callbackFieldName, callbackMethodName, errorCallbackFieldName, errorCallbackMethodName);
                FlushAsyncHelperWriter();
                return;
            }

            // Detect String return - requires UTF-8 unmarshalling from SBW_Utf8Slice
            bool isStringReturn = !voidReturn && returnType.SwiftTypeSpec.ToString() == "Swift.String";
            if (isStringReturn)
            {
                EmitAsyncWrapperForString(callbackWriter, callbackFieldName, callbackMethodName, errorCallbackFieldName, errorCallbackMethodName);
                FlushAsyncHelperWriter();
                return;
            }

            // Detect Array<String> return - requires flat buffer unmarshalling
            bool isArrayStringReturn = !voidReturn && IsArrayOfString(returnType.SwiftTypeSpec);
            if (isArrayStringReturn)
            {
                EmitAsyncWrapperForArrayString(callbackWriter, callbackFieldName, callbackMethodName, errorCallbackFieldName, errorCallbackMethodName);
                FlushAsyncHelperWriter();
                return;
            }

            // Detect collection returns (Array, Dictionary, Set) — these pass through OpaquePointer
            // on the Swift side (same as complex types) but need MarshalFromSwift with the runtime
            // container type (e.g., SwiftArray<int>), not the public type (IReadOnlyList<int>).
            if (!voidReturn && TryGetCollectionAsyncInfo(returnType.SwiftTypeSpec, out var runtimeType, out var conversionExpr))
            {
                EmitAsyncWrapperForCollection(callbackWriter, callbackFieldName, callbackMethodName,
                    errorCallbackFieldName, errorCallbackMethodName, runtimeType, conversionExpr);
                FlushAsyncHelperWriter();
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
                // Optional<ClassType>: uses nullable pointer ABI — same buffer layout as class
                // (retained pointer or zero for nil) but needs null check on C# side.
                bool isOptionalClassType = !isClassType &&
                    CdeclParamMapper.IsOptionalWithReferenceInner(returnType.SwiftTypeSpec, _env.TypeDatabase);
                // ObjCBridged requires class type — the GetNSObject path reads _retainedObjPtr
                // which is only declared when isClassType is true
                bool isComplexObjCBridged = isClassType && complexTypeRecord != null && MarshallingHelpers.IsObjCBridged(complexTypeRecord);
                // Non-frozen structs/enums with memory management: NewFromPayload takes ownership → no SBW_Free.
                // All other types (frozen, classes, collections): NewFromPayload copies → SBW_Free needed.
                bool cbTakesOwnership = false;
                if (!isClassType && !isOptionalClassType && complexTypeRecord != null)
                {
                    bool requiresMemMgmt = MarshallingHelpers.RequiresMemoryManagement(complexTypeRecord);
                    bool isFrozenAsClass = MarshallingHelpers.IsFrozenStructProjectedAsClass(complexTypeRecord);
                    cbTakesOwnership = requiresMemMgmt && !isFrozenAsClass;
                }
                EmitAsyncWrapperForComplexType(callbackWriter, callbackFieldName, callbackMethodName, errorCallbackFieldName, errorCallbackMethodName, isClassType, isComplexObjCBridged, cbTakesOwnership, isOptionalClassType);
                FlushAsyncHelperWriter();
                return;
            }

            // Non-tuple return handling
            TypeRecord returnTypeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(returnType.SwiftTypeSpec);
            var isObjCBridged = !voidReturn && MarshallingHelpers.IsObjCBridged(returnTypeRecord);

            // Convertible types (SwiftString -> string, SwiftArray -> IReadOnlyList, etc.) are already
            // properly marshalled and don't need InitWithCopy. Using SwiftObjectHelper with their projected
            // types (string, IReadOnlyList<T>) would fail since those types don't implement ISwiftObject.
            var isConvertibleType = MarshallingHelpers.IsConvertibleType(returnType.SwiftTypeSpec);

            // ObjC bridged types and convertible types don't need InitWithCopy
            var requiresInitWithCopy = !voidReturn && !isObjCBridged && !isConvertibleType && (MarshallingHelpers.RequiresMemoryManagement(returnTypeRecord) || returnType.IsGeneric);

            // For ObjC bridged types, the rawResult is the ObjC object pointer directly
            // For Swift types, we need to marshal from Swift memory layout
            // For class types, rawResult is IntPtr (the raw object pointer) — pass directly
            // For non-class types, rawResult is a value type — take its address
            bool isClassReturn = !voidReturn && returnTypeRecord.Kind == TypeRecordKind.Class;
            string marshalResultCode;
            if (isObjCBridged)
            {
                // ObjC types: rawResult is the ObjC object pointer, wrap with appropriate bridge call.
                // Swift passed +1 via passRetained or calling convention. GetNSObject adds +1.
                // DangerousRelease() balances the extra retain.
                var bridgeCall = MarshallingHelpers.FormatObjCBridgeCall(_wrapperSignature.ReturnType, "rawResult");
                if (MarshallingHelpers.IsCoreFoundationType(_wrapperSignature.ReturnType))
                    marshalResultCode = $"var result = {MarshallingHelpers.FormatObjCBridgeCall(_wrapperSignature.ReturnType, "rawResult", ownsReference: true)};";
                else
                    marshalResultCode = $"var result = {bridgeCall};\n                                result?.DangerousRelease();";
            }
            else if (isClassReturn)
            {
                // Class types: rawResult is IntPtr (the raw Swift object pointer), pass directly
                marshalResultCode = $"var result = SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>(rawResult);";
            }
            else
            {
                marshalResultCode = $"var result = SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>(new IntPtr(&rawResult));";
            }

            var text = $$"""
                        {{AsyncFieldVisibility}} static unsafe delegate* unmanaged[Cdecl]<{{(voidReturn ? "" : $"{_pInvokeSignature.ReturnType}, ")}}IntPtr, void> {{callbackFieldName}} = &{{callbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
                        private static unsafe void {{callbackMethodName}}({{(voidReturn ? "" : $"{_pInvokeSignature.ReturnType} rawResult, ")}}IntPtr task)
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
                {{BuildHolderCleanupCode("holder", "                    ")}}
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

                        {{BuildErrorCallbackBlock(errorCallbackFieldName, errorCallbackMethodName, voidReturn ? "" : $"<{_wrapperSignature.ReturnType}>")}}
                """;
            callbackWriter.WriteLine(text);
            FlushAsyncHelperWriter();
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
                        {{AsyncFieldVisibility}} static unsafe delegate* unmanaged[Cdecl]<{{delegateTypeParams}}> {{callbackFieldName}} = &{{callbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
                        private static unsafe void {{callbackMethodName}}({{methodParamList}})
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
                {{BuildHolderCleanupCode("holder", "                    ")}}
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

                        {{BuildErrorCallbackBlock(errorCallbackFieldName, errorCallbackMethodName, $"<{_wrapperSignature.ReturnType}>")}}
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
            var freePInvokeDecl = GetFreePInvokeDeclIfNeeded();

            var text = $$"""
                        {{freePInvokeDecl}}{{AsyncFieldVisibility}} static unsafe delegate* unmanaged[Cdecl]<IntPtr, nint, IntPtr, void> {{callbackFieldName}} = &{{callbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
                        private static unsafe void {{callbackMethodName}}(IntPtr slicePtr, nint sliceLen, IntPtr task)
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
                                    result = global::System.Runtime.InteropServices.Marshal.PtrToStringUTF8(slicePtr, (int)sliceLen)!;
                                }

                                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> holderTcs)
                                {
                                    // Free copy buffer memory for non-frozen params and release retained self
                {{BuildHolderCleanupCode("holder", "                    ")}}
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

                        {{BuildErrorCallbackBlock(errorCallbackFieldName, errorCallbackMethodName, $"<{_wrapperSignature.ReturnType}>")}}
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
            var freePInvokeDecl = GetFreePInvokeDeclIfNeeded();

            // The wrapper return type is IReadOnlyList<string> (matches non-async Array<String> return type with WU2 element conversion)
            var text = $$"""
                        {{freePInvokeDecl}}{{AsyncFieldVisibility}} static unsafe delegate* unmanaged[Cdecl]<IntPtr, nint, IntPtr, void> {{callbackFieldName}} = &{{callbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
                        private static unsafe void {{callbackMethodName}}(IntPtr bufferPtr, nint bufferLen, IntPtr task)
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);
                            System.Exception? deserializationError = null;
                            System.Collections.Generic.List<string>? result = null;

                            try
                            {
                                // Deserialize Array<String> from flat buffer into string instances
                                // Buffer format: [count: Int64][len0: Int64]...[lenN-1: Int64][str0 bytes]...[strN-1 bytes]
                                if (bufferLen <= sizeof(long))
                                {
                                    // Empty array or just count field
                                    result = new System.Collections.Generic.List<string>();
                                }
                                else
                                {
                                    long count = *(long*)bufferPtr;

                                    // Validate count is in valid range for int cast
                                    if (count < 0 || count > int.MaxValue)
                                        throw new InvalidOperationException($"Invalid array count in async callback buffer: {count}");

                                    if (count == 0)
                                    {
                                        result = new System.Collections.Generic.List<string>();
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

                                        // Read strings from buffer (casts are safe after validation)
                                        result = new System.Collections.Generic.List<string>((int)count);
                                        int dataOffset = headerSize;
                                        for (int i = 0; i < count; i++)
                                        {
                                            int strLen = (int)lengthsPtr[i];
                                            string s = strLen == 0
                                                ? string.Empty
                                                : global::System.Runtime.InteropServices.Marshal.PtrToStringUTF8(bufferPtr + dataOffset, strLen)!;
                                            result.Add(s);
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
                {{BuildHolderCleanupCode("holder", "                    ")}}
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

                        {{BuildErrorCallbackBlock(errorCallbackFieldName, errorCallbackMethodName, $"<{_wrapperSignature.ReturnType}>")}}
                """;
            csWriter.WriteLine(text);
        }

        /// <summary>
        /// Emits async wrapper for methods returning complex types (classes, enums, structs).
        /// These types can't be passed directly through @convention(c) callbacks, so Swift
        /// allocates memory, stores the result, and passes an OpaquePointer.
        /// C# receives IntPtr, reads the value, and frees the memory.
        /// </summary>
        private void EmitAsyncWrapperForComplexType(CSharpWriter csWriter, string callbackFieldName, string callbackMethodName, string errorCallbackFieldName, string errorCallbackMethodName, bool isClassType, bool isObjCBridged = false, bool newFromPayloadTakesOwnership = false, bool isOptionalClass = false)
        {
            var freePInvokeDecl = GetFreePInvokeDeclIfNeeded();

            // For class types (including optional class), Swift retained the object before passing through callback.
            // We must read the object pointer from the buffer (resultPtr points to buffer containing the pointer).
            // SwiftClassHandle takes ownership of the +1 retain — no Arc.Release needed here.
            // Optional class: buffer contains retained pointer (non-nil) or zero (nil).
            var readObjPtrCode = (isClassType || isOptionalClass)
                ? "\n                            // Read object pointer from buffer (for class types, buffer contains the object reference)\n                            IntPtr _retainedObjPtr = *(IntPtr*)resultPtr;"
                : "";

            // For ObjC-bridged types, read the object pointer from the buffer and wrap with GetNSObject<T>
            // For class types, use the dereferenced object pointer (NewFromPayload expects raw pointer, not buffer)
            // For optional class types, same dereference but with null check (IntPtr.Zero = Swift nil)
            // For non-class types, marshal from Swift memory layout (resultPtr is the buffer)
            // For optional (nullable) types, use SwiftOptional<T> to read the discriminator byte correctly.
            // The inner type must be the runtime/marshal type (e.g., SwiftString not string, SwiftArray<T>
            // not IReadOnlyList<T>) — resolved via TypeProjectionFactory.
            string marshalResultCode;
            if (isObjCBridged)
            {
                // Swift passed +1 via passRetained. GetNSObject/GetINativeObject adds its own +1 retain
                // (NSObject(handle, false) → DangerousRetain). DangerousRelease() balances passRetained,
                // matching the SwiftHandle constructor pattern for ObjC-rooted classes.
                var bridgeCall = MarshallingHelpers.FormatObjCBridgeCall(_wrapperSignature.ReturnType, "_retainedObjPtr");
                if (MarshallingHelpers.IsCoreFoundationType(_wrapperSignature.ReturnType))
                {
                    // CoreFoundation: change owns=false to owns=true to take ownership of passRetained
                    bridgeCall = MarshallingHelpers.FormatObjCBridgeCall(_wrapperSignature.ReturnType, "_retainedObjPtr", ownsReference: true);
                    marshalResultCode = $"var result = {bridgeCall};";
                }
                else
                {
                    marshalResultCode = $"var result = {bridgeCall};\n                                // Balance passRetained: GetNSObject added its own retain via DangerousRetain\n                                result?.DangerousRelease();";
                }
            }
            else if (isOptionalClass)
            {
                // Optional<ClassType>: buffer contains retained pointer or zero (nil).
                // Check for nil before marshalling — IntPtr.Zero means Swift returned .none.
                marshalResultCode = $"var result = _retainedObjPtr != IntPtr.Zero ? SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>(_retainedObjPtr) : null;";
            }
            else if (isClassType)
                marshalResultCode = $"var result = SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>(_retainedObjPtr);";
            else if (TryGetOptionalMarshalType(out var optionalMarshalType, out var objcBridgeConversion, out var containerBridgeConversion))
            {
                if (containerBridgeConversion != null)
                {
                    // Optional<Array/Set/Dictionary<ObjCBridgeable>>: paired with the Swift-side
                    // `isOptionalObjCContainer` branch in EmitAsync. The Swift wrapper unwraps the
                    // Optional and calls `_unwrapped as AnyObject`, which dispatches through
                    // `_ObjectiveCBridgeable` to produce a real NSArray/NSDictionary/NSSet (NOT the
                    // raw `_ContiguousArrayStorage<T>` / `Foundation._SwiftURL` storage class — those
                    // are NOT toll-free bridged, and feeding their pointers into ArrayFromHandle /
                    // GetNSObject crashes in Class.Lookup). The carrier buffer holds the +1 retained
                    // NS collection pointer (or 0 for nil, via Optional's extra-inhabitant encoding).
                    // We bypass SwiftOptional<SwiftArray<>> here because its .Some would be a
                    // SwiftArray<IntPtr>, which is the wrong logical shape for the TCS<IReadOnlyList<NSUrl>?>.
                    // DO NOT remove the Swift-side half of this fix — both sides are load-bearing.
                    marshalResultCode = $"IntPtr _ptr = *(IntPtr*)resultPtr;\n                                var result = _ptr == IntPtr.Zero ? null : {containerBridgeConversion};";
                }
                else if (objcBridgeConversion != null)
                    marshalResultCode = $"var _rawResult = SwiftMarshal.MarshalFromSwift<{optionalMarshalType}>(resultPtr);\n                                var result = _rawResult.Case == SwiftOptionalCases.Some ? {objcBridgeConversion} : null;";
                else
                    marshalResultCode = $"var result = SwiftMarshal.MarshalFromSwift<{optionalMarshalType}>(resultPtr).ToNullable();";
            }
            else
                marshalResultCode = $"var result = SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>(resultPtr);";

            // Determine whether to free the Swift-allocated buffer:
            // When NewFromPayload takes ownership (non-frozen structs/enums), don't free — the SafeHandle owns it.
            // All other cases (classes, frozen structs, collections): free the carrier buffer.
            bool shouldFreeSbwBuffer = !newFromPayloadTakesOwnership;
            var freeCode = shouldFreeSbwBuffer
                ? "\n                                // Free Swift-allocated memory\n                                SBW_Free(resultPtr);"
                : "";

            var text = $$"""
                        {{freePInvokeDecl}}{{AsyncFieldVisibility}} static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> {{callbackFieldName}} = &{{callbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
                        private static unsafe void {{callbackMethodName}}(IntPtr resultPtr, IntPtr task)
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);{{readObjPtrCode}}
                            try
                            {
                                // Read result from pointer (Swift allocated memory and stored the value)
                                {{marshalResultCode}}

                                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> holderTcs)
                                {
                                    // Free copy buffer memory for non-frozen params and release retained self
                {{BuildHolderCleanupCode("holder", "                    ")}}
                                    holderTcs.TrySetResult(result);
                                }
                                else if (handle.Target is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> directTcs)
                                {
                                    directTcs.TrySetResult(result);
                                }
                            }
                            finally
                            {{{freeCode}}
                                handle.Free();
                            }
                        }

                        {{BuildErrorCallbackBlock(errorCallbackFieldName, errorCallbackMethodName, $"<{_wrapperSignature.ReturnType}>")}}
                """;
            csWriter.WriteLine(text);
        }

        /// <summary>
        /// Tries to detect if the return TypeSpec is a collection type (Array, Dictionary, Set)
        /// and extracts the runtime container type name and conversion expression needed for
        /// async callback marshalling.
        /// </summary>
        /// <returns>True if the type is a collection with extractable async info.</returns>
        private bool TryGetCollectionAsyncInfo(TypeSpec returnTypeSpec,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? runtimeType,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? conversionExpr)
        {
            runtimeType = null;
            conversionExpr = null;

            var ctx = new ProjectionContext
            {
                TypeDatabase = _env.TypeDatabase,
                IsParameter = false,
                IsAsync = false
            };

            var projection = s_projectionFactory.Project(returnTypeSpec, ctx);
            if (projection is ArrayProjection ap)
            {
                runtimeType = ap.ContainerTypeName;
                conversionExpr = ap.GetReturnContainerConversion("_collection")!;
                return true;
            }
            if (projection is DictionaryProjection dp)
            {
                runtimeType = dp.ContainerTypeName;
                conversionExpr = dp.GetReturnContainerConversion("_collection")!;
                return true;
            }
            if (projection is SetProjection sp)
            {
                runtimeType = sp.ContainerTypeName;
                // SetProjection returns null when no element conversion is needed
                // (SwiftSet<T> already implements IReadOnlySet<T>). Use identity.
                conversionExpr = sp.GetReturnContainerConversion("_collection") ?? "_collection";
                return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if the method's return type is Optional and resolves the correct runtime/marshal
        /// type for MarshalFromSwift. Uses TypeProjectionFactory to get the projection-resolved
        /// container type (e.g., SwiftOptional&lt;SwiftString&gt; not SwiftOptional&lt;string&gt;).
        ///
        /// Three result shapes (mutually exclusive, the marshalResultCode caller picks the branch):
        ///   1. <paramref name="containerBridgeConversion"/> set: inner is Array/Set/Dict whose elements
        ///      use ObjC container bridge (e.g., <c>Optional&lt;Array&lt;URL&gt;&gt;</c>). Caller reads the
        ///      buffer as a nullable IntPtr (the Swift wrapper stores a +1 retained NSArray /
        ///      NSDictionary / NSSet pointer via <c>as AnyObject</c>) and applies the bridge conversion.
        ///   2. <paramref name="objcBridgeConversion"/> set: inner is an ObjC-bridgeable scalar
        ///      (e.g., <c>Optional&lt;URLRequest&gt;</c>). Caller reads via SwiftOptional&lt;IntPtr&gt;
        ///      then bridges the Some payload through GetNSObject.
        ///   3. Neither set: ordinary value-type optional. Caller reads via SwiftOptional&lt;T&gt;.ToNullable().
        /// </summary>
        private bool TryGetOptionalMarshalType(
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? marshalType,
            out string? objcBridgeConversion,
            out string? containerBridgeConversion)
        {
            marshalType = null;
            objcBridgeConversion = null;
            containerBridgeConversion = null;
            var returnSpec = _env.MethodDecl.CSSignature.First().SwiftTypeSpec;
            if (returnSpec is not NamedTypeSpec { Name: "Swift.Optional", GenericParameters.Count: 1 } optionalSpec)
                return false;

            // Class and ObjC-bridged optionals use nullable pointer ABI (nil = IntPtr.Zero),
            // handled by the isClassType/isObjCBridged paths above. Only value-type optionals
            // need SwiftOptional<T> with discriminator byte reading.
            var innerSpec = optionalSpec.GenericParameters[0];
            if (_env.TypeDatabase.TryGetTypeRecord(innerSpec, out var innerRecord))
            {
                if (innerRecord.Kind == TypeRecordKind.Class)
                    return false;
                if (MarshallingHelpers.IsObjCBridged(innerRecord))
                    return false;
                // ObjC-bridgeable types (e.g., URLRequest) marshal as SwiftOptional<IntPtr>.
                // ToNullable() returns nint (not nint?) due to unconstrained T? semantics,
                // so we work with SwiftOptional<IntPtr> directly: check .Case, then wrap .Some.
                if (MarshallingHelpers.IsObjCBridgeable(innerRecord) && innerRecord.NativeTypeName != null)
                {
                    objcBridgeConversion = MarshallingHelpers.FormatObjCBridgeCall(
                        innerRecord.NativeTypeName.FullyQualifiedName, "_rawResult.Some", nonNull: true);
                }
            }

            var projection = ProjectReturn(returnSpec);
            if (projection is OptionalProjection op)
            {
                marshalType = op.ContainerTypeName;

                // Optional<Container<ObjCBridgeable>>: the inner container projection bridges to
                // NSArray / NSDictionary / NSSet. The Swift @_cdecl wrapper coerces the unwrapped
                // value via `as AnyObject` (which dispatches through _ObjectiveCBridgeable to
                // produce a real NSArray/NSDictionary/NSSet, NOT the raw Swift storage class —
                // Foundation._SwiftURL is not an NSObject subclass and would crash the ObjC
                // registrar) and stores the resulting +1 retained pointer in the carrier buffer.
                // The C# side reads the IntPtr and hands it to the container projection's
                // GetReturnContainerConversion which expects an IntPtr-typed variable name.
                if (op.InnerProjection.UsesObjCContainerBridge)
                {
                    containerBridgeConversion = op.InnerProjection.GetReturnContainerConversion("_ptr");
                    // Drop the no-longer-used objcBridgeConversion guard — we're switching strategies.
                    objcBridgeConversion = null;
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Returns true when the method's async return is <c>Optional&lt;Array/Set/Dictionary&lt;ObjCBridgeable&gt;&gt;</c>.
        /// Used by the Swift @_cdecl wrapper emitter to pick the nullable-pointer ABI shape
        /// (bridge to NS collection via <c>as AnyObject</c>) instead of the raw <c>copyMemory</c>
        /// path, which would store a Swift storage class pointer that the C# side cannot use
        /// as an NSArray handle (Foundation._SwiftURL crashes ObjC registrar lookup).
        /// </summary>
        private bool IsOptionalObjCBridgeContainerReturn(TypeSpec returnSpec)
        {
            if (returnSpec is not NamedTypeSpec { Name: "Swift.Optional", GenericParameters.Count: 1 })
                return false;
            return ProjectReturn(returnSpec) is OptionalProjection op
                && op.InnerProjection.UsesObjCContainerBridge;
        }

        /// <summary>
        /// Builds the standard return-projection context for the current async method.
        /// Centralizes the <c>IsParameter=false, IsAsync=false</c> setup so callers don't have
        /// to duplicate it (and so the projection-cache key stays consistent across uses).
        /// </summary>
        private ITypeProjection? ProjectReturn(TypeSpec returnSpec)
        {
            var ctx = new ProjectionContext
            {
                TypeDatabase = _env.TypeDatabase,
                IsParameter = false,
                IsAsync = false
            };
            return s_projectionFactory.Project(returnSpec, ctx);
        }

        /// <summary>
        /// Emits async wrapper for methods returning collection types (Array, Dictionary, Set).
        /// These use the same OpaquePointer pattern as complex types on the Swift side,
        /// but require MarshalFromSwift with the runtime container type (e.g., SwiftArray&lt;int&gt;)
        /// instead of the public type (e.g., IReadOnlyList&lt;int&gt;).
        /// </summary>
        private void EmitAsyncWrapperForCollection(CSharpWriter csWriter,
            string callbackFieldName, string callbackMethodName,
            string errorCallbackFieldName, string errorCallbackMethodName,
            string runtimeType, string conversionExpr)
        {
            var freePInvokeDecl = GetFreePInvokeDeclIfNeeded();

            var text = $$"""
                        {{freePInvokeDecl}}{{AsyncFieldVisibility}} static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> {{callbackFieldName}} = &{{callbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
                        private static unsafe void {{callbackMethodName}}(IntPtr resultPtr, IntPtr task)
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);
                            try
                            {
                                // Marshal collection from Swift-allocated memory using runtime container type
                                var _collection = SwiftMarshal.MarshalFromSwift<{{runtimeType}}>(resultPtr);
                                var result = {{conversionExpr}};

                                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> holderTcs)
                                {
                                    // Free copy buffer memory for non-frozen params and release retained self
                {{BuildHolderCleanupCode("holder", "                    ")}}
                                    holderTcs.TrySetResult(result);
                                }
                                else if (handle.Target is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> directTcs)
                                {
                                    directTcs.TrySetResult(result);
                                }
                            }
                            finally
                            {
                                // Free Swift-allocated memory
                                SBW_Free(resultPtr);
                                handle.Free();
                            }
                        }

                        {{BuildErrorCallbackBlock(errorCallbackFieldName, errorCallbackMethodName, $"<{_wrapperSignature.ReturnType}>")}}
                """;
            csWriter.WriteLine(text);
        }

        /// <summary>
        /// Builds the C# error callback code block (delegate + method) for async wrappers.
        /// For typed throws, emits a 5-param callback (errorPtr, errorSize, messagePtr, isCancellation, task).
        /// For untyped throws, emits a 3-param callback (messagePtr, isCancellation, task).
        /// The isCancellation parameter (Int32) is 1 when the Swift error is CancellationError.
        /// </summary>
        private string BuildErrorCallbackBlock(
            string errorCallbackFieldName,
            string errorCallbackMethodName,
            string tcsType)
        {
            // Common cancellation handling code — used by both typed and untyped paths.
            // When isCancellation is set, find the CancellationToken from the holder and call TrySetCanceled.
            // For typed throws, the Swift-allocated error buffer must also be freed.
            var freeErrorInCancellation = useTypedErrorCallback
                ? "\n                                        SBW_Free(errorPtr);"
                : "";
            var cancellationBlock = $$"""
                                    if (isCancellation != 0)
                                    {
                                        // Swift reported CancellationError — find token and cancel the Task
                                        global::System.Threading.CancellationToken cancelToken = default;
                                        for (int i = 1; i < holder.Length; i++)
                                        {
                                            if (holder[i] is CancellationRegistrationHolder cancelReg)
                                            {
                                                cancelToken = cancelReg.Token;
                                                cancelReg.Registration.Dispose();
                                            }
                                            else if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                                                Arc.Release(retained.Ptr);
                                            else if (holder[i] is DeferredSafeHandleRelease deferred)
                                                deferred.Handle.DangerousRelease();
                                            else if (holder[i] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                                            {
                                                copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                                                NativeMemory.Free((void*)copyBuffer.Buffer);
                                            }
                                        }{{freeErrorInCancellation}}
                                        holderTcs.TrySetCanceled(cancelToken);
                                    }
                """;

            // Build the error creation and TCS dispatch code.
            // Typed throws: unmarshal typed error from Swift memory, free error buffer, create SwiftException<T>.
            // Untyped throws: parse error message string, create SwiftException.
            string holderErrorBody;
            string directErrorBody;

            if (useTypedErrorCallback)
            {
                // For error types that transfer ownership (complex enums, non-frozen structs,
                // frozen-with-memory structs, classes): MarshalFromSwift creates a SafeHandle
                // wrapping the buffer. SBW_Free must only run on exception (otherwise double-free
                // when SafeHandle finalizes). Same pattern as sync typed-throws path.
                var asyncErrorFreeBlock = typedErrorTransfersOwnershipAsync
                    ? "catch { SBW_Free(errorPtr); throw; }"
                    : "finally { SBW_Free(errorPtr); }";
                holderErrorBody = $$"""
                                        var errorMessage = Marshal.PtrToStringUTF8(errorMessagePtr) ?? "Unknown Swift error";
                                        {{typedThrowsCSharpErrorType}} typedError;
                                        try
                                        {
                                            typedError = ({{typedThrowsCSharpErrorType}})SwiftMarshal.MarshalFromSwift<{{typedThrowsCSharpErrorType}}>(errorPtr);
                                        }
                                        {{asyncErrorFreeBlock}}
                                        var exception = new SwiftException<{{typedThrowsCSharpErrorType}}>(typedError, errorMessage);
                                        // Free copy buffer memory for non-frozen params and release retained self
                {{BuildHolderCleanupCode("holder", "                        ", cancelRegVarName: "cancelReg2")}}
                                        holderTcs.TrySetException(exception);
                """;
                directErrorBody = $$"""
                                    var errorMessage = Marshal.PtrToStringUTF8(errorMessagePtr) ?? "Unknown Swift error";
                                    {{typedThrowsCSharpErrorType}} typedError;
                                    try
                                    {
                                        typedError = ({{typedThrowsCSharpErrorType}})SwiftMarshal.MarshalFromSwift<{{typedThrowsCSharpErrorType}}>(errorPtr);
                                    }
                                    {{asyncErrorFreeBlock}}
                                    directTcs.TrySetException(new SwiftException<{{typedThrowsCSharpErrorType}}>(typedError, errorMessage));
                """;
            }
            else
            {
                holderErrorBody = $$"""
                                        var errorMessage = Marshal.PtrToStringUTF8(errorMessagePtr) ?? "Unknown Swift error";
                                        var exception = new SwiftException(errorMessage);
                                        // Free copy buffer memory for non-frozen params and release retained self
                {{BuildHolderCleanupCode("holder", "                        ", cancelRegVarName: "cancelReg2")}}
                                        holderTcs.TrySetException(exception);
                """;
                directErrorBody = $$"""
                                    var errorMessage = Marshal.PtrToStringUTF8(errorMessagePtr) ?? "Unknown Swift error";
                                    directTcs.TrySetException(new SwiftException(errorMessage));
                """;
            }

            // Build delegate and method signatures based on typed vs untyped throws
            var freePInvokeDecl = useTypedErrorCallback ? GetFreePInvokeDeclIfNeeded() : "";
            var delegateParams = useTypedErrorCallback
                ? "IntPtr, nint, IntPtr, int, IntPtr, void"
                : "IntPtr, int, IntPtr, void";
            var methodParams = useTypedErrorCallback
                ? "IntPtr errorPtr, nint errorSize, IntPtr errorMessagePtr, int isCancellation, IntPtr task"
                : "IntPtr errorMessagePtr, int isCancellation, IntPtr task";

            return $$"""
                        {{freePInvokeDecl}}{{AsyncFieldVisibility}} static unsafe delegate* unmanaged[Cdecl]<{{delegateParams}}> {{errorCallbackFieldName}} = &{{errorCallbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
                        private static unsafe void {{errorCallbackMethodName}}({{methodParams}})
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);
                            try
                            {
                                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource{{tcsType}} holderTcs)
                                {
                {{cancellationBlock}}
                                    else
                                    {
                {{holderErrorBody}}
                                    }
                                }
                                else if (handle.Target is TaskCompletionSource{{tcsType}} directTcs)
                                {
                {{directErrorBody}}
                                }
                            }
                            finally
                            {
                                handle.Free();
                            }
                        }
                """;
        }

        /// <summary>
        /// Gets the SBW_Free P/Invoke declaration string if not already emitted for the current type.
        /// Handles deduplication — types with multiple async string/complex methods only emit once.
        /// </summary>
        private string GetFreePInvokeDeclIfNeeded()
        {
            var moduleDecl = _env.MethodDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(_env.MethodDecl.ModuleDecl));
            var moduleLibPath = _env.TypeDatabase.GetLibraryPath(moduleDecl.Name);
            var wrapperLibPath = _env.TypeDatabase.AsyncLibraryName ?? moduleLibPath;
            var freeSymbolName = Utf8SliceEmitter.GetFreeSymbolName(moduleDecl.Name);
            var typeKey = (_env.ParentDecl as TypeDecl)?.SwiftTypeName.ModuleQualifiedName ?? moduleDecl.Name;
            var needsFreePInvoke = !Utf8SliceEmitter.HasFreePInvokeForType(typeKey, _emissionContext);
            if (needsFreePInvoke)
            {
                Utf8SliceEmitter.MarkFreePInvokeEmittedForType(typeKey, _emissionContext);
            }
            return needsFreePInvoke
                ? $"""
                        [global::System.Runtime.InteropServices.LibraryImport("{wrapperLibPath}", EntryPoint = "{freeSymbolName}")]
                        private static partial void SBW_Free(IntPtr ptr);

                """
                : "";
        }

        /// <summary>
        /// Builds the Swift catch block body for typed vs untyped throws, parameterized by indent.
        /// </summary>
        private string BuildSwiftCatchBody(string indent)
        {
            if (useTypedErrorCallback)
            {
                // Cancellation must be handled before the force-cast to the typed error.
                // CancellationError is not the typed error type, so `error as! T` would trap.
                // For cancellation: allocate a zeroed buffer (C# only reads _isCancelled flag).
                // For typed errors: cast and copy the error value into the buffer.
                return
                    $"let _isCancelled: Int32 = (error is CancellationError) ? 1 : 0\n" +
                    $"{indent}let _errSize = MemoryLayout<{typedThrowsSwiftErrorType}>.size\n" +
                    $"{indent}let _errPtr = UnsafeMutableRawPointer.allocate(\n" +
                    $"{indent}    byteCount: _errSize, alignment: MemoryLayout<{typedThrowsSwiftErrorType}>.alignment)\n" +
                    $"{indent}if _isCancelled == 0 {{\n" +
                    $"{indent}    _errPtr.initializeMemory(as: {typedThrowsSwiftErrorType}.self, repeating: error as! {typedThrowsSwiftErrorType}, count: 1)\n" +
                    $"{indent}}}\n" +
                    $"{indent}let errorMessage = String(describing: error)\n" +
                    $"{indent}errorMessage.withCString {{ _msgPtr in\n" +
                    $"{indent}    errorCallback(UnsafeRawPointer(_errPtr), Int(Int64(_errSize)), _msgPtr, _isCancelled, _sbwTask)\n" +
                    $"{indent}}}";
            }
            else
            {
                return
                    $"let _isCancelled: Int32 = (error is CancellationError) ? 1 : 0\n" +
                    $"{indent}let errorMessage = String(describing: error)\n" +
                    $"{indent}errorMessage.withCString {{ errorCallback($0, _isCancelled, _sbwTask) }}";
            }
        }

        /// <summary>
        /// Builds the Swift async wrapper code for all 3 scope variants (free function, extension, top-level).
        /// Parameterized by isExtension and hasReadCode to collapse 6 templates into 1.
        /// </summary>
        private string BuildSwiftAsyncWrapperCode(
            bool isExtension,
            SwiftTypeName? parentTypeName,
            string staticModifier,
            string genericParams,
            string parameters,
            string whereClause,
            bool hasReadCode,
            string readCode,
            string selfConversion,
            string selfComment,
            bool isEmptyTuple,
            string methodCallArgs,
            string methodCallPrefix,
            string stringMarshalCode,
            string callbackResultArgs,
            string catchBody,
            bool needsMainActor = false,
            string adapterSetupCode = "")
        {
            var mangledName = NameProvider.GetMangledName(_env.MethodDecl);
            var pInvokeName = NameProvider.GetPInvokeName(_env.MethodDecl);
            var resultAssign = isEmptyTuple ? "" : $"let result{_env.MethodDecl.Name} = ";

            // Extension adds 4-space indent to everything inside the extension { } block
            var i = isExtension ? "    " : "";

            // Build the function body lines
            var readCodeBlock = hasReadCode
                ? $"""
                {i}    // Read non-frozen parameters via .pointee (bitwise copy)
                {i}    // C# created copies using InitializeWithCopy (owns a proper reference)
                {i}    {readCode}
                {i}    {selfConversion}
                {i}    {selfComment}

                """
                : (selfConversion != "" || selfComment != ""
                    ? $"""
                {i}    {selfConversion}
                {i}    {selfComment}
                """
                    : "");

            // Ensure readCodeBlock ends with a newline so _entry starts on its own line
            if (readCodeBlock.Length > 0 && !readCodeBlock.EndsWith("\n"))
                readCodeBlock += "\n";

            var mainActorLine = needsMainActor ? $"{i}@MainActor\n" : "";
            // @MainActor functions: Task { } doesn't inherit actor context, so we need
            // Task { @MainActor in } to access actor-isolated members within the task body.
            var taskOpen = needsMainActor ? "Task { @MainActor in" : "Task {";
            var annotation = _env.MethodDecl.UsesCdeclMethodWrapper ? "@_cdecl" : "@_silgen_name";

            // Async @_cdecl wrappers don't inherit the enclosing type's availability,
            // so emit @available lines explicitly when the method or any ancestor type
            // is gated behind an OS version (e.g., StoreKit Product APIs gated to iOS 15+).
            var availability = WrapperEmitterHelpers.MergeAvailabilityFromAncestors(
                _env.MethodDecl.AvailabilityAnnotations, _env.ParentDecl);
            var availabilityLines = BuildAvailabilityLines(availability, i);

            // Async property getters use property access syntax (no parens), not method call syntax.
            var asyncPropertyName = _env.MethodDecl.AsyncPropertyName;
            var callExpression = asyncPropertyName != null
                ? $"{methodCallPrefix}{asyncPropertyName}"
                : $"{methodCallPrefix}{_env.MethodDecl.Name}(\n{i}                {methodCallArgs}\n{i}            )";

            // Non-throwing async: use plain `await` without do/catch wrapper
            // to avoid "no calls to throwing functions occur within 'try' expression" warning.
            bool throws = _env.MethodDecl.Throws;
            var awaitKeyword = throws ? "try await" : "await";

            string funcBody;
            if (throws)
            {
                funcBody = $$"""
            {{availabilityLines}}{{mainActorLine}}{{i}}{{annotation}}("{{mangledName}}")
            {{i}}public {{staticModifier}}func {{pInvokeName}}{{genericParams}}({{parameters}}){{whereClause}}{
            {{readCodeBlock}}{{i}}    let _entry = _SBWTaskEntry()
            {{i}}    _sbwRegisterTask(_sbwTask, _entry)
            {{i}}    _entry.task = {{taskOpen}}
            {{i}}        defer {
            {{i}}            _sbwUnregisterTask(_sbwTask)
            {{i}}        }
            {{i}}        do {
            {{adapterSetupCode}}{{i}}            {{resultAssign}}{{awaitKeyword}} {{callExpression}}
            {{i}}            {{stringMarshalCode}}
            {{i}}            callback({{callbackResultArgs}}_sbwTask)
            {{i}}        } catch {
            {{i}}            {{catchBody}}
            {{i}}        }
            {{i}}    }
            {{i}}}
            """;
            }
            else
            {
                funcBody = $$"""
            {{availabilityLines}}{{mainActorLine}}{{i}}{{annotation}}("{{mangledName}}")
            {{i}}public {{staticModifier}}func {{pInvokeName}}{{genericParams}}({{parameters}}){{whereClause}}{
            {{readCodeBlock}}{{i}}    let _entry = _SBWTaskEntry()
            {{i}}    _sbwRegisterTask(_sbwTask, _entry)
            {{i}}    _entry.task = {{taskOpen}}
            {{i}}        defer {
            {{i}}            _sbwUnregisterTask(_sbwTask)
            {{i}}        }
            {{i}}        {{resultAssign}}{{awaitKeyword}} {{callExpression}}
            {{i}}        {{stringMarshalCode}}
            {{i}}        callback({{callbackResultArgs}}_sbwTask)
            {{i}}    }
            {{i}}}
            """;
            }

            if (isExtension)
            {
                // Extension declarations on nested types inside OS-gated outer types
                // (e.g. `extension TipKit.Tips.Event`, where `TipKit.Tips` is iOS 17+)
                // must themselves carry availability — Swift treats a bare
                // `extension Foo.Bar { @available(...) func ... }` as using `Foo.Bar`
                // outside its window and emits `'Foo' is only available in …`.
                // Use ONLY ancestor annotations (not the method's own) so a method
                // introduced later than its containing type (e.g. iOS 26 on a Tips.Event)
                // doesn't produce a more-restrictive extension than the inner method claims,
                // which Swift rejects as "instance method cannot be more available than enclosing scope".
                var ancestorAvailability = WrapperEmitterHelpers.MergeAvailabilityFromAncestors(
                    null, _env.ParentDecl);
                var extensionAvailabilityLines = BuildAvailabilityLines(ancestorAvailability, "");
                return $$"""
            {{extensionAvailabilityLines}}extension {{parentTypeName!.ModuleQualifiedName}} {
            {{funcBody}}
            }
            """;
            }
            return funcBody;
        }

        /// <summary>
        /// Builds Swift @available annotation lines for the async wrapper template.
        /// Returns a string with each annotation on its own line (terminated by a newline) so it can
        /// be inlined directly in front of the @MainActor / @_cdecl line in the async wrapper template.
        /// Returns an empty string when there are no annotations.
        /// </summary>
        private static string BuildAvailabilityLines(IReadOnlyList<AvailabilityAnnotation>? annotations, string indent)
        {
            if (annotations == null || annotations.Count == 0)
                return "";

            var sb = new System.Text.StringBuilder();
            var emitted = new HashSet<string>();
            foreach (var annotation in annotations)
            {
                if (annotation.Platform != null && annotation.IntroducedVersion != null)
                {
                    var key = $"{annotation.Platform} {annotation.IntroducedVersion}";
                    if (emitted.Add(key))
                        sb.Append(indent).Append("@available(").Append(key).Append(", *)\n");
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Builds the holder cleanup loop code for freeing async call resources.
        /// Handles RetainedSelfPtr, DeferredSafeHandleRelease, CopyBufferWithType, and CancellationRegistrationHolder.
        /// </summary>
        /// <param name="holderVar">The variable name for the holder array (e.g., "holder" or "_asyncCallHolder").</param>
        /// <param name="indent">The whitespace indent prefix for each line.</param>
        /// <param name="includeCancellationReg">Whether to include CancellationRegistrationHolder cleanup.</param>
        /// <param name="cancelRegVarName">Variable name for the CancellationRegistrationHolder (to avoid shadowing).</param>
        private static string BuildHolderCleanupCode(string holderVar, string indent, bool includeCancellationReg = true, string cancelRegVarName = "cancelReg")
        {
            var cancelRegLine = includeCancellationReg
                ? $"\n{indent}    else if ({holderVar}[i] is CancellationRegistrationHolder {cancelRegVarName})\n{indent}        {cancelRegVarName}.Registration.Dispose();"
                : "";
            return $$"""
                {{indent}}for (int i = 1; i < {{holderVar}}.Length; i++)
                {{indent}}{
                {{indent}}    if ({{holderVar}}[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                {{indent}}        Arc.Release(retained.Ptr);
                {{indent}}    else if ({{holderVar}}[i] is DeferredSafeHandleRelease deferred)
                {{indent}}        deferred.Handle.DangerousRelease();
                {{indent}}    else if ({{holderVar}}[i] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                {{indent}}    {
                {{indent}}        copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                {{indent}}        NativeMemory.Free((void*)copyBuffer.Buffer);
                {{indent}}    }{{cancelRegLine}}
                {{indent}}}
                """;
        }

        /// <summary>
        /// Determines if a TypeSpec represents Swift.Array&lt;Swift.String&gt;.
        /// Used to detect array-of-string returns that need flat buffer serialization in async callbacks.
        /// </summary>
        private bool IsArrayOfString(TypeSpec typeSpec)
        {
            if (typeSpec is not NamedTypeSpec namedType)
                return false;

            // Guard against DynamicSelf or unqualified names
            if (!namedType.Name.Contains('.'))
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
        /// Delegates to the canonical implementation in ClosureEmitter.
        /// </summary>
        private static bool IsSwiftPrimitive(string swiftTypeName)
            => ClosureEmitter.IsSwiftPrimitive(swiftTypeName);
    }
}
