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

            if (_env.ParentDecl is ClassDecl cd && cd.IsObjCRooted)
            {
                // ObjC-rooted: static helper resolves handle BEFORE base() is called.
                // The helper name uses the Swift init name to disambiguate overloads.
                var helperName = $"CreateSwiftInstance_{NameProvider.GetPInvokeName((MethodDecl)_env.MethodDecl)}";
                var paramArgs = string.Join(", ", _wrapperSignature.Parameters.Select(p => p.Name));
                csWriter.WriteLine($"{accessModifier} {constructorName}({_wrapperSignature.ParametersString(BuildOriginalSwiftTypeAttributes())}) : base({helperName}({paramArgs}))");
            }
            else
            {
                // Derived class constructors must chain to the base's protected sentinel constructor
                // to satisfy C#'s requirement for a parameterless base constructor.
                var baseChain = _env.ParentDecl is ClassDecl cd2 && cd2.HasResolvedSuperclass
                    ? " : base(default(SwiftInheritanceChain))"
                    : "";
                csWriter.WriteLine($"{accessModifier} {constructorName}({_wrapperSignature.ParametersString(BuildOriginalSwiftTypeAttributes())}){baseChain}");
            }
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

            // Compute virtual/override/sealed override modifier for class instance methods.
            // Excludes: static methods, constructors, async constructors, accessor methods.
            string dispatchModifier = "";
            bool isClassInstanceMethod =
                _env.ParentDecl is ClassDecl
                && _env.MethodDecl.MethodType != MethodType.Static
                && !_env.MethodDecl.IsConstructor
                && !isAsyncConstructor
                && !_env.MethodDecl.IsAccessor;
            if (isClassInstanceMethod)
            {
                var classParent = (ClassDecl)_env.ParentDecl;
                // Can only emit "override" if a resolved ancestor actually has this method in C#.
                // Otherwise CS0115 ("no suitable method found to override") occurs when:
                // - The ancestor is external (NSObject, UIView, etc.) — no C# base class
                // - The ancestor method was skipped by validation gates — no C# method to override
                if (_env.MethodDecl.IsOverride && HasMethodInResolvedAncestors(classParent, _env.MethodDecl, _env.CSharpMethodName, _env.TypeDatabase))
                {
                    dispatchModifier = _env.MethodDecl.IsFinal ? "sealed override " : "override ";
                }
                else if (!classParent.IsFinal && !_env.MethodDecl.IsFinal)
                {
                    dispatchModifier = "virtual ";
                }
            }

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
                ? $"{(_wrapperSignature.Parameters.Count > 0 ? ", " : "")}global::System.Threading.CancellationToken cancellationToken = default"
                : "";
            csWriter.WriteLine($"{accessModifier} {staticKeyword}{dispatchModifier}{returnType} {methodName}{genericParams}({_wrapperSignature.ParametersString(BuildOriginalSwiftTypeAttributes())}{cancellationTokenParam})");

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

            // Merge availability deprecation into safety obsolete
            var deprecationMsg = AvailabilityAttributeEmitter.GetDeprecationMessage(_env.MethodDecl);
            if (deprecationMsg != null)
                issues.Insert(0, $"Deprecated: {deprecationMsg}");

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

            bool hasSafetyIssues = hasJitRisk || (!_env.MethodDecl.IsAccessor && _env.MethodDecl.IsMissingExportedSymbol);
            if (issues.Count > 0)
            {
                var message = string.Join(". ", issues) + ".";
                if (hasSafetyIssues)
                {
                    // SB0001: JIT risk (suppressible on NativeAOT via SwiftBindingsInteropMode=Direct)
                    // SB0002: Missing symbol (not runtime-dependent — always relevant)
                    var diagnosticId = hasJitRisk ? "SB0001" : "SB0002";
                    csWriter.WriteLine($"[Obsolete(\"{UnsupportedSwiftTypeSupport.EscapeStringLiteral(message)}\", " +
                        $"DiagnosticId = \"{diagnosticId}\", " +
                        $"UrlFormat = \"https://github.com/justinwojo/swift-dotnet-bindings/blob/main/src/docs/known-issues-workarounds.md\")]");
                }
                else
                {
                    // Deprecation-only — plain [Obsolete] without DiagnosticId
                    csWriter.WriteLine($"[Obsolete(\"{UnsupportedSwiftTypeSupport.EscapeStringLiteral(message)}\")]");
                }
            }

            // Hide original method when a simplified throwing closure overload exists.
            // The post-processor emits the user-facing convenience overload (Action/Func params);
            // this hides the raw SwiftResult-based signature from IntelliSense.
            if (!_env.MethodDecl.IsAccessor && _env.MethodDecl.HasThrowingClosureSimplification)
            {
                csWriter.WriteLine("[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]");
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

        /// <summary>
        /// Walks the resolved superclass chain looking for a method with the given Swift name
        /// and matching parameter types that was actually emitted into C# output.
        /// Matches by name + parameter count + Swift type spec strings to handle overloaded methods
        /// where only some overloads were emitted.
        /// Also verifies that the ancestor method's C# name matches the derived method's C# name,
        /// because property collision rules (e.g., "With" prefix for self-returning builders) can
        /// produce different C# names for the same Swift method in base vs derived classes.
        /// Returns false when: the chain reaches an external ancestor (null), the ancestor has
        /// unsupported constraints, or no ancestor has an emitted method matching by full signature.
        /// </summary>
        internal static bool HasMethodInResolvedAncestors(ClassDecl classDecl, MethodDecl method, string? derivedCSharpName = null, ITypeDatabase? typeDatabase = null)
        {
            var ancestor = classDecl.ResolvedSuperclass;
            // CSSignature[0] is the return type; parameters start at [1]
            int paramCount = method.CSSignature.Count - 1;
            var paramTypes = GetParameterTypeStrings(method);
            while (ancestor != null)
            {
                if (GenericTypeEmitter.TryGetUnsupportedConstraint(ancestor, out _))
                    return false; // ancestor has unsupported constraints, won't be emitted as base
                if (ancestor.Methods.Any(m =>
                    m.WasEmitted
                    && m.Name == method.Name
                    && !m.IsAccessor
                    && !m.IsConstructor
                    && (m.CSSignature.Count - 1) == paramCount
                    && ParameterTypesMatch(m, paramTypes)
                    && (derivedCSharpName == null || AncestorCSharpNameMatches(m, ancestor, derivedCSharpName, typeDatabase))))
                    return true;
                ancestor = ancestor.ResolvedSuperclass;
            }
            return false;
        }

        /// <summary>
        /// Computes the C# method name for an ancestor method and checks if it matches the derived name.
        /// Uses the production ComputePropertyRenames path (ClassHandler.cs:104) when a TypeDatabase is
        /// available, which applies type-based filtering and AsyncStream handling. Falls back to
        /// ComputePropertyRenamesForNestedTypeCollisions (nested-type collision only) when no TypeDatabase
        /// is provided (e.g., from tests that don't set up a full type database).
        /// </summary>
        private static bool AncestorCSharpNameMatches(MethodDecl ancestorMethod, ClassDecl ancestorClass, string derivedCSharpName, ITypeDatabase? typeDatabase)
        {
            // Build property name set matching ClassHandler.cs:262-267:
            // - GetPropertyName (handles keyword escaping, wrapper sanitization, type-name collision)
            // - GetFinalMemberName (applies property renames computed identically to ClassHandler.cs:104)
            // - Nested type names (CS0102 collision with method names)
            // Production uses ALL declared properties (not just emitted ones) in the final
            // collision set — a non-emitted property still occupies the name and can cause
            // method name collisions.
            var propertyRenames = typeDatabase != null
                ? NameProvider.ComputePropertyRenames(ancestorClass, typeDatabase)
                : NameProvider.ComputePropertyRenamesForNestedTypeCollisions(
                    ancestorClass.Properties.Select(p => NameProvider.GetPropertyName(p.Name, ancestorClass.Name)),
                    ancestorClass.Types.Select(t => t.Name));
            var ancestorProps = new HashSet<string>(
                ancestorClass.Properties
                    .Select(p => NameProvider.GetFinalMemberName(
                        NameProvider.GetPropertyName(p.Name, ancestorClass.Name), propertyRenames)),
                StringComparer.Ordinal);
            // Nested type names collide with method names in C# (CS0102)
            foreach (var nestedType in ancestorClass.Types)
                ancestorProps.Add(NameProvider.ToPascalCase(nestedType.Name));

            // Use the canonical IsSelfReturningMethod helper which also checks
            // concrete parent-type returns (not just DynamicSelf/literal "Self").
            bool isSelfReturning = MethodEnvironment.IsSelfReturningMethod(ancestorMethod);

            int parameterCount = ancestorMethod.CSSignature.Skip(1)
                .Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple);

            var ancestorCSharpName = NameProvider.GetPublicMethodName(
                ancestorMethod.Name, ancestorMethod.IsAsync,
                hasReturnValue: !ancestorMethod.IsAccessor && ancestorMethod.CSSignature.Count > 0 && !ancestorMethod.CSSignature.First().SwiftTypeSpec.IsEmptyTuple,
                ancestorProps,
                isSelfReturning: isSelfReturning,
                parentTypeName: ancestorClass.Name,
                parameterCount: parameterCount);

            return ancestorCSharpName == derivedCSharpName;
        }

        /// <summary>
        /// Gets the Swift type spec strings for all parameters (excluding CSSignature[0] which is the return type).
        /// </summary>
        private static List<string> GetParameterTypeStrings(MethodDecl method)
        {
            var types = new List<string>(method.CSSignature.Count - 1);
            for (int i = 1; i < method.CSSignature.Count; i++)
                types.Add(method.CSSignature[i].SwiftTypeSpec.ToString());
            return types;
        }

        /// <summary>
        /// Returns true if the candidate method's parameter Swift type specs match the given list.
        /// Assumes parameter counts are already verified equal.
        /// </summary>
        private static bool ParameterTypesMatch(MethodDecl candidate, List<string> expectedTypes)
        {
            for (int i = 0; i < expectedTypes.Count; i++)
            {
                if (candidate.CSSignature[i + 1].SwiftTypeSpec.ToString() != expectedTypes[i])
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Walks the resolved superclass chain looking for a property with the given Swift name
        /// that was actually emitted into C# output.
        /// Returns false when: the chain reaches an external ancestor, the ancestor has
        /// unsupported constraints, or no ancestor has an emitted property with this name.
        /// </summary>
        internal static bool HasPropertyInResolvedAncestors(ClassDecl classDecl, string propertyName)
        {
            var ancestor = classDecl.ResolvedSuperclass;
            while (ancestor != null)
            {
                if (GenericTypeEmitter.TryGetUnsupportedConstraint(ancestor, out _))
                    return false;
                if (ancestor.Properties.Any(p => p.WasEmitted && p.Name == propertyName))
                    return true;
                ancestor = ancestor.ResolvedSuperclass;
            }
            return false;
        }
    }
}
