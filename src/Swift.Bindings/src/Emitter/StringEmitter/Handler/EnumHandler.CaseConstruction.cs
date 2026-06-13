// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Swift.Runtime;

namespace BindingsGeneration
{
    public partial class EnumHandler
    {
        /// <summary>
        /// Emits a static method for an enum case with associated values.
        /// </summary>
        private bool EmitEnumCaseWithAssociatedValues(CSharpWriter csWriter, EnumDecl enumDecl, EnumCaseDecl caseDecl, ModuleDecl moduleDecl, ITypeDatabase typeDatabase, string enumTypeName, PInvokeHelperContext? pinvokeHelperContext, Dictionary<string, string>? propertyRenames = null, Dictionary<string, string>? caseNameMap = null, SwiftWriter? swiftWriter = null, ModuleEmissionContext? emissionCtx = null)
        {
            var caseName = caseDecl.Name;
            var capitalizedName = NameProvider.GetFinalMemberName(
                NameProvider.GetCaseName(caseName, caseNameMap), propertyRenames);
            var pInvokeName = $"PInvoke_{capitalizedName}";
            var libPath = typeDatabase.GetLibraryPath(moduleDecl.Name);
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Build parameter list from associated values
            // Track both internal type (for validation/P/Invoke) and public type (for method signatures)
            var parameters = new List<(string type, string publicType, string name, TypeSpec typeSpec)>();
            for (int i = 0; i < caseDecl.AssociatedValues.Count; i++)
            {
                var typeSpec = caseDecl.AssociatedValues[i];
                var enumGenericParams = enumDecl.IsGeneric ? enumDecl.GenericParameters : null;
                var csharpType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, enumGenericParams, moduleDecl);

                // Check if type is unsupported.
                // Direct AnyType fallback (whole payload resolves to AnyType) — bail for the
                // single-payload case, and for any tuple element that is itself a generic-bound
                // NamedTypeSpec whose resolved name has AnyType embedded in its generic args
                // (the StoreKit2 nested-type bug: "VerificationResult.VerificationError<Swift.AnyType>" —
                // outer-generic-args mis-placed onto inner nested type, a pre-existing emitter bug).
                // Plain tuple elements that resolve directly to AnyType (e.g. "(Int, UnknownType)") are still emittable: the per-element factory body uses
                // value0.ItemN.Payload.DangerousGetHandle(), which compiles since Swift.AnyType has a Payload.
                if (HasUnsupportedAnyTypeInPayload(typeSpec, csharpType, typeDatabase, boundGenericsHandler, enumGenericParams, moduleDecl))
                {
                    _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' has unsupported associated value type at index {i}. Skipping case.");
                    return false;
                }

                var publicType = GetPublicCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, enumGenericParams, moduleDecl);

                // Use type label if available, otherwise derive from type
                var paramName = typeSpec.TypeLabel;
                if (string.IsNullOrEmpty(paramName))
                {
                    paramName = NameProvider.DeriveParameterNameFromType(typeSpec) ?? $"value{i}";
                    // For multi-payload cases, append the index when the derived name
                    // collapses to "value" — multiple unlabeled payloads would otherwise
                    // collide. Single-payload `(_: T)` keeps the bare "value" so the
                    // factory parameter doesn't surface as a positional placeholder.
                    if (paramName == "value" && caseDecl.AssociatedValues.Count > 1)
                        paramName = $"value{i}";
                }
                // Sanitize parameter name (remove invalid characters, ensure starts with letter)
                paramName = SanitizeParameterName(paramName);
                // Skip enum cases with tuple parameters containing unresolved existential elements.
                // When a tuple element is an existential that can't be projected to an interface
                // (e.g., Any → ExistentialContainer0), the public type leaks ABI types that are
                // invalid as C# tuple elements. The method body would also pass @object.object
                // to P/Invoke expecting ExistentialContainer0, with no runtime conversion available.
                if (typeSpec is TupleTypeSpec && publicType.Contains("ExistentialContainer"))
                {
                    _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' has tuple with unresolved existential element. Skipping case.");
                    return false;
                }

                // Skip enum cases with associated values that don't support parameter-direction
                // marshalling (e.g., Result<T,E>). The projection exists for return direction
                // but GetParameterPlan throws because C#-created instances lack native payloads.
                // Emitting the factory would produce code that compiles but throws at runtime.
                if (typeSpec is NamedTypeSpec projCheckSpec && projCheckSpec.ContainsGenericParameters)
                {
                    var projCheck = new TypeProjectionFactory().Project(typeSpec, new ProjectionContext
                    {
                        TypeDatabase = typeDatabase, IsParameter = true,
                        GenericContext = enumDecl.IsGeneric
                            ? BuildGenericContextFromEnumParams(enumDecl.GenericParameters)
                            : GenericContext.Empty
                    });
                    if (projCheck is ResultProjection)
                    {
                        _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' has Result<T,E> associated value which does not support parameter-direction marshalling. Skipping case factory.");
                        return false;
                    }
                }

                // Skip enum cases whose bound-generic payload has ObjC-bridged type remap
                // in a generic argument position (e.g., Trend<UnitTemperature> → Trend<NSUnitTemperature>).
                // The outer generic type is emitted with `where T : ISwiftObject`, but the remapped
                // NSObject-rooted type does not implement ISwiftObject — the factory would produce CS0311.
                // The case remains constructable from native code (pattern matching still works in C#),
                // we just can't emit a C#-side factory that passes the constraint check.
                if (ContainsRemappedObjCTypeInGenericArgs(typeSpec))
                {
                    _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' has a bound generic payload with an ObjC-bridged type remap as a generic argument. Skipping case factory to avoid ISwiftObject constraint violation.");
                    return false;
                }

                // Skip enum cases whose bound-generic payload references a concrete type
                // that doesn't satisfy a user-protocol constraint on the outer generic
                // (e.g., RichTextInsertion<T> where T : IRichTextInsertable, but the case
                // binds T = Swift.SwiftString, which cannot retroactively conform to the
                // user protocol). Without this gate the static factory's parameter type
                // produces CS0311 at compile time. Mirrors the same check applied to
                // methods/properties in MemberEmissionValidator.
                if (boundGenericsHandler.TryGetFirstUnsatisfiedConstraint(typeSpec, caseDecl, out var constraintDetails))
                {
                    _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' associated value violates generic constraint: {constraintDetails}. Skipping case factory.");
                    return false;
                }

                parameters.Add((csharpType, publicType, paramName, typeSpec));
            }

            // Deduplicate derived parameter names using a usedNames set
            // (same collision-safe pattern as NameProvider.DeduplicateParameterNamesCore)
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < parameters.Count; i++)
            {
                var (type, publicType, name, typeSpec) = parameters[i];
                if (usedNames.Add(name))
                    continue;

                int suffix = 2;
                while (!usedNames.Add($"{name}{suffix}"))
                    suffix++;
                parameters[i] = (type, publicType, $"{name}{suffix}", typeSpec);
            }

            // Determine early whether this case can route through a @_cdecl wrapper (C calling
            // convention). Hoisted above all emission so the Defect A fail-closed guard can run
            // before anything is written to csWriter.
            var useCdeclWrapper = swiftWriter != null && emissionCtx != null &&
                EnumCaseWrapperEmitter.ShouldEmitCaseFactoryWrapper(enumDecl, caseDecl, typeDatabase);

            // Defect A — fail closed for enum payload-case constructors that need generic
            // type-metadata routing we don't yet support. Two shapes reach here:
            //   (1) the enum is itself generic (enum E<T>): its case constructor is never
            //       exported as a plain function symbol — `nm -gU` of the built framework shows
            //       only the `…mlFWC` case-descriptor DATA, not an `…mlF` function — and the
            //       @_cdecl wrapper path declines generic enums, so useCdeclWrapper is false and
            //       the only remaining path imports caseDecl.MangledName directly via
            //       CallConvSwift: a dangling P/Invoke that throws EntryPointNotFoundException.
            //   (2) the enum is nested in a generic parent (struct Outer<T> { enum E }): E is
            //       still parameterized by the parent's T, so constructing Outer<T>.E needs T's
            //       metadata. The parser normally stamps the outer signature onto E so
            //       IsGeneric is already true (case 1 covers it); IsInheritedGenericContext also
            //       fail-closes the edge where E's own generic signature is absent from the ABI.
            //       With the @_cdecl wrapper now declined for both, the remaining direct path is
            //       likewise unexported — skip rather than ship a trap.
            // Emitting either would compile but crash at runtime. Pattern-matching these cases
            // from natively-constructed values still works; only the C#-side factory is
            // unavailable. Correct metadata-aware case-factory routing is Session 8 territory.
            if ((enumDecl.IsGeneric || WrapperValidation.IsInheritedGenericContext(enumDecl)) && !useCdeclWrapper)
            {
                _logger.LogWarning(
                    "Enum case '{Enum}.{Case}' belongs to a generic enum; its case constructor is not an exported function symbol and cannot be reached through a @_cdecl wrapper. Skipping the C# factory to avoid a dangling P/Invoke (EntryPointNotFoundException at runtime).",
                    enumDecl.Name, caseName);
                UnsupportedCommentEmitter.EmitMemberSkipped(
                    csWriter, capitalizedName, BindingItemKind.Method, SkipReason.MissingWrapperSymbol,
                    "generic-enum payload-case constructor has no exported function symbol and no @_cdecl wrapper route; a direct mangled-symbol P/Invoke would throw EntryPointNotFoundException on first use.");
                return false;
            }

            // Generate the static method for this case — prefer symbol graph doc over synthetic
            if (caseDecl.Documentation != null && !caseDecl.Documentation.IsEmpty)
            {
                XmlDocCommentEmitter.EmitDocComment(csWriter, caseDecl);
            }
            else
            {
                csWriter.WriteLine($"/// <summary>");
                csWriter.WriteLine($"/// Creates the '{caseName}' case of {enumTypeName}.");
                csWriter.WriteLine($"/// </summary>");
            }

            // Mirror Swift @available on the case onto the C# factory method —
            // matches SimpleEnum's per-case emission. Without this, deprecated /
            // platform-restricted enum cases lower to factory methods
            // with no [Obsolete] / [SupportedOSPlatform] guard.
            AvailabilityAttributeEmitter.EmitAvailabilityAttributes(
                csWriter, caseDecl, parentDecl: enumDecl, emitObsolete: true);

            var parameterString = string.Join(", ", parameters.Select(p => $"{p.publicType} {p.name}"));
            // C10: Use unique local variable name to avoid CS0136 if a parameter is also named "result"
            var resultVarName = parameters.Any(p => p.name == "result") ? "__enumResult" : "result";
            csWriter.WriteLine($"public static unsafe {enumTypeName} {capitalizedName}({parameterString})");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine($"var {resultVarName} = new {enumTypeName}();");

            // (useCdeclWrapper computed above, before any emission, for the Defect A fail-closed guard.)

            // Emit conversions for parameters that differ between public and internal types
            var typeConversionHandler = new TypeConversionHandler(typeDatabase);
            var projectedArgs = new Dictionary<int, MarshalPlan>();
            // Track tuple params with per-element conversion (maps param index → converted tuple expression)
            var tuplePInvokeExprs = new Dictionary<int, string>();
            for (int i = 0; i < parameters.Count; i++)
            {
                var (type, publicType, name, typeSpec) = parameters[i];
                // Strip @ verbatim prefix for compound variable names (e.g., @in → in).
                // The @ prefix is valid at the START of a C# identifier but invalid mid-identifier.
                // Compound names like __{bareName} produce @__in (valid), not __@in (invalid).
                var bareName = NameProvider.StripVerbatimPrefix(name);
                if (typeConversionHandler.IsSwiftString(typeSpec))
                {
                    if (useCdeclWrapper)
                    {
                        // @_cdecl: encode string to UTF-8 bytes, pass pointer + length
                        csWriter.WriteLine($"var __{bareName}Utf8 = System.Text.Encoding.UTF8.GetBytes({name});");
                    }
                    else
                    {
                        csWriter.WriteLine($"using var __{bareName} = new SwiftString({name});");
                    }
                }
                else if (typeSpec is NamedTypeSpec dataSpec && dataSpec.Name == "Foundation.Data")
                {
                    csWriter.WriteLine($"var __{bareName} = Swift.Foundation.Data.FromByteArray({name});");
                }
                else if (typeSpec is NamedTypeSpec dateSpec && dateSpec.Name == "Foundation.Date")
                {
                    csWriter.WriteLine($"var __{bareName} = ({name} - {DateProjection.SwiftEpoch}).TotalSeconds;");
                }
                else if (typeSpec is TupleTypeSpec tupleSpec && publicType != type)
                {
                    // Tuple with projected elements — emit per-element conversion.
                    // Build a converted tuple expression for the P/Invoke call.
                    var genericContext = enumDecl.IsGeneric
                        ? BuildGenericContextFromEnumParams(enumDecl.GenericParameters)
                        : GenericContext.Empty;
                    // Emit per-element marshalling from public types → ABI types.
                    // The public signature uses factory-projected types (string, int?, etc.)
                    // but the P/Invoke tuple uses lowered types (IntPtr, nint, etc.).
                    // We marshal each element from public → ABI, then use GetPInvokeArgument
                    // to get the correct lowered expression for the tuple P/Invoke call.
                    var factory = new TypeProjectionFactory();
                    var elementExprs = new List<string>();
                    for (int j = 0; j < tupleSpec.Elements.Count; j++)
                    {
                        var element = tupleSpec.Elements[j];
                        var elementAccess = !string.IsNullOrEmpty(element.TypeLabel)
                            ? $"{name}.{element.TypeLabel}"
                            : $"{name}.Item{j + 1}";

                        var proj = factory.Project(element, new ProjectionContext
                        {
                            TypeDatabase = typeDatabase, IsParameter = true, GenericContext = genericContext
                        });
                        if (proj is StringProjection)
                        {
                            // String: public is `string`, P/Invoke tuple element is IntPtr.
                            // Convert string → SwiftString, then extract IntPtr handle.
                            var elemVarName = $"__{bareName}_e{j}";
                            csWriter.WriteLine($"using var {elemVarName} = new SwiftString({elementAccess});");
                            elementExprs.Add(GetPInvokeArgument(elemVarName, element, typeDatabase));
                        }
                        else if (proj is DataProjection)
                        {
                            // Data: convert byte[] → Swift.Foundation.Data for P/Invoke tuple element.
                            var elemVarName = $"__{bareName}_e{j}";
                            csWriter.WriteLine($"var {elemVarName} = Swift.Foundation.Data.FromByteArray({elementAccess});");
                            elementExprs.Add(elemVarName);
                        }
                        else if (proj is DateProjection)
                        {
                            // Date: convert DateTimeOffset → double (seconds since 2001 epoch).
                            var elemVarName = $"__{bareName}_e{j}";
                            csWriter.WriteLine($"var {elemVarName} = ({elementAccess} - {DateProjection.SwiftEpoch}).TotalSeconds;");
                            elementExprs.Add(elemVarName);
                        }
                        else if (proj is NativeRemappedProjection nrp)
                        {
                            // NativeRemapped (URL, etc.): factory creates wrapper, but P/Invoke tuple
                            // element is IntPtr. Use factory setup, then extract IntPtr from the wrapper.
                            var elemVarName = $"__{bareName}_e{j}";
                            csWriter.WriteLine($"var {elemVarName} = {elementAccess};");
                            var elemPlan = nrp.GetParameterPlan(elemVarName);
                            MarshalPlanRenderer.RenderStatements(csWriter, elemPlan.SetupStatements);
                            // For non-frozen NativeRemapped (URL): plan gives "{name}Swift.Payload" (SwiftSafeHandle).
                            // Need to add .DangerousGetHandle() for IntPtr.
                            var pinvokeExpr = elemPlan.PInvokeExpression;
                            if (pinvokeExpr.EndsWith(".Payload"))
                                pinvokeExpr += ".DangerousGetHandle()";
                            elementExprs.Add(pinvokeExpr);
                        }
                        else if (proj is OptionalProjection or ArrayProjection or DictionaryProjection
                            or SetProjection or ExistentialProjection)
                        {
                            // These projections produce PInvokeExpression that already matches
                            // the tuple P/Invoke type (IntPtr for Optional/Array/Dict, ExistentialContainer for existentials).
                            var elemVarName = $"__{bareName}_e{j}";
                            csWriter.WriteLine($"var {elemVarName} = {elementAccess};");
                            var elemPlan = proj.GetParameterPlan(elemVarName);

                            // @_cdecl wrapper: collection and non-reference optional tuple elements
                            // pass pointer via UnsafeRawPointer. Use shared helper to skip
                            // PayloadBuffer and emit DangerousGetHandle instead.
                            if (useCdeclWrapper && CdeclMarshallingHelper.NeedsCdeclPointerOverride(proj))
                            {
                                CdeclMarshallingHelper.RenderWithHandleOverride(csWriter, elemPlan, elemVarName);
                                elementExprs.Add(NameProvider.GetBoundGenericBufferName(elemVarName));
                            }
                            else
                            {
                                MarshalPlanRenderer.RenderStatements(csWriter, elemPlan.SetupStatements);
                                elementExprs.Add(elemPlan.PInvokeExpression);
                            }
                        }
                        else
                        {
                            // No projection or unsupported — use direct P/Invoke argument lowering
                            elementExprs.Add(GetPInvokeArgument(elementAccess, element, typeDatabase));
                        }
                    }
                    tuplePInvokeExprs[i] = $"({string.Join(", ", elementExprs)})";
                }
                else if (publicType != type && typeSpec is NamedTypeSpec ns && ns.ContainsGenericParameters)
                {
                    // Bound generic with factory projection — emit conversion via MarshalPlan
                    var genericContext = enumDecl.IsGeneric
                        ? BuildGenericContextFromEnumParams(enumDecl.GenericParameters)
                        : GenericContext.Empty;
                    var projection = new TypeProjectionFactory().Project(typeSpec, new ProjectionContext
                    {
                        TypeDatabase = typeDatabase, IsParameter = true, GenericContext = genericContext
                    });
                    if (projection != null)
                    {
                        var plan = projection.GetParameterPlan(name);

                        // @_cdecl wrapper: collections and non-reference optionals pass pointer
                        // via UnsafeRawPointer. Use shared helper to skip PayloadBuffer and
                        // emit DangerousGetHandle instead.
                        if (useCdeclWrapper && CdeclMarshallingHelper.NeedsCdeclPointerOverride(projection))
                        {
                            CdeclMarshallingHelper.RenderWithHandleOverride(csWriter, plan, name);
                            projectedArgs[i] = plan;
                        }
                        else
                        {
                            MarshalPlanRenderer.RenderStatements(csWriter, plan.SetupStatements);
                            projectedArgs[i] = plan;
                        }
                    }
                }
            }

            string? cdeclSymbol = null;
            if (useCdeclWrapper)
            {
                var enumSwiftName = enumDecl.SwiftTypeName?.Name ?? enumDecl.Name;
                cdeclSymbol = EnumCaseWrapperEmitter.GetCaseFactorySymbolName(
                    moduleDecl.Name, enumSwiftName, caseName, caseDecl.MangledName);

                // Create a minimal MethodEnvironment for GetCdeclParamMapping
                var dummyMethod = new MethodDecl
                {
                    Name = caseName,
                    MangledName = caseDecl.MangledName,
                    MethodType = MethodType.Static,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>(),
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = enumDecl,
                    ModuleDecl = moduleDecl,
                    Throws = false,
                    IsAsync = false,
                    Visibility = Visibility.Public
                };
                var wrapperEnv = new MethodEnvironment(dummyMethod, typeDatabase);

                EnumCaseWrapperEmitter.EmitSwiftCaseFactoryWrapper(
                    swiftWriter!, enumDecl, caseDecl, cdeclSymbol, wrapperEnv, emissionCtx);
            }

            // Pre-scan for existential params: declare heap pointers (and, for owning EC1
            // candidates, the owns-bit) before the try so they're accessible in finally for
            // cleanup (same pattern as WrapperEmitter). The owning condition must match the
            // marshalling loop's GetOrCreate gate exactly so the owns-bit only exists when a
            // value conformer may have been boxed at +1.
            var existentialHeaps = new List<(string HeapName, string? OwnsVar, int WitnessTableCount, string? KeepAliveVar)>();
            if (useCdeclWrapper)
            {
                var preScanHandler = new ExistentialHandler(typeDatabase);
                for (int i = 0; i < parameters.Count; i++)
                {
                    var (_, _, name, typeSpec) = parameters[i];
                    var bareName = NameProvider.StripVerbatimPrefix(name);
                    if (preScanHandler.IsExistential(typeSpec))
                    {
                        var protocolList = preScanHandler.ToProtocolListTypeSpec(typeSpec);
                        if (protocolList != null)
                        {
                            var heapName = $"{bareName}Heap";
                            var containerType = preScanHandler.GetCSharpExistentialType(protocolList);
                            bool hasTypeRecords = preScanHandler.AllProtocolsHaveTypeRecords(protocolList);
                            bool owningCandidate =
                                hasTypeRecords &&
                                containerType == "Swift.Runtime.ExistentialContainer1" &&
                                !preScanHandler.TryGetWellKnownProtocolType(protocolList, out _);
                            string? ownsVar = owningCandidate ? $"{bareName}Owns" : null;
                            // Change 4 (B2): an auto-wrapped proxy is registered WEAKLY now, so
                            // nothing strong roots it across the native call. Capture it here and
                            // GC.KeepAlive it in the finally so the proxy (and its construction-time
                            // R0) survives until the @_cdecl factory has consumed the container.
                            //  - EC1 owning candidate: capture the (possibly auto-wrapped) proxy via the
                            //    GetOrCreate out-keepAlive into a dedicated `object?` local.
                            //  - EC2+/well-known borrowed proxy (records present, not owning): the param
                            //    itself IS the proxy (no auto-wrap), so pin the param directly — no backing
                            //    local. (Unknown protocols with no records pass a raw blittable container
                            //    with no R0 to protect, so no pin.)
                            string? keepAliveVar =
                                owningCandidate ? $"{bareName}KeepAlive"
                                : hasTypeRecords ? name
                                : null;
                            existentialHeaps.Add((heapName, ownsVar, protocolList.Protocols.Count, keepAliveVar));
                            csWriter.WriteLine($"void* {heapName} = null;");
                            if (ownsVar != null)
                                csWriter.WriteLine($"bool {ownsVar} = false;");
                            if (owningCandidate)
                                csWriter.WriteLine($"object? {bareName}KeepAlive = null;");
                        }
                    }
                }
            }

            bool hasExistentialHeap = existentialHeaps.Count > 0;
            if (hasExistentialHeap)
            {
                csWriter.WriteLine("try");
                csWriter.WriteLine("{");
                csWriter.Indent++;
            }

            // @_cdecl wrappers need additional setup for existential and tuple params:
            // - Strings: handled above (UTF-8 encoding), no extra setup needed
            // - Existentials: extract container into local, heap-allocate for pass-by-pointer (UnsafeRawPointer in Swift)
            int existentialIndex = 0;
            if (useCdeclWrapper)
            {
                var existentialHandler = new ExistentialHandler(typeDatabase);
                for (int i = 0; i < parameters.Count; i++)
                {
                    var (_, _, name, typeSpec) = parameters[i];
                    var bareName = NameProvider.StripVerbatimPrefix(name);
                    if (existentialHandler.IsExistential(typeSpec))
                    {
                        var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
                        if (protocolList != null)
                        {
                            var containerType = existentialHandler.GetCSharpExistentialType(protocolList);
                            if (existentialHandler.AllProtocolsHaveTypeRecords(protocolList))
                            {
                                // GetOrCreate only works for single-protocol (EC1) interfaces.
                                if (containerType == "Swift.Runtime.ExistentialContainer1" && !existentialHandler.TryGetWellKnownProtocolType(protocolList, out _))
                                {
                                    var publicType = existentialHandler.GetPublicExistentialType(protocolList);
                                    // Auto-wrap fallback only when a proxy class is actually emitted
                                    // (skips Swift stdlib protocols like Encodable that project to "object").
                                    string? proxyClassName = null;
                                    if (publicType != "object" &&
                                        existentialHandler.TryGetFilteredProxyClassName(protocolList, out var filteredProxy))
                                    {
                                        proxyClassName = existentialHandler.QualifyProxyClassName(filteredProxy, protocolList);
                                    }
                                    // Thread the runtime owns-bit so the finally can run
                                    // the existential value-witness destroy only when a value
                                    // conformer was boxed at +1 (borrowed proxy/class containers
                                    // report owns=false and must not be over-released).
                                    var expr = proxyClassName != null
                                        ? $"Swift.Runtime.ExistentialContainerFactory.GetOrCreate<{publicType}>({name}, static __v => new {proxyClassName}(__v), out {bareName}Owns, out {bareName}KeepAlive)"
                                        : $"Swift.Runtime.ExistentialContainerFactory.GetOrCreate<{publicType}>({name}, out {bareName}Owns, out {bareName}KeepAlive)";
                                    csWriter.WriteLine($"var {bareName}Container = {expr};");
                                }
                                else
                                    csWriter.WriteLine($"var {bareName}Container = ((Swift.Runtime.ISwiftExistentialConvertible<{containerType}>){name}).GetExistentialContainer();");
                            }
                            else
                            {
                                // Unknown protocol: container is already the right type
                                csWriter.WriteLine($"var {bareName}Container = {name};");
                            }
                            // Heap-allocate the container to avoid NativeAOT stack reuse issues
                            // (same fix as WrapperEmitter.EmitExistentialContainerMarshalling)
                            var heapName = existentialHeaps[existentialIndex++].HeapName;
                            csWriter.WriteLine($"{heapName} = NativeMemory.Alloc((nuint)Unsafe.SizeOf<{containerType}>());");
                            csWriter.WriteLine($"Unsafe.Copy({heapName}, ref {bareName}Container);");
                        }
                    }
                    else if (typeSpec is TupleTypeSpec)
                    {
                        // @_cdecl: tuples are passed as UnsafeRawPointer in Swift.
                        // Store the tuple value in a local so we can take its address.
                        // Tuples with projected elements (string, existential, container) are
                        // gated out by ShouldEmitCaseFactoryWrapper → IsTupleElementAbiCompatible,
                        // so only ABI-identical tuples (primitives, frozen structs, etc.) reach here.
                        csWriter.WriteLine($"var {bareName}Tuple = {name};");
                    }
                }
            }

            // Swift enum case constructors use indirect return - allocate buffer and pass it.
            // Use the type-metadata-accessor argument list (which includes PWTs for any
            // protocol-constrained generic params) since this calls the metadata accessor
            // PInvoke that carries both metadata and witness-table arguments.
            var getMetadataCall = pinvokeHelperContext != null
                ? $"{pinvokeHelperContext.HelperClassName}.PInvoke_getMetadata(TypeMetadataRequest.Complete, {string.Join(", ", pinvokeHelperContext.GetTypeMetadataAccessorArgumentList())})"
                : "PInvoke_getMetadata()";
            csWriter.WriteLine($"var metadata = {getMetadataCall};");
            csWriter.WriteLine($"IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);");

            if (!useCdeclWrapper)
            {
                csWriter.WriteLine($"var indirectResult = new SwiftIndirectResult((void*)buffer);");
            }

            // Build the P/Invoke call with arguments
            // For @_cdecl wrappers: associated value args first, resultPtr (buffer) last
            // For direct P/Invoke: indirectResult first, then associated value args
            var argList = new List<string>();
            if (!useCdeclWrapper)
            {
                argList.Add("indirectResult");
            }

            var enumGenericParamsForArgs = enumDecl.IsGeneric ? enumDecl.GenericParameters : null;
            for (int i = 0; i < parameters.Count; i++)
            {
                var (type, _, name, typeSpec) = parameters[i];
                var bareName = NameProvider.StripVerbatimPrefix(name);
                // Recognize generic-T params via the same helper used by GetCSharpTypeNameForEnumCase
                // so Apple-shape sugared names ("SignedType") take this path. Otherwise the
                // factory body falls through to GetPInvokeArgument and produces
                // ".Payload.DangerousGetHandle()" on a bare TSignedType, which doesn't compile.
                if (typeSpec is NamedTypeSpec genericParamType &&
                    TryGetGenericTypeParameterName(genericParamType.Name, out _, enumGenericParamsForArgs))
                {
                    csWriter.WriteLine($"var {bareName}Metadata = TypeMetadata.GetTypeMetadataOrThrow<{type}>();");
                    csWriter.WriteLine($"byte* {bareName}SwiftBuffer = stackalloc byte[(int){bareName}Metadata.Size];");
                    csWriter.WriteLine($"var {bareName}SwiftSpan = new Span<byte>({bareName}SwiftBuffer, (int){bareName}Metadata.Size);");
                    csWriter.WriteLine($"SwiftMarshal.MarshalToSwift({name}, ref {bareName}SwiftSpan);");
                    argList.Add($"(IntPtr){bareName}SwiftBuffer");
                }
                else if (projectedArgs.TryGetValue(i, out var projPlan))
                {
                    argList.Add(projPlan.PInvokeExpression);
                }
                else if (useCdeclWrapper && typeSpec is TupleTypeSpec)
                {
                    // @_cdecl: pass tuple by pointer (matches Swift's UnsafeRawPointer param)
                    argList.Add($"(IntPtr)(&{bareName}Tuple)");
                }
                else if (tuplePInvokeExprs.TryGetValue(i, out var tupleExpr))
                {
                    argList.Add(tupleExpr);
                }
                else if (useCdeclWrapper && typeConversionHandler.IsSwiftString(typeSpec))
                {
                    // @_cdecl: pass UTF-8 pointer + length (wrapped in fixed block below)
                    argList.Add($"(IntPtr)__{bareName}Ptr");
                    argList.Add($"__{bareName}Utf8.Length");
                }
                else if (useCdeclWrapper && new ExistentialHandler(typeDatabase).IsExistential(typeSpec))
                {
                    // @_cdecl: pass heap-allocated pointer to container (matches Swift's UnsafeRawPointer param)
                    argList.Add($"(IntPtr){bareName}Heap");
                }
                else if (useCdeclWrapper && IsCustomFrozenStructParam(typeSpec, typeDatabase))
                {
                    // @_cdecl: custom frozen structs are passed as UnsafeRawPointer on the Swift side.
                    // Marshal to a stack buffer and pass the pointer. This is required because
                    // C calling convention passes float/double struct fields in FPR, but
                    // UnsafeRawPointer expects a pointer in GPR — ABI mismatch.
                    csWriter.WriteLine($"var {bareName}Metadata = TypeMetadata.GetTypeMetadataOrThrow<{type}>();");
                    csWriter.WriteLine($"byte* {bareName}Buffer = stackalloc byte[(int){bareName}Metadata.Size];");
                    csWriter.WriteLine($"var {bareName}Span = new Span<byte>({bareName}Buffer, (int){bareName}Metadata.Size);");
                    csWriter.WriteLine($"SwiftMarshal.MarshalToSwift({name}, ref {bareName}Span);");
                    argList.Add($"(IntPtr){bareName}Buffer");
                }
                else
                {
                    var isConvertedToLocal = typeConversionHandler.IsSwiftString(typeSpec) ||
                        (typeSpec is NamedTypeSpec ds && ds.Name == "Foundation.Data") ||
                        (typeSpec is NamedTypeSpec dts && dts.Name == "Foundation.Date");
                    var argName = isConvertedToLocal ? $"__{bareName}" : name;
                    argList.Add(GetPInvokeArgument(argName, typeSpec, typeDatabase));
                }
            }

            if (useCdeclWrapper)
            {
                argList.Add("buffer"); // resultPtr as last arg for @_cdecl
            }

            var invokeArgList = string.Join(", ", argList);

            // Collect string params that need fixed blocks for UTF-8 byte pinning
            var stringFixedParams = new List<string>();
            if (useCdeclWrapper)
            {
                for (int i = 0; i < parameters.Count; i++)
                {
                    var (_, _, name, typeSpec) = parameters[i];
                    if (typeConversionHandler.IsSwiftString(typeSpec))
                        stringFixedParams.Add(NameProvider.StripVerbatimPrefix(name));
                }
            }

            // Wrap P/Invoke call in fixed blocks for string UTF-8 byte arrays
            if (stringFixedParams.Count > 0)
            {
                csWriter.WriteLine("unsafe {");
                csWriter.Indent++;
                foreach (var bareName in stringFixedParams)
                {
                    csWriter.WriteLine($"fixed (byte* __{bareName}Ptr = __{bareName}Utf8) {{");
                    csWriter.Indent++;
                }
            }

            if (pinvokeHelperContext != null)
            {
                var metadataArgs = string.Join(", ", pinvokeHelperContext.GetMetadataArgumentList());
                invokeArgList = string.IsNullOrEmpty(invokeArgList) ? metadataArgs : $"{invokeArgList}, {metadataArgs}";
                csWriter.WriteLine($"{pinvokeHelperContext.HelperClassName}.{pInvokeName}({invokeArgList});");
            }
            else
            {
                csWriter.WriteLine($"{pInvokeName}({invokeArgList});");
            }

            // Close fixed blocks
            if (stringFixedParams.Count > 0)
            {
                for (int i = 0; i < stringFixedParams.Count; i++)
                {
                    csWriter.Indent--;
                    csWriter.WriteLine("}");
                }
                csWriter.Indent--;
                csWriter.WriteLine("}");
            }

            csWriter.WriteLine($"{resultVarName}._payload = new SwiftSafeHandle<{enumTypeName}>(buffer);");
            csWriter.WriteLine($"return {resultVarName};");

            // Close try/finally for existential heap cleanup
            if (hasExistentialHeap)
            {
                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine("finally");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                foreach (var (heapName, ownsVar, witnessCount, keepAliveVar) in existentialHeaps)
                {
                    // Route through the centralized helper — it null-checks the heap,
                    // runs the existential value-witness destroy only when owns==true (the
                    // enum-case factory borrows the @in_guaranteed buffer like every other
                    // existential-param site), and frees the buffer.
                    csWriter.WriteLine($"Swift.Runtime.ExistentialContainerFactory.DestroyAndFreeExistential({heapName}, {witnessCount}, {ownsVar ?? "false"});");
                    // Change 4 (B2): pin the borrowed proxy until the native consumer is done.
                    if (keepAliveVar != null)
                        csWriter.WriteLine($"global::System.GC.KeepAlive({keepAliveVar});");
                }
                csWriter.Indent--;
                csWriter.WriteLine("}");
            }

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            // P/Invoke declaration
            if (useCdeclWrapper)
            {
                // @_cdecl wrapper: C calling convention, associated value params + IntPtr resultPtr
                // Types must match the Swift wrapper ABI from GetCdeclParamMapping:
                // - Strings: IntPtr + int (UTF-8 pointer + length, NativeAOT-safe)
                // - Existentials: IntPtr (pointer to ExistentialContainer, matches Unsafe.AsPointer at call site)
                // - Everything else: same as legacy GetPInvokeType
                var pInvokeParams = new List<string>();
                var cdeclExistentialHandler = new ExistentialHandler(typeDatabase);
                for (int i = 0; i < parameters.Count; i++)
                {
                    var (_, _, name, typeSpec) = parameters[i];
                    if (typeConversionHandler.IsSwiftString(typeSpec))
                    {
                        pInvokeParams.Add($"IntPtr {name}Utf8Ptr");
                        pInvokeParams.Add($"nint {name}Utf8Len");
                    }
                    else if (cdeclExistentialHandler.IsExistential(typeSpec))
                    {
                        // Pass as IntPtr — call site uses heap-allocated container pointer
                        pInvokeParams.Add($"IntPtr {name}");
                    }
                    else if (typeSpec is TupleTypeSpec)
                    {
                        // @_cdecl: tuples pass as UnsafeRawPointer in Swift, IntPtr in C#
                        pInvokeParams.Add($"IntPtr {name}");
                    }
                    else if (IsCustomFrozenStructParam(typeSpec, typeDatabase))
                    {
                        // @_cdecl: custom frozen structs pass as UnsafeRawPointer in Swift, IntPtr in C#
                        pInvokeParams.Add($"IntPtr {name}");
                    }
                    else
                    {
                        var pInvokeType = GetPInvokeType(typeSpec, typeDatabase, enumGenericParamsForArgs);
                        var marshalPrefix = MarshallingHelpers.IsBoolType(pInvokeType) ? "[MarshalAs(UnmanagedType.U1)] " : "";
                        pInvokeParams.Add($"{marshalPrefix}{pInvokeType} {name}");
                    }
                }
                pInvokeParams.Add("IntPtr resultPtr"); // Result pointer as last param

                // @_cdecl case factory symbols live in the wrapper library, not the original
                var caseFactoryLibPath = typeDatabase.AsyncLibraryName ?? libPath;

                if (pinvokeHelperContext != null)
                {
                    pinvokeHelperContext.AddDeclaration(new PInvokeDeclaration
                    {
                        LibraryPath = caseFactoryLibPath,
                        EntryPoint = cdeclSymbol!,
                        MethodName = pInvokeName,
                        ReturnType = "void",
                        ParametersString = string.Join(", ", pInvokeParams),
                        IsAsync = false,
                        MetadataParameters = pinvokeHelperContext.GetMetadataParameterDeclarations()
                    });
                }
                else
                {
                    PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
                    {
                        LibraryPath = caseFactoryLibPath,
                        EntryPoint = cdeclSymbol!,
                        MethodName = pInvokeName,
                        ReturnType = "void",
                        ParametersString = string.Join(", ", pInvokeParams),
                        CallingConvention = PInvokeCallingConvention.Cdecl
                    });
                    csWriter.WriteLine();
                }
            }
            else
            {
                // Direct P/Invoke path with SwiftIndirectResult
                // C5: Use unique name for indirect result param to avoid CS0100 if an associated value
                // is also named "result"
                var indirectResultParamName = parameters.Any(p => p.name == "result") ? "__result" : "result";
                var pInvokeParams = new List<string> { $"SwiftIndirectResult {indirectResultParamName}" };
                for (int i = 0; i < parameters.Count; i++)
                {
                    var (_, _, name, typeSpec) = parameters[i];
                    var pInvokeType = GetPInvokeType(typeSpec, typeDatabase, enumGenericParamsForArgs);
                    var marshalPrefix = MarshallingHelpers.IsBoolType(pInvokeType) ? "[MarshalAs(UnmanagedType.U1)] " : "";
                    pInvokeParams.Add($"{marshalPrefix}{pInvokeType} {name}");
                }

                if (pinvokeHelperContext != null)
                {
                    pinvokeHelperContext.AddDeclaration(new PInvokeDeclaration
                    {
                        LibraryPath = libPath,
                        EntryPoint = caseDecl.MangledName,
                        MethodName = pInvokeName,
                        ReturnType = "void",
                        ParametersString = string.Join(", ", pInvokeParams),
                        IsAsync = false,
                        MetadataParameters = pinvokeHelperContext.GetMetadataParameterDeclarations(),
                        CallingConvention = PInvokeCallingConvention.Swift
                    });
                }
                else
                {
                    PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
                    {
                        LibraryPath = libPath,
                        EntryPoint = caseDecl.MangledName,
                        MethodName = pInvokeName,
                        ReturnType = "void",
                        ParametersString = string.Join(", ", pInvokeParams),
                        CallingConvention = PInvokeCallingConvention.Swift
                    });
                    csWriter.WriteLine();
                }
            }
            return true;
        }

        /// <summary>
        /// Gets the C# type name for an enum case associated value type.
        /// </summary>
        private static string GetCSharpTypeNameForEnumCase(TypeSpec typeSpec, ITypeDatabase typeDatabase, BoundGenericsHandler boundGenericsHandler,
            IReadOnlyList<GenericArgumentDecl>? genericParams = null, ModuleDecl? moduleDecl = null)
        {
            // TryGetGenericTypeParameterName handles τ_X_Y, T+digit, AND multi-character
            // sugared declarator names (e.g. "SignedType" for VerificationResult<SignedType>
            // from Apple framework ABI JSON). It returns false for non-generic-param names,
            // so calling it unconditionally is safe — non-matches fall through to the typedb
            // lookup below. Pre-gating with IsGenericTypeParameter (single-letter shortlist)
            // would re-introduce the regression that hides Apple-shape sugared names from
            // both TryGet emission AND case-factory emission.
            if (typeSpec is NamedTypeSpec genericParamType &&
                TryGetGenericTypeParameterName(genericParamType.Name, out var typeParameterName, genericParams))
            {
                return typeParameterName;
            }

            // Handle existential types (any Protocol) - return ExistentialContainer
            var existentialHandler = new ExistentialHandler(typeDatabase);
            if (existentialHandler.IsExistential(typeSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null)
                {
                    return existentialHandler.GetCSharpExistentialType(protocolList);
                }
            }

            // Handle protocol list types (protocol composition)
            if (typeSpec is ProtocolListTypeSpec protocolListSpec)
            {
                return existentialHandler.GetCSharpExistentialType(protocolListSpec);
            }

            // Handle bound generics (e.g., Optional<T>, Array<T>)
            // Use TranslateBoundGenericTypeToCSharp (NOT factory) to produce raw ABI type names
            // (e.g., SwiftArray<string>, SwiftOptional<SwiftResult<...>>). The factory produces
            // public types (IReadOnlyList<string>) which don't have .Payload for P/Invoke access.
            if (typeSpec is NamedTypeSpec namedTypeSpec && namedTypeSpec.ContainsGenericParameters)
            {
                var genericContext = genericParams != null
                    ? BuildGenericContextFromEnumParams(genericParams)
                    : GenericContext.Empty;
                return boundGenericsHandler.TranslateBoundGenericTypeToCSharp(typeSpec, genericContext, moduleDecl);
            }

            // Handle tuple types
            if (typeSpec is TupleTypeSpec tupleType)
            {
                var tupleHandler = new TupleHandler(typeDatabase);
                // Use a recursive translator that handles bound generics for each element
                return tupleHandler.GetCSharpTupleType(tupleType, elementTypeSpec =>
                    GetCSharpTypeNameForEnumCase(elementTypeSpec, typeDatabase, boundGenericsHandler, genericParams, moduleDecl));
            }

            // For non-generic types, use the standard lookup
            return typeDatabase.GetTypeRecordOrAnyType(typeSpec).CSharpTypeName.FullyQualifiedName;
        }

        /// <summary>
        /// Gets the public C# type name for an enum case associated value type.
        /// For existentials where all protocols have TypeRecords, returns the interface type (e.g., "IImageProcessing").
        /// For existentials with unknown/unregistered protocols, falls back to the container type (ExistentialContainer{N}).
        /// For tuples, recurses per element.
        /// For everything else, delegates to GetCSharpTypeNameForEnumCase.
        /// </summary>
        private static string GetPublicCSharpTypeNameForEnumCase(TypeSpec typeSpec, ITypeDatabase typeDatabase, BoundGenericsHandler boundGenericsHandler,
            IReadOnlyList<GenericArgumentDecl>? genericParams = null, ModuleDecl? moduleDecl = null)
        {
            var existentialHandler = new ExistentialHandler(typeDatabase);
            var typeConversionHandler = new TypeConversionHandler(typeDatabase);

            // Handle existential types (any Protocol) - return interface if all protocols are known
            if (existentialHandler.IsExistential(typeSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null && existentialHandler.AllProtocolsHaveTypeRecords(protocolList))
                {
                    return existentialHandler.GetPublicExistentialType(protocolList);
                }
            }

            // Handle protocol list types (protocol composition)
            if (typeSpec is ProtocolListTypeSpec protocolListSpec)
            {
                if (existentialHandler.AllProtocolsHaveTypeRecords(protocolListSpec))
                {
                    return existentialHandler.GetPublicExistentialType(protocolListSpec);
                }
            }

            // Handle tuple types - recurse per element using idiomatic projection.
            // TupleProjection.GetParameterPlan() handles per-element conversion in the body.
            if (typeSpec is TupleTypeSpec tupleType)
            {
                var tupleHandler = new TupleHandler(typeDatabase);
                return tupleHandler.GetCSharpTupleType(tupleType, elementTypeSpec =>
                    GetPublicCSharpTypeNameForEnumCase(elementTypeSpec, typeDatabase, boundGenericsHandler, genericParams, moduleDecl));
            }

            // Handle SwiftString → string for public API
            if (typeConversionHandler.IsSwiftString(typeSpec))
                return "string";

            // Handle Foundation.Data → byte[] for public API
            if (typeSpec is NamedTypeSpec dataType && dataType.Name == "Foundation.Data")
                return "byte[]";

            // Handle Foundation.Date → DateTimeOffset for public API
            if (typeSpec is NamedTypeSpec dateType && dateType.Name == "Foundation.Date")
                return "System.DateTimeOffset";

            // Handle bound generics (Optional<T>, Array<T>, Dictionary<K,V>) via factory
            // The factory produces idiomatic public types (string?, IReadOnlyList<T>, IReadOnlyDictionary<K,V>).
            // Always use IsParameter=false so types are consistent between construction (factory methods)
            // and deconstruction (TryGet out parameters). IReadOnlyList/IReadOnlyDictionary work for both
            // since construction uses IEnumerable-based conversion and TryGet uses AsProjected.
            // Skip closure types — delegate* can't be used as generic type arguments in MarshalFromSwift<T>.
            // Use recursive check to catch nested closures like Optional<Array<Closure>>.
            if (typeSpec is NamedTypeSpec namedBoundGeneric && namedBoundGeneric.ContainsGenericParameters
                && !ContainsClosureTypeSpec(namedBoundGeneric))
            {
                var genericContext = genericParams != null
                    ? BuildGenericContextFromEnumParams(genericParams)
                    : GenericContext.Empty;
                var projection = new TypeProjectionFactory().Project(typeSpec, new ProjectionContext
                {
                    TypeDatabase = typeDatabase,
                    IsParameter = false,
                    GenericContext = genericContext
                });
                if (projection != null)
                    return projection.PublicType;
            }

            // ObjC-bridgeable and native-remapped types: use the public native type (e.g., NSUrl, NSUrlRequest)
            if (typeSpec is NamedTypeSpec namedForNative)
            {
                var nativeRecord = typeDatabase.GetTypeRecordOrAnyType(namedForNative);
                if (nativeRecord.NativeTypeName != null)
                    return nativeRecord.NativeTypeName.FullyQualifiedName;
            }

            // Everything else: delegate to the internal type
            return GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, genericParams, moduleDecl);
        }

        /// <summary>
        /// Gets the P/Invoke argument expression for a parameter.
        /// </summary>
        private static string GetPInvokeArgument(string paramName, TypeSpec typeSpec, ITypeDatabase typeDatabase)
        {
            if (typeSpec is NamedTypeSpec genericParamType &&
                TypeSpecHelpers.IsGenericTypeParameter(genericParamType.Name))
            {
                return $"{paramName}.Payload.DangerousGetHandle()";
            }

            // Handle existential types
            var existentialHandler = new ExistentialHandler(typeDatabase);
            if (existentialHandler.IsExistential(typeSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null && existentialHandler.AllProtocolsHaveTypeRecords(protocolList))
                {
                    var containerType = existentialHandler.GetCSharpExistentialType(protocolList);
                    // GetOrCreate only works for single-protocol (EC1) interfaces
                    if (containerType == "Swift.Runtime.ExistentialContainer1" && !existentialHandler.TryGetWellKnownProtocolType(protocolList, out _))
                    {
                        var publicType = existentialHandler.GetPublicExistentialType(protocolList);
                        // Auto-wrap fallback only when a proxy class is actually emitted
                        // (skips Swift stdlib protocols like Encodable that project to "object").
                        string? proxyClassName = null;
                        if (publicType != "object" &&
                            existentialHandler.TryGetFilteredProxyClassName(protocolList, out var filteredProxy))
                        {
                            proxyClassName = existentialHandler.QualifyProxyClassName(filteredProxy, protocolList);
                        }
                        return proxyClassName != null
                            ? $"Swift.Runtime.ExistentialContainerFactory.GetOrCreate<{publicType}>({paramName}, static __v => new {proxyClassName}(__v))"
                            : $"Swift.Runtime.ExistentialContainerFactory.GetOrCreate<{publicType}>({paramName})";
                    }
                    return $"((Swift.Runtime.ISwiftExistentialConvertible<{containerType}>){paramName}).GetExistentialContainer()";
                }
                // Unknown protocol: pass the container directly (it's a blittable struct)
                return paramName;
            }

            // Handle tuple types - need to construct a ValueTuple with extracted payloads
            if (typeSpec is TupleTypeSpec tupleType)
            {
                var elementArgs = new List<string>();
                for (int i = 0; i < tupleType.Elements.Count; i++)
                {
                    var element = tupleType.Elements[i];
                    // Access tuple element by name if it has a label, otherwise by Item1, Item2, etc.
                    var elementAccess = !string.IsNullOrEmpty(element.TypeLabel)
                        ? $"{paramName}.{element.TypeLabel}"
                        : $"{paramName}.Item{i + 1}";

                    // Recursively get the P/Invoke argument for this element
                    elementArgs.Add(GetPInvokeArgument(elementAccess, element, typeDatabase));
                }
                return $"({string.Join(", ", elementArgs)})";
            }

            var typeRecord = typeDatabase.GetTypeRecordOrAnyType(typeSpec);

            // ObjC bridged/rooted/bridgeable types use .Handle to get the native pointer
            if (MarshallingHelpers.IsObjCBridged(typeRecord) ||
                MarshallingHelpers.IsObjCRooted(typeRecord) ||
                MarshallingHelpers.IsObjCBridgeable(typeRecord))
            {
                return $"{paramName}.Handle";
            }

            // Enum values are projected as managed wrappers with SafeHandle payload.
            // Extract the raw pointer for P/Invoke.
            if (typeRecord.Kind == TypeRecordKind.Enum)
            {
                if (typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
                {
                    var underlyingType = EnumHandler.GetCSharpEnumUnderlyingType(typeRecord.RawValueTypeName);
                    return $"({underlyingType}){paramName}";
                }
                return $"{paramName}.Payload.DangerousGetHandle()";
            }

            // For types that have payloads (non-frozen structs, classes), access the Payload.DangerousGetHandle()
            if (MarshallingHelpers.RequiresMemoryManagement(typeRecord))
            {
                return $"{paramName}.Payload.DangerousGetHandle()";
            }

            // Non-frozen structs (ClassWithOpaquePayload) — extract SafeHandle payload
            if (typeRecord.Kind == TypeRecordKind.Struct && !MarshallingHelpers.IsTypeFrozen(typeRecord))
            {
                return $"{paramName}.Payload.DangerousGetHandle()";
            }

            // Swift classes — extract SafeHandle payload
            if (typeRecord.Kind == TypeRecordKind.Class)
            {
                return $"{paramName}.Payload.DangerousGetHandle()";
            }

            // AnyType fallback — extract SafeHandle payload. Reached when an unknown type
            // appears inside a tuple (GetPInvokeArgument recurses per element; the unknown
            // element resolves to AnyType which has Kind=Protocol). Direct AnyType associated
            // values are caught earlier (line 32 skip). Protocol existentials use
            // ProtocolListTypeSpec handled by the existential path above.
            if (typeRecord == TypeDatabaseExtensions.AnyType)
            {
                return $"{paramName}.Payload.DangerousGetHandle()";
            }

            return paramName;
        }

        /// <summary>
        /// Returns true if a type is a custom frozen struct that needs pointer passing
        /// in @_cdecl wrappers. The Swift wrapper receives these as UnsafeRawPointer
        /// (not by-value) because custom structs are not C-representable in @_cdecl.
        /// System framework frozen structs (CGRect, etc.) are C-representable and pass by-value.
        /// </summary>
        private static bool IsCustomFrozenStructParam(TypeSpec typeSpec, ITypeDatabase typeDatabase)
        {
            if (typeSpec is not NamedTypeSpec named) return false;
            if (!typeDatabase.TryGetTypeRecord(named, out var record)) return false;
            if (record.Kind != TypeRecordKind.Struct) return false;
            if (!MarshallingHelpers.IsTypeFrozen(record)) return false;
            if (MarshallingHelpers.RequiresMemoryManagement(record)) return false;
            // System structs pass by-value — only custom structs need pointer
            if (CdeclParamMapper.IsSystemFrozenStruct(named)) return false;
            return true;
        }

        /// <summary>
        /// Gets the P/Invoke parameter type for an associated value.
        /// </summary>
        private static string GetPInvokeType(TypeSpec typeSpec, ITypeDatabase typeDatabase,
            IReadOnlyList<GenericArgumentDecl>? genericParams = null)
        {
            // Recognize generic-T params via TryGetGenericTypeParameterName so Apple-shape
            // sugared names ("SignedType") project to IntPtr like τ_X_Y / T+digit.
            if (typeSpec is NamedTypeSpec genericParamType &&
                TryGetGenericTypeParameterName(genericParamType.Name, out _, genericParams))
            {
                return "IntPtr";
            }

            // Handle existential types - use the ExistentialContainer struct directly
            var existentialHandler = new ExistentialHandler(typeDatabase);
            if (existentialHandler.IsExistential(typeSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null)
                {
                    return existentialHandler.GetPInvokeExistentialType(protocolList);
                }
            }

            // Handle tuple types
            if (typeSpec is TupleTypeSpec tupleType)
            {
                var tupleHandler = new TupleHandler(typeDatabase);
                // Use recursive type translation for P/Invoke tuple elements
                return tupleHandler.GetPInvokeTupleType(tupleType, elementTypeSpec =>
                    GetPInvokeType(elementTypeSpec, typeDatabase, genericParams));
            }

            var typeRecord = typeDatabase.GetTypeRecordOrAnyType(typeSpec);

            // Enum values are projected as managed wrappers (C# classes with SafeHandle payload),
            // which are non-blittable for Swift calling convention P/Invoke.
            // Always use IntPtr for enum parameters.
            if (typeRecord.Kind == TypeRecordKind.Enum)
            {
                if (typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
                    return EnumHandler.GetCSharpEnumUnderlyingType(typeRecord.RawValueTypeName);
                return "IntPtr";
            }

            // For types that require memory management, use IntPtr in P/Invoke
            if (MarshallingHelpers.RequiresMemoryManagement(typeRecord))
            {
                return "IntPtr";
            }

            // Non-frozen structs (ClassWithOpaquePayload) are C# classes — non-blittable.
            if (typeRecord.Kind == TypeRecordKind.Struct && !MarshallingHelpers.IsTypeFrozen(typeRecord))
            {
                return "IntPtr";
            }

            // Swift classes are also non-blittable C# classes.
            if (typeRecord.Kind == TypeRecordKind.Class)
            {
                return "IntPtr";
            }

            // Frozen blittable structs — use the C# type directly
            if (typeRecord.Kind == TypeRecordKind.Struct && MarshallingHelpers.IsTypeFrozen(typeRecord))
            {
                return typeRecord.CSharpTypeName.FullyQualifiedName;
            }

            // Fallback — IntPtr is safe for any unknown type (Protocol/AnyType, etc.)
            return "IntPtr";
        }

        /// <summary>
        /// Determines whether the case's payload type would emit AnyType in a position
        /// that does not compile. Two patterns count as unsupported:
        /// (1) the whole single payload resolves directly to <c>Swift.AnyType</c>
        ///     (opaque value has no marshallable factory shape), and
        /// (2) a tuple element is a bound generic whose resolved name has AnyType
        ///     embedded in a generic-arg position.
        /// Plain tuple elements that resolve directly to AnyType (e.g. <c>(Int, UnknownType)</c>) stay emittable — <c>Swift.AnyType</c> has a
        /// <c>.Payload</c> property and the per-element factory body compiles.
        /// </summary>
        private static bool HasUnsupportedAnyTypeInPayload(TypeSpec typeSpec, string csharpType,
            ITypeDatabase typeDatabase, BoundGenericsHandler boundGenericsHandler,
            IReadOnlyList<GenericArgumentDecl>? enumGenericParams, ModuleDecl? moduleDecl = null)
        {
            var anyTypeName = TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
            if (typeSpec is TupleTypeSpec tuple)
            {
                foreach (var element in tuple.Elements)
                {
                    if (element is NamedTypeSpec elNamed && elNamed.ContainsGenericParameters)
                    {
                        var elName = GetCSharpTypeNameForEnumCase(element, typeDatabase, boundGenericsHandler, enumGenericParams, moduleDecl);
                        if (elName != anyTypeName && elName.Contains(anyTypeName))
                            return true;
                    }
                }
                return false;
            }

            return csharpType == anyTypeName;
        }

        /// <summary>
        /// Recursively checks whether a TypeSpec contains any ClosureTypeSpec.
        /// Closure function pointers (delegate*) cannot be used as C# generic type arguments,
        /// so containers with nested closures must skip factory projection.
        /// </summary>
        private static bool ContainsClosureTypeSpec(TypeSpec typeSpec)
        {
            if (typeSpec is ClosureTypeSpec)
                return true;
            if (typeSpec is NamedTypeSpec named)
                return named.GenericParameters.Any(ContainsClosureTypeSpec);
            if (typeSpec is TupleTypeSpec tuple)
                return tuple.Elements.Any(ContainsClosureTypeSpec);
            return false;
        }

        /// <summary>
        /// Builds a GenericContext from enum generic parameters for use with TypeProjectionFactory.
        /// Maps τ_0_0 → T0/T/TKey etc. based on the enum's GenericArgumentDecl list.
        /// </summary>
        private static GenericContext BuildGenericContextFromEnumParams(IReadOnlyList<GenericArgumentDecl> genericParams)
        {
            var mapping = new Dictionary<string, GenericParameterCSName>();
            for (int i = 0; i < genericParams.Count; i++)
            {
                var param = genericParams[i];
                var csName = NameProvider.GetCSharpGenericParameterName(param, i);
                mapping[param.TypeName] = new GenericParameterCSName(csName);
            }
            return new GenericContext(mapping);
        }

        private static bool TryGetGenericTypeParameterName(string swiftTypeName, out string typeParameterName,
            IReadOnlyList<GenericArgumentDecl>? genericParams = null)
        {
            typeParameterName = string.Empty;
            if (string.IsNullOrWhiteSpace(swiftTypeName))
                return false;

            if (swiftTypeName.StartsWith("τ_"))
            {
                var parts = swiftTypeName.Split('_');
                if (parts.Length > 0 && int.TryParse(parts[^1], out var index))
                {
                    if (genericParams != null && index < genericParams.Count)
                        typeParameterName = NameProvider.GetCSharpGenericParameterName(genericParams[index], index);
                    else
                        typeParameterName = $"T{index}";
                    return true;
                }
            }

            if (swiftTypeName.Length > 1 &&
                swiftTypeName[0] == 'T' &&
                int.TryParse(swiftTypeName.Substring(1), out _))
            {
                typeParameterName = swiftTypeName;
                return true;
            }

            // Apple framework ABI JSON encodes generic-parameter payloads as the SUGARED
            // declarator name (e.g. "SignedType" for VerificationResult<SignedType>),
            // not the τ_X_Y form swift-api-digester emits for source-compiled libraries.
            // The branches above catch the τ_ form and the synthetic "T0/T1" form;
            // this lookup matches the Apple shape against the enum's own generic
            // parameter list (both sugared and raw) and resolves to the C# parameter
            // name. Without it, GetCSharpTypeNameForEnumCase returns AnyType for
            // payload typespecs like NamedTypeSpec("SignedType") and TryGet emission
            // bails — leaving generic enums imported from Apple frameworks
            // (StoreKit2.VerificationResult<T>) without payload extractors.
            if (genericParams != null)
            {
                for (int i = 0; i < genericParams.Count; i++)
                {
                    var p = genericParams[i];
                    if (swiftTypeName == p.SugaredTypeName || swiftTypeName == p.TypeName)
                    {
                        typeParameterName = NameProvider.GetCSharpGenericParameterName(p, i);
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Sanitizes a parameter name to be a valid C# identifier.
        /// </summary>
        private static string SanitizeParameterName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "value";

            // Replace invalid characters with underscores
            var sanitized = new System.Text.StringBuilder();
            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '_')
                    sanitized.Append(c);
                else
                    sanitized.Append('_');
            }

            var result = sanitized.ToString();

            // Ensure starts with letter or underscore
            if (result.Length > 0 && char.IsDigit(result[0]))
                result = "_" + result;

            // Handle C# reserved keywords — delegate to NameProvider's canonical keyword list
            // to avoid divergence between the two keyword sets.
            result = NameProvider.EscapeForCSharpSignature(result);

            return string.IsNullOrEmpty(result) ? "value" : result;
        }

        /// <summary>
        /// Walks a TypeSpec and returns true if any generic argument (at any nesting level)
        /// names an ObjC-bridged type — either an explicit remap in AppleFrameworkRegistry
        /// (e.g. Foundation.UnitTemperature → Foundation.NSUnitTemperature) or an auto-bridged
        /// type matching a known ObjC class prefix (e.g. Security.SecPolicy) — AND the
        /// immediate container enforces a <c>where T : ISwiftObject</c> constraint that the
        /// ObjC-bridged arg would violate.
        ///
        /// Generator-emitted generic types always emit <c>where T : ISwiftObject</c>
        /// (<see cref="GenericTypeEmitter"/>). Stdlib containers hand-rolled in Swift.Runtime
        /// (<c>SwiftOptional&lt;T&gt;</c>, <c>SwiftArray&lt;Element&gt;</c>,
        /// <c>SwiftDictionary&lt;TKey,TValue&gt;</c>, <c>SwiftSet&lt;Element&gt;</c>,
        /// <c>SwiftResult&lt;TSuccess,TFailure&gt;</c>) intentionally omit it so that ObjC-bridged
        /// element types (<c>UIImage?</c>, <c>[NSURL]</c>) bind cleanly — the container marshals
        /// elements via IntPtr, no ISwiftObject conformance is required.
        ///
        /// Kept in sync with <see cref="IsObjCBridgedTypeSpec"/>.
        /// </summary>
        private static bool ContainsRemappedObjCTypeInGenericArgs(TypeSpec typeSpec)
        {
            if (typeSpec is not NamedTypeSpec named)
                return false;

            // The direct check fires only when this container actually imposes
            // `where T : ISwiftObject` on its type parameters. For hand-rolled stdlib
            // containers (Optional, Array, Dictionary, Set, Result) we still recurse into
            // nested generic args — an inner generic that IS constrained (e.g.
            // Optional<Trend<UIImage>>) must still be caught.
            bool directCheckFires = !IsStdlibContainerWithoutISwiftObjectConstraint(named.Name);

            foreach (var genericArg in named.GenericParameters)
            {
                if (directCheckFires && genericArg is NamedTypeSpec argNamed)
                {
                    if (AppleFrameworkRegistry.TryGetNetTypeName(argNamed.Name, out _))
                        return true;
                    if (AppleFrameworkRegistry.HasObjCClassPrefix(argNamed.Name))
                        return true;
                }
                // Recurse into nested generics — an inner container re-evaluates its own
                // constraint signal on its own args.
                if (ContainsRemappedObjCTypeInGenericArgs(genericArg))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Returns true for Swift stdlib container types whose hand-rolled counterparts in
        /// Swift.Runtime do NOT declare <c>where T : ISwiftObject</c>. ObjC-bridged element
        /// types used as generic args of these containers do not produce CS0311.
        /// </summary>
        private static bool IsStdlibContainerWithoutISwiftObjectConstraint(string typeName) =>
            typeName is "Swift.Optional" or "Swift.Array" or "Swift.Dictionary"
                      or "Swift.Set" or "Swift.Result" or "Swift.ClosedRange";

        /// <summary>
        /// Returns true when the TypeSpec itself names an NSObject-rooted ObjC-bridged type
        /// (either an explicit remap in AppleFrameworkRegistry or an auto-bridged type matching
        /// a known ObjC class prefix like `Sec` for Security.SecPolicy). Such types do not
        /// implement ISwiftObject, so using them in positions that require `T : ISwiftObject`
        /// (like tuple element metadata accessors) produces CS0311. Complements
        /// <see cref="ContainsRemappedObjCTypeInGenericArgs"/>, which walks generic arguments
        /// rather than the top-level type.
        /// </summary>
        private static bool IsObjCBridgedTypeSpec(TypeSpec typeSpec)
        {
            if (typeSpec is not NamedTypeSpec named)
                return false;

            if (AppleFrameworkRegistry.TryGetNetTypeName(named.Name, out _))
                return true;

            return AppleFrameworkRegistry.HasObjCClassPrefix(named.Name);
        }

        /// <summary>
        /// Mirrors the branch selection in <see cref="EmitGetTypeMetadataForElement"/>: returns
        /// true when emission would fall through to the <c>SwiftObjectHelper&lt;T&gt;.GetTypeMetadata()</c>
        /// branch (which requires <c>T : ISwiftObject</c>). Simple enums, primitives, known Apple value
        /// types, and frozen structs take safe paths via <c>TypeMetadata.GetTypeMetadataOrThrow</c>
        /// and are not affected.
        /// </summary>
        private bool WouldEmitSwiftObjectHelper(TypeSpec typeSpec, ITypeDatabase typeDatabase)
        {
            var existentialHandler = new ExistentialHandler(typeDatabase);
            if (existentialHandler.IsExistential(typeSpec))
                return false;

            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);
            var csharpType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, null);

            if (IsPrimitiveTypeWithKnownMetadata(csharpType))
                return false;

            if (TryGetSimpleEnumMetadataType(typeSpec, typeDatabase, out _))
                return false;

            if (typeSpec is NamedTypeSpec appleSpec && TypeDatabaseExtensions.IsKnownAppleValueType(appleSpec))
                return false;

            if (typeSpec is NamedTypeSpec frozenSpec && frozenSpec.HasModule() &&
                typeDatabase.TryGetTypeRecord(frozenSpec, out var frozenRecord) &&
                frozenRecord.Kind == TypeRecordKind.Struct &&
                MarshallingHelpers.IsTypeFrozen(frozenRecord) &&
                !MarshallingHelpers.IsFrozenStructProjectedAsClass(frozenRecord))
            {
                return false;
            }

            return true;
        }
    }
}
