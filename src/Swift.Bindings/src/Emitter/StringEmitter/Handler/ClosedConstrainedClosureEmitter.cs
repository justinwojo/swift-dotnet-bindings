// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Emits working C# bindings for escaping-closure methods that live inside an
/// <em>inheritance-constrained extension on a generic class</em> — e.g.
/// <c>extension HostWrapper where Base: PixelHost { func loadPixels(scaleBy:onSuccess:onFailure:) }</c>.
///
/// Such members are skipped today with reason <see cref="SkipReason.GenericTypeCallback"/>:
/// a <c>[UnmanagedCallersOnly]</c> reverse thunk cannot be emitted inside a generic C# type
/// (CS7042), and the generic-parent <see cref="MethodClosureBridge"/> path emits a
/// <c>@_silgen_name</c> generic-extension symbol that the dylib does not export at runtime.
///
/// The closed-instantiation strategy sidesteps both walls. A <c>where Base: PixelHost</c>
/// constraint has a natural closed receiver <c>HostWrapper&lt;PixelHost&gt;</c> — a fully
/// concrete type. A non-generic Swift <c>@_cdecl</c> wrapper referring to that concrete
/// receiver is legal and produces a real, callable symbol (Swift materializes the closed
/// generic's metadata statically). This emitter writes, per (parent, concrete-anchor, method):
/// a C# static <em>extension</em> method on <c>HostWrapper&lt;PixelHost&gt;</c>, its closure
/// callback trampolines + function-pointer fields + a Cdecl P/Invoke (all non-generic, so
/// no CS7042), and the matching <c>@_cdecl</c> Swift wrapper that reconstructs the concrete
/// receiver and forwards the call.
///
/// First cut (v1) scope — kept deliberately narrow so the emitted subset is exactly what the
/// gate admits (no silent drops):
///   • parent is a public, non-nested, generic <b>class</b> (heap receiver → <c>Unmanaged.fromOpaque</c>);
///   • instance, non-async, non-throwing, non-constructor, non-accessor methods with no method-own generics;
///   • the closed anchor is a class the constraint resolves to (same-module or ObjC-bridged);
///   • non-closure parameters are blittable primitive scalars;
///   • closure parameters are escaping, non-optional, cdecl-compatible, non-throwing.
/// Everything outside this subset falls through to the normal validation path and surfaces a
/// proper skip reason. String / structured-Result / optional-closure arguments and struct
/// parents are tracked follow-ups.
/// </summary>
public static class ClosedConstrainedClosureEmitter
{
    /// <summary>
    /// Blittable primitive scalars that pass directly through the <c>@_cdecl</c> / P/Invoke
    /// boundary with identity marshalling. Bool is excluded from v1 (needs a U1 projection).
    /// Keyed by the last dot-segment of the Swift type name.
    /// </summary>
    private static readonly Dictionary<string, (string Swift, string CSharp)> PrimitiveScalars =
        new(System.StringComparer.Ordinal)
        {
            ["Int8"] = ("Int8", "sbyte"),
            ["Int16"] = ("Int16", "short"),
            ["Int32"] = ("Int32", "int"),
            ["Int64"] = ("Int64", "long"),
            ["Int"] = ("Int", "nint"),
            ["UInt8"] = ("UInt8", "byte"),
            ["UInt16"] = ("UInt16", "ushort"),
            ["UInt32"] = ("UInt32", "uint"),
            ["UInt64"] = ("UInt64", "ulong"),
            ["UInt"] = ("UInt", "nuint"),
            ["Float"] = ("Float", "float"),
            ["Double"] = ("Double", "double"),
        };

    // ─────────────────────────────── Plan (single source of truth) ───────────────────────────────

    /// <summary>A validated, ready-to-emit parameter: either a blittable primitive or an escaping closure.</summary>
    internal sealed record PlanParam(
        ArgumentDecl Arg,
        string Identifier,          // valid Swift+C# identifier (the closure/param name)
        string? CallLabel,          // Swift argument label for the forwarded call, or null for `_`
        bool IsClosure,
        ClosureTypeSpec? Closure,   // set iff IsClosure
        string SwiftScalar,         // set iff !IsClosure (e.g. "Int32")
        string CSharpScalar);       // set iff !IsClosure (e.g. "int")

    /// <summary>A fully-resolved emission plan for one closed-constrained-closure method.</summary>
    internal sealed record Plan(
        MethodDecl Method,
        ClassDecl Parent,
        SwiftTypeName ConcreteAnchor,   // e.g. SwiftBindingsTestLib.PixelHost
        string ConcreteAnchorCSharp,    // e.g. global::SwiftBindingsTestLib.PixelHost
        string ClosedSwiftType,         // e.g. SwiftBindingsTestLib.HostWrapper<SwiftBindingsTestLib.PixelHost>
        string ClosedCSharpType,        // e.g. global::SwiftBindingsTestLib.HostWrapper<global::...PixelHost>
        IReadOnlyList<PlanParam> Params);

    // ─────────────────────────────── Gate predicate ───────────────────────────────

    /// <summary>
    /// GTC-gate rescue predicate. True iff <paramref name="method"/> is a closure-bearing member of an
    /// inheritance-constrained extension on a generic class that this emitter can fully emit as a closed
    /// specialization. Shares <see cref="TryBuildPlan"/> with the emitter, so the gate can never admit a
    /// member the emitter would silently drop.
    /// </summary>
    public static bool IsEligible(MethodDecl method, ITypeDatabase typeDatabase)
        => TryBuildPlan(method, new ClosureHandler(typeDatabase), typeDatabase) != null;

    // ─────────────────────────────── Emission entry point ───────────────────────────────

    /// <summary>
    /// Entry point, invoked at namespace scope after <paramref name="parentDecl"/>'s body is closed.
    /// If the generic class has any eligible closure-bearing constrained-extension methods, emits a
    /// <c>{Parent}ClosedConstrainedClosureExtensions</c> static class holding one extension method per
    /// (concrete-anchor, method), plus their closure trampolines and Cdecl P/Invokes, and the matching
    /// <c>@_cdecl</c> Swift wrappers. No-op for any other shape.
    /// </summary>
    public static void EmitClosedConstrainedClosures(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        TypeDecl parentDecl,
        ModuleDecl moduleDecl,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        ILogger logger)
    {
        if (parentDecl is not ClassDecl parentClass) return;
        if (!parentClass.IsGeneric || parentClass.IsModuleInternal) return;
        // A nested parent is emitted INLINE inside its enclosing type; an extension method must live in a
        // top-level static class (CS1109). Decline nested parents, mirroring the SCP / CSM guards.
        if (parentClass.ParentDecl is TypeDecl) return;
        // The @_cdecl wrappers only exist in xcframework (wrapper) mode.
        if (!WrapperValidation.IsXCFrameworkMode(typeDatabase)) return;

        var closureHandler = new ClosureHandler(typeDatabase);
        var plans = parentClass.Methods
            .Select(m => TryBuildPlan(m, closureHandler, typeDatabase))
            .Where(p => p != null)
            .Select(p => p!)
            .ToList();
        if (plans.Count == 0) return;

        // Ensure the escaping-closure owner-token box helper (_sbWrapClosureContext) exists — this
        // emitter may be the only closure user in the module.
        ClosureContextHelperEmitter.EmitIfNeeded(swiftWriter, emissionContext);

        var extClassName = $"{parentClass.Name}ClosedConstrainedClosureExtensions";
        csWriter.WriteLine();
        csWriter.WriteLine($"/// <summary>Closed-instantiation closure extensions surfacing constrained-extension methods on {parentClass.Name}&lt;…&gt;.</summary>");
        csWriter.WriteLine($"public static unsafe partial class {extClassName}");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        foreach (var plan in plans)
            TryEmitOne(csWriter, swiftWriter, plan, closureHandler, moduleDecl, typeDatabase, emissionContext, logger);

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();
    }

    // ─────────────────────────────── Plan construction ───────────────────────────────

    private static Plan? TryBuildPlan(MethodDecl method, ClosureHandler closureHandler, ITypeDatabase typeDatabase)
    {
        var plan = TryBuildPlanCore(method, closureHandler, typeDatabase);
        if (plan == null) return null;

        // Two label-only Swift overloads on the same closed receiver — same method name and same C#
        // parameter types, differing ONLY by argument label (`register(success:)` vs `register(failure:)`)
        // — project to the SAME C# extension signature. Emitting both is CS0111, and they also share a
        // type-only wrapper-claim key, so the later one would silently lose the TryClaimWrapperSymbol race
        // and vanish with no skip — breaking this emitter's "gate ≡ emit, no silent drops" invariant.
        // Preserve it here: decline the LATER-declared colliding overload so it falls through to a visible
        // GenericTypeCallback skip, while the first still emits. Same closed receiver only — a different
        // anchor yields a distinct C# receiver type and therefore a distinct, legal overload.
        var key = ComputeCSharpExtensionSignatureKey(plan, closureHandler);
        foreach (var sibling in plan.Parent.Methods)
        {
            if (ReferenceEquals(sibling, method)) break; // only earlier-declared siblings claim the slot
            var siblingPlan = TryBuildPlanCore(sibling, closureHandler, typeDatabase);
            if (siblingPlan != null && ComputeCSharpExtensionSignatureKey(siblingPlan, closureHandler) == key)
                return null;
        }
        return plan;
    }

    /// <summary>
    /// The projected C# extension-method signature that <see cref="EmitCSharpSide"/> will emit for
    /// <paramref name="plan"/>: closed receiver type + PascalCase name + ordered C# parameter types. Two
    /// plans sharing this key would emit the same extension method (CS0111), so it is the collision key.
    /// </summary>
    private static string ComputeCSharpExtensionSignatureKey(Plan plan, ClosureHandler closureHandler)
    {
        var csName = NameProvider.ToPascalCase(plan.Method.Name);
        var paramTypes = string.Join(",", plan.Params.Select(p =>
            p.IsClosure ? closureHandler.GetCSharpDelegateType(p.Closure!) : p.CSharpScalar));
        return $"{plan.ClosedCSharpType}::{csName}({paramTypes})";
    }

    private static Plan? TryBuildPlanCore(MethodDecl method, ClosureHandler closureHandler, ITypeDatabase typeDatabase)
    {
        // The @_cdecl wrapper only exists in xcframework (wrapper) mode. The gate predicate
        // (IsEligible) and the emitter share this method, so this check MUST live here: in
        // Direct mode the gate would otherwise route the member to RoutedElsewhere while emit
        // no-ops, and the member would vanish with no skip surface. Keeping the check in the
        // shared plan makes IsEligible false in Direct mode → the member falls through to the
        // normal GenericTypeCallback skip (visible), exactly matching emit capability.
        if (!WrapperValidation.IsXCFrameworkMode(typeDatabase)) return null;

        // Parent must be a public, generic class.
        if (method.ParentDecl is not ClassDecl parent) return null;
        if (!parent.IsGeneric || parent.IsModuleInternal) return null;
        if (parent.ParentDecl is TypeDecl) return null;
        // v1 closes exactly one generic slot to the constraint anchor (`Wrapper<Anchor>`).
        // A multi-parameter parent (`Box<T, U>`) has no single closed arity, so decline it —
        // building `Box<Anchor>` would be wrong arity.
        if (parent.GenericParameters.Count != 1) return null;

        // Method shape: instance, sync, non-throwing, non-ctor, non-accessor, no method-own generics.
        if (method.MethodType != MethodType.Instance) return null;
        if (method.IsConstructor || method.IsAccessor || method.IsSubscriptAccessor) return null;
        if (method.IsAsync || method.Throws) return null;
        if (method.IsModuleInternal || method.IsSpiProtected) return null;
        if (HasMethodOwnGenerics(method)) return null;
        // Void return only (v1). CSSignature[0] is the return slot.
        if (method.CSSignature.Count == 0) return null;
        if (!method.CSSignature[0].SwiftTypeSpec.IsEmptyTuple) return null;

        // Resolve the concrete class anchor from a constraint on a parent (depth-0) generic slot.
        var anchor = ResolveConcreteClassAnchor(method, parent, typeDatabase);
        if (anchor == null) return null;
        if (!typeDatabase.TryGetTypeRecord(anchor, out var anchorRecord)) return null;
        var anchorCSharp = anchorRecord.CSharpTypeName.FullyQualifiedName;

        // At least one escaping closure param that needs a reverse thunk (that is WHY it hits GTC).
        int closureParamCount = method.CSSignature.Skip(1).Count(closureHandler.IsClosure);
        if (closureParamCount == 0) return null;

        var planParams = new List<PlanParam>();
        bool sawThunkClosure = false;
        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue; // empty-tuple placeholder is not a real parameter

            var identifier = arg.PrivateName;
            // Underscore-labeled Swift params (`func f(_ x: Int)`) are synthesized by the parser
            // to `argN`; the forwarded Swift call must omit the label, not emit `f(arg0: …)`.
            var callLabel = (string.IsNullOrEmpty(arg.Name) || arg.Name == "_" || SwiftBuilder.IsAutoGeneratedArgName(arg.Name))
                ? null
                : arg.Name;

            if (closureHandler.IsClosure(arg))
            {
                // Escaping, non-optional, cdecl-compatible, non-throwing closure only (v1).
                if (arg.SwiftTypeSpec is not ClosureTypeSpec closureSpec) return null; // Optional<Closure> etc. unsupported
                if (!closureSpec.IsEscaping) return null;
                if (closureSpec.Throws) return null;
                if (!ClosureEmitter.IsClosureCdeclCompatible(closureSpec, closureHandler)) return null;
                if (closureHandler.RequiresThunk(closureSpec, method.MangledName, closureParamCount))
                    sawThunkClosure = true;
                planParams.Add(new PlanParam(arg, identifier, callLabel, IsClosure: true, closureSpec, "", ""));
            }
            else
            {
                // Blittable primitive scalar passed by value only (v1). An `inout` (or otherwise
                // address-passed) scalar has a different ABI than the by-value scalar this path
                // emits, so decline it rather than mis-marshal.
                if (arg.IsInOut) return null;
                if (!TryResolveScalar(arg.SwiftTypeSpec, out var swiftScalar, out var csScalar)) return null;
                planParams.Add(new PlanParam(arg, identifier, callLabel, IsClosure: false, null, swiftScalar, csScalar));
            }
        }
        // Guard against a degenerate group with closures that don't actually need a thunk (would not
        // have hit the GTC gate) — keep the gate/emit contract tight.
        if (!sawThunkClosure) return null;

        var closedSwiftType = $"{parent.SwiftTypeName.ModuleQualifiedName}<{anchor.ModuleQualifiedName}>";
        var closedCSharpType = $"{QualifyParentCSharp(parent, typeDatabase)}<{anchorCSharp}>";

        return new Plan(method, parent, anchor, anchorCSharp, closedSwiftType, closedCSharpType, planParams);
    }

    /// <summary>
    /// Finds a constraint on one of the parent's depth-0 generic slots whose target resolves to a class
    /// (same-module or ObjC-bridged). Both a same-type <c>== Class</c> and a superclass <c>: Class</c>
    /// bound (parsed as <see cref="ConformanceKind.Protocol"/> — the parser can't tell a class from a
    /// protocol) qualify; a real protocol bound resolves to no class record and is rejected.
    /// </summary>
    private static SwiftTypeName? ResolveConcreteClassAnchor(MethodDecl method, ClassDecl parent, ITypeDatabase typeDatabase)
    {
        var parentSlotNames = new HashSet<string>(parent.GenericParameters.Select(p => p.TypeName), System.StringComparer.Ordinal);

        foreach (var genericParam in method.GenericParameters)
        {
            // The constrained slot must be one the PARENT introduces (τ_0_*), not a method-own slot.
            // GenericParameterDecl.TypeName is the canonical slot name (e.g. "Base" / "τ_0_0").
            bool isParentSlot = parentSlotNames.Contains(genericParam.TypeName)
                || IsDepthZeroCanonical(genericParam.TypeName);
            if (!isParentSlot) continue;

            // v1 accepts a slot bound by EXACTLY ONE constraint — the class anchor. If the slot
            // carries additional constraints (`where Base: Anchor, Base: SomeProtocol`), the
            // concrete anchor may not itself satisfy them, so the extension method would not be
            // available on `Wrapper<Anchor>` and the forwarded Swift call would not compile.
            // Decline rather than emit an uncompilable wrapper.
            if (genericParam.GenericConformances.Count != 1) continue;

            foreach (var conformance in genericParam.GenericConformances)
            {
                if (typeDatabase.TryGetTypeRecord(conformance.ConformanceTarget, out var record)
                    && record.Kind == TypeRecordKind.Class
                    && (conformance.ConformanceTarget.Module == (parent.ModuleDecl?.Name ?? "")
                        || record.Flags.HasFlag(TypeRecordFlags.ObjCBridged)))
                {
                    return conformance.ConformanceTarget;
                }
            }
        }
        return null;
    }

    // ─────────────────────────────── Per-method emission ───────────────────────────────

    private static void TryEmitOne(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        Plan plan,
        ClosureHandler closureHandler,
        ModuleDecl moduleDecl,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        ILogger logger)
    {
        var method = plan.Method;
        var moduleName = plan.Parent.SwiftTypeName.Module;
        var concreteShort = LastSegment(plan.ConcreteAnchor.ModuleQualifiedName);

        // Overload- and anchor-safe symbol: the deterministic hash folds in the method's mangled name,
        // which is distinct per concrete-anchor extension and per overload (H2).
        var hashInput = $"{moduleName}.{plan.ClosedSwiftType}|{method.MangledName}";
        var hash = EmitterUtility.DeterministicHash8(hashInput);
        var cdeclSymbol = $"SBW_CCC_{moduleName}_{plan.Parent.Name}_{concreteShort}_{method.Name}_{hash}";
        var pinvokeName = $"PInvoke_{plan.Parent.Name}_{concreteShort}_{NameProvider.ToPascalCase(method.Name)}_{hash}";

        // Session-04 wrapper-symbol integrity: claim the structural identity (parent<concrete> + method +
        // overload-distinct source key) before emitting anything. A collision skips this specialization
        // rather than shipping a P/Invoke that references a doubly-claimed symbol.
        var paramSig = string.Join("|", plan.Params.Select(p =>
            p.IsClosure ? $"c:{ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(p.Arg.SwiftTypeSpec)}"
                        : $"v:{p.SwiftScalar}"));
        var sourceKey = $"closed-constrained-closure::method::{plan.Parent.SwiftTypeName.ModuleQualifiedName}::{plan.ConcreteAnchor.ModuleQualifiedName}::instance::{method.Name}::{paramSig}";
        if (!emissionContext.TryClaimWrapperSymbol(plan.ClosedSwiftType, method.Name, sourceKey, cdeclSymbol, DeclIdFactory.ForMethod(method)))
        {
            logger.LogDebug(
                "ClosedConstrainedClosure: identity already claimed for {Type}.{Method} — skipping duplicate specialization.",
                plan.ClosedSwiftType, method.Name);
            return;
        }

        var mergedAvailability = WrapperEmitterHelpers.MergeAvailability(method.AvailabilityAnnotations, plan.Parent);
        bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
            plan.Parent, method.IsMainActorIsolated, method.IsNonisolated);

        EmitSwiftWrapper(swiftWriter, plan, closureHandler, cdeclSymbol, needsMainActor, mergedAvailability, emissionContext, moduleName);
        EmitCSharpSide(csWriter, plan, closureHandler, cdeclSymbol, pinvokeName, method.MangledName, mergedAvailability, typeDatabase, emissionContext);
    }

    /// <summary>Emits the non-generic <c>@_cdecl</c> Swift wrapper over the concrete closed receiver.</summary>
    private static void EmitSwiftWrapper(
        SwiftWriter swiftWriter,
        Plan plan,
        ClosureHandler closureHandler,
        string cdeclSymbol,
        bool needsMainActor,
        IReadOnlyList<AvailabilityAnnotation>? mergedAvailability,
        ModuleEmissionContext emissionContext,
        string moduleName)
    {
        var swiftParams = new List<string>();
        foreach (var p in plan.Params)
        {
            if (p.IsClosure)
            {
                swiftParams.Add($"_ {p.Identifier}FuncPtr: UnsafeMutableRawPointer?");
                swiftParams.Add($"_ {p.Identifier}Context: UnsafeMutableRawPointer?");
            }
            else
            {
                swiftParams.Add($"_ {p.Identifier}: {p.SwiftScalar}");
            }
        }
        swiftParams.Add("_ self_: UnsafeMutableRawPointer");

        swiftWriter.WriteLine();
        swiftWriter.WriteLine($"// Closed constrained-extension closure wrapper: {plan.ClosedSwiftType}.{plan.Method.Name}");
        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, cdeclSymbol, needsMainActor, mergedAvailability);
        swiftWriter.WriteLine($"public func {cdeclSymbol}({string.Join(", ", swiftParams)}) {{");

        // Build the closure adapters (_adapted_{name}) via the shared closure-adapter routine.
        foreach (var p in plan.Params.Where(p => p.IsClosure))
        {
            var adapterLines = ClosureEmitter.GetSwiftClosureAdapterCode(
                p.Identifier, p.Closure!, closureHandler, isOptional: false, isEscaping: true,
                swiftWriter: swiftWriter, ctx: emissionContext, moduleName: moduleName);
            foreach (var line in adapterLines)
                swiftWriter.WriteLine($"    {line}");
        }

        swiftWriter.WriteLine($"    let obj = Unmanaged<{plan.ClosedSwiftType}>.fromOpaque(self_).takeUnretainedValue()");

        var callArgs = new List<string>();
        foreach (var p in plan.Params)
        {
            var value = p.IsClosure ? $"_adapted_{p.Identifier}" : p.Identifier;
            callArgs.Add(p.CallLabel == null ? value : $"{p.CallLabel}: {value}");
        }
        var swiftCallName = NameProvider.ParserNameToSwift(plan.Method);
        swiftWriter.WriteLine($"    obj.{swiftCallName}({string.Join(", ", callArgs)})");
        swiftWriter.WriteLine("}");
    }

    /// <summary>Emits the closure trampolines, funcptr fields, Cdecl P/Invoke, and the public C# extension method.</summary>
    private static void EmitCSharpSide(
        CSharpWriter csWriter,
        Plan plan,
        ClosureHandler closureHandler,
        string cdeclSymbol,
        string pinvokeName,
        string mangledName,
        IReadOnlyList<AvailabilityAnnotation>? mergedAvailability,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext)
    {
        var method = plan.Method;
        var csMethodName = NameProvider.ToPascalCase(method.Name);
        var wrapperLibPath = typeDatabase.AsyncLibraryName ?? "libSwiftBindings";

        // --- Per-closure: funcptr field + [UnmanagedCallersOnly] trampoline (both non-generic → no CS7042) ---
        foreach (var p in plan.Params.Where(p => p.IsClosure))
        {
            csWriter.WriteLine();
            ClosureEmitter.EmitClosureCallbackPointer(
                csWriter, csMethodName, p.Identifier, p.Closure!, closureHandler, mangledName, useCdecl: true);
            ClosureEmitter.EmitEscapingClosureCallback(
                csWriter, csMethodName, p.Identifier, p.Closure!, closureHandler, mangledName, useCdecl: true, useBoxedContext: false);
        }

        // --- Cdecl P/Invoke declaration (params in the SAME order as the Swift wrapper) ---
        var pinvokeParams = new List<string>();
        foreach (var p in plan.Params)
        {
            if (p.IsClosure)
            {
                pinvokeParams.Add($"IntPtr {p.Identifier}FuncPtr");
                pinvokeParams.Add($"IntPtr {p.Identifier}Context");
            }
            else
            {
                pinvokeParams.Add($"{p.CSharpScalar} {p.Identifier}");
            }
        }
        pinvokeParams.Add("IntPtr _self");

        csWriter.WriteLine();
        AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(
            csWriter, mergedAvailability, plan.Parent.AvailabilityAnnotations);
        PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
        {
            LibraryPath = wrapperLibPath,
            EntryPoint = cdeclSymbol,
            MethodName = pinvokeName,
            ReturnType = "void",
            ParametersString = string.Join(", ", pinvokeParams),
            CallingConvention = PInvokeCallingConvention.Cdecl,
            Visibility = PInvokeVisibility.Internal,
            EmissionContext = emissionContext,
            EnforceWrapperContract = true
        });

        // --- Public C# extension method ---
        var sigParams = new List<string> { $"this {plan.ClosedCSharpType} self" };
        foreach (var p in plan.Params)
        {
            sigParams.Add(p.IsClosure
                ? $"{closureHandler.GetCSharpDelegateType(p.Closure!)} {p.Identifier}"
                : $"{p.CSharpScalar} {p.Identifier}");
        }

        csWriter.WriteLine();
        AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(
            csWriter, mergedAvailability, plan.Parent.AvailabilityAnnotations);
        csWriter.WriteLine($"/// <summary>Constrained-extension method on {plan.Parent.Name}&lt;{LastSegment(plan.ConcreteAnchor.ModuleQualifiedName)}&gt; (escaping closure).</summary>");
        csWriter.WriteLine($"public static void {csMethodName}({string.Join(", ", sigParams)})");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        AvailabilityAttributeEmitter.EmitRuntimeAvailabilityGuard(
            csWriter, mergedAvailability, $"{plan.Parent.Name}.{csMethodName}");

        var closures = plan.Params.Where(p => p.IsClosure).ToList();
        // GCHandle bookkeeping — escaping closures transfer ownership to the Swift box on success.
        foreach (var p in closures)
        {
            csWriter.WriteLine($"global::System.Runtime.InteropServices.GCHandle {p.Identifier}Handle = default;");
            csWriter.WriteLine($"bool {p.Identifier}Transferred = false;");
        }
        csWriter.WriteLine("try");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        foreach (var p in closures)
            csWriter.WriteLine($"{p.Identifier}Handle = global::System.Runtime.InteropServices.GCHandle.Alloc({p.Identifier});");

        var callArgs = new List<string>();
        foreach (var p in plan.Params)
        {
            if (p.IsClosure)
            {
                var callbackName = ClosureHandler.GetCallbackFunctionName(csMethodName, p.Identifier, mangledName);
                callArgs.Add($"{p.Identifier}Handle.IsAllocated ? (IntPtr)s_{callbackName} : IntPtr.Zero");
                callArgs.Add($"{p.Identifier}Handle.IsAllocated ? global::System.Runtime.InteropServices.GCHandle.ToIntPtr({p.Identifier}Handle) : IntPtr.Zero");
            }
            else
            {
                callArgs.Add(p.Identifier);
            }
        }
        // The live `self` reference keeps the handle valid across the synchronous P/Invoke (matches the
        // sync CSM / SCP extension convention — no AddRef/Release needed).
        callArgs.Add("((global::Swift.Runtime.ISwiftObject)self).SwiftHandle");
        csWriter.WriteLine($"{pinvokeName}({string.Join(", ", callArgs)});");
        foreach (var p in closures)
            csWriter.WriteLine($"{p.Identifier}Transferred = true;");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine("finally");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        foreach (var p in closures)
            csWriter.WriteLine($"if (!{p.Identifier}Transferred && {p.Identifier}Handle.IsAllocated) {p.Identifier}Handle.Free();");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.Indent--;
        csWriter.WriteLine("}");
    }

    // ─────────────────────────────── Helpers ───────────────────────────────

    private static bool HasMethodOwnGenerics(MethodDecl method)
        => method.GenericParameters.Any(gp => TryGetGenericDepth(gp.TypeName, out var d) && d >= 1);

    private static bool IsDepthZeroCanonical(string typeName)
        => TryGetGenericDepth(typeName, out var d) && d == 0;

    private static bool TryGetGenericDepth(string typeName, out int depth)
    {
        depth = 0;
        var parts = typeName.Split('_');
        return parts.Length >= 3 && int.TryParse(parts[1], out depth);
    }

    private static bool TryResolveScalar(TypeSpec spec, out string swiftScalar, out string csScalar)
    {
        swiftScalar = "";
        csScalar = "";
        if (spec is not NamedTypeSpec named) return false;
        if (named.GenericParameters.Count > 0) return false;
        var name = LastSegment(named.Name);
        if (!PrimitiveScalars.TryGetValue(name, out var pair)) return false;
        swiftScalar = pair.Swift;
        csScalar = pair.CSharp;
        return true;
    }

    private static string LastSegment(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name.Substring(dot + 1) : name;
    }

    /// <summary>Fully-qualified C# name of the open generic parent, without its type-parameter list.</summary>
    private static string QualifyParentCSharp(ClassDecl parent, ITypeDatabase typeDatabase)
    {
        if (typeDatabase.TryGetTypeRecord(parent.SwiftTypeName, out var record))
            return record.CSharpTypeName.FullyQualifiedName;
        return $"global::{parent.SwiftTypeName.ModuleQualifiedName}";
    }
}
