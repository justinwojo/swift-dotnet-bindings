// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using BindingsGeneration.ObjC;
using Xunit;
using static BindingsGeneration.Tests.ObjCTests.ObjCTestHelpers;

namespace BindingsGeneration.Tests.ObjCTests;

public class ObjCAvailabilityEmitterTests
{

    [Fact]
    public void EmitAvailability_Introduced_EmitsIntroducedAttribute()
    {
        var sb = new StringBuilder();
        var avail = new List<ObjCAvailability>
        {
            new() { Platform = "ios", IntroducedVersion = "14.0" }
        };

        var isUnavailable = ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, avail, "    ");

        Assert.False(isUnavailable);
        Assert.Contains("[Introduced(PlatformName.iOS, 14, 0)]", sb.ToString());
    }

    [Fact]
    public void EmitAvailability_Deprecated_EmitsDeprecatedAttribute()
    {
        var sb = new StringBuilder();
        var avail = new List<ObjCAvailability>
        {
            new() { Platform = "ios", DeprecatedVersion = "15.0" }
        };

        var isUnavailable = ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, avail, "    ");

        Assert.False(isUnavailable);
        Assert.Contains("[Deprecated(PlatformName.iOS, 15, 0)]", sb.ToString());
    }

    [Fact]
    public void EmitAvailability_DeprecatedWithMessage_IncludesMessage()
    {
        var sb = new StringBuilder();
        var avail = new List<ObjCAvailability>
        {
            new() { Platform = "ios", DeprecatedVersion = "16.0", Message = "Use newMethod instead" }
        };

        var isUnavailable = ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, avail, "    ");

        Assert.False(isUnavailable);
        Assert.Contains("[Deprecated(PlatformName.iOS, 16, 0, message: \"Use newMethod instead\")]", sb.ToString());
    }

    [Fact]
    public void EmitAvailability_Obsoleted_EmitsObsoletedAttribute()
    {
        var sb = new StringBuilder();
        var avail = new List<ObjCAvailability>
        {
            new() { Platform = "ios", ObsoletedVersion = "17.0" }
        };

        var isUnavailable = ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, avail, "    ");

        Assert.False(isUnavailable);
        Assert.Contains("[Obsoleted(PlatformName.iOS, 17, 0)]", sb.ToString());
    }

    [Fact]
    public void EmitAvailability_ObsoletedWithMessage_IncludesMessage()
    {
        var sb = new StringBuilder();
        var avail = new List<ObjCAvailability>
        {
            new() { Platform = "ios", ObsoletedVersion = "17.0", Message = "No longer supported" }
        };

        var isUnavailable = ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, avail, "    ");

        Assert.False(isUnavailable);
        Assert.Contains("[Obsoleted(PlatformName.iOS, 17, 0, message: \"No longer supported\")]", sb.ToString());
    }

    [Fact]
    public void EmitAvailability_Unavailable_ReturnsTrueAndEmitsNothing()
    {
        var sb = new StringBuilder();
        var avail = new List<ObjCAvailability>
        {
            new() { Platform = "ios", IsUnavailable = true }
        };

        var isUnavailable = ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, avail, "    ");

        Assert.True(isUnavailable);
        Assert.Empty(sb.ToString());
    }

    [Fact]
    public void EmitAvailability_NonIosPlatform_IgnoresEntry()
    {
        var sb = new StringBuilder();
        var avail = new List<ObjCAvailability>
        {
            new() { Platform = "macos", IntroducedVersion = "12.0" }
        };

        var isUnavailable = ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, avail, "    ");

        Assert.False(isUnavailable);
        Assert.Empty(sb.ToString());
    }

    [Fact]
    public void EmitAvailability_MixedAvailability_UnavailableAfterIntroduced_ReturnsTrue()
    {
        var sb = new StringBuilder();
        var avail = new List<ObjCAvailability>
        {
            new() { Platform = "ios", IntroducedVersion = "14.0" },
            new() { Platform = "ios", IsUnavailable = true }
        };

        var isUnavailable = ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, avail, "    ");

        Assert.True(isUnavailable);
        // Should NOT have emitted anything since symbol is unavailable
        Assert.Empty(sb.ToString());
    }

    [Fact]
    public void EmitAvailability_MessageWithQuotes_EscapesProperly()
    {
        var sb = new StringBuilder();
        var avail = new List<ObjCAvailability>
        {
            new() { Platform = "ios", DeprecatedVersion = "16.0", Message = "Use \"newAPI\" instead" }
        };

        var isUnavailable = ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, avail, "    ");

        Assert.False(isUnavailable);
        Assert.Contains("message: \"Use \\\"newAPI\\\" instead\"", sb.ToString());
    }

    [Fact]
    public void ApiDefinitionEmitter_SkipsUnavailableClass()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "DeprecatedClass",
                    Availability = [new ObjCAvailability { Platform = "ios", IsUnavailable = true }]
                }
            ]
        };

        var dir = Path.Combine(Path.GetTempPath(), $"avail_test_{Guid.NewGuid():N}");
        try
        {
            var diag = new ObjCBindingDiagnostics();
            var path = ApiDefinitionEmitter.Emit(module, dir, "TestNamespace", Logger, diag);
            var content = File.ReadAllText(path);
            Assert.DoesNotContain("DeprecatedClass", content);
            Assert.Single(diag.SkippedSymbols);
            Assert.Equal(ObjCSkipReason.UnavailableApi, diag.SkippedSymbols[0].Reason);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ApiDefinitionEmitter_SkipsUnavailableMethod()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "MyClass",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "doThing",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true,
                            Availability = [new ObjCAvailability { Platform = "ios", IsUnavailable = true }]
                        },
                        new ObjCMethodDecl
                        {
                            Selector = "doOtherThing",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true,
                        }
                    ]
                }
            ]
        };

        var dir = Path.Combine(Path.GetTempPath(), $"avail_test_{Guid.NewGuid():N}");
        try
        {
            var diag = new ObjCBindingDiagnostics();
            var path = ApiDefinitionEmitter.Emit(module, dir, "TestNamespace", Logger, diag);
            var content = File.ReadAllText(path);
            Assert.DoesNotContain("doThing", content);
            Assert.Contains("DoOtherThing", content);
            Assert.Single(diag.SkippedSymbols);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void StructsAndEnumsEmitter_EmitsEnumAvailability()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums =
            [
                new ObjCEnumDecl
                {
                    Name = "TLStatus",
                    Availability = [new ObjCAvailability { Platform = "ios", IntroducedVersion = "13.0", DeprecatedVersion = "16.0", Message = "Use TLState" }],
                    Cases = [new ObjCEnumCaseDecl { Name = "TLStatusA" }]
                }
            ]
        };

        var dir = Path.Combine(Path.GetTempPath(), $"avail_test_{Guid.NewGuid():N}");
        try
        {
            var result = StructsAndEnumsEmitter.Emit(module, dir, "TestLib.Binding", Logger);
            Assert.NotNull(result);
            var content = File.ReadAllText(result!.FilePath);
            Assert.Contains("[Introduced(PlatformName.iOS, 13, 0)]", content);
            Assert.Contains("[Deprecated(PlatformName.iOS, 16, 0, message: \"Use TLState\")]", content);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ApiDefinitionEmitter_UnavailableClassWithDocComment_NoOrphanedDocs()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "OldClass",
                    DocComment = "This class is deprecated.",
                    Availability = [new ObjCAvailability { Platform = "ios", IsUnavailable = true }]
                },
                new ObjCClassDecl
                {
                    Name = "NewClass",
                }
            ]
        };

        var dir = Path.Combine(Path.GetTempPath(), $"avail_test_{Guid.NewGuid():N}");
        try
        {
            var path = ApiDefinitionEmitter.Emit(module, dir, "TestNamespace", Logger);
            var content = File.ReadAllText(path);
            Assert.DoesNotContain("OldClass", content);
            Assert.DoesNotContain("This class is deprecated.", content);
            Assert.Contains("NewClass", content);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ApiDefinitionEmitter_UnavailableMethodWithDocComment_NoOrphanedDocs()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "MyClass",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "oldMethod",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true,
                            DocComment = "This method is old.",
                            Availability = [new ObjCAvailability { Platform = "ios", IsUnavailable = true }]
                        },
                        new ObjCMethodDecl
                        {
                            Selector = "newMethod",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true,
                        }
                    ]
                }
            ]
        };

        var dir = Path.Combine(Path.GetTempPath(), $"avail_test_{Guid.NewGuid():N}");
        try
        {
            var path = ApiDefinitionEmitter.Emit(module, dir, "TestNamespace", Logger);
            var content = File.ReadAllText(path);
            Assert.DoesNotContain("oldMethod", content);
            Assert.DoesNotContain("This method is old.", content);
            Assert.Contains("NewMethod", content);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ApiDefinitionEmitter_UnavailablePropertyWithDocComment_NoOrphanedDocs()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "MyClass",
                    Properties =
                    [
                        new ObjCPropertyDecl
                        {
                            Name = "oldProp",
                            Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                            DocComment = "This property is old.",
                            Availability = [new ObjCAvailability { Platform = "ios", IsUnavailable = true }]
                        },
                        new ObjCPropertyDecl
                        {
                            Name = "newProp",
                            Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                        }
                    ]
                }
            ]
        };

        var dir = Path.Combine(Path.GetTempPath(), $"avail_test_{Guid.NewGuid():N}");
        try
        {
            var path = ApiDefinitionEmitter.Emit(module, dir, "TestNamespace", Logger);
            var content = File.ReadAllText(path);
            Assert.DoesNotContain("This property is old.", content);
            Assert.Contains("NewProp", content);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void StructsAndEnumsEmitter_UnavailableEnumWithDocComment_NoOrphanedDocs()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums =
            [
                new ObjCEnumDecl
                {
                    Name = "TLOldEnum",
                    DocComment = "This enum is old.",
                    Availability = [new ObjCAvailability { Platform = "ios", IsUnavailable = true }],
                    Cases = [new ObjCEnumCaseDecl { Name = "TLOldEnumA" }]
                },
                new ObjCEnumDecl
                {
                    Name = "TLNewEnum",
                    Cases = [new ObjCEnumCaseDecl { Name = "TLNewEnumA" }]
                }
            ]
        };

        var dir = Path.Combine(Path.GetTempPath(), $"avail_test_{Guid.NewGuid():N}");
        try
        {
            var result = StructsAndEnumsEmitter.Emit(module, dir, "TestLib.Binding", Logger);
            Assert.NotNull(result);
            var content = File.ReadAllText(result!.FilePath);
            Assert.DoesNotContain("This enum is old.", content);
            Assert.DoesNotContain("TLOldEnum", content);
            Assert.Contains("TLNewEnum", content);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void StructsAndEnumsEmitter_SkipsUnavailableEnum()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Enums =
            [
                new ObjCEnumDecl
                {
                    Name = "TLOldStatus",
                    Availability = [new ObjCAvailability { Platform = "ios", IsUnavailable = true }],
                    Cases = [new ObjCEnumCaseDecl { Name = "TLOldStatusA" }]
                },
                new ObjCEnumDecl
                {
                    Name = "TLNewStatus",
                    Cases = [new ObjCEnumCaseDecl { Name = "TLNewStatusA" }]
                }
            ]
        };

        var dir = Path.Combine(Path.GetTempPath(), $"avail_test_{Guid.NewGuid():N}");
        try
        {
            var diag = new ObjCBindingDiagnostics();
            var result = StructsAndEnumsEmitter.Emit(module, dir, "TestLib.Binding", Logger, diag);
            Assert.NotNull(result);
            var content = File.ReadAllText(result!.FilePath);
            Assert.DoesNotContain("TLOldStatus", content);
            Assert.Contains("TLNewStatus", content);
            Assert.Single(diag.SkippedSymbols);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }
}
