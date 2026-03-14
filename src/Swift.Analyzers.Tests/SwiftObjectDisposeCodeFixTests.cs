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

    public interface ISwiftStruct : ISwiftObject
    {
    }

    public sealed class SwiftDisposeScope : IDisposable
    {
        public void Dispose() { }
    }
}

public class StructProxy : Swift.Runtime.ISwiftStruct
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
        var x = new StructProxy();
    }
}
";

        var fixedCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        using var x = new StructProxy();
    }
}
";

        // StructProxy implements ISwiftStruct — Warning severity
        var expected = new DiagnosticResult(SwiftObjectDisposeAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithSpan(29, 13, 29, 34)
            .WithArguments("x");

        var test = new CodeFixTest
        {
            TestCode = testCode,
            FixedCode = fixedCode,
            ExpectedDiagnostics = { expected },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task CodeFix_WrapsInSwiftDisposeScope()
    {
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        var x = new StructProxy();
    }
}
";

        var fixedCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        using var _ = new Swift.Runtime.SwiftDisposeScope();
        var x = new StructProxy();
    }
}
";

        // StructProxy implements ISwiftStruct — Warning severity
        var expected = new DiagnosticResult(SwiftObjectDisposeAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithSpan(29, 13, 29, 34)
            .WithArguments("x");

        var test = new CodeFixTest
        {
            TestCode = testCode,
            FixedCode = fixedCode,
            ExpectedDiagnostics = { expected },
            CodeActionIndex = 1, // "Wrap in SwiftDisposeScope" (index 0 is "Add 'using'")
        };

        await test.RunAsync();
    }
}
