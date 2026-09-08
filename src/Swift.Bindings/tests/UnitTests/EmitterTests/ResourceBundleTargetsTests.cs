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

    [Fact]
    public void ResourceCopy_ReplacesRemovedPayloadAndEmptyFallbackDoesNotRetainRealBytes()
    {
        var source = Path.Combine(directory, "Source.framework");
        var bundle = Path.Combine(source, "Probe_Resources.bundle");
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

    [Fact]
    public void ResourceCopy_OutputAtSourceDoesNotDeleteInputPayload()
    {
        var bundle = Path.Combine(directory, "Probe_Resources.bundle");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "payload.txt"), "source identity");
        SwiftWrapperCompiler.CreateResourceBundleStubs(new List<string> { "Probe_Resources" },
            directory, NullLogger.Instance, Path.Combine(directory, "Source"));
        Assert.Equal("source identity", File.ReadAllText(Path.Combine(bundle, "payload.txt")));
        Assert.Equal(new[] { "Probe_Resources" }, File.ReadAllLines(Path.Combine(directory, "_resource-bundles.txt")));
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
