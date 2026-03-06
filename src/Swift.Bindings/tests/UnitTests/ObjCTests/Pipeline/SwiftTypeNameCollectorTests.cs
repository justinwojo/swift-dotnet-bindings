// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests.ObjCTests;

public class SwiftTypeNameCollectorTests
{
    private string CreateTempDirWithFiles(Dictionary<string, string> files)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"collector_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        foreach (var (name, content) in files)
            File.WriteAllText(Path.Combine(dir, name), content);
        return dir;
    }

    [Fact]
    public void CollectsPublicClasses()
    {
        var dir = CreateTempDirWithFiles(new()
        {
            ["Module.cs"] = "namespace M {\n  public class Foo { }\n  public class Bar { }\n}"
        });
        try
        {
            var names = BindingsGenerator.CollectSwiftEmittedTypeNames(dir);
            Assert.Contains("Foo", names);
            Assert.Contains("Bar", names);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CollectsUnsafeClasses()
    {
        var dir = CreateTempDirWithFiles(new()
        {
            ["Module.cs"] = "public unsafe class UnsafeClass { }"
        });
        try
        {
            var names = BindingsGenerator.CollectSwiftEmittedTypeNames(dir);
            Assert.Contains("UnsafeClass", names);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CollectsStructsEnumsInterfaces()
    {
        var dir = CreateTempDirWithFiles(new()
        {
            ["Module.cs"] = """
                public struct MyStruct { }
                public enum MyEnum { }
                public interface IMyProtocol { }
                """
        });
        try
        {
            var names = BindingsGenerator.CollectSwiftEmittedTypeNames(dir);
            Assert.Contains("MyStruct", names);
            Assert.Contains("MyEnum", names);
            Assert.Contains("IMyProtocol", names);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void IgnoresInternalTypes()
    {
        var dir = CreateTempDirWithFiles(new()
        {
            ["Module.cs"] = """
                public class PublicType { }
                internal class InternalType { }
                class DefaultType { }
                """
        });
        try
        {
            var names = BindingsGenerator.CollectSwiftEmittedTypeNames(dir);
            Assert.Contains("PublicType", names);
            Assert.DoesNotContain("InternalType", names);
            Assert.DoesNotContain("DefaultType", names);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void HandlesInheritanceColon()
    {
        var dir = CreateTempDirWithFiles(new()
        {
            ["Module.cs"] = "public class Derived : Base { }"
        });
        try
        {
            var names = BindingsGenerator.CollectSwiftEmittedTypeNames(dir);
            Assert.Contains("Derived", names);
            Assert.DoesNotContain("Base", names);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CollectsPartialClasses()
    {
        var dir = CreateTempDirWithFiles(new()
        {
            ["Module.cs"] = "public partial class PartialType { }"
        });
        try
        {
            var names = BindingsGenerator.CollectSwiftEmittedTypeNames(dir);
            Assert.Contains("PartialType", names);
        }
        finally { Directory.Delete(dir, true); }
    }
}
