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
    ///
    /// Each overload is its own recovery unit (<see cref="OverloadId"/>): it owns its Swift wrapper,
    /// its P/Invoke and its C# overload, calls nothing the method itself emits, and so stands or
    /// falls on its own. A wrapper that fails to compile withdraws only that overload, with its own
    /// skip row, and a method the recovery loop withdraws keeps its overloads
    /// (<see cref="TryEmitForWithdrawnMethod"/>).
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
        /// The parameters an overload passes straight through to its wrapper: the Swift scalars whose
        /// C# primitive has the same Swift-convention lowering.
        /// </summary>
        private static readonly Dictionary<string, string> PassThroughTypeMap = new()
        {
            ["Swift.Bool"] = "bool",
            ["Swift.Int"] = "nint",
            ["Swift.UInt"] = "nuint",
            ["Swift.Int8"] = "sbyte",
            ["Swift.UInt8"] = "byte",
            ["Swift.Int16"] = "short",
            ["Swift.UInt16"] = "ushort",
            ["Swift.Int32"] = "int",
            ["Swift.UInt32"] = "uint",
            ["Swift.Int64"] = "long",
            ["Swift.UInt64"] = "ulong",
            ["Swift.Float"] = "float",
            ["Swift.Double"] = "double",
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
        /// The recovery identity of the overload that binds <paramref name="methodId"/>'s marker
        /// parameter (an existential of <paramref name="markerProtocol"/>) to the primitive
        /// <paramref name="conformerSwiftType"/>: the method's identity qualified by that
        /// substitution, as a concrete specialization of a generic member is
        /// (<see cref="DeclIdFactory.ForSpecialization"/>). Stable across attempts, so a withdrawal
        /// recorded on one attempt finds the same overload on the retry.
        /// </summary>
        internal static DeclId OverloadId(DeclId methodId, string markerProtocol, string conformerSwiftType)
            => DeclIdFactory.ForSpecialization(methodId, $"{markerProtocol}={conformerSwiftType}");

        /// <summary>
        /// Replays the overloads of <paramref name="source"/>, a method the recovery loop withdrew.
        /// The overloads call their own wrappers, not the method's, so a withdrawal that removes the
        /// method's surface leaves them sound; each one is still checked against its own denial.
        /// Only a settled recovery withdrawal qualifies (<see cref="NativeDefaultOverloadRecovery.IsRecoveryWithdrawal"/>):
        /// a method the emitter threw on is not re-entered.
        /// </summary>
        internal static void TryEmitForWithdrawnMethod(
            MethodDecl source,
            CSharpWriter csWriter,
            SwiftWriter swiftWriter,
            ITypeDatabase typeDatabase,
            TypeHandlerContext context,
            IReadOnlySet<string>? siblingPropertyNames,
            HashSet<string>? emittedProjectedSignatures,
            Dictionary<string, int>? reservedOverloadShapes)
        {
            if (context.MarkerProtocolConformances is not { Count: > 0 } markerProtocolConformances)
                return;
            var sourceId = DeclIdFactory.ForMethod(source);
            if (!NativeDefaultOverloadRecovery.IsRecoveryWithdrawal(sourceId))
                return;
            // The overloads replay under the names and signature the method's own emission gave
            // them, captured when an earlier attempt emitted them: a fresh environment would
            // recompute the bare name and lose the overload resolver's disambiguation or an adopted
            // override name. A method whose overloads never emitted has nothing to replay.
            var emissionContext = context.GetEmissionContext();
            if (!emissionContext.TryGetDefaultOverloadRecipe(RecipeId(sourceId), out var recipe))
                return;
            var current = new MethodEnvironment(source, typeDatabase, siblingPropertyNames,
                context.PInvokeHelperContext, context.CompositionCollector)
            {
                SourceDeclId = sourceId,
                EmissionContext = emissionContext,
                EmittedProjectedSignatures = emittedProjectedSignatures,
                ReservedOverloadShapes = reservedOverloadShapes,
            };
            var env = recipe.CreateEnvironment(current);
            EmitOverloads(csWriter, swiftWriter, env.MethodDecl, env, source.ParentDecl as TypeDecl, markerProtocolConformances);
        }

        /// <summary>
        /// The key under which a method's overload environment is kept for replay. It shares the
        /// convenience-overload recipe store, which the verify-recover driver carries from a
        /// completed render into the next, under an identity of the method's own.
        /// </summary>
        internal static DeclId RecipeId(DeclId methodId) =>
            DeclIdFactory.ForSpecialization(methodId, "marker-protocol-overloads");

        /// <summary>
        /// The protocol of a single-protocol existential value, in either shape the parser produces:
        /// <c>any P</c> as the ABI JSON prints it today (a nominal flagged <see cref="TypeSpec.IsAny"/>,
        /// <see cref="ExistentialHandler.IsExistentialValue"/>, never the metatype <c>any P.Type</c>) or
        /// a one-member composition. Anything else, an opaque <c>some P</c> included, is not one.
        /// </summary>
        private static string? SingleProtocolExistential(TypeSpec spec) => spec switch
        {
            NamedTypeSpec { GenericParameters.Count: 0, InnerType: null } named
                when ExistentialHandler.IsExistentialValue(named) => named.Name,
            ProtocolListTypeSpec { IsOpaque: false, Protocols.Count: 1 } list => list.Protocols.Keys.First().Name,
            _ => null,
        };

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
            string? markerProtocolName = null;
            List<(string CSharpType, string SwiftType)>? conformers = null;

            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var param = methodDecl.CSSignature[i];

                if (!param.IsInOut && SingleProtocolExistential(param.SwiftTypeSpec) is { } protocolName)
                {
                    var dotIdx = protocolName.LastIndexOf('.');
                    var unqualified = dotIdx >= 0 ? protocolName.Substring(dotIdx + 1) : protocolName;

                    if (markerProtocolConformances.ContainsKey(unqualified))
                    {
                        var c = GetPrimitiveConformers(unqualified, markerProtocolConformances);
                        if (c.Count > 0)
                        {
                            markerParamIndex = i;
                            markerProtocolName = protocolName;
                            conformers = c;
                            break; // Only handle first marker protocol parameter
                        }
                    }
                }
            }

            if (markerParamIndex < 0 || conformers == null || markerProtocolName == null)
                return;

            // Every other parameter crosses as itself: the overload passes it straight through to
            // the wrapper, which declares it with its Swift type. Only a parameter whose C# primitive
            // has the same Swift-convention lowering can do that; anything else needs marshalling
            // this emitter does not do, so the overload is declined rather than emitted with a
            // parameter list that disagrees with its wrapper's.
            var passThrough = new List<(int Index, string CSharpType, string Name)>();
            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                if (i == markerParamIndex)
                    continue;
                var other = methodDecl.CSSignature[i];
                if (other.IsInOut || other.SwiftTypeSpec is not NamedTypeSpec { GenericParameters.Count: 0 } named
                    || !PassThroughTypeMap.TryGetValue(named.Name, out var otherCsType))
                    return;
                passThrough.Add((i, otherCsType, string.Empty));
            }

            var markerParam = methodDecl.CSSignature[markerParamIndex];
            // The overloads sit beside the method under its own C# name.
            var csMethodName = env.CSharpMethodName;

            var returnSpec = methodDecl.CSSignature[0].SwiftTypeSpec;
            bool isVoidReturn = returnSpec.IsEmptyTuple;
            bool isStatic = MethodEnvironment.EmitsStaticMember(methodDecl, methodDecl.ParentDecl);
            var staticModifier = isStatic ? "static " : "";

            // Determine the return type — only void and self-returning methods are supported.
            // Other return types would need full marshalling (string conversion, struct layout, etc.)
            // which this simple overload emitter doesn't handle. A self return is the parent class
            // itself, matched by its full Swift name (a type whose name merely ends in the parent's
            // is not the parent), and only for a class: the wrapper hands back the retained
            // reference the overload adopts, where a struct would come back by value.
            bool isSelfReturn = parentTypeDecl is ClassDecl && parentTypeDecl.SwiftTypeName != null
                && returnSpec is NamedTypeSpec namedReturn
                && string.Equals(namedReturn.Name, parentTypeDecl.SwiftTypeName.ToString(), StringComparison.Ordinal);

            if (!isVoidReturn && !isSelfReturn)
                return;

            string csReturnType = isVoidReturn ? "void" : parentTypeDecl!.Name;

            // Resolve wrapper library path (same pattern as other wrapper emitters)
            var moduleDecl = methodDecl.ModuleDecl;
            var moduleLibPath = moduleDecl != null ? env.TypeDatabase.GetLibraryPath(moduleDecl.Name) : "SwiftBindings";
            var wrapperLibPath = env.TypeDatabase.AsyncLibraryName ?? moduleLibPath;

            bool isInstance = !isStatic && parentTypeDecl != null;
            bool isClass = parentTypeDecl is ClassDecl;

            // Parameter name
            var paramName = markerParam.Name;
            if (paramName == "_" || string.IsNullOrEmpty(paramName))
                paramName = string.IsNullOrEmpty(markerParam.PrivateName) ? "value" : markerParam.PrivateName;

            // The pass-through parameters' C# names, kept clear of the marker's and of each other.
            var usedNames = new HashSet<string>(StringComparer.Ordinal) { paramName, "self" };
            for (int k = 0; k < passThrough.Count; k++)
            {
                var (index, otherCsType, _) = passThrough[k];
                var name = NameProvider.GetCSharpParameterName(methodDecl.CSSignature[index]);
                if (string.IsNullOrEmpty(name) || name == "_")
                    name = $"arg{index}";
                var unique = name;
                for (int n = 2; !usedNames.Add(unique); n++)
                    unique = $"{name}{n}";
                passThrough[k] = (index, otherCsType, unique);
            }

            var passThroughByIndex = passThrough.ToDictionary(p => p.Index, p => (p.CSharpType, p.Name));
            var methodId = env.SourceDeclId;
            var emittedAny = false;
            foreach (var (csType, swiftType) in conformers)
            {
                // Its own recovery unit: denied up front with its own skip row when an earlier
                // attempt withdrew it, and every artifact it writes below is attributed to it.
                var overloadId = OverloadId(methodId, markerProtocolName, swiftType);
                if (EmissionSeam.TryDenySpecializationUpFront(
                        overloadId, methodDecl, $"{swiftType} as {markerProtocolName}", csWriter))
                    continue;
                using var csScope = csWriter.BeginFragment(FragmentOwners.ForDeclId(overloadId));
                using var swiftScope = swiftWriter.BeginFragment(FragmentOwners.ForDeclId(overloadId, ArtifactRole.SwiftWrapper));

                // The overload is the method as a sibling taking the conformer would project. A
                // sibling that declares that signature, or one a call site binds as well, keeps it
                // whatever the declaration order; otherwise the signature is claimed here, so a later
                // producer sees it taken.
                if (!TryReserveSignature(methodDecl, markerParamIndex, swiftType, env))
                    continue;
                emittedAny = true;

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
                    markerProtocolName, swiftType, wrapperMangledName, wrapperPInvokeName, isStatic, isClass, env);

                // --- Emit C# P/Invoke (LibraryImport + CallConvSwift) ---
                // Self, then every Swift parameter in declaration order, as the wrapper declares them.
                string NativeParameter(string type, string name) =>
                    type == "bool" ? $"[global::System.Runtime.InteropServices.MarshalAs(global::System.Runtime.InteropServices.UnmanagedType.U1)] bool {name}" : $"{type} {name}";
                var pInvokeParams = new List<string>();
                var publicParams = new List<string>();
                var callArgs = new List<string>();
                if (isInstance)
                {
                    pInvokeParams.Add("global::System.IntPtr self");
                    callArgs.Add(LiveHandleExpressions.SafeHandle("this.Payload"));
                }
                for (int i = 1; i < methodDecl.CSSignature.Count; i++)
                {
                    var (type, name) = i == markerParamIndex
                        ? (csType, paramName)
                        : passThroughByIndex[i];
                    pInvokeParams.Add(NativeParameter(type, name));
                    publicParams.Add($"{type} {name}");
                    callArgs.Add(name);
                }

                var pInvokeReturnType = isVoidReturn ? "void" : "global::System.IntPtr";

                PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
                {
                    LibraryPath = wrapperLibPath,
                    EntryPoint = wrapperMangledName,
                    MethodName = wrapperPInvokeName,
                    ReturnType = pInvokeReturnType,
                    ParametersString = string.Join(", ", pInvokeParams),
                    CallingConvention = PInvokeCallingConvention.Swift,
                    EmissionContext = env.EmissionContext
                });
                csWriter.WriteLine();

                // --- Emit C# overload --- (doc first: after an attribute it documents nothing, CS1587)
                csWriter.WriteLine($"/// <summary>Convenience overload accepting <c>{XmlDocCommentEmitter.XmlEscape(csType)}</c> for marker protocol parameter.</summary>");
                AvailabilityAttributeEmitter.EmitAvailabilityAttributes(
                    csWriter, methodDecl, methodDecl.ParentDecl, emitObsolete: false);
                csWriter.WriteLine($"public {staticModifier}{csReturnType} {csMethodName}({string.Join(", ", publicParams)})");
                csWriter.WriteLine("{");
                csWriter.Indent++;

                // The @_silgen_name wrapper this calls is availability-gated (see EmitSwiftWrapper); on an OS
                // below the merged floor its body dereferences a weak-linked, null gated symbol (uncatchable
                // SIGSEGV). Throw a catchable exception before the P/Invoke.
                AvailabilityAttributeEmitter.EmitRuntimeAvailabilityGuard(
                    csWriter,
                    WrapperEmitterHelpers.MergeAvailability(methodDecl.AvailabilityAnnotations, methodDecl.ParentDecl),
                    methodDecl.ParentDecl is { Name: { Length: > 0 } pn } ? $"{pn}.{csMethodName}" : csMethodName);

                var callExpr = $"{wrapperPInvokeName}({string.Join(", ", callArgs)})";
                if (isVoidReturn)
                {
                    csWriter.WriteLine($"{callExpr};");
                }
                else
                {
                    // Self-returning (builder pattern). The wrapper returns the reference at +1,
                    // which the type's own payload factory adopts, as the primary member's
                    // conversion does, so the result joins the caller's dispose scope too.
                    csWriter.WriteLine($"var __result = {callExpr};");
                    csWriter.WriteLine($"return global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwiftObject<{csReturnType}>(__result);");
                }

                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine();
            }

            // Keep the environment the overloads were emitted under, so a later attempt that
            // withdraws the method replays them under the same names (TryEmitForWithdrawnMethod).
            if (emittedAny && env.EmissionContext is { } emissionContext && EmissionAttempt.Current?.Abandoned != true)
                emissionContext.RecordDefaultOverloadRecipe(RecipeId(methodId), DefaultOverloadReplayRecipe.Capture(env));
        }

        /// <summary>
        /// Claims the C# signature of the overload taking <paramref name="conformerSwiftType"/> where
        /// <paramref name="methodDecl"/> takes the marker existential, or returns false when a sibling
        /// declaration owns it (the same signature, CS0111, or one a call site cannot tell apart,
        /// CS0121) or an earlier producer already claimed it.
        /// </summary>
        private static bool TryReserveSignature(MethodDecl methodDecl, int markerParamIndex, string conformerSwiftType, MethodEnvironment env)
        {
            var signature = new List<ArgumentDecl>(methodDecl.CSSignature);
            signature[markerParamIndex] = signature[markerParamIndex] with { SwiftTypeSpec = new NamedTypeSpec(conformerSwiftType) };
            var overload = methodDecl with
            {
                MangledName = $"{methodDecl.MangledName}_MP_{conformerSwiftType}",
                CSSignature = signature,
            };
            var key = DefaultParameterOverloadEmitter.GetProjectedOverloadKey(
                overload, env.TypeDatabase, env.SiblingPropertyNames, env.DisambiguatedNameInput);
            // An override that adopted its ancestor slot's name emits under it (CSharpMethodName), so
            // it reserves under it too.
            if (env.AdoptedOverrideCSharpName != null && key.IndexOf('(') is > 0 and var paren)
                key = env.AdoptedOverrideCSharpName + key.Substring(paren);
            var requiredCount = OverloadAmbiguityGuard.RequiredCountFor(overload, env.TypeDatabase, key);
            if (DefaultParameterOverloadEmitter.TryFindSignatureCollision(methodDecl, overload, key, requiredCount, env, out _))
                return false;
            if (env.EmittedProjectedSignatures is { } emitted)
            {
                if (OverloadAmbiguityGuard.FindAmbiguousReservation(emitted, env.ReservedOverloadShapes, key, requiredCount) != null
                    || !emitted.Add(key))
                    return false;
                OverloadAmbiguityGuard.RecordReservation(env.ReservedOverloadShapes, key, requiredCount);
            }
            return true;
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
            string markerProtocolName,
            string swiftConcreteType,
            string wrapperMangledName,
            string wrapperPInvokeName,
            bool isStatic,
            bool isClass,
            MethodEnvironment env)
        {
            var parentTypeName = parentTypeDecl?.SwiftTypeName;
            bool isInstance = !isStatic && parentTypeName != null;

            // Build Swift parameter list — self as UnsafeMutableRawPointer (not concrete type)
            var swiftParams = new List<string>();
            if (isInstance)
                swiftParams.Add("_ _self: Swift.UnsafeMutableRawPointer");

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
                // The marker argument goes back in as the existential the method declares, so Swift
                // resolves the call to this method and not to a sibling overload the concrete value
                // also fits (one taking a composition the conformer satisfies too).
                var value = i == markerParamIndex
                    ? $"{pName} as any {markerProtocolName}"
                    : SwiftClosureArgument.Render(pName, p.SwiftTypeSpec);
                if (externalLabel == "_" || string.IsNullOrEmpty(externalLabel)
                    || SwiftBuilder.IsAutoGeneratedArgName(externalLabel))
                    callArgs.Add(value);
                else
                    callArgs.Add($"{externalLabel}: {value}");
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
            string[] declarationLines =
            [
                $"@_silgen_name(\"{wrapperMangledName}\")",
                $"public func {wrapperPInvokeName}({string.Join(", ", swiftParams)}){returnTypeStr} {{",
            ];
            foreach (var line in declarationLines)
                swiftWriter.WriteLine(line);
            // The C# overload binds this wrapper with the Swift convention: describe it from the
            // declaration just written, so the binding's transition carries its facts.
            env.EmissionContext?.RegisterSyntheticSwiftDeclaration(
                declarationLines,
                SwiftWrapperSynthesizer.MarkerProtocolOverload,
                PInvokeEmitHelper.ResolveWrapperLibrary(env.TypeDatabase),
                forwards: methodDecl.MangledName,
                availability: SyntheticSwiftFunction.FloorsOf(mergedAvailability));

            // Emit self conversion for instance methods (matching ClosureEmitter pattern)
            if (isInstance)
            {
                var qualifiedTypeName = parentTypeName!.ModuleQualifiedName;
                if (isClass)
                    swiftWriter.WriteLine($"    let __self = Swift.unsafeBitCast(Swift.OpaquePointer(_self), to: {qualifiedTypeName}.self)");
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
