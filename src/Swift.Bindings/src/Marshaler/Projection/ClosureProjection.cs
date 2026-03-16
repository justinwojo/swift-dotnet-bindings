// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for Swift closures ↔ C# Action/Func delegates.
/// Handles both escaping (SwiftClosureData) and non-escaping (function pointer) closures.
///
/// Parameter direction (escaping): GCHandle.Alloc + SwiftClosureData construction,
///   plus [UnmanagedCallersOnly] callback declaration for Swift-to-C# calls.
/// Return direction: Lambda body wrapping function pointer invocation with type conversion.
/// </summary>
public class ClosureProjection : ITypeProjection
{
    private readonly IReadOnlyList<ITypeProjection> _argProjections;
    private readonly ITypeProjection? _returnProjection;
    private readonly bool _isEscaping;
    private readonly bool _throws;
    private readonly bool _isAsync;
    private readonly string _callbackName;

    /// <summary>
    /// Creates a closure projection.
    /// </summary>
    /// <param name="argProjections">Projections for each closure argument.</param>
    /// <param name="returnProjection">Projection for the closure return type, or null for void.</param>
    /// <param name="isEscaping">Whether the closure is @escaping.</param>
    /// <param name="throws">Whether the closure throws.</param>
    /// <param name="isAsync">Whether the closure is async.</param>
    /// <param name="callbackName">Unique callback name derived from CallbackNamePrefix.</param>
    public ClosureProjection(
        IReadOnlyList<ITypeProjection> argProjections,
        ITypeProjection? returnProjection,
        bool isEscaping,
        bool throws,
        bool isAsync,
        string callbackName)
    {
        _argProjections = argProjections;
        _returnProjection = returnProjection;
        _isEscaping = isEscaping;
        _throws = throws;
        _isAsync = isAsync;
        _callbackName = callbackName;
    }

    /// <summary>The argument projections.</summary>
    public IReadOnlyList<ITypeProjection> ArgProjections => _argProjections;

    /// <summary>The return projection (null for void closures).</summary>
    public ITypeProjection? ReturnProjection => _returnProjection;

    public string PublicType => BuildDelegateType();
    public string PInvokeType => _isEscaping ? "SwiftClosureData" : BuildFuncPtrType();
    public string? PInvokeAttribute => null;

    public MarshalPlan GetParameterPlan(string paramName)
    {
        if (_isEscaping)
        {
            // No CleanupStatements for escaping closures — the GCHandle is freed
            // inside the callback trampoline after the delegate is invoked, because
            // escaping callbacks fire asynchronously after the calling method returns.
            return new MarshalPlan
            {
                SetupStatements = new List<MarshalStatement>
                {
                    new MarshalStatement.Line($"var {paramName}Handle = GCHandle.Alloc({paramName});"),
                    new MarshalStatement.Line($"var {paramName}Closure = new SwiftClosureData((IntPtr)s_{_callbackName}, GCHandle.ToIntPtr({paramName}Handle));")
                },
                PInvokeExpression = $"{paramName}Closure"
            };
        }

        // Non-escaping — function pointer
        return MarshalPlan.PassThrough(paramName);
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        // Closure returns produce a lambda wrapping function pointer invocation
        var argTypes = string.Join(", ", _argProjections.Select(p => p.PublicType));
        var setup = new List<MarshalStatement>();

        setup.Add(new MarshalStatement.Line(
            $"if ({resultName}.FunctionPointer == IntPtr.Zero) return null;"));

        // Build lambda args and body
        var lambdaArgs = new List<string>();
        var callArgs = new List<string>();
        var bodySetup = new List<MarshalStatement>();
        var bodyCleanup = new List<MarshalStatement>();

        for (int i = 0; i < _argProjections.Count; i++)
        {
            var argName = $"arg{i}";
            lambdaArgs.Add(argName);
            var conv = _argProjections[i].GetParameterElementConversion(argName);

            // For blittable types with different public/PInvoke types (enums),
            // element conversion may be null (containers don't need it) but closures
            // still need the cast for function pointer calls.
            // Only for castable types (int, byte, etc.) — NOT IntPtr/SafeHandle which
            // need constructor-based marshalling (classes, non-frozen structs).
            if (conv == null && IsCastablePInvokeType(_argProjections[i].PInvokeType) &&
                _argProjections[i].PInvokeType != _argProjections[i].PublicType)
                conv = $"({_argProjections[i].PInvokeType}){argName}";

            if (conv != null && _argProjections[i].PInvokeType != _argProjections[i].PublicType)
            {
                // Non-frozen struct args need VWT copy
                var convertedArg = $"{argName}Converted";
                bodySetup.Add(new MarshalStatement.Line(
                    $"var {convertedArg} = {conv};"));
                callArgs.Add(convertedArg);
            }
            else
            {
                callArgs.Add(conv ?? argName);
            }
        }

        // Build the function pointer call expression
        var fpArgs = string.Join(", ", callArgs.Append($"{resultName}.Context"));
        var returnConv = _returnProjection?.GetReturnElementConversion("fpResult");
        // For blittable types with different public/PInvoke types (enums),
        // element conversion may be null but closures need the cast.
        // Only for castable types — NOT IntPtr/SafeHandle (classes, non-frozen structs).
        if (returnConv == null && _returnProjection != null &&
            IsCastablePInvokeType(_returnProjection.PInvokeType) &&
            _returnProjection.PInvokeType != _returnProjection.PublicType)
            returnConv = $"({_returnProjection.PublicType})fpResult";

        string lambdaBody;
        if (_returnProjection != null)
        {
            if (returnConv != null)
                lambdaBody = $"var fpResult = (({BuildFuncPtrType()}){resultName}.FunctionPointer)({fpArgs}); return {returnConv};";
            else
                lambdaBody = $"return (({BuildFuncPtrType()}){resultName}.FunctionPointer)({fpArgs});";
        }
        else
        {
            lambdaBody = $"(({BuildFuncPtrType()}){resultName}.FunctionPointer)({fpArgs});";
        }

        // Prepend bodySetup conversion statements into the lambda body
        var lambdaArgList = string.Join(", ", lambdaArgs);
        var bodySetupCode = string.Join(" ", bodySetup.OfType<MarshalStatement.Line>().Select(l => l.Code));
        var fullLambdaBody = bodySetupCode.Length > 0 ? $"{bodySetupCode} {lambdaBody}" : lambdaBody;
        setup.Add(new MarshalStatement.Line(
            $"var closureResult = ({lambdaArgList}) => {{ {fullLambdaBody} }};"));

        return new MarshalPlan
        {
            SetupStatements = setup,
            PInvokeExpression = "closureResult",
            RequiresUnsafe = true
        };
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public IReadOnlyList<CallbackDeclaration> CallbackDeclarations
    {
        get
        {
            if (!_isEscaping)
                return Array.Empty<CallbackDeclaration>();

            var pInvokeArgTypes = _argProjections.Select(p => p.PInvokeType).ToList();
            pInvokeArgTypes.Add("IntPtr"); // context
            var signature = string.Join(", ", pInvokeArgTypes.Select((t, i) =>
                i < _argProjections.Count ? $"{t} arg{i}" : $"{t} context"));
            var returnType = _returnProjection?.PInvokeType ?? "void";

            // Build callback body: extract delegate from GCHandle, convert args, invoke
            var body = new List<MarshalStatement>();
            var delegateType = BuildDelegateType();
            body.Add(new MarshalStatement.Line(
                $"var del = SwiftClosureMarshaller.GetDelegateFromContext<{delegateType}>(context);"));

            // Convert P/Invoke args to delegate types
            var invokeArgs = new List<string>();
            for (int i = 0; i < _argProjections.Count; i++)
            {
                var conv = _argProjections[i].GetReturnElementConversion($"arg{i}");
                // For blittable types with different public/PInvoke types (enums),
                // element conversion may be null but closures need the cast.
                // Only for castable types — NOT IntPtr/SafeHandle (classes, non-frozen structs).
                if (conv == null && IsCastablePInvokeType(_argProjections[i].PInvokeType) &&
                    _argProjections[i].PInvokeType != _argProjections[i].PublicType)
                    conv = $"({_argProjections[i].PublicType})arg{i}";
                invokeArgs.Add(conv ?? $"arg{i}");
            }

            // Build the invoke statements, wrapped in try/finally to free the GCHandle
            var invokeStatements = new List<MarshalStatement>();
            var invokeArgList = string.Join(", ", invokeArgs);
            if (_returnProjection != null)
            {
                var retConv = _returnProjection.GetParameterElementConversion("delResult");
                // For blittable types with different public/PInvoke types (enums),
                // element conversion may be null but closures need the cast.
                // Only for castable types — NOT IntPtr/SafeHandle (classes, non-frozen structs).
                if (retConv == null && IsCastablePInvokeType(_returnProjection.PInvokeType) &&
                    _returnProjection.PInvokeType != _returnProjection.PublicType)
                    retConv = $"({_returnProjection.PInvokeType})delResult";
                if (retConv != null)
                {
                    invokeStatements.Add(new MarshalStatement.Line($"var delResult = del({invokeArgList});"));
                    invokeStatements.Add(new MarshalStatement.Line($"return {retConv};"));
                }
                else
                {
                    invokeStatements.Add(new MarshalStatement.Line($"return del({invokeArgList});"));
                }
            }
            else
            {
                invokeStatements.Add(new MarshalStatement.Line($"del({invokeArgList});"));
            }

            // Wrap invoke in try/finally to free GCHandle after callback fires
            body.Add(new MarshalStatement.Block("try", invokeStatements));
            body.Add(new MarshalStatement.Block("finally", new List<MarshalStatement>
            {
                new MarshalStatement.Line("GCHandle.FromIntPtr(context).Free();")
            }));

            // Static field for function pointer — last type arg is always the return type
            var pInvokeTypes = _argProjections.Select(p => p.PInvokeType).Append("IntPtr");
            pInvokeTypes = pInvokeTypes.Append(_returnProjection?.PInvokeType ?? "void");
            var staticField = $"private static unsafe delegate* unmanaged[Cdecl]<{string.Join(", ", pInvokeTypes)}> s_{_callbackName} = &{_callbackName};";

            return new[]
            {
                new CallbackDeclaration(
                    MethodName: _callbackName,
                    CallingConvention: "CallConvCdecl",
                    Signature: signature,
                    ReturnType: returnType,
                    Body: body,
                    StaticFieldDeclaration: staticField
                )
            };
        }
    }

    // NOTE: Async/throws wrapping logic here mirrors ClosureHandler.GetCSharpDelegateType()
    // (lines 555-581). Both paths take different inputs — ClosureProjection uses
    // ITypeProjection.PublicType strings while ClosureHandler uses TranslateTypeSpecToCSharp
    // on TypeSpec. Future centralization should extract a shared helper.
    private string BuildDelegateType()
    {
        var argTypes = _argProjections.Select(p => p.PublicType).ToList();
        var coreReturnType = _returnProjection?.PublicType;

        // Wrap return type based on async/throws modifiers
        string? finalReturnType;
        if (_isAsync && _throws)
        {
            // Async+throwing: error via continuation, not SwiftResult
            finalReturnType = coreReturnType != null ? $"Task<{coreReturnType}>" : "Task";
        }
        else if (_throws)
        {
            // Throwing (non-async): wrap in SwiftResult
            var successType = coreReturnType ?? "Swift.SwiftVoid";
            finalReturnType = $"Swift.SwiftResult<{successType}, SwiftError>";
        }
        else if (_isAsync)
        {
            // Async only: Task or Task<T>
            finalReturnType = coreReturnType != null ? $"Task<{coreReturnType}>" : "Task";
        }
        else
        {
            finalReturnType = coreReturnType;
        }

        if (finalReturnType != null)
        {
            argTypes.Add(finalReturnType);
            return $"Func<{string.Join(", ", argTypes)}>";
        }

        if (argTypes.Count == 0)
            return "Action";
        return $"Action<{string.Join(", ", argTypes)}>";
    }

    /// <summary>
    /// Builds the function pointer type for thick (escaping) closure invocation.
    /// Uses Swift calling convention to match Swift's closure ABI.
    /// </summary>
    private string BuildFuncPtrType()
    {
        var types = _argProjections.Select(p => p.PInvokeType).Append("IntPtr"); // context arg
        if (_returnProjection != null)
            types = types.Append(_returnProjection.PInvokeType);
        else
            types = types.Append("void");
        return $"delegate* unmanaged[Swift]<{string.Join(", ", types)}>";
    }

    /// <summary>
    /// Returns true if the PInvokeType supports direct C# casting (enum underlying types like int, byte).
    /// Returns false for IntPtr/SafeHandle types which need constructor-based marshalling
    /// (classes, non-frozen structs) and would produce invalid casts like (MyClass)intPtrArg.
    /// </summary>
    private static bool IsCastablePInvokeType(string pInvokeType) =>
        pInvokeType is not "IntPtr" and not "SafeHandle" and not "SwiftClosureData";

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
