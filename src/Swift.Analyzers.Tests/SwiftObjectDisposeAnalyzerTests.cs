// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace Swift.Analyzers.Tests;

using AnalyzerTest = CSharpAnalyzerTest<SwiftObjectDisposeAnalyzer, DefaultVerifier>;

public class SwiftObjectDisposeAnalyzerTests
{
    /// <summary>
    /// Mock ISwiftObject interface and FooProxy class that tests compile against.
    /// The analyzer resolves types via semantic model, so these must be present.
    /// </summary>
    private const string MockTypes = @"
using System;

namespace Swift.Runtime
{
    public interface ISwiftObject : IDisposable
    {
    }

    public sealed class SwiftDisposeScope : IDisposable
    {
        public void Dispose() { }
    }
}

public class FooProxy : Swift.Runtime.ISwiftObject
{
    public void Dispose() { }
}

public class DerivedProxy : FooProxy
{
}

public class RegularDisposable : IDisposable
{
    public void Dispose() { }
}
";

    // The variable declarator for "var x = new FooProxy();" in a method at
    // depth 2 (class > method > block) starts at line 29 after the MockTypes preamble.
    // Column 13 = start of variable name after "        var " (8 spaces + "var ").
    // Column 31 = end of declarator "x = new FooProxy()".

    [Fact]
    public async Task UndisposedLocal_Warns()
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

        var expected = new DiagnosticResult(SwiftObjectDisposeAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithSpan(34, 13, 34, 31)
            .WithArguments("x");

        var test = new AnalyzerTest
        {
            TestCode = testCode,
            ExpectedDiagnostics = { expected },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task UsingDeclaration_NoDiagnostic()
    {
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        using var x = new FooProxy();
    }
}
";

        var test = new AnalyzerTest
        {
            TestCode = testCode,
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task UsingStatement_NoDiagnostic()
    {
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        using (var x = new FooProxy())
        {
        }
    }
}
";

        var test = new AnalyzerTest
        {
            TestCode = testCode,
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task ExplicitDispose_NoDiagnostic()
    {
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        var x = new FooProxy();
        x.Dispose();
    }
}
";

        var test = new AnalyzerTest
        {
            TestCode = testCode,
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task ReturnedValue_NoDiagnostic()
    {
        var testCode = MockTypes + @"
public class TestClass
{
    public FooProxy Method()
    {
        var x = new FooProxy();
        return x;
    }
}
";

        var test = new AnalyzerTest
        {
            TestCode = testCode,
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task PassedToMethod_StillWarns()
    {
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        var x = new FooProxy();
        SomeMethod(x);
    }

    private void SomeMethod(FooProxy p) { }
}
";

        var expected = new DiagnosticResult(SwiftObjectDisposeAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithSpan(34, 13, 34, 31)
            .WithArguments("x");

        var test = new AnalyzerTest
        {
            TestCode = testCode,
            ExpectedDiagnostics = { expected },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task NonSwiftObject_NoDiagnostic()
    {
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        var x = new RegularDisposable();
    }
}
";

        var test = new AnalyzerTest
        {
            TestCode = testCode,
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task DerivedType_StillWarns()
    {
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        var x = new DerivedProxy();
    }
}
";

        var expected = new DiagnosticResult(SwiftObjectDisposeAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithSpan(34, 13, 34, 35)
            .WithArguments("x");

        var test = new AnalyzerTest
        {
            TestCode = testCode,
            ExpectedDiagnostics = { expected },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task ConditionalDispose_StillWarns()
    {
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method(bool flag)
    {
        var x = new FooProxy();
        if (flag) x.Dispose();
    }
}
";

        var expected = new DiagnosticResult(SwiftObjectDisposeAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithSpan(34, 13, 34, 31)
            .WithArguments("x");

        var test = new AnalyzerTest
        {
            TestCode = testCode,
            ExpectedDiagnostics = { expected },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task ConditionalDisposeInBlock_StillWarns()
    {
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method(bool flag)
    {
        var x = new FooProxy();
        if (flag)
        {
            x.Dispose();
        }
    }
}
";

        var expected = new DiagnosticResult(SwiftObjectDisposeAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithSpan(34, 13, 34, 31)
            .WithArguments("x");

        var test = new AnalyzerTest
        {
            TestCode = testCode,
            ExpectedDiagnostics = { expected },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task InsideSwiftDisposeScopeUsingDeclaration_NoDiagnostic()
    {
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        using var scope = new Swift.Runtime.SwiftDisposeScope();
        var x = new FooProxy();
    }
}
";

        var test = new AnalyzerTest
        {
            TestCode = testCode,
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task InsideSwiftDisposeScopeUsingStatement_NoDiagnostic()
    {
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        using (new Swift.Runtime.SwiftDisposeScope())
        {
            var x = new FooProxy();
        }
    }
}
";

        var test = new AnalyzerTest
        {
            TestCode = testCode,
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task InsideSwiftDisposeScopeUsingStatementWithDecl_NoDiagnostic()
    {
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        using (var scope = new Swift.Runtime.SwiftDisposeScope())
        {
            var x = new FooProxy();
        }
    }
}
";

        var test = new AnalyzerTest
        {
            TestCode = testCode,
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task NestedInsideSwiftDisposeScope_NoDiagnostic()
    {
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        using (new Swift.Runtime.SwiftDisposeScope())
        {
            var x = new FooProxy();
            if (true)
            {
                var y = new FooProxy();
            }
        }
    }
}
";

        var test = new AnalyzerTest
        {
            TestCode = testCode,
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task InsideSwiftDisposeScopeNestedBlock_NoDiagnostic()
    {
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method(bool cond)
    {
        using var scope = new Swift.Runtime.SwiftDisposeScope();
        if (cond)
        {
            var x = new FooProxy();
        }
    }
}
";

        var test = new AnalyzerTest
        {
            TestCode = testCode,
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task OutsideSwiftDisposeScope_StillWarns()
    {
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        var x = new FooProxy();
        using var scope = new Swift.Runtime.SwiftDisposeScope();
        var y = new FooProxy();
    }
}
";

        // x is declared BEFORE the scope — should still warn
        // y is declared AFTER the scope — should NOT warn
        var expected = new DiagnosticResult(SwiftObjectDisposeAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithSpan(34, 13, 34, 31)
            .WithArguments("x");

        var test = new AnalyzerTest
        {
            TestCode = testCode,
            ExpectedDiagnostics = { expected },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task TryFinallyDispose_NoDiagnostic()
    {
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        var x = new FooProxy();
        try
        {
            // use x
        }
        finally
        {
            x.Dispose();
        }
    }
}
";

        var test = new AnalyzerTest
        {
            TestCode = testCode,
        };

        await test.RunAsync();
    }
}
