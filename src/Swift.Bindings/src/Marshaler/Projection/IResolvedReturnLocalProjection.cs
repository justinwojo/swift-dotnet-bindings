// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Implemented by the return projections that declare a body local of their own with a FIXED
/// spelling instead of deriving it from the name they are handed. Every other projection builds
/// its scratch names off <c>resultName</c>, so they move out of the way on their own; these do
/// not, and a member whose parameter is spelled the same way would shadow the local (the
/// generated C# then fails to compile).
/// <para>
/// The emitter asks for <see cref="DefaultReturnLocalName"/>, resolves it against the member's
/// parameter names, and hands the resolved name back through
/// <see cref="GetReturnPlan(string, ReturnStrategy, string)"/>. The two-argument
/// <c>ITypeProjection.GetReturnPlan</c> stays as the default-name entry point.
/// </para>
/// </summary>
public interface IResolvedReturnLocalProjection
{
    /// <summary>The spelling this projection uses when no resolved name is supplied.</summary>
    string DefaultReturnLocalName { get; }

    /// <summary>Builds the return plan, declaring the body local as <paramref name="localName"/>.</summary>
    MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy, string localName);
}
