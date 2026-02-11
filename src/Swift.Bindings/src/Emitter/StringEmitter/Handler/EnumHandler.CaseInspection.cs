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
        private void EmitCaseTagEnum(CSharpWriter csWriter, EnumDecl enumDecl)
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
                var capitalizedName = char.ToUpper(caseDecl.Name[0]) + caseDecl.Name.Substring(1);
                var tag = enumDecl.GetCaseTag(caseDecl);
                csWriter.WriteLine($"{capitalizedName} = {tag},");
            }

            // Then emit no-payload cases
            foreach (var caseDecl in enumDecl.NoPayloadCases)
            {
                var capitalizedName = char.ToUpper(caseDecl.Name[0]) + caseDecl.Name.Substring(1);
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
        private void EmitTryGetMethod(CSharpWriter csWriter, EnumDecl enumDecl, EnumCaseDecl caseDecl, ITypeDatabase typeDatabase, string typeNameWithGenerics)
        {
            var caseName = caseDecl.Name;
            var capitalizedName = char.ToUpper(caseName[0]) + caseName.Substring(1);

            // Swift represents multi-value associated types as a single tuple type.
            // Check if the single associated value is a tuple (multi-element extraction).
            if (caseDecl.AssociatedValues.Count == 1 && caseDecl.AssociatedValues[0] is TupleTypeSpec tupleSpec && tupleSpec.Elements.Count > 1)
            {
                // Delegate to tuple-specific TryGet emission
                EmitTryGetMethodForTuple(csWriter, enumDecl, caseDecl, tupleSpec, typeDatabase, typeNameWithGenerics);
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
            List<(string type, string name, TypeSpec typeSpec)> parameters = new();

            for (int i = 0; i < caseDecl.AssociatedValues.Count; i++)
            {
                var typeSpec = caseDecl.AssociatedValues[i];
                var csharpType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler);

                // Check if type is unsupported
                if (csharpType == TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName)
                {
                    _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' has unsupported associated value type at index {i}. Skipping TryGet method.");
                    return;
                }

                // Use type label if available, otherwise generate a name
                var paramName = typeSpec.TypeLabel ?? $"value{i}";
                paramName = SanitizeParameterName(paramName);
                parameters.Add((csharpType, paramName, typeSpec));
            }

            // Single value: output is just that type
            // Multiple values: output is a tuple
            if (parameters.Count == 1)
            {
                outType = parameters[0].type;
            }
            else
            {
                var tupleElements = parameters.Select(p =>
                    !string.IsNullOrEmpty(p.typeSpec.TypeLabel)
                        ? $"{p.type} {SanitizeParameterName(p.typeSpec.TypeLabel)}"
                        : p.type);
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
            if (parameters.Count == 1)
            {
                var (type, name, typeSpec) = parameters[0];
                EmitPayloadMarshal(csWriter, typeSpec, "value", "enumCopy", typeDatabase);
            }
            else
            {
                // For tuples, we need to marshal each element
                // Swift stores tuple elements sequentially in memory
                // We'll create individual values and then construct the tuple
                csWriter.WriteLine("// For multi-value associated types, values are stored sequentially");

                var valueNames = new List<string>();
                for (int i = 0; i < parameters.Count; i++)
                {
                    var (_, _, typeSpec) = parameters[i];
                    var valueName = $"_val{i}";
                    valueNames.Add(valueName);

                    if (i == 0)
                    {
                        EmitPayloadMarshalWithDeclaration(csWriter, typeSpec, valueName, "enumCopy", typeDatabase);
                    }
                    else
                    {
                        // For subsequent elements, we need to calculate offset based on type sizes
                        // This is a simplification - in practice, we'd need alignment handling
                        csWriter.WriteLine($"// TODO: Proper offset calculation for element {i}");
                        EmitPayloadMarshalWithDeclaration(csWriter, typeSpec, valueName, "enumCopy", typeDatabase);
                    }
                }

                // Construct the tuple
                var tupleConstruction = string.Join(", ", valueNames);
                csWriter.WriteLine($"value = ({tupleConstruction});");
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
        private void EmitTryGetMethodForTuple(CSharpWriter csWriter, EnumDecl enumDecl, EnumCaseDecl caseDecl, TupleTypeSpec tupleSpec, ITypeDatabase typeDatabase, string typeNameWithGenerics)
        {
            var caseName = caseDecl.Name;
            var capitalizedName = char.ToUpper(caseName[0]) + caseName.Substring(1);
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);
            var tupleHandler = new TupleHandler(typeDatabase);

            // Validate tuple element count (max 7 per C# ValueTuple limit)
            if (tupleSpec.Elements.Count > TupleHandler.MaxSupportedTupleElements)
            {
                _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' has tuple with {tupleSpec.Elements.Count} elements (max {TupleHandler.MaxSupportedTupleElements}). Skipping TryGet method.");
                return;
            }

            // Validate and build parameter list from tuple elements
            var parameters = new List<(string type, string name, TypeSpec typeSpec)>();
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

                var csharpType = GetCSharpTypeNameForEnumCase(element, typeDatabase, boundGenericsHandler);

                // Check if type is unsupported
                if (csharpType == TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName)
                {
                    _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' has unsupported tuple element type at index {i}. Skipping TryGet method.");
                    return;
                }

                // Use element label if available, otherwise generate a name
                var paramName = element.TypeLabel ?? $"value{i}";
                paramName = SanitizeParameterName(paramName);
                parameters.Add((csharpType, paramName, element));
            }

            // Build the out parameter list for the method signature
            var outParams = parameters.Select(p => $"[MaybeNullWhen(false)] out {p.type} {p.name}");
            var outParamString = string.Join(", ", outParams);

            // Emit the TryGet method
            csWriter.WriteLine("/// <summary>");
            csWriter.WriteLine($"/// Attempts to extract the associated value(s) for the '{caseName}' case.");
            csWriter.WriteLine("/// </summary>");
            foreach (var (_, name, _) in parameters)
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
            foreach (var (_, name, _) in parameters)
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
                var (_, name, typeSpec) = parameters[i];
                csWriter.WriteLine($"var offset{i} = tupleMetadata->GetElementOffset({i});");
                EmitPayloadMarshalWithOffset(csWriter, typeSpec, name, "enumCopy", $"offset{i}", typeDatabase);
            }
            csWriter.WriteLine();

            csWriter.WriteLine("return true;");
            csWriter.Indent--;
            csWriter.WriteLine("}"); // unsafe
            csWriter.Indent--;
            csWriter.WriteLine("}"); // TryGet
            csWriter.WriteLine();

            // Emit the tuple metadata accessor helper
            EmitTupleMetadataAccessor(csWriter, capitalizedName, parameters, typeDatabase);
        }
    }
}
