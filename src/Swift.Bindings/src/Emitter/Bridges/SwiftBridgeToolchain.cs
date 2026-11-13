// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;

namespace BindingsGeneration;

/// <summary>
/// The Xcode tools the bridge checks run for one platform: the assembler and linker (through
/// <c>clang</c>), <c>nm</c>, <c>llvm-objdump</c> and <c>llvm-dwarfdump</c>, all through <c>xcrun</c>.
/// A bridge is the same machine code on every platform; what differs is the target triple and SDK it
/// is assembled and linked against, which are the platform's evidence slices
/// (<see cref="SwiftArtifactScope.PlatformSlices"/>).
/// </summary>
public sealed class SwiftBridgeToolchain
{
    private readonly string _xcrun;
    private readonly Dictionary<string, string> _sdks = new(StringComparer.Ordinal);

    private SwiftBridgeToolchain(string xcrun, string platform)
    {
        _xcrun = xcrun;
        Platform = platform;
    }

    /// <summary>The platform lane the bridges are assembled for (<c>ios</c>, <c>maccatalyst</c>, …).</summary>
    public string Platform { get; }

    /// <summary>
    /// The toolchain on this host for <paramref name="platform"/>, or null when there is none (not
    /// macOS, no Xcode, or no SDK of one of the platform's bridge targets).
    /// </summary>
    public static SwiftBridgeToolchain? Locate(string platform = "ios")
    {
        var sdks = Architectures.Select(a => TargetOf(platform, a).Sdk).Distinct(StringComparer.Ordinal).ToList();
        if (!OperatingSystem.IsMacOS() || !File.Exists("/usr/bin/xcrun"))
            return null;
        var toolchain = new SwiftBridgeToolchain("/usr/bin/xcrun", platform);
        return sdks.All(sdk => toolchain.Sdk(sdk) is not null) ? toolchain : null;
    }

    /// <summary>The architectures a bridge is assembled for on every platform.</summary>
    public static readonly IReadOnlyList<SwiftArchitecture> Architectures = [SwiftArchitecture.Arm64, SwiftArchitecture.X86_64];

    /// <summary>The SDKs <paramref name="platform"/>'s bridge targets build against, for a diagnostic.</summary>
    public static IReadOnlyList<string> SdksOf(string platform) =>
        Architectures.Select(a => TargetOf(platform, a).Sdk).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// The target triple and SDK a bridge of <paramref name="architecture"/> is assembled for on
    /// <paramref name="platform"/>: the platform's first evidence slice of that architecture (on iOS
    /// and tvOS the arm64 device and the x86_64 simulator; on Mac Catalyst and macOS the one slice
    /// of each architecture).
    /// </summary>
    public static (string Triple, string Sdk) TargetOf(string platform, SwiftArchitecture architecture)
    {
        var prefix = architecture switch
        {
            SwiftArchitecture.Arm64 => "arm64-",
            SwiftArchitecture.X86_64 => "x86_64-",
            _ => throw new ArgumentOutOfRangeException(nameof(architecture), architecture, "no bridge target"),
        };
        var triple = SwiftArtifactScope.PlatformSlices(platform).FirstOrDefault(s => s.StartsWith(prefix, StringComparison.Ordinal))
            ?? throw new ArgumentException($"the {platform} platform claims no {architecture} slice", nameof(platform));
        return (triple, SwiftArtifactScope.SdkOf(triple));
    }

    /// <summary>The target triple and SDK a bridge of <paramref name="architecture"/> is assembled for on this toolchain's platform.</summary>
    public (string Triple, string Sdk) TargetOf(SwiftArchitecture architecture) => TargetOf(Platform, architecture);

    /// <summary>
    /// The platforms whose evidence slices <paramref name="slices"/> claim, in the order
    /// <see cref="KnownPlatforms"/> lists them. A slice no platform claims is ignored.
    /// </summary>
    public static IReadOnlyList<string> PlatformsOf(IEnumerable<string> slices)
    {
        var claimed = slices.ToHashSet(StringComparer.Ordinal);
        return KnownPlatforms.Where(p => SwiftArtifactScope.PlatformSlices(p).Any(claimed.Contains)).ToList();
    }

    /// <summary>Every platform lane that defines evidence slices.</summary>
    public static readonly IReadOnlyList<string> KnownPlatforms = ["ios", "tvos", "maccatalyst", "macos"];

    public string? Sdk(string sdk)
    {
        lock (_sdks)
        {
            if (_sdks.TryGetValue(sdk, out var cached))
                return cached;
            var (code, output, _) = Run("--sdk", sdk, "--show-sdk-path");
            var path = code == 0 ? output.Trim() : null;
            if (path is not null && Directory.Exists(path))
                _sdks[sdk] = path;
            return path is not null && Directory.Exists(path) ? path : null;
        }
    }

    /// <summary>Assembles <paramref name="sourcePath"/> into an object for the bridge target of <paramref name="architecture"/>.</summary>
    public (bool Ok, string Output) Assemble(string sourcePath, string objectPath, SwiftArchitecture architecture)
    {
        var (triple, sdk) = TargetOf(architecture);
        var (code, output, error) = Run("clang", "-c", "-target", triple, "-isysroot", Sdk(sdk)!, sourcePath, "-o", objectPath);
        return (code == 0 && error.Length == 0, output + error);
    }

    /// <summary>
    /// Links objects into a scratch dynamic library, leaving <paramref name="undefined"/> to be found
    /// at load time: only so the linker writes the image's final unwind sections.
    /// </summary>
    public (bool Ok, string Output) LinkScratch(IEnumerable<string> objects, string imagePath, SwiftArchitecture architecture, IEnumerable<string> undefined)
    {
        var (triple, sdk) = TargetOf(architecture);
        var arguments = new List<string> { "clang", "-dynamiclib", "-target", triple, "-isysroot", Sdk(sdk)!, "-o", imagePath };
        foreach (var symbol in undefined)
            arguments.Add("-Wl,-U," + symbol);
        arguments.AddRange(objects);
        var (code, output, error) = Run(arguments.ToArray());
        return (code == 0, output + error);
    }

    /// <summary>Runs an Xcode tool and returns its exit code, standard output and standard error.</summary>
    public (int Code, string Output, string Error) Run(params string[] arguments)
    {
        var info = new ProcessStartInfo(_xcrun)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("xcrun did not start");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, output.Result, error.Result);
    }
}
