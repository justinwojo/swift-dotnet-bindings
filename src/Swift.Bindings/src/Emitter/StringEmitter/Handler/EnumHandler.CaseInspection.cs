// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    public partial class EnumHandler
    {
        /// <summary>
        /// Emits a nested CaseTag enum for type-safe case discrimination. Each constant is the case's
        /// managed identity (<see cref="EnumDecl.GetCaseId"/>): the Swift tag of a <c>@frozen</c> enum's
        /// case (payload cases first, then no-payload cases, each in declaration order), and a value
        /// derived from the case's name otherwise, which <see cref="EmitCaseTagMap"/> translates to the
        /// running library's tag.
        /// </summary>
        private void EmitCaseTagEnum(CSharpWriter csWriter, EnumDecl enumDecl, Dictionary<string, string>? caseNameMap = null)
        {
            csWriter.WriteLine("/// <summary>");
            csWriter.WriteLine($"/// Enum representing the possible cases of {enumDecl.Name}.");
            if (enumDecl.HasNameKeyedCaseIds)
                csWriter.WriteLine("/// Each value identifies its case by name and is the same in every binding of the enum; it is not the case's Swift tag.");
            else
                csWriter.WriteLine("/// Tag values follow Swift's ordering: payload cases first, then no-payload cases.");
            csWriter.WriteLine("/// </summary>");
            csWriter.WriteLine("public enum CaseTag : uint");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // Emit payload cases first (in declaration order)
            foreach (var caseDecl in enumDecl.PayloadCases)
            {
                var capitalizedName = NameProvider.GetCaseName(caseDecl.Name, caseNameMap);
                var tag = enumDecl.GetCaseId(caseDecl);
                csWriter.WriteLine($"{capitalizedName} = {tag},");
            }

            // Then emit no-payload cases
            foreach (var caseDecl in enumDecl.NoPayloadCases)
            {
                var capitalizedName = NameProvider.GetCaseName(caseDecl.Name, caseNameMap);
                var tag = enumDecl.GetCaseId(caseDecl);
                csWriter.WriteLine($"{capitalizedName} = {tag},");
            }

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();
        }

        /// <summary>
        /// Computes the emitted name of the case-discriminator property (normally "Tag").
        /// A property literally named "Tag" is illegal when the enclosing enum's own C# type
        /// name is also "Tag" (CS0542: a member cannot share the name of its enclosing type),
        /// which happens for a Swift enum named <c>Tag</c> (e.g. rive-ios <c>RiveLog.Tag</c>).
        /// In that case a deterministic non-colliding fallback is chosen, guarded against the
        /// nested <c>CaseTag</c> enum and every case-derived member name so the fallback can
        /// never itself collide.
        /// </summary>
        internal static string GetTagPropertyName(EnumDecl enumDecl, string typeNameWithGenerics, Dictionary<string, string>? caseNameMap, ITypeDatabase? typeDatabase = null, Dictionary<string, string>? propertyRenames = null)
        {
            const string preferred = NameProvider.CaseDiscriminatorName;

            // The enclosing C# type's simple name is typeNameWithGenerics minus any generic list.
            var simpleName = typeNameWithGenerics;
            var lt = simpleName.IndexOf('<');
            if (lt >= 0)
                simpleName = simpleName[..lt];

            if (!string.Equals(simpleName, preferred, StringComparison.Ordinal))
                return preferred;

            // CS0542 collision: the enclosing type is itself named "Tag". Choose a deterministic
            // alternative that avoids the type name, the nested CaseTag enum, every case member name
            // (case factories and TryGet{Case} helpers are not renamed around this property), and the
            // emitted leaf name of every nested type (a nested type cannot take a Value-suffix rename,
            // so the discriminator must dodge it). User properties and methods, by contrast, yield TO
            // this name — a colliding property is recovered with the Value-suffix idiom in the caller
            // and a colliding method is renamed via the method-name-shaping propertyNames set — so they
            // are deliberately absent from this guard set.
            var reserved = new HashSet<string>(StringComparer.Ordinal) { simpleName, "CaseTag" };
            foreach (var c in enumDecl.Cases)
            {
                var caseName = NameProvider.GetCaseName(c.Name, caseNameMap);
                reserved.Add(caseName);
                // A case named like the enum emits under its member-side rename, which
                // ComputePropertyRenames keeps off this fallback's first choice.
                reserved.Add(NameProvider.GetFinalMemberName(caseName, propertyRenames));
                if (c.HasAssociatedValues)
                    reserved.Add($"TryGet{caseName}");
            }
            foreach (var nestedType in enumDecl.Types)
                reserved.Add(NameProvider.GetEmittedNestedTypeLeafName(nestedType, typeDatabase));

            var candidate = NameProvider.CaseDiscriminatorFallbackName;
            var n = 2;
            while (reserved.Contains(candidate))
                candidate = $"Tag{n++}";
            return candidate;
        }

        /// <summary>The private property holding an enum's <see cref="Swift.Runtime.SwiftEnumCaseMap"/>.</summary>
        internal const string CaseTagMapField = "_swiftCaseTagMap";

        /// <summary>
        /// Emits the map between the managed identity of each case and the tag the running library
        /// gives it (<c>Swift.Runtime.SwiftEnumCaseMap</c>), for an enum with
        /// <see cref="EnumDecl.HasNameKeyedCaseIds"/>. Every case is in it, a case the binding does not
        /// emit included, so a value of that case is read as no case the binding names rather than as
        /// another one. The map is created on first use: cached cases are field initializers that may
        /// come before it in the type, so a field would still be null when they read it.
        /// </summary>
        private static void EmitCaseTagMap(CSharpWriter csWriter, EnumDecl enumDecl, string libraryPath)
        {
            csWriter.WriteLine($"private static global::Swift.Runtime.SwiftEnumCaseMap? {CaseTagMapField}Value;");
            csWriter.WriteLine();
            csWriter.WriteLine("/// <summary>The tags the running library gives the cases, by case identity.</summary>");
            csWriter.WriteLine($"private static global::Swift.Runtime.SwiftEnumCaseMap {CaseTagMapField} => {CaseTagMapField}Value ??= new(");
            csWriter.Indent++;
            csWriter.WriteLine($"\"{libraryPath}\",");
            csWriter.WriteLine($"new string[] {{ {string.Join(", ", enumDecl.Cases.Select(c => $"\"{EnumDecl.GetCaseTagSymbol(c)}\""))} }},");
            csWriter.WriteLine($"new uint[] {{ {string.Join(", ", enumDecl.Cases.Select(c => $"{enumDecl.GetCaseId(c)}u"))} }},");
            csWriter.WriteLine($"new uint[] {{ {string.Join(", ", enumDecl.Cases.Select(c => $"{enumDecl.GetCaseTag(c)}u"))} }});");
            csWriter.Indent--;
            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits the Tag property that returns the current case of the enum.
        /// Uses ValueWitnessTable->GetEnumTag to determine the case, translated to the case's
        /// managed identity through <see cref="EmitCaseTagMap"/> when the constants are name-keyed.
        /// </summary>
        private void EmitTagProperty(CSharpWriter csWriter, EnumDecl enumDecl, string typeNameWithGenerics, string tagPropertyName, string libraryPath)
        {
            if (enumDecl.HasNameKeyedCaseIds)
                EmitCaseTagMap(csWriter, enumDecl, libraryPath);

            csWriter.WriteLine("/// <summary>");
            csWriter.WriteLine("/// Gets the current case of this enum instance.");
            csWriter.WriteLine("/// </summary>");
            csWriter.WriteLine($"public CaseTag {tagPropertyName}");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("get");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("unsafe");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            csWriter.WriteLine("bool success = false;");
            csWriter.WriteLine("_payload.DangerousAddRef(ref success);");
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            csWriter.WriteLine($"var metadata = SwiftObjectHelper<{typeNameWithGenerics}>.GetTypeMetadata();");
            csWriter.WriteLine("byte* payload = (byte*)_payload.DangerousGetHandle();");
            csWriter.WriteLine(enumDecl.HasNameKeyedCaseIds
                ? $"return (CaseTag){CaseTagMapField}.ToCaseId(metadata.ValueWitnessTable->GetEnumTag(payload, metadata));"
                : "return (CaseTag)metadata.ValueWitnessTable->GetEnumTag(payload, metadata);");

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("finally");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("if (success)");
            csWriter.Indent++;
            csWriter.WriteLine("_payload.DangerousRelease();");
            csWriter.Indent--;
            csWriter.Indent--;
            csWriter.WriteLine("}");

            csWriter.Indent--;
            csWriter.WriteLine("}"); // unsafe
            csWriter.Indent--;
            csWriter.WriteLine("}"); // get
            csWriter.Indent--;
            csWriter.WriteLine($"}}"); // {tagPropertyName}
            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits a TryGet method for an enum case with associated values.
        /// The method extracts the associated value(s) if the enum is in the specified case.
        /// </summary>
        private void EmitTryGetMethod(CSharpWriter csWriter, EnumDecl enumDecl, EnumCaseDecl caseDecl, ITypeDatabase typeDatabase, string typeNameWithGenerics, string tagPropertyName, Dictionary<string, string>? caseNameMap = null, ModuleEmissionContext? emissionCtx = null, SwiftWriter? swiftWriter = null)
        {
            var caseName = caseDecl.Name;
            var capitalizedName = NameProvider.GetCaseName(caseName, caseNameMap);

            // Swift represents multi-value associated types as a tuple payload. The ABI parser
            // unwraps the tuple's elements into AssociatedValues (Count > 1), so build a synthetic
            // TupleTypeSpec and delegate to the tuple-specific TryGet emission. The tuple path
            // uses runtime tuple metadata to compute element offsets, which is what a packed
            // multi-value payload requires.
            if (caseDecl.AssociatedValues.Count == 1 && caseDecl.AssociatedValues[0] is TupleTypeSpec tupleSpec && tupleSpec.Elements.Count > 1)
            {
                EmitTryGetMethodForTuple(csWriter, enumDecl, caseDecl, tupleSpec, typeDatabase, typeNameWithGenerics, tagPropertyName, caseNameMap, emissionCtx);
                return;
            }

            if (caseDecl.AssociatedValues.Count > 1)
            {
                var synthesizedTuple = new TupleTypeSpec(caseDecl.AssociatedValues);
                EmitTryGetMethodForTuple(csWriter, enumDecl, caseDecl, synthesizedTuple, typeDatabase, typeNameWithGenerics, tagPropertyName, caseNameMap, emissionCtx);
                return;
            }

            var tag = enumDecl.GetCaseTag(caseDecl);
            // Module context so an existential reached through this handler's closure arm names
            // the module owning its protocol; a bare interface name in the emitted out-parameter
            // type resolves to nothing in the consuming assembly.
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase, conformanceGraph: null,
                currentModuleName: enumDecl.ModuleDecl?.Name);

            // Determine the output type based on associated values
            string outType;
            List<(string type, string publicType, string name, TypeSpec typeSpec)> parameters = new();

            for (int i = 0; i < caseDecl.AssociatedValues.Count; i++)
            {
                var typeSpec = caseDecl.AssociatedValues[i];
                var enumGenericParams = enumDecl.IsGeneric ? enumDecl.GenericParameters : null;
                var csharpType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, enumGenericParams, enumDecl.ModuleDecl);

                // Parity with EmitEnumCaseWithAssociatedValues: an Apple-framework payload the binding
                // declares as a value/static/absent shape has no reconstructable representation. The
                // case factory already skips it, but the TryGet accessor would still emit an `out`
                // parameter of a nonexistent (CS0234/CS0246) or static-constants (CS0721) type. Checked
                // first — an absent Apple type is unconditionally unbindable — so the withdrawal is
                // always reported rather than being masked by the ObjC-bridge skip below.
                if (ValidationRuleSet.ClassifyUnsupportedReference(typeSpec, typeDatabase, out var absentTryGetType)
                        == ValidationRuleSet.UnsupportedReferenceKind.AbsentBridgedValueType)
                {
                    _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' has associated value '{absentTryGetType}', an Apple-framework type with no reconstructable representation. Skipping TryGet method.");
                    ReportCollector.RecordMemberSkipped(
                        BindingItemKind.Method, $"TryGet{capitalizedName}", enumDecl,
                        SkipReason.AbsentFrameworkType,
                        $"Associated value '{absentTryGetType}' is an Apple-framework type absent from the .NET binding surface; the case cannot be reconstructed.");
                    return;
                }

                if (TryFindUnownedExistentialPayload(typeSpec, typeDatabase, out var unownedExistential, out var unmirroredLayout))
                {
                    RefuseUnownedExistentialTryGet(enumDecl, caseName, capitalizedName, unownedExistential, unmirroredLayout);
                    return;
                }

                // The read mirror of the case factory's refusal: the payload bridge would hand back an
                // Objective-C collection holding an element C# cannot read.
                if (BridgedContainerDisagrees(typeSpec, typeDatabase, enumDecl, isParameter: false, out var bridgeDetail))
                {
                    _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' carries '{typeSpec}', a collection Swift bridges to Objective-C that C# cannot bridge to match. Skipping TryGet method.");
                    ReportCollector.RecordMemberSkipped(
                        BindingItemKind.Method, $"TryGet{capitalizedName}", enumDecl, SkipReason.UnsupportedSignature, bridgeDetail);
                    return;
                }

                if (TryClassifyBridgedPayload(typeSpec, typeDatabase, emissionCtx, enumDecl.ModuleDecl?.Name, out var bridgedPayload))
                {
                    if (!TryEmitBridgedPayloadTryGet(csWriter, swiftWriter, enumDecl, caseDecl, typeDatabase, capitalizedName, tagPropertyName, bridgedPayload, emissionCtx))
                        RefuseBridgedPayloadTryGet(enumDecl, caseName, capitalizedName, bridgedPayload, inTuple: false);
                    return;
                }

                // Skip when the resolved payload type contains AnyType in any position —
                // direct AnyType cannot round-trip (AnyType.GetTypeMetadata and MarshalToSwift
                // both throw; SwiftSafeHandle<AnyType> would NativeMemory.Free stack memory on
                // dispose). Embedded AnyType in a generic arg position means the outer cannot
                // be constructed as a closed type that satisfies ISwiftObject constraints.
                var anyTypeName = TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
                if (csharpType.Contains(anyTypeName))
                {
                    _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' has unsupported associated value type at index {i}. Skipping TryGet method.");
                    ReportCollector.RecordMemberSkipped(
                        BindingItemKind.Method, $"TryGet{capitalizedName}", enumDecl,
                        SkipReason.AnyTypeFallback,
                        $"Associated value {i} ('{typeSpec}') resolves to AnyType, which cannot be read back out of the case.");
                    return;
                }

                // Parity with EmitEnumCaseWithAssociatedValues: skip TryGet when the payload
                // is a bound generic with an ObjC-bridged remapped type in a generic argument
                // position. The ISwiftObject constraint on the outer generic type is violated
                // by the NSObject-rooted remapped type, producing CS0311 at the `out` parameter.
                if (ContainsRemappedObjCTypeInGenericArgs(typeSpec))
                {
                    _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' has a bound generic payload with an ObjC-bridged type remap as a generic argument. Skipping TryGet to avoid ISwiftObject constraint violation.");
                    return;
                }

                // Skip TryGet when the payload binds a concrete type that doesn't satisfy a
                // user-protocol constraint on the outer generic (e.g.,
                // RichTextInsertion<Swift.SwiftString> where T : IRichTextInsertable).
                // The `out` parameter type would produce CS0311. Mirrors the same gate on
                // case factory emission in EmitEnumCaseWithAssociatedValues.
                if (boundGenericsHandler.TryGetFirstUnsatisfiedConstraint(typeSpec, caseDecl, out var constraintDetails))
                {
                    _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' associated value violates generic constraint: {constraintDetails}. Skipping TryGet.");
                    return;
                }

                var publicType = GetPublicCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, enumGenericParams, enumDecl.ModuleDecl);

                // Use type label if available, otherwise generate a name
                var paramName = typeSpec.TypeLabel ?? $"value{i}";
                paramName = SanitizeParameterName(paramName);
                parameters.Add((csharpType, publicType, paramName, typeSpec));
            }

            // Single value: output is just that type (public type for the API surface)
            // Multiple values: output is a tuple
            if (parameters.Count == 1)
            {
                outType = parameters[0].publicType;
            }
            else
            {
                var tupleElements = parameters.Select(p =>
                    !string.IsNullOrEmpty(p.typeSpec.TypeLabel)
                        ? $"{p.publicType} {SanitizeParameterName(p.typeSpec.TypeLabel)}"
                        : p.publicType);
                outType = $"({string.Join(", ", tupleElements)})";
            }

            // Emit the TryGet method
            csWriter.WriteLine("/// <summary>");
            csWriter.WriteLine($"/// Attempts to extract the associated value(s) for the '{caseName}' case.");
            csWriter.WriteLine("/// </summary>");
            csWriter.WriteLine($"/// <param name=\"value\">When this method returns true, contains the associated value(s).</param>");
            csWriter.WriteLine($"/// <returns>True if this enum is the '{caseName}' case; otherwise, false.</returns>");
            // Checkpoint between the doc and the signature so a suppressed-proxy produce-throw can inject
            // the compile-time-visible SB0006 [Obsolete(error:true)] marker ahead of the signature but
            // after the doc, which documents the method only while it precedes every attribute (CS1587).
            // The body checkpoint below only rolls back the half-written body.
            var __tryGetSigCp = csWriter.Checkpoint();
            csWriter.WriteLine($"public bool TryGet{capitalizedName}([MaybeNullWhen(false)] out {outType} value)");
            // Predict-before-emit: a payload existential whose EveryProtocol conformance was not
            // emitted throws SuppressedProxyReferenceException from EmitPayloadMarshal* mid-body.
            // The checkpoint/catch rolls the half-written body back to just after the signature and
            // re-emits the proxy-suppressed throw stub — the in-emission transactional replacement
            // for the old generate-then-regex-strip CoGater member-strip pass.
            var __tryGetCp = csWriter.Checkpoint();
            try
            {
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine("unsafe");
                csWriter.WriteLine("{");
                csWriter.Indent++;

                // Check if we're in the right case
                csWriter.WriteLine($"if ({tagPropertyName} != CaseTag.{capitalizedName})");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine("value = default;");
                csWriter.WriteLine("return false;");
                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine();

                csWriter.WriteLine($"var metadata = SwiftObjectHelper<{typeNameWithGenerics}>.GetTypeMetadata();");
                csWriter.WriteLine();

                // Create a copy to avoid destroying the original
                csWriter.WriteLine("// Create a non-destructive copy of the enum");
                csWriter.WriteLine("byte* enumCopy = stackalloc byte[(int)metadata.Size];");
                csWriter.WriteLine("bool success = false;");
                csWriter.WriteLine("_payload.DangerousAddRef(ref success);");
                csWriter.WriteLine("try");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine("metadata.ValueWitnessTable->InitializeWithCopy(enumCopy, (void*)_payload.DangerousGetHandle(), metadata);");
                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine("finally");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine("if (success)");
                csWriter.Indent++;
                csWriter.WriteLine("_payload.DangerousRelease();");
                csWriter.Indent--;
                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine();

                // Strip the tag to get the payload
                csWriter.WriteLine("// Strip the tag to get the raw payload");
                csWriter.WriteLine("metadata.ValueWitnessTable->DestructiveProjectEnumData(enumCopy, metadata);");
                csWriter.WriteLine();

                // Marshal the payload to C# type(s)
                csWriter.WriteLine("// Marshal the payload to C# type(s)");
                var typeConversionHandler = new TypeConversionHandler(typeDatabase);
                var marshalGenericParams = enumDecl.IsGeneric ? enumDecl.GenericParameters : null;
                if (parameters.Count == 1)
                {
                    var (type, publicType, name, typeSpec) = parameters[0];
                    if (typeConversionHandler.IsSwiftString(typeSpec))
                    {
                        EmitPayloadMarshalWithDeclaration(csWriter, typeSpec, "__value_raw", "enumCopy", typeDatabase, marshalGenericParams, enumDecl.ModuleDecl, emissionCtx);
                        csWriter.WriteLine("value = __value_raw.ToString();");
                    }
                    else if (typeSpec is NamedTypeSpec dataSpec && dataSpec.Name == "Foundation.Data")
                    {
                        // The payload marshal declares a Swift.Foundation.Data local
                        // (MarshalFromSwift<Swift.Foundation.Data>) without going through the
                        // projection path that records the supplement reference. Record it so
                        // the TryGet inspection side carries the SwiftBindings.Apple
                        // PackageReference even when construction of this case is not emitted.
                        AppleSupplementReferences.Record("Foundation.Data", "EnumHandler.CaseInspection:FoundationData");
                        EmitPayloadMarshalWithDeclaration(csWriter, typeSpec, "__value_raw", "enumCopy", typeDatabase, marshalGenericParams, enumDecl.ModuleDecl, emissionCtx);
                        csWriter.WriteLine("value = __value_raw.ToByteArray();");
                        EmitDataPayloadRelease(csWriter, "enumCopy", "__value");
                    }
                    else
                    {
                        EmitPayloadMarshal(csWriter, typeSpec, "value", "enumCopy", typeDatabase, marshalGenericParams, enumDecl.ModuleDecl, emissionCtx);
                    }
                }
                else
                {
                    // Unreachable: multi-element tuples are handled by EmitTryGetMethodForTuple,
                    // and multi-value associated types (>1 AssociatedValues) return early above.
                    throw new InvalidOperationException(
                        $"Unexpected multi-parameter enum case '{enumDecl.Name}.{caseName}' with {parameters.Count} parameters. " +
                        "Multi-element tuples should be handled by EmitTryGetMethodForTuple.");
                }

                csWriter.WriteLine("return true;");
                csWriter.Indent--;
                csWriter.WriteLine("}"); // unsafe
                csWriter.Indent--;
                csWriter.WriteLine("}"); // TryGet
            }
            catch (SuppressedProxyReferenceException ex)
            {
                csWriter.RollbackTo(__tryGetCp);
                // Inject the SB0006 marker between the (already-written) doc and signature so calling
                // TryGet is a COMPILE error, not just a runtime throw; restore the captured signature
                // after it, then emit the throw-stub body as the runtime backstop.
                var __tryGetDocSig = csWriter.RollbackToAndCapture(__tryGetSigCp);
                WrapperEmitter.EmitSuppressedProxyReadPoison(csWriter);
                csWriter.AppendCaptured(__tryGetDocSig);
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine($"throw new NotSupportedException(\"{WrapperEmitter.ProxySuppressedMessage}\");");
                csWriter.Indent--;
                csWriter.WriteLine("}");
                SuppressedProxyReporting.Record(BindingItemKind.Method, $"{enumDecl.Name}.TryGet{capitalizedName}", enumDecl,
                    SuppressedProxyReporting.Site.ProduceThrow, ex.ProxyClassName);
            }
            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits a TryGet method for an enum case with tuple associated values.
        /// Generates multiple out parameters, one for each tuple element.
        /// Uses TupleTypeMetadata to get element offsets at runtime.
        /// </summary>
        private void EmitTryGetMethodForTuple(CSharpWriter csWriter, EnumDecl enumDecl, EnumCaseDecl caseDecl, TupleTypeSpec tupleSpec, ITypeDatabase typeDatabase, string typeNameWithGenerics, string tagPropertyName, Dictionary<string, string>? caseNameMap = null, ModuleEmissionContext? emissionCtx = null)
        {
            var caseName = caseDecl.Name;
            var capitalizedName = NameProvider.GetCaseName(caseName, caseNameMap);
            // Module context so an existential reached through this handler's closure arm names
            // the module owning its protocol (see EmitTryGetMethod).
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase, conformanceGraph: null,
                currentModuleName: enumDecl.ModuleDecl?.Name);

            // The read mirror of the case factory's refusal, ahead of the bridged-value refusal below
            // so the reader reports the same reason as the factory.
            if (BridgedContainerDisagrees(tupleSpec, typeDatabase, enumDecl, isParameter: false, out var bridgeDetail))
            {
                _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' carries a collection Swift bridges to Objective-C that C# cannot bridge to match. Skipping TryGet method.");
                ReportCollector.RecordMemberSkipped(
                    BindingItemKind.Method, $"TryGet{capitalizedName}", enumDecl, SkipReason.UnsupportedSignature, bridgeDetail);
                return;
            }

            // Validate tuple element count (max 7 per C# ValueTuple limit)
            if (tupleSpec.Elements.Count > TupleHandler.MaxSupportedTupleElements)
            {
                _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' has tuple with {tupleSpec.Elements.Count} elements (max {TupleHandler.MaxSupportedTupleElements}). Skipping TryGet method.");
                return;
            }

            // Validate and build parameter list from tuple elements
            var parameters = new List<(string type, string publicType, string name, TypeSpec typeSpec)>();
            for (int i = 0; i < tupleSpec.Elements.Count; i++)
            {
                var element = tupleSpec.Elements[i];

                // Same absent-Apple-payload gate as the single-value path, checked first: a tuple element
                // the binding declares as a value/static/absent shape can't be reconstructed, and its
                // `out` parameter would reference a nonexistent (CS0234/CS0246) or static-constants
                // (CS0721) type. Run ahead of the ObjC-bridge skip below so an absent Apple type is
                // reported as such rather than silently dropped as an unsupported bridge.
                if (ValidationRuleSet.ClassifyUnsupportedReference(element, typeDatabase, out var absentTupleType)
                        == ValidationRuleSet.UnsupportedReferenceKind.AbsentBridgedValueType)
                {
                    _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' has tuple element '{absentTupleType}' at index {i}, an Apple-framework type with no reconstructable representation. Skipping TryGet method.");
                    ReportCollector.RecordMemberSkipped(
                        BindingItemKind.Method, $"TryGet{capitalizedName}", enumDecl,
                        SkipReason.AbsentFrameworkType,
                        $"Tuple associated value '{absentTupleType}' is an Apple-framework type absent from the .NET binding surface; the case cannot be reconstructed.");
                    return;
                }

                if (TryFindUnownedExistentialPayload(element, typeDatabase, out var unownedTupleExistential, out var unmirroredTupleLayout))
                {
                    RefuseUnownedExistentialTryGet(enumDecl, caseName, capitalizedName, unownedTupleExistential, unmirroredTupleLayout);
                    return;
                }

                if (TryClassifyBridgedPayload(element, typeDatabase, emissionCtx, enumDecl.ModuleDecl?.Name, out var bridgedTupleElement))
                {
                    RefuseBridgedPayloadTryGet(enumDecl, caseName, capitalizedName, bridgedTupleElement, inTuple: true);
                    return;
                }

                // Check if element is a nested tuple (not supported)
                if (element is TupleTypeSpec)
                {
                    // Nested tuples not supported
                    _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' has nested tuple at element {i}. Skipping TryGet method.");
                    return;
                }

                var enumGenericParams2 = enumDecl.IsGeneric ? enumDecl.GenericParameters : null;
                var csharpType = GetCSharpTypeNameForEnumCase(element, typeDatabase, boundGenericsHandler, enumGenericParams2, enumDecl.ModuleDecl);

                // Skip when the tuple element's resolved type contains AnyType in any position.
                // Direct AnyType elements cannot round-trip: NewFromPayload wraps the source
                // pointer in SwiftSafeHandle<AnyType>, and the dispose path NativeMemory.Free's
                // that pointer — which is stack memory (enumCopy), producing an invalid free.
                // Embedded AnyType in a bound generic arg similarly can't be constructed closed.
                var anyTypeName = TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
                if (csharpType.Contains(anyTypeName))
                {
                    _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' has unsupported tuple element type at index {i}. Skipping TryGet method.");
                    ReportCollector.RecordMemberSkipped(
                        BindingItemKind.Method, $"TryGet{capitalizedName}", enumDecl,
                        SkipReason.AnyTypeFallback,
                        $"Tuple element {i} ('{element}') resolves to AnyType, which cannot be read back out of the case.");
                    return;
                }

                // Skip when the tuple element is (or contains) an ObjC-bridged remapped type
                // AND would fall through to the SwiftObjectHelper<T> branch in the metadata
                // accessor. That branch requires T : ISwiftObject, which NSObject-rooted remaps
                // (e.g. Security.SecPolicy) violate. Simple enums (routed to GetTypeMetadataOrThrow
                // with an integer metadata type) and frozen structs (routed to GetTypeMetadataOrThrow<T>)
                // are safe even when their Swift name is ObjC-bridged.
                if ((IsObjCBridgedTypeSpec(element) || ContainsRemappedObjCTypeInGenericArgs(element))
                    && WouldEmitSwiftObjectHelper(element, typeDatabase))
                {
                    _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' tuple element at index {i} is an ObjC-bridged remapped type. Skipping TryGet to avoid ISwiftObject constraint violation.");
                    return;
                }

                var publicType = GetPublicCSharpTypeNameForEnumCase(element, typeDatabase, boundGenericsHandler, enumGenericParams2, enumDecl.ModuleDecl);

                // Use element label if available, otherwise generate a name
                var paramName = element.TypeLabel ?? $"value{i}";
                paramName = SanitizeParameterName(paramName);
                parameters.Add((csharpType, publicType, paramName, element));
            }

            // Build the out parameter list for the method signature (use public types)
            var outParams = parameters.Select(p => $"[MaybeNullWhen(false)] out {p.publicType} {p.name}");
            var outParamString = string.Join(", ", outParams);

            // The out parameters are named from the tuple's own element labels, and every local the
            // body declares lands in the same scope as them. Minting the locals through one scope
            // seeded with those names moves a generated local aside when an element is labelled like
            // it; nothing moves when nothing collides.
            var bodyScope = new SyntheticNameScope(parameters.Select(p => p.name));
            var metadataName = bodyScope.Mint("metadata");
            var enumCopyName = bodyScope.Mint("enumCopy");
            var successName = bodyScope.Mint("success");
            var tupleMetadataName = bodyScope.Mint("tupleMetadata");

            // Emit the TryGet method
            csWriter.WriteLine("/// <summary>");
            csWriter.WriteLine($"/// Attempts to extract the associated value(s) for the '{caseName}' case.");
            csWriter.WriteLine("/// </summary>");
            foreach (var (_, _, name, _) in parameters)
            {
                csWriter.WriteLine(XmlDocCommentEmitter.ParamTag(name, "When this method returns true, contains the associated value."));
            }
            csWriter.WriteLine($"/// <returns>True if this enum is the '{caseName}' case; otherwise, false.</returns>");
            // Checkpoint between the doc and the signature so a suppressed-proxy produce-throw can inject
            // the SB0006 marker after the doc (see the single-value TryGet above).
            var __tryGetTupleSigCp = csWriter.Checkpoint();
            csWriter.WriteLine($"public bool TryGet{capitalizedName}({outParamString})");
            // Predict-before-emit: a tuple-element existential whose EveryProtocol conformance was
            // not emitted throws SuppressedProxyReferenceException from EmitPayloadMarshalWithOffset
            // mid-body. The checkpoint/catch rolls the half-written body back to just after the
            // signature and re-emits the proxy-suppressed throw stub — the in-emission transactional
            // replacement for the old generate-then-regex-strip CoGater member-strip pass. The tuple
            // metadata accessor helper below is left intact, matching the CoGater (which only stubbed
            // the member referencing the suppressed proxy).
            var __tryGetTupleCp = csWriter.Checkpoint();
            try
            {
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine("unsafe");
                csWriter.WriteLine("{");
                csWriter.Indent++;

                // Check if we're in the right case
                csWriter.WriteLine($"if ({tagPropertyName} != CaseTag.{capitalizedName})");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                foreach (var (_, _, name, _) in parameters)
                {
                    csWriter.WriteLine($"{name} = default;");
                }
                csWriter.WriteLine("return false;");
                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine();

                csWriter.WriteLine($"var {metadataName} = SwiftObjectHelper<{typeNameWithGenerics}>.GetTypeMetadata();");
                csWriter.WriteLine();

                // Create a copy to avoid destroying the original
                csWriter.WriteLine("// Create a non-destructive copy of the enum");
                csWriter.WriteLine($"byte* {enumCopyName} = stackalloc byte[(int){metadataName}.Size];");
                csWriter.WriteLine($"bool {successName} = false;");
                csWriter.WriteLine($"_payload.DangerousAddRef(ref {successName});");
                csWriter.WriteLine("try");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine($"{metadataName}.ValueWitnessTable->InitializeWithCopy({enumCopyName}, (void*)_payload.DangerousGetHandle(), {metadataName});");
                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine("finally");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine($"if ({successName})");
                csWriter.Indent++;
                csWriter.WriteLine("_payload.DangerousRelease();");
                csWriter.Indent--;
                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine();

                // Strip the tag to get the payload
                csWriter.WriteLine("// Strip the tag to get the raw payload (which is the tuple)");
                csWriter.WriteLine($"{metadataName}.ValueWitnessTable->DestructiveProjectEnumData({enumCopyName}, {metadataName});");
                csWriter.WriteLine();

                // Get tuple type metadata to access element offsets
                csWriter.WriteLine("// Get tuple metadata to determine element offsets");
                csWriter.WriteLine($"var {tupleMetadataName} = GetTupleMetadata_{capitalizedName}();");
                csWriter.WriteLine();

                // Marshal each tuple element using its computed offset
                csWriter.WriteLine("// Marshal each tuple element from its computed offset");
                for (int i = 0; i < parameters.Count; i++)
                {
                    var (_, _, name, typeSpec) = parameters[i];
                    var offsetName = bodyScope.Mint($"offset{i}");
                    csWriter.WriteLine($"var {offsetName} = {tupleMetadataName}->GetElementOffset({i});");
                    var tupleGenericParams = enumDecl.IsGeneric ? enumDecl.GenericParameters : null;
                    EmitPayloadMarshalWithOffset(csWriter, typeSpec, name, enumCopyName, offsetName, typeDatabase, tupleGenericParams, enumDecl.ModuleDecl, emissionCtx);
                }
                csWriter.WriteLine();

                csWriter.WriteLine("return true;");
                csWriter.Indent--;
                csWriter.WriteLine("}"); // unsafe
                csWriter.Indent--;
                csWriter.WriteLine("}"); // TryGet
            }
            catch (SuppressedProxyReferenceException ex)
            {
                csWriter.RollbackTo(__tryGetTupleCp);
                // Inject the SB0006 marker between the doc and the signature so calling TryGet is a
                // compile error, not just a runtime throw; restore the signature, then emit the throw stub.
                var __tryGetTupleDocSig = csWriter.RollbackToAndCapture(__tryGetTupleSigCp);
                WrapperEmitter.EmitSuppressedProxyReadPoison(csWriter);
                csWriter.AppendCaptured(__tryGetTupleDocSig);
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine($"throw new NotSupportedException(\"{WrapperEmitter.ProxySuppressedMessage}\");");
                csWriter.Indent--;
                csWriter.WriteLine("}");
                SuppressedProxyReporting.Record(BindingItemKind.Method, $"{enumDecl.Name}.TryGet{capitalizedName}", enumDecl,
                    SuppressedProxyReporting.Site.ProduceThrow, ex.ProxyClassName);
            }
            csWriter.WriteLine();

            // Emit the tuple metadata accessor helper
            var metadataGenericParams = enumDecl.IsGeneric ? enumDecl.GenericParameters : null;
            EmitTupleMetadataAccessor(csWriter, capitalizedName, parameters, typeDatabase, metadataGenericParams, enumDecl.ModuleDecl);
        }

        /// <summary>
        /// Finds an existential payload that the read would hand out as a bare
        /// <c>ExistentialContainerN</c>: one with witness tables, not <c>Swift.Error</c>, and with a
        /// protocol the type database has no record for, so no proxy can own it. The TryGet copy
        /// holds the boxed value at +1, and the container struct has no owner that could release
        /// it, so every read would leak one reference. The arm order mirrors
        /// <c>EmitPayloadMarshal</c>'s existential branch exactly. Also finds a payload holding an
        /// existential whose library marker changes the layout
        /// (<see cref="ExistentialHandler.ContainsLayoutChangingMarker"/>), which no arm reads at the
        /// right size.
        /// </summary>
        private static bool TryFindUnownedExistentialPayload(TypeSpec typeSpec, ITypeDatabase typeDatabase, out string displayType, out bool layoutUnmirrored)
        {
            displayType = string.Empty;
            layoutUnmirrored = false;
            var existentialHandler = new ExistentialHandler(typeDatabase);
            if (existentialHandler.ContainsLayoutChangingMarker(typeSpec))
            {
                displayType = typeSpec.ToString();
                layoutUnmirrored = true;
                return true;
            }
            if (!existentialHandler.IsExistential(typeSpec))
                return false;
            var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
            if (protocolList is null
                || ExistentialHandler.IsZeroWitnessExistential(protocolList, typeDatabase)
                || existentialHandler.TryGetWellKnownProtocolType(protocolList, out _)
                || existentialHandler.AllProtocolsHaveTypeRecords(protocolList))
                return false;
            displayType = typeSpec.ToString();
            return true;
        }

        private void RefuseUnownedExistentialTryGet(EnumDecl enumDecl, string caseName, string capitalizedName, string existentialType, bool layoutUnmirrored)
        {
            if (layoutUnmirrored)
            {
                _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' carries '{existentialType}', whose library marker changes the existential's layout. Skipping TryGet method.");
                ReportCollector.RecordMemberSkipped(
                    BindingItemKind.Method, $"TryGet{capitalizedName}", enumDecl,
                    SkipReason.UnsupportedType,
                    $"Associated value '{existentialType}' has a library marker protocol that changes the existential's layout (class-bound, or beside Error alone), which no carrier mirrors.");
                return;
            }
            _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' carries '{existentialType}', an existential with no protocol record to own it. Skipping TryGet method.");
            ReportCollector.RecordMemberSkipped(
                BindingItemKind.Method, $"TryGet{capitalizedName}", enumDecl,
                SkipReason.UnsupportedType,
                $"Associated value '{existentialType}' is an existential whose protocol has no binding record, so no proxy can own the reference a read takes; the case factory still constructs it.");
        }

        private enum BridgedPayloadKind
        {
            Value,
            Collection,
            /// <summary>Holds bridgeable values in a shape the payload bridge does not return
            /// (<c>URL??</c>, <c>[[URL]?]</c> without a container bridge, <c>(URL, Int)?</c>, …).</summary>
            Unsupported,
        }

        /// <summary>
        /// A payload that holds Objective-C-bridgeable Swift values: the value itself
        /// (<c>URL</c>, <c>IndexPath</c>, <c>Decimal</c>, …), or an <c>Array</c>, <c>Dictionary</c>
        /// or <c>Set</c> of them, either one optionally wrapped in <c>Optional</c>.
        /// <see cref="CoreProjection"/> is the non-optional projection, or null when it cannot
        /// be projected (a generic enum's payload).
        /// </summary>
        private sealed record BridgedPayload(string DisplayType, BridgedPayloadKind Kind, bool IsOptional, ITypeProjection? CoreProjection);

        /// <summary>
        /// Finds a payload of Objective-C-bridgeable Swift values (see <see cref="BridgedPayload"/>).
        /// In the enum's memory such a payload is Swift values, not the Objective-C objects they
        /// bridge to: a <c>URL</c> is a word holding its Swift storage object, and an array of
        /// URLs is native array storage whose <c>objectAtIndex:</c> hands back each element's
        /// storage word unbridged (an <c>IndexPath</c> or <c>Decimal</c> element is not even an
        /// object). Reading either straight out of the enum copy therefore sends Objective-C
        /// messages to objects that do not implement them. Only Swift's bridge produces the
        /// right object, so TryGet reaches these payloads through a Swift payload bridge or not
        /// at all.
        /// </summary>
        private static bool TryClassifyBridgedPayload(TypeSpec typeSpec, ITypeDatabase typeDatabase, ModuleEmissionContext? emissionCtx, string? moduleName,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BridgedPayload? payload)
        {
            if (TryClassifyBridgeableShape(typeSpec, typeDatabase, emissionCtx, moduleName, out payload))
                return true;
            // Any other shape that still holds a bridgeable value would fall through to a read of
            // the enum's storage, which is the same unbridged-word read in a deeper position.
            if (ContainsObjCBridgeableValue(typeSpec, typeDatabase))
            {
                payload = new BridgedPayload(typeSpec.ToString(), BridgedPayloadKind.Unsupported, IsOptional: false, CoreProjection: null);
                return true;
            }
            return false;
        }

        /// <summary>
        /// True when an Objective-C-bridgeable Swift value appears anywhere in
        /// <paramref name="typeSpec"/>: the type itself, a generic argument at any depth, or a
        /// tuple element.
        /// </summary>
        private static bool ContainsObjCBridgeableValue(TypeSpec typeSpec, ITypeDatabase typeDatabase) => typeSpec switch
        {
            TupleTypeSpec tuple => tuple.Elements.Any(e => ContainsObjCBridgeableValue(e, typeDatabase)),
            NamedTypeSpec { GenericParameters.Count: > 0 } named => named.GenericParameters.Any(g => ContainsObjCBridgeableValue(g, typeDatabase)),
            NamedTypeSpec named => !TypeSpecHelpers.IsGenericTypeParameter(named.Name)
                && typeDatabase.TryGetTypeRecord(named, out var record) && MarshallingHelpers.IsObjCBridgeable(record),
            _ => false,
        };

        private static bool TryClassifyBridgeableShape(TypeSpec typeSpec, ITypeDatabase typeDatabase, ModuleEmissionContext? emissionCtx, string? moduleName,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BridgedPayload? payload)
        {
            payload = null;
            if (typeSpec is not NamedTypeSpec named)
                return false;
            var isOptional = false;
            var core = named;
            if (named.Name is "Swift.Optional" or "Optional" && named.GenericParameters.Count == 1)
            {
                if (named.GenericParameters[0] is not NamedTypeSpec inner)
                    return false;
                isOptional = true;
                core = inner;
            }

            if (core.Name is "Swift.Array" or "Swift.Dictionary" or "Swift.Set")
            {
                var projection = ProjectBridgedPayload(core, typeDatabase, emissionCtx, moduleName);
                if (projection is not (ArrayProjection or DictionaryProjection or SetProjection) || !projection.UsesObjCContainerBridge)
                    return false;
                payload = new BridgedPayload($"{core.Name}<{string.Join(", ", core.GenericParameters)}>", BridgedPayloadKind.Collection, isOptional, projection);
                return true;
            }

            if (core.ContainsGenericParameters || TypeSpecHelpers.IsGenericTypeParameter(core.Name))
                return false;
            if (!typeDatabase.TryGetTypeRecord(core, out var record) || !MarshallingHelpers.IsObjCBridgeable(record))
                return false;
            var valueProjection = ProjectBridgedPayload(core, typeDatabase, emissionCtx, moduleName) as ObjCBridgeableProjection;
            payload = new BridgedPayload(core.Name, BridgedPayloadKind.Value, isOptional, valueProjection);
            return true;
        }

        private static ITypeProjection? ProjectBridgedPayload(NamedTypeSpec core, ITypeDatabase typeDatabase, ModuleEmissionContext? emissionCtx, string? moduleName)
        {
            try
            {
                return new TypeProjectionFactory().Project(core, new ProjectionContext
                {
                    TypeDatabase = typeDatabase, IsParameter = false, GenericContext = GenericContext.Empty,
                    EmissionContext = emissionCtx, CurrentModuleName = moduleName
                });
            }
            catch (SuppressedProxyReferenceException)
            {
                return null;
            }
        }

        /// <summary>
        /// Emits TryGet for a single-value case whose payload is Objective-C-bridgeable through
        /// the case's Swift payload bridge (<see cref="EnumCaseWrapperEmitter.EmitSwiftCasePayloadBridge"/>):
        /// Swift matches the case on the live enum value and returns the payload bridged to its
        /// Objective-C object at +1, which the projection's owning read adopts. No enum copy is
        /// taken, so there is no copied reference to release. Returns false, having written
        /// nothing, when the enum cannot carry a bridge.
        /// </summary>
        private bool TryEmitBridgedPayloadTryGet(CSharpWriter csWriter, SwiftWriter? swiftWriter, EnumDecl enumDecl, EnumCaseDecl caseDecl, ITypeDatabase typeDatabase,
            string capitalizedName, string tagPropertyName, BridgedPayload payload, ModuleEmissionContext? emissionCtx)
        {
            if (swiftWriter is null || swiftWriter.IsDiscarding || emissionCtx is null || payload.CoreProjection is not { } core
                || enumDecl.ModuleDecl is not { } moduleDecl
                || !EnumCaseWrapperEmitter.CanEmitCasePayloadBridge(enumDecl, typeDatabase))
                return false;

            const string rawName = "_value_raw";
            var read = payload.Kind == BridgedPayloadKind.Value
                ? ((ObjCBridgeableProjection)core).BridgeReadExpression(rawName, nonNull: true, ownsReference: true)
                : core.GetReturnContainerConversion(rawName);
            if (read is null)
                return false;

            var enumSwiftName = enumDecl.SwiftTypeName?.Name ?? enumDecl.Name;
            var symbol = EnumCaseWrapperEmitter.GetCasePayloadBridgeSymbolName(moduleDecl.Name, enumSwiftName, caseDecl.Name, caseDecl.MangledName);
            EnumCaseWrapperEmitter.EmitSwiftCasePayloadBridge(swiftWriter, enumDecl, caseDecl, symbol, payload.IsOptional, emissionCtx);

            var outType = payload.IsOptional ? $"{core.PublicType}?" : core.PublicType;
            var pinvokeName = $"PInvoke_payload_{EmitterUtility.DeterministicHash8(symbol)}";
            var caseName = caseDecl.Name;

            csWriter.WriteLine("/// <summary>");
            csWriter.WriteLine($"/// Attempts to extract the associated value(s) for the '{caseName}' case.");
            csWriter.WriteLine("/// </summary>");
            csWriter.WriteLine($"/// <param name=\"value\">When this method returns true, contains the associated value(s).</param>");
            csWriter.WriteLine($"/// <returns>True if this enum is the '{caseName}' case; otherwise, false.</returns>");
            csWriter.WriteLine($"public bool TryGet{capitalizedName}([MaybeNullWhen(false)] out {outType} value)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine($"if ({tagPropertyName} != CaseTag.{capitalizedName})");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("value = default;");
            csWriter.WriteLine("return false;");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine($"IntPtr {rawName};");
            csWriter.WriteLine("bool success = false;");
            csWriter.WriteLine("_payload.DangerousAddRef(ref success);");
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine($"{rawName} = {pinvokeName}(_payload.DangerousGetHandle());");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("finally");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("if (success)");
            csWriter.Indent++;
            csWriter.WriteLine("_payload.DangerousRelease();");
            csWriter.Indent--;
            csWriter.Indent--;
            csWriter.WriteLine("}");
            // The bridge returns nil only for another case, which the tag check ruled out, or
            // for an optional payload that holds nil.
            csWriter.WriteLine(payload.IsOptional
                ? $"value = {rawName} == IntPtr.Zero ? ({core.PublicType}?)null : {read};"
                : $"value = {read};");
            csWriter.WriteLine("return true;");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();
            PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
            {
                LibraryPath = typeDatabase.AsyncLibraryName!,
                EntryPoint = symbol,
                MethodName = pinvokeName,
                ReturnType = "IntPtr",
                ParametersString = "IntPtr enumPtr",
                CallingConvention = PInvokeCallingConvention.Cdecl
            });
            csWriter.WriteLine();
            return true;
        }

        private void RefuseBridgedPayloadTryGet(EnumDecl enumDecl, string caseName, string capitalizedName, BridgedPayload payload, bool inTuple)
        {
            var what = payload.Kind switch
            {
                BridgedPayloadKind.Value => "an Objective-C-bridgeable Swift value",
                BridgedPayloadKind.Collection => "a collection of Objective-C-bridgeable Swift values",
                _ => "a type holding Objective-C-bridgeable Swift values",
            };
            _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' carries '{payload.DisplayType}', {what}. Skipping TryGet method.");
            var why = inTuple
                ? "and it is one element of a multi-value payload, which the case's payload bridge does not return"
                : payload.Kind == BridgedPayloadKind.Unsupported
                    ? "in a shape the case's payload bridge does not return (it takes one value or one collection, optionally wrapped once in Optional)"
                    : "and this enum cannot carry a Swift payload bridge to bridge it";
            ReportCollector.RecordMemberSkipped(
                BindingItemKind.Method, $"TryGet{capitalizedName}", enumDecl,
                SkipReason.UnsupportedType,
                $"Associated value '{payload.DisplayType}' is stored as Swift values, not as the Objective-C objects they bridge to, {why}; the case factory still constructs it.");
        }
    }
}
