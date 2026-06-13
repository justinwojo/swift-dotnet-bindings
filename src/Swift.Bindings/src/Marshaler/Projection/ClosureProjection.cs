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
            // Escaping closures: GCHandle is intentionally leaked. Swift may store the
            // function pointer + context beyond the P/Invoke return (e.g., EventHandler stores
            // onComplete for later fire()). The callback thunk also does NOT free — escaping
            // closures may fire multiple times. This matches MethodClosureBridge's pattern.
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
        // +0 borrowed existential closure ARGS whose backing proxy must be pinned across the native
        // function-pointer call (design change 4 / mechanism 3) — GC.KeepAlive'd after _fp(...) returns.
        var keepAliveVars = new List<string>();

        for (int i = 0; i < _argProjections.Count; i++)
        {
            var argName = $"arg{i}";
            lambdaArgs.Add(argName);
            var conv = _argProjections[i].GetParameterElementConversion(argName);

            // +0 borrowed existential closure ARG (design change 4 / mechanism 3): the EC1 aliases the
            // auto-wrapped proxy's sole R0, which under B2's weak proxy registration a GC could release
            // while the Swift function pointer is still borrowing it. Capture the proxy via the keepAlive
            // GetOrCreate overload (hoisted into a named local below) and pin it across the _fp(...) call.
            // Returns null for bare Any (owned box), EC2+ composition, and no-proxy existentials, which
            // have no GetOrCreate keepAlive overload — NOT because EC2+ doesn't alias R0 (it does, via
            // GetExistentialContainer()). This projection lambda-builder is, however, dead code for live
            // closure emission: WrapperEmitter diverts every closure return (Return.cs `IsClosure` guard)
            // and closure param (Marshalling.cs loop guards) to the string-emitter ClosureEmitter before a
            // projection is built — so the live EC2+ closure-arg keepAlive is emitted by
            // ClosureEmitter.GetSwiftInvokeArgExpression, not here.
            if (_argProjections[i] is ExistentialProjection existArg)
            {
                var kaVar = $"{argName}__ka";
                var kaConv = existArg.GetKeepAliveParameterElementConversion(argName, kaVar);
                if (kaConv != null)
                {
                    conv = kaConv;
                    keepAliveVars.Add(kaVar);
                }
            }

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

        // GC.KeepAlive(...) for any +0 existential args, emitted AFTER the native call returns so the
        // backing proxy's R0 cannot be released while Swift is still borrowing it (design change 4).
        var keepAliveCode = keepAliveVars.Count > 0
            ? " " + string.Join(" ", keepAliveVars.Select(v => $"GC.KeepAlive({v});"))
            : string.Empty;
        var fpCall = $"(({BuildFuncPtrType()}){resultName}.FunctionPointer)({fpArgs})";

        string lambdaBody;
        if (_returnProjection != null)
        {
            if (returnConv != null)
                lambdaBody = $"var fpResult = {fpCall};{keepAliveCode} return {returnConv};";
            else if (keepAliveVars.Count > 0)
                // KeepAlive must land after the native call but still return its result.
                lambdaBody = $"var fpResult = {fpCall};{keepAliveCode} return fpResult;";
            else
                lambdaBody = $"return {fpCall};";
        }
        else
        {
            lambdaBody = $"{fpCall};{keepAliveCode}";
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

            // Build the invoke statements — no GCHandle.Free here; the caller's finally block handles it.
            // Escaping closures may fire multiple times during a synchronous P/Invoke call.
            var invokeArgList = string.Join(", ", invokeArgs);
            if (_returnProjection != null)
            {
                // Existential RETURN: the C# delegate hands Swift a +1-owned existential (the thunk
                // writes it into the return buffer; Swift adopts it after the thunk returns). Mint an
                // independent +1 rather than borrow the proxy's R0 — under B2's weak proxy registration a
                // GC could release R0 before Swift loads, and the proxy finalizer would over-release the
                // +1 Swift now owns. Mirrors the reverse-dispatch getter return (task #8); no-op for
                // non-existential returns.
                var retConv = _returnProjection is ExistentialProjection existRet
                    ? existRet.GetOwnedParameterElementConversion("delResult")
                    : _returnProjection.GetParameterElementConversion("delResult");
                // For blittable types with different public/PInvoke types (enums),
                // element conversion may be null but closures need the cast.
                // Only for castable types — NOT IntPtr/SafeHandle (classes, non-frozen structs).
                if (retConv == null && IsCastablePInvokeType(_returnProjection.PInvokeType) &&
                    _returnProjection.PInvokeType != _returnProjection.PublicType)
                    retConv = $"({_returnProjection.PInvokeType})delResult";
                if (retConv != null)
                {
                    body.Add(new MarshalStatement.Line($"var delResult = del({invokeArgList});"));
                    body.Add(new MarshalStatement.Line($"return {retConv};"));
                }
                else
                {
                    body.Add(new MarshalStatement.Line($"return del({invokeArgList});"));
                }
            }
            else
            {
                body.Add(new MarshalStatement.Line($"del({invokeArgList});"));
            }

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

        // Wrap return type based on async/throws modifiers.
        // BCL types are globally qualified so Swift types with matching names
        // (e.g., TipKit.Tips.Action) can't shadow them in nested scopes.
        string? finalReturnType;
        if (_isAsync && _throws)
        {
            // Async+throwing: error via continuation, not SwiftResult
            finalReturnType = coreReturnType != null
                ? $"global::System.Threading.Tasks.Task<{coreReturnType}>"
                : "global::System.Threading.Tasks.Task";
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
            finalReturnType = coreReturnType != null
                ? $"global::System.Threading.Tasks.Task<{coreReturnType}>"
                : "global::System.Threading.Tasks.Task";
        }
        else
        {
            finalReturnType = coreReturnType;
        }

        if (finalReturnType != null)
        {
            argTypes.Add(finalReturnType);
            return $"global::System.Func<{string.Join(", ", argTypes)}>";
        }

        if (argTypes.Count == 0)
            return "global::System.Action";
        return $"global::System.Action<{string.Join(", ", argTypes)}>";
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
