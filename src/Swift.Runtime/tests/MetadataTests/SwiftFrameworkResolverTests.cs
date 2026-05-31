// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.InteropServices;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

public class SwiftFrameworkResolverTests
{
    // A well-known absolute dyld-shared-cache path that is always loadable on
    // macOS (and the iOS/tvOS simulators) without any xcframework wiring. The
    // file itself is not on disk post-BigSur — dyld resolves it out of the
    // shared cache — but NativeLibrary.TryLoad succeeds uniformly for every
    // process on every supported host, so it's the cleanest positive-path
    // fixture for "the dyld-style branch actually loaded something."
    private const string KnownLoadableAbsolutePath = "/usr/lib/libSystem.B.dylib";

    // A bare name that is ALSO always loadable by dyld's default resolution
    // but that the prefix-based search path in GetSearchPaths() will NEVER
    // find (none of @rpath/libSystem.B.dylib.framework/libSystem.B.dylib,
    // @rpath/liblibSystem.B.dylib.dylib, etc. exist on a test host). Pairing
    // this against the absolute-path positive test lets us observe the
    // branch selection from outside: absolute path → non-zero handle;
    // bare name → IntPtr.Zero. That's the A/B Codex was asking for.
    private const string BareNameDyldCouldLoadButPrefixSearchCannot = "libSystem.B.dylib";

    [Fact]
    public void DiagnoseResolution_ReturnsAllSearchPaths()
    {
        var result = SwiftFrameworkResolver.DiagnoseResolution("TestLibrary");

        Assert.Contains("TestLibrary", result);
        Assert.Contains("@rpath/TestLibrary.framework/TestLibrary", result);
        Assert.Contains("@rpath/libTestLibrary.dylib", result);
        Assert.Contains("@rpath/TestLibrary.dylib", result);
        Assert.Contains("@executable_path/libTestLibrary.dylib", result);
        Assert.Contains("@executable_path/TestLibrary.dylib", result);
        // System-framework fallback: supports bare DllImport names emitted by the
        // Apple supplement (e.g. "CryptoKit") so they resolve to system frameworks
        // without the `.framework/` substring that would force build-time linkage.
        Assert.Contains("/System/Library/Frameworks/TestLibrary.framework/TestLibrary", result);
    }

    [Fact]
    public void DiagnoseResolution_ShowsOkOrFail()
    {
        var result = SwiftFrameworkResolver.DiagnoseResolution("NonExistentLibrary");

        // All paths should fail for a non-existent library
        Assert.Contains("FAIL", result);
        Assert.DoesNotContain("OK", result);
    }

    [Theory]
    [InlineData("@rpath/StoreKit.framework/StoreKit")]
    [InlineData("@executable_path/libFoo.dylib")]
    [InlineData("@loader_path/Bar.dylib")]
    [InlineData("/System/Library/Frameworks/StoreKit.framework/StoreKit")]
    public void DiagnoseResolution_DyldStylePath_TriesVerbatimOnly(string libraryName)
    {
        // Regression guard: Apple-framework target bindings emit DllImport entries
        // whose library name is already a dyld-style path. The resolver tries that string
        // verbatim and, on failure, walks the bare-name search list -- but it MUST NOT
        // prepend "@rpath/{name}.framework/{name}" to the already-dyld-style input (which
        // would produce nonsense like "@rpath/@rpath/StoreKit.framework/...").
        var result = SwiftFrameworkResolver.DiagnoseResolution(libraryName);

        Assert.Contains(libraryName, result);
        Assert.Contains("dyld-style path", result);
        // The prefix-based candidates must not appear for dyld-style inputs.
        Assert.DoesNotContain($"@rpath/{libraryName}.framework/", result);
        Assert.DoesNotContain($"@rpath/lib{libraryName}.dylib", result);
    }

    [Theory]
    [InlineData("@rpath/StoreKit.framework/StoreKit", true)]
    [InlineData("@executable_path/libFoo.dylib", true)]
    [InlineData("@loader_path/Bar.dylib", true)]
    [InlineData("/System/Library/Frameworks/StoreKit.framework/StoreKit", true)]
    [InlineData("/absolute/path/to/libfoo.dylib", true)]
    // The detection is intentionally strict: only the three documented dyld load-command
    // tokens are accepted. Malformed @-prefixed inputs fall through to the normal search.
    [InlineData("@foo/bar", false)]
    [InlineData("@rpathtypo", false)]
    [InlineData("TestLibrary", false)]
    [InlineData("libTestLibrary", false)]
    [InlineData("", false)]
    public void IsDyldStylePath_MatchesOnlyDocumentedTokens(string libraryName, bool expected)
    {
        Assert.Equal(expected, SwiftFrameworkResolver.IsDyldStylePath(libraryName));
    }

    [Theory]
    // SYNTHETIC names that resolve nowhere on a test host. A REAL system framework
    // (e.g. @rpath/StoreKit.framework/StoreKit) must NOT be used here: its verbatim load
    // fails, then the bare-name fallback walks to /System/Library/Frameworks and
    // (correctly) returns a non-zero handle -- that is the on-device fix, not a regression.
    [InlineData("@rpath/NoSuchSwiftBindingsFrameworkXYZ.framework/NoSuchSwiftBindingsFrameworkXYZ")]
    [InlineData("@executable_path/libFoo.dylib")]
    [InlineData("@loader_path/Bar.dylib")]
    [InlineData("/Some/Nonexistent/Absolute/Path/libfoo.dylib")]
    public void ResolveSwiftFramework_DyldStylePath_DoesNotPrefix(string libraryName)
    {
        // Direct regression guard against the double-prefix bug inside the resolver hot path
        // (not the diagnostic helper): feed a nonexistent dyld-style path and assert the
        // method returns IntPtr.Zero. The dyld-style branch tries the path verbatim, then
        // reduces it to its bare framework name and walks the ordered search list --
        // ExtractFrameworkName strips the existing prefix first, so no "@rpath/@rpath/..."
        // candidate is ever produced. A name that resolves nowhere must yield IntPtr.Zero so
        // the .NET default resolver takes over. Paired with the IsDyldStylePath_* test this
        // pins down both the detection predicate and the resolver's consumption of it.
        var result = SwiftFrameworkResolver.ResolveSwiftFramework(
            libraryName, Assembly.GetExecutingAssembly(), searchPath: null);
        Assert.Equal(IntPtr.Zero, result);
    }

    [Fact]
    public void ResolveSwiftFramework_BareName_DoesNotThrowAndFailsClean()
    {
        // Non-dyld-style bare names take the prefix-based search path. None of the
        // candidate paths exist for an unknown library, so the resolver should
        // return IntPtr.Zero (letting the .NET default resolver take over).
        var result = SwiftFrameworkResolver.ResolveSwiftFramework(
            "NonExistentLibrary_8e7a3", Assembly.GetExecutingAssembly(), searchPath: null);
        Assert.Equal(IntPtr.Zero, result);
    }

    [Fact]
    public void ResolveSwiftFramework_AbsoluteDyldPath_ReturnsNonZeroHandle()
    {
        // POSITIVE path test — this is the piece that the previous
        // ResolveSwiftFramework_DyldStylePath_DoesNotPrefix theory was missing:
        // with a nonexistent path, both the correct implementation and a
        // regressed "prefix everything" implementation return IntPtr.Zero, so
        // the theory couldn't distinguish them. Feeding a known-loadable
        // absolute path proves the dyld-style branch runs NativeLibrary.TryLoad
        // against the verbatim input and hands back the dyld handle. If some
        // future regression rewrites the input to
        // @rpath/libSystem.B.dylib.framework/libSystem.B.dylib (the classic
        // "prefix everything" bug), this test fails with IntPtr.Zero.
        var handle = SwiftFrameworkResolver.ResolveSwiftFramework(
            KnownLoadableAbsolutePath, Assembly.GetExecutingAssembly(), searchPath: null);
        try
        {
            Assert.NotEqual(IntPtr.Zero, handle);
        }
        finally
        {
            if (handle != IntPtr.Zero)
                NativeLibrary.Free(handle);
        }
    }

    [Fact]
    public void ResolveSwiftFramework_AbsolutePathVsBareName_BranchesDifferently()
    {
        // A/B test that pins the BRANCH selection, not just the return value.
        // Both inputs describe a library dyld knows how to load, but only the
        // absolute-path form goes through the dyld-style passthrough branch —
        // the bare name "libSystem.B.dylib" takes the prefix-based search
        // path (which rewrites it into @rpath/libSystem.B.dylib.framework/...
        // and friends, none of which exist on the test host). The pair of
        // assertions below proves the two branches produce different results
        // for inputs dyld would otherwise treat identically: absolute path
        // SUCCEEDS (dyld branch), bare name FAILS (prefix branch). If someone
        // ever regresses the resolver into a "prefix the absolute path too"
        // mode, the first assertion would flip to IntPtr.Zero because
        // @rpath//usr/lib/libSystem.B.dylib.framework/... is nonsense and
        // NativeLibrary.TryLoad would refuse it.
        var absoluteHandle = SwiftFrameworkResolver.ResolveSwiftFramework(
            KnownLoadableAbsolutePath, Assembly.GetExecutingAssembly(), searchPath: null);
        try
        {
            Assert.NotEqual(IntPtr.Zero, absoluteHandle);
        }
        finally
        {
            if (absoluteHandle != IntPtr.Zero)
                NativeLibrary.Free(absoluteHandle);
        }

        var bareHandle = SwiftFrameworkResolver.ResolveSwiftFramework(
            BareNameDyldCouldLoadButPrefixSearchCannot, Assembly.GetExecutingAssembly(), searchPath: null);
        try
        {
            // The prefix search would rewrite this into:
            //   @rpath/libSystem.B.dylib.framework/libSystem.B.dylib
            //   @rpath/liblibSystem.B.dylib.dylib
            //   @rpath/libSystem.B.dylib.dylib
            //   @executable_path/liblibSystem.B.dylib.dylib
            //   @executable_path/libSystem.B.dylib.dylib
            // None of those exist on the test host, so the call must return
            // IntPtr.Zero. If it returns non-zero, either (a) the bare-name
            // branch is incorrectly going through the verbatim dyld loader
            // (which would bypass the standard framework search path for all
            // bare names — a regression), or (b) one of the test-host @rpath
            // candidates happened to resolve (vanishingly unlikely for a
            // mangled libSystem-inside-a-framework name; still, log and fail
            // loudly so a future human can untangle it).
            Assert.Equal(IntPtr.Zero, bareHandle);
        }
        finally
        {
            if (bareHandle != IntPtr.Zero)
                NativeLibrary.Free(bareHandle);
        }
    }

    [Theory]
    // Bare module name passes through unchanged.
    [InlineData("CryptoKit", "CryptoKit")]
    // The dyld-style framework path the generator embeds for an Apple-framework-target
    // binding's conformance-descriptor load. Extracting "CryptoKit" lets the system-path
    // fallback in TryLoadWithFrameworkFallback reach
    // /System/Library/Frameworks/CryptoKit.framework/CryptoKit on a physical device (§3).
    [InlineData("@rpath/CryptoKit.framework/CryptoKit", "CryptoKit")]
    [InlineData("/System/Library/Frameworks/CryptoKit.framework/CryptoKit", "CryptoKit")]
    [InlineData("@executable_path/StoreKit.framework/StoreKit", "StoreKit")]
    // Plain dylib paths reduce to the bare name, stripping the lib prefix and .dylib suffix.
    [InlineData("/usr/lib/libSystem.B.dylib", "System.B")]
    [InlineData("@rpath/libFoo.dylib", "Foo")]
    [InlineData("Foo.dylib", "Foo")]
    public void ExtractFrameworkName_ReducesEmbeddedPathToFrameworkName(string libraryName, string expected)
    {
        Assert.Equal(expected, SwiftFrameworkResolver.ExtractFrameworkName(libraryName));
    }

    [Fact]
    public void ExtractFrameworkName_FeedsSystemPathFallback()
    {
        // The whole point of the extraction: the reduced name, run through the resolver's
        // ordered search-path list, must include the /System/Library/Frameworks system path
        // (last, so app-bundled / rpath-resident frameworks still win when present). This
        // is the device fix for CSM conformance-descriptor loads of Apple system frameworks.
        var name = SwiftFrameworkResolver.ExtractFrameworkName(
            "@rpath/CryptoKit.framework/CryptoKit");
        var paths = SwiftFrameworkResolver.GetSearchPaths(name);

        Assert.Equal("CryptoKit", name);
        Assert.Contains("/System/Library/Frameworks/CryptoKit.framework/CryptoKit", paths);
        Assert.Equal("/System/Library/Frameworks/CryptoKit.framework/CryptoKit", paths[^1]);
    }

    [Fact]
    public void TryLoadWithFrameworkFallback_ReachesSystemPathForDyldStyleName()
    {
        // Regression guard for the descriptor-loader asymmetry (Codex r1 High): BOTH
        // ProtocolDescriptor.LoadFromSymbol (class-bound existential metadata registration)
        // and ProtocolConformanceDescriptor.LoadFromSymbol (CSM conformance loads) route
        // through this one helper, so the upgraded /System/Library/Frameworks fallback can
        // never apply to only one of them again. An already-dyld-style name must NOT be
        // double-@rpath-prefixed — the extracted bare name's search list ends at the system
        // path. (We assert the resolution plan, not an actual dlopen, so the test is
        // host-portable: CoreFoundation always resolves but isn't a class-bound producer.)
        var name = SwiftFrameworkResolver.ExtractFrameworkName(
            "@rpath/RealityFoundation.framework/RealityFoundation");
        Assert.Equal("RealityFoundation", name);

        var paths = SwiftFrameworkResolver.GetSearchPaths(name);
        Assert.DoesNotContain(paths, p => p.Contains("@rpath/@rpath", StringComparison.Ordinal));
        Assert.Equal("/System/Library/Frameworks/RealityFoundation.framework/RealityFoundation", paths[^1]);
    }

    [Fact]
    public void ResolveSwiftFramework_DyldStyleSystemFrameworkPath_ResolvesViaFallback()
    {
        // The generator embeds @rpath/X.framework/X as the [LibraryImport] library name for
        // EVERY Apple-system-framework P/Invoke (type-metadata accessors, witness getters, ...).
        // On a physical device @rpath cannot reach a system framework, so the verbatim load
        // fails; the resolver must then reduce to the bare name and walk the search list to
        // /System/Library/Frameworks and resolve it there. Before this fix the dyld-style
        // branch returned IntPtr.Zero immediately, which surfaced as a
        // TypeInitializationException -> DllNotFoundException from the static initializer of a
        // generic Apple type (CryptoKit's HMAC<H> metadata accessor) on device while resolving
        // fine on the simulator. CoreFoundation stands in for the system framework because it
        // is reliably loadable on every macOS / simulator unit-test host.
        var handle = SwiftFrameworkResolver.ResolveSwiftFramework(
            "@rpath/CoreFoundation.framework/CoreFoundation",
            typeof(SwiftFrameworkResolverTests).Assembly,
            searchPath: null);
        try
        {
            Assert.NotEqual(IntPtr.Zero, handle);
        }
        finally
        {
            if (handle != IntPtr.Zero)
                NativeLibrary.Free(handle);
        }
    }

    [Fact]
    public void ResolveSwiftFramework_UnresolvableDyldStylePath_DefersToDefault()
    {
        // When neither the verbatim dyld-style load nor the bare-name fallback resolves, the
        // resolver must return IntPtr.Zero so the .NET default native-probing chain still runs
        // (a regression here would convert "defer to default" into a hard failure). The
        // fallback walk must not throw for a framework that exists nowhere on the host.
        var handle = SwiftFrameworkResolver.ResolveSwiftFramework(
            "@rpath/NoSuchSwiftBindingsFrameworkXYZ.framework/NoSuchSwiftBindingsFrameworkXYZ",
            typeof(SwiftFrameworkResolverTests).Assembly,
            searchPath: null);

        Assert.Equal(IntPtr.Zero, handle);
    }
}
