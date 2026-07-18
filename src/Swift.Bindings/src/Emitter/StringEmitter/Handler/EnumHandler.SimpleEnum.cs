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
            // Accept BOTH the module-qualified ("Swift.Int64") and unqualified ("Int64") raw-value
            // spellings for every known integral type. This must mirror the domain of
            // CdeclParamMapper.GetSwiftRawValueType, which already recognizes both forms — the two
            // helpers describe the SAME value crossing the @_cdecl boundary and must agree on its
            // byte width. If only the unqualified form were handled here, a qualified raw-value name
            // would fall through to 4-byte `int` while the Swift wrapper emitted the true width
            // (e.g. Int64 = 8), corrupting the call frame on any non-arm64-zero-extended path.
            // Pinned by EnumAbiWidthConsistencyTests across the full qualified+unqualified domain.
            return rawValueTypeName switch
            {
                "Swift.Int8" or "Int8" => "sbyte",
                "Swift.UInt8" or "UInt8" => "byte",
                "Swift.Int16" or "Int16" => "short",
                "Swift.UInt16" or "UInt16" => "ushort",
                "Swift.Int32" or "Int32" => "int",
                "Swift.UInt32" or "UInt32" => "uint",
                // Swift.Int and Swift.UInt are platform-width (64-bit on arm64/x86_64).
                // Map to long/ulong to match NSInteger/NSUInteger ABI.
                "Swift.Int64" or "Int64" or "Swift.Int" or "Int" => "long",
                "Swift.UInt64" or "UInt64" or "Swift.UInt" or "UInt" => "ulong",
                // No raw value → int (tag values fit in int for practical enum sizes)
                null or "" => "int",
                _ => "int"
            };
        }

        /// <summary>
        /// Maps a C# enum underlying type to the corresponding Swift scalar type for P/Invoke.
        /// </summary>
        internal static string GetSwiftScalarType(string csUnderlyingType)
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
        /// Computes the fully qualified C# name for a nested enum by walking up the parent chain.
        /// E.g., for Swift's ImageProcessingOptions.Unit, returns "ImageProcessingOptions.Unit".
        /// </summary>
        private static string GetQualifiedEnumName(EnumDecl enumDecl, ITypeDatabase? typeDatabase = null)
        {
            var parts = new List<string>();
            BaseDecl? current = enumDecl;
            while (current is TypeDecl typeDecl)
            {
                // Use CSharpTypeName leaf if the type was renamed (e.g., nested type collision)
                parts.Add(GenericTypeEmitter.GetTypeNameWithGenerics(typeDecl, typeDatabase));
                current = typeDecl.ParentDecl;
            }
            parts.Reverse();
            return string.Join(".", parts);
        }

        /// <summary>
        /// Computes the flattened extension class name for a nested enum.
        /// E.g., ImageProcessingOptions.Unit → "ImageProcessingOptionsUnitExtensions"
        /// </summary>
        private static string GetFlattenedExtensionClassName(EnumDecl enumDecl, ITypeDatabase? typeDatabase = null)
        {
            var parts = new List<string>();
            BaseDecl? current = enumDecl;
            while (current is TypeDecl typeDecl)
            {
                parts.Add(GenericTypeEmitter.GetTypeNameWithGenerics(typeDecl, typeDatabase));
                current = typeDecl.ParentDecl;
            }
            parts.Reverse();
            return string.Concat(parts) + "Extensions";
        }

        /// <summary>
        /// Emits a simple enum as a C# enum value type, with an optional extensions class
        /// for instance methods and properties.
        /// </summary>
        private void EmitSimpleEnum(CSharpWriter csWriter, SwiftWriter swiftWriter,
            EnumDecl enumDecl, ModuleDecl moduleDecl, ITypeDatabase typeDatabase, Conductor conductor, TypeHandlerContext context)
        {
            // Simple enums emit as C# enum value types (cases become enum members, not properties),
            // so they bypass ComputePropertyRenames — no CS0542 risk from nested-type collisions.
            // Use GenericTypeEmitter to resolve the name (handles CSharpTypeName renames for nested type collisions)
            var enumName = GenericTypeEmitter.GetTypeNameWithGenerics(enumDecl, typeDatabase);
            var csUnderlyingType = GetCSharpEnumUnderlyingType(enumDecl.RawValueTypeName);

            // Compute case name map for case-insensitive collision avoidance
            var caseNameMap = NameProvider.ComputeCaseNameMap(enumDecl.Cases);

            // Emit the C# enum declaration
            XmlDocCommentEmitter.EmitDocComment(csWriter, enumDecl);
            AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, enumDecl, emitObsolete: true);
            if (enumDecl.Name.StartsWith("_"))
                csWriter.WriteLine("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
            csWriter.WriteLine($"public enum {enumName} : {csUnderlyingType}");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // Emit enum members (skip @_spi-protected cases)
            foreach (var caseDecl in enumDecl.Cases)
            {
                if (caseDecl.IsSpiProtected)
                    continue;
                var casePascalName = NameProvider.GetCaseName(caseDecl.Name, caseNameMap);

                // The emitted C# member value is the SAME scalar the @_cdecl wrapper marshals
                // this case as (GetCaseMarshalScalar): the real Swift source raw value for
                // integral RawRepresentable enums, the declaration-order tag otherwise. The
                // two must agree, so both read this one helper. String-raw-value enums keep
                // their tag here and expose the string via ToRawValue/FromRawValue extensions.
                long memberValue = enumDecl.GetCaseMarshalScalar(caseDecl);

                XmlDocCommentEmitter.EmitDocComment(csWriter, caseDecl);
                AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, caseDecl, parentDecl: enumDecl, emitObsolete: true);
                csWriter.WriteLine($"{casePascalName} = {memberValue},");
            }

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            // Emit @_cdecl metadata wrapper for simple enums (needed for correct SwiftOptional<T> layout).
            // Simple enums can't implement ISwiftObject, so metadata is registered via module initializer.
            // Gate on the SAME "can wrapper source spell this type?" predicate the enum-level Swift
            // discard writer consults, not the enum's own flag alone: an enum nested in an internal parent (e.g. one
            // declared in an extension on a foreign receiver that is absent from this module's public
            // set) is unspellable, so its @_cdecl is discarded — recording the C# DllImport+register on
            // the narrower own-flag check leaves a P/Invoke pointing at a symbol nothing defines, which
            // the wrapper-symbol integrity gate rightly fail-closes. The registration is a swallowed
            // no-op when the symbol can't resolve, so skipping both together loses no working capability.
            var metadataEmissionCtx = context.GetEmissionContext();
            if (metadataEmissionCtx != null && !WrapperValidation.IsTypeOrEnclosingModuleInternal(enumDecl))
            {
                var moduleQualified = enumDecl.SwiftTypeName.ModuleQualifiedName;
                var swiftModuleName = enumDecl.SwiftTypeName.Module;
                var metadataSymbol = MetadataWrapperEmitter.GetMetadataSymbolName(swiftModuleName, moduleQualified);
                MetadataWrapperEmitter.EmitIfNeeded(swiftWriter, swiftModuleName, moduleQualified, metadataSymbol, metadataEmissionCtx, enumDecl);

                var wrapperLibName = typeDatabase.AsyncLibraryName ?? typeDatabase.GetLibraryPath(moduleDecl.Name);
                metadataEmissionCtx.RecordSimpleEnumMetadata(enumName, metadataSymbol, wrapperLibName);
            }

            // Determine if this enum is nested inside another type (not just a module).
            // C# extension methods must be in top-level static classes, so nested enums
            // need their extension classes deferred to namespace level.
            bool isNestedEnum = enumDecl.ParentDecl is TypeDecl;
            string qualifiedEnumName = isNestedEnum ? GetQualifiedEnumName(enumDecl, typeDatabase) : enumName;

            // For String-raw-value enums, emit ToRawValue/FromRawValue extension methods
            if (enumDecl.IsStringRawValue)
            {
                if (isNestedEnum)
                {
                    var flatClassName = GetFlattenedExtensionClassName(enumDecl, typeDatabase);
                    var deferredSw = new System.IO.StringWriter();
                    var deferredWriter = new CSharpWriter(deferredSw);
                    deferredWriter.Indent = 1; // namespace level
                    EmitStringRawValueExtensions(deferredWriter, enumDecl, qualifiedEnumName, caseNameMap, extensionsClassName: flatClassName);
                    context.GetEmissionContext().AddDeferredEnumExtensionClass(deferredSw.ToString());
                }
                else
                {
                    EmitStringRawValueExtensions(csWriter, enumDecl, enumName, caseNameMap);
                }
            }

            // Emit extension methods class if there are instance methods or properties.
            // Module-internal methods are excluded — they cannot be called from Swift wrappers.
            // Synthesized protocol conformance members are excluded — C# enums handle them natively.
            var instanceMethods = enumDecl.Methods
                .Where(m => !m.IsConstructor && m.MethodType != MethodType.Static && !m.IsModuleInternal)
                .Where(m => !IsSynthesizedMethod(m, enumDecl))
                .ToList();
            var staticMethods = enumDecl.Methods.Where(m => !m.IsConstructor && m.MethodType == MethodType.Static && !m.IsModuleInternal).ToList();
            var instanceProperties = enumDecl.Properties
                .Where(p => !p.IsStatic)
                .Where(p => !IsSynthesizedProperty(p, enumDecl))
                .Where(p => !p.IsModuleInternal)
                .ToList();
            var staticProperties = enumDecl.Properties
                .Where(p => p.IsStatic)
                .Where(p => !IsSynthesizedProperty(p, enumDecl))
                .Where(p => !p.IsModuleInternal)
                .ToList();

            // Record enum operators — equality is handled by C# enum semantics
            foreach (var operatorDecl in enumDecl.Operators)
            {
                if (operatorDecl.Name == "==" || operatorDecl.Name == "!=")
                    ReportCollector.RecordMemberEmitted(operatorDecl);
                else
                    ReportCollector.RecordMemberSkipped(operatorDecl, SkipReason.UnsupportedType, $"Operator '{operatorDecl.Name}' is not supported on simple enum types.");
            }

            // Record constructors as emitted
            foreach (var methodDecl in enumDecl.Methods.Where(m => m.IsConstructor))
                ReportCollector.RecordMemberEmitted(methodDecl);

            // Record synthesized protocol conformance members — C# enums handle
            // Hashable (GetHashCode), RawRepresentable (underlying value), CaseIterable
            // (Enum.GetValues), etc. natively. These are not "skipped" — their functionality
            // is available via synthesized .NET equivalents.
            foreach (var prop in enumDecl.Properties.Where(p => !p.IsStatic && IsSynthesizedProperty(p, enumDecl)))
                ReportCollector.RecordMemberSynthesized(prop);
            foreach (var method in enumDecl.Methods.Where(m => !m.IsConstructor && m.MethodType != MethodType.Static && IsSynthesizedMethod(m, enumDecl)))
                ReportCollector.RecordMemberSynthesized(method);

            // Check CaseIterable conformance for AllCases property
            var hasCaseIterable = enumDecl.Conformances.Any(c => c.Protocol.Name == "CaseIterable");

            if (instanceMethods.Count > 0 || staticMethods.Count > 0 || instanceProperties.Count > 0
                || staticProperties.Count > 0 || hasCaseIterable)
            {
                if (isNestedEnum)
                {
                    // Buffer extensions at namespace level and defer.
                    // Use the flattened name for the class (ImageProcessingOptionsUnitExtensions)
                    // and the qualified name for type references (ImageProcessingOptions.Unit).
                    var flatClassName = GetFlattenedExtensionClassName(enumDecl, typeDatabase);
                    var deferredSw = new System.IO.StringWriter();
                    var deferredWriter = new CSharpWriter(deferredSw);
                    deferredWriter.Indent = 1; // namespace level
                    EmitSimpleEnumExtensions(deferredWriter, swiftWriter, enumDecl, qualifiedEnumName, instanceMethods,
                        staticMethods, instanceProperties, staticProperties, hasCaseIterable, moduleDecl, typeDatabase, conductor, context,
                        extensionsClassName: flatClassName);
                    var content = deferredSw.ToString();
                    if (!string.IsNullOrWhiteSpace(content))
                        context.GetEmissionContext().AddDeferredEnumExtensionClass(content);
                }
                else
                {
                    EmitSimpleEnumExtensions(csWriter, swiftWriter, enumDecl, enumName, instanceMethods,
                        staticMethods, instanceProperties, staticProperties, hasCaseIterable, moduleDecl, typeDatabase, conductor, context);
                }
            }

            // Emit nested types using base handler
            var emissionCtx = context.GetEmissionContext();
            emissionCtx?.PushTypeNesting(enumDecl.Name);
            base.HandleBaseDecl(csWriter, swiftWriter, enumDecl.Types, conductor, typeDatabase, context);
            emissionCtx?.PopTypeNesting();
        }

        /// <summary>
        /// Emits the extensions class containing methods and properties for a simple enum.
        /// Instance methods become static extension methods, static methods stay static.
        /// </summary>
        private void EmitSimpleEnumExtensions(CSharpWriter csWriter, SwiftWriter swiftWriter,
            EnumDecl enumDecl, string enumName, List<MethodDecl> instanceMethods, List<MethodDecl> staticMethods,
            List<PropertyDecl> instanceProperties, List<PropertyDecl> staticProperties,
            bool hasCaseIterable, ModuleDecl moduleDecl,
            ITypeDatabase typeDatabase, Conductor conductor, TypeHandlerContext context,
            string? extensionsClassName = null)
        {
            var csUnderlyingType = GetCSharpEnumUnderlyingType(enumDecl.RawValueTypeName);
            var swiftScalarType = GetSwiftScalarType(csUnderlyingType);
            _wrapperLibName = typeDatabase.AsyncLibraryName ?? typeDatabase.GetLibraryPath(moduleDecl.Name);
            _simpleEnumEmissionContext = context.GetEmissionContext();

            // Buffer extensions content — only emit class if at least one member was emitted
            var bufferSw = new System.IO.StringWriter();
            var bufferWriter = new CSharpWriter(bufferSw);
            bufferWriter.Indent = csWriter.Indent + 1;

            // Pre-emit Utf8Slice struct and Free function at top level if any member returns String or String?
            var hasStringReturn = instanceProperties.Any(p => IsStringOrOptionalStringReturn(p.SwiftTypeSpec))
                || staticProperties.Any(p => IsStringOrOptionalStringReturn(p.SwiftTypeSpec))
                || instanceMethods.Any(m => IsStringOrOptionalStringReturn(m.CSSignature.FirstOrDefault()?.SwiftTypeSpec))
                || staticMethods.Any(m => IsStringOrOptionalStringReturn(m.CSSignature.FirstOrDefault()?.SwiftTypeSpec));
            if (hasStringReturn)
            {
                var emissionCtx = context.GetEmissionContext();
                Utf8SliceEmitter.EmitIfNeeded(swiftWriter, emissionCtx);
                Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter, moduleDecl.Name, emissionCtx);
            }

            // Emit instance methods as extension methods with Swift wrapper
            foreach (var methodDecl in instanceMethods)
            {
                EmitSimpleEnumExtensionMethod(bufferWriter, swiftWriter, enumDecl, enumName, methodDecl,
                    moduleDecl, typeDatabase, csUnderlyingType, swiftScalarType);
            }

            // Emit instance properties as extension methods
            foreach (var propertyDecl in instanceProperties)
            {
                EmitSimpleEnumExtensionProperty(bufferWriter, swiftWriter, enumDecl, enumName, propertyDecl,
                    moduleDecl, typeDatabase, csUnderlyingType, swiftScalarType);
            }

            // Emit static methods directly
            foreach (var methodDecl in staticMethods)
            {
                EmitSimpleEnumStaticMethod(bufferWriter, swiftWriter, enumDecl, enumName, methodDecl,
                    moduleDecl, typeDatabase, csUnderlyingType, swiftScalarType);
            }

            // Emit static properties
            foreach (var propertyDecl in staticProperties)
            {
                EmitSimpleEnumStaticProperty(bufferWriter, swiftWriter, enumDecl, enumName, propertyDecl,
                    moduleDecl, typeDatabase, csUnderlyingType, swiftScalarType);
            }

            // Emit CaseIterable AllCases property (pure C#, no Swift P/Invoke)
            if (hasCaseIterable)
            {
                EmitCaseIterableAllCases(bufferWriter, enumDecl, enumName);
            }

            var bufferedContent = bufferSw.ToString();
            if (!string.IsNullOrWhiteSpace(bufferedContent))
            {
                var className = extensionsClassName ?? $"{enumName}Extensions";
                csWriter.WriteLine($"public static partial class {className}");
                csWriter.WriteLine("{");
                csWriter.InnerWriter.Write(bufferedContent);
                csWriter.WriteLine("}");
                csWriter.WriteLine();
            }
        }

        /// <summary>
        /// Emits ToRawValue/FromRawValue extension methods for String-raw-value enums.
        /// These are pure C# (no Swift P/Invoke needed) since the mapping is known at codegen time.
        /// Uses actual raw values from .swiftinterface when available, falls back to case names.
        /// </summary>
        private static void EmitStringRawValueExtensions(CSharpWriter csWriter, EnumDecl enumDecl, string enumName, Dictionary<string, string>? caseNameMap = null, string? extensionsClassName = null)
        {
            var className = extensionsClassName ?? $"{enumName}Extensions";
            csWriter.WriteLine($"public static partial class {className}");
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
                if (caseDecl.IsSpiProtected) continue;
                var casePascalName = NameProvider.GetCaseName(caseDecl.Name, caseNameMap);
                var rawValue = caseDecl.RawValue ?? caseDecl.Name;
                csWriter.WriteLine($"{enumName}.{casePascalName} => \"{rawValue}\",");
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
                if (caseDecl.IsSpiProtected) continue;
                var casePascalName = NameProvider.GetCaseName(caseDecl.Name, caseNameMap);
                var rawValue = caseDecl.RawValue ?? caseDecl.Name;
                csWriter.WriteLine($"\"{rawValue}\" => {enumName}.{casePascalName},");
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

            // Determine return type
            var returnTypeSpec = methodDecl.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
            bool returnsEnum = IsSimpleEnumReturn(returnTypeSpec, enumDecl, typeDatabase);
            bool returnsVoid = returnTypeSpec == null || IsVoidReturn(returnTypeSpec);
            bool returnsString = !returnsVoid && IsStringReturn(returnTypeSpec);

            string csReturnType;
            if (returnsVoid)
                csReturnType = "void";
            else if (returnsEnum)
                csReturnType = enumName;
            else if (returnsString)
                csReturnType = "string";
            else
            {
                csReturnType = GetSimpleReturnType(returnTypeSpec!, typeDatabase) ?? null!;
                if (csReturnType == null)
                {
                    ReportCollector.RecordMemberSkipped(methodDecl,
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
                bool isEnumParam = IsSimpleEnumParam(param.SwiftTypeSpec, enumDecl);
                var paramType = isEnumParam ? enumName : GetSimpleParamType(param.SwiftTypeSpec, typeDatabase);
                if (paramType == null)
                {
                    ReportCollector.RecordMemberSkipped(methodDecl,
                        SkipReason.UnsupportedSignature, $"Parameter '{param.Name}' has unsupported type for simple enum extension method.");
                    return;
                }
                var csParamName = NameProvider.GetCSharpParameterName(param);
                csParams.Add($"{paramType} {csParamName}");
            }

            // All parameters validated — now emit Swift wrapper
            var wrapperSymbol = $"SBW_{moduleName}_{enumName}_{methodDecl.Name}_{DeterministicHash8(methodDecl.MangledName)}";
            // simple-enum instance method wrapper. The
            // _simpleEnumEmissionContext is a per-enum context disjoint from regular
            // type emission; wrapper symbols include the enum name, so cross-emitter
            // collision is impossible. Per-kind method bucket is collision-safe.
            _simpleEnumEmissionContext?.TryAddMethodWrapperSymbol(wrapperSymbol, DeclIdFactory.ForMethod(methodDecl));
            EmitSimpleEnumSwiftWrapper(swiftWriter, enumDecl, methodDecl, wrapperSymbol,
                swiftScalarType, moduleName, returnsEnum, returnsString);

            if (returnsString)
            {
                // String return: use Utf8Slice marshalling pattern
                // Utf8Slice struct is shared at module level

                csWriter.WriteLine($"public static unsafe string {methodPascalName}({string.Join(", ", csParams)})");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                var callArgs = new List<string> { $"({csUnderlyingType})self" };
                foreach (var param in paramDecls)
                {
                    bool isEnumParam = IsSimpleEnumParam(param.SwiftTypeSpec, enumDecl);
                    var argName = NameProvider.GetCSharpParameterName(param);
                    callArgs.Add(isEnumParam ? $"({csUnderlyingType}){argName}" : argName);
                }
                csWriter.WriteLine($"IntPtr resultPtr = PInvoke_{methodPascalName}({string.Join(", ", callArgs)});");
                csWriter.WriteLine("try");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine("var slice = *(Utf8Slice*)resultPtr;");
                csWriter.WriteLine("return slice.Len > 0");
                csWriter.Indent++;
                csWriter.WriteLine("? global::System.Text.Encoding.UTF8.GetString((byte*)slice.Ptr, (int)slice.Len)");
                csWriter.WriteLine(": string.Empty;");
                csWriter.Indent--;
                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine($"finally {{ PInvoke_SBW_Free(resultPtr); }}");
                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine();

                // P/Invoke for method
                var pinvokeParams = new List<string> { $"{csUnderlyingType} tag" };
                foreach (var param in paramDecls)
                {
                    bool isEnumParam = IsSimpleEnumParam(param.SwiftTypeSpec, enumDecl);
                    var pinvokeType = isEnumParam ? csUnderlyingType : GetSimpleParamType(param.SwiftTypeSpec, typeDatabase)!;
                    var marshalPrefix = MarshallingHelpers.IsBoolType(pinvokeType) ? MarshallingHelpers.BoolPInvokeParamAttribute + " " : "";
                    pinvokeParams.Add($"{marshalPrefix}{pinvokeType} {NameProvider.GetCSharpParameterName(param)}");
                }
                csWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
                csWriter.WriteLine($"[global::System.Runtime.InteropServices.LibraryImport(\"{_wrapperLibName}\", EntryPoint = \"{wrapperSymbol}\")]");
                csWriter.WriteLine($"private static partial IntPtr PInvoke_{methodPascalName}({PInvokeEmitHelper.DeduplicateCSharpParamNames(string.Join(", ", pinvokeParams))});");
                csWriter.WriteLine();

                EmitFreePInvokeIfNeeded(csWriter, moduleName);
            }
            else
            {
                // Emit extension method
                csWriter.WriteLine($"public static {csReturnType} {methodPascalName}({string.Join(", ", csParams)})");
                csWriter.WriteLine("{");
                csWriter.Indent++;

                // Build P/Invoke call arguments: cast enum to underlying type
                var callArgs = new List<string> { $"({csUnderlyingType})self" };
                foreach (var param in paramDecls)
                {
                    bool isEnumParam = IsSimpleEnumParam(param.SwiftTypeSpec, enumDecl);
                    var argName = NameProvider.GetCSharpParameterName(param);
                    callArgs.Add(isEnumParam ? $"({csUnderlyingType}){argName}" : argName);
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
                    bool isEnumParam = IsSimpleEnumParam(param.SwiftTypeSpec, enumDecl);
                    var pinvokeType = isEnumParam ? csUnderlyingType : GetSimpleParamType(param.SwiftTypeSpec, typeDatabase)!;
                    var marshalPrefix = MarshallingHelpers.IsBoolType(pinvokeType) ? MarshallingHelpers.BoolPInvokeParamAttribute + " " : "";
                    pinvokeParams.Add($"{marshalPrefix}{pinvokeType} {NameProvider.GetCSharpParameterName(param)}");
                }

                var pinvokeReturnType = returnsVoid ? "void" : (returnsEnum ? csUnderlyingType : csReturnType);
                csWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
                csWriter.WriteLine($"[global::System.Runtime.InteropServices.LibraryImport(\"{_wrapperLibName}\", EntryPoint = \"{wrapperSymbol}\")]");
                if (MarshallingHelpers.IsBoolType(pinvokeReturnType))
                    csWriter.WriteLine(MarshallingHelpers.BoolPInvokeReturnAttribute);
                csWriter.WriteLine($"private static partial {pinvokeReturnType} PInvoke_{methodPascalName}({PInvokeEmitHelper.DeduplicateCSharpParamNames(string.Join(", ", pinvokeParams))});");
                csWriter.WriteLine();
            }

            ReportCollector.RecordMemberEmitted(methodDecl);
        }

        /// <summary>
        /// Emits the Swift wrapper function for a simple enum instance method.
        /// The wrapper takes a scalar tag, converts to the enum case, calls the method,
        /// and converts any enum return back to scalar.
        /// </summary>
        private void EmitSimpleEnumSwiftWrapper(SwiftWriter swiftWriter, EnumDecl enumDecl,
            MethodDecl methodDecl, string wrapperSymbol, string swiftScalarType,
            string moduleName, bool returnsEnum, bool returnsString = false)
        {
            var enumQualifiedName = enumDecl.SwiftTypeName.ModuleQualifiedName;
            var returnTypeStr = returnsString ? "UnsafeMutableRawPointer" : (returnsEnum ? swiftScalarType : GetSwiftReturnType(methodDecl));

            // Build parameter list: tag + method params
            // Enum-typed params are declared as scalar (matching C# P/Invoke) and converted before the call.
            var swiftParams = new List<string> { $"_ tag: {swiftScalarType}" };
            var paramDecls = methodDecl.CSSignature
                .Skip(1)
                .Where(a => a.Name != "self")
                .ToList();
            // Sibling bindings so a reserved-name escape (e.g. `tag`) also dodges a sibling user
            // binding. The call-value loop reuses the same set, keeping decl and call in sync.
            var siblings = CdeclParamMapper.CollectSiblingBindingNames(paramDecls);
            foreach (var param in paramDecls)
            {
                bool isEnumParam = IsSimpleEnumParam(param.SwiftTypeSpec, enumDecl);
                var swiftType = isEnumParam ? swiftScalarType : GetSwiftParamType(param.SwiftTypeSpec, moduleName);
                if (swiftType != null)
                {
                    var label = NameProvider.IsGeneratedArgName(param.Name) ? "_" : param.Name;
                    // Escape only the internal binding token (not the external label) when it
                    // collides with a synthetic injected into this wrapper (e.g. `tag`) OR a sibling
                    // user binding. The call value below escapes the same token identically, keeping
                    // the two in sync.
                    var rawLabel = !string.IsNullOrEmpty(param.PrivateName) ? param.PrivateName : param.Name;
                    var binding = NameProvider.EscapeReservedSwiftWrapperLabel(
                        rawLabel, CdeclParamMapper.ExcludeSelf(siblings, rawLabel));
                    swiftParams.Add($"{label} {binding}: {swiftType}");
                }
            }

            // Emit availability annotations from the enum, member, and ancestor chain.
            // @_cdecl wrappers are top-level functions and don't inherit enclosing type availability.
            var availability = WrapperEmitterHelpers.MergeAvailability(methodDecl.AvailabilityAnnotations, enumDecl);
            WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);

            swiftWriter.WriteLine($"@_cdecl(\"{wrapperSymbol}\")");
            swiftWriter.WriteLine($"public func _sbw_{GetSwiftWrapperIdentifierPath(enumDecl)}_{methodDecl.Name}({string.Join(", ", swiftParams)}) -> {returnTypeStr} {{");
            swiftWriter.Indent++;

            // Convert the incoming scalar to the enum case via a switch keyed on
            // GetCaseMarshalScalar — the SAME value the C# enum member carries, so the cast
            // C# performs (`(underlying)self`) and this switch agree by construction. For
            // integral RawRepresentable enums that scalar is the real Swift raw value (e.g.
            // 17009); otherwise it is the declaration-order tag.
            EmitTagToEnumSwitch(swiftWriter, enumDecl, enumQualifiedName, swiftScalarType);

            // Build method call with arguments, converting enum-typed scalar params back to enum
            var callArgs = new List<string>();
            foreach (var param in paramDecls)
            {
                bool isEnumParam = IsSimpleEnumParam(param.SwiftTypeSpec, enumDecl);
                var label = NameProvider.IsGeneratedArgName(param.Name) ? "" : $"{NameProvider.StripCSharpKeywordPrefix(param.Name)}: ";
                // Escape identically to the param-decl loop (same sibling set, self-excluded).
                var rawLabel = !string.IsNullOrEmpty(param.PrivateName) ? param.PrivateName : param.Name;
                var argExpr = NameProvider.EscapeReservedSwiftWrapperLabel(
                    rawLabel, CdeclParamMapper.ExcludeSelf(siblings, rawLabel));
                if (isEnumParam)
                    argExpr = EmitEnumParamConversion(enumDecl, enumQualifiedName, swiftScalarType, argExpr);
                callArgs.Add($"{label}{argExpr}");
            }

            var callStr = callArgs.Count > 0
                ? $"value.{NameProvider.ParserNameToSwift(methodDecl)}({string.Join(", ", callArgs)})"
                : $"value.{NameProvider.ParserNameToSwift(methodDecl)}()";

            if (returnsString)
            {
                EmitStringReturnSwiftBody(swiftWriter, callStr);
            }
            else if (returnsEnum)
            {
                // Convert the returned enum back to the scalar the C# side will cast to the
                // enum — GetCaseMarshalScalar, matching the emitted C# member value (real raw
                // value for integral RawRepresentable enums, declaration-order tag otherwise).
                swiftWriter.WriteLine($"let result = {callStr}");
                EmitEnumToTagSwitch(swiftWriter, enumDecl, enumQualifiedName, "result", swiftScalarType);
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
                var tag = enumDecl.GetCaseMarshalScalar(caseDecl);
                // Skip @_spi-protected cases — inaccessible without @_spi import
                if (caseDecl.IsSpiProtected)
                {
                    swiftWriter.WriteLine($"case {tag}: fatalError(\"[SwiftBindings] Case at index \\({tag}) is @_spi protected\")");
                    continue;
                }
                var caseName = NameProvider.EscapeSwiftKeyword(caseDecl.Name);
                var caseAvailability = GetTighterCaseAvailability(caseDecl, enumDecl);
                if (caseAvailability.Count > 0)
                {
                    var clause = BuildIfAvailableClause(caseAvailability);
                    swiftWriter.WriteLine($"case {tag}:");
                    swiftWriter.Indent++;
                    swiftWriter.WriteLine($"if #available({clause}) {{");
                    swiftWriter.Indent++;
                    swiftWriter.WriteLine($"value = .{caseName}");
                    swiftWriter.Indent--;
                    swiftWriter.WriteLine("} else {");
                    swiftWriter.Indent++;
                    swiftWriter.WriteLine($"fatalError(\"[SwiftBindings] Case {caseDecl.Name} requires a newer OS version\")");
                    swiftWriter.Indent--;
                    swiftWriter.WriteLine("}");
                    swiftWriter.Indent--;
                    continue;
                }
                swiftWriter.WriteLine($"case {tag}: value = .{caseName}");
            }
            swiftWriter.WriteLine($"default: fatalError(\"[SwiftBindings] Invalid enum tag\")");
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
                // Skip @_spi-protected cases — inaccessible without @_spi import
                if (caseDecl.IsSpiProtected)
                    continue;
                var tag = enumDecl.GetCaseMarshalScalar(caseDecl);
                swiftWriter.WriteLine($"case .{NameProvider.EscapeSwiftKeyword(caseDecl.Name)}: return {tag}");
            }
            // Cases introduced in newer OS versions add a @unknown default implicitly
            // (the switch is no longer exhaustive on the older deployment target).
            if (enumDecl.Cases.Any(c => GetTighterCaseAvailability(c, enumDecl).Count > 0))
            {
                swiftWriter.WriteLine("@unknown default: fatalError(\"[SwiftBindings] Unknown enum case\")");
            }
            swiftWriter.WriteLine("}");
        }

        /// <summary>
        /// Returns the case's availability annotations only when they introduce a tighter
        /// platform intro than the parent enum — i.e., cases that require a newer OS version
        /// than the enum itself. Returns empty when the case inherits the enum's intros.
        /// Used by <see cref="EmitTagToEnumSwitch"/> to emit <c>#available</c> guards.
        /// </summary>
        private static List<AvailabilityAnnotation> GetTighterCaseAvailability(EnumCaseDecl caseDecl, EnumDecl enumDecl)
        {
            var result = new List<AvailabilityAnnotation>();
            if (caseDecl.AvailabilityAnnotations is not { Count: > 0 } caseAvail)
                return result;

            var enumAvail = enumDecl.AvailabilityAnnotations;
            foreach (var ca in caseAvail)
            {
                if (ca.Platform == null || ca.IntroducedVersion == null)
                    continue;
                // Skip if the enum itself introduces the same-or-newer version on this platform.
                var parentVersion = enumAvail?.FirstOrDefault(pa =>
                    string.Equals(pa.Platform, ca.Platform, StringComparison.OrdinalIgnoreCase))?.IntroducedVersion;
                if (parentVersion != null && CompareSwiftVersions(parentVersion, ca.IntroducedVersion) >= 0)
                    continue;
                result.Add(ca);
            }
            return result;
        }

        private static string BuildIfAvailableClause(List<AvailabilityAnnotation> annotations)
        {
            var parts = annotations
                .Where(a => a.Platform != null && a.IntroducedVersion != null)
                .Select(a => $"{a.Platform} {a.IntroducedVersion}")
                .ToList();
            parts.Add("*");
            return string.Join(", ", parts);
        }

        private static int CompareSwiftVersions(string a, string b)
        {
            var aParts = a.Split('.').Select(p => int.TryParse(p, out var v) ? v : 0).ToArray();
            var bParts = b.Split('.').Select(p => int.TryParse(p, out var v) ? v : 0).ToArray();
            int n = Math.Max(aParts.Length, bParts.Length);
            for (int i = 0; i < n; i++)
            {
                int ai = i < aParts.Length ? aParts[i] : 0;
                int bi = i < bParts.Length ? bParts[i] : 0;
                if (ai != bi) return ai.CompareTo(bi);
            }
            return 0;
        }

        /// <summary>
        /// Emits an instance property as a static extension getter method.
        /// Setters are not supported — C# extension methods receive a copy of the value type,
        /// so mutations cannot propagate back to the caller.
        /// </summary>
        private void EmitSimpleEnumExtensionProperty(CSharpWriter csWriter, SwiftWriter swiftWriter,
            EnumDecl enumDecl, string enumName, PropertyDecl propertyDecl, ModuleDecl moduleDecl,
            ITypeDatabase typeDatabase, string csUnderlyingType, string swiftScalarType)
        {
            var moduleName = moduleDecl.Name;
            var propertyPascalName = NameProvider.ToPascalCase(propertyDecl.Name);

            // Record setter as skipped if present
            if (propertyDecl.Accessors.Any(a => a is SetAccessorDecl))
            {
                ReportCollector.RecordMemberSkipped(propertyDecl,
                    SkipReason.UnsupportedType, "Setters on value-type enums cannot propagate mutations via extension methods.",
                    accessor: AccessorKind.Setter);
            }

            // Get the getter accessor
            var getter = propertyDecl.Accessors.OfType<GetAccessorDecl>().FirstOrDefault();
            if (getter == null)
            {
                ReportCollector.RecordMemberSkipped(propertyDecl,
                    SkipReason.UnsupportedType, "Property has no getter accessor.");
                return;
            }

            // Determine return type
            var returnTypeSpec = propertyDecl.SwiftTypeSpec;
            bool returnsEnum = IsSimpleEnumReturn(returnTypeSpec, enumDecl, typeDatabase);
            bool returnsString = IsStringReturn(returnTypeSpec);
            bool returnsOptionalString = IsOptionalStringReturn(returnTypeSpec);

            string csReturnType;
            if (returnsEnum)
                csReturnType = enumName;
            else if (returnsString)
                csReturnType = "string";
            else if (returnsOptionalString)
                csReturnType = "string?";
            else
            {
                csReturnType = GetSimpleReturnType(returnTypeSpec!, typeDatabase) ?? null!;
                if (csReturnType == null)
                {
                    ReportCollector.RecordMemberSkipped(propertyDecl,
                        SkipReason.UnsupportedSignature, "Property return type is unsupported for simple enum extension.");
                    return;
                }
            }

            // Compute wrapper symbol
            var getterMangledName = getter.Method.MangledName;
            var wrapperSymbol = $"SBW_{moduleName}_{enumName}_get_{propertyDecl.Name}_{DeterministicHash8(getterMangledName)}";
            // simple-enum instance property getter. The `_get_`
            // infix and per-enum scoping make this symbol structurally distinct from
            // method wrappers and from non-simple-enum property wrappers. Per-kind
            // property bucket is collision-safe. This path emits a getter only (the `_get_`
            // infix), so the id carries the getter accessor axis.
            _simpleEnumEmissionContext?.TryAddPropertyWrapperSymbol(wrapperSymbol, DeclIdFactory.ForProperty(propertyDecl, AccessorKind.Getter));

            // Emit Swift wrapper
            EmitSimpleEnumPropertySwiftWrapper(swiftWriter, enumDecl, propertyDecl, wrapperSymbol,
                swiftScalarType, moduleName, returnsEnum, returnsString, returnsOptionalString);

            // Emit C# extension getter method
            if (returnsString || returnsOptionalString)
            {
                // Utf8Slice struct is shared at module level
                EmitStringReturnExtensionMethod(csWriter, enumName, propertyPascalName,
                    wrapperSymbol, csUnderlyingType, moduleName, isStatic: false, isNullable: returnsOptionalString);
            }
            else
            {
                csWriter.WriteLine($"public static {csReturnType} Get{propertyPascalName}(this {enumName} self)");
                csWriter.WriteLine("{");
                csWriter.Indent++;

                if (returnsEnum)
                    csWriter.WriteLine($"return ({enumName})PInvoke_Get{propertyPascalName}(({csUnderlyingType})self);");
                else
                    csWriter.WriteLine($"return PInvoke_Get{propertyPascalName}(({csUnderlyingType})self);");

                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine();

                // P/Invoke
                var pinvokeReturnType = returnsEnum ? csUnderlyingType : csReturnType;
                csWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
                csWriter.WriteLine($"[global::System.Runtime.InteropServices.LibraryImport(\"{_wrapperLibName}\", EntryPoint = \"{wrapperSymbol}\")]");
                if (MarshallingHelpers.IsBoolType(pinvokeReturnType))
                    csWriter.WriteLine(MarshallingHelpers.BoolPInvokeReturnAttribute);
                csWriter.WriteLine($"private static partial {pinvokeReturnType} PInvoke_Get{propertyPascalName}({csUnderlyingType} tag);");
                csWriter.WriteLine();
            }

            ReportCollector.RecordMemberEmitted(propertyDecl);
        }

        /// <summary>
        /// Emits a static method in the extensions class.
        /// </summary>
        private void EmitSimpleEnumStaticMethod(CSharpWriter csWriter, SwiftWriter swiftWriter,
            EnumDecl enumDecl, string enumName, MethodDecl methodDecl, ModuleDecl moduleDecl,
            ITypeDatabase typeDatabase, string csUnderlyingType, string swiftScalarType)
        {
            var moduleName = moduleDecl.Name;
            var methodPascalName = NameProvider.ToPascalCase(methodDecl.Name);

            // Determine return type
            var returnTypeSpec = methodDecl.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
            bool returnsEnum = IsSimpleEnumReturn(returnTypeSpec, enumDecl, typeDatabase);
            bool returnsVoid = returnTypeSpec == null || IsVoidReturn(returnTypeSpec);
            bool returnsString = !returnsVoid && IsStringReturn(returnTypeSpec);

            string csReturnType;
            if (returnsVoid)
                csReturnType = "void";
            else if (returnsEnum)
                csReturnType = enumName;
            else if (returnsString)
                csReturnType = "string";
            else
            {
                csReturnType = GetSimpleReturnType(returnTypeSpec!, typeDatabase) ?? null!;
                if (csReturnType == null)
                {
                    ReportCollector.RecordMemberSkipped(methodDecl,
                        SkipReason.UnsupportedSignature, "Return type is unsupported for simple enum static method.");
                    return;
                }
            }

            // Validate parameters (skip index 0 = return type, skip self)
            var paramDecls = methodDecl.CSSignature
                .Skip(1)
                .Where(a => a.Name != "self")
                .ToList();

            var csParams = new List<string>();
            foreach (var param in paramDecls)
            {
                bool isEnumParam = IsSimpleEnumParam(param.SwiftTypeSpec, enumDecl);
                var paramType = isEnumParam ? enumName : GetSimpleParamType(param.SwiftTypeSpec, typeDatabase);
                if (paramType == null)
                {
                    ReportCollector.RecordMemberSkipped(methodDecl,
                        SkipReason.UnsupportedSignature, $"Parameter '{param.Name}' has unsupported type for simple enum static method.");
                    return;
                }
                csParams.Add($"{paramType} {NameProvider.GetCSharpParameterName(param)}");
            }

            // Emit Swift wrapper
            var wrapperSymbol = $"SBW_{moduleName}_{enumName}_{methodDecl.Name}_{DeterministicHash8(methodDecl.MangledName)}";
            // simple-enum static method — same per-enum scoping as
            // the instance method path; static-vs-instance distinction is encoded in the
            // mangled-name hash. Per-kind method bucket is collision-safe.
            _simpleEnumEmissionContext?.TryAddMethodWrapperSymbol(wrapperSymbol, DeclIdFactory.ForMethod(methodDecl));
            EmitSimpleEnumStaticMethodSwiftWrapper(swiftWriter, enumDecl, methodDecl, wrapperSymbol,
                swiftScalarType, moduleName, returnsEnum, returnsString);

            if (returnsString)
            {
                // String return: use Utf8Slice pattern
                // Utf8Slice struct is shared at module level
                EmitStringReturnStaticMethod(csWriter, enumName, methodPascalName,
                    wrapperSymbol, csUnderlyingType, moduleName, paramDecls, enumDecl, typeDatabase);
            }
            else
            {
                // Emit C# static method (NOT extension method — no `this`)
                csWriter.WriteLine($"public static {csReturnType} {methodPascalName}({string.Join(", ", csParams)})");
                csWriter.WriteLine("{");
                csWriter.Indent++;

                // Build P/Invoke call arguments
                var callArgs = new List<string>();
                foreach (var param in paramDecls)
                {
                    bool isEnumParam = IsSimpleEnumParam(param.SwiftTypeSpec, enumDecl);
                    var argName = NameProvider.GetCSharpParameterName(param);
                    callArgs.Add(isEnumParam ? $"({csUnderlyingType}){argName}" : argName);
                }

                if (returnsVoid)
                    csWriter.WriteLine($"PInvoke_{methodPascalName}({string.Join(", ", callArgs)});");
                else if (returnsEnum)
                    csWriter.WriteLine($"return ({enumName})PInvoke_{methodPascalName}({string.Join(", ", callArgs)});");
                else
                    csWriter.WriteLine($"return PInvoke_{methodPascalName}({string.Join(", ", callArgs)});");

                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine();

                // Emit P/Invoke
                var pinvokeParams = new List<string>();
                foreach (var param in paramDecls)
                {
                    bool isEnumParam = IsSimpleEnumParam(param.SwiftTypeSpec, enumDecl);
                    var pinvokeType = isEnumParam ? csUnderlyingType : GetSimpleParamType(param.SwiftTypeSpec, typeDatabase)!;
                    var marshalPrefix = MarshallingHelpers.IsBoolType(pinvokeType) ? MarshallingHelpers.BoolPInvokeParamAttribute + " " : "";
                    pinvokeParams.Add($"{marshalPrefix}{pinvokeType} {NameProvider.GetCSharpParameterName(param)}");
                }

                var pinvokeReturnType = returnsVoid ? "void" : (returnsEnum ? csUnderlyingType : csReturnType);
                csWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
                csWriter.WriteLine($"[global::System.Runtime.InteropServices.LibraryImport(\"{_wrapperLibName}\", EntryPoint = \"{wrapperSymbol}\")]");
                if (MarshallingHelpers.IsBoolType(pinvokeReturnType))
                    csWriter.WriteLine(MarshallingHelpers.BoolPInvokeReturnAttribute);
                csWriter.WriteLine($"private static partial {pinvokeReturnType} PInvoke_{methodPascalName}({PInvokeEmitHelper.DeduplicateCSharpParamNames(string.Join(", ", pinvokeParams))});");
                csWriter.WriteLine();
            }

            ReportCollector.RecordMemberEmitted(methodDecl);
        }

        /// <summary>
        /// Emits a static property in the extensions class.
        /// </summary>
        private void EmitSimpleEnumStaticProperty(CSharpWriter csWriter, SwiftWriter swiftWriter,
            EnumDecl enumDecl, string enumName, PropertyDecl propertyDecl, ModuleDecl moduleDecl,
            ITypeDatabase typeDatabase, string csUnderlyingType, string swiftScalarType)
        {
            var moduleName = moduleDecl.Name;
            var propertyPascalName = NameProvider.ToPascalCase(propertyDecl.Name);

            // Record setter as skipped if present
            if (propertyDecl.Accessors.Any(a => a is SetAccessorDecl))
            {
                ReportCollector.RecordMemberSkipped(propertyDecl,
                    SkipReason.UnsupportedType, "Setters on value-type enums cannot propagate mutations via extension methods.",
                    accessor: AccessorKind.Setter);
            }

            var getter = propertyDecl.Accessors.OfType<GetAccessorDecl>().FirstOrDefault();
            if (getter == null)
            {
                ReportCollector.RecordMemberSkipped(propertyDecl,
                    SkipReason.UnsupportedType, "Property has no getter accessor.");
                return;
            }

            var returnTypeSpec = propertyDecl.SwiftTypeSpec;
            bool returnsEnum = IsSimpleEnumReturn(returnTypeSpec, enumDecl, typeDatabase);
            bool returnsString = IsStringReturn(returnTypeSpec);
            bool returnsOptionalString = IsOptionalStringReturn(returnTypeSpec);

            string csReturnType;
            if (returnsEnum)
                csReturnType = enumName;
            else if (returnsString)
                csReturnType = "string";
            else if (returnsOptionalString)
                csReturnType = "string?";
            else
            {
                csReturnType = GetSimpleReturnType(returnTypeSpec!, typeDatabase) ?? null!;
                if (csReturnType == null)
                {
                    ReportCollector.RecordMemberSkipped(propertyDecl,
                        SkipReason.UnsupportedSignature, "Static property return type is unsupported for simple enum extension.");
                    return;
                }
            }

            var getterMangledName = getter.Method.MangledName;
            var wrapperSymbol = $"SBW_{moduleName}_{enumName}_get_{propertyDecl.Name}_{DeterministicHash8(getterMangledName)}";
            // simple-enum static property getter — same per-enum
            // scoping as the instance variant; static-vs-instance distinction is encoded
            // in the getter's mangled-name hash. Per-kind property bucket is collision-safe.
            // Getter-only path, so the id carries the getter accessor axis; the static-vs-instance
            // split is already on the id's discriminator.
            _simpleEnumEmissionContext?.TryAddPropertyWrapperSymbol(wrapperSymbol, DeclIdFactory.ForProperty(propertyDecl, AccessorKind.Getter));

            // Emit Swift wrapper (no tag param for static)
            EmitSimpleEnumStaticPropertySwiftWrapper(swiftWriter, enumDecl, propertyDecl, wrapperSymbol,
                swiftScalarType, moduleName, returnsEnum, returnsString, returnsOptionalString);

            if (returnsString || returnsOptionalString)
            {
                // Utf8Slice struct is shared at module level
                EmitStringReturnStaticPropertyAccessor(csWriter, enumName, propertyPascalName,
                    wrapperSymbol, moduleName, isNullable: returnsOptionalString);
            }
            else
            {
                // Emit C# static property
                var pinvokeReturnType = returnsEnum ? csUnderlyingType : csReturnType;
                var valueExpr = returnsEnum
                    ? $"({enumName})PInvoke_Get{propertyPascalName}()"
                    : $"PInvoke_Get{propertyPascalName}()";

                csWriter.WriteLine($"public static {csReturnType} {propertyPascalName} => {valueExpr};");
                csWriter.WriteLine();

                // P/Invoke
                csWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
                csWriter.WriteLine($"[global::System.Runtime.InteropServices.LibraryImport(\"{_wrapperLibName}\", EntryPoint = \"{wrapperSymbol}\")]");
                if (MarshallingHelpers.IsBoolType(pinvokeReturnType))
                    csWriter.WriteLine(MarshallingHelpers.BoolPInvokeReturnAttribute);
                csWriter.WriteLine($"private static partial {pinvokeReturnType} PInvoke_Get{propertyPascalName}();");
                csWriter.WriteLine();
            }

            ReportCollector.RecordMemberEmitted(propertyDecl);
        }

        /// <summary>
        /// Emits CaseIterable AllCases property as a pure C# implementation.
        /// </summary>
        private static void EmitCaseIterableAllCases(CSharpWriter csWriter, EnumDecl enumDecl, string enumName)
        {
            csWriter.WriteLine("/// <summary>Returns all cases of the enum.</summary>");
            csWriter.WriteLine($"public static global::System.Collections.Generic.IReadOnlyList<{enumName}> AllCases {{ get; }} =");
            csWriter.Indent++;
            csWriter.WriteLine($"global::System.Array.AsReadOnly(Enum.GetValues<{enumName}>());");
            csWriter.Indent--;
            csWriter.WriteLine();
        }

        // === Helper Methods for Simple Enum Emission ===

        /// <summary>
        /// Builds a Swift-identifier-safe path that disambiguates nested enums sharing the
        /// same leaf name across sibling parent types. For a top-level enum the path is just
        /// the enum name, but a nested enum like <c>RoomPlan.RoomBuilder.BuildError</c> becomes
        /// <c>RoomBuilder_BuildError</c> so its wrapper functions do not collide with a
        /// sibling <c>RoomPlan.StructureBuilder.BuildError</c>'s wrappers.
        /// </summary>
        private static string GetSwiftWrapperIdentifierPath(EnumDecl enumDecl)
        {
            var qualified = enumDecl.SwiftTypeName.ModuleQualifiedName;
            var dot = qualified.IndexOf('.');
            if (dot < 0)
                return qualified;
            return qualified.Substring(dot + 1).Replace('.', '_');
        }

        /// <summary>
        /// Checks whether a TypeSpec represents a Swift.String return.
        /// </summary>
        private static bool IsStringReturn(TypeSpec? typeSpec)
        {
            return typeSpec is NamedTypeSpec named && named.Name == "Swift.String";
        }

        /// <summary>
        /// Checks whether a TypeSpec represents a Swift.Optional&lt;Swift.String&gt; return.
        /// Used for LocalizedError conformance (errorDescription, failureReason, etc.).
        /// </summary>
        private static bool IsOptionalStringReturn(TypeSpec? typeSpec)
        {
            return typeSpec is NamedTypeSpec named
                && named.Name == "Swift.Optional"
                && named.GenericParameters.Count == 1
                && IsStringReturn(named.GenericParameters[0]);
        }

        /// <summary>
        /// True if the type is either Swift.String or Swift.Optional&lt;Swift.String&gt;.
        /// Both are emitted via the Utf8Slice path; nullable returns check for IntPtr.Zero.
        /// </summary>
        private static bool IsStringOrOptionalStringReturn(TypeSpec? typeSpec) =>
            IsStringReturn(typeSpec) || IsOptionalStringReturn(typeSpec);

        /// <summary>
        /// Checks whether a TypeSpec represents a parameter of the same enum type.
        /// </summary>
        private static bool IsSimpleEnumParam(TypeSpec typeSpec, EnumDecl enumDecl)
        {
            return typeSpec is NamedTypeSpec named && named.NameWithoutModule == enumDecl.Name;
        }

        /// <summary>
        /// Emits the Swift wrapper for an instance property getter.
        /// </summary>
        private void EmitSimpleEnumPropertySwiftWrapper(SwiftWriter swiftWriter, EnumDecl enumDecl,
            PropertyDecl propertyDecl, string wrapperSymbol, string swiftScalarType,
            string moduleName, bool returnsEnum, bool returnsString, bool returnsOptionalString = false)
        {
            var enumQualifiedName = enumDecl.SwiftTypeName.ModuleQualifiedName;
            string swiftReturnType;
            if (returnsOptionalString) swiftReturnType = "UnsafeMutableRawPointer?";
            else if (returnsString) swiftReturnType = "UnsafeMutableRawPointer";
            else if (returnsEnum) swiftReturnType = swiftScalarType;
            else swiftReturnType = GetSwiftPropertyReturnType(propertyDecl);

            // Emit availability annotations from the enum, member, and ancestor chain.
            // @_cdecl wrappers are top-level functions and don't inherit enclosing type availability.
            var availability = WrapperEmitterHelpers.MergeAvailability(propertyDecl.AvailabilityAnnotations, enumDecl);
            WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);

            swiftWriter.WriteLine($"@_cdecl(\"{wrapperSymbol}\")");
            swiftWriter.WriteLine($"public func _sbw_{GetSwiftWrapperIdentifierPath(enumDecl)}_get_{propertyDecl.Name}(_ tag: {swiftScalarType}) -> {swiftReturnType} {{");
            swiftWriter.Indent++;

            // Convert tag to enum value. Always use switch-based reconstruction
            // because C# enum values are sequential tags (ABI JSON lacks raw values).
            EmitTagToEnumSwitch(swiftWriter, enumDecl, enumQualifiedName, swiftScalarType);

            if (returnsOptionalString)
            {
                EmitOptionalStringReturnSwiftBody(swiftWriter, $"value.{NameProvider.EscapeSwiftKeyword(propertyDecl.Name)}");
            }
            else if (returnsString)
            {
                EmitStringReturnSwiftBody(swiftWriter, $"value.{NameProvider.EscapeSwiftKeyword(propertyDecl.Name)}");
            }
            else if (returnsEnum)
            {
                swiftWriter.WriteLine($"let result = value.{NameProvider.EscapeSwiftKeyword(propertyDecl.Name)}");
                EmitEnumToTagSwitch(swiftWriter, enumDecl, enumQualifiedName, "result", swiftScalarType);
            }
            else
            {
                swiftWriter.WriteLine($"return value.{NameProvider.EscapeSwiftKeyword(propertyDecl.Name)}");
            }

            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
            swiftWriter.WriteLine();
        }

        /// <summary>
        /// Emits the Swift wrapper for a static property getter.
        /// </summary>
        private void EmitSimpleEnumStaticPropertySwiftWrapper(SwiftWriter swiftWriter, EnumDecl enumDecl,
            PropertyDecl propertyDecl, string wrapperSymbol, string swiftScalarType,
            string moduleName, bool returnsEnum, bool returnsString, bool returnsOptionalString = false)
        {
            var enumQualifiedName = enumDecl.SwiftTypeName.ModuleQualifiedName;
            string swiftReturnType;
            if (returnsOptionalString) swiftReturnType = "UnsafeMutableRawPointer?";
            else if (returnsString) swiftReturnType = "UnsafeMutableRawPointer";
            else if (returnsEnum) swiftReturnType = swiftScalarType;
            else swiftReturnType = GetSwiftPropertyReturnType(propertyDecl);

            // Emit availability annotations from the enum, member, and ancestor chain.
            // @_cdecl wrappers are top-level functions and don't inherit enclosing type availability.
            var availability = WrapperEmitterHelpers.MergeAvailability(propertyDecl.AvailabilityAnnotations, enumDecl);
            WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);

            swiftWriter.WriteLine($"@_cdecl(\"{wrapperSymbol}\")");
            swiftWriter.WriteLine($"public func _sbw_{GetSwiftWrapperIdentifierPath(enumDecl)}_get_{propertyDecl.Name}() -> {swiftReturnType} {{");
            swiftWriter.Indent++;

            if (returnsOptionalString)
            {
                EmitOptionalStringReturnSwiftBody(swiftWriter, $"{enumQualifiedName}.{NameProvider.EscapeSwiftKeyword(propertyDecl.Name)}");
            }
            else if (returnsString)
            {
                EmitStringReturnSwiftBody(swiftWriter, $"{enumQualifiedName}.{NameProvider.EscapeSwiftKeyword(propertyDecl.Name)}");
            }
            else if (returnsEnum)
            {
                swiftWriter.WriteLine($"let result = {enumQualifiedName}.{NameProvider.EscapeSwiftKeyword(propertyDecl.Name)}");
                EmitEnumToTagSwitch(swiftWriter, enumDecl, enumQualifiedName, "result", swiftScalarType);
            }
            else
            {
                swiftWriter.WriteLine($"return {enumQualifiedName}.{NameProvider.EscapeSwiftKeyword(propertyDecl.Name)}");
            }

            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
            swiftWriter.WriteLine();
        }

        /// <summary>
        /// Emits the Swift wrapper for a static method.
        /// </summary>
        private void EmitSimpleEnumStaticMethodSwiftWrapper(SwiftWriter swiftWriter, EnumDecl enumDecl,
            MethodDecl methodDecl, string wrapperSymbol, string swiftScalarType,
            string moduleName, bool returnsEnum, bool returnsString)
        {
            var enumQualifiedName = enumDecl.SwiftTypeName.ModuleQualifiedName;
            var returnTypeStr = returnsString ? "UnsafeMutableRawPointer" : (returnsEnum ? swiftScalarType : GetSwiftReturnType(methodDecl));

            // Build parameter list (no tag/self for static methods)
            var swiftParams = new List<string>();
            var paramDecls = methodDecl.CSSignature
                .Skip(1)
                .Where(a => a.Name != "self")
                .ToList();
            // Sibling bindings so a reserved-name escape also dodges a sibling user binding.
            // The call-value loop reuses the same set, keeping decl and call in sync.
            var siblings = CdeclParamMapper.CollectSiblingBindingNames(paramDecls);
            foreach (var param in paramDecls)
            {
                bool isEnumParam = IsSimpleEnumParam(param.SwiftTypeSpec, enumDecl);
                var swiftType = isEnumParam ? swiftScalarType : GetSwiftParamType(param.SwiftTypeSpec, moduleName);
                if (swiftType != null)
                {
                    var label = NameProvider.IsGeneratedArgName(param.Name) ? "_" : param.Name;
                    // Escape only the internal binding token (not the external label) when it
                    // collides with a synthetic injected into this wrapper OR a sibling user binding.
                    // The call value below escapes the same token identically, keeping the two in sync.
                    var rawLabel = !string.IsNullOrEmpty(param.PrivateName) ? param.PrivateName : param.Name;
                    var binding = NameProvider.EscapeReservedSwiftWrapperLabel(
                        rawLabel, CdeclParamMapper.ExcludeSelf(siblings, rawLabel));
                    swiftParams.Add($"{label} {binding}: {swiftType}");
                }
            }

            // Emit availability annotations from the enum, member, and ancestor chain.
            // @_cdecl wrappers are top-level functions and don't inherit enclosing type availability.
            var availability = WrapperEmitterHelpers.MergeAvailability(methodDecl.AvailabilityAnnotations, enumDecl);
            WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);

            swiftWriter.WriteLine($"@_cdecl(\"{wrapperSymbol}\")");
            swiftWriter.WriteLine($"public func _sbw_{GetSwiftWrapperIdentifierPath(enumDecl)}_{methodDecl.Name}({string.Join(", ", swiftParams)}) -> {returnTypeStr} {{");
            swiftWriter.Indent++;

            // Build method call with arguments
            var callArgs = new List<string>();
            foreach (var param in paramDecls)
            {
                bool isEnumParam = IsSimpleEnumParam(param.SwiftTypeSpec, enumDecl);
                var label = NameProvider.IsGeneratedArgName(param.Name) ? "" : $"{NameProvider.StripCSharpKeywordPrefix(param.Name)}: ";
                // Escape identically to the param-decl loop (same sibling set, self-excluded).
                var rawLabel = !string.IsNullOrEmpty(param.PrivateName) ? param.PrivateName : param.Name;
                var argExpr = NameProvider.EscapeReservedSwiftWrapperLabel(
                    rawLabel, CdeclParamMapper.ExcludeSelf(siblings, rawLabel));
                if (isEnumParam)
                    argExpr = EmitEnumParamConversion(enumDecl, enumQualifiedName, swiftScalarType, argExpr);
                callArgs.Add($"{label}{argExpr}");
            }

            var callStr = $"{enumQualifiedName}.{NameProvider.ParserNameToSwift(methodDecl)}({string.Join(", ", callArgs)})";

            if (returnsString)
            {
                EmitStringReturnSwiftBody(swiftWriter, callStr);
            }
            else if (returnsEnum)
            {
                swiftWriter.WriteLine($"let result = {callStr}");
                EmitEnumToTagSwitch(swiftWriter, enumDecl, enumQualifiedName, "result", swiftScalarType);
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
        /// Emits the Swift body for a string return: allocates SBW_Utf8Slice on heap, returns pointer.
        /// </summary>
        private static void EmitStringReturnSwiftBody(SwiftWriter swiftWriter, string expression)
        {
            swiftWriter.WriteLine($"let result: String = {expression}");
            swiftWriter.WriteLine("let utf8 = Array(result.utf8)");
            swiftWriter.WriteLine("let bufferPtr = UnsafeMutablePointer<UInt8>.allocate(capacity: max(utf8.count, 1))");
            swiftWriter.WriteLine("utf8.withUnsafeBufferPointer { src in");
            swiftWriter.Indent++;
            swiftWriter.WriteLine("if utf8.count > 0 { bufferPtr.initialize(from: src.baseAddress!, count: src.count) }");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
            swiftWriter.WriteLine("let slicePtr = UnsafeMutablePointer<SBW_Utf8Slice>.allocate(capacity: 1)");
            swiftWriter.WriteLine("slicePtr.initialize(to: SBW_Utf8Slice(ptr: bufferPtr, len: utf8.count))");
            swiftWriter.WriteLine("return UnsafeMutableRawPointer(slicePtr)");
        }

        /// <summary>
        /// Emits the Swift body for an Optional&lt;String&gt; return: nil → return nil; some → allocate Utf8Slice.
        /// Caller receives `UnsafeMutableRawPointer?`; C# side checks IntPtr.Zero for the None case.
        /// </summary>
        private static void EmitOptionalStringReturnSwiftBody(SwiftWriter swiftWriter, string expression)
        {
            swiftWriter.WriteLine($"guard let result: String = {expression} else {{ return nil }}");
            swiftWriter.WriteLine("let utf8 = Array(result.utf8)");
            swiftWriter.WriteLine("let bufferPtr = UnsafeMutablePointer<UInt8>.allocate(capacity: max(utf8.count, 1))");
            swiftWriter.WriteLine("utf8.withUnsafeBufferPointer { src in");
            swiftWriter.Indent++;
            swiftWriter.WriteLine("if utf8.count > 0 { bufferPtr.initialize(from: src.baseAddress!, count: src.count) }");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
            swiftWriter.WriteLine("let slicePtr = UnsafeMutablePointer<SBW_Utf8Slice>.allocate(capacity: 1)");
            swiftWriter.WriteLine("slicePtr.initialize(to: SBW_Utf8Slice(ptr: bufferPtr, len: utf8.count))");
            swiftWriter.WriteLine("return UnsafeMutableRawPointer(slicePtr)");
        }

        // Utf8Slice struct is now shared at module level (emitted by ModuleHandler).
        private string _wrapperLibName = "SwiftBindings";

        // EmissionContext captured at EmitSimpleEnumExtensions entry so the per-method
        // and per-property emit helpers can register their SBW_ wrapper symbols with
        // the wrapper-symbol contract without threading the context through every
        // signature.
        private ModuleEmissionContext? _simpleEnumEmissionContext;

        private bool _freePInvokeEmittedInExtensions;

        /// <summary>
        /// Emits a C# string-returning extension method for an instance property using Utf8Slice marshalling.
        /// When <paramref name="isNullable"/> is true, the return type is `string?` and IntPtr.Zero from
        /// the Swift wrapper maps to null (used for Optional&lt;String&gt; returns like LocalizedError.errorDescription).
        /// </summary>
        private void EmitStringReturnExtensionMethod(CSharpWriter csWriter, string enumName,
            string propertyPascalName, string wrapperSymbol, string csUnderlyingType, string moduleName,
            bool isStatic, bool isNullable = false)
        {
            var freeSymbol = Utf8SliceEmitter.GetFreeSymbolName(moduleName);
            var selfParam = isStatic ? "" : $"this {enumName} self";
            var callArg = isStatic ? "" : $"({csUnderlyingType})self";
            var returnType = isNullable ? "string?" : "string";

            csWriter.WriteLine($"public static unsafe {returnType} Get{propertyPascalName}({selfParam})");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine($"IntPtr resultPtr = PInvoke_Get{propertyPascalName}({callArg});");
            if (isNullable)
                csWriter.WriteLine("if (resultPtr == IntPtr.Zero) return null;");
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("var slice = *(Utf8Slice*)resultPtr;");
            csWriter.WriteLine("return slice.Len > 0");
            csWriter.Indent++;
            csWriter.WriteLine("? global::System.Text.Encoding.UTF8.GetString((byte*)slice.Ptr, (int)slice.Len)");
            csWriter.WriteLine(": string.Empty;");
            csWriter.Indent--;
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine($"finally {{ PInvoke_SBW_Free(resultPtr); }}");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            // P/Invoke for getter
            var pinvokeParams = isStatic ? "" : $"{csUnderlyingType} tag";
            csWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
            csWriter.WriteLine($"[global::System.Runtime.InteropServices.LibraryImport(\"{_wrapperLibName}\", EntryPoint = \"{wrapperSymbol}\")]");
            csWriter.WriteLine($"private static partial IntPtr PInvoke_Get{propertyPascalName}({pinvokeParams});");
            csWriter.WriteLine();

            // P/Invoke for free
            EmitFreePInvokeIfNeeded(csWriter, moduleName);
        }

        /// <summary>
        /// Emits a C# string-returning static method using Utf8Slice marshalling.
        /// </summary>
        private void EmitStringReturnStaticMethod(CSharpWriter csWriter, string enumName,
            string methodPascalName, string wrapperSymbol, string csUnderlyingType, string moduleName,
            List<ArgumentDecl> paramDecls, EnumDecl enumDecl, ITypeDatabase typeDatabase)
        {
            var freeSymbol = Utf8SliceEmitter.GetFreeSymbolName(moduleName);

            // Build C# parameter list
            var csParams = new List<string>();
            foreach (var param in paramDecls)
            {
                bool isEnumParam = IsSimpleEnumParam(param.SwiftTypeSpec, enumDecl);
                var paramType = isEnumParam ? enumName
                    : GetSimpleParamType(param.SwiftTypeSpec, typeDatabase)!;
                csParams.Add($"{paramType} {NameProvider.GetCSharpParameterName(param)}");
            }

            csWriter.WriteLine($"public static unsafe string {methodPascalName}({string.Join(", ", csParams)})");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // Build call args
            var callArgs = new List<string>();
            foreach (var param in paramDecls)
            {
                bool isEnumParam = IsSimpleEnumParam(param.SwiftTypeSpec, enumDecl);
                var argName = NameProvider.GetCSharpParameterName(param);
                callArgs.Add(isEnumParam ? $"({csUnderlyingType}){argName}" : argName);
            }

            csWriter.WriteLine($"IntPtr resultPtr = PInvoke_{methodPascalName}({string.Join(", ", callArgs)});");
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("var slice = *(Utf8Slice*)resultPtr;");
            csWriter.WriteLine("return slice.Len > 0");
            csWriter.Indent++;
            csWriter.WriteLine("? global::System.Text.Encoding.UTF8.GetString((byte*)slice.Ptr, (int)slice.Len)");
            csWriter.WriteLine(": string.Empty;");
            csWriter.Indent--;
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine($"finally {{ PInvoke_SBW_Free(resultPtr); }}");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            // P/Invoke for method
            var pinvokeParams = new List<string>();
            foreach (var param in paramDecls)
            {
                bool isEnumParam = IsSimpleEnumParam(param.SwiftTypeSpec, enumDecl);
                var pinvokeType = isEnumParam ? csUnderlyingType : GetSimpleParamType(param.SwiftTypeSpec, typeDatabase)!;
                var marshalPrefix = MarshallingHelpers.IsBoolType(pinvokeType) ? MarshallingHelpers.BoolPInvokeParamAttribute + " " : "";
                pinvokeParams.Add($"{marshalPrefix}{pinvokeType} {NameProvider.GetCSharpParameterName(param)}");
            }
            var hasStringParam = pinvokeParams.Any(p => p.Contains("string ") || p.Contains("string?"));
            var stringMarshal = hasStringParam ? ", StringMarshalling = global::System.Runtime.InteropServices.StringMarshalling.Utf8" : "";
            csWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
            csWriter.WriteLine($"[global::System.Runtime.InteropServices.LibraryImport(\"{_wrapperLibName}\", EntryPoint = \"{wrapperSymbol}\"{stringMarshal})]");
            csWriter.WriteLine($"private static partial IntPtr PInvoke_{methodPascalName}({PInvokeEmitHelper.DeduplicateCSharpParamNames(string.Join(", ", pinvokeParams))});");
            csWriter.WriteLine();

            EmitFreePInvokeIfNeeded(csWriter, moduleName);
        }

        /// <summary>
        /// Emits a C# string-returning static property accessor using Utf8Slice marshalling.
        /// When <paramref name="isNullable"/> is true, the return type is `string?` and IntPtr.Zero
        /// from the Swift wrapper maps to null.
        /// </summary>
        private void EmitStringReturnStaticPropertyAccessor(CSharpWriter csWriter, string enumName,
            string propertyPascalName, string wrapperSymbol, string moduleName, bool isNullable = false)
        {
            var returnType = isNullable ? "string?" : "string";
            csWriter.WriteLine($"public static unsafe {returnType} {propertyPascalName}");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("get");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine($"IntPtr resultPtr = PInvoke_Get{propertyPascalName}();");
            if (isNullable)
                csWriter.WriteLine("if (resultPtr == IntPtr.Zero) return null;");
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("var slice = *(Utf8Slice*)resultPtr;");
            csWriter.WriteLine("return slice.Len > 0");
            csWriter.Indent++;
            csWriter.WriteLine("? global::System.Text.Encoding.UTF8.GetString((byte*)slice.Ptr, (int)slice.Len)");
            csWriter.WriteLine(": string.Empty;");
            csWriter.Indent--;
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine($"finally {{ PInvoke_SBW_Free(resultPtr); }}");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            csWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
            csWriter.WriteLine($"[global::System.Runtime.InteropServices.LibraryImport(\"{_wrapperLibName}\", EntryPoint = \"{wrapperSymbol}\")]");
            csWriter.WriteLine($"private static partial IntPtr PInvoke_Get{propertyPascalName}();");
            csWriter.WriteLine();

            EmitFreePInvokeIfNeeded(csWriter, moduleName);
        }

        /// <summary>
        /// Emits the SBW_Free P/Invoke once per extensions class.
        /// </summary>
        private void EmitFreePInvokeIfNeeded(CSharpWriter csWriter, string moduleName)
        {
            if (_freePInvokeEmittedInExtensions) return;
            _freePInvokeEmittedInExtensions = true;
            var freeSymbol = Utf8SliceEmitter.GetFreeSymbolName(moduleName);
            csWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
            csWriter.WriteLine($"[global::System.Runtime.InteropServices.LibraryImport(\"{_wrapperLibName}\", EntryPoint = \"{freeSymbol}\")]");
            csWriter.WriteLine("private static partial void PInvoke_SBW_Free(IntPtr ptr);");
            csWriter.WriteLine();
        }

        /// <summary>
        /// Gets the Swift return type string for a property.
        /// </summary>
        private static string GetSwiftPropertyReturnType(PropertyDecl propertyDecl)
        {
            var typeSpec = propertyDecl.SwiftTypeSpec;
            if (typeSpec is NamedTypeSpec named)
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

        // === Helper Methods for Simple Enum Emission ===

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

        /// <summary>
        /// Generates a Swift expression to convert a scalar parameter to an enum value.
        /// For RawRepresentable enums, uses the rawValue initializer.
        /// For non-RawRepresentable enums, uses a direct case mapping expression.
        /// </summary>
        private static string EmitEnumParamConversion(EnumDecl enumDecl, string enumQualifiedName,
            string swiftScalarType, string paramExpr)
        {
            // Reconstruct the enum from the incoming scalar via a switch keyed on
            // GetCaseMarshalScalar — the SAME value the C# side passed (the cast enum member),
            // i.e. the real Swift raw value for integral RawRepresentable enums and the
            // declaration-order tag otherwise. Generate an inline closure with the switch.
            return $"{{ () -> {enumQualifiedName} in\n" +
                   $"    switch {paramExpr} {{\n" +
                   string.Join("\n", enumDecl.Cases.Select(c =>
                       $"    case {enumDecl.GetCaseMarshalScalar(c)}: return .{NameProvider.EscapeSwiftKeyword(c.Name)}")) +
                   $"\n    default: fatalError(\"[SwiftBindings] Invalid enum tag\")\n" +
                   $"    }}\n" +
                   $"}}()";
        }

        private static string DeterministicHash8(string input) => EmitterUtility.DeterministicHash8(input);

        /// <summary>
        /// Checks whether an enum can be safely emitted as a C# enum value type.
        /// Checks structural constraints only (nested types, non-equality operators).
        /// Members with compatible signatures are emitted as extensions; incompatible
        /// members are skipped with ReportCollector tracking — they don't block the gate.
        /// </summary>
        // Maps synthesized property names to the protocol conformance that generates them.
        // Only filtered when the enum actually conforms to the relevant protocol.
        private static readonly Dictionary<string, string> SynthesizedPropertyProtocols = new(StringComparer.Ordinal)
        {
            ["hashValue"]      = "Hashable",
            ["rawValue"]       = "RawRepresentable",
            ["allCases"]       = "CaseIterable",
            ["stringValue"]    = "CodingKey",
            ["intValue"]       = "CodingKey",
            ["_nsErrorDomain"] = "_ObjectiveCBridgeableError",
        };

        private static bool IsSynthesizedProperty(PropertyDecl prop, EnumDecl enumDecl)
        {
            if (!SynthesizedPropertyProtocols.TryGetValue(prop.Name, out var requiredProtocol))
                return false;
            // Note: a user-defined allCases on a CaseIterable enum can't be distinguished from
            // the synthesized one via ABI JSON alone. This is safe because allCases returns
            // [Self] (Array<EnumType>) which isn't emittable on the class path either — simple
            // enum types don't conform to ISwiftObject, so Array projection would fail.
            return enumDecl.Conformances.Any(c => c.Protocol.Name == requiredProtocol);
        }

        private static bool IsSynthesizedMethod(MethodDecl method, EnumDecl enumDecl)
            => MemberEmissionValidator.IsSynthesizedProtocolMethod(method, enumDecl);

        internal static bool CanSafelyEmitAsSimpleEnum(EnumDecl enumDecl)
        {
            // C# enums cannot contain nested types — if the enum has nested types,
            // they must be emitted inside the parent container (class-based path)
            if (enumDecl.Types.Any())
                return false;

            // Allow equality and comparison operators — C# integral enums support these natively.
            // Other operators (e.g., custom |, &, +) force the class-based path.
            if (enumDecl.Operators.Any(o => o.Name is not ("==" or "!=" or "<" or ">" or "<=" or ">=")))
                return false;

            // Properties, static methods, and instance methods with incompatible signatures
            // are handled by the extension emission path — compatible members are emitted,
            // incompatible members are skipped with ReportCollector tracking. They no longer
            // force the entire enum to the heavyweight class path.

            return true;
        }

        /// <summary>
        /// Checks whether all instance methods on an enum have signatures compatible with the
        /// simple-enum extension method emitter. Only methods whose return types and parameter
        /// types are within the supported primitive/string/bool/void/same-enum set qualify.
        /// If any instance method has an unsupported signature, the enum should stay class-based
        /// to avoid silently dropping members that the class path would have emitted.
        /// Module-internal methods (@usableFromInline internal) are excluded — they appear in
        /// ABI JSON but cannot be called from external Swift wrappers, so they must not block
        /// the simple path or be emitted as extension methods.
        /// </summary>
        internal static bool AreAllInstanceMethodsSimpleEmitterCompatible(EnumDecl enumDecl)
        {
            var instanceMethods = enumDecl.Methods
                .Where(m => !m.IsConstructor && m.MethodType != MethodType.Static)
                .Where(m => !IsSynthesizedMethod(m, enumDecl))
                .Where(m => !m.IsModuleInternal);

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
