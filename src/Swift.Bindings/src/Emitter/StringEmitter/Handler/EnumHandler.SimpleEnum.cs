// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    public partial class EnumHandler
    {
        /// <summary>
        /// Maps a Swift raw value type name to the corresponding C# enum underlying type.
        /// </summary>
        internal static string GetCSharpEnumUnderlyingType(string? rawValueTypeName)
        {
            return rawValueTypeName switch
            {
                "Int8" => "sbyte",
                "UInt8" => "byte",
                "Int16" => "short",
                "UInt16" => "ushort",
                "Int32" or "Int" => "int",
                "UInt32" or "UInt" => "uint",
                "Int64" => "long",
                "UInt64" => "ulong",
                // No raw value → int (tag values fit in int for practical enum sizes)
                null or "" => "int",
                _ => "int"
            };
        }

        /// <summary>
        /// Maps a C# enum underlying type to the corresponding Swift scalar type for P/Invoke.
        /// </summary>
        private static string GetSwiftScalarType(string csUnderlyingType)
        {
            return csUnderlyingType switch
            {
                "sbyte" => "Int8",
                "byte" => "UInt8",
                "short" => "Int16",
                "ushort" => "UInt16",
                "int" => "Int32",
                "uint" => "UInt32",
                "long" => "Int64",
                "ulong" => "UInt64",
                _ => "Int32"
            };
        }

        /// <summary>
        /// Emits a simple enum as a C# enum value type, with an optional extensions class
        /// for instance methods and properties.
        /// </summary>
        private void EmitSimpleEnum(CSharpWriter csWriter, SwiftWriter swiftWriter,
            EnumDecl enumDecl, ModuleDecl moduleDecl, ITypeDatabase typeDatabase, Conductor conductor)
        {
            var enumName = conductor.NestedTypeRenames != null &&
                conductor.NestedTypeRenames.TryGetValue(enumDecl.Name, out var renamedName)
                ? renamedName : enumDecl.Name;
            var csUnderlyingType = GetCSharpEnumUnderlyingType(enumDecl.RawValueTypeName);

            // Emit the C# enum declaration
            XmlDocCommentEmitter.EmitDocComment(csWriter, enumDecl);
            csWriter.WriteLine($"public enum {enumName} : {csUnderlyingType}");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // Emit enum members
            foreach (var caseDecl in enumDecl.Cases)
            {
                var casePascalName = NameProvider.ToPascalCase(caseDecl.Name);
                int tagValue;

                if (enumDecl.IsStringRawValue)
                {
                    // String raw value enums use tag values (not raw values)
                    tagValue = enumDecl.GetCaseTag(caseDecl);
                }
                else if (enumDecl.IsRawRepresentable)
                {
                    // Use raw values for integral RawRepresentable enums
                    tagValue = GetRawValueAsInt(enumDecl, caseDecl);
                }
                else
                {
                    // Use tag values from GetCaseTag for non-RawRepresentable enums
                    tagValue = enumDecl.GetCaseTag(caseDecl);
                }

                XmlDocCommentEmitter.EmitDocComment(csWriter, caseDecl);
                csWriter.WriteLine($"{casePascalName} = {tagValue},");
            }

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            // For String-raw-value enums, emit ToRawValue/FromRawValue extension methods
            if (enumDecl.IsStringRawValue)
            {
                EmitStringRawValueExtensions(csWriter, enumDecl, enumName);
            }

            // Emit extension methods class if there are instance methods or properties
            var instanceMethods = enumDecl.Methods.Where(m => !m.IsConstructor && m.MethodType != MethodType.Static).ToList();
            var staticMethods = enumDecl.Methods.Where(m => !m.IsConstructor && m.MethodType == MethodType.Static).ToList();
            var instanceProperties = enumDecl.Properties.Where(p => !p.IsStatic).ToList();

            // Record enum operators — equality is handled by C# enum semantics
            foreach (var operatorDecl in enumDecl.Operators)
            {
                if (operatorDecl.Name == "==" || operatorDecl.Name == "!=")
                    ReportCollector.RecordMemberEmitted(BindingItemKind.Operator, operatorDecl.Name, enumDecl);
                else
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Operator, operatorDecl.Name, enumDecl, SkipReason.UnsupportedType, $"Operator '{operatorDecl.Name}' is not supported on simple enum types.");
            }

            // Record constructors as emitted
            foreach (var methodDecl in enumDecl.Methods.Where(m => m.IsConstructor))
                ReportCollector.RecordMemberEmitted(BindingItemKind.Method, methodDecl.Name, enumDecl);

            if (instanceMethods.Count > 0 || staticMethods.Count > 0 || instanceProperties.Count > 0)
            {
                EmitSimpleEnumExtensions(csWriter, swiftWriter, enumDecl, enumName, instanceMethods,
                    staticMethods, instanceProperties, moduleDecl, typeDatabase, conductor);
            }

            // Emit nested types using base handler
            base.HandleBaseDecl(csWriter, swiftWriter, enumDecl.Types, conductor, typeDatabase);
        }

        /// <summary>
        /// Emits the extensions class containing methods and properties for a simple enum.
        /// Instance methods become static extension methods, static methods stay static.
        /// </summary>
        private void EmitSimpleEnumExtensions(CSharpWriter csWriter, SwiftWriter swiftWriter,
            EnumDecl enumDecl, string enumName, List<MethodDecl> instanceMethods, List<MethodDecl> staticMethods,
            List<PropertyDecl> instanceProperties, ModuleDecl moduleDecl,
            ITypeDatabase typeDatabase, Conductor conductor)
        {
            var csUnderlyingType = GetCSharpEnumUnderlyingType(enumDecl.RawValueTypeName);
            var swiftScalarType = GetSwiftScalarType(csUnderlyingType);

            // Buffer extensions content — only emit class if at least one member was emitted
            var bufferSw = new System.IO.StringWriter();
            var bufferWriter = new CSharpWriter(bufferSw);
            bufferWriter.Indent = csWriter.Indent + 1;

            // Emit instance methods as extension methods with Swift wrapper
            foreach (var methodDecl in instanceMethods)
            {
                EmitSimpleEnumExtensionMethod(bufferWriter, swiftWriter, enumDecl, enumName, methodDecl,
                    moduleDecl, typeDatabase, csUnderlyingType, swiftScalarType);
            }

            // Emit instance properties as extension methods
            foreach (var propertyDecl in instanceProperties)
            {
                EmitSimpleEnumExtensionProperty(bufferWriter, swiftWriter, enumDecl, propertyDecl,
                    moduleDecl, typeDatabase, csUnderlyingType, swiftScalarType);
            }

            // Emit static methods directly
            foreach (var methodDecl in staticMethods)
            {
                EmitSimpleEnumStaticMethod(bufferWriter, swiftWriter, enumDecl, methodDecl,
                    moduleDecl, typeDatabase, csUnderlyingType, swiftScalarType);
            }

            var bufferedContent = bufferSw.ToString();
            if (!string.IsNullOrWhiteSpace(bufferedContent))
            {
                csWriter.WriteLine($"public static partial class {enumName}Extensions");
                csWriter.WriteLine("{");
                csWriter.InnerWriter.Write(bufferedContent);
                csWriter.WriteLine("}");
                csWriter.WriteLine();
            }
        }

        /// <summary>
        /// Emits ToRawValue/FromRawValue extension methods for String-raw-value enums.
        /// These are pure C# (no Swift P/Invoke needed) since the mapping is known at codegen time.
        /// Note: Uses case names as raw values (known limitation — ABI JSON lacks individual case raw values).
        /// </summary>
        private static void EmitStringRawValueExtensions(CSharpWriter csWriter, EnumDecl enumDecl, string enumName)
        {
            csWriter.WriteLine($"public static partial class {enumName}Extensions");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // ToRawValue()
            csWriter.WriteLine($"public static string ToRawValue(this {enumName} value)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("return value switch");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            foreach (var caseDecl in enumDecl.Cases)
            {
                var casePascalName = NameProvider.ToPascalCase(caseDecl.Name);
                csWriter.WriteLine($"{enumName}.{casePascalName} => \"{caseDecl.Name}\",");
            }
            csWriter.WriteLine($"_ => throw new ArgumentOutOfRangeException(nameof(value), value, null),");
            csWriter.Indent--;
            csWriter.WriteLine("};");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            // FromRawValue()
            csWriter.WriteLine($"public static {enumName}? FromRawValue(string rawValue)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("return rawValue switch");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            foreach (var caseDecl in enumDecl.Cases)
            {
                var casePascalName = NameProvider.ToPascalCase(caseDecl.Name);
                csWriter.WriteLine($"\"{caseDecl.Name}\" => {enumName}.{casePascalName},");
            }
            csWriter.WriteLine("_ => null,");
            csWriter.Indent--;
            csWriter.WriteLine("};");
            csWriter.Indent--;
            csWriter.WriteLine("}");

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits a single instance method as a static extension method with Swift wrapper.
        /// </summary>
        private void EmitSimpleEnumExtensionMethod(CSharpWriter csWriter, SwiftWriter swiftWriter,
            EnumDecl enumDecl, string enumName, MethodDecl methodDecl, ModuleDecl moduleDecl,
            ITypeDatabase typeDatabase, string csUnderlyingType, string swiftScalarType)
        {
            var moduleName = moduleDecl.Name;
            var methodPascalName = NameProvider.ToPascalCase(methodDecl.Name);
            var libPath = typeDatabase.GetLibraryPath(moduleName);

            // Determine return type
            var returnTypeSpec = methodDecl.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
            bool returnsEnum = IsSimpleEnumReturn(returnTypeSpec, enumDecl, typeDatabase);
            bool returnsVoid = returnTypeSpec == null || IsVoidReturn(returnTypeSpec);

            string csReturnType;
            if (returnsVoid)
                csReturnType = "void";
            else if (returnsEnum)
                csReturnType = enumName;
            else
            {
                csReturnType = GetSimpleReturnType(returnTypeSpec!, typeDatabase) ?? null!;
                if (csReturnType == null)
                {
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodDecl.Name, enumDecl,
                        SkipReason.UnsupportedSignature, "Return type is unsupported for simple enum extension method.");
                    return;
                }
            }

            // Validate all parameters BEFORE emitting anything
            // Skip parameters at index 0 (return type) and any 'self' parameters
            var paramDecls = methodDecl.CSSignature
                .Skip(1) // skip return type
                .Where(a => a.Name != "self")
                .ToList();

            var csParams = new List<string> { $"this {enumName} self" };
            foreach (var param in paramDecls)
            {
                var paramType = GetSimpleParamType(param.SwiftTypeSpec, typeDatabase);
                if (paramType == null)
                {
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodDecl.Name, enumDecl,
                        SkipReason.UnsupportedSignature, $"Parameter '{param.Name}' has unsupported type for simple enum extension method.");
                    return;
                }
                var csParamName = NameProvider.GetCSharpParameterName(param);
                csParams.Add($"{paramType} {csParamName}");
            }

            // All parameters validated — now emit Swift wrapper
            var wrapperSymbol = $"SBW_{moduleName}_{enumName}_{methodDecl.Name}_{DeterministicHash8(methodDecl.MangledName)}";
            EmitSimpleEnumSwiftWrapper(swiftWriter, enumDecl, methodDecl, wrapperSymbol,
                swiftScalarType, moduleName, returnsEnum);

            // Emit extension method
            csWriter.WriteLine($"public static {csReturnType} {methodPascalName}({string.Join(", ", csParams)})");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // Build P/Invoke call arguments: cast enum to underlying type
            var callArgs = new List<string> { $"({csUnderlyingType})self" };
            foreach (var param in paramDecls)
            {
                callArgs.Add(NameProvider.GetCSharpParameterName(param));
            }

            if (returnsVoid)
            {
                csWriter.WriteLine($"PInvoke_{methodPascalName}({string.Join(", ", callArgs)});");
            }
            else if (returnsEnum)
            {
                csWriter.WriteLine($"return ({enumName})PInvoke_{methodPascalName}({string.Join(", ", callArgs)});");
            }
            else
            {
                csWriter.WriteLine($"return PInvoke_{methodPascalName}({string.Join(", ", callArgs)});");
            }

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            // Emit P/Invoke declaration
            var pinvokeParams = new List<string> { $"{csUnderlyingType} tag" };
            foreach (var param in paramDecls)
            {
                var paramType = GetSimpleParamType(param.SwiftTypeSpec, typeDatabase);
                var marshalPrefix = paramType == "bool" ? "[MarshalAs(UnmanagedType.U1)] " : "";
                pinvokeParams.Add($"{marshalPrefix}{paramType} {NameProvider.GetCSharpParameterName(param)}");
            }

            var pinvokeReturnType = returnsVoid ? "void" : (returnsEnum ? csUnderlyingType : csReturnType);
            csWriter.WriteLine($"[LibraryImport(\"SwiftBindings\", EntryPoint = \"{wrapperSymbol}\")]");
            if (MarshallingHelpers.IsBoolReturnType(pinvokeReturnType))
                csWriter.WriteLine("[return: MarshalAs(UnmanagedType.U1)]");
            csWriter.WriteLine($"private static partial {pinvokeReturnType} PInvoke_{methodPascalName}({string.Join(", ", pinvokeParams)});");
            csWriter.WriteLine();

            ReportCollector.RecordMemberEmitted(BindingItemKind.Method, methodDecl.Name, enumDecl);
        }

        /// <summary>
        /// Emits the Swift wrapper function for a simple enum instance method.
        /// The wrapper takes a scalar tag, converts to the enum case, calls the method,
        /// and converts any enum return back to scalar.
        /// </summary>
        private void EmitSimpleEnumSwiftWrapper(SwiftWriter swiftWriter, EnumDecl enumDecl,
            MethodDecl methodDecl, string wrapperSymbol, string swiftScalarType,
            string moduleName, bool returnsEnum)
        {
            var enumQualifiedName = $"{moduleName}.{enumDecl.Name}";
            var returnTypeStr = returnsEnum ? swiftScalarType : GetSwiftReturnType(methodDecl);

            // Build parameter list: tag + method params
            var swiftParams = new List<string> { $"_ tag: {swiftScalarType}" };
            var paramDecls = methodDecl.CSSignature
                .Skip(1)
                .Where(a => a.Name != "self")
                .ToList();
            foreach (var param in paramDecls)
            {
                var swiftType = GetSwiftParamType(param.SwiftTypeSpec, moduleName);
                if (swiftType != null)
                {
                    var label = NameProvider.IsGeneratedArgName(param.Name) ? "_" : param.Name;
                    swiftParams.Add($"{label} {param.PrivateName ?? param.Name}: {swiftType}");
                }
            }

            swiftWriter.WriteLine($"@_silgen_name(\"{wrapperSymbol}\")");
            swiftWriter.WriteLine($"func _sbw_{enumDecl.Name}_{methodDecl.Name}({string.Join(", ", swiftParams)}) -> {returnTypeStr} {{");
            swiftWriter.Indent++;

            // Convert tag to enum value using switch
            if (enumDecl.IsRawRepresentable)
            {
                swiftWriter.WriteLine($"let value = {enumQualifiedName}(rawValue: {GetSwiftRawValueCast(enumDecl, swiftScalarType)})!");
            }
            else
            {
                EmitTagToEnumSwitch(swiftWriter, enumDecl, enumQualifiedName, swiftScalarType);
            }

            // Build method call with arguments
            var callArgs = new List<string>();
            foreach (var param in paramDecls)
            {
                var label = NameProvider.IsGeneratedArgName(param.Name) ? "" : $"{NameProvider.StripCSharpKeywordPrefix(param.Name)}: ";
                callArgs.Add($"{label}{param.PrivateName ?? param.Name}");
            }

            var callStr = callArgs.Count > 0
                ? $"value.{methodDecl.Name}({string.Join(", ", callArgs)})"
                : $"value.{methodDecl.Name}()";

            if (returnsEnum)
            {
                // Convert return value back to tag
                swiftWriter.WriteLine($"let result = {callStr}");
                if (enumDecl.IsRawRepresentable)
                {
                    swiftWriter.WriteLine($"return {GetSwiftRawValueReturn(enumDecl, swiftScalarType)}");
                }
                else
                {
                    EmitEnumToTagSwitch(swiftWriter, enumDecl, enumQualifiedName, "result", swiftScalarType);
                }
            }
            else
            {
                swiftWriter.WriteLine($"return {callStr}");
            }

            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
            swiftWriter.WriteLine();
        }

        /// <summary>
        /// Emits a Swift switch statement that converts a scalar tag to an enum case.
        /// </summary>
        private void EmitTagToEnumSwitch(SwiftWriter swiftWriter, EnumDecl enumDecl,
            string enumQualifiedName, string swiftScalarType)
        {
            swiftWriter.WriteLine("let value: " + enumQualifiedName);
            swiftWriter.WriteLine("switch tag {");
            foreach (var caseDecl in enumDecl.Cases)
            {
                var tag = enumDecl.GetCaseTag(caseDecl);
                swiftWriter.WriteLine($"case {tag}: value = .{caseDecl.Name}");
            }
            swiftWriter.WriteLine($"default: fatalError(\"Invalid enum tag\")");
            swiftWriter.WriteLine("}");
        }

        /// <summary>
        /// Emits a Swift switch statement that converts an enum case to a scalar tag.
        /// </summary>
        private void EmitEnumToTagSwitch(SwiftWriter swiftWriter, EnumDecl enumDecl,
            string enumQualifiedName, string varName, string swiftScalarType)
        {
            swiftWriter.WriteLine($"switch {varName} {{");
            foreach (var caseDecl in enumDecl.Cases)
            {
                var tag = enumDecl.GetCaseTag(caseDecl);
                swiftWriter.WriteLine($"case .{caseDecl.Name}: return {tag}");
            }
            swiftWriter.WriteLine("}");
        }

        /// <summary>
        /// Emits an instance property as a static extension method.
        /// </summary>
        private void EmitSimpleEnumExtensionProperty(CSharpWriter csWriter, SwiftWriter swiftWriter,
            EnumDecl enumDecl, PropertyDecl propertyDecl, ModuleDecl moduleDecl,
            ITypeDatabase typeDatabase, string csUnderlyingType, string swiftScalarType)
        {
            // For now, skip instance properties on simple enums — they require more complex
            // wrapper infrastructure. Record as emitted if we can or skipped if not.
            // Most simple enums don't have instance properties.
            ReportCollector.RecordMemberSkipped(BindingItemKind.Property, propertyDecl.Name, enumDecl,
                SkipReason.UnsupportedType, "Instance properties on simple enums are not yet supported as extension methods.");
        }

        /// <summary>
        /// Emits a static method in the extensions class.
        /// </summary>
        private void EmitSimpleEnumStaticMethod(CSharpWriter csWriter, SwiftWriter swiftWriter,
            EnumDecl enumDecl, MethodDecl methodDecl, ModuleDecl moduleDecl,
            ITypeDatabase typeDatabase, string csUnderlyingType, string swiftScalarType)
        {
            // Static methods don't need enum conversion — they operate on the type level.
            // For now, skip them as they require the full method emission pipeline.
            ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodDecl.Name, enumDecl,
                SkipReason.UnsupportedType, "Static methods on simple enums are not yet supported.");
        }

        // === Helper Methods for Simple Enum Emission ===

        /// <summary>
        /// Gets the raw value as int for a case in a RawRepresentable enum.
        /// For Int-based enums, the raw value equals the tag value in sequential enums.
        /// </summary>
        private static int GetRawValueAsInt(EnumDecl enumDecl, EnumCaseDecl caseDecl)
        {
            // For RawRepresentable enums, Swift assigns raw values sequentially from 0
            // unless explicitly specified. The ABI JSON doesn't contain explicit raw values,
            // so we use tag values which match sequential raw values.
            return enumDecl.GetCaseTag(caseDecl);
        }

        private static bool IsSimpleEnumReturn(TypeSpec? typeSpec, EnumDecl enumDecl, ITypeDatabase typeDatabase)
        {
            if (typeSpec is NamedTypeSpec namedType)
            {
                var typeName = namedType.NameWithoutModule;
                return typeName == enumDecl.Name;
            }
            return false;
        }

        private static bool IsVoidReturn(TypeSpec typeSpec)
        {
            if (typeSpec is TupleTypeSpec tuple && tuple.Elements.Count == 0)
                return true;
            if (typeSpec is NamedTypeSpec named && named.Name == "Swift.Void")
                return true;
            return false;
        }

        /// <summary>
        /// Gets C# return type for a non-void, non-enum return.
        /// Returns null if the type is unsupported.
        /// </summary>
        private static string? GetSimpleReturnType(TypeSpec typeSpec, ITypeDatabase typeDatabase)
        {
            if (typeSpec is NamedTypeSpec named)
            {
                // Check for primitive types
                var primitiveType = MapSwiftPrimitive(named.Name);
                if (primitiveType != null) return primitiveType;

                // Check if SwiftString
                if (named.Name == "Swift.String") return "string";
                if (named.Name == "Swift.Bool") return "bool";
            }
            return null;
        }

        /// <summary>
        /// Gets C# parameter type for a simple enum method parameter.
        /// Returns null if the type is unsupported.
        /// </summary>
        private static string? GetSimpleParamType(TypeSpec typeSpec, ITypeDatabase typeDatabase)
        {
            if (typeSpec is NamedTypeSpec named)
            {
                var primitiveType = MapSwiftPrimitive(named.Name);
                if (primitiveType != null) return primitiveType;

                if (named.Name == "Swift.String") return "string";
                if (named.Name == "Swift.Bool") return "bool";
            }
            return null;
        }

        /// <summary>
        /// Gets the Swift return type string for a method.
        /// </summary>
        private static string GetSwiftReturnType(MethodDecl methodDecl)
        {
            var returnTypeSpec = methodDecl.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
            if (returnTypeSpec == null) return "Void";

            if (returnTypeSpec is TupleTypeSpec tuple && tuple.Elements.Count == 0)
                return "Void";

            if (returnTypeSpec is NamedTypeSpec named)
            {
                if (named.Name == "Swift.String") return "String";
                if (named.Name == "Swift.Bool") return "Bool";
                if (named.Name == "Swift.Int") return "Int";
                if (named.Name == "Swift.Int32") return "Int32";
                if (named.Name == "Swift.Double") return "Double";
                if (named.Name == "Swift.Float") return "Float";
                return named.NameWithoutModule;
            }

            return "Void";
        }

        /// <summary>
        /// Gets the Swift parameter type string.
        /// </summary>
        private static string? GetSwiftParamType(TypeSpec typeSpec, string moduleName)
        {
            if (typeSpec is NamedTypeSpec named)
            {
                var name = named.Name;
                if (name.StartsWith("Swift.")) return named.NameWithoutModule;
                if (name.StartsWith(moduleName + ".")) return named.NameWithoutModule;
                return named.NameWithoutModule;
            }
            return null;
        }

        private static string? MapSwiftPrimitive(string swiftName)
        {
            return swiftName switch
            {
                "Swift.Int" => "nint",
                "Swift.UInt" => "nuint",
                "Swift.Int8" => "sbyte",
                "Swift.UInt8" => "byte",
                "Swift.Int16" => "short",
                "Swift.UInt16" => "ushort",
                "Swift.Int32" => "int",
                "Swift.UInt32" => "uint",
                "Swift.Int64" => "long",
                "Swift.UInt64" => "ulong",
                "Swift.Float" => "float",
                "Swift.Double" => "double",
                _ => null
            };
        }

        private static string GetSwiftRawValueCast(EnumDecl enumDecl, string swiftScalarType)
        {
            // For Int raw values, need to cast the scalar type to the raw value type
            var rawType = enumDecl.RawValueTypeName!;
            if (rawType == swiftScalarType || rawType == "Int32" && swiftScalarType == "Int32")
                return "tag";
            return $"{rawType}(tag)";
        }

        private static string GetSwiftRawValueReturn(EnumDecl enumDecl, string swiftScalarType)
        {
            var rawType = enumDecl.RawValueTypeName!;
            if (rawType == swiftScalarType || rawType == "Int32" && swiftScalarType == "Int32")
                return "result.rawValue";
            return $"{swiftScalarType}(result.rawValue)";
        }

        private static string DeterministicHash8(string input) => EmitterUtility.DeterministicHash8(input);

        /// <summary>
        /// Checks whether an enum can be safely emitted as a C# enum value type
        /// without losing members that the class-based path would have emitted.
        /// Returns false if the enum has properties (instance or static), static methods,
        /// non-equality operators, or incompatible instance method signatures.
        /// </summary>
        internal static bool CanSafelyEmitAsSimpleEnum(EnumDecl enumDecl)
        {
            // C# enums cannot contain nested types — if the enum has nested types,
            // they must be emitted inside the parent container (class-based path)
            if (enumDecl.Types.Any())
                return false;

            // All properties are skipped on the simple path (instance AND static)
            if (enumDecl.Properties.Any())
                return false;

            // Static methods are always skipped on the simple path
            if (enumDecl.Methods.Any(m => !m.IsConstructor && m.MethodType == MethodType.Static))
                return false;

            // Non-equality operators are always skipped on the simple path
            if (enumDecl.Operators.Any(o => o.Name != "==" && o.Name != "!="))
                return false;

            // Instance methods must have simple-emitter-compatible signatures
            if (!AreAllInstanceMethodsSimpleEmitterCompatible(enumDecl))
                return false;

            return true;
        }

        /// <summary>
        /// Checks whether all instance methods on an enum have signatures compatible with the
        /// simple-enum extension method emitter. Only methods whose return types and parameter
        /// types are within the supported primitive/string/bool/void/same-enum set qualify.
        /// If any instance method has an unsupported signature, the enum should stay class-based
        /// to avoid silently dropping members that the class path would have emitted.
        /// </summary>
        internal static bool AreAllInstanceMethodsSimpleEmitterCompatible(EnumDecl enumDecl)
        {
            var instanceMethods = enumDecl.Methods
                .Where(m => !m.IsConstructor && m.MethodType != MethodType.Static);

            foreach (var method in instanceMethods)
            {
                // Check return type
                var returnTypeSpec = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
                if (returnTypeSpec != null && !IsVoidReturn(returnTypeSpec))
                {
                    bool returnsEnum = returnTypeSpec is NamedTypeSpec namedReturn &&
                        namedReturn.NameWithoutModule == enumDecl.Name;
                    if (!returnsEnum && GetSimpleReturnType(returnTypeSpec, null!) == null)
                        return false;
                }

                // Check parameters (skip index 0 = return type, skip self)
                var paramDecls = method.CSSignature
                    .Skip(1)
                    .Where(a => a.Name != "self");

                foreach (var param in paramDecls)
                {
                    if (GetSimpleParamType(param.SwiftTypeSpec, null!) == null)
                        return false;
                }
            }

            return true;
        }
    }
}
