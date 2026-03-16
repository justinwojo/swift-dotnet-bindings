// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests for Scope (class with static singleton properties), TreeNode (self-referencing
/// class with optional parent), and Dog (class inheritance with base property access).
/// </summary>
public class ClassSingletonTests : TestBase
{
    public ClassSingletonTests(TestResults results) : base(results) { }

    #region Tier 2 — Scope Singletons

    [TestTier(TestTier.Tier2)]
    public void TestScopeTransientAccess()
    {
        var scope = Scope.Transient;
        AssertNotNull(scope, "Scope.Transient not null");
        AssertEqual("transient", scope.Name, "Transient scope name");
        TestLogger.Info($"Scope.Transient.Name = \"{scope.Name}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestScopeGraphAccess()
    {
        var scope = Scope.Graph;
        AssertNotNull(scope, "Scope.Graph not null");
        AssertEqual("graph", scope.Name, "Graph scope name");
        TestLogger.Info($"Scope.Graph.Name = \"{scope.Name}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestScopeContainerAccess()
    {
        var scope = Scope.Container;
        AssertNotNull(scope, "Scope.Container not null");
        AssertEqual("container", scope.Name, "Container scope name");
        TestLogger.Info($"Scope.Container.Name = \"{scope.Name}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestScopeWeakAccess()
    {
        var scope = Scope.Weak;
        AssertNotNull(scope, "Scope.Weak not null");
        AssertEqual("weak", scope.Name, "Weak scope name");
        TestLogger.Info($"Scope.Weak.Name = \"{scope.Name}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestScopeDistinctInstances()
    {
        var names = new HashSet<string>
        {
            Scope.Transient.Name,
            Scope.Graph.Name,
            Scope.Container.Name,
            Scope.Weak.Name,
        };
        AssertEqual(4, names.Count, "All 4 scope singletons have distinct names");
        TestLogger.Info("All Scope singletons have distinct names");
    }

    [TestTier(TestTier.Tier2)]
    public void TestScopeGetDescribe()
    {
        var desc = Scope.Transient.GetDescribe();
        AssertEqual("Scope: transient", desc, "Transient describe matches");
        TestLogger.Info($"Scope.Transient.GetDescribe() = \"{desc}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestScopeSingletonIdentity()
    {
        // Accessing the same static property twice should return the same Swift object
        var a = Scope.Transient;
        var b = Scope.Transient;
        AssertEqual(a.Name, b.Name, "Same singleton returns same Name");
        TestLogger.Info("Scope singleton identity verified");
    }

    #endregion

    #region Tier 3 — TreeNode (Mono JIT crash on class with string ctor)

    // Mono JIT crash: TreeNode constructor takes SwiftString through CallConvSwift
    [TestTier(TestTier.Tier3)]
    public void TestTreeNodeRootNode()
    {
        var root = new TreeNode("root", null);
        AssertEqual("root", root.Label, "Root label");
        AssertNull(root.Parent, "Root parent is null");
        AssertEqual(0, root.GetDepth(), "Root depth is 0");
        TestLogger.Info($"TreeNode root: Label={root.Label}, Depth={root.GetDepth()}");
    }

    // Mono JIT crash: TreeNode constructor takes SwiftString through CallConvSwift
    [TestTier(TestTier.Tier3)]
    public void TestTreeNodeRootLabel()
    {
        var root = new TreeNode("top", null);
        AssertEqual("top", root.GetRootLabel(), "Root node GetRootLabel returns own label");
        TestLogger.Info($"TreeNode root GetRootLabel() = \"{root.GetRootLabel()}\"");
    }

    // Mono JIT crash: TreeNode constructor takes SwiftString + optional parent through CallConvSwift
    [TestTier(TestTier.Tier3)]
    public void TestTreeNodeChildNode()
    {
        var root = new TreeNode("parent-node", null);
        var child = new TreeNode("child-node", root);
        AssertEqual("child-node", child.Label, "Child label");
        AssertNotNull(child.Parent, "Child parent is not null");
        AssertEqual(1, child.GetDepth(), "Child depth is 1");
        TestLogger.Info($"TreeNode child: Label={child.Label}, Depth={child.GetDepth()}");
    }

    // Mono JIT crash: TreeNode constructor takes SwiftString through CallConvSwift
    [TestTier(TestTier.Tier3)]
    public void TestTreeNodeChildGetRootLabel()
    {
        var root = new TreeNode("root-label", null);
        var child = new TreeNode("child-label", root);
        AssertEqual("root-label", child.GetRootLabel(), "Child GetRootLabel returns parent's label");
        TestLogger.Info($"TreeNode child GetRootLabel() = \"{child.GetRootLabel()}\"");
    }

    // Mono JIT crash: TreeNode chain construction with SwiftString through CallConvSwift
    [TestTier(TestTier.Tier3)]
    public void TestTreeNodeDeepChain()
    {
        var root = new TreeNode("level0", null);
        var mid = new TreeNode("level1", root);
        var leaf = new TreeNode("level2", mid);

        AssertEqual(2, leaf.GetDepth(), "Leaf depth is 2");
        AssertEqual("level0", leaf.GetRootLabel(), "Leaf GetRootLabel returns root's label");
        TestLogger.Info($"TreeNode deep chain: Depth={leaf.GetDepth()}, RootLabel={leaf.GetRootLabel()}");
    }

    #endregion

    #region Tier 3 — Dog (Mono JIT crash on class with string ctor)

    // Mono JIT crash: Dog constructor takes SwiftString params through CallConvSwift
    [TestTier(TestTier.Tier3)]
    public void TestDogNameProperty()
    {
        var dog = new Dog("Rex", "Labrador");
        AssertEqual("Rex", dog.Name, "Dog.Name (inherited from Animal)");
        TestLogger.Info($"Dog.Name = \"{dog.Name}\"");
    }

    // Mono JIT crash: Dog constructor takes SwiftString params through CallConvSwift
    [TestTier(TestTier.Tier3)]
    public void TestDogSoundProperty()
    {
        var dog = new Dog("Rex", "Lab");
        // Dog's init sets sound to "Woof" via super.init
        AssertEqual("Woof", dog.Sound, "Dog.Sound (inherited from Animal) is 'Woof'");
        TestLogger.Info($"Dog.Sound = \"{dog.Sound}\"");
    }

    // Mono JIT crash: Dog constructor takes SwiftString params through CallConvSwift
    [TestTier(TestTier.Tier3)]
    public void TestDogBreedProperty()
    {
        var dog = new Dog("Buddy", "Golden Retriever");
        AssertEqual("Golden Retriever", dog.Breed, "Dog.Breed property");
        TestLogger.Info($"Dog.Breed = \"{dog.Breed}\"");
    }

    // Mono JIT crash: Dog constructor takes SwiftString params through CallConvSwift
    [TestTier(TestTier.Tier3)]
    public void TestDogGetDescribe()
    {
        var dog = new Dog("Rex", "Lab");
        var desc = dog.GetDescribe();
        // Dog.describe() returns "Dog: Rex (Lab)"
        AssertTrue(desc.Contains("Rex"), "Dog.GetDescribe contains name");
        AssertTrue(desc.Contains("Lab"), "Dog.GetDescribe contains breed");
        TestLogger.Info($"Dog.GetDescribe() = \"{desc}\"");
    }

    #endregion
}
