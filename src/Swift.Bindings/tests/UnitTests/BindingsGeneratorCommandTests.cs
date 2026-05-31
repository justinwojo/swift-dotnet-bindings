// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.CommandLine;
using Xunit;

namespace BindingsGeneration.Tests;

// ═══════════════════════════════════════════════════════════════════════
// BindingsGeneratorCommand: small CLI-level helpers
// ═══════════════════════════════════════════════════════════════════════

#region IsSystemFrameworkTarget gate

/// <summary>
/// The direct-mode wrapper-compile and csproj-emit branches in
/// <see cref="BindingsGeneratorCommand.Execute"/> are gated on
/// <c>IsSystemFrameworkTarget</c>. The gate must say "yes" only when
/// the user is targeting an Apple system framework via <c>-l @rpath/...</c>
/// or <c>-l /System/Library/...</c> AND has not supplied an
/// <c>--xcframework</c>. Anything else (xcframework mode, plain user
/// dylibs, missing library name) must keep the legacy "emit C# only,
/// don't try to package or compile a wrapper" behavior — that contract
/// is what keeps non-system manual workflows from breaking.
/// </summary>
public class IsSystemFrameworkTargetTests
{
    [Theory]
    [InlineData("@rpath/StoreKit.framework/StoreKit")]
    [InlineData("@rpath/Foundation.framework/Foundation")]
    [InlineData("/System/Library/Frameworks/StoreKit.framework/StoreKit")]
    [InlineData("/System/Library/PrivateFrameworks/Whatever.framework/Whatever")]
    public void RecognizesSystemFrameworkLibraryNames(string libraryName)
    {
        Assert.True(BindingsGeneratorCommand.IsSystemFrameworkTarget(hasXcframework: false, libraryName));
    }

    [Theory]
    [InlineData("/usr/local/lib/libfoo.dylib")]
    [InlineData("libfoo.dylib")]
    [InlineData("MyCustomLib")]
    [InlineData("/Users/me/build/MyLib.framework/MyLib")]
    public void RejectsNonSystemLibraryNames(string libraryName)
    {
        Assert.False(BindingsGeneratorCommand.IsSystemFrameworkTarget(hasXcframework: false, libraryName));
    }

    [Fact]
    public void XcframeworkAlwaysWinsOverLibraryName()
    {
        // --xcframework mode is the canonical packaged-binary path; even if the
        // user *also* passes -l @rpath/... we must stay in xcframework mode and
        // not flip into the direct-mode system-framework branch (which would
        // emit a system-framework csproj over the top of an xcframework one).
        Assert.False(BindingsGeneratorCommand.IsSystemFrameworkTarget(
            hasXcframework: true,
            libraryName: "@rpath/StoreKit.framework/StoreKit"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmptyLibraryNameIsNotSystemFramework(string? libraryName)
    {
        Assert.False(BindingsGeneratorCommand.IsSystemFrameworkTarget(hasXcframework: false, libraryName));
    }
}

#endregion

#region ResolveRuntimeLibraryName

/// <summary>
/// The library name baked into generated [LibraryImport]/LoadFromSymbol strings must be the
/// BARE framework name for Apple system-framework targets (so SwiftFrameworkResolver maps it
/// to /System on a physical device), and unchanged for everything else. A generic system
/// type's metadata accessor — which has no @_cdecl wrapper primary to fall back from — would
/// otherwise emit a raw @rpath path that throws DllNotFoundException on a NativeAOT device.
/// This is the T1.3 apple-framework-gaps fix (CryptoKit HMAC&lt;H&gt; on device).
/// </summary>
public class ResolveRuntimeLibraryNameTests
{
    [Theory]
    [InlineData("@rpath/CryptoKit.framework/CryptoKit", "CryptoKit")]
    [InlineData("/System/Library/Frameworks/CryptoKit.framework/CryptoKit", "CryptoKit")]
    [InlineData("@rpath/RealityKit.framework/RealityKit", "RealityKit")]
    public void SystemFramework_ReducesToBareModuleName(string embedded, string moduleName)
    {
        var resolved = BindingsGeneratorCommand.ResolveRuntimeLibraryName(
            embedded, moduleName, isSystemFrameworkTarget: true);

        Assert.Equal(moduleName, resolved);
        Assert.DoesNotContain(".framework/", resolved);
        Assert.DoesNotContain("@rpath", resolved);
    }

    [Theory]
    [InlineData("@rpath/MyLib.framework/MyLib", "MyLib")]
    [InlineData("/path/to/libMyLib.dylib", "MyLib")]
    public void NonSystemFramework_ReturnsEmbeddedNameUnchanged(string embedded, string moduleName)
    {
        Assert.Equal(
            embedded,
            BindingsGeneratorCommand.ResolveRuntimeLibraryName(embedded, moduleName, isSystemFrameworkTarget: false));
    }

    [Fact]
    public void SystemFramework_MissingModuleName_LeavesEmbeddedNameUnchanged()
    {
        // Defensive: never collapse to an empty library name when the module name is absent.
        const string embedded = "@rpath/CryptoKit.framework/CryptoKit";
        Assert.Equal(embedded, BindingsGeneratorCommand.ResolveRuntimeLibraryName(embedded, "", true));
        Assert.Equal(embedded, BindingsGeneratorCommand.ResolveRuntimeLibraryName(embedded, null, true));
    }
}

#endregion

#region --apple-version forwarding into BindingProjectEmitter

/// <summary>
/// <c>--apple-version</c> must flow into <c>BindingProjectEmitterOptions.AppleSupplementVersion</c>
/// from BOTH emitter call sites in <c>BindingsGeneratorCommand.Execute</c>:
/// the xcframework path and the direct system-framework path. If either call site drops the field,
/// the emitted <c>SwiftBindings.Apple</c> PackageReference falls back to the hardcoded
/// default in <c>BindingProjectEmitterOptions</c> and consumers silently target the wrong
/// Apple SDK train. Unit tests on <c>BindingProjectEmitter</c> already cover end-to-end
/// csproj content for a non-default version; this source-level guard catches a future
/// regression in the command layer without spinning up the full CLI pipeline (which
/// requires real dylib/ABI-JSON artifacts).
/// </summary>
public class AppleVersionForwardingTests
{
    [Fact]
    public void BothEmitterCallSites_ForwardAppleVersion()
    {
        var commandFile = LocateCommandFile();
        var source = File.ReadAllText(commandFile);
        // Count is asserted ==2 (xcframework + direct-framework). A weaker >=1 check would
        // have let the direct-framework regression Codex flagged slip through.
        var occurrences = System.Text.RegularExpressions.Regex.Matches(
            source, @"AppleSupplementVersion\s*=\s*appleVersion\s*,").Count;
        Assert.Equal(2, occurrences);
    }

    private static string LocateCommandFile()
    {
        // Walk up from the test assembly to the repo root, then down to the source file.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SwiftBindings.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(
            dir!.FullName, "src", "Swift.Bindings", "src", "BindingsGeneratorCommand.cs");
        Assert.True(File.Exists(path), $"Command source not found at {path}");
        return path;
    }
}

#endregion

#region --platform-version CLI option

/// <summary>
/// The <c>--platform-version</c> flag lets the Apple-framework publishing flow stamp a
/// concrete OS version (e.g. iOS 17.0) into generated artifacts. It must (a) parse
/// cleanly off the CLI, (b) default to null when not supplied so existing callers don't
/// break, and (c) be in the option set returned by
/// <see cref="CliOptions.CreateRootCommand"/> so System.CommandLine actually picks it up.
/// </summary>
public class PlatformVersionCliOptionTests
{
    [Fact]
    public void PlatformVersionOption_Parses_FromCommandLine()
    {
        var opts = new CliOptions();
        var root = opts.CreateRootCommand();
        var parsed = root.Parse(new[] { "--platform-version", "26.2" });
        var value = parsed.GetValueForOption(opts.PlatformVersion);
        Assert.Equal("26.2", value);
    }

    [Fact]
    public void PlatformVersionOption_DefaultsToNull_WhenNotSupplied()
    {
        // Null default is load-bearing: PlatformInfoFactory.Create treats null as
        // "use the in-tree DefaultPlatformVersion fallback", so existing CLI callers
        // that never pass the flag continue to produce the same csproj they always did.
        var opts = new CliOptions();
        var root = opts.CreateRootCommand();
        var parsed = root.Parse(Array.Empty<string>());
        var value = parsed.GetValueForOption(opts.PlatformVersion);
        Assert.Null(value);
    }

    [Fact]
    public void PlatformVersionOption_IsRegisteredOnRootCommand()
    {
        // System.CommandLine ignores GetValueForOption() for an Option that wasn't
        // added to the command's option set. Pin the registration so a future refactor
        // doesn't accidentally drop the flag from CreateRootCommand().
        var opts = new CliOptions();
        var root = opts.CreateRootCommand();
        Assert.Contains(opts.PlatformVersion, root.Options);
    }
}

#endregion

#region --platform-version format validation

/// <summary>
/// The <c>--platform-version</c> override is woven straight into
/// <c>&lt;TargetFramework&gt;net10.0-ios{value}&lt;/TargetFramework&gt;</c> and the
/// buildTransitive/ pack path. The CLI MUST reject anything that isn't a canonical
/// Apple TPV "&lt;major&gt;.&lt;minor&gt;" pair before that value escapes into the
/// emitted csproj — otherwise typos surface as opaque MSBuild/NuGet failures
/// downstream and the user has nothing pointing back at the flag they typed.
/// </summary>
public class PlatformVersionFormatValidationTests
{
    [Theory]
    [InlineData("26.0")]
    [InlineData("26.2")]
    [InlineData("17.0")]
    [InlineData("100.500")]
    [InlineData("0.0")]
    public void Accepts_CanonicalMajorMinor(string value)
    {
        Assert.True(BindingsGeneratorCommand.IsValidPlatformVersion(value));
    }

    [Theory]
    [InlineData("26")]              // missing minor
    [InlineData("26.")]             // dangling dot
    [InlineData(".2")]              // dangling dot
    [InlineData("26.2.0")]          // patch component (.NET TFMs are major.minor only)
    [InlineData("26.two")]          // non-numeric minor
    [InlineData("twenty.six")]      // non-numeric major
    [InlineData("26.2-preview")]    // pre-release tail
    [InlineData("26.2 ")]           // trailing whitespace
    [InlineData(" 26.2")]           // leading whitespace
    [InlineData("v26.2")]           // version prefix
    [InlineData("")]                // empty
    [InlineData(null)]              // null
    public void Rejects_NonCanonicalShapes(string? value)
    {
        Assert.False(BindingsGeneratorCommand.IsValidPlatformVersion(value));
    }
}

#endregion

#region Publishable-path enforcement of --platform-version

/// <summary>
/// The publishable path of the generator (where <c>--swift-runtime-version</c> is set
/// to anything other than the dev sentinel) MUST require an explicit
/// <c>--platform-version</c>. Without that requirement the generator silently falls
/// back to <c>PlatformInfo.DefaultPlatformVersion</c> and writes a nupkg labeled for
/// the wrong SDK cut — a failure mode that only surfaces downstream as a NETSDK1005
/// or TPV mismatch on whoever consumes the package. The gate must mirror the
/// emitter's <c>isPackable</c> rule exactly so the CLI rejects the same runs the
/// emitter would have packed.
/// </summary>
public class RequiresExplicitPlatformVersionTests
{
    [Theory]
    [InlineData("0.8.0")]
    [InlineData("0.10.0")]
    [InlineData("1.2.3")]
    [InlineData("0.8.0-preview.1")]
    public void RealRuntimeVersion_RequiresExplicitPlatformVersion(string runtimeVersion)
    {
        Assert.True(BindingsGeneratorCommand.RequiresExplicitPlatformVersion(runtimeVersion));
    }

    [Theory]
    [InlineData(null)]   // not supplied: dev sentinel applies in the emitter
    [InlineData("")]     // empty: treat the same as not supplied
    [InlineData("   ")]  // whitespace: same
    [InlineData("0.0.0-dev")] // explicit dev sentinel
    public void DevPath_DoesNotRequireExplicitPlatformVersion(string? runtimeVersion)
    {
        Assert.False(BindingsGeneratorCommand.RequiresExplicitPlatformVersion(runtimeVersion));
    }
}

#endregion

#region DeriveModuleNameFromSwiftInterfacePath

/// <summary>
/// The apple-framework cross-module dep detector derives the current module name
/// from the swiftinterface path's <c>&lt;Module&gt;.swiftmodule</c> parent dir, so
/// the MSBuild SDK doesn't need to thread a separate "what module is this?" flag.
/// The path layout is canonical for Apple SDK frameworks
/// (<c>&lt;Framework&gt;.framework/Modules/&lt;Module&gt;.swiftmodule/&lt;arch&gt;-&lt;platform&gt;.swiftinterface</c>).
/// Any path that doesn't match the layout returns null so the CLI can fail loud.
/// </summary>
public class DeriveModuleNameFromSwiftInterfacePathTests
{
    [Theory]
    [InlineData(
        "/SDK/iPhoneOS26.2.sdk/System/Library/Frameworks/RealityKit.framework/Modules/RealityKit.swiftmodule/arm64e-apple-ios.swiftinterface",
        "RealityKit")]
    [InlineData(
        "/SDK/MacOSX.sdk/System/Library/Frameworks/Foo.framework/Modules/Foo.swiftmodule/x86_64-apple-macos.swiftinterface",
        "Foo")]
    public void RecognizesCanonicalAppleSdkLayout(string path, string expected)
    {
        Assert.Equal(expected, BindingsGeneratorCommand.DeriveModuleNameFromSwiftInterfacePath(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/path/with/no/swiftmodule/parent.swiftinterface")]
    [InlineData(".swiftmodule/arm64.swiftinterface")] // empty module name
    public void RejectsNonconformantPaths(string? path)
    {
        Assert.Null(BindingsGeneratorCommand.DeriveModuleNameFromSwiftInterfacePath(path!));
    }
}

#endregion
