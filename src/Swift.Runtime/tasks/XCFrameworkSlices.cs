// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace SwiftBindings.Tasks;

/// <summary>One slice an XCFramework's <c>Info.plist</c> lists.</summary>
/// <param name="Kind">Its platform and variant (<c>ios</c>, <c>ios simulator</c>, <c>ios maccatalyst</c>).</param>
/// <param name="LibraryPath">The library in the slice's directory a build links (<c>Kit.framework</c>, <c>libKit.a</c>); null when unlisted.</param>
/// <param name="Architectures">The architectures the slice declares (<c>SupportedArchitectures</c>).</param>
public sealed record XCFrameworkSlice(string Kind, string? LibraryPath, IReadOnlySet<string> Architectures);

/// <summary>What an XCFramework's <c>Info.plist</c> says of its slices.</summary>
public static class XCFrameworkSlices
{
    /// <summary>
    /// Each slice the <c>Info.plist</c> <paramref name="plist"/> lists in <c>AvailableLibraries</c>, by
    /// its <c>LibraryIdentifier</c> (the slice's directory): the slices a build that references the
    /// XCFramework chooses from. An XML plist is read directly and a binary one through <c>plutil</c>;
    /// empty for a file that reads as neither.
    /// </summary>
    public static IReadOnlyDictionary<string, XCFrameworkSlice> Listed(byte[] plist) => Read.GetValue(plist, Parse);

    // A plist is read once however many slice paths ask about it.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], Dictionary<string, XCFrameworkSlice>> Read = new();

    private static Dictionary<string, XCFrameworkSlice> Parse(byte[] plist)
    {
        var slices = new Dictionary<string, XCFrameworkSlice>(StringComparer.Ordinal);
        var xml = plist.AsSpan().StartsWith("bplist"u8) ? BinaryAsXml(plist) : Encoding.UTF8.GetString(plist);
        if (xml is null)
            return slices;
        try
        {
            var root = XDocument.Parse(xml, LoadOptions.None).Root?.Element("dict");
            if (root is null || !Entries(root).TryGetValue("AvailableLibraries", out var libraries))
                return slices;
            foreach (var library in libraries.Elements("dict").Select(Entries))
            {
                if (!library.TryGetValue("LibraryIdentifier", out var id) || !library.TryGetValue("SupportedPlatform", out var platform))
                    continue;
                var kind = library.TryGetValue("SupportedPlatformVariant", out var variant) && variant.Value.Length > 0
                    ? $"{platform.Value} {variant.Value}"
                    : platform.Value;
                var architectures = library.TryGetValue("SupportedArchitectures", out var archs)
                    ? archs.Elements("string").Select(a => a.Value).ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
                slices[id.Value] = new XCFrameworkSlice(kind, library.TryGetValue("LibraryPath", out var path) && path.Value.Length > 0 ? path.Value : null, architectures);
            }
        }
        catch (System.Xml.XmlException)
        {
            // A malformed plist lists nothing.
        }
        return slices;

        static Dictionary<string, XElement> Entries(XElement dict)
        {
            var entries = new Dictionary<string, XElement>(StringComparer.Ordinal);
            var elements = dict.Elements().ToList();
            for (var i = 0; i + 1 < elements.Count; i++)
                if (elements[i].Name == "key")
                    entries[elements[i].Value] = elements[++i];
            return entries;
        }
    }

    /// <summary>
    /// The platform and variant of an XCFramework slice directory as <c>xcodebuild -create-xcframework</c>
    /// names it (<c>&lt;platform&gt;-&lt;architectures&gt;[-&lt;variant&gt;]</c>); null for any other name.
    /// </summary>
    public static string? KindFromName(string slice)
    {
        var segments = slice.Split('-');
        return segments.Length is 2 or 3 && segments[0].Length > 0 && segments[1].Length > 0
            ? segments.Length == 2 ? segments[0] : $"{segments[0]} {segments[2]}"
            : null;
    }

    private static string? BinaryAsXml(byte[] plist)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("/usr/bin/plutil", "-convert xml1 -o - -")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEndAsync();
            process.StandardInput.BaseStream.Write(plist);
            process.StandardInput.Close();
            process.WaitForExit();
            return process.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
