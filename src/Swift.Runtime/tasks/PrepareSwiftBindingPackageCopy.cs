// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using BindingsGeneration;
using Microsoft.Build.Framework;

namespace SwiftBindings.Tasks;

/// <summary>
/// Prepares one copy of a binding package at pack time: refuses a copy label an older toolchain's
/// apps could select while the copy carries a newer platform SDK's surface (SWIFTBIND137), refuses
/// consumer targets that reference natives outside the copy's own <c>native/&lt;tfm&gt;/</c>
/// (SWIFTBIND138), and writes the copy declaration packed beside the consumer targets
/// (<see cref="PackageCopyDeclarationEmitter"/>).
/// </summary>
public sealed class PrepareSwiftBindingPackageCopy : Microsoft.Build.Utilities.Task
{
    /// <summary>The producer label diagnostic.</summary>
    public const string LabelCode = "SWIFTBIND137";

    /// <summary>The copy layout diagnostic (shared with the merge).</summary>
    public const string LayoutCode = "SWIFTBIND138";

    [Required] public string PackageId { get; set; } = "";

    /// <summary>The version-qualified pack TFM: the copy's identity.</summary>
    [Required] public string PackTfm { get; set; } = "";

    /// <summary><c>AppleFramework</c> for a binding of an Apple SDK framework; anything else for a library binding.</summary>
    public string TargetKind { get; set; } = "";

    /// <summary>The platform SDK version the generator and wrapper used (<c>xcrun --show-sdk-version</c>).</summary>
    public string PlatformSdkVersion { get; set; } = "";

    /// <summary>The toolchain the copy is built on, as the build log names it.</summary>
    public string Toolchain { get; set; } = "";

    /// <summary>The SwiftBindings SDK (generator) version.</summary>
    public string Generator { get; set; } = "";

    /// <summary>The consumer targets packed into this copy's <c>buildTransitive/&lt;tfm&gt;/</c>.</summary>
    public string ConsumerTargetsFile { get; set; } = "";

    /// <summary>Where to write the copy declaration.</summary>
    [Required] public string DeclarationFile { get; set; } = "";

    public override bool Execute()
    {
        var label = PackageCopyRules.CheckLabel(PackageId, PackTfm, TargetKind, PlatformSdkVersion);
        if (label is not null)
            Log.LogError(null, LabelCode, null, null, 0, 0, 0, 0, "{0}", label);
        if (ConsumerTargetsFile.Length > 0 && File.Exists(ConsumerTargetsFile))
            foreach (var problem in PackageCopyRules.CheckTargetsReferences(PackTfm, File.ReadAllText(ConsumerTargetsFile)))
                Log.LogError(null, LayoutCode, null, ConsumerTargetsFile, 0, 0, 0, 0, "{0}",
                    $"{PackageId} {PackTfm}: the consumer targets {problem}. Each copy's targets must reference only its own native/{PackTfm}/. "
                    + "Delete the binding's obj/ and pack again so the targets are regenerated.");
        if (Log.HasLoggedErrors)
            return false;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(DeclarationFile))!);
        var text = PackageCopyDeclarationEmitter.Compose(PackageId, PackTfm, Toolchain, PlatformSdkVersion, Generator);
        if (!File.Exists(DeclarationFile) || File.ReadAllText(DeclarationFile) != text)
            File.WriteAllText(DeclarationFile, text);
        return true;
    }
}
