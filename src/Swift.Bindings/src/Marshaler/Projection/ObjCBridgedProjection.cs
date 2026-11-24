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
/// As the key of a <c>Dictionary</c> or the element of a <c>Set</c>, the object pointer alone would make
/// the Swift collection one over <c>Swift.Int</c>: hashed by address and holding its keys unretained,
/// while Swift reads the storage as the real key type and hashes by value. In that position the
/// projection carries the key's <see cref="ObjectKeyIdentity"/> (<see cref="AsHashedKey"/>), and the
/// collection's slots hold a <c>Swift.Runtime.SwiftObjectKey&lt;Identity&gt;</c>, which the module
/// initializer gives the key type's own metadata and <c>Hashable</c> witness table.
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
    /// The Swift key type this projection carries as a hashed collection key or set element, or
    /// <c>null</c> everywhere else.
    /// </summary>
    public ObjectKeyIdentity? KeyIdentity => _keyIdentity;

    /// <summary>The same projection in a hashed key or set-element position of <paramref name="identity"/>'s type.</summary>
    public ObjCBridgedProjection AsHashedKey(ObjectKeyIdentity identity) => new(_csharpTypeName, identity);

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

    // A key slot holds the carrier; every other slot (an array element, a dictionary value) still
    // holds the bare pointer.
    public string SwiftContainerGenericType => _keyIdentity?.CarrierType ?? PInvokeType;
    public string ContainerTypeName => SwiftContainerGenericType;

    public string? GetParameterElementConversion(string elementVar) => _keyIdentity is { } identity
        ? $"new {identity.CarrierType}({LiveHandleExpressions.ObjCSelf(elementVar)})"
        : LiveHandleExpressions.ObjCSelf(elementVar);

    // A key or element read out of a Swift collection is a copy of the key value, so it holds a +1
    // on the object whichever way the collection itself was reached; the wrapper adopts it.
    public string? GetReturnElementConversion(string elementVar) => _keyIdentity is not null
        ? MarshallingHelpers.FormatObjCBridgeCall(_csharpTypeName, $"{elementVar}.Handle", nonNull: true, ownsReference: true)
        : MarshallingHelpers.FormatObjCBridgeCall(_csharpTypeName, elementVar, nonNull: true);

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
