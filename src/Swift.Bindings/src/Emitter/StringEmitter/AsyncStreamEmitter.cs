// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Provides methods for emitting AsyncStream-related code, including callback functions
/// and Swift wrappers for async iteration.
/// </summary>
public static class AsyncStreamEmitter
{
    /// <summary>
    /// Emits the [UnmanagedCallersOnly] element callback for an AsyncStream property.
    /// This callback receives elements from Swift and writes them to the SwiftAsyncStream channel.
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <param name="asyncStreamHandler">The AsyncStream handler.</param>
    /// <param name="callbackName">The callback function name.</param>
    public static void EmitElementCallback(
        CSharpWriter csWriter,
        PropertyDecl propertyDecl,
        AsyncStreamHandler asyncStreamHandler,
        string callbackName)
    {
        // Channel storage type — preserves SwiftArray<T> etc. so SwiftMarshal.MarshalFromSwift<T>
        // in SwiftAsyncStream.DeliverElement can deserialize the Swift payload. The public-API
        // IAsyncEnumerable<T> uses the boundary projection type and resolves via
        // IAsyncEnumerable<out T> covariance at the property getter return.
        var elementType = asyncStreamHandler.GetCSharpInternalChannelElementType(propertyDecl.SwiftTypeSpec);

        // [UnmanagedCallersOnly] body guarded by the StreamFault policy: a marshalling failure inside
        // DeliverElement faults the channel (the consumer observes the exception) rather than unwinding
        // across the Swift boundary (undefined behaviour) or silently truncating the stream.
        csWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        csWriter.WriteLine($"private static unsafe byte {callbackName}_OnElement(void* elementPtr, long context)");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine($"var stream = SwiftAsyncStream<{elementType}>.FromContext(context);");
        csWriter.WriteLine("if (stream == null) return 0;");
        UcoGuardEmitter.EmitOpen(csWriter);
        csWriter.WriteLine("return stream.DeliverElement(new IntPtr(elementPtr)) ? (byte)1 : (byte)0;");
        UcoGuardEmitter.EmitClose(csWriter, UcoGuardEmitter.UcoFaultPolicy.StreamFault,
            streamFaultBody: new[] { "stream.FaultChannel(__uco_ex);", "return 0;" });
        csWriter.Indent--;
        csWriter.WriteLine("}");
    }

    /// <summary>
    /// Emits the [UnmanagedCallersOnly] completion callback for an AsyncStream property.
    /// This callback signals when the Swift stream has completed.
    /// </summary>
    /// <remarks>
    /// Mirrors the element-callback shape: resolve the stream from <c>context</c>, then signal
    /// completion. <see cref="SwiftAsyncStream{TElement}.Complete"/> closes the channel writer (a
    /// no-op completion would leave it open forever and hang any C# consumer iterating with
    /// <c>await foreach</c>) and, because completion is the LAST Swift→C# callback for this context,
    /// frees the context handle. The body is StreamFault-guarded for parity with the element callback.
    /// </remarks>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <param name="asyncStreamHandler">The AsyncStream handler.</param>
    /// <param name="callbackName">The callback function name.</param>
    public static void EmitCompletionCallback(
        CSharpWriter csWriter,
        PropertyDecl propertyDecl,
        AsyncStreamHandler asyncStreamHandler,
        string callbackName)
    {
        // Must match the channel-storage type used in EmitElementCallback —
        // FromContext casts the GCHandle target back to SwiftAsyncStream<TElement>,
        // and a mismatch between the OnElement and OnComplete <T> would break the cast.
        var elementType = asyncStreamHandler.GetCSharpInternalChannelElementType(propertyDecl.SwiftTypeSpec);

        csWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        csWriter.WriteLine($"private static void {callbackName}_OnComplete(long context)");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine($"var stream = SwiftAsyncStream<{elementType}>.FromContext(context);");
        csWriter.WriteLine("if (stream == null) return;");
        UcoGuardEmitter.EmitOpen(csWriter);
        csWriter.WriteLine("stream.Complete();");
        UcoGuardEmitter.EmitClose(csWriter, UcoGuardEmitter.UcoFaultPolicy.StreamFault,
            streamFaultBody: new[] { "stream.FaultChannel(__uco_ex);" });
        csWriter.Indent--;
        csWriter.WriteLine("}");
    }

    /// <summary>
    /// Emits the [UnmanagedCallersOnly] producer-error callback for an AsyncThrowingStream property.
    /// </summary>
    /// <remarks>
    /// Invoked by the Swift wrapper's <c>catch</c> arm when the throwing stream terminates via
    /// <c>finish(throwing:)</c> — the producer-threw path. It marshals the Swift error description and
    /// faults the channel so the consumer's <c>await foreach</c> rethrows at the boundary instead of
    /// the stream silently truncating. This is distinct from the StreamFault policy on the element /
    /// completion callbacks (which faults on a <em>managed</em> marshalling failure in those
    /// trampolines): this callback faults on a <em>Swift-side</em> producer error.
    /// <para>
    /// The body is itself StreamFault-guarded: constructing the bridge exception or marshalling the
    /// message must not unwind across the Swift <c>@convention(c)</c> boundary, because the Swift
    /// wrapper invokes <c>completionCallback</c> (which owns the GCHandle free) AFTER this returns — an
    /// escaping managed exception would skip it and leak the context handle. Only emitted for throwing
    /// streams.
    /// </para>
    /// </remarks>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <param name="asyncStreamHandler">The AsyncStream handler.</param>
    /// <param name="callbackName">The callback function name.</param>
    public static void EmitErrorCallback(
        CSharpWriter csWriter,
        PropertyDecl propertyDecl,
        AsyncStreamHandler asyncStreamHandler,
        string callbackName)
    {
        // Must match the channel-storage type used in EmitElementCallback/EmitCompletionCallback —
        // FromContext casts the GCHandle target back to SwiftAsyncStream<TElement>.
        var elementType = asyncStreamHandler.GetCSharpInternalChannelElementType(propertyDecl.SwiftTypeSpec);

        csWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        csWriter.WriteLine($"private static unsafe void {callbackName}_OnError(long context, byte* messagePtr)");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine($"var stream = SwiftAsyncStream<{elementType}>.FromContext(context);");
        csWriter.WriteLine("if (stream == null) return;");
        UcoGuardEmitter.EmitOpen(csWriter);
        csWriter.WriteLine("var __msg = messagePtr == null ? \"Swift async stream producer threw\" : (global::System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)messagePtr) ?? \"Swift async stream producer threw\");");
        csWriter.WriteLine("stream.FaultChannel(new global::Swift.Runtime.SwiftRuntimeException(__msg));");
        UcoGuardEmitter.EmitClose(csWriter, UcoGuardEmitter.UcoFaultPolicy.StreamFault,
            streamFaultBody: new[] { "stream.FaultChannel(__uco_ex);" });
        csWriter.Indent--;
        csWriter.WriteLine("}");
    }

    /// <summary>
    /// Emits the P/Invoke declaration for the Swift wrapper function.
    /// </summary>
    /// <remarks>
    /// All streams carry a <c>cancelKey</c> so a suspended Swift producer can be task-cancelled
    /// (producer-cancel registry). Throwing streams additionally carry an <c>errorCallback</c> that
    /// the Swift wrapper invokes on <c>finish(throwing:)</c> termination (see
    /// <see cref="EmitErrorCallback"/>). The parameter order MUST match the Swift @_cdecl wrapper in
    /// <see cref="EmitSwiftWrapper"/>: self?, element, completion, error?, cancelKey, context.
    /// </remarks>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="swiftWrapperName">The Swift wrapper function name.</param>
    /// <param name="libraryPath">The library path.</param>
    /// <param name="isStatic">Whether the property is static.</param>
    /// <param name="isThrowing">Whether this is an AsyncThrowingStream (emits the producer-error callback param).</param>
    public static void EmitPInvokeDeclaration(
        CSharpWriter csWriter,
        string swiftWrapperName,
        string libraryPath,
        bool isStatic,
        bool isThrowing)
    {
        var ps = new List<string>();
        if (!isStatic)
            ps.Add("void* self");
        ps.Add("delegate* unmanaged[Cdecl]<void*, long, byte> elementCallback");
        ps.Add("delegate* unmanaged[Cdecl]<long, void> completionCallback");
        if (isThrowing)
            ps.Add("delegate* unmanaged[Cdecl]<long, byte*, void> errorCallback");
        ps.Add("long cancelKey");
        ps.Add("long context");
        var paramList = string.Join(", ", ps);

        csWriter.WriteLines($$"""
            [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
            [global::System.Runtime.InteropServices.LibraryImport("{{libraryPath}}", EntryPoint = "{{swiftWrapperName}}")]
            private static unsafe partial void PInvoke_{{swiftWrapperName}}({{paramList}});
            """);
    }

    /// <summary>
    /// Emits the Swift wrapper function that iterates the AsyncStream and calls callbacks.
    /// </summary>
    /// <remarks>
    /// The producer <c>Task</c> is registered with the shared cancellation registry
    /// (<see cref="CancellationTaskEmitter"/>) under <c>cancelKey</c>, so a C# <c>Cancel()</c>/
    /// <c>Dispose()</c> can task-cancel a <em>suspended</em> producer (one awaiting a slow/never-yielding
    /// upstream) rather than leaving it pinned until its next element boundary. The <c>defer</c> inside
    /// the task unregisters on every exit path. For throwing streams the iteration is wrapped in
    /// <c>do/catch</c>: <c>finish(throwing:)</c> surfaces as a caught error that is marshalled back
    /// through <c>errorCallback</c> (see <see cref="EmitErrorCallback"/>); a <c>CancellationError</c>
    /// is swallowed because consumer task-cancel is not a producer fault. <c>completionCallback</c> is
    /// always the final call (it owns the GCHandle free on the C# side).
    /// </remarks>
    /// <param name="swiftWriter">The Swift writer.</param>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <param name="asyncStreamHandler">The AsyncStream handler.</param>
    /// <param name="swiftWrapperName">The Swift wrapper function name.</param>
    /// <param name="parentTypeName">The parent Swift type name.</param>
    /// <param name="isThrowing">Whether this is an AsyncThrowingStream (emits do/catch + error callback).</param>
    public static void EmitSwiftWrapper(
        SwiftWriter swiftWriter,
        PropertyDecl propertyDecl,
        AsyncStreamHandler asyncStreamHandler,
        string swiftWrapperName,
        string parentTypeName,
        bool isThrowing)
    {
        var isStatic = propertyDecl.IsStatic;
        var selfAccess = isStatic ? parentTypeName : "__self";
        // Custom actor AsyncStream properties are supported: `await __self.prop` hops to the
        // actor's serial executor (whether the property is actor-isolated or nonisolated), and
        // the resulting AsyncStream iterates without further isolation because it is Sendable.
        // The gate in PropertyHandler + MemberEmissionValidator still rejects parameterized-protocol
        // element types (iOS 16+ spelling at the @_cdecl level).
        bool isOnCustomActor = (propertyDecl.ParentDecl as ClassDecl)?.IsActor == true;
        // @MainActor properties need Task { @MainActor in } and await to access the
        // actor-isolated member. Unmanaged<T> strips isolation from the reference, so
        // even inside a @MainActor context, the compiler requires explicit `await`.
        bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
            propertyDecl.ParentDecl, propertyDecl.IsMainActorIsolated);
        // Custom actor isolated properties and @MainActor properties need `await` for actor-isolated
        // access. `nonisolated` actor properties are synchronous from any context — adding `await`
        // there produces a "no async operations" Swift warning.
        //
        // Pass 2 of the two-pass isolation policy — see MemberEmissionValidator.GetPropertySkipReason
        // (AsyncStream branch). Pass 1 (there) already decided that a parameterized-protocol actor
        // stream gets skipped regardless of isolation. Here we take the admitted property and decide
        // how its Swift wrapper body hops onto the actor. Keeping the two decisions separate
        // (validator vs. emitter) is deliberate: they serve orthogonal correctness questions. Update
        // both in lockstep if the isolation rules change.
        bool needsActorAwait = (isOnCustomActor && !propertyDecl.IsNonisolated) || needsMainActor;
        var awaitPrefix = needsActorAwait ? "await " : "";
        var taskOpen = needsMainActor
            ? $"{SwiftConcurrencyNames.Task} {{ @MainActor in"
            : $"{SwiftConcurrencyNames.Task} {{";
        var propAccess = NameProvider.EscapeSwiftKeyword(propertyDecl.Name);

        // Emit availability annotations from the member and ancestor chain.
        // @_cdecl wrappers are top-level functions and don't inherit enclosing type availability.
        var availability = WrapperEmitterHelpers.MergeAvailability(propertyDecl.AvailabilityAnnotations, propertyDecl.ParentDecl);
        WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);

        // Built line-by-line (relative indentation baked in) so the producer-cancel registration,
        // the optional do/catch, and the variable parameter list compose without raw-string holes.
        var lines = new List<string>();
        if (needsMainActor)
            lines.Add("@MainActor");
        lines.Add($"@_cdecl(\"{swiftWrapperName}\")");
        lines.Add($"public func {swiftWrapperName}(");
        if (!isStatic)
            lines.Add("    _ self_: UnsafeMutableRawPointer,");
        lines.Add("    _ elementCallback: @convention(c) (UnsafeRawPointer, Int64) -> Bool,");
        lines.Add("    _ completionCallback: @convention(c) (Int64) -> Void,");
        if (isThrowing)
            lines.Add("    _ errorCallback: @convention(c) (Int64, UnsafePointer<CChar>) -> Void,");
        lines.Add("    _ cancelKey: Int64,");
        lines.Add("    _ context: Int64");
        lines.Add(") {");
        if (!isStatic)
            lines.Add($"    let __self = Unmanaged<{parentTypeName}>.fromOpaque(self_).takeUnretainedValue()");
        // Register the producer Task so a C# Cancel()/Dispose() can task-cancel a suspended producer.
        lines.Add("    let _sbwEntry = _SBWTaskEntry()");
        lines.Add("    _sbwRegisterTask(cancelKey, _sbwEntry)");
        lines.Add($"    let _sbwTask = {taskOpen}");
        lines.Add("        defer { _sbwUnregisterTask(cancelKey) }");
        var bodyIndent = isThrowing ? "            " : "        ";
        if (isThrowing)
            lines.Add("        do {");
        var tryKeyword = isThrowing ? "try " : "";
        lines.Add($"{bodyIndent}for {tryKeyword}await element in {awaitPrefix}{selfAccess}.{propAccess} {{");
        lines.Add($"{bodyIndent}    let shouldContinue = withUnsafePointer(to: element) {{ ptr in");
        lines.Add($"{bodyIndent}        elementCallback(UnsafeRawPointer(ptr), context)");
        lines.Add($"{bodyIndent}    }}");
        lines.Add($"{bodyIndent}    if !shouldContinue {{ break }}");
        lines.Add($"{bodyIndent}}}");
        if (isThrowing)
        {
            lines.Add("        } catch is CancellationError {");
            lines.Add("            // Consumer task-cancel (Cancel/Dispose) — not a producer fault; fall through to completion.");
            lines.Add("        } catch {");
            lines.Add("            let _sbwErr = \"\\(error)\"");
            lines.Add("            _sbwErr.withCString { errorCallback(context, $0) }");
            lines.Add("        }");
        }
        lines.Add("        completionCallback(context)");
        lines.Add("    }");
        lines.Add("    if _sbwAssignTask(_sbwEntry, _sbwTask) { _sbwTask.cancel() }");
        lines.Add("}");

        swiftWriter.WriteLines(string.Join("\n", lines));
    }

    /// <summary>
    /// Emits the C# property getter that returns IAsyncEnumerable.
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <param name="asyncStreamHandler">The AsyncStream handler.</param>
    /// <param name="swiftWrapperName">The Swift wrapper function name.</param>
    /// <param name="callbackName">The callback function name prefix.</param>
    /// <param name="isThrowing">Whether this is an AsyncThrowingStream (passes the producer-error callback).</param>
    /// <param name="containingTypeName">Optional containing type name for collision detection (CS0542).</param>
    /// <param name="propertyRenames">Optional property renames for nested-type collision resolution.</param>
    public static void EmitPropertyGetter(
        CSharpWriter csWriter,
        PropertyDecl propertyDecl,
        AsyncStreamHandler asyncStreamHandler,
        string swiftWrapperName,
        string callbackName,
        bool isThrowing,
        string? containingTypeName = null,
        Dictionary<string, string>? propertyRenames = null)
    {
        // Public element type — substitutes Swift collection containers
        // (SwiftArray<T> → IReadOnlyList<T>, etc.) for the consumer-facing
        // IAsyncEnumerable<T> return by substituting Swift collection containers for
        // standard .NET read-only abstractions at the public API.
        var publicElementType = asyncStreamHandler.GetCSharpElementType(propertyDecl.SwiftTypeSpec);
        // Channel storage type — keeps SwiftArray<T> etc. so SwiftAsyncStream<TElement>'s
        // SwiftMarshal.MarshalFromSwift<TElement> in OnElement can deserialize the Swift
        // payload. The `return stream;` resolves to IAsyncEnumerable<publicElement> via
        // IAsyncEnumerable<out T> covariance and the inheritance
        // SwiftArray<T>:IReadOnlyList<T> / SwiftSet<T>:IReadOnlySet<T> /
        // SwiftDictionary<K,V>:IReadOnlyDictionary<K,V>.
        var channelElementType = asyncStreamHandler.GetCSharpInternalChannelElementType(propertyDecl.SwiftTypeSpec);
        var asyncEnumerableType = $"IAsyncEnumerable<{publicElementType}>";
        var isStatic = propertyDecl.IsStatic;
        var staticModifier = isStatic ? "static " : "";
        var baseName = NameProvider.GetPropertyName(propertyDecl.Name, containingTypeName);
        var propertyName = NameProvider.GetFinalMemberName(baseName, propertyRenames);
        var classParent = propertyDecl.ParentDecl as ClassDecl;
        var selfExpr = classParent != null
            ? (classParent.IsObjCRooted ? "Handle" : "_handle.DangerousGetHandle()")
            : "_payload.DangerousGetHandle()";
        var selfArg = isStatic ? "" : $"(void*){selfExpr}, ";
        // Throwing streams pass the producer-error trampoline; all streams pass the cancel key.
        var errorArg = isThrowing ? $"&{callbackName}_OnError, " : "";

        csWriter.WriteLines($$"""
            public {{staticModifier}}{{asyncEnumerableType}} {{propertyName}}
            {
                get
                {
                    unsafe
                    {
                        var stream = new SwiftAsyncStream<{{channelElementType}}>();
                        // Distinct from the GCHandle context cookie: the cancel key routes a C#
                        // Cancel()/Dispose() to SBW_CancelTask so a suspended Swift producer is
                        // task-cancelled (and runs its completion path), not merely channel-completed.
                        long _sbwCancelKey = global::Swift.Runtime.SwiftAsyncCancellation.NextCancelKey();
                        stream.SetProducerCancellation(_sbwCancelKey, static __k => SBW_CancelTask(__k));
                        PInvoke_{{swiftWrapperName}}({{selfArg}}&{{callbackName}}_OnElement, &{{callbackName}}_OnComplete, {{errorArg}}_sbwCancelKey, stream.GetContext());
                        return stream;
                    }
                }
            }
            """);
    }
}
