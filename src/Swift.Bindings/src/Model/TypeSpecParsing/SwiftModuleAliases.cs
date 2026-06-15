// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;

namespace BindingsGeneration;

/// <summary>
/// Swift-toolchain module aliases applied to type names as they are parsed.
///
/// swift-api-digester prints some Foundation value types under the legacy <c>ObjectiveC</c>
/// module name (e.g. <c>ObjectiveC.NSString</c>), but those types are consumed everywhere else
/// in the generator under <c>Foundation.*</c>. This normalization used to live as an inline
/// substring rewrite buried inside <see cref="TypeSpecParser"/>'s tokenizer-dispatch loop; it is
/// extracted here so the alias policy is a single, named, testable concern rather than a magic
/// rewrite hidden in the grammar.
///
/// It is applied at the parse choke point because that is the one place every type-name string
/// flows through today. The longer-term home for module/type aliasing is the unified type
/// resolver (see architecture review Finding 10); until that lands, keeping it here preserves
/// the historical behavior exactly while making the rule explicit.
/// </summary>
internal static class SwiftModuleAliases
{
    private const string ObjectiveCModulePrefix = "ObjectiveC.";
    private const string FoundationModule = "Foundation";

    /// <summary>
    /// Rewrites a fully-qualified type name's legacy <c>ObjectiveC.</c> module prefix to
    /// <c>Foundation.</c>. Any other name is returned unchanged.
    /// </summary>
    public static string NormalizeTypeName(string typeName)
    {
        if (typeName.StartsWith(ObjectiveCModulePrefix, StringComparison.Ordinal))
        {
            // Replace only the leading module component; keep the remaining "." + member intact.
            return FoundationModule + typeName.Substring("ObjectiveC".Length);
        }
        return typeName;
    }
}
