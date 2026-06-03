// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Single source of truth for the per-module Swift helper that mints a Swift
/// <c>Error</c> from a C# message string, plus its C# P/Invoke declaration.
/// <para>
/// A managed exception must never unwind out of an <c>[UnmanagedCallersOnly]</c>
/// callback into native Swift (that aborts the process). For throwing-closure
/// callbacks the recovery is to convert the caught exception into the Swift error
/// the adapter expects in its <c>errorOut</c> slot. Pure C# cannot construct a
/// Swift <c>Error</c> existential, so we emit a tiny <c>@_cdecl</c> helper —
/// <c>SBW_CreateError_{module}</c> — that wraps the message in an <c>NSError</c>
/// and returns the retained boxed reference as a raw pointer. Because a class-typed
/// <c>Error</c> existential is represented as that single boxed reference, the
/// adapter can <c>unsafeBitCast</c> the pointer straight to <c>Swift.Error</c> and
/// throw it.
/// </para>
/// <para>
/// Both the generic-closure bridge and the standard throwing-closure path emit the
/// same symbol, so emission is funneled here and deduplicated on
/// <see cref="ModuleEmissionContext"/>: the Swift helper once per module, the C#
/// P/Invoke once per type-key.
/// </para>
/// </summary>
public static class SwiftErrorMintEmitter
{
    /// <summary>The Swift/C# symbol that mints a Swift error for a module.</summary>
    public static string SymbolFor(string moduleName) => $"SBW_CreateError_{moduleName}";

    /// <summary>
    /// Emits the Swift <c>@_cdecl("SBW_CreateError_{module}")</c> helper. Idempotent
    /// per module via <see cref="ModuleEmissionContext.SwiftErrorMintHelperEmitted"/>.
    /// </summary>
    public static void EmitSwiftHelperIfNeeded(SwiftWriter swiftWriter, string moduleName, ModuleEmissionContext? ctx)
    {
        // No ctx → no dedup state (and the matching C# P/Invoke contract is not enforced
        // either). Mirrors ClosureContextHelperEmitter.EmitIfNeeded's nullable-ctx contract.
        if (ctx is null || ctx.SwiftErrorMintHelperEmitted) return;

        var symbol = SymbolFor(moduleName);
        swiftWriter.WriteLines($$"""
            // Create a Swift Error from a C string message (closure-callback error propagation).
            @_cdecl("{{symbol}}")
            public func SBW_CreateError(_ message: UnsafePointer<CChar>) -> UnsafeMutableRawPointer {
                let msg = String(cString: message)
                let error = NSError(domain: "SwiftBindings", code: -1, userInfo: [NSLocalizedDescriptionKey: msg])
                return Unmanaged.passRetained(error as AnyObject).toOpaque()
            }

            """);
        // Register the helper so the wrapper-symbol registry reflects every SBW_…
        // symbol we actually emit. Closes a registry hole that would false-trip the
        // contract gate if direct-helper enforcement is widened.
        ctx.TryAddDirectHelperWrapperSymbol(symbol);
        ctx.SwiftErrorMintHelperEmitted = true;
    }

    /// <summary>
    /// Emits the C# <c>[DllImport]</c> for <c>SBW_CreateError_{module}</c>. Idempotent
    /// per type-key via <see cref="ModuleEmissionContext.TryAddSwiftErrorMintPInvoke"/>.
    /// </summary>
    public static void EmitPInvokeIfNeeded(
        CSharpWriter csWriter, string moduleName, string libName,
        MethodEnvironment env, ModuleEmissionContext ctx)
    {
        var typeKey = (env.ParentDecl as TypeDecl)?.SwiftTypeName.ModuleQualifiedName ?? moduleName;
        if (!ctx.TryAddSwiftErrorMintPInvoke(typeKey)) return;

        PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
        {
            LibraryPath = libName,
            EntryPoint = SymbolFor(moduleName),
            MethodName = SymbolFor(moduleName),
            ReturnType = "IntPtr",
            ParametersString = "[MarshalAs(UnmanagedType.LPUTF8Str)] string message",
            CallingConvention = PInvokeCallingConvention.Cdecl,
            Visibility = PInvokeVisibility.Internal,
            EmissionContext = ctx,
            EnforceWrapperContract = true
        });
        csWriter.WriteLine();
    }
}
