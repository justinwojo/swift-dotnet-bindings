// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;

namespace BindingsGeneration
{
    /// <summary>
    /// Represents a parameter with a type-safe marshalled type encoding.
    /// </summary>
    /// <param name="Type">The marshalled type of the parameter.</param>
    /// <param name="Name">The parameter name.</param>
    /// <param name="modifier">Optional modifier (e.g., "out", "ref").</param>
    public record Parameter(MarshalledType Type, string Name, string modifier = "", string? DefaultValue = null)
    {
        public string CallString()
        {
            var typeStr = Type switch
            {
                MarshalledType.Existential(var containerType, var publicType) => publicType,
                MarshalledType.CdeclExistential(_, var publicType) => publicType,
                MarshalledType.SimpleEnum(var underlyingType, var enumTypeName) => enumTypeName,
                MarshalledType.ObjCBridged(var csTypeName) => csTypeName,
                MarshalledType.CdeclFrozenStruct(var cdeclFsType) => cdeclFsType,
                MarshalledType.CdeclClosureFuncPtr => "IntPtr",
                MarshalledType.CdeclClosureContext => "IntPtr",
                MarshalledType.AsyncThrowingContext => "IntPtr",
                MarshalledType.AsyncThrowingStartFunc => "delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>",
                MarshalledType.NativeRemappedFrozen(var swiftWrapperType) => swiftWrapperType,
                MarshalledType.FrozenBuffer(var typeName) => typeName + ".Buffer",
                MarshalledType.ConventionCFuncPtr(var funcPtrType) => funcPtrType,
                MarshalledType.SwiftSelfTyped(var innerType) => $"SwiftSelf<{innerType}>",
                MarshalledType.AsyncCallbackType => "void*",
                MarshalledType.AsyncErrorCallbackType => "void*",
                MarshalledType.AsyncContextType => "void*",
                MarshalledType.AsyncTaskType => "IntPtr",
                MarshalledType.NonFrozenIntPtrType => "IntPtr",
                MarshalledType.EnumSafeHandleType => "IntPtr",
                MarshalledType.NativeRemappedNonFrozenType => "SafeHandle",
                MarshalledType.NonFrozenSafeHandleType => "SafeHandle",
                MarshalledType.SwiftClosureLegacyType => "SwiftClosureData",
                MarshalledType.BoolType => "bool",
                MarshalledType.SwiftSelfUntypedType => "SwiftSelf",
                MarshalledType.Simple(var csharpType) => csharpType,
                _ => "unknown"
            };
            return $"{typeStr} {Name}";
        }

        /// <summary>
        /// Returns the parameter string for P/Invoke declarations.
        /// Existential types use the container type (not the public interface type).
        /// </summary>
        public string PInvokeSignatureString() => Type switch
        {
            // Existential types: use container type in P/Invoke declaration
            MarshalledType.Existential(var containerType, _) => $"{modifier} {containerType} {Name}",
            // @_cdecl existential: pass container by ref (matches UnsafeRawPointer in Swift)
            MarshalledType.CdeclExistential(var containerType, _) => $"ref {containerType} {Name}",
            // @_cdecl frozen struct: pass as IntPtr (pointer to marshalled buffer)
            MarshalledType.CdeclFrozenStruct => $"IntPtr {Name}",
            // @_cdecl tuple: pass as IntPtr (pointer to buffer with elements at ABI offsets)
            MarshalledType.CdeclTuple => $"IntPtr {Name}",
            // Bool requires explicit [MarshalAs] with LibraryImport + DisableRuntimeMarshalling
            MarshalledType.BoolType => $"[MarshalAs(UnmanagedType.U1)] {modifier} bool {Name}",
            // All other types delegate to SignatureString
            _ => SignatureString()
        };

        public string SignatureString() => Type switch
        {
            MarshalledType.AsyncCallbackType => $"{modifier} void* {Name}",
            MarshalledType.AsyncErrorCallbackType => $"{modifier} void* {Name}",
            MarshalledType.AsyncContextType => $"{modifier} void* {Name}",
            MarshalledType.AsyncTaskType => $"{modifier} IntPtr {Name}",
            MarshalledType.NonFrozenIntPtrType => $"{modifier} IntPtr {Name}",
            // ObjC bridged types use IntPtr in P/Invoke
            MarshalledType.ObjCBridged => $"{modifier} IntPtr {Name}",
            // Enum values use IntPtr in Swift calling-convention P/Invoke (SafeHandle is non-blittable there).
            MarshalledType.EnumSafeHandleType => $"{modifier} IntPtr {Name}",
            // Simple enums (C# value types) use their underlying integer type in P/Invoke.
            MarshalledType.SimpleEnum(var underlyingType, _) => $"{modifier} {underlyingType} {Name}",
            // Existential protocol types: show public interface type in signature.
            MarshalledType.Existential(_, var publicType) => $"{modifier} {publicType} {Name}",
            // @_cdecl existential: same public type in wrapper signature
            MarshalledType.CdeclExistential(_, var publicType) => $"{modifier} {publicType} {Name}",
            // @_cdecl frozen struct: show public C# type in wrapper signature (not IntPtr)
            MarshalledType.CdeclFrozenStruct(var cdeclFsCsType) => $"{modifier} {cdeclFsCsType} {Name}",
            // Native-remapped types: URL uses SafeHandle, Data uses the actual Swift type
            MarshalledType.NativeRemappedNonFrozenType => $"{modifier} SafeHandle {Name}",
            MarshalledType.NativeRemappedFrozen(var swiftWrapperType) => $"{modifier} {swiftWrapperType} {Name}",
            // Async+throwing closure context pointer
            MarshalledType.AsyncThrowingContext => $"{modifier} IntPtr {Name}",
            // Async+throwing closure start function pointer
            MarshalledType.AsyncThrowingStartFunc =>
                $"{modifier} delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void> {Name}",
            // Cdecl closure wrapper: func pointer and context as separate IntPtr params
            MarshalledType.CdeclClosureFuncPtr => $"{modifier} IntPtr {Name}",
            MarshalledType.CdeclClosureContext => $"{modifier} IntPtr {Name}",
            // Frozen struct buffer type
            MarshalledType.FrozenBuffer(var typeName) => $"{modifier} {typeName}.Buffer {Name}",
            // @convention(c) function pointer — emit the full delegate* type
            MarshalledType.ConventionCFuncPtr(var funcPtrType) => $"{modifier} {funcPtrType} {Name}",
            // Typed SwiftSelf with generic parameter
            MarshalledType.SwiftSelfTyped(var innerType) => $"{modifier} SwiftSelf<{innerType}> {Name}",
            // Untyped SwiftSelf
            MarshalledType.SwiftSelfUntypedType => $"{modifier} SwiftSelf {Name}",
            // Non-frozen SafeHandle
            MarshalledType.NonFrozenSafeHandleType => $"{modifier} SafeHandle {Name}",
            // Legacy SwiftClosureData
            MarshalledType.SwiftClosureLegacyType => $"{modifier} SwiftClosureData {Name}",
            // Bool
            MarshalledType.BoolType => $"{modifier} bool {Name}",
            // Catch-all: use the C# type name directly
            MarshalledType.Simple(var csharpType) => $"{modifier} {csharpType} {Name}",
            _ => $"{modifier} unknown {Name}"
        };
    }

    /// <summary>
    /// Represents a signature.
    /// </summary>
    /// <param name="ReturnType"></param>
    /// <param name="Parameters"></param>
    public record Signature(string ReturnType, IReadOnlyList<Parameter> Parameters)
    {
        public bool ContainsPlaceholder =>
        Parameters.Any(p => p.Type.ContainsAnyTypePlaceholder())
        || ReturnType.Contains(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
        public string ParametersString() => string.Join(", ", Parameters.Select(p =>
            p.DefaultValue != null ? $"{p.SignatureString()} = {p.DefaultValue}" : p.SignatureString()));

        /// <summary>
        /// Returns the parameters string without any default values.
        /// Used by failable factory (TryCreate) where trailing 'out' param makes defaults invalid.
        /// </summary>
        public string ParametersStringWithoutDefaults() => string.Join(", ", Parameters.Select(p => p.SignatureString()));

        /// <summary>
        /// Returns the parameters string with optional per-parameter attribute prefixes.
        /// Used to emit [OriginalSwiftType] attributes on AnyType-fallback parameters.
        /// </summary>
        /// <param name="paramAttributes">Map of parameter name to attribute string, or null for default behavior.</param>
        public string ParametersString(IReadOnlyDictionary<string, string>? paramAttributes)
        {
            if (paramAttributes == null || paramAttributes.Count == 0) return ParametersString();
            return string.Join(", ", Parameters.Select(p =>
            {
                var prefix = paramAttributes.TryGetValue(p.Name, out var attr) ? attr + " " : "";
                var defaultSuffix = p.DefaultValue != null ? $" = {p.DefaultValue}" : "";
                return prefix + p.SignatureString() + defaultSuffix;
            }));
        }

        /// <summary>
        /// Returns the parameters string for P/Invoke declarations, where existential types
        /// use container types instead of public interface types.
        /// </summary>
        public string PInvokeParametersString() => string.Join(", ", Parameters.Select(p => p.PInvokeSignatureString()));

        public string CallArgumentsString() => string.Join(", ", Parameters.Select(p => GetCallArgumentString(p)));

        public static string GetCallArgumentString(Parameter parameter)
        {
            return parameter switch
            {
                { Type: MarshalledType.NonFrozenSafeHandleType } => $"{parameter.Name}.Payload",
                { Type: MarshalledType.EnumSafeHandleType } => $"{parameter.Name}.Payload.DangerousGetHandle()",
                // Simple enums: cast to underlying integer type for P/Invoke
                { Type: MarshalledType.SimpleEnum(var underlyingType, _) } => $"({underlyingType}){parameter.Name}",
                // Existential protocol types: GetOrCreate handles both proxy and concrete types.
                // Only for single-protocol (EC1) interfaces — compositions (EC2+), well-known
                // value types (AnyError/EC0), and "object" use direct ISwiftExistentialConvertible.
                { Type: MarshalledType.Existential(var containerType, var publicType) } when containerType == "Swift.Runtime.ExistentialContainer1" && publicType.StartsWith("I") && publicType.Length > 1 && char.IsUpper(publicType[1]) =>
                    $"Swift.Runtime.ExistentialContainerFactory.GetOrCreate<{publicType}>({parameter.Name})",
                { Type: MarshalledType.Existential(var containerType, var publicType) } =>
                    $"((Swift.Runtime.ISwiftExistentialConvertible<{containerType}>){parameter.Name}).GetExistentialContainer()",
                // @_cdecl existential: ref to pre-extracted container local variable
                { Type: MarshalledType.CdeclExistential(var containerType, _) } =>
                    $"ref {parameter.Name}Container",
                // @_cdecl frozen struct: use the marshalled pointer local variable
                { Type: MarshalledType.CdeclFrozenStruct } => $"{parameter.Name}Ptr",
                // @_cdecl tuple: use the marshalled buffer pointer
                { Type: MarshalledType.CdeclTuple } => $"{parameter.Name}Ptr",
                { Type: MarshalledType.NonFrozenIntPtrType } => $"{parameter.Name}Handle",
                // Handle .Buffer params: ref modifier uses BufferRef (ref-returning property) for in-place mutation
                { Type: MarshalledType.FrozenBuffer, modifier: "ref" } => $"ref {parameter.Name}Disposable.BufferRef",
                { Type: MarshalledType.FrozenBuffer } => $"{parameter.Name}Disposable.Buffer",
                { Type: MarshalledType.AsyncCallbackType } => $"{parameter.Name}",
                { Type: MarshalledType.AsyncErrorCallbackType } => $"{parameter.Name}",
                { Type: MarshalledType.AsyncContextType } => "null",
                { Type: MarshalledType.AsyncTaskType } => $"GCHandle.ToIntPtr({parameter.Name})",
                { modifier: "out" } => $"out var {parameter.Name}",
                { modifier: "ref" } => $"ref {parameter.Name}",
                // Handle escaping closures: parameter is SwiftClosureData, variable is {name}Closure
                { Type: MarshalledType.SwiftClosureLegacyType } => $"{parameter.Name}Closure",
                // Cdecl closure: func pointer — uses Handle.IsAllocated guard for optional nil safety.
                { Type: MarshalledType.CdeclClosureFuncPtr(var callbackName, var sourceCsName) } =>
                    $"{sourceCsName}Handle.IsAllocated ? (IntPtr)s_{callbackName} : IntPtr.Zero",
                // Cdecl closure: context — same Handle.IsAllocated guard for consistency.
                { Type: MarshalledType.CdeclClosureContext(var sourceCsName) } =>
                    $"{sourceCsName}Handle.IsAllocated ? GCHandle.ToIntPtr({sourceCsName}Handle) : IntPtr.Zero",
                // Handle async+throwing closure context
                { Type: MarshalledType.AsyncThrowingContext(var paramName) } =>
                    $"{paramName}ContextPtr",
                // Handle async+throwing closure start function
                { Type: MarshalledType.AsyncThrowingStartFunc(var callbackName) } =>
                    $"s_{callbackName}_Start",
                // Handle @convention(c) closure function pointers
                { Type: MarshalledType.ConventionCFuncPtr } =>
                    parameter.Name.EndsWith("FuncPtr") ? parameter.Name : $"{parameter.Name}FuncPtr",
                // ObjC bridged types: extract Handle from the .NET iOS binding object
                { Type: MarshalledType.ObjCBridged } => $"{parameter.Name}Handle",
                // Native-remapped types (URL, Data): use the converted Swift variable
                { Type: MarshalledType.NativeRemappedNonFrozenType } => $"{parameter.Name}Swift.Payload",
                { Type: MarshalledType.NativeRemappedFrozen } => $"{parameter.Name}Swift",
                // ObjC-rooted class self: Handle IS the object pointer (no buffer dereference)
                { Name: "_selfClassObjC" } => "Handle",
                // Instance methods on free-function wrapper paths pass self as explicit IntPtr.
                // SwiftClassHandle: DangerousGetHandle() IS the Swift object pointer (no dereference).
                { Name: "_selfClass" } => "_handle.DangerousGetHandle()",
                { Name: "_selfFixed" } => "(IntPtr)__self",
                { Name: "_self", Type: MarshalledType.Simple("IntPtr") } => "_payload.DangerousGetHandle()",
                // Generic type metadata: local is TypeMetadata struct, P/Invoke expects IntPtr.
                // Use .Handle to extract the IntPtr from the TypeMetadata local.
                { Type: MarshalledType.Simple("IntPtr") } when parameter.Name.EndsWith("Metadata")
                    => $"{parameter.Name}.Handle",
                // Implicit trailing metadata for generic @_silgen_name wrappers:
                // reuse the same TypeMetadata variable's Handle as the explicit metadata.
                { Type: MarshalledType.Simple("IntPtr") } when parameter.Name.EndsWith("Implicit")
                    => $"{parameter.Name.Replace("Implicit", "")}.Handle",
                // Protocol witness table: local is ProtocolWitnessTable struct, P/Invoke expects IntPtr.
                // Use .Handle to extract the IntPtr. Same pattern as TypeMetadata fix.
                { Type: MarshalledType.Simple("IntPtr") } when parameter.Name.EndsWith("PWT")
                    => $"{parameter.Name}.Handle",
                _ => parameter.Name
            };
        }
    }

    /// <summary>
    /// Base class for signature builders that provides common fields and methods.
    /// </summary>
    public abstract class SignatureBuilderBase
    {
        /// <summary>The return type of the method.</summary>
        protected string _returnType = "invalid";

        /// <summary>The list of parameters.</summary>
        protected readonly List<Parameter> _parameters = new();

        /// <summary>The method environment.</summary>
        protected readonly MethodEnvironment _env;

        /// <summary>Merged generic context for resolving type-level + method-level generic parameters.</summary>
        protected readonly GenericContext _genericContext;

        /// <summary>
        /// Initializes a new instance of the <see cref="SignatureBuilderBase"/> class.
        /// </summary>
        /// <param name="env">The method environment.</param>
        protected SignatureBuilderBase(MethodEnvironment env)
        {
            _env = env;
            _genericContext = env.ParentDecl is TypeDecl parentType
                ? GenericContext.FromMethodInType(env.MethodDecl, parentType)
                : GenericContext.FromMethod(env.MethodDecl);
        }

        /// <summary>
        /// Builds the signature, deduplicating any parameter name collisions.
        /// </summary>
        /// <returns>The signature.</returns>
        public Signature Build()
        {
            DeduplicateParameterNames();
            return new Signature(_returnType, _parameters.ToArray());
        }

        /// <summary>
        /// Deduplicates parameter names by appending _N suffix when collisions exist.
        /// This handles cases like SwiftIndirectResult 'result' colliding with a method parameter 'result'.
        /// </summary>
        private void DeduplicateParameterNames() => DeduplicateParameterNames(_parameters);

        /// <summary>
        /// Deduplicates parameter names by appending _N suffix when collisions exist.
        /// Avoids generating names that collide with existing parameters.
        /// </summary>
        internal static void DeduplicateParameterNames(List<Parameter> parameters)
        {
            // Collect all names that appear more than once
            var nameCount = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var param in parameters)
            {
                nameCount.TryGetValue(param.Name, out var count);
                nameCount[param.Name] = count + 1;
            }

            var duplicateNames = nameCount.Where(kvp => kvp.Value > 1).Select(kvp => kvp.Key).ToHashSet();
            if (duplicateNames.Count == 0)
                return;

            // Build set of ALL names (including non-duplicates) to avoid collisions
            // e.g. params [value, value_1, value] must not produce [value, value_1, value_1]
            var allNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var param in parameters)
                allNames.Add(param.Name);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < parameters.Count; i++)
            {
                var param = parameters[i];
                if (!duplicateNames.Contains(param.Name))
                {
                    seen.Add(param.Name);
                    continue;
                }

                if (seen.Add(param.Name))
                    continue; // First occurrence keeps its name

                // Find a unique suffix that doesn't collide with any existing name
                var suffix = 1;
                var candidate = $"{param.Name}_{suffix}";
                while (allNames.Contains(candidate))
                {
                    suffix++;
                    candidate = $"{param.Name}_{suffix}";
                }

                parameters[i] = param with { Name = candidate };
                allNames.Add(candidate);
            }
        }

        /// <summary>
        /// Sets the return type of the method.
        /// </summary>
        /// <param name="returnType">The return type.</param>
        protected void SetReturnType(string returnType)
        {
            _returnType = returnType;
        }

        /// <summary>
        /// Adds a parameter to the signature with a MarshalledType.
        /// </summary>
        /// <param name="type">The marshalled type of the parameter.</param>
        /// <param name="name">The parameter name.</param>
        /// <param name="modifier">Optional parameter modifier (e.g., "out").</param>
        protected void AddParameter(MarshalledType type, string name, string modifier = "")
        {
            _parameters.Add(new Parameter(type, name, modifier));
        }

        /// <summary>
        /// Adds a parameter to the signature using a string type name (wraps in MarshalledType.Simple).
        /// Intercepts "bool" to use MarshalledType.Bool for correct [MarshalAs] handling.
        /// </summary>
        /// <param name="type">The C# type name.</param>
        /// <param name="name">The parameter name.</param>
        /// <param name="modifier">Optional parameter modifier (e.g., "out").</param>
        protected void AddParameter(string type, string name, string modifier = "")
        {
            var marshalledType = MarshallingHelpers.IsBoolType(type) ? MarshalledType.Bool : new MarshalledType.Simple(type);
            _parameters.Add(new Parameter(marshalledType, name, modifier));
        }
    }

    /// <summary>
    /// Builds the wrapper method signature (C# public API).
    /// Uses TypeProjectionFactory for type projection, with legacy fallbacks
    /// for generic type parameters, bound generics, and protocol-kind types.
    /// </summary>
    public class WrapperSignatureBuilder : SignatureBuilderBase
    {
        private readonly TypeProjectionFactory _factory = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="WrapperSignatureBuilder"/> class.
        /// </summary>
        /// <param name="env">The method environment.</param>
        public WrapperSignatureBuilder(MethodEnvironment env) : base(env)
        {
        }

        /// <summary>
        /// Handles the return type of the method.
        /// </summary>
        public void HandleReturnType()
        {
            var argument = _env.MethodDecl.CSSignature.First();

            // @_cdecl property wrapper: String returns via Utf8Slice (C struct), not SwiftString.
            // The Swift @_cdecl wrapper returns SBW_Utf8Slice; C# receives it as Utf8Slice.
            // No MarshalFromSwift needed — PropertyHandler's getter body handles the conversion.
            if (_env.MethodDecl.UsesCdeclPropertyWrapper &&
                argument.SwiftTypeSpec is NamedTypeSpec cdeclStrNts && cdeclStrNts.Name == "Swift.String")
            {
                SetReturnType("Utf8Slice");
                return;
            }

            // Decomposed Optional getter: return the inner type as nullable (T?) directly.
            // The Swift wrapper passes (rawPayload, hasValue) separately; C# reads the inner value
            // and constructs T? without going through SwiftOptional<T> / VWT operations.
            if (_env.MethodDecl.UsesCdeclPropertyWrapper &&
                !_env.MethodDecl.IsSubscriptAccessor &&
                OptionalMarshalClassifier.IsDecomposed(argument.SwiftTypeSpec, _env.TypeDatabase))
            {
                var projection = _factory.Project(argument.SwiftTypeSpec, new ProjectionContext
                {
                    TypeDatabase = _env.TypeDatabase,
                    IsParameter = false,
                    GenericContext = _genericContext,
                    ParentTypeDecl = _env.ParentDecl as TypeDecl,
                    CurrentModuleName = _env.ExistentialHandler.CurrentModuleName
                });
                if (projection is OptionalProjection optProj)
                {
                    // Use the inner type's MarshalFromSwift type as nullable
                    SetReturnType($"{optProj.InnerProjection.MarshalFromSwiftType}?");
                    return;
                }
            }

            // Try factory-based projection for non-tuple types.
            // Tuples use legacy handling to preserve element labels and match marshalling
            // (factory TupleProjection does deep conversion; marshalling hasn't been updated yet).
            // Property accessors skip the result for convertible/native-remapped types
            // (String→string, Array→IReadOnlyList, etc.) to maintain raw-type consistency.
            if (argument.SwiftTypeSpec is not TupleTypeSpec)
            {
                var projection = _factory.Project(argument.SwiftTypeSpec, new ProjectionContext
                {
                    TypeDatabase = _env.TypeDatabase,
                    IsParameter = false,
                    GenericContext = _genericContext,
                    ParentTypeDecl = _env.ParentDecl as TypeDecl,
                    CurrentModuleName = _env.ExistentialHandler.CurrentModuleName
                });
                if (projection != null && !ShouldSkipProjectionForAccessor(argument.SwiftTypeSpec))
                {
                    // Guard: unsupported closure returns must use AnyType to match P/Invoke.
                    // The factory can project closures with existential args (any P → object)
                    // but the P/Invoke falls back to AnyType when IsSupportedClosure is false.
                    // Check both direct ClosureTypeSpec and Optional<Closure> (P/Invoke uses
                    // IsClosure() which unwraps Optional, so we must match that behavior).
                    if (_env.ClosureHandler.IsClosure(argument) &&
                        !_env.ClosureHandler.IsSupportedClosure(_env.ClosureHandler.GetClosureTypeSpec(argument)!))
                    {
                        // Fall through to legacy path which produces AnyType
                    }
                    else
                    {
                        SetReturnType(projection.PublicType);
                        return;
                    }
                }
            }

            // Fallback: tuple return types (preserves element labels and shallow conversion)
            if (_env.TupleHandler.IsTuple(argument.SwiftTypeSpec))
            {
                var tupleTypeSpec = (TupleTypeSpec)argument.SwiftTypeSpec;
                bool hasGenericElements = _env.TupleHandler.HasGenericTypeParameterElements(tupleTypeSpec);
                if (_env.TupleHandler.IsSupportedTuple(tupleTypeSpec) ||
                    (_env.TupleHandler.IsSupportedTuple(tupleTypeSpec, _genericContext) && !(hasGenericElements && _env.MethodDecl.IsAsync)))
                    SetReturnType(_env.TupleHandler.GetCSharpTupleType(tupleTypeSpec, typeSpec =>
                    {
                        // Try factory projection first for bound generics (Optional, Array, Dictionary)
                        var projection = _factory.Project(typeSpec, new ProjectionContext
                        {
                            TypeDatabase = _env.TypeDatabase,
                            IsParameter = false,
                            GenericContext = _genericContext,
                            ParentTypeDecl = _env.ParentDecl as TypeDecl,
                            CurrentModuleName = _env.ExistentialHandler.CurrentModuleName
                        });
                        if (projection != null)
                            return projection.PublicType;

                        if (MarshallingHelpers.IsSwiftString(typeSpec))
                            return "string";
                        return TranslateTypeSpecForConversion(typeSpec);
                    }));
                else
                    SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                return;
            }

            // Optional<ObjC> accessor: ObjC optionals use nullable pointer ABI (IntPtr where nil = 0).
            // The accessor helper returns raw IntPtr, PropertyHandler applies GetNSObject conversion.
            if (_env.MethodDecl.IsAccessor && MarshallingHelpers.IsOptionalObjCBridged(argument.SwiftTypeSpec, _env.TypeDatabase))
            {
                SetReturnType("IntPtr");
                return;
            }

            // Fallback: bound generic return types that the factory couldn't project.
            // Covers two cases:
            //   1. Accessors: factory skipped by ShouldSkipProjectionForAccessor to preserve raw ABI types
            //   2. Non-accessors: factory returns null when inner type is unsupported (e.g., frozen-with-memory
            //      structs like AsyncResult cause Optional<AsyncResult> to fail projection)
            // TranslateBoundGenericTypeToCSharp produces the correct raw name with generic args
            // (e.g., SwiftOptional<SwiftArray<int>>). These types must stay raw because the marshalling
            // body (WrapperEmitter) generates .Payload access that requires SwiftOptional<T>, not T?.
            if (_env.BoundGenericsHandler.IsBoundGeneric(argument))
            {
                SetReturnType(_env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(argument, _genericContext));
                return;
            }

            // Fallback: generic type parameters (T, U)
            if (argument.IsGeneric)
            {
                var csTypeParamName = _env.GenericTypeMapping[argument.SwiftTypeSpec.ToString()].TypeParameter;
                SetReturnType(csTypeParamName);
                return;
            }

            // Fallback: type record (Protocol kind → AnyType, frozen-with-memory-management, etc.)
            var typeRecord = _env.TypeDatabase.GetTypeRecordOrAnyType(argument.SwiftTypeSpec);
            if (typeRecord.Kind == TypeRecordKind.Protocol)
            {
                SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                return;
            }

            // Guard: if resolved name is bare generic (e.g., "SwiftOptional" from
            // Optional<UnsupportedClosure>), use AnyType to prevent CS0305.
            var resolvedReturnName = typeRecord.CSharpTypeName.FullyQualifiedName;
            if (TypeDatabaseExtensions.IsBareGenericTypeName(resolvedReturnName))
            {
                SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                return;
            }
            SetReturnType(resolvedReturnName);
        }

        /// <summary>
        /// Handles the arguments of the method.
        /// Uses GetCSharpParameterName() for public API parameter names.
        /// </summary>
        public void HandleArguments()
        {
            // Pre-compute deduplicated parameter names so all consumers see consistent names
            NameProvider.DeduplicateParameterNames(_env.MethodDecl.CSSignature);

            foreach (var argument in _env.MethodDecl.CSSignature.Skip(1))
            {
                // Strip Swift compiler-injected debug params (#file, #line, #column, #function)
                if (DefaultParameterOverloadEmitter.IsDebugParameter(argument))
                    continue;

                // Skip empty tuple () parameters — Swift's Void type is zero-sized and carries no value.
                // Common in ExpressibleByNilLiteral conformances: init(nilLiteral: ()).
                if (argument.SwiftTypeSpec.IsEmptyTuple)
                    continue;

                var csParamName = NameProvider.GetCSharpParameterName(argument);

                // @_cdecl property wrapper: String params use UTF-8 pointer + length.
                // nint matches Swift's Int (64-bit on ARM64) to avoid truncation.
                if (_env.MethodDecl.UsesCdeclPropertyWrapper &&
                    argument.SwiftTypeSpec is NamedTypeSpec cdeclStrArgNts && cdeclStrArgNts.Name == "Swift.String")
                {
                    AddParameter("IntPtr", csParamName + "Utf8Ptr");
                    AddParameter("nint", csParamName + "Utf8Len");
                    continue;
                }

                // Decomposed Optional setter: parameter is (IntPtr payload, bool hasValue).
                // The accessor method signature shows the inner type as nullable (T?),
                // matching the property type. P/Invoke decomposes to raw pointer + flag.
                if (_env.MethodDecl.UsesCdeclPropertyWrapper &&
                    !_env.MethodDecl.IsSubscriptAccessor &&
                    OptionalMarshalClassifier.IsDecomposed(argument.SwiftTypeSpec, _env.TypeDatabase))
                {
                    var projection = _factory.Project(argument.SwiftTypeSpec, new ProjectionContext
                    {
                        TypeDatabase = _env.TypeDatabase,
                        IsParameter = true,
                        GenericContext = _genericContext,
                        ParentTypeDecl = _env.ParentDecl as TypeDecl,
                        CurrentModuleName = _env.ExistentialHandler.CurrentModuleName
                    });
                    if (projection is OptionalProjection optProj)
                    {
                        AddParameter("IntPtr", "payload");
                        AddParameter("bool", "hasValue");
                        continue;
                    }
                }

                // Try factory-based projection for non-tuple types (same guards as HandleReturnType)
                if (argument.SwiftTypeSpec is not TupleTypeSpec)
                {
                    var projection = _factory.Project(argument.SwiftTypeSpec, new ProjectionContext
                    {
                        TypeDatabase = _env.TypeDatabase,
                        IsParameter = true,
                        GenericContext = _genericContext,
                        ParentTypeDecl = _env.ParentDecl as TypeDecl,
                        CurrentModuleName = _env.ExistentialHandler.CurrentModuleName
                    });
                    if (projection != null && !ShouldSkipProjectionForAccessor(argument.SwiftTypeSpec))
                    {
                        // Preserve ref modifier for inout generic type parameters (e.g., non-frozen struct T).
                        // Only generic params need ref — concrete types have marshalling handled by WrapperEmitter.
                        var projInoutModifier = (argument.IsInOut && argument.IsGeneric) ? "ref" : "";
                        AddParameter(projection.PublicType, csParamName, projInoutModifier);
                        continue;
                    }
                }

                // Fallback: tuple arguments (preserves element labels and shallow conversion)
                // NOTE: Method tuple params are NOT factory-projected because the P/Invoke expects
                // ABI types (e.g., IntPtr for Optional) and there's no per-element conversion in
                // the wrapper body. Enum case tuples ARE projected (see EnumHandler.CaseConstruction.cs).
                if (_env.TupleHandler.IsTuple(argument))
                {
                    var tupleTypeSpec = _env.TupleHandler.GetTupleTypeSpec(argument)!;
                    if (_env.TupleHandler.IsSupportedTuple(tupleTypeSpec) ||
                        _env.TupleHandler.IsSupportedTuple(tupleTypeSpec, _genericContext))
                        AddParameter(_env.TupleHandler.GetCSharpTupleType(tupleTypeSpec, _genericContext), csParamName);
                    else
                        AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, csParamName);
                    continue;
                }

                // Optional<ObjC> accessor setter: use IntPtr (nullable pointer ABI).
                // PropertyHandler passes `value.Handle` or `IntPtr.Zero`.
                if (_env.MethodDecl.IsAccessor && MarshallingHelpers.IsOptionalObjCBridged(argument.SwiftTypeSpec, _env.TypeDatabase))
                {
                    AddParameter("IntPtr", csParamName);
                    continue;
                }

                // Fallback: bound generic parameters the factory couldn't project.
                // Same rationale as HandleReturnType — raw types needed for marshalling body.
                if (_env.BoundGenericsHandler.IsBoundGeneric(argument))
                {
                    AddParameter(_env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(argument, _genericContext), csParamName);
                    continue;
                }

                // Determine ref modifier for inout parameters
                var inoutModifier = argument.IsInOut ? "ref" : "";

                // Fallback: generic type parameters (T, U)
                if (argument.IsGeneric)
                {
                    var csTypeParamName = _env.GenericTypeMapping[argument.SwiftTypeSpec.ToString()].TypeParameter;
                    AddParameter(csTypeParamName, csParamName, inoutModifier);
                    continue;
                }

                // Fallback: type record (Protocol kind → AnyType, frozen-with-memory-management, etc.)
                var typeRecord = _env.TypeDatabase.GetTypeRecordOrAnyType(argument.SwiftTypeSpec);
                if (typeRecord.Kind == TypeRecordKind.Protocol)
                {
                    AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, csParamName);
                    continue;
                }

                // Guard: if resolved name is bare generic (e.g., "SwiftOptional" from
                // Optional<UnsupportedClosure>), use AnyType to prevent CS0305.
                var resolvedParamName = typeRecord.CSharpTypeName.FullyQualifiedName;
                if (TypeDatabaseExtensions.IsBareGenericTypeName(resolvedParamName))
                {
                    AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, csParamName, inoutModifier);
                    continue;
                }
                AddParameter(resolvedParamName, csParamName, inoutModifier);
            }

            // Resolve C# default values from Swift default expressions.
            // Must run after all parameters are added so we can enforce the trailing suffix constraint.
            ResolveDefaultValues();
        }

        /// <summary>
        /// Resolves C# default values from SwiftDefaultExpression on each parameter's ArgumentDecl.
        /// Enforces the maximal trailing suffix constraint: only consecutive trailing parameters
        /// where every HasDefaultArg param has a mappable C# default keep their defaults.
        /// </summary>
        private void ResolveDefaultValues()
        {
            // Skip property accessors — they don't have user-facing parameter defaults
            if (_env.MethodDecl.IsAccessor)
                return;

            var args = _env.MethodDecl.CSSignature.Skip(1)
                .Where(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple)
                .ToList();

            if (args.Count != _parameters.Count)
                return; // Defensive — counts should match

            // Map each parameter's default
            var mappedDefaults = new string?[_parameters.Count];
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i].HasDefaultArg && args[i].SwiftDefaultExpression != null)
                {
                    mappedDefaults[i] = SwiftDefaultValueMapper.TryMapToCSharpDefault(
                        args[i].SwiftDefaultExpression!, args[i].SwiftTypeSpec, _env.TypeDatabase);
                }
            }

            // Find maximal trailing suffix: longest run from the end where every
            // HasDefaultArg param also has a mapped C# default.
            int suffixStart = _parameters.Count;
            for (int i = _parameters.Count - 1; i >= 0; i--)
            {
                if (!args[i].HasDefaultArg)
                    break; // Non-default param ends the suffix
                if (mappedDefaults[i] == null)
                    break; // Default param without C# mapping ends the suffix
                suffixStart = i;
            }

            // Apply defaults only within the trailing suffix
            for (int i = suffixStart; i < _parameters.Count; i++)
            {
                if (mappedDefaults[i] != null)
                    _parameters[i] = _parameters[i] with { DefaultValue = mappedDefaults[i] };
            }
        }

        /// <summary>
        /// Returns true when this is a property accessor and the type is one that would
        /// receive an idiomatic conversion (String→string, Array→IReadOnlyList, etc.).
        /// Accessor methods must use raw types for these to maintain consistency with
        /// property declarations where PropertyHandler does the conversion.
        ///
        /// Closures and existentials are excluded — they use the projected delegate/interface
        /// type in both accessor and non-accessor contexts (no raw/idiomatic distinction).
        /// </summary>
        private bool ShouldSkipProjectionForAccessor(TypeSpec typeSpec)
        {
            if (!_env.MethodDecl.IsAccessor)
                return false;

            // Closures (including Optional<Closure>) always use projected delegate types
            if (typeSpec is ClosureTypeSpec)
                return false;
            if (_env.ClosureHandler.IsOptionalClosure(typeSpec))
                return false;

            // Existentials (including Optional<any Protocol>) always use projected types
            if (_env.ExistentialHandler.IsExistential(typeSpec))
                return false;
            if (_env.ExistentialHandler.IsOptionalExistential(typeSpec))
                return false;

            return MarshallingHelpers.IsConvertibleType(typeSpec) ||
                   _env.TypeConversionHandler.HasNativeTypeRemapping(typeSpec);
        }

        /// <summary>
        /// Translates a TypeSpec to C# type name for use in tuple element translation.
        /// Handles existentials, generic type parameters, and type record lookup.
        /// Kept for tuple handling until marshalling is updated in step 3c.
        /// </summary>
        private string TranslateTypeSpecForConversion(TypeSpec typeSpec)
        {
            // Handle existential types (ProtocolListTypeSpec and NamedTypeSpec with IsAny)
            if (_env.ExistentialHandler.IsExistential(typeSpec))
            {
                var protocolList = _env.ExistentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null && _env.ExistentialHandler.IsSupportedExistential(protocolList))
                {
                    var publicType = _env.ExistentialHandler.GetPublicExistentialType(protocolList);
                    if (publicType == "object")
                        return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
                    return publicType;
                }
                return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
            }

            if (typeSpec is NamedTypeSpec namedTypeSpec)
            {
                if (TypeSpecHelpers.IsGenericTypeParameter(namedTypeSpec.Name) &&
                    _genericContext.TryResolve(namedTypeSpec.Name, out var csName))
                {
                    return csName;
                }

                var typeRecord = _env.TypeDatabase.GetTypeRecordOrAnyType(namedTypeSpec);
                if (typeRecord == TypeDatabaseExtensions.AnyType ||
                    typeRecord == TypeDatabaseExtensions.IntPtrType)
                {
                    return typeRecord.CSharpTypeName.FullyQualifiedName;
                }

                if (namedTypeSpec.GenericParameters.Count > 0)
                {
                    var translatedParams = namedTypeSpec.GenericParameters
                        .Select(p => TranslateTypeSpecForConversion(p))
                        .ToList();
                    return $"{typeRecord.CSharpTypeName.FullyQualifiedName}<{string.Join(", ", translatedParams)}>";
                }

                return typeRecord.CSharpTypeName.FullyQualifiedName;
            }
            return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        }
    }

    /// <summary>
    /// Provides methods for handling method signatures.
    /// </summary>
    public class SignatureHandler
    {
        private Signature? _pInvokeSignature;
        private Signature? _wrapperSignature;
        private readonly MethodEnvironment _env;

        public SignatureHandler(MethodEnvironment env)
        {
            _env = env;
        }

        /// <summary>
        /// Gets the PInvoke signature.
        /// </summary>
        /// <returns>The PInvoke signature.</returns>
        public Signature GetPInvokeSignature()
        {
            if (_pInvokeSignature == null)
            {
                var pInvokeSignature = new PInvokeSignatureBuilder(_env);
                pInvokeSignature.HandleReturnType();
                pInvokeSignature.HandleSwiftAsync();

                // Parameter ordering is determined by the CdeclSignatureContract —
                // the single source of truth for phase ordering across C# P/Invoke
                // and Swift wrapper emitters.
                var order = CdeclSignatureContract.DetermineParameterOrder(_env);
                foreach (var phase in order.Phases)
                {
                    switch (phase)
                    {
                        case CdeclPhase.ResultPtr:
                            break; // Already handled in HandleReturnType
                        case CdeclPhase.ErrorOut:
                            pInvokeSignature.HandleSwiftError();
                            break;
                        case CdeclPhase.Self:
                            pInvokeSignature.HandleSwiftSelf();
                            break;
                        case CdeclPhase.Arguments:
                            pInvokeSignature.HandleArguments();
                            break;
                        case CdeclPhase.Metadata:
                            pInvokeSignature.HandleGenericMetadata();
                            // @_cdecl METHOD wrappers don't accept PWT parameters — protocol conformance
                            // is baked in at compile time through the Swift extension mechanism.
                            // Constructor wrappers still need PWT for the metadata accessor
                            // (which requires PWT to instantiate constrained generic types).
                            if (!_env.MethodDecl.UsesCdeclMethodWrapper)
                                pInvokeSignature.HandleProtocolConformance();
                            break;
                    }
                }

                // For non-throwing methods and non-cdecl constructors, HandleSwiftError
                // is still needed for the [SwiftError] attribute on the return type.
                // The contract only includes ErrorOut for throwing methods, so call
                // HandleSwiftError unconditionally to handle the attribute annotation
                // (it's a no-op if already called via the ErrorOut phase above).
                if (!order.Phases.Contains(CdeclPhase.ErrorOut))
                {
                    pInvokeSignature.HandleSwiftError();
                }

                _pInvokeSignature = pInvokeSignature.Build();
            }
            return _pInvokeSignature;
        }

        /// <summary>
        /// Gets the wrapper method signature.
        /// </summary>
        /// <returns>The wrapper method signature.</returns>
        public Signature GetWrapperSignature()
        {
            if (_wrapperSignature == null)
            {
                var wrapperSignature = new WrapperSignatureBuilder(_env);
                wrapperSignature.HandleReturnType();
                wrapperSignature.HandleArguments();
                _wrapperSignature = wrapperSignature.Build();
            }
            return _wrapperSignature;
        }
    }
}
