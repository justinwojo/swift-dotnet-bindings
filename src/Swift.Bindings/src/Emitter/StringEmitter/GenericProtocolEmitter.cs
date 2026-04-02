// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared utility for emitting Swift protocols used in generic type dispatch patterns.
/// All wrapper emitters (Method, Property, Constructor) use protocol-based type erasure
/// to dispatch through generic types. This class centralizes the common emission pattern:
///
///   private protocol _SBW_{prefix}_{hash} {
///       {memberDeclaration}
///   }
///   extension ModuleQualifiedName: _SBW_{prefix}_{hash} {}
///
/// Protocol name prefixes by dispatch pattern:
///   PG   = Property Getter — instance protocol for concrete property on generic class
///   P    = method Protocol — instance protocol for concrete method on generic class
///   CI   = Constructor Init — instance protocol for generic class init (: AnyObject)
///   GSPG = Generic Static Property Getter — static dispatch protocol for T-typed property getter
///   GSPS = Generic Static Property Setter — static dispatch protocol for T-typed property setter
///   GSM  = Generic Static Method — static dispatch protocol for T-typed method
///   GSF  = Generic Static Factory — static dispatch protocol for T-typed constructor
/// </summary>
internal static class GenericProtocolEmitter
{
    /// <summary>
    /// Emits a private protocol declaration with a conformance extension.
    /// This is the common pattern shared by Method, Property, and Constructor emitters
    /// for generic class instance dispatch (protocol-based type erasure).
    ///
    /// The protocol contains a single member declaration (func, var, or init) and the
    /// generic type is extended to unconditionally conform. At the call site, the self
    /// pointer is cast to the protocol existential to erase the generic parameter.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer for the wrapper .swift file.</param>
    /// <param name="prefix">The protocol name prefix (e.g., "P", "PG", "CI").</param>
    /// <param name="symbolName">The mangled symbol name used for hash-based uniqueness.</param>
    /// <param name="memberDeclaration">The protocol member declaration (e.g., "func foo() -> Int", "var name: String { get }").</param>
    /// <param name="moduleQualifiedName">The module-qualified Swift type name for the conformance extension.</param>
    /// <param name="protocolConstraint">Optional protocol constraint (e.g., "AnyObject" for class-only protocols).</param>
    /// <returns>The generated protocol name (e.g., "_SBW_P_A1B2C3D4").</returns>
    internal static string EmitProtocolAndConformance(
        SwiftWriter swiftWriter,
        string prefix,
        string symbolName,
        string memberDeclaration,
        string moduleQualifiedName,
        string? protocolConstraint = null)
    {
        var protocolName = GetProtocolName(prefix, symbolName);
        var constraintClause = protocolConstraint != null ? $": {protocolConstraint} " : "";

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            private protocol {{protocolName}}{{constraintClause}} {
                {{memberDeclaration}}
            }
            extension {{moduleQualifiedName}}: {{protocolName}} {}
            """);

        return protocolName;
    }

    /// <summary>
    /// Gets the protocol name for a given prefix and symbol name without emitting anything.
    /// Useful when the protocol name is needed before or independently of emission.
    /// </summary>
    internal static string GetProtocolName(string prefix, string symbolName)
    {
        return $"_SBW_{prefix}_{EmitterUtility.DeterministicHash8(symbolName)}";
    }

    /// <summary>
    /// Builds the Swift protocol member declaration for a method.
    /// Delegates to MethodWrapperEmitter.BuildProtocolMethodDeclaration for the actual
    /// method signature construction (parameter labels, types, throws, return type).
    /// </summary>
    internal static string BuildMethodMemberDeclaration(MethodDecl methodDecl, MethodEnvironment env)
    {
        return MethodWrapperEmitter.BuildProtocolMethodDeclaration(methodDecl, env);
    }

    /// <summary>
    /// Builds the Swift protocol member declaration for a property getter.
    /// Format: "var {name}: {swiftType} { get }"
    /// </summary>
    internal static string BuildPropertyGetterMemberDeclaration(string propertyName, TypeSpec propertyTypeSpec)
    {
        var propertySwiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(propertyTypeSpec);
        return $"var {propertyName}: {propertySwiftType} {{ get }}";
    }

    /// <summary>
    /// Builds the Swift protocol member declaration for a constructor.
    /// Format: "init{?}({params}){throwsClause}"
    /// </summary>
    internal static string BuildConstructorMemberDeclaration(
        MethodDecl methodDecl,
        ModuleDecl moduleDecl,
        bool isFailable,
        bool throws)
    {
        var initParams = new List<string>();
        var keptArgs = methodDecl.CSSignature.Skip(1).ToList();

        for (int i = 0; i < keptArgs.Count; i++)
        {
            var arg = keptArgs[i];
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            if (arg.SwiftTypeSpec.IsEmptyTuple)
                continue;

            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec);
            var label = arg.Name switch
            {
                var n when SwiftBuilder.IsAutoGeneratedArgName(n) => "_",
                var n when n.StartsWith("_") => n.Substring(1),
                var n when string.IsNullOrEmpty(n) => "_",
                var n => n
            };
            initParams.Add($"{label}: {swiftType}");
        }

        var paramString = string.Join(", ", initParams);
        var throwsClause = throws ? " throws" : "";
        var failableQ = isFailable ? "?" : "";

        return $"init{failableQ}({paramString}){throwsClause}";
    }
}
