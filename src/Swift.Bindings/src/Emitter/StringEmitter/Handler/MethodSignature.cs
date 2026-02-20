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
            var marshalledType = type == "bool" ? MarshalledType.Bool : new MarshalledType.Simple(type);
            _parameters.Add(new Parameter(marshalledType, name, modifier));
        }
    }

    /// <summary>
    /// Builds the wrapper method signature (C# public API).
    /// </summary>
    public class WrapperSignatureBuilder : SignatureBuilderBase
    {
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

            // Check for automatic .NET type conversion (SwiftString -> string, SwiftArray -> IReadOnlyList, etc.)
            if (!_env.MethodDecl.IsAccessor)
            {
                var idiomaticType = _env.TypeConversionHandler.GetIdiomaticCSharpType(
                    argument.SwiftTypeSpec,
                    isParameter: false,
                    typeSpec => TranslateTypeSpecForConversion(typeSpec));
                if (idiomaticType != null)
                {
                    SetReturnType(idiomaticType);
                    return;
                }
            }

            if (_env.BoundGenericsHandler.IsBoundGeneric(argument))
            {
                var csTypeParam = _env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(argument, _genericContext);
                SetReturnType(csTypeParam);
                return;
            }

            // Handle closure return types (including optional closures)
            if (_env.ClosureHandler.IsClosure(argument))
            {
                var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argument)!;
                if (_env.ClosureHandler.IsSupportedClosure(closureTypeSpec))
                {
                    bool isOptional = _env.ClosureHandler.IsOptionalClosure(argument.SwiftTypeSpec);
                    var delegateType = isOptional
                        ? _env.ClosureHandler.GetCSharpOptionalDelegateType(argument.SwiftTypeSpec)
                        : _env.ClosureHandler.GetCSharpDelegateType(closureTypeSpec);
                    SetReturnType(delegateType);
                }
                else
                {
                    SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                }
                return;
            }

            // Handle tuple return types
            if (_env.TupleHandler.IsTuple(argument.SwiftTypeSpec))
            {
                var tupleTypeSpec = (TupleTypeSpec)argument.SwiftTypeSpec;
                bool hasGenericElements = _env.TupleHandler.HasGenericTypeParameterElements(tupleTypeSpec);
                // Generic-element tuples (e.g., (T, U)) are accepted when a generic context can resolve them
                // and the method is not async (async methods skip indirect result, which generic tuples need).
                if (_env.TupleHandler.IsSupportedTuple(tupleTypeSpec) ||
                    (_env.TupleHandler.IsSupportedTuple(tupleTypeSpec, _genericContext) && !(hasGenericElements && _env.MethodDecl.IsAsync)))
                    SetReturnType(_env.TupleHandler.GetCSharpTupleType(tupleTypeSpec, typeSpec =>
                    {
                        // Convert bare Swift.String → string (not inside generics — only top-level elements)
                        if (_env.TypeConversionHandler.IsSwiftString(typeSpec))
                            return "string";
                        return TranslateTypeSpecForConversion(typeSpec);
                    }));
                else
                    SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                return;
            }

            if (argument.IsGeneric)
            {
                var csTypeParamName = _env.GenericTypeMapping[argument.SwiftTypeSpec.ToString()].TypeParameter;
                SetReturnType(csTypeParamName);
                return;
            }

            // Handle existential return types (any Protocol)
            if (_env.ExistentialHandler.IsExistential(argument.SwiftTypeSpec))
            {
                var protocolList = _env.ExistentialHandler.ToProtocolListTypeSpec(argument.SwiftTypeSpec)!;
                if (_env.ExistentialHandler.IsSupportedExistential(protocolList))
                {
                    var publicType = _env.ExistentialHandler.GetPublicExistentialType(protocolList);
                    SetReturnType(publicType);
                }
                else
                {
                    SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                }
                return;
            }

            // Handle Optional-wrapped existential return types like (any DataCaching)?
            if (_env.ExistentialHandler.IsOptionalExistential(argument.SwiftTypeSpec))
            {
                var innerProtocolList = _env.ExistentialHandler.UnwrapOptionalExistential(argument.SwiftTypeSpec)!;
                if (_env.ExistentialHandler.IsSupportedExistential(innerProtocolList))
                {
                    var publicOptionalType = _env.ExistentialHandler.GetPublicOptionalExistentialType(innerProtocolList);
                    SetReturnType(publicOptionalType);
                }
                else
                {
                    SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                }
                return;
            }

            // Check for native type remapping (URL → NSUrl, Data → NSData)
            if (!_env.MethodDecl.IsAccessor && _env.TypeConversionHandler.HasNativeTypeRemapping(argument.SwiftTypeSpec))
            {
                var nativeType = _env.TypeConversionHandler.GetNativeTypeName(argument.SwiftTypeSpec);
                if (nativeType != null)
                {
                    SetReturnType(nativeType);
                    return;
                }
            }

            var typeRecord = _env.TypeDatabase.GetTypeRecordOrAnyType(argument.SwiftTypeSpec);
            // Protocol types (interfaces) are not supported as return types because they don't have Payload property
            if (typeRecord.Kind == TypeRecordKind.Protocol)
            {
                SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                return;
            }
            SetReturnType(typeRecord.CSharpTypeName.FullyQualifiedName);
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

                // Check for automatic .NET type conversion (SwiftString -> string, SwiftArray -> IEnumerable, etc.)
                if (!_env.MethodDecl.IsAccessor)
                {
                    var idiomaticType = _env.TypeConversionHandler.GetIdiomaticCSharpType(
                        argument.SwiftTypeSpec,
                        isParameter: true,
                        typeSpec => TranslateTypeSpecForConversion(typeSpec));
                    if (idiomaticType != null)
                    {
                        AddParameter(idiomaticType, csParamName);
                        continue;
                    }
                }

                if (_env.BoundGenericsHandler.IsBoundGeneric(argument))
                {
                    var csTypeParam = _env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(argument, _genericContext);
                    AddParameter(csTypeParam, csParamName);
                    continue;
                }

                // Handle closure arguments (including optional closures)
                if (_env.ClosureHandler.IsClosure(argument))
                {
                    var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argument)!;
                    if (_env.ClosureHandler.IsSupportedClosure(closureTypeSpec))
                    {
                        bool isOptional = _env.ClosureHandler.IsOptionalClosure(argument.SwiftTypeSpec);
                        var delegateType = isOptional
                            ? _env.ClosureHandler.GetCSharpOptionalDelegateType(argument.SwiftTypeSpec)
                            : _env.ClosureHandler.GetCSharpDelegateType(closureTypeSpec);
                        AddParameter(delegateType, csParamName);
                    }
                    else
                    {
                        // Unsupported closure - use placeholder that will cause method to be skipped
                        AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, csParamName);
                    }
                    continue;
                }

                // Handle tuple arguments
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

                // Handle existential arguments (any Protocol)
                if (_env.ExistentialHandler.IsExistential(argument.SwiftTypeSpec))
                {
                    var protocolList = _env.ExistentialHandler.ToProtocolListTypeSpec(argument.SwiftTypeSpec)!;
                    if (_env.ExistentialHandler.IsSupportedExistential(protocolList))
                    {
                        var publicType = _env.ExistentialHandler.GetPublicExistentialType(protocolList);
                        var containerType = _env.ExistentialHandler.GetCSharpExistentialType(protocolList);
                        // Use Existential type so P/Invoke call extracts container from interface
                        AddParameter(new MarshalledType.Existential(containerType, publicType), csParamName);
                    }
                    else
                    {
                        AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, csParamName);
                    }
                    continue;
                }

                // Handle Optional-wrapped existential arguments like (any DataCaching)?
                if (_env.ExistentialHandler.IsOptionalExistential(argument.SwiftTypeSpec))
                {
                    var innerProtocolList = _env.ExistentialHandler.UnwrapOptionalExistential(argument.SwiftTypeSpec)!;
                    if (_env.ExistentialHandler.IsSupportedExistential(innerProtocolList))
                    {
                        var publicOptionalType = _env.ExistentialHandler.GetPublicOptionalExistentialType(innerProtocolList);
                        AddParameter(publicOptionalType, csParamName);
                    }
                    else
                    {
                        AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, csParamName);
                    }
                    continue;
                }

                // Determine ref modifier for inout parameters
                var inoutModifier = argument.IsInOut ? "ref" : "";

                if (argument.IsGeneric)
                {
                    var csTypeParamName = _env.GenericTypeMapping[argument.SwiftTypeSpec.ToString()].TypeParameter;
                    AddParameter(csTypeParamName, csParamName, inoutModifier);
                }
                else
                {
                    // Check for native type remapping (URL → NSUrl, Data → NSData)
                    // Skip for property accessors to maintain property/accessor type consistency
                    if (!_env.MethodDecl.IsAccessor && _env.TypeConversionHandler.HasNativeTypeRemapping(argument.SwiftTypeSpec))
                    {
                        var nativeType = _env.TypeConversionHandler.GetNativeTypeName(argument.SwiftTypeSpec);
                        if (nativeType != null)
                        {
                            AddParameter(nativeType, csParamName, inoutModifier);
                            continue;
                        }
                    }

                    var typeRecord = _env.TypeDatabase.GetTypeRecordOrAnyType(argument.SwiftTypeSpec);
                    // Protocol types (interfaces) are not supported as parameters because they don't have Payload property
                    if (typeRecord.Kind == TypeRecordKind.Protocol)
                    {
                        AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, csParamName);
                        continue;
                    }
                    AddParameter(typeRecord.CSharpTypeName.FullyQualifiedName, csParamName, inoutModifier);
                }
            }
        }

        /// <summary>
        /// Translates a TypeSpec to C# type name for use in type conversion handlers.
        /// Handles generic types by translating their type parameters.
        /// </summary>
        private string TranslateTypeSpecForConversion(TypeSpec typeSpec)
        {
            // Handle existential types (ProtocolListTypeSpec and NamedTypeSpec with IsAny)
            // Use public interface type for wrapper signatures (e.g., ISwiftDescribable instead of ExistentialContainer1)
            if (_env.ExistentialHandler.IsExistential(typeSpec))
            {
                var protocolList = _env.ExistentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null && _env.ExistentialHandler.IsSupportedExistential(protocolList))
                {
                    var publicType = _env.ExistentialHandler.GetPublicExistentialType(protocolList);
                    // "object" fallback means the protocol has no C# interface (Any, unknown protocols).
                    // In bound generic contexts (e.g., Dictionary<K, Any>), "object" can't be used because
                    // there's no ISwiftExistentialConvertible to extract the ExistentialContainer.
                    // Use AnyType placeholder so ContainsPlaceholder skips the method.
                    if (publicType == "object")
                        return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
                    return publicType;
                }
                return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
            }

            if (typeSpec is NamedTypeSpec namedTypeSpec)
            {
                // Check if this is a generic type parameter that can be resolved
                if (TypeSpecHelpers.IsGenericTypeParameter(namedTypeSpec.Name) &&
                    _genericContext.TryResolve(namedTypeSpec.Name, out var csName))
                {
                    return csName;
                }

                var typeRecord = _env.TypeDatabase.GetTypeRecordOrAnyType(namedTypeSpec);

                // If the type falls back to AnyType or is IntPtr (pointer types), don't append generic parameters
                // Pointer types like UnsafeMutablePointer<T> resolve to IntPtr which doesn't support generics
                if (typeRecord == TypeDatabaseExtensions.AnyType ||
                    typeRecord == TypeDatabaseExtensions.IntPtrType)
                {
                    return typeRecord.CSharpTypeName.FullyQualifiedName;
                }

                // Handle generic parameters
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
