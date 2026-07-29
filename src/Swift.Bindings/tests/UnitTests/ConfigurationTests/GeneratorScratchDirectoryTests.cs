// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

public class GeneratorScratchDirectoryTests
{
    [Fact]
    public void Path_IsAProcessScopedSubdirectoryOfTheTempRoot()
    {
        // Dependency resolution writes derived inputs named after the Swift module they describe
        // ({Module}.abi.json, {Module}.tbd). Landing those in the OS temp root put two generator
        // processes resolving a dependency with the same module name on one path — including two
        // unrelated projects that happen to share a vendored framework. The scratch directory has
        // to be keyed to the process for those names to stop colliding.
        var scratch = GeneratorScratchDirectory.Path;
        var tempRoot = System.IO.Path.GetTempPath();

        Assert.True(Directory.Exists(scratch));
        Assert.NotEqual(
            System.IO.Path.TrimEndingDirectorySeparator(tempRoot),
            System.IO.Path.TrimEndingDirectorySeparator(scratch));
        Assert.Equal(
            System.IO.Path.TrimEndingDirectorySeparator(tempRoot),
            System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetDirectoryName(scratch)!));
        Assert.Contains(Environment.ProcessId.ToString(), System.IO.Path.GetFileName(scratch));
    }

    [Fact]
    public void Path_IsStableWithinTheProcess()
    {
        // One run's derived inputs must all land together — the resolver writes a file and hands
        // its path on to a later phase, so a directory that moved between calls would break it.
        Assert.Equal(GeneratorScratchDirectory.Path, GeneratorScratchDirectory.Path);
    }

    [Fact]
    public void Path_IsWritableAndDoesNotLeakIntoTheSharedTempRoot()
    {
        var scratch = GeneratorScratchDirectory.Path;
        var moduleNamedFile = System.IO.Path.Combine(scratch, "ScratchProbeModule.abi.json");
        try
        {
            File.WriteAllText(moduleNamedFile, "{}");

            Assert.True(File.Exists(moduleNamedFile));
            Assert.False(File.Exists(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ScratchProbeModule.abi.json")));
        }
        finally
        {
            try { File.Delete(moduleNamedFile); }
            catch { /* test cleanup */ }
        }
    }
}
