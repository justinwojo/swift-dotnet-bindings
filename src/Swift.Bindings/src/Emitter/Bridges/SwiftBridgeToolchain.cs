// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;

namespace BindingsGeneration;

/// <summary>
/// The Xcode tools the bridge checks run: the assembler and linker (through <c>clang</c>),
/// <c>nm</c>, <c>llvm-objdump</c> and <c>llvm-dwarfdump</c>, all through <c>xcrun</c>.
/// </summary>
public sealed class SwiftBridgeToolchain
{
    private readonly string _xcrun;
    private readonly Dictionary<string, string> _sdks = new(StringComparer.Ordinal);

    private SwiftBridgeToolchain(string xcrun) => _xcrun = xcrun;

    /// <summary>The toolchain on this host, or null when there is none (not macOS, or no Xcode).</summary>
    public static SwiftBridgeToolchain? Locate()
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists("/usr/bin/xcrun"))
            return null;
        var toolchain = new SwiftBridgeToolchain("/usr/bin/xcrun");
        return toolchain.Sdk("iphoneos") is not null && toolchain.Sdk("iphonesimulator") is not null ? toolchain : null;
    }

    /// <summary>The target triple and SDK a bridge of <paramref name="architecture"/> is assembled for: the device, or the x86_64 simulator.</summary>
    public static (string Triple, string Sdk) TargetOf(SwiftArchitecture architecture) => architecture switch
    {
        SwiftArchitecture.Arm64 => ("arm64-apple-ios15.0", "iphoneos"),
        SwiftArchitecture.X86_64 => ("x86_64-apple-ios15.0-simulator", "iphonesimulator"),
        _ => throw new ArgumentOutOfRangeException(nameof(architecture), architecture, "no bridge target"),
    };

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
