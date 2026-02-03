// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Swift.Runtime;

namespace BindingsGeneration
{
    /// <summary>
    /// Factory class for creating instances of EnumHandler.
    /// </summary>
    public class EnumHandlerFactory : HandlerFactory, IFactory<BaseDecl, ITypeHandler>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="EnumHandlerFactory"/> class.
        /// </summary>
        /// <param name="loggerFactory">The logger factory instance.</param>
        public EnumHandlerFactory(ILoggerFactory loggerFactory) : base(loggerFactory.CreateLogger<EnumHandler>())
        {
        }

        /// <summary>
        /// Determines if the factory handles the specified declaration.
        /// </summary>
        /// <param name="decl">The base declaration.</param>
        public bool Handles(BaseDecl decl)
        {
            return decl is EnumDecl;
        }

        /// <summary>
        /// Constructs a new instance of EnumHandler.
        /// </summary>
        public ITypeHandler Construct()
        {
            return new EnumHandler(_handlerLogger);
        }
    }

    /// <summary>
    /// Handler class for enum declarations.
    /// </summary>
    public class EnumHandler : BaseHandler, ITypeHandler
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="EnumHandler"/> class.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        public EnumHandler(ILogger logger) : base(logger)
        {
        }

        /// <inheritdoc/>
        public IEnvironment Marshal(BaseDecl baseDecl, ITypeDatabase typeDatabase)
        {
            if (baseDecl is not EnumDecl enumDecl)
            {
                throw new ArgumentException("The provided decl must be an EnumDecl.", nameof(baseDecl));
            }
            return new TypeEnvironment(enumDecl, typeDatabase);
        }

        /// <inheritdoc/>
        public void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor)
        {
            var enumEnv = (TypeEnvironment)env;
            var enumDecl = (EnumDecl)enumEnv.TypeDecl;
            var parentDecl = enumDecl.ParentDecl ?? throw new ArgumentNullException(nameof(enumDecl.ParentDecl));
            var moduleDecl = enumDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(enumDecl.ModuleDecl));

            if (GenericTypeEmitter.TryGetUnsupportedConstraint(enumDecl, out var unsupportedConstraint))
            {
                var reason = unsupportedConstraint.Module == "SwiftUI"
                    ? SkipReason.SwiftUIConstraint
                    : unsupportedConstraint.Module == "Combine"
                        ? SkipReason.CombineFramework
                        : SkipReason.UnsupportedType;
                ReportCollector.RecordTypeSkipped(enumDecl, reason, $"Unsupported generic constraint: {unsupportedConstraint.ModuleQualifiedName}");
                _logger.LogWarning(
                    "Skipping type '{TypeName}' - generic constraint references unsupported protocol '{Protocol}' from module '{Module}'.",
                    enumDecl.Name,
                    unsupportedConstraint.Name,
                    unsupportedConstraint.Module);
                return;
            }

            ReportCollector.RecordTypeEmitted(enumDecl);

            var typeNameWithGenerics = GenericTypeEmitter.GetTypeNameWithGenerics(enumDecl);
            var whereClause = GenericTypeEmitter.GetWhereClause(enumDecl, env.TypeDatabase);

            // Create P/Invoke helper context for generic enums (to avoid CS7042).
            var pinvokeHelperContext = PInvokeHelperContext.CreateIfGeneric(enumDecl);
            var previousContext = conductor.CurrentPInvokeHelperContext;
            conductor.CurrentPInvokeHelperContext = pinvokeHelperContext;

            try
            {
                // Use unsafe class since methods may use function pointers
                var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
                    enumDecl,
                    typeNameWithGenerics,
                    moduleDecl.Name,
                    env.TypeDatabase);
                var classDeclaration = $"public unsafe class {typeNameWithGenerics} : {string.Join(", ", interfaces)}";
                if (!string.IsNullOrEmpty(whereClause))
                    classDeclaration += $" {whereClause}";
                csWriter.WriteLine(classDeclaration);
                csWriter.WriteLine("{");
                csWriter.Indent++;

                // Emit payload field and property - enums need this for property accessors
                csWriter.WriteLine($"static nuint _payloadSize = SwiftObjectHelper<{typeNameWithGenerics}>.GetTypeMetadata().Size;");
                csWriter.WriteLine($"SwiftSafeHandle<{typeNameWithGenerics}> _payload = SwiftSafeHandle<{typeNameWithGenerics}>.Zero;");
                csWriter.WriteLine($"public SwiftSafeHandle<{typeNameWithGenerics}> Payload => _payload;");
                csWriter.WriteLine();

            // Emit case constructors for all cases
            // Cases with associated values become static methods with P/Invoke constructors
            // Simple cases (no associated values) use RawRepresentable if available
            var simpleCases = new List<EnumCaseDecl>();
            var emittedCaseConstructorNames = new HashSet<string>();
            foreach (var caseDecl in enumDecl.Cases)
            {
                if (caseDecl.HasAssociatedValues)
                {
                    if (EmitEnumCaseWithAssociatedValues(csWriter, enumDecl, caseDecl, moduleDecl, env.TypeDatabase, typeNameWithGenerics, pinvokeHelperContext))
                    {
                        emittedCaseConstructorNames.Add(NameProvider.ToPascalCase(caseDecl.Name));
                    }
                }
                else
                {
                    simpleCases.Add(caseDecl);
                }
            }

            // Precompute naming-collision context for enum properties.
            var nestedTypeNames = new HashSet<string>(enumDecl.Types.Select(t => t.Name));

            // Handle simple cases via RawRepresentable if available, otherwise via direct P/Invoke
            if (simpleCases.Count > 0)
            {
                if (enumDecl.IsRawRepresentable)
                {
                    EmitRawRepresentableSupport(csWriter, enumDecl, simpleCases, moduleDecl, env.TypeDatabase, typeNameWithGenerics, pinvokeHelperContext);
                }
                else
                {
                    // No RawRepresentable - emit simple cases via direct P/Invoke
                    foreach (var caseDecl in simpleCases)
                    {
                        EmitSimpleCaseDirectPInvoke(csWriter, enumDecl, caseDecl, moduleDecl, env.TypeDatabase, typeNameWithGenerics, pinvokeHelperContext);
                    }
                }
            }

            // Emit CaseTag enum and Tag property for enums with any cases
            if (enumDecl.Cases.Any())
            {
                EmitCaseTagEnum(csWriter, enumDecl);
                EmitTagProperty(csWriter, enumDecl);
            }

            // Emit TryGet methods for cases with associated values
            foreach (var caseDecl in enumDecl.Cases.Where(c => c.HasAssociatedValues))
            {
                EmitTryGetMethod(csWriter, enumDecl, caseDecl, env.TypeDatabase);
            }

            // Add a blank line between cases and other members
            if (enumDecl.Cases.Any())
            {
                csWriter.WriteLine();
            }

            // Emit properties using the same pattern as other handlers
            foreach (var propertyDecl in enumDecl.Properties)
            {
                var propertyName = NameProvider.GetPropertyName(propertyDecl.Name, nestedTypeNames, enumDecl.Name);
                if (propertyDecl.IsStatic && emittedCaseConstructorNames.Contains(propertyName))
                {
                    _logger.LogInformation($"Skipping enum static property '{enumDecl.Name}.{propertyName}' because a case constructor with the same C# name is already emitted.");
                    continue;
                }

                if (conductor.TryGetPropertyHandler(propertyDecl, out var propertyHandler))
                {
                    var propertyEnv = propertyHandler.Marshal(propertyDecl, env.TypeDatabase);
                    propertyHandler.Emit(csWriter, swiftWriter, propertyEnv, conductor);
                }
                else
                {
                    _logger.LogWarning($"No handler found for property {propertyDecl.Name}");
                }
            }

            // Emit ISwiftObject implementation
            var iSwiftObjectWriter = new EnumISwiftObjectMethodWriter(csWriter, env.TypeDatabase, moduleDecl, enumDecl, typeNameWithGenerics, pinvokeHelperContext);
            iSwiftObjectWriter.WriteEnumImplementation();

            // Collect property names for method/property collision detection
            // Include nested type names and containing type name for consistent naming with PropertyHandler
            var propertyNames = new HashSet<string>(enumDecl.Properties.Select(p =>
                NameProvider.GetPropertyName(p.Name, nestedTypeNames, enumDecl.Name)));

            // Emit nested types and methods using base handler
            base.HandleBaseDecl(csWriter, swiftWriter, enumDecl.Types, conductor, env.TypeDatabase);
            base.HandleBaseDecl(csWriter, swiftWriter, enumDecl.Methods.Where(m => !m.IsConstructor).ToList(), conductor, env.TypeDatabase, propertyNames, pinvokeHelperContext);

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

                // Emit the P/Invoke helper class after the main enum.
                pinvokeHelperContext?.EmitHelperClass(csWriter);
            }
            finally
            {
                conductor.CurrentPInvokeHelperContext = previousContext;
            }
        }

        /// <summary>
        /// Emits a static property for a simple enum case (no associated values).
        /// </summary>
        private void EmitEnumCase(CSharpWriter csWriter, EnumDecl enumDecl, EnumCaseDecl caseDecl, ModuleDecl moduleDecl, ITypeDatabase typeDatabase)
        {
            var caseName = caseDecl.Name;
            var enumTypeName = enumDecl.Name;
            var capitalizedName = char.ToUpper(caseName[0]) + caseName.Substring(1);
            var pInvokeName = $"PInvoke_{capitalizedName}";
            var libPath = typeDatabase.GetLibraryPath(moduleDecl.Name);

            // Generate a static property for this case with backing P/Invoke
            csWriter.WriteLine($"/// <summary>");
            csWriter.WriteLine($"/// Gets the '{caseName}' case of {enumTypeName}.");
            csWriter.WriteLine($"/// </summary>");
            csWriter.WriteLine($"public static {enumTypeName} {capitalizedName}");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("get");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine($"var result = new {enumTypeName}();");
            csWriter.WriteLine($"IntPtr casePtr = {pInvokeName}();");
            csWriter.WriteLine($"result._payload = new SwiftSafeHandle<{enumTypeName}>(casePtr);");
            csWriter.WriteLine("return result;");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            // P/Invoke declaration for the case constructor
            csWriter.WriteLine($"[DllImport(\"{libPath}\", EntryPoint = \"{caseDecl.MangledName}\")]");
            csWriter.WriteLine($"private static extern IntPtr {pInvokeName}();");
            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits a simple enum case (no associated values) via direct P/Invoke.
        /// Swift enum case constructors use indirect return - they write to a buffer provided by the caller.
        /// </summary>
        private void EmitSimpleCaseDirectPInvoke(CSharpWriter csWriter, EnumDecl enumDecl, EnumCaseDecl caseDecl, ModuleDecl moduleDecl, ITypeDatabase typeDatabase, string enumTypeName, PInvokeHelperContext? pinvokeHelperContext)
        {
            var caseName = caseDecl.Name;
            var capitalizedName = char.ToUpper(caseName[0]) + caseName.Substring(1);
            var pInvokeName = $"PInvoke_{capitalizedName}";
            var libPath = typeDatabase.GetLibraryPath(moduleDecl.Name);

            // Generate a static property for this case with backing P/Invoke
            csWriter.WriteLine($"/// <summary>");
            csWriter.WriteLine($"/// Gets the '{caseName}' case of {enumTypeName}.");
            csWriter.WriteLine($"/// </summary>");
            csWriter.WriteLine($"public static {enumTypeName} {capitalizedName}");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("get");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // Swift enum case constructors use indirect return - allocate buffer and pass it
            csWriter.WriteLine($"var result = new {enumTypeName}();");
            var getMetadataCall = pinvokeHelperContext != null
                ? $"{pinvokeHelperContext.HelperClassName}.PInvoke_getMetadata({string.Join(", ", pinvokeHelperContext.GetMetadataArgumentList())})"
                : "PInvoke_getMetadata()";
            csWriter.WriteLine($"var metadata = {getMetadataCall};");
            csWriter.WriteLine($"IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);");
            csWriter.WriteLine($"var indirectResult = new SwiftIndirectResult((void*)buffer);");
            var invokeArgs = pinvokeHelperContext != null
                ? $"indirectResult, {string.Join(", ", pinvokeHelperContext.GetMetadataArgumentList())}"
                : "indirectResult";
            var pInvokeTarget = pinvokeHelperContext != null
                ? $"{pinvokeHelperContext.HelperClassName}.{pInvokeName}"
                : pInvokeName;
            csWriter.WriteLine($"{pInvokeTarget}({invokeArgs});");
            csWriter.WriteLine($"result._payload = new SwiftSafeHandle<{enumTypeName}>(buffer);");
            csWriter.WriteLine("return result;");

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            // P/Invoke declaration for the case constructor - uses indirect result
            if (pinvokeHelperContext != null)
            {
                pinvokeHelperContext.AddDeclaration(new PInvokeDeclaration
                {
                    LibraryPath = libPath,
                    EntryPoint = caseDecl.MangledName,
                    MethodName = pInvokeName,
                    ReturnType = "void",
                    ParametersString = "SwiftIndirectResult result",
                    IsAsync = false,
                    MetadataParameters = pinvokeHelperContext.GetMetadataParameterDeclarations()
                });
            }
            else
            {
                csWriter.WriteLine($"[DllImport(\"{libPath}\", EntryPoint = \"{caseDecl.MangledName}\")]");
                csWriter.WriteLine("[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]");
                csWriter.WriteLine($"private static extern void {pInvokeName}(SwiftIndirectResult result);");
                csWriter.WriteLine();
            }
        }

        /// <summary>
        /// Emits a static method for an enum case with associated values.
        /// </summary>
        private bool EmitEnumCaseWithAssociatedValues(CSharpWriter csWriter, EnumDecl enumDecl, EnumCaseDecl caseDecl, ModuleDecl moduleDecl, ITypeDatabase typeDatabase, string enumTypeName, PInvokeHelperContext? pinvokeHelperContext)
        {
            var caseName = caseDecl.Name;
            var capitalizedName = char.ToUpper(caseName[0]) + caseName.Substring(1);
            var pInvokeName = $"PInvoke_{capitalizedName}";
            var libPath = typeDatabase.GetLibraryPath(moduleDecl.Name);
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Build parameter list from associated values
            var parameters = new List<(string type, string name, TypeSpec typeSpec)>();
            for (int i = 0; i < caseDecl.AssociatedValues.Count; i++)
            {
                var typeSpec = caseDecl.AssociatedValues[i];
                var csharpType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler);

                // Check if type is unsupported
                if (csharpType == TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName)
                {
                    _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' has unsupported associated value type at index {i}. Skipping case.");
                    return false;
                }

                // Use type label if available, otherwise generate a name
                var paramName = typeSpec.TypeLabel ?? $"value{i}";
                // Sanitize parameter name (remove invalid characters, ensure starts with letter)
                paramName = SanitizeParameterName(paramName);
                parameters.Add((csharpType, paramName, typeSpec));
            }

            // Generate the static method for this case
            csWriter.WriteLine($"/// <summary>");
            csWriter.WriteLine($"/// Creates the '{caseName}' case of {enumTypeName}.");
            csWriter.WriteLine($"/// </summary>");

            var parameterString = string.Join(", ", parameters.Select(p => $"{p.type} {p.name}"));
            csWriter.WriteLine($"public static {enumTypeName} {capitalizedName}({parameterString})");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine($"var result = new {enumTypeName}();");

            // Swift enum case constructors use indirect return - allocate buffer and pass it
            var getMetadataCall = pinvokeHelperContext != null
                ? $"{pinvokeHelperContext.HelperClassName}.PInvoke_getMetadata({string.Join(", ", pinvokeHelperContext.GetMetadataArgumentList())})"
                : "PInvoke_getMetadata()";
            csWriter.WriteLine($"var metadata = {getMetadataCall};");
            csWriter.WriteLine($"IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);");
            csWriter.WriteLine($"var indirectResult = new SwiftIndirectResult((void*)buffer);");

            // Build the P/Invoke call with arguments
            var argList = new List<string> { "indirectResult" };
            for (int i = 0; i < parameters.Count; i++)
            {
                var (type, name, typeSpec) = parameters[i];
                argList.Add(GetPInvokeArgument(name, typeSpec, typeDatabase));
            }

            var invokeArgList = string.Join(", ", argList);
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
            csWriter.WriteLine($"result._payload = new SwiftSafeHandle<{enumTypeName}>(buffer);");
            csWriter.WriteLine("return result;");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            // P/Invoke declaration for the case constructor with associated values - uses indirect result
            var pInvokeParams = new List<string> { "SwiftIndirectResult result" };
            for (int i = 0; i < parameters.Count; i++)
            {
                var (_, name, typeSpec) = parameters[i];
                var pInvokeType = GetPInvokeType(typeSpec, typeDatabase);
                pInvokeParams.Add($"{pInvokeType} {name}");
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
                    MetadataParameters = pinvokeHelperContext.GetMetadataParameterDeclarations()
                });
            }
            else
            {
                csWriter.WriteLine($"[DllImport(\"{libPath}\", EntryPoint = \"{caseDecl.MangledName}\")]");
                csWriter.WriteLine("[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]");
                csWriter.WriteLine($"private static extern void {pInvokeName}({string.Join(", ", pInvokeParams)});");
                csWriter.WriteLine();
            }
            return true;
        }

        /// <summary>
        /// Gets the C# type name for an enum case associated value type.
        /// </summary>
        private static string GetCSharpTypeNameForEnumCase(TypeSpec typeSpec, ITypeDatabase typeDatabase, BoundGenericsHandler boundGenericsHandler)
        {
            if (typeSpec is NamedTypeSpec genericParamType &&
                TypeSpecHelpers.IsGenericTypeParameter(genericParamType.Name) &&
                TryGetGenericTypeParameterName(genericParamType.Name, out var typeParameterName))
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
            if (typeSpec is NamedTypeSpec namedTypeSpec && namedTypeSpec.ContainsGenericParameters)
            {
                // Create a temporary property to use the BoundGenericsHandler
                var tempProperty = new PropertyDecl
                {
                    Name = "_temp",
                    SwiftTypeSpec = typeSpec,
                    IsStatic = false,
                    HasStorage = false,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = null
                };
                return boundGenericsHandler.TranslateBoundGenericTypeToCSharp(tempProperty);
            }

            // Handle tuple types
            if (typeSpec is TupleTypeSpec tupleType)
            {
                var tupleHandler = new TupleHandler(typeDatabase);
                // Use a recursive translator that handles bound generics for each element
                return tupleHandler.GetCSharpTupleType(tupleType, elementTypeSpec =>
                    GetCSharpTypeNameForEnumCase(elementTypeSpec, typeDatabase, boundGenericsHandler));
            }

            // For non-generic types, use the standard lookup
            return typeDatabase.GetTypeRecordOrAnyType(typeSpec).CSharpTypeName.FullyQualifiedName;
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

            // Handle existential types - pass the container directly (it's a blittable struct)
            var existentialHandler = new ExistentialHandler(typeDatabase);
            if (existentialHandler.IsExistential(typeSpec))
            {
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

            // ObjC bridged types use .Handle to get the native pointer
            if (MarshallingHelpers.IsObjCBridged(typeRecord))
            {
                return $"{paramName}.Handle";
            }

            // For types that have payloads (non-frozen structs, classes), access the Payload.DangerousGetHandle()
            if (MarshallingHelpers.RequiresMemoryManagement(typeRecord))
            {
                return $"{paramName}.Payload.DangerousGetHandle()";
            }

            return paramName;
        }

        /// <summary>
        /// Gets the P/Invoke parameter type for an associated value.
        /// </summary>
        private static string GetPInvokeType(TypeSpec typeSpec, ITypeDatabase typeDatabase)
        {
            if (typeSpec is NamedTypeSpec genericParamType &&
                TypeSpecHelpers.IsGenericTypeParameter(genericParamType.Name))
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
                    GetPInvokeType(elementTypeSpec, typeDatabase));
            }

            var typeRecord = typeDatabase.GetTypeRecordOrAnyType(typeSpec);

            // For types that require memory management, use IntPtr in P/Invoke
            if (MarshallingHelpers.RequiresMemoryManagement(typeRecord))
            {
                return "IntPtr";
            }

            // For primitives and frozen structs, use the C# type directly
            return typeRecord.CSharpTypeName.FullyQualifiedName;
        }

        private static bool TryGetGenericTypeParameterName(string swiftTypeName, out string typeParameterName)
        {
            typeParameterName = string.Empty;
            if (string.IsNullOrWhiteSpace(swiftTypeName))
                return false;

            if (swiftTypeName.StartsWith("τ_"))
            {
                var parts = swiftTypeName.Split('_');
                if (parts.Length > 0 && int.TryParse(parts[^1], out var index))
                {
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

            // Handle C# reserved keywords
            var keywords = new HashSet<string> { "string", "int", "bool", "float", "double", "object", "class", "struct", "enum", "delegate", "event", "interface", "namespace", "using", "static", "public", "private", "protected", "internal", "abstract", "sealed", "virtual", "override", "new", "return", "if", "else", "for", "foreach", "while", "do", "switch", "case", "default", "break", "continue", "goto", "throw", "try", "catch", "finally", "lock", "using", "checked", "unchecked", "fixed", "unsafe", "volatile", "extern", "ref", "out", "in", "params", "this", "base", "null", "true", "false", "is", "as", "typeof", "sizeof", "stackalloc", "await", "async", "yield", "nameof", "var", "dynamic" };

            if (keywords.Contains(result))
                result = "@" + result;

            return string.IsNullOrEmpty(result) ? "value" : result;
        }

        /// <summary>
        /// Emits RawRepresentable support for enums with simple cases.
        /// This includes a FromRawValue method and static properties for each case.
        /// </summary>
        private void EmitRawRepresentableSupport(CSharpWriter csWriter, EnumDecl enumDecl, List<EnumCaseDecl> simpleCases, ModuleDecl moduleDecl, ITypeDatabase typeDatabase, string enumTypeName, PInvokeHelperContext? pinvokeHelperContext)
        {
            var rawTypeName = enumDecl.RawValueTypeName!;
            var libPath = typeDatabase.GetLibraryPath(moduleDecl.Name);

            // Map Swift raw type to C# type
            var csharpRawType = rawTypeName switch
            {
                "Int" => "long",
                "Int8" => "sbyte",
                "Int16" => "short",
                "Int32" => "int",
                "Int64" => "long",
                "UInt" => "ulong",
                "UInt8" => "byte",
                "UInt16" => "ushort",
                "UInt32" => "uint",
                "UInt64" => "ulong",
                "String" => "string",
                _ => rawTypeName // Fall back to the Swift name
            };

            // Find the init(rawValue:) constructor in the enum's methods
            var initRawValueMethod = enumDecl.Methods.FirstOrDefault(m =>
                m.IsConstructor &&
                m.Name == "init" &&
                m.CSSignature.Count == 2 && // Return type + rawValue parameter
                m.CSSignature.Any(a => a.Name == "rawValue" || a.PrivateName == "rawValue"));

            if (initRawValueMethod == null)
            {
                _logger.LogWarning($"Enum '{enumTypeName}' is RawRepresentable but init(rawValue:) constructor not found. Skipping simple case emission.");
                foreach (var caseDecl in simpleCases)
                {
                    _logger.LogWarning($"Skipping enum case '{enumTypeName}.{caseDecl.Name}' - init(rawValue:) constructor not found.");
                }
                return;
            }

            // Emit FromRawValue method - different implementations for frozen vs non-frozen enums
            // Frozen enums can return directly, non-frozen enums require indirect return via SwiftOptional
            if (enumDecl.IsFrozen)
            {
                // Frozen enum: P/Invoke returns IntPtr directly, null check via IntPtr.Zero
                csWriter.WriteLine("/// <summary>");
                csWriter.WriteLine($"/// Creates a {enumTypeName} from its raw value.");
                csWriter.WriteLine("/// Returns null if the raw value doesn't correspond to a valid case.");
                csWriter.WriteLine("/// </summary>");
                csWriter.WriteLine($"public static {enumTypeName}? FromRawValue({csharpRawType} rawValue)");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                var rawInitCall = pinvokeHelperContext != null
                    ? $"{pinvokeHelperContext.HelperClassName}.PInvoke_InitWithRawValue(rawValue, {string.Join(", ", pinvokeHelperContext.GetMetadataArgumentList())})"
                    : "PInvoke_InitWithRawValue(rawValue)";
                csWriter.WriteLine($"IntPtr resultPtr = {rawInitCall};");
                csWriter.WriteLine("if (resultPtr == IntPtr.Zero)");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine("return null;");
                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine($"var result = new {enumTypeName}();");
                csWriter.WriteLine($"result._payload = new SwiftSafeHandle<{enumTypeName}>(resultPtr);");
                csWriter.WriteLine("return result;");
                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine();

                // Emit P/Invoke for init(rawValue:) - frozen version returns IntPtr directly
                if (pinvokeHelperContext != null)
                {
                    pinvokeHelperContext.AddDeclaration(new PInvokeDeclaration
                    {
                        LibraryPath = libPath,
                        EntryPoint = initRawValueMethod.MangledName,
                        MethodName = "PInvoke_InitWithRawValue",
                        ReturnType = "IntPtr",
                        ParametersString = $"{csharpRawType} rawValue",
                        IsAsync = false,
                        MetadataParameters = pinvokeHelperContext.GetMetadataParameterDeclarations()
                    });
                }
                else
                {
                    csWriter.WriteLine($"[DllImport(\"{libPath}\", EntryPoint = \"{initRawValueMethod.MangledName}\")]");
                    csWriter.WriteLine("[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]");
                    csWriter.WriteLine($"private static extern IntPtr PInvoke_InitWithRawValue({csharpRawType} rawValue);");
                    csWriter.WriteLine();
                }
            }
            else
            {
                // Non-frozen enum: failable initializer returns Optional<Self> via indirect return
                // We allocate buffer for SwiftOptional<EnumType>, call P/Invoke, then check the tag
                csWriter.WriteLine("/// <summary>");
                csWriter.WriteLine($"/// Creates a {enumTypeName} from its raw value.");
                csWriter.WriteLine("/// Returns null if the raw value doesn't correspond to a valid case.");
                csWriter.WriteLine("/// </summary>");
                csWriter.WriteLine($"public static unsafe {enumTypeName}? FromRawValue({csharpRawType} rawValue)");
                csWriter.WriteLine("{");
                csWriter.Indent++;

                // Get metadata for the enum type and SwiftOptional<EnumType>
                csWriter.WriteLine("// Get metadata for the enum type");
                var getMetadataCall = pinvokeHelperContext != null
                    ? $"{pinvokeHelperContext.HelperClassName}.PInvoke_getMetadata({string.Join(", ", pinvokeHelperContext.GetMetadataArgumentList())})"
                    : "PInvoke_getMetadata()";
                csWriter.WriteLine($"var enumMetadata = {getMetadataCall};");
                csWriter.WriteLine();
                csWriter.WriteLine("// Get metadata for SwiftOptional<EnumType>");
                var optionalMetadataAccessorCall = pinvokeHelperContext != null
                    ? $"{pinvokeHelperContext.HelperClassName}.PInvokesForSwiftOptional_MetadataAccessor"
                    : "PInvokesForSwiftOptional_MetadataAccessor";
                csWriter.WriteLine($"var optionalMetadata = {optionalMetadataAccessorCall}(");
                csWriter.Indent++;
                csWriter.WriteLine("TypeMetadataRequest.Complete, enumMetadata);");
                csWriter.Indent--;
                csWriter.WriteLine();

                // Allocate buffer for optional result
                csWriter.WriteLine("// Allocate buffer for SwiftOptional<EnumType> result");
                csWriter.WriteLine("void* resultBuffer = NativeMemory.AllocZeroed(optionalMetadata.Size);");
                csWriter.WriteLine("try");
                csWriter.WriteLine("{");
                csWriter.Indent++;

                // Call P/Invoke with indirect result
                csWriter.WriteLine("// Call the failable initializer with indirect result");
                csWriter.WriteLine("var swiftIndirectResult = new SwiftIndirectResult(resultBuffer);");
                var rawInitIndirectCall = pinvokeHelperContext != null
                    ? $"{pinvokeHelperContext.HelperClassName}.PInvoke_InitWithRawValue(swiftIndirectResult, rawValue, {string.Join(", ", pinvokeHelperContext.GetMetadataArgumentList())})"
                    : "PInvoke_InitWithRawValue(swiftIndirectResult, rawValue)";
                csWriter.WriteLine($"{rawInitIndirectCall};");
                csWriter.WriteLine();

                // Check if Some or None via enum tag
                csWriter.WriteLine("// Check if result is Some (tag 0) or None (tag 1)");
                csWriter.WriteLine("uint tag = optionalMetadata.ValueWitnessTable->GetEnumTag((byte*)resultBuffer, optionalMetadata);");
                csWriter.WriteLine();
                csWriter.WriteLine("// SwiftOptionalCases.None = 1");
                csWriter.WriteLine("if (tag == 1)");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine("return null;");
                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine();

                // Extract enum payload (it's at the start of the optional buffer)
                csWriter.WriteLine("// Extract the enum value from the optional's payload");
                csWriter.WriteLine("IntPtr enumBuffer = (IntPtr)NativeMemory.Alloc(enumMetadata.Size);");
                csWriter.WriteLine("enumMetadata.ValueWitnessTable->InitializeWithCopy((void*)enumBuffer, resultBuffer, enumMetadata);");
                csWriter.WriteLine();
                csWriter.WriteLine($"var result = new {enumTypeName}();");
                csWriter.WriteLine($"result._payload = new SwiftSafeHandle<{enumTypeName}>(enumBuffer);");
                csWriter.WriteLine("return result;");

                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine("finally");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine("// Clean up the optional buffer");
                csWriter.WriteLine("optionalMetadata.ValueWitnessTable->Destroy(resultBuffer, optionalMetadata);");
                csWriter.WriteLine("NativeMemory.Free(resultBuffer);");
                csWriter.Indent--;
                csWriter.WriteLine("}");

                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine();

                // Emit P/Invoke for init(rawValue:) - non-frozen version uses indirect result
                if (pinvokeHelperContext != null)
                {
                    pinvokeHelperContext.AddDeclaration(new PInvokeDeclaration
                    {
                        LibraryPath = libPath,
                        EntryPoint = initRawValueMethod.MangledName,
                        MethodName = "PInvoke_InitWithRawValue",
                        ReturnType = "void",
                        ParametersString = $"SwiftIndirectResult result, {csharpRawType} rawValue",
                        IsAsync = false,
                        MetadataParameters = pinvokeHelperContext.GetMetadataParameterDeclarations()
                    });
                }
                else
                {
                    csWriter.WriteLine($"[DllImport(\"{libPath}\", EntryPoint = \"{initRawValueMethod.MangledName}\")]");
                    csWriter.WriteLine("[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]");
                    csWriter.WriteLine($"private static extern void PInvoke_InitWithRawValue(SwiftIndirectResult result, {csharpRawType} rawValue);");
                    csWriter.WriteLine();
                }

                // Emit P/Invoke for SwiftOptional metadata accessor (using Swift stdlib symbol)
                if (pinvokeHelperContext != null)
                {
                    pinvokeHelperContext.AddDeclaration(new PInvokeDeclaration
                    {
                        LibraryPath = "/usr/lib/swift/libswiftCore.dylib",
                        EntryPoint = "$sSqMa",
                        MethodName = "PInvokesForSwiftOptional_MetadataAccessor",
                        ReturnType = "TypeMetadata",
                        ParametersString = "TypeMetadataRequest request, TypeMetadata typeMetadata",
                        IsAsync = false
                    });
                }
                else
                {
                    csWriter.WriteLine("// SwiftOptional metadata accessor from Swift stdlib");
                    csWriter.WriteLine("[DllImport(\"/usr/lib/swift/libswiftCore.dylib\", EntryPoint = \"$sSqMa\")]");
                    csWriter.WriteLine("[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]");
                    csWriter.WriteLine("private static extern TypeMetadata PInvokesForSwiftOptional_MetadataAccessor(TypeMetadataRequest request, TypeMetadata typeMetadata);");
                    csWriter.WriteLine();
                }
            }

            // Emit static properties for each simple case
            // Simple cases use sequential raw values starting from 0 (Swift default behavior)
            for (int i = 0; i < simpleCases.Count; i++)
            {
                var caseDecl = simpleCases[i];
                var caseName = caseDecl.Name;
                var capitalizedName = char.ToUpper(caseName[0]) + caseName.Substring(1);

                // Determine the raw value - for Int-based enums, Swift uses sequential values starting at 0
                // For String-based enums, the raw value is the case name
                string rawValueLiteral;
                if (csharpRawType == "string")
                {
                    rawValueLiteral = $"\"{caseName}\"";
                }
                else
                {
                    rawValueLiteral = i.ToString();
                }

                csWriter.WriteLine("/// <summary>");
                csWriter.WriteLine($"/// Gets the '{caseName}' case of {enumTypeName}.");
                csWriter.WriteLine("/// </summary>");
                csWriter.WriteLine($"public static {enumTypeName} {capitalizedName}");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine("get");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine($"var result = FromRawValue({rawValueLiteral});");
                csWriter.WriteLine("if (result == null)");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                // Escape quotes in rawValueLiteral for the error message string
                var escapedRawValue = rawValueLiteral.Replace("\"", "\\\"");
                csWriter.WriteLine($"throw new InvalidOperationException(\"Failed to create {enumTypeName}.{capitalizedName} from raw value {escapedRawValue}\");");
                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine("return result;");
                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine();
            }
        }

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
        private void EmitTagProperty(CSharpWriter csWriter, EnumDecl enumDecl)
        {
            csWriter.WriteLine("/// <summary>");
            csWriter.WriteLine("/// Gets the current case of this enum instance.");
            csWriter.WriteLine("/// </summary>");
            csWriter.WriteLine("public unsafe CaseTag Tag");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("get");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            csWriter.WriteLine("bool success = false;");
            csWriter.WriteLine("_payload.DangerousAddRef(ref success);");
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            csWriter.WriteLine($"var metadata = SwiftObjectHelper<{GenericTypeEmitter.GetTypeNameWithGenerics(enumDecl)}>.GetTypeMetadata();");
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
            csWriter.WriteLine("}");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits a TryGet method for an enum case with associated values.
        /// The method extracts the associated value(s) if the enum is in the specified case.
        /// </summary>
        private void EmitTryGetMethod(CSharpWriter csWriter, EnumDecl enumDecl, EnumCaseDecl caseDecl, ITypeDatabase typeDatabase)
        {
            var caseName = caseDecl.Name;
            var capitalizedName = char.ToUpper(caseName[0]) + caseName.Substring(1);

            // Swift represents multi-value associated types as a single tuple type.
            // Check if the single associated value is a tuple (multi-element extraction).
            if (caseDecl.AssociatedValues.Count == 1 && caseDecl.AssociatedValues[0] is TupleTypeSpec tupleSpec && tupleSpec.Elements.Count > 1)
            {
                // Delegate to tuple-specific TryGet emission
                EmitTryGetMethodForTuple(csWriter, enumDecl, caseDecl, tupleSpec, typeDatabase);
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
            csWriter.WriteLine($"public unsafe bool TryGet{capitalizedName}([MaybeNullWhen(false)] out {outType} value)");
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

            csWriter.WriteLine($"var metadata = SwiftObjectHelper<{GenericTypeEmitter.GetTypeNameWithGenerics(enumDecl)}>.GetTypeMetadata();");
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
            csWriter.WriteLine("}");
            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits a TryGet method for an enum case with tuple associated values.
        /// Generates multiple out parameters, one for each tuple element.
        /// Uses TupleTypeMetadata to get element offsets at runtime.
        /// </summary>
        private void EmitTryGetMethodForTuple(CSharpWriter csWriter, EnumDecl enumDecl, EnumCaseDecl caseDecl, TupleTypeSpec tupleSpec, ITypeDatabase typeDatabase)
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
            csWriter.WriteLine($"public unsafe bool TryGet{capitalizedName}({outParamString})");
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

            csWriter.WriteLine($"var metadata = SwiftObjectHelper<{GenericTypeEmitter.GetTypeNameWithGenerics(enumDecl)}>.GetTypeMetadata();");
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
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            // Emit the tuple metadata accessor helper
            EmitTupleMetadataAccessor(csWriter, capitalizedName, parameters, typeDatabase);
        }

        /// <summary>
        /// Emits a cached tuple metadata accessor for a specific enum case.
        /// This generates the tuple type metadata once and caches it for efficiency.
        /// </summary>
        private void EmitTupleMetadataAccessor(CSharpWriter csWriter, string capitalizedCaseName, List<(string type, string name, TypeSpec typeSpec)> parameters, ITypeDatabase typeDatabase)
        {
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Emit the static cached field
            csWriter.WriteLine($"private static TupleTypeMetadata* _tupleMetadata_{capitalizedCaseName};");
            csWriter.WriteLine();

            // Emit the accessor method
            csWriter.WriteLine($"private static unsafe TupleTypeMetadata* GetTupleMetadata_{capitalizedCaseName}()");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            csWriter.WriteLine($"if (_tupleMetadata_{capitalizedCaseName} != null)");
            csWriter.Indent++;
            csWriter.WriteLine($"return _tupleMetadata_{capitalizedCaseName};");
            csWriter.Indent--;
            csWriter.WriteLine();

            // Get element type metadata - build an array of TypeMetadata
            csWriter.WriteLine("// Build tuple metadata from element types");
            csWriter.WriteLine($"var elementMetadataArray = new TypeMetadata[{parameters.Count}];");

            for (int i = 0; i < parameters.Count; i++)
            {
                var (_, _, typeSpec) = parameters[i];
                EmitGetTypeMetadataForElement(csWriter, typeSpec, i, typeDatabase);
            }
            csWriter.WriteLine();

            // Use TypeMetadata.GetTupleTypeMetadataFromElements
            csWriter.WriteLine("// Get tuple type metadata from Swift runtime");
            csWriter.WriteLine("var tupleMetadata = TypeMetadata.GetTupleTypeMetadataFromElements(elementMetadataArray);");
            csWriter.WriteLine();

            csWriter.WriteLine($"_tupleMetadata_{capitalizedCaseName} = tupleMetadata.AsTupleMetadata();");
            csWriter.WriteLine($"return _tupleMetadata_{capitalizedCaseName};");

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits code to get the TypeMetadata for a tuple element type.
        /// Stores the result in elementMetadataArray[index].
        /// </summary>
        private void EmitGetTypeMetadataForElement(CSharpWriter csWriter, TypeSpec typeSpec, int index, ITypeDatabase typeDatabase)
        {
            var existentialHandler = new ExistentialHandler(typeDatabase);
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Handle existential types
            if (existentialHandler.IsExistential(typeSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null)
                {
                    var protocolCount = protocolList.Protocols.Count;
                    csWriter.WriteLine($"elementMetadataArray[{index}] = TypeMetadata.GetExistentialTypeMetadata({protocolCount});");
                    return;
                }
            }

            // For types that implement ISwiftObject, use their static metadata accessor
            var csharpType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler);

            // Check if it's a primitive type with known metadata
            if (IsPrimitiveTypeWithKnownMetadata(csharpType))
            {
                csWriter.WriteLine($"elementMetadataArray[{index}] = TypeMetadata.GetTypeMetadataOrThrow<{csharpType}>();");
            }
            else
            {
                // Assume the type implements ISwiftObject
                csWriter.WriteLine($"elementMetadataArray[{index}] = SwiftObjectHelper<{csharpType}>.GetTypeMetadata();");
            }
        }

        /// <summary>
        /// Checks if a C# type name corresponds to a primitive type with known metadata.
        /// </summary>
        private static bool IsPrimitiveTypeWithKnownMetadata(string csharpType)
        {
            return csharpType switch
            {
                "bool" or "System.Boolean" => true,
                "sbyte" or "System.SByte" => true,
                "byte" or "System.Byte" => true,
                "short" or "System.Int16" => true,
                "ushort" or "System.UInt16" => true,
                "int" or "System.Int32" => true,
                "uint" or "System.UInt32" => true,
                "long" or "System.Int64" => true,
                "ulong" or "System.UInt64" => true,
                "nint" or "System.IntPtr" => true,
                "nuint" or "System.UIntPtr" => true,
                "float" or "System.Single" => true,
                "double" or "System.Double" => true,
                _ => false
            };
        }

        /// <summary>
        /// Emits code to marshal a payload value from Swift memory at a specific offset.
        /// </summary>
        private void EmitPayloadMarshalWithOffset(CSharpWriter csWriter, TypeSpec typeSpec, string varName, string sourcePtr, string offsetVar, ITypeDatabase typeDatabase)
        {
            var existentialHandler = new ExistentialHandler(typeDatabase);
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Get the C# type name
            var csharpType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler);

            // Handle existential types
            if (existentialHandler.IsExistential(typeSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null)
                {
                    var containerType = existentialHandler.GetCSharpExistentialType(protocolList);
                    csWriter.WriteLine($"{varName} = SwiftMarshal.MarshalFromSwift<{containerType}>(new IntPtr({sourcePtr} + (int){offsetVar}));");
                    return;
                }
            }

            csWriter.WriteLine($"{varName} = SwiftMarshal.MarshalFromSwift<{csharpType}>(new IntPtr({sourcePtr} + (int){offsetVar}));");
        }

        /// <summary>
        /// Emits code to marshal a payload value from Swift memory to a C# variable (with assignment).
        /// </summary>
        private void EmitPayloadMarshal(CSharpWriter csWriter, TypeSpec typeSpec, string varName, string sourcePtr, ITypeDatabase typeDatabase)
        {
            var existentialHandler = new ExistentialHandler(typeDatabase);
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Handle existential types
            if (existentialHandler.IsExistential(typeSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null)
                {
                    var containerType = existentialHandler.GetCSharpExistentialType(protocolList);
                    csWriter.WriteLine($"{varName} = SwiftMarshal.MarshalFromSwift<{containerType}>(new IntPtr({sourcePtr}));");
                    return;
                }
            }

            // Use GetCSharpTypeNameForEnumCase to properly handle bound generics
            var csharpType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler);
            csWriter.WriteLine($"{varName} = SwiftMarshal.MarshalFromSwift<{csharpType}>(new IntPtr({sourcePtr}));");
        }

        /// <summary>
        /// Emits code to marshal a payload value from Swift memory with a variable declaration.
        /// </summary>
        private void EmitPayloadMarshalWithDeclaration(CSharpWriter csWriter, TypeSpec typeSpec, string varName, string sourcePtr, ITypeDatabase typeDatabase)
        {
            var existentialHandler = new ExistentialHandler(typeDatabase);
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Get the C# type name for this typeSpec
            string csharpType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler);

            // Handle existential types
            if (existentialHandler.IsExistential(typeSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null)
                {
                    var containerType = existentialHandler.GetCSharpExistentialType(protocolList);
                    csWriter.WriteLine($"var {varName} = SwiftMarshal.MarshalFromSwift<{containerType}>(new IntPtr({sourcePtr}));");
                    return;
                }
            }

            var typeRecord = typeDatabase.GetTypeRecordOrAnyType(typeSpec);

            // For types requiring memory management (classes, non-frozen structs), use SwiftMarshal
            if (MarshallingHelpers.RequiresMemoryManagement(typeRecord))
            {
                csWriter.WriteLine($"var {varName} = SwiftMarshal.MarshalFromSwift<{csharpType}>(new IntPtr({sourcePtr}));");
            }
            else
            {
                // For primitives and frozen structs, marshal directly
                csWriter.WriteLine($"var {varName} = SwiftMarshal.MarshalFromSwift<{csharpType}>(new IntPtr({sourcePtr}));");
            }
        }
    }

    /// <summary>
    /// Class responsible for emitting the necessary code for ISwiftObject methods for enums.
    /// </summary>
    class EnumISwiftObjectMethodWriter
    {
        private readonly IndentedTextWriter _writer;
        private readonly ITypeDatabase _typeDatabase;
        private readonly ModuleDecl _moduleDecl;
        private readonly EnumDecl _enumDecl;
        private readonly string _typeNameWithGenerics;
        private readonly PInvokeHelperContext? _pinvokeHelperContext;

        public EnumISwiftObjectMethodWriter(CSharpWriter csWriter, ITypeDatabase typeDatabase, ModuleDecl moduleDecl, EnumDecl enumDecl, string typeNameWithGenerics, PInvokeHelperContext? pinvokeHelperContext)
        {
            _writer = csWriter;
            _typeDatabase = typeDatabase;
            _moduleDecl = moduleDecl;
            _enumDecl = enumDecl;
            _typeNameWithGenerics = typeNameWithGenerics;
            _pinvokeHelperContext = pinvokeHelperContext;
        }

        /// <summary>
        /// Writes the implementation for ISwiftObject methods for enums.
        /// </summary>
        public void WriteEnumImplementation()
        {
            WriteGetTypeMetadata();
            WriteNewFromPayload();
            WriteMarshalToSwift();
            WriteGetProtocolConformanceDescriptor();
        }

        /// <summary>
        /// Writes the GetTypeMetadata method for the enum along with the PInvoke method.
        /// </summary>
        private void WriteGetTypeMetadata()
        {
            string libPath = _typeDatabase.GetLibraryPath(_moduleDecl.Name);
            if (_pinvokeHelperContext != null)
            {
                var metadataArgs = string.Join(", ", _pinvokeHelperContext.GetMetadataArgumentList());
                _writer.WriteLine($"static TypeMetadata ISwiftObject.GetTypeMetadata() => {_pinvokeHelperContext.HelperClassName}.PInvoke_getMetadata({metadataArgs});");
                _writer.WriteLine();
                _pinvokeHelperContext.AddDeclaration(new PInvokeDeclaration
                {
                    LibraryPath = libPath,
                    EntryPoint = _enumDecl.MetadataAccessor,
                    MethodName = "PInvoke_getMetadata",
                    ReturnType = "TypeMetadata",
                    ParametersString = "",
                    IsAsync = false,
                    MetadataParameters = _pinvokeHelperContext.GetMetadataParameterDeclarations()
                });
                return;
            }

            _writer.WriteLine("static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();");
            _writer.WriteLine();

            var pinvokeText = $$"""
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("{{libPath}}", EntryPoint = "{{_enumDecl.MetadataAccessor}}")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            """;

            _writer.WriteLines(pinvokeText);
            _writer.WriteLine();
        }

        /// <summary>
        /// Writes the NewFromPayload method for the enum.
        /// </summary>
        private void WriteNewFromPayload()
        {
            var text = $$"""
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new {{_typeNameWithGenerics}}(handle);
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();

            EmitDefaultConstructor();
            EmitPrivateConstructor();
        }

        /// <summary>
        /// Writes the default private constructor (for enum case construction).
        /// </summary>
        private void EmitDefaultConstructor()
        {
            var text = $$"""
            {{_enumDecl.Name}}()
            {
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
        }

        /// <summary>
        /// Writes the private constructor accepting a SwiftHandle.
        /// </summary>
        private void EmitPrivateConstructor()
        {
            var text = $$"""
            {{_enumDecl.Name}}(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<{{_typeNameWithGenerics}}>(handle);
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
        }

        /// <summary>
        /// Writes the MarshalToSwift method for the enum.
        /// </summary>
        private void WriteMarshalToSwift()
        {
            var text = $$"""
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<{{_typeNameWithGenerics}}>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
                    bool success = false;
                    _payload.DangerousAddRef(ref success);
                    try
                    {
                        metadata.ValueWitnessTable->InitializeWithCopy(swiftDest, (void*)_payload.DangerousGetHandle(), metadata);
                        return (int)metadata.Size;
                    }
                    finally
                    {
                        if (success)
                            _payload.DangerousRelease();
                    }
                }
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
        }

        /// <summary>
        /// Writes the GetProtocolConformanceDescriptor method for the enum.
        /// </summary>
        private void WriteGetProtocolConformanceDescriptor()
        {
            WriteStaticConstructor();
            var libPath = _typeDatabase.GetLibraryPath(_moduleDecl.Name);
            var text = $$"""
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type {{_enumDecl.Name}} and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }

                return ProtocolConformanceDescriptor.LoadFromSymbol("{{libPath}}", symbolName);
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
        }

        /// <summary>
        /// Writes the static constructor for the enum.
        /// </summary>
        private void WriteStaticConstructor()
        {
            var text = $$"""
            private static Dictionary<Type, string> _protocolConformanceSymbols;

            static {{_enumDecl.Name}}()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {{GenerateGetProtocolConformanceDictionaryEntries()}}
                };
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
        }

        private string GenerateGetProtocolConformanceDictionaryEntries()
        {
            return ProtocolConformanceHelper.GenerateProtocolConformanceDictionaryEntries(
                _enumDecl.Conformances,
                _moduleDecl.Name,
                _enumDecl.Name,
                _typeDatabase);
        }
    }
}
