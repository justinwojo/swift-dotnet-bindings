// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// A plan for marshalling a single parameter or return value between C# and Swift.
/// Contains the setup code, the P/Invoke expression, and any cleanup needed.
/// </summary>
public record MarshalPlan
{
    /// <summary>Statements to execute before the P/Invoke call (allocations, conversions).</summary>
    public List<MarshalStatement> SetupStatements { get; init; } = new();

    /// <summary>The expression to pass to (or receive from) the P/Invoke call.</summary>
    public string PInvokeExpression { get; init; } = "";

    /// <summary>Statements to execute after the P/Invoke call (disposal, conversion).</summary>
    public List<MarshalStatement> CleanupStatements { get; init; } = new();

    /// <summary>Using declarations that wrap the P/Invoke call for disposal.</summary>
    public List<(string Type, string Name)> UsingDeclarations { get; init; } = new();

    /// <summary>
    /// A statement that hands this parameter's value to the callee at +1, emitted after
    /// <see cref="SetupStatements"/> and only where the callee's Swift lowering takes the argument
    /// <c>@owned</c> — an initializer's value parameters and every parameter of a setter, reached
    /// without an intervening Swift-source frame. Null on plans whose value carries no reference to
    /// transfer, and ignored on every borrowing call, so a plan can declare it unconditionally.
    /// </summary>
    public string? OwnedHandOverStatement { get; init; }

    /// <summary>Whether the plan requires an unsafe context.</summary>
    public bool RequiresUnsafe { get; init; }

    /// <summary>Whether the plan requires a fixed statement.</summary>
    public bool RequiresFixed { get; init; }

    /// <summary>Creates a simple pass-through plan with no setup or cleanup.</summary>
    public static MarshalPlan PassThrough(string expression) => new() { PInvokeExpression = expression };
}

/// <summary>
/// A single statement in a marshal plan. Can be a line of code, a block (if/else, try/finally),
/// or a using declaration.
/// </summary>
public abstract record MarshalStatement
{
    private MarshalStatement() { }

    /// <summary>A single line of code.</summary>
    public sealed record Line(string Code) : MarshalStatement;

    /// <summary>A block with a header and body (e.g., if/else, try/finally).</summary>
    public sealed record Block(string Header, List<MarshalStatement> Body) : MarshalStatement;

    /// <summary>A using declaration that ensures disposal.</summary>
    public sealed record Using(string Type, string Name, string InitExpression) : MarshalStatement;
}

/// <summary>
/// A callback method + optional static field that must be emitted as a sibling to the main method.
/// Used by closure and async projections.
/// </summary>
public record CallbackDeclaration(
    string MethodName,
    string CallingConvention,
    string Signature,
    string ReturnType,
    List<MarshalStatement> Body,
    string? StaticFieldDeclaration
);
