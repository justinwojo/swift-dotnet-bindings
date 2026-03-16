// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// Tests for SwiftDisposeScope — automatic batch disposal of Swift objects.
/// These tests verify that scope-based disposal works correctly with real
/// Swift objects created through the generated bindings.
/// </summary>
public class DisposeScopeTests : TestBase
{
    public DisposeScopeTests(TestResults results) : base(results) { }

    #region Basic Scope Disposal

    public void TestScopeDisposesAnimalOnExit()
    {
        Animal animal;

        using (new SwiftDisposeScope())
        {
            animal = TestLibFunctions.CreateAnimal("Scoped", "Meow");
            var name = animal.Name.ToString();
            AssertEqual("Scoped", name, "Animal accessible inside scope");
        }

        // After scope exit, the animal should be disposed
        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = animal.Name;
        }, "Animal should be disposed after scope exit");

        TestLogger.Info("Scope correctly disposed Animal on exit");
    }

    // Depends on createUniqueResource whose wrapper was stripped during compilation
    [Skip("createUniqueResource wrapper stripped")]
    public void TestScopeDisposesMultipleObjects()
    {
        Animal animal;
        UniqueResource resource;

        using (new SwiftDisposeScope())
        {
            animal = TestLibFunctions.CreateAnimal("Multi1", "Woof");
            resource = TestLibFunctions.CreateUniqueResource(42);

            AssertEqual("Multi1", animal.Name.ToString(), "Animal accessible");
            AssertEqual(42, resource.Id, "Resource accessible");
        }

        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = animal.Name;
        }, "Animal disposed after scope exit");

        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = resource.Id;
        }, "Resource disposed after scope exit");

        TestLogger.Info("Scope correctly disposed multiple objects");
    }

    #endregion

    #region Nested Scopes

    public void TestNestedScopesDisposeIndependently()
    {
        Animal outerAnimal;
        Animal innerAnimal;

        using (new SwiftDisposeScope())
        {
            outerAnimal = TestLibFunctions.CreateAnimal("Outer", "Moo");

            using (new SwiftDisposeScope())
            {
                innerAnimal = TestLibFunctions.CreateAnimal("Inner", "Baa");
                AssertEqual("Inner", innerAnimal.Name.ToString(), "Inner animal accessible");
            }

            // Inner scope exited — inner animal should be disposed
            AssertThrows<ObjectDisposedException>(() =>
            {
                _ = innerAnimal.Name;
            }, "Inner animal disposed after inner scope exit");

            // Outer animal should still be accessible
            AssertEqual("Outer", outerAnimal.Name.ToString(), "Outer animal still accessible");
        }

        // Outer scope exited — outer animal should be disposed
        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = outerAnimal.Name;
        }, "Outer animal disposed after outer scope exit");

        TestLogger.Info("Nested scopes dispose independently");
    }

    #endregion

    #region Detach and MoveToParent

    public void TestDetachFromScopeSurvivesExit()
    {
        Animal animal;

        using (new SwiftDisposeScope())
        {
            animal = TestLibFunctions.CreateAnimal("Detached", "Chirp");
            animal.DetachFromScope();
        }

        // Animal was detached — should NOT be disposed
        var name = animal.Name.ToString();
        AssertEqual("Detached", name, "Detached animal survives scope exit");

        // Clean up manually
        animal.Dispose();

        TestLogger.Info("Detach correctly prevents disposal on scope exit");
    }

    public void TestMoveToParentScope()
    {
        Animal animal;

        using (new SwiftDisposeScope())
        {
            using (new SwiftDisposeScope())
            {
                animal = TestLibFunctions.CreateAnimal("Moved", "Quack");
                animal.MoveToParentScope();
            }

            // Inner scope exited — animal was moved to outer, so still accessible
            AssertEqual("Moved", animal.Name.ToString(), "Moved animal survives inner scope");
        }

        // Outer scope exited — animal should now be disposed
        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = animal.Name;
        }, "Moved animal disposed when outer scope exits");

        TestLogger.Info("MoveToParent correctly transfers to parent scope");
    }

    #endregion

    #region Scope With Exception

    public void TestScopeDisposesOnException()
    {
        Animal animal = null!;

        try
        {
            using (new SwiftDisposeScope())
            {
                animal = TestLibFunctions.CreateAnimal("Exception", "Oops");
                AssertEqual("Exception", animal.Name.ToString(), "Animal accessible before throw");
                throw new InvalidOperationException("test");
            }
        }
        catch (InvalidOperationException)
        {
            // Expected
        }

        // Animal should still be disposed despite the exception
        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = animal.Name;
        }, "Animal disposed after scope exits via exception");

        TestLogger.Info("Scope correctly disposes objects when exception occurs");
    }

    #endregion

    #region No Scope Active

    public void TestNoScopeActiveDoesNotCrash()
    {
        // Creating objects without a scope should work normally
        // (no scope to register with — objects must be manually disposed)
        var animal = TestLibFunctions.CreateAnimal("NoScope", "Hiss");
        AssertEqual("NoScope", animal.Name.ToString(), "Animal works without scope");
        animal.Dispose();

        TestLogger.Info("Objects work correctly without an active scope");
    }

    #endregion
}
