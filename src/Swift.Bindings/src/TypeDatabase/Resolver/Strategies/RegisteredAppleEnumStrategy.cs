// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Resolves an Apple framework type the registry lists as a value type AND describes as an
/// integer-backed enum.
/// <para>
/// A value-type listing exists to withhold the synthetic ObjC bridged-class record from a name that
/// is not a class. Withholding it is correct — but on its own it leaves the type with no record at
/// all, so every member mentioning it is skipped as unresolvable, even when the type is a plain
/// NS_ENUM that crosses the boundary as its raw integer. This strategy supplies the missing value
/// -type record for exactly the entries the registry can fully describe; a listing that says nothing
/// about the shape resolves no further here and stays fail-closed.
/// </para>
/// <para>
/// Runs after the database cascade — a hand-authored database entry still wins — and before
/// <see cref="ObjCBridgingStrategy"/>, which would in any case decline a registered value type.
/// </para>
/// </summary>
internal sealed class RegisteredAppleEnumStrategy : IResolutionStrategy
{
    public string Name => "RegisteredAppleEnum";

    public bool TryResolve(
        TypeSpec typeSpec,
        ResolutionContext context,
        [NotNullWhen(true)] out TypeResolutionResult? result)
    {
        if (typeSpec is NamedTypeSpec named)
        {
            var typeName = SwiftTypeName.FromTypeSpec(named);
            var record = TypeDatabaseExtensions.TryCreateRegisteredAppleEnumRecord(
                typeName, named.Usr, AppleTypeSurfaceIndex.Default);
            if (record is not null)
            {
                result = new TypeResolutionResult(
                    Record: record,
                    Provenance: new ResolutionProvenance($"strategy:{Name}"));
                return true;
            }
        }

        result = null;
        return false;
    }
}
