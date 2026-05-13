// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

public partial class ProtocolProxyEmitter
{
    /// <summary>
    /// Emits the C# side of per-closure-param invoke thunks for the protocol's
    /// dispatchable closure-receiving methods. For each (method, closure-param) pair this
    /// emits a <c>[DllImport]</c> P/Invoke into the Swift wrapper's @_cdecl thunk plus a
    /// nested invoker class. The invoker class is what the C# receiver constructs once it
    /// has the (fnPtr, ctx) pair — it holds those values and exposes an <c>Invoke()</c>
    /// method whose method-group can be assigned to a managed delegate (e.g. <c>Action</c>).
    ///
    /// The Swift-side thunk is emitted by
    /// <see cref="EveryProtocolEmitter.EmitProtocolClosureInvokeThunks"/>; both sides agree
    /// on the entry-point name via
    /// <see cref="EveryProtocolEmitter.GetProtocolClosureInvokeThunkEntryPoint"/>.
    /// </summary>
    private void EmitProtocolClosureInvokeThunkHelpers(CSharpWriter writer, ProtocolDecl protocolDecl)
    {
        var libraryPath = _typeDatabase.AsyncLibraryName ?? _typeDatabase.GetLibraryPath(_moduleName);
        var closureHandler = new ClosureHandler(_typeDatabase);

        bool emittedAny = false;
        foreach (var (method, methodIdx) in EveryProtocolEmitter.EnumerateProtocolMethodsForDispatch(protocolDecl))
        {
            if (!EveryProtocolEmitter.IsDispatchableClosureMethod(method, closureHandler))
                continue;

            foreach (var (param, argIdx, closure, _) in EveryProtocolEmitter.EnumerateDispatchableClosureParams(method, closureHandler))
            {
                if (!emittedAny)
                {
                    writer.WriteLine("#region Closure Parameter Invoke Thunks");
                    writer.WriteLine();
                    emittedAny = true;
                }

                var entryPoint = EveryProtocolEmitter.GetProtocolClosureInvokeThunkEntryPoint(protocolDecl, method, methodIdx, argIdx);
                var helperName = EveryProtocolEmitter.GetProtocolClosureInvokeThunkHelperName(entryPoint);
                ClosureEmitter.EmitCSharpInvokeThunkHelper(writer, closure, closureHandler,
                    helperName, entryPoint, libraryPath);
            }
        }

        // Per-property closure invoke thunk helpers. Matches the Swift-side @_cdecl
        // thunk emitted by EveryProtocolEmitter so the C# proxy setter receiver can
        // construct a managed Action that calls back into Swift.
        foreach (var (property, closure, _) in EveryProtocolEmitter.EnumerateDispatchableClosureProperties(protocolDecl, closureHandler))
        {
            if (!emittedAny)
            {
                writer.WriteLine("#region Closure Parameter Invoke Thunks");
                writer.WriteLine();
                emittedAny = true;
            }

            var entryPoint = EveryProtocolEmitter.GetProtocolClosurePropertyInvokeThunkEntryPoint(protocolDecl, property);
            var helperName = EveryProtocolEmitter.GetProtocolClosureInvokeThunkHelperName(entryPoint);
            ClosureEmitter.EmitCSharpInvokeThunkHelper(writer, closure, closureHandler,
                helperName, entryPoint, libraryPath);
        }

        // Per-(method, arg) async closure invoke thunk helpers. Emits a [DllImport]
        // into SBW_..._AsyncInvCR and a nested async invoker class whose InvokeAsync()
        // returns Task<int> via a TaskCompletionSource bridge.
        foreach (var (method, methodIdx) in EveryProtocolEmitter.EnumerateProtocolMethodsForDispatch(protocolDecl))
        {
            if (!EveryProtocolEmitter.IsDispatchableAsyncClosureMethod(method, closureHandler))
                continue;
            foreach (var (param, argIdx, closure) in EveryProtocolEmitter.EnumerateDispatchableAsyncClosureParams(method, closureHandler))
            {
                if (!emittedAny)
                {
                    writer.WriteLine("#region Closure Parameter Invoke Thunks");
                    writer.WriteLine();
                    emittedAny = true;
                }
                var entryPoint = EveryProtocolEmitter.GetProtocolAsyncClosureInvokeThunkEntryPoint(protocolDecl, method, methodIdx, argIdx);
                var helperName = EveryProtocolEmitter.GetProtocolAsyncClosureInvokeThunkHelperName(entryPoint);
                EmitAsyncClosureInvokeThunkHelper(writer, closure, closureHandler, helperName, entryPoint, libraryPath);
            }
        }

        if (emittedAny)
        {
            writer.WriteLine("#endregion");
            writer.WriteLine();
        }
    }

    /// <summary>
    /// Emits the C# infrastructure for invoking an <c>@escaping
    /// () async -&gt; Int32</c> protocol closure parameter:
    /// 1. <c>[DllImport]</c> into the Swift <c>SBW_..._AsyncInvCR</c> @_cdecl thunk, which
    ///    spawns a <c>Task</c> to <c>await</c> the closure and signals completion via a
    ///    function-pointer callback (no by-ref params; pass <c>delegate*</c> directly).
    /// 2. <c>[UnmanagedCallersOnly]</c> static completion callback that resumes a pinned
    ///    <see cref="System.Threading.Tasks.TaskCompletionSource{TResult}"/> with the
    ///    Int32 result.
    /// 3. Nested invoker class with <c>InvokeAsync()</c> returning <c>Task&lt;int&gt;</c> —
    ///    creates the TCS, pins it via <see cref="System.Runtime.InteropServices.GCHandle"/>,
    ///    calls the @_cdecl thunk, and returns <c>tcs.Task</c>. The completion thunk frees
    ///    the GCHandle once the result is published.
    /// </summary>
    private static void EmitAsyncClosureInvokeThunkHelper(
        CSharpWriter writer,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string helperMethodName,
        string entryPointName,
        string libraryName)
    {
        // Gate guarantees no closure args + Swift.Int32 return + non-throwing.
        var delegateType = closureHandler.GetCSharpDelegateType(closureTypeSpec);
        var invokerClassName = ProtocolProxyEmitter.GetAsyncClosureInvokerClassName(helperMethodName);
        var completionThunkName = $"{invokerClassName}_Completion";

        writer.WriteLines($$"""
            [global::System.Runtime.InteropServices.DllImport("{{libraryName}}", EntryPoint = "{{entryPointName}}", CallingConvention = global::System.Runtime.InteropServices.CallingConvention.Cdecl)]
            private static unsafe extern void {{helperMethodName}}(nint funcPtr, nint ctx, nint tcsHandle, delegate* unmanaged[Cdecl]<nint, int, void> completion);
            """);
        writer.WriteLine();

        // Static completion callback: resumes the pinned TCS and frees the GCHandle.
        // Kept at file scope (outside the invoker class) so it has a stable function-pointer
        // address irrespective of generic instantiation. Mono JIT compiles this as a
        // direct cdecl entry point — no display class, no delegate allocation.
        writer.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        writer.WriteLine($"private static void {completionThunkName}(nint tcsHandle, int result)");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine("if (tcsHandle == 0) return;");
        writer.WriteLine("var _gch = global::System.Runtime.InteropServices.GCHandle.FromIntPtr(tcsHandle);");
        writer.WriteLine("if (!_gch.IsAllocated) return;");
        writer.WriteLine("if (_gch.Target is global::System.Threading.Tasks.TaskCompletionSource<int> _tcs)");
        writer.Indent++;
        writer.WriteLine("_tcs.TrySetResult(result);");
        writer.Indent--;
        writer.WriteLine("_gch.Free();");
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();

        // Invoker class: stores (funcPtr, ctx) + the SwiftEscapingClosure wrapper (so its
        // ARC retain on the Swift context survives until the impl drops its reference).
        // InvokeAsync() is the Func<Task<int>> method group handed to the C# impl.
        writer.WriteLine($"private sealed class {invokerClassName}");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine("private readonly nint _funcPtr;");
        writer.WriteLine("private readonly nint _ctx;");
        writer.WriteLine($"private readonly SwiftEscapingClosure<{delegateType}> _wrapper;");
        writer.WriteLine();
        writer.WriteLine($"public {invokerClassName}(nint funcPtr, nint ctx, SwiftEscapingClosure<{delegateType}> wrapper)");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine("_funcPtr = funcPtr;");
        writer.WriteLine("_ctx = ctx;");
        writer.WriteLine("_wrapper = wrapper;");
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
        writer.WriteLine("internal unsafe global::System.Threading.Tasks.Task<int> InvokeAsync()");
        writer.WriteLine("{");
        writer.Indent++;
        // RunContinuationsAsynchronously avoids re-entering the Swift completion callback
        // synchronously from inside SetResult — important on Mono where the cdecl callback
        // may be executing on a Swift dispatch thread.
        writer.WriteLine("var _tcs = new global::System.Threading.Tasks.TaskCompletionSource<int>(global::System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);");
        writer.WriteLine("var _gch = global::System.Runtime.InteropServices.GCHandle.Alloc(_tcs);");
        writer.WriteLine($"{helperMethodName}(_funcPtr, _ctx, (nint)global::System.Runtime.InteropServices.GCHandle.ToIntPtr(_gch), &{completionThunkName});");
        writer.WriteLine("return _tcs.Task;");
        writer.Indent--;
        writer.WriteLine("}");
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }
}
