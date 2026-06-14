// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for async Swift methods ↔ C# Task/Task&lt;T&gt;.
/// Wraps an inner return projection (null for void → Task, non-null → Task&lt;T&gt;).
///
/// Async methods return void at P/Invoke level — the result is delivered via callback.
/// Generates Swift wrapper code with Task { } pattern and success/error callbacks.
/// </summary>
public class AsyncProjection : ITypeProjection
{
    private readonly ITypeProjection? _innerReturnProjection;
    private readonly bool _throws;
    private readonly string _callbackPrefix;

    /// <summary>
    /// Creates an async projection.
    /// </summary>
    /// <param name="innerReturnProjection">Projection for the inner return type, or null for void (Task).</param>
    /// <param name="throws">Whether the async method throws.</param>
    /// <param name="callbackPrefix">Unique prefix for callback names.</param>
    public AsyncProjection(ITypeProjection? innerReturnProjection, bool throws, string? callbackPrefix)
    {
        _innerReturnProjection = innerReturnProjection;
        _throws = throws;
        _callbackPrefix = callbackPrefix ?? "async";
    }

    /// <summary>The inner return projection (null for void).</summary>
    public ITypeProjection? InnerReturnProjection => _innerReturnProjection;

    public string PublicType => _innerReturnProjection != null
        ? $"global::System.Threading.Tasks.Task<{_innerReturnProjection.PublicType}>"
        : "global::System.Threading.Tasks.Task";

    public string PInvokeType => "void";
    public string? PInvokeAttribute => null;

    public MarshalPlan GetParameterPlan(string paramName)
    {
        // Async projection is return-side only; parameter plan is not used
        return MarshalPlan.PassThrough(paramName);
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        // For AsyncCallback strategy, produce the TCS + GCHandle setup
        if (strategy == ReturnStrategy.AsyncCallback)
        {
            var publicReturnType = _innerReturnProjection?.PublicType ?? "object";
            var tcsType = _innerReturnProjection != null
                ? $"TaskCompletionSource<{_innerReturnProjection.PublicType}>"
                : "TaskCompletionSource<object>";

            return new MarshalPlan
            {
                SetupStatements = new List<MarshalStatement>
                {
                    new MarshalStatement.Line($"var tcs = new {tcsType}();"),
                    new MarshalStatement.Line($"var holder = new object[] {{ tcs }};"),
                    new MarshalStatement.Line($"var {resultName}Handle = GCHandle.Alloc(holder);")
                },
                PInvokeExpression = $"{resultName}Handle"
            };
        }

        return MarshalPlan.PassThrough(resultName);
    }

    public bool RequiresSwiftWrapper => true;

    /// <summary>
    /// Legacy Swift async-wrapper template. <b>Not on the production async-emission path</b>: no
    /// generator code path invokes <see cref="GetSwiftWrapperCode"/> or <see cref="CallbackDeclarations"/>
    /// (only <see cref="GetReturnPlan"/> — the C# TCS/holder/GCHandle setup — is consumed, by
    /// <c>WrapperEmitter.Return</c>). The live async Swift wrappers are emitted by the dedicated
    /// emitters (<c>WrapperEmitter.Async</c>, <c>AsyncHarnessEmitter</c>,
    /// <c>AsyncMethodGenericBridgeEmitter</c>), which thread a separate process-monotonic registry
    /// key (<c>_sbwCancelKey</c>) distinct from the recyclable GCHandle callback context
    /// (<c>_sbwTask</c>). This template is retained only because the marshaler unit tests pin its
    /// shape. Before it is ever made live again it MUST adopt that <c>_sbwTask</c>/<c>_sbwCancelKey</c>
    /// separation: registering on the GCHandle id (as below) would reintroduce the key-reuse race
    /// the live path avoids — a freed/re-alloc'd handle value could collide with another call's
    /// registry entry.
    /// </summary>
    public string? GetSwiftWrapperCode(SwiftWrapperContext context)
    {
        var wrapperName = !string.IsNullOrEmpty(context.MangledName)
            ? $"{context.MangledName}_async"
            : $"_sb_{context.ModuleName}_{context.MethodName}_async";
        var hasReturn = _innerReturnProjection != null;

        // Use SwiftCallbackReturnType from context if provided (set by the emitter),
        // otherwise map C# PInvokeType to Swift equivalent
        var swiftReturnType = !string.IsNullOrEmpty(context.SwiftCallbackReturnType)
            ? context.SwiftCallbackReturnType
            : hasReturn ? MapPInvokeTypeToSwift(_innerReturnProjection!.PInvokeType) : null;

        var callbackReturnParams = swiftReturnType != null ? $"{swiftReturnType}, " : "";
        var resultCapture = hasReturn ? "let result = " : "";
        var callbackResultArg = hasReturn ? "result, " : "";
        var tryKeyword = _throws ? "try " : "";
        var awaitExpr = $"{tryKeyword}await";

        // Use OriginalCallExpression from context if provided, otherwise fall back to MethodName
        var callExpression = !string.IsNullOrEmpty(context.OriginalCallExpression)
            ? context.OriginalCallExpression
            : $"{context.MethodName}()";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"@_silgen_name(\"{wrapperName}\")");
        sb.AppendLine($"public func {wrapperName}(");
        sb.AppendLine($"    _ callback: @convention(c) ({callbackReturnParams}Int64) -> Void,");
        if (_throws)
        {
            sb.AppendLine($"    _ errorCallback: @convention(c) (UnsafeRawPointer, Int, UnsafePointer<CChar>, Int32, Int64) -> Void,");
        }
        sb.AppendLine($"    _ task: Int64) {{");
        sb.AppendLine($"    let _entry = _SBWTaskEntry()");
        sb.AppendLine($"    _sbwRegisterTask(task, _entry)");
        sb.AppendLine($"    let _sbwLaunchedTask = Task {{");
        sb.AppendLine($"        defer {{");
        sb.AppendLine($"            _sbwUnregisterTask(task)");
        sb.AppendLine($"        }}");

        if (_throws)
        {
            sb.AppendLine($"        do {{");
            sb.AppendLine($"            {resultCapture}{awaitExpr} {callExpression}");
            sb.AppendLine($"            callback({callbackResultArg}task)");
            sb.AppendLine($"        }} catch {{");
            sb.AppendLine($"            let _isCancelled: Int32 = (error is CancellationError) ? 1 : 0");
            sb.AppendLine($"            let errorMessage = String(describing: error)");
            sb.AppendLine($"            errorMessage.withCString {{ _msgPtr in");
            sb.AppendLine($"                errorCallback(UnsafeRawPointer(bitPattern: 1)!, 0, _msgPtr, _isCancelled, task)");
            sb.AppendLine($"            }}");
            sb.AppendLine($"        }}");
        }
        else
        {
            sb.AppendLine($"        {resultCapture}{awaitExpr} {callExpression}");
            sb.AppendLine($"        callback({callbackResultArg}task)");
        }

        sb.AppendLine($"    }}");
        // Finding 39: assign under the registry lock and replay an early cancel (see _sbwAssignTask).
        sb.AppendLine($"    if _sbwAssignTask(_entry, _sbwLaunchedTask) {{ _sbwLaunchedTask.cancel() }}");
        sb.AppendLine($"}}");

        return sb.ToString();
    }

    public IReadOnlyList<CallbackDeclaration> CallbackDeclarations
    {
        get
        {
            var declarations = new List<CallbackDeclaration>();

            // Success callback
            var successParams = new List<string>();
            if (_innerReturnProjection != null)
                successParams.Add($"{_innerReturnProjection.PInvokeType} rawResult");
            successParams.Add("IntPtr task");
            var successSignature = string.Join(", ", successParams);

            var successBody = new List<MarshalStatement>();
            successBody.Add(new MarshalStatement.Line(
                "var handle = GCHandle.FromIntPtr(task);"));
            successBody.Add(new MarshalStatement.Line(
                "var holder = (object[])handle.Target!;"));

            if (_innerReturnProjection != null)
            {
                var tcsType = $"TaskCompletionSource<{_innerReturnProjection.PublicType}>";
                successBody.Add(new MarshalStatement.Line(
                    $"var tcs = ({tcsType})holder[0];"));
                var retConv = _innerReturnProjection.GetReturnElementConversion("rawResult");
                var resultExpr = retConv ?? "rawResult";
                successBody.Add(new MarshalStatement.Line(
                    $"tcs.TrySetResult({resultExpr});"));
            }
            else
            {
                successBody.Add(new MarshalStatement.Line(
                    "var tcs = (TaskCompletionSource<object>)holder[0];"));
                successBody.Add(new MarshalStatement.Line(
                    "tcs.TrySetResult(null!);"));
            }
            successBody.Add(new MarshalStatement.Line("handle.Free();"));

            declarations.Add(new CallbackDeclaration(
                MethodName: $"{_callbackPrefix}SuccessCallback",
                CallingConvention: "CallConvCdecl",
                Signature: successSignature,
                ReturnType: "void",
                Body: successBody,
                StaticFieldDeclaration: null
            ));

            // Error callback (only if method throws)
            if (_throws)
            {
                var errorBody = new List<MarshalStatement>();
                errorBody.Add(new MarshalStatement.Line(
                    "var handle = GCHandle.FromIntPtr(task);"));
                errorBody.Add(new MarshalStatement.Line(
                    "var holder = (object[])handle.Target!;"));

                if (_innerReturnProjection != null)
                {
                    var tcsType = $"TaskCompletionSource<{_innerReturnProjection.PublicType}>";
                    errorBody.Add(new MarshalStatement.Line(
                        $"var tcs = ({tcsType})holder[0];"));
                }
                else
                {
                    errorBody.Add(new MarshalStatement.Line(
                        "var tcs = (TaskCompletionSource<object>)holder[0];"));
                }

                errorBody.Add(new MarshalStatement.Line(
                    "var errorMessage = global::System.Runtime.InteropServices.Marshal.PtrToStringUTF8(msg) ?? \"Unknown Swift error\";"));
                errorBody.Add(new MarshalStatement.Line(
                    "tcs.TrySetException(isCancelled == 1 ? new OperationCanceledException(errorMessage) : new SwiftException(errorMessage));"));
                errorBody.Add(new MarshalStatement.Line("handle.Free();"));

                declarations.Add(new CallbackDeclaration(
                    MethodName: $"{_callbackPrefix}ErrorCallback",
                    CallingConvention: "CallConvCdecl",
                    Signature: "IntPtr errorPtr, nint errorSize, IntPtr msg, int isCancelled, IntPtr task",
                    ReturnType: "void",
                    Body: errorBody,
                    StaticFieldDeclaration: null
                ));
            }

            return declarations;
        }
    }

    /// <summary>
    /// Maps common C# P/Invoke type names to their Swift equivalents for callback signatures.
    /// For complex types (tuples, structs), the emitter should set SwiftCallbackReturnType directly.
    /// </summary>
    private static string MapPInvokeTypeToSwift(string pInvokeType) => pInvokeType switch
    {
        "IntPtr" => "UnsafeRawPointer",
        "nint" => "Int",
        "nuint" => "UInt",
        "Int32" or "int" => "Int32",
        "Int64" or "long" => "Int64",
        "UInt32" => "UInt32",
        "UInt64" => "UInt64",
        "Double" or "double" => "Double",
        "Float" or "float" => "Float",
        "byte" => "UInt8",
        "SwiftString" => "UnsafeRawPointer",  // strings pass as raw pointer in callbacks
        _ => pInvokeType  // fallback — the emitter provides SwiftCallbackReturnType for complex types
    };

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
