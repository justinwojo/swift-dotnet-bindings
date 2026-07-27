// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    public partial class EnumHandler
    {
        // Wrapper symbol dedup state stored on ModuleEmissionContext (per-module instance).

        /// <summary>
        /// Records the RawRepresentable surface of a module-internal enum (or one nested in an
        /// internal type) as skipped, and leaves a greppable <c>// Unsupported:</c> tombstone for
        /// each dropped member, instead of emitting <c>FromRawValue</c> plus one case accessor per
        /// case against wrapper symbols the discarded Swift plane never defines.
        /// </summary>
        private void TombstoneRawRepresentableSurface(
            CSharpWriter csWriter,
            EnumDecl enumDecl,
            List<EnumCaseDecl> simpleCases,
            Dictionary<string, string>? propertyRenames,
            Dictionary<string, string>? caseNameMap)
        {
            const string details =
                "module-internal raw-representable enum: the wrapper module cannot name its qualified "
                + "path, so the init(rawValue:) and case-by-index @_cdecl wrappers this surface calls "
                + "are never emitted, and the ABI JSON carries no real raw values to construct the "
                + "cases from instead.";

            _logger.LogInformation(
                "Skipping the RawRepresentable surface of enum '{Enum}' — the enum (or a type enclosing it) is module-internal, so its Swift wrapper plane is discarded and every FromRawValue / case accessor would reference an undefined wrapper symbol.",
                enumDecl.Name);

            ReportCollector.RecordMemberSkipped(
                BindingItemKind.Method, "FromRawValue", enumDecl, SkipReason.ModuleInternal, details);
            UnsupportedCommentEmitter.EmitMemberSkipped(
                csWriter, "FromRawValue", BindingItemKind.Method, SkipReason.ModuleInternal,
                details, containingDecl: enumDecl);

            foreach (var caseDecl in simpleCases)
            {
                var caseName = NameProvider.GetFinalMemberName(
                    NameProvider.GetCaseName(caseDecl.Name, caseNameMap), propertyRenames);
                ReportCollector.RecordMemberSkipped(
                    BindingItemKind.Property, caseName, enumDecl, SkipReason.ModuleInternal, details);
                UnsupportedCommentEmitter.EmitMemberSkipped(
                    csWriter, caseName, BindingItemKind.Property, SkipReason.ModuleInternal,
                    details, containingDecl: enumDecl);
            }
        }

        /// <summary>
        /// Emits RawRepresentable support for enums with simple cases.
        /// This includes a FromRawValue method and static properties for each case.
        /// </summary>
        private void EmitRawRepresentableSupport(CSharpWriter csWriter, SwiftWriter swiftWriter, EnumDecl enumDecl, List<EnumCaseDecl> simpleCases, ModuleDecl moduleDecl, ITypeDatabase typeDatabase, string enumTypeName, PInvokeHelperContext? pinvokeHelperContext, bool canCacheCases = false, Dictionary<string, string>? propertyRenames = null, Dictionary<string, string>? caseNameMap = null, ModuleEmissionContext? ctx = null)
        {
            // Every route out of this method — FromRawValue and each case accessor — is backed by an
            // SBW_ @_cdecl wrapper written into swiftWriter, so the whole surface is unemittable the
            // moment that plane is discarded. The caller decides that (and tombstones instead); this
            // pins the contract at the site that would otherwise claim the symbols.
            WrapperValidation.RequireLiveWrapperPlane(
                swiftWriter, $"The RawRepresentable surface of enum '{enumDecl.SwiftTypeName.ModuleQualifiedName}'");

            var rawTypeName = enumDecl.RawValueTypeName!;
            var libPath = typeDatabase.GetLibraryPath(moduleDecl.Name);
            var wrapperLibPath = typeDatabase.AsyncLibraryName ?? libPath;
            var isStringRawType = rawTypeName == "String";
            // Per-enum suffix for helper P/Invoke method names to avoid dedup collisions
            // when multiple enums share a PInvokeHelperContext (nested in same generic parent).
            // Uses module-qualified name (e.g., "Mod_Outer_Foo_Status") so same-named enums
            // under different nested paths (Outer.Foo.Status vs Outer.Bar.Status) don't collide.
            var enumPInvokeSuffix = pinvokeHelperContext != null
                ? $"_{enumDecl.SwiftTypeName.ModuleQualifiedName.Replace(".", "_")}"
                : "";

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
                "Float" => "float",
                "Double" or "CGFloat" => "double",
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

            // For String raw types, emit Swift wrapper for init(rawValue:) conversion
            string? wrapperSymbol = null;
            string? caseByIndexSymbol = null;
            var sanitizedName = enumDecl.SwiftTypeName.ModuleQualifiedName.Replace(".", "_");
            if (isStringRawType)
            {
                // Use full module-qualified name to avoid collisions for same-named nested enums
                // (e.g., Module.Foo.ErrorType and Module.Bar.ErrorType get unique symbols)
                wrapperSymbol = $"SBW_{sanitizedName}_InitWithRawValue";
                EmitStringRawValueSwiftWrapper(swiftWriter, enumDecl, moduleDecl, wrapperSymbol, ctx);
            }
            else if (!enumDecl.IsFrozen && WrapperValidation.IsXCFrameworkMode(typeDatabase))
            {
                // Non-frozen blittable enum: emit @_cdecl wrapper for init(rawValue:) to avoid
                // CallConvSwift + SwiftIndirectResult crash on Mono JIT.
                // The wrapper writes Optional<Self> to a caller-provided buffer using Cdecl ABI.
                wrapperSymbol = $"SBW_{sanitizedName}_InitWithRawValue";
                EmitBlittableRawValueSwiftWrapper(swiftWriter, enumDecl, rawTypeName, wrapperSymbol, ctx);
            }

            // Emit CaseByIndex Swift wrapper for raw-representable enums.
            // - String enums: always (case name != raw value is common, e.g., case ok = "OK")
            // - Non-string enums: when wrapper library exists (ABI JSON lacks actual raw values,
            //   so ordinal-based FromRawValue(i) fails for enums like Unit: TimeInterval where seconds=1)
            // CaseByIndex constructs cases directly (.seconds, .milliseconds) without needing raw values.
            if (isStringRawType || WrapperValidation.IsXCFrameworkMode(typeDatabase))
            {
                caseByIndexSymbol = $"SBW_{sanitizedName}_CaseByIndex";
                EmitCaseByIndexSwiftWrapper(swiftWriter, enumDecl, simpleCases, caseByIndexSymbol, ctx);
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

                if (isStringRawType)
                {
                    // String raw type: use UTF-8 marshalling via wrapper
                    csWriter.WriteLine($"public static unsafe {enumTypeName}? FromRawValue({csharpRawType} rawValue)");
                    csWriter.WriteLine("{");
                    csWriter.Indent++;
                    csWriter.WriteLine("var utf8Bytes = global::System.Text.Encoding.UTF8.GetBytes(rawValue ?? string.Empty);");
                    csWriter.WriteLine("fixed (byte* utf8Ptr = utf8Bytes)");
                    csWriter.WriteLine("{");
                    csWriter.Indent++;
                    csWriter.WriteLine("var slice = new Utf8Slice { Ptr = (IntPtr)utf8Ptr, Len = (nint)utf8Bytes.Length };");
                    csWriter.WriteLine("IntPtr resultPtr = PInvoke_InitWithRawValue_Wrapper((IntPtr)(&slice));");
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
                    csWriter.Indent--;
                    csWriter.WriteLine("}");
                    csWriter.WriteLine();

                    // P/Invoke for the Swift wrapper (not the original init)
                    csWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
                    csWriter.WriteLine($"[global::System.Runtime.InteropServices.LibraryImport(\"{wrapperLibPath}\", EntryPoint = \"{wrapperSymbol}\")]");
                    csWriter.WriteLine("private static partial IntPtr PInvoke_InitWithRawValue_Wrapper(IntPtr slicePtr);");
                    csWriter.WriteLine();
                }
                else
                {
                    // Blittable raw type: direct P/Invoke
                    csWriter.WriteLine($"public static {enumTypeName}? FromRawValue({csharpRawType} rawValue)");
                    csWriter.WriteLine("{");
                    csWriter.Indent++;
                    var rawInitCall = pinvokeHelperContext != null
                        ? $"{pinvokeHelperContext.HelperClassName}.PInvoke_InitWithRawValue{enumPInvokeSuffix}(rawValue, {string.Join(", ", pinvokeHelperContext.GetMetadataArgumentList())})"
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
                            MethodName = $"PInvoke_InitWithRawValue{enumPInvokeSuffix}",
                            ReturnType = "IntPtr",
                            ParametersString = $"{csharpRawType} rawValue",
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
                            EntryPoint = initRawValueMethod.MangledName,
                            MethodName = "PInvoke_InitWithRawValue",
                            ReturnType = "IntPtr",
                            ParametersString = $"{csharpRawType} rawValue",
                            CallingConvention = PInvokeCallingConvention.Swift
                        });
                        csWriter.WriteLine();
                    }
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

                // Get metadata for the enum type and SwiftOptional<EnumType>.
                // The metadata accessor PInvoke takes PWT args for any
                // protocol-constrained generic params.
                csWriter.WriteLine("// Get metadata for the enum type");
                var getMetadataCall = pinvokeHelperContext != null
                    ? $"{pinvokeHelperContext.HelperClassName}.PInvoke_getMetadata(TypeMetadataRequest.Complete, {string.Join(", ", pinvokeHelperContext.GetTypeMetadataAccessorArgumentList())})"
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

                // Call P/Invoke with indirect result - different for String vs blittable
                csWriter.WriteLine("// Call the failable initializer with indirect result");

                if (isStringRawType)
                {
                    // String raw type: encode to UTF-8 and use wrapper
                    csWriter.WriteLine("var utf8Bytes = global::System.Text.Encoding.UTF8.GetBytes(rawValue ?? string.Empty);");
                    csWriter.WriteLine("fixed (byte* utf8Ptr = utf8Bytes)");
                    csWriter.WriteLine("{");
                    csWriter.Indent++;
                    csWriter.WriteLine("var slice = new Utf8Slice { Ptr = (IntPtr)utf8Ptr, Len = (nint)utf8Bytes.Length };");
                    csWriter.WriteLine("PInvoke_InitWithRawValue_Wrapper((IntPtr)resultBuffer, (IntPtr)(&slice));");
                    csWriter.Indent--;
                    csWriter.WriteLine("}");
                }
                else if (wrapperSymbol != null && pinvokeHelperContext == null)
                {
                    // Blittable raw type with @_cdecl wrapper: Cdecl ABI, IntPtr result buffer
                    csWriter.WriteLine($"PInvoke_InitWithRawValue_Wrapper((IntPtr)resultBuffer, rawValue);");
                }
                else
                {
                    // Blittable raw type: direct P/Invoke (fallback for generic parents or no wrapper lib)
                    csWriter.WriteLine("var swiftIndirectResult = new SwiftIndirectResult(resultBuffer);");
                    var rawInitIndirectCall = pinvokeHelperContext != null
                        ? $"{pinvokeHelperContext.HelperClassName}.PInvoke_InitWithRawValue{enumPInvokeSuffix}(swiftIndirectResult, rawValue, {string.Join(", ", pinvokeHelperContext.GetMetadataArgumentList())})"
                        : "PInvoke_InitWithRawValue(swiftIndirectResult, rawValue)";
                    csWriter.WriteLine($"{rawInitIndirectCall};");
                }

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

                // Emit P/Invoke for init(rawValue:)
                if (isStringRawType)
                {
                    // String raw type: P/Invoke for the Swift wrapper
                    csWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
                    csWriter.WriteLine($"[global::System.Runtime.InteropServices.LibraryImport(\"{wrapperLibPath}\", EntryPoint = \"{wrapperSymbol}\")]");
                    csWriter.WriteLine("private static partial void PInvoke_InitWithRawValue_Wrapper(IntPtr resultPtr, IntPtr slicePtr);");
                    csWriter.WriteLine();
                }
                else if (wrapperSymbol != null && pinvokeHelperContext == null)
                {
                    // Blittable raw type with @_cdecl wrapper: Cdecl P/Invoke targeting wrapper
                    PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
                    {
                        LibraryPath = wrapperLibPath,
                        EntryPoint = wrapperSymbol,
                        MethodName = "PInvoke_InitWithRawValue_Wrapper",
                        ReturnType = "void",
                        ParametersString = $"IntPtr resultPtr, {csharpRawType} rawValue"
                    });
                    csWriter.WriteLine();
                }
                else if (pinvokeHelperContext != null)
                {
                    pinvokeHelperContext.AddDeclaration(new PInvokeDeclaration
                    {
                        LibraryPath = libPath,
                        EntryPoint = initRawValueMethod.MangledName,
                        MethodName = $"PInvoke_InitWithRawValue{enumPInvokeSuffix}",
                        ReturnType = "void",
                        ParametersString = $"SwiftIndirectResult result, {csharpRawType} rawValue",
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
                        EntryPoint = initRawValueMethod.MangledName,
                        MethodName = "PInvoke_InitWithRawValue",
                        ReturnType = "void",
                        ParametersString = $"SwiftIndirectResult result, {csharpRawType} rawValue",
                        CallingConvention = PInvokeCallingConvention.Swift
                    });
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
                        IsAsync = false,
                        CallingConvention = PInvokeCallingConvention.Swift
                    });
                }
                else
                {
                    csWriter.WriteLine("// SwiftOptional metadata accessor from Swift stdlib");
                    PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
                    {
                        LibraryPath = "/usr/lib/swift/libswiftCore.dylib",
                        EntryPoint = "$sSqMa",
                        MethodName = "PInvokesForSwiftOptional_MetadataAccessor",
                        ReturnType = "TypeMetadata",
                        ParametersString = "TypeMetadataRequest request, TypeMetadata typeMetadata",
                        CallingConvention = PInvokeCallingConvention.Swift
                    });
                    csWriter.WriteLine();
                }
            }

            // Emit CaseByIndex P/Invoke for enums with a wrapper library
            // (allows constructing cases by index without knowing raw values)
            if (caseByIndexSymbol != null)
            {
                if (pinvokeHelperContext != null)
                {
                    pinvokeHelperContext.AddDeclaration(new PInvokeDeclaration
                    {
                        LibraryPath = wrapperLibPath,
                        EntryPoint = caseByIndexSymbol,
                        MethodName = $"PInvoke_CaseByIndex{enumPInvokeSuffix}",
                        ReturnType = "IntPtr",
                        ParametersString = "nint index",
                        IsAsync = false
                    });
                }
                else
                {
                    csWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
                    csWriter.WriteLine($"[global::System.Runtime.InteropServices.LibraryImport(\"{wrapperLibPath}\", EntryPoint = \"{caseByIndexSymbol}\")]");
                    csWriter.WriteLine("private static partial IntPtr PInvoke_CaseByIndex(nint index);");
                    csWriter.WriteLine();
                }
            }

            // Emit static properties for each simple case
            // Simple cases use sequential raw values starting from 0 (Swift default behavior)
            for (int i = 0; i < simpleCases.Count; i++)
            {
                var caseDecl = simpleCases[i];
                // Skip @_spi-protected cases — inaccessible from external code.
                // The CaseByIndex Swift wrapper also traps on these indices.
                if (caseDecl.IsSpiProtected)
                    continue;
                var caseName = caseDecl.Name;
                var capitalizedName = NameProvider.GetFinalMemberName(
                    NameProvider.GetCaseName(caseName, caseNameMap), propertyRenames);
                var fieldName = caseName;

                // For enums with CaseByIndex wrapper: construct cases by index
                // This avoids the ABI JSON limitation where actual raw values are unknown.
                // FromRawValue(ordinal) fails when the raw value != ordinal (e.g., seconds=1.0 not 0).
                if (caseByIndexSymbol != null)
                {
                    var caseByIndexCall = pinvokeHelperContext != null
                        ? $"{pinvokeHelperContext.HelperClassName}.PInvoke_CaseByIndex{enumPInvokeSuffix}({i})"
                        : $"PInvoke_CaseByIndex({i})";

                    if (canCacheCases)
                    {
                        csWriter.WriteLine($"private static readonly Lazy<{enumTypeName}> _lazy_{fieldName} = new(() =>");
                        csWriter.WriteLine("{");
                        csWriter.Indent++;
                        csWriter.WriteLine($"IntPtr ptr = {caseByIndexCall};");
                        csWriter.WriteLine($"var result = new {enumTypeName}();");
                        csWriter.WriteLine($"result._payload = new SwiftSafeHandle<{enumTypeName}>(ptr);");
                        csWriter.WriteLine("result._isCachedSingleton = true;");
                        csWriter.WriteLine("return result;");
                        csWriter.Indent--;
                        csWriter.WriteLine("});");

                        csWriter.WriteLine("/// <summary>");
                        csWriter.WriteLine($"/// Gets the '{caseName}' case of {enumTypeName}.");
                        csWriter.WriteLine("/// </summary>");
                        csWriter.WriteLine("/// <remarks>Cached singleton instance — does not require disposal.</remarks>");
                        csWriter.WriteLine($"public static {enumTypeName} {capitalizedName} => _lazy_{fieldName}.Value;");
                        csWriter.WriteLine();
                    }
                    else
                    {
                        csWriter.WriteLine("/// <summary>");
                        csWriter.WriteLine($"/// Gets the '{caseName}' case of {enumTypeName}.");
                        csWriter.WriteLine("/// </summary>");
                        csWriter.WriteLine($"public static {enumTypeName} {capitalizedName}");
                        csWriter.WriteLine("{");
                        csWriter.Indent++;
                        csWriter.WriteLine("get");
                        csWriter.WriteLine("{");
                        csWriter.Indent++;
                        csWriter.WriteLine($"IntPtr ptr = {caseByIndexCall};");
                        csWriter.WriteLine($"var result = new {enumTypeName}();");
                        csWriter.WriteLine($"result._payload = new SwiftSafeHandle<{enumTypeName}>(ptr);");
                        csWriter.WriteLine("return result;");
                        csWriter.Indent--;
                        csWriter.WriteLine("}");
                        csWriter.Indent--;
                        csWriter.WriteLine("}");
                        csWriter.WriteLine();
                    }
                    continue;
                }

                // Non-string enums: use FromRawValue with sequential integer raw values
                string rawValueLiteral = i.ToString();

                if (canCacheCases)
                {
                    // Lazy-cached singleton: exactly one native allocation per case, thread-safe.
                    csWriter.WriteLine($"private static readonly Lazy<{enumTypeName}> _lazy_{fieldName} = new(() =>");
                    csWriter.WriteLine("{");
                    csWriter.Indent++;
                    csWriter.WriteLine($"var result = FromRawValue({rawValueLiteral})");
                    csWriter.WriteLine($"    ?? throw new InvalidOperationException(\"Failed to create {enumTypeName}.{capitalizedName} from raw value {rawValueLiteral}\");");
                    csWriter.WriteLine("result._isCachedSingleton = true;");
                    csWriter.WriteLine("return result;");
                    csWriter.Indent--;
                    csWriter.WriteLine("});");

                    csWriter.WriteLine("/// <summary>");
                    csWriter.WriteLine($"/// Gets the '{caseName}' case of {enumTypeName}.");
                    csWriter.WriteLine("/// </summary>");
                    csWriter.WriteLine("/// <remarks>Cached singleton instance — does not require disposal.</remarks>");
                    csWriter.WriteLine($"public static {enumTypeName} {capitalizedName} => _lazy_{fieldName}.Value;");
                    csWriter.WriteLine();
                }
                else
                {
                    // Per-access construction: enum has mutating methods or writable properties,
                    // so caching would allow global mutation of a shared instance.
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
                    csWriter.WriteLine($"throw new InvalidOperationException(\"Failed to create {enumTypeName}.{capitalizedName} from raw value {rawValueLiteral}\");");
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
        }

        /// <summary>
        /// Emits the Swift wrapper function for String-based enum init(rawValue:).
        /// The wrapper accepts SBW_Utf8Slice, decodes to String, and calls the real init.
        /// </summary>
        private void EmitStringRawValueSwiftWrapper(SwiftWriter swiftWriter, EnumDecl enumDecl, ModuleDecl moduleDecl, string wrapperSymbol, ModuleEmissionContext? ctx = null)
        {
            ctx ??= ModuleEmissionContext.Default;
            // RawRep wrappers live in a dedicated `_enum_raw_rep` bucket — no other emitter writes to it. One init(rawValue:) wrapper per enum, keyed by symbol name.
            if (!ctx.TryAddEnumRawRepWrapperSymbol(wrapperSymbol, DeclIdFactory.ForType(enumDecl)))
            {
                return;
            }

            // Use the full module-qualified name for nested enums (e.g., Module.ParentClass.NestedEnum)
            var enumFullName = enumDecl.SwiftTypeName.ModuleQualifiedName;

            // Emit SBW_Utf8Slice struct if not already done for this module
            Utf8SliceEmitter.EmitIfNeeded(swiftWriter, ctx);

            // Emit availability annotations from the enum and its ancestors.
            // @_cdecl wrappers are top-level functions and don't inherit enclosing type availability.
            var availability = WrapperEmitterHelpers.MergeAvailability(null, enumDecl);

            // Determine if enum is frozen (affects return style)
            if (enumDecl.IsFrozen)
            {
                // Frozen enum: return Optional pointer directly
                // Returns nil (NULL) if rawValue is invalid
                WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);
                swiftWriter.WriteLines($$"""
                    @_cdecl("{{wrapperSymbol}}")
                    public func {{wrapperSymbol}}(_ slicePtr: UnsafeRawPointer) -> UnsafeMutableRawPointer? {
                        let slice = slicePtr.load(as: SBW_Utf8Slice.self)
                        let str: String
                        if slice.len > 0 {
                            str = String(unsafeUninitializedCapacity: slice.len) { buf in
                                UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
                                return slice.len
                            }
                        } else {
                            str = ""
                        }
                        guard let result = {{enumFullName}}(rawValue: str) else {
                            return nil
                        }
                        let ptr = UnsafeMutablePointer<{{enumFullName}}>.allocate(capacity: 1)
                        ptr.initialize(to: result)
                        return UnsafeMutableRawPointer(ptr)
                    }

                    """);
            }
            else
            {
                // Non-frozen enum: write result to indirect return buffer
                // The caller provides the buffer for Optional<EnumType>
                WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);
                swiftWriter.WriteLines($$"""
                    @_cdecl("{{wrapperSymbol}}")
                    public func {{wrapperSymbol}}(_ resultPtr: UnsafeMutableRawPointer, _ slicePtr: UnsafeRawPointer) {
                        let slice = slicePtr.load(as: SBW_Utf8Slice.self)
                        let str: String
                        if slice.len > 0 {
                            str = String(unsafeUninitializedCapacity: slice.len) { buf in
                                UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
                                return slice.len
                            }
                        } else {
                            str = ""
                        }
                        let result: {{enumFullName}}? = {{enumFullName}}(rawValue: str)
                        // Use withUnsafePointer + copyMemory instead of storeBytes to avoid
                        // BitwiseCopyable requirement (Swift 6+) for Optional<Enum> with String raw values
                        withUnsafePointer(to: result) { _srcPtr in
                            resultPtr.copyMemory(from: UnsafeRawPointer(_srcPtr), byteCount: MemoryLayout<{{enumFullName}}?>.size)
                        }
                    }

                    """);
            }
        }

        /// <summary>
        /// Emits a @_cdecl Swift wrapper for non-frozen blittable enum init(rawValue:).
        /// Writes Optional&lt;Self&gt; to a caller-provided buffer, avoiding CallConvSwift + SwiftIndirectResult
        /// which crashes on Mono JIT.
        /// </summary>
        private void EmitBlittableRawValueSwiftWrapper(SwiftWriter swiftWriter, EnumDecl enumDecl, string rawTypeName, string wrapperSymbol, ModuleEmissionContext? ctx = null)
        {
            ctx ??= ModuleEmissionContext.Default;
            // RawRep wrappers live in a dedicated `_enum_raw_rep` bucket — no other emitter writes to it. One init(rawValue:) wrapper per enum, keyed by symbol name.
            if (!ctx.TryAddEnumRawRepWrapperSymbol(wrapperSymbol, DeclIdFactory.ForType(enumDecl)))
                return;

            var enumFullName = enumDecl.SwiftTypeName.ModuleQualifiedName;
            var availability = WrapperEmitterHelpers.MergeAvailability(null, enumDecl);

            WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);
            swiftWriter.WriteLines($$"""
                @_cdecl("{{wrapperSymbol}}")
                public func {{wrapperSymbol}}(_ resultPtr: UnsafeMutableRawPointer, _ rawValue: {{rawTypeName}}) {
                    let result: {{enumFullName}}? = {{enumFullName}}(rawValue: rawValue)
                    withUnsafePointer(to: result) { _srcPtr in
                        resultPtr.copyMemory(from: UnsafeRawPointer(_srcPtr), byteCount: MemoryLayout<{{enumFullName}}?>.size)
                    }
                }

                """);
        }

        /// <summary>
        /// Emits a Swift wrapper that constructs a String enum case by its index.
        /// This avoids the ABI JSON limitation where actual raw values are unknown —
        /// cases are constructed directly (e.g., .ok, .notFound) rather than through init(rawValue:).
        /// </summary>
        private void EmitCaseByIndexSwiftWrapper(SwiftWriter swiftWriter, EnumDecl enumDecl, List<EnumCaseDecl> simpleCases, string caseByIndexSymbol, ModuleEmissionContext? ctx = null)
        {
            ctx ??= ModuleEmissionContext.Default;
            // RawRep wrappers live in a dedicated `_enum_raw_rep` bucket — no other emitter writes to it. One case-by-index wrapper per enum, keyed by symbol name.
            if (!ctx.TryAddEnumRawRepWrapperSymbol(caseByIndexSymbol, DeclIdFactory.ForType(enumDecl)))
                return;

            var enumFullName = enumDecl.SwiftTypeName.ModuleQualifiedName;
            // Walk every case's @available so cases added in newer SDKs (e.g.
            // ModelDebugOptionsComponent.VisualizationMode.clearcoatNormal at iOS 18 in an
            // iOS 14 enum) lift the wrapper's floor — referencing them under the enum's
            // own iOS 14 floor is a swiftc availability error.
            List<AvailabilityAnnotation>? caseAnnotations = null;
            foreach (var simpleCase in simpleCases)
            {
                if (simpleCase.AvailabilityAnnotations is { Count: > 0 } caseAvail)
                {
                    caseAnnotations ??= new List<AvailabilityAnnotation>();
                    caseAnnotations.AddRange(caseAvail);
                }
            }
            var availability = WrapperEmitterHelpers.MergeAvailability(caseAnnotations, enumDecl);

            WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"@_cdecl(\"{caseByIndexSymbol}\")");
            sb.AppendLine($"public func {caseByIndexSymbol}(_ index: Int) -> UnsafeMutableRawPointer {{");
            sb.AppendLine($"    let value: {enumFullName}");
            sb.AppendLine("    switch index {");
            for (int i = 0; i < simpleCases.Count; i++)
            {
                // Skip @_spi-protected cases — inaccessible without @_spi import
                if (simpleCases[i].IsSpiProtected)
                {
                    sb.AppendLine($"    case {i}: fatalError(\"[SwiftBindings] Case at index \\({i}) is @_spi protected\")");
                    continue;
                }
                sb.AppendLine($"    case {i}: value = .{simpleCases[i].Name}");
            }
            sb.AppendLine($"    default: fatalError(\"[SwiftBindings] Invalid case index \\(index) for {enumFullName}\")");
            sb.AppendLine("    }");
            sb.AppendLine($"    let ptr = UnsafeMutablePointer<{enumFullName}>.allocate(capacity: 1)");
            sb.AppendLine("    ptr.initialize(to: value)");
            sb.AppendLine("    return UnsafeMutableRawPointer(ptr)");
            sb.AppendLine("}");

            swiftWriter.WriteLines(sb.ToString());
            swiftWriter.WriteLine();
        }

        // Utf8Slice struct is now shared at module level (emitted by ModuleHandler).
    }
}
