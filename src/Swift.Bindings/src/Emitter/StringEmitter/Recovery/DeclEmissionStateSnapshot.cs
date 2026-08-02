// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Captures mutable emission state on the declaration tree so a failed emission attempt
/// can restore the tree to its pre-attempt shape before a retry. Emission stamps fields
/// (emission flags, wrapper strategy, and related routing bits) and mutates argument lists
/// in place; a retry that starts from those mutations produces different output than a
/// clean run would.
/// </summary>
/// <remarks>
/// Restoration is always in place: the same <see cref="MethodDecl"/>, <see cref="PropertyDecl"/>,
/// and <see cref="ArgumentDecl"/> object instances are updated, never replaced with clones.
/// Other subsystems key dictionaries on reference identity, so cloning declarations would
/// break those lookups.
/// </remarks>
internal sealed class DeclEmissionStateSnapshot
{
    private readonly List<MethodState> _methods;
    private readonly List<PropertyState> _properties;
    private readonly List<ClassState> _classes;
    private readonly List<ArgumentState> _arguments;

    private DeclEmissionStateSnapshot(
        List<MethodState> methods,
        List<PropertyState> properties,
        List<ClassState> classes,
        List<ArgumentState> arguments)
    {
        _methods = methods;
        _properties = properties;
        _classes = classes;
        _arguments = arguments;
    }

    /// <summary>
    /// Walks the whole module declaration tree once and stores per-object mutable state.
    /// </summary>
    public static DeclEmissionStateSnapshot Capture(ModuleDecl module)
    {
        List<MethodState> methods = new();
        List<PropertyState> properties = new();
        List<ClassState> classes = new();
        List<ArgumentState> arguments = new();
        HashSet<object> visited = new(ReferenceEqualityComparer.Instance);

        CaptureModule(module, methods, properties, classes, arguments, visited);
        return new DeclEmissionStateSnapshot(methods, properties, classes, arguments);
    }

    /// <summary>
    /// Writes every captured value back onto the original declaration objects.
    /// Safe to call repeatedly; each call re-applies the same captured pre-image.
    /// </summary>
    public void Restore()
    {
        foreach (MethodState state in _methods)
        {
            state.Restore();
        }

        foreach (PropertyState state in _properties)
        {
            state.Restore();
        }

        foreach (ClassState state in _classes)
        {
            state.Restore();
        }

        foreach (ArgumentState state in _arguments)
        {
            state.Restore();
        }
    }

    private static void CaptureModule(
        ModuleDecl module,
        List<MethodState> methods,
        List<PropertyState> properties,
        List<ClassState> classes,
        List<ArgumentState> arguments,
        HashSet<object> visited)
    {
        if (module.Methods is not null)
        {
            foreach (MethodDecl method in module.Methods)
            {
                CaptureMethod(method, methods, arguments, visited);
            }
        }

        if (module.Properties is not null)
        {
            foreach (PropertyDecl property in module.Properties)
            {
                CaptureProperty(property, methods, properties, arguments, visited);
            }
        }

        if (module.Types is not null)
        {
            foreach (TypeDecl type in module.Types)
            {
                CaptureType(type, methods, properties, classes, arguments, visited);
            }
        }

        if (module.Protocols is not null)
        {
            foreach (ProtocolDecl protocol in module.Protocols)
            {
                CaptureType(protocol, methods, properties, classes, arguments, visited);
            }
        }
    }

    private static void CaptureType(
        TypeDecl type,
        List<MethodState> methods,
        List<PropertyState> properties,
        List<ClassState> classes,
        List<ArgumentState> arguments,
        HashSet<object> visited)
    {
        if (!visited.Add(type))
        {
            return;
        }

        if (type is ClassDecl classDecl)
        {
            classes.Add(new ClassState(classDecl, classDecl.EmittedMetadataPInvoke));
        }

        if (type.Methods is not null)
        {
            foreach (MethodDecl method in type.Methods)
            {
                CaptureMethod(method, methods, arguments, visited);
            }
        }

        if (type.Properties is not null)
        {
            foreach (PropertyDecl property in type.Properties)
            {
                CaptureProperty(property, methods, properties, arguments, visited);
            }
        }

        if (type.Operators is not null)
        {
            foreach (OperatorDecl op in type.Operators)
            {
                CaptureOperator(op, methods, arguments, visited);
            }
        }

        if (type.Subscripts is not null)
        {
            foreach (SubscriptDecl subscript in type.Subscripts)
            {
                CaptureSubscript(subscript, methods, arguments, visited);
            }
        }

        if (type.Types is not null)
        {
            foreach (TypeDecl nested in type.Types)
            {
                CaptureType(nested, methods, properties, classes, arguments, visited);
            }
        }
    }

    private static void CaptureProperty(
        PropertyDecl property,
        List<MethodState> methods,
        List<PropertyState> properties,
        List<ArgumentState> arguments,
        HashSet<object> visited)
    {
        if (!visited.Add(property))
        {
            return;
        }

        properties.Add(new PropertyState(property, property.WasEmitted, property.EmittedCSharpName));

        if (property.Accessors is not null)
        {
            foreach (AccessorDecl accessor in property.Accessors)
            {
                if (accessor.Method is not null)
                {
                    CaptureMethod(accessor.Method, methods, arguments, visited);
                }
            }
        }
    }

    private static void CaptureSubscript(
        SubscriptDecl subscript,
        List<MethodState> methods,
        List<ArgumentState> arguments,
        HashSet<object> visited)
    {
        if (!visited.Add(subscript))
        {
            return;
        }

        if (subscript.Accessors is not null)
        {
            foreach (AccessorDecl accessor in subscript.Accessors)
            {
                if (accessor.Method is not null)
                {
                    CaptureMethod(accessor.Method, methods, arguments, visited);
                }
            }
        }
    }

    private static void CaptureOperator(
        OperatorDecl op,
        List<MethodState> methods,
        List<ArgumentState> arguments,
        HashSet<object> visited)
    {
        if (!visited.Add(op))
        {
            return;
        }

        if (op.UnderlyingMethod is not null)
        {
            CaptureMethod(op.UnderlyingMethod, methods, arguments, visited);
        }
    }

    private static void CaptureMethod(
        MethodDecl method,
        List<MethodState> methods,
        List<ArgumentState> arguments,
        HashSet<object> visited)
    {
        if (!visited.Add(method))
        {
            return;
        }

        List<ArgumentDecl>? signatureReference = method.CSSignature;
        List<ArgumentDecl> signatureElements = signatureReference is null
            ? new List<ArgumentDecl>()
            : new List<ArgumentDecl>(signatureReference);

        methods.Add(new MethodState(
            Target: method,
            Name: method.Name,
            AvailabilityAnnotations: method.AvailabilityAnnotations,
            IsSynthesizedAccessor: method.IsSynthesizedAccessor,
            IsAccessor: method.IsAccessor,
            IsSubscriptAccessor: method.IsSubscriptAccessor,
            WasEmitted: method.WasEmitted,
            EmittedCSharpName: method.EmittedCSharpName,
            UsesWrapperLibrary: method.UsesWrapperLibrary,
            HasClosureCdeclWrapper: method.HasClosureCdeclWrapper,
            UsesFreeFunctionWrapper: method.UsesFreeFunctionWrapper,
            HasOptionalPointerWrapper: method.HasOptionalPointerWrapper,
            IsMissingExportedSymbol: method.IsMissingExportedSymbol,
            HasGenericClosureBridge: method.HasGenericClosureBridge,
            HasThrowingClosureSimplification: method.HasThrowingClosureSimplification,
            HideRawAsyncIteratorSurface: method.HideRawAsyncIteratorSurface,
            StructuralIdentityKey: method.StructuralIdentityKey,
            WrapperStrategy: method.WrapperStrategy,
            ThunkAssemblyEmitted: method.ThunkAssemblyEmitted,
            AsyncPropertyName: method.AsyncPropertyName,
            HasClosureParams: method.HasClosureParams,
            HasNilOptionalClosures: method.HasNilOptionalClosures,
            OriginalArgsWithNilClosures: method.OriginalArgsWithNilClosures,
            IsClosureParamTombstone: method.IsClosureParamTombstone,
            IsGateReducedOverload: method.IsGateReducedOverload,
            SignatureListReference: signatureReference,
            SignatureElements: signatureElements));

        if (signatureReference is not null)
        {
            foreach (ArgumentDecl argument in signatureReference)
            {
                CaptureArgument(argument, arguments, visited);
            }
        }
    }

    private static void CaptureArgument(
        ArgumentDecl argument,
        List<ArgumentState> arguments,
        HashSet<object> visited)
    {
        if (!visited.Add(argument))
        {
            return;
        }

        arguments.Add(new ArgumentState(argument, argument.CSharpName, argument.IsInOut));
    }

    private sealed class MethodState
    {
        private readonly MethodDecl _target;
        private readonly string _name;
        private readonly List<AvailabilityAnnotation>? _availabilityAnnotations;
        private readonly bool _isSynthesizedAccessor;
        private readonly bool _isAccessor;
        private readonly bool _isSubscriptAccessor;
        private readonly bool _wasEmitted;
        private readonly string? _emittedCSharpName;
        private readonly bool _usesWrapperLibrary;
        private readonly bool _hasClosureCdeclWrapper;
        private readonly bool _usesFreeFunctionWrapper;
        private readonly bool _hasOptionalPointerWrapper;
        private readonly bool _isMissingExportedSymbol;
        private readonly bool _hasGenericClosureBridge;
        private readonly bool _hasThrowingClosureSimplification;
        private readonly bool _hideRawAsyncIteratorSurface;
        private readonly string? _structuralIdentityKey;
        private readonly WrapperStrategy _wrapperStrategy;
        private readonly bool _thunkAssemblyEmitted;
        private readonly string? _asyncPropertyName;
        private readonly bool _hasClosureParams;
        private readonly bool _hasNilOptionalClosures;
        private readonly List<(ArgumentDecl Arg, bool IsNilClosure, string ArgLabel)>? _originalArgsWithNilClosures;
        private readonly bool _isClosureParamTombstone;
        private readonly bool _isGateReducedOverload;
        private readonly List<ArgumentDecl>? _signatureListReference;
        private readonly List<ArgumentDecl> _signatureElements;

        public MethodState(
            MethodDecl Target,
            string Name,
            List<AvailabilityAnnotation>? AvailabilityAnnotations,
            bool IsSynthesizedAccessor,
            bool IsAccessor,
            bool IsSubscriptAccessor,
            bool WasEmitted,
            string? EmittedCSharpName,
            bool UsesWrapperLibrary,
            bool HasClosureCdeclWrapper,
            bool UsesFreeFunctionWrapper,
            bool HasOptionalPointerWrapper,
            bool IsMissingExportedSymbol,
            bool HasGenericClosureBridge,
            bool HasThrowingClosureSimplification,
            bool HideRawAsyncIteratorSurface,
            string? StructuralIdentityKey,
            WrapperStrategy WrapperStrategy,
            bool ThunkAssemblyEmitted,
            string? AsyncPropertyName,
            bool HasClosureParams,
            bool HasNilOptionalClosures,
            List<(ArgumentDecl Arg, bool IsNilClosure, string ArgLabel)>? OriginalArgsWithNilClosures,
            bool IsClosureParamTombstone,
            bool IsGateReducedOverload,
            List<ArgumentDecl>? SignatureListReference,
            List<ArgumentDecl> SignatureElements)
        {
            _target = Target;
            _name = Name;
            _availabilityAnnotations = AvailabilityAnnotations;
            _isSynthesizedAccessor = IsSynthesizedAccessor;
            _isAccessor = IsAccessor;
            _isSubscriptAccessor = IsSubscriptAccessor;
            _wasEmitted = WasEmitted;
            _emittedCSharpName = EmittedCSharpName;
            _usesWrapperLibrary = UsesWrapperLibrary;
            _hasClosureCdeclWrapper = HasClosureCdeclWrapper;
            _usesFreeFunctionWrapper = UsesFreeFunctionWrapper;
            _hasOptionalPointerWrapper = HasOptionalPointerWrapper;
            _isMissingExportedSymbol = IsMissingExportedSymbol;
            _hasGenericClosureBridge = HasGenericClosureBridge;
            _hasThrowingClosureSimplification = HasThrowingClosureSimplification;
            _hideRawAsyncIteratorSurface = HideRawAsyncIteratorSurface;
            _structuralIdentityKey = StructuralIdentityKey;
            _wrapperStrategy = WrapperStrategy;
            _thunkAssemblyEmitted = ThunkAssemblyEmitted;
            _asyncPropertyName = AsyncPropertyName;
            _hasClosureParams = HasClosureParams;
            _hasNilOptionalClosures = HasNilOptionalClosures;
            _originalArgsWithNilClosures = OriginalArgsWithNilClosures;
            _isClosureParamTombstone = IsClosureParamTombstone;
            _isGateReducedOverload = IsGateReducedOverload;
            _signatureListReference = SignatureListReference;
            _signatureElements = SignatureElements;
        }

        public void Restore()
        {
            _target.Name = _name;
            _target.AvailabilityAnnotations = _availabilityAnnotations;
            _target.IsSynthesizedAccessor = _isSynthesizedAccessor;
            _target.IsAccessor = _isAccessor;
            _target.IsSubscriptAccessor = _isSubscriptAccessor;
            _target.WasEmitted = _wasEmitted;
            _target.EmittedCSharpName = _emittedCSharpName;
            _target.UsesWrapperLibrary = _usesWrapperLibrary;
            _target.HasClosureCdeclWrapper = _hasClosureCdeclWrapper;
            _target.UsesFreeFunctionWrapper = _usesFreeFunctionWrapper;
            _target.HasOptionalPointerWrapper = _hasOptionalPointerWrapper;
            _target.IsMissingExportedSymbol = _isMissingExportedSymbol;
            _target.HasGenericClosureBridge = _hasGenericClosureBridge;
            _target.HasThrowingClosureSimplification = _hasThrowingClosureSimplification;
            _target.HideRawAsyncIteratorSurface = _hideRawAsyncIteratorSurface;
            _target.StructuralIdentityKey = _structuralIdentityKey;
            _target.WrapperStrategy = _wrapperStrategy;
            _target.ThunkAssemblyEmitted = _thunkAssemblyEmitted;
            _target.AsyncPropertyName = _asyncPropertyName;
            _target.HasClosureParams = _hasClosureParams;
            _target.HasNilOptionalClosures = _hasNilOptionalClosures;
            _target.OriginalArgsWithNilClosures = _originalArgsWithNilClosures;
            _target.IsClosureParamTombstone = _isClosureParamTombstone;
            _target.IsGateReducedOverload = _isGateReducedOverload;

            List<ArgumentDecl>? currentSignature = _target.CSSignature;
            if (!ReferenceEquals(currentSignature, _signatureListReference)
                || !SignatureContentsMatch(currentSignature, _signatureElements))
            {
                _target.CSSignature = new List<ArgumentDecl>(_signatureElements);
            }
        }

        private static bool SignatureContentsMatch(
            List<ArgumentDecl>? current,
            List<ArgumentDecl> captured)
        {
            if (current is null)
            {
                return captured.Count == 0;
            }

            if (current.Count != captured.Count)
            {
                return false;
            }

            for (int i = 0; i < captured.Count; i++)
            {
                if (!ReferenceEquals(current[i], captured[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private readonly struct PropertyState
    {
        private readonly PropertyDecl _target;
        private readonly bool _wasEmitted;
        private readonly string? _emittedCSharpName;

        public PropertyState(PropertyDecl target, bool wasEmitted, string? emittedCSharpName)
        {
            _target = target;
            _wasEmitted = wasEmitted;
            _emittedCSharpName = emittedCSharpName;
        }

        public void Restore()
        {
            _target.WasEmitted = _wasEmitted;
            // Rolled back alongside the emission flag, mirroring MethodState: the name stamp feeds
            // the module database's rename ledger, so a stamp left over from a discarded render
            // would advertise a member the retry never emitted.
            _target.RestoreEmittedCSharpName(_emittedCSharpName);
        }
    }

    private readonly struct ClassState
    {
        private readonly ClassDecl _target;
        private readonly bool _emittedMetadataPInvoke;

        public ClassState(ClassDecl target, bool emittedMetadataPInvoke)
        {
            _target = target;
            _emittedMetadataPInvoke = emittedMetadataPInvoke;
        }

        public void Restore()
        {
            _target.EmittedMetadataPInvoke = _emittedMetadataPInvoke;
        }
    }

    private readonly struct ArgumentState
    {
        private readonly ArgumentDecl _target;
        private readonly string? _csharpName;
        private readonly bool _isInOut;

        public ArgumentState(ArgumentDecl target, string? csharpName, bool isInOut)
        {
            _target = target;
            _csharpName = csharpName;
            _isInOut = isInOut;
        }

        public void Restore()
        {
            _target.CSharpName = _csharpName;
            _target.IsInOut = _isInOut;
        }
    }
}
