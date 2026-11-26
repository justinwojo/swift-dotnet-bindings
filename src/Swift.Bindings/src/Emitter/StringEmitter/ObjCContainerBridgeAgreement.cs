// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Keeps the two halves of a bridged collection in agreement. The Swift wrapper passes a Swift
/// Array, Set or Dictionary as the Objective-C collection it bridges to whenever any key or element,
/// at any depth, bridges to Objective-C (<see cref="CdeclParamMapper.IsObjCBridgeableContainer"/>):
/// it reads the argument as an <c>AnyObject</c> and force-casts it to the Swift collection, and
/// hands a result back the same way. The C# side can only match that through a projection that
/// bridges the whole collection (<see cref="ITypeProjection.UsesObjCContainerBridge"/>) and
/// converts every key and element to and from its Objective-C form
/// (<see cref="DictionaryProjection.FindElementWithoutObjCForm"/>).
/// </summary>
/// <remarks>
/// Without either, the C# side compiles and still disagrees with Swift: with no bridged projection
/// it falls back to passing the native Swift collection's payload, which Swift then reads as an
/// Objective-C object; with an element it cannot convert, its cast does not compile, or fails at
/// run time. Both are refused before emission, with the reason, rather than emitted or withdrawn
/// later as an emitter fault.
/// </remarks>
internal static class ObjCContainerBridgeAgreement
{
    /// <summary>
    /// True when <paramref name="typeSpec"/> (a parameter, result or property type) is a collection
    /// Swift bridges to Objective-C, or an Optional of one, that the C# side cannot bridge to match.
    /// <paramref name="detail"/> then says why.
    /// </summary>
    public static bool Disagrees(
        TypeSpec? typeSpec, ITypeDatabase typeDatabase, TypeDecl? parentType, bool isParameter, out string detail)
    {
        detail = "";
        if (typeSpec is null)
            return false;

        TypeSpec container;
        if (CdeclParamMapper.IsObjCBridgeableContainer(typeSpec, typeDatabase))
            container = typeSpec;
        else if (CdeclParamMapper.IsOptionalObjCBridgeableContainer(typeSpec, typeDatabase))
            container = ((NamedTypeSpec)typeSpec).GenericParameters[0];
        else
            return false;

        var projection = new TypeProjectionFactory().Project(container, new ProjectionContext
        {
            TypeDatabase = typeDatabase,
            IsParameter = isParameter,
            GenericContext = parentType is { IsGeneric: true } ? GenericContext.FromType(parentType) : GenericContext.Empty,
            ParentTypeDecl = parentType,
        });

        if (projection is null || !projection.UsesObjCContainerBridge)
        {
            detail = $"Swift passes '{container}' as the Objective-C collection it bridges to, and this generator has no C# "
                + "projection that bridges it, so the C# side would pass the native Swift collection where Swift reads an "
                + "Objective-C object.";
            return true;
        }

        if (DictionaryProjection.FindElementWithoutObjCForm(projection) is { } element)
        {
            detail = $"Swift passes '{container}' as the Objective-C collection it bridges to, and holds each "
                + $"'{element.PublicType}' in it in a form C# can neither build nor read (an opaque Swift box, an "
                + "Objective-C class this generator does not convert, or a Swift object in a nested set).";
            return true;
        }

        return false;
    }
}
