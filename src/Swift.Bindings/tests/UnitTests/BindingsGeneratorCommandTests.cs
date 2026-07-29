// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using BindingsGeneration.ObjC;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
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
/// from EVERY <c>BindingProjectEmitter.Emit</c> call site in <c>BindingsGeneratorCommand.Execute</c>:
/// the xcframework path, the direct system-framework path, and the C# verify-recover verification
/// csproj the in-emission loop emits to compile-check the generated C#. If any call site drops the
/// field, that csproj's <c>SwiftBindings.Apple</c> PackageReference falls back to the hardcoded
/// default in <c>BindingProjectEmitterOptions</c> — consumers silently target the wrong Apple SDK
/// train on the two shipped paths, and the verify-recover compile gate checks the emitted C# against
/// the wrong reference set on the third. Unit tests on <c>BindingProjectEmitter</c> already cover
/// end-to-end csproj content for a non-default version; this source-level guard catches a future
/// regression in the command layer without spinning up the full CLI pipeline (which
/// requires real dylib/ABI-JSON artifacts).
/// </summary>
public class AppleVersionForwardingTests
{
    [Fact]
    public void EveryEmitterCallSite_ForwardsAppleVersion()
    {
        var commandFile = LocateCommandFile();
        var source = File.ReadAllText(commandFile);
        // Count is asserted ==3 (xcframework + direct-framework + C# verify-recover verification csproj).
        // A weaker >=1 check would have let a per-call-site regression slip through.
        var occurrences = System.Text.RegularExpressions.Regex.Matches(
            source, @"AppleSupplementVersion\s*=\s*appleVersion\s*,").Count;
        Assert.Equal(3, occurrences);
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

/// <summary>
/// The Apple-supplement floor is a deliberate NuGet-contract value, not incidental. A
/// generated binding stamps <c>&lt;PackageReference Include="SwiftBindings.Apple" Version="[X,)"/&gt;</c>,
/// and a floor <c>[X,)</c> resolves to the LOWEST applicable PUBLISHED supplement. If X predates
/// the published version that first shipped <c>AnyError(ExistentialContainer1, ownsContainer:)</c>,
/// the resolved package lacks that constructor and consumer compiles fail with CS1739 (the
/// observed SwiftyStoreKit/Siren break) even though the in-tree emitter and supplement agree.
/// These pin the floor, its single-constant flow through the CLI, and the in-tree ctor shape the
/// emitter targets — so the floor can only move by a deliberate edit, never silently drift.
/// </summary>
public class AppleSupplementFloorTests
{
    [Fact]
    public void DefaultAppleSupplementVersion_PinsPublishedFloorWithOwnsContainerCtor()
    {
        // 26.2.4 is the first PUBLISHED SwiftBindings.Apple carrying the ownsContainer ctor the
        // owned-error return paths emit. Changing this is a compatibility decision: raise it when
        // the emitter starts emitting Apple surface a still-newer published supplement introduces;
        // never lower it below the published version that first shipped the emitted surface.
        Assert.Equal("26.2.4", CliOptions.DefaultAppleSupplementVersion);
    }

    [Fact]
    public void AppleVersionOption_DefaultsToSupplementFloor_WhenNotSupplied()
    {
        // The --apple-version default must resolve to the single DefaultAppleSupplementVersion
        // constant, so a binding generated without an explicit --apple-version stamps the pinned
        // floor and can't diverge from it.
        var opts = new CliOptions();
        var root = opts.CreateRootCommand();
        var parsed = root.Parse(Array.Empty<string>());
        Assert.Equal(CliOptions.DefaultAppleSupplementVersion,
            parsed.GetValueForOption(opts.AppleVersion));
    }

    [Fact]
    public void AppleVersionOption_OverrideWins_WhenSupplied()
    {
        var opts = new CliOptions();
        var root = opts.CreateRootCommand();
        var parsed = root.Parse(new[] { "--apple-version", "30.1.2" });
        Assert.Equal("30.1.2", parsed.GetValueForOption(opts.AppleVersion));
    }

    [Fact]
    public void AnyErrorSupplement_DeclaresOwnsContainerCtor_MatchingEmittedCall()
    {
        // Owned-error/Optional<Error> return marshalling emits
        // `new Swift.Foundation.AnyError(container, ownsContainer: true)`. Pin the in-tree
        // supplement's matching two-arg ctor so removing or reshaping it is caught here rather
        // than at a downstream consumer compile — the emitter<->runtime shape half of this bug.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SwiftBindings.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var anyErrorPath = Path.Combine(dir!.FullName, "src", "Swift.Bindings.Apple",
            "Sources", "Foundation", "AnyError.cs");
        Assert.True(File.Exists(anyErrorPath), $"AnyError supplement not found at {anyErrorPath}");
        var src = File.ReadAllText(anyErrorPath);
        Assert.Matches(
            @"public\s+AnyError\s*\(\s*ExistentialContainer1\s+\w+\s*,\s*bool\s+ownsContainer\s*\)",
            src);
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

#region --link-framework / --link-library CLI options

/// <summary>
/// A force-loaded static-archive source can depend on Apple system frameworks/libraries
/// that carry no autolink hints and aren't discoverable from the binary. The
/// <c>--link-framework</c> / <c>--link-library</c> flags let the author declare them so
/// the wrapper link resolves. They must (a) parse repeatably off the CLI, (b) default to
/// null/empty when absent so existing callers are unaffected, and (c) be registered on the
/// root command so System.CommandLine actually binds them.
/// </summary>
public class LinkDependencyCliOptionTests
{
    [Fact]
    public void LinkFramework_Parses_Repeatably()
    {
        var opts = new CliOptions();
        var root = opts.CreateRootCommand();
        var parsed = root.Parse(new[]
        {
            "--link-framework", "CoreVideo",
            "--link-framework", "Metal",
        });
        var value = parsed.GetValueForOption(opts.LinkFramework);
        Assert.NotNull(value);
        Assert.Equal(new[] { "CoreVideo", "Metal" }, value);
    }

    [Fact]
    public void LinkLibrary_Parses_Repeatably()
    {
        var opts = new CliOptions();
        var root = opts.CreateRootCommand();
        var parsed = root.Parse(new[]
        {
            "--link-library", "c++",
            "--link-library", "z",
        });
        var value = parsed.GetValueForOption(opts.LinkLibrary);
        Assert.NotNull(value);
        Assert.Equal(new[] { "c++", "z" }, value);
    }

    [Fact]
    public void LinkFramework_DefaultsToNull_WhenNotSupplied()
    {
        // Null/empty default keeps the wrapper link byte-identical for the overwhelming
        // majority of bindings that declare no extra system dependencies.
        var opts = new CliOptions();
        var root = opts.CreateRootCommand();
        var parsed = root.Parse(Array.Empty<string>());
        var fw = parsed.GetValueForOption(opts.LinkFramework);
        var lib = parsed.GetValueForOption(opts.LinkLibrary);
        Assert.True(fw is null || fw.Length == 0);
        Assert.True(lib is null || lib.Length == 0);
    }

    [Fact]
    public void LinkFramework_And_LinkLibrary_AreRegisteredOnRootCommand()
    {
        var opts = new CliOptions();
        var root = opts.CreateRootCommand();
        Assert.Contains(opts.LinkFramework, root.Options);
        Assert.Contains(opts.LinkLibrary, root.Options);
    }

    // The flags only affect the wrapper link of a force-loaded static-archive source — an
    // --xcframework-mode concept. Supplying them in -a/-d/-t direct mode must fail closed
    // (the CLI descriptions say "Requires --xcframework") rather than silently dropping the
    // author's declared system dependencies. The guard predicate is the unit under test.

    [Fact]
    public void LinkFlags_WithoutXcframework_TripGuard()
    {
        Assert.True(BindingsGeneratorCommand.LinkDependenciesSuppliedWithoutXcframework(
            hasXcframework: false, linkFrameworks: new[] { "Metal" }, linkLibraries: null));
        Assert.True(BindingsGeneratorCommand.LinkDependenciesSuppliedWithoutXcframework(
            hasXcframework: false, linkFrameworks: null, linkLibraries: new[] { "c++" }));
    }

    [Fact]
    public void LinkFlags_WithXcframework_DoNotTripGuard()
    {
        // --xcframework mode is exactly where these flags are consumed (the wrapper link),
        // so the guard must stay silent there.
        Assert.False(BindingsGeneratorCommand.LinkDependenciesSuppliedWithoutXcframework(
            hasXcframework: true, linkFrameworks: new[] { "Metal" }, linkLibraries: new[] { "c++" }));
    }

    [Fact]
    public void LinkFlags_AbsentOrEmpty_DoNotTripGuard()
    {
        // No flags is the common case; an empty array (System.CommandLine can surface one)
        // must not be treated as "supplied", or every direct-mode build would break.
        Assert.False(BindingsGeneratorCommand.LinkDependenciesSuppliedWithoutXcframework(
            hasXcframework: false, linkFrameworks: null, linkLibraries: null));
        Assert.False(BindingsGeneratorCommand.LinkDependenciesSuppliedWithoutXcframework(
            hasXcframework: false, linkFrameworks: Array.Empty<string>(), linkLibraries: Array.Empty<string>()));
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

#region --interface-facts-producer validation + option

/// <summary>
/// Phase B left a single SwiftSyntax interface-facts producer; the legacy <c>regex</c>
/// value was retired. <see cref="BindingsGeneratorCommand.Execute"/> now validates the
/// <c>--interface-facts-producer</c> flag through this predicate up front and exits
/// cleanly (logged error + <c>ExitCode = 1</c>) on an unrecognized value, rather than
/// constructing an aggregator that would throw. Pin the accept/reject set so a future
/// edit can't silently re-admit <c>regex</c> (which no longer maps to any producer) or
/// start accepting arbitrary strings that would then fall through to the host aggregator.
/// </summary>
public class InterfaceFactsProducerValidationTests
{
    [Theory]
    [InlineData("auto")]
    [InlineData("swift-syntax")]
    public void Accepts_TheTwoRetainedProducers(string value)
    {
        Assert.True(BindingsGeneratorCommand.IsValidInterfaceFactsProducer(value));
    }

    [Theory]
    [InlineData("regex")]        // retired in Phase B — must NOT be silently re-admitted
    [InlineData("Regex")]        // case-sensitive: only the exact lowercase tokens map
    [InlineData("Auto")]
    [InlineData("swiftsyntax")]  // missing the hyphen
    [InlineData("syntax")]
    [InlineData("")]
    [InlineData(" auto ")]       // whitespace is not trimmed by the predicate
    [InlineData("unknown")]
    public void Rejects_RetiredOrUnknownProducers(string value)
    {
        Assert.False(BindingsGeneratorCommand.IsValidInterfaceFactsProducer(value));
    }
}

/// <summary>
/// The <c>--interface-facts-producer</c> option must (a) default to <c>auto</c> so callers
/// that never pass it keep the SwiftSyntax host behavior, (b) parse <c>swift-syntax</c> off
/// the CLI, and (c) be registered on the root command so System.CommandLine binds it.
/// </summary>
public class InterfaceFactsProducerCliOptionTests
{
    [Fact]
    public void Option_DefaultsToAuto_WhenNotSupplied()
    {
        var opts = new CliOptions();
        var root = opts.CreateRootCommand();
        var parsed = root.Parse(Array.Empty<string>());
        var value = parsed.GetValueForOption(opts.InterfaceFactsProducer);
        Assert.Equal("auto", value);
    }

    [Fact]
    public void Option_Parses_SwiftSyntax_FromCommandLine()
    {
        var opts = new CliOptions();
        var root = opts.CreateRootCommand();
        var parsed = root.Parse(new[] { "--interface-facts-producer", "swift-syntax" });
        var value = parsed.GetValueForOption(opts.InterfaceFactsProducer);
        Assert.Equal("swift-syntax", value);
    }

    [Fact]
    public void Option_IsRegisteredOnRootCommand()
    {
        var opts = new CliOptions();
        var root = opts.CreateRootCommand();
        Assert.Contains(opts.InterfaceFactsProducer, root.Options);
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

#region --strict-inputs CLI option + fail-closed gate

/// <summary>
/// Finding 50: <c>--strict-inputs</c> escalates a degraded input edge (slice fallback, missing
/// swiftinterface, ABI-JSON fallback, ambiguous TBD, degraded auto-detected dependency) to a fatal
/// generator exit. The flag must (a) parse off the CLI, (b) default to false so existing callers
/// are unaffected, and (c) be registered on the root command so System.CommandLine binds it.
/// </summary>
public class StrictInputsCliOptionTests
{
    [Fact]
    public void StrictInputsOption_Parses_FromCommandLine()
    {
        var opts = new CliOptions();
        var root = opts.CreateRootCommand();
        var parsed = root.Parse(new[] { "--strict-inputs" });
        Assert.True(parsed.GetValueForOption(opts.StrictInputs));
    }

    [Fact]
    public void StrictInputsOption_DefaultsToFalse_WhenNotSupplied()
    {
        // False default is load-bearing: a normal (non-strict) generation must keep degrading
        // gracefully — the report still records the degradation, but the run exits 0.
        var opts = new CliOptions();
        var root = opts.CreateRootCommand();
        var parsed = root.Parse(Array.Empty<string>());
        Assert.False(parsed.GetValueForOption(opts.StrictInputs));
    }

    [Fact]
    public void StrictInputsOption_IsRegisteredOnRootCommand()
    {
        var opts = new CliOptions();
        var root = opts.CreateRootCommand();
        Assert.Contains(opts.StrictInputs, root.Options);
    }
}

/// <summary>
/// Finding 50: the fail-closed decision is its own predicate so a future refactor of
/// <c>Execute</c> can't silently turn the gate back into warn-and-continue. The gate fires iff
/// BOTH <c>--strict-inputs</c> is set AND at least one input degradation was recorded.
/// </summary>
public class ShouldFailClosedOnDegradedInputsTests
{
    [Fact]
    public void StrictAndDegraded_FailsClosed()
    {
        Assert.True(BindingsGeneratorCommand.ShouldFailClosedOnDegradedInputs(
            strictInputs: true, hasDegradations: true));
    }

    [Theory]
    [InlineData(false, true)]   // not strict: degrade gracefully, exit 0
    [InlineData(true, false)]   // strict but every input resolved cleanly
    [InlineData(false, false)]  // neither
    public void OtherwiseDoesNotFailClosed(bool strict, bool degraded)
    {
        Assert.False(BindingsGeneratorCommand.ShouldFailClosedOnDegradedInputs(strict, degraded));
    }
}

#endregion

#region Mixed-framework ObjC fail-closed decision

/// <summary>
/// <c>ShouldAbortForFailedMixedObjC</c> is the fail-closed gate behind the generator's
/// "refuse to emit a Swift-only binding when a known ObjC surface failed to bind" contract
/// (the round-1 correctness hole). The pipeline only runs when an ObjC surface was detected,
/// so a non-zero exit OR a null module MUST abort the whole generation (propagating a non-zero
/// exit code) rather than degrade to a Swift-only package that silently drops the ObjC types
/// and never reaches the <c>SWIFTBIND039</c> pack-time guard. A null result means the pipeline
/// never ran (not a mixed framework) and must never abort. These tests pin the LIVE oracle used
/// by both the pre-Swift parse gate (<see cref="ObjCParseResult"/>) and the post-Swift
/// FilterAndEmit gate (<see cref="ObjCPipelineResult"/>) so a future refactor of <c>Execute</c>
/// can't quietly reinstate warn-and-continue or re-diverge the two call sites.
/// </summary>
public class ShouldAbortForFailedMixedObjCTests
{
    private static ObjCModule EmptyModule() => new() { ModuleName = "M" };

    private static ObjCPipelineResult PipelineResult(int exitCode, ObjCModule? module = null) =>
        new(exitCode, module, exitCode == 0 ? null : "synthetic failure");

    private static ObjCParseResult ParseResult(int exitCode, ObjCModule? module = null) =>
        new(
            exitCode,
            module,
            exitCode == 0 ? null : "synthetic failure",
            ResolvedNamespace: "M",
            PlatformInfo: PlatformInfoFactory.Create(ApplePlatform.iOS),
            Diagnostics: new ObjCBindingDiagnostics());

    [Fact]
    public void NullPipelineResult_DoesNotAbort()
    {
        // Pipeline never ran (Swift-only framework) → nothing to fail closed on.
        Assert.False(BindingsGeneratorCommand.ShouldAbortForFailedMixedObjC((ObjCPipelineResult?)null));
    }

    [Fact]
    public void NullParseResult_DoesNotAbort()
    {
        // Pre-Swift parse never ran → nothing to fail closed on.
        Assert.False(BindingsGeneratorCommand.ShouldAbortForFailedMixedObjC((ObjCParseResult?)null));
    }

    [Fact]
    public void ZeroExit_WithModule_DoesNotAbort()
    {
        // Successful bind of a known ObjC surface — generation continues.
        Assert.False(BindingsGeneratorCommand.ShouldAbortForFailedMixedObjC(PipelineResult(0, EmptyModule())));
        Assert.False(BindingsGeneratorCommand.ShouldAbortForFailedMixedObjC(ParseResult(0, EmptyModule())));
    }

    [Fact]
    public void ZeroExit_NullModule_Aborts()
    {
        // Exit 0 with a null module is still a failed ObjC surface (e.g. eligibility filters
        // emptied the module without elevating the exit code). The live pre-Swift gate aborts
        // on this shape; the shared oracle must match.
        Assert.True(BindingsGeneratorCommand.ShouldAbortForFailedMixedObjC(PipelineResult(0, module: null)));
        Assert.True(BindingsGeneratorCommand.ShouldAbortForFailedMixedObjC(ParseResult(0, module: null)));
        Assert.True(BindingsGeneratorCommand.ShouldAbortForFailedMixedObjC(exitCode: 0, module: null));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(255)]
    public void NonZeroExit_Aborts(int exitCode)
    {
        // A detected ObjC surface that failed to bind must abort generation, NOT degrade.
        Assert.True(BindingsGeneratorCommand.ShouldAbortForFailedMixedObjC(PipelineResult(exitCode)));
        Assert.True(BindingsGeneratorCommand.ShouldAbortForFailedMixedObjC(ParseResult(exitCode)));
        Assert.True(BindingsGeneratorCommand.ShouldAbortForFailedMixedObjC(exitCode, module: null));
        // Non-zero exit aborts even when a partial module is attached.
        Assert.True(BindingsGeneratorCommand.ShouldAbortForFailedMixedObjC(PipelineResult(exitCode, EmptyModule())));
        Assert.True(BindingsGeneratorCommand.ShouldAbortForFailedMixedObjC(ParseResult(exitCode, EmptyModule())));
    }
}

/// <summary>
/// <c>IsMixedFramework</c> decides <c>frameworkType</c> ("Mixed" vs "Swift") and whether an
/// <c>objcProjectName</c>/companion-embed machinery is recorded. A framework is Mixed iff the
/// ObjC pipeline succeeded AND produced at least one bindable class, protocol, category, or
/// bridgeable enum after mixed-framework filtering. The deliberate edge: a zero-exit run whose
/// module filtered down to zero bindable types is a plain Swift framework — no managed ObjC surface
/// exists to embed, so emitting a companion (and its SWIFTBIND039 contract) would be spurious.
/// Pinning this keeps the "zero types → Swift-only" outcome a documented, tested decision rather
/// than silent behavior.
///
/// The enum cases are the regression guard for an enum-only companion (a bridged NS_ENUM/NS_OPTIONS
/// with no ObjC class): the ObjCPipeline still emits a companion for it because a synthesized bridge
/// record resolves a Swift member to that companion's [Flags] enum, so this predicate must classify
/// it Mixed too — otherwise the metadata says "Swift", the SDK never builds/references the
/// companion, and the Swift binding's reference to the enum fails CS0234. No class-bearing fixture
/// covers that shape, so it lives here.
/// </summary>
public class IsMixedFrameworkTests
{
    private static ObjCModule ModuleWith(
        bool withClass = false, bool withProtocol = false, bool withCategory = false,
        bool withEnum = false, bool enumIsOptions = false)
    {
        var module = new ObjCModule { ModuleName = "M" };
        if (withClass)
            module.Classes.Add(new ObjCClassDecl { Name = "Foo" });
        if (withProtocol)
            module.Protocols.Add(new ObjCProtocolDecl { Name = "Bar" });
        if (withCategory)
            module.Categories.Add(new ObjCCategoryDecl { CategoryName = "Ext", ClassName = "Foo" });
        if (withEnum)
            module.Enums.Add(new ObjCEnumDecl { Name = "Level", IsOptions = enumIsOptions });
        return module;
    }

    [Fact]
    public void NullResult_IsNotMixed()
    {
        Assert.False(BindingsGeneratorCommand.IsMixedFramework(null));
    }

    [Fact]
    public void ZeroExit_EmptyModule_IsNotMixed()
    {
        // The deliberate decision: ObjC surface detected but everything filtered out → Swift-only.
        var result = new ObjCPipelineResult(0, ModuleWith(), null);
        Assert.False(BindingsGeneratorCommand.IsMixedFramework(result));
    }

    [Fact]
    public void ZeroExit_NullModule_IsNotMixed()
    {
        var result = new ObjCPipelineResult(0, null, null);
        Assert.False(BindingsGeneratorCommand.IsMixedFramework(result));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void ZeroExit_WithBindableTypes_IsMixed(bool cls, bool proto, bool cat)
    {
        var result = new ObjCPipelineResult(
            0, ModuleWith(withClass: cls, withProtocol: proto, withCategory: cat), null);
        Assert.True(BindingsGeneratorCommand.IsMixedFramework(result));
    }

    [Theory]
    [InlineData(false)] // bridged NS_ENUM
    [InlineData(true)]  // bridged NS_OPTIONS
    public void ZeroExit_WithBridgedEnumOnly_IsMixed(bool enumIsOptions)
    {
        // An enum-only companion (no ObjC class/protocol/category) is still Mixed: the ObjCPipeline
        // emits a companion for the bridged enum, and a Swift member resolves to that companion's
        // [Flags] enum — so the metadata MUST say "Mixed" or the SDK never builds/references the
        // companion and the Swift binding's enum reference fails CS0234. This is the FBSDKShareKit
        // (FBSDKShareBridgeOptions) shape that no class-bearing fixture exercises.
        var result = new ObjCPipelineResult(
            0, ModuleWith(withEnum: true, enumIsOptions: enumIsOptions), null);
        Assert.True(BindingsGeneratorCommand.IsMixedFramework(result));
    }

    [Fact]
    public void NonZeroExit_WithBindableTypes_IsNotMixed()
    {
        // A failed pipeline is never "Mixed" even if a partial module is attached — the abort
        // gate (ShouldAbortForFailedMixedObjC) fires first, so emission never reaches here.
        var result = new ObjCPipelineResult(1, ModuleWith(withClass: true), "synthetic failure");
        Assert.False(BindingsGeneratorCommand.IsMixedFramework(result));
    }
}

#endregion

#region CanVerifyCSharpInLoop

/// <summary>
/// <c>CanVerifyCSharpInLoop</c> is the gate deciding whether the C# verify-recover leg runs INSIDE
/// the wrapper loop. It is sound only when the emitted C# references no unbuilt ObjC companion
/// assembly — the in-loop verification csproj sets <c>ObjCProjectFileName = null</c>, so a bridged
/// companion type in the C# would fail to resolve and be withdrawn on a false error. The
/// companion-freeness signal available at gate time is the bridged-record set threaded into
/// generation: no ObjC surface at all, OR a "potential mixed" framework whose bridge filtered to
/// zero records (an umbrella header re-exporting only Swift — the CocoaMQTT/Eureka/Hero shape) both
/// emit companion-free C# and ARE verifiable in-loop; ≥1 bridged record keeps the post-loop
/// fail-closed gate. The env preconditions (the mode emits a verifiable binding project, non-SDK,
/// verify not opted out) must all hold. The project-mode term is deliberately NOT "a Swift wrapper
/// loop is running": the Apple system-framework direct path emits and grades a binding csproj without
/// one, and gating on the wrapper delegate is what left it with no recovery net.
/// </summary>
public class CanVerifyCSharpInLoopTests
{
    private static XCFrameworkResolver.ObjCFrameworkResolution ObjCSurface() =>
        new("/tmp/Fixture.xcframework/ios-arm64_x86_64-simulator", true, "Fixture", "Fixture.framework");

    private static TypeRecord BridgeRecord(string name) => new()
    {
        CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Fixture", name),
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"Fixture.{name}"),
        MetadataAccessor = $"$s7Fixture{name.Length}{name}CMa",
        Flags = TypeRecordFlags.None,
        Kind = TypeRecordKind.Class,
    };

    [Fact]
    public void PureSwift_NoObjCSurface_VerifiesInLoop()
    {
        // mixedObjcResolution null: a framework with no modulemap/extra header at all.
        Assert.True(BindingsGeneratorCommand.CanVerifyCSharpInLoop(
            verifiableProjectMode: true, sdkMode: false, noVerifyCSharp: false,
            mixedObjcResolution: null, mixedBridgeRecords: null));
    }

    [Fact]
    public void PotentialMixed_ButZeroBridgedRecords_VerifiesInLoop()
    {
        // The CocoaMQTT/Eureka/Hero shape: DetectMixedFrameworkObjC returns non-null off the
        // umbrella header, but the ObjC bridge filtered to zero records, so the emitted C# has no
        // companion reference — it IS verifiable in-loop.
        Assert.True(BindingsGeneratorCommand.CanVerifyCSharpInLoop(
            verifiableProjectMode: true, sdkMode: false, noVerifyCSharp: false,
            mixedObjcResolution: ObjCSurface(), mixedBridgeRecords: new List<TypeRecord>()));
    }

    [Fact]
    public void GenuinelyMixed_WithBridgedRecords_DeclinesInLoop()
    {
        // ≥1 bridged ObjC record → the emitted C# references a companion assembly built only after
        // GenerateBindings returns → decline the loop and keep the post-loop fail-closed gate.
        Assert.False(BindingsGeneratorCommand.CanVerifyCSharpInLoop(
            verifiableProjectMode: true, sdkMode: false, noVerifyCSharp: false,
            mixedObjcResolution: ObjCSurface(),
            mixedBridgeRecords: new List<TypeRecord> { BridgeRecord("MqttClient") }));
    }

    [Fact]
    public void NoVerifiableProjectMode_DeclinesRegardlessOfObjCSurface()
    {
        // The run reaches no branch that emits a consumer-facing binding csproj (device/all wrapper
        // arch or --skip-wrapper-compilation in xcframework mode; a non-system-framework direct run,
        // which emits C# + Wrapper.swift only). Nothing to build, nothing to verify.
        Assert.False(BindingsGeneratorCommand.CanVerifyCSharpInLoop(
            verifiableProjectMode: false, sdkMode: false, noVerifyCSharp: false,
            mixedObjcResolution: null, mixedBridgeRecords: null));
    }

    [Fact]
    public void DirectSystemFrameworkMode_WithNoWrapperLoop_StillVerifiesInLoop()
    {
        // The Apple system-framework direct path: no in-generation Swift wrapper compile exists for a
        // wrapper loop to run on, but the run DOES emit a binding csproj and grade it with the
        // fail-closed publication gate — so the C# leg is available and the loop runs on that plane
        // alone. Keying this gate on a wrapper delegate is exactly what left the mode netless.
        Assert.True(BindingsGeneratorCommand.CanVerifyCSharpInLoop(
            verifiableProjectMode: true, sdkMode: false, noVerifyCSharp: false,
            mixedObjcResolution: null, mixedBridgeRecords: null));
    }

    [Fact]
    public void SdkMode_DeclinesInLoop()
    {
        // SDK two-pass flow defers wrapper compilation; the loop is a non-SDK-path facility.
        Assert.False(BindingsGeneratorCommand.CanVerifyCSharpInLoop(
            verifiableProjectMode: true, sdkMode: true, noVerifyCSharp: false,
            mixedObjcResolution: null, mixedBridgeRecords: null));
    }

    [Fact]
    public void NoVerifyCSharpOptOut_DeclinesInLoop()
    {
        Assert.False(BindingsGeneratorCommand.CanVerifyCSharpInLoop(
            verifiableProjectMode: true, sdkMode: false, noVerifyCSharp: true,
            mixedObjcResolution: null, mixedBridgeRecords: null));
    }
}

#endregion

#region RecordUnresolvedDependencyDegradations

/// <summary>
/// Finding 50: an auto-detected companion dependency the analyzer cannot resolve to an xcframework
/// shrinks the binding's API surface (its types resolve to <c>AnyType</c> and dependent members are
/// pruned), so each one must be recorded as a <see cref="InputResolutionCategory.Dependency"/>
/// degradation — the recording <c>--strict-inputs</c> escalates to SWIFTBIND027 — not merely logged.
/// The loop runs before the fail-closed gate at the call site, so the degradation is counted.
/// </summary>
public class RecordUnresolvedDependencyDegradationsTests
{
    [Fact]
    public void EachUnresolvedDependency_RecordsADependencyDegradation()
    {
        InputResolutionReport.Reset();
        var unresolved = new List<DetectedDependency>
        {
            new() { FrameworkName = "Alpha", InstallName = "@rpath/Alpha.framework/Alpha", UnresolvedReason = "no-xcframework" },
            new() { FrameworkName = "Beta", InstallName = "@rpath/Beta.framework/Beta", UnresolvedReason = "missing-slice" },
        };

        var count = BindingsGeneratorCommand.RecordUnresolvedDependencyDegradations(unresolved, NullLogger.Instance);

        Assert.Equal(2, count);
        var decisions = InputResolutionReport.Decisions;
        Assert.Equal(2, decisions.Count);
        Assert.All(decisions, d => Assert.Equal(InputResolutionCategory.Dependency, d.Category));
        Assert.All(decisions, d => Assert.Equal(InputResolutionSeverity.Degradation, d.Severity));
        Assert.True(InputResolutionReport.HasDegradations);
        Assert.Contains(decisions, d => d.Detail.Contains("Alpha") && d.Detail.Contains("no-xcframework"));
        Assert.Contains(decisions, d => d.Detail.Contains("Beta") && d.Detail.Contains("missing-slice"));

        InputResolutionReport.Reset();
    }

    [Fact]
    public void NullUnresolvedReason_FallsBackToMissingXcframework()
    {
        InputResolutionReport.Reset();
        var unresolved = new List<DetectedDependency>
        {
            new() { FrameworkName = "Gamma", InstallName = "@rpath/Gamma.framework/Gamma" }, // UnresolvedReason null
        };

        BindingsGeneratorCommand.RecordUnresolvedDependencyDegradations(unresolved, NullLogger.Instance);

        var decision = Assert.Single(InputResolutionReport.Decisions);
        Assert.Contains("missing-xcframework", decision.Detail);

        InputResolutionReport.Reset();
    }

    [Fact]
    public void EmptyList_RecordsNothing()
    {
        InputResolutionReport.Reset();

        var count = BindingsGeneratorCommand.RecordUnresolvedDependencyDegradations(
            new List<DetectedDependency>(), NullLogger.Instance);

        Assert.Equal(0, count);
        Assert.Empty(InputResolutionReport.Decisions);
        Assert.False(InputResolutionReport.HasDegradations);
    }
}

#endregion

#region RecordSystemicDependencyAnalysisFailure

/// <summary>
/// Finding 50 (Codex round 2): a systemic <c>otool -L</c> failure makes
/// <see cref="BinaryDependencyAnalyzer.Analyze"/> return null, hiding every companion dependency and
/// silently shrinking the API. That must be recorded as a Dependency degradation — not treated as a
/// clean "no dependencies" — so <c>--strict-inputs</c> fails closed.
/// </summary>
public class RecordSystemicDependencyAnalysisFailureTests
{
    [Fact]
    public void RecordsExactlyOneDependencyDegradation()
    {
        InputResolutionReport.Reset();

        BindingsGeneratorCommand.RecordSystemicDependencyAnalysisFailure(NullLogger.Instance);

        var decision = Assert.Single(InputResolutionReport.Decisions);
        Assert.Equal(InputResolutionCategory.Dependency, decision.Category);
        Assert.Equal(InputResolutionSeverity.Degradation, decision.Severity);
        Assert.True(InputResolutionReport.HasDegradations);

        InputResolutionReport.Reset();
    }
}

#endregion

#region EmitStrictInputsFailureIfDegraded gate

/// <summary>
/// Finding 50 (Codex round 2): the SWIFTBIND027 fail-closed gate is shared by every generation route
/// (Swift, --objc-forced pure-ObjC, and the Swift-resolution-failed ObjC fallback) so the
/// fail-closed guarantee has no per-path holes. It returns true (abort) only when --strict-inputs is
/// set AND a degradation was recorded.
/// </summary>
public class EmitStrictInputsFailureIfDegradedTests
{
    [Fact]
    public void StrictAndDegraded_ReturnsTrue()
    {
        InputResolutionReport.Reset();
        InputResolutionReport.RecordDegradation(
            InputResolutionCategory.SliceSelection, "device slice absent; fell back to simulator");

        Assert.True(BindingsGeneratorCommand.EmitStrictInputsFailureIfDegraded(strictInputs: true, NullLogger.Instance));

        InputResolutionReport.Reset();
    }

    [Fact]
    public void StrictButClean_ReturnsFalse()
    {
        InputResolutionReport.Reset();
        InputResolutionReport.RecordInfo(InputResolutionCategory.SliceSelection, "selected simulator slice");

        Assert.False(BindingsGeneratorCommand.EmitStrictInputsFailureIfDegraded(strictInputs: true, NullLogger.Instance));

        InputResolutionReport.Reset();
    }

    [Fact]
    public void NotStrictButDegraded_ReturnsFalse()
    {
        InputResolutionReport.Reset();
        InputResolutionReport.RecordDegradation(
            InputResolutionCategory.SliceSelection, "device slice absent; fell back to simulator");

        Assert.False(BindingsGeneratorCommand.EmitStrictInputsFailureIfDegraded(strictInputs: false, NullLogger.Instance));

        InputResolutionReport.Reset();
    }

    [Fact]
    public void StrictAndDegraded_LogsEachDegradationAndPointsAtThoseEntries_NotABogusFile()
    {
        // Finding 50 (Codex round 3): the summary line must not direct users to a file. There is no
        // "input-resolution.json" — the real artifact is binding-artifact-manifest.json (and the
        // pure-ObjC routes abort before any manifest is written). Every degradation is logged as its
        // own SWIFTBIND027 line, so the summary points at those entries — accurate on every path.
        InputResolutionReport.Reset();
        InputResolutionReport.RecordDegradation(
            InputResolutionCategory.SliceSelection, "device slice absent; fell back to simulator");
        var logger = new CapturingLogger();

        Assert.True(BindingsGeneratorCommand.EmitStrictInputsFailureIfDegraded(strictInputs: true, logger));

        var all = string.Join("\n", logger.Messages);
        Assert.Contains("SWIFTBIND027", all);
        Assert.Contains("device slice absent; fell back to simulator", all); // the per-degradation line
        Assert.Contains("entries above", all);                              // summary points at those lines
        Assert.DoesNotContain("input-resolution.json", all);               // never the fabricated filename

        InputResolutionReport.Reset();
    }

    // The strict-inputs abort exits nonzero AFTER a successful generation already cleared any stale
    // binding-failure-report.json, so the composed helper must both log the SWIFTBIND027 lines and
    // write the structured report — otherwise the artifact contract ("report present ⇔ last
    // generation failed") breaks on exactly this path.
    [Fact]
    public void FailStrictInputsWithReport_Degraded_AbortsAndWritesStructuredReport()
    {
        InputResolutionReport.Reset();
        InputResolutionReport.RecordDegradation(
            InputResolutionCategory.SliceSelection, "device slice absent; fell back to simulator");
        var dir = Path.Combine(Path.GetTempPath(), $"strict_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        var aborted = BindingsGeneratorCommand.FailStrictInputsWithReport(
            strictInputs: true, "MyModule",
            new BindingFailureInputPaths(null, null, null, null, "iOS"), dir, NullLogger.Instance);

        Assert.True(aborted);
        var path = Path.Combine(dir, BindingFailureReporting.FileName);
        Assert.True(File.Exists(path));
        var doc = JObject.Parse(File.ReadAllText(path));
        Assert.Equal("MyModule", doc.Value<string>("Module"));
        Assert.Equal("StrictInputsDegraded", doc["Outcome"]!.Value<string>("Kind"));
        Assert.Equal("SWIFTBIND027", doc["Outcome"]!.Value<string>("ReasonCode"));
        // The report's evidence carries the recorded degradations, not just a generic banner.
        Assert.Contains("device slice absent; fell back to simulator",
            ((JArray)doc["Diagnostics"]!)[0].Value<string>("Message"));

        InputResolutionReport.Reset();
    }

    [Fact]
    public void FailStrictInputsWithReport_Clean_DoesNotAbortOrWrite()
    {
        InputResolutionReport.Reset();
        var dir = Path.Combine(Path.GetTempPath(), $"strict_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        var aborted = BindingsGeneratorCommand.FailStrictInputsWithReport(
            strictInputs: true, "MyModule",
            new BindingFailureInputPaths(null, null, null, null), dir, NullLogger.Instance);

        Assert.False(aborted);
        Assert.False(File.Exists(Path.Combine(dir, BindingFailureReporting.FileName)));

        InputResolutionReport.Reset();
    }

    /// <summary>Captures emitted log messages for assertion.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}

/// <summary>
/// A pure-ObjC lane exits with the ObjC pipeline's own exit code; when that is nonzero the run
/// has a resolved module identity, so the structured failure report must be written — Parse stage
/// when the pipeline died before producing a module (clang/AST/umbrella-header), Emit stage when a
/// parsed module failed during filtering/emission.
/// </summary>
public class ReportPureObjCPipelineFailureTests
{
    private static string FreshDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"objcrep_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void SuccessfulPipeline_WritesNoReport()
    {
        var dir = FreshDir();

        BindingsGeneratorCommand.ReportPureObjCPipelineFailure(
            new BindingsGeneration.ObjC.ObjCPipelineResult(0, null, null),
            "FBSDKCoreKit", new BindingFailureInputPaths(null, null, null, null, "iOS"),
            dir, NullLogger.Instance);

        Assert.False(File.Exists(Path.Combine(dir, BindingFailureReporting.FileName)));
    }

    [Fact]
    public void ParseFailure_WritesParseStageReport()
    {
        var dir = FreshDir();

        BindingsGeneratorCommand.ReportPureObjCPipelineFailure(
            new BindingsGeneration.ObjC.ObjCPipelineResult(1, null, "Could not locate umbrella header"),
            "FBSDKCoreKit", new BindingFailureInputPaths(null, null, null, null, "iOS"),
            dir, NullLogger.Instance);

        var doc = Newtonsoft.Json.Linq.JObject.Parse(
            File.ReadAllText(Path.Combine(dir, BindingFailureReporting.FileName)));
        Assert.Equal("FBSDKCoreKit", doc.Value<string>("Module"));
        Assert.Equal(
            nameof(BindingFailureOutcomeKind.ObjCPipelineFailure),
            doc["Outcome"]!.Value<string>("Kind"));
        Assert.Equal(nameof(RecoveryStage.Parse), doc["Outcome"]!.Value<string>("Stage"));
        var diagnostics = (Newtonsoft.Json.Linq.JArray)doc["Diagnostics"]!;
        Assert.Contains("umbrella header", diagnostics[0].Value<string>("Message"));
    }

    [Fact]
    public void EmitFailure_WithParsedModule_WritesEmitStageReport()
    {
        var dir = FreshDir();
        var module = new BindingsGeneration.ObjC.ObjCModule { ModuleName = "FBSDKCoreKit" };

        BindingsGeneratorCommand.ReportPureObjCPipelineFailure(
            new BindingsGeneration.ObjC.ObjCPipelineResult(1, module, "api definition emission failed"),
            "FBSDKCoreKit", new BindingFailureInputPaths(null, null, null, null, "iOS"),
            dir, NullLogger.Instance);

        var doc = Newtonsoft.Json.Linq.JObject.Parse(
            File.ReadAllText(Path.Combine(dir, BindingFailureReporting.FileName)));
        Assert.Equal(nameof(RecoveryStage.Emit), doc["Outcome"]!.Value<string>("Stage"));
    }
}

#endregion

#region ObjC dependency -F search-path threading (A2)

/// <summary>
/// A cross-framework <c>#import</c> in an ObjC umbrella header (e.g. FBSDKLoginKit importing
/// FBSDKCoreKit) only resolves during the clang AST dump if each <c>--framework-dependency</c>'s
/// resolved slice directory is threaded into the dump's <c>-F</c> search path. These tests pin the
/// two source helpers (<c>SelectObjCDependencySearchPaths</c> for the rich resolved-dependency set
/// used on the mixed path, <c>ResolveObjCDependencySliceDirs</c> for the raw paths used on the
/// pure-ObjC paths) and the merge that feeds <c>ObjCPipeline.Run</c>.
/// </summary>
public class ObjCDependencySearchPathTests
{
    [Fact]
    public void SelectObjCDependencySearchPaths_Simulator_PicksSimulatorSlice()
    {
        var deps = new List<FrameworkDependencyInfo>
        {
            new() { XCFrameworkPath = "/d/A.xcframework", ModuleName = "A",
                    SimulatorFrameworkSearchPath = "/d/A.xcframework/ios-sim",
                    DeviceFrameworkSearchPath = "/d/A.xcframework/ios-dev" },
        };

        var paths = BindingsGeneratorCommand.SelectObjCDependencySearchPaths(
            deps, XCFrameworkPlatformTarget.Simulator);

        Assert.Equal(new[] { "/d/A.xcframework/ios-sim" }, paths);
    }

    [Fact]
    public void SelectObjCDependencySearchPaths_Device_PicksDeviceSlice()
    {
        var deps = new List<FrameworkDependencyInfo>
        {
            new() { XCFrameworkPath = "/d/A.xcframework", ModuleName = "A",
                    SimulatorFrameworkSearchPath = "/d/A.xcframework/ios-sim",
                    DeviceFrameworkSearchPath = "/d/A.xcframework/ios-dev" },
        };

        var paths = BindingsGeneratorCommand.SelectObjCDependencySearchPaths(
            deps, XCFrameworkPlatformTarget.Device);

        Assert.Equal(new[] { "/d/A.xcframework/ios-dev" }, paths);
    }

    [Fact]
    public void SelectObjCDependencySearchPaths_OnlyOppositeSliceResolved_FallsBack()
    {
        // A device-only dependency still contributes its (device) slice when generating for the
        // simulator — better an imperfect -F than a guaranteed "file not found".
        var deps = new List<FrameworkDependencyInfo>
        {
            new() { XCFrameworkPath = "/d/A.xcframework", ModuleName = "A",
                    SimulatorFrameworkSearchPath = null,
                    DeviceFrameworkSearchPath = "/d/A.xcframework/ios-dev" },
        };

        var paths = BindingsGeneratorCommand.SelectObjCDependencySearchPaths(
            deps, XCFrameworkPlatformTarget.Simulator);

        Assert.Equal(new[] { "/d/A.xcframework/ios-dev" }, paths);
    }

    [Fact]
    public void SelectObjCDependencySearchPaths_Null_ReturnsEmpty()
    {
        Assert.Empty(BindingsGeneratorCommand.SelectObjCDependencySearchPaths(
            null, XCFrameworkPlatformTarget.Simulator));
    }

    [Fact]
    public void ResolveObjCDependencySliceDirs_Null_ReturnsEmpty()
    {
        Assert.Empty(BindingsGeneratorCommand.ResolveObjCDependencySliceDirs(
            null, XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, null));
    }

    [Fact]
    public void ResolveObjCDependencySliceDirs_UnparseablePath_SkippedNotThrown()
    {
        // TryResolveSliceSearchPath returns null for a non-xcframework path; the helper must skip
        // it (best-effort), not throw — mirrors the sibling resolver.
        var paths = BindingsGeneratorCommand.ResolveObjCDependencySliceDirs(
            new[] { "/does/not/exist.xcframework" },
            XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, null);

        Assert.Empty(paths);
    }

    [Fact]
    public void MergeObjCFrameworkSearchPaths_DependenciesFirst_ThenSiblings()
    {
        // Explicit/resolved dependencies must lead: clang searches -F left-to-right and takes the
        // first match, so a deliberately-declared --framework-dependency outranks an incidental
        // co-located sibling that happens to export the same module name.
        var siblings = new[] { "/p/Sib.xcframework/ios-sim" };
        var deps = new[] { "/p/Dep.xcframework/ios-sim" };

        var merged = BindingsGeneratorCommand.MergeObjCFrameworkSearchPaths(siblings, deps);

        Assert.Equal(2, merged.Count);
        Assert.Equal(Path.GetFullPath("/p/Dep.xcframework/ios-sim"), merged[0]);
        Assert.Equal(Path.GetFullPath("/p/Sib.xcframework/ios-sim"), merged[1]);
    }

    [Fact]
    public void MergeObjCFrameworkSearchPaths_DeDuplicatesCoLocatedDependency()
    {
        // A --framework-dependency that is also a co-located sibling must appear once, at its
        // first (dependency) position.
        var shared = "/p/Dep.xcframework/ios-sim";

        var merged = BindingsGeneratorCommand.MergeObjCFrameworkSearchPaths(
            new[] { shared }, new[] { shared });

        Assert.Single(merged);
        Assert.Equal(Path.GetFullPath(shared), merged[0]);
    }

    [Fact]
    public void MergeObjCFrameworkSearchPaths_SkipsEmptyEntries()
    {
        var merged = BindingsGeneratorCommand.MergeObjCFrameworkSearchPaths(
            Array.Empty<string>(), new[] { "" });

        Assert.Empty(merged);
    }
}

#endregion

#region ClassifyStrippedSymbols reconciler-retirement gate

/// <summary>
/// The stripped-symbol reconciler's retirement seam. On the verify-recover loop path the loop has
/// already settled the on-disk C# against a clean wrapper compile, so
/// <see cref="StrippedSymbolCSharpReconciler"/> is never invoked — any post-loop stripped symbol
/// there is a soundness surprise that must fail closed rather than be clawed back. Off the loop path
/// (SDK two-pass, <c>--compile-wrapper-only</c>) the reconciler still owns the claw-back. These pin
/// that policy on the pure classifier so "reconciler not invoked on the loop path" is a test failure
/// from here on, without standing up a whole generation run.
/// </summary>
public class ClassifyStrippedSymbolsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NoStrippedSymbols_IsAlwaysNone_RegardlessOfPath(int count)
    {
        Assert.Equal(
            StrippedSymbolDisposition.None,
            BindingsGeneratorCommand.ClassifyStrippedSymbols(onVerifyRecoverLoopPath: false, count));
        Assert.Equal(
            StrippedSymbolDisposition.None,
            BindingsGeneratorCommand.ClassifyStrippedSymbols(onVerifyRecoverLoopPath: true, count));
    }

    [Fact]
    public void OffLoopPath_WithStrippedSymbols_Reconciles()
    {
        // The legacy legs (SDK two-pass, --compile-wrapper-only) keep the reconciler until session 06:
        // a stripped symbol there is clawed back through StrippedSymbolCSharpReconciler as before.
        Assert.Equal(
            StrippedSymbolDisposition.Reconcile,
            BindingsGeneratorCommand.ClassifyStrippedSymbols(onVerifyRecoverLoopPath: false, strippedSymbolCount: 3));
    }

    [Fact]
    public void OnLoopPath_WithStrippedSymbols_FailsClosed_NeverReconciles()
    {
        // The load-bearing retirement pin: on the loop path a stripped symbol must NOT reach the
        // reconciler. It is a soundness surprise (the loop settled a clean simulator wrapper that the
        // post-loop recompile then contradicted) that fails the module closed.
        var disposition =
            BindingsGeneratorCommand.ClassifyStrippedSymbols(onVerifyRecoverLoopPath: true, strippedSymbolCount: 1);

        Assert.Equal(StrippedSymbolDisposition.FailClosedOnLoopPath, disposition);
        Assert.NotEqual(StrippedSymbolDisposition.Reconcile, disposition);
    }
}

#endregion
