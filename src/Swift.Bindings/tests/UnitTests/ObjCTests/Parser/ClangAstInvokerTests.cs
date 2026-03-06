// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.ObjC;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests.ObjCTests;

public class ClangAstInvokerTests
{
    private static readonly Microsoft.Extensions.Logging.ILogger Logger = NullLogger.Instance;

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
        Assert.Contains("Failed to locate iOS SDK", ex.Message);
    }

    [Fact]
    public void InvokeClangAstDump_ClangFails_Throws()
    {
        var runner = new MockCommandRunner();
        runner.SetResponse("--show-sdk-path", 0, "/Applications/Xcode.app/SDKs/iPhoneSimulator.sdk");
        runner.SetResponse("-ast-dump=json", 1, "", "fatal error: module not found");

        var invoker = new ClangAstInvoker(runner, Logger);
        var ex = Assert.Throws<InvalidOperationException>(
            () => invoker.InvokeClangAstDump("/tmp/test.h", "/tmp/frameworks", isSimulator: true));
        Assert.Contains("Clang AST dump failed", ex.Message);
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
}
