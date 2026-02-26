// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Handles emission of Swift subscripts as C# indexers on concrete types.
    /// Follows the PropertyHandler pattern: accessor methods are emitted via MethodHandler,
    /// then wrapped in indexer syntax with factory-projected types and getter/setter conversion.
    /// </summary>
    internal static class SubscriptHandler
    {
        private static readonly TypeProjectionFactory s_projectionFactory = new();

        /// <summary>
        /// Emits subscripts as C# indexers for a concrete type.
        /// </summary>
        public static void EmitSubscripts(
            CSharpWriter csWriter,
            SwiftWriter swiftWriter,
            TypeDecl typeDecl,
            ITypeDatabase typeDatabase,
            Conductor conductor,
            TypeHandlerContext context,
            ILogger logger)
        {
            var subscripts = typeDecl.Subscripts;
            if (subscripts == null || subscripts.Count == 0)
                return;

            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);
            var emittedKeys = new HashSet<string>();

            foreach (var subscriptDecl in subscripts)
            {
                // Skip static subscripts (not supported as indexers)
                if (subscriptDecl.IsStatic)
                {
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Subscript, "subscript", typeDecl,
                        SkipReason.StaticProtocolMember, "Static subscripts cannot be emitted as C# indexers.");
                    continue;
                }

                // Skip subscripts referencing unsupported modules (SwiftUI, Combine)
                if (MemberEmissionValidator.ReferencesUnsupportedModule(subscriptDecl.ReturnTypeSpec) ||
                    subscriptDecl.IndexParameters.Any(p => MemberEmissionValidator.ReferencesUnsupportedModule(p.SwiftTypeSpec)))
                {
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Subscript, "subscript", typeDecl,
                        SkipReason.SwiftUIConstraint, "Subscript signature references unsupported module.");
                    continue;
                }

                // Skip if return type or any parameter resolves to AnyType
                var returnTypeName = ResolveSubscriptTypeName(subscriptDecl.ReturnTypeSpec, typeDatabase, boundGenericsHandler, isParameter: false);
                if (returnTypeName.Contains("AnyType"))
                {
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Subscript, "subscript", typeDecl,
                        SkipReason.AnyTypeFallback, "Subscript return type resolved to AnyType.");
                    continue;
                }

                bool hasAnyTypeParam = false;
                bool hasComplexIndexParam = false;
                var paramInfos = new List<(string typeName, string paramName, ITypeProjection? projection)>();
                NameProvider.DeduplicateParameterNamesForParameterList(subscriptDecl.IndexParameters);
                foreach (var param in subscriptDecl.IndexParameters)
                {
                    var paramTypeName = ResolveSubscriptTypeName(param.SwiftTypeSpec, typeDatabase, boundGenericsHandler, isParameter: true);
                    if (paramTypeName.Contains("AnyType"))
                    {
                        hasAnyTypeParam = true;
                        break;
                    }
                    // Skip subscripts with index parameters that have projections requiring
                    // complex conversion (dictionary, existential, array, optional). Only
                    // StringProjection and NativeRemappedProjection have simple conversions
                    // handled by BuildIndexParamConversions.
                    var paramProj = s_projectionFactory.Project(param.SwiftTypeSpec,
                        new ProjectionContext { TypeDatabase = typeDatabase, IsParameter = true });
                    if (paramProj is DictionaryProjection or ExistentialProjection or ArrayProjection or OptionalProjection)
                    {
                        hasComplexIndexParam = true;
                        break;
                    }
                    paramInfos.Add((paramTypeName, NameProvider.GetCSharpParameterName(param), paramProj));
                }
                if (hasAnyTypeParam)
                {
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Subscript, "subscript", typeDecl,
                        SkipReason.AnyTypeFallback, "Subscript index parameter resolved to AnyType.");
                    continue;
                }
                if (hasComplexIndexParam)
                {
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Subscript, "subscript", typeDecl,
                        SkipReason.UnsupportedSignature, "Subscript index parameter requires conversion not supported in indexer body.");
                    continue;
                }

                // Dedup by signature key
                var key = string.Join(",", paramInfos.Select(p => p.typeName));
                if (!emittedKeys.Add(key))
                {
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Subscript, "subscript", typeDecl,
                        SkipReason.DuplicateSignature, "Duplicate subscript signature.");
                    continue;
                }

                // Preflight accessor methods — ensure all can be emitted
                bool allAccessorsValid = true;
                foreach (var accessor in subscriptDecl.Accessors)
                {
                    if (!conductor.TryGetMethodHandler(accessor.Method, out var methodHandler))
                    {
                        allAccessorsValid = false;
                        break;
                    }

                    accessor.Method.IsAccessor = true;
                    var accessorEnv = (MethodEnvironment)methodHandler.Marshal(accessor.Method, typeDatabase);
                    if (context.PInvokeHelperContext != null && accessorEnv.PInvokeHelperContext == null)
                    {
                        accessorEnv = new MethodEnvironment(accessorEnv.MethodDecl, accessorEnv.TypeDatabase,
                            accessorEnv.SiblingPropertyNames, context.PInvokeHelperContext, context.CompositionCollector);
                    }

                    var signatureHandler = new SignatureHandler(accessorEnv);
                    if (signatureHandler.GetWrapperSignature().ContainsPlaceholder)
                    {
                        allAccessorsValid = false;
                        break;
                    }

                    // Skip subscripts whose accessors would trigger Swift wrapper generation
                    // from emitters that don't yet support subscript syntax (`__self[index]`).
                    // OptionalPointerWrapperEmitter now supports instance subscripts, so large Optional
                    // params/returns are allowed through for instance accessors. Static subscripts
                    // with large optionals are still blocked (emitter only handles instance syntax).
                    bool hasOpaqueReturn = accessor.Method.CSSignature.Count > 0 &&
                        accessor.Method.CSSignature[0].SwiftTypeSpec is ProtocolListTypeSpec { IsOpaque: true };
                    bool isStaticWithLargeOptional = accessor.Method.MethodType == MethodType.Static &&
                        (accessorEnv.BoundGenericsHandler.HasLargeOptionalParams(accessor.Method) ||
                         accessorEnv.BoundGenericsHandler.IsLargeOptionalReturn(accessor.Method));
                    if (DefaultParameterOverloadEmitter.HasDebugParameters(accessor.Method) ||
                        hasOpaqueReturn ||
                        isStaticWithLargeOptional ||
                        accessor.Method.IsAsync)
                    {
                        allAccessorsValid = false;
                        break;
                    }
                }
                if (!allAccessorsValid)
                {
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Subscript, "subscript", typeDecl,
                        SkipReason.UnsupportedSignature, "Subscript accessor would trigger Swift wrapper with incompatible call syntax.");
                    continue;
                }

                // Emit accessor methods via MethodHandler
                foreach (var accessor in subscriptDecl.Accessors)
                {
                    if (conductor.TryGetMethodHandler(accessor.Method, out var methodHandler))
                    {
                        accessor.Method.IsAccessor = true;
                        var accessorEnv = (MethodEnvironment)methodHandler.Marshal(accessor.Method, typeDatabase);
                        if (context.PInvokeHelperContext != null && accessorEnv.PInvokeHelperContext == null)
                        {
                            accessorEnv = new MethodEnvironment(accessorEnv.MethodDecl, accessorEnv.TypeDatabase,
                                accessorEnv.SiblingPropertyNames, context.PInvokeHelperContext, context.CompositionCollector);
                        }
                        if (context.CompositionCollector != null)
                            accessorEnv.ExistentialHandler.SetCompositionCollector(context.CompositionCollector);
                        methodHandler.Emit(csWriter, swiftWriter, accessorEnv, conductor, context);
                    }
                }

                // Emit indexer declaration
                EmitIndexer(csWriter, subscriptDecl, typeDatabase, returnTypeName, paramInfos);
                ReportCollector.RecordMemberEmitted(BindingItemKind.Subscript, "subscript", typeDecl);
            }
        }

        /// <summary>
        /// Emits the C# indexer syntax with getter/setter bodies that call the accessor methods.
        /// </summary>
        private static void EmitIndexer(
            CSharpWriter csWriter,
            SubscriptDecl subscriptDecl,
            ITypeDatabase typeDatabase,
            string returnTypeName,
            List<(string typeName, string paramName, ITypeProjection? projection)> paramInfos)
        {
            var paramList = string.Join(", ", paramInfos.Select(p => $"{p.typeName} {p.paramName}"));

            // Emit [UnsupportedSwiftType] if needed
            var closureHandler = new ClosureHandler(typeDatabase);
            if (UnsupportedSwiftTypeSupport.TryFindFallbackInfo(typeDatabase, closureHandler, subscriptDecl.ReturnTypeSpec, out var fallbackInfo))
            {
                UnsupportedSwiftTypeSupport.EmitAttribute(csWriter, fallbackInfo);
            }

            csWriter.WriteLine($"public {returnTypeName} this[{paramList}]");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            var getter = subscriptDecl.Accessors.OfType<GetAccessorDecl>().FirstOrDefault();
            if (getter != null)
            {
                EmitIndexerGetter(csWriter, getter, subscriptDecl, typeDatabase, paramInfos);
            }

            var setter = subscriptDecl.Accessors.OfType<SetAccessorDecl>().FirstOrDefault();
            if (setter != null)
            {
                EmitIndexerSetter(csWriter, setter, subscriptDecl, typeDatabase, paramInfos);
            }

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();
        }

        private static void EmitIndexerGetter(
            CSharpWriter csWriter,
            GetAccessorDecl getter,
            SubscriptDecl subscriptDecl,
            ITypeDatabase typeDatabase,
            List<(string typeName, string paramName, ITypeProjection? projection)> paramInfos)
        {
            var methodName = NameProvider.GetMethodName(getter.Method.Name, null);
            var (convertedArgs, setupLines, usingLines) = BuildIndexParamConversions(paramInfos);
            var args = convertedArgs;

            var retProjection = s_projectionFactory.Project(subscriptDecl.ReturnTypeSpec,
                new ProjectionContext { TypeDatabase = typeDatabase, IsParameter = false });

            // If any index param needs conversion, we must use block form
            bool hasParamConversion = setupLines.Count > 0 || usingLines.Count > 0;

            if (retProjection != null)
            {
                var (conv, requiresDisposal) = GetAccessorGetterConversion(retProjection, $"{methodName}({args})");
                if (conv != null)
                {
                    if (hasParamConversion || requiresDisposal)
                    {
                        var (usingConv, _) = GetAccessorGetterConversion(retProjection, "__ret");
                        csWriter.Write("get { ");
                        foreach (var line in usingLines) csWriter.Write($"{line} ");
                        foreach (var line in setupLines) csWriter.Write($"{line} ");
                        if (requiresDisposal)
                            csWriter.WriteLine($"using var __ret = {methodName}({args}); return {usingConv}; }}");
                        else
                            csWriter.WriteLine($"return {conv}; }}");
                    }
                    else
                    {
                        csWriter.WriteLine($"get => {conv};");
                    }
                    return;
                }
            }
            if (hasParamConversion)
            {
                csWriter.Write("get { ");
                foreach (var line in usingLines) csWriter.Write($"{line} ");
                foreach (var line in setupLines) csWriter.Write($"{line} ");
                csWriter.WriteLine($"return {methodName}({args}); }}");
            }
            else
            {
                csWriter.WriteLine($"get => {methodName}({args});");
            }
        }

        private static void EmitIndexerSetter(
            CSharpWriter csWriter,
            SetAccessorDecl setter,
            SubscriptDecl subscriptDecl,
            ITypeDatabase typeDatabase,
            List<(string typeName, string paramName, ITypeProjection? projection)> paramInfos)
        {
            var methodName = NameProvider.GetMethodName(setter.Method.Name, null);
            var (convertedArgs, setupLines, usingLines) = BuildIndexParamConversions(paramInfos);
            var args = convertedArgs;

            var retProjection = s_projectionFactory.Project(subscriptDecl.ReturnTypeSpec,
                new ProjectionContext { TypeDatabase = typeDatabase, IsParameter = true });
            if (retProjection != null)
            {
                var (conv, requiresDisposal) = GetAccessorSetterConversion(retProjection, "value");
                if (conv != null)
                {
                    // Always block form when we have value conversion or param conversion
                    csWriter.Write("set { ");
                    foreach (var line in usingLines) csWriter.Write($"{line} ");
                    foreach (var line in setupLines) csWriter.Write($"{line} ");
                    if (requiresDisposal)
                    {
                        csWriter.WriteLine($"using var __val = {conv}; {methodName}(__val, {args}); }}");
                    }
                    else
                    {
                        csWriter.WriteLine($"{methodName}({conv}, {args}); }}");
                    }
                    return;
                }
            }
            if (setupLines.Count > 0 || usingLines.Count > 0)
            {
                csWriter.Write("set { ");
                foreach (var line in usingLines) csWriter.Write($"{line} ");
                foreach (var line in setupLines) csWriter.Write($"{line} ");
                csWriter.WriteLine($"{methodName}(value, {args}); }}");
            }
            else
            {
                csWriter.WriteLine($"set => {methodName}(value, {args});");
            }
        }

        /// <summary>
        /// Builds conversion code for index parameters that need type conversion
        /// (e.g., string → SwiftString) when passing from the idiomatic public indexer
        /// signature to the raw-typed accessor method.
        /// </summary>
        private static (string convertedArgs, List<string> setupLines, List<string> usingLines) BuildIndexParamConversions(
            List<(string typeName, string paramName, ITypeProjection? projection)> paramInfos)
        {
            var argParts = new List<string>();
            var setupLines = new List<string>();
            var usingLines = new List<string>();

            foreach (var (_, paramName, proj) in paramInfos)
            {
                if (proj is StringProjection)
                {
                    var convertedName = $"__{paramName}Swift";
                    usingLines.Add($"using var {convertedName} = new SwiftString({paramName});");
                    argParts.Add(convertedName);
                }
                else if (proj is NativeRemappedProjection nrp)
                {
                    var convertedName = $"__{paramName}Swift";
                    var convExpr = nrp.FromFactoryMethod != null
                        ? $"{nrp.SwiftWrapperType}.{nrp.FromFactoryMethod}({paramName})"
                        : $"new {nrp.SwiftWrapperType}({paramName})";
                    if (nrp.RequiresDisposal)
                        usingLines.Add($"using var {convertedName} = {convExpr};");
                    else
                        setupLines.Add($"var {convertedName} = {convExpr};");
                    argParts.Add(convertedName);
                }
                else
                {
                    argParts.Add(paramName);
                }
            }

            return (string.Join(", ", argParts), setupLines, usingLines);
        }

        /// <summary>
        /// Resolves a TypeSpec to a C# type name for subscript signatures.
        /// Uses factory projection first (idiomatic types), falls back to raw type translation.
        /// </summary>
        private static string ResolveSubscriptTypeName(
            TypeSpec typeSpec,
            ITypeDatabase typeDatabase,
            BoundGenericsHandler boundGenericsHandler,
            bool isParameter)
        {
            // Try factory projection first for idiomatic types
            var projection = s_projectionFactory.Project(typeSpec,
                new ProjectionContext { TypeDatabase = typeDatabase, IsParameter = isParameter });
            if (projection != null)
                return projection.PublicType;

            // Check bound generics
            if (typeSpec is NamedTypeSpec namedType && namedType.ContainsGenericParameters)
            {
                return boundGenericsHandler.TranslateBoundGenericTypeToCSharp(typeSpec, GenericContext.Empty);
            }

            // Fall back to raw type translation
            if (typeDatabase.TryGetTypeRecord(typeSpec, out var record))
                return record.CSharpTypeName.FullyQualifiedName;

            return typeSpec.ToString();
        }

        // Getter/setter conversion helpers — same logic as PropertyHandler

        private static (string? conversion, bool requiresDisposal) GetAccessorGetterConversion(
            ITypeProjection projection, string resultExpr)
        {
            return projection switch
            {
                StringProjection => ($"{resultExpr}.ToString()", true),
                NativeRemappedProjection nrp => ($"{resultExpr}.{nrp.ToConversionMethod}()", nrp.RequiresDisposal),
                OptionalProjection opt => GetOptionalAccessorGetterConversion(opt, resultExpr),
                ArrayProjection arr => GetArrayAccessorGetterConversion(arr, resultExpr),
                DictionaryProjection dict => GetDictAccessorGetterConversion(dict, resultExpr),
                _ => (null, false)
            };
        }

        private static (string? conversion, bool requiresDisposal) GetOptionalAccessorGetterConversion(
            OptionalProjection opt, string resultExpr)
        {
            var inner = opt.InnerProjection;
            return inner switch
            {
                StringProjection => ($"((SwiftString?){resultExpr})?.ToString()", true),
                ArrayProjection arr => GetOptionalContainerGetterConversion(arr, resultExpr),
                DictionaryProjection dict => GetOptionalContainerGetterConversion(dict, resultExpr),
                NativeRemappedProjection nrp => ($"(({nrp.SwiftWrapperType}?){resultExpr})?.{nrp.ToConversionMethod}()", true),
                ClosureProjection => (null, false),
                // Existentials, classes, non-frozen structs: accessor already returns
                // the projected type — no conversion or disposal needed.
                ExistentialProjection or ClassProjection or NonFrozenStructProjection => (null, false),
                _ => ($"(({inner.PublicType}?){resultExpr})", true)
            };
        }

        private static (string? conversion, bool requiresDisposal) GetOptionalContainerGetterConversion(
            ITypeProjection innerContainer, string resultExpr)
        {
            var innerHasConversion = innerContainer switch
            {
                ArrayProjection arr => arr.ElementProjection.GetReturnElementConversion("e") != null,
                DictionaryProjection dict => dict.KeyProjection.GetReturnElementConversion("k") != null
                    || dict.ValueProjection.GetReturnElementConversion("v") != null,
                _ => false
            };
            var idiomaticType = innerContainer.PublicType;
            var someExpr = innerHasConversion
                ? innerContainer.GetReturnContainerConversion($"{resultExpr}.Some") ?? $"{resultExpr}.Some"
                : $"{resultExpr}.Some";
            return ($"({resultExpr}.Case == Swift.SwiftOptionalCases.None ? ({idiomaticType}?)null : {someExpr})", true);
        }

        private static (string? conversion, bool requiresDisposal) GetArrayAccessorGetterConversion(
            ArrayProjection arr, string resultExpr)
        {
            var elemConv = arr.ElementProjection.GetReturnElementConversion("e");
            if (elemConv != null)
                return ($"{resultExpr}.AsProjected(e => {elemConv})", false);
            return (null, false);
        }

        private static (string? conversion, bool requiresDisposal) GetDictAccessorGetterConversion(
            DictionaryProjection dict, string resultExpr)
        {
            var keyConv = dict.KeyProjection.GetReturnElementConversion("k");
            var valueConv = dict.ValueProjection.GetReturnElementConversion("v");
            if (keyConv != null || valueConv != null)
                return ($"{resultExpr}.AsProjected(k => {keyConv ?? "k"}, v => {valueConv ?? "v"})", false);
            return (null, false);
        }

        private static (string? conversion, bool requiresDisposal) GetAccessorSetterConversion(
            ITypeProjection projection, string valueExpr)
        {
            return projection switch
            {
                StringProjection => ($"new SwiftString({valueExpr})", true),
                NativeRemappedProjection nrp => (
                    nrp.FromFactoryMethod != null
                        ? $"{nrp.SwiftWrapperType}.{nrp.FromFactoryMethod}({valueExpr})"
                        : $"new {nrp.SwiftWrapperType}({valueExpr})",
                    nrp.RequiresDisposal),
                ArrayProjection arr => GetArrayAccessorSetterConversion(arr, valueExpr),
                DictionaryProjection dict => GetDictAccessorSetterConversion(dict, valueExpr),
                OptionalProjection opt => GetOptionalAccessorSetterConversion(opt, valueExpr),
                _ => (null, false)
            };
        }

        private static (string? conversion, bool requiresDisposal) GetArrayAccessorSetterConversion(
            ArrayProjection arr, string valueExpr)
        {
            var rawElem = arr.ElementProjection.MarshalFromSwiftType;
            var elemConv = arr.ElementProjection is ClassProjection or NonFrozenStructProjection
                ? null
                : arr.ElementProjection.GetParameterElementConversion("e");
            if (elemConv != null)
                return ($"SwiftArray<{rawElem}>.FromEnumerable({valueExpr}.Select(e => {elemConv}))", true);
            return ($"SwiftArray<{rawElem}>.FromEnumerable({valueExpr})", true);
        }

        private static (string? conversion, bool requiresDisposal) GetDictAccessorSetterConversion(
            DictionaryProjection dict, string valueExpr)
        {
            var rawK = dict.KeyProjection.MarshalFromSwiftType;
            var rawV = dict.ValueProjection.MarshalFromSwiftType;
            var keyConv = dict.KeyProjection is ClassProjection or NonFrozenStructProjection
                ? null
                : dict.KeyProjection.GetParameterElementConversion("kvp.Key");
            var valConv = dict.ValueProjection is ClassProjection or NonFrozenStructProjection
                ? null
                : dict.ValueProjection.GetParameterElementConversion("kvp.Value");
            if (keyConv != null || valConv != null)
            {
                var keyExpr = keyConv ?? "kvp.Key";
                var valExpr = valConv ?? "kvp.Value";
                return ($"SwiftDictionary<{rawK}, {rawV}>.FromDictionary({valueExpr}.Select(kvp => new KeyValuePair<{rawK}, {rawV}>({keyExpr}, {valExpr})))", true);
            }
            return ($"SwiftDictionary<{rawK}, {rawV}>.FromDictionary({valueExpr})", true);
        }

        private static (string? conversion, bool requiresDisposal) GetOptionalAccessorSetterConversion(
            OptionalProjection opt, string valueExpr)
        {
            var inner = opt.InnerProjection;

            if (inner is ClosureProjection)
                return (null, false);

            var optType = inner.MarshalFromSwiftType;

            if (inner is ArrayProjection arr)
            {
                var (arrConv, _) = GetArrayAccessorSetterConversion(arr, $"{valueExpr}Val");
                return ($"({valueExpr} is {{}} {valueExpr}Val ? SwiftOptional<{optType}>.NewSome({arrConv}) : SwiftOptional<{optType}>.NewNone())", true);
            }
            if (inner is DictionaryProjection dict)
            {
                var (dictConv, _) = GetDictAccessorSetterConversion(dict, $"{valueExpr}Val");
                return ($"({valueExpr} is {{}} {valueExpr}Val ? SwiftOptional<{optType}>.NewSome({dictConv}) : SwiftOptional<{optType}>.NewNone())", true);
            }

            if (inner is ClassProjection or NonFrozenStructProjection or ExistentialProjection)
                return (null, false);

            var innerConv = inner.GetParameterElementConversion($"{valueExpr}Val");
            if (innerConv != null)
                return ($"({valueExpr} is {{}} {valueExpr}Val ? SwiftOptional<{optType}>.NewSome({innerConv}) : SwiftOptional<{optType}>.NewNone())", true);

            return ($"({valueExpr} is {{}} {valueExpr}Val ? SwiftOptional<{optType}>.NewSome({valueExpr}Val) : SwiftOptional<{optType}>.NewNone())", true);
        }
    }
}
