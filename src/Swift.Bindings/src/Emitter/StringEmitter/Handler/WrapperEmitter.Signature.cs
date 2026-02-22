// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;

namespace BindingsGeneration
{
    internal partial class WrapperEmitter
    {
        /// <summary>
        /// Emits the constructor signature.
        /// </summary>
        /// <param name="csWriter">The IndentedTextWriter instance.</param>
        private void EmitSignatureConstructor(CSharpWriter csWriter)
        {
            // C# does not support generic constructors — never emit <...> on a constructor.
            // Type-level generic params are already declared on the containing type.
            var accessModifier = NameProvider.GetAccessModifier(_env.MethodDecl.Visibility);
            // Use the resolved C# type name (may be renamed for nested type collision avoidance)
            var constructorName = GetResolvedTypeName();
            csWriter.WriteLine($"{accessModifier} {constructorName}({_wrapperSignature.ParametersString(BuildOriginalSwiftTypeAttributes())})");
        }

        /// <summary>
        /// Emits the method signature.
        /// </summary>
        /// <param name="writer">The IndentedTextWriter instance.</param>
        private void EmitSignatureMethod(CSharpWriter csWriter)
        {
            // Only emit <T0, T1, ...> for method-own generic params.
            // Type-level params are already declared on the containing type and must not be redeclared.
            var methodOwnParams = GetMethodOwnGenericParams();
            var genericParams = methodOwnParams.Count > 0
                ? $"<{string.Join(", ", methodOwnParams.Select(p => _env.GenericTypeMapping[p.TypeName].TypeParameter))}>"
                : "";

            // Async constructors emit as static CreateAsync() factory methods
            // (C# doesn't support async constructors)
            bool isAsyncConstructor = _env.MethodDecl.IsConstructor && _env.MethodDecl.IsAsync;

            var staticKeyword = _env.MethodDecl.MethodType == MethodType.Static || _env.ParentDecl is ModuleDecl || isAsyncConstructor ? "static " : "";
            var returnType = _wrapperSignature.ReturnType;
            if (_requiresSwiftAsync)
            {
                returnType = $"Task{(_env.MethodDecl.CSSignature.First().SwiftTypeSpec.IsEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}";
            }

            // Use CreateAsync for async constructors (with collision detection)
            var methodName = isAsyncConstructor
                ? NameProvider.GetMethodName("createAsync", _env.SiblingPropertyNames)
                : _env.CSharpMethodName;

            var accessModifier = NameProvider.GetAccessModifier(_env.MethodDecl.Visibility);
            // Async methods get CancellationToken as the last parameter
            var cancellationTokenParam = _requiresSwiftAsync
                ? $"{(_wrapperSignature.Parameters.Count > 0 ? ", " : "")}System.Threading.CancellationToken cancellationToken = default"
                : "";
            csWriter.WriteLine($"{accessModifier} {staticKeyword}{returnType} {methodName}{genericParams}({_wrapperSignature.ParametersString(BuildOriginalSwiftTypeAttributes())}{cancellationTokenParam})");

            // Emit where clauses for generic constraints
            var whereClause = BuildWhereClause();
            if (!string.IsNullOrEmpty(whereClause))
                csWriter.WriteLines(whereClause);
        }

        /// <summary>
        /// Returns only the method-own generic parameters (excluding those inherited from the parent type).
        /// Methods inside generic types have their parent type's generic params copied into GenericParameters
        /// by the parser. These should not be redeclared on the method/constructor signature because:
        /// - For methods: it shadows the type's params (CS0693 warning, semantically wrong)
        /// - For constructors: C# doesn't support generic constructors
        /// </summary>
        private List<GenericArgumentDecl> GetMethodOwnGenericParams()
        {
            if (!_env.MethodDecl.IsGeneric)
                return new List<GenericArgumentDecl>();

            // Accessor methods never have their own generic params
            if (_env.MethodDecl.IsAccessor)
                return new List<GenericArgumentDecl>();

            // If parent is not a generic type, all params are method-own
            if (_env.ParentDecl is not TypeDecl typeDecl || !typeDecl.IsGeneric)
                return _env.MethodDecl.GenericParameters;

            // Filter out params that match the parent type's generic params
            var typeParamNames = new HashSet<string>(typeDecl.GenericParameters.Select(p => p.TypeName));
            return _env.MethodDecl.GenericParameters
                .Where(p => !typeParamNames.Contains(p.TypeName))
                .ToList();
        }

        /// <summary>
        /// Builds the where clause for generic constraints.
        /// Only emits constraints for method-own generic parameters (not type-inherited ones).
        /// Type-level constraints are already declared on the containing type.
        /// </summary>
        /// <returns>The where clause string, or empty string if no constraints.</returns>
        private string BuildWhereClause()
        {
            var methodOwnParams = GetMethodOwnGenericParams();
            if (methodOwnParams.Count == 0)
                return "";

            var constraints = new List<string>();

            foreach (var param in methodOwnParams)
            {
                if (!_env.GenericTypeMapping.TryGetValue(param.TypeName, out var csNameInfo))
                    continue;

                var csName = csNameInfo.TypeParameter;
                var paramConstraints = new List<string> { "ISwiftObject" };

                foreach (var conformance in param.GenericConformances)
                {
                    // Skip unknown protocols and protocols with associated types
                    // (protocols with associated types generate generic interfaces which can't be used as constraints)
                    if (!IsProtocolAvailableForConstraint(conformance.ConformanceTarget))
                        continue;

                    var interfaceName = NameProvider.GetInterfaceName(conformance.ConformanceTarget.Name, moduleName: conformance.ConformanceTarget.Module);
                    paramConstraints.Add(interfaceName);
                }

                constraints.Add($"where {csName} : {string.Join(", ", paramConstraints)}");
            }

            return constraints.Count > 0
                ? "    " + string.Join("\n    ", constraints)
                : "";
        }

        /// <summary>
        /// Emits [Obsolete] with custom DiagnosticId for methods with unmitigated JIT risks or missing exported symbols.
        /// Uses SB0001 for JIT risk (Mono-specific, safe on NativeAOT) and SB0002 for missing symbols.
        /// Combined issues use SB0001 (broader scope). Skips accessors — property-level [Obsolete] requires
        /// separate PropertyHandler wiring. Consumer .targets suppress these via SwiftBindingsInteropMode=Direct.
        /// </summary>
        private void EmitSafetyObsolete(CSharpWriter csWriter)
        {
            bool hasJitRisk = false;
            var issues = new List<string>();

            // Deliverable 1: JIT risk (skip accessors — see property deferral)
            if (!_env.MethodDecl.IsAccessor &&
                _env.MethodDecl.DetectedJitRisks != MonoJitRiskDetector.MonoJitRisk.None)
            {
                var (_, needsWrapper) = PInvokeEmitter.ComputeEntryPoint((MethodDecl)_env.MethodDecl);
                if (!needsWrapper)
                {
                    hasJitRisk = true;
                    issues.Add("Mono JIT crash risk: this method uses CallConvSwift P/Invoke patterns " +
                        "that crash on Mono runtime. Safe on NativeAOT (PublishAot=true)");
                }
            }

            // Deliverable 2: Missing symbol (skip accessors — same as JIT risk above)
            if (!_env.MethodDecl.IsAccessor && _env.MethodDecl.IsMissingExportedSymbol)
            {
                issues.Add("P/Invoke entry point not exported by the library. " +
                    "This method will throw EntryPointNotFoundException at runtime");
            }

            if (issues.Count > 0)
            {
                var message = string.Join(". ", issues) + ".";
                // SB0001: JIT risk (suppressible on NativeAOT via SwiftBindingsInteropMode=Direct)
                // SB0002: Missing symbol (not runtime-dependent — always relevant)
                var diagnosticId = hasJitRisk ? "SB0001" : "SB0002";
                csWriter.WriteLine($"[Obsolete(\"{UnsupportedSwiftTypeSupport.EscapeStringLiteral(message)}\", " +
                    $"DiagnosticId = \"{diagnosticId}\", " +
                    $"UrlFormat = \"https://github.com/malinicr/swift-bindings/blob/main/src/docs/known-issues-workarounds.md\")]");
            }
        }

        /// <summary>
        /// Builds a dictionary mapping parameter names to [OriginalSwiftType] attribute strings
        /// for parameters that fell back to AnyType during type projection.
        /// Returns null when no parameters have fallbacks (avoids allocation).
        /// </summary>
        private Dictionary<string, string>? BuildOriginalSwiftTypeAttributes()
        {
            Dictionary<string, string>? attrs = null;
            var parameters = _wrapperSignature.Parameters;
            var csSignatureParams = _env.MethodDecl.CSSignature.Skip(1).ToList();

            for (int i = 0; i < parameters.Count && i < csSignatureParams.Count; i++)
            {
                if (UnsupportedSwiftTypeSupport.TryFindFallbackInfo(
                    _env.TypeDatabase, _env.ClosureHandler, csSignatureParams[i].SwiftTypeSpec, out var info))
                {
                    attrs ??= new Dictionary<string, string>();
                    attrs[parameters[i].Name] = $"[global::Swift.OriginalSwiftType(\"{UnsupportedSwiftTypeSupport.EscapeStringLiteral(info.SwiftType)}\")]";
                }
            }
            return attrs;
        }

        /// <summary>
        /// Emits [return: OriginalSwiftType("...")] before the method signature when the return type
        /// fell back to AnyType. Not called for constructors (C# constructors have no return type).
        /// </summary>
        private void EmitReturnTypeOriginalSwiftType(CSharpWriter csWriter)
        {
            // Constructors have no return type in C#, so [return:] is invalid
            if (_env.MethodDecl.IsConstructor) return;

            var returnArg = _env.MethodDecl.CSSignature.First();
            if (UnsupportedSwiftTypeSupport.TryFindFallbackInfo(
                _env.TypeDatabase, _env.ClosureHandler, returnArg.SwiftTypeSpec, out var info))
            {
                csWriter.WriteLine($"[return: global::Swift.OriginalSwiftType(\"{UnsupportedSwiftTypeSupport.EscapeStringLiteral(info.SwiftType)}\")]");
            }
        }
    }
}
