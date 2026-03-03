// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace Swift.Analyzers.Tests;

using CodeFixTest = CSharpCodeFixTest<SwiftObjectDisposeAnalyzer, SwiftObjectDisposeCodeFixProvider, DefaultVerifier>;

public class SwiftObjectDisposeCodeFixTests
{
    private const string MockTypes = @"
using System;

namespace Swift.Runtime
{
    public interface ISwiftObject : IDisposable
    {
    }
}

public class FooProxy : Swift.Runtime.ISwiftObject
{
    public void Dispose() { }
}
";

    [Fact]
    public async Task CodeFix_AddsUsing()
    {
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        var x = new FooProxy();
    }
}
";

        var fixedCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        using var x = new FooProxy();
    }
}
";

        var expected = new DiagnosticResult(SwiftObjectDisposeAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithSpan(20, 13, 20, 31)
            .WithArguments("x");

        var test = new CodeFixTest
        {
            TestCode = testCode,
            FixedCode = fixedCode,
            ExpectedDiagnostics = { expected },
        };

        await test.RunAsync();
    }
}
