// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Renders MarshalPlan statement trees to CSharpWriter output.
/// Pure "statement tree → text" — no type resolution or marshalling logic.
/// </summary>
internal static class MarshalPlanRenderer
{
    /// <summary>
    /// Renders a complete return plan. Emits setup statements, optionally emits
    /// <c>return {PInvokeExpression};</c> (when non-empty), then cleanup statements.
    /// Projections that embed the return inside setup (e.g., ClassProjection try/catch)
    /// leave PInvokeExpression empty and the renderer skips the return line.
    /// </summary>
    public static void RenderReturnPlan(CSharpWriter writer, MarshalPlan plan)
    {
        RenderStatements(writer, plan.SetupStatements);

        if (!string.IsNullOrEmpty(plan.PInvokeExpression))
            writer.WriteLine($"return {plan.PInvokeExpression};");

        RenderStatements(writer, plan.CleanupStatements);
    }

    /// <summary>
    /// Renders a list of MarshalStatements to the writer.
    /// </summary>
    public static void RenderStatements(CSharpWriter writer, IReadOnlyList<MarshalStatement> statements)
    {
        foreach (var statement in statements)
            RenderStatement(writer, statement);
    }

    /// <summary>
    /// Renders a single MarshalStatement, dispatching by type.
    /// </summary>
    public static void RenderStatement(CSharpWriter writer, MarshalStatement statement)
    {
        switch (statement)
        {
            case MarshalStatement.Line line:
                writer.WriteLine(line.Code);
                break;

            case MarshalStatement.Block block:
                writer.WriteLine(block.Header);
                writer.WriteLine("{");
                writer.Indent++;
                RenderStatements(writer, block.Body);
                writer.Indent--;
                writer.WriteLine("}");
                break;

            case MarshalStatement.Using use:
                writer.WriteLine($"using var {use.Name} = {use.InitExpression};");
                break;
        }
    }
}
