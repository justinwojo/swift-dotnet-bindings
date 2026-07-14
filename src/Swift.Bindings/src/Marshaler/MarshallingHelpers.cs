// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    public static class MarshallingHelpers
    {
        private static readonly SwiftTypeName SwiftStringTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String");
        private static readonly SwiftTypeName SwiftArrayTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array");
        private static readonly SwiftTypeName SwiftDictionaryTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Dictionary");
        private static readonly SwiftTypeName SwiftSetTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Set");
        private static readonly SwiftTypeName SwiftOptionalTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional");
        private static readonly SwiftTypeName SwiftResultTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Result");

        /// <summary>
        /// Determines whether the specified type spec represents a type that can be
        /// automatically converted to/from an idiomatic .NET type (String, Array, Dictionary, or Optional).
        /// </summary>
        public static bool IsConvertibleType(TypeSpec? typeSpec)
        {
            return IsSwiftString(typeSpec) ||
                   IsLocalizedStringResource(typeSpec) ||
                   IsSwiftArray(typeSpec) ||
                   IsSwiftDictionary(typeSpec) ||
                   IsSwiftSet(typeSpec) ||
                   IsSwiftOptional(typeSpec) ||
                   IsFoundationDate(typeSpec);
        }

        /// <summary>
        /// Checks whether the type spec represents Foundation.Date.
        /// Date needs conversion: Swift Date = Double (8 bytes), C# DateTimeOffset = 12 bytes.
        /// The type database maps Date → double (matching ABI). DateProjection handles
        /// the double ↔ DateTimeOffset conversion in method bodies and property accessors.
        /// </summary>
        public static bool IsFoundationDate(TypeSpec? typeSpec)
        {
            return typeSpec is NamedTypeSpec named && named.Name == "Foundation.Date";
        }

        /// <summary>
        /// Checks whether a type spec is a <see cref="NamedTypeSpec"/> with a module
        /// and matches the given <paramref name="expectedName"/>.
        /// </summary>
        private static bool MatchesSwiftTypeName(TypeSpec? typeSpec, SwiftTypeName expectedName)
        {
            if (typeSpec is not NamedTypeSpec namedTypeSpec)
                return false;
            if (!namedTypeSpec.HasModule())
                return false;
            return SwiftTypeName.FromTypeSpec(namedTypeSpec).Equals(expectedName);
        }

        /// <summary>
        /// Determines whether the specified type spec represents Swift.String.
        /// </summary>
        public static bool IsSwiftString(TypeSpec? typeSpec) => MatchesSwiftTypeName(typeSpec, SwiftStringTypeName);

        /// <summary>
        /// Checks whether the type spec represents <c>Foundation.LocalizedStringResource</c>
        /// (iOS 16+). Its public ABI is a String wrapper, so on the simple concrete @_cdecl
        /// wire path it is projected to a C# <c>string</c> via <see cref="StringProjection"/>:
        /// the wire format is identical to a Swift.String, and the wrapper body converts with
        /// <c>LocalizedStringResource(stringLiteral:)</c> (param) / <c>String(localized:)</c>
        /// (return). The type is not yet present in the .NET Foundation assembly, so any
        /// non-scalar use (container/closure/protocol position) stays dropped — see the
        /// <c>allowProjectableScalar</c> carve-out in
        /// <see cref="ValidationRuleSet.ClassifyUnsupportedReference"/>.
        /// </summary>
        public static bool IsLocalizedStringResource(TypeSpec? typeSpec)
            => typeSpec is NamedTypeSpec named && named.Name == "Foundation.LocalizedStringResource";

        /// <summary>
        /// Whether a method/constructor is on the simple concrete @_cdecl wire path that can
        /// carry a carved-out scalar <c>LocalizedStringResource</c> as a string. Async,
        /// method-generic, and generic-parent members route to specialized emitters that do
        /// not know the LSR ↔ string conversion, so they must NOT receive the carve-out (the
        /// member is dropped with an accurate net-unavailable reason instead).
        /// </summary>
        public static bool AllowsProjectableScalarCarveOut(MethodDecl method)
            => !method.IsAsync
               && !method.IsGeneric
               && !HasGenericParent(method.ParentDecl);

        /// <summary>
        /// Property counterpart of <see cref="AllowsProjectableScalarCarveOut(MethodDecl)"/>.
        /// Async properties are re-emitted as methods upstream (so a PropertyDecl reaching the
        /// gate is synchronous); generic-parent properties route through the
        /// constrained-extension/specialization paths that do not know the LSR ↔ string conversion.
        /// </summary>
        public static bool AllowsProjectableScalarCarveOut(PropertyDecl property)
            => !HasGenericParent(property.ParentDecl);

        private static bool HasGenericParent(BaseDecl? parentDecl)
            => parentDecl is TypeDecl typeDecl && typeDecl.GenericParameters.Count > 0;

        /// <summary>
        /// Whether a SwiftString parameter should be decomposed into two nint words for @_cdecl
        /// constructor/method wrappers. The @_cdecl Swift wrappers receive String as two Int words
        /// (_sW0_, _sW1_), so the C# P/Invoke must emit matching nint pairs instead of a Buffer struct.
        /// Invariant: SwiftString.Buffer is exactly 16 bytes (two nint-sized words).
        /// A carved-out scalar <see cref="IsLocalizedStringResource"/> param marshals as a string
        /// (StringProjection) and the @_cdecl wrapper reconstructs the resource from the same two
        /// Int words, so it decomposes identically.
        /// </summary>
        public static bool ShouldDecomposeStringForCdecl(MethodDecl methodDecl, TypeSpec? typeSpec)
            => (methodDecl.UsesCdeclConstructorWrapper || methodDecl.UsesCdeclMethodWrapper)
                && (IsSwiftString(typeSpec) || IsLocalizedStringResource(typeSpec));

        /// <summary>
        /// Checks whether the type spec represents Foundation.Data.
        /// </summary>
        public static bool IsFoundationData(TypeSpec? typeSpec)
        {
            return typeSpec is NamedTypeSpec named && named.Name == "Foundation.Data";
        }

        /// <summary>
        /// Whether a Foundation.Data parameter should be decomposed into two nint words for @_cdecl
        /// constructor/method wrappers. The @_cdecl Swift wrappers receive Data as two Int words
        /// (_dW0_, _dW1_; see <see cref="CdeclParamMapper"/>), so the C# P/Invoke must emit a matching
        /// nint pair instead of passing the 16-byte Swift.Foundation.Data struct by value. Without
        /// this, on AArch64 a Data composite that lands after 7 leading integer args is split between
        /// the last GP register and the stack while the Swift side reads two whole-register Ints — the
        /// second word is lost. Mirrors <see cref="ShouldDecomposeStringForCdecl"/>.
        /// Invariant: Swift.Foundation.Data is exactly 16 bytes (two nint-sized words).
        ///
        /// Property/subscript (cdecl-property) wrappers are deliberately excluded: a setter's Data
        /// value is always at argument slot 0 or 1 (after at most a single self/inout pointer), so it
        /// never lands beyond x7. There "one 16-byte struct in x1:x2" and "two Int words in x1,x2" are
        /// ABI-identical, so passing the struct by value (the accessor path) matches the Swift side's
        /// two-word reconstruction without a register-straddle hazard. The constructor/method wrappers
        /// are the only paths where a Data composite can be pushed past x7 by leading scalar args.
        /// </summary>
        public static bool ShouldDecomposeDataForCdecl(MethodDecl methodDecl, TypeSpec? typeSpec)
            => (methodDecl.UsesCdeclConstructorWrapper || methodDecl.UsesCdeclMethodWrapper)
                && IsFoundationData(typeSpec);

        /// <summary>
        /// Determines whether the specified type spec represents Swift.Array.
        /// </summary>
        public static bool IsSwiftArray(TypeSpec? typeSpec) => MatchesSwiftTypeName(typeSpec, SwiftArrayTypeName);

        /// <summary>
        /// Determines whether the specified type spec represents Swift.Dictionary.
        /// </summary>
        public static bool IsSwiftDictionary(TypeSpec? typeSpec) => MatchesSwiftTypeName(typeSpec, SwiftDictionaryTypeName);

        /// <summary>
        /// Determines whether the specified type spec represents Swift.Set.
        /// </summary>
        public static bool IsSwiftSet(TypeSpec? typeSpec) => MatchesSwiftTypeName(typeSpec, SwiftSetTypeName);

        /// <summary>
        /// Determines whether the specified type spec represents Swift.Optional.
        /// </summary>
        public static bool IsSwiftOptional(TypeSpec? typeSpec) => MatchesSwiftTypeName(typeSpec, SwiftOptionalTypeName);

        /// <summary>
        /// Determines whether the specified type spec represents Swift.Result&lt;Success, Failure&gt;.
        /// </summary>
        public static bool IsSwiftResult(TypeSpec? typeSpec) => MatchesSwiftTypeName(typeSpec, SwiftResultTypeName);

        /// <summary>
        /// Determines whether the specified type spec represents the read-only Swift.UnsafeRawBufferPointer.
        /// Marshalled via splitting into (pointer, length) at the @_cdecl C ABI boundary,
        /// bridged to ReadOnlySpan&lt;byte&gt; on the C# side.
        /// </summary>
        public static bool IsUnsafeRawBufferPointer(TypeSpec? typeSpec)
        {
            return typeSpec is NamedTypeSpec named && named.Name == "Swift.UnsafeRawBufferPointer";
        }

        /// <summary>
        /// Determines whether the specified type spec represents the writable Swift.UnsafeMutableRawBufferPointer.
        /// Marshalled identically to <see cref="IsUnsafeRawBufferPointer"/> at the C ABI boundary
        /// (split into IntPtr pointer + nint length); the C# side exposes Span&lt;byte&gt; instead of
        /// ReadOnlySpan&lt;byte&gt; so callers can observe Swift-side mutations after the synchronous call.
        /// The read-only/mutable distinction lives only on the Swift wrapper side.
        /// </summary>
        public static bool IsUnsafeMutableRawBufferPointer(TypeSpec? typeSpec)
        {
            return typeSpec is NamedTypeSpec named && named.Name == "Swift.UnsafeMutableRawBufferPointer";
        }

        /// <summary>
        /// Determines whether the specified type spec represents either the read-only
        /// Swift.UnsafeRawBufferPointer or the writable Swift.UnsafeMutableRawBufferPointer.
        /// Both share the same (IntPtr pointer, nint length) C ABI; differences live in the
        /// public C# parameter type (ReadOnlySpan vs Span) and the Swift-side reconstruction
        /// (UnsafeRawBufferPointer vs UnsafeMutableRawBufferPointer).
        /// </summary>
        public static bool IsAnyUnsafeRawBufferPointer(TypeSpec? typeSpec)
        {
            return IsUnsafeRawBufferPointer(typeSpec) || IsUnsafeMutableRawBufferPointer(typeSpec);
        }

        /// <summary>
        /// Determines whether the specified type spec represents Swift.Optional wrapping
        /// an ObjC bridged type (e.g., Optional&lt;UIImage&gt;, Optional&lt;NSUrlResponse&gt;).
        /// ObjC optionals use nullable pointer ABI (nil = IntPtr.Zero), not SwiftOptional layout.
        /// </summary>
        public static bool IsOptionalObjCBridged(TypeSpec? typeSpec, ITypeDatabase typeDatabase)
        {
            if (!IsSwiftOptional(typeSpec))
                return false;
            var namedType = (NamedTypeSpec)typeSpec!;
            if (namedType.GenericParameters.Count != 1)
                return false;
            var inner = namedType.GenericParameters[0];
            if (inner is not NamedTypeSpec innerNamed || !innerNamed.HasModule())
                return false;
            var innerTypeName = SwiftTypeName.FromModuleQualifiedName(innerNamed.Name);
            if (typeDatabase.TryGetTypeRecord(innerTypeName, out var typeRecord))
                return IsObjCBridged(typeRecord) || IsObjCBridgeable(typeRecord);
            // Fallback: Apple framework ObjC classes (e.g., QuartzCore.CALayer) not in the module
            // database. Shares the exact four-clause heuristic core with TypeProjectionFactory's
            // Optional<T> / collection-element ObjC fallbacks via IsObjCPrefixBridgeCandidate, so
            // the two readers can no longer drift (the parity constraints.md demands).
            return IsObjCPrefixBridgeCandidate(innerNamed);
        }

        /// <summary>
        /// The ObjC-prefix bridging heuristic core: an Apple-framework reference type that has NO
        /// database record but is recognized as an ObjC class purely by its owning module and a
        /// 2–3 letter uppercase class-name prefix (UI/NS/CA/SK/…). This is the single source of
        /// truth for the four-clause heuristic shared by <see cref="IsOptionalObjCBridged"/> and
        /// <c>TypeProjectionFactory</c>'s Optional&lt;T&gt; inner and collection-element ObjC
        /// fallbacks; extracting it makes their long-standing "must stay in sync" parity structural
        /// rather than copy-paste. The value-type guard is load-bearing: an ObjC prefix alone does
        /// not prove a class (e.g. <c>PassKit.PKPaymentNetwork</c> is a value type with a PK
        /// prefix), and bridging a value type here would emit the wrong ARC shape. Clause order is
        /// immaterial — every clause is a side-effect-free registry/set lookup.
        /// </summary>
        public static bool IsObjCPrefixBridgeCandidate(NamedTypeSpec named)
        {
            ArgumentNullException.ThrowIfNull(named);
            return AppleFrameworkRegistry.IsOptionalFallbackModule(named.Module) &&
                !AppleFrameworkRegistry.IsNestedType(named.Name) &&
                !TypeDatabaseExtensions.IsKnownAppleValueType(named) &&
                AppleFrameworkRegistry.HasObjCClassPrefix(named.Name);
        }

        public static bool MethodRequiresIndirectResult(MethodEnvironment env)
        {
            if (env.MethodDecl.IsAsync) return false;

            if (IsCdeclNonSetterWrapper(env))
            {
                var cdeclResult = IsCdeclIndirectResultRequired(env);
                if (cdeclResult.HasValue) return cdeclResult.Value;
            }

            var ctorResult = IsConstructorIndirectResultRequired(env);
            if (ctorResult.HasValue) return ctorResult.Value;

            return IsTypeInherentlyIndirect(env);
        }

        /// <summary>
        /// Returns true if the method uses a @_cdecl wrapper and is NOT a setter.
        /// This guard condition appears repeatedly in indirect-result logic because
        /// setters return void and never need indirect result handling.
        /// </summary>
        internal static bool IsCdeclNonSetterWrapper(MethodEnvironment env)
        {
            return (env.MethodDecl.UsesCdeclPropertyWrapper || env.MethodDecl.UsesCdeclMethodWrapper)
                && !MethodIsSetter(env.MethodDecl);
        }

        /// <summary>
        /// Determines indirect result requirements specific to @_cdecl wrappers.
        /// Checks String, existential, Optional&lt;value&gt;, closure, DynamicSelf, and tuple returns.
        /// Returns null if no @_cdecl-specific decision applies (fall through to general logic).
        /// </summary>
        internal static bool? IsCdeclIndirectResultRequired(MethodEnvironment env)
        {
            var returnTypeForCdecl = env.MethodDecl.CSSignature.First();

            // Void returns never need indirect result
            if (returnTypeForCdecl.SwiftTypeSpec.IsEmptyTuple)
                return false;

            // String and the LocalizedStringResource carve-out both return their UTF-8 bytes via
            // the resultPtr out-parameter (SBW_Utf8Slice) — @_cdecl can't return a Swift struct.
            if (returnTypeForCdecl.SwiftTypeSpec is NamedTypeSpec nts &&
                (nts.Name == "Swift.String" || IsLocalizedStringResource(nts)))
                return true;

            // @objc protocol existential returns (any P / (any P)?): a single 8-byte object pointer,
            // returned BY VALUE via the ClassPointer / OptionalClassPointer convention — NOT through an
            // indirect result buffer. Decided before the generic existential arms below, which would
            // otherwise force the 40-byte opaque-container indirect path. (Checked here, ahead of the
            // generic existential branches that follow.)
            if (ExistentialHandler.IsObjCProtocolExistentialSpec(returnTypeForCdecl.SwiftTypeSpec, env.TypeDatabase))
                return false;

            // Existential returns: @_cdecl can't return existential containers directly.
            if (env.ExistentialHandler.IsExistential(returnTypeForCdecl.SwiftTypeSpec))
                return true;

            // Optional<existential> returns: too large (40+ bytes) for register return.
            if (env.ExistentialHandler.IsOptionalExistential(returnTypeForCdecl.SwiftTypeSpec))
                return true;

            // Optional<value-type>: @_cdecl can't return generics directly.
            // Exception: Optional<ObjC-bridgeable container> (e.g., [URL]?) returns as nullable ObjC pointer.
            if (MethodWrapperEmitter.IsOptionalType(returnTypeForCdecl.SwiftTypeSpec) &&
                !CdeclParamMapper.IsOptionalWithReferenceInner(returnTypeForCdecl.SwiftTypeSpec, env.TypeDatabase) &&
                !CdeclParamMapper.IsOptionalObjCBridgeableContainer(returnTypeForCdecl.SwiftTypeSpec, env.TypeDatabase))
                return true;

            // Closure returns: @_cdecl can't return closures directly — write to resultPtr buffer.
            if (returnTypeForCdecl.SwiftTypeSpec is ClosureTypeSpec)
                return true;

            // Bound generic returns requiring marshalling: @_cdecl can't return generics directly.
            // Swift wrapper writes to resultPtr via initializeMemory(as:).
            // Exceptions: Optional (handled above), ObjC-bridgeable containers (retained pointer),
            // bound-generic CLASS returns (use ClassPointer convention — retained AnyObject pointer
            // returned by value; matches CdeclReturnMapping.Classify's typeRecord.Kind == Class branch).
            if (env.BoundGenericsHandler.IsBoundGeneric(returnTypeForCdecl) &&
                env.BoundGenericsHandler.RequiresBoundGenericMarshalling(returnTypeForCdecl) &&
                !MethodWrapperEmitter.IsOptionalType(returnTypeForCdecl.SwiftTypeSpec) &&
                !CdeclParamMapper.IsObjCBridgeableContainer(returnTypeForCdecl.SwiftTypeSpec, env.TypeDatabase) &&
                !IsBoundGenericClassReturn(returnTypeForCdecl.SwiftTypeSpec, env.TypeDatabase))
                return true;

            // DynamicSelf (Self): @_cdecl wrapper returns retained class pointer directly.
            if (returnTypeForCdecl.SwiftTypeSpec.IsDynamicSelf)
                return false;

            // Tuple returns: @_cdecl wrapper writes result to resultPtr buffer.
            if (returnTypeForCdecl.SwiftTypeSpec is TupleTypeSpec ts && !ts.IsEmptyTuple)
                return true;

            return null; // No @_cdecl-specific decision — fall through
        }

        /// <summary>
        /// Returns true when the bound-generic type's parent generic (the unbound type) is a
        /// Swift class in the database. Bound-generic class returns use the ClassPointer
        /// convention — the @_cdecl wrapper returns the retained AnyObject pointer by value
        /// as UnsafeMutableRawPointer; the C# P/Invoke must receive IntPtr directly, NOT via
        /// indirect resultPtr buffer. Mirrors CdeclReturnMapping.Classify's class branch.
        /// </summary>
        internal static bool IsBoundGenericClassReturn(TypeSpec typeSpec, ITypeDatabase typeDatabase)
        {
            if (typeSpec is not NamedTypeSpec namedTypeSpec)
                return false;
            var swiftTypeName = SwiftTypeName.FromTypeSpec(namedTypeSpec);
            return typeDatabase.TryGetTypeRecord(swiftTypeName, out var record) &&
                   record.Kind == TypeRecordKind.Class;
        }

        /// <summary>
        /// Determines indirect result requirements for constructors.
        /// Failable constructors and non-frozen struct constructors need indirect result.
        /// Returns null if the method is not a constructor or no constructor-specific rule applies.
        /// </summary>
        internal static bool? IsConstructorIndirectResultRequired(MethodEnvironment env)
        {
            if (!env.MethodDecl.IsConstructor) return null;

            // Failable constructors (init?) normally need indirect result because they return
            // Optional<Self> which must be checked for None before extracting the value.
            //
            // Exception: a CLASS routed through a @_cdecl wrapper. The Swift wrapper returns a
            // nullable retained class pointer (UnsafeMutableRawPointer?) DIRECTLY — nil maps to a
            // null pointer — exactly like a non-failable class constructor. The Swift side proves
            // this independently: CdeclSignatureContract emits no ResultPtr phase for a class
            // constructor (needsResultPtr = !isClass). So the C# P/Invoke must also return the
            // pointer directly; adding a leading resultPtr here would shift every real argument one
            // slot to the right (the first scalar/pointer arg lands in the wrong Swift parameter).
            if (env.MethodDecl.IsFailable)
                return !(env.MethodDecl.UsesCdeclConstructorWrapper && env.ParentDecl is ClassDecl);

            // Non-frozen struct constructors use indirect result (struct too large for registers).
            // Class constructors return a pointer directly — NOT via indirect result.
            // Enum constructors fall through to type-based checks.
            if (env.ParentDecl is StructDecl structDecl && !structDecl.IsFrozen) return true;

            // @_cdecl constructor wrappers for structs/enums always write to resultPtr.
            // The Swift @_cdecl function signature takes UnsafeMutableRawPointer as the first
            // parameter and returns void (see CdeclSignatureContract: "Struct constructors
            // always write to result buffer"). The C# P/Invoke must match by adding an IntPtr
            // resultPtr parameter and returning void, not returning the struct by value.
            if (env.MethodDecl.UsesCdeclConstructorWrapper && env.ParentDecl is not ClassDecl)
                return true;

            return null; // Enum constructors or frozen struct constructors — fall through
        }

        /// <summary>
        /// Type-based indirect result determination for non-@_cdecl and non-constructor cases.
        /// Checks DynamicSelf, closures, existentials, tuples, bound generics, and TypeRecord-based dispatch.
        /// </summary>
        internal static bool IsTypeInherentlyIndirect(MethodEnvironment env)
        {
            var returnType = env.MethodDecl.CSSignature.First();
            bool isCdeclNonSetter = IsCdeclNonSetterWrapper(env);

            // DynamicSelf (Self return type) always requires indirect result.
            if (returnType.SwiftTypeSpec.IsDynamicSelf)
                return true;

            // Closure return types: non-cdecl passes as function pointers directly.
            if (returnType.SwiftTypeSpec is ClosureTypeSpec)
                return false;

            // Existential return types (protocol types) are passed via existential containers (IntPtr)
            if (env.ExistentialHandler.IsExistential(returnType.SwiftTypeSpec))
                return false;

            // Optional<existential> return types: too large for register return via CallConvSwift.
            // When the @_cdecl path didn't catch this (e.g., flag timing), force indirect result.
            if (env.ExistentialHandler.IsOptionalExistential(returnType.SwiftTypeSpec) &&
                (env.MethodDecl.UsesCdeclPropertyWrapper || env.MethodDecl.UsesCdeclMethodWrapper))
                return true;

            // Non-generic tuple return types are handled by TupleHandler, not via indirect result.
            // Tuples with generic type parameter elements require indirect result (sret).
            if (returnType.SwiftTypeSpec is TupleTypeSpec tupleSpec && !tupleSpec.IsEmptyTuple)
            {
                var tupleHandler = new TupleHandler(env.TypeDatabase);
                return tupleHandler.HasGenericTypeParameterElements(tupleSpec);
            }

            // Bound generics that require marshalling return IntPtr directly from PInvoke.
            if (!env.MethodDecl.IsConstructor &&
                env.BoundGenericsHandler.IsBoundGeneric(returnType) &&
                env.BoundGenericsHandler.RequiresBoundGenericMarshalling(returnType))
            {
                // @_cdecl Optional<value-type> or Optional<existential>: force IndirectResult.
                if (isCdeclNonSetter &&
                    MethodWrapperEmitter.IsOptionalType(returnType.SwiftTypeSpec) &&
                    !CdeclParamMapper.IsOptionalWithReferenceInner(returnType.SwiftTypeSpec, env.TypeDatabase))
                {
                    return true;
                }
                // Optional<existential> without @_cdecl flag (e.g., property accessor where
                // UsesCdeclPropertyWrapper isn't visible yet): check existential type directly.
                // Exception: Optional<any Error> is 8 bytes (boxed reference, nil = 0) and
                // returned directly in x0 — keep it on the direct-IntPtr path so the wrapper
                // body uses the returned pointer instead of a never-written sret buffer.
                // Both predicates (IsOptionalExistential + IsProtocolExistentialType) match
                // Optional<any Error>, so the AnyError exclusion has to gate the whole branch.
                if (!env.ExistentialHandler.IsOptionalAnyError(returnType.SwiftTypeSpec) &&
                    (env.ExistentialHandler.IsOptionalExistential(returnType.SwiftTypeSpec) ||
                     CdeclParamMapper.IsProtocolExistentialType(returnType.SwiftTypeSpec, env.TypeDatabase)))
                {
                    return true;
                }
                return false;
            }

            if (returnType.IsGeneric) return true;

            TypeRecord typeRecord = env.TypeDatabase.GetTypeRecordOrThrow(returnType.SwiftTypeSpec);

            // @_cdecl: NSString typedef structs need indirect result.
            if (isCdeclNonSetter &&
                IsObjCBridged(typeRecord) &&
                returnType.SwiftTypeSpec is NamedTypeSpec nsTypedefSpec &&
                AppleFrameworkRegistry.TryGetNetTypeName(nsTypedefSpec.Name, out var remappedName) &&
                remappedName == "Foundation.NSString")
                return true;

            // Swift classes return pointers directly in registers
            if (typeRecord.Kind == TypeRecordKind.Class)
                return false;

            // Simple enums are C# value types returned directly in registers.
            // Note: non-frozen simple enums technically use indirect return under resilient ABI
            // when called via direct CallConvSwift, but this is handled at the PInvokeEmitter
            // level (which knows whether a wrapper is present). This general query returns false
            // for all simple enums so that wrapper-protected paths aren't affected.
            if (typeRecord.Kind == TypeRecordKind.Enum &&
                (typeRecord.Flags & TypeRecordFlags.SimpleEnum) != 0)
                return false;

            // ObjC-bridgeable value types (e.g., URL) return as ObjC class pointers, not indirect result.
            if (IsObjCBridgeable(typeRecord))
                return false;

            if (!IsTypeFrozen(typeRecord)) return true;

            // @_cdecl: frozen structs also need indirect result (except primitives).
            if (isCdeclNonSetter &&
                !CdeclParamMapper.IsCdeclPrimitive(returnType.SwiftTypeSpec))
                return true;

            return false;
        }

        public static bool MethodRequiresSwiftSelf(MethodEnvironment env)
        {
            if (env.ParentDecl is ModuleDecl) return false; // global funcs
            if (env.MethodDecl.MethodType == MethodType.Static) return false;
            if (env.MethodDecl.IsConstructor) return false;

            return true;
        }

        /// <summary>
        /// True for non-cdecl sync returns of multi-element tuples where every top-level element
        /// is a bare generic type parameter. Such tuples are uniformly address-only in Swift's
        /// ABI and use split @out registers — one per element (x0, x1, …) rather than a single
        /// x8 SwiftIndirectResult register. The P/Invoke must declare N IntPtr result params,
        /// the wrapper must allocate per-element buffers, and the read site must marshal each
        /// element separately and synthesize the tuple.
        ///
        /// Mixed tuples (e.g., (T, Int) → (@out T, Int)) and tuples whose elements are bound
        /// generics returned direct (Array&lt;T&gt;, UnsafePointer&lt;T&gt;, etc.) use a different
        /// lowering and are NOT handled by this branch — see
        /// <see cref="IsUnmodeledMixedGenericTupleReturn"/>, which fails those shapes closed
        /// (member skipped) instead of letting them fall through to the single-x8
        /// SwiftIndirectResult fallback, whose register assignment does not match theirs.
        /// The narrow gate trades coverage for safety: only the fully bare-generic shape is
        /// provably uniform-@out across Swift's ABI rules.
        ///
        /// Excluded paths:
        ///   - @_cdecl wrappers (use a single resultPtr buffer the wrapper writes into).
        ///   - Native thunks (use AAPCS64 hidden x8 register for struct return buffers).
        ///   - Wrapper-library indirection (declared with explicit indirect-result shapes).
        ///   - Async methods (use callback flattening, not indirect result).
        ///   - Constructors (use _payload SafeHandle).
        ///   - Empty/single-element tuples (Swift collapses single element to bare type).
        ///   - Any tuple element that is not a bare generic type parameter.
        /// </summary>
        public static bool IsMultiElementGenericTupleIndirectReturn(MethodEnvironment env)
        {
            if (env.MethodDecl.IsConstructor) return false;
            if (env.MethodDecl.IsAsync) return false;
            if (env.MethodDecl.UsesCdeclWrapper) return false;
            if (env.MethodDecl.UsesNativeThunk) return false;
            if (env.MethodDecl.UsesWrapperLibrary) return false;

            var returnType = env.MethodDecl.CSSignature.First();
            if (returnType.SwiftTypeSpec is not TupleTypeSpec tupleSpec) return false;
            if (tupleSpec.IsEmptyTuple) return false;
            if (tupleSpec.Elements.Count < 2) return false;

            return env.TupleHandler.AllElementsAreBareGenericTypeParameter(tupleSpec);
        }

        /// <summary>
        /// True for non-cdecl sync returns of multi-element tuples that contain generic type
        /// parameters but are NOT uniformly bare (e.g. (T, Int), (Array&lt;T&gt;, T), (T, T?)).
        /// Swift lowers such a tuple result element-wise: each address-only element becomes its
        /// own leading indirect-result pointer argument (x0, x1, …) while loadable elements
        /// (Int, Array's ref, String's two words, …) return direct in result registers —
        /// verified against SIL (`(@out T, Int)`) and LLVM IR (`i64 f(ptr, …)`). Neither the
        /// single-x8 SwiftIndirectResult fallback nor the uniform multi-@out branch
        /// (<see cref="IsMultiElementGenericTupleIndirectReturn"/>) matches that register
        /// assignment, so emitting either produces a call that reads garbage or corrupts
        /// arguments. Callers must skip the member (fail closed) rather than emit.
        ///
        /// Excluded paths mirror <see cref="IsMultiElementGenericTupleIndirectReturn"/>:
        /// @_cdecl wrappers marshal the tuple through a wrapper-owned buffer, native thunks and
        /// wrapper-library shapes declare their own explicit lowering, async methods flatten
        /// results through a callback, and constructors cannot return tuples.
        /// </summary>
        public static bool IsUnmodeledMixedGenericTupleReturn(MethodEnvironment env)
        {
            if (env.MethodDecl.IsConstructor) return false;
            if (env.MethodDecl.IsAsync) return false;
            if (env.MethodDecl.UsesCdeclWrapper) return false;
            if (env.MethodDecl.UsesNativeThunk) return false;
            if (env.MethodDecl.UsesWrapperLibrary) return false;

            var returnType = env.MethodDecl.CSSignature.First();
            if (returnType.SwiftTypeSpec is not TupleTypeSpec tupleSpec) return false;
            if (tupleSpec.IsEmptyTuple) return false;
            if (tupleSpec.Elements.Count < 2) return false;

            return env.TupleHandler.HasGenericTypeParameterElements(tupleSpec) &&
                   !env.TupleHandler.AllElementsAreBareGenericTypeParameter(tupleSpec);
        }

        public static bool IsTypeFrozen(TypeRecord typeRecord)
        {
            return (typeRecord.Flags & TypeRecordFlags.Frozen) != 0;
        }

        public static bool RequiresMemoryManagement(TypeRecord typeRecord)
        {
            return (typeRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0;
        }

        public static bool IsFrozenStructProjectedAsClass(TypeRecord typeRecord)
        {
            return typeRecord.Kind == TypeRecordKind.Struct &&
                   (typeRecord.Flags & TypeRecordFlags.Frozen) != 0 &&
                   (typeRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0;
        }

        /// <summary>
        /// Determines if a type is an Objective-C bridged class that should be remapped
        /// to its .NET iOS binding equivalent (e.g., UIKit.UIImage instead of Swift.UIImage).
        /// </summary>
        /// <param name="typeRecord">The type record to check.</param>
        /// <returns>True if the type is ObjC bridged, false otherwise.</returns>
        public static bool IsObjCBridged(TypeRecord typeRecord)
        {
            return (typeRecord.Flags & TypeRecordFlags.ObjCBridged) != 0;
        }

        /// <summary>
        /// Checks whether a type record represents an ObjC-bridgeable value type (e.g., Foundation.URL).
        /// These Swift value types freely bridge to ObjC classes via _ObjectiveCBridgeable and cross
        /// the @_cdecl boundary as ObjC object pointers instead of Swift struct bytes.
        /// </summary>
        public static bool IsObjCBridgeable(TypeRecord typeRecord)
        {
            return (typeRecord.Flags & TypeRecordFlags.ObjCBridgeable) != 0;
        }

        /// <summary>
        /// Unwraps Optional&lt;T&gt; to get the inner type spec, recursively handling nested optionals.
        /// Returns null if the input is not a NamedTypeSpec.
        /// </summary>
        public static TypeSpec? UnwrapOptionalTypeSpec(TypeSpec typeSpec)
        {
            if (typeSpec is not NamedTypeSpec namedType)
                return null;

            var nameWithoutModule = namedType.Name.Contains('.')
                ? namedType.Name.Substring(namedType.Name.LastIndexOf('.') + 1)
                : namedType.Name;
            if (nameWithoutModule == "Optional" && namedType.GenericParameters.Count == 1)
                return UnwrapOptionalTypeSpec(namedType.GenericParameters[0]);

            return namedType;
        }

        /// <summary>
        /// Determines if a class type is rooted in an ObjC hierarchy (e.g., inherits from NSObject/CALayer).
        /// </summary>
        public static bool IsObjCRooted(TypeRecord typeRecord)
        {
            return (typeRecord.Flags & TypeRecordFlags.ObjCRooted) != 0;
        }

        /// <summary>
        /// Determines if a method is a property setter based on its name.
        /// Property setters are generated with names ending in "_Set".
        /// </summary>
        /// <param name="methodDecl">The method declaration to check.</param>
        /// <returns>True if the method is a property setter, false otherwise.</returns>
        public static bool MethodIsSetter(MethodDecl methodDecl)
        {
            return methodDecl.Name.EndsWith("_Set");
        }

        /// <summary>
        /// Maps a Swift module name to its corresponding .NET namespace.
        /// Returns the original module name if no mapping exists.
        /// All emission paths MUST use this method instead of maintaining local copies of the mapping.
        /// </summary>
        public static string MapSwiftModuleToNetNamespace(string swiftModule)
            => AppleFrameworkRegistry.MapModuleToNetNamespace(swiftModule);

        /// <summary>
        /// Maps a module-qualified Swift type name (e.g., "QuartzCore.CALayer") to its
        /// .NET equivalent (e.g., "CoreAnimation.CALayer"). If the module has no mapping,
        /// the original name is returned unchanged.
        /// </summary>
        public static string MapQualifiedTypeToNet(string qualifiedSwiftTypeName)
        {
            if (string.IsNullOrEmpty(qualifiedSwiftTypeName))
                return qualifiedSwiftTypeName;

            // Check explicit type remapping first (handles both module remapping AND type name changes)
            if (AppleFrameworkRegistry.TryGetNetTypeName(qualifiedSwiftTypeName, out var remapped))
                return remapped;

            var dotIndex = qualifiedSwiftTypeName.IndexOf('.');
            if (dotIndex <= 0)
                return qualifiedSwiftTypeName;

            var swiftModule = qualifiedSwiftTypeName.Substring(0, dotIndex);
            var typeName = qualifiedSwiftTypeName.Substring(dotIndex + 1);
            var netNamespace = MapSwiftModuleToNetNamespace(swiftModule);
            return $"{netNamespace}.{typeName}";
        }

        /// <summary>
        /// Replaces all known Swift module names with their .NET namespace equivalents
        /// within a free-form string (e.g., diagnostic messages, attribute content).
        /// Matches module names followed by a dot to avoid false positives.
        /// </summary>
        public static string MapModulesInString(string text)
            => AppleFrameworkRegistry.MapModulesInString(text);

        /// <summary>
        /// Gets the fully-qualified .NET base type name for an ObjC-rooted class.
        /// Maps the Swift module to the corresponding .NET namespace and uses the
        /// ObjC class name from the superclass chain.
        /// </summary>
        /// <param name="classDecl">The class declaration with an ObjC superclass.</param>
        /// <returns>The .NET type name (e.g., "CoreAnimation.CALayer", "UIKit.UIControl").</returns>
        public static string? GetObjCBaseTypeName(ClassDecl classDecl)
        {
            if (!classDecl.HasObjCSuperclass || classDecl.DirectSuperclassName == null)
                return null;

            // If the superclass is from an unsupported Apple module (XCTest, SwiftUI, etc.),
            // fall back to Foundation.NSObject — all ObjC-rooted types ultimately derive from it.
            // Source of truth is apple-frameworks.json's "unsupported" flag via
            // AppleFrameworkRegistry; do not duplicate the list here.
            var dotIdx = classDecl.DirectSuperclassName.IndexOf('.');
            if (dotIdx > 0)
            {
                var module = classDecl.DirectSuperclassName.Substring(0, dotIdx);
                if (AppleFrameworkRegistry.IsUnsupportedModule(module))
                    return "Foundation.NSObject";

                // A *pure* Objective-C superclass — one declared in ObjC headers and imported
                // into Swift under a stripped name (a Clang `swift_name`/NS_SWIFT_NAME attribute
                // or automatic prefix stripping): the ObjC class name carries a class prefix that
                // its Swift-exported name drops, so the superclass name carried in the ABI is the
                // shortened `<module>.<SwiftName>`. The dependency's C# binding for such a class is
                // produced by the ObjC ApiDefinition pipeline under its full ObjC name, not the
                // stripped Swift name, so a base reference built from the Swift superclass name
                // resolves to a type that does not exist. The authoritative ObjC class name is
                // encoded in the Clang superclass USR; use it for modules outside the curated Apple set.
                //
                // This must NOT fire for an @objc-exported *Swift* class (`@objc(<ObjCName>) open
                // class <SwiftName>`): although it too has a Clang USR, the dependency binds it via
                // the *Swift* pipeline under its Swift name, so the Swift superclass name on the
                // mapping path below is the correct reference. The USR form is the discriminator —
                // a pure ObjC class is the bare `c:objc(cs)<Name>`, whereas an @objc Swift class
                // carries a Swift-module origin marker `c:@M@<module>@objc(cs)<Name>`.
                // Apple superclasses keep their ObjC name in Swift (or are handled by the remap
                // table), so they stay on the mapping path below and are unaffected.
                if (!AppleFrameworkRegistry.IsKnownModule(module)
                    && IsPureObjCClassUsr(classDecl.SuperclassUsr))
                {
                    var objcName = ExtractObjCClassName(classDecl.SuperclassUsr);
                    if (!string.IsNullOrEmpty(objcName))
                        return $"{MapSwiftModuleToNetNamespace(module)}.{objcName}";
                }
            }

            return MapQualifiedTypeToNet(classDecl.DirectSuperclassName);
        }

        /// <summary>
        /// Determines whether a Clang class USR denotes a <em>pure</em> Objective-C class — one
        /// declared in Objective-C headers — as opposed to an <c>@objc</c>-exported Swift class.
        /// A pure ObjC class has the bare Clang form <c>c:objc(cs)&lt;Name&gt;</c>; an <c>@objc</c>
        /// Swift class carries a Swift-module origin marker (<c>c:@M@&lt;module&gt;@objc(cs)&lt;Name&gt;</c>),
        /// so the absence of that marker (i.e. the USR starting with <c>c:objc(cs)</c>) is the
        /// discriminator. Only the pure ObjC case is bound by a dependency's ObjC ApiDefinition
        /// pipeline under its ObjC name.
        /// </summary>
        private static bool IsPureObjCClassUsr(string? usr)
            => usr != null && usr.StartsWith("c:objc(cs)", System.StringComparison.Ordinal);

        /// <summary>
        /// Extracts the Objective-C class name from a Clang class USR of the form
        /// <c>c:objc(cs)&lt;ClassName&gt;</c> (e.g. <c>c:objc(cs)MyClass</c> → <c>MyClass</c>).
        /// Returns <c>null</c> when the USR is null or is not a Clang ObjC class USR.
        /// </summary>
        private static string? ExtractObjCClassName(string? usr)
        {
            const string marker = "objc(cs)";
            if (usr == null)
                return null;
            var idx = usr.IndexOf(marker, System.StringComparison.Ordinal);
            if (idx < 0)
                return null;
            var name = usr.Substring(idx + marker.Length);
            return name.Length > 0 ? name : null;
        }

        /// <summary>
        /// Checks if a P/Invoke type string represents bool, which requires
        /// [MarshalAs(UnmanagedType.U1)] for LibraryImport compatibility.
        /// Used for both parameter and return type marshalling.
        /// </summary>
        public static bool IsBoolType(string type) => type == "bool";

        /// <summary>
        /// Checks if a Swift type spec represents Bool, which requires conversion in callbacks.
        /// </summary>
        public static bool IsBoolType(TypeSpec typeSpec)
        {
            return typeSpec is NamedTypeSpec namedType && namedType.Name == "Swift.Bool";
        }

        /// <summary>
        /// Checks if a type name represents a Swift primitive type.
        /// </summary>
        public static bool IsSwiftPrimitive(string typeName)
        {
            return typeName switch
            {
                "Swift.Int" or "Swift.Int8" or "Swift.Int16" or "Swift.Int32" or "Swift.Int64" => true,
                "Swift.UInt" or "Swift.UInt8" or "Swift.UInt16" or "Swift.UInt32" or "Swift.UInt64" => true,
                "Swift.Float" or "Swift.Double" => true,
                "Swift.Bool" => true,
                "CoreFoundation.CGFloat" => true,
                "CoreFoundation.CGSize" or "CoreFoundation.CGPoint" or "CoreFoundation.CGRect" => true,
                _ => false,
            };
        }

        /// <summary>
        /// Maps a Swift primitive type name to its C# type name (e.g. <c>Swift.Int32</c> → <c>int</c>,
        /// <c>CoreFoundation.CGFloat</c> → <c>NFloat</c>). Single source of truth for the closure-bridge
        /// callback-parameter mappers; non-primitive names fall back to <c>nint</c> (pointer ABI).
        /// </summary>
        public static string MapSwiftPrimitiveToCSharpType(string swiftName)
        {
            return swiftName switch
            {
                "Swift.Bool" => "bool",
                "Swift.Int" => "nint",
                "Swift.UInt" => "nuint",
                "Swift.Int8" => "sbyte",
                "Swift.UInt8" => "byte",
                "Swift.Int16" => "short",
                "Swift.UInt16" => "ushort",
                "Swift.Int32" => "int",
                "Swift.UInt32" => "uint",
                "Swift.Int64" => "long",
                "Swift.UInt64" => "ulong",
                "Swift.Float" => "float",
                "Swift.Double" => "double",
                "CoreFoundation.CGFloat" => "NFloat",
                _ => "nint"
            };
        }

        /// <summary>
        /// Swift type aliases that resolve to primitives.
        /// </summary>
        public static readonly Dictionary<string, string> TypeAliasToCSPrimitive = new(StringComparer.Ordinal)
        {
            { "Foundation.TimeInterval", "double" },
            // Apple's C-bridge modules (Darwin, AVFAudio, …) expose typealiases for stdlib
            // primitives that the swiftinterface scanner doesn't materialize as type
            // records. Without these entries the closure-parameter gate at
            // ClosureHandler.IsSupportedClosureParameterType rejects any signature that
            // names them — eg RealityFoundation's AudioGenerator PlayAudio render
            // handler `(UnsafeMutablePointer<AudioBufferList>) -> OSStatus`.
            // Values are C# keywords (not CTS short names) because
            // TypeProjectionFactory.TryGetPureProjection emits them verbatim
            // via `new BlittableProjection(aliasCsName)` — the result becomes
            // the literal C# identifier in generated signatures, so "int32"
            // would not compile while "int" does. Keep this dict's value
            // contract identical to the "double" entry above. PrimitiveAliasStrategy
            // mirrors the same keys when mapping to the underlying Swift type.
            { "Darwin.OSStatus", "int" },
            // AVFAudio count aliases. Note the spelling: the AVFoundation
            // overlay re-exports these under both `AVFAudio.` (the ABI-mangled
            // module) and `AVFoundation.`; the gate sees the AVFAudio form.
            { "AVFAudio.AVAudioFrameCount", "uint" },
            { "AVFAudio.AVAudioChannelCount", "uint" },
            { "AVFAudio.AVAudioPacketCount", "uint" },
            { "AVFAudio.AVAudioFramePosition", "long" },
        };

        /// <summary>
        /// Checks if a C# type name belongs to a CoreFoundation framework namespace.
        /// CoreFoundation types inherit from INativeObject (not NSObject), requiring
        /// GetINativeObject instead of GetNSObject for bridging.
        /// </summary>
        public static bool IsCoreFoundationType(string typeName)
        {
            return typeName.StartsWith("CoreText.") ||
                   typeName.StartsWith("CoreGraphics.") ||
                   typeName.StartsWith("CoreImage.") ||
                   typeName.StartsWith("CoreAnimation.") ||
                   typeName.StartsWith("CoreMedia.") ||
                   typeName.StartsWith("CoreVideo.") ||
                   typeName.StartsWith("Security.") ||
                   typeName.StartsWith("CoreFoundation.");
        }

        /// <summary>
        /// Formats the correct ObjC bridge call for a given type, dispatching between
        /// GetNSObject (for NSObject subclasses) and GetINativeObject (for CoreFoundation types).
        /// </summary>
        /// <param name="publicType">The C# public type name (e.g., "UIKit.UIImage", "CoreText.CTFont").</param>
        /// <param name="resultExpr">The expression holding the IntPtr result.</param>
        /// <param name="nonNull">If true, appends ! for non-null assertion.</param>
        public static string FormatObjCBridgeCall(string publicType, string resultExpr, bool nonNull = false, bool ownsReference = false)
        {
            var suffix = nonNull ? "!" : "";
            // GetINativeObject<T>(ptr, owns) handles both CF and NSObject types uniformly.
            // owns=true: wrapper takes ownership of +1 reference without adding another retain.
            // owns=false: wrapper adds DangerousRetain (caller must balance with DangerousRelease).
            if (IsCoreFoundationType(publicType) || ownsReference)
                return $"ObjCRuntime.Runtime.GetINativeObject<{publicType}>({resultExpr}, {(ownsReference ? "true" : "false")}){suffix}";
            return $"ObjCRuntime.Runtime.GetNSObject<{publicType}>({resultExpr}){suffix}";
        }
    }
}
