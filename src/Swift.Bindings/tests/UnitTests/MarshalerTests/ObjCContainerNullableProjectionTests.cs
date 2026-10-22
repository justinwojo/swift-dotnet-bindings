// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Compiles the shared bridge expressions under both the legacy oblivious Apple API contract and
/// the nullable contract (NSArray readers return T?[]? and NSObject.ToString returns string?).
/// The public Swift contracts stay non-null, including elements of optional collections.
/// </summary>
public class ObjCContainerNullableProjectionTests
{
    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, false, 1)]
    [InlineData(false, false, 2)]
    [InlineData(false, true, 0)]
    [InlineData(false, true, 1)]
    [InlineData(false, true, 2)]
    [InlineData(true, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(true, false, 2)]
    [InlineData(true, true, 0)]
    [InlineData(true, true, 1)]
    [InlineData(true, true, 2)]
    public void ArrayReturn_CompilesWithNonNullableElements(bool nullableAppleApis, bool typedEnum, int nestedDepth)
    {
        ITypeProjection element = typedEnum
            ? new ObjCBridgeableProjection("Vision.Barcode", new AppleTypedEnumAdapter("Vision.Barcode", "Foundation.NSString"))
            : new ObjCBridgeableProjection("Foundation.NSUrl");
        for (var depth = 0; depth < nestedDepth; depth++)
            element = new ArrayProjection(element, isParameter: false);
        var projection = new ArrayProjection(element, isParameter: false);
        var optional = new OptionalProjection(projection);
        var direct = projection.GetReturnPlan("result", ReturnStrategy.Direct);
        var optionalDirect = optional.GetReturnPlan("result", ReturnStrategy.Direct);
        var optionalIndirect = optional.GetReturnPlan("result", ReturnStrategy.IndirectResult);
        var type = projection.PublicType;

        var source = $$"""
            public static class Bridge
            {
                public static {{type}} Direct(IntPtr result) { {{ReturnBody(direct)}} }
                public static {{type}} Accessor(IntPtr result) => {{projection.GetReturnContainerConversion("result")}};
                public static {{type}} Borrowed(Foundation.NSArray element) => {{projection.GetReturnElementConversion("element")}};
                public static {{type}}? Optional(IntPtr result) => {{optionalDirect.PInvokeExpression}};
                public static unsafe {{type}}? OptionalIndirect(IntPtr result) => {{optionalIndirect.PInvokeExpression}};
                public static void Async(IntPtr result, System.Threading.Tasks.TaskCompletionSource<{{type}}> completion)
                    => completion.TrySetResult({{direct.PInvokeExpression}});
                public static void OptionalAsync(IntPtr result, System.Threading.Tasks.TaskCompletionSource<{{type}}?> completion)
                    => completion.TrySetResult({{optionalDirect.PInvokeExpression}});
            }
            """;

        AssertCompiles(source, nullableAppleApis);
        Assert.Contains("result == IntPtr.Zero ?", optionalDirect.PInvokeExpression);
        Assert.Contains("null :", optionalDirect.PInvokeExpression);
    }

    [Theory]
    [InlineData(false, "string-key")]
    [InlineData(false, "string-value")]
    [InlineData(false, "typed-enum")]
    [InlineData(true, "string-key")]
    [InlineData(true, "string-value")]
    [InlineData(true, "typed-enum")]
    public void DictionaryReturn_CompilesWithNonNullableKeysAndValues(bool nullableAppleApis, string shape)
    {
        var url = new ObjCBridgeableProjection("Foundation.NSUrl");
        var text = new StringProjection();
        var barcode = new ObjCBridgeableProjection("Vision.Barcode", new AppleTypedEnumAdapter("Vision.Barcode", "Foundation.NSString"));
        var projection = shape switch
        {
            "string-value" => new DictionaryProjection(url, text, isParameter: false),
            "typed-enum" => new DictionaryProjection(text, barcode, isParameter: false),
            _ => new DictionaryProjection(text, url, isParameter: false)
        };
        var optional = new OptionalProjection(projection);
        var direct = projection.GetReturnPlan("result", ReturnStrategy.Direct);
        var optionalDirect = optional.GetReturnPlan("result", ReturnStrategy.Direct);
        var type = projection.PublicType;

        var source = $$"""
            public static class Bridge
            {
                public static {{type}} Direct(IntPtr result) { {{ReturnBody(direct)}} }
                public static {{type}} Accessor(IntPtr result) => {{projection.GetReturnContainerConversion("result")}};
                public static {{type}}? Optional(IntPtr result) => {{optionalDirect.PInvokeExpression}};
                public static void Async(IntPtr result, System.Threading.Tasks.TaskCompletionSource<{{type}}> completion)
                    => completion.TrySetResult(Accessor(result));
            }
            """;

        AssertCompiles(source, nullableAppleApis);
        Assert.Contains("result == IntPtr.Zero ?", optionalDirect.PInvokeExpression);
        Assert.Contains("null :", optionalDirect.PInvokeExpression);
    }

    [Fact]
    public void NullableAppleApis_UnassertedBridgeReads_ProduceNullabilityErrors()
    {
        // Positive control: these API stubs must expose the actual compiler failure, rather than
        // accidentally testing against an oblivious or non-nullable substitute.
        var diagnostics = Compile("""
            public static class Bridge
            {
                public static IReadOnlyList<Foundation.NSUrl> Array(IntPtr result)
                    => Foundation.NSArray.ArrayFromHandleFunc<Foundation.NSUrl>(result,
                        h => ObjCRuntime.Runtime.GetNSObject<Foundation.NSUrl>(h)!, true);
                public static void Dictionary(Foundation.NSObject key, Dictionary<string, int> result)
                    => result[key.ToString()] = 1;
            }
            """, nullableAppleApis: true);

        Assert.Contains(diagnostics, d => d.Id == "CS8619");
        Assert.Contains(diagnostics, d => d.Id == "CS8604");
    }

    private static string ReturnBody(MarshalPlan plan)
        => string.Join("\n", plan.SetupStatements.Cast<MarshalStatement.Line>().Select(s => s.Code))
            + $"\nreturn {plan.PInvokeExpression};";

    private static void AssertCompiles(string source, bool nullableAppleApis)
    {
        var errors = Compile(source, nullableAppleApis);
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    private static List<Diagnostic> Compile(string source, bool nullableAppleApis)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            .Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("ObjCBridgeNullable" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText("""
                #nullable enable
                using System;
                using System.Collections.Generic;
                """ + "\n" + source + "\n" + AppleApiStubs(nullableAppleApis))], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable,
                generalDiagnosticOption: ReportDiagnostic.Error));
        return compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
    }

    private static string AppleApiStubs(bool nullableAppleApis)
    {
        var nullable = nullableAppleApis ? "?" : "";
        return $$"""
            #nullable {{(nullableAppleApis ? "enable" : "disable")}}
            namespace Foundation
            {
                public class NSObject
                {
                    public IntPtr Handle => throw new NotSupportedException();
                    public override string{{nullable}} ToString() => throw new NotSupportedException();
                }
                public class NSUrl : NSObject { }
                public class NSString : NSObject { }
                public class NSArray : NSObject
                {
                    public static T{{nullable}}[]{{nullable}} ArrayFromHandleFunc<T>(IntPtr handle,
                        Func<IntPtr, T{{nullable}}> factory, bool releaseHandle) where T : NSObject
                        => throw new NotSupportedException();
                    public static T{{nullable}}[]{{nullable}} ArrayFromHandle<T>(IntPtr handle) where T : NSObject
                        => throw new NotSupportedException();
                }
                public class NSDictionary : NSObject
                {
                    public nuint Count => throw new NotSupportedException();
                    public NSObject[] Keys => throw new NotSupportedException();
                    public NSObject{{nullable}} ObjectForKey(NSObject key) => throw new NotSupportedException();
                }
            }
            namespace ObjCRuntime
            {
                public static class Runtime
                {
                    public static T{{nullable}} GetNSObject<T>(IntPtr handle) where T : Foundation.NSObject
                        => throw new NotSupportedException();
                    public static T{{nullable}} GetINativeObject<T>(IntPtr handle, bool owns) where T : Foundation.NSObject
                        => throw new NotSupportedException();
                }
            }
            namespace Vision
            {
                public enum Barcode { QR }
                public static class BarcodeExtensions
                {
                    public static Barcode GetValue(Foundation.NSString constant) => throw new NotSupportedException();
                }
            }
            """;
    }
}
