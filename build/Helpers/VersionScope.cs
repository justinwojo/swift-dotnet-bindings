// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nuke.Common.IO;

/// <summary>
/// Temporarily stamps version numbers in project files for NuGet packaging.
/// Restores originals on Dispose (even on exception).
/// Replaces the backup-sed-restore-on-trap pattern in pack-all.sh.
/// </summary>
public sealed class VersionScope : IDisposable
{
    private readonly Dictionary<string, byte[]> _originals = new();

    public VersionScope(string version, AbsolutePath repoRoot)
    {
        var files = new[]
        {
            repoRoot / "src" / "Swift.Runtime" / "src" / "Swift.Runtime.csproj",
            repoRoot / "src" / "Swift.Bindings.Sdk" / "Swift.Bindings.Sdk.csproj",
            repoRoot / "src" / "Swift.Bindings.Templates" / "Swift.Bindings.Templates.csproj",
            repoRoot / "src" / "Swift.Bindings.Sdk" / "Sdk" / "Sdk.props",
            repoRoot / "src" / "Swift.Bindings.Templates" / "content" / "swift-binding" / "ProjectName.csproj",
            repoRoot / "src" / "Swift.Bindings.Templates" / "content" / "swift-binding" / ".template.config" / "template.json",
            repoRoot / "src" / "Swift.Bindings" / "src" / "Emitter" / "BindingProjectEmitter.cs",
        };

        foreach (var file in files)
        {
            if (!File.Exists(file))
                throw new FileNotFoundException($"Version file not found: {file}");
            _originals[file] = File.ReadAllBytes(file);
        }

        // Apply version stamps
        StampPackageVersion(files[0], version); // Runtime
        StampPackageVersion(files[1], version); // SDK
        StampPackageVersion(files[2], version); // Templates
        StampSdkProps(files[3], version);       // _SwiftBindingSdkVersion + SwiftRuntimeVersion
        StampTemplateSdk(files[4], version);    // Sdk="SwiftBindings.Sdk/..."
        StampTemplateJson(files[5], version);   // template.json sdkVersion symbol
        StampGeneratorDefault(files[6], version); // DefaultSwiftRuntimeVersion constant
    }

    public void Dispose()
    {
        Exception? firstError = null;
        foreach (var (file, bytes) in _originals)
        {
            try
            {
                File.WriteAllBytes(file, bytes);
            }
            catch (Exception ex)
            {
                firstError ??= ex;
            }
        }
        if (firstError != null)
            throw new AggregateException("Failed to restore one or more version files", firstError);
    }

    private static void StampPackageVersion(string file, string version)
    {
        var content = File.ReadAllText(file);
        content = Regex.Replace(content,
            @"<PackageVersion>[^<]*</PackageVersion>",
            $"<PackageVersion>{version}</PackageVersion>");
        File.WriteAllText(file, content);
    }

    private static void StampSdkProps(string file, string version)
    {
        var content = File.ReadAllText(file);
        content = Regex.Replace(content,
            @"<_SwiftBindingSdkVersion>[^<]*</_SwiftBindingSdkVersion>",
            $"<_SwiftBindingSdkVersion>{version}</_SwiftBindingSdkVersion>");
        content = Regex.Replace(content,
            @"(<SwiftRuntimeVersion Condition=""[^""]*"">)[^<]*(</SwiftRuntimeVersion>)",
            $"${{1}}{version}${{2}}");
        File.WriteAllText(file, content);
    }

    private static void StampTemplateSdk(string file, string version)
    {
        var content = File.ReadAllText(file);
        content = Regex.Replace(content,
            @"Sdk=""SwiftBindings\.Sdk/[^""]*""",
            $@"Sdk=""SwiftBindings.Sdk/{version}""");
        File.WriteAllText(file, content);
    }

    private static void StampTemplateJson(string file, string version)
    {
        // Update only the sdkVersion symbol's defaultValue and replaces
        var node = JsonNode.Parse(File.ReadAllText(file))!;
        var sdk = node["symbols"]!["sdkVersion"]!;
        sdk["defaultValue"] = version;
        sdk["replaces"] = version;
        File.WriteAllText(file, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static void StampGeneratorDefault(string file, string version)
    {
        var content = File.ReadAllText(file);
        content = Regex.Replace(content,
            @"DefaultSwiftRuntimeVersion\s*=\s*""[^""]*""",
            $@"DefaultSwiftRuntimeVersion = ""{version}""");
        File.WriteAllText(file, content);
    }
}
