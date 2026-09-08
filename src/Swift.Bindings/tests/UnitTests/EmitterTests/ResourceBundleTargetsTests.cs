// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

public sealed class ResourceBundleTargetsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "swift resource targets " + Guid.NewGuid().ToString("N"));

    public ResourceBundleTargetsTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResourceManifestProducedAfterEmission_IsCollectedWithBundleRelativeLinks(bool package)
    {
        var root = directory.Replace('\\', '/') + "/";
        var target = ResourceBundleTargetsEmitter.Emit("ResourceProbe", root,
            "ResolveProjectReferences", package ? "runtimes/ios/native/" : null);
        // Emit before the inventory exists: this is the deferred-wrapper contract.
        File.WriteAllText(Path.Combine(directory, "resources.targets"), "<Project>" + target + "</Project>");
        Directory.CreateDirectory(Path.Combine(directory, "Probe_Resources.bundle", "nested"));
        File.WriteAllText(Path.Combine(directory, "Probe_Resources.bundle", "nested", "payload.txt"), "known payload");
        File.WriteAllText(Path.Combine(directory, "probe.proj"), """
            <Project>
              <Import Project="resources.targets" />
              <Target Name="ResolveProjectReferences">
                <WriteLinesToFile File="_resource-bundles.txt" Lines="Probe_Resources" Overwrite="true" />
              </Target>
              <Target Name="_CollectBundleResources">
                <WriteLinesToFile File="result.txt" Lines="@(BundleResource->'%(FullPath)|%(Link)')" Overwrite="true" />
                <WriteLinesToFile File="pack.txt" Lines="@(None->'%(FullPath)|%(PackagePath)')" Overwrite="true" />
              </Target>
            </Project>
            """);
        RunMsbuild("_CollectBundleResources");
        var result = File.ReadAllLines(Path.Combine(directory, "result.txt"));
        Assert.Single(result);
        Assert.Equal(Path.Combine(directory, "Probe_Resources.bundle", "nested", "payload.txt")
            + "|Probe_Resources.bundle/nested/payload.txt", result[0]);
        if (package)
        {
            var packed = File.ReadAllLines(Path.Combine(directory, "pack.txt"));
            Assert.Contains(packed, item => item.EndsWith("|runtimes/ios/native/Probe_Resources.bundle/nested/"));
            Assert.Contains(packed, item => item.EndsWith("_resource-bundles.txt|runtimes/ios/native/"));
        }
    }

    [Fact]
    public void MissingManifest_CollectsNoResources()
    {
        var target = ResourceBundleTargetsEmitter.Emit("ResourceProbe", directory.Replace('\\', '/') + "/");
        File.WriteAllText(Path.Combine(directory, "probe.proj"), "<Project>" + target + """
              <Target Name="_CollectBundleResources">
                <Error Condition="'@(BundleResource)' != ''" Text="Unexpected resource" />
              </Target>
            </Project>
            """);
        RunMsbuild("_CollectBundleResources");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResourceCopy_ReplacesRemovedPayloadAndEmptyFallbackDoesNotRetainRealBytes(bool resourcesChild)
    {
        var source = Path.Combine(directory, "Source.framework");
        var bundle = Path.Combine(resourcesChild ? Path.Combine(source, "Resources") : source, "Probe_Resources.bundle");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "old.txt"), "old payload");
        var output = Path.Combine(directory, "output");
        Directory.CreateDirectory(output);
        var names = new List<string> { "Probe_Resources" };
        SwiftWrapperCompiler.CreateResourceBundleStubs(names, output, NullLogger.Instance, Path.Combine(source, "Source"));
        File.Delete(Path.Combine(bundle, "old.txt"));
        File.WriteAllBytes(Path.Combine(bundle, "payload.bin"), new byte[] { 0, 127, 255, 13 });
        SwiftWrapperCompiler.CreateResourceBundleStubs(names, output, NullLogger.Instance, Path.Combine(source, "Source"));
        var generated = Path.Combine(output, "Probe_Resources.bundle");
        Assert.False(File.Exists(Path.Combine(generated, "old.txt")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(bundle, "payload.bin")), File.ReadAllBytes(Path.Combine(generated, "payload.bin")));
        Directory.Delete(bundle, recursive: true);
        SwiftWrapperCompiler.CreateResourceBundleStubs(names, output, NullLogger.Instance, Path.Combine(source, "Source"));
        Assert.Equal(new[] { "_sbw_stub" }, Directory.GetFiles(generated).Select(Path.GetFileName));
        SwiftWrapperCompiler.CreateResourceBundleStubs(new List<string>(), output, NullLogger.Instance, Path.Combine(source, "Source"));
        Assert.Empty(File.ReadAllLines(Path.Combine(output, "_resource-bundles.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResourceCopy_OutputAtSourceDoesNotDeleteInputPayload(bool resourcesChild)
    {
        var output = resourcesChild ? Path.Combine(directory, "Resources") : directory;
        var bundle = Path.Combine(output, "Probe_Resources.bundle");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "payload.txt"), "source identity");
        SwiftWrapperCompiler.CreateResourceBundleStubs(new List<string> { "Probe_Resources" },
            output, NullLogger.Instance, Path.Combine(directory, "Source"));
        Assert.Equal("source identity", File.ReadAllText(Path.Combine(bundle, "payload.txt")));
        Assert.Equal(new[] { "Probe_Resources" }, File.ReadAllLines(Path.Combine(output, "_resource-bundles.txt")));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResourceCopy_MacFrameworkPreservesBundleRelativeTree(bool frameworkRootAlias)
    {
        if (frameworkRootAlias)
            Skip.If(OperatingSystem.IsWindows(), "Framework symlink fixture requires Unix symlink support");
        var framework = Path.Combine(directory, "Source.framework");
        var version = Path.Combine(framework, "Versions", "A");
        var sourceBundle = Path.Combine(version, "Resources", "Probe_Resources.bundle");
        var payloadDirectory = Path.Combine(sourceBundle, "Contents", "Resources");
        Directory.CreateDirectory(payloadDirectory);
        var payload = new byte[] { 0, 127, 255, 13, 10 };
        File.WriteAllBytes(Path.Combine(payloadDirectory, "payload.bin"), payload);
        File.WriteAllText(Path.Combine(sourceBundle, "Contents", "Info.plist"), "original bundle metadata");
        var binary = Path.Combine(version, "Source");
        File.WriteAllText(binary, "source binary location marker");
        if (frameworkRootAlias)
        {
            Directory.CreateSymbolicLink(Path.Combine(framework, "Versions", "Current"), "A");
            Directory.CreateSymbolicLink(Path.Combine(framework, "Resources"), "Versions/Current/Resources");
            File.CreateSymbolicLink(Path.Combine(framework, "Source"), "Versions/Current/Source");
            binary = Path.Combine(framework, "Source");
        }
        var output = Path.Combine(directory, "output");
        Directory.CreateDirectory(output);
        SwiftWrapperCompiler.CreateResourceBundleStubs(new List<string> { "Probe_Resources" },
            output, NullLogger.Instance, binary);
        var result = Path.Combine(output, "Probe_Resources.bundle");
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(result, "Contents", "Resources", "payload.bin")));
        Assert.Equal("original bundle metadata", File.ReadAllText(Path.Combine(result, "Contents", "Info.plist")));
        Assert.Equal(new[] { Path.Combine("Contents", "Info.plist"), Path.Combine("Contents", "Resources", "payload.bin") },
            Directory.GetFiles(result, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(result, path)).OrderBy(path => path, StringComparer.Ordinal));
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(payloadDirectory, "payload.bin")));
        Assert.Equal(new[] { "Probe_Resources" }, File.ReadAllLines(Path.Combine(output, "_resource-bundles.txt")));
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ResourceCopy_CrossedFrameworkAliasesPreserveInput(bool sourceUsesAlias, bool ancestorAlias)
    {
        Skip.If(OperatingSystem.IsWindows(), "Framework symlink fixture requires Unix symlink support");
        var framework = Path.Combine(directory, "Source.framework");
        var version = Path.Combine(framework, "Versions", "A");
        var resources = Path.Combine(version, "Resources");
        var bundle = Path.Combine(resources, "Probe_Resources.bundle");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "Resources"));
        var payloadPath = Path.Combine(bundle, "Contents", "Resources", "payload.bin");
        var payload = new byte[] { 0, 127, 255, 13, 10 };
        File.WriteAllBytes(payloadPath, payload);
        File.WriteAllText(Path.Combine(version, "Source"), "binary identity");
        Directory.CreateSymbolicLink(Path.Combine(framework, "Versions", "Current"), "A");
        Directory.CreateSymbolicLink(Path.Combine(framework, "Resources"), "Versions/Current/Resources");
        File.CreateSymbolicLink(Path.Combine(framework, "Source"), "Versions/Current/Source");
        var aliasFramework = framework;
        if (ancestorAlias)
        {
            var alias = Path.Combine(directory, "ancestor-alias");
            Directory.CreateSymbolicLink(alias, framework);
            aliasFramework = alias;
        }
        var binary = sourceUsesAlias ? Path.Combine(aliasFramework, "Source") : Path.Combine(version, "Source");
        var output = sourceUsesAlias ? resources : Path.Combine(aliasFramework, "Resources");
        SwiftWrapperCompiler.CreateResourceBundleStubs(new List<string> { "Probe_Resources" },
            output, NullLogger.Instance, binary);
        Assert.Equal(payload, File.ReadAllBytes(payloadPath));
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(output, "Probe_Resources.bundle", "Contents", "Resources", "payload.bin")));
        Assert.Equal("binary identity", File.ReadAllText(Path.Combine(version, "Source")));
        Assert.Equal("A", new DirectoryInfo(Path.Combine(framework, "Versions", "Current")).LinkTarget);
        Assert.Equal("Versions/Current/Resources", new DirectoryInfo(Path.Combine(framework, "Resources")).LinkTarget);
        Assert.Equal(new[] { Path.Combine("Contents", "Resources", "payload.bin") },
            Directory.GetFiles(bundle, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(bundle, path)));
        Assert.False(File.Exists(Path.Combine(bundle, "_sbw_stub")));
        Assert.Equal(new[] { "Probe_Resources" }, File.ReadAllLines(Path.Combine(output, "_resource-bundles.txt")));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResourceCopy_TmpAncestorAliasPreservesInput(bool sourceUsesAlias)
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "Exercises macOS /tmp -> /private/tmp alias");
        var name = "swift-resource-alias-" + Guid.NewGuid().ToString("N");
        var physical = Path.Combine("/private/tmp", name);
        var alias = Path.Combine("/tmp", name);
        var bundle = Path.Combine(physical, "Probe_Resources.bundle");
        Directory.CreateDirectory(bundle);
        try
        {
            File.WriteAllText(Path.Combine(bundle, "payload.txt"), "source identity");
            SwiftWrapperCompiler.CreateResourceBundleStubs(new List<string> { "Probe_Resources" },
                sourceUsesAlias ? physical : alias, NullLogger.Instance,
                Path.Combine(sourceUsesAlias ? alias : physical, "Source"));
            Assert.Equal("source identity", File.ReadAllText(Path.Combine(bundle, "payload.txt")));
            Assert.False(File.Exists(Path.Combine(bundle, "_sbw_stub")));
            Assert.Equal(new[] { "Probe_Resources" }, File.ReadAllLines(Path.Combine(physical, "_resource-bundles.txt")));
        }
        finally
        {
            Directory.Delete(physical, recursive: true);
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResourceCopy_CaseAliasPreservesInput(bool sourceUsesAlias)
    {
        var source = Path.Combine(directory, "CaseSource.framework");
        var alias = Path.Combine(directory, "casesource.framework");
        Directory.CreateDirectory(source);
        var probe = Path.Combine(source, "case-probe.txt");
        File.WriteAllText(probe, "filesystem case probe");
        Skip.IfNot(File.Exists(Path.Combine(alias, "case-probe.txt")), "Fixture volume is case-sensitive");
        var bundle = Path.Combine(source, "Probe_Resources.bundle");
        Directory.CreateDirectory(bundle);
        File.WriteAllBytes(Path.Combine(bundle, "payload.bin"), new byte[] { 0, 127, 255 });
        SwiftWrapperCompiler.CreateResourceBundleStubs(new List<string> { "Probe_Resources" },
            sourceUsesAlias ? source : alias, NullLogger.Instance,
            Path.Combine(sourceUsesAlias ? alias : source, "Source"));
        Assert.Equal(new byte[] { 0, 127, 255 }, File.ReadAllBytes(Path.Combine(bundle, "payload.bin")));
        Assert.False(File.Exists(Path.Combine(bundle, "_sbw_stub")));
        Assert.Equal(new[] { "Probe_Resources" }, File.ReadAllLines(Path.Combine(source, "_resource-bundles.txt")));
        Assert.Equal("filesystem case probe", File.ReadAllText(probe));
    }

    [SkippableFact]
    public void ResourceCopy_CaseDistinctDirectoriesReplaceDestination()
    {
        var source = Path.Combine(directory, "CaseSource");
        var output = Path.Combine(directory, "casesource");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "case-probe.txt"), "probe");
        Skip.If(File.Exists(Path.Combine(output, "case-probe.txt")), "Fixture volume is case-insensitive");
        var bundle = Path.Combine(source, "Probe_Resources.bundle");
        var dest = Path.Combine(output, "Probe_Resources.bundle");
        Directory.CreateDirectory(bundle);
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(bundle, "payload.txt"), "current");
        File.WriteAllText(Path.Combine(dest, "old.txt"), "stale");
        SwiftWrapperCompiler.CreateResourceBundleStubs(new List<string> { "Probe_Resources" },
            output, NullLogger.Instance, Path.Combine(source, "Source"));
        Assert.Equal("current", File.ReadAllText(Path.Combine(dest, "payload.txt")));
        Assert.False(File.Exists(Path.Combine(dest, "old.txt")));
        Assert.Equal("current", File.ReadAllText(Path.Combine(bundle, "payload.txt")));
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ResourceCopy_NestedSourceAndDestinationRefusedBeforeMutation(bool destinationInsideSource, bool aliasParent)
    {
        if (aliasParent)
            Skip.If(OperatingSystem.IsWindows(), "Ancestor symlink fixture requires Unix symlink support");
        var outer = Path.Combine(directory, "Probe_Resources.bundle");
        var innerParent = Path.Combine(outer, "nested");
        var inner = Path.Combine(innerParent, "Probe_Resources.bundle");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(outer, "outer.txt"), "outer identity");
        File.WriteAllText(Path.Combine(inner, "inner.txt"), "inner identity");
        var sourceParent = destinationInsideSource ? directory : innerParent;
        var output = destinationInsideSource ? innerParent : directory;
        if (aliasParent)
        {
            var alias = Path.Combine(directory, "parent-alias");
            Directory.CreateSymbolicLink(alias, sourceParent);
            sourceParent = alias;
        }
        var manifest = Path.Combine(output, "_resource-bundles.txt");
        File.WriteAllText(manifest, "previous inventory\n");
        Assert.Throws<IOException>(() => SwiftWrapperCompiler.CreateResourceBundleStubs(
            new List<string> { "Probe_Resources" }, output, NullLogger.Instance, Path.Combine(sourceParent, "Source")));
        Assert.Equal("outer identity", File.ReadAllText(Path.Combine(outer, "outer.txt")));
        Assert.Equal("inner identity", File.ReadAllText(Path.Combine(inner, "inner.txt")));
        Assert.Equal("previous inventory\n", File.ReadAllText(manifest));
    }

    [SkippableFact]
    public void ResourceCopy_FailedSnapshotPreservesDestinationAndManifest()
    {
        Skip.If(OperatingSystem.IsWindows(), "Dangling file symlink fixture requires Unix symlink support");
        var source = Path.Combine(directory, "Source.framework");
        var bundle = Path.Combine(source, "Probe_Resources.bundle");
        var output = Path.Combine(directory, "output");
        var dest = Path.Combine(output, "Probe_Resources.bundle");
        Directory.CreateDirectory(bundle);
        Directory.CreateDirectory(dest);
        File.CreateSymbolicLink(Path.Combine(bundle, "missing.txt"), "does-not-exist.txt");
        File.WriteAllBytes(Path.Combine(dest, "old.bin"), new byte[] { 0, 255, 13 });
        var manifest = Path.Combine(output, "_resource-bundles.txt");
        File.WriteAllText(manifest, "previous inventory\n");
        Assert.ThrowsAny<IOException>(() => SwiftWrapperCompiler.CreateResourceBundleStubs(
            new List<string> { "Probe_Resources" }, output, NullLogger.Instance, Path.Combine(source, "Source")));
        Assert.Equal(new byte[] { 0, 255, 13 }, File.ReadAllBytes(Path.Combine(dest, "old.bin")));
        Assert.Equal("previous inventory\n", File.ReadAllText(manifest));
        Assert.Equal(new[] { "Probe_Resources.bundle", "_resource-bundles.txt" },
            Directory.GetFileSystemEntries(output).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal("does-not-exist.txt", new FileInfo(Path.Combine(bundle, "missing.txt")).LinkTarget);
    }

    [Fact]
    public void ResourcePromotion_FailedMoveRestoresPreviousBundle()
    {
        var destination = Path.Combine(directory, "Probe_Resources.bundle");
        Directory.CreateDirectory(destination);
        File.WriteAllBytes(Path.Combine(destination, "payload.bin"), new byte[] { 0, 255, 13 });
        var manifest = Path.Combine(directory, "_resource-bundles.txt");
        File.WriteAllText(manifest, "previous inventory\n");
        Assert.ThrowsAny<IOException>(() => SwiftWrapperCompiler.PromoteResourceBundle(
            Path.Combine(directory, "missing-stage"), destination, NullLogger.Instance));
        Assert.Equal(new byte[] { 0, 255, 13 }, File.ReadAllBytes(Path.Combine(destination, "payload.bin")));
        Assert.Equal("previous inventory\n", File.ReadAllText(manifest));
        Assert.Equal(new[] { "Probe_Resources.bundle", "_resource-bundles.txt" },
            Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void ResourceCopy_InPlaceMissingBundleCreatesStubWithoutChangingBinary()
    {
        var binary = Path.Combine(directory, "Source");
        var bytes = new byte[] { 0, 127, 255, 13, 10 };
        File.WriteAllBytes(binary, bytes);
        SwiftWrapperCompiler.CreateResourceBundleStubs(new List<string> { "Missing_Resources" },
            directory, NullLogger.Instance, binary);
        Assert.Equal(bytes, File.ReadAllBytes(binary));
        Assert.Equal(new[] { "_sbw_stub" }, Directory.GetFiles(Path.Combine(directory, "Missing_Resources.bundle"))
            .Select(Path.GetFileName));
        Assert.Equal(new[] { "Missing_Resources" }, File.ReadAllLines(Path.Combine(directory, "_resource-bundles.txt")));
    }

    [SkippableFact]
    public void ResourceCopy_SecondSnapshotFailurePreservesCompletedFirstAndOriginalSecond()
    {
        Skip.If(OperatingSystem.IsWindows(), "Dangling file symlink fixture requires Unix symlink support");
        var source = Path.Combine(directory, "Source.framework");
        var output = Path.Combine(directory, "output");
        foreach (var name in new[] { "First", "Second" })
        {
            Directory.CreateDirectory(Path.Combine(source, name + ".bundle"));
            Directory.CreateDirectory(Path.Combine(output, name + ".bundle"));
            File.WriteAllText(Path.Combine(output, name + ".bundle", "old.txt"), name + " original");
        }
        File.WriteAllText(Path.Combine(source, "First.bundle", "current.txt"), "first current");
        File.CreateSymbolicLink(Path.Combine(source, "Second.bundle", "missing.txt"), "does-not-exist.txt");
        var manifest = Path.Combine(output, "_resource-bundles.txt");
        File.WriteAllText(manifest, "previous inventory\n");
        Assert.ThrowsAny<IOException>(() => SwiftWrapperCompiler.CreateResourceBundleStubs(
            new List<string> { "First", "Second" }, output, NullLogger.Instance, Path.Combine(source, "Source")));
        Assert.Equal("first current", File.ReadAllText(Path.Combine(output, "First.bundle", "current.txt")));
        Assert.False(File.Exists(Path.Combine(output, "First.bundle", "old.txt")));
        Assert.Equal("first current", File.ReadAllText(Path.Combine(source, "First.bundle", "current.txt")));
        Assert.Equal("Second original", File.ReadAllText(Path.Combine(output, "Second.bundle", "old.txt")));
        Assert.Equal("does-not-exist.txt", new FileInfo(Path.Combine(source, "Second.bundle", "missing.txt")).LinkTarget);
        Assert.Equal("previous inventory\n", File.ReadAllText(manifest));
        Assert.Equal(new[] { "First.bundle", "Second.bundle", "_resource-bundles.txt" },
            Directory.GetFileSystemEntries(output).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void ResourceCopy_FlatBundleTakesPrecedenceOverResourcesChild()
    {
        var framework = Path.Combine(directory, "Source.framework");
        var flat = Path.Combine(framework, "Probe_Resources.bundle");
        var nested = Path.Combine(framework, "Resources", "Probe_Resources.bundle");
        Directory.CreateDirectory(flat);
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(flat, "payload.txt"), "flat payload");
        File.WriteAllText(Path.Combine(nested, "payload.txt"), "resources payload");
        var output = Path.Combine(directory, "output");
        Directory.CreateDirectory(output);
        SwiftWrapperCompiler.CreateResourceBundleStubs(new List<string> { "Probe_Resources" },
            output, NullLogger.Instance, Path.Combine(framework, "Source"));
        Assert.Equal("flat payload", File.ReadAllText(Path.Combine(output, "Probe_Resources.bundle", "payload.txt")));
        Assert.Equal("resources payload", File.ReadAllText(Path.Combine(nested, "payload.txt")));
    }

    [Theory]
    [InlineData(2, "")]
    [InlineData(2, "   ")]
    [InlineData(2, "unable to find bundle named Partial_Resources")]
    public void ResourceDetection_CommandFailurePreservesPreviousInventoryAndPayload(int exitCode, string stdout)
    {
        var runner = new MockCommandRunner();
        runner.SetResponse("grep", exitCode, stdout, "read failed");
        AssertDetectionFailurePreservesState(runner);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResourceDetection_TimeoutOrLaunchFailurePreservesPreviousInventoryAndPayload(bool timeout)
    {
        AssertDetectionFailurePreservesState(new ThrowingResourceDetectionRunner(timeout));
    }

    private void AssertDetectionFailurePreservesState(ICommandRunner runner)
    {
        var manifest = Path.Combine(directory, "_resource-bundles.txt");
        var bundle = Path.Combine(directory, "Previous_Resources.bundle");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(manifest, "Previous_Resources\n");
        var payload = Path.Combine(bundle, "payload.txt");
        File.WriteAllBytes(payload, new byte[] { 0, 127, 255, 13 });
        var payloadBefore = File.ReadAllBytes(payload);
        var manifestBefore = File.ReadAllBytes(manifest);
        SwiftWrapperCompiler.PrepareResourceBundles("Source", directory, runner, NullLogger.Instance);
        Assert.Equal(manifestBefore, File.ReadAllBytes(manifest));
        Assert.Equal(payloadBefore, File.ReadAllBytes(payload));
        Assert.False(Directory.Exists(Path.Combine(directory, "Partial_Resources.bundle")));

        // A first build has no previous inventory to preserve: do not manufacture an
        // empty authoritative result when the operational detection never completed.
        File.Delete(manifest);
        SwiftWrapperCompiler.PrepareResourceBundles("Source", directory, runner, NullLogger.Instance);
        Assert.False(File.Exists(manifest));
        Assert.Equal(payloadBefore, File.ReadAllBytes(payload));
    }

    [Fact]
    public void ResourceDetection_SuccessfulNoMatchPublishesEmptyInventory()
    {
        var manifest = Path.Combine(directory, "_resource-bundles.txt");
        File.WriteAllText(manifest, "Previous_Resources\n");
        var runner = new MockCommandRunner();
        runner.SetResponse("grep", 1, "");
        SwiftWrapperCompiler.PrepareResourceBundles("Source", directory, runner, NullLogger.Instance);
        Assert.Empty(File.ReadAllLines(manifest));
    }

    [Fact]
    public void ResourceDetection_SuccessfulNamesPublishRealPayloadAndDeduplicate()
    {
        var source = Path.Combine(directory, "Source.framework");
        var bundle = Path.Combine(source, "Probe_Resources.bundle");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "payload.txt"), "current payload");
        var output = Path.Combine(directory, "output");
        Directory.CreateDirectory(output);
        var runner = new MockCommandRunner();
        runner.SetResponse("grep", 0, "unable to find bundle named Probe_Resources\nunable to find bundle named Probe_Resources");
        SwiftWrapperCompiler.PrepareResourceBundles(Path.Combine(source, "Source"), output, runner, NullLogger.Instance);
        Assert.Equal(new[] { "Probe_Resources" }, File.ReadAllLines(Path.Combine(output, "_resource-bundles.txt")));
        Assert.Equal("current payload", File.ReadAllText(Path.Combine(output, "Probe_Resources.bundle", "payload.txt")));
    }

    private sealed class ThrowingResourceDetectionRunner : ICommandRunner
    {
        private readonly bool timeout;
        public ThrowingResourceDetectionRunner(bool timeout) => this.timeout = timeout;
        public (int ExitCode, string StdOut, string StdErr) Run(string command, string arguments, int timeoutMs = 30000)
            => throw (timeout ? (Exception)new TimeoutException("detector timeout") : new InvalidOperationException("could not start detector"));
    }

    private void RunMsbuild(string target)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("msbuild");
        start.ArgumentList.Add("probe.proj");
        start.ArgumentList.Add("-t:" + target);
        start.ArgumentList.Add("-v:minimal");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(60000), "Resource target MSBuild timed out");
        Assert.True(process.ExitCode == 0, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
