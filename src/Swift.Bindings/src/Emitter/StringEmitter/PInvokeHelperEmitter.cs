// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Context for collecting P/Invoke declarations that need to be emitted in a helper class.
/// Used for generic types where DllImport cannot appear directly inside the generic class (CS7042).
/// </summary>
public class PInvokeHelperContext
{
    /// <summary>
    /// The name of the helper class that will contain the P/Invoke declarations.
    /// </summary>
    public string HelperClassName { get; }

    /// <summary>
    /// The list of generic type parameter names (T0, T1, etc.) from the containing generic type.
    /// </summary>
    public IReadOnlyList<string> GenericTypeParameters { get; }

    /// <summary>
    /// The collected P/Invoke declarations.
    /// </summary>
    public List<PInvokeDeclaration> Declarations { get; } = new();

    /// <summary>
    /// Creates a new P/Invoke helper context for a generic type.
    /// </summary>
    /// <param name="typeName">The name of the containing generic type.</param>
    /// <param name="genericTypeParameters">The generic type parameter names (T0, T1, etc.).</param>
    public PInvokeHelperContext(string typeName, IReadOnlyList<string> genericTypeParameters)
    {
        HelperClassName = $"{typeName}_PInvoke";
        GenericTypeParameters = genericTypeParameters;
    }

    /// <summary>
    /// Creates a P/Invoke helper context from a type declaration if it's generic.
    /// Returns null for non-generic types.
    /// </summary>
    /// <param name="typeDecl">The type declaration.</param>
    /// <returns>A new context for generic types, null otherwise.</returns>
    public static PInvokeHelperContext? CreateIfGeneric(TypeDecl typeDecl)
    {
        if (!typeDecl.IsGeneric)
            return null;

        var typeParams = typeDecl.GenericParameters
            .Select((_, i) => $"T{i}")
            .ToList();

        // Use qualified name (e.g., "Outer_Inner") to avoid helper class name collisions
        // when deferred helpers from different parent types share the same simple name.
        return new PInvokeHelperContext(GetQualifiedTypeName(typeDecl), typeParams);
    }

    /// <summary>
    /// Builds a qualified type name by walking the parent type chain.
    /// For nested types, produces "Parent_Child" to ensure unique helper class names
    /// when multiple nested types with the same simple name exist under different parents.
    /// </summary>
    private static string GetQualifiedTypeName(TypeDecl typeDecl)
    {
        var parts = new List<string>();
        BaseDecl? current = typeDecl;
        while (current is TypeDecl td)
        {
            parts.Add(td.Name);
            current = td.ParentDecl;
        }

        if (parts.Count <= 1)
            return typeDecl.Name;

        parts.Reverse();
        return string.Join("_", parts);
    }

    /// <summary>
    /// Adds a P/Invoke declaration to the context.
    /// </summary>
    /// <param name="declaration">The P/Invoke declaration to add.</param>
    public void AddDeclaration(PInvokeDeclaration declaration)
    {
        // Deduplicate by method name to avoid duplicate P/Invoke declarations
        // (e.g., multiple failable inits in the same type share PInvokesForSwiftOptional_MetadataAccessor)
        if (Declarations.Any(d => d.MethodName == declaration.MethodName))
            return;
        Declarations.Add(declaration);
    }

    /// <summary>
    /// Gets the additional TypeMetadata parameters needed for P/Invoke declarations in a generic type.
    /// These parameters allow the non-generic helper class to receive the type metadata at runtime.
    /// </summary>
    /// <returns>A list of parameter strings like "TypeMetadata t0Metadata".</returns>
    public IReadOnlyList<string> GetMetadataParameterDeclarations()
    {
        return GenericTypeParameters
            .Select(t => $"TypeMetadata {t.ToLowerInvariant()}Metadata")
            .ToList();
    }

    /// <summary>
    /// Gets the argument list for passing type metadata to the helper class methods.
    /// </summary>
    /// <returns>A list of argument strings like "SwiftObjectHelper&lt;T0&gt;.GetTypeMetadata()".</returns>
    public IReadOnlyList<string> GetMetadataArgumentList()
    {
        return GenericTypeParameters
            .Select(t => $"SwiftObjectHelper<{t}>.GetTypeMetadata()")
            .ToList();
    }

    /// <summary>
    /// Emits the helper class with all collected P/Invoke declarations.
    /// </summary>
    /// <param name="csWriter">The C# code writer.</param>
    public void EmitHelperClass(CSharpWriter csWriter)
    {
        if (Declarations.Count == 0)
            return;

        csWriter.WriteLine($"internal static partial class {HelperClassName}");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        foreach (var decl in Declarations)
        {
            decl.Emit(csWriter);
            csWriter.WriteLine();
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();
    }
}

/// <summary>
/// Represents a P/Invoke declaration that will be emitted in a helper class.
/// </summary>
public class PInvokeDeclaration
{
    /// <summary>
    /// The library path for DllImport.
    /// </summary>
    public required string LibraryPath { get; init; }

    /// <summary>
    /// The entry point (mangled Swift symbol name).
    /// </summary>
    public required string EntryPoint { get; init; }

    /// <summary>
    /// The P/Invoke method name.
    /// </summary>
    public required string MethodName { get; init; }

    /// <summary>
    /// The return type of the P/Invoke method.
    /// </summary>
    public required string ReturnType { get; init; }

    /// <summary>
    /// The parameter list string for the P/Invoke method.
    /// </summary>
    public required string ParametersString { get; init; }

    /// <summary>
    /// Whether this P/Invoke is for an async method (always returns void).
    /// </summary>
    public bool IsAsync { get; init; }

    /// <summary>
    /// Additional TypeMetadata parameters for generic type support.
    /// </summary>
    public IReadOnlyList<string>? MetadataParameters { get; init; }

    /// <summary>
    /// Emits the P/Invoke declaration.
    /// </summary>
    /// <param name="csWriter">The C# code writer.</param>
    public void Emit(CSharpWriter csWriter)
    {
        csWriter.WriteLine("[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]");
        csWriter.WriteLine($"[LibraryImport(\"{LibraryPath}\", EntryPoint = \"{EntryPoint}\")]");

        var returnTypeStr = IsAsync ? "void" : ReturnType;
        if (MarshallingHelpers.IsBoolType(returnTypeStr))
            csWriter.WriteLine("[return: MarshalAs(UnmanagedType.U1)]");
        var paramsStr = ParametersString;

        // Add metadata parameters if present
        if (MetadataParameters != null && MetadataParameters.Count > 0)
        {
            var metadataParams = string.Join(", ", MetadataParameters);
            if (!string.IsNullOrEmpty(paramsStr))
                paramsStr = $"{paramsStr}, {metadataParams}";
            else
                paramsStr = metadataParams;
        }

        csWriter.WriteLine($"internal static partial {returnTypeStr} {MethodName}({paramsStr});");
    }
}
