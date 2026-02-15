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
            _pinvokeMethodNames.TryGetValue(symbol, out var name) ? $"PInvoke_{name}" : $"PInvoke_op_{symbol}";

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
        public bool EmitOperator(CSharpWriter csWriter, OperatorDecl operatorDecl, ITypeDatabase typeDatabase, PInvokeHelperContext? pinvokeHelperContext = null)
        {
            var symbol = operatorDecl.OperatorSymbol;
            if (!IsSupportedOperator(symbol))
            {
                _logger.LogWarning($"Operator '{symbol}' is not supported for C# emission.");
                ReportCollector.RecordMemberSkipped(BindingItemKind.Operator, symbol, operatorDecl.ParentDecl, SkipReason.UnsupportedType, "Operator symbol is not supported for C# emission.");
                return false;
            }

            var methodDecl = operatorDecl.UnderlyingMethod;
            var parentDecl = operatorDecl.ParentDecl as TypeDecl;
            if (parentDecl == null)
            {
                _logger.LogWarning($"Operator '{symbol}' has no valid parent type declaration.");
                ReportCollector.RecordMemberSkipped(BindingItemKind.Operator, symbol, operatorDecl.ParentDecl, SkipReason.UnsupportedType, "Operator has no valid containing type.");
                return false;
            }

            var moduleDecl = operatorDecl.ModuleDecl;
            if (moduleDecl == null)
            {
                _logger.LogWarning($"Operator '{symbol}' has no module declaration.");
                ReportCollector.RecordMemberSkipped(BindingItemKind.Operator, symbol, operatorDecl.ParentDecl, SkipReason.UnsupportedType, "Operator has no module declaration.");
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
            var signatureHandler = new SignatureHandler(methodEnv);

            // Check if signature is supported
            if (signatureHandler.GetWrapperSignature().ContainsPlaceholder)
            {
                _logger.LogWarning($"Operator {symbol} has unsupported signature: ({signatureHandler.GetWrapperSignature().ParametersString()}) -> {signatureHandler.GetWrapperSignature().ReturnType}");
                ReportCollector.RecordMemberSkipped(BindingItemKind.Operator, symbol, operatorDecl.ParentDecl, SkipReason.UnsupportedSignature, "Operator signature contains unsupported placeholder type.");
                return false;
            }

            // Bug #4: C# operators cannot have generic type parameters. If any operand is a bare
            // generic type parameter (e.g., shift operators with generic second operand), skip.
            if (methodDecl.CSSignature.Skip(1).Any(arg => arg.IsGeneric))
            {
                _logger.LogWarning($"Operator '{symbol}' has generic type parameter operand — C# operators cannot be generic.");
                ReportCollector.RecordMemberSkipped(BindingItemKind.Operator, symbol, operatorDecl.ParentDecl, SkipReason.UnsupportedSignature, "C# operators cannot have generic type parameters.");
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
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Operator, symbol, operatorDecl.ParentDecl,
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
            EmitOperatorWrapper(csWriter, operatorDecl, signatureHandler, resolvedSimpleName, typeNameWithGenerics, pinvokeHelperContext, isReferenceType, methodEnv);
            EmitOperatorPInvoke(csWriter, operatorDecl, methodEnv, signatureHandler, typeDatabase, pinvokeHelperContext);
            ReportCollector.RecordMemberEmitted(BindingItemKind.Operator, symbol, operatorDecl.ParentDecl);
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
        private void EmitOperatorWrapper(CSharpWriter csWriter, OperatorDecl operatorDecl, SignatureHandler signatureHandler, string typeName, string typeNameWithGenerics, PInvokeHelperContext? pinvokeHelperContext, bool isReferenceType, MethodEnvironment methodEnv)
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
                        var methodParamCsName = $"T{typeParamCount + i}";
                        var typeParamCsName = $"T{i}";
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
                var leftType = ApplyRemap(FixGenericTypeName(leftParam.Type));
                var rightType = ApplyRemap(FixGenericTypeName(rightParam.Type));

                XmlDocCommentEmitter.EmitDocComment(csWriter, operatorDecl);
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

                // Wrap in unsafe block when indirect result uses pointer operations
                if (requiresIndirectResult) { csWriter.WriteLine("unsafe {"); csWriter.Indent++; }

                // Emit P/Invoke call and return
                EmitOperatorPInvokeCall(csWriter, symbol, returnType, pInvokeSignature, pinvokeHelperContext, requiresIndirectResult);

                if (requiresIndirectResult) { csWriter.Indent--; csWriter.WriteLine("}"); }

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
                var operandType = ApplyRemap(FixGenericTypeName(operand.Type));

                XmlDocCommentEmitter.EmitDocComment(csWriter, operatorDecl);
                csWriter.WriteLine($"public static {returnType} operator {csOperator}({operandType} {operand.Name})");
                csWriter.WriteLine("{");
                csWriter.Indent++;

                // Wrap in unsafe block when indirect result uses pointer operations
                if (requiresIndirectResult) { csWriter.WriteLine("unsafe {"); csWriter.Indent++; }

                // Emit P/Invoke call and return
                EmitOperatorPInvokeCall(csWriter, symbol, returnType, pInvokeSignature, pinvokeHelperContext, requiresIndirectResult);

                if (requiresIndirectResult) { csWriter.Indent--; csWriter.WriteLine("}"); }

                csWriter.Indent--;
                csWriter.WriteLine("}");
            }
        }

        /// <summary>
        /// Emits the P/Invoke call and return statement for an operator.
        /// Handles both direct returns and indirect result allocation (Bug #1).
        /// </summary>
        private void EmitOperatorPInvokeCall(CSharpWriter csWriter, string symbol, string returnType, Signature pInvokeSignature, PInvokeHelperContext? pinvokeHelperContext, bool requiresIndirectResult)
        {
            var pinvokeName = GetPInvokeMethodName(symbol);
            var callArgs = pInvokeSignature.CallArgumentsString();

            if (requiresIndirectResult)
            {
                // Allocate memory and create SwiftIndirectResult for non-frozen/class return types
                csWriter.WriteLine($"var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<{returnType}>();");
                csWriter.WriteLine($"var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);");
                csWriter.WriteLine($"var swiftIndirectResult = new SwiftIndirectResult(payload);");

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

                // Marshal the result back from the indirect result buffer
                csWriter.WriteLine($"return SwiftMarshal.MarshalFromSwift<{returnType}>(new IntPtr(swiftIndirectResult.Value));");
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
        /// </summary>
        /// <param name="pinvokeHelperContext">Optional P/Invoke helper context for generic types.</param>
        private void EmitOperatorPInvoke(CSharpWriter csWriter, OperatorDecl operatorDecl, MethodEnvironment methodEnv, SignatureHandler signatureHandler, ITypeDatabase typeDatabase, PInvokeHelperContext? pinvokeHelperContext)
        {
            var methodDecl = operatorDecl.UnderlyingMethod;
            var moduleDecl = methodDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(methodDecl.ModuleDecl));

            var pinvokeName = GetPInvokeMethodName(operatorDecl.OperatorSymbol);
            var libPath = typeDatabase.GetLibraryPath(moduleDecl.Name);
            var pInvokeSignature = signatureHandler.GetPInvokeSignature();

            if (pinvokeHelperContext != null)
            {
                // Collect to helper context for generic types
                var declaration = new PInvokeDeclaration
                {
                    LibraryPath = libPath,
                    EntryPoint = methodDecl.MangledName,
                    MethodName = pinvokeName,
                    ReturnType = pInvokeSignature.ReturnType,
                    ParametersString = pInvokeSignature.PInvokeParametersString(),
                    IsAsync = false,
                    MetadataParameters = pinvokeHelperContext.GetMetadataParameterDeclarations()
                };
                pinvokeHelperContext.AddDeclaration(declaration);
            }
            else
            {
                // Emit directly for non-generic types
                csWriter.WriteLine("[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]");
                csWriter.WriteLine($"[LibraryImport(\"{libPath}\", EntryPoint = \"{methodDecl.MangledName}\")]");
                if (pInvokeSignature.ReturnType == "bool")
                    csWriter.WriteLine("[return: MarshalAs(UnmanagedType.U1)]");
                csWriter.WriteLine($"private static partial {pInvokeSignature.ReturnType} {pinvokeName}({pInvokeSignature.PInvokeParametersString()});");
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
    }
}
