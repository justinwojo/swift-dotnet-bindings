// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Resolves identities from Apple framework modules that have no .NET iOS
/// binding equivalent (SwiftUI, XCTest, Combine, …). Most identities degrade
/// to <see cref="TypeDatabaseExtensions.AnyType"/> so members referencing them
/// are gracefully suppressed. Non-generic identities that have a registered
/// C# stub (e.g., the <c>SwiftUIDatabase.xml</c> entries that own
/// <c>ISwiftObject</c> projections) keep their real <see cref="TypeRecord"/>
/// — that exception is the resolution surface that lets hand-rolled
/// supplements coexist with bulk suppression.
/// </summary>
internal sealed class UnsupportedAppleModuleStrategy : IResolutionStrategy
{
    public string Name => "UnsupportedAppleModule";

    public bool TryResolve(
        TypeSpec typeSpec,
        ResolutionContext context,
        [NotNullWhen(true)] out TypeResolutionResult? result)
    {
        if (typeSpec is NamedTypeSpec named && TypeDatabaseExtensions.IsUnsupportedAppleModule(named))
        {
            if (!named.ContainsGenericParameters)
            {
                var registeredName = SwiftTypeName.FromTypeSpec(named);
                if (context.Database.TryGetTypeRecord(registeredName, out var registered))
                {
                    result = new TypeResolutionResult(
                        Record: registered,
                        Provenance: new ResolutionProvenance($"strategy:{Name}:Registered"));
                    return true;
                }
            }

            result = new TypeResolutionResult(
                Record: TypeDatabaseExtensions.AnyType,
                Provenance: new ResolutionProvenance($"strategy:{Name}"));
            return true;
        }

        result = null;
        return false;
    }
}
