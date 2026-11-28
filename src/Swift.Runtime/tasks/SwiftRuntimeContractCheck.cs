// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.Build.Framework;
using SwiftBindings.TransitionGate;

namespace SwiftBindings.Tasks;

/// <summary>
/// Refuses to build against a generated binding that the resolved <c>SwiftBindings.Runtime</c> will
/// not load (SWIFTBIND144): one whose module initializer passes <c>RuntimeContract.AssertCompatible</c>
/// an epoch outside the runtime's window, or one generated before that handshake existed.
/// </summary>
/// <remarks>
/// NuGet keeps a binding package and the runtime it was packed against within one minor, but a
/// direct dependency on a newer runtime, a plain <c>Reference</c> to a binding assembly or a
/// <c>ProjectReference</c> diamond bypasses that, and the app then builds cleanly and throws
/// <c>SwiftRuntimeContractMismatchException</c> from the binding's module initializer when it loads,
/// on every engine. The window is read from the runtime assembly the build resolves
/// (<see cref="RuntimeContractCheck.ReadWindow"/>), and each binding's epoch from its module
/// initializer's IL, so the check and the load gate decide alike. A runtime that records no window
/// (a reference assembly, or one from before the handshake) is not judged.
/// </remarks>
public sealed class SwiftRuntimeContractCheck : Microsoft.Build.Utilities.Task
{
    /// <summary>The diagnostic code of a binding the runtime will not load.</summary>
    public const string Code = "SWIFTBIND144";

    /// <summary>The compile references (<c>@(ReferencePath)</c>).</summary>
    public ITaskItem[] CompileReferences { get; set; } = [];

    /// <summary>The copy-local runtime assemblies (<c>@(ReferenceCopyLocalPaths)</c>), which win over a compile reference of the same name.</summary>
    public ITaskItem[] RuntimeReferences { get; set; } = [];

    public override bool Execute()
    {
        var byName = new Dictionary<string, ITaskItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in RuntimeReferences.Concat(CompileReferences))
        {
            var path = item.GetMetadata("FullPath");
            if (string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                byName.TryAdd(Path.GetFileNameWithoutExtension(path), item);
        }
        if (!byName.TryGetValue("Swift.Runtime", out var runtime))
            return true;
        var window = RuntimeContractCheck.ReadWindow(File.ReadAllBytes(runtime.GetMetadata("FullPath")));
        if (window is null)
        {
            Log.LogMessage(MessageImportance.Low, "{0} records no runtime contract window, so the bindings' contract epochs are not checked.", runtime.GetMetadata("FullPath"));
            return true;
        }
        foreach (var item in byName.Values.OrderBy(i => i.GetMetadata("FullPath"), StringComparer.Ordinal))
        {
            var path = item.GetMetadata("FullPath");
            if (ReferenceEquals(item, runtime) || !ReferencesRuntime(path))
                continue;
            var problem = RuntimeContractCheck.Problem(RuntimeContractCheck.ReadGenerated(File.ReadAllBytes(path)), window);
            if (problem is null)
                continue;
            var package = item.GetMetadata("NuGetPackageId");
            var project = item.GetMetadata("MSBuildSourceProjectFile");
            var origin = package.Length > 0 ? $"package {package} {item.GetMetadata("NuGetPackageVersion")}".TrimEnd()
                : project.Length > 0 ? $"project {Path.GetFileName(project)}"
                : $"reference {path}";
            var remedy = package.Length > 0
                ? $"Use a version of {package} built with SwiftBindings.Sdk {Minor(window)}, or a SwiftBindings.Runtime of the version the package was built with."
                : $"Regenerate the binding with SwiftBindings.Sdk {Minor(window)} (a clean build of the binding project), or reference the SwiftBindings.Runtime it was generated with.";
            Log.LogError(null, Code, null, null, 0, 0, 0, 0, "{0}",
                $"{Path.GetFileName(path)} ({origin}) cannot load on this build's SwiftBindings.Runtime: {problem}. {remedy}");
        }
        return !Log.HasLoggedErrors;
    }

    private static string Minor(RuntimeContractWindow window) =>
        window.Version == 0 ? window.PackageVersion : $"{window.Version / 1000}.{window.Version % 1000}";

    /// <summary>Whether the assembly at <paramref name="path"/> references <c>Swift.Runtime</c>, from its metadata alone.</summary>
    private static bool ReferencesRuntime(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                return false;
            var md = pe.GetMetadataReader();
            return md.AssemblyReferences.Any(h => md.GetString(md.GetAssemblyReference(h).Name) == "Swift.Runtime");
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }
}
