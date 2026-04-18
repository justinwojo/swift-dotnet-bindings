// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("Swift.Bindings.Unit.Tests")]

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
            // Collect convenience indexer overload candidates during the primary loop,
            // then emit them after all primary indexers are processed. This ensures
            // primary indexers always take precedence over convenience nint→int overloads.
            var convenienceCandidates = new List<(SubscriptDecl decl, string returnTypeName, List<(string typeName, string paramName, ITypeProjection? projection)> paramInfos)>();

            foreach (var subscriptDecl in subscripts)
            {
                // Skip static subscripts (not supported as indexers)
                if (subscriptDecl.IsStatic)
                {
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Subscript, "subscript", typeDecl,
                        SkipReason.StaticProtocolMember, "Static subscripts cannot be emitted as C# indexers.");
                    continue;
                }

                // Skip subscripts referencing unsupported modules (SwiftUI, Combine) unless registered in type database
                if (MemberEmissionValidator.ReferencesUnsupportedModule(subscriptDecl.ReturnTypeSpec, typeDatabase) ||
                    subscriptDecl.IndexParameters.Any(p => MemberEmissionValidator.ReferencesUnsupportedModule(p.SwiftTypeSpec, typeDatabase)))
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
                    // complex conversion (dictionary, existential, array, set, optional). Only
                    // StringProjection and NativeRemappedProjection have simple conversions
                    // handled by BuildIndexParamConversions.
                    var paramProj = s_projectionFactory.Project(param.SwiftTypeSpec,
                        new ProjectionContext { TypeDatabase = typeDatabase, IsParameter = true });
                    if (paramProj is DictionaryProjection or ExistentialProjection or ArrayProjection or OptionalProjection or SetProjection)
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

                // Determine per-accessor thunk and @_cdecl eligibility
                var accessorThunkFlags = new Dictionary<AccessorDecl, bool>();
                var accessorCdeclFlags = new Dictionary<AccessorDecl, bool>();
                foreach (var accessor in subscriptDecl.Accessors)
                {
                    bool thunkEligible = false;
                    bool cdeclEligible = false;
                    if (typeDecl.SwiftTypeName != null && conductor.TryGetMethodHandler(accessor.Method, out var checkHandler))
                    {
                        accessor.Method.IsAccessor = true;
                        var checkEnv = (MethodEnvironment)checkHandler.Marshal(accessor.Method, typeDatabase);
                        // Thunk takes priority over @_cdecl
                        thunkEligible = NativeThunkEmitter.ShouldEmitThunk(checkEnv);
                        if (!thunkEligible)
                            cdeclEligible = SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscriptDecl, accessor, checkEnv);
                    }
                    accessorThunkFlags[accessor] = thunkEligible;
                    accessorCdeclFlags[accessor] = cdeclEligible;
                }

                // Track subscript wrapper strategy and skip reasons for emission report (per accessor).
                if (WrapperValidation.IsXCFrameworkMode(typeDatabase))
                {
                    foreach (var acc in subscriptDecl.Accessors)
                    {
                        if (accessorThunkFlags.TryGetValue(acc, out var thunk) && thunk)
                        {
                            context.GetEmissionContext().IncrementWrapperStrategy("NativeThunk");
                        }
                        else if (accessorCdeclFlags.TryGetValue(acc, out var cdecl) && cdecl)
                        {
                            context.GetEmissionContext().IncrementWrapperStrategy("CdeclSubscript");
                        }
                        else
                        {
                            context.GetEmissionContext().IncrementWrapperStrategy("DirectCdecl");
                            if (conductor.TryGetMethodHandler(acc.Method, out var skipCheckHandler))
                            {
                                var skipCheckEnv = (MethodEnvironment)skipCheckHandler.Marshal(acc.Method, typeDatabase);
                                var skipReason = SubscriptWrapperEmitter.GetRejectionReason(subscriptDecl, acc, skipCheckEnv);
                                if (skipReason != null)
                                    context.GetEmissionContext().IncrementWrapperSkipReason(skipReason);
                            }
                        }
                    }
                }

                // Emit accessor methods via MethodHandler
                foreach (var accessor in subscriptDecl.Accessors)
                {
                    if (conductor.TryGetMethodHandler(accessor.Method, out var methodHandler))
                    {
                        accessor.Method.IsAccessor = true;

                        // Native ARM64 thunk: set flags BEFORE Marshal/Emit.
                        // If EmitThunk fails, revert flags and fall through to @_cdecl path.
                        bool thunkHandled = false;
                        if (accessorThunkFlags.TryGetValue(accessor, out var useThunk) && useThunk &&
                            typeDecl.SwiftTypeName != null)
                        {
                            var originalMangledName = accessor.Method.MangledName;
                            var thunkSymbol = NativeThunkEmitter.GetThunkSymbol(accessor.Method, typeDecl.SwiftTypeName.Module);
                            accessor.Method.WrapperStrategy = WrapperStrategy.NativeThunk;
                            accessor.Method.IsSubscriptAccessor = true;
                            accessor.Method.UsesWrapperLibrary = true;
                            accessor.Method.MangledName = thunkSymbol;

                            // Emit thunk assembly — pass the original mangled name since MangledName
                            // has been overwritten with the thunk symbol above
                            var thunkEnv = (MethodEnvironment)methodHandler.Marshal(accessor.Method, typeDatabase);
                            bool emitted = NativeThunkEmitter.EmitThunk(thunkEnv, typeDecl.SwiftTypeName.Module, context.GetEmissionContext().AssemblyBuilder, originalMangledName);
                            if (emitted)
                            {
                                // Mark as emitted to prevent duplicate emission in MethodHandler.Emit
                                accessor.Method.ThunkAssemblyEmitted = true;
                                thunkHandled = true;
                            }
                            else
                            {
                                // Revert thunk state — fall through to @_cdecl path below
                                accessor.Method.WrapperStrategy = WrapperStrategy.None;
                                accessor.Method.IsSubscriptAccessor = false;
                                accessor.Method.UsesWrapperLibrary = false;
                                accessor.Method.MangledName = originalMangledName;
                            }
                        }
                        // @_cdecl subscript wrapper: set flags BEFORE Marshal/Emit
                        // Also fires as fallback when thunk emission fails above — in that case,
                        // accessorCdeclFlags may not have been computed (only set when thunk was
                        // rejected upfront), so we re-evaluate eligibility on-the-fly.
                        bool cdeclEligible = accessorCdeclFlags.TryGetValue(accessor, out var useCdecl) && useCdecl;
                        if (!thunkHandled && !cdeclEligible && !accessor.Method.UsesCdeclPropertyWrapper)
                        {
                            // Thunk failed at emission time — check @_cdecl eligibility now
                            if (conductor.TryGetMethodHandler(accessor.Method, out var fallbackHandler))
                            {
                                var fallbackEnv = (MethodEnvironment)fallbackHandler.Marshal(accessor.Method, typeDatabase);
                                cdeclEligible = SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscriptDecl, accessor, fallbackEnv);
                                if (cdeclEligible)
                                    accessorCdeclFlags[accessor] = true; // Update for downstream bookkeeping (SBW_Free, etc.)
                            }
                        }
                        if (!thunkHandled && cdeclEligible &&
                            typeDecl.SwiftTypeName != null)
                        {
                            bool isGetter = accessor is GetAccessorDecl;
                            var symbol = SubscriptWrapperEmitter.GetSubscriptAccessorSymbolName(
                                typeDecl.SwiftTypeName.Module, typeDecl.Name, accessor.Method.MangledName, isGetter);

                            accessor.Method.UsesCdeclPropertyWrapper = true;
                            accessor.Method.IsSubscriptAccessor = true;
                            accessor.Method.UsesWrapperLibrary = true;
                            accessor.Method.UsesFreeFunctionWrapper = true;
                            accessor.Method.MangledName = symbol;

                            var cdeclEnv = (MethodEnvironment)methodHandler.Marshal(accessor.Method, typeDatabase);
                            if (isGetter)
                                SubscriptWrapperEmitter.EmitSwiftSubscriptGetterWrapper(
                                    swiftWriter, subscriptDecl, symbol, cdeclEnv, context.GetEmissionContext());
                            else
                                SubscriptWrapperEmitter.EmitSwiftSubscriptSetterWrapper(
                                    swiftWriter, subscriptDecl, symbol, cdeclEnv, context.GetEmissionContext());
                        }

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

                // @_cdecl subscript wrapper: emit SBW_Free P/Invoke for string returns (once per type)
                if (accessorCdeclFlags.Values.Any(v => v) && WitnessDispatchEmitter.IsStringType(subscriptDecl.ReturnTypeSpec))
                {
                    var typeKey = typeDecl.SwiftTypeName?.ModuleQualifiedName ?? "";
                    if (!Utf8SliceEmitter.HasFreePInvokeForType(typeKey, context.GetEmissionContext()))
                    {
                        Utf8SliceEmitter.MarkFreePInvokeEmittedForType(typeKey, context.GetEmissionContext());
                        var moduleName = typeDecl.SwiftTypeName?.Module ?? typeDecl.ModuleDecl?.Name ?? "";
                        var wrapperLibPath = typeDatabase.AsyncLibraryName
                            ?? typeDatabase.GetLibraryPath(moduleName);
                        var freeSymbol = Utf8SliceEmitter.GetFreeSymbolName(moduleName);
                        if (context.PInvokeHelperContext != null)
                        {
                            context.PInvokeHelperContext.AddDeclaration(new PInvokeDeclaration
                            {
                                LibraryPath = wrapperLibPath,
                                EntryPoint = freeSymbol,
                                MethodName = "SBW_Free",
                                ReturnType = "void",
                                ParametersString = "IntPtr ptr",
                                UsePrivateVisibility = false,
                            });
                        }
                        else
                        {
                            csWriter.WriteLine($"[LibraryImport(\"{wrapperLibPath}\", EntryPoint = \"{freeSymbol}\")]");
                            csWriter.WriteLine("private static partial void SBW_Free(IntPtr ptr);");
                            csWriter.WriteLine();
                        }
                    }
                }

                // Emit indexer declaration
                var subscriptHelperPrefix = context.PInvokeHelperContext != null ? $"{context.PInvokeHelperContext.HelperClassName}." : "";
                EmitIndexer(csWriter, subscriptDecl, typeDatabase, returnTypeName, paramInfos, subscriptHelperPrefix);

                // Collect candidate for convenience int/uint overload (deferred to second pass)
                convenienceCandidates.Add((subscriptDecl, returnTypeName, paramInfos));

                ReportCollector.RecordMemberEmitted(BindingItemKind.Subscript, "subscript", typeDecl);
            }

            // Second pass: emit convenience int/uint indexer overloads for nint/nuint params.
            // Deferred so primary indexers always take precedence — a real this[int] primary
            // indexer won't be shadowed by a convenience nint→int overload from an earlier subscript.
            foreach (var (decl, retType, pInfos) in convenienceCandidates)
            {
                NativeIntOverloadEmitter.TryEmitIndexerOverload(csWriter, decl, retType, pInfos, emittedKeys);
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
            List<(string typeName, string paramName, ITypeProjection? projection)> paramInfos,
            string helperPrefix = "")
        {
            var paramList = string.Join(", ", paramInfos.Select(p => $"{p.typeName} {p.paramName}"));

            // Emit [UnsupportedSwiftType] if needed
            var closureHandler = new ClosureHandler(typeDatabase);
            if (UnsupportedSwiftTypeSupport.TryFindFallbackInfo(typeDatabase, closureHandler, subscriptDecl.ReturnTypeSpec, out var fallbackInfo))
            {
                UnsupportedSwiftTypeSupport.EmitAttribute(csWriter, fallbackInfo);
            }

            AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, subscriptDecl, subscriptDecl.ParentDecl, emitObsolete: true);
            csWriter.WriteLine($"public {returnTypeName} this[{paramList}]");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            var getter = subscriptDecl.Accessors.OfType<GetAccessorDecl>().FirstOrDefault();
            if (getter != null)
            {
                EmitIndexerGetter(csWriter, getter, subscriptDecl, typeDatabase, paramInfos, helperPrefix);
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
            List<(string typeName, string paramName, ITypeProjection? projection)> paramInfos,
            string helperPrefix = "")
        {
            var methodName = NameProvider.GetMethodName(getter.Method.Name, null);
            bool isCdecl = getter.Method.UsesCdeclPropertyWrapper;
            var (convertedArgs, setupLines, usingLines) = BuildIndexParamConversions(paramInfos, isCdecl);
            var args = convertedArgs;
            bool hasStringIndexParam = isCdecl && paramInfos.Any(p => p.projection is StringProjection);

            // @_cdecl subscript wrapper: String getters return SBW_Utf8Slice → decode to string
            if (isCdecl && WitnessDispatchEmitter.IsStringType(subscriptDecl.ReturnTypeSpec))
            {
                EmitCdeclGetterWithFixedBlock(csWriter, methodName, args, setupLines, usingLines,
                    paramInfos, hasStringIndexParam, returnProjection: null, isStringReturn: true, helperPrefix: helperPrefix);
                return;
            }

            var retProjection = s_projectionFactory.Project(subscriptDecl.ReturnTypeSpec,
                new ProjectionContext { TypeDatabase = typeDatabase, IsParameter = false });

            // @_cdecl subscript wrapper with string index params: wrap call in unsafe fixed block
            // Pass retProjection so element type conversion (e.g. SwiftString→string) is applied.
            if (hasStringIndexParam)
            {
                EmitCdeclGetterWithFixedBlock(csWriter, methodName, args, setupLines, usingLines,
                    paramInfos, hasStringIndexParam, returnProjection: retProjection, isStringReturn: false, helperPrefix: helperPrefix);
                return;
            }

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
            bool isCdecl = setter.Method.UsesCdeclPropertyWrapper;
            var (convertedArgs, setupLines, usingLines) = BuildIndexParamConversions(paramInfos, isCdecl);
            var args = convertedArgs;
            bool hasStringIndexParam = isCdecl && paramInfos.Any(p => p.projection is StringProjection);

            // @_cdecl subscript wrapper: String setters encode to UTF-8 bytes, pin, pass pointer+length
            if (isCdecl && WitnessDispatchEmitter.IsStringType(subscriptDecl.ReturnTypeSpec))
            {
                EmitCdeclSetterWithFixedBlock(csWriter, methodName, args, setupLines, usingLines,
                    paramInfos, hasStringIndexParam, valueProjection: null, isStringValue: true);
                return;
            }

            var retProjection = s_projectionFactory.Project(subscriptDecl.ReturnTypeSpec,
                new ProjectionContext { TypeDatabase = typeDatabase, IsParameter = true });

            // @_cdecl subscript wrapper with string index params: wrap call in unsafe fixed block
            // Pass retProjection so value type conversion (e.g. string→SwiftString) is applied.
            if (hasStringIndexParam)
            {
                EmitCdeclSetterWithFixedBlock(csWriter, methodName, args, setupLines, usingLines,
                    paramInfos, hasStringIndexParam, valueProjection: retProjection, isStringValue: false);
                return;
            }
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
                        // ObjC container bridge: method expects IntPtr (.Handle), not the collection object
                        var valArg = retProjection is ArrayProjection { UsesObjCContainerBridge: true }
                            or SetProjection { UsesObjCContainerBridge: true }
                            or DictionaryProjection { UsesObjCContainerBridge: true }
                            ? "__val.Handle" : "__val";
                        csWriter.WriteLine($"using var __val = {conv}; {methodName}({valArg}, {args}); }}");
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
            List<(string typeName, string paramName, ITypeProjection? projection)> paramInfos, bool isCdecl = false)
        {
            var argParts = new List<string>();
            var setupLines = new List<string>();
            var usingLines = new List<string>();

            foreach (var (_, paramName, proj) in paramInfos)
            {
                var bareName = NameProvider.StripVerbatimPrefix(paramName);
                if (proj is StringProjection)
                {
                    if (isCdecl)
                    {
                        // @_cdecl: string index params → UTF-8 bytes, pointer+length
                        var bytesName = $"__{bareName}Utf8";
                        setupLines.Add($"var {bytesName} = System.Text.Encoding.UTF8.GetBytes({paramName});");
                        // Pointer and length are passed as separate args; fixed block wrapping
                        // is handled by EmitCdeclGetterWithFixedBlock/EmitCdeclSetterWithFixedBlock.
                        argParts.Add($"(IntPtr)__{bareName}Ptr");
                        argParts.Add($"{bytesName}.Length");
                    }
                    else
                    {
                        var convertedName = $"__{bareName}Swift";
                        usingLines.Add($"using var {convertedName} = new SwiftString({paramName});");
                        argParts.Add(convertedName);
                    }
                }
                else if (proj is DataProjection)
                {
                    var convertedName = $"__{bareName}Swift";
                    setupLines.Add($"var {convertedName} = Swift.Foundation.Data.FromByteArray({paramName});");
                    argParts.Add(convertedName);
                }
                else if (proj is DateProjection)
                {
                    var convertedName = $"__{bareName}Swift";
                    setupLines.Add($"var {convertedName} = ({paramName} - {DateProjection.SwiftEpoch}).TotalSeconds;");
                    argParts.Add(convertedName);
                }
                else if (proj is NativeRemappedProjection nrp)
                {
                    var convertedName = $"__{bareName}Swift";
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
        /// Emits a @_cdecl subscript getter body with unsafe fixed blocks for string index params.
        /// </summary>
        internal static void EmitCdeclGetterWithFixedBlock(
            CSharpWriter csWriter, string methodName, string args,
            List<string> setupLines, List<string> usingLines,
            List<(string typeName, string paramName, ITypeProjection? projection)> paramInfos,
            bool hasStringIndexParam, ITypeProjection? returnProjection, bool isStringReturn,
            string helperPrefix = "")
        {
            csWriter.WriteLine("get {");
            csWriter.Indent++;
            foreach (var line in usingLines) csWriter.WriteLine(line);
            foreach (var line in setupLines) csWriter.WriteLine(line);

            if (hasStringIndexParam)
            {
                // Emit nested fixed blocks for each string index param
                var fixedParams = GetFixedParamNames(paramInfos);
                csWriter.WriteLine("unsafe {");
                csWriter.Indent++;
                foreach (var (bareName, bytesName) in fixedParams)
                    csWriter.WriteLine($"fixed (byte* __{bareName}Ptr = __{bareName}Utf8) {{");
                csWriter.Indent += fixedParams.Count;
            }

            if (isStringReturn)
            {
                csWriter.WriteLine($"return SwiftMarshal.ReadUtf8Slice({methodName}({args}));");
            }
            else
            {
                // Apply return projection if present (e.g. SwiftString→string, Data→byte[])
                EmitProjectedReturn(csWriter, methodName, args, returnProjection);
            }

            if (hasStringIndexParam)
            {
                var fixedParams = GetFixedParamNames(paramInfos);
                for (int i = 0; i < fixedParams.Count; i++)
                    csWriter.Write("}");
                csWriter.WriteLine();
                csWriter.Indent -= fixedParams.Count;
                csWriter.Indent--;
                csWriter.WriteLine("}");
            }

            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        /// <summary>
        /// Emits a projected return statement, applying conversion if the return type needs it.
        /// </summary>
        internal static void EmitProjectedReturn(CSharpWriter csWriter, string methodName, string args, ITypeProjection? projection)
        {
            if (projection != null)
            {
                var (conv, requiresDisposal) = GetAccessorGetterConversion(projection, $"{methodName}({args})");
                if (conv != null)
                {
                    if (requiresDisposal)
                    {
                        var (usingConv, _) = GetAccessorGetterConversion(projection, "__ret");
                        csWriter.WriteLine($"using var __ret = {methodName}({args}); return {usingConv};");
                    }
                    else
                    {
                        csWriter.WriteLine($"return {conv};");
                    }
                    return;
                }
            }
            csWriter.WriteLine($"return {methodName}({args});");
        }

        /// <summary>
        /// Emits a @_cdecl subscript setter body with unsafe fixed blocks for string index params.
        /// </summary>
        internal static void EmitCdeclSetterWithFixedBlock(
            CSharpWriter csWriter, string methodName, string args,
            List<string> setupLines, List<string> usingLines,
            List<(string typeName, string paramName, ITypeProjection? projection)> paramInfos,
            bool hasStringIndexParam, ITypeProjection? valueProjection, bool isStringValue)
        {
            csWriter.WriteLine("set {");
            csWriter.Indent++;
            foreach (var line in usingLines) csWriter.WriteLine(line);
            foreach (var line in setupLines) csWriter.WriteLine(line);

            // For string value (subscript return type is String), encode the newValue
            if (isStringValue)
            {
                csWriter.WriteLine("var __valueUtf8 = System.Text.Encoding.UTF8.GetBytes(value);");
            }

            if (hasStringIndexParam || isStringValue)
            {
                csWriter.WriteLine("unsafe {");
                csWriter.Indent++;

                // Fixed block for value string
                if (isStringValue)
                    csWriter.WriteLine("fixed (byte* __valuePtr = __valueUtf8) {");

                // Fixed blocks for string index params
                var fixedParams = GetFixedParamNames(paramInfos);
                foreach (var (bareName, _) in fixedParams)
                    csWriter.WriteLine($"fixed (byte* __{bareName}Ptr = __{bareName}Utf8) {{");
                csWriter.Indent += fixedParams.Count + (isStringValue ? 1 : 0);

                if (isStringValue)
                {
                    csWriter.WriteLine($"{methodName}((IntPtr)__valuePtr, __valueUtf8.Length, {args});");
                }
                else
                {
                    EmitProjectedSetterCall(csWriter, methodName, args, valueProjection);
                }

                int closingBraces = fixedParams.Count + (isStringValue ? 1 : 0);
                for (int i = 0; i < closingBraces; i++)
                    csWriter.Write("}");
                csWriter.WriteLine();
                csWriter.Indent -= closingBraces;
                csWriter.Indent--;
                csWriter.WriteLine("}");
            }
            else
            {
                EmitProjectedSetterCall(csWriter, methodName, args, valueProjection);
            }

            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        /// <summary>
        /// Emits a setter call, applying value projection conversion if present.
        /// </summary>
        internal static void EmitProjectedSetterCall(CSharpWriter csWriter, string methodName, string args, ITypeProjection? valueProjection)
        {
            if (valueProjection != null)
            {
                var (conv, requiresDisposal) = GetAccessorSetterConversion(valueProjection, "value");
                if (conv != null)
                {
                    if (requiresDisposal)
                    {
                        // ObjC container bridge: method expects IntPtr (.Handle), not the collection object
                        var valArg = valueProjection is ArrayProjection { UsesObjCContainerBridge: true }
                            or SetProjection { UsesObjCContainerBridge: true }
                            or DictionaryProjection { UsesObjCContainerBridge: true }
                            ? "__val.Handle" : "__val";
                        csWriter.WriteLine($"using var __val = {conv}; {methodName}({valArg}, {args});");
                    }
                    else
                    {
                        csWriter.WriteLine($"{methodName}({conv}, {args});");
                    }
                    return;
                }
            }
            csWriter.WriteLine($"{methodName}(value, {args});");
        }

        /// <summary>
        /// Gets the fixed parameter names for string index params that need unsafe fixed blocks.
        /// </summary>
        private static List<(string bareName, string bytesName)> GetFixedParamNames(
            List<(string typeName, string paramName, ITypeProjection? projection)> paramInfos)
        {
            var result = new List<(string, string)>();
            foreach (var (_, paramName, proj) in paramInfos)
            {
                if (proj is StringProjection)
                {
                    var bareName = NameProvider.StripVerbatimPrefix(paramName);
                    result.Add((bareName, $"__{bareName}Utf8"));
                }
            }
            return result;
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

        // Getter/setter conversion helpers — delegates to shared AccessorConversionVisitors

        internal static (string? conversion, bool requiresDisposal) GetAccessorGetterConversion(
            ITypeProjection projection, string resultExpr)
        {
            return projection.Accept(new AccessorGetterConversionVisitor(resultExpr));
        }

        internal static (string? conversion, bool requiresDisposal) GetOptionalAccessorGetterConversion(
            OptionalProjection opt, string resultExpr)
        {
            return AccessorGetterConversionVisitor.OptionalAccessorGetterConversion(opt, resultExpr);
        }

        internal static (string? conversion, bool requiresDisposal) GetDictAccessorGetterConversion(
            DictionaryProjection dict, string resultExpr)
        {
            return AccessorGetterConversionVisitor.DictGetterConversion(dict, resultExpr);
        }

        internal static (string? conversion, bool requiresDisposal) GetSetAccessorGetterConversion(
            SetProjection set, string resultExpr)
        {
            return AccessorGetterConversionVisitor.SetGetterConversion(set, resultExpr);
        }

        internal static (string? conversion, bool requiresDisposal) GetAccessorSetterConversion(
            ITypeProjection projection, string valueExpr)
        {
            return projection.Accept(new AccessorSetterConversionVisitor(valueExpr));
        }

        internal static (string? conversion, bool requiresDisposal) GetSetAccessorSetterConversion(
            SetProjection set, string valueExpr)
        {
            return AccessorSetterConversionVisitor.SetSetterConversion(set, valueExpr);
        }

        internal static (string? conversion, bool requiresDisposal) GetOptionalAccessorSetterConversion(
            OptionalProjection opt, string valueExpr)
        {
            return AccessorSetterConversionVisitor.OptionalSetterConversion(opt, valueExpr);
        }
    }
}
