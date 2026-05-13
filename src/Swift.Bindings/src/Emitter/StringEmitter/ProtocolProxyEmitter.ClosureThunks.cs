// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

public partial class ProtocolProxyEmitter
{
    /// <summary>
    /// Session 4a: emits the C# side of per-closure-param invoke thunks for the protocol's
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

        if (emittedAny)
        {
            writer.WriteLine("#endregion");
            writer.WriteLine();
        }
    }
}
