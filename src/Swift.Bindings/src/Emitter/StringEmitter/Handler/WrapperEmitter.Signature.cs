// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;

namespace BindingsGeneration
{
    internal partial class WrapperEmitter
    {
        /// <summary>
        /// The API-manifest parameter portion for the declaration this emitter is about to write.
        /// Taken from the same wrapper signature the declaration itself renders from, so the
        /// recorded shape cannot drift from the emitted one.
        /// </summary>
        private string BuildEmittedParameterPortion(bool includeCancellationToken = false)
        {
            var types = _wrapperSignature.ApiSurfaceParameterTypes();
            if (includeCancellationToken)
                types = types.Append("global::System.Threading.CancellationToken");
            return ModuleEmissionContext.FormatParameterPortion(types);
        }

        /// <summary>
        /// Emits the constructor signature.
        /// </summary>
        /// <param name="csWriter">The IndentedTextWriter instance.</param>
        private void EmitSignatureConstructor(CSharpWriter csWriter)
        {
            // C# does not support generic constructors — never emit <...> on a constructor.
            // Type-level generic params are already declared on the containing type.
            var accessModifier = NameProvider.GetAccessModifier(_env.MethodDecl.IsSynthesizedAccessor);
            // Use the resolved C# type name (may be renamed for nested type collision avoidance)
            var constructorName = GetResolvedTypeName();

            if (_emittingInitFactory)
            {
                EmitSignatureInitFactory(csWriter, accessModifier);
                return;
            }

            // A constructor is emitted under the type's own name; CSharpMethodName holds only the
            // internal dedup identity for it. Record the emitted name so the API manifest keys the
            // member a consumer can actually call.
            _emissionContext.RecordEmittedApiShape(
                _env.MethodDecl,
                csharpName: constructorName,
                parameterPortion: BuildEmittedParameterPortion());

            if (_env.ParentDecl is ClassDecl cd && cd.IsObjCRooted)
            {
                // ObjC-rooted: static helper resolves handle BEFORE base() is called.
                // The helper name uses the Swift init name to disambiguate overloads.
                var helperName = $"CreateSwiftInstance_{NameProvider.GetPInvokeName(_env.EmissionSymbol, (MethodDecl)_env.MethodDecl)}";
                var paramArgs = string.Join(", ", _wrapperSignature.Parameters.Select(p => p.Name));
                csWriter.WriteLine($"{accessModifier} {constructorName}({_wrapperSignature.ParametersString(BuildOriginalSwiftTypeAttributes())}) : base({helperName}({paramArgs}))");
            }
            else
            {
                // Derived class constructors must chain to the base's protected sentinel constructor
                // to satisfy C#'s requirement for a parameterless base constructor. Cross-module
                // Swift parents (Bug #14) also need the chain — the parent assembly emitted the
                // protected sentinel ctor and the C# compiler enforces the same chaining rule.
                var baseChain = _env.ParentDecl is ClassDecl cd2
                    && (cd2.HasResolvedSuperclass || cd2.HasCrossModuleSwiftSuperclass)
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

            var staticKeyword = EmitsStatic ? "static " : "";

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

            // An async constructor emits as a static factory under a name this site chooses, not
            // under CSharpMethodName; an async method carries a trailing CancellationToken the
            // declared signature has no parameter for. Record what is about to be written so the
            // API manifest describes the member as a caller sees it.
            _emissionContext.RecordEmittedApiShape(
                _env.MethodDecl,
                csharpName: methodName,
                parameterPortion: BuildEmittedParameterPortion(_requiresSwiftAsync));

            var accessModifier = NameProvider.GetAccessModifier(_env.MethodDecl.IsSynthesizedAccessor);
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
        /// ISwiftObject is seeded whenever the Swift param declares any non-Sendable protocol
        /// conformance — even ones filtered from the C# constraint list — because the
        /// descriptor-symbol PWT path still emits <c>ProtocolWitnessTable.GetOrThrowAuto&lt;T,…&gt;</c>
        /// calls that require <c>T : ISwiftObject</c>. Dropped only for genuinely
        /// unconstrained params so blittable instantiations (Vector3, float, …) compile.
        /// Mirrors <see cref="GenericTypeEmitter.GetWhereClause"/>.
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
                // Collect surviving protocol constraints; ISwiftObject seeding decision
                // happens after based on whether the Swift param has ANY non-Sendable
                // protocol conformance (filtered or otherwise).
                var paramConstraints = new List<string>();
                // Captures the ObjC-bridged class form of `some Protocol` constraints
                // (e.g. `some UIScene` → `where T : UIKit.UIScene`). Class constraints
                // displace the `ISwiftObject` seed because ObjC-bridged classes do
                // not implement `ISwiftObject`; the C# `where` syntax also requires
                // the class constraint to come first ahead of any interface
                // constraints. See <see cref="MethodValidationGates.TryGetClassConstraintTarget"/>.
                string? classConstraint = null;
                bool hasClassBoundConstraint = false;

                foreach (var conformance in param.GenericConformances)
                {
                    // Class-bound generic constraint (`<T : SomeClass>`). The parser tags
                    // every `:` clause as ConformanceKind.Protocol; consult the resolved
                    // record's Kind/Flags to recognise the class-target case. Class
                    // constraints emit the projected C# class name and contribute no PWT
                    // lookup — mirrors GenericTypeEmitter and
                    // PInvokeHelperEmitter.FlattenConformances.
                    //
                    // Gating: promote when (a) the class is registered in the same module
                    // as the consuming method — covers same-module Swift classes — OR
                    // (b) the class is ObjC-bridged — covers Foundation/UIKit hand-
                    // registered classes. Non-ObjC-bridged cross-module class records
                    // fall through to the historical permissive `I`-prefixed interface
                    // path lower down.
                    if (_env.TypeDatabase.TryGetTypeRecord(conformance.ConformanceTarget, out var maybeClassRecord)
                        && maybeClassRecord.Kind == TypeRecordKind.Class
                        && (conformance.ConformanceTarget.Module == (_env.MethodDecl.ModuleDecl?.Name ?? "")
                            || maybeClassRecord.Flags.HasFlag(TypeRecordFlags.ObjCBridged)))
                    {
                        paramConstraints.Add(maybeClassRecord.CSharpTypeName.FullyQualifiedName);
                        hasClassBoundConstraint = true;
                        continue;
                    }

                    // Protocol records → interface constraint (existing path).
                    // Resolve emission namespace via the umbrella fallback so a
                    // method-level constraint on a protocol re-exported through an
                    // umbrella module (e.g. `where T : RealityKit.IEvent` in ABI)
                    // emits the dep-module qualification.
                    if (IsProtocolAvailableForConstraint(conformance.ConformanceTarget))
                    {
                        var emissionModule = ProtocolConformanceHelper.ResolveProtocolEmissionModule(
                            conformance.ConformanceTarget, _env.TypeDatabase);
                        var interfaceName = NameProvider.GetInterfaceName(
                            conformance.ConformanceTarget.Name,
                            moduleName: emissionModule,
                            currentModuleName: _env.MethodDecl.ModuleDecl?.Name ?? "");
                        paramConstraints.Add(interfaceName);
                        continue;
                    }

                    // ObjC-bridged class records ("@protocol UIScene" projected as the
                    // class `UIKit.UIScene` in MAUI iOS bindings) → class constraint.
                    // First match wins; multiple class targets on the same param are
                    // unrepresentable in C# anyway (single-inheritance) so we keep the
                    // first projected one and let downstream validation surface the
                    // (so far hypothetical) collision.
                    if (classConstraint == null &&
                        MethodValidationGates.TryGetClassConstraintTarget(
                            conformance.ConformanceTarget, _env.TypeDatabase, out var csClassName))
                    {
                        classConstraint = csClassName;
                        continue;
                    }

                    // Otherwise: associated-type / Self-requirement / unknown / well-
                    // known runtime-only protocol — drop from the C# constraint list,
                    // ISwiftObject seeding below preserves the descriptor-symbol PWT
                    // path.
                }

                if (classConstraint != null)
                {
                    // ObjC-bridged class constraint: emit `where T : UIKit.UIScene`
                    // without ISwiftObject. This is correct for the StoreKit
                    // `some UIScene` shape — the consumer gets compile-time type
                    // safety, and the body's metadata path already routes through
                    // the unconstrained `TypeMetadata.GetTypeMetadataOrThrow<T>()`
                    // helper. Other surviving interface constraints (rare with this
                    // shape today) are appended after the class constraint per C#
                    // syntax requirements.
                    var ordered = new List<string> { classConstraint };
                    ordered.AddRange(paramConstraints);
                    constraints.Add($"where {csName} : {string.Join(", ", ordered)}");
                    continue;
                }

                bool hasAnyProtocolConformance = HasAnyNonMarkerProtocolConformance(param);
                if (paramConstraints.Count == 0 && !hasAnyProtocolConformance)
                    continue;

                // Class-bound constraints already imply ISwiftObject, and the class
                // constraint must appear FIRST in C# (CS0405/CS0406). Skip the
                // ISwiftObject seed when a class bound is already present.
                if (!hasClassBoundConstraint)
                    paramConstraints.Insert(0, "ISwiftObject");
                constraints.Add($"where {csName} : {string.Join(", ", paramConstraints)}");
            }

            return constraints.Count > 0
                ? "    " + string.Join("\n    ", constraints)
                : "";
        }

        /// <summary>
        /// Returns true if the generic param has any non-marker Swift protocol conformance.
        /// Stdlib marker protocols (<c>Swift.Sendable</c>, <c>Swift.Copyable</c>,
        /// <c>Swift.Escapable</c>, <c>Swift.SendableMetatype</c>, <c>Swift.BitwiseCopyable</c>)
        /// carry no runtime witness table, so they never drive a PWT lookup. Used by
        /// <see cref="BuildWhereClause"/> to determine whether the ISwiftObject seed must
        /// remain even when no constraint survives projection.
        /// </summary>
        private static bool HasAnyNonMarkerProtocolConformance(GenericArgumentDecl param)
        {
            foreach (var conformance in param.GenericConformances)
            {
                if (conformance.Kind != ConformanceKind.Protocol)
                    continue;
                if (IsStdlibMarkerProtocol(conformance.ConformanceTarget))
                    continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Stdlib marker protocols carry no runtime witness table — the Swift compiler
        /// does not pass them as PWT args to type metadata accessors. Module-qualified
        /// to avoid misidentifying a same-name app/framework protocol as a marker.
        /// Kept in sync with <c>PInvokeHelperEmitter.IsStdlibMarkerProtocol</c> and
        /// <c>ExistentialHandler.IsMarkerProtocol</c>.
        /// </summary>
        private static bool IsStdlibMarkerProtocol(SwiftTypeName protocolTypeName) =>
            protocolTypeName.Module == "Swift" &&
            protocolTypeName.Name is "Sendable" or "Escapable" or "Copyable"
                                  or "SendableMetatype" or "BitwiseCopyable";

        /// <summary>
        /// Emits [Obsolete] with a custom DiagnosticId for members carrying unmitigated direct-CallConvSwift
        /// risk or a missing exported symbol. SB0001 marks a member whose direct-CallConvSwift P/Invoke is
        /// predicted non-blittable but which is still callable; SB0009 marks the uncallable subset, whose
        /// body is a throwing tombstone. SB0002 marks a missing symbol or a silent-tombstone return.
        /// Combined issues take the non-blittable id (broader scope). Skips accessors — property-level
        /// [Obsolete] requires separate PropertyHandler wiring.
        /// </summary>
        /// <param name="emittedName">
        /// The name the member is being emitted under, when it is not the projected method name —
        /// the failable-factory path writes a static factory whose name the report has to use to
        /// name a member the binding actually has. Null everywhere else.
        /// </param>
        private void EmitSafetyObsolete(CSharpWriter csWriter, string? emittedName = null)
        {
            bool hasJitRisk = false;
            // Tracks whether an [EditorBrowsable(Never)] has already been written for this
            // method. EditorBrowsableAttribute is AllowMultiple=false, so the several hide
            // conditions below must never emit it twice (CS0579).
            bool editorBrowsableNeverEmitted = false;
            var issues = new List<string>();

            // Merge availability deprecation into safety obsolete
            var deprecationMsg = AvailabilityAttributeEmitter.GetDeprecationMessage(_env.MethodDecl);
            if (deprecationMsg != null)
                issues.Insert(0, $"Deprecated: {deprecationMsg}");

            // Unmitigated direct-CallConvSwift risk (skip accessors — see property deferral).
            // UsesFreeFunctionWrapper means a Swift @_silgen_name wrapper exists — C# calls it with
            // CallConvSwift matching the wrapper's swiftcc, so there's no ABI mismatch risk.
            // Also skip when every P/Invoke type is blittable: CallConvSwift on a blittable
            // signature is ABI-stable on both Mono and NativeAOT. The sentence is chosen centrally
            // so it matches the body actually emitted — a throw for the uncallable subset, a live
            // call for the rest.
            var nonBlittableIssue = WrapperValidation.GetNonBlittableCallConvSwiftIssue(_env);
            string? nonBlittableDiagnosticId = null;
            if (nonBlittableIssue != null)
            {
                hasJitRisk = true;
                nonBlittableDiagnosticId = nonBlittableIssue.Value.DiagnosticId;
                issues.Add(nonBlittableIssue.Value.Message);
            }

            // Deliverable 2: Missing symbol (skip accessors — same as JIT risk above)
            if (!_env.MethodDecl.IsAccessor && _env.MethodDecl.IsMissingExportedSymbol)
            {
                issues.Add("P/Invoke entry point not exported by the library. " +
                    "This method will throw EntryPointNotFoundException at runtime");
            }

            // Silent tombstone: return type was emitted with [OpaqueSwiftType] but has no usable
            // members. Callers can't do anything useful with the returned value — flag via SB0002
            // so audits catch them by grep. Skip accessors (property-level surfacing deferred).
            bool returnsSilentTombstone = !_env.MethodDecl.IsAccessor && IsReturnTypeSilentTombstone();
            if (returnsSilentTombstone)
            {
                issues.Add("Return type has no usable surface (all members skipped during emission). " +
                    "The returned value cannot be meaningfully consumed");
            }

            bool hasSafetyIssues = hasJitRisk
                || (!_env.MethodDecl.IsAccessor && _env.MethodDecl.IsMissingExportedSymbol)
                || returnsSilentTombstone;
            if (issues.Count > 0)
            {
                var message = string.Join(". ", issues) + ".";
                if (hasSafetyIssues)
                {
                    // The non-blittable condition carries its own id (SB0001 advisory / SB0009
                    // uncallable-and-throwing) chosen alongside the sentence, so the marker and the
                    // body agree. SB0002 covers a missing symbol or a silent-tombstone return (not
                    // runtime-dependent — always relevant). A member with both takes the
                    // non-blittable id: it is the broader condition.
                    var diagnosticId = nonBlittableDiagnosticId ?? "SB0002";
                    csWriter.WriteLine($"[Obsolete(\"{UnsupportedSwiftTypeSupport.EscapeStringLiteral(message)}\", " +
                        $"DiagnosticId = \"{diagnosticId}\", " +
                        $"UrlFormat = \"https://github.com/justinwojo/swift-dotnet-bindings/wiki/Troubleshooting\")]");
                    RecordSafetyMarked(diagnosticId, deprecationMsg != null, emittedName);
                    // Such a member is either uncallable (its body throws) or reachable only through a
                    // direct P/Invoke whose ABI we cannot vouch for. Either way the declaration stays: a
                    // conformance that requires it must still compile (dropping it would be CS0535) and the
                    // pinned public surface must not silently shrink. Hide it from IntelliSense so it does
                    // not clutter completion for the majority case; the [Obsolete] message + wiki URL remain
                    // the discovery path for anyone who reaches it deliberately.
                    if (hasJitRisk)
                    {
                        csWriter.WriteLine("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
                        editorBrowsableNeverEmitted = true;
                    }
                }
                else
                {
                    // Deprecation-only — plain [Obsolete] without DiagnosticId
                    csWriter.WriteLine($"[Obsolete(\"{UnsupportedSwiftTypeSupport.EscapeStringLiteral(message)}\")]");
                }
            }

            // The three IntelliSense-hide conditions below are NOT mutually exclusive in the
            // type system: EditorBrowsableAttribute is AllowMultiple=false, so emitting it twice
            // on one method is CS0579 and the whole binding fails to compile. Emit it at most
            // once — later conditions no-op if an earlier one already wrote the attribute.

            // Hide original method when a simplified throwing closure overload exists.
            // The post-processor emits the user-facing convenience overload (Action/Func params);
            // this hides the raw SwiftResult-based signature from IntelliSense.
            if (!editorBrowsableNeverEmitted && !_env.MethodDecl.IsAccessor && _env.MethodDecl.HasThrowingClosureSimplification)
            {
                csWriter.WriteLine("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
                editorBrowsableNeverEmitted = true;
            }

            // Hide the raw makeAsyncIterator once the IAsyncEnumerable bridge gives consumers
            // the idiomatic `await foreach` path. The method stays public and callable for
            // advanced use; this only removes it from IntelliSense. The flag is set by the host
            // handler ONLY when the Element gate succeeds, so a sequence with no bridge keeps
            // its raw factory visible as the sole consumption path. In today's fixtures
            // makeAsyncIterator carries a @_cdecl method wrapper (so hasJitRisk is false) and no
            // throwing closures, so it hits only this branch — but the guard makes the
            // single-attribute guarantee structural rather than incidental.
            if (!editorBrowsableNeverEmitted && !_env.MethodDecl.IsAccessor && _env.MethodDecl.HideRawAsyncIteratorSurface)
            {
                csWriter.WriteLine("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
                editorBrowsableNeverEmitted = true;
            }
        }

        /// <summary>
        /// Records the marker just written, so the report names the same members a consumer meets in
        /// the binding. Recorded here rather than recomputed from the declarations afterwards: the
        /// question is which attribute is in the emitted C#, and a later pass re-running the
        /// predicates would answer a different question that merely usually agrees.
        ///
        /// <para>The wrapper cause rides along only for the advisory id. The uncallable id already
        /// carries a specific sentence about why no call route exists, and the missing-symbol id is
        /// about a symbol the library did not export — attaching an eligibility token to either
        /// would put a true statement next to a diagnostic it did not cause.</para>
        ///
        /// <para>The name and the static-ness recorded are the ones this emitter is writing, not the
        /// ones the Swift declaration implies. They diverge on two shapes: a failable init emits as a
        /// static factory under <paramref name="emittedName"/>, and a free function or async
        /// constructor emits <c>static</c> from its parent rather than its method type. Recording the
        /// declaration's answer instead names a member the binding does not have, and drops the
        /// static factor from the prominence score of one that it does.</para>
        /// </summary>
        private void RecordSafetyMarked(string diagnosticId, bool isDeprecated, string? emittedName)
        {
            var wrapperReason = diagnosticId == WrapperValidation.DirectCallConvSwiftDiagnosticId
                ? WrapperValidation.GetWrapperRejectionReason(_env)
                : null;

            ReportCollector.RecordMemberSafetyMarked(
                _env.MethodDecl,
                _env.MethodDecl.ParentDecl,
                emittedName ?? _env.CSharpMethodName,
                diagnosticId,
                wrapperReason,
                isDeprecated,
                EmitsStatic);
        }

        /// <summary>
        /// Whether the member this emitter writes carries the <c>static</c> keyword. Single source
        /// for the signature line and the report row, which have to agree: the two once answered it
        /// from different expressions, and a free function emitted <c>static</c> while the report
        /// called it an instance member.
        /// </summary>
        private bool EmitsStatic => _env.EmitsStatic;

        /// <summary>
        /// Returns true if this method's return type was recorded as a silent tombstone
        /// (emitted with [OpaqueSwiftType] but zero usable members). Optional&lt;T&gt; is unwrapped
        /// so Optional of a tombstone is also flagged. Constructors and void returns short-circuit.
        /// </summary>
        private bool IsReturnTypeSilentTombstone()
        {
            if (_env.MethodDecl.IsConstructor)
                return false;
            if (_env.MethodDecl.CSSignature.Count == 0)
                return false;

            var returnSpec = _env.MethodDecl.CSSignature[0].SwiftTypeSpec;
            if (returnSpec is null)
                return false;

            var unwrapped = MarshallingHelpers.UnwrapOptionalTypeSpec(returnSpec) ?? returnSpec;
            if (unwrapped is not NamedTypeSpec named)
                return false;

            return _emissionContext.IsSilentTombstone(named.Name);
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

            // A return that projects to Swift.Runtime.ExistentialUnion is NOT a degradation — the wrapper
            // returns the projected union type, not object — so it must NOT carry the degradation marker.
            // The degradation oracle (TryFindFallbackInfo) is type-based and direction-blind and would match
            // the PAT existential regardless, so consult the SAME single position/engine predicate the
            // signature builder and MethodHandler's degradation suppression use, keeping the emitted signature
            // type and this marker in lockstep (a parameter/settable-getter/async/subscript of the same type
            // stays object and keeps the marker).
            if (_env.ReturnProjectsToExistentialUnion) return;

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
                if (TypeSkipConditions.ClassAncestorWillBeSkipped(ancestor))
                    return false; // ancestor won't be emitted, so it can't serve as a C# base
                if (ancestor.Methods.Any(m =>
                    m.WasEmitted
                    && m.Name == method.Name
                    && !m.IsAccessor
                    && !m.IsConstructor
                    && (m.CSSignature.Count - 1) == paramCount
                    && ParameterTypesMatch(m, paramTypes)
                    && (derivedCSharpName == null || AncestorCSharpNameMatches(m, ancestor, derivedCSharpName, typeDatabase))))
                    return true;
                // Same-module walk reached a class whose parent lives cross-module — fall through
                // to the cross-module record check (verifying against the parent's persisted
                // EmittedClassMethods) so we don't lose the override at a 3-level chain.
                if (!ancestor.HasResolvedSuperclass && ancestor.HasCrossModuleSwiftSuperclass && method.IsOverride
                    && CrossModuleAncestorHasMethod(ancestor.CrossModuleSuperclassRecord, method, paramCount, paramTypes, derivedCSharpName, typeDatabase))
                    return true;
                ancestor = ancestor.ResolvedSuperclass;
            }
            // Cross-module fallthrough at the immediate parent: consult the parent module's
            // persisted EmittedClassMethods (populated by ClassHandler.PopulateEmittedClassMethods
            // post-emission and serialized via ModuleDatabaseEmitter). Verifying that the parent
            // actually emitted a matching method prevents silent CS0115 when a validation gate
            // skipped the parent method. When the persisted list is null (legacy XML database
            // generated before this field existed) we fall back to trusting Swift's IsOverride
            // bit — that preserves the v0.8.x behavior for already-published parent NuGets.
            if (classDecl.HasCrossModuleSwiftSuperclass && method.IsOverride
                && CrossModuleAncestorHasMethod(classDecl.CrossModuleSuperclassRecord, method, paramCount, paramTypes, derivedCSharpName, typeDatabase))
                return true;
            return false;
        }

        /// <summary>
        /// Verifies the cross-module parent's TypeRecord actually has an emitted instance method
        /// matching the override target's Swift name + parameter Swift type strings. The verifier
        /// walks the parent record chain (parent → grandparent → …) via each record's
        /// <see cref="TypeRecord.SuperclassTypeName"/>, so an override targeting a method declared
        /// on a cross-module grandparent (rather than the immediate parent) still resolves — that
        /// matches Swift's vtable rules where <c>override</c> binds to whichever ancestor first
        /// declared the slot. Returns true at any point in the chain where the persisted list is
        /// null (legacy database before <see cref="TypeRecord.EmittedClassMethods"/> existed) so
        /// already-published parent NuGets compile against newly generated children.
        /// </summary>
        private static bool CrossModuleAncestorHasMethod(
            TypeRecord? parentRecord,
            MethodDecl method,
            int paramCount,
            List<string> paramTypes,
            string? derivedCSharpName,
            ITypeDatabase? typeDatabase)
        {
            var current = parentRecord;
            while (current != null)
            {
                // Legacy XML database at any point in the chain: preserve prior trust-the-Swift-bit
                // behavior so already-published parent NuGets keep compiling.
                if (current.EmittedClassMethods == null) return true;

                foreach (var emitted in current.EmittedClassMethods)
                {
                    if (emitted.SwiftName != method.Name) continue;
                    if (emitted.ParameterSwiftTypes.Count != paramCount) continue;
                    bool allMatch = true;
                    for (int i = 0; i < paramCount; i++)
                    {
                        if (emitted.ParameterSwiftTypes[i] != paramTypes[i]) { allMatch = false; break; }
                    }
                    if (!allMatch) continue;
                    // C# name parity: NameProvider can rename methods in the parent binding due
                    // to property/nested-type collisions or self-returning builder rules
                    // (see AncestorCSharpNameMatches). Swift name + parameter types alone are
                    // not enough — the derived class must emit the SAME C# name the parent did,
                    // otherwise `override` targets a non-existent method. Empty CSharpName means
                    // the persisted record predates this field (loaded from a legacy database
                    // generated before EmittedClassMethod gained CSharpName) — skip the check
                    // so already-published parent NuGets keep compiling.
                    if (derivedCSharpName != null
                        && !string.IsNullOrEmpty(emitted.CSharpName)
                        && emitted.CSharpName != derivedCSharpName)
                        continue;
                    return true;
                }

                // Walk up the chain via the record's SuperclassTypeName. Without a TypeDatabase
                // the verifier cannot resolve the next record — the cross-module override target
                // will fall back to `virtual`. (Tests that don't pass a TypeDatabase exercise the
                // immediate-parent case only.)
                if (typeDatabase == null || current.SuperclassTypeName == null)
                    return false;
                if (!typeDatabase.TryGetTypeRecord(current.SuperclassTypeName, out var next)
                    || next.Kind != TypeRecordKind.Class)
                    return false;
                current = next;
            }
            return false;
        }

        /// <summary>
        /// Computes the C# method name for an ancestor method and checks if it matches the derived name.
        /// </summary>
        private static bool AncestorCSharpNameMatches(MethodDecl ancestorMethod, ClassDecl ancestorClass, string derivedCSharpName, ITypeDatabase? typeDatabase)
            // Prefer the ground-truth emitted name. It carries the collision-disambiguation
            // name (`Handle`/`HandleWithInt`, assigned per-class-body at emission via DisambiguatedNameInput)
            // that ComputeMethodCSharpName recomputes WITHOUT — a fresh NameProvider pass cannot see
            // a suffix that only exists because a sibling already claimed the base name. The ancestor
            // is emitted before the derived class whose override we verify, so its EmittedCSharpName
            // is already stamped (IHandler.HandleBaseDecl, `methodDecl.EmittedCSharpName = env.Csharp-
            // MethodName`); fall back to recompute only for an unstamped
            // ancestor, which degrades to the prior behavior. Mirrors the cross-module path's
            // emitted.CSharpName check (CrossModuleAncestorHasMethod) and ClassHandler.cs:582-583.
            => (ancestorMethod.EmittedCSharpName ?? ComputeMethodCSharpName(ancestorMethod, ancestorClass, typeDatabase)) == derivedCSharpName;

        /// <summary>
        /// Computes the public C# method name as it would appear after NameProvider renaming
        /// for the given method on the given class (property collisions, nested-type collisions,
        /// self-returning builder rules, "Get" prefix, "Async" suffix). Single source of truth
        /// shared by the same-module override verifier and the cross-module
        /// <see cref="ClassHandler.PopulateEmittedClassMethods"/> populator — the latter persists
        /// the result on each <see cref="EmittedClassMethod"/> so a downstream module can compare
        /// names without recomputing renames it can't see.
        ///
        /// Uses the production <see cref="NameProvider.ComputePropertyRenames(TypeDecl, ITypeDatabase)"/>
        /// path (which applies type-based filtering and AsyncStream handling) when a TypeDatabase is
        /// available. Falls back to <see cref="NameProvider.ComputePropertyRenamesForNestedTypeCollisions"/>
        /// for callers (e.g. unit tests) that don't set up a full type database.
        /// </summary>
        internal static string ComputeMethodCSharpName(MethodDecl method, ClassDecl classDecl, ITypeDatabase? typeDatabase)
        {
            // Build property name set matching ClassHandler.cs:262-267:
            // - GetPropertyName (handles keyword escaping, wrapper sanitization, type-name collision)
            // - GetFinalMemberName (applies property renames computed identically to ClassHandler.cs:104)
            // - Nested type names (CS0102 collision with method names)
            // Production uses ALL declared properties (not just emitted ones) in the final
            // collision set — a non-emitted property still occupies the name and can cause
            // method name collisions.
            var propertyRenames = typeDatabase != null
                ? NameProvider.ComputePropertyRenames(classDecl, typeDatabase)
                : NameProvider.ComputePropertyRenamesForNestedTypeCollisions(
                    classDecl.Properties.Select(p => NameProvider.GetPropertyName(p, classDecl.Name)),
                    classDecl.Types.Select(t => t.Name));
            var props = new HashSet<string>(
                classDecl.Properties
                    .Select(p => NameProvider.GetFinalMemberName(
                        NameProvider.GetPropertyName(p, classDecl.Name), propertyRenames)),
                StringComparer.Ordinal);
            // Nested type names collide with method names in C# (CS0102) — reserve the EMITTED
            // leaf so a renamed nested type (e.g. Entry → EntryInfo) forces a method projecting
            // to the renamed name to disambiguate, not one projecting to the pre-rename name.
            // typeDatabase may be null in the test-only fallback path above; the helper then
            // degrades to the raw ToPascalCase leaf, preserving the prior behavior.
            foreach (var nestedType in classDecl.Types)
                props.Add(NameProvider.GetEmittedNestedTypeLeafName(nestedType, typeDatabase));

            // Build the context via ForMethod — the same builder the emitted name derives from —
            // so this predictor folds in every collision-shaping axis (including
            // ParentGenericParameterNames, which a positional call would silently drop and
            // mispredict `T` where the body emitted `TMethod`). The property set is this
            // predictor's richer local derivation (renames + nested-type names), and
            // ParentTypeName is pinned to the passed-in class so test-only callers that
            // construct methods without a parent keep the prior behavior.
            var ctx = PublicMethodNameContext.ForMethod(method, props) with { ParentTypeName = classDecl.Name };
            return NameProvider.GetPublicMethodName(in ctx);
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
                if (TypeSkipConditions.ClassAncestorWillBeSkipped(ancestor))
                    return false;
                if (ancestor.Properties.Any(p => p.WasEmitted && p.Name == propertyName))
                    return true;
                ancestor = ancestor.ResolvedSuperclass;
            }
            // Cross-module Swift parent: we don't carry per-property emission info on the
            // parent's TypeRecord, so the safest answer is "no". Callers fall back to the
            // virtual / new path. If a cross-module parent's binding does redeclare the
            // same property, the C# compiler reports CS0108 and the user adds `new`/`override`
            // by hand. Inheriting and not redeclaring (the common case) is unaffected.
            return false;
        }
    }
}
