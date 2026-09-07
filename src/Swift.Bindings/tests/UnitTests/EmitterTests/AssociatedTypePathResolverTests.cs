// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

public class AssociatedTypePathResolverTests
{
    [Theory]
    [InlineData("Swift.Int32", "Swift.String")]
    [InlineData("Swift.String", "Swift.Int32")]
    public void CompletePath_UsesChildOwnerInsteadOfRootLeaf(string childElement, string rootElement)
    {
        var scope = new AssociatedTypePathResolver();
        scope.AddFacts("Other.Child", new Dictionary<string, string> { ["Element"] = childElement });
        scope.AddFacts("Main.Child", new Dictionary<string, string> { ["Element"] = rootElement });
        var root = new ConcreteSpecializationEngine.ConcreteConformer("Main.Root", "Root",
            AssociatedTypes: new Dictionary<string, string> { ["Child"] = "Other.Child", ["Element"] = rootElement })
            { AssociatedTypeScope = scope };
        Assert.Equal(new AssociatedTypePathResolver.Resolution(
            AssociatedTypePathResolver.ResolutionKind.Resolved, childElement),
            AssociatedTypePathResolver.Resolve(root, "Child.Element"));
    }

    [Fact]
    public void MissingChild_IsUnknownEvenWhenRootLeafExists()
    {
        var root = new ConcreteSpecializationEngine.ConcreteConformer("Main.Root", "Root",
            AssociatedTypes: new Dictionary<string, string> { ["Element"] = "Swift.Int32" });
        var result = AssociatedTypePathResolver.Resolve(root, "Child.Element");
        Assert.Equal(AssociatedTypePathResolver.ResolutionKind.Unknown, result.Kind);
        Assert.True(AssociatedTypePathResolver.DeferToCompiler(result, "Child.Element"));
    }

    [Fact]
    public void ConflictingChildWitnesses_AreAmbiguousRegardlessOfOrder()
    {
        foreach (var reverse in new[] { false, true })
        {
            var scope = new AssociatedTypePathResolver();
            foreach (var name in reverse ? new[] { "Swift.String", "Swift.Int32" } : new[] { "Swift.Int32", "Swift.String" })
                scope.AddFacts("Main.Child", new Dictionary<string, string> { ["Element"] = name });
            var root = new ConcreteSpecializationEngine.ConcreteConformer("Main.Root", "Root",
                AssociatedTypes: new Dictionary<string, string> { ["Child"] = "Main.Child" })
                { AssociatedTypeScope = scope };
            Assert.Equal(AssociatedTypePathResolver.ResolutionKind.Ambiguous,
                AssociatedTypePathResolver.Resolve(root, "Child.Element").Kind);
        }
    }

    [Fact]
    public void FullPathHint_DoesNotAuthorizeAnotherPathWithSameLeaf()
    {
        var conformer = ConcreteSpecializationEngine.GetHintConformers("Swift.Collection")
            .Single(c => c.SwiftQualifiedName == "Swift.Array<Swift.String>");
        var result = AssociatedTypePathResolver.Resolve(conformer, "SubSequence.Element");
        Assert.Equal(AssociatedTypePathResolver.ResolutionKind.Resolved, result.Kind);
        Assert.Equal("Swift.String", result.TypeName);
        Assert.Equal(AssociatedTypePathResolver.ResolutionKind.Unknown,
            AssociatedTypePathResolver.Resolve(conformer, "Child.Element").Kind);
    }

    [Fact]
    public void CyclicWitnesses_TerminateAfterTheRequestedPath()
    {
        var facts = new Dictionary<string, string> { ["Child"] = "Main.Root", ["Element"] = "Swift.Int32" };
        var scope = new AssociatedTypePathResolver();
        scope.AddFacts("Main.Root", facts);
        var root = new ConcreteSpecializationEngine.ConcreteConformer("Main.Root", "Root", AssociatedTypes: facts)
            { AssociatedTypeScope = scope };
        Assert.Equal("Swift.Int32", AssociatedTypePathResolver.Resolve(root, "Child.Child.Element").TypeName);
    }
}
