// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;

namespace BindingsGeneration;

/// <summary>
/// A Swift <c>Hashable</c> type whose value is one object reference (an Objective-C class, or a
/// string-backed Objective-C key type such as <c>NSAttributedString.Key</c>), used as a dictionary key
/// or set element. The binding declares one identity class per such type in its module's
/// <c>__SwiftObjectKeys</c> class, and its wrapper exports the metadata of <c>Set&lt;Key&gt;</c>, from
/// which the module initializer registers the key type's metadata and <c>Hashable</c> witness table.
/// </summary>
/// <param name="SwiftTypeName">The module-qualified Swift type (<c>UIKit.UIApplication.LaunchOptionsKey</c>).</param>
public sealed record ObjectKeyIdentity(string SwiftTypeName)
{
    /// <summary>
    /// The class that holds a module's identities. Referenced unqualified: the generated code of a
    /// module (its types, its free functions and its <c>SwiftInterop</c> proxies) all live in or under
    /// the module's namespace, where the class is declared.
    /// </summary>
    public const string HolderClassName = "__SwiftObjectKeys";

    /// <summary>The identity class's name, unique per Swift type.</summary>
    public string IdentityClassName { get; } = SafeName(SwiftTypeName);

    /// <summary>The identity as referenced from the module's generated code.</summary>
    public string IdentityType => $"{HolderClassName}.{IdentityClassName}";

    /// <summary>The C# type a collection slot of this key holds.</summary>
    public string CarrierType => $"global::Swift.Runtime.SwiftObjectKey<{IdentityType}>";

    /// <summary>The Swift <c>Set</c> whose metadata the wrapper exports for this key.</summary>
    public string SwiftSetTypeName => $"Swift.Set<{SwiftTypeName}>";

    /// <summary>The exported symbol of the wrapper's <c>Set&lt;Key&gt;</c> metadata accessor.</summary>
    public string AccessorSymbol(string moduleName)
        => $"SBW_GetObjectKeySetMetadata_{moduleName}_{IdentityClassName}_{EmitterUtility.DeterministicHash8($"{moduleName}.{SwiftTypeName}")}";

    /// <summary>The C# name of the P/Invoke that calls <see cref="AccessorSymbol"/>.</summary>
    public string AccessorMethodName => $"__GetSetMetadata_{IdentityClassName}";

    private static string SafeName(string swiftTypeName)
    {
        var builder = new StringBuilder(swiftTypeName.Length);
        foreach (var c in swiftTypeName)
            builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        return builder.ToString();
    }
}
