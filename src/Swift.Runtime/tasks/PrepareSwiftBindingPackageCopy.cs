// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Linq;
using BindingsGeneration;
using Microsoft.Build.Framework;
using SwiftBindings.TransitionGate;

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

    /// <summary>
    /// The platform SDK version selected when the copy packs (<c>xcrun --show-sdk-version</c>): used only
    /// when <see cref="Assembly"/> records no SDK of its own.
    /// </summary>
    public string PlatformSdkVersion { get; set; } = "";

    /// <summary>
    /// The binding's managed assembly. The wrapper identities generation stamped on it
    /// (<see cref="SwiftWrapperIdentityFormat"/>) record the platform SDK the binding was generated
    /// from, which is what the label guard judges and the declaration records: a copy generated on
    /// one Xcode and packed after another was selected is labelled by the SDK its surface came from.
    /// </summary>
    public string Assembly { get; set; } = "";

    /// <summary>The toolchain the copy is built on, as the build log names it.</summary>
    public string Toolchain { get; set; } = "";

    /// <summary>The SwiftBindings SDK (generator) version.</summary>
    public string Generator { get; set; } = "";

    /// <summary>The consumer targets packed into this copy's <c>buildTransitive/&lt;tfm&gt;/</c>.</summary>
    public string ConsumerTargetsFile { get; set; } = "";

    /// <summary>Where to write the copy declaration.</summary>
    [Required] public string DeclarationFile { get; set; } = "";

    /// <summary>
    /// Where the copy's natives are (<see cref="PackageCopyDeclarationEmitter.NativePayloadNative"/>,
    /// <see cref="PackageCopyDeclarationEmitter.NativePayloadNone"/> or
    /// <see cref="PackageCopyDeclarationEmitter.NativePayloadResources"/>); <c>native</c> when empty.
    /// </summary>
    public string NativePayload { get; set; } = "";

    public override bool Execute()
    {
        var sdk = GeneratedSdk();
        if (Log.HasLoggedErrors)
            return false;
        var label = PackageCopyRules.CheckLabel(PackageId, PackTfm, TargetKind, sdk);
        if (label is not null)
            Log.LogError(null, LabelCode, null, null, 0, 0, 0, 0, "{0}", label);
        var payload = NativePayload.Trim().Length == 0 ? PackageCopyDeclarationEmitter.NativePayloadNative : NativePayload.Trim();
        if (!PackageCopyDeclarationEmitter.IsNativePayload(payload))
        {
            Log.LogError(null, LayoutCode, null, null, 0, 0, 0, 0, "{0}",
                $"{PackageId} {PackTfm}: '{payload}' is not a native payload a copy can declare (native, none or resources).");
            return false;
        }
        var hasTargets = ConsumerTargetsFile.Length > 0 && File.Exists(ConsumerTargetsFile);
        if (hasTargets)
            foreach (var problem in PackageCopyRules.CheckTargetsReferences(PackTfm, File.ReadAllText(ConsumerTargetsFile), payload))
                Log.LogError(null, LayoutCode, null, ConsumerTargetsFile, 0, 0, 0, 0, "{0}",
                    $"{PackageId} {PackTfm}: the consumer targets {problem}. Each copy's targets must reference only its own native/{PackTfm}/. "
                    + "Delete the binding's obj/ and pack again so the targets are regenerated.");
        if (Log.HasLoggedErrors)
            return false;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(DeclarationFile))!);
        // A copy whose natives are under native/<tfm>/ is reached only through its consumer targets, so
        // it always declares them (the merge and the consumer check then refuse a copy that lost them).
        var text = PackageCopyDeclarationEmitter.Compose(PackageId, PackTfm, Toolchain, sdk, Generator, payload,
            hasConsumerTargets: hasTargets || payload == PackageCopyDeclarationEmitter.NativePayloadNative);
        if (!File.Exists(DeclarationFile) || File.ReadAllText(DeclarationFile) != text)
            File.WriteAllText(DeclarationFile, text);
        return true;
    }

    /// <summary>
    /// The SDK the binding was generated from, as its assembly records it; the SDK it was built
    /// against when it records no generation SDK (a pure Objective-C binding has no wrapper identity);
    /// the SDK selected now only for an assembly that records neither (one built before the records).
    /// </summary>
    private string GeneratedSdk()
    {
        if (Assembly.Length == 0 || !File.Exists(Assembly))
            return PlatformSdkVersion;
        var sdk = PackageCopyRules.CopySdk(File.ReadAllBytes(Assembly), PlatformSdkVersion, out var errors, out var message);
        foreach (var error in errors)
            Log.LogError(null, LabelCode, null, Assembly, 0, 0, 0, 0, "{0}", $"{PackageId} {PackTfm}: {error}");
        if (message is not null)
            Log.LogMessage(MessageImportance.High, "{0} {1}: {2}", PackageId, PackTfm, message);
        return sdk;
    }
}
