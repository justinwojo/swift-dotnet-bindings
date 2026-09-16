// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>Carrier-backed constraint proofs for the bounded B1 method-generic routes.</summary>
internal static partial class MethodLevelGenericWrapperEmitter
{
    private static void EmitCarrierBackedWrapper(
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        ModuleEmissionContext ctx,
        string symbolName,
        MlgOpenedGeneric opened,
        MlgLocalNames localNames,
        string refusalParam,
        IReadOnlyList<string> swiftParams,
        IReadOnlyList<string> carrierParams,
        IReadOnlyList<string> carrierCallArgs,
        IReadOnlyList<string> reconstructionLines,
        IReadOnlyList<MlgPayloadBinding> payloadBindings,
        string innerCallExpr,
        string moduleQualifiedSwiftName,
        bool isClass,
        bool isStatic,
        bool isMutating,
        TypeSpec returnTypeSpec,
        CdeclReturnMapping returnMapping,
        bool needsResultPtr,
        bool isVoidReturn,
        bool isString,
        bool throws)
    {
        var methodDecl = env.MethodDecl;
        var parentTypeDecl = env.ParentDecl as TypeDecl;
        var parentModuleDecl = env.ParentDecl as ModuleDecl;
        var methodHash = EmitterUtility.DeterministicHash8(symbolName);
        var carrierName = $"_SBW_MLG_{methodHash}Carrier";
        var openName = $"_SBW_MLG_{methodHash}Open";
        var outcomeName = $"_SBW_MLG_{methodHash}Outcome";
        var callName = $"_sbw_mlg_call_{methodHash}";
        var swiftFuncName = $"_sbw_method_{methodHash}";
        var returnType = RenderCarrierReturnType(env, returnTypeSpec, isVoidReturn);
        var returnClause = returnType == null ? "" : $" -> {returnType}";
        var throwsClause = throws ? " throws" : "";
        var needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
            parentTypeDecl ?? (BaseDecl?)parentModuleDecl, methodDecl.IsMainActorIsolated, methodDecl.IsNonisolated);
        var mainActorPrefix = needsMainActor ? "@MainActor " : "";
        var mergedAvailability = WrapperEmitterHelpers.MergeAvailability(
            methodDecl.AvailabilityAnnotations, parentTypeDecl);
        var originAnchor = OriginAnchorEmitter.LineForWrapper(methodDecl);

        // MethodWrapperEmitter claimed the symbol in this ModuleEmissionContext before reaching
        // this emitter. The stable symbol hash therefore also deduplicates these private helpers:
        // no second call for the same declaration can emit the same carrier family.
        swiftWriter.WriteLine();
        swiftWriter.WriteLine(originAnchor);
        WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, mergedAvailability);
        swiftWriter.WriteLine($"private enum {outcomeName} {{");
        swiftWriter.Indent++;
        swiftWriter.WriteLine(isVoidReturn ? "case success" : $"case success({returnType})");
        swiftWriter.WriteLine("case refused");
        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");

        swiftWriter.WriteLine(originAnchor);
        WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, mergedAvailability);
        swiftWriter.WriteLine($"private protocol {carrierName} {{");
        swiftWriter.Indent++;
        swiftWriter.WriteLine(
            $"{mainActorPrefix}static func {callName}({string.Join(", ", carrierParams)}){throwsClause}{returnClause}");
        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");

        if (opened.Strategy == MlgOpeningStrategy.AssociatedTypeCarrier)
        {
            var localType = LocalGenericName(opened);
            swiftWriter.WriteLine(originAnchor);
            WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, mergedAvailability);
            swiftWriter.WriteLine($"private enum {openName}<{localType}{opened.ConstraintClause}> {{}}");
            // The conformance body references and calls the wrapped API before control reaches
            // the annotated @_cdecl below. Swift checks this extension independently, so it needs
            // the same availability floor as the method and its parent type.
            swiftWriter.WriteLine(originAnchor);
            WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, mergedAvailability);
            swiftWriter.WriteLine(
                $"extension {openName}: {carrierName} where {localType}.{opened.ConditionalRequirement!.MemberPath}: "
                + opened.ConditionalRequirement.Target + " {");
            EmitCarrierCallImplementation(
                swiftWriter, env, callName, carrierParams, reconstructionLines, payloadBindings,
                innerCallExpr, moduleQualifiedSwiftName, isClass, isStatic, isMutating, throws,
                isVoidReturn, localType, needsMainActor);
            swiftWriter.WriteLine("}");
        }
        else
        {
            swiftWriter.WriteLine(originAnchor);
            WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, mergedAvailability);
            swiftWriter.WriteLine($"extension {opened.SuperclassTarget}: {carrierName} {{");
            EmitCarrierCallImplementation(
                swiftWriter, env, callName, carrierParams, reconstructionLines, payloadBindings,
                innerCallExpr, moduleQualifiedSwiftName, isClass, isStatic, isMutating, throws,
                isVoidReturn, "Self", needsMainActor);
            swiftWriter.WriteLine("}");
        }

        var wrapperTarget = string.IsNullOrEmpty(moduleQualifiedSwiftName)
            ? $"free function {methodDecl.Name}"
            : $"{moduleQualifiedSwiftName}.{methodDecl.Name}";
        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Method-level generic carrier @_cdecl wrapper for {{wrapperTarget}}.
            // Every runtime constraint is proved before receiver or payload reconstruction.
            """);
        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, symbolName, needsMainActor,
            mergedAvailability);

        var cdeclReturnClause = isVoidReturn || needsResultPtr ? "" : $" -> {returnMapping.CdeclReturnType}";
        swiftWriter.WriteLine($"public func {swiftFuncName}({string.Join(", ", swiftParams)}){cdeclReturnClause} {{");
        swiftWriter.Indent++;
        swiftWriter.WriteLine($"{refusalParam}.pointee = 0");
        swiftWriter.WriteLine(
            $"let {localNames.AnyType[opened.Ordinal]} = unsafeBitCast({localNames.Metadata[opened.Ordinal]}, to: Any.Type.self)");

        string outcomeExpression;
        if (opened.Strategy == MlgOpeningStrategy.AssociatedTypeCarrier)
        {
            EmitRootGuard(swiftWriter, opened, localNames, refusalParam, returnMapping, needsResultPtr, isVoidReturn);
            var localType = LocalGenericName(opened);
            var bodyName = localNames.Body[0];
            swiftWriter.WriteLine(
                $"func {bodyName}<{localType}{opened.ConstraintClause}>(_: {localType}.Type){throwsClause} -> {outcomeName} {{");
            swiftWriter.Indent++;
            swiftWriter.WriteLine(
                $"guard let carrier = {openName}<{localType}>.self as? any {carrierName}.Type else {{ return .refused }}");
            var carrierCall = $"carrier.{callName}({string.Join(", ", carrierCallArgs)})";
            if (throws)
                carrierCall = "try " + carrierCall;
            swiftWriter.WriteLine(isVoidReturn
                ? $"{carrierCall}; return .success"
                : $"return .success({carrierCall})");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
            outcomeExpression = $"{bodyName}({localNames.OpenedType[opened.Ordinal]})";
        }
        else
        {
            swiftWriter.WriteLine(
                $"guard let carrier = {localNames.AnyType[opened.Ordinal]} as? any {carrierName}.Type else {{");
            swiftWriter.Indent++;
            WriteRefusalReturn(swiftWriter, opened.Ordinal, refusalParam, returnMapping, needsResultPtr, isVoidReturn);
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
            var carrierCall = $"carrier.{callName}({string.Join(", ", carrierCallArgs)})";
            outcomeExpression = isVoidReturn
                ? carrierCall
                : $"{outcomeName}.success({carrierCall})";
        }

        if (throws)
        {
            ThrowingWrapperErrorContractEmitter.EmitInitialization(swiftWriter);
            swiftWriter.WriteLine("do {");
            swiftWriter.Indent++;
            if (opened.Strategy == MlgOpeningStrategy.SuperclassCarrier && isVoidReturn)
            {
                swiftWriter.WriteLine($"try {outcomeExpression}");
            }
            else
            {
                EmitOutcomeSwitch(swiftWriter, "try " + outcomeExpression, outcomeName, opened.Ordinal,
                    refusalParam, returnTypeSpec, returnMapping, needsResultPtr, isVoidReturn, isString,
                    env.TypeDatabase);
            }
            swiftWriter.Indent--;
            swiftWriter.WriteLine("} catch {");
            swiftWriter.Indent++;
            swiftWriter.WriteLine("errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()");
            if (!isVoidReturn && !needsResultPtr)
                CdeclReturnRenderer.WriteErrorSentinel(swiftWriter, returnMapping);
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
        }
        else
        {
            if (opened.Strategy == MlgOpeningStrategy.SuperclassCarrier && isVoidReturn)
            {
                swiftWriter.WriteLine(outcomeExpression);
            }
            else
            {
                EmitOutcomeSwitch(swiftWriter, outcomeExpression, outcomeName, opened.Ordinal,
                    refusalParam, returnTypeSpec, returnMapping, needsResultPtr, isVoidReturn, isString,
                    env.TypeDatabase);
            }
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    private static void EmitCarrierCallImplementation(
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        string callName,
        IReadOnlyList<string> carrierParams,
        IReadOnlyList<string> reconstructionLines,
        IReadOnlyList<MlgPayloadBinding> payloadBindings,
        string innerCallExpr,
        string moduleQualifiedSwiftName,
        bool isClass,
        bool isStatic,
        bool isMutating,
        bool throws,
        bool isVoidReturn,
        string payloadType,
        bool needsMainActor)
    {
        var returnClause = ReturnClauseFor(env, env.MethodDecl.CSSignature.First().SwiftTypeSpec, isVoidReturn);
        if (!isVoidReturn && MarshallingHelpers.IsLocalizedStringResource(env.MethodDecl.CSSignature.First().SwiftTypeSpec))
            returnClause = " -> Swift.String";
        swiftWriter.Indent++;
        swiftWriter.WriteLine(
            $"{(needsMainActor ? "@MainActor " : "")}static func {callName}({string.Join(", ", carrierParams)}){(throws ? " throws" : "")}{returnClause} {{");
        swiftWriter.Indent++;
        foreach (var line in reconstructionLines)
            swiftWriter.WriteLine(line);
        if (!isStatic)
            MethodWrapperEmitter.EmitSelfReconstruction(swiftWriter, isClass, isMutating, moduleQualifiedSwiftName);
        foreach (var payload in payloadBindings)
            swiftWriter.WriteLine(payload.Render(payloadType));
        var call = throws ? $"try {innerCallExpr}" : innerCallExpr;
        swiftWriter.WriteLine(isVoidReturn ? call : $"return {call}");
        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
        swiftWriter.Indent--;
    }

    private static void EmitRootGuard(
        SwiftWriter swiftWriter,
        MlgOpenedGeneric opened,
        MlgLocalNames localNames,
        string refusalParam,
        CdeclReturnMapping returnMapping,
        bool needsResultPtr,
        bool isVoidReturn)
    {
        swiftWriter.WriteLine(
            $"guard let {localNames.OpenedType[opened.Ordinal]} = {localNames.AnyType[opened.Ordinal]} as? {opened.ExistentialMetatype} else {{");
        swiftWriter.Indent++;
        WriteRefusalReturn(swiftWriter, opened.Ordinal, refusalParam, returnMapping, needsResultPtr, isVoidReturn);
        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    private static void EmitOutcomeSwitch(
        SwiftWriter swiftWriter,
        string expression,
        string outcomeName,
        int ordinal,
        string refusalParam,
        TypeSpec returnTypeSpec,
        CdeclReturnMapping returnMapping,
        bool needsResultPtr,
        bool isVoidReturn,
        bool isString,
        ITypeDatabase typeDatabase)
    {
        swiftWriter.WriteLine($"switch {expression} {{");
        swiftWriter.WriteLine("case .refused:");
        swiftWriter.Indent++;
        WriteRefusalReturn(swiftWriter, ordinal, refusalParam, returnMapping, needsResultPtr, isVoidReturn);
        swiftWriter.Indent--;
        swiftWriter.WriteLine(isVoidReturn ? "case .success:" : "case .success(let result):");
        swiftWriter.Indent++;
        if (isVoidReturn)
        {
            swiftWriter.WriteLine("return");
        }
        else if (isString)
        {
            MethodWrapperEmitter.EmitStringReturnBody(swiftWriter, "result");
        }
        else if (needsResultPtr)
        {
            swiftWriter.WriteLine(
                $"resultPtr.initializeMemory(as: {MethodWrapperEmitter.RenderIndirectResultMetatype(returnTypeSpec)}, repeating: result, count: 1)");
        }
        else
        {
            MethodWrapperEmitter.EmitDirectReturn(swiftWriter, "result", returnTypeSpec, typeDatabase, returnMapping);
        }
        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    private static void WriteRefusalReturn(
        SwiftWriter swiftWriter,
        int ordinal,
        string refusalParam,
        CdeclReturnMapping returnMapping,
        bool needsResultPtr,
        bool isVoidReturn)
    {
        swiftWriter.WriteLine($"{refusalParam}.pointee = {ordinal + 1}");
        if (!isVoidReturn && !needsResultPtr)
            CdeclReturnRenderer.WriteErrorSentinel(swiftWriter, returnMapping);
        else
            swiftWriter.WriteLine("return");
    }

    private static string? RenderCarrierReturnType(
        MethodEnvironment env,
        TypeSpec returnTypeSpec,
        bool isVoidReturn)
    {
        if (isVoidReturn)
            return null;
        if (MarshallingHelpers.IsLocalizedStringResource(returnTypeSpec))
            return "Swift.String";
        return ReturnClauseFor(env, returnTypeSpec, false)[4..];
    }
}
