// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using static BindingsGeneration.ExtensionMarshallingHelper;

namespace BindingsGeneration;

/// <summary>
/// Class-receiver trampoline path for <see cref="CrossModuleExtensionEmitter"/>.
///
/// The simple direct-CallConvSwift path in <see cref="CrossModuleExtensionEmitter"/>
/// covers methods whose ABI shape lets the .NET runtime dispatch the original
/// swiftcc symbol from a P/Invoke alone (void / primitive / ObjC class / Swift
/// class returns + primitive / ObjC class / Swift class params). Closure
/// parameters fall outside that shape because swiftcc passes a closure as a
/// (function pointer, captured-context pointer) pair through registers in a
/// layout the .NET runtime cannot synthesize from a P/Invoke signature.
///
/// This path emits a per-method <c>@_cdecl</c> Swift trampoline into the current
/// module's wrapper library (same dylib the in-module <see cref="WrapperEmitter"/>
/// writes to). The trampoline:
///
/// - Receives <c>self</c> as <c>UnsafeRawPointer</c> and resurrects the receiver
///   via <c>Unmanaged&lt;T&gt;.fromOpaque(self_).takeUnretainedValue()</c>
///   (Swift class) or via the equivalent ObjC bridge (ObjC-rooted class).
/// - Accepts each closure parameter as a <c>@convention(c)</c> function pointer
///   plus a context <c>UnsafeRawPointer</c>; the body wraps the raw context in
///   a Swift-ARC owned <c>_SBClosureCtx</c> box via <c>_sbWrapClosureContext</c>
///   (idempotently emitted by <see cref="ClosureContextHelperEmitter"/>) and
///   captures the box in the adapter closure with an explicit <c>[_box]</c>
///   capture list plus an observability statement. When Swift releases the
///   captured closure (synchronously or async-deferred), the box deinit fires
///   the runtime-registered destroy callback exactly once and the C# GCHandle
///   is freed — closing the use-after-free window for escaping completions.
/// - Calls the extension method on <c>__self</c>.
///
/// The C# side allocates a <see cref="System.Runtime.InteropServices.GCHandle"/>
/// per closure, passes its raw pointer as the context, and only frees the
/// handle itself in the failure path before Swift takes ownership (the
/// <c>__{name}Transferred</c> flag). On the success path Swift's box deinit
/// becomes the sole owner of the GCHandle lifetime. A per-method
/// <c>UnmanagedCallersOnly</c> static callback is emitted to bridge the cdecl
/// signature back into the user's strongly typed delegate.
///
/// Initial supported shape (Session 1 minimum):
/// - Instance method on a pure Swift class (<c>self.Payload.DangerousGetHandle()</c>)
///   OR an ObjC-rooted class (<c>self.Handle</c>).
/// - One or more closure parameters, all <c>@escaping (Args...) -&gt; Void</c>
///   where each Args entry is a primitive scalar.
/// - Other params: primitive / ObjC class / Swift class.
/// - Return type: void / primitive / ObjC class / Swift class.
/// - Non-mutating, non-generic, non-async, non-throws.
///
/// Async-throws overloads, static <c>@objc class func</c>, String parameter
/// marshalling, and Optional&lt;class&gt; closure arguments will be layered
/// onto this scaffold in follow-up changes — the trampoline shape is the
/// foundation for all of them.
/// </summary>
public static partial class CrossModuleExtensionEmitter
{
    /// <summary>
    /// Attempts to emit a class-receiver extension method via the trampoline path
    /// when the simple direct-CallConvSwift path rejected it. Returns true when
    /// emitted; false to indicate the method shape is still unsupported.
    /// </summary>
    private static bool TryEmitClosureMethodExtensionTrampoline(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        MethodDecl method,
        ClassDecl classDecl,
        string origCSharpType,
        string origSwiftTypeQualified,
        string wrapperLibPath,
        string currentModule,
        ITypeDatabase typeDatabase,
        HashSet<string> emittedSignatures,
        List<ClassTrampolinePInvokeInfo> pinvokeDecls,
        TypeHandlerContext? context,
        ILogger logger)
    {
        // Hard gates — out of scope for this trampoline path.
        if (method.IsGeneric || method.IsAsync || method.Throws || method.IsMutating || method.IsAccessor)
            return false;

        bool isStatic = method.MethodType == MethodType.Static;

        // Return shape — restrict to the shapes the simple path already handles
        // so the trampoline body can call the method and forward the return
        // through the cdecl boundary without sret machinery.
        var returnTypeSpec = method.CSSignature.Count > 0 ? method.CSSignature[0].SwiftTypeSpec : null;
        var returnCategory = ClassifyReturnType(returnTypeSpec, typeDatabase);
        if (returnCategory == null)
            return false;
        if (returnCategory.Value == ReturnKind.FrozenStruct || returnCategory.Value == ReturnKind.NonFrozenStruct)
            return false;

        // Classify every parameter. The trampoline path is the fallback for any
        // shape the simple direct-CallConvSwift path rejected — closure params,
        // String params, static methods, etc. An unsupported shape on any
        // parameter (FrozenStruct, SimpleEnum, ...) still disqualifies.
        var parameters = new List<ClassTrampolineParamInfo>();
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var arg = method.CSSignature[i];
            var name = NameProvider.GetCSharpParameterName(arg);
            var info = TryClassifyClosureTrampolineParam(name, arg.SwiftTypeSpec, typeDatabase);
            if (info == null)
                return false;
            parameters.Add(info);
        }

        // Deduplicate against the simple path's signature key shape so we don't
        // emit a trampoline overload that conflicts with a simple-path emission.
        var methodName = NameProvider.ToPascalCase(method.Name);
        var staticPrefix = isStatic ? "static:" : "instance:";
        var signatureKey = $"{staticPrefix}{methodName}({string.Join(",", parameters.Select(p => p.PublicCSharpType))})";
        if (!emittedSignatures.Add(signatureKey))
            return false;

        // Deterministic, overload-safe symbol naming — hash the mangled name when
        // available (preferred — guarantees overload distinction) and fall back
        // to a structural signature hash for synthetic methods.
        var hashSeed = !string.IsNullOrEmpty(method.MangledName)
            ? method.MangledName
            : $"{currentModule}|{origSwiftTypeQualified}|{method.Name}|{string.Join(",", parameters.Select(p => p.SwiftTypeRendering))}";
        var symbolHash = EmitterUtility.DeterministicHash8(hashSeed);
        var symbolName = $"SBW_{currentModule}_ClsExt_{SafeTypeName(classDecl.Name)}_{method.Name}_{symbolHash}";
        var pinvokeName = $"PInvoke_{methodName}_{symbolHash}";

        var csharpReturnType = returnCategory.Value == ReturnKind.Void || returnTypeSpec == null
            ? "void"
            : ResolveCSharpTypeName(returnTypeSpec, typeDatabase);
        var publicReturnType = MapBoolType(csharpReturnType);

        // Per-closure UnmanagedCallersOnly callback method names. These are
        // emitted as static members of the enclosing extension class and
        // referenced by `&MethodName` at the call site.
        var closureParams = parameters.Where(p => p.Kind == ClassTrampolineParamKind.Closure).ToList();
        var closureCallbackNames = new Dictionary<string, string>(); // paramName -> C# callback name
        foreach (var cp in closureParams)
        {
            closureCallbackNames[cp.Name] = $"__{methodName}_{cp.Name}_Callback_{symbolHash}";
        }

        // ====== Emit public C# extension method ======
        csWriter.WriteLine();
        var publicParams = new List<string>();
        if (!isStatic)
            publicParams.Add($"this {origCSharpType} self");
        foreach (var p in parameters)
        {
            publicParams.Add($"{p.PublicCSharpType} {p.Name}");
        }
        csWriter.WriteLine($"public static unsafe {publicReturnType} {methodName}({string.Join(", ", publicParams)})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // Pin each closure with a GCHandle and pass its raw pointer as the cdecl
        // context. The Swift trampoline wraps the raw pointer in a Swift-ARC
        // owned _SBClosureCtx box whose deinit fires the runtime-registered
        // destroy callback (Swift.Runtime SwiftClosureContext.DestroyClosureContext),
        // which frees the GCHandle exactly once when Swift drops its last reference.
        //
        // Lifetime contract:
        //  - Transferred=false until the P/Invoke returns successfully; if anything
        //    throws before that point, the alloc-but-no-call leak window is closed
        //    by freeing the GCHandle in the C# `finally`.
        //  - Transferred=true after the P/Invoke returns; from that moment Swift
        //    owns the GCHandle via the box and C# MUST NOT free it (the box deinit
        //    is the sole authoritative release).
        foreach (var cp in closureParams)
        {
            csWriter.WriteLine($"var __{cp.Name}Handle = global::System.Runtime.InteropServices.GCHandle.Alloc({cp.Name});");
            csWriter.WriteLine($"bool __{cp.Name}Transferred = false;");
        }

        csWriter.WriteLine("try");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // String params marshal across the cdecl boundary as (UTF-8 byte pointer,
        // length). Encode each Swift.String param up front (outside the `fixed`
        // block so the byte[] is rooted) and stage the pointer var names that
        // GetClosureTrampolineNativeArgExpr will reference.
        var stringParams = parameters.Where(p => p.Kind == ClassTrampolineParamKind.String).ToList();
        foreach (var sp in stringParams)
        {
            csWriter.WriteLine($"var __{sp.Name}Bytes = global::System.Text.Encoding.UTF8.GetBytes({sp.Name} ?? string.Empty);");
        }

        // Open one combined `fixed` over every String byte[] so all pointers are
        // pinned for the duration of the native call. Empty byte arrays pin to
        // a non-null address with length 0 (CLR contract), so the Swift side can
        // unconditionally treat the pointer as non-nil when length > 0.
        if (stringParams.Count > 0)
        {
            var fixedDecls = string.Join(", ",
                stringParams.Select(sp => $"__{sp.Name}Ptr = __{sp.Name}Bytes"));
            csWriter.WriteLine($"fixed (byte* {fixedDecls})");
            csWriter.WriteLine("{");
            csWriter.Indent++;
        }

        // Build native call argument list in the order the Swift trampoline expects.
        var nativeArgs = new List<string>();
        foreach (var p in parameters)
        {
            if (p.Kind == ClassTrampolineParamKind.Closure)
            {
                nativeArgs.Add($"&{closureCallbackNames[p.Name]}");
                nativeArgs.Add($"global::System.Runtime.InteropServices.GCHandle.ToIntPtr(__{p.Name}Handle)");
            }
            else
            {
                nativeArgs.Add(GetClosureTrampolineNativeArgExpr(p));
            }
        }
        if (!isStatic)
        {
            var selfExpr = classDecl.IsObjCRooted
                ? "self.Handle"
                : "self.Payload.DangerousGetHandle()";
            nativeArgs.Add(selfExpr);
        }

        var nativeCall = $"NativeMethods.{pinvokeName}({string.Join(", ", nativeArgs)})";

        // Receiver-pin guard: pure Swift class receivers go through DangerousAddRef
        // so a concurrent dispose can't hand Swift a freed pointer mid-call. ObjC-rooted
        // receivers manage their own lifetime via NSObject retain and skip the guard.
        // Static methods have no instance receiver to pin.
        if (isStatic || classDecl.IsObjCRooted)
        {
            EmitClosureCallSiteReturnWithTransfer(csWriter, returnCategory.Value, nativeCall, publicReturnType, closureParams);
        }
        else
        {
            csWriter.WriteLine("bool __payloadPinned = false;");
            csWriter.WriteLine("self.Payload.DangerousAddRef(ref __payloadPinned);");
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            EmitClosureCallSiteReturnWithTransfer(csWriter, returnCategory.Value, nativeCall, publicReturnType, closureParams);
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("finally");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("if (__payloadPinned) self.Payload.DangerousRelease();");
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        if (stringParams.Count > 0)
        {
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine("finally");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        foreach (var cp in closureParams)
        {
            csWriter.WriteLine($"if (!__{cp.Name}Transferred && __{cp.Name}Handle.IsAllocated) __{cp.Name}Handle.Free();");
        }
        csWriter.Indent--;
        csWriter.WriteLine("}");

        csWriter.Indent--;
        csWriter.WriteLine("}");

        // ====== Emit per-closure UnmanagedCallersOnly callbacks ======
        foreach (var cp in closureParams)
        {
            EmitClosureCallback(csWriter, cp, closureCallbackNames[cp.Name]);
        }

        // ====== Collect P/Invoke declaration for emission in NativeMethods block ======
        var pinvokeParams = new List<string>();
        foreach (var p in parameters)
        {
            if (p.Kind == ClassTrampolineParamKind.Closure)
            {
                pinvokeParams.Add($"delegate* unmanaged[Cdecl]<{p.ClosureCSharpCdeclSig}> {p.Name}Fn");
                pinvokeParams.Add($"IntPtr {p.Name}Ctx");
            }
            else
            {
                pinvokeParams.Add(BuildClosureTrampolinePInvokeParam(p, typeDatabase));
            }
        }
        if (!isStatic)
            pinvokeParams.Add("IntPtr __self");

        pinvokeDecls.Add(new ClassTrampolinePInvokeInfo(
            EntryPoint: symbolName,
            MethodName: pinvokeName,
            ReturnType: GetClosureTrampolinePInvokeReturnType(returnTypeSpec, returnCategory.Value, typeDatabase),
            Parameters: pinvokeParams));

        // ====== Emit the Swift @_cdecl trampoline ======
        // Ensure the per-module _sbWrapClosureContext helper is present (idempotent).
        ClosureContextHelperEmitter.EmitIfNeeded(swiftWriter, context?.GetEmissionContext());

        EmitSwiftClosureTrampoline(
            swiftWriter, method, classDecl, origSwiftTypeQualified,
            symbolName, parameters, returnTypeSpec, returnCategory.Value, typeDatabase, isStatic);

        logger.LogDebug(
            "Emitted closure-bearing cross-module class extension trampoline {Symbol} for {Type}.{Method}",
            symbolName, classDecl.Name, method.Name);
        return true;
    }

    private static void EmitClosureCallSiteReturnWithTransfer(
        CSharpWriter csWriter,
        ReturnKind category,
        string nativeCall,
        string csharpReturnType,
        List<ClassTrampolineParamInfo> closureParams)
    {
        // The Transferred flag must be set immediately after a successful
        // P/Invoke return so the outer `finally` knows Swift has accepted
        // ownership of the GCHandle via the _SBClosureCtx box and must
        // not double-free it. For non-void returns we materialize the
        // result first, mark Transferred, then return the materialized
        // value — keeping the transfer-on-success guarantee even when
        // marshalling the return throws.
        void EmitMarkTransferred()
        {
            foreach (var cp in closureParams)
            {
                csWriter.WriteLine($"__{cp.Name}Transferred = true;");
            }
        }

        switch (category)
        {
            case ReturnKind.Void:
                csWriter.WriteLine($"{nativeCall};");
                EmitMarkTransferred();
                break;
            case ReturnKind.Primitive:
                csWriter.WriteLine($"var __r = {nativeCall};");
                EmitMarkTransferred();
                csWriter.WriteLine($"return __r;");
                break;
            case ReturnKind.ObjCClass:
                csWriter.WriteLine($"var __r = {nativeCall};");
                EmitMarkTransferred();
                // Swift trampoline returns the result via Unmanaged.passRetained(...).toOpaque(),
                // so the C# side owns the +1 retain and must release it through the ObjC bridge.
                csWriter.WriteLine($"return {MarshallingHelpers.FormatObjCBridgeCall(csharpReturnType, "__r", nonNull: true, ownsReference: true)};");
                break;
            case ReturnKind.SwiftClass:
                csWriter.WriteLine($"var __r = {nativeCall};");
                EmitMarkTransferred();
                csWriter.WriteLine($"return ({csharpReturnType})SwiftMarshal.MarshalFromSwift<{csharpReturnType}>(__r);");
                break;
        }
    }

    private static void EmitClosureCallback(
        CSharpWriter csWriter,
        ClassTrampolineParamInfo closure,
        string callbackName)
    {
        // C# callback signature uses cdecl-level types (IntPtr for class refs).
        // The body bridges each arg back to the public delegate's signature
        // (e.g. IntPtr -> STPToken? for Optional<ObjCClass>) and invokes the
        // user-supplied Action<...>.
        var sigParts = new List<string>();
        for (int i = 0; i < closure.ClosureArgInfos.Count; i++)
        {
            sigParts.Add($"{closure.ClosureArgInfos[i].CSharpCdeclType} arg{i}");
        }
        sigParts.Add("IntPtr ctx");

        csWriter.WriteLine();
        csWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        csWriter.WriteLine($"private static void {callbackName}({string.Join(", ", sigParts)})");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine("var __handle = global::System.Runtime.InteropServices.GCHandle.FromIntPtr(ctx);");
        csWriter.WriteLine($"var __del = ({closure.ClosureCSharpDelegateType})__handle.Target!;");
        var invokeArgs = string.Join(", ",
            closure.ClosureArgInfos.Select((info, i) => BuildCSharpCallbackInvokeArg(info, $"arg{i}")));
        csWriter.WriteLine($"__del({invokeArgs});");
        csWriter.Indent--;
        csWriter.WriteLine("}");
    }

    private static void EmitSwiftClosureTrampoline(
        SwiftWriter swiftWriter,
        MethodDecl method,
        ClassDecl classDecl,
        string origSwiftTypeQualified,
        string symbolName,
        List<ClassTrampolineParamInfo> parameters,
        TypeSpec? returnTypeSpec,
        ReturnKind returnCategory,
        ITypeDatabase typeDatabase,
        bool isStatic)
    {
        var swiftParams = new List<string>();
        foreach (var p in parameters)
        {
            if (p.Kind == ClassTrampolineParamKind.Closure)
            {
                swiftParams.Add($"_ {p.Name}Fn: {p.ClosureSwiftCdeclSig}");
                swiftParams.Add($"_ {p.Name}Ctx: UnsafeRawPointer");
            }
            else if (p.Kind == ClassTrampolineParamKind.String)
            {
                // Swift.String marshals as a (UTF-8 byte pointer, length) pair.
                // The C# side pins a byte[] via `fixed` for the duration of the
                // native call; the Swift body re-materializes a Swift.String
                // from the buffer before calling the user method.
                swiftParams.Add($"_ {p.Name}Ptr: UnsafePointer<UInt8>?");
                swiftParams.Add($"_ {p.Name}Len: Int");
            }
            else
            {
                swiftParams.Add($"_ {p.Name}: {RenderClosureTrampolineSwiftParamType(p)}");
            }
        }
        if (!isStatic)
            swiftParams.Add("_ self_: UnsafeRawPointer");

        string swiftReturn = returnCategory switch
        {
            ReturnKind.Void => "",
            ReturnKind.Primitive => " -> " + ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec!),
            ReturnKind.ObjCClass or ReturnKind.SwiftClass => " -> UnsafeMutableRawPointer",
            _ => "",
        };

        swiftWriter.WriteLine();
        swiftWriter.WriteLine($"// Cross-module class-extension @_cdecl trampoline for {origSwiftTypeQualified}.{method.Name}");
        swiftWriter.WriteLine($"@_cdecl(\"{symbolName}\")");
        swiftWriter.WriteLine($"public func _sbw_clsext_{symbolName}({string.Join(", ", swiftParams)}){swiftReturn} {{");
        swiftWriter.Indent++;

        // Reconstruct receiver. ObjC-rooted classes arrive as an Objective-C
        // pointer registered in the ObjC runtime; bridging back goes through
        // the AnyObject Unmanaged path with a downcast to the Swift class.
        // Pure Swift classes round-trip through Unmanaged<T>. Static methods
        // have no receiver — the call dispatches against the type metatype
        // directly.
        if (!isStatic)
        {
            if (classDecl.IsObjCRooted)
            {
                swiftWriter.WriteLine($"let __self = (Unmanaged<AnyObject>.fromOpaque(self_).takeUnretainedValue() as! {origSwiftTypeQualified})");
            }
            else
            {
                swiftWriter.WriteLine($"let __self = Unmanaged<{origSwiftTypeQualified}>.fromOpaque(self_).takeUnretainedValue()");
            }
        }

        // Build local Swift closures that forward into the cdecl function-pointer
        // pair. The raw context pointer is wrapped in a Swift-ARC owned box
        // (_SBClosureCtx) so its lifetime tracks the closure: when Swift drops
        // the last reference (synchronously OR after an async @escaping
        // capture), the box deinit fires the runtime-registered destroy
        // callback exactly once and the C# GCHandle is freed. The closure
        // captures the box explicitly with [_box_{name}] and pins it inside
        // the body via `_ = _box_{name}` so Swift's optimizer cannot elide
        // the capture and shorten the lifetime.
        //
        // Per-arg conversion (Swift Optional<class> → optional pointer, etc.)
        // is delegated to BuildSwiftClosureCdeclArg so the rules stay in sync
        // with the C# callback side's bridging logic.
        foreach (var cp in parameters.Where(p => p.Kind == ClassTrampolineParamKind.Closure))
        {
            var sigArgs = string.Join(", ",
                cp.ClosureArgInfos.Select((info, i) => $"arg{i}: {info.SwiftType}"));
            var boxName = $"_box_{cp.Name}";
            swiftWriter.WriteLine($"let {boxName}: AnyObject = {ClosureContextHelperEmitter.WrapFunctionName}(UnsafeMutableRawPointer(mutating: {cp.Name}Ctx))");
            swiftWriter.WriteLine($"let {cp.Name}: {cp.ClosureSwiftSig} = {{ [{boxName}] ({sigArgs}) in");
            swiftWriter.Indent++;
            swiftWriter.WriteLine($"_ = {boxName}");
            var convertedArgs = cp.ClosureArgInfos
                .Select((info, i) => BuildSwiftClosureCdeclArg(info, $"arg{i}"))
                .Concat(new[] { $"{cp.Name}Ctx" });
            swiftWriter.WriteLine($"{cp.Name}Fn({string.Join(", ", convertedArgs)})");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
        }

        // Call args with external labels matching the original method signature.
        var callArgsForMethod = new List<string>();
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var argDecl = method.CSSignature[i];
            var p = parameters[i - 1];
            var label = argDecl.Name;
            var valueExpr = p.Kind == ClassTrampolineParamKind.Closure
                ? p.Name
                : ConvertClosureTrampolineCdeclArg(p);
            // Underscore-labeled Swift params (`func foo(_ x: Int)`) are synthesized
            // by the parser to `argN`. The Swift call site must omit the label.
            bool unlabeled = string.IsNullOrEmpty(label) || label == "_" || SwiftBuilder.IsAutoGeneratedArgName(label);
            callArgsForMethod.Add(unlabeled ? valueExpr : $"{label}: {valueExpr}");
        }

        // Static class func dispatches on the type metatype; instance methods
        // dispatch on the resurrected __self receiver.
        var dispatchTarget = isStatic ? origSwiftTypeQualified : "__self";
        var callExpr = $"{dispatchTarget}.{NameProvider.EscapeSwiftKeyword(method.Name)}({string.Join(", ", callArgsForMethod)})";

        switch (returnCategory)
        {
            case ReturnKind.Void:
                swiftWriter.WriteLine(callExpr);
                break;
            case ReturnKind.Primitive:
                swiftWriter.WriteLine($"return {callExpr}");
                break;
            case ReturnKind.ObjCClass:
            case ReturnKind.SwiftClass:
                swiftWriter.WriteLine($"let __r = {callExpr}");
                swiftWriter.WriteLine("return Unmanaged.passRetained(__r).toOpaque()");
                break;
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    // ===== Param classification + helpers =====

    private static ClassTrampolineParamInfo? TryClassifyClosureTrampolineParam(
        string paramName,
        TypeSpec typeSpec,
        ITypeDatabase typeDatabase)
    {
        if (typeSpec is ClosureTypeSpec closure)
        {
            return TryClassifyClosureSpec(paramName, closure, typeDatabase);
        }

        // Swift.String marshals across the cdecl boundary as a (UTF-8 byte
        // pointer, length) pair. ClassifyParameterType returns null for String
        // because the simple direct-CallConvSwift path can't synthesize the
        // two-word _StringObject value layout — that's exactly why this
        // trampoline path picks it up.
        if (typeSpec is NamedTypeSpec named && named.Name == "Swift.String")
        {
            return new ClassTrampolineParamInfo
            {
                Name = paramName,
                Kind = ClassTrampolineParamKind.String,
                CSharpType = "string",
                SwiftTypeRendering = "Swift.String",
                TypeSpec = typeSpec,
            };
        }

        var paramCategory = ClassifyParameterType(typeSpec, typeDatabase);
        if (paramCategory == null || paramCategory == ParamKind.FrozenStruct || paramCategory == ParamKind.SimpleEnum)
            return null;

        var csharpType = ResolveCSharpTypeName(typeSpec, typeDatabase);
        var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(typeSpec);

        return new ClassTrampolineParamInfo
        {
            Name = paramName,
            Kind = paramCategory.Value switch
            {
                ParamKind.Primitive => ClassTrampolineParamKind.Primitive,
                ParamKind.ObjCClass => ClassTrampolineParamKind.ObjCClass,
                ParamKind.SwiftClass => ClassTrampolineParamKind.SwiftClass,
                _ => ClassTrampolineParamKind.Primitive,
            },
            CSharpType = csharpType,
            SwiftTypeRendering = swiftType,
            TypeSpec = typeSpec,
        };
    }

    private static ClassTrampolineParamInfo? TryClassifyClosureSpec(
        string paramName,
        ClosureTypeSpec closure,
        ITypeDatabase typeDatabase)
    {
        // Restrict to void-return closures for this drop.
        if (!closure.ReturnType.IsEmptyTuple)
        {
            if (!(closure.ReturnType is NamedTypeSpec ret && (ret.Name == "()" || ret.Name == "Swift.Void")))
                return null;
        }

        // Walk closure arguments. Each is classified individually; the supported
        // shapes are: primitive scalars, non-optional class references (ObjC or
        // pure Swift), Optional<class>, and Optional<any Error> (the standard
        // Stripe completion-block shape). Anything else (frozen struct,
        // existential container, generic, etc.) disqualifies the closure.
        var argSpecs = ExtractClosureArgSpecs(closure);
        var argInfos = new List<ClosureArgInfo>();

        foreach (var argSpec in argSpecs)
        {
            var info = TryClassifyClosureArg(argSpec, typeDatabase);
            if (info == null)
                return null;
            argInfos.Add(info);
        }

        var swiftArgTypes = argInfos.Select(a => a.SwiftType).ToList();
        var csharpArgTypes = argInfos.Select(a => a.CSharpType).ToList();
        var cdeclSwiftArgTypes = argInfos.Select(a => a.SwiftCdeclType).ToList();
        var cdeclCSharpArgTypes = argInfos.Select(a => a.CSharpCdeclType).ToList();

        var swiftClosureSig = swiftArgTypes.Count == 0
            ? "() -> Void"
            : $"({string.Join(", ", swiftArgTypes)}) -> Void";
        // Per-call ctx is appended as the last cdecl arg of the function pointer.
        var swiftCdeclSig = $"@convention(c) ({string.Join(", ", cdeclSwiftArgTypes.Concat(new[] { "UnsafeRawPointer" }))}) -> Void";

        var csharpDelegateType = csharpArgTypes.Count == 0
            ? "global::System.Action"
            : $"global::System.Action<{string.Join(", ", csharpArgTypes)}>";
        var csharpCdeclSig = string.Join(", ", cdeclCSharpArgTypes.Concat(new[] { "IntPtr", "void" }));

        return new ClassTrampolineParamInfo
        {
            Name = paramName,
            Kind = ClassTrampolineParamKind.Closure,
            CSharpType = csharpDelegateType,
            SwiftTypeRendering = swiftClosureSig,
            TypeSpec = closure,
            ClosureCSharpDelegateType = csharpDelegateType,
            ClosureCSharpCdeclSig = csharpCdeclSig,
            ClosureSwiftCdeclSig = swiftCdeclSig,
            ClosureSwiftSig = swiftClosureSig,
            ClosureArgSwiftTypes = swiftArgTypes,
            ClosureArgCSharpTypes = csharpArgTypes,
            ClosureArgInfos = argInfos,
        };
    }

    /// <summary>
    /// Classifies a single closure argument TypeSpec into a model that captures
    /// every type rendering needed downstream (public Swift / C# / cdecl). The
    /// supported argument shapes are kept in sync with the cross-module class
    /// trampoline's adapter code below — any new kind added here MUST add the
    /// matching forward/back conversion to <see cref="BuildSwiftClosureCdeclArg"/>
    /// and <see cref="BuildCSharpCallbackInvokeArg"/>.
    /// </summary>
    private static ClosureArgInfo? TryClassifyClosureArg(TypeSpec argSpec, ITypeDatabase typeDatabase)
    {
        // Optional<inner>. The Swift parser models this as
        // NamedTypeSpec "Swift.Optional" with a single generic param.
        if (argSpec is NamedTypeSpec opt && opt.Name == "Swift.Optional" &&
            opt.ContainsGenericParameters && opt.GenericParameters.Count == 1 &&
            opt.GenericParameters[0] is TypeSpec inner)
        {
            // Optional<any Error> — bridged to Foundation.NSError on the C# side
            // via the AnyObject existential coercion (`error as AnyObject?`).
            if (inner is NamedTypeSpec namedInner && namedInner.IsAny && namedInner.Name == "Swift.Error")
            {
                return new ClosureArgInfo
                {
                    Kind = ClosureArgKind.OptionalError,
                    SwiftType = "(any Swift.Error)?",
                    CSharpType = "global::Foundation.NSError?",
                    SwiftCdeclType = "UnsafeMutableRawPointer?",
                    CSharpCdeclType = "IntPtr",
                    InnerSwiftType = "any Swift.Error",
                    InnerCSharpType = "global::Foundation.NSError",
                };
            }

            var innerCat = ClassifyParameterType(inner, typeDatabase);
            if (innerCat == ParamKind.ObjCClass || innerCat == ParamKind.SwiftClass)
            {
                var innerCs = ResolveCSharpTypeName(inner, typeDatabase);
                var innerSw = ExistentialBypassEmitter.RenderSwiftTypeSpec(inner);
                return new ClosureArgInfo
                {
                    Kind = innerCat == ParamKind.ObjCClass
                        ? ClosureArgKind.OptionalObjCClass
                        : ClosureArgKind.OptionalSwiftClass,
                    SwiftType = $"{innerSw}?",
                    CSharpType = $"{innerCs}?",
                    SwiftCdeclType = "UnsafeMutableRawPointer?",
                    CSharpCdeclType = "IntPtr",
                    InnerSwiftType = innerSw,
                    InnerCSharpType = innerCs,
                };
            }

            return null;
        }

        // Non-optional shapes.
        var cat = ClassifyParameterType(argSpec, typeDatabase);
        if (cat == null)
            return null;

        var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(argSpec);
        var csharpType = ResolveCSharpTypeName(argSpec, typeDatabase);

        return cat.Value switch
        {
            ParamKind.Primitive => new ClosureArgInfo
            {
                Kind = ClosureArgKind.Primitive,
                SwiftType = swiftType,
                CSharpType = MapBoolType(csharpType),
                SwiftCdeclType = swiftType,
                CSharpCdeclType = MapBoolType(csharpType),
                InnerSwiftType = swiftType,
                InnerCSharpType = csharpType,
            },
            ParamKind.ObjCClass => new ClosureArgInfo
            {
                Kind = ClosureArgKind.ObjCClass,
                SwiftType = swiftType,
                CSharpType = csharpType,
                SwiftCdeclType = "UnsafeMutableRawPointer",
                CSharpCdeclType = "IntPtr",
                InnerSwiftType = swiftType,
                InnerCSharpType = csharpType,
            },
            ParamKind.SwiftClass => new ClosureArgInfo
            {
                Kind = ClosureArgKind.SwiftClass,
                SwiftType = swiftType,
                CSharpType = csharpType,
                SwiftCdeclType = "UnsafeMutableRawPointer",
                CSharpCdeclType = "IntPtr",
                InnerSwiftType = swiftType,
                InnerCSharpType = csharpType,
            },
            _ => null,
        };
    }

    private enum ClosureArgKind
    {
        Primitive,
        ObjCClass,
        SwiftClass,
        OptionalObjCClass,
        OptionalSwiftClass,
        OptionalError,
    }

    private sealed class ClosureArgInfo
    {
        public required ClosureArgKind Kind { get; init; }
        // Type as it appears in the Swift native closure signature
        // (e.g. "StripePayments.STPToken?", "(any Swift.Error)?").
        public required string SwiftType { get; init; }
        // Type as it appears in the C# Action<...> public delegate signature.
        public required string CSharpType { get; init; }
        // @convention(c) cdecl arg type used between Swift trampoline and C# callback.
        public required string SwiftCdeclType { get; init; }
        // C# unmanaged callback / function-pointer cdecl arg type.
        public required string CSharpCdeclType { get; init; }
        // Inner (unwrapped) type names, useful for optional bridging code.
        public required string InnerSwiftType { get; init; }
        public required string InnerCSharpType { get; init; }
    }

    /// <summary>
    /// Builds the Swift expression that forwards a closure arg from its native
    /// Swift type (e.g. <c>STPToken?</c>) to the cdecl signature (e.g.
    /// <c>UnsafeMutableRawPointer?</c>) when invoking the C# callback.
    /// Class-typed args use <c>passRetained</c> so the C# wrapper owns +1 (its
    /// SafeHandle finalizer releases). <c>passUnretained</c> would let Swift
    /// drop the only retain when the closure returns, leaving C# pointing at
    /// freed memory once user code reads the wrapper after the callback.
    /// </summary>
    private static string BuildSwiftClosureCdeclArg(ClosureArgInfo info, string sourceExpr) => info.Kind switch
    {
        ClosureArgKind.Primitive => sourceExpr,
        ClosureArgKind.ObjCClass => $"Unmanaged.passRetained({sourceExpr}).toOpaque()",
        ClosureArgKind.SwiftClass => $"Unmanaged.passRetained({sourceExpr}).toOpaque()",
        ClosureArgKind.OptionalObjCClass => $"{sourceExpr}.map {{ Unmanaged.passRetained($0).toOpaque() }}",
        ClosureArgKind.OptionalSwiftClass => $"{sourceExpr}.map {{ Unmanaged.passRetained($0).toOpaque() }}",
        // any Error is bridged through the AnyObject coercion which preserves
        // the underlying NSError pointer on Apple platforms.
        ClosureArgKind.OptionalError => $"({sourceExpr} as AnyObject?).map {{ Unmanaged.passRetained($0).toOpaque() }}",
        _ => sourceExpr,
    };

    /// <summary>
    /// Builds the C# expression that converts a cdecl callback arg back into the
    /// public delegate's parameter type (e.g. <c>IntPtr</c> → <c>STPToken?</c>).
    /// Class wrappers take ownership of the +1 the Swift adapter produced via
    /// <c>passRetained</c> — finalizer / Dispose drops it back to zero.
    /// </summary>
    private static string BuildCSharpCallbackInvokeArg(ClosureArgInfo info, string sourceExpr) => info.Kind switch
    {
        ClosureArgKind.Primitive => sourceExpr,
        ClosureArgKind.ObjCClass => MarshallingHelpers.FormatObjCBridgeCall(info.InnerCSharpType, sourceExpr, nonNull: true, ownsReference: true),
        ClosureArgKind.SwiftClass => $"({info.InnerCSharpType})global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<{info.InnerCSharpType}>({sourceExpr})",
        ClosureArgKind.OptionalObjCClass => $"({sourceExpr} == IntPtr.Zero ? null : {MarshallingHelpers.FormatObjCBridgeCall(info.InnerCSharpType, sourceExpr, nonNull: true, ownsReference: true)})",
        ClosureArgKind.OptionalSwiftClass => $"({sourceExpr} == IntPtr.Zero ? null : ({info.InnerCSharpType})global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<{info.InnerCSharpType}>({sourceExpr}))",
        ClosureArgKind.OptionalError => $"({sourceExpr} == IntPtr.Zero ? null : {MarshallingHelpers.FormatObjCBridgeCall("global::Foundation.NSError", sourceExpr, nonNull: true, ownsReference: true)})",
        _ => sourceExpr,
    };

    private static IEnumerable<TypeSpec> ExtractClosureArgSpecs(ClosureTypeSpec closure)
    {
        if (!closure.HasArguments())
            return Array.Empty<TypeSpec>();
        if (closure.Arguments is TupleTypeSpec tts)
            return tts.Elements;
        return new[] { closure.Arguments };
    }

    private static string GetClosureTrampolineNativeArgExpr(ClassTrampolineParamInfo p) => p.Kind switch
    {
        ClassTrampolineParamKind.Primitive => p.Name,
        ClassTrampolineParamKind.ObjCClass => $"{p.Name}.Handle",
        ClassTrampolineParamKind.SwiftClass => $"{p.Name}.Payload.DangerousGetHandle()",
        // String: emit two comma-joined tokens — the pinned byte pointer and
        // the byte-array length. The enclosing C# body pre-encoded the bytes
        // and opened a `fixed` block over __{name}Bytes producing __{name}Ptr.
        ClassTrampolineParamKind.String => $"__{p.Name}Ptr, (nint)__{p.Name}Bytes.Length",
        _ => p.Name,
    };

    private static string BuildClosureTrampolinePInvokeParam(ClassTrampolineParamInfo p, ITypeDatabase typeDatabase) => p.Kind switch
    {
        ClassTrampolineParamKind.Primitive => p.TypeSpec is NamedTypeSpec n && n.Name == "Swift.Bool"
            ? $"[MarshalAs(UnmanagedType.U1)] bool {p.Name}"
            : $"{ResolveCSharpTypeName(p.TypeSpec, typeDatabase)} {p.Name}",
        ClassTrampolineParamKind.ObjCClass => $"IntPtr {p.Name}",
        ClassTrampolineParamKind.SwiftClass => $"IntPtr {p.Name}",
        // String: two comma-joined P/Invoke parameters matching the Swift
        // trampoline's (UnsafePointer<UInt8>?, Int) shape.
        ClassTrampolineParamKind.String => $"byte* {p.Name}Ptr, nint {p.Name}Len",
        _ => $"IntPtr {p.Name}",
    };

    private static string GetClosureTrampolinePInvokeReturnType(
        TypeSpec? returnTypeSpec,
        ReturnKind category,
        ITypeDatabase typeDatabase) => category switch
        {
            ReturnKind.Void => "void",
            ReturnKind.Primitive => returnTypeSpec is NamedTypeSpec n && n.Name == "Swift.Bool"
                ? "bool"
                : ResolveCSharpTypeName(returnTypeSpec!, typeDatabase),
            ReturnKind.ObjCClass => "IntPtr",
            ReturnKind.SwiftClass => "IntPtr",
            _ => "void",
        };

    private static string RenderClosureTrampolineSwiftParamType(ClassTrampolineParamInfo p) => p.Kind switch
    {
        ClassTrampolineParamKind.Primitive => p.SwiftTypeRendering,
        ClassTrampolineParamKind.ObjCClass => "UnsafeMutableRawPointer",
        ClassTrampolineParamKind.SwiftClass => "UnsafeMutableRawPointer",
        // String emits its own pair of Swift params directly in
        // EmitSwiftClosureTrampoline; this helper isn't consulted for it.
        _ => "UnsafeMutableRawPointer",
    };

    private static string ConvertClosureTrampolineCdeclArg(ClassTrampolineParamInfo p) => p.Kind switch
    {
        ClassTrampolineParamKind.Primitive => p.Name,
        ClassTrampolineParamKind.ObjCClass => $"(Unmanaged<AnyObject>.fromOpaque({p.Name}).takeUnretainedValue() as! {p.SwiftTypeRendering})",
        ClassTrampolineParamKind.SwiftClass => $"Unmanaged<{p.SwiftTypeRendering}>.fromOpaque({p.Name}).takeUnretainedValue()",
        // String: reconstitute a Swift.String from the pinned UTF-8 buffer.
        // The C# side guarantees a non-nil pointer for non-empty inputs (the
        // CLR returns a non-null pinned address even for zero-length arrays),
        // so the force-unwrap on Ptr is safe when Len > 0; empty buffers fall
        // through to an empty String literal.
        ClassTrampolineParamKind.String => $"({p.Name}Len > 0 ? String(decoding: UnsafeBufferPointer(start: {p.Name}Ptr!, count: {p.Name}Len), as: UTF8.self) : \"\")",
        _ => p.Name,
    };

    // =================================================================
    //  Async / throws / async-throws trampoline
    // =================================================================
    //
    // Stripe shape: `extension STPAPIClient { public func createToken(withCard:)
    // async throws -> STPToken }`. The Swift side `async throws` collapses to
    // a synthetic completion-handler @_cdecl signature:
    //
    //     (Args..., completionFn, completionCtx, self_) -> Void
    //
    // The trampoline spawns a Task that awaits the original method, then calls
    // completionFn with either (result, nil) on success or (default, NSError*)
    // on failure. The C# side stages a TaskCompletionSource in a GCHandle and
    // unpacks it from the completion callback, raising SwiftException for the
    // failure leg. The GCHandle is freed inside the C# completion callback
    // (one-shot lifetime — completion always fires from the Swift do/catch).
    //
    // Supported shapes in this drop:
    //  - Instance method on a pure Swift class OR an ObjC-rooted class.
    //  - Params: primitive / SimpleEnum / ObjC class / Swift class.
    //  - Return: void / primitive / ObjC class / Swift class.
    //  - Modifiers: { async, throws, async + throws }. Sync non-throws is
    //    already handled by the simple direct-CallConvSwift path.
    //  - Not handled: generic, mutating, accessor, static, FrozenStruct
    //    params/returns, String params, closure params.
    private static bool TryEmitAsyncOrThrowsExtensionTrampoline(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        MethodDecl method,
        ClassDecl classDecl,
        string origCSharpType,
        string origSwiftTypeQualified,
        string wrapperLibPath,
        string currentModule,
        ITypeDatabase typeDatabase,
        HashSet<string> emittedSignatures,
        List<ClassTrampolinePInvokeInfo> pinvokeDecls,
        TypeHandlerContext? context,
        ILogger logger)
    {
        if (!method.IsAsync && !method.Throws)
            return false;
        if (method.IsGeneric || method.IsMutating || method.IsAccessor)
            return false;
        if (method.MethodType == MethodType.Static)
            return false;

        var returnTypeSpec = method.CSSignature.Count > 0 ? method.CSSignature[0].SwiftTypeSpec : null;
        var returnCategory = ClassifyReturnType(returnTypeSpec, typeDatabase);
        if (returnCategory == null)
            return false;
        if (returnCategory.Value == ReturnKind.FrozenStruct || returnCategory.Value == ReturnKind.NonFrozenStruct)
            return false;

        // Parameter classification — closures, FrozenStruct, SimpleEnum, and
        // unsupported shapes bounce out. SimpleEnum is excluded from this drop
        // because the rawValue scalar isn't available on the bare NamedTypeSpec
        // without re-querying the TypeRecord; the Stripe surface doesn't use
        // SimpleEnum params on async-throws methods.
        var parameters = new List<AsyncTrampolineParamInfo>();
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var arg = method.CSSignature[i];
            var pk = ClassifyParameterType(arg.SwiftTypeSpec, typeDatabase);
            if (pk == null || pk == ParamKind.FrozenStruct || pk == ParamKind.SimpleEnum)
                return false;
            var pname = NameProvider.GetCSharpParameterName(arg);
            var pctype = ResolveCSharpTypeName(arg.SwiftTypeSpec, typeDatabase);
            parameters.Add(new AsyncTrampolineParamInfo
            {
                Name = pname,
                CSharpType = pctype,
                Kind = pk.Value,
                TypeSpec = arg.SwiftTypeSpec,
                ArgLabel = arg.Name,
            });
        }

        bool isAsync = method.IsAsync;
        bool isThrowing = method.Throws;

        var methodName = NameProvider.ToPascalCase(method.Name);
        var publicMethodName = isAsync && !methodName.EndsWith("Async", StringComparison.Ordinal)
            ? methodName + "Async"
            : methodName;
        var modeKey = (isAsync, isThrowing) switch
        {
            (true, true) => "asyncthrows",
            (true, false) => "async",
            (false, true) => "throws",
            _ => "sync",
        };
        var signatureKey = $"{modeKey}:instance:{publicMethodName}({string.Join(",", parameters.Select(p => p.CSharpType))})";
        if (!emittedSignatures.Add(signatureKey))
            return false;

        var hashSeed = !string.IsNullOrEmpty(method.MangledName)
            ? method.MangledName + "|" + modeKey
            : $"{currentModule}|{origSwiftTypeQualified}|{method.Name}|{modeKey}|{string.Join(",", parameters.Select(p => p.CSharpType))}";
        var symbolHash = EmitterUtility.DeterministicHash8(hashSeed);
        var symbolName = $"SBW_{currentModule}_ClsExtAT_{SafeTypeName(classDecl.Name)}_{method.Name}_{symbolHash}";
        var pinvokeName = $"PInvoke_{publicMethodName}_{symbolHash}";
        var completionCallbackName = $"__{publicMethodName}_Completion_{symbolHash}";

        var csharpReturnType = returnCategory.Value == ReturnKind.Void || returnTypeSpec == null
            ? "void"
            : ResolveCSharpTypeName(returnTypeSpec, typeDatabase);
        var publicReturnType = MapBoolType(csharpReturnType);

        // Public-facing return: Task for async, Task<T> for async-with-value,
        // T for sync-throws, void for sync-throws-void.
        string publicWrapperReturn;
        if (isAsync)
            publicWrapperReturn = returnCategory.Value == ReturnKind.Void
                ? "global::System.Threading.Tasks.Task"
                : $"global::System.Threading.Tasks.Task<{publicReturnType}>";
        else
            publicWrapperReturn = publicReturnType;

        // ---------- C# public extension method ----------
        csWriter.WriteLine();
        var publicParams = new List<string> { $"this {origCSharpType} self" };
        foreach (var p in parameters)
            publicParams.Add($"{p.PublicCSharpType} {p.Name}");
        csWriter.WriteLine($"public static unsafe {publicWrapperReturn} {publicMethodName}({string.Join(", ", publicParams)})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // TaskCompletionSource (or its non-generic form for void async) is what
        // the completion callback unpacks. The GCHandle pins it for the native
        // boundary; ownership transfers to Swift on a successful P/Invoke and
        // the C# completion callback frees it after the TCS is resolved.
        string tcsType = returnCategory.Value == ReturnKind.Void
            ? "global::System.Threading.Tasks.TaskCompletionSource"
            : $"global::System.Threading.Tasks.TaskCompletionSource<{publicReturnType}>";
        csWriter.WriteLine($"var __tcs = new {tcsType}(global::System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);");
        csWriter.WriteLine("var __ctxHandle = global::System.Runtime.InteropServices.GCHandle.Alloc(__tcs);");
        csWriter.WriteLine("bool __ctxTransferred = false;");
        csWriter.WriteLine("try");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        var selfExpr = classDecl.IsObjCRooted ? "self.Handle" : "self.Payload.DangerousGetHandle()";

        var nativeArgs = new List<string>();
        foreach (var p in parameters)
            nativeArgs.Add(GetPInvokeArgExpression(p.Name, p.Kind));
        nativeArgs.Add($"&{completionCallbackName}");
        nativeArgs.Add("global::System.Runtime.InteropServices.GCHandle.ToIntPtr(__ctxHandle)");
        nativeArgs.Add(selfExpr);

        if (!classDecl.IsObjCRooted)
        {
            csWriter.WriteLine("bool __payloadPinned = false;");
            csWriter.WriteLine("self.Payload.DangerousAddRef(ref __payloadPinned);");
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
        }

        csWriter.WriteLine($"NativeMethods.{pinvokeName}({string.Join(", ", nativeArgs)});");
        // For async, the P/Invoke returns immediately and Swift owns the
        // GCHandle for the duration of the Task; the completion callback frees
        // it. For sync throws, the P/Invoke does not return until the Swift
        // method completes — by then completion has already fired (and freed
        // the GCHandle), so the transfer flag prevents the outer `finally`
        // from double-freeing.
        csWriter.WriteLine("__ctxTransferred = true;");

        if (!classDecl.IsObjCRooted)
        {
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("finally");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("if (__payloadPinned) self.Payload.DangerousRelease();");
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine("finally");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine("if (!__ctxTransferred && __ctxHandle.IsAllocated) __ctxHandle.Free();");
        csWriter.Indent--;
        csWriter.WriteLine("}");

        // Return shape — Task for async, materialize sync result for non-async.
        if (isAsync)
        {
            csWriter.WriteLine("return __tcs.Task;");
        }
        else
        {
            // Sync throws — block on the Task. The P/Invoke synchronously calls
            // completionFn inside the Swift trampoline before returning, so the
            // TCS is already resolved by the time we read it.
            if (returnCategory.Value == ReturnKind.Void)
                csWriter.WriteLine("__tcs.Task.GetAwaiter().GetResult();");
            else
                csWriter.WriteLine("return __tcs.Task.GetAwaiter().GetResult();");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");

        // ---------- Completion callback (UnmanagedCallersOnly) ----------
        string resultCdeclType = returnCategory.Value switch
        {
            ReturnKind.Void => "byte",
            ReturnKind.Primitive => publicReturnType == "bool" ? "byte" : publicReturnType,
            ReturnKind.ObjCClass => "IntPtr",
            ReturnKind.SwiftClass => "IntPtr",
            _ => "byte",
        };

        csWriter.WriteLine();
        csWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        // For non-throwing async, the errorPtr parameter is still emitted by
        // the Swift trampoline (always nil) to keep the cdecl signature stable
        // across the throws/non-throws shapes — a single completion shape lets
        // the runtime route both paths through the same callback type.
        csWriter.WriteLine($"private static void {completionCallbackName}({resultCdeclType} result, IntPtr errorPtr, IntPtr ctx)");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine("var __h = global::System.Runtime.InteropServices.GCHandle.FromIntPtr(ctx);");
        csWriter.WriteLine("try");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine($"var __tcs = ({tcsType})__h.Target!;");
        csWriter.WriteLine("if (errorPtr != IntPtr.Zero)");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine("var __err = ObjCRuntime.Runtime.GetINativeObject<global::Foundation.NSError>(errorPtr, true);");
        csWriter.WriteLine("__tcs.TrySetException(new global::Swift.Runtime.SwiftException(__err?.LocalizedDescription ?? \"Swift error\"));");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine("else");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        switch (returnCategory.Value)
        {
            case ReturnKind.Void:
                csWriter.WriteLine("__tcs.TrySetResult();");
                break;
            case ReturnKind.Primitive:
                if (publicReturnType == "bool")
                    csWriter.WriteLine("__tcs.TrySetResult(result != 0);");
                else
                    csWriter.WriteLine("__tcs.TrySetResult(result);");
                break;
            case ReturnKind.ObjCClass:
                csWriter.WriteLine($"__tcs.TrySetResult({MarshallingHelpers.FormatObjCBridgeCall(publicReturnType, "result", nonNull: true, ownsReference: true)});");
                break;
            case ReturnKind.SwiftClass:
                csWriter.WriteLine($"__tcs.TrySetResult(({publicReturnType})global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<{publicReturnType}>(result));");
                break;
        }
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine("finally");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine("if (__h.IsAllocated) __h.Free();");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.Indent--;
        csWriter.WriteLine("}");

        // ---------- P/Invoke declaration ----------
        var pinvokeParams = new List<string>();
        foreach (var p in parameters)
            pinvokeParams.Add($"{ResolvePInvokeParamType(p.TypeSpec, p.Kind, typeDatabase)} {p.Name}");
        pinvokeParams.Add($"delegate* unmanaged[Cdecl]<{resultCdeclType}, IntPtr, IntPtr, void> completionFn");
        pinvokeParams.Add("IntPtr completionCtx");
        pinvokeParams.Add("IntPtr __self");

        pinvokeDecls.Add(new ClassTrampolinePInvokeInfo(
            EntryPoint: symbolName,
            MethodName: pinvokeName,
            ReturnType: "void",
            Parameters: pinvokeParams));

        // ---------- Swift @_cdecl trampoline ----------
        EmitSwiftAsyncOrThrowsTrampoline(
            swiftWriter, method, classDecl, origSwiftTypeQualified,
            symbolName, parameters, returnTypeSpec, returnCategory.Value,
            resultCdeclType, isAsync, isThrowing, typeDatabase);

        logger.LogDebug(
            "Emitted {Mode} cross-module class extension trampoline {Symbol} for {Type}.{Method}",
            modeKey, symbolName, classDecl.Name, method.Name);
        return true;
    }

    private static void EmitSwiftAsyncOrThrowsTrampoline(
        SwiftWriter swiftWriter,
        MethodDecl method,
        ClassDecl classDecl,
        string origSwiftTypeQualified,
        string symbolName,
        List<AsyncTrampolineParamInfo> parameters,
        TypeSpec? returnTypeSpec,
        ReturnKind returnCategory,
        string resultCdeclType,
        bool isAsync,
        bool isThrowing,
        ITypeDatabase typeDatabase)
    {
        swiftWriter.WriteLine();
        swiftWriter.WriteLine($"// Cross-module class-extension {(isAsync ? "async " : "")}{(isThrowing ? "throws " : "")}trampoline for {origSwiftTypeQualified}.{method.Name}");
        swiftWriter.WriteLine($"@_cdecl(\"{symbolName}\")");

        // Swift cdecl signature
        var swiftParams = new List<string>();
        foreach (var p in parameters)
            swiftParams.Add($"_ {p.Name}: {RenderAsyncTrampolineSwiftParam(p)}");
        var completionResultType = returnCategory switch
        {
            ReturnKind.Void => "UInt8", // unused
            ReturnKind.Primitive => MapResolvedToSwiftScalar(returnTypeSpec!),
            ReturnKind.ObjCClass => "UnsafeMutableRawPointer",
            ReturnKind.SwiftClass => "UnsafeMutableRawPointer",
            _ => "UInt8",
        };
        swiftParams.Add($"_ completionFn: @convention(c) ({completionResultType}, UnsafeMutableRawPointer?, UnsafeRawPointer) -> Void");
        swiftParams.Add("_ completionCtx: UnsafeRawPointer");
        swiftParams.Add("_ self_: UnsafeRawPointer");

        var swiftFuncName = $"_sbw_clsextAT_{symbolName.Substring("SBW_".Length)}";
        swiftWriter.WriteLine($"public func {swiftFuncName}({string.Join(", ", swiftParams)}) {{");
        swiftWriter.Indent++;

        // Resurrect receiver
        if (classDecl.IsObjCRooted)
        {
            swiftWriter.WriteLine($"let __self = Unmanaged<AnyObject>.fromOpaque(self_).takeUnretainedValue() as! {origSwiftTypeQualified}");
        }
        else
        {
            swiftWriter.WriteLine($"let __self = Unmanaged<{origSwiftTypeQualified}>.fromOpaque(self_).takeUnretainedValue()");
        }

        // Build call arg list (in-Swift call to method)
        var callArgs = new List<string>();
        for (int i = 0; i < parameters.Count; i++)
        {
            var p = parameters[i];
            var label = p.ArgLabel;
            var converted = ConvertAsyncTrampolineCdeclArg(p);
            // Underscore-labeled Swift params (`func foo(_ x: Int)`) are synthesized
            // by the parser to `argN`. The Swift call site must omit the label.
            bool unlabeled = string.IsNullOrEmpty(label) || label == "_" || SwiftBuilder.IsAutoGeneratedArgName(label);
            if (!unlabeled)
                callArgs.Add($"{label}: {converted}");
            else
                callArgs.Add(converted);
        }
        var callArgsString = string.Join(", ", callArgs);

        // Build the success/failure marshalling for completionFn args.
        // - Void success → completionFn(0, nil, completionCtx)
        // - Primitive success → completionFn(scalar, nil, completionCtx)
        // - ObjC/Swift class → completionFn(passRetained(result).toOpaque(), nil, completionCtx)
        // Bool primitive: Swift result is `Bool` but the cdecl boundary uses `UInt8`
        // (see MapResolvedToSwiftScalar), so widen on the way out.
        bool returnIsBool = returnCategory == ReturnKind.Primitive
            && returnTypeSpec is NamedTypeSpec rnts && rnts.Name == "Swift.Bool";
        string primitiveSuccessArg = returnIsBool ? "__r ? 1 : 0" : "__r";
        string successCompletionCall = returnCategory switch
        {
            ReturnKind.Void => "completionFn(0, nil, completionCtx)",
            ReturnKind.Primitive => $"completionFn({primitiveSuccessArg}, nil, completionCtx)",
            ReturnKind.ObjCClass => "completionFn(Unmanaged.passRetained(__r as AnyObject).toOpaque(), nil, completionCtx)",
            ReturnKind.SwiftClass => "completionFn(Unmanaged.passRetained(__r).toOpaque(), nil, completionCtx)",
            _ => "completionFn(0, nil, completionCtx)",
        };
        // Failure default for the value slot — we want a benign value, the C# side ignores it when errorPtr != nil.
        string failureDefaultArg = returnCategory switch
        {
            ReturnKind.Void => "0",
            ReturnKind.Primitive => $"{completionResultType}(0)",
            ReturnKind.ObjCClass => "UnsafeMutableRawPointer(bitPattern: -1)!",
            ReturnKind.SwiftClass => "UnsafeMutableRawPointer(bitPattern: -1)!",
            _ => "0",
        };

        var callExpr = $"__self.{method.Name}({callArgsString})";

        if (isAsync && isThrowing)
        {
            swiftWriter.WriteLine("Task {");
            swiftWriter.Indent++;
            swiftWriter.WriteLine("do {");
            swiftWriter.Indent++;
            if (returnCategory == ReturnKind.Void)
                swiftWriter.WriteLine($"_ = try await {callExpr}");
            else
                swiftWriter.WriteLine($"let __r = try await {callExpr}");
            swiftWriter.WriteLine(successCompletionCall);
            swiftWriter.Indent--;
            swiftWriter.WriteLine("} catch {");
            swiftWriter.Indent++;
            swiftWriter.WriteLine($"completionFn({failureDefaultArg}, Unmanaged.passRetained(error as AnyObject).toOpaque(), completionCtx)");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
        }
        else if (isAsync)
        {
            swiftWriter.WriteLine("Task {");
            swiftWriter.Indent++;
            if (returnCategory == ReturnKind.Void)
                swiftWriter.WriteLine($"_ = await {callExpr}");
            else
                swiftWriter.WriteLine($"let __r = await {callExpr}");
            swiftWriter.WriteLine(successCompletionCall);
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
        }
        else if (isThrowing)
        {
            // Sync throws — call inline.
            swiftWriter.WriteLine("do {");
            swiftWriter.Indent++;
            if (returnCategory == ReturnKind.Void)
                swiftWriter.WriteLine($"_ = try {callExpr}");
            else
                swiftWriter.WriteLine($"let __r = try {callExpr}");
            swiftWriter.WriteLine(successCompletionCall);
            swiftWriter.Indent--;
            swiftWriter.WriteLine("} catch {");
            swiftWriter.Indent++;
            swiftWriter.WriteLine($"completionFn({failureDefaultArg}, Unmanaged.passRetained(error as AnyObject).toOpaque(), completionCtx)");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    private static string RenderAsyncTrampolineSwiftParam(AsyncTrampolineParamInfo p) => p.Kind switch
    {
        ParamKind.Primitive => RenderPrimitiveSwiftType(p.TypeSpec),
        ParamKind.ObjCClass => "UnsafeMutableRawPointer",
        ParamKind.SwiftClass => "UnsafeMutableRawPointer",
        _ => "UnsafeMutableRawPointer",
    };

    private static string ConvertAsyncTrampolineCdeclArg(AsyncTrampolineParamInfo p) => p.Kind switch
    {
        ParamKind.Primitive => p.Name,
        ParamKind.ObjCClass => $"(Unmanaged<AnyObject>.fromOpaque({p.Name}).takeUnretainedValue() as! {RenderSwiftTypeName(p.TypeSpec)})",
        ParamKind.SwiftClass => $"Unmanaged<{RenderSwiftTypeName(p.TypeSpec)}>.fromOpaque({p.Name}).takeUnretainedValue()",
        _ => p.Name,
    };

    private static string RenderPrimitiveSwiftType(TypeSpec spec)
    {
        if (spec is NamedTypeSpec n)
        {
            // Strip the leading "Swift." for the wrapper rendering — Swift module
            // is implicitly imported and bare names are idiomatic in the wrapper.
            return n.Name.StartsWith("Swift.", StringComparison.Ordinal)
                ? n.Name.Substring("Swift.".Length)
                : n.Name;
        }
        return "Int";
    }

    private static string RenderSwiftTypeName(TypeSpec spec)
    {
        if (spec is NamedTypeSpec n)
            return n.Name;
        return "AnyObject";
    }

    private static string MapResolvedToSwiftScalar(TypeSpec spec)
    {
        if (spec is NamedTypeSpec n && n.Name == "Swift.Bool")
            return "UInt8";
        return RenderPrimitiveSwiftType(spec);
    }

    private sealed class AsyncTrampolineParamInfo
    {
        public required string Name { get; init; }
        public required ParamKind Kind { get; init; }
        public required string CSharpType { get; init; }
        public required TypeSpec TypeSpec { get; init; }
        public required string ArgLabel { get; init; }
        public string PublicCSharpType => MapBoolType(CSharpType);
    }

    private enum ClassTrampolineParamKind
    {
        Primitive,
        ObjCClass,
        SwiftClass,
        Closure,
        String,
    }

    private sealed class ClassTrampolineParamInfo
    {
        public required string Name { get; init; }
        public required ClassTrampolineParamKind Kind { get; init; }
        public required string CSharpType { get; init; }
        public required string SwiftTypeRendering { get; init; }
        public required TypeSpec TypeSpec { get; init; }

        // Public C# signature type (Bool mapped to bool, closure mapped to Action<...>).
        public string PublicCSharpType => Kind == ClassTrampolineParamKind.Closure
            ? ClosureCSharpDelegateType!
            : MapBoolType(CSharpType);

        // Closure-only fields. Populated when Kind == Closure.
        public string? ClosureCSharpDelegateType { get; init; }
        public string? ClosureCSharpCdeclSig { get; init; } // arg types + "IntPtr" + "void"
        public string? ClosureSwiftCdeclSig { get; init; }  // @convention(c) (..., UnsafeRawPointer) -> Void
        public string? ClosureSwiftSig { get; init; }       // (Swift args) -> Void
        public List<string> ClosureArgSwiftTypes { get; init; } = new();
        public List<string> ClosureArgCSharpTypes { get; init; } = new();
        public List<ClosureArgInfo> ClosureArgInfos { get; init; } = new();
    }

    private readonly record struct ClassTrampolinePInvokeInfo(
        string EntryPoint,
        string MethodName,
        string ReturnType,
        List<string> Parameters);
}
