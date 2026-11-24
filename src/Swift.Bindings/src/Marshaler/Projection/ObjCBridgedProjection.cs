// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for ObjC bridged types (UIImage, NSObject subclasses, etc.).
/// Parameter direction: extract .Handle from the .NET iOS binding object.
/// Return direction: adopt the +1 reference a Swift result carries (see
/// <see cref="MarshallingHelpers.ReturnedObjectPointer"/>).
/// </summary>
/// <remarks>
/// Inside a Swift collection the object pointer alone would make the collection one over
/// <c>Swift.Int</c>: holding its elements unretained and, as a dictionary key or set element, hashed by
/// address, while Swift reads the storage as the real element type, releases what it replaces or
/// destroys, bridges it to Objective-C as that type and hashes by value. There the projection carries
/// the type's <see cref="ObjectKeyIdentity"/> (<see cref="AsHashedKey"/> for a key or set element,
/// <see cref="AsCollectionValue"/> for an array element or dictionary value), and the collection's
/// slots hold a <c>Swift.Runtime.SwiftObjectKey&lt;Identity&gt;</c>, which the module initializer gives
/// the type's own metadata (and, for a key, its <c>Hashable</c> witness table).
/// </remarks>
public class ObjCBridgedProjection : ITypeProjection
{
    private readonly string _csharpTypeName;
    private readonly ObjectKeyIdentity? _keyIdentity;

    public ObjCBridgedProjection(string csharpTypeName)
        : this(csharpTypeName, keyIdentity: null)
    {
    }

    private ObjCBridgedProjection(string csharpTypeName, ObjectKeyIdentity? keyIdentity)
    {
        _csharpTypeName = csharpTypeName;
        _keyIdentity = keyIdentity;
    }

    /// <summary>
    /// The Swift type this projection carries as a collection's key, set element, array element or
    /// dictionary value, or <c>null</c> outside a collection.
    /// </summary>
    public ObjectKeyIdentity? KeyIdentity => _keyIdentity;

    /// <summary>The same projection in a hashed key or set-element position of <paramref name="identity"/>'s type.</summary>
    public ObjCBridgedProjection AsHashedKey(ObjectKeyIdentity identity) => new(_csharpTypeName, identity);

    /// <summary>
    /// The same projection as an array element or dictionary value of <paramref name="identity"/>'s
    /// type. The slot carries the same value as a key's; only the registration differs (no
    /// <c>Hashable</c> conformance), which the module's recorded identities decide.
    /// </summary>
    public ObjCBridgedProjection AsCollectionValue(ObjectKeyIdentity identity) => new(_csharpTypeName, identity);

    public string PublicType => _csharpTypeName;
    public string PInvokeType => "IntPtr";
    public string? PInvokeAttribute => null;

    public MarshalPlan GetParameterPlan(string paramName)
    {
        return new MarshalPlan
        {
            SetupStatements = new List<MarshalStatement>
            {
                new MarshalStatement.Line($"var {paramName}Handle = {LiveHandleExpressions.ObjCArgument(paramName, paramName)};")
            },
            PInvokeExpression = $"{paramName}Handle"
        };
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        return new MarshalPlan
        {
            PInvokeExpression = MarshallingHelpers.FormatObjCBridgeCall(
                _csharpTypeName, MarshallingHelpers.ReturnedObjectPointer(resultName, strategy), nonNull: true, ownsReference: true),
            RequiresUnsafe = strategy == ReturnStrategy.IndirectResult
        };
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    // A collection slot holds the carrier; outside a collection (an optional, a result) the value is
    // the bare pointer.
    public string SwiftContainerGenericType => _keyIdentity?.CarrierType ?? PInvokeType;
    public string ContainerTypeName => SwiftContainerGenericType;

    public string? GetParameterElementConversion(string elementVar) => _keyIdentity is { } identity
        ? $"new {identity.CarrierType}({LiveHandleExpressions.ObjCSelf(elementVar)})"
        : LiveHandleExpressions.ObjCSelf(elementVar);

    // A key, element or value read out of a Swift collection is borrowed from the collection (the runtime
    // releases the copy the iterator handed over), so the wrapper takes its own reference.
    public string? GetReturnElementConversion(string elementVar) => MarshallingHelpers.FormatObjCBridgeCall(
        _csharpTypeName, _keyIdentity is null ? elementVar : $"{elementVar}.Handle", nonNull: true);

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
