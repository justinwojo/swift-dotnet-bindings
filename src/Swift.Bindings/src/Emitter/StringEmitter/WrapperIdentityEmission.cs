// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// What a generation run knows about the copy it generates, which every module's wrapper identity
/// (<see cref="SwiftWrapperIdentityFormat"/>) records: the copy's full target framework, the platform
/// SDK version the surface is generated from, and the generator.
/// </summary>
public sealed record WrapperIdentityContext(string Copy, string Sdk, string Generator);

/// <summary>
/// Writes a module's wrapper identity beside its wrapper source: the data-only assembly file every
/// wrapper slice links (picked up with the thunk objects, <see cref="NativeThunkCompiler"/>) and the
/// C# file declaring the same value, which the binding compiles.
/// </summary>
public static class WrapperIdentityEmission
{
    /// <summary>
    /// The run's context, set by the generator command before it emits. Null (unit tests, and runs
    /// that cannot name their copy or SDK) writes no identity, and removes an earlier one. It flows
    /// with the run's execution context, so concurrent runs in one process (tests) do not share it.
    /// </summary>
    public static WrapperIdentityContext? Current
    {
        get => current.Value;
        set => current.Value = value;
    }

    private static readonly AsyncLocal<WrapperIdentityContext?> current = new();

    /// <summary>
    /// Writes the identity of <paramref name="module"/>'s wrapper generated from
    /// <paramref name="wrapperSource"/> under <paramref name="namespace"/> in <paramref name="directory"/>,
    /// and returns the C# attribute file's text; or removes both files and returns null when there is
    /// no context, no wrapper library or no wrapper source. The attribute file also carries
    /// <paramref name="declarationWitness"/>, the witness of the module's Swift declaration record
    /// (<see cref="SwiftDeclarationRecordFormat.Witness"/>), when there is one, and with no identity
    /// carries the witness alone: every module that has a record names it, so the merge can refuse a
    /// copy that lost it.
    /// </summary>
    public static string? Write(string directory, string @namespace, string module, string? library, string wrapperSource,
        string? declarationWitness = null)
    {
        var assemblyPath = Path.Combine(directory, SwiftWrapperIdentityFormat.AssemblyFileName(@namespace));
        var attributePath = Path.Combine(directory, SwiftWrapperIdentityFormat.AttributeFileName(@namespace));
        if (Current is not { } context || string.IsNullOrWhiteSpace(library) || string.IsNullOrWhiteSpace(wrapperSource))
        {
            if (File.Exists(assemblyPath))
                File.Delete(assemblyPath);
            // A module with no identity still names its record: the merge refuses a copy whose
            // assembly names a record it does not carry, whether or not the wrapper is identified.
            if (declarationWitness is not null)
                return WriteIfChanged(attributePath, SwiftDeclarationRecordFormat.AttributeSource(declarationWitness));
            if (File.Exists(attributePath))
                File.Delete(attributePath);
            return null;
        }
        var value = new SwiftWrapperIdentity(module, library, context.Copy, context.Sdk, context.Generator,
            SwiftWrapperIdentityFormat.SourceDigest(wrapperSource)).Value;
        var assembly = SwiftWrapperIdentityFormat.NativeAssembly(module, value);
        var attribute = SwiftWrapperIdentityFormat.AttributeSource(value)
                        + (declarationWitness is null ? "" : SwiftDeclarationRecordFormat.AttributeSource(declarationWitness));
        if (!File.Exists(assemblyPath) || File.ReadAllText(assemblyPath) != assembly)
            File.WriteAllText(assemblyPath, assembly);
        return WriteIfChanged(attributePath, attribute);
    }

    private static string WriteIfChanged(string path, string text)
    {
        if (!File.Exists(path) || File.ReadAllText(path) != text)
            File.WriteAllText(path, text);
        return text;
    }
}
