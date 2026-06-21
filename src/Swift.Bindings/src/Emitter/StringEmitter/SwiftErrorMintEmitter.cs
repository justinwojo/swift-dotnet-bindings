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
            // managedTypeName (optional) carries the originating .NET exception's CLR type name so a
            // Swift caller that receives this error on the reverse path (a C# closure/proxy threw into
            // native Swift) can recover the managed exception's identity beyond the flattened message.
            @_cdecl("{{symbol}}")
            public func SBW_CreateError(_ message: UnsafePointer<CChar>, _ managedTypeName: UnsafePointer<CChar>?) -> UnsafeMutableRawPointer {
                let msg = String(cString: message)
                var userInfo: [String: Any] = [NSLocalizedDescriptionKey: msg]
                if let managedTypeName = managedTypeName {
                    userInfo["SwiftBindingsManagedExceptionType"] = String(cString: managedTypeName)
                }
                let error = NSError(domain: "SwiftBindings", code: -1, userInfo: userInfo)
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
    /// Emits the per-module error-mint helper if <paramref name="env"/>'s method (or
    /// constructor) has any throwing-closure parameter. Call this from the method/constructor
    /// handler dispatch BEFORE the leaf Swift-wrapper emitters and the C# binding's
    /// wrapper-symbol contract check.
    /// <para>
    /// Why at the handler layer rather than inside each Swift-wrapper emitter: the C# side
    /// emits a throwing-closure callback whose catch block mints a Swift error via
    /// <c>SBW_CreateError_{module}</c> for EVERY synchronous throwing-closure parameter
    /// (<see cref="ClosureEmitter.EmitThrowingClosureCallback"/>, driven by
    /// WrapperEmitter.Marshalling -> <see cref="EmitPInvokeIfNeeded"/>), independent of which
    /// Swift-wrapper path renders the parameter. The Swift helper, however, was historically
    /// registered only by paths that funnel through
    /// <see cref="ClosureEmitter.GetSwiftClosureAdapterCode"/>. Paths that forward the closure
    /// to Swift natively — the optional-pointer/_optbuf wrapper, the default-parameter shims,
    /// the non-optional closure property setter — skipped that funnel, so the C# P/Invoke
    /// referenced an unregistered wrapper symbol and the in-band contract gate dropped that
    /// member, stranding its <c>s_&lt;cb&gt; = &amp;&lt;cb&gt;</c> field and call-site → CS0103
    /// (historically the gate left an orphan call that a generate-then-strip post-pass cleaned
    /// up; the gate now predicts the missing symbol and skips the member at emission). Emitting here covers
    /// every wrapper path (current and future) for a given decl kind in one place. Idempotent
    /// per module, so paths that already register through the adapter funnel are unaffected.
    /// </para>
    /// <para>
    /// Timing: the Swift wrapper pass for a decl runs before that same decl's C# binding (the
    /// handler emits Swift wrappers, then the C# binding via WrapperEmitter), so registering
    /// here is always in time for the contract check. Each decl self-satisfies, so the fix is
    /// independent of emission order across decls.
    /// </para>
    /// </summary>
    public static void EmitForMethodIfNeeded(SwiftWriter swiftWriter, MethodEnvironment env, ModuleEmissionContext? ctx)
    {
        if (ctx is null || ctx.SwiftErrorMintHelperEmitted) return;
        if (!MethodHasThrowingClosureParam(env)) return;
        EmitSwiftHelperIfNeeded(swiftWriter, env.MethodDecl.ModuleDecl?.Name ?? "SwiftBindings", ctx);
    }

    /// <summary>
    /// Emits the per-module error-mint helper if <paramref name="propertyDecl"/>'s type is a
    /// throwing closure (optionally wrapped in a single <c>Optional&lt;…&gt;</c> layer). A
    /// settable throwing-closure property's C# setter callback mints a Swift error via
    /// <c>SBW_CreateError_{module}</c> exactly like a method parameter, but the setter's Swift
    /// wrapper (<see cref="PropertyWrapperEmitter"/>'s non-optional closure-setter branch) does
    /// not funnel through the adapter. Same contract/timing rationale as
    /// <see cref="EmitForMethodIfNeeded"/>. Idempotent per module.
    /// </summary>
    public static void EmitForPropertyIfNeeded(SwiftWriter swiftWriter, PropertyDecl propertyDecl, ModuleEmissionContext? ctx)
    {
        if (ctx is null || ctx.SwiftErrorMintHelperEmitted) return;
        if (!IsThrowingClosureType(propertyDecl.SwiftTypeSpec)) return;
        EmitSwiftHelperIfNeeded(swiftWriter, propertyDecl.ModuleDecl?.Name ?? "SwiftBindings", ctx);
    }

    /// <summary>True if any parameter (not the return slot) is a *synchronous* throwing closure.</summary>
    private static bool MethodHasThrowingClosureParam(MethodEnvironment env)
    {
        // Skip(1): CSSignature[0] is the return slot. Only parameters drive the C#
        // throwing-closure callback; a returned throwing closure marshals through
        // EmitThrowingClosureReturnMarshalling, which consumes Swift's errorOut and
        // never mints via SBW_CreateError.
        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            if (!env.ClosureHandler.IsClosure(arg))
                continue;
            var closureTypeSpec = env.ClosureHandler.GetClosureTypeSpec(arg);
            // Only the SYNCHRONOUS throwing-closure callback (ClosureEmitter.EmitThrowingClosureCallback,
            // the `else if (IsThrowingClosure)` branch in WrapperEmitter.Marshalling.EmitClosureCallbacks)
            // mints via SBW_CreateError. An async-throwing closure is handled by the async branch first
            // (continuation-based error propagation, no error mint) — or skipped entirely when non-baseline —
            // so emitting the helper for it registers a symbol the binding never references.
            if (closureTypeSpec != null
                && env.ClosureHandler.IsThrowingClosure(closureTypeSpec)
                && !env.ClosureHandler.IsAsyncClosure(closureTypeSpec))
                return true;
        }
        return false;
    }

    /// <summary>True if <paramref name="typeSpec"/> is a *synchronous* throwing closure, looking through
    /// a single <c>Optional&lt;…&gt;</c> layer (the Optional&lt;closure&gt; setter shape). Async-throwing
    /// closures are excluded: they propagate errors via the continuation, never via SBW_CreateError, and
    /// an Optional async-throwing closure property is skipped from emission entirely.</summary>
    private static bool IsThrowingClosureType(TypeSpec typeSpec)
    {
        if (typeSpec is NamedTypeSpec { Name: "Swift.Optional" } opt && opt.GenericParameters.Count == 1)
            typeSpec = opt.GenericParameters[0];
        return typeSpec is ClosureTypeSpec { Throws: true, IsAsync: false };
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
            ParametersString = "[MarshalAs(UnmanagedType.LPUTF8Str)] string message, [MarshalAs(UnmanagedType.LPUTF8Str)] string? managedTypeName",
            CallingConvention = PInvokeCallingConvention.Cdecl,
            Visibility = PInvokeVisibility.Internal,
            EmissionContext = ctx,
            EnforceWrapperContract = true
        });
        csWriter.WriteLine();
    }
}
