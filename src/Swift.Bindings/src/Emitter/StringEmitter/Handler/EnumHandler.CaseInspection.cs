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
        /// Emits a nested CaseTag enum for type-safe case discrimination.
        /// Tag values follow Swift's ordering: payload cases first (in declaration order),
        /// then no-payload cases (in declaration order).
        /// </summary>
        private void EmitCaseTagEnum(CSharpWriter csWriter, EnumDecl enumDecl, Dictionary<string, string>? caseNameMap = null)
        {
            csWriter.WriteLine("/// <summary>");
            csWriter.WriteLine($"/// Enum representing the possible cases of {enumDecl.Name}.");
            csWriter.WriteLine("/// Tag values follow Swift's ordering: payload cases first, then no-payload cases.");
            csWriter.WriteLine("/// </summary>");
            csWriter.WriteLine("public enum CaseTag : uint");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // Emit payload cases first (in declaration order)
            foreach (var caseDecl in enumDecl.PayloadCases)
            {
                var capitalizedName = NameProvider.GetCaseName(caseDecl.Name, caseNameMap);
                var tag = enumDecl.GetCaseTag(caseDecl);
                csWriter.WriteLine($"{capitalizedName} = {tag},");
            }

            // Then emit no-payload cases
            foreach (var caseDecl in enumDecl.NoPayloadCases)
            {
                var capitalizedName = NameProvider.GetCaseName(caseDecl.Name, caseNameMap);
                var tag = enumDecl.GetCaseTag(caseDecl);
                csWriter.WriteLine($"{capitalizedName} = {tag},");
            }

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits the Tag property that returns the current case of the enum.
        /// Uses ValueWitnessTable->GetEnumTag to determine the case.
        /// </summary>
        private void EmitTagProperty(CSharpWriter csWriter, EnumDecl enumDecl, string typeNameWithGenerics)
        {
            csWriter.WriteLine("/// <summary>");
            csWriter.WriteLine("/// Gets the current case of this enum instance.");
            csWriter.WriteLine("/// </summary>");
            csWriter.WriteLine("public CaseTag Tag");
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
            csWriter.WriteLine("return (CaseTag)metadata.ValueWitnessTable->GetEnumTag(payload, metadata);");

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
            csWriter.WriteLine("}"); // Tag
            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits a TryGet method for an enum case with associated values.
        /// The method extracts the associated value(s) if the enum is in the specified case.
        /// </summary>
        private void EmitTryGetMethod(CSharpWriter csWriter, EnumDecl enumDecl, EnumCaseDecl caseDecl, ITypeDatabase typeDatabase, string typeNameWithGenerics, Dictionary<string, string>? caseNameMap = null)
        {
            var caseName = caseDecl.Name;
            var capitalizedName = NameProvider.GetCaseName(caseName, caseNameMap);

            // Swift represents multi-value associated types as a single tuple type.
            // Check if the single associated value is a tuple (multi-element extraction).
            if (caseDecl.AssociatedValues.Count == 1 && caseDecl.AssociatedValues[0] is TupleTypeSpec tupleSpec && tupleSpec.Elements.Count > 1)
            {
                // Delegate to tuple-specific TryGet emission
                EmitTryGetMethodForTuple(csWriter, enumDecl, caseDecl, tupleSpec, typeDatabase, typeNameWithGenerics, caseNameMap);
                return;
            }

            // Also skip if somehow there are multiple associated values (shouldn't happen with current parsing)
            if (caseDecl.AssociatedValues.Count > 1)
            {
                _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' has {caseDecl.AssociatedValues.Count} associated values. TryGet for multi-value cases not yet supported.");
                return;
            }

            var tag = enumDecl.GetCaseTag(caseDecl);
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Determine the output type based on associated values
            string outType;
            List<(string type, string publicType, string name, TypeSpec typeSpec)> parameters = new();

            for (int i = 0; i < caseDecl.AssociatedValues.Count; i++)
            {
                var typeSpec = caseDecl.AssociatedValues[i];
                var enumGenericParams = enumDecl.IsGeneric ? enumDecl.GenericParameters : null;
                var csharpType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, enumGenericParams);

                // Check if type is unsupported
                if (csharpType == TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName)
                {
                    _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' has unsupported associated value type at index {i}. Skipping TryGet method.");
                    return;
                }

                var publicType = GetPublicCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler, enumGenericParams);

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
            csWriter.WriteLine($"public bool TryGet{capitalizedName}([MaybeNullWhen(false)] out {outType} value)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("unsafe");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // Check if we're in the right case
            csWriter.WriteLine($"if (Tag != CaseTag.{capitalizedName})");
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
                    EmitPayloadMarshalWithDeclaration(csWriter, typeSpec, "__value_raw", "enumCopy", typeDatabase, marshalGenericParams);
                    csWriter.WriteLine("value = __value_raw.ToString();");
                }
                else if (typeSpec is NamedTypeSpec dataSpec && dataSpec.Name == "Foundation.Data")
                {
                    EmitPayloadMarshalWithDeclaration(csWriter, typeSpec, "__value_raw", "enumCopy", typeDatabase, marshalGenericParams);
                    csWriter.WriteLine("value = __value_raw.ToByteArray();");
                }
                else
                {
                    EmitPayloadMarshal(csWriter, typeSpec, "value", "enumCopy", typeDatabase, marshalGenericParams);
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
            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits a TryGet method for an enum case with tuple associated values.
        /// Generates multiple out parameters, one for each tuple element.
        /// Uses TupleTypeMetadata to get element offsets at runtime.
        /// </summary>
        private void EmitTryGetMethodForTuple(CSharpWriter csWriter, EnumDecl enumDecl, EnumCaseDecl caseDecl, TupleTypeSpec tupleSpec, ITypeDatabase typeDatabase, string typeNameWithGenerics, Dictionary<string, string>? caseNameMap = null)
        {
            var caseName = caseDecl.Name;
            var capitalizedName = NameProvider.GetCaseName(caseName, caseNameMap);
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);
            var tupleHandler = new TupleHandler(typeDatabase);

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

                // Check if element is a nested tuple (not supported)
                if (element is TupleTypeSpec)
                {
                    // Nested tuples not supported
                    _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' has nested tuple at element {i}. Skipping TryGet method.");
                    return;
                }

                var enumGenericParams2 = enumDecl.IsGeneric ? enumDecl.GenericParameters : null;
                var csharpType = GetCSharpTypeNameForEnumCase(element, typeDatabase, boundGenericsHandler, enumGenericParams2);

                // Check if type is unsupported
                if (csharpType == TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName)
                {
                    _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' has unsupported tuple element type at index {i}. Skipping TryGet method.");
                    return;
                }

                var publicType = GetPublicCSharpTypeNameForEnumCase(element, typeDatabase, boundGenericsHandler, enumGenericParams2);

                // Use element label if available, otherwise generate a name
                var paramName = element.TypeLabel ?? $"value{i}";
                paramName = SanitizeParameterName(paramName);
                parameters.Add((csharpType, publicType, paramName, element));
            }

            // Build the out parameter list for the method signature (use public types)
            var outParams = parameters.Select(p => $"[MaybeNullWhen(false)] out {p.publicType} {p.name}");
            var outParamString = string.Join(", ", outParams);

            // Emit the TryGet method
            csWriter.WriteLine("/// <summary>");
            csWriter.WriteLine($"/// Attempts to extract the associated value(s) for the '{caseName}' case.");
            csWriter.WriteLine("/// </summary>");
            foreach (var (_, _, name, _) in parameters)
            {
                csWriter.WriteLine($"/// <param name=\"{name}\">When this method returns true, contains the associated value.</param>");
            }
            csWriter.WriteLine($"/// <returns>True if this enum is the '{caseName}' case; otherwise, false.</returns>");
            csWriter.WriteLine($"public bool TryGet{capitalizedName}({outParamString})");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("unsafe");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // Check if we're in the right case
            csWriter.WriteLine($"if (Tag != CaseTag.{capitalizedName})");
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
            csWriter.WriteLine("// Strip the tag to get the raw payload (which is the tuple)");
            csWriter.WriteLine("metadata.ValueWitnessTable->DestructiveProjectEnumData(enumCopy, metadata);");
            csWriter.WriteLine();

            // Get tuple type metadata to access element offsets
            csWriter.WriteLine("// Get tuple metadata to determine element offsets");
            csWriter.WriteLine($"var tupleMetadata = GetTupleMetadata_{capitalizedName}();");
            csWriter.WriteLine();

            // Marshal each tuple element using its computed offset
            csWriter.WriteLine("// Marshal each tuple element from its computed offset");
            for (int i = 0; i < parameters.Count; i++)
            {
                var (_, _, name, typeSpec) = parameters[i];
                csWriter.WriteLine($"var offset{i} = tupleMetadata->GetElementOffset({i});");
                var tupleGenericParams = enumDecl.IsGeneric ? enumDecl.GenericParameters : null;
                EmitPayloadMarshalWithOffset(csWriter, typeSpec, name, "enumCopy", $"offset{i}", typeDatabase, tupleGenericParams);
            }
            csWriter.WriteLine();

            csWriter.WriteLine("return true;");
            csWriter.Indent--;
            csWriter.WriteLine("}"); // unsafe
            csWriter.Indent--;
            csWriter.WriteLine("}"); // TryGet
            csWriter.WriteLine();

            // Emit the tuple metadata accessor helper
            var metadataGenericParams = enumDecl.IsGeneric ? enumDecl.GenericParameters : null;
            EmitTupleMetadataAccessor(csWriter, capitalizedName, parameters, typeDatabase, metadataGenericParams);
        }
    }
}
