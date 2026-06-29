// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration.ObjC;

/// <summary>
/// Result of umbrella header resolution.
/// </summary>
public sealed record UmbrellaHeaderResult(string HeaderPath, string? ModulemapPath = null);

/// <summary>
/// Invokes xcrun clang to produce AST JSON from ObjC headers.
/// </summary>
public sealed class ClangAstInvoker
{
    private readonly ICommandRunner _commandRunner;
    private readonly ILogger _logger;

    public ClangAstInvoker(ICommandRunner commandRunner, ILogger logger)
    {
        _commandRunner = commandRunner;
        _logger = logger;
    }

    /// <summary>
    /// Invokes clang to dump the AST of the given header as JSON.
    /// When modulemapPath is provided, -fmodules is enabled (needed for @import strategy).
    /// </summary>
    public string InvokeClangAstDump(string headerPath, string frameworkSearchPath, bool isSimulator,
        string? modulemapPath = null, IReadOnlyList<string>? additionalFrameworkSearchPaths = null,
        SliceVariant? sliceVariant = null)
    {
        string sdkName;
        if (sliceVariant != null)
        {
            sdkName = sliceVariant.SdkName;
        }
        else
        {
            sdkName = isSimulator ? "iphonesimulator" : "iphoneos";
        }
        var (sdkExit, sdkPath, sdkErr) = _commandRunner.Run("xcrun", $"--sdk {sdkName} --show-sdk-path");
        if (sdkExit != 0 || string.IsNullOrWhiteSpace(sdkPath))
        {
            throw new InvalidOperationException(
                $"Failed to locate SDK ({sdkName}). Ensure Xcode and the platform SDK are installed. stderr: {sdkErr}");
        }

        // -fobjc-arc: dump the AST under ARC so an umbrella header that cross-imports an ARC
        // framework (the common case for third-party SDKs) matches its ownership model instead of
        // failing the parse with an ownership mismatch.
        var baseArgs = $"clang -x objective-c -fobjc-arc -Xclang -ast-dump=json " +
                   $"-isysroot \"{sdkPath}\" " +
                   $"-F \"{frameworkSearchPath}\" ";

        // XCTest (and other test-support frameworks pulled in by libraries like Quick/Nimble)
        // does NOT ship in the SDK — it lives under the platform's Developer/Library/Frameworks.
        // Add that directory so a header doing `@import XCTest;` / `#import <XCTest/XCTest.h>`
        // resolves; without it clang fails with "'XCTest/XCTest.h' file not found" and the whole
        // module fails to build. Best-effort: if the platform-path lookup fails or the directory
        // is absent we just omit the extra -F, leaving non-test-importing libraries unaffected.
        var platformFrameworks = ResolvePlatformDeveloperFrameworks(sdkName);
        if (platformFrameworks != null)
            baseArgs += $"-F \"{platformFrameworks}\" ";

        if (additionalFrameworkSearchPaths != null)
        {
            foreach (var path in additionalFrameworkSearchPaths)
                baseArgs += $"-F \"{path}\" ";
        }

        if (modulemapPath != null)
            baseArgs += $"-fmodules -fmodule-map-file=\"{modulemapPath}\" ";

        var args = baseArgs + $"-fsyntax-only \"{headerPath}\"";

        _logger.LogInformation("Invoking clang AST dump: xcrun {Args}", args);

        var (exitCode, stdout, stderr) = _commandRunner.Run("xcrun", args, timeoutMs: 120000);

        // Retry with -fmodules if a header uses @import without modules enabled
        if (exitCode != 0 && modulemapPath == null &&
            stderr != null && stderr.Contains("use of '@import' when modules are disabled"))
        {
            _logger.LogInformation("Retrying with -fmodules (header uses @import)");
            args = baseArgs + $"-fmodules -fsyntax-only \"{headerPath}\"";
            (exitCode, stdout, stderr) = _commandRunner.Run("xcrun", args, timeoutMs: 120000);
        }

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Clang AST dump failed (exit {exitCode}): {stderr}");
        }

        if (string.IsNullOrWhiteSpace(stdout))
        {
            throw new InvalidOperationException(
                "Clang AST dump returned empty output.");
        }

        return stdout;
    }

    /// <summary>
    /// Resolves the platform's <c>Developer/Library/Frameworks</c> directory (where XCTest and
    /// other test-support frameworks live) for the given SDK, or null if it can't be located.
    /// Best-effort by design: a missing platform path simply means the extra <c>-F</c> is omitted.
    /// </summary>
    private string? ResolvePlatformDeveloperFrameworks(string sdkName)
    {
        var (exit, platformPath, _) = _commandRunner.Run("xcrun", $"--sdk {sdkName} --show-sdk-platform-path");
        if (exit != 0 || string.IsNullOrWhiteSpace(platformPath))
        {
            _logger.LogInformation("Could not locate platform path for SDK '{Sdk}'; skipping Developer/Library/Frameworks search path.", sdkName);
            return null;
        }

        var frameworks = Path.Combine(platformPath.Trim(), "Developer", "Library", "Frameworks");
        if (!Directory.Exists(frameworks))
        {
            _logger.LogInformation("Platform frameworks directory '{Path}' not found; skipping.", frameworks);
            return null;
        }

        return frameworks;
    }

    /// <summary>
    /// Finds the umbrella header to pass to clang for the given framework.
    /// Returns null if no suitable header can be found.
    /// When using the directory umbrella strategy, the result includes a modulemap path
    /// that enables -fmodules for the @import approach.
    /// </summary>
    public UmbrellaHeaderResult? FindUmbrellaHeader(string frameworkPath, string moduleName)
    {
        var headersDir = Path.Combine(frameworkPath, "Headers");
        var modulesDir = Path.Combine(frameworkPath, "Modules");
        var modulemapPath = Path.Combine(modulesDir, "module.modulemap");

        // 1. Convention: Headers/{moduleName}.h
        var conventionHeader = Path.Combine(headersDir, $"{moduleName}.h");
        if (File.Exists(conventionHeader))
        {
            _logger.LogInformation("Found umbrella header by convention: {Path}", conventionHeader);
            return new UmbrellaHeaderResult(conventionHeader);
        }

        // 2-4. Parse modulemap for umbrella header directives
        if (!File.Exists(modulemapPath))
        {
            _logger.LogWarning("No module.modulemap found at {Path}", modulemapPath);
            return null;
        }

        var modulemapContent = File.ReadAllText(modulemapPath);
        var lines = modulemapContent.Split('\n');

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            // 2. umbrella header "X.h"
            if (line.StartsWith("umbrella header", StringComparison.Ordinal))
            {
                var headerName = ExtractQuotedString(line);
                if (headerName != null)
                {
                    var path = Path.Combine(headersDir, headerName);
                    if (File.Exists(path))
                    {
                        _logger.LogInformation("Found umbrella header from modulemap directive: {Path}", path);
                        return new UmbrellaHeaderResult(path);
                    }
                }
            }

            // 3. umbrella "Headers" (directory umbrella)
            if (line.StartsWith("umbrella \"", StringComparison.Ordinal) &&
                !line.StartsWith("umbrella header", StringComparison.Ordinal))
            {
                // Generate temp file with @import — needs -fmodules
                var importFile = CreateModuleImportFile(moduleName);
                return new UmbrellaHeaderResult(importFile, modulemapPath);
            }
        }

        // 4. Collect explicit header entries
        var explicitHeaders = new List<string>();
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if ((line.StartsWith("header ", StringComparison.Ordinal) ||
                 line.StartsWith("export header ", StringComparison.Ordinal)) &&
                !line.StartsWith("umbrella", StringComparison.Ordinal))
            {
                var headerName = ExtractQuotedString(line);
                if (headerName != null)
                {
                    var path = Path.Combine(headersDir, headerName);
                    if (File.Exists(path))
                        explicitHeaders.Add(path);
                }
            }
        }

        if (explicitHeaders.Count > 0)
        {
            _logger.LogInformation("Creating combined header from {Count} explicit modulemap entries", explicitHeaders.Count);
            return new UmbrellaHeaderResult(CreateCombinedHeaderFile(explicitHeaders, moduleName));
        }

        _logger.LogWarning("Could not locate umbrella header for module '{Module}'", moduleName);
        return null;
    }

    private string CreateModuleImportFile(string moduleName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"objc_binding_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, $"{moduleName}_import.m");
        File.WriteAllText(tempFile, $"@import {moduleName};\n");
        _logger.LogInformation("Created module import file for directory umbrella: {Path}", tempFile);
        return tempFile;
    }

    private string CreateCombinedHeaderFile(List<string> headers, string moduleName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"objc_binding_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, $"{moduleName}_combined.h");
        var imports = string.Join("\n", headers.Select(h => $"#import \"{h}\""));
        File.WriteAllText(tempFile, imports + "\n");
        _logger.LogInformation("Created combined header file from {Count} headers: {Path}", headers.Count, tempFile);
        return tempFile;
    }

    private static string? ExtractQuotedString(string line)
    {
        var firstQuote = line.IndexOf('"');
        if (firstQuote < 0) return null;
        var secondQuote = line.IndexOf('"', firstQuote + 1);
        if (secondQuote < 0) return null;
        return line[(firstQuote + 1)..secondQuote];
    }
}
