// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Describes how a projected C# value that is NOT itself an <c>NSObject</c> converts to and from
/// the ObjC object it bridges as. Carried by an <see cref="ObjCBridgeableProjection"/> for an Apple
/// NS_STRING_ENUM / NS_TYPED_ENUM: the Swift side is a String-backed <c>RawRepresentable</c> newtype
/// that bridges to <c>NSString</c>, but the platform binding projects the constant group as a C#
/// <c>enum</c> with a sibling <c>{Enum}Extensions</c> converter. The enum stays in the public
/// signature; the converter runs at the marshalling boundary.
/// </summary>
/// <param name="ProjectedType">The idiomatic C# type in the public signature (the enum).</param>
/// <param name="CarrierType">The ObjC class the value bridges as (<c>Foundation.NSString</c>).</param>
public sealed record AppleTypedEnumAdapter(string ProjectedType, string CarrierType)
{
    /// <summary>The platform binding's static converter class, e.g. <c>VNBarcodeSymbologyExtensions</c>.</summary>
    public string ExtensionsType => ProjectedType + TypeDatabaseExtensions.AppleTypedEnumExtensionsSuffix;

    /// <summary>
    /// Converts a projected C# value to the ObjC object that crosses the boundary.
    /// </summary>
    /// <remarks>
    /// The platform converter is declared nullable because it returns <c>null</c> for an enum value
    /// outside the constant group — i.e. a value the caller invented rather than one the binding
    /// defines. The suppression makes that a <see cref="NullReferenceException"/> at the call site
    /// instead of a null pointer handed to Swift, and matches how the return direction already
    /// suppresses <c>GetNSObject&lt;T&gt;</c>'s nullable result.
    /// </remarks>
    public string ToCarrier(string value) => $"{ExtensionsType}.GetConstant({value})!";

    /// <summary>Converts an ObjC object read back across the boundary to the projected C# value.</summary>
    public string FromCarrier(string carrier) => $"{ExtensionsType}.GetValue({carrier})";
}

/// <summary>
/// Projection for ObjC-bridgeable Swift value types (e.g., Foundation.URL ↔ NSURL).
/// These types freely bridge to ObjC classes via _ObjectiveCBridgeable and cross
/// the @_cdecl boundary as ObjC object pointers (IntPtr), not Swift struct bytes.
///
/// Parameter direction: extract .Handle from the .NET iOS binding object (e.g., NSUrl.Handle).
/// Return direction: wrap IntPtr with GetNSObject&lt;T&gt;() (same as ObjCBridgedProjection).
///
/// An optional <see cref="AppleTypedEnumAdapter"/> covers the variant whose .NET projection is a C#
/// enum rather than an NSObject subclass (an Apple NS_STRING_ENUM / NS_TYPED_ENUM). The ABI is
/// identical — still an NSString pointer, still the whole-container NSArray/NSSet/NSDictionary
/// bridge for collections — so only the managed-side conversion differs: the enum converts through
/// the platform binding's <c>{Enum}Extensions.GetConstant</c>/<c>GetValue</c> instead of exposing a
/// <c>Handle</c>.
///
/// Distinct from ObjCBridgedProjection (which handles ObjC class wrappers like UIImage)
/// and NativeRemappedProjection (which handles Swift wrapper ↔ .NET native type conversion).
/// </summary>
public class ObjCBridgeableProjection : ITypeProjection
{
    private readonly string _csharpTypeName;
    private readonly AppleTypedEnumAdapter? _typedEnum;

    public ObjCBridgeableProjection(string csharpTypeName, AppleTypedEnumAdapter? typedEnum = null)
    {
        _csharpTypeName = csharpTypeName;
        _typedEnum = typedEnum;
    }

    public string PublicType => _typedEnum?.ProjectedType ?? _csharpTypeName;
    public string PInvokeType => "IntPtr";
    public string? PInvokeAttribute => null;

    public AppleTypedEnumAdapter? TypedEnumAdapter => _typedEnum;

    /// <summary>
    /// The .NET type an ObjC pointer of this projection must be materialized as before it can be
    /// projected — the same as <see cref="PublicType"/> except for a typed enum, whose pointer is
    /// an <c>NSString</c> constant.
    /// </summary>
    public string BridgedObjCType => _typedEnum?.CarrierType ?? _csharpTypeName;

    /// <summary>
    /// Reads an ObjC pointer expression into the public C# value: materialize the ObjC object,
    /// then (for a typed enum) convert it to the projected enum.
    /// </summary>
    public string BridgeReadExpression(string ptrExpr, bool nonNull = false, bool ownsReference = false)
    {
        // A typed enum's carrier feeds {Enum}Extensions.GetValue, whose parameter is NOT nullable.
        // Every caller reaching here has already established the pointer is non-nil — the
        // nullable-pointer ABI paths test it against IntPtr.Zero before reading, and the direct
        // return path is non-optional by construction — so materialize the carrier as non-null
        // rather than widening the platform converter's contract.
        var carrier = MarshallingHelpers.FormatObjCBridgeCall(
            BridgedObjCType, ptrExpr, nonNull: nonNull || _typedEnum is not null, ownsReference: ownsReference);
        return _typedEnum is null ? carrier : _typedEnum.FromCarrier(carrier);
    }

    /// <summary>
    /// Converts a public C# value to the ObjC object whose <c>Handle</c> crosses the boundary.
    /// Identity for a value that already IS the ObjC object.
    /// </summary>
    public string BridgeWriteExpression(string value) => _typedEnum?.ToCarrier(value) ?? value;

    /// <summary>
    /// Signals container projections that this element type should use whole-container
    /// ObjC bridge (NSArray, NSDictionary, NSSet) instead of SwiftArray&lt;T&gt; pipeline.
    /// </summary>
    public bool UsesObjCContainerBridge => true;

    public MarshalPlan GetParameterPlan(string paramName)
    {
        // The typed-enum arm binds the converted constant to a local before reading its Handle: the
        // constant is a managed wrapper around a global NSString, and holding it in a local keeps
        // that wrapper rooted for the duration of the call.
        var setup = _typedEnum is null
            ? new List<MarshalStatement>
            {
                new MarshalStatement.Line($"var {paramName}Handle = {paramName}.Handle;")
            }
            : new List<MarshalStatement>
            {
                new MarshalStatement.Line($"var {paramName}Constant = {BridgeWriteExpression(paramName)};"),
                new MarshalStatement.Line($"var {paramName}Handle = {paramName}Constant.Handle;")
            };

        return new MarshalPlan
        {
            SetupStatements = setup,
            PInvokeExpression = $"{paramName}Handle",
            // The handle is read off an object the caller keeps owning — its own wrapper, or the
            // managed wrapper around the constant group's global NSString — so against a callee that
            // consumes its argument the pointer would arrive at +0 and the callee's release would
            // take a count nobody transferred. Retained isa-dispatched, since an NSObject-backed
            // bridge needs objc_retain rather than swift_retain, and null-tolerant so a zero handle
            // stays a no-op. Ignored on every borrowing call.
            OwnedHandOverStatement = $"global::Swift.Runtime.Arc.UnknownObjectRetain({paramName}Handle);"
        };
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
        => new MarshalPlan { PInvokeExpression = BridgeReadExpression(resultName, nonNull: true) };

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public string? GetParameterElementConversion(string elementVar)
        => _typedEnum is null ? $"{elementVar}.Handle" : BridgeWriteExpression(elementVar);

    public string? GetReturnElementConversion(string elementVar)
        => BridgeReadExpression(elementVar, nonNull: true);

    public bool ElementRequiresDisposal => false;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
