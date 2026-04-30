// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Last-resort resolution for ObjC class identities — root classes from
/// <c>ObjectiveC</c> / <c>Foundation</c> (NSObject, NSProxy) and types from
/// auto-bridge Apple framework modules (UIKit, AppKit, …) that have no
/// hand-rolled C# binding. The synthetic <see cref="TypeRecord"/> carries the
/// <see cref="TypeRecordFlags.ObjCBridged"/> + Class kind that drives the
/// existing IntPtr-in-PInvoke / Handle-extraction marshalling pipeline. Runs
/// after <see cref="DatabaseLookupStrategy"/> so explicit overrides
/// registered in the type database win.
/// </summary>
internal sealed class ObjCBridgingStrategy : IResolutionStrategy
{
    public string Name => "ObjCBridging";

    public bool TryResolve(
        TypeSpec typeSpec,
        ResolutionContext context,
        [NotNullWhen(true)] out TypeResolutionResult? result)
    {
        if (typeSpec is NamedTypeSpec named && TypeDatabaseExtensions.IsObjCModuleType(named))
        {
            var typeName = SwiftTypeName.FromTypeSpec(named);
            result = new TypeResolutionResult(
                Record: TypeDatabaseExtensions.CreateObjCBridgedTypeRecord(typeName),
                Provenance: new ResolutionProvenance($"strategy:{Name}"));
            return true;
        }

        result = null;
        return false;
    }
}
