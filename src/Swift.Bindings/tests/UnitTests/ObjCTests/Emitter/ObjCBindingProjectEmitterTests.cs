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
            // The packageId determines the companion csproj/assembly NAME (it is embedded into
            // the Swift binding's package, not packed under its own id), so it is asserted on the
            // emitted file name rather than a <PackageId> property.
            var path = ObjCBindingProjectEmitter.Emit(CreateOptions(tmpDir), Logger);
            Assert.EndsWith("TestModule.ObjC.iOS.csproj", path);
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

    /// <summary>
    /// The array-overload file the emitter can now produce pins its managed array with <c>fixed</c>,
    /// which is only legal in an unsafe context. The binding-project SDK enables unsafe code too, but
    /// that is a property-evaluation-order dependency on a file we now REQUIRE to compile, so the
    /// project states it outright.
    /// </summary>
    [Fact]
    public void Contains_AllowUnsafeBlocks()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            ObjCBindingProjectEmitter.Emit(CreateOptions(tmpDir), Logger);
            var content = File.ReadAllText(Path.Combine(tmpDir, "TestModule.ObjC.iOS.csproj"));
            Assert.Contains("<AllowUnsafeBlocks>true</AllowUnsafeBlocks>", content);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    /// <summary>
    /// The array-overload file is a plain <c>Compile</c> item guarded by <c>Exists()</c>: it must NOT
    /// be an <c>ObjcBindingCoreSource</c>, which is also fed to bgen's api-definition contract compile
    /// — that compile runs before bgen generates the <c>[Internal]</c> members the overloads forward
    /// to, so the file would fail it with unresolved members.
    /// </summary>
    [Fact]
    public void ArrayOverloadsFile_IsAPlainConditionalCompileItem()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            ObjCBindingProjectEmitter.Emit(CreateOptions(tmpDir), Logger);
            var content = File.ReadAllText(Path.Combine(tmpDir, "TestModule.ObjC.iOS.csproj"));

            var itemLine = content.Split('\n').Single(l => l.Contains($"Include=\"{ObjCArrayOverloadsEmitter.FileName}\""));
            Assert.Contains("<Compile", itemLine);
            Assert.DoesNotContain("ObjcBindingCoreSource", itemLine);
            Assert.Contains($"Exists('{ObjCArrayOverloadsEmitter.FileName}')", content);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    /// <summary>
    /// The category-statics file is a plain <c>Compile</c> item for the same reason: it adds parts to
    /// the static classes bgen generates FROM the ApiDefinition, so it cannot be present during the
    /// contract compile bgen runs over its own inputs.
    /// </summary>
    [Fact]
    public void CategoryStaticsFile_IsAPlainConditionalCompileItem()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            ObjCBindingProjectEmitter.Emit(CreateOptions(tmpDir), Logger);
            var content = File.ReadAllText(Path.Combine(tmpDir, "TestModule.ObjC.iOS.csproj"));

            var itemLine = content.Split('\n').Single(l => l.Contains($"Include=\"{ObjCCategoryStaticsEmitter.FileName}\""));
            Assert.Contains("<Compile", itemLine);
            Assert.DoesNotContain("ObjcBindingCoreSource", itemLine);
            Assert.DoesNotContain("ObjcBindingApiDefinition", itemLine);
            Assert.Contains($"Exists('{ObjCCategoryStaticsEmitter.FileName}')", content);
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
    public void Contains_ExplicitVersionQualifiedTargetFramework()
    {
        // Mixed-framework Swift binding csprojs ProjectReference this ObjC csproj.
        // The Swift side now sources <TargetFramework> from PlatformInfo.PackTfm
        // (version-qualified, e.g. net10.0-ios26.0) so the ObjC side MUST match —
        // otherwise the ProjectReference resolution fails restore with NETSDK1005
        // ("Assets file ... doesn't have a target for 'net10.0-ios'"). Pin the
        // explicit form so a future revert is caught here, not by the validation
        // gate's mixed-ObjC+Swift framework infra failures.
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            ObjCBindingProjectEmitter.Emit(CreateOptions(tmpDir), Logger);
            var content = File.ReadAllText(Path.Combine(tmpDir, "TestModule.ObjC.iOS.csproj"));
            var defaultPi = PlatformInfoFactory.Create(ApplePlatform.iOS);
            Assert.Contains($"<TargetFramework>{defaultPi.PackTfm}</TargetFramework>", content);
            Assert.DoesNotContain("<TargetFramework>net10.0-ios</TargetFramework>", content);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void TargetFramework_HonorsPlatformVersionOverride()
    {
        // The --platform-version CLI override flows into PlatformInfo.PlatformVersion
        // and must reach the ObjC csproj's <TargetFramework> element so the parallel
        // Swift/ObjC ProjectReference pair stays consistent under non-default Apple
        // workload versions (e.g. iOS 26.2 publishing).
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var pi262 = PlatformInfoFactory.Create(ApplePlatform.iOS, "26.2");
            var opts = new ObjCBindingProjectOptions
            {
                OutputDirectory = tmpDir,
                ModuleName = "TestModule",
                SourceXCFrameworkPath = Path.Combine(tmpDir, "..", "TestModule.xcframework"),
                PlatformInfo = pi262,
            };
            ObjCBindingProjectEmitter.Emit(opts, Logger);
            var content = File.ReadAllText(Path.Combine(tmpDir, "TestModule.ObjC.iOS.csproj"));
            Assert.Contains("<TargetFramework>net10.0-ios26.2</TargetFramework>", content);
            Assert.DoesNotContain("net10.0-ios26.0", content);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void Companion_IsNotPackable_EmbeddedNotSeparatePackage()
    {
        // A mixed framework is ONE xcframework → ONE package: the companion's assembly is
        // embedded into the Swift binding's package, never packed as a separate package or
        // promoted to a nuspec <dependency>. So the companion must declare IsPackable=false and
        // carry no separate-package markers (no <PackageVersion>, no <PackageId>).
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            ObjCBindingProjectEmitter.Emit(CreateOptions(tmpDir), Logger);
            var content = File.ReadAllText(Path.Combine(tmpDir, "TestModule.ObjC.iOS.csproj"));
            Assert.Contains("<IsPackable>false</IsPackable>", content);
            Assert.DoesNotContain("<PackageVersion>", content);
            Assert.DoesNotContain("<PackageId>", content);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void NativeReference_IsNotPacked_ManagedOnly()
    {
        // The companion is managed-only: its ObjC class symbols resolve at consume time from
        // the Swift package's native (loaded once). The local-build NativeReference must carry
        // Pack=false so the same Mach-O is never shipped twice (single-registration).
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            ObjCBindingProjectEmitter.Emit(CreateOptions(tmpDir), Logger);
            var content = File.ReadAllText(Path.Combine(tmpDir, "TestModule.ObjC.iOS.csproj"));
            Assert.Contains("<Pack>false</Pack>", content);
            // And no native pack item ships the xcframework from the companion.
            Assert.DoesNotContain("PackagePath", content);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void Companion_IsolatesObjOutput_ViaExplicitSdkImport()
    {
        // Gap 1.5 (obj-stomp): the companion and the Swift binding csproj are co-located in one
        // output directory. With the default obj/ both restore graphs write the same
        // obj/project.assets.json and the companion's (no Runtime ref) can win the race → Swift
        // CS0246. The companion relocates ONLY its own obj/bin via the explicit Sdk-import form
        // (BaseIntermediateOutputPath set before Sdk.props), leaving the Swift binding at the
        // default obj/ so tooling reading obj/project.assets.json at the fixed path is unaffected.
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            ObjCBindingProjectEmitter.Emit(CreateOptions(tmpDir), Logger);
            var content = File.ReadAllText(Path.Combine(tmpDir, "TestModule.ObjC.iOS.csproj"));
            Assert.Contains("<BaseIntermediateOutputPath>obj.objc/</BaseIntermediateOutputPath>", content);
            Assert.Contains("<Import Project=\"Sdk.props\" Sdk=\"Microsoft.NET.Sdk\" />", content);
            Assert.Contains("<Import Project=\"Sdk.targets\" Sdk=\"Microsoft.NET.Sdk\" />", content);
            // The relocation PropertyGroup must precede the Sdk.props import, else it is too late
            // (MSB3539/MSB3540) and the assets still land in the shared obj/.
            var baseIdx = content.IndexOf("<BaseIntermediateOutputPath>", StringComparison.Ordinal);
            var importIdx = content.IndexOf("<Import Project=\"Sdk.props\"", StringComparison.Ordinal);
            Assert.True(baseIdx >= 0 && importIdx > baseIdx,
                "BaseIntermediateOutputPath must be set before the Sdk.props import.");
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

    // Gap 2 (@objc:NSObject reverse-dispatch issue #40 "Class X is implemented in both …"): when the source framework's native is
    // a static archive AND a Swift wrapper carries the binding, the wrapper force-loads that archive
    // and is the sole native carrier. The companion must then DROP its own source NativeReference —
    // re-linking the same Mach-O would duplicate-register every ObjC class. Every other shape keeps
    // the reference (the companion is, or may be, the sole carrier).

    [Fact]
    public void NativeReference_Dropped_ForStaticSourceWithWrapper()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            ObjCBindingProjectEmitter.Emit(
                CreateOptions(tmpDir) with
                {
                    SourceNativeLinkage = NativeLinkage.Static,
                    HasWrapperXCFramework = true,
                },
                Logger);
            var content = File.ReadAllText(Path.Combine(tmpDir, "TestModule.ObjC.iOS.csproj"));
            // No source NativeReference: the wrapper is the sole carrier (single-registration).
            Assert.DoesNotContain("<NativeReference Include=\"", content);
            // The drop is documented in-place so the omission can't read as an accident.
            Assert.Contains("Source NativeReference dropped (Gap 2)", content);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [Theory]
    // Static source but no wrapper: the companion IS the sole carrier — keep it.
    [InlineData(NativeLinkage.Static, false)]
    // Dynamic source (with or without wrapper): the reference is inert/deduped — keep it.
    [InlineData(NativeLinkage.Dynamic, true)]
    [InlineData(NativeLinkage.Dynamic, false)]
    public void NativeReference_Kept_WhenCompanionMayBeCarrier(
        NativeLinkage linkage, bool hasWrapper)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            ObjCBindingProjectEmitter.Emit(
                CreateOptions(tmpDir) with
                {
                    SourceNativeLinkage = linkage,
                    HasWrapperXCFramework = hasWrapper,
                },
                Logger);
            var content = File.ReadAllText(Path.Combine(tmpDir, "TestModule.ObjC.iOS.csproj"));
            Assert.Contains("<NativeReference Include=\"", content);
            Assert.Contains("TestModule.xcframework", content);
            Assert.DoesNotContain("Source NativeReference dropped (Gap 2)", content);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }
}
