// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Packaged SwiftBindings.Apple NativeAOT consumer gate (OWNER-03 D09).
// Builds throwaway Runtime + Apple packages, publishes a one-PackageReference
// iOS app with no app-authored trimmer roots, then materializes the elements of
// a SwiftArray<Foundation.Locale.Language> on a physical device.

using System;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    [Parameter("Opt-in: pack SwiftBindings.Runtime + SwiftBindings.Apple, then NativeAOT-publish and run a no-extra-TrimmerRoots iOS device consumer that materializes SwiftArray<Locale.Language> elements.")]
    readonly bool ApplePackageConsumer;

    const string ApplePackageConsumerVersion = "0.0.0-b2";
    const string ApplePackageConsumerAppleVersion = "26.2.0-b2";
    const string ApplePackageConsumerAppName = "ApplePackageConsumerApp";
    const string ApplePackageConsumerBundleId = "com.swiftbindings.applepackageconsumer";

    // Keep the generated app outside the repository tree so MSBuild cannot discover and inherit
    // the repo's Directory.Build.props/targets. The consumer must prove the package's own roots.
    AbsolutePath ApplePackageConsumerScratch =>
        (AbsolutePath)Path.Combine(Path.GetTempPath(), "swift-bindings-apple-package-consumer");

    void RunApplePackageConsumerLeg()
    {
        Log.Information("========================================================");
        Log.Information(" BindingTests — packaged Apple NativeAOT rooting consumer");
        Log.Information("========================================================");

        RunBuildAppleSupplementXcframework();

        var scratch = ApplePackageConsumerScratch;
        if (Directory.Exists(scratch))
            scratch.DeleteDirectory();
        var packages = scratch / "packages";
        var appDir = scratch / "consumer";
        packages.CreateDirectory();
        appDir.CreateDirectory();

        using (var scope = new VersionScope(
            ApplePackageConsumerVersion, RootDirectory, ApplePackageConsumerAppleVersion))
        {
            foreach (var project in new[]
            {
                SourceDir / "Swift.Runtime" / "src" / "Swift.Runtime.csproj",
                SourceDir / "Swift.Bindings.Apple" / "Swift.Bindings.Apple.csproj",
            })
            {
                DotNetPack(s => scope.Apply(s
                    .SetProject(project)
                    .SetConfiguration("Release")
                    .SetOutputDirectory(packages)
                    .EnableNoLogo()
                    .SetVerbosity(DotNetVerbosity.quiet)));
            }
        }

        // The throwaway versions are intentionally stable so repeated local runs are cheap,
        // but NuGet's global-packages cache is content-addressed only by id/version. Remove just
        // these two exact entries so a prior run cannot shadow the packages built above.
        var globalPackages = (AbsolutePath)(Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget", "packages"));
        foreach (var (id, version) in new[]
        {
            ("swiftbindings.runtime", ApplePackageConsumerVersion),
            ("swiftbindings.apple", ApplePackageConsumerAppleVersion),
        })
        {
            var cached = globalPackages / id / version;
            if (Directory.Exists(cached))
                cached.DeleteDirectory();
        }

        WriteApplePackageConsumer(appDir, packages);

        var consumerProject = appDir / $"{ApplePackageConsumerAppName}.csproj";
        var consumerProjectText = File.ReadAllText(consumerProject);
        if (consumerProjectText.Contains("TrimmerRootDescriptor", StringComparison.Ordinal)
            || consumerProjectText.Contains("--descriptor:", StringComparison.Ordinal)
            || consumerProjectText.Contains("<IlcArg", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "--apple-package-consumer: the app declares an extra trimming root. " +
                "This gate must model an ordinary package consumer with package-provided roots only.");
        }

        Log.Information("=== apple-package-consumer: NativeAOT publishing no-extra-roots app ===");
        DotNetPublish(s => s
            .SetProject(consumerProject)
            .SetConfiguration("Release")
            .SetRuntime("ios-arm64")
            .EnableNoLogo()
            .SetVerbosity(DotNetVerbosity.quiet));

        var publishSegment = $"{Path.DirectorySeparatorChar}publish{Path.DirectorySeparatorChar}";
        var appPath = Directory
            .GetDirectories(appDir / "bin", $"{ApplePackageConsumerAppName}.app", SearchOption.AllDirectories)
            .Where(path => path.Contains("Release", StringComparison.Ordinal)
                && path.Contains("ios-arm64", StringComparison.Ordinal))
            .OrderByDescending(path => path.Contains(publishSegment, StringComparison.Ordinal))
            .ThenBy(path => path.Length)
            .ThenBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"--apple-package-consumer: {ApplePackageConsumerAppName}.app not found after publish.");

        var device = !string.IsNullOrEmpty(DeviceUdid)
            ? new DeviceCtl.PhysicalDevice(DeviceUdid, "specified")
            : DeviceCtl.ListDevices().FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "--apple-package-consumer: no connected iOS device found. Connect an iPhone " +
                    "or pass --device-udid UDID.");

        Log.Information("=== apple-package-consumer: install + launch on {Name} ({Udid}) ===",
            device.Name, device.Udid);
        var result = LaunchUntilAppRuns(() =>
        {
            DeviceCtl.Install(device.Udid, appPath);
            return DeviceCtl.Launch(
                device.Udid,
                ApplePackageConsumerBundleId,
                Array.Empty<string>(),
                TimeSpan.FromSeconds(Timeout));
        }, "--apple-package-consumer (device NativeAOT)");

        Log.Information("");
        Log.Information("=== CONSUMER OUTPUT (Apple package, device NativeAOT) ===");
        Log.Information(result.Output);

        if (LaunchDiagnostics.LauncherNeverStartedApp(result))
            throw new InvalidOperationException(
                "--apple-package-consumer: the launcher never started the app; this is a " +
                $"device infrastructure failure, not a binding verdict.\n{result.Output}");
        if (!result.Output.Contains("LANGUAGE_ELEMENTS:2", StringComparison.Ordinal)
            || result.Result != TestResult.Success)
        {
            throw new InvalidOperationException(
                "--apple-package-consumer: the packaged NativeAOT app did not materialize both " +
                $"Locale.Language elements.\n{result.Output}");
        }

        Log.Information(
            "--apple-package-consumer PASS — packaged roots materialized two Locale.Language elements under NativeAOT.");
    }

    static void WriteApplePackageConsumer(AbsolutePath appDir, AbsolutePath packages)
    {
        File.WriteAllText(appDir / $"{ApplePackageConsumerAppName}.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0-ios</TargetFramework>
                <RuntimeIdentifier>ios-arm64</RuntimeIdentifier>
                <PublishAot>true</PublishAot>
                <PublishAotUsingRuntimePack>true</PublishAotUsingRuntimePack>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <ApplicationId>{{ApplePackageConsumerBundleId}}</ApplicationId>
                <ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>
                <ApplicationVersion>1</ApplicationVersion>
                <SupportedOSPlatformVersion>15.0</SupportedOSPlatformVersion>
                <NoWarn>$(NoWarn);CA1416;CA1422</NoWarn>
                <CodesignKey>Apple Development: Justin Wojciechowski (KBKS29A36Q)</CodesignKey>
                <CodesignProvision>Wildcard Dev</CodesignProvision>
                <TeamIdentifierPrefix>TL2K6QUQEH</TeamIdentifierPrefix>
              </PropertyGroup>
              <ItemGroup>
                <AssemblyAttribute Include="System.Runtime.CompilerServices.DisableRuntimeMarshallingAttribute" />
                <None Include="Info.plist" />
                <PackageReference Include="SwiftBindings.Apple" Version="{{ApplePackageConsumerAppleVersion}}" />
              </ItemGroup>
            </Project>
            """);

        File.WriteAllText(appDir / "Program.cs", $$"""
            // Copyright (c) 2026 Justin Wojciechowski.
            // Licensed under the MIT License.

            using System.Runtime.InteropServices;
            using CoreFoundation;
            using Foundation;
            using Swift;
            using Swift.Runtime;
            using UIKit;

            namespace ApplePackageConsumerApp;

            public static class Application
            {
                static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
            }

            public sealed class AppDelegate : UIApplicationDelegate
            {
                public override UIWindow? Window { get; set; }

                [DllImport("SBApple", CallingConvention = CallingConvention.Cdecl,
                    EntryPoint = "SBW_AppleSupplement_Probe_CreateLocaleLanguages")]
                private static extern void CreateLocaleLanguages(IntPtr result);

                public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
                {
                    Window = new UIWindow(UIScreen.MainScreen.Bounds);
                    Window.RootViewController = new UIViewController();
                    Window.MakeKeyAndVisible();
                    CoreFoundation.DispatchQueue.MainQueue.DispatchAsync(RunProbe);
                    return true;
                }

                private static unsafe void RunProbe()
                {
                    try
                    {
                        if (!OperatingSystem.IsIOSVersionAtLeast(16))
                            throw new PlatformNotSupportedException("Locale.Language requires iOS 16 or newer.");

                        var metadata = SwiftObjectHelper<SwiftArray<Swift.Foundation.Locale.Language>>
                            .GetTypeMetadata();
                        void* source = NativeMemory.Alloc(metadata.Size);
                        var initialized = false;
                        SwiftArray<Swift.Foundation.Locale.Language>? languages = null;
                        try
                        {
                            CreateLocaleLanguages((IntPtr)source);
                            initialized = true;
                            languages = (SwiftArray<Swift.Foundation.Locale.Language>)
                                SwiftObjectHelper<SwiftArray<Swift.Foundation.Locale.Language>>
                                    .NewFromPayload((IntPtr)source);
                        }
                        finally
                        {
                            if (initialized)
                                metadata.ValueWitnessTable->Destroy(source, metadata);
                            NativeMemory.Free(source);
                        }

                        using (languages)
                        {
                            var values = languages.ToArray();
                            try
                            {
                                if (values.Length != 2 || values.Any(value =>
                                        !ReferenceEquals(
                                            value.GetType(),
                                            typeof(Swift.Foundation.Locale.Language))))
                                {
                                    throw new InvalidOperationException(
                                        "SwiftArray did not materialize two canonical Locale.Language values.");
                                }
                                Console.WriteLine("LANGUAGE_ELEMENTS:" + values.Length);
                            }
                            finally
                            {
                                foreach (var value in values)
                                    value.Dispose();
                            }
                        }

                        Console.WriteLine("RESULTS FLUSHED");
                        Console.WriteLine("TEST SUCCESS");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("RESULTS FLUSHED");
                        Console.WriteLine("TEST FAILURE: " + ex);
                    }
                }
            }
            """);

        File.WriteAllText(appDir / "Info.plist",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
            "<plist version=\"1.0\">\n<dict>\n" +
            "    <key>UILaunchScreen</key>\n    <dict/>\n" +
            "</dict>\n</plist>\n");

        File.WriteAllText(appDir / "NuGet.config", $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="apple-consumer-local" value="{{packages}}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="apple-consumer-local">
                  <package pattern="SwiftBindings.*" />
                </packageSource>
                <packageSource key="nuget.org">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);
    }
}
