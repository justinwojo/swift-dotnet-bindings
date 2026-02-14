// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests
{
    public class FrameworkDependencyInfoTests
    {
        [Fact]
        public void EffectivePackageId_WithoutOverride_UsesConvention()
        {
            var dep = new FrameworkDependencyInfo
            {
                XCFrameworkPath = "/path/to/SmartCardIO.xcframework",
                ModuleName = "SmartCardIO"
            };
            Assert.Equal("SmartCardIO.Swift.iOS", dep.EffectivePackageId);
        }

        [Fact]
        public void EffectivePackageId_WithOverride_UsesCustomId()
        {
            var dep = new FrameworkDependencyInfo
            {
                XCFrameworkPath = "/path/to/SmartCardIO.xcframework",
                ModuleName = "SmartCardIO",
                PackageId = "Custom.SmartCardIO"
            };
            Assert.Equal("Custom.SmartCardIO", dep.EffectivePackageId);
        }

        [Fact]
        public void EffectiveVersion_WithExtractedVersion_UsesExtracted()
        {
            var dep = new FrameworkDependencyInfo
            {
                XCFrameworkPath = "/path/to/SmartCardIO.xcframework",
                ModuleName = "SmartCardIO",
                PackageVersion = "2.3.1"
            };
            Assert.Equal("2.3.1", dep.EffectiveVersion);
        }

        [Fact]
        public void EffectiveVersion_WithoutVersion_UsesPlaceholder()
        {
            var dep = new FrameworkDependencyInfo
            {
                XCFrameworkPath = "/path/to/SmartCardIO.xcframework",
                ModuleName = "SmartCardIO"
            };
            Assert.Equal("0.0.0", dep.EffectiveVersion);
        }

        [Fact]
        public void EffectiveVersion_NullVersion_UsesPlaceholder()
        {
            var dep = new FrameworkDependencyInfo
            {
                XCFrameworkPath = "/path/to/SmartCardIO.xcframework",
                ModuleName = "SmartCardIO",
                PackageVersion = null
            };
            Assert.Equal("0.0.0", dep.EffectiveVersion);
        }

        [Fact]
        public void AllProperties_CanBeSet()
        {
            var dep = new FrameworkDependencyInfo
            {
                XCFrameworkPath = "/path/to/Lib.xcframework",
                ModuleName = "Lib",
                PackageVersion = "1.0.0",
                PackageId = "Lib.Custom",
                SimulatorFrameworkSearchPath = "/sim/path",
                DeviceFrameworkSearchPath = "/device/path"
            };
            Assert.Equal("/path/to/Lib.xcframework", dep.XCFrameworkPath);
            Assert.Equal("Lib", dep.ModuleName);
            Assert.Equal("1.0.0", dep.PackageVersion);
            Assert.Equal("Lib.Custom", dep.PackageId);
            Assert.Equal("/sim/path", dep.SimulatorFrameworkSearchPath);
            Assert.Equal("/device/path", dep.DeviceFrameworkSearchPath);
        }

        [Fact]
        public void IsObjCOnly_DefaultFalse()
        {
            var dep = new FrameworkDependencyInfo
            {
                XCFrameworkPath = "/path/to/Lib.xcframework",
                ModuleName = "Lib"
            };
            Assert.False(dep.IsObjCOnly);
        }

        [Fact]
        public void IsObjCOnly_WhenSet_ReturnsTrue()
        {
            var dep = new FrameworkDependencyInfo
            {
                XCFrameworkPath = "/path/to/Stripe3DS2.xcframework",
                ModuleName = "Stripe3DS2",
                IsObjCOnly = true
            };
            Assert.True(dep.IsObjCOnly);
        }
    }
}
