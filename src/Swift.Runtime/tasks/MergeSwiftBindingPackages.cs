// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Linq;
using Microsoft.Build.Framework;

namespace SwiftBindings.Tasks;

/// <summary>
/// Merges the per-toolchain contributions of one binding package (each packed by SwiftBindings.Sdk on
/// its own .NET + Xcode pairing, same id, version and source) into the one package that ships, with a
/// copy per toolchain. Refuses (SWIFTBIND138) contributions that do not make one well-formed package,
/// then checks the merged package as a pack would (SWIFTBIND133, the Windows path budget).
/// </summary>
public sealed class MergeSwiftBindingPackages : Microsoft.Build.Utilities.Task
{
    /// <summary>The contribution packages, one per toolchain.</summary>
    [Required]
    public ITaskItem[] Contributions { get; set; } = [];

    /// <summary>The full target frameworks the merged package must carry, exactly (the pipeline's matrix).</summary>
    [Required]
    public ITaskItem[] TargetFrameworks { get; set; } = [];

    /// <summary>Where the merged package is written (<c>&lt;id&gt;.&lt;version&gt;.nupkg</c>).</summary>
    [Required]
    public string OutputDirectory { get; set; } = "";

    /// <summary>Directories the packed assemblies' references (the runtime) resolve in, for the transition check.</summary>
    public ITaskItem[] ReferenceDirectories { get; set; } = [];

    [Required]
    public string ScratchDirectory { get; set; } = "";

    /// <summary>The merged package written.</summary>
    [Output]
    public string MergedPackage { get; set; } = "";

    public override bool Execute()
    {
        var result = SwiftBindingPackageMerge.Merge(
            Contributions.Select(c => c.GetMetadata("FullPath")).ToList(),
            TargetFrameworks.Select(t => t.ItemSpec.Trim()).Where(t => t.Length > 0).ToList(),
            OutputDirectory);
        foreach (var line in result.Notes)
            Log.LogMessage(MessageImportance.High, "{0}", line);
        foreach (var problem in result.Problems)
            Log.LogError(null, PrepareSwiftBindingPackageCopy.LayoutCode, null, null, 0, 0, 0, 0, "{0}", problem);
        if (result.Package is null)
            return false;
        var check = new SwiftTransitionPackageCheck
        {
            BuildEngine = BuildEngine,
            Packages = [new Microsoft.Build.Utilities.TaskItem(result.Package)],
            ReferenceDirectories = ReferenceDirectories,
            ScratchDirectory = ScratchDirectory,
        };
        if (!check.Execute())
            return false;
        MergedPackage = SwiftBindingPackageMerge.Publish(result);
        Log.LogMessage(MessageImportance.High, "Merged {0} contribution(s) into {1}: copies {2}.",
            Contributions.Length, MergedPackage, string.Join(", ", result.Copies));
        return !Log.HasLoggedErrors;
    }
}
