// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Handles the emission of Swift operator declarations as C# operator overloads.
    /// </summary>
    public class OperatorHandler
    {
        private readonly ILogger _logger;

        /// <summary>
        /// Mapping of Swift operator symbols to C# operator symbols.
        /// </summary>
        private static readonly Dictionary<string, string> _csharpOperators = new()
        {
            // Binary arithmetic
            { "+", "+" },
            { "-", "-" },
            { "*", "*" },
            { "/", "/" },
            { "%", "%" },
            // Comparison
            { "==", "==" },
            { "!=", "!=" },
            { "<", "<" },
            { ">", ">" },
            { "<=", "<=" },
            { ">=", ">=" },
            // Bitwise
            { "&", "&" },
            { "|", "|" },
            { "^", "^" },
            { "<<", "<<" },
            { ">>", ">>" },
            // Unary
            { "!", "!" },
            { "~", "~" }
        };

        /// <summary>
        /// Mapping of Swift operators to C# method names used for PInvoke.
        /// </summary>
        private static readonly Dictionary<string, string> _pinvokeMethodNames = new()
        {
            { "+", "op_Addition" },
            { "-", "op_Subtraction" },
            { "*", "op_Multiply" },
            { "/", "op_Division" },
            { "%", "op_Modulus" },
            { "==", "op_Equality" },
            { "!=", "op_Inequality" },
            { "<", "op_LessThan" },
            { ">", "op_GreaterThan" },
            { "<=", "op_LessThanOrEqual" },
            { ">=", "op_GreaterThanOrEqual" },
            { "&", "op_BitwiseAnd" },
            { "|", "op_BitwiseOr" },
            { "^", "op_ExclusiveOr" },
            { "<<", "op_LeftShift" },
            { ">>", "op_RightShift" },
            { "!", "op_LogicalNot" },
            { "~", "op_OnesComplement" }
        };

        /// <summary>
        /// Operators that require paired operators in C#.
        /// Key is the operator that might be defined, value is the paired operator that must also exist.
        /// </summary>
        private static readonly Dictionary<string, string> _requiredPairs = new()
        {
            { "==", "!=" },
            { "!=", "==" },
            { "<", ">" },
            { ">", "<" },
            { "<=", ">=" },
            { ">=", "<=" }
        };

        public OperatorHandler(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Checks if the given Swift operator symbol is supported for C# emission.
        /// </summary>
        /// <param name="symbol">The Swift operator symbol.</param>
        /// <returns>True if the operator is supported.</returns>
        public static bool IsSupportedOperator(string symbol) => _csharpOperators.ContainsKey(symbol);

        /// <summary>
        /// Gets the C# operator symbol for a Swift operator symbol.
        /// </summary>
        /// <param name="symbol">The Swift operator symbol.</param>
        /// <returns>The C# operator symbol, or null if not supported.</returns>
        public static string? GetCSharpOperator(string symbol) =>
            _csharpOperators.TryGetValue(symbol, out var csOp) ? csOp : null;

        /// <summary>
        /// Gets the PInvoke method name for an operator.
        /// </summary>
        /// <param name="symbol">The operator symbol.</param>
        /// <returns>The PInvoke method name.</returns>
        public static string GetPInvokeMethodName(string symbol) =>
            _pinvokeMethodNames.TryGetValue(symbol, out var name)
                ? $"PInvoke_{name}"
                : $"PInvoke_op_{new string(symbol.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray())}";

        /// <summary>
        /// Gets the paired operator that is required for C# if the given operator is defined.
        /// </summary>
        /// <param name="symbol">The operator symbol.</param>
        /// <returns>The paired operator symbol, or null if no pair is required.</returns>
        public static string? GetRequiredPairedOperator(string symbol) =>
            _requiredPairs.TryGetValue(symbol, out var pair) ? pair : null;

        /// <summary>
        /// Emits a C# operator overload for the given operator declaration.
        /// </summary>
        /// <param name="csWriter">The C# code writer.</param>
        /// <param name="operatorDecl">The operator declaration.</param>
        /// <param name="typeDatabase">The type database.</param>
        /// <param name="pinvokeHelperContext">Optional P/Invoke helper context for generic types.</param>
        /// <param name="swiftWriter">Optional Swift writer for generating @_cdecl wrappers (xcframework mode).</param>
        /// <param name="emissionContext">Optional emission context for dedup tracking.</param>
        public bool EmitOperator(CSharpWriter csWriter, OperatorDecl operatorDecl, ITypeDatabase typeDatabase, PInvokeHelperContext? pinvokeHelperContext = null,
            SwiftWriter? swiftWriter = null, ModuleEmissionContext? emissionContext = null)
        {
            // Gate 0: operators never reach MemberValidationPipeline — this bool return is their only
            // refusal channel — so a poisoned operator is denied here or not at all.
            if (EmitterFaultGate.IsDenied(DeclIdFactory.ForOperator(operatorDecl), out var poisonDetails))
            {
                ReportCollector.RecordMemberSkipped(operatorDecl, SkipReason.EmitterFault, poisonDetails);
                return false;
            }

            var symbol = operatorDecl.OperatorSymbol;
            if (!IsSupportedOperator(symbol))
            {
                _logger.LogWarning($"Operator '{symbol}' is not supported for C# emission.");
                ReportCollector.RecordMemberSkipped(operatorDecl, SkipReason.UnsupportedType, "Operator symbol is not supported for C# emission.");
                return false;
            }

            var methodDecl = operatorDecl.UnderlyingMethod;
            var parentDecl = operatorDecl.ParentDecl as TypeDecl;
            if (parentDecl == null)
            {
                _logger.LogWarning($"Operator '{symbol}' has no valid parent type declaration.");
                ReportCollector.RecordMemberSkipped(operatorDecl, SkipReason.UnsupportedType, "Operator has no valid containing type.");
                return false;
            }

            var moduleDecl = operatorDecl.ModuleDecl;
            if (moduleDecl == null)
            {
                _logger.LogWarning($"Operator '{symbol}' has no module declaration.");
                ReportCollector.RecordMemberSkipped(operatorDecl, SkipReason.UnsupportedType, "Operator has no module declaration.");
                return false;
            }

            // Public operator on a @usableFromInline internal parent type. Dropped at
            // emission for EVERY parent kind, and that is correct, not over-broad — the
            // two parent kinds reach the same outcome for different reasons:
            //   * Frozen struct (ShouldEmitOperatorWrapper would return true below): the
            //     operator must be a @_cdecl wrapper — a direct CallConvSwift P/Invoke
            //     for a static operator function segfaults ILC on NativeAOT — and that
            //     wrapper body reconstructs the operands by naming the parent type,
            //     which the separate wrapper-compilation module cannot reference. No
            //     CallConvSwift fallback exists (arm 2b in WrapperValidation keeps only
            //     sync method/ctor/property/subscript, never operators), so the broken
            //     wrapper would emit-then-strip.
            //   * Class / non-frozen struct (no @_cdecl wrapper emitted): the operator
            //     is unreachable dead surface. A @usableFromInline internal type is not
            //     constructible from C# (its init's effective access is internal however
            //     it is declared), and a static C# operator cannot satisfy a protocol /
            //     interface requirement the way an arm-2b sync member can — so unlike a
            //     kept sync member it serves no dispatch purpose. Keeping it would add an
            //     uncallable operator bound to a direct CallConvSwift P/Invoke against an
            //     effective-internal symbol; dropping it is strictly cleaner.
            // Drop here, at emission, before the wrapper-decision below. Mirrors the
            // ParentModuleInternalNoFallback drop for async/closure methods in
            // MemberValidationPipeline.ValidateMethodEmission. Reference-safe:
            // ValidateAndEmitPairs only synthesizes a paired operator (e.g. !=) from
            // operators actually emitted, so a dropped operator synthesizes no partner.
            if (parentDecl.IsModuleInternal)
            {
                _logger.LogInformation($"Operator '{symbol}' on @usableFromInline internal parent '{parentDecl.Name}' dropped at emission: an internal-parent operator is either a parent-naming @_cdecl wrapper with no CallConvSwift fallback (frozen struct) or unreachable dead surface (class / non-frozen struct).");
                ReportCollector.RecordMemberSkipped(operatorDecl, SkipReason.ParentModuleInternalNoFallback,
                    "Public operator on a @usableFromInline internal parent type: a frozen-struct operator needs a @_cdecl wrapper that names the internal parent (no CallConvSwift fallback — a static-operator P/Invoke crashes ILC on NativeAOT), and a class / non-frozen-struct operator is unreachable (the internal parent is not constructible from C# and a static operator cannot satisfy a protocol requirement), so the operator is dropped either way.");
                return false;
            }

            // Determine if the containing type is a C# reference type (class) for null guard emission.
            // Reference types: ClassDecl, EnumDecl, non-frozen structs, frozen structs projected as classes.
            bool isReferenceType = parentDecl is ClassDecl || parentDecl is EnumDecl ||
                (parentDecl is StructDecl sd && (!sd.IsFrozen ||
                (typeDatabase.TryGetTypeRecord(sd.SwiftTypeName, out var structRecord) &&
                 MarshallingHelpers.IsFrozenStructProjectedAsClass(structRecord!))));

            // Create a MethodEnvironment for signature handling, passing the P/Invoke helper context
            var methodEnv = new MethodEnvironment(methodDecl, typeDatabase, pinvokeHelperContext: pinvokeHelperContext);

            // Check if this operator should use a @_cdecl wrapper.
            // Needed when struct has non-blittable fields (Bool), float/double fields, or is > 16 bytes.
            bool usesCdeclWrapper = ShouldEmitOperatorWrapper(operatorDecl, typeDatabase, swiftWriter, methodEnv);
            if (usesCdeclWrapper && swiftWriter != null && emissionContext != null)
            {
                var cdeclSymbol = EmitOperatorSwiftWrapper(swiftWriter, operatorDecl, parentDecl, emissionContext);
                if (cdeclSymbol != null)
                {
                    methodEnv.PromoteSymbol(cdeclSymbol);
                    methodDecl.UsesWrapperLibrary = true;
                }
                else
                {
                    usesCdeclWrapper = false;
                }
            }

            var signatureHandler = new SignatureHandler(methodEnv);

            // Check if signature is supported
            if (signatureHandler.GetWrapperSignature().ContainsPlaceholder)
            {
                _logger.LogWarning($"Operator {symbol} has unsupported signature: ({signatureHandler.GetWrapperSignature().ParametersString()}) -> {signatureHandler.GetWrapperSignature().ReturnType}");
                ReportCollector.RecordMemberSkipped(operatorDecl, SkipReason.UnsupportedSignature, "Operator signature contains unsupported placeholder type.");
                return false;
            }

            // Bug #4: C# operators cannot have generic type parameters. If any operand is a bare
            // generic type parameter (e.g., shift operators with generic second operand), skip.
            if (methodDecl.CSSignature.Skip(1).Any(arg => arg.IsGeneric))
            {
                _logger.LogWarning($"Operator '{symbol}' has generic type parameter operand — C# operators cannot be generic.");
                ReportCollector.RecordMemberSkipped(operatorDecl, SkipReason.UnsupportedSignature, "C# operators cannot have generic type parameters.");
                return false;
            }

            // Check if the P/Invoke signature references marshalling variables (e.g., arg0Buffer, T0Metadata)
            // that the operator wrapper scope doesn't declare. This happens for generic-type operators
            // where the P/Invoke builder renames operands for buffer marshalling we don't emit.
            if (pinvokeHelperContext != null)
            {
                var pInvokeSig = signatureHandler.GetPInvokeSignature();
                var wrapperSig = signatureHandler.GetWrapperSignature();
                bool requiresIndirectResult = MarshallingHelpers.MethodRequiresIndirectResult(methodEnv);
                var availableNames = new HashSet<string>(wrapperSig.Parameters.Select(p => p.Name));
                if (requiresIndirectResult)
                    availableNames.Add("swiftIndirectResult");
                bool hasUndeclaredRefs = pInvokeSig.Parameters.Any(p => !availableNames.Contains(p.Name));
                if (hasUndeclaredRefs)
                {
                    _logger.LogWarning($"Operator '{symbol}' on generic type requires buffer marshalling preamble — skipping.");
                    ReportCollector.RecordMemberSkipped(operatorDecl,
                        SkipReason.UnsupportedSignature, "Operator on generic type requires buffer marshalling.");
                    return false;
                }
            }

            // Get type name with generics for proper operator parameter types (fixes CS0563, CS0305)
            // Use resolved name from TypeDatabase to account for nested type renames
            var resolvedSimpleName = parentDecl.Name;
            if (typeDatabase.TryGetTypeRecord(parentDecl.SwiftTypeName, out var parentRecord))
            {
                var name = parentRecord.CSharpTypeName.Name;
                var lastDot = name.LastIndexOf('.');
                resolvedSimpleName = lastDot >= 0 ? name.Substring(lastDot + 1) : name;
            }
            var typeNameWithGenerics = $"{resolvedSimpleName}{GenericTypeEmitter.GetGenericParameterList(parentDecl)}";

            // Emit the operator wrapper and PInvoke
            EmitOperatorWrapper(csWriter, operatorDecl, signatureHandler, resolvedSimpleName, typeNameWithGenerics, pinvokeHelperContext, isReferenceType, methodEnv, usesCdeclWrapper);
            EmitOperatorPInvoke(csWriter, operatorDecl, methodEnv, signatureHandler, typeDatabase, pinvokeHelperContext, usesCdeclWrapper);
            ReportCollector.RecordMemberEmitted(operatorDecl);
            csWriter.WriteLine();
            return true;
        }

        /// <summary>
        /// Emits the C# operator overload method.
        /// </summary>
        /// <param name="csWriter">The C# code writer.</param>
        /// <param name="operatorDecl">The operator declaration.</param>
        /// <param name="signatureHandler">The signature handler.</param>
        /// <param name="typeName">The base type name (without generics).</param>
        /// <param name="typeNameWithGenerics">The type name with generic parameters (e.g., "DateResult&lt;T0&gt;").</param>
        /// <param name="pinvokeHelperContext">Optional P/Invoke helper context for generic types.</param>
        /// <param name="isReferenceType">Whether the containing type is a reference type.</param>
        /// <param name="methodEnv">The method environment for indirect result detection.</param>
        private void EmitOperatorWrapper(CSharpWriter csWriter, OperatorDecl operatorDecl, SignatureHandler signatureHandler, string typeName, string typeNameWithGenerics, PInvokeHelperContext? pinvokeHelperContext, bool isReferenceType, MethodEnvironment methodEnv, bool usesCdeclWrapper = false)
        {
            var symbol = operatorDecl.OperatorSymbol;
            var csOperator = GetCSharpOperator(symbol)!;
            var wrapperSignature = signatureHandler.GetWrapperSignature();
            var pInvokeSignature = signatureHandler.GetPInvokeSignature();

            // Bug #1: Detect if the operator return type requires indirect result allocation.
            // Non-frozen/class return types (BigUInt, BigInt) need SwiftIndirectResult.
            bool requiresIndirectResult = MarshallingHelpers.MethodRequiresIndirectResult(methodEnv);

            // Bug #10: Build generic parameter remapping for operators on generic types.
            // Operators are static methods that may have method-own generic params shadowing the
            // type's generics (e.g., τ_1_0 for the method vs τ_0_0 for the type). Since C# operators
            // can't be generic, remap method-own params back to type-level names (T1 → T0, etc.).
            Dictionary<string, string>? genericRemap = null;
            var parentTypeDecl = operatorDecl.ParentDecl as TypeDecl;
            if (parentTypeDecl is { IsGeneric: true } && operatorDecl.UnderlyingMethod.IsGeneric)
            {
                var typeParamNames = new HashSet<string>(parentTypeDecl.GenericParameters.Select(p => p.TypeName));
                var methodOnlyParams = operatorDecl.UnderlyingMethod.GenericParameters
                    .Where(p => !typeParamNames.Contains(p.TypeName))
                    .ToList();

                if (methodOnlyParams.Count > 0 && methodOnlyParams.Count <= parentTypeDecl.GenericParameters.Count)
                {
                    genericRemap = new Dictionary<string, string>();
                    int typeParamCount = parentTypeDecl.GenericParameters.Count;
                    for (int i = 0; i < methodOnlyParams.Count; i++)
                    {
                        var methodParamCsName = NameProvider.GetCSharpGenericParameterName(
                            methodOnlyParams[i], typeParamCount + i);
                        var typeParamCsName = NameProvider.GetCSharpGenericParameterName(
                            parentTypeDecl.GenericParameters[i], i);
                        if (methodParamCsName != typeParamCsName)
                            genericRemap[methodParamCsName] = typeParamCsName;
                    }
                }
            }

            // Helper function to fix generic type names in operator signatures
            // When the type is the containing type, replace with the generic version
            // e.g., "DateResult" -> "DateResult<T0>" for generic types
            string FixGenericTypeName(string type) =>
                type == typeName ? typeNameWithGenerics : type;

            // Helper to apply method-own → type-level generic parameter remapping (Bug #10)
            string ApplyRemap(string type) => ApplyGenericRemap(type, genericRemap);

            if (operatorDecl.Kind == OperatorKind.Binary)
            {
                // Binary operator: public static ReturnType operator +(Type left, Type right)
                var parameters = wrapperSignature.Parameters.ToArray();
                if (parameters.Length < 2)
                {
                    _logger.LogWarning($"Binary operator '{symbol}' has fewer than 2 parameters.");
                    return;
                }

                var leftParam = parameters[0];
                var rightParam = parameters[1];
                var returnType = ApplyRemap(FixGenericTypeName(wrapperSignature.ReturnType));
                var leftType = ApplyRemap(FixGenericTypeName(leftParam.Type.PublicTypeName));
                var rightType = ApplyRemap(FixGenericTypeName(rightParam.Type.PublicTypeName));

                XmlDocCommentEmitter.EmitDocComment(csWriter, operatorDecl);
                AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, operatorDecl, operatorDecl.ParentDecl, emitObsolete: true);
                csWriter.WriteLine($"public static {returnType} operator {csOperator}({leftType} {leftParam.Name}, {rightType} {rightParam.Name})");
                csWriter.WriteLine("{");
                csWriter.Indent++;

                // Emit null guards for equality/inequality operators on reference types.
                // Without these, `obj == null` or `obj != null` would call .Payload on null and throw NRE.
                if (isReferenceType && (csOperator == "==" || csOperator == "!="))
                {
                    if (csOperator == "==")
                    {
                        csWriter.WriteLine($"if ({leftParam.Name} is null) return {rightParam.Name} is null;");
                        csWriter.WriteLine($"if ({rightParam.Name} is null) return false;");
                    }
                    else
                    {
                        csWriter.WriteLine($"if ({leftParam.Name} is null) return {rightParam.Name} is not null;");
                        csWriter.WriteLine($"if ({rightParam.Name} is null) return true;");
                    }
                }

                // The Swift @_cdecl operator wrapper is availability-gated (see EmitSwiftWrapper), so on an
                // OS below the operator's effective floor its body dereferences a weak-linked, null gated
                // symbol — an uncatchable SIGSEGV. Throw a catchable exception before reaching the P/Invoke.
                EmitOperatorAvailabilityGuard(csWriter, operatorDecl, csOperator);

                // Emit handle extraction for ObjC-bridged/rooted parameters
                EmitObjCHandleExtraction(csWriter, pInvokeSignature, wrapperSignature);

                if (usesCdeclWrapper)
                {
                    // @_cdecl path: marshal struct params to pointers, use SwiftIndirectResult for struct returns
                    EmitCdeclOperatorCall(csWriter, symbol, returnType, parameters.Select(p => p.Name).ToArray(), pinvokeHelperContext, methodEnv, leftType);
                }
                else
                {
                    // Wrap in unsafe block when indirect result uses pointer operations
                    if (requiresIndirectResult) { csWriter.WriteLine("unsafe {"); csWriter.Indent++; }

                    // Emit P/Invoke call and return
                    EmitOperatorPInvokeCall(csWriter, symbol, returnType, pInvokeSignature, pinvokeHelperContext, requiresIndirectResult, methodEnv);

                    if (requiresIndirectResult) { csWriter.Indent--; csWriter.WriteLine("}"); }
                }

                csWriter.Indent--;
                csWriter.WriteLine("}");
            }
            else
            {
                // Unary operator: public static ReturnType operator !(Type operand)
                var parameters = wrapperSignature.Parameters.ToArray();
                if (parameters.Length < 1)
                {
                    _logger.LogWarning($"Unary operator '{symbol}' has no parameters.");
                    return;
                }

                var operand = parameters[0];
                var returnType = ApplyRemap(FixGenericTypeName(wrapperSignature.ReturnType));
                var operandType = ApplyRemap(FixGenericTypeName(operand.Type.PublicTypeName));

                XmlDocCommentEmitter.EmitDocComment(csWriter, operatorDecl);
                AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, operatorDecl, operatorDecl.ParentDecl, emitObsolete: true);
                csWriter.WriteLine($"public static {returnType} operator {csOperator}({operandType} {operand.Name})");
                csWriter.WriteLine("{");
                csWriter.Indent++;

                // See the binary-operator note above: guard the weak-linked gated-symbol crash before the P/Invoke.
                EmitOperatorAvailabilityGuard(csWriter, operatorDecl, csOperator);

                // Emit handle extraction for ObjC-bridged/rooted parameters
                EmitObjCHandleExtraction(csWriter, pInvokeSignature, wrapperSignature);

                if (usesCdeclWrapper)
                {
                    // @_cdecl path: marshal struct params to pointers
                    EmitCdeclOperatorCall(csWriter, symbol, returnType, new[] { operand.Name }, pinvokeHelperContext, methodEnv, operandType);
                }
                else
                {
                    // Wrap in unsafe block when indirect result uses pointer operations
                    if (requiresIndirectResult) { csWriter.WriteLine("unsafe {"); csWriter.Indent++; }

                    // Emit P/Invoke call and return
                    EmitOperatorPInvokeCall(csWriter, symbol, returnType, pInvokeSignature, pinvokeHelperContext, requiresIndirectResult, methodEnv);

                    if (requiresIndirectResult) { csWriter.Indent--; csWriter.WriteLine("}"); }
                }

                csWriter.Indent--;
                csWriter.WriteLine("}");
            }
        }

        /// <summary>
        /// Emits the runtime OS-version guard for a generated operator. The floor is the operator's own
        /// availability merged with its full enclosing-type chain, so an operator on an OS-gated type is
        /// guarded even when it declares no stricter floor of its own. Mirrors the guard the regular
        /// method/constructor paths get via <see cref="AvailabilityAttributeEmitter.EmitRuntimeAvailabilityGuard"/>.
        /// </summary>
        private static void EmitOperatorAvailabilityGuard(CSharpWriter csWriter, OperatorDecl operatorDecl, string csOperator)
        {
            var parentName = operatorDecl.ParentDecl?.Name;
            var description = string.IsNullOrEmpty(parentName)
                ? $"operator {csOperator}"
                : $"{parentName}.operator {csOperator}";
            AvailabilityAttributeEmitter.EmitRuntimeAvailabilityGuard(
                csWriter,
                WrapperEmitterHelpers.MergeAvailability(operatorDecl.AvailabilityAnnotations, operatorDecl.ParentDecl),
                description);
        }

        /// <summary>
        /// Emits the P/Invoke call and return statement for an operator.
        /// Handles both direct returns and indirect result allocation.
        /// </summary>
        private void EmitOperatorPInvokeCall(CSharpWriter csWriter, string symbol, string returnType, Signature pInvokeSignature, PInvokeHelperContext? pinvokeHelperContext, bool requiresIndirectResult, MethodEnvironment? methodEnv = null)
        {
            var pinvokeName = GetPInvokeMethodName(symbol);
            var callArgs = pInvokeSignature.CallArgumentsString();

            if (requiresIndirectResult)
            {
                // Determine if the buffer should be freed after reading the return value.
                // Non-frozen structs and complex enums transfer ownership to SafeHandle via
                // NewFromPayload — the buffer must NOT be freed. All other types copy data out.
                bool transfersOwnership = false;
                if (methodEnv != null)
                {
                    var returnArg = methodEnv.MethodDecl.CSSignature.First();
                    if (returnArg.SwiftTypeSpec is NamedTypeSpec retNts && retNts.HasModule())
                    {
                        var retTypeName = SwiftTypeName.FromTypeSpec(retNts);
                        if (methodEnv.TypeDatabase.TryGetTypeRecord(retTypeName, out var retRecord))
                        {
                            bool isNonFrozenStruct = retRecord.Kind == TypeRecordKind.Struct &&
                                !MarshallingHelpers.IsTypeFrozen(retRecord);
                            bool isComplexEnum = retRecord.Kind == TypeRecordKind.Enum &&
                                !retRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum);
                            transfersOwnership = isNonFrozenStruct || isComplexEnum;
                        }
                    }
                }

                // Declare _cdeclBuf before try so it's accessible in finally for cleanup.
                csWriter.WriteLine("void* _cdeclBuf = null;");
                csWriter.WriteLine("try");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine($"var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<{returnType}>();");
                csWriter.WriteLine($"_cdeclBuf = NativeMemory.Alloc((nuint)returnMetadata.Size);");
                csWriter.WriteLine($"var swiftIndirectResult = new SwiftIndirectResult(_cdeclBuf);");

                // Call P/Invoke (void return — writes through SwiftIndirectResult)
                if (pinvokeHelperContext != null)
                {
                    var metadataArgs = string.Join(", ", pinvokeHelperContext.GetMetadataArgumentList());
                    var fullArgs = string.IsNullOrEmpty(callArgs) ? metadataArgs : $"{callArgs}, {metadataArgs}";
                    csWriter.WriteLine($"{pinvokeHelperContext.HelperClassName}.{pinvokeName}({fullArgs});");
                }
                else
                {
                    csWriter.WriteLine($"{pinvokeName}({callArgs});");
                }

                csWriter.WriteLine($"return SwiftMarshal.MarshalFromSwift<{returnType}>(new IntPtr(swiftIndirectResult.Value));");
                csWriter.Indent--;
                csWriter.WriteLine("}");
                if (transfersOwnership)
                {
                    // Ownership transfers to SafeHandle — only free on exception
                    csWriter.WriteLine("catch { NativeMemory.Free(_cdeclBuf); throw; }");
                }
                else
                {
                    // Data copied out — always free the temp buffer
                    csWriter.WriteLine("finally { NativeMemory.Free(_cdeclBuf); }");
                }
            }
            else
            {
                // Direct call path
                if (pinvokeHelperContext != null)
                {
                    var metadataArgs = string.Join(", ", pinvokeHelperContext.GetMetadataArgumentList());
                    var fullArgs = string.IsNullOrEmpty(callArgs) ? metadataArgs : $"{callArgs}, {metadataArgs}";
                    if (returnType == "void")
                        csWriter.WriteLine($"{pinvokeHelperContext.HelperClassName}.{pinvokeName}({fullArgs});");
                    else
                        csWriter.WriteLine($"return {pinvokeHelperContext.HelperClassName}.{pinvokeName}({fullArgs});");
                }
                else
                {
                    if (returnType == "void")
                        csWriter.WriteLine($"{pinvokeName}({callArgs});");
                    else
                        csWriter.WriteLine($"return {pinvokeName}({callArgs});");
                }
            }
        }

        /// <summary>
        /// Emits the @_cdecl operator call with pointer marshalling for struct params and
        /// SwiftIndirectResult for struct returns. Bool returns use direct return.
        /// </summary>
        private void EmitCdeclOperatorCall(CSharpWriter csWriter, string symbol, string returnType, string[] paramNames, PInvokeHelperContext? pinvokeHelperContext, MethodEnvironment methodEnv, string? paramTypeName = null)
        {
            var pinvokeName = GetPInvokeMethodName(symbol);
            bool returnsBool = returnType == "bool";
            // For Bool-returning operators (comparisons), the param type is the struct type (not bool)
            var structTypeName = paramTypeName ?? returnType;

            csWriter.WriteLine("unsafe");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // Stackalloc struct params to get pointers
            foreach (var name in paramNames)
            {
                csWriter.WriteLine($"byte* {name}Bytes = stackalloc byte[Unsafe.SizeOf<{structTypeName}>()];");
                csWriter.WriteLine($"Unsafe.Write({name}Bytes, {name});");
            }

            if (returnsBool)
            {
                // Bool return: direct call with pointer args
                var ptrArgs = string.Join(", ", paramNames.Select(n => $"(IntPtr){n}Bytes"));
                csWriter.WriteLine($"return {pinvokeName}({ptrArgs});");
            }
            else
            {
                // Struct return: use SwiftIndirectResult
                csWriter.WriteLine("void* _cdeclBuf = null;");
                csWriter.WriteLine("try");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine($"var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<{returnType}>();");
                csWriter.WriteLine("_cdeclBuf = NativeMemory.Alloc((nuint)returnMetadata.Size);");
                csWriter.WriteLine("var swiftIndirectResult = new SwiftIndirectResult(_cdeclBuf);");

                var ptrArgs = string.Join(", ", paramNames.Select(n => $"(IntPtr){n}Bytes"));
                csWriter.WriteLine($"{pinvokeName}(swiftIndirectResult, {ptrArgs});");
                csWriter.WriteLine($"return SwiftMarshal.MarshalFromSwift<{returnType}>(new IntPtr(swiftIndirectResult.Value));");

                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine("finally { NativeMemory.Free(_cdeclBuf); }");
            }

            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        /// <summary>
        /// Emits handle extraction for ObjC-bridged/rooted parameters in operator wrappers.
        /// For each parameter with ObjCBridged type, emits: IntPtr {name}Handle = {name}.Handle;
        /// This is needed because CallArgumentsString() generates "{name}Handle" references.
        /// </summary>
        private static void EmitObjCHandleExtraction(CSharpWriter csWriter, Signature pInvokeSignature, Signature wrapperSignature)
        {
            foreach (var param in pInvokeSignature.Parameters)
            {
                if (param.Type is MarshalledType.ObjCBridged)
                {
                    // Find the matching wrapper parameter name for the source object
                    var wrapperParam = wrapperSignature.Parameters.FirstOrDefault(p => p.Name == param.Name);
                    var sourceName = wrapperParam?.Name ?? param.Name;
                    csWriter.WriteLine($"IntPtr {sourceName}Handle = {sourceName}.Handle;");
                }
            }
        }

        /// <summary>
        /// Applies generic parameter remapping to a type string using word-boundary matching.
        /// Used for Bug #10: operator method-own generic params (T1) → type-level params (T0).
        /// </summary>
        private static string ApplyGenericRemap(string type, Dictionary<string, string>? remap)
        {
            if (remap == null || remap.Count == 0) return type;
            foreach (var kvp in remap)
            {
                type = Regex.Replace(type, $@"\b{kvp.Key}\b", kvp.Value);
            }
            return type;
        }

        /// <summary>
        /// Emits the PInvoke declaration for an operator.
        /// When using @_cdecl wrapper, emits Cdecl calling convention with pointer params
        /// and SwiftIndirectResult for struct returns.
        /// </summary>
        /// <param name="pinvokeHelperContext">Optional P/Invoke helper context for generic types.</param>
        private void EmitOperatorPInvoke(CSharpWriter csWriter, OperatorDecl operatorDecl, MethodEnvironment methodEnv, SignatureHandler signatureHandler, ITypeDatabase typeDatabase, PInvokeHelperContext? pinvokeHelperContext, bool usesCdeclWrapper = false)
        {
            var methodDecl = operatorDecl.UnderlyingMethod;
            var moduleDecl = methodDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(methodDecl.ModuleDecl));

            var pinvokeName = GetPInvokeMethodName(operatorDecl.OperatorSymbol);
            // Use wrapper library path when operator has @_cdecl wrapper
            var libPath = methodDecl.UsesWrapperLibrary
                ? typeDatabase.AsyncLibraryName ?? typeDatabase.GetLibraryPath(moduleDecl.Name)
                : typeDatabase.GetLibraryPath(moduleDecl.Name);
            var pInvokeSignature = signatureHandler.GetPInvokeSignature();

            // For @_cdecl wrappers, adjust calling convention and parameter types.
            // When the operator @_cdecl wrapper was NOT emitted (e.g. operator on a
            // class / non-frozen struct parent — see ShouldEmitOperatorWrapper guards),
            // the EntryPoint stays the original Swift-mangled `$s…` symbol, which
            // Swift compiles with the Swift calling convention. Pairing that symbol
            // with CallConvCdecl (the previous hardcoded value) reads return values
            // and self/parameters from the wrong registers — see
            // Pairing a Swift-mangled symbol with CallConvCdecl reads return values
            // and self/parameters from the wrong registers. Use Cdecl iff the wrapper actually emitted.
            var callingConvention = usesCdeclWrapper
                ? PInvokeCallingConvention.Cdecl
                : PInvokeCallingConvention.Swift;
            string returnType;
            string parametersString;

            if (usesCdeclWrapper)
            {
                var returnArg = methodDecl.CSSignature.FirstOrDefault();
                bool returnsBool = returnArg != null && MarshallingHelpers.IsBoolType(returnArg.SwiftTypeSpec);

                // Struct returns → void (result via SwiftIndirectResult), Bool returns → bool
                returnType = returnsBool ? pInvokeSignature.ReturnType : "void";

                // Build pointer parameter list
                var paramParts = new List<string>();
                if (!returnsBool)
                    paramParts.Add("SwiftIndirectResult swiftIndirectResult");
                foreach (var p in pInvokeSignature.Parameters)
                    paramParts.Add($"IntPtr {p.Name}");
                parametersString = string.Join(", ", paramParts);
            }
            else
            {
                returnType = pInvokeSignature.ReturnType;
                parametersString = pInvokeSignature.PInvokeParametersString();
            }

            if (pinvokeHelperContext != null)
            {
                // Collect to helper context for generic types. The same callingConvention
                // selection as the direct path applies: when the @_cdecl wrapper was NOT
                // emitted (operator on a class / non-frozen-struct parent), the EntryPoint
                // remains the Swift-mangled symbol and MUST be paired with CallConvSwift
                // (PInvokeDeclaration's default of Cdecl reproduces the same register-
                // mismatch that arises when a Swift-mangled symbol is paired with Cdecl for
                // non-generic parents).
                var declaration = new PInvokeDeclaration
                {
                    LibraryPath = libPath,
                    EntryPoint = methodEnv.EmissionSymbol,
                    MethodName = pinvokeName,
                    ReturnType = returnType,
                    ParametersString = parametersString,
                    CallingConvention = callingConvention,
                    IsAsync = false,
                    MetadataParameters = pinvokeHelperContext.GetMetadataParameterDeclarations()
                };
                pinvokeHelperContext.AddDeclaration(declaration);
            }
            else
            {
                // Emit directly for non-generic types
                PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
                {
                    LibraryPath = libPath,
                    EntryPoint = methodEnv.EmissionSymbol,
                    MethodName = pinvokeName,
                    ReturnType = returnType,
                    ParametersString = parametersString,
                    CallingConvention = callingConvention,
                    IsUnsafe = usesCdeclWrapper
                });
            }
        }

        /// <summary>
        /// Emits a synthesized paired operator (e.g., != from ==).
        /// </summary>
        /// <param name="csWriter">The C# code writer.</param>
        /// <param name="existingOperator">The existing operator that has been defined.</param>
        /// <param name="missingOperator">The paired operator that needs to be synthesized.</param>
        /// <param name="typeName">The name of the containing type.</param>
        public void EmitSynthesizedPairedOperator(CSharpWriter csWriter, OperatorDecl existingOperator, string missingOperator, string typeName, bool isReferenceType = false)
        {
            var existingSymbol = existingOperator.OperatorSymbol;
            var methodDecl = existingOperator.UnderlyingMethod;

            // Get the parameter types from the existing operator
            var returnArg = methodDecl.CSSignature.First();
            var paramArgs = methodDecl.CSSignature.Skip(1).ToArray();

            if (paramArgs.Length < 2)
            {
                _logger.LogWarning($"Cannot synthesize paired operator '{missingOperator}' from '{existingSymbol}' - insufficient parameters.");
                return;
            }

            if (missingOperator == "!=" && existingSymbol == "==")
            {
                // Synthesize != from ==: negate the result
                csWriter.WriteLine($"public static bool operator !=({typeName} left, {typeName} right)");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                if (isReferenceType)
                {
                    csWriter.WriteLine("if (left is null) return right is not null;");
                    csWriter.WriteLine("if (right is null) return true;");
                }
                csWriter.WriteLine("return !(left == right);");
                csWriter.Indent--;
                csWriter.WriteLine("}");
            }
            else if (missingOperator == "==" && existingSymbol == "!=")
            {
                // Synthesize == from !=: negate the result
                csWriter.WriteLine($"public static bool operator ==({typeName} left, {typeName} right)");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                if (isReferenceType)
                {
                    csWriter.WriteLine("if (left is null) return right is null;");
                    csWriter.WriteLine("if (right is null) return false;");
                }
                csWriter.WriteLine("return !(left != right);");
                csWriter.Indent--;
                csWriter.WriteLine("}");
            }
            else if (missingOperator == ">" && existingSymbol == "<")
            {
                // Synthesize > from <: swap parameters
                csWriter.WriteLine($"public static bool operator >({typeName} left, {typeName} right)");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine("return right < left;");
                csWriter.Indent--;
                csWriter.WriteLine("}");
            }
            else if (missingOperator == "<" && existingSymbol == ">")
            {
                // Synthesize < from >: swap parameters
                csWriter.WriteLine($"public static bool operator <({typeName} left, {typeName} right)");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine("return right > left;");
                csWriter.Indent--;
                csWriter.WriteLine("}");
            }
            else if (missingOperator == ">=" && existingSymbol == "<=")
            {
                // Synthesize >= from <=: swap parameters
                csWriter.WriteLine($"public static bool operator >=({typeName} left, {typeName} right)");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine("return right <= left;");
                csWriter.Indent--;
                csWriter.WriteLine("}");
            }
            else if (missingOperator == "<=" && existingSymbol == ">=")
            {
                // Synthesize <= from >=: swap parameters
                csWriter.WriteLine($"public static bool operator <=({typeName} left, {typeName} right)");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine("return right >= left;");
                csWriter.Indent--;
                csWriter.WriteLine("}");
            }
            else
            {
                _logger.LogWarning($"Cannot synthesize paired operator '{missingOperator}' from '{existingSymbol}'.");
            }
        }

        /// <summary>
        /// Validates operators and emits any missing paired operators.
        /// </summary>
        /// <param name="csWriter">The C# code writer.</param>
        /// <param name="operators">The list of operator declarations.</param>
        /// <param name="typeName">The name of the containing type.</param>
        public void ValidateAndEmitPairs(CSharpWriter csWriter, List<OperatorDecl> operators, string typeName, ISet<string> emittedSymbols, bool isReferenceType = false)
        {
            var definedSymbols = new HashSet<string>(emittedSymbols);

            foreach (var op in operators)
            {
                var symbol = op.OperatorSymbol;
                // Only synthesize from operators that were actually emitted.
                if (!definedSymbols.Contains(symbol)) continue;

                var pairedSymbol = GetRequiredPairedOperator(symbol);
                if (pairedSymbol != null && !definedSymbols.Contains(pairedSymbol))
                {
                    // Need to synthesize the paired operator
                    _logger.LogInformation($"Synthesizing paired operator '{pairedSymbol}' from '{symbol}' for type '{typeName}'.");
                    EmitSynthesizedPairedOperator(csWriter, op, pairedSymbol, typeName, isReferenceType);
                    ReportCollector.RecordMemberSynthesized(BindingItemKind.Operator, pairedSymbol, op.ParentDecl);
                    // Mark as defined to avoid duplicate synthesis
                    definedSymbols.Add(pairedSymbol);
                    emittedSymbols.Add(pairedSymbol);
                    csWriter.WriteLine();
                }
            }
        }

        /// <summary>
        /// Checks if the operators list contains an explicit equality operator.
        /// </summary>
        /// <param name="operators">The list of operators.</param>
        /// <returns>True if == operator is explicitly defined.</returns>
        public static bool HasExplicitEqualityOperator(List<OperatorDecl> operators) =>
            operators.Any(o => o.OperatorSymbol == "==");

        /// <summary>
        /// Checks if the operators list contains an explicit inequality operator.
        /// </summary>
        /// <param name="operators">The list of operators.</param>
        /// <returns>True if != operator is explicitly defined.</returns>
        public static bool HasExplicitInequalityOperator(List<OperatorDecl> operators) =>
            operators.Any(o => o.OperatorSymbol == "!=");

        /// <summary>
        /// Checks if an equality operator will be emitted (either explicit or synthesized from !=).
        /// </summary>
        /// <param name="operators">The list of operators.</param>
        /// <returns>True if == operator will be present in the generated code.</returns>
        public static bool WillHaveEqualityOperator(List<OperatorDecl> operators) =>
            HasExplicitEqualityOperator(operators) || HasExplicitInequalityOperator(operators);

        /// <summary>
        /// Checks if an inequality operator will be emitted (either explicit or synthesized from ==).
        /// </summary>
        /// <param name="operators">The list of operators.</param>
        /// <returns>True if != operator will be present in the generated code.</returns>
        public static bool WillHaveInequalityOperator(List<OperatorDecl> operators) =>
            HasExplicitInequalityOperator(operators) || HasExplicitEqualityOperator(operators);

        // ==================== Operator @_cdecl Wrapper ====================

        /// <summary>
        /// Determines if an operator should use a @_cdecl wrapper.
        /// All eligible operators get @_cdecl wrappers — CallConvSwift is eliminated.
        /// </summary>
        /// <summary>
        /// Full ABI safety check: delegates to RequiresCdeclForAbiSafety which checks
        /// non-blittable types (Bool), float fields, large structs, etc.
        /// Requires a MethodEnvironment — call from EmitOperator after env is created.
        /// </summary>
        internal static bool ShouldEmitOperatorWrapper(OperatorDecl operatorDecl, ITypeDatabase typeDatabase, SwiftWriter? swiftWriter, MethodEnvironment? methodEnv = null)
        {
            if (swiftWriter == null) return false;
            if (!WrapperValidation.IsXCFrameworkMode(typeDatabase)) return false;

            // Only frozen structs projected as C# value types need operator wrappers.
            // Non-frozen structs, classes, and enums use different P/Invoke strategies.
            var parentDecl = operatorDecl.ParentDecl as StructDecl;
            if (parentDecl == null || !parentDecl.IsFrozen) return false;

            // Skip frozen structs projected as classes (they use SafeHandle, different issue)
            if (typeDatabase.TryGetTypeRecord(parentDecl.SwiftTypeName, out var record) &&
                MarshallingHelpers.IsFrozenStructProjectedAsClass(record))
                return false;

            // Generic frozen structs need metadata arguments that the @_cdecl operator
            // wrapper path doesn't emit. Fall back to RequiresCdeclForAbiSafety for those.
            if (parentDecl.GenericParameters != null && parentDecl.GenericParameters.Count > 0)
            {
                if (methodEnv != null)
                    return WrapperValidation.RequiresCdeclForAbiSafety(methodEnv);
                return true; // Conservative fallback
            }

            // Always emit @_cdecl wrappers for non-generic frozen struct operators.
            // NativeAOT's ILC segfaults when compiling CallConvSwift P/Invoke stubs
            // for static operator functions, even on simple blittable structs (Int32/UInt32).
            // Using @_cdecl wrappers universally avoids this and is safe for both runtimes.
            return true;
        }

        /// <summary>
        /// Gets the @_cdecl symbol name for an operator wrapper.
        /// </summary>
        private static string GetOperatorCdeclSymbol(string moduleName, string typeName, string operatorSymbol, string originalMangledName)
        {
            var safeTypeName = typeName.Replace(".", "_");
            var opName = _pinvokeMethodNames.TryGetValue(operatorSymbol, out var name) ? name : "op";
            var hash = EmitterUtility.DeterministicHash8(originalMangledName);
            return $"SBW_{moduleName}_{safeTypeName}_{opName}_{hash}";
        }

        /// <summary>
        /// Emits a Swift @_cdecl wrapper for an operator P/Invoke.
        /// Uses C-compatible types: UnsafeRawPointer for struct params, UnsafeMutableRawPointer
        /// for struct returns (via resultPtr). Bool returns are direct (C-compatible).
        /// </summary>
        /// <returns>The @_cdecl symbol name, or null if emission was skipped.</returns>
        private static string? EmitOperatorSwiftWrapper(
            SwiftWriter swiftWriter,
            OperatorDecl operatorDecl,
            TypeDecl parentDecl,
            ModuleEmissionContext ctx)
        {
            var methodDecl = operatorDecl.UnderlyingMethod;
            var moduleName = parentDecl.SwiftTypeName.Module;
            var symbolName = GetOperatorCdeclSymbol(moduleName, parentDecl.Name, operatorDecl.OperatorSymbol, methodDecl.MangledName);

            // operator wrappers carry an `_{opName}_` segment
            // (e.g. `_Equal_`, `_Add_`) that namespaces them away from regular
            // `_{methodName}_` symbols. The mangled-name hash makes the symbol unique
            // per overload. Per-kind method bucket is collision-safe.
            // Attribute to the OPERATOR, not its UnderlyingMethod: every report row for this
            // declaration is built by ReportCollector from `operatorDecl` (Kind=Operator, name =
            // the operator symbol, container = operatorDecl.ParentDecl). Registering the
            // underlying method's id instead yields Kind=Method under the method's name, which
            // can never join against the reporting identity for the same declaration.
            if (!ctx.TryAddMethodWrapperSymbol(symbolName, DeclIdFactory.ForOperator(operatorDecl)))
                return symbolName; // Already emitted, but still use it

            var moduleQualifiedSwiftName = parentDecl.SwiftTypeName.ModuleQualifiedName;
            var funcHash = EmitterUtility.DeterministicHash8(symbolName);
            var symbol = operatorDecl.OperatorSymbol;
            bool isUnary = operatorDecl.Kind == OperatorKind.Unary;

            // Determine return type in Swift
            var returnArg = methodDecl.CSSignature.FirstOrDefault();
            bool returnsBool = returnArg != null && MarshallingHelpers.IsBoolType(returnArg.SwiftTypeSpec);
            // Struct returns need resultPtr (structs are not C-representable in @_cdecl)
            bool needsResultPtr = !returnsBool;

            // Resolve the actual Swift return type (may differ from parent type for some operators)
            string swiftReturnType = moduleQualifiedSwiftName;
            if (returnArg?.SwiftTypeSpec is NamedTypeSpec retNts && retNts.HasModule())
                swiftReturnType = retNts.Name;

            swiftWriter.WriteLine();
            swiftWriter.WriteLines($$"""
                // Operator @_cdecl wrapper for {{moduleQualifiedSwiftName}}.{{symbol}}.
                // Routes operator through C calling convention to avoid CallConvSwift crash on NativeAOT.
                """);

            // Emit availability annotations from the operator and ancestor chain.
            // @_cdecl wrappers are top-level functions and don't inherit enclosing type availability.
            var availability = WrapperEmitterHelpers.MergeAvailability(operatorDecl.AvailabilityAnnotations, parentDecl);
            WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);

            swiftWriter.WriteLines($$"""
                @_cdecl("{{symbolName}}")
                """);

            if (isUnary)
            {
                var paramList = needsResultPtr
                    ? "_ resultPtr: UnsafeMutableRawPointer, _ operand: UnsafeRawPointer"
                    : "_ operand: UnsafeRawPointer";
                var returnClause = needsResultPtr ? "" : " -> Bool";
                swiftWriter.WriteLine($"public func _sbw_op_{funcHash}({paramList}){returnClause} {{");
                swiftWriter.Indent++;
                swiftWriter.WriteLine($"let op = operand.load(as: {moduleQualifiedSwiftName}.self)");
                if (needsResultPtr)
                {
                    swiftWriter.WriteLine($"let result = {symbol}op");
                    swiftWriter.WriteLine($"resultPtr.initializeMemory(as: {swiftReturnType}.self, repeating: result, count: 1)");
                }
                else
                {
                    swiftWriter.WriteLine($"return {symbol}op");
                }
                swiftWriter.Indent--;
            }
            else
            {
                var paramList = needsResultPtr
                    ? "_ resultPtr: UnsafeMutableRawPointer, _ lhs: UnsafeRawPointer, _ rhs: UnsafeRawPointer"
                    : "_ lhs: UnsafeRawPointer, _ rhs: UnsafeRawPointer";
                var returnClause = needsResultPtr ? "" : " -> Bool";
                swiftWriter.WriteLine($"public func _sbw_op_{funcHash}({paramList}){returnClause} {{");
                swiftWriter.Indent++;
                swiftWriter.WriteLine($"let l = lhs.load(as: {moduleQualifiedSwiftName}.self)");
                swiftWriter.WriteLine($"let r = rhs.load(as: {moduleQualifiedSwiftName}.self)");
                if (needsResultPtr)
                {
                    swiftWriter.WriteLine($"let result = l {symbol} r");
                    swiftWriter.WriteLine($"resultPtr.initializeMemory(as: {swiftReturnType}.self, repeating: result, count: 1)");
                }
                else
                {
                    swiftWriter.WriteLine($"return l {symbol} r");
                }
                swiftWriter.Indent--;
            }

            swiftWriter.WriteLine("}");
            return symbolName;
        }

    }
}
