// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for Swift.Optional&lt;T&gt; ↔ C# T?.
/// Composes with an inner projection for the wrapped type.
///
/// Parameter direction: null-check branching → SwiftOptional.NewSome/NewNone + PayloadBuffer.
/// Return direction: MarshalFromSwift + ToNullable (or discriminant check for existentials/containers).
/// </summary>
public class OptionalProjection : ITypeProjection
{
    private readonly ITypeProjection _innerProjection;
    private readonly bool _isExistentialInner;
    private readonly bool _useDangerousGetHandle;

    /// <summary>
    /// Creates an optional projection.
    /// </summary>
    /// <param name="innerProjection">The projection for the wrapped type.</param>
    /// <param name="isExistentialInner">Whether the inner type is an existential (uses discriminant check instead of ToNullable).</param>
    /// <param name="useDangerousGetHandle">When true, uses DangerousGetHandle() instead of PayloadBuffer for large Optional params passed to Swift wrappers.</param>
    public OptionalProjection(ITypeProjection innerProjection, bool isExistentialInner = false, bool useDangerousGetHandle = false)
    {
        _innerProjection = innerProjection;
        _isExistentialInner = isExistentialInner;
        _useDangerousGetHandle = useDangerousGetHandle;
    }

    /// <summary>The inner projection for the wrapped type.</summary>
    public ITypeProjection InnerProjection => _innerProjection;

    /// <summary>Whether the inner type is an existential.</summary>
    public bool IsExistentialInner => _isExistentialInner;

    public string PublicType => $"{_innerProjection.PublicType}?";
    public string PInvokeType => "IntPtr";
    public string? PInvokeAttribute => null;

    /// <summary>
    /// The container type name for this Optional — used in MarshalFromSwift calls (return direction).
    /// Uses MarshalFromSwiftType for the inner type so that non-frozen structs/classes use their
    /// public type name (e.g., SwiftOptional&lt;AssetType&gt;) instead of IntPtr.
    /// </summary>
    public string ContainerTypeName => $"SwiftOptional<{_innerProjection.MarshalFromSwiftType}>";

    /// <summary>
    /// For MarshalFromSwift deserialization, use the inner type's MarshalFromSwiftType.
    /// This ensures classes/non-frozen structs use their public name (e.g., SwiftOptional&lt;URLRequest&gt;)
    /// rather than IntPtr (which SwiftContainerGenericType would produce).
    /// </summary>
    public string MarshalFromSwiftType => ContainerTypeName;

    /// <summary>
    /// When this Optional appears as a generic parameter inside another container,
    /// use the full SwiftOptional type name with P/Invoke-level inner type.
    /// </summary>
    public string SwiftContainerGenericType => $"SwiftOptional<{_innerProjection.SwiftContainerGenericType}>";

    /// <summary>
    /// The SwiftOptional type parameter — uses SwiftContainerGenericType which returns the correct
    /// C# type for use as a generic parameter in Swift containers (enum name for enums,
    /// SwiftArray&lt;T&gt; for arrays, etc.)
    /// </summary>
    private string OptionalTypeParam => _innerProjection.SwiftContainerGenericType;

    public MarshalPlan GetParameterPlan(string paramName)
    {
        // ObjC bridged and ObjC-rooted types use nullable pointer ABI — no SwiftOptional wrapper needed.
        if (_innerProjection is ObjCBridgedProjection or ObjCRootedClassProjection)
        {
            return new MarshalPlan
            {
                SetupStatements = new List<MarshalStatement>
                {
                    new MarshalStatement.Line(
                        $"IntPtr {paramName}Buffer = {paramName} is {{ }} {paramName}Val ? {paramName}Val.Handle : IntPtr.Zero;")
                },
                PInvokeExpression = $"{paramName}Buffer"
            };
        }

        // Swift class types also use nullable pointer ABI (nil pointer = None, object pointer = Some).
        // Using SwiftOptional<IntPtr> would create Optional<Swift.Int> metadata (9 bytes) instead of
        // Optional<ClassName> (8 bytes), causing a PayloadBuffer assert on Mono.
        if (_innerProjection is ClassProjection)
        {
            return new MarshalPlan
            {
                SetupStatements = new List<MarshalStatement>
                {
                    new MarshalStatement.Line(
                        $"IntPtr {paramName}Buffer = {paramName} is {{ }} {paramName}Val ? {paramName}Val.Payload.DangerousGetHandle() : IntPtr.Zero;")
                },
                PInvokeExpression = $"{paramName}Buffer"
            };
        }

        var optTypeParam = OptionalTypeParam;
        var innerParamConv = _innerProjection.GetParameterElementConversion($"{paramName}Value");
        var containerPlan = _innerProjection.GetContainerCreationPlan($"{paramName}Value");
        var needsComplexInner = innerParamConv != null || containerPlan != null ||
            _innerProjection.PublicType != _innerProjection.PInvokeType;
        var setup = new List<MarshalStatement>();

        if (needsComplexInner)
        {
            // Complex inner type — multi-statement branching
            setup.Add(new MarshalStatement.Line(
                $"SwiftOptional<{optTypeParam}> {paramName}SwiftInner;"));

            var someBody = new List<MarshalStatement>();
            if (containerPlan != null)
            {
                // Inner is a container (Array, Dictionary) — use container creation plan
                // which creates the SwiftArray/SwiftDictionary without PayloadBuffer extraction
                someBody.AddRange(containerPlan.SetupStatements);
                someBody.Add(new MarshalStatement.Line(
                    $"{paramName}SwiftInner = SwiftOptional<{optTypeParam}>.NewSome({containerPlan.PInvokeExpression});"));
            }
            else if (innerParamConv != null)
            {
                // Inner has element conversion (string, enum, etc.) — use it directly
                someBody.Add(new MarshalStatement.Line(
                    $"{paramName}SwiftInner = SwiftOptional<{optTypeParam}>.NewSome({innerParamConv});"));
            }
            else
            {
                // Inner has different public/pinvoke types but no conversion/container — passthrough
                someBody.Add(new MarshalStatement.Line(
                    $"{paramName}SwiftInner = SwiftOptional<{optTypeParam}>.NewSome({paramName}Value);"));
            }
            setup.Add(new MarshalStatement.Block(
                $"if ({paramName} is {{ }} {paramName}Value)", someBody));

            var noneBody = new List<MarshalStatement>
            {
                new MarshalStatement.Line(
                    $"{paramName}SwiftInner = SwiftOptional<{optTypeParam}>.NewNone();")
            };
            setup.Add(new MarshalStatement.Block("else", noneBody));

            setup.Add(new MarshalStatement.Using(
                $"SwiftOptional<{optTypeParam}>", $"{paramName}Swift", $"{paramName}SwiftInner"));
        }
        else
        {
            // Simple inner type — inline ternary
            setup.Add(new MarshalStatement.Using(
                $"SwiftOptional<{optTypeParam}>", $"{paramName}Swift",
                $"{paramName} is {{ }} {paramName}Value ? SwiftOptional<{optTypeParam}>.NewSome({paramName}Value) : SwiftOptional<{optTypeParam}>.NewNone()"));
        }

        if (_useDangerousGetHandle)
        {
            // Large Optional passed to Swift wrapper — pass pointer to full Optional buffer
            setup.Add(new MarshalStatement.Line(
                $"IntPtr {paramName}Buffer = {paramName}Swift.Payload.DangerousGetHandle();"));
        }
        else
        {
            setup.Add(new MarshalStatement.Using(
                "PayloadBuffer<IntPtr>", $"{paramName}Disposable", $"{paramName}Swift.PayloadBuffer"));
            setup.Add(new MarshalStatement.Line(
                $"IntPtr {paramName}Buffer = {paramName}Disposable.Buffer;"));
        }

        return new MarshalPlan
        {
            SetupStatements = setup,
            PInvokeExpression = $"{paramName}Buffer"
        };
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        // Use MarshalFromSwiftType for return MarshalFromSwift calls — for classes/non-frozen structs,
        // this is the actual type name (not IntPtr), which MarshalFromSwift needs to construct instances.
        var returnTypeParam = _innerProjection.MarshalFromSwiftType;

        // ObjC bridged and ObjC-rooted types use nullable pointer ABI (nil = IntPtr.Zero, Some = ObjC pointer).
        // Bypass SwiftOptional entirely — the IntPtr result IS the payload.
        if (_innerProjection is ObjCBridgedProjection or ObjCRootedClassProjection)
        {
            var innerPublicType = _innerProjection.PublicType;
            var bridgeCall = MarshallingHelpers.FormatObjCBridgeCall(innerPublicType, resultName);
            var indirectBridgeCall = MarshallingHelpers.FormatObjCBridgeCall(innerPublicType, $"*(IntPtr*){resultName}");
            return strategy switch
            {
                ReturnStrategy.Direct => new MarshalPlan
                {
                    PInvokeExpression = $"({resultName} == IntPtr.Zero ? null : {bridgeCall})"
                },
                ReturnStrategy.IndirectResult or ReturnStrategy.OutBuffer => new MarshalPlan
                {
                    PInvokeExpression = $"(*(IntPtr*){resultName} == IntPtr.Zero ? null : {indirectBridgeCall})",
                    RequiresUnsafe = true
                },
                _ => MarshalPlan.PassThrough(resultName)
            };
        }

        if (_isExistentialInner)
        {
            // Existential inner — discriminant check + proxy construction
            var elemConversion = _innerProjection.GetReturnElementConversion("swiftResult.Some");
            var convExpr = elemConversion ?? "swiftResult.Some";
            return BuildDiscriminantReturnPlan(resultName, strategy, returnTypeParam, convExpr);
        }

        // Container inner (Array, Dictionary) — discriminant check + container conversion
        var containerConv = _innerProjection.GetReturnContainerConversion("swiftResult.Some");
        if (containerConv != null)
        {
            return BuildDiscriminantReturnPlan(resultName, strategy, returnTypeParam, containerConv);
        }

        // Blittable primitive inner types: read the discriminator byte directly from the buffer
        // instead of going through SwiftOptional<T> and VWT GetEnumTag. This avoids a known issue
        // where VWT GetEnumTag returns incorrect values for Optional<Int32> on some runtimes.
        // Layout: [sizeof(T) bytes payload][1 byte discriminator: 0=Some, 1=None]
        var blittableSize = GetBlittablePrimitiveSize(_innerProjection);
        if (blittableSize != null && strategy is ReturnStrategy.IndirectResult or ReturnStrategy.OutBuffer)
        {
            var innerType = _innerProjection.PublicType;
            var innerRetConvBlittable = _innerProjection.GetReturnElementConversion("_optVal");
            // Direct byte read: check discriminator at offset sizeof(T)
            var tagExpr = $"((byte*){resultName})[{blittableSize.Value}]";
            var valueExpr = $"*({innerType}*){resultName}";
            if (innerRetConvBlittable != null)
            {
                return new MarshalPlan
                {
                    SetupStatements = new List<MarshalStatement>
                    {
                        new MarshalStatement.Line(
                            $"var _optVal = {tagExpr} == 0 ? ({innerType}?){valueExpr} : null;")
                    },
                    PInvokeExpression = $"_optVal is {{ }} rawVal ? {innerRetConvBlittable} : null",
                    RequiresUnsafe = true
                };
            }
            return new MarshalPlan
            {
                PInvokeExpression = $"({tagExpr} == 0 ? ({innerType}?){valueExpr} : null)",
                RequiresUnsafe = true
            };
        }

        // Non-existential, non-container — ToNullable() path
        var marshalFromSwift = $"SwiftMarshal.MarshalFromSwift<SwiftOptional<{returnTypeParam}>>";
        var innerRetConv = _innerProjection.GetReturnElementConversion("rawVal");

        if (innerRetConv != null)
        {
            // Element conversion needed: MarshalFromSwift → ToNullable() → conditional convert
            return strategy switch
            {
                ReturnStrategy.Direct => new MarshalPlan
                {
                    SetupStatements = new List<MarshalStatement>
                    {
                        new MarshalStatement.Line(
                            $"var rawOpt = {marshalFromSwift}(new IntPtr(&{resultName})).ToNullable();")
                    },
                    PInvokeExpression = $"rawOpt is {{ }} rawVal ? {innerRetConv} : null",
                    RequiresUnsafe = true
                },
                ReturnStrategy.IndirectResult or ReturnStrategy.OutBuffer => new MarshalPlan
                {
                    SetupStatements = new List<MarshalStatement>
                    {
                        new MarshalStatement.Line(
                            $"var rawOpt = {marshalFromSwift}({resultName}).ToNullable();")
                    },
                    PInvokeExpression = $"rawOpt is {{ }} rawVal ? {innerRetConv} : null"
                },
                ReturnStrategy.AsyncCallback => new MarshalPlan
                {
                    SetupStatements = new List<MarshalStatement>
                    {
                        new MarshalStatement.Line(
                            $"var rawOpt = {marshalFromSwift}({resultName}).ToNullable();")
                    },
                    PInvokeExpression = $"rawOpt is {{ }} rawVal ? {innerRetConv} : null"
                },
                _ => MarshalPlan.PassThrough(resultName)
            };
        }

        // No element conversion — simple ToNullable()
        return strategy switch
        {
            ReturnStrategy.Direct => new MarshalPlan
            {
                PInvokeExpression = $"{marshalFromSwift}(new IntPtr(&{resultName})).ToNullable()",
                RequiresUnsafe = true
            },
            ReturnStrategy.IndirectResult or ReturnStrategy.OutBuffer => new MarshalPlan
            {
                PInvokeExpression = $"{marshalFromSwift}({resultName}).ToNullable()"
            },
            ReturnStrategy.AsyncCallback => new MarshalPlan
            {
                PInvokeExpression = $"{marshalFromSwift}({resultName}).ToNullable()"
            },
            _ => MarshalPlan.PassThrough(resultName)
        };
    }

    /// <summary>
    /// Returns the size in bytes for blittable primitive C# types, or null if not a known blittable primitive.
    /// Used for direct discriminator byte reading in Optional return/parameter marshalling.
    /// </summary>
    private static int? GetBlittablePrimitiveSize(ITypeProjection inner)
    {
        return GetBlittablePrimitiveSizePublic(inner);
    }

    /// <summary>
    /// Public accessor for blittable primitive size check.
    /// Used by WrapperEmitter.Return.cs for accessor return fast path.
    /// </summary>
    public static int? GetBlittablePrimitiveSizePublic(ITypeProjection inner)
    {
        if (inner is not BlittableProjection) return null;
        return inner.PublicType switch
        {
            "bool" or "byte" or "sbyte" => 1,
            "short" or "ushort" => 2,
            "int" or "uint" or "float" => 4,
            "long" or "ulong" or "double" => 8,
            "nint" or "nuint" => 8, // 8 bytes on arm64
            _ => null
        };
    }

    /// <summary>
    /// Builds a return plan using discriminant check (Case == None ? null : conversion).
    /// Used for both existential and container inners.
    /// </summary>
    private static MarshalPlan BuildDiscriminantReturnPlan(
        string resultName, ReturnStrategy strategy, string optTypeParam, string convExpr)
    {
        var marshalFromSwift = $"SwiftMarshal.MarshalFromSwift<SwiftOptional<{optTypeParam}>>";
        return strategy switch
        {
            ReturnStrategy.Direct => new MarshalPlan
            {
                SetupStatements = new List<MarshalStatement>
                {
                    new MarshalStatement.Line(
                        $"var swiftResult = {marshalFromSwift}(new IntPtr(&{resultName}));")
                },
                PInvokeExpression = $"swiftResult.Case == SwiftOptionalCases.None ? null : {convExpr}",
                RequiresUnsafe = true
            },
            ReturnStrategy.IndirectResult or ReturnStrategy.OutBuffer => new MarshalPlan
            {
                SetupStatements = new List<MarshalStatement>
                {
                    new MarshalStatement.Line(
                        $"var swiftResult = {marshalFromSwift}({resultName});")
                },
                PInvokeExpression = $"swiftResult.Case == SwiftOptionalCases.None ? null : {convExpr}"
            },
            ReturnStrategy.AsyncCallback => MarshalPlan.PassThrough(resultName),
            _ => MarshalPlan.PassThrough(resultName)
        };
    }

    /// <summary>
    /// Element-level conversion for when this Optional appears as a dictionary/array value.
    /// Converts SwiftOptional&lt;T&gt; → T? via .ToNullable() with optional inner element conversion.
    /// </summary>
    public string? GetReturnElementConversion(string elementVar)
    {
        // Derive a unique inner variable name from elementVar to avoid CS0128 when
        // multiple optional elements appear in the same scope (e.g., tuple elements).
        var safeVar = elementVar.Replace(".", "_").Replace("[", "").Replace("]", "");
        var innerVar = $"_optVal_{safeVar}";
        var innerConv = _innerProjection.GetReturnElementConversion(innerVar);
        if (innerConv != null)
        {
            // Inner needs conversion: e.g., SwiftOptional<SwiftString> → string?
            // ToNullable() gives SwiftString?, then convert the inner value.
            return $"({elementVar}.ToNullable() is {{ }} {innerVar} ? ({_innerProjection.PublicType}?){innerConv} : null)";
        }
        // Simple inner: SwiftOptional<T> → T?
        return $"{elementVar}.ToNullable()";
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
