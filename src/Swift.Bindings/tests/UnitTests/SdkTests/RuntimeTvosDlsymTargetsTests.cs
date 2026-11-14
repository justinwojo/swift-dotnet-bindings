// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The packaged Runtime targets give a .NET 10 tvOS Mono app the P/Invoke binding iOS gives it by
/// default. The workload's bundler resolves a non-SDK assembly's P/Invokes through dlsym on iOS and
/// Mac Catalyst but links them directly on tvOS (dotnet-linker <c>Application.UseDlsym</c>), so an
/// AOT-compiled tvOS app references symbols such as <c>$sS2ayxGycfC</c> in libswiftCore that nothing
/// links. The fixture stands in for the workload in a Rebuild's order: the bundler options are
/// parsed during Clean, references resolve afterwards, and <c>_ComputeLinkerInputs</c> then writes
/// one <c>Dlsym=</c> line per <c>_BundlerDlsym</c> item into the linker's options.
/// </summary>
public sealed class RuntimeTvosDlsymTargetsTests : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "swift-runtime-dlsym-" + Guid.NewGuid().ToString("N"));

    public RuntimeTvosDlsymTargetsTests() => Directory.CreateDirectory(tempDir);

    public void Dispose() => Directory.Delete(tempDir, recursive: true);

    [Fact]
    public void TvosMonoApp_ResolvesEveryNonSdkAssemblyThroughDlsym()
    {
        Assert.Equal(
            ["Dlsym=+Swift.Runtime.dll", "Dlsym=+SwiftBindings.Apple.dll", "Dlsym=+My Binding.dll", "Dlsym=+ConsumerApp.dll"],
            Run("net10.0-tvos", useMonoRuntime: true));
    }

    [Theory]
    [InlineData("net10.0-ios", true)]         // iOS already resolves non-SDK assemblies through dlsym
    [InlineData("net10.0-maccatalyst", true)] // and so does Mac Catalyst
    [InlineData("net10.0-tvos", false)]       // NativeAOT does not use the Mono bundler's dlsym choice
    [InlineData("net11.0-tvos", false)]       // CoreCLR compiles no P/Invoke wrappers into the app
    public void OtherPlatformsAndEngines_AreLeftAlone(string tfm, bool useMonoRuntime) =>
        Assert.Empty(Run(tfm, useMonoRuntime));

    [Fact]
    public void AppThatChoosesItsOwnDlsym_IsLeftAlone() =>
        Assert.Equal(["Dlsym=false"], Run("net10.0-tvos", useMonoRuntime: true, appDlsym: "false"));

    [Fact]
    public void LibraryProducer_IsLeftAlone() =>
        Assert.Empty(Run("net10.0-tvos", useMonoRuntime: true, outputType: "Library"));

    private string[] Run(string tfm, bool useMonoRuntime, string appDlsym = "", string outputType = "Exe")
    {
        string target = Path.Combine(FindRepoRoot(), "src/Swift.Runtime/src/build/SwiftBindings.Runtime.targets");
        string optionsFile = Path.Combine(tempDir, "linker-options.txt");
        string project = Path.Combine(tempDir, "dlsym.proj");
        File.WriteAllText(project, $"""
            <Project>
              <PropertyGroup><TargetFramework>{tfm}</TargetFramework></PropertyGroup>
              <Import Project="{SecurityElement.Escape(target)}" />
              <PropertyGroup>
                <OutputType>{outputType}</OutputType>
                <AssemblyName>ConsumerApp</AssemblyName>
                <UseMonoRuntime>{useMonoRuntime.ToString().ToLowerInvariant()}</UseMonoRuntime>
              </PropertyGroup>
              <Target Name="_ParseBundlerArguments">
                {(appDlsym == "" ? "" : $"<ItemGroup><_BundlerDlsym Include=\"{appDlsym}\" /></ItemGroup>")}
              </Target>
              <Target Name="ResolveAssemblyReferences">
                <ItemGroup>
                  <ReferencePath Include="/refs/System.Runtime.dll" FrameworkReferenceName="Microsoft.NETCore.App" />
                  <ReferencePath Include="/refs/Microsoft.tvOS.dll" FrameworkReferenceName="Microsoft.tvOS" />
                  <ReferencePath Include="/packages/Swift.Runtime.dll" NuGetPackageId="SwiftBindings.Runtime" />
                  <ReferencePath Include="/packages/SwiftBindings.Apple.dll" NuGetPackageId="SwiftBindings.Apple" />
                  <ReferencePath Include="/projects/My Binding.dll" />
                </ItemGroup>
              </Target>
              <Target Name="_ComputeLinkerInputs">
                <WriteLinesToFile File="{SecurityElement.Escape(optionsFile)}" Lines="@(_BundlerDlsym->'Dlsym=%(Identity)')" Overwrite="true" />
              </Target>
            </Project>
            """);
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "msbuild", project, "-nologo", "-v:quiet", "-t:_ParseBundlerArguments;ResolveAssemblyReferences;_ComputeLinkerInputs" })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Runtime dlsym MSBuild fixture timed out.");
        }
        Assert.True(process.ExitCode == 0, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        return File.Exists(optionsFile) ? File.ReadAllLines(optionsFile) : [];
    }

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src/Swift.Runtime/src/build/SwiftBindings.Runtime.targets")))
                return directory.FullName;
        throw new InvalidOperationException("Could not locate Runtime buildTransitive targets.");
    }
}
