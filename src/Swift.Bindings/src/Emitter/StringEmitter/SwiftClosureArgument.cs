// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Renders the argument a generated Swift call passes for a closure-typed parameter of the API it
/// wraps, from the closure the generated code holds for it: a bound adapter, a closure literal, a
/// wrapper parameter of function type, or a stored closure.
/// </summary>
/// <remarks>
/// The generated code holds that closure as a function value, but an <c>@autoclosure</c> parameter
/// takes an expression of its result type. Passing the function itself either fails to compile or,
/// when a same-named sibling takes a function whose type the closure converts to, quietly calls
/// that sibling instead. So the argument for an autoclosure parameter is the closure's call,
/// <c>closure()</c>, which Swift wraps back into an autoclosure: the callee still evaluates it only
/// if and when it asks. The call carries the autoclosure's own effects, <c>try</c> and
/// <c>await</c>, because Swift checks them where the expression is written; a call that is itself
/// marked <c>try</c> or <c>await</c> accepts the inner marks as well.
/// </remarks>
internal static class SwiftClosureArgument
{
    /// <summary>
    /// The argument for <paramref name="parameterType"/> given <paramref name="closure"/>, an
    /// expression of function type.
    /// </summary>
    /// <param name="closure">The closure: an adapter's name, a literal, or a wrapper parameter.</param>
    /// <param name="parameterType">The callee's declared type for the parameter.</param>
    /// <param name="calleeTakesAFunction">
    /// The callee is a generated shim that redeclares the autoclosure parameter as a plain
    /// function and evaluates it itself, so it takes the closure unchanged.
    /// </param>
    public static string Render(string closure, TypeSpec? parameterType, bool calleeTakesAFunction = false)
    {
        var (before, after) = Invocation(parameterType, calleeTakesAFunction);
        return before + closure + after;
    }

    /// <summary>
    /// The text <see cref="Render"/> writes before and after the closure, for a closure literal
    /// written across several lines: <c>before</c> precedes its opening brace and <c>after</c>
    /// follows its closing one. Both are empty unless the parameter is an autoclosure.
    /// </summary>
    public static (string Before, string After) Invocation(TypeSpec? parameterType, bool calleeTakesAFunction = false)
    {
        if (calleeTakesAFunction || parameterType is not ClosureTypeSpec { IsAutoClosure: true } autoclosure)
            return ("", "");
        var effects = (autoclosure.Throws ? "try " : "") + (autoclosure.IsAsync ? "await " : "");
        return (effects, "()");
    }
}
