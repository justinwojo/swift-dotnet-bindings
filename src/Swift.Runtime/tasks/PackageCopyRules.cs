// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using BindingsGeneration;
using SwiftBindings.TransitionGate;

namespace SwiftBindings.Tasks;

/// <summary>The pack-time and merge-time rules of a package copy, free of MSBuild.</summary>
public static class PackageCopyRules
{
    /// <summary>
    /// The SDK a copy is labelled and declared by, from its assembly <paramref name="image"/>: the SDK
    /// its wrapper identities record it was generated from, else the SDK the SDK's build recorded it
    /// was built against (<see cref="SwiftWrapperIdentityFormat.PlatformSdkAttributeKey"/>), else
    /// <paramref name="selected"/>, the SDK selected at pack time. A pack run without a build
    /// (<c>--no-build</c>) after another Xcode was selected therefore still labels the copy by the SDK
    /// its surface came from. <paramref name="errors"/> holds why the assembly's records cannot name
    /// one SDK; <paramref name="message"/> notes a recorded SDK that differs from the selected one.
    /// </summary>
    public static string CopySdk(byte[] image, string selected, out List<string> errors, out string? message)
    {
        errors = new List<string>();
        message = null;
        const string rebuild = "Rebuild the binding clean (delete its bin/ and obj/) and pack again.";
        var read = SwiftWrapperIdentityCheck.Read(image);
        foreach (var problem in read.Problems)
            errors.Add($"the binding's record of the SDK it was generated from does not read: {problem}. {rebuild}");
        var generated = read.Identities.Select(i => i.Sdk).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (generated.Count > 1)
            errors.Add($"the binding's modules were generated from different SDKs ({string.Join(", ", generated)}), so the copy has no one SDK to be labelled by. {rebuild}");
        string recorded;
        string how;
        if (generated.Count == 1)
        {
            recorded = generated[0];
            how = "generated from";
        }
        else if (generated.Count == 0 && SwiftWrapperIdentityCheck.ReadPlatformSdks(image) is { Length: > 0 } built)
        {
            if (built.Length > 1)
            {
                errors.Add($"the binding records that it was built against different SDKs ({string.Join(", ", built)}), so the copy has no one SDK to be labelled by. {rebuild}");
                return selected;
            }
            recorded = built[0];
            how = "built against";
        }
        else
            return selected;
        if (recorded != selected.Trim())
            message = $"the binding was {how} the {recorded} SDK; the SDK selected now is {(selected.Trim().Length > 0 ? selected.Trim() : "unknown")}. "
                      + $"The copy is labelled and declared by the SDK it was {how}.";
        return recorded;
    }


    /// <summary>
    /// SWIFTBIND137: the copy <paramref name="packTfm"/> of an Apple-framework binding must be labelled
    /// with the platform SDK it was generated from: the same major version, and no newer. A label below
    /// the SDK's major (a versionless .NET 10 library defaults to <c>26.0</c> on Xcode 27.2, whose SDK is
    /// 27.2) would let an app on the older SDK select a copy whose API and wrapper need the newer one.
    /// Only Apple-framework bindings are checked: a library binding's surface is the library's, not the
    /// SDK's, and the documented flow packs a versionless library binding on any supported toolchain.
    /// Returns the refusal, or null.
    /// </summary>
    public static string? CheckLabel(string packageId, string packTfm, string targetKind, string platformSdkVersion)
    {
        if (!string.Equals(targetKind, "AppleFramework", StringComparison.Ordinal))
            return null;
        if (!PackageCopyCheck.TryParse(packTfm, out var net, out var platform, out var label) || label is null)
            return $"{packageId}: the pack target framework '{packTfm}' names no platform version, so the copy cannot be labelled. Use a versioned TargetFramework.";
        if (!Version.TryParse(platformSdkVersion.Trim(), out var sdk))
            return $"{packageId} {packTfm}: the {platform} platform SDK version could not be determined (got '{platformSdkVersion.Trim()}'), so the copy's label cannot be checked against the SDK it was generated from. Check that Xcode is selected (xcode-select -p, or DEVELOPER_DIR).";
        if (label.Major == sdk.Major && label <= sdk)
            return null;
        var exact = $"net{net.ToString(2)}-{platform}{sdk.Major}.{Math.Max(sdk.Minor, 0)}";
        return $"{packageId} {packTfm}: this copy is labelled {platform} {label.ToString(2)}, but its API surface and wrapper were generated from the {platform} {sdk.ToString(2)} SDK. "
               + $"An app on an older toolchain whose platform version is at least {label.ToString(2)} would select it and compile against API its SDK lacks. "
               + $"Pack this copy with an explicit TargetFramework that names the SDK it is built from (for example {exact}), or on the toolchain whose SDK matches the label "
               + "(the oldest .NET 10 copy, labelled 26.0, is built with Xcode 26).";
    }

    /// <summary>
    /// SWIFTBIND138: what is wrong with the native references of a copy's consumer targets: each must
    /// point into <c>native/&lt;packTfm&gt;/</c>, and none into <c>runtimes/</c> or another copy. The
    /// text is scanned for package-relative paths (a property or a condition can name one too), and
    /// every <c>NativeReference</c> is resolved as NuGet lays the file out, from
    /// <c>buildTransitive/&lt;packTfm&gt;/</c>, so a reference the scan cannot see (through a property,
    /// relative to the consuming project, or absolute) is refused as well.
    /// </summary>
    public static IEnumerable<string> CheckTargetsReferences(string packTfm, string targets,
        string nativePayload = BindingsGeneration.PackageCopyDeclarationEmitter.NativePayloadNative)
    {
        var scanned = ScanTargetsText(packTfm, targets).ToList();
        if (scanned.Count > 0)
            return scanned;
        // A copy that declares no native/<tfm>/ payload has nothing there for its targets to reference.
        if (nativePayload != BindingsGeneration.PackageCopyDeclarationEmitter.NativePayloadNative)
            return NativeReferenceIncludes(targets, out var unreadable) is { Count: > 0 } includes
                ? [$"reference {string.Join(", ", includes)}, but the copy declares its native payload '{nativePayload}', so it has no native/{packTfm}/ for them to reference"]
                : unreadable is not null ? [unreadable] : [];
        return ResolveNativeReferences(packTfm, targets);
    }

    /// <summary>Every <c>NativeReference</c> include of <paramref name="targets"/>; null with a reason when it does not read.</summary>
    private static List<string> NativeReferenceIncludes(string targets, out string? unreadable)
    {
        unreadable = null;
        try
        {
            return XDocument.Parse(targets).Descendants().Where(e => e.Name.LocalName == "NativeReference")
                .SelectMany(e => ((string?)e.Attribute("Include") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToList();
        }
        catch (XmlException ex)
        {
            unreadable = $"do not read as an MSBuild project ({ex.Message})";
            return [];
        }
    }

    private static IEnumerable<string> ScanTargetsText(string packTfm, string targets)
    {
        var text = targets.Replace('\\', '/');
        if (text.Contains("../runtimes/", StringComparison.OrdinalIgnoreCase))
            yield return "reference natives under runtimes/, the layout of an earlier SwiftBindings.Sdk";
        var index = 0;
        while ((index = text.IndexOf("../native/", index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            index += "../native/".Length;
            var end = text.IndexOfAny(['"', '\'', '<', ';', ' ', '\n', '\r', '\t'], index);
            var segments = Unescape(end < 0 ? text[index..] : text[index..end]).Replace('\\', '/').Split('/');
            if (!string.Equals(segments[0], packTfm, StringComparison.OrdinalIgnoreCase))
            {
                yield return $"reference native/{segments[0]}/, another copy's natives";
                yield break;
            }
            if (Array.IndexOf(segments, "..") >= 0)
            {
                yield return $"reference native/{string.Join("/", segments)}, which leaves the copy's native/{packTfm}/";
                yield break;
            }
        }
    }

    private const string ThisFileDirectory = "$(MSBuildThisFileDirectory)";

    /// <summary>Each <c>NativeReference</c> of <paramref name="targets"/>, resolved from <c>buildTransitive/&lt;packTfm&gt;/</c>.</summary>
    private static IEnumerable<string> ResolveNativeReferences(string packTfm, string targets)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(targets);
        }
        catch (XmlException ex)
        {
            return [$"do not read as an MSBuild project ({ex.Message})"];
        }
        var problems = new List<string>();
        var own = $"native/{packTfm}/";
        foreach (var element in document.Descendants().Where(e => e.Name.LocalName == "NativeReference"))
        {
            foreach (var include in ((string?)element.Attribute("Include") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var path = include.Replace('\\', '/');
                if (!path.StartsWith(ThisFileDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"reference {include}, which is not a path from the targets' own directory into the copy's {own}");
                    continue;
                }
                var rest = path[ThisFileDirectory.Length..];
                if (rest.Contains("$(", StringComparison.Ordinal) || rest.Contains("@(", StringComparison.Ordinal) || rest.Contains("%(", StringComparison.Ordinal))
                {
                    problems.Add($"reference {include}, which goes through a property or an item the pack cannot resolve into the copy's {own}");
                    continue;
                }
                var resolved = Resolve($"buildTransitive/{packTfm}/{Unescape(rest).Replace('\\', '/')}");
                if (resolved is null || !resolved.StartsWith(own, StringComparison.OrdinalIgnoreCase) || resolved.Length == own.Length)
                    problems.Add($"reference {include}, which resolves to {resolved ?? "a path outside the package"}, outside the copy's {own}");
            }
        }
        return problems;
    }

    /// <summary><paramref name="value"/> with MSBuild's <c>%XX</c> escapes decoded, as MSBuild reads an item's path.</summary>
    private static string Unescape(string value)
    {
        if (!value.Contains('%'))
            return value;
        var decoded = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 2 < value.Length && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            {
                decoded.Append((char)Convert.ToInt32(value.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                decoded.Append(value[i]);
            }
        }
        return decoded.ToString();
    }

    /// <summary><paramref name="path"/> with its <c>.</c> and <c>..</c> segments applied; null when it leaves the package root.</summary>
    private static string? Resolve(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment is "" or ".")
                continue;
            if (segment == "..")
            {
                if (segments.Count == 0)
                    return null;
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return string.Join("/", segments) + (path.EndsWith('/') ? "/" : "");
    }
}
