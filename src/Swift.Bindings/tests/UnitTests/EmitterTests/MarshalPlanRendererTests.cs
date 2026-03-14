// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for MarshalPlanRenderer — verifies correct rendering of MarshalStatement trees.
/// </summary>
public class MarshalPlanRendererTests
{
    #region RenderReturnPlan

    [Fact]
    public void RenderReturnPlan_SimpleExpression_EmitsReturn()
    {
        var plan = new MarshalPlan
        {
            PInvokeExpression = "(MyEnum)result"
        };

        var output = Render(plan);

        Assert.Contains("return (MyEnum)result;", output);
    }

    [Fact]
    public void RenderReturnPlan_EmptyExpression_SkipsReturn()
    {
        var plan = new MarshalPlan
        {
            SetupStatements = new List<MarshalStatement>
            {
                new MarshalStatement.Line("return someValue;")
            }
        };

        var output = Render(plan);

        Assert.Contains("return someValue;", output);
        // Should NOT have a second "return ;" line
        Assert.DoesNotContain("return ;", output);
    }

    [Fact]
    public void RenderReturnPlan_WithSetupAndCleanup_RendersAll()
    {
        var plan = new MarshalPlan
        {
            SetupStatements = new List<MarshalStatement>
            {
                new MarshalStatement.Line("var x = 42;")
            },
            PInvokeExpression = "x + 1",
            CleanupStatements = new List<MarshalStatement>
            {
                new MarshalStatement.Line("Dispose(x);")
            }
        };

        var output = Render(plan);

        Assert.Contains("var x = 42;", output);
        Assert.Contains("return x + 1;", output);
        Assert.Contains("Dispose(x);", output);
    }

    #endregion

    #region RenderStatement

    [Fact]
    public void RenderStatement_Line_EmitsCode()
    {
        var statement = new MarshalStatement.Line("var result = 42;");

        var output = RenderSingle(statement);

        Assert.Contains("var result = 42;", output);
    }

    [Fact]
    public void RenderStatement_Block_EmitsHeaderAndBody()
    {
        var statement = new MarshalStatement.Block("try", new List<MarshalStatement>
        {
            new MarshalStatement.Line("DoSomething();"),
            new MarshalStatement.Line("return value;")
        });

        var output = RenderSingle(statement);

        Assert.Contains("try", output);
        Assert.Contains("{", output);
        Assert.Contains("DoSomething();", output);
        Assert.Contains("return value;", output);
        Assert.Contains("}", output);
    }

    [Fact]
    public void RenderStatement_NestedBlocks_RendersCorrectly()
    {
        var statement = new MarshalStatement.Block("try", new List<MarshalStatement>
        {
            new MarshalStatement.Line("var x = Alloc();"),
            new MarshalStatement.Block("if (x != null)", new List<MarshalStatement>
            {
                new MarshalStatement.Line("Process(x);")
            })
        });

        var output = RenderSingle(statement);

        Assert.Contains("try", output);
        Assert.Contains("var x = Alloc();", output);
        Assert.Contains("if (x != null)", output);
        Assert.Contains("Process(x);", output);
    }

    [Fact]
    public void RenderStatement_Using_EmitsUsingDeclaration()
    {
        var statement = new MarshalStatement.Using("SwiftString", "str", "new SwiftString(value)");

        var output = RenderSingle(statement);

        Assert.Contains("using var str = new SwiftString(value);", output);
    }

    #endregion

    #region ClassProjection Integration

    [Fact]
    public void RenderReturnPlan_ClassProjection_EmitsDirectMarshalFromSwift()
    {
        var projection = new ClassProjection("MyApp.ViewController");
        var plan = projection.GetReturnPlan("result", ReturnStrategy.Direct);

        var output = Render(plan);

        // ARC bridge: direct MarshalFromSwift, no buffer allocation
        Assert.Contains("MarshalFromSwift<MyApp.ViewController>", output);
        Assert.DoesNotContain("NativeMemory", output);
        Assert.DoesNotContain("try", output);
        Assert.DoesNotContain("catch", output);
    }

    #endregion

    #region Helpers

    private static string Render(MarshalPlan plan)
    {
        using var sw = new StringWriter();
        var writer = new CSharpWriter(sw);
        MarshalPlanRenderer.RenderReturnPlan(writer, plan);
        writer.Flush();
        return sw.ToString();
    }

    private static string RenderSingle(MarshalStatement statement)
    {
        using var sw = new StringWriter();
        var writer = new CSharpWriter(sw);
        MarshalPlanRenderer.RenderStatement(writer, statement);
        writer.Flush();
        return sw.ToString();
    }

    #endregion
}
