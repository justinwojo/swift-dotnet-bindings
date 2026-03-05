// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using Xunit;

namespace Swift.Bindings.Tests.SdkTests;

/// <summary>
/// Compile-smoke tests that verify representative generated C# patterns compile
/// successfully against the real Swift.Runtime.dll. These catch API mismatches
/// like P14-3 (handle.Pointer → handle.Handle) and P14-6 (.Payload on ObjC-rooted types)
/// that string-pattern unit tests miss.
///
/// Gap addressed: V3 (compile-smoke), V5 (runtime API compatibility).
/// </summary>
public class CompileSmokeTests : IDisposable
{
    private readonly string _tempDir;
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string RuntimeDll = Path.Combine(
        RepoRoot, "src", "Swift.Runtime", "src", "bin", "Debug", "net10.0-ios", "Swift.Runtime.dll");

    private const string CommonUsings = @"
#nullable enable
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;
using Swift;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
";

    public CompileSmokeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "compile-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static void RequireRuntimeDll()
    {
        Assert.True(File.Exists(RuntimeDll),
            $"Swift.Runtime.dll not found at {RuntimeDll}. Build the runtime first: dotnet build src/Swift.Runtime/src");
    }

    static string MakeSwiftClassBody(string typeName)
    {
        return @"
    static nuint _payloadSize = 8;
    [EditorBrowsable(EditorBrowsableState.Never)]
    SwiftSafeHandle<" + typeName + @"> _payload = SwiftSafeHandle<" + typeName + @">.Zero;
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal SwiftSafeHandle<" + typeName + @"> Payload => _payload;
    IntPtr ISwiftObject.SwiftHandle => _payload.DangerousGetHandle();

    public void Dispose()
    {
        _payload.Dispose();
        GC.SuppressFinalize(this);
    }

    " + typeName + @"(SwiftHandle handle)
    {
        _payload = new SwiftSafeHandle<" + typeName + @">(handle);
    }

    protected " + typeName + @"(SwiftInheritanceChain _swiftObject) { }

    [DllImport(""/tmp/test.dylib"", EntryPoint = ""test_meta"")]
    internal static extern TypeMetadata PInvoke_getMetadata();

    static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();

    [EditorBrowsable(EditorBrowsableState.Never)]
    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        return new " + typeName + @"(handle);
    }

    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
        where TProtocol : class
    {
        throw new NotSupportedException();
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        nint payload = _payload.DangerousGetHandle();
        MemoryMarshal.Write(swiftDestSpan, in payload);
        return sizeof(nint);
    }
";
    }

    /// <summary>
    /// Verifies that a Swift class with SwiftSafeHandle uses correct boilerplate.
    /// Regression guard for P14-3 (handle.Handle vs handle.Pointer).
    /// </summary>
    [Fact]
    [Trait("Category", "CompileSmoke")]
    [Trait("Category", "Regression")]
    public void SwiftClass_HandleBoilerplate_Compiles()
    {
        RequireRuntimeDll();

        var code = CommonUsings + @"
namespace TestModule
{
    public class TestClass : ISwiftObject, IDisposable
    {
" + MakeSwiftClassBody("TestClass") + @"
    }
}
";

        AssertCompiles(code, "SwiftClass_HandleBoilerplate");
    }

    /// <summary>
    /// Verifies ObjC-rooted class uses .Handle (not .Payload.DangerousGetHandle()).
    /// Regression guard for P14-6 and P14-11.
    /// </summary>
    [Fact]
    [Trait("Category", "CompileSmoke")]
    [Trait("Category", "Regression")]
    public void ObjCRootedClass_HandleProperty_Compiles()
    {
        RequireRuntimeDll();

        var code = CommonUsings + @"
namespace TestModule
{
    public class ObjCRootedType : Foundation.NSObject, ISwiftObject
    {
        internal ObjCRootedType(SwiftHandle handle) : base((ObjCRuntime.NativeHandle)handle.Handle)
        {
            DangerousRelease();
        }

        [DllImport(""/tmp/test.dylib"", EntryPoint = ""test_meta"")]
        internal static extern TypeMetadata PInvoke_getMetadata();

        static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();

        [EditorBrowsable(EditorBrowsableState.Never)]
        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
        {
            return new ObjCRootedType(handle);
        }

        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
            where TProtocol : class
        {
            throw new NotSupportedException();
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            nint payload = this.Handle.Handle;
            MemoryMarshal.Write(swiftDestSpan, in payload);
            return sizeof(nint);
        }
    }
}
";

        AssertCompiles(code, "ObjCRootedClass_HandleProperty");
    }

    /// <summary>
    /// Verifies simple enum (BX2) with extension methods compiles.
    /// Regression guard for P14-5.
    /// </summary>
    [Fact]
    [Trait("Category", "CompileSmoke")]
    [Trait("Category", "Regression")]
    public void SimpleEnum_WithExtensions_Compiles()
    {
        RequireRuntimeDll();

        var code = CommonUsings + @"
namespace TestModule
{
    public enum Priority : long
    {
        Low = 0,
        Normal = 1,
        High = 2,
    }

    public static class PriorityExtensions
    {
        [DllImport(""/tmp/test.dylib"", EntryPoint = ""test"")]
        private static extern long PInvoke_GetRawValue(long self);

        public static long GetRawValue(this Priority self)
        {
            return PInvoke_GetRawValue((long)self);
        }
    }
}
";

        AssertCompiles(code, "SimpleEnum_WithExtensions");
    }

    /// <summary>
    /// Verifies Optional&lt;ObjCRootedType&gt; pattern compiles (nullable pointer ABI).
    /// Regression guard for P14-4.
    /// </summary>
    [Fact]
    [Trait("Category", "CompileSmoke")]
    [Trait("Category", "Regression")]
    public void OptionalObjCRooted_NullablePattern_Compiles()
    {
        RequireRuntimeDll();

        var code = CommonUsings + @"
namespace TestModule
{
    public class ObjCClass : Foundation.NSObject, ISwiftObject
    {
        internal ObjCClass(SwiftHandle handle) : base((ObjCRuntime.NativeHandle)handle.Handle)
        {
            DangerousRelease();
        }

        [DllImport(""/tmp/test.dylib"", EntryPoint = ""test_meta"")]
        internal static extern TypeMetadata PInvoke_getMetadata();
        static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
        [EditorBrowsable(EditorBrowsableState.Never)]
        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle) => new ObjCClass(handle);
        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
            where TProtocol : class => throw new NotSupportedException();
        [EditorBrowsable(EditorBrowsableState.Never)]
        unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            nint payload = this.Handle.Handle;
            MemoryMarshal.Write(swiftDestSpan, in payload);
            return sizeof(nint);
        }
    }

    public class Container : ISwiftObject, IDisposable
    {
" + MakeSwiftClassBody("Container") + @"

        [DllImport(""/tmp/test.dylib"", EntryPoint = ""test_get"")]
        private static extern IntPtr PInvoke_GetOptionalObjC(IntPtr self);

        public virtual ObjCClass? OptionalProperty
        {
            get
            {
                var result = PInvoke_GetOptionalObjC(_payload.DangerousGetHandle());
                if (result == IntPtr.Zero) return null;
                return (ObjCClass)SwiftMarshal.MarshalFromSwift<ObjCClass>(result);
            }
        }
    }
}
";

        AssertCompiles(code, "OptionalObjCRooted_NullablePattern");
    }

    /// <summary>
    /// Verifies cross-module type reference compiles when assembly reference is provided.
    /// Regression guard for P14-7.
    /// </summary>
    [Fact]
    [Trait("Category", "CompileSmoke")]
    [Trait("Category", "Regression")]
    public void CrossModuleReference_WithAssemblyRef_Compiles()
    {
        RequireRuntimeDll();

        var coreCode = CommonUsings + @"
namespace CoreModule
{
    public class CoreType : ISwiftObject, IDisposable
    {
" + MakeSwiftClassBody("CoreType") + @"
    }
}
";

        var coreDll = CompileToTemp(coreCode, "CoreModule");
        if (string.IsNullOrEmpty(coreDll))
        {
            Assert.Fail("Failed to compile core module dependency");
            return;
        }

        var dependentCode = CommonUsings + @"
namespace DependentModule
{
    public class DependentType : ISwiftObject, IDisposable
    {
" + MakeSwiftClassBody("DependentType") + @"

        [DllImport(""/tmp/test.dylib"", EntryPoint = ""test_get"")]
        private static extern IntPtr PInvoke_GetCore(IntPtr self);

        public virtual CoreModule.CoreType GetCore()
        {
            var result = PInvoke_GetCore(_payload.DangerousGetHandle());
            return (CoreModule.CoreType)SwiftMarshal.MarshalFromSwift<CoreModule.CoreType>(result);
        }
    }
}
";

        AssertCompiles(dependentCode, "CrossModuleDependent", additionalRefs: new string[] { coreDll });
    }

    /// <summary>
    /// Verifies protocol proxy pattern compiles.
    /// </summary>
    [Fact]
    [Trait("Category", "CompileSmoke")]
    public void ProtocolProxy_Compiles()
    {
        RequireRuntimeDll();

        var code = CommonUsings + @"
namespace TestModule
{
    public interface ITestProtocol
    {
        string Name { get; }
    }

    public class TestProtocolProxy : ITestProtocol, ISwiftObject, IDisposable
    {
" + MakeSwiftClassBody("TestProtocolProxy") + @"

        [DllImport(""/tmp/test.dylib"", EntryPoint = ""test_get"")]
        private static extern IntPtr PInvoke_GetName(IntPtr self);

        public string Name
        {
            get
            {
                var result = PInvoke_GetName(_payload.DangerousGetHandle());
                return SwiftMarshal.MarshalFromSwift<SwiftString>(result).ToString();
            }
        }
    }
}
";

        AssertCompiles(code, "ProtocolProxy");
    }

    /// <summary>
    /// Verifies OptionSet (RawRepresentable) pattern with GetHashCode compiles.
    /// Regression guard for P14-8.
    /// </summary>
    [Fact]
    [Trait("Category", "CompileSmoke")]
    [Trait("Category", "Regression")]
    public void OptionSet_GetHashCode_Compiles()
    {
        RequireRuntimeDll();

        var code = CommonUsings + @"
namespace TestModule
{
    public class CacheOptions : ISwiftObject, IDisposable
    {
" + MakeSwiftClassBody("CacheOptions") + @"

        [DllImport(""/tmp/test.dylib"", EntryPoint = ""test_raw"")]
        private static extern nuint PInvoke_GetRawValue(IntPtr self);

        public nuint RawValue => PInvoke_GetRawValue(_payload.DangerousGetHandle());

        public override int GetHashCode() => RawValue.GetHashCode();
    }
}
";

        AssertCompiles(code, "OptionSet_GetHashCode");
    }

    // --- Helpers ---

    private void AssertCompiles(string code, string projectName, string[] additionalRefs = default)
    {
        var projDir = Path.Combine(_tempDir, projectName);
        Directory.CreateDirectory(projDir);

        File.WriteAllText(Path.Combine(projDir, projectName + ".cs"), code);

        var refItems = @"<Reference Include=""Swift.Runtime""><HintPath>" + RuntimeDll + @"</HintPath></Reference>";

        if (additionalRefs != null)
        {
            foreach (var refPath in additionalRefs)
            {
                var asmName = Path.GetFileNameWithoutExtension(refPath);
                refItems += @"<Reference Include=""" + asmName + @"""><HintPath>" + refPath + @"</HintPath></Reference>";
            }
        }

        var csproj = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFramework>net10.0-ios</TargetFramework>
    <SupportedOSPlatformVersion>15.0</SupportedOSPlatformVersion>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Nullable>enable</Nullable>
    <NoWarn>0169;CA1420</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <AssemblyAttribute Include=""System.Runtime.CompilerServices.DisableRuntimeMarshallingAttribute"" />
  </ItemGroup>
  <ItemGroup>
    " + refItems + @"
  </ItemGroup>
  <ItemGroup>
    <Compile Include=""" + projectName + @".cs"" />
  </ItemGroup>
</Project>";

        var csprojPath = Path.Combine(projDir, projectName + ".csproj");
        File.WriteAllText(csprojPath, csproj);

        var restore = RunProcess("dotnet", "restore \"" + csprojPath + "\" -v quiet");
        Assert.True(restore.ExitCode == 0,
            "Restore failed for " + projectName + ":\n" + restore.StdErr + "\n" + restore.StdOut);

        var build = RunProcess("dotnet",
            "build \"" + csprojPath + "\" -p:EnableDefaultCompileItems=false --no-restore -v quiet");
        Assert.True(build.ExitCode == 0,
            "Build failed for " + projectName + ":\n" + build.StdErr + "\n" + build.StdOut);
    }

    private string CompileToTemp(string code, string projectName)
    {
        var projDir = Path.Combine(_tempDir, projectName);
        Directory.CreateDirectory(projDir);

        File.WriteAllText(Path.Combine(projDir, projectName + ".cs"), code);

        var csproj = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFramework>net10.0-ios</TargetFramework>
    <SupportedOSPlatformVersion>15.0</SupportedOSPlatformVersion>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Nullable>enable</Nullable>
    <NoWarn>0169;CA1420</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <AssemblyAttribute Include=""System.Runtime.CompilerServices.DisableRuntimeMarshallingAttribute"" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include=""Swift.Runtime""><HintPath>" + RuntimeDll + @"</HintPath></Reference>
  </ItemGroup>
  <ItemGroup>
    <Compile Include=""" + projectName + @".cs"" />
  </ItemGroup>
</Project>";

        var csprojPath = Path.Combine(projDir, projectName + ".csproj");
        File.WriteAllText(csprojPath, csproj);

        var restore = RunProcess("dotnet", "restore \"" + csprojPath + "\" -v quiet");
        if (restore.ExitCode != 0) return "";

        var build = RunProcess("dotnet",
            "build \"" + csprojPath + "\" -p:EnableDefaultCompileItems=false --no-restore -v quiet");
        if (build.ExitCode != 0) return "";

        var dllPath = Path.Combine(projDir, "bin", "Debug", "net10.0-ios", projectName + ".dll");
        return File.Exists(dllPath) ? dllPath : "";
    }

    private static (int ExitCode, string StdOut, string StdErr) RunProcess(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)!;
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            return (-1, stdOut, "Process timed out after 60s and was killed");
        }
        return (process.ExitCode, stdOut, stdErr);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "SwiftBindings.sln")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return AppContext.BaseDirectory;
    }
}
