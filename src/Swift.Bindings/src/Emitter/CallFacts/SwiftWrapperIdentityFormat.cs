// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BindingsGeneration;

/// <summary>
/// The wrapper identity: the one value that ties a generated binding's managed assembly to the native
/// wrapper it was generated with. Generation writes it twice, as an <c>AssemblyMetadataAttribute</c>
/// the assembly compiles (<see cref="AttributeKey"/>) and as an exported data symbol
/// <c>SBWI_&lt;module&gt;</c> every slice of the wrapper links (<see cref="NativeAssembly"/>), so a
/// packaged or embedded wrapper of another copy, toolchain or build is told apart from the assembly's own.
/// </summary>
/// <remarks>
/// <para>
/// The value is fixed when the module is generated, before its wrapper is compiled: it names the
/// module, the wrapper library, the copy (the full target framework the binding packs under), the
/// platform SDK the surface was generated from, the generator, and the SHA-256 of the wrapper source
/// as generated. Nothing the wrapper compile settles later (stripping, bridge routing, a relink or its
/// rollback) changes it, so every wrapper a build links carries the value its assembly embeds. This is
/// unlike the transition manifest's hash (<see cref="SwiftTransitionManifestFormat"/>), which routing
/// rewrites after the first wrapper compile and which only a wrapper that defines bridges carries.
/// </para>
/// <para>
/// The SDK it records is also the binding's own record of the SDK it was generated on, which the pack's
/// label check (SWIFTBIND137) reads instead of the SDK selected when it packs. This file is
/// self-contained (base class library only): the generator writes the identity with it, and the
/// build's gates and the SDK's MSBuild tasks read it with it.
/// </para>
/// </remarks>
public static class SwiftWrapperIdentityFormat
{
    /// <summary>The identity schema this file writes and reads.</summary>
    public const int Schema = 1;

    /// <summary>The <c>AssemblyMetadataAttribute</c> key of an identity.</summary>
    public const string AttributeKey = "SwiftBindings.WrapperIdentity";

    /// <summary>
    /// The <c>AssemblyMetadataAttribute</c> key of the platform SDK version a binding's assembly was
    /// built against, which the SDK's build stamps on every binding (<c>_RecordSwiftBindingPlatformSdk</c>
    /// in Sdk.targets). The pack's label check (SWIFTBIND137) reads it for a copy whose assembly carries
    /// no wrapper identity (a pure Objective-C binding), so a copy packed after another Xcode was
    /// selected is still labelled by the SDK it was built with.
    /// </summary>
    public const string PlatformSdkAttributeKey = "SwiftBindings.PlatformSdk";

    /// <summary>The prefix of the native data symbol holding a module's identity.</summary>
    public const string NativeSymbolPrefix = "SBWI_";

    /// <summary>The suffix of the assembly file (one for every architecture: it holds only data).</summary>
    public const string AssemblyFileSuffix = ".WrapperIdentity.s";

    /// <summary>The suffix of the C# file holding the identity's attribute.</summary>
    public const string AttributeFileSuffix = ".WrapperIdentity.g.cs";

    /// <summary>The assembly file of the module generated under <paramref name="namespace"/>.</summary>
    public static string AssemblyFileName(string @namespace) => @namespace + AssemblyFileSuffix;

    /// <summary>The attribute file of the module generated under <paramref name="namespace"/>.</summary>
    public static string AttributeFileName(string @namespace) => @namespace + AttributeFileSuffix;

    /// <summary>The native data symbol (without the Mach-O underscore) holding <paramref name="module"/>'s identity.</summary>
    public static string NativeSymbol(string module)
    {
        var name = new StringBuilder(NativeSymbolPrefix);
        foreach (var c in module)
            name.Append(c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' ? c : '_');
        return name.ToString();
    }

    /// <summary>The SHA-256 of a wrapper source's text, spelled <c>sha256:&lt;hex&gt;</c>.</summary>
    public static string SourceDigest(string source) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();

    /// <summary>
    /// The identity of a module's wrapper, as both sides spell it. Every field is required; a field
    /// must not hold <c>;</c>, <c>=</c>, <c>"</c> or <c>\</c>, which the spelling and its C# and
    /// assembly strings reserve.
    /// </summary>
    public static string Compose(SwiftWrapperIdentity identity)
    {
        var fields = new (string Name, string Value)[]
        {
            ("module", identity.Module), ("library", identity.Library), ("copy", identity.Copy),
            ("sdk", identity.Sdk), ("generator", identity.Generator), ("source", identity.Source),
        };
        foreach (var (name, value) in fields)
            if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(Reserved) >= 0 || value.Any(char.IsControl))
                throw new ArgumentException($"the wrapper identity's {name} '{value}' is empty or holds a reserved character");
        return $"schema={Schema};" + string.Join(";", fields.Select(f => $"{f.Name}={f.Value}"));
    }

    private static readonly char[] Reserved = [';', '=', '"', '\\'];

    /// <summary>
    /// Reads an identity's spelling. Returns null, with why, when it is not exactly the fields
    /// <see cref="Compose"/> writes, in its order, under a schema this reader knows.
    /// </summary>
    public static SwiftWrapperIdentity? Parse(string? value, out string? error)
    {
        error = null;
        var parts = (value ?? "").Split(';');
        string[] names = ["schema", "module", "library", "copy", "sdk", "generator", "source"];
        if (parts.Length != names.Length)
        {
            error = $"the wrapper identity '{value}' does not have the fields {string.Join(", ", names)}";
            return null;
        }
        var values = new string[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            var separator = parts[i].IndexOf('=');
            if (separator <= 0 || parts[i][..separator] != names[i] || separator == parts[i].Length - 1)
            {
                error = $"the wrapper identity '{value}' does not have the fields {string.Join(", ", names)}";
                return null;
            }
            values[i] = parts[i][(separator + 1)..];
            if (string.IsNullOrWhiteSpace(values[i]) || values[i].IndexOfAny(Reserved) >= 0 || values[i].Any(char.IsControl))
            {
                error = $"the wrapper identity '{value}' holds a {names[i]} that is empty or holds a reserved character";
                return null;
            }
        }
        if (values[0] != Schema.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            error = $"the wrapper identity '{value}' has schema {values[0]}; this reader reads {Schema}";
            return null;
        }
        return new SwiftWrapperIdentity(values[1], values[2], values[3], values[4], values[5], values[6]);
    }

    /// <summary>The C# file declaring <paramref name="value"/> as the assembly's identity of its module's wrapper.</summary>
    public static string AttributeSource(string value) =>
        "// <auto-generated>\n" +
        "// The identity of the native wrapper this binding was generated with. Every slice of that wrapper\n" +
        "// exports the same value, so a wrapper of another copy or build is refused where the binding is\n" +
        "// packed and where an app links it. Written by Swift.Bindings with the wrapper source; do not edit.\n" +
        "// </auto-generated>\n" +
        $"[assembly: global::System.Reflection.AssemblyMetadataAttribute(\"{AttributeKey}\", \"{value}\")]\n";

    /// <summary>
    /// The assembly that exports <c>SBWI_&lt;module&gt;</c> holding <paramref name="value"/>. It holds only
    /// data, so the one file assembles for every architecture. The symbol is a constant, kept by the
    /// linker even if nothing references it.
    /// </summary>
    public static string NativeAssembly(string module, string value)
    {
        var symbol = NativeSymbol(module);
        return "/* The identity of this wrapper: the value its managed binding embeds (SwiftBindings.WrapperIdentity). */\n" +
               ".section __TEXT,__const\n" +
               $".globl _{symbol}\n" +
               $".no_dead_strip _{symbol}\n" +
               $"_{symbol}:\n" +
               $"  .asciz \"{value}\"\n" +
               ".subsections_via_symbols\n";
    }
}

/// <summary>A read wrapper identity (<see cref="SwiftWrapperIdentityFormat.Parse"/>).</summary>
/// <param name="Module">The Swift module the wrapper wraps.</param>
/// <param name="Library">The wrapper library's name (its framework binary).</param>
/// <param name="Copy">The full target framework of the copy the binding was generated for.</param>
/// <param name="Sdk">The version of the platform SDK the binding was generated from.</param>
/// <param name="Generator">The generator that wrote it, without build metadata.</param>
/// <param name="Source">The SHA-256 of the wrapper source as generated.</param>
public sealed record SwiftWrapperIdentity(string Module, string Library, string Copy, string Sdk, string Generator, string Source)
{
    /// <summary>The identity's spelling.</summary>
    public string Value => SwiftWrapperIdentityFormat.Compose(this);
}
