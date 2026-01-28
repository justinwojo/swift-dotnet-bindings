// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

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
        public void EmitOperator(CSharpWriter csWriter, OperatorDecl operatorDecl, ITypeDatabase typeDatabase)
        {
            var symbol = operatorDecl.OperatorSymbol;
            if (!IsSupportedOperator(symbol))
            {
                _logger.LogWarning($"Operator '{symbol}' is not supported for C# emission.");
                return;
            }

            var methodDecl = operatorDecl.UnderlyingMethod;
            var parentDecl = operatorDecl.ParentDecl as TypeDecl;
            if (parentDecl == null)
            {
                _logger.LogWarning($"Operator '{symbol}' has no valid parent type declaration.");
                return;
            }

            var moduleDecl = operatorDecl.ModuleDecl;
            if (moduleDecl == null)
            {
                _logger.LogWarning($"Operator '{symbol}' has no module declaration.");
                return;
            }

            // Create a MethodEnvironment for signature handling
            var methodEnv = new MethodEnvironment(methodDecl, typeDatabase);
            var signatureHandler = new SignatureHandler(methodEnv);

            // Check if signature is supported
            if (signatureHandler.GetWrapperSignature().ContainsPlaceholder)
            {
                _logger.LogWarning($"Operator {symbol} has unsupported signature: ({signatureHandler.GetWrapperSignature().ParametersString()}) -> {signatureHandler.GetWrapperSignature().ReturnType}");
                return;
            }

            // Emit the operator wrapper and PInvoke
            EmitOperatorWrapper(csWriter, operatorDecl, signatureHandler, parentDecl.Name);
            EmitOperatorPInvoke(csWriter, operatorDecl, methodEnv, signatureHandler, typeDatabase);
            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits the C# operator overload method.
        /// </summary>
        private void EmitOperatorWrapper(CSharpWriter csWriter, OperatorDecl operatorDecl, SignatureHandler signatureHandler, string typeName)
        {
            var symbol = operatorDecl.OperatorSymbol;
            var csOperator = GetCSharpOperator(symbol)!;
            var wrapperSignature = signatureHandler.GetWrapperSignature();
            var pInvokeSignature = signatureHandler.GetPInvokeSignature();

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
                var returnType = wrapperSignature.ReturnType;

                csWriter.WriteLine($"public static {returnType} operator {csOperator}({leftParam.Type} {leftParam.Name}, {rightParam.Type} {rightParam.Name})");
                csWriter.WriteLine("{");
                csWriter.Indent++;

                // Call the PInvoke method
                var pinvokeName = GetPInvokeMethodName(symbol);
                var callArgs = pInvokeSignature.CallArgumentsString();
                if (returnType == "void")
                {
                    csWriter.WriteLine($"{pinvokeName}({callArgs});");
                }
                else
                {
                    csWriter.WriteLine($"return {pinvokeName}({callArgs});");
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
                var returnType = wrapperSignature.ReturnType;

                csWriter.WriteLine($"public static {returnType} operator {csOperator}({operand.Type} {operand.Name})");
                csWriter.WriteLine("{");
                csWriter.Indent++;

                var pinvokeName = GetPInvokeMethodName(symbol);
                var callArgs = pInvokeSignature.CallArgumentsString();
                if (returnType == "void")
                {
                    csWriter.WriteLine($"{pinvokeName}({callArgs});");
                }
                else
                {
                    csWriter.WriteLine($"return {pinvokeName}({callArgs});");
                }

                csWriter.Indent--;
                csWriter.WriteLine("}");
            }
        }

        /// <summary>
        /// Emits the PInvoke declaration for an operator.
        /// </summary>
        private void EmitOperatorPInvoke(CSharpWriter csWriter, OperatorDecl operatorDecl, MethodEnvironment methodEnv, SignatureHandler signatureHandler, ITypeDatabase typeDatabase)
        {
            var methodDecl = operatorDecl.UnderlyingMethod;
            var moduleDecl = methodDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(methodDecl.ModuleDecl));

            var pinvokeName = GetPInvokeMethodName(operatorDecl.OperatorSymbol);
            var libPath = typeDatabase.GetLibraryPath(moduleDecl.Name);
            var pInvokeSignature = signatureHandler.GetPInvokeSignature();

            csWriter.WriteLine("[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]");
            csWriter.WriteLine($"[DllImport(\"{libPath}\", EntryPoint = \"{methodDecl.MangledName}\")]");
            csWriter.WriteLine($"private static extern {pInvokeSignature.ReturnType} {pinvokeName}({pInvokeSignature.ParametersString()});");
        }

        /// <summary>
        /// Emits a synthesized paired operator (e.g., != from ==).
        /// </summary>
        /// <param name="csWriter">The C# code writer.</param>
        /// <param name="existingOperator">The existing operator that has been defined.</param>
        /// <param name="missingOperator">The paired operator that needs to be synthesized.</param>
        /// <param name="typeName">The name of the containing type.</param>
        public void EmitSynthesizedPairedOperator(CSharpWriter csWriter, OperatorDecl existingOperator, string missingOperator, string typeName)
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
        public void ValidateAndEmitPairs(CSharpWriter csWriter, List<OperatorDecl> operators, string typeName)
        {
            var definedSymbols = operators
                .Where(o => IsSupportedOperator(o.OperatorSymbol))
                .Select(o => o.OperatorSymbol)
                .ToHashSet();

            foreach (var op in operators)
            {
                var symbol = op.OperatorSymbol;
                if (!IsSupportedOperator(symbol)) continue;

                var pairedSymbol = GetRequiredPairedOperator(symbol);
                if (pairedSymbol != null && !definedSymbols.Contains(pairedSymbol))
                {
                    // Need to synthesize the paired operator
                    _logger.LogInformation($"Synthesizing paired operator '{pairedSymbol}' from '{symbol}' for type '{typeName}'.");
                    EmitSynthesizedPairedOperator(csWriter, op, pairedSymbol, typeName);
                    // Mark as defined to avoid duplicate synthesis
                    definedSymbols.Add(pairedSymbol);
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
    }
}
