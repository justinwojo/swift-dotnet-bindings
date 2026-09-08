// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits the wrapper-side helpers that wrap an escaping closure's GCHandle
/// pointer in a Swift-ARC-owned <c>_SBClosureCtx</c> box (defined in the
/// SwiftBindingsRuntime native framework). When Swift releases the closure,
/// the box's deinit upcalls the C# free callback registered by
/// <c>SwiftClosureContext.EnsureRegistered</c> and the GCHandle is freed
/// exactly once.
/// </summary>
/// <remarks>
/// <para>
/// Symbol resolution uses <c>dlsym(RTLD_DEFAULT, ...)</c>. Swift.Runtime's
/// <c>[ModuleInitializer]</c> registers the destroy callback and makes the selected
/// runtime image process-visible before wrapper P/Invokes can initialize this cache.
/// A missing native factory retains the fallback below; registration visibility
/// failures are reported separately by Swift.Runtime.
/// We intentionally do NOT use the deprecated <c>-undefined dynamic_lookup</c>
/// linker flag — the symbol is a real export of the runtime dylib, looked
/// up at runtime once and cached in a <c>fileprivate let</c>.
/// </para>
/// <para>
/// Restricted to escaping closures: the C# free path at
/// <c>WrapperEmitter.Marshalling.cs</c> still frees the GCHandle in
/// <c>finally</c> for non-escaping closures, where Swift cannot retain the
/// closure past the call.
/// </para>
/// </remarks>
public static class ClosureContextHelperEmitter
{
    /// <summary>
    /// Fixed Swift identifier the per-closure adapter code uses to wrap the
    /// captured <c>{paramName}Context!</c> in an <c>AnyObject</c> box.
    /// </summary>
    public const string WrapFunctionName = "_sbWrapClosureContext";

    /// <summary>
    /// Emits the dlsym-cached <c>SwiftBindings_NewClosureContext</c> reference
    /// and the <see cref="WrapFunctionName"/> helper into the wrapper Swift
    /// source. Idempotent per module — subsequent calls are no-ops.
    /// </summary>
    public static bool EmitIfNeeded(SwiftWriter swiftWriter, ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.CreateImplicitFallback();
        if (ctx.ClosureContextHelpersEmitted)
            return false;

        // The closure that initializes _sbNewClosureContextSymbol runs lazily on
        // first access. By that point Swift.Runtime's [ModuleInitializer] has
        // registered the destroy callback and promoted its selected native image
        // to the process symbol namespace. Merely loading an image LOCAL does
        // not make its exports visible here. On Darwin, dlopen(nil, 0) queries
        // the same namespace as RTLD_DEFAULT.
        //
        // When the native runtime is absent, dlsym returns nil. The BindingTests
        // harness injects it after app build, so its ProjectReference opt-out
        // alone does not imply absence. We fall back to a fileprivate Swift
        // class with no destroy hook — the closure-context owner-token degrades
        // gracefully to the prior leak behaviour, matching the C# side's
        // unavailable-native-API fallback in SwiftClosureContext.cs.
        swiftWriter.WriteLines("""
            // MARK: - Escaping-closure GCHandle owner token
            //
            // Wraps each escaping closure's pinned GCHandle pointer in a Swift class
            // owned solely by Swift ARC. When the closure (and thus the box) is
            // released, the box's deinit fires the C# free callback registered by
            // SwiftClosureContext.EnsureRegistered, freeing the GCHandle exactly once.
            // Restricted to escaping closures — non-escaping closures still free in
            // the C# wrapper's `finally`.
            //
            // The factory symbol is exported by the SwiftBindingsRuntime native framework
            // and resolved here via dlsym. We deliberately do NOT use
            // `-undefined dynamic_lookup` — this is a runtime symbol lookup against
            // a real exported symbol of the runtime image made process-visible at
            // managed callback registration. When the native runtime is absent, this falls back
            // to a no-destroy-hook box — same leak behaviour as 0.10.0 and earlier.

            fileprivate final class _SBClosureCtxFallback {
                let ctx: UnsafeMutableRawPointer
                init(_ ctx: UnsafeMutableRawPointer) { self.ctx = ctx }
            }

            fileprivate let _sbNewClosureContextSymbol: UnsafeMutableRawPointer? = {
                let handle = dlopen(nil, 0)
                return dlsym(handle, "SwiftBindings_NewClosureContext")
            }()

            @inline(never)
            fileprivate func _sbWrapClosureContext(_ ctx: UnsafeMutableRawPointer) -> AnyObject {
                if let sym = _sbNewClosureContextSymbol {
                    let factory = unsafeBitCast(sym, to: (@convention(c) (UnsafeMutableRawPointer) -> UnsafeMutableRawPointer).self)
                    return Unmanaged<AnyObject>.fromOpaque(factory(ctx)).takeRetainedValue()
                }
                return _SBClosureCtxFallback(ctx)
            }

            """);
        ctx.ClosureContextHelpersEmitted = true;
        return true;
    }
}
