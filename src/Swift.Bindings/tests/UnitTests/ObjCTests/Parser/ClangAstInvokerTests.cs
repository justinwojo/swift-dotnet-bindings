// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.ObjC;
using Xunit;
using static BindingsGeneration.Tests.ObjCTests.ObjCTestHelpers;

namespace BindingsGeneration.Tests.ObjCTests;

public class ClangAstInvokerTests
{

    [Fact]
    public void InvokeClangAstDump_Success_ReturnsJson()
    {
        var runner = new MockCommandRunner();
        runner.SetResponse("--show-sdk-path", 0, "/Applications/Xcode.app/Contents/Developer/Platforms/iPhoneSimulator.platform/Developer/SDKs/iPhoneSimulator.sdk");
        runner.SetResponse("-ast-dump=json", 0, "{\"kind\":\"TranslationUnitDecl\",\"inner\":[]}");

        var invoker = new ClangAstInvoker(runner, Logger);
        var result = invoker.InvokeClangAstDump("/tmp/test.h", "/tmp/frameworks", isSimulator: true);

        Assert.Contains("TranslationUnitDecl", result);
        Assert.True(runner.Invocations.Count >= 2);
    }

    [Fact]
    public void InvokeClangAstDump_SdkLookupFails_Throws()
    {
        var runner = new MockCommandRunner();
        runner.SetResponse("--show-sdk-path", 1, "", "xcrun error");

        var invoker = new ClangAstInvoker(runner, Logger);
        var ex = Assert.Throws<InvalidOperationException>(
            () => invoker.InvokeClangAstDump("/tmp/test.h", "/tmp/frameworks", isSimulator: true));
        Assert.Contains("Failed to locate SDK", ex.Message);
    }

    [Fact]
    public void InvokeClangAstDump_ClangFails_Throws()
    {
        var runner = new MockCommandRunner();
        runner.SetResponse("--show-sdk-path", 0, "/Applications/Xcode.app/SDKs/iPhoneSimulator.sdk");
        runner.SetResponse("-ast-dump=json", 1, "", "fatal error: module not found");

        var invoker = new ClangAstInvoker(runner, Logger);
        // The invoker raises the specific ClangAstDumpException carrying the raw exit code and
        // stderr, so the pipeline can classify the cause (missing header/module/platform) into a
        // SWIFTBIND109 diagnostic rather than surfacing one opaque InvalidOperationException line.
        var ex = Assert.Throws<ClangAstDumpException>(
            () => invoker.InvokeClangAstDump("/tmp/test.h", "/tmp/frameworks", isSimulator: true));
        Assert.Contains("Clang AST dump failed", ex.Message);
        Assert.Equal(1, ex.ExitCode);
        Assert.Contains("module not found", ex.Stderr);
    }

    [Fact]
    public void InvokeClangAstDump_SimulatorVsDevice_UsesCorrectSdkName()
    {
        var runner = new MockCommandRunner();
        runner.SetResponse("--show-sdk-path", 0, "/path/to/sdk");
        runner.SetResponse("-ast-dump=json", 0, "{\"kind\":\"TranslationUnitDecl\",\"inner\":[]}");

        var invoker = new ClangAstInvoker(runner, Logger);

        invoker.InvokeClangAstDump("/tmp/test.h", "/tmp/fw", isSimulator: true);
        Assert.Contains(runner.Invocations, i => i.Arguments.Contains("iphonesimulator"));

        runner.Invocations.Clear();
        invoker.InvokeClangAstDump("/tmp/test.h", "/tmp/fw", isSimulator: false);
        Assert.Contains(runner.Invocations, i => i.Arguments.Contains("iphoneos"));
    }

    [Fact]
    public void InvokeClangAstDump_AddsPlatformDeveloperFrameworks_WhenPresent()
    {
        // XCTest and friends live under <platform>/Developer/Library/Frameworks, not in the SDK.
        // When that directory exists, the clang invocation must include it as a -F search path so
        // a header doing `@import XCTest;` (e.g. Quick) resolves.
        var tempDir = Path.Combine(Path.GetTempPath(), $"objc_platform_{Guid.NewGuid():N}");
        try
        {
            var platformPath = Path.Combine(tempDir, "iPhoneSimulator.platform");
            var devFrameworks = Path.Combine(platformPath, "Developer", "Library", "Frameworks");
            Directory.CreateDirectory(devFrameworks);

            var runner = new MockCommandRunner();
            runner.SetResponse("--show-sdk-path", 0, "/path/to/iPhoneSimulator.sdk");
            runner.SetResponse("--show-sdk-platform-path", 0, platformPath);
            runner.SetResponse("-ast-dump=json", 0, "{\"kind\":\"TranslationUnitDecl\",\"inner\":[]}");

            var invoker = new ClangAstInvoker(runner, Logger);
            invoker.InvokeClangAstDump("/tmp/test.h", "/tmp/frameworks", isSimulator: true);

            var clangInvocation = Assert.Single(runner.Invocations, i => i.Arguments.Contains("-ast-dump=json"));
            Assert.Contains($"-F \"{devFrameworks}\"", clangInvocation.Arguments);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void InvokeClangAstDump_PassesObjcArc()
    {
        // The AST dump must compile under ARC (-fobjc-arc): an umbrella header that does a
        // cross-framework `#import` of an ARC framework otherwise mismatches the ownership model
        // and clang refuses, so the whole ObjC surface fails to bind.
        var runner = new MockCommandRunner();
        runner.SetResponse("--show-sdk-path", 0, "/path/to/sdk");
        runner.SetResponse("-ast-dump=json", 0, "{\"kind\":\"TranslationUnitDecl\",\"inner\":[]}");

        var invoker = new ClangAstInvoker(runner, Logger);
        invoker.InvokeClangAstDump("/tmp/test.h", "/tmp/fw", isSimulator: true);

        var clangInvocation = Assert.Single(runner.Invocations, i => i.Arguments.Contains("-ast-dump=json"));
        Assert.Contains("-fobjc-arc", clangInvocation.Arguments);
    }

    [Fact]
    public void InvokeClangAstDump_AdditionalFrameworkSearchPaths_AddedAsDashF()
    {
        // Dependency xcframework slice dirs (e.g. FBSDKCoreKit for FBSDKLoginKit) must reach the
        // clang -F search path so the umbrella's `#import <Dep/Dep.h>` resolves.
        var runner = new MockCommandRunner();
        runner.SetResponse("--show-sdk-path", 0, "/path/to/sdk");
        runner.SetResponse("-ast-dump=json", 0, "{\"kind\":\"TranslationUnitDecl\",\"inner\":[]}");

        var depA = "/deps/FBSDKCoreKit.xcframework/ios-arm64_x86_64-simulator";
        var depB = "/deps/GoogleUtilities.xcframework/ios-arm64_x86_64-simulator";

        var invoker = new ClangAstInvoker(runner, Logger);
        invoker.InvokeClangAstDump(
            "/tmp/test.h", "/tmp/fw", isSimulator: true,
            additionalFrameworkSearchPaths: new[] { depA, depB });

        var clangInvocation = Assert.Single(runner.Invocations, i => i.Arguments.Contains("-ast-dump=json"));
        Assert.Contains($"-F \"{depA}\"", clangInvocation.Arguments);
        Assert.Contains($"-F \"{depB}\"", clangInvocation.Arguments);
    }

    [Fact]
    public void InvokeClangAstDump_OmitsPlatformFrameworks_WhenLookupEmpty()
    {
        // Best-effort: if the platform-path lookup yields nothing, the generation must still
        // proceed (libraries that don't import test frameworks are unaffected) — just without
        // the extra -F. The MockCommandRunner returns ("",0) for the unset platform-path key.
        var runner = new MockCommandRunner();
        runner.SetResponse("--show-sdk-path", 0, "/path/to/iPhoneSimulator.sdk");
        runner.SetResponse("-ast-dump=json", 0, "{\"kind\":\"TranslationUnitDecl\",\"inner\":[]}");

        var invoker = new ClangAstInvoker(runner, Logger);
        invoker.InvokeClangAstDump("/tmp/test.h", "/tmp/frameworks", isSimulator: true);

        var clangInvocation = Assert.Single(runner.Invocations, i => i.Arguments.Contains("-ast-dump=json"));
        Assert.DoesNotContain("Developer/Library/Frameworks", clangInvocation.Arguments);
    }

    [Fact]
    public void FindUmbrellaHeader_ConventionHeader_ReturnsPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"objc_test_{Guid.NewGuid():N}");
        try
        {
            var fwPath = Path.Combine(tempDir, "TestLib.framework");
            var headersDir = Path.Combine(fwPath, "Headers");
            Directory.CreateDirectory(headersDir);
            Directory.CreateDirectory(Path.Combine(fwPath, "Modules"));

            File.WriteAllText(Path.Combine(headersDir, "TestLib.h"), "#import <Foundation/Foundation.h>");

            var runner = new MockCommandRunner();
            var invoker = new ClangAstInvoker(runner, Logger);
            var result = invoker.FindUmbrellaHeader(fwPath, "TestLib");

            Assert.NotNull(result);
            Assert.EndsWith("TestLib.h", result!.HeaderPath);
            Assert.Null(result.ModulemapPath);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void FindUmbrellaHeader_ModulemapUmbrellaDirective_ReturnsPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"objc_test_{Guid.NewGuid():N}");
        try
        {
            var fwPath = Path.Combine(tempDir, "Foo.framework");
            var headersDir = Path.Combine(fwPath, "Headers");
            var modulesDir = Path.Combine(fwPath, "Modules");
            Directory.CreateDirectory(headersDir);
            Directory.CreateDirectory(modulesDir);

            // Convention name doesn't match
            File.WriteAllText(Path.Combine(headersDir, "FooPublic.h"), "#import <Foundation/Foundation.h>");
            File.WriteAllText(Path.Combine(modulesDir, "module.modulemap"),
                "framework module Foo {\n  umbrella header \"FooPublic.h\"\n}\n");

            var runner = new MockCommandRunner();
            var invoker = new ClangAstInvoker(runner, Logger);
            var result = invoker.FindUmbrellaHeader(fwPath, "Foo");

            Assert.NotNull(result);
            Assert.EndsWith("FooPublic.h", result!.HeaderPath);
            Assert.Null(result.ModulemapPath);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void FindUmbrellaHeader_NoHeadersNoModulemap_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"objc_test_{Guid.NewGuid():N}");
        try
        {
            var fwPath = Path.Combine(tempDir, "Empty.framework");
            Directory.CreateDirectory(fwPath);

            var runner = new MockCommandRunner();
            var invoker = new ClangAstInvoker(runner, Logger);
            var result = invoker.FindUmbrellaHeader(fwPath, "Empty");

            Assert.Null(result);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    // ──────────────────────────────────────────────
    // -fmodules retry: flag construction
    //
    // The end-to-end pipeline fixtures for this path only run where Xcode is installed and only
    // assert the happy outcome, so they cannot catch the flag itself being dropped or misspelled —
    // and both failure modes are silent (clang exits 0 and emits a near-empty AST). These mock the
    // command runner so the constructed argument string is asserted directly, on any host.
    // ──────────────────────────────────────────────

    /// <summary>
    /// Arranges the mock so the first AST dump fails the way a header using <c>@import</c> without
    /// modules does, and the <c>-fmodules</c> retry succeeds. Insertion order matters: the runner
    /// returns the first response whose key is a substring of the invocation, so the retry-only
    /// <c>-fmodules</c> key must be registered before the general one.
    /// </summary>
    private static MockCommandRunner ImportRetryRunner()
    {
        var runner = new MockCommandRunner();
        runner.SetResponse("-fmodules", 0, "{\"kind\":\"TranslationUnitDecl\",\"inner\":[]}");
        runner.SetResponse("--show-sdk-path", 0, "/path/to/sdk");
        runner.SetResponse("-ast-dump=json", 1, "", "error: use of '@import' when modules are disabled");
        return runner;
    }

    [Fact]
    public void InvokeClangAstDump_ImportRetry_PassesModuleName()
    {
        // Without -fmodule-name the retry builds the framework AS a module: every
        // `#import <Module/Sibling.h>` collapses to an ImportDecl, the sibling declarations never
        // enter the translation unit, and clang still exits 0 — a near-empty binding with no error.
        var runner = ImportRetryRunner();
        var invoker = new ClangAstInvoker(runner, Logger);

        invoker.InvokeClangAstDump("/tmp/test.h", "/tmp/fw", isSimulator: true, moduleName: "Intercom");

        var retry = Assert.Single(runner.Invocations, i => i.Arguments.Contains("-fmodules"));
        Assert.Contains("-fmodule-name=Intercom", retry.Arguments);
    }

    [Fact]
    public void InvokeClangAstDump_ImportRetryWithoutModuleName_OmitsFlagRatherThanEmittingEmpty()
    {
        // No name to pass is a degraded case, not a crash: the flag is omitted (an empty
        // -fmodule-name= would be a clang error) and the invoker warns. Pinned so a future change
        // cannot start emitting a malformed flag.
        var runner = ImportRetryRunner();
        var invoker = new ClangAstInvoker(runner, Logger);

        invoker.InvokeClangAstDump("/tmp/test.h", "/tmp/fw", isSimulator: true);

        var retry = Assert.Single(runner.Invocations, i => i.Arguments.Contains("-fmodules"));
        Assert.DoesNotContain("-fmodule-name", retry.Arguments);
    }

    [Fact]
    public void InvokeClangAstDump_ModulemapPath_AlsoNamesTheModule()
    {
        // -fmodules from any source needs the module name for the same reason the retry does.
        var runner = new MockCommandRunner();
        runner.SetResponse("--show-sdk-path", 0, "/path/to/sdk");
        runner.SetResponse("-ast-dump=json", 0, "{\"kind\":\"TranslationUnitDecl\",\"inner\":[]}");

        var invoker = new ClangAstInvoker(runner, Logger);
        invoker.InvokeClangAstDump(
            "/tmp/test.h", "/tmp/fw", isSimulator: true,
            modulemapPath: "/tmp/fw/Modules/module.modulemap", moduleName: "Widgets");

        var clang = Assert.Single(runner.Invocations, i => i.Arguments.Contains("-ast-dump=json"));
        Assert.Contains("-fmodules", clang.Arguments);
        Assert.Contains("-fmodule-name=Widgets", clang.Arguments);
    }

    // ──────────────────────────────────────────────
    // Declared module-name selection
    // ──────────────────────────────────────────────

    [Fact]
    public void ExtractDeclaredModuleName_FrameworkModule_IsRead()
    {
        Assert.Equal("Intercom", ClangAstInvoker.ExtractDeclaredModuleName(
            "framework module Intercom {\n  umbrella header \"Intercom.h\"\n  export *\n}\n"));
    }

    [Fact]
    public void ExtractDeclaredModuleName_WildcardSubmodule_IsNotAName()
    {
        // `module * { export * }` is the wildcard submodule form. Returning "*" would produce
        // -fmodule-name=* — accepted by clang, matching nothing, silently lossy.
        Assert.Equal("Intercom", ClangAstInvoker.ExtractDeclaredModuleName(
            "framework module Intercom {\n  umbrella \"Headers\"\n  module * { export * }\n}\n"));
    }

    [Fact]
    public void ExtractDeclaredModuleName_LeadingHelperModule_DoesNotWinOverFrameworkModule()
    {
        // A modulemap may legally declare several top-level modules. Taking the first would name
        // the helper, and a wrong -fmodule-name exits 0 with exactly the lossy AST the flag exists
        // to prevent — so the framework module must win regardless of declaration order.
        const string modulemap = """
        module Helper {
          header "Helper.h"
        }

        framework module Real {
          umbrella header "Real.h"
          export *
        }
        """;
        Assert.Equal("Real", ClangAstInvoker.ExtractDeclaredModuleName(modulemap));
    }

    [Fact]
    public void ExtractDeclaredModuleName_ExactMatchOnResolvedName_Wins()
    {
        // When the xcframework layout already resolved a name and the modulemap declares it, that
        // is the module being bound — even if another framework module is declared first.
        const string modulemap = """
        framework module Other {
          umbrella header "Other.h"
        }

        framework module Real {
          umbrella header "Real.h"
        }
        """;
        Assert.Equal("Real", ClangAstInvoker.ExtractDeclaredModuleName(modulemap, "Real"));
    }

    [Fact]
    public void ExtractDeclaredModuleName_NestedSubmodule_IsIgnored()
    {
        // Only brace-depth-0 declarations are module identities; a nested submodule is not one.
        const string modulemap = """
        framework module Outer {
          umbrella "Headers"
          module Inner {
            header "Inner.h"
          }
        }
        """;
        Assert.Equal("Outer", ClangAstInvoker.ExtractDeclaredModuleName(modulemap, "Nonexistent"));
    }

    [Fact]
    public void ExtractDeclaredModuleName_BraceInLineComment_DoesNotHideALaterModule()
    {
        // A brace inside a comment is not structure. Counting it leaves the scan believing it is
        // still inside the first module, so every later top-level declaration is skipped and the
        // framework module is never seen — back to a wrong -fmodule-name and a silently lossy AST.
        const string modulemap = """
        module Helper {
          header "Helper.h"
        }
        // careful with an unmatched { when editing this file
        framework module Real {
          umbrella header "Real.h"
        }
        """;
        Assert.Equal("Real", ClangAstInvoker.ExtractDeclaredModuleName(modulemap));
        Assert.Equal("Real", ClangAstInvoker.ExtractDeclaredModuleName(modulemap, "Real"));
    }

    [Fact]
    public void ExtractDeclaredModuleName_BraceInBlockCommentOrHeaderPath_DoesNotHideALaterModule()
    {
        // Same for block comments and quoted strings: a header path may legally contain a brace,
        // and a commented-out declaration must not be mistaken for a real one.
        const string modulemap = """
        module Helper {
          header "weird{name}.h"
        }
        /* framework module Commented {
             umbrella header "Nope.h"
        */
        framework module Real {
          umbrella header "Real.h"
        }
        """;
        Assert.Equal("Real", ClangAstInvoker.ExtractDeclaredModuleName(modulemap));
    }

    [Fact]
    public void ExtractDeclaredModuleName_DeclarationSplitAcrossLines_IsStillRead()
    {
        // The modulemap grammar is token-based, so a newline between `framework module` and the name
        // is ordinary whitespace and clang accepts it. A line-anchored scan reads no name at all and
        // falls back to the xcframework layout name, which is exactly the mismatch this guards.
        const string modulemap = """
        framework module
            Real
        {
          umbrella header "Real.h"
        }
        """;
        Assert.Equal("Real", ClangAstInvoker.ExtractDeclaredModuleName(modulemap));
    }

    [Fact]
    public void ExtractDeclaredModuleName_ExternModuleReference_IsNotThisMapsIdentity()
    {
        // `extern module M "other.modulemap"` is a legal top-level form, but it only POINTS AT a
        // module defined in another file — it is not what this map declares. Naming it would give
        // -fmodule-name a module this translation unit never builds.
        const string modulemap = """
        extern module NotThis "NotThis.modulemap"

        framework module Real {
          umbrella header "Real.h"
        }
        """;
        Assert.Equal("Real", ClangAstInvoker.ExtractDeclaredModuleName(modulemap));
        Assert.Null(ClangAstInvoker.ExtractDeclaredModuleName("extern module Only \"Only.modulemap\"\n"));
    }

    // ──────────────────────────────────────────────
    // Directory umbrella (strategy 3)
    // ──────────────────────────────────────────────

    [Fact]
    public void FindUmbrellaHeader_DirectoryUmbrella_CombinesHeadersTextually()
    {
        // A directory umbrella must be read TEXTUALLY. The former `@import {Module};` translation
        // unit produced an AST holding nothing but clang's builtin `Protocol` interface — the
        // framework's declarations stayed in the precompiled module and the JSON dumper never
        // re-emitted them, so the framework bound EMPTY with a zero exit code.
        var tempDir = Path.Combine(Path.GetTempPath(), $"objc_test_{Guid.NewGuid():N}");
        string synthesized = null;
        try
        {
            var fwPath = Path.Combine(tempDir, "DirUmb.framework");
            var headersDir = Path.Combine(fwPath, "Headers");
            var modulesDir = Path.Combine(fwPath, "Modules");
            Directory.CreateDirectory(Path.Combine(headersDir, "Sub"));
            Directory.CreateDirectory(modulesDir);
            File.WriteAllText(Path.Combine(headersDir, "Alpha.h"), "// alpha\n");
            File.WriteAllText(Path.Combine(headersDir, "Sub", "Beta.h"), "// beta\n");
            File.WriteAllText(Path.Combine(modulesDir, "module.modulemap"),
                "framework module DeclaredName {\n  umbrella \"Headers\"\n  export *\n  module * { export * }\n}\n");

            var invoker = new ClangAstInvoker(new MockCommandRunner(), Logger);
            // Deliberately a different resolved name than the modulemap declares: the declared name
            // is the only one -fmodule-name accepts.
            var result = invoker.FindUmbrellaHeader(fwPath, "DirUmb");

            Assert.NotNull(result);
            synthesized = result.SynthesizedHeaderDirectory;
            Assert.NotNull(synthesized);
            Assert.Equal("DeclaredName", result.ClangModuleName);
            // No modulemap path: enabling -fmodules is what made this strategy emit nothing.
            Assert.Null(result.ModulemapPath);

            var combined = File.ReadAllText(result.HeaderPath);
            Assert.Contains("Alpha.h", combined);
            Assert.Contains("Beta.h", combined);
        }
        finally
        {
            if (synthesized != null && Directory.Exists(synthesized))
                Directory.Delete(synthesized, true);
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void FindUmbrellaHeader_DirectoryUmbrella_ExcludeIsPathScoped_NotByFileName()
    {
        // `exclude header "Internal/Shared.h"` must drop exactly that file. Matching on the bare
        // file name also drops a public `Shared.h` elsewhere in the tree, silently losing its
        // declarations — the framework binds short with no error.
        var tempDir = Path.Combine(Path.GetTempPath(), $"objc_test_{Guid.NewGuid():N}");
        string synthesized = null;
        try
        {
            var fwPath = Path.Combine(tempDir, "Excl.framework");
            var headersDir = Path.Combine(fwPath, "Headers");
            var modulesDir = Path.Combine(fwPath, "Modules");
            Directory.CreateDirectory(Path.Combine(headersDir, "Public"));
            Directory.CreateDirectory(Path.Combine(headersDir, "Internal"));
            Directory.CreateDirectory(modulesDir);
            File.WriteAllText(Path.Combine(headersDir, "Public", "Shared.h"), "// public shared\n");
            File.WriteAllText(Path.Combine(headersDir, "Internal", "Shared.h"), "// internal shared\n");
            File.WriteAllText(Path.Combine(modulesDir, "module.modulemap"),
                "framework module Excl {\n  umbrella \"Headers\"\n  exclude header \"Internal/Shared.h\"\n  export *\n}\n");

            var invoker = new ClangAstInvoker(new MockCommandRunner(), Logger);
            var result = invoker.FindUmbrellaHeader(fwPath, "Excl");

            Assert.NotNull(result);
            synthesized = result.SynthesizedHeaderDirectory;

            var combined = File.ReadAllText(result.HeaderPath);
            Assert.Contains(Path.Combine("Public", "Shared.h"), combined);
            Assert.DoesNotContain(Path.Combine("Internal", "Shared.h"), combined);
        }
        finally
        {
            if (synthesized != null && Directory.Exists(synthesized))
                Directory.Delete(synthesized, true);
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void FindUmbrellaHeader_ConventionHeader_IsNotReportedAsSynthesized()
    {
        // A real framework header must never be handed to the caller as something to delete.
        var tempDir = Path.Combine(Path.GetTempPath(), $"objc_test_{Guid.NewGuid():N}");
        try
        {
            var fwPath = Path.Combine(tempDir, "Conv.framework");
            var headersDir = Path.Combine(fwPath, "Headers");
            Directory.CreateDirectory(headersDir);
            File.WriteAllText(Path.Combine(headersDir, "Conv.h"), "// umbrella\n");

            var invoker = new ClangAstInvoker(new MockCommandRunner(), Logger);
            var result = invoker.FindUmbrellaHeader(fwPath, "Conv");

            Assert.NotNull(result);
            Assert.Null(result.SynthesizedHeaderDirectory);
            Assert.Equal(Path.Combine(headersDir, "Conv.h"), result.HeaderPath);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
