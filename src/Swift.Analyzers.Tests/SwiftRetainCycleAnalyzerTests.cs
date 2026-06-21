// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace Swift.Analyzers.Tests;

using AnalyzerTest = CSharpAnalyzerTest<SwiftRetainCycleAnalyzer, DefaultVerifier>;

/// <summary>
/// Tests for SB1002 (<see cref="SwiftRetainCycleAnalyzer"/>): a callback handed to a Swift-backed
/// object that captures that same object forms an unbreakable cross-heap retain cycle. The analyzer
/// fires on the self-capturing lambda and points the consumer at
/// <c>Swift.Runtime.WeakSwiftReference&lt;T&gt;</c>.
/// </summary>
public class SwiftRetainCycleAnalyzerTests
{
    /// <summary>
    /// Mock ISwiftObject, a Swift-backed proxy exposing a stored-callback setter + an instance
    /// method, WeakSwiftReference (the prescribed fix), and a plain class (negative control).
    /// </summary>
    private const string MockTypes = @"
using System;

namespace Swift.Runtime
{
    public interface ISwiftObject : IDisposable
    {
    }

    public sealed class WeakSwiftReference<T> where T : class, ISwiftObject
    {
        public WeakSwiftReference(T target) { }
        public T Target => throw new NotImplementedException();
    }
}

public class FooProxy : Swift.Runtime.ISwiftObject
{
    public void SetCallback(Action callback) { }
    public Action Handler { get; set; }
    public void DoWork() { }
    public void Dispose() { }
}

public class RegularClass
{
    public void SetCallback(Action callback) { }
    public void DoWork() { }
}
";

    [Fact]
    public async Task SelfCapturingCallback_ReportsDiagnostic()
    {
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        var obj = new FooProxy();
        obj.SetCallback({|SB1002:() => obj.DoWork()|});
    }
}
";

        var test = new AnalyzerTest { TestCode = testCode };
        await test.RunAsync();
    }

    [Fact]
    public async Task SelfCapturingAnonymousMethod_ReportsDiagnostic()
    {
        // The classic delegate form captures identically to a lambda.
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        var obj = new FooProxy();
        obj.SetCallback({|SB1002:delegate { obj.DoWork(); }|});
    }
}
";

        var test = new AnalyzerTest { TestCode = testCode };
        await test.RunAsync();
    }

    [Fact]
    public async Task SelfCapturingStoredProperty_ReportsDiagnostic()
    {
        // The dominant binding shape: a Swift stored closure property projects to a C# property setter,
        // so the cycle is formed by an assignment (`obj.Handler = ...`), not a method call. This is the
        // exact shape the F35 runtime fixture uses.
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        var obj = new FooProxy();
        obj.Handler = {|SB1002:() => obj.DoWork()|};
    }
}
";

        var test = new AnalyzerTest { TestCode = testCode };
        await test.RunAsync();
    }

    [Fact]
    public async Task SelfCapturingStoredProperty_DelegateCast_ReportsDiagnostic()
    {
        // An explicit delegate cast — `obj.Handler = (Action)(() => …)` — wraps the same self-capturing
        // lambda. The analyzer peels the cast (and the parentheses it requires) to reach the lambda, so
        // the cycle is still flagged rather than silently missed.
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        var obj = new FooProxy();
        obj.Handler = (Action)({|SB1002:() => obj.DoWork()|});
    }
}
";

        var test = new AnalyzerTest { TestCode = testCode };
        await test.RunAsync();
    }

    [Fact]
    public async Task SelfCapturingStoredProperty_Parenthesized_ReportsDiagnostic()
    {
        // Redundant parentheses around the lambda must not hide the cycle.
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        var obj = new FooProxy();
        obj.Handler = ({|SB1002:() => obj.DoWork()|});
    }
}
";

        var test = new AnalyzerTest { TestCode = testCode };
        await test.RunAsync();
    }

    [Fact]
    public async Task SelfCapturingCallback_DelegateCastArgument_ReportsDiagnostic()
    {
        // The same unwrap applies on the method-argument path: `SetCallback((Action)(() => …))`.
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        var obj = new FooProxy();
        obj.SetCallback((Action)({|SB1002:() => obj.DoWork()|}));
    }
}
";

        var test = new AnalyzerTest { TestCode = testCode };
        await test.RunAsync();
    }

    [Fact]
    public async Task StoredPropertyWeakBroken_NoDiagnostic()
    {
        // Reaching the object through a WeakSwiftReference in the assigned callback breaks the C# leg.
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        var obj = new FooProxy();
        var weak = new Swift.Runtime.WeakSwiftReference<FooProxy>(obj);
        obj.Handler = () => weak.Target.DoWork();
    }
}
";

        var test = new AnalyzerTest { TestCode = testCode };
        await test.RunAsync();
    }

    [Fact]
    public async Task StoredPropertyCapturesDifferentObject_NoDiagnostic()
    {
        // Assigning a callback that captures a *different* Swift object is not a self-referential cycle.
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        var a = new FooProxy();
        var b = new FooProxy();
        a.Handler = () => b.DoWork();
    }
}
";

        var test = new AnalyzerTest { TestCode = testCode };
        await test.RunAsync();
    }

    [Fact]
    public async Task WeakReferenceBroken_NoDiagnostic()
    {
        // Reaching the object through a WeakSwiftReference breaks the C# leg of the cycle: the lambda
        // captures `weak`, not `obj`, so the analyzer must stay silent.
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        var obj = new FooProxy();
        var weak = new Swift.Runtime.WeakSwiftReference<FooProxy>(obj);
        obj.SetCallback(() => weak.Target.DoWork());
    }
}
";

        var test = new AnalyzerTest { TestCode = testCode };
        await test.RunAsync();
    }

    [Fact]
    public async Task StaticLambda_NoDiagnostic()
    {
        // A `static` lambda cannot capture, so it cannot form the cycle.
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        var obj = new FooProxy();
        obj.SetCallback(static () => { });
    }
}
";

        var test = new AnalyzerTest { TestCode = testCode };
        await test.RunAsync();
    }

    [Fact]
    public async Task NonSwiftObjectReceiver_NoDiagnostic()
    {
        // The receiver is a plain class, not an ISwiftObject — no cross-heap cycle is possible.
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        var obj = new RegularClass();
        obj.SetCallback(() => obj.DoWork());
    }
}
";

        var test = new AnalyzerTest { TestCode = testCode };
        await test.RunAsync();
    }

    [Fact]
    public async Task CallbackCapturesDifferentObject_NoDiagnostic()
    {
        // The callback captures a *different* Swift object than the one it is attached to, so there
        // is no self-referential cycle on the receiver.
        var testCode = MockTypes + @"
public class TestClass
{
    public void Method()
    {
        var a = new FooProxy();
        var b = new FooProxy();
        a.SetCallback(() => b.DoWork());
    }
}
";

        var test = new AnalyzerTest { TestCode = testCode };
        await test.RunAsync();
    }
}
