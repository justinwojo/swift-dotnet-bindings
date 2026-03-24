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

    public interface ISwiftStruct : ISwiftObject
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

public class StructProxy : Swift.Runtime.ISwiftStruct
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

    // Span coordinates: line depends on MockTypes length. Column 13 = start of
    // variable name after "        var " (8 spaces + "var "). End column varies by type name length.

    [Fact]
    public async Task UndisposedClassLocal_ReportsInfo()
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

        // FooProxy implements ISwiftObject (class type) — Info severity
        var expected = new DiagnosticResult(SwiftObjectDisposeAnalyzer.DiagnosticId, DiagnosticSeverity.Info)
            .WithSpan(43, 13, 43, 31)
            .WithArguments("x");

        var test = new AnalyzerTest
        {
            TestCode = testCode,
            ExpectedDiagnostics = { expected },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task UndisposedStructLocal_ReportsInfo()
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

        // StructProxy implements ISwiftStruct — Info severity (finalizer-safe VWT Destroy via Cdecl trampoline)
        var expected = new DiagnosticResult(SwiftObjectDisposeAnalyzer.DiagnosticId, DiagnosticSeverity.Info)
            .WithSpan(43, 13, 43, 34)
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
    public async Task PassedToMethod_StillReportsDiagnostic()
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

        // FooProxy is a class type — Info severity
        var expected = new DiagnosticResult(SwiftObjectDisposeAnalyzer.DiagnosticId, DiagnosticSeverity.Info)
            .WithSpan(43, 13, 43, 31)
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
    public async Task DerivedClassType_ReportsInfo()
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

        // DerivedProxy extends FooProxy (ISwiftObject, not ISwiftStruct) — Info severity
        var expected = new DiagnosticResult(SwiftObjectDisposeAnalyzer.DiagnosticId, DiagnosticSeverity.Info)
            .WithSpan(43, 13, 43, 35)
            .WithArguments("x");

        var test = new AnalyzerTest
        {
            TestCode = testCode,
            ExpectedDiagnostics = { expected },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task ConditionalDispose_StillReportsDiagnostic()
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

        // FooProxy is a class type — Info severity
        var expected = new DiagnosticResult(SwiftObjectDisposeAnalyzer.DiagnosticId, DiagnosticSeverity.Info)
            .WithSpan(43, 13, 43, 31)
            .WithArguments("x");

        var test = new AnalyzerTest
        {
            TestCode = testCode,
            ExpectedDiagnostics = { expected },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task ConditionalDisposeInBlock_StillReportsDiagnostic()
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

        // FooProxy is a class type — Info severity
        var expected = new DiagnosticResult(SwiftObjectDisposeAnalyzer.DiagnosticId, DiagnosticSeverity.Info)
            .WithSpan(43, 13, 43, 31)
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
    public async Task OutsideSwiftDisposeScope_StillReportsDiagnostic()
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

        // x is declared BEFORE the scope — should still report (Info for class type)
        // y is declared AFTER the scope — should NOT report
        var expected = new DiagnosticResult(SwiftObjectDisposeAnalyzer.DiagnosticId, DiagnosticSeverity.Info)
            .WithSpan(43, 13, 43, 31)
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
