// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Detects SwiftUI View conformance on type declarations.
/// </summary>
public static class SwiftUIViewDetector
{
    /// <summary>
    /// Module names that indicate SwiftUI View conformance.
    /// SwiftUI types may appear under either "SwiftUI" or "SwiftUICore" depending on SDK version.
    /// </summary>
    private static readonly HashSet<string> ViewModules = new(StringComparer.Ordinal)
    {
        "SwiftUI",
        "SwiftUICore",
    };

    private const string ViewProtocolName = "View";

    /// <summary>
    /// Returns true if the struct conforms to SwiftUI.View.
    /// </summary>
    public static bool IsSwiftUIView(StructDecl structDecl)
    {
        ArgumentNullException.ThrowIfNull(structDecl);
        return HasViewConformance(structDecl.Conformances);
    }

    /// <summary>
    /// Returns true if the class conforms to SwiftUI.View.
    /// </summary>
    public static bool IsSwiftUIView(ClassDecl classDecl)
    {
        ArgumentNullException.ThrowIfNull(classDecl);
        return HasViewConformance(classDecl.Conformances);
    }

    /// <summary>
    /// Returns true if the type declaration conforms to SwiftUI.View.
    /// Dispatches to the appropriate overload based on type.
    /// </summary>
    public static bool IsSwiftUIView(TypeDecl typeDecl)
    {
        ArgumentNullException.ThrowIfNull(typeDecl);
        return typeDecl switch
        {
            StructDecl structDecl => IsSwiftUIView(structDecl),
            ClassDecl classDecl => IsSwiftUIView(classDecl),
            _ => false,
        };
    }

    private static bool HasViewConformance(List<TypeConformance> conformances)
    {
        foreach (var conformance in conformances)
        {
            if (conformance.Protocol.Name == ViewProtocolName &&
                ViewModules.Contains(conformance.Protocol.Module))
            {
                return true;
            }
        }
        return false;
    }
}
