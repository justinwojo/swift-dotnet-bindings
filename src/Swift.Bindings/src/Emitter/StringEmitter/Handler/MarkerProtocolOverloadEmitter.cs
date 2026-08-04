// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Emits typed convenience overloads for methods whose parameters use marker protocols
    /// (empty protocols with only primitive-type conformers).
    ///
    /// For example, a marker protocol like ConstraintOffsetTarget is an empty protocol with conformances
    /// from Swift.Int, Swift.Double, Swift.Float, etc. Methods like offset(amount: any ConstraintOffsetTarget)
    /// become uncallable from C# because primitives don't implement the interface.
    ///
    /// This emitter generates Swift wrapper functions that accept concrete primitive types and
    /// let Swift handle the existential boxing, plus C# overloads that call those wrappers.
    /// </summary>
    internal static class MarkerProtocolOverloadEmitter
    {
        /// <summary>
        /// Known primitive type mappings: Swift fully-qualified name → C# type.
        /// </summary>
        private static readonly Dictionary<string, string> PrimitiveTypeMap = new()
        {
            ["Swift.Double"] = "double",
            ["Swift.Float"] = "float",
            ["Swift.Int"] = "nint",
            ["Swift.UInt"] = "nuint",
            ["CoreFoundation.CGFloat"] = "nfloat",
            ["Swift.Int32"] = "int",
            ["Swift.Int64"] = "long",
            ["Swift.Bool"] = "bool",
        };

        /// <summary>
        /// Gets the list of C# primitive types that conform to a marker protocol.
        /// </summary>
        public static List<(string CSharpType, string SwiftType)> GetPrimitiveConformers(
            string protocolName,
            Dictionary<string, List<string>>? markerProtocolConformances)
        {
            var result = new List<(string CSharpType, string SwiftType)>();

            if (markerProtocolConformances == null)
                return result;

            if (!markerProtocolConformances.TryGetValue(protocolName, out var conformers))
                return result;

            foreach (var conformer in conformers)
            {
                if (PrimitiveTypeMap.TryGetValue(conformer, out var csType))
                    result.Add((csType, conformer));
            }

            return result;
        }

        /// <summary>
        /// Emits convenience overloads for a method with marker protocol parameters.
        /// </summary>
        public static void EmitOverloads(
            CSharpWriter csWriter,
            SwiftWriter swiftWriter,
            MethodDecl methodDecl,
            MethodEnvironment env,
            TypeDecl? parentTypeDecl,
            Dictionary<string, List<string>>? markerProtocolConformances)
        {
            if (markerProtocolConformances == null || markerProtocolConformances.Count == 0)
                return;

            // Skip constructors, async methods, throwing methods, and generic parent types
            // (generic types would need proper generic-argument rendering in return type and constructor)
            if (methodDecl.IsConstructor || methodDecl.IsAsync || methodDecl.Throws)
                return;
            if (parentTypeDecl?.GenericParameters?.Count > 0)
                return;

            // Find the first parameter that is a marker protocol type with primitive conformers
            int markerParamIndex = -1;
            List<(string CSharpType, string SwiftType)>? conformers = null;

            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var param = methodDecl.CSSignature[i];

                if (param.SwiftTypeSpec is ProtocolListTypeSpec protocolSpec && protocolSpec.Protocols.Count == 1)
                {
                    var protocolName = protocolSpec.Protocols.Keys.First().Name;
                    var dotIdx = protocolName.LastIndexOf('.');
                    var unqualified = dotIdx >= 0 ? protocolName.Substring(dotIdx + 1) : protocolName;

                    if (markerProtocolConformances.ContainsKey(unqualified))
                    {
                        var c = GetPrimitiveConformers(unqualified, markerProtocolConformances);
                        if (c.Count > 0)
                        {
                            markerParamIndex = i;
                            conformers = c;
                            break; // Only handle first marker protocol parameter
                        }
                    }
                }
            }

            if (markerParamIndex < 0 || conformers == null)
                return;

            var markerParam = methodDecl.CSSignature[markerParamIndex];
            var csMethodName = NameProvider.ToPascalCase(methodDecl.Name);
            if (methodDecl.ParentDecl is TypeDecl td && csMethodName == td.Name)
                csMethodName = $"Get{csMethodName}";

            var returnSpec = methodDecl.CSSignature[0].SwiftTypeSpec;
            bool isVoidReturn = returnSpec.IsEmptyTuple;
            bool isStatic = methodDecl.MethodType == MethodType.Static;
            var staticModifier = isStatic ? "static " : "";

            // Determine the return type — only void and self-returning methods are supported.
            // Other return types would need full marshalling (string conversion, struct layout, etc.)
            // which this simple overload emitter doesn't handle.
            bool isSelfReturn = parentTypeDecl != null && returnSpec is NamedTypeSpec namedReturn &&
                                namedReturn.Name.EndsWith(parentTypeDecl.Name);

            if (!isVoidReturn && !isSelfReturn)
                return;

            string csReturnType = isVoidReturn ? "void" : parentTypeDecl!.Name;

            // Resolve wrapper library path (same pattern as other wrapper emitters)
            var moduleDecl = methodDecl.ModuleDecl;
            var moduleLibPath = moduleDecl != null ? env.TypeDatabase.GetLibraryPath(moduleDecl.Name) : "SwiftBindings";
            var wrapperLibPath = env.TypeDatabase.AsyncLibraryName ?? moduleLibPath;

            bool isInstance = !isStatic && parentTypeDecl != null;
            bool isClass = parentTypeDecl is ClassDecl;

            foreach (var (csType, swiftType) in conformers)
            {
                // Parameter name
                var paramName = markerParam.Name;
                if (paramName == "_" || string.IsNullOrEmpty(paramName))
                    paramName = string.IsNullOrEmpty(markerParam.PrivateName) ? "value" : markerParam.PrivateName;

                // Wrapper names. The raw MangledName is used here only as a unique-name
                // SEED: this emitter writes its own @_silgen_name Swift wrapper under
                // wrapperMangledName and points the P/Invoke EntryPoint at that same
                // string, so producer and consumer are self-paired by construction. The
                // P/Invoke never targets the original symbol, so a promoted emission
                // symbol (carried on the method environment, not on MethodDecl) is
                // irrelevant to this pairing.
                var swiftTypeShort = swiftType.Contains('.') ? swiftType.Substring(swiftType.LastIndexOf('.') + 1) : swiftType;
                var wrapperSuffix = $"_MP_{swiftTypeShort}";
                var wrapperMangledName = NameProvider.GetMangledName(methodDecl) + wrapperSuffix;
                var wrapperPInvokeName = NameProvider.GetPInvokeName(methodDecl) + wrapperSuffix;

                // --- Emit Swift wrapper ---
                EmitSwiftWrapper(swiftWriter, methodDecl, parentTypeDecl, markerParamIndex,
                    swiftType, wrapperMangledName, wrapperPInvokeName, isStatic, isClass);

                // --- Emit C# P/Invoke (LibraryImport + CallConvSwift) ---
                var pInvokeParams = new List<string>();
                if (isInstance)
                    pInvokeParams.Add("global::System.IntPtr self");
                pInvokeParams.Add($"{csType} {paramName}");

                var pInvokeReturnType = isVoidReturn ? "void" : "global::System.IntPtr";

                PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
                {
                    LibraryPath = wrapperLibPath,
                    EntryPoint = wrapperMangledName,
                    MethodName = wrapperPInvokeName,
                    ReturnType = pInvokeReturnType,
                    ParametersString = string.Join(", ", pInvokeParams),
                    CallingConvention = PInvokeCallingConvention.Swift
                });
                csWriter.WriteLine();

                // --- Emit C# overload ---
                AvailabilityAttributeEmitter.EmitAvailabilityAttributes(
                    csWriter, methodDecl, methodDecl.ParentDecl, emitObsolete: false);
                csWriter.WriteLine($"/// <summary>Convenience overload accepting <c>{csType}</c> for marker protocol parameter.</summary>");
                csWriter.WriteLine($"public {staticModifier}{csReturnType} {csMethodName}({csType} {paramName})");
                csWriter.WriteLine("{");
                csWriter.Indent++;

                // The @_silgen_name wrapper this calls is availability-gated (see EmitSwiftWrapper); on an OS
                // below the merged floor its body dereferences a weak-linked, null gated symbol (uncatchable
                // SIGSEGV). Throw a catchable exception before the P/Invoke.
                AvailabilityAttributeEmitter.EmitRuntimeAvailabilityGuard(
                    csWriter,
                    WrapperEmitterHelpers.MergeAvailability(methodDecl.AvailabilityAnnotations, methodDecl.ParentDecl),
                    methodDecl.ParentDecl is { Name: { Length: > 0 } pn } ? $"{pn}.{csMethodName}" : csMethodName);

                var callArgs = new List<string>();
                if (isInstance)
                    callArgs.Add("this.Payload.DangerousGetHandle()");
                callArgs.Add(paramName);

                var callExpr = $"{wrapperPInvokeName}({string.Join(", ", callArgs)})";
                if (isVoidReturn)
                {
                    csWriter.WriteLine($"{callExpr};");
                }
                else
                {
                    // Self-returning (builder pattern) — wrap raw IntPtr in new instance
                    csWriter.WriteLine($"var __result = {callExpr};");
                    csWriter.WriteLine($"return new {csReturnType}(__result);");
                }

                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine();
            }
        }

        /// <summary>
        /// Emits a Swift wrapper function that accepts a concrete primitive type
        /// and calls the original method, letting Swift handle existential boxing.
        /// Uses UnsafeMutableRawPointer for the self parameter with proper conversion
        /// (unsafeBitCast for classes, assumingMemoryBound for value types).
        /// </summary>
        private static void EmitSwiftWrapper(
            SwiftWriter swiftWriter,
            MethodDecl methodDecl,
            TypeDecl? parentTypeDecl,
            int markerParamIndex,
            string swiftConcreteType,
            string wrapperMangledName,
            string wrapperPInvokeName,
            bool isStatic,
            bool isClass)
        {
            var parentTypeName = parentTypeDecl?.SwiftTypeName;
            bool isInstance = !isStatic && parentTypeName != null;

            // Build Swift parameter list — self as UnsafeMutableRawPointer (not concrete type)
            var swiftParams = new List<string>();
            if (isInstance)
                swiftParams.Add("_ _self: UnsafeMutableRawPointer");

            // Sibling bindings: this emitter binds each param to its external label (p.Name), or
            // arg{i} for an unnamed param — NOT the canonical PrivateName??Name — so collect the set
            // with that exact formula so a reserved-name escape also dodges a sibling user binding.
            // Both loops recompute the identical raw name + reuse this set, keeping decl and
            // call in sync.
            var markerSiblings = new HashSet<string>(StringComparer.Ordinal);
            for (int si = 1; si < methodDecl.CSSignature.Count; si++)
            {
                var sp = methodDecl.CSSignature[si];
                var sraw = sp.Name == "_" ? (string.IsNullOrEmpty(sp.PrivateName) ? $"arg{si}" : sp.PrivateName) : sp.Name;
                if (!string.IsNullOrEmpty(sraw) && sraw != "_")
                    markerSiblings.Add(sraw);
            }

            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var p = methodDecl.CSSignature[i];
                // Escape a user binding colliding with the injected `_self` synthetic OR a sibling user
                // binding; the external call label is p.Name below, so this rename is source-local and
                // safe. The call-value loop escapes the same binding identically, keeping decl and call
                // in sync.
                var rawName = p.Name == "_" ? (string.IsNullOrEmpty(p.PrivateName) ? $"arg{i}" : p.PrivateName) : p.Name;
                var pName = NameProvider.EscapeReservedSwiftWrapperLabel(rawName, CdeclParamMapper.ExcludeSelf(markerSiblings, rawName));
                if (i == markerParamIndex)
                    swiftParams.Add($"_ {pName}: {swiftConcreteType}");
                else
                    swiftParams.Add($"_ {pName}: {p.SwiftTypeSpec}");
            }

            // Build return type
            var returnSpec = methodDecl.CSSignature[0].SwiftTypeSpec;
            bool isVoid = returnSpec.IsEmptyTuple;
            var returnTypeStr = isVoid ? "" : $" -> {returnSpec}";

            // Build call arguments
            var callArgs = new List<string>();
            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var p = methodDecl.CSSignature[i];
                // Same escape as the param-decl loop (same sibling set, self-excluded) so the call
                // references the (possibly) renamed binding.
                var rawName = p.Name == "_" ? (string.IsNullOrEmpty(p.PrivateName) ? $"arg{i}" : p.PrivateName) : p.Name;
                var pName = NameProvider.EscapeReservedSwiftWrapperLabel(rawName, CdeclParamMapper.ExcludeSelf(markerSiblings, rawName));
                // The raw Swift label, via the shared recovery: p.Name is the C#-safe spelling, so a
                // keyword label arrives here as `_default` and a label that genuinely begins with an
                // underscore as itself. Only a truly unlabeled parameter may drop its label at the
                // call site — suppressing on a bare leading underscore instead silently deletes both.
                var externalLabel = NameProvider.RecoverSwiftArgumentLabel(p);
                if (externalLabel == "_" || string.IsNullOrEmpty(externalLabel)
                    || SwiftBuilder.IsAutoGeneratedArgName(externalLabel))
                    callArgs.Add(pName);
                else
                    callArgs.Add($"{externalLabel}: {pName}");
            }

            // Emit @MainActor only for @MainActor isolation (not custom actors)
            bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
                parentTypeDecl, methodDecl.IsMainActorIsolated, methodDecl.IsNonisolated);

            // Top-level @_silgen_name wrapper functions do NOT inherit enclosing type
            // availability — both method-level and parent-type availability must be
            // applied explicitly. MergeAvailability collapses by strictest version.
            var mergedAvailability = WrapperEmitterHelpers.MergeAvailability(
                methodDecl.AvailabilityAnnotations, parentTypeDecl);
            WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, mergedAvailability);
            if (needsMainActor)
                swiftWriter.WriteLine("@MainActor");
            swiftWriter.WriteLine($"@_silgen_name(\"{wrapperMangledName}\")");
            swiftWriter.WriteLine($"public func {wrapperPInvokeName}({string.Join(", ", swiftParams)}){returnTypeStr} {{");

            // Emit self conversion for instance methods (matching ClosureEmitter pattern)
            if (isInstance)
            {
                var qualifiedTypeName = parentTypeName!.ModuleQualifiedName;
                if (isClass)
                    swiftWriter.WriteLine($"    let __self = unsafeBitCast(OpaquePointer(_self), to: {qualifiedTypeName}.self)");
                else
                    swiftWriter.WriteLine($"    let __self = _self.assumingMemoryBound(to: {qualifiedTypeName}.self).pointee");
            }

            var callPrefix = isInstance ? "__self." : (parentTypeName != null ? $"{parentTypeName.ModuleQualifiedName}." : "");
            var callExpr = $"{callPrefix}{NameProvider.ParserNameToSwift(methodDecl)}({string.Join(", ", callArgs)})";

            if (isVoid)
                swiftWriter.WriteLine($"    {callExpr}");
            else
                swiftWriter.WriteLine($"    return {callExpr}");
            swiftWriter.WriteLine("}");
            swiftWriter.WriteLine();
        }
    }
}
