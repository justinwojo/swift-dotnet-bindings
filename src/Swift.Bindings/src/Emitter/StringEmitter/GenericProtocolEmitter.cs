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
    /// <param name="originAnchor">
    /// Identity of the member this dispatch protocol + conformance pair belongs to. Emitted as a
    /// <c>// SBW-ORIGIN:</c> anchor ahead of each of the two symbol-less blocks so a wrapper-compile
    /// diagnostic landing in either attributes to that member rather than the coarse module scope.
    /// </param>
    /// <param name="protocolConstraint">Optional protocol constraint (e.g., "AnyObject" for class-only protocols).</param>
    /// <param name="extensionAvailability">Optional merged availability annotations applied to the conformance extension. Conformance extensions are top-level decls and don't inherit the enclosing type's availability.</param>
    /// <returns>The generated protocol name (e.g., "_SBW_P_A1B2C3D4").</returns>
    internal static string EmitProtocolAndConformance(
        SwiftWriter swiftWriter,
        string prefix,
        string symbolName,
        string memberDeclaration,
        string moduleQualifiedName,
        ArtifactId originAnchor,
        string? protocolConstraint = null,
        IReadOnlyList<AvailabilityAnnotation>? extensionAvailability = null)
    {
        var protocolName = GetProtocolName(prefix, symbolName);
        var constraintClause = protocolConstraint != null ? $": {protocolConstraint} " : "";
        var extensionAvailPrefix = WrapperEmitterHelpers.BuildAvailabilityHeredocPrefix(
            extensionAvailability, string.Empty);
        // The anchor leads each block, ahead of any availability prefix, so the block head stays
        // byte-identical to the pre-anchor output (only the comment line is new) and the strip
        // fast-path removes the whole `[anchor .. @available]` preamble with the block it names.
        var anchor = OriginAnchorEmitter.Line(originAnchor);

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            {{anchor}}
            {{extensionAvailPrefix}}private protocol {{protocolName}}{{constraintClause}} {
                {{memberDeclaration}}
            }
            {{anchor}}
            {{extensionAvailPrefix}}extension {{moduleQualifiedName}}: {{protocolName}} {}
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
            // Bare external label via the shared recovery, so a label that genuinely begins with
            // '_' (e.g. _self) survives into the emitted Swift signature.
            var label = arg.Name switch
            {
                var n when SwiftBuilder.IsAutoGeneratedArgName(n) => "_",
                var n when string.IsNullOrEmpty(n) => "_",
                _ => NameProvider.RecoverSwiftArgumentLabel(arg)
            };
            initParams.Add($"{label}: {swiftType}");
        }

        var paramString = string.Join(", ", initParams);
        var throwsClause = throws ? " throws" : "";
        var failableQ = isFailable ? "?" : "";

        return $"init{failableQ}({paramString}){throwsClause}";
    }
}
