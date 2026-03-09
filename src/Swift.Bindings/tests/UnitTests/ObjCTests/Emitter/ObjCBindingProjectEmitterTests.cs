// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using BindingsGeneration.ObjC;
using Xunit;
using static BindingsGeneration.Tests.ObjCTests.ObjCTestHelpers;

namespace BindingsGeneration.Tests.ObjCTests;

public class ObjCBindingProjectEmitterTests
{

    private static ObjCBindingProjectOptions CreateOptions(string outputDir, string? packageId = null) =>
        new()
        {
            OutputDirectory = outputDir,
            ModuleName = "TestModule",
            SourceXCFrameworkPath = Path.Combine(outputDir, "..", "TestModule.xcframework"),
            PackageId = packageId,
        };

    [Fact]
    public void DefaultPackageId_IsModuleObjCiOS()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var path = ObjCBindingProjectEmitter.Emit(CreateOptions(tmpDir), Logger);
            Assert.EndsWith("TestModule.ObjC.iOS.csproj", path);
            var content = File.ReadAllText(path);
            Assert.Contains("<PackageId>TestModule.ObjC.iOS</PackageId>", content);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void CustomPackageId_IsUsed()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var path = ObjCBindingProjectEmitter.Emit(CreateOptions(tmpDir, "MyCustom.Package"), Logger);
            Assert.EndsWith("MyCustom.Package.csproj", path);
            var content = File.ReadAllText(path);
            Assert.Contains("<PackageId>MyCustom.Package</PackageId>", content);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void Contains_IsBindingProjectTrue()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            ObjCBindingProjectEmitter.Emit(CreateOptions(tmpDir), Logger);
            var content = File.ReadAllText(Path.Combine(tmpDir, "TestModule.ObjC.iOS.csproj"));
            Assert.Contains("<IsBindingProject>true</IsBindingProject>", content);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void Contains_ObjcBindingApiDefinition()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            ObjCBindingProjectEmitter.Emit(CreateOptions(tmpDir), Logger);
            var content = File.ReadAllText(Path.Combine(tmpDir, "TestModule.ObjC.iOS.csproj"));
            Assert.Contains("<ObjcBindingApiDefinition Include=\"ApiDefinition.cs\" />", content);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void Contains_ObjcBindingCoreSourceWithCondition()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            ObjCBindingProjectEmitter.Emit(CreateOptions(tmpDir), Logger);
            var content = File.ReadAllText(Path.Combine(tmpDir, "TestModule.ObjC.iOS.csproj"));
            Assert.Contains("<ObjcBindingCoreSource Include=\"StructsAndEnums.cs\"", content);
            Assert.Contains("Condition=\"Exists('StructsAndEnums.cs')\"", content);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void Contains_NativeReferenceWithRelativePath()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            ObjCBindingProjectEmitter.Emit(CreateOptions(tmpDir), Logger);
            var content = File.ReadAllText(Path.Combine(tmpDir, "TestModule.ObjC.iOS.csproj"));
            Assert.Contains("<NativeReference Include=\"", content);
            Assert.Contains("TestModule.xcframework", content);
            Assert.Contains("<Kind>Framework</Kind>", content);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void Contains_EnableDefaultCompileItemsFalse()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            ObjCBindingProjectEmitter.Emit(CreateOptions(tmpDir), Logger);
            var content = File.ReadAllText(Path.Combine(tmpDir, "TestModule.ObjC.iOS.csproj"));
            Assert.Contains("<EnableDefaultCompileItems>false</EnableDefaultCompileItems>", content);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void NativeReference_UsesAbsolutePath()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            ObjCBindingProjectEmitter.Emit(CreateOptions(tmpDir), Logger);
            var content = File.ReadAllText(Path.Combine(tmpDir, "TestModule.ObjC.iOS.csproj"));
            // NativeReference should use absolute path (starts with /)
            var nativeRefLine = content.Split('\n').First(l => l.Contains("<NativeReference"));
            Assert.Contains("Include=\"/", nativeRefLine);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void DoesNotContain_SwiftRuntime()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            ObjCBindingProjectEmitter.Emit(CreateOptions(tmpDir), Logger);
            var content = File.ReadAllText(Path.Combine(tmpDir, "TestModule.ObjC.iOS.csproj"));
            Assert.DoesNotContain("SwiftBindings.Runtime", content);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void DoesNotContain_AllowUnsafeBlocks()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            ObjCBindingProjectEmitter.Emit(CreateOptions(tmpDir), Logger);
            var content = File.ReadAllText(Path.Combine(tmpDir, "TestModule.ObjC.iOS.csproj"));
            Assert.DoesNotContain("AllowUnsafeBlocks", content);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void DoesNotContain_DisableRuntimeMarshalling()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            ObjCBindingProjectEmitter.Emit(CreateOptions(tmpDir), Logger);
            var content = File.ReadAllText(Path.Combine(tmpDir, "TestModule.ObjC.iOS.csproj"));
            Assert.DoesNotContain("DisableRuntimeMarshalling", content);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void FileWrittenToCorrectPath()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var path = ObjCBindingProjectEmitter.Emit(CreateOptions(tmpDir), Logger);
            Assert.True(File.Exists(path));
            Assert.Equal(Path.Combine(Path.GetFullPath(tmpDir), "TestModule.ObjC.iOS.csproj"), path);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }
}
