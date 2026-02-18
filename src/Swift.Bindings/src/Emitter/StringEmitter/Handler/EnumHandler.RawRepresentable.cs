// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    public partial class EnumHandler
    {
        /// <summary>
        /// Tracks which Swift wrapper symbols have been emitted to avoid duplicates.
        /// This is needed because nested enums may be processed multiple times.
        /// </summary>
        private static readonly HashSet<string> _emittedWrapperSymbols = new();

        /// <summary>
        /// Resets the UTF-8 slice emission tracking. Call at the start of each module.
        /// </summary>
        public static void ResetUtf8SliceTracking()
        {
            Utf8SliceEmitter.ResetForModule();
            _emittedWrapperSymbols.Clear();
        }

        /// <summary>
        /// Emits RawRepresentable support for enums with simple cases.
        /// This includes a FromRawValue method and static properties for each case.
        /// </summary>
        private void EmitRawRepresentableSupport(CSharpWriter csWriter, SwiftWriter swiftWriter, EnumDecl enumDecl, List<EnumCaseDecl> simpleCases, ModuleDecl moduleDecl, ITypeDatabase typeDatabase, string enumTypeName, PInvokeHelperContext? pinvokeHelperContext, bool canCacheCases = false)
        {
            var rawTypeName = enumDecl.RawValueTypeName!;
            var libPath = typeDatabase.GetLibraryPath(moduleDecl.Name);
            var wrapperLibPath = typeDatabase.AsyncLibraryName ?? libPath;
            var isStringRawType = rawTypeName == "String";

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

            // For String raw types, emit Swift wrapper and C# marshalling infrastructure
            string? wrapperSymbol = null;
            if (isStringRawType)
            {
                // Use full module-qualified name to avoid collisions for same-named nested enums
                // e.g., BlinkID.Foo.ErrorType and BlinkID.Bar.ErrorType get unique symbols
                var sanitizedName = enumDecl.SwiftTypeName.ModuleQualifiedName.Replace(".", "_");
                wrapperSymbol = $"SBW_{sanitizedName}_InitWithRawValue";
                EmitStringRawValueSwiftWrapper(swiftWriter, enumDecl, moduleDecl, wrapperSymbol);
                EmitUtf8SliceStruct(csWriter);
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
                    csWriter.WriteLine("var utf8Bytes = System.Text.Encoding.UTF8.GetBytes(rawValue ?? string.Empty);");
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
                    csWriter.WriteLine($"[LibraryImport(\"{wrapperLibPath}\", EntryPoint = \"{wrapperSymbol}\")]");
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
                        csWriter.WriteLine($"[LibraryImport(\"{libPath}\", EntryPoint = \"{initRawValueMethod.MangledName}\")]");
                        csWriter.WriteLine("[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]");
                        csWriter.WriteLine($"private static partial IntPtr PInvoke_InitWithRawValue({csharpRawType} rawValue);");
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

                // Call P/Invoke with indirect result - different for String vs blittable
                csWriter.WriteLine("// Call the failable initializer with indirect result");

                if (isStringRawType)
                {
                    // String raw type: encode to UTF-8 and use wrapper
                    csWriter.WriteLine("var utf8Bytes = System.Text.Encoding.UTF8.GetBytes(rawValue ?? string.Empty);");
                    csWriter.WriteLine("fixed (byte* utf8Ptr = utf8Bytes)");
                    csWriter.WriteLine("{");
                    csWriter.Indent++;
                    csWriter.WriteLine("var slice = new Utf8Slice { Ptr = (IntPtr)utf8Ptr, Len = (nint)utf8Bytes.Length };");
                    csWriter.WriteLine("PInvoke_InitWithRawValue_Wrapper((IntPtr)resultBuffer, (IntPtr)(&slice));");
                    csWriter.Indent--;
                    csWriter.WriteLine("}");
                }
                else
                {
                    // Blittable raw type: direct P/Invoke
                    csWriter.WriteLine("var swiftIndirectResult = new SwiftIndirectResult(resultBuffer);");
                    var rawInitIndirectCall = pinvokeHelperContext != null
                        ? $"{pinvokeHelperContext.HelperClassName}.PInvoke_InitWithRawValue(swiftIndirectResult, rawValue, {string.Join(", ", pinvokeHelperContext.GetMetadataArgumentList())})"
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
                    csWriter.WriteLine($"[LibraryImport(\"{wrapperLibPath}\", EntryPoint = \"{wrapperSymbol}\")]");
                    csWriter.WriteLine("private static partial void PInvoke_InitWithRawValue_Wrapper(IntPtr resultPtr, IntPtr slicePtr);");
                    csWriter.WriteLine();
                }
                else if (pinvokeHelperContext != null)
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
                    csWriter.WriteLine($"[LibraryImport(\"{libPath}\", EntryPoint = \"{initRawValueMethod.MangledName}\")]");
                    csWriter.WriteLine("[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]");
                    csWriter.WriteLine($"private static partial void PInvoke_InitWithRawValue(SwiftIndirectResult result, {csharpRawType} rawValue);");
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
                    csWriter.WriteLine("[LibraryImport(\"/usr/lib/swift/libswiftCore.dylib\", EntryPoint = \"$sSqMa\")]");
                    csWriter.WriteLine("[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]");
                    csWriter.WriteLine("private static partial TypeMetadata PInvokesForSwiftOptional_MetadataAccessor(TypeMetadataRequest request, TypeMetadata typeMetadata);");
                    csWriter.WriteLine();
                }
            }

            // Emit static properties for each simple case
            // Simple cases use sequential raw values starting from 0 (Swift default behavior)
            for (int i = 0; i < simpleCases.Count; i++)
            {
                var caseDecl = simpleCases[i];
                var caseName = caseDecl.Name;
                var capitalizedName = NameProvider.ToPascalCase(caseName);
                var fieldName = caseName;

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

                // Escape quotes in rawValueLiteral for the error message string
                var escapedRawValue = rawValueLiteral.Replace("\"", "\\\"");

                if (canCacheCases)
                {
                    // Lazy-cached singleton: exactly one native allocation per case, thread-safe.
                    csWriter.WriteLine($"private static readonly Lazy<{enumTypeName}> _lazy_{fieldName} = new(() =>");
                    csWriter.WriteLine("{");
                    csWriter.Indent++;
                    csWriter.WriteLine($"var result = FromRawValue({rawValueLiteral})");
                    csWriter.WriteLine($"    ?? throw new InvalidOperationException(\"Failed to create {enumTypeName}.{capitalizedName} from raw value {escapedRawValue}\");");
                    csWriter.WriteLine("result._isCachedSingleton = true;");
                    csWriter.WriteLine("return result;");
                    csWriter.Indent--;
                    csWriter.WriteLine("});");

                    csWriter.WriteLine("/// <summary>");
                    csWriter.WriteLine($"/// Gets the '{caseName}' case of {enumTypeName}.");
                    csWriter.WriteLine("/// </summary>");
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
        }

        /// <summary>
        /// Emits the Swift wrapper function for String-based enum init(rawValue:).
        /// The wrapper accepts SBW_Utf8Slice, decodes to String, and calls the real init.
        /// </summary>
        private void EmitStringRawValueSwiftWrapper(SwiftWriter swiftWriter, EnumDecl enumDecl, ModuleDecl moduleDecl, string wrapperSymbol)
        {
            // Skip if this wrapper has already been emitted (nested enums may be processed multiple times)
            if (!_emittedWrapperSymbols.Add(wrapperSymbol))
            {
                return;
            }

            // Use the full module-qualified name for nested enums (e.g., BlinkID.SomeClass.ErrorType)
            var enumFullName = enumDecl.SwiftTypeName.ModuleQualifiedName;

            // Emit SBW_Utf8Slice struct if not already done for this module
            Utf8SliceEmitter.EmitIfNeeded(swiftWriter);

            // Determine if enum is frozen (affects return style)
            if (enumDecl.IsFrozen)
            {
                // Frozen enum: return Optional pointer directly
                // Returns nil (NULL) if rawValue is invalid
                swiftWriter.WriteLines($$"""
                    @_silgen_name("{{wrapperSymbol}}")
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
                swiftWriter.WriteLines($$"""
                    @_silgen_name("{{wrapperSymbol}}")
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
        /// Emits the C# Utf8Slice struct used for UTF-8 string marshalling.
        /// This is emitted inside the enum class.
        /// </summary>
        private static void EmitUtf8SliceStruct(CSharpWriter csWriter)
        {
            csWriter.WriteLines("""
                [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
                private struct Utf8Slice
                {
                    public IntPtr Ptr;
                    public nint Len;
                }

                """);
        }
    }
}
