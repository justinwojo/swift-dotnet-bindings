// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using BindingsGeneration;

namespace SwiftBindings.TransitionGate;

/// <summary>An assembly's wrapper identities, as read, and the attributes that did not read as one.</summary>
public sealed record WrapperIdentities(ImmutableArray<SwiftWrapperIdentity> Identities, ImmutableArray<string> Problems);

/// <summary>
/// Checks that the native wrapper an assembly is packed or linked with is the one it was generated
/// with (<see cref="SwiftWrapperIdentityFormat"/>): every slice of every image of the wrapper library
/// an identity names must export that module's <c>SBWI_</c> symbol holding exactly the assembly's value.
/// </summary>
/// <remarks>
/// A wrapper of another copy of the same package (another toolchain's SDK or target framework), of
/// another build, or from a generator that did not stamp one is refused. An assembly that records no
/// identity (a hand-written table, a binding generated before identities, a module with no wrapper) is
/// not checked here; a library none of the images provide is reported unverified rather than refused,
/// since what is missing then is the wrapper itself, which the app's load fails on loudly.
/// </remarks>
public static class SwiftWrapperIdentityCheck
{
    /// <summary>The wrapper identities <paramref name="image"/> declares.</summary>
    public static WrapperIdentities Read(byte[] image)
    {
        var identities = ImmutableArray.CreateBuilder<SwiftWrapperIdentity>();
        var problems = ImmutableArray.CreateBuilder<string>();
        try
        {
            using var pe = new PEReader(new MemoryStream(image, writable: false));
            if (!pe.HasMetadata)
                return new([], []);
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly)
                return new([], []);
            foreach (var handle in md.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = md.GetCustomAttribute(handle);
                if (SwiftTransitionManifestReader.AttributeTypeName(md, attribute) != SwiftTransitionManifestReader.MetadataAttribute)
                    continue;
                var value = attribute.DecodeValue(SwiftTransitionScanner.AttributeTypeProvider.Instance);
                if (value.FixedArguments.Length != 2 || value.FixedArguments[0].Value as string != SwiftWrapperIdentityFormat.AttributeKey)
                    continue;
                if (SwiftWrapperIdentityFormat.Parse(value.FixedArguments[1].Value as string, out var error) is { } identity)
                    identities.Add(identity);
                else
                    problems.Add(error!);
            }
        }
        catch (BadImageFormatException)
        {
            return new([], []);
        }
        var duplicated = identities.GroupBy(i => i.Module, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        foreach (var module in duplicated)
            problems.Add($"it declares more than one wrapper identity for {module}");
        return new(identities.ToImmutable(), problems.ToImmutable());
    }

    /// <summary>
    /// The problems of <paramref name="natives"/> against the identities <paramref name="assembly"/>
    /// declares, and the wrapper libraries no image of which was found to check.
    /// </summary>
    public static (ImmutableArray<string> Problems, ImmutableArray<string> Unverified) Check(
        PackagedAssembly assembly, IEnumerable<PackagedNative> natives)
    {
        var read = Read(assembly.Image);
        var problems = new List<string>(read.Problems);
        var unverified = new List<string>();
        var offered = natives.ToList();
        foreach (var identity in read.Identities)
        {
            var images = offered.Where(n => n.Library == identity.Library).ToList();
            if (images.Count == 0)
            {
                unverified.Add(identity.Library);
                continue;
            }
            var symbol = SwiftWrapperIdentityFormat.NativeSymbol(identity.Module);
            foreach (var native in images)
            {
                ImmutableArray<MachOSlice> slices;
                try
                {
                    slices = MachOSymbols.Read(native.Image);
                }
                catch (BadImageFormatException ex)
                {
                    problems.Add($"{native.DisplayPath}: {ex.Message}");
                    continue;
                }
                foreach (var slice in slices)
                {
                    var where = $"{native.DisplayPath} ({slice.Architecture})";
                    var held = slice.CString(symbol);
                    if (held is null)
                        problems.Add($"{where} does not export {symbol}, so it is not the wrapper {identity.Module} was generated with "
                                     + $"(copy {identity.Copy}, {identity.Sdk} SDK, wrapper source {identity.Source})");
                    else if (held != identity.Value)
                        problems.Add($"{where} is not the wrapper {identity.Module} was generated with: it was generated as {Describe(held)}, "
                                     + $"the assembly as {Describe(identity.Value)}");
                }
            }
        }
        return (problems.Distinct(StringComparer.Ordinal).ToImmutableArray(), unverified.Distinct(StringComparer.Ordinal).ToImmutableArray());
    }

    /// <summary>An identity's spelling, named by its copy, SDK, generator and source when it reads as one.</summary>
    private static string Describe(string value) =>
        SwiftWrapperIdentityFormat.Parse(value, out _) is { } identity
            ? $"copy {identity.Copy} from the {identity.Sdk} SDK by {identity.Generator}, wrapper source {identity.Source}"
            : $"'{value}'";
}
