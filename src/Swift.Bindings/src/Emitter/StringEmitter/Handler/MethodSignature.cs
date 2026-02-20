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
    public record Parameter(MarshalledType Type, string Name, string modifier = "")
    {
        public string CallString()
        {
            var typeStr = Type switch
            {
                MarshalledType.Existential(var containerType, var publicType) => publicType,
                MarshalledType.SimpleEnum(var underlyingType, var enumTypeName) => enumTypeName,
                MarshalledType.ObjCBridged(var csTypeName) => csTypeName,
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
        public string ParametersString() => string.Join(", ", Parameters.Select(p => p.SignatureString()));

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
                return prefix + p.SignatureString();
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
                // Existential protocol types: extract container from interface
                { Type: MarshalledType.Existential(var containerType, _) } =>
                    $"((Swift.Runtime.ISwiftExistentialConvertible<{containerType}>){parameter.Name}).GetExistentialContainer()",
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
                // Instance methods on free-function wrapper paths pass self as explicit IntPtr.
                { Name: "_selfClass" } => "*(IntPtr*)_payload.DangerousGetHandle()",
                { Name: "_selfFixed" } => "(IntPtr)__self",
                { Name: "_self", Type: MarshalledType.Simple("IntPtr") } => "_payload.DangerousGetHandle()",
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
                    IsParameter = false
                });
                if (projection != null && !ShouldSkipProjectionForAccessor(argument.SwiftTypeSpec))
                {
                    SetReturnType(projection.PublicType);
                    return;
                }
            }

            // Fallback: bound generics (user-defined generic types like Result<T, E>
            // that the factory returns null for because it can't translate generic args)
            if (_env.BoundGenericsHandler.IsBoundGeneric(argument))
            {
                var csTypeParam = _env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(argument, _genericContext);
                SetReturnType(csTypeParam);
                return;
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
                        if (_env.TypeConversionHandler.IsSwiftString(typeSpec))
                            return "string";
                        return TranslateTypeSpecForConversion(typeSpec);
                    }));
                else
                    SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
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
                var csParamName = NameProvider.GetCSharpParameterName(argument);

                // Try factory-based projection for non-tuple types (same guards as HandleReturnType)
                if (argument.SwiftTypeSpec is not TupleTypeSpec)
                {
                    var projection = _factory.Project(argument.SwiftTypeSpec, new ProjectionContext
                    {
                        TypeDatabase = _env.TypeDatabase,
                        IsParameter = true
                    });
                    if (projection != null && !ShouldSkipProjectionForAccessor(argument.SwiftTypeSpec))
                    {
                        AddParameter(projection.PublicType, csParamName);
                        continue;
                    }
                }

                // Fallback: bound generics (user-defined generic types)
                if (_env.BoundGenericsHandler.IsBoundGeneric(argument))
                {
                    var csTypeParam = _env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(argument, _genericContext);
                    AddParameter(csTypeParam, csParamName);
                    continue;
                }

                // Fallback: tuple arguments (preserves element labels and shallow conversion)
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

            return _env.TypeConversionHandler.IsConvertibleType(typeSpec) ||
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
                pInvokeSignature.HandleArguments();
                pInvokeSignature.HandleGenericMetadata();
                pInvokeSignature.HandleProtocolConformance();
                pInvokeSignature.HandleSwiftSelf();
                pInvokeSignature.HandleSwiftError();
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
