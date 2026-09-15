// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for frozen structs with memory management (ClassWithBufferStruct).
/// These are Swift structs that contain reference-counted fields (e.g., String fields),
/// are frozen (ABI-stable layout), but need memory management for their reference fields.
///
/// P/Invoke returns a .Buffer struct by value (blittable layout).
/// Direct returns need new IntPtr(&amp;result) with RequiresUnsafe = true.
/// Indirect returns receive a pointer directly.
///
/// Parameter direction: PayloadBuffer extraction (same as existing WrapperEmitter.Marshalling.cs:711).
/// Return direction: MarshalFromSwift constructs the ISwiftObject from the buffer pointer.
/// </summary>
public class FrozenWithMemoryProjection : ITypeProjection
{
    private readonly string _typeName;

    public FrozenWithMemoryProjection(string typeName)
    {
        _typeName = typeName;
    }

    public string PublicType => _typeName;
    public string PInvokeType => $"{_typeName}.Buffer";
    public string? PInvokeAttribute => null;

    /// <summary>
    /// Swift generic containers use the metadata-bearing wrapper type. The nested
    /// <c>.Buffer</c> is only the lowered carrier for a bare P/Invoke value; using it as
    /// <c>SwiftArray&lt;T&gt;</c>, <c>SwiftSet&lt;T&gt;</c>, or <c>SwiftDictionary&lt;K,V&gt;</c>'s
    /// generic argument loses the Swift type metadata and disagrees with the public wrapper
    /// values supplied to <c>FromEnumerable</c>/<c>FromDictionary</c>. The runtime collection
    /// marshaler invokes <c>ISwiftObject.MarshalToSwift</c> on this wrapper and hands the
    /// resulting owned value to Swift's consuming collection operation.
    /// </summary>
    public string SwiftContainerGenericType => _typeName;

    /// <summary>
    /// For MarshalFromSwift calls, use the type name (not .Buffer). MarshalFromSwift needs
    /// the real type to construct instances via ISwiftObject.NewFromPayload.
    /// </summary>
    public string MarshalFromSwiftType => _typeName;

    /// <summary>
    /// The container type name for use in SwiftOptional&lt;T&gt; and TypeMetadata resolution.
    /// Uses .Buffer to match the P/Invoke return type.
    /// </summary>
    public string ContainerTypeName => $"{_typeName}.Buffer";

    public MarshalPlan GetParameterPlan(string paramName)
    {
        return new MarshalPlan
        {
            SetupStatements = new List<MarshalStatement>
            {
                new MarshalStatement.Using(
                    $"PayloadBuffer<{_typeName}.Buffer>", $"{paramName}Disposable", $"{paramName}.PayloadBuffer")
            },
            PInvokeExpression = $"{paramName}Disposable.Buffer",
            // The lowered buffer carries the wrapper's own references. The wrapper is the caller's
            // long-lived object and destroys them on Dispose, so a consuming callee needs a count of
            // its own rather than a share of that one.
            OwnedValueArgument = new(_typeName, $"{paramName}.Payload")
        };
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        return strategy switch
        {
            // Direct (by-value register) return: `resultName` is a C# stack temporary the caller
            // OWNS (the Swift value moved out of the callee, carrying +1 on its heap fields).
            // A copyable carrier's NewFromPayload makes an InitializeWithCopy duplicate for the
            // wrapper's SafeHandle, so the owned temporary must be value-witness-destroyed
            // afterwards or its +1 leaks — C# never runs Swift destruction when the stack local
            // goes out of scope. A ~Copyable carrier cannot be value-witness copied, so its
            // NewFromPayload takes the value instead and the temporary is left moved-from;
            // MarshalFromSwiftObjectConsuming reads the carrier's declared
            // PayloadConstructionSemantics and skips the destroy on that arm, so this one
            // expression is correct for both without a per-carrier branch here.
            ReturnStrategy.Direct => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwiftObjectConsuming<{_typeName}>(&{resultName})",
                RequiresUnsafe = true
            },
            ReturnStrategy.IndirectResult => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwiftObject<{_typeName}>({resultName})"
            },
            ReturnStrategy.OutBuffer => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwiftObject<{_typeName}>({resultName})"
            },
            _ => MarshalPlan.PassThrough(resultName)
        };
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    /// <summary>
    /// No parameter element conversion. Swift generic containers carry the metadata-bearing
    /// wrapper type and let the runtime collection marshal it through
    /// <c>ISwiftObject.MarshalToSwift</c>; extracting <c>.Buffer</c> here would instead lose the
    /// value's Swift metadata and require an unsafe per-element SafeHandle pin lifetime.
    /// </summary>
    public string? GetParameterElementConversion(string elementVar) => null;

    /// <summary>
    /// No return element conversion needed. When used inside Optional, ToNullable() handles
    /// construction via ISwiftObject.NewFromPayload. Standalone returns use GetReturnPlan.
    /// </summary>
    public string? GetReturnElementConversion(string elementVar) => null;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
