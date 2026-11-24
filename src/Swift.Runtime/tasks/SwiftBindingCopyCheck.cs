// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Linq;
using Microsoft.Build.Framework;

namespace SwiftBindings.Tasks;

/// <summary>
/// Checks, in a consumer build, that every SwiftBindings binding package resolved one copy whole:
/// the assembly the build compiles against comes from the <c>lib/&lt;tfm&gt;/</c> of the same copy whose
/// <c>buildTransitive/&lt;tfm&gt;/</c> consumer targets (and so whose <c>native/&lt;tfm&gt;/</c>) NuGet chose
/// (SWIFTBIND136). Announces the selected copy, and reports a copy that is older than the build's own
/// toolchain (SWIFTBIND139: a message by default, an error under <c>SwiftBindingsRequireExactCopy</c>).
/// </summary>
/// <remarks>
/// A binding package carries one copy per toolchain, each keyed by its full pack TFM, and each copy's
/// <c>buildTransitive/&lt;tfm&gt;/&lt;PackageId&gt;.props</c> declares it (a <c>SwiftBindingPackageCopy</c>
/// item). NuGet selects <c>lib/</c> and <c>buildTransitive/</c> independently by the same rule, so the
/// two agree only when the package holds the same set of TFMs in both. A package that does not would
/// otherwise link one toolchain's native payload under another toolchain's managed binding, and the
/// mismatch would only show at the first call that reaches a missing export.
/// </remarks>
public sealed class SwiftBindingCopyCheck : Microsoft.Build.Utilities.Task
{
    /// <summary>The copy-selection diagnostic.</summary>
    public const string SelectionCode = "SWIFTBIND136";

    /// <summary>The older-copy fallback diagnostic.</summary>
    public const string FallbackCode = "SWIFTBIND139";

    /// <summary>The copies the build's package consumer targets declared (<c>@(SwiftBindingPackageCopy)</c>).</summary>
    public ITaskItem[] Copies { get; set; } = [];

    /// <summary>The build's compile references (<c>@(ReferencePath)</c>).</summary>
    public ITaskItem[] References { get; set; } = [];

    /// <summary>The build's target framework.</summary>
    [Required]
    public string TargetFramework { get; set; } = "";

    /// <summary>The build's target platform version.</summary>
    public string TargetPlatformVersion { get; set; } = "";

    /// <summary>When set, a copy older than the build's toolchain is an error rather than a message.</summary>
    public bool RequireExactCopy { get; set; }

    public override bool Execute()
    {
        var copies = Copies.Select(c => new PackageCopy(
            c.ItemSpec, c.GetMetadata("CopyTfm"), c.GetMetadata("Toolchain"),
            c.GetMetadata("LibDirectory"), c.GetMetadata("NativeDirectory"), c.GetMetadata("TargetsFile"),
            c.GetMetadata("NativePayload"))).ToList();
        var references = References.Select(r => new ResolvedReference(
            r.GetMetadata("FullPath"), r.GetMetadata("NuGetPackageId"), r.GetMetadata("NuGetPackageVersion"))).ToList();
        foreach (var finding in PackageCopyCheck.Evaluate(copies, references, TargetFramework, TargetPlatformVersion, RequireExactCopy))
        {
            switch (finding.Kind)
            {
                case PackageCopyFindingKind.Selected:
                    Log.LogMessage(MessageImportance.High, "{0}", finding.Text);
                    break;
                case PackageCopyFindingKind.Fallback:
                    Log.LogMessage(null, FallbackCode, null, null, 0, 0, 0, 0, MessageImportance.High, "{0}", finding.Text);
                    break;
                case PackageCopyFindingKind.FallbackRefused:
                    Log.LogError(null, FallbackCode, null, null, 0, 0, 0, 0, "{0}", finding.Text);
                    break;
                default:
                    Log.LogError(null, SelectionCode, null, null, 0, 0, 0, 0, "{0}", finding.Text);
                    break;
            }
        }
        return !Log.HasLoggedErrors;
    }
}
