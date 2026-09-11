// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits the free <c>@_cdecl</c> opening wrapper for a member that declares its own generic
/// parameters.
///
/// <para>
/// A <c>@_cdecl</c> function cannot carry generic context, so this wrapper takes the method's
/// type-argument metadata as ordinary pointers — the value the C# generic arm already computes as
/// <c>T0Metadata.Handle</c> — casts each one to the constraint's existential metatype and re-enters
/// the generic context through a local generic function (SE-0352 implicit existential opening).
/// Several parameters nest, one level per opened parameter. Once inside, the payload pointer binds
/// to the opened type and the real Swift method is called normally.
/// </para>
///
/// <para>
/// The cast is the conformance check, and it runs before the payload pointer is dereferenced: a
/// type argument whose Swift metadata does not conform is refused rather than reinterpreted. The
/// refusal is reported through its own out-parameter, ordinal-encoded (0 = success, N = the N-th
/// generic parameter refused), so it stays distinct from the C return channel and from
/// <c>errorOut</c>. <see cref="MethodLevelGenericOpening"/> decides which shapes qualify.
/// </para>
/// </summary>
internal static class MethodLevelGenericWrapperEmitter
{
    /// <summary>The Swift binding for the refusal out-pointer, and its C# P/Invoke parameter name.</summary>
    internal const string RefusalParameterName = "_openRefused";

    /// <summary>
    /// Emits the opening wrapper. The caller has already claimed the wrapper symbol and resolved
    /// the parent, so this writes Swift only.
    /// </summary>
    internal static void Emit(
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        ModuleEmissionContext ctx,
        string symbolName,
        IReadOnlyList<MlgOpenedGeneric> opened)
    {
        var methodDecl = env.MethodDecl;
        var parentTypeDecl = env.ParentDecl as TypeDecl;
        var parentModuleDecl = env.ParentDecl as ModuleDecl;
        var moduleName = parentTypeDecl?.SwiftTypeName.Module ?? parentModuleDecl!.Name;
        var moduleQualifiedSwiftName = parentTypeDecl?.SwiftTypeName.ModuleQualifiedName ?? "";

        bool isClass = env.ParentDecl is ClassDecl;
        bool isStatic = methodDecl.MethodType == MethodType.Static || parentTypeDecl == null;
        bool isMutating = methodDecl.IsMutating;
        bool throws = methodDecl.Throws;

        var returnTypeSpec = methodDecl.CSSignature.First().SwiftTypeSpec;
        bool isVoidReturn = returnTypeSpec.IsEmptyTuple;
        bool isLsrReturn = !isVoidReturn && MarshallingHelpers.IsLocalizedStringResource(returnTypeSpec);
        bool isString = !isVoidReturn && (WitnessDispatchEmitter.IsStringType(returnTypeSpec) || isLsrReturn);

        var (returnMapping, needsResultPtr) = isVoidReturn
            ? (new CdeclReturnMapping("Void", CdeclReturnKind.Direct), false)
            : CdeclReturnMapping.Classify(returnTypeSpec, env.TypeDatabase);
        if (isString)
            needsResultPtr = true;

        if (isString)
        {
            Utf8SliceEmitter.EmitIfNeeded(swiftWriter, ctx);
            Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter, moduleName, ctx);
        }

        // The opened parameters, indexed by the parser's generic-parameter name, so a payload
        // argument can find the local generic parameter it binds to.
        var openedByName = opened.ToDictionary(o => o.SwiftName, StringComparer.Ordinal);

        var swiftParams = new List<string>();
        var reconstructionLines = new List<string>();
        var payloadBindings = new List<string>();
        var callArgs = new List<string>();
        var keptArgs = methodDecl.CSSignature.Skip(1).ToList();
        var siblings = CdeclParamMapper.CollectSiblingBindingNames(keptArgs);

        // Every binding emitted into one @_cdecl scope — parameters and body locals alike — has to
        // be unique or swiftc rejects the whole function, and a rejected wrapper is dropped from
        // the compiled dylib rather than failing loudly. The names below are the ones this emitter
        // synthesises, so they are what moves when a user parameter already spells one; the user's
        // own binding is left alone, matching the escape policy the shared wrapper path uses.
        // Seeding the scope with the user bindings keeps the common case byte-identical, since
        // Reserve returns the desired name whenever it is free. Allocating all of them here — not
        // at each emission site — is what stops the signature and the nested bodies from drifting
        // onto different spellings of the same local.
        var names = new SyntheticNameScope(siblings);
        var refusalParam = names.Reserve(RefusalParameterName);
        var localNames = new MlgLocalNames(
            Metadata: opened.ToDictionary(o => o.Ordinal, o => names.Reserve($"_metadata{o.Ordinal}")),
            AnyType: opened.ToDictionary(o => o.Ordinal, o => names.Reserve($"_mlgAny{o.Ordinal}")),
            OpenedType: opened.ToDictionary(o => o.Ordinal, o => names.Reserve($"_mlgType{o.Ordinal}")),
            Body: Enumerable.Range(0, opened.Count).ToDictionary(d => d, d => names.Reserve($"_mlgBody{d}")));

        if (needsResultPtr)
            swiftParams.Add("_ resultPtr: UnsafeMutableRawPointer");

        var order = CdeclSignatureContract.DetermineParameterOrder(env, overrideNeedsResultPtr: needsResultPtr);
        foreach (var phase in order.Phases)
        {
            switch (phase)
            {
                case CdeclPhase.ResultPtr:
                    break;

                case CdeclPhase.ErrorOut:
                    swiftParams.Add("_ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>");
                    break;

                case CdeclPhase.OpenRefusal:
                    swiftParams.Add($"_ {refusalParam}: UnsafeMutablePointer<UInt8>");
                    break;

                case CdeclPhase.Self:
                    swiftParams.Add(isClass || isMutating
                        ? "_ self_: UnsafeMutableRawPointer"
                        : "_ self_: UnsafeRawPointer");
                    break;

                case CdeclPhase.Metadata:
                    // One metadata pointer per generic parameter, in declaration order — the same
                    // order PInvokeEmitter.HandleGenericMetadata walks. Protocol witness tables are
                    // suppressed on this route on BOTH sides (the opened cast is the conformance
                    // check), so no _pwt slots appear here.
                    foreach (var og in opened)
                        swiftParams.Add($"_ {localNames.Metadata[og.Ordinal]}: UnsafeRawPointer");
                    break;

                case CdeclPhase.Arguments:
                    for (int i = 0; i < keptArgs.Count; i++)
                    {
                        var arg = keptArgs[i];
                        if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                            continue;

                        // A Void parameter carries no bytes, so it contributes no @_cdecl ABI slot
                        // on either side — PInvokeEmitter.HandleArguments skips it too, and a slot
                        // only this side declared would shift every argument after it. Swift still
                        // requires the argument at the inner call, so forward the unique Void value.
                        if (arg.SwiftTypeSpec.IsEmptyTuple)
                        {
                            callArgs.Add($"{CdeclParamMapper.BuildSwiftCallArgLabel(arg)}()");
                            continue;
                        }

                        var label = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
                        if (label == "_")
                            label = $"arg{i}";

                        // A parameter whose type IS one of the opened generic parameters crosses as
                        // a raw pointer and binds to the opened type inside the innermost body,
                        // where that type is in scope.
                        if (arg.SwiftTypeSpec is NamedTypeSpec named
                            && named.GenericParameters.Count == 0
                            && openedByName.TryGetValue(named.Name, out var payloadGeneric))
                        {
                            var binding = CdeclParamMapper.BuildSwiftBindingName(label, siblings);
                            var payloadParam = names.Reserve($"{binding}Payload");
                            swiftParams.Add($"_ {payloadParam}: UnsafeRawPointer");
                            var localGenericName = LocalGenericName(payloadGeneric);
                            payloadBindings.Add(
                                $"let {binding} = {payloadParam}.assumingMemoryBound(to: {localGenericName}.self).pointee");
                            callArgs.Add($"{CdeclParamMapper.BuildSwiftCallArgLabel(arg)}{binding}");
                            continue;
                        }

                        var (cdeclParam, reconstruction, callArg) =
                            CdeclParamMapper.Map(arg, label, env, reservedSiblings: siblings);
                        swiftParams.Add(cdeclParam);
                        if (reconstruction != null)
                            reconstructionLines.Add(reconstruction);
                        callArgs.Add(callArg);
                    }
                    break;
            }
        }

        string returnClause = isVoidReturn || needsResultPtr ? "" : $" -> {returnMapping.CdeclReturnType}";
        var swiftFuncName = $"_sbw_method_{EmitterUtility.DeterministicHash8(symbolName)}";

        // The call on the real Swift method, made from inside the innermost opened body.
        string selfRef;
        if (isStatic && parentTypeDecl != null)
            selfRef = moduleQualifiedSwiftName;
        else if (isStatic)
            selfRef = "";
        else if (isMutating && !isClass)
            selfRef = $"self_.assumingMemoryBound(to: {moduleQualifiedSwiftName}.self).pointee";
        else
            selfRef = "obj";

        var swiftMethodName = NameProvider.ParserNameToSwift(methodDecl);
        var callArgString = string.Join(", ", callArgs);
        var innerCallExpr = string.IsNullOrEmpty(selfRef)
            ? $"{swiftMethodName}({callArgString})"
            : $"{selfRef}.{swiftMethodName}({callArgString})";
        if (isLsrReturn)
            innerCallExpr = $"String(localized: {innerCallExpr})";

        swiftWriter.WriteLine();
        var wrapperTarget = string.IsNullOrEmpty(moduleQualifiedSwiftName)
            ? $"free function {methodDecl.Name}"
            : $"{moduleQualifiedSwiftName}.{methodDecl.Name}";
        swiftWriter.WriteLines($$"""
            // Method-level generic opening @_cdecl wrapper for {{wrapperTarget}}.
            // Takes the method's own type-argument metadata as plain pointers and re-enters the
            // generic context by opening them, so self crosses as an ordinary pointer.
            """);

        bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
            parentTypeDecl ?? (BaseDecl?)parentModuleDecl, methodDecl.IsMainActorIsolated, methodDecl.IsNonisolated);
        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, symbolName, needsMainActor,
            WrapperEmitterHelpers.MergeAvailability(methodDecl.AvailabilityAnnotations, parentTypeDecl));

        swiftWriter.WriteLine($"public func {swiftFuncName}({string.Join(", ", swiftParams)}){returnClause} {{");
        swiftWriter.Indent++;

        swiftWriter.WriteLine($"{refusalParam}.pointee = 0");

        // Cast each metadata pointer to its existential metatype BEFORE anything reads a payload.
        // A refusal here leaves every payload untouched.
        foreach (var og in opened)
        {
            swiftWriter.WriteLine(
                $"let {localNames.AnyType[og.Ordinal]} = unsafeBitCast({localNames.Metadata[og.Ordinal]}, to: Any.Type.self)");
            if (og.ConstraintTargets.Count == 0)
                continue;

            swiftWriter.WriteLine(
                $"guard let {localNames.OpenedType[og.Ordinal]} = {localNames.AnyType[og.Ordinal]} as? {og.ExistentialMetatype} else {{");
            swiftWriter.Indent++;
            swiftWriter.WriteLine($"{refusalParam}.pointee = {og.Ordinal + 1}");
            if (!isVoidReturn && !needsResultPtr)
                CdeclReturnRenderer.WriteErrorSentinel(swiftWriter, returnMapping);
            else
                swiftWriter.WriteLine("return");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
        }

        foreach (var line in reconstructionLines)
            swiftWriter.WriteLine(line);

        if (!isStatic)
            MethodWrapperEmitter.EmitSelfReconstruction(swiftWriter, isClass, isMutating, moduleQualifiedSwiftName);

        // Nested local generic functions, outermost first. Each one opens exactly one parameter;
        // the innermost holds the payload bindings and the real call.
        EmitOpenedBodies(swiftWriter, env, opened, localNames, 0, payloadBindings, innerCallExpr,
            returnTypeSpec, returnMapping, needsResultPtr, isVoidReturn, isString, throws);

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    /// <summary>
    /// Writes the local generic function for <paramref name="depth"/> and everything under it, then
    /// the call that opens it. At the innermost level the result handling is the shared
    /// <see cref="MethodWrapperEmitter"/> body emission, driven by a rewritten call expression —
    /// so throwing, string, indirect-result and direct returns behave exactly as on the
    /// non-generic wrapper route.
    /// </summary>
    private static void EmitOpenedBodies(
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        IReadOnlyList<MlgOpenedGeneric> opened,
        MlgLocalNames localNames,
        int depth,
        IReadOnlyList<string> payloadBindings,
        string innerCallExpr,
        TypeSpec returnTypeSpec,
        CdeclReturnMapping returnMapping,
        bool needsResultPtr,
        bool isVoidReturn,
        bool isString,
        bool throws)
    {
        var og = opened[depth];
        var bodyName = localNames.Body[depth];
        var localName = LocalGenericName(og);
        var throwsClause = throws ? " throws" : "";

        // The innermost body returns the Swift value; the outer levels forward it. The result is
        // only handled once, at the @_cdecl level, by the shared body emitters.
        var bodyReturn = ReturnClauseFor(env, returnTypeSpec, isVoidReturn);

        swiftWriter.WriteLine(
            $"func {bodyName}<{localName}{og.ConstraintClause}>(_: {localName}.Type){throwsClause}{bodyReturn} {{");
        swiftWriter.Indent++;

        if (depth + 1 < opened.Count)
        {
            EmitOpenedBodies(swiftWriter, env, opened, localNames, depth + 1, payloadBindings, innerCallExpr,
                returnTypeSpec, returnMapping, needsResultPtr, isVoidReturn, isString, throws);
        }
        else
        {
            foreach (var binding in payloadBindings)
                swiftWriter.WriteLine(binding);
            var call = throws ? $"try {innerCallExpr}" : innerCallExpr;
            swiftWriter.WriteLine(isVoidReturn ? call : $"return {call}");
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");

        // Open this level. An unconstrained parameter has no existential metatype to cast to, so it
        // rides _openExistential; a constrained one was already narrowed by the guard above.
        var opener = og.ConstraintTargets.Count == 0
            ? $"_openExistential({localNames.AnyType[og.Ordinal]}, do: {bodyName})"
            : $"{bodyName}({localNames.OpenedType[og.Ordinal]})";

        if (depth > 0)
        {
            // An enclosing body forwards the inner one's result. The inner body carries the same
            // `throws` clause (and `_openExistential` is `rethrows`), so the call needs `try` —
            // only the outermost level hands the expression to the shared body emitters, which
            // add their own.
            var forwarded = throws ? $"try {opener}" : opener;
            swiftWriter.WriteLine(isVoidReturn ? forwarded : $"return {forwarded}");
            return;
        }

        // Outermost level: hand the opener to the shared result-handling emitters as the call
        // expression, so this route reuses the non-generic wrapper's return semantics verbatim.
        if (throws)
        {
            MethodWrapperEmitter.EmitThrowingMethodBody(swiftWriter, opener, returnTypeSpec, returnMapping,
                needsResultPtr, isVoidReturn, isString, env.TypeDatabase);
        }
        else if (isVoidReturn)
        {
            swiftWriter.WriteLine(opener);
        }
        else if (isString)
        {
            MethodWrapperEmitter.EmitStringReturnBody(swiftWriter, opener);
        }
        else if (needsResultPtr)
        {
            swiftWriter.WriteLine($"let result = {opener}");
            swiftWriter.WriteLine(
                $"resultPtr.initializeMemory(as: {MethodWrapperEmitter.RenderIndirectResultMetatype(returnTypeSpec)}, " +
                "repeating: result, count: 1)");
        }
        else
        {
            MethodWrapperEmitter.EmitDirectReturn(swiftWriter, opener, returnTypeSpec, env.TypeDatabase, returnMapping);
        }
    }

    /// <summary>
    /// The Swift return clause for the opened bodies. The bodies return the ORIGINAL Swift value —
    /// result lowering (utf8 slice, indirect buffer, error sentinel) happens once, outside them.
    ///
    /// <para>
    /// <c>Self</c> is the one spelling that cannot be carried through verbatim: the opened bodies are
    /// LOCAL functions inside a free <c>@_cdecl</c>, which has no enclosing type, so swiftc rejects
    /// "local function cannot return 'Self'" and the whole wrapper is withdrawn. It resolves to the
    /// parent type, which is also the static type the inner call actually produces — self was
    /// reconstructed as the concrete parent by <see cref="SelfReconstructionEmitter"/>. A dynamic
    /// <c>Self</c> return is admitted for class parents only (the guard in
    /// <see cref="WrapperValidation"/>), which is what makes the parent name always available here;
    /// the same substitution is why the async free-function wrapper resolves it too.
    /// </para>
    /// </summary>
    private static string ReturnClauseFor(MethodEnvironment env, TypeSpec returnTypeSpec, bool isVoidReturn)
    {
        if (isVoidReturn)
            return "";

        if (returnTypeSpec.HasDynamicSelf)
        {
            // TryBuildPlan asks the same question and declines a shape this cannot spell, so a null
            // here means the plan builder and the renderer have drifted apart. Interpolating the
            // null would write `-> ` and hand swiftc a wrapper it rejects — back to the silent
            // withdrawal this pair exists to prevent — so fail where the drift is, not downstream.
            var selfReturn = TryRenderDynamicSelfReturn(env.ParentDecl, returnTypeSpec)
                ?? throw new InvalidOperationException(
                    $"Opening wrapper for '{env.MethodDecl.Name}' reached emission with a dynamic Self "
                    + "return the renderer cannot spell; MethodLevelGenericOpening.TryBuildPlan should "
                    + "have declined it.");
            return $" -> {selfReturn}";
        }

        return $" -> {ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(returnTypeSpec)}";
    }

    /// <summary>
    /// The parent-type spelling that stands in for a dynamic <c>Self</c> return inside the opened
    /// bodies, or <c>null</c> when this shape cannot be spelled there at all.
    ///
    /// <para>
    /// Bare <c>Self</c> and <c>Optional&lt;Self&gt;</c> are the two shapes the wrapper gates admit —
    /// the same pair <see cref="CdeclReturnMapping.Classify"/> maps to the (optional) class-pointer
    /// return — and the only two that rewrite to a name without rebuilding the spec structurally.
    /// <see cref="MethodLevelGenericOpening.TryBuildPlan"/> asks this question too, so a shape this
    /// cannot spell is declined off the route rather than emitted and then withdrawn.
    /// </para>
    /// </summary>
    internal static string? TryRenderDynamicSelfReturn(BaseDecl? parentDecl, TypeSpec returnTypeSpec)
    {
        // A dynamic Self return is admitted for class parents only (the guard in WrapperValidation),
        // so the parent is always a nominal type whose name can be written here.
        if (parentDecl is not TypeDecl parentTypeDecl)
            return null;

        var parentName = parentTypeDecl.SwiftTypeName.ModuleQualifiedName;
        if (returnTypeSpec.IsDynamicSelf)
            return parentName;
        if (returnTypeSpec is NamedTypeSpec { Name: "Swift.Optional", GenericParameters.Count: 1 } optSelf
            && optSelf.GenericParameters[0].IsDynamicSelf)
            return $"{parentName}?";
        return null;
    }

    /// <summary>
    /// The local generic parameter name introduced by the opened body at this level. This one is a
    /// TYPE name, which Swift keeps in a different namespace from value bindings, so it does not
    /// participate in the parameter-name allocation above.
    /// </summary>
    private static string LocalGenericName(MlgOpenedGeneric og) => $"_MLG{og.Ordinal}";

    /// <summary>
    /// The collision-checked names this wrapper injects into one <c>@_cdecl</c> scope, keyed by
    /// generic-parameter ordinal (<see cref="Metadata"/>, <see cref="AnyType"/>,
    /// <see cref="OpenedType"/>) or by nesting depth (<see cref="Body"/>). Allocated once by the
    /// caller so the signature and the nested bodies cannot spell the same local differently.
    /// </summary>
    /// <param name="Metadata">The <c>@_cdecl</c> metadata pointer parameter for each opened parameter.</param>
    /// <param name="AnyType">The <c>Any.Type</c> local holding the bitcast metadata.</param>
    /// <param name="OpenedType">The narrowed existential metatype local produced by the conformance cast.</param>
    /// <param name="Body">The local generic function opening each nesting level.</param>
    private sealed record MlgLocalNames(
        IReadOnlyDictionary<int, string> Metadata,
        IReadOnlyDictionary<int, string> AnyType,
        IReadOnlyDictionary<int, string> OpenedType,
        IReadOnlyDictionary<int, string> Body);
}
