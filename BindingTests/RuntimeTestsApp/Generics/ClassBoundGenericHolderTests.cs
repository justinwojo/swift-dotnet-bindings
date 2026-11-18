// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Runtime gate for <c>ClassBoundGenericHolders.swift</c>: generic classes whose parameter is
/// class-bound, through an <c>AnyObject</c> protocol or through Combine's <c>ObservableObject</c>,
/// and whose other parameter has an associated type, so every member is a direct Swift-convention
/// import with <c>self</c> in the self register. Swift passes and returns a class-bound generic
/// value, <c>V?</c> included, as one object reference. The imports once passed the address of a
/// buffer and read the result through the indirect-result register, so each call here checks the
/// object Swift saw or returned by its pointer and its revision, and Swift reads the holder's state
/// back after the call so a reference count the import got wrong shows up as a wrong or dead object.
/// </summary>
public class ClassBoundGenericHolderTests : TestBase
{
    public ClassBoundGenericHolderTests(TestResults results) : base(results) { }

    private const SwiftTransitionFacts MemberShapes = SwiftTransitionFacts.SelfRegister | SwiftTransitionFacts.LoweredSignature;

    /// <summary>
    /// Runs <paramref name="call"/>, which must work wherever a bridge carries the member or the
    /// engine runs its direct transition, and must refuse before Swift runs everywhere else.
    /// </summary>
    private void Run(string member, Action call)
    {
        if (ImportRoutes.BridgesRouted || SwiftTransitionGuard.IsSupported(MemberShapes))
        {
            call();
            return;
        }
        SwiftTransitionUnsupportedException? refusal = null;
        try
        {
            call();
        }
        catch (SwiftTransitionUnsupportedException ex)
        {
            refusal = ex;
        }
        AssertNotNull(refusal, $"{member} must refuse on an engine that mishandles its direct transition");
        AssertTrue(refusal!.Member.Contains(member, StringComparison.Ordinal), $"refusal names the member: {refusal.Member}");
    }

    private static IntPtr Pointer(HolderRevision value) => value.Payload.DangerousGetHandle();

    public void TestInitAndModelGetterPassTheObject()
    {
        using var model = new HolderRevision(5);
        Run("ClassBoundModelHolder", () =>
        {
            using var holder = new ClassBoundModelHolder<HolderRevision, HolderAlert>(model);
            for (int i = 0; i < 3; i++)
            {
                using var read = holder.Model;
                AssertEqual(Pointer(model), Pointer(read), $"model returns the object init received (read {i})");
                AssertEqual(5, read.Revision, $"model's revision (read {i})");
            }
            AssertEqual((nint)5, TestLibFunctions.ClassBoundHolderCurrentRevision(holder), "Swift reads the object init stored");
        });
        AssertEqual(5, model.Revision, "the model outlives the holder");
    }

    public void TestCurrentSetterAndGetterRoundTrip()
    {
        using var first = new HolderRevision(1);
        using var second = new HolderRevision(2);
        Run("ClassBoundModelHolder", () =>
        {
            using var holder = new ClassBoundModelHolder<HolderRevision, HolderAlert>(first);
            holder.Current = second;
            AssertEqual((nint)2, TestLibFunctions.ClassBoundHolderCurrentRevision(holder), "Swift sees the object the setter passed");
            using (var current = holder.Current)
                AssertEqual(Pointer(second), Pointer(current), "current returns the object the setter stored");
            second.Revision = 20;
            AssertEqual((nint)20, TestLibFunctions.ClassBoundHolderCurrentRevision(holder), "the setter stored a reference, not a copy");
            holder.Current = first;
            AssertEqual((nint)1, TestLibFunctions.ClassBoundHolderCurrentRevision(holder), "a second set replaces the first");
        });
        AssertEqual(20, second.Revision, "a replaced value stays alive for its C# owner");
    }

    public void TestOptionalArgumentAndResultAreOneReference()
    {
        using var first = new HolderRevision(3);
        using var other = new HolderRevision(4);
        Run("OptionalExchange", () =>
        {
            using var holder = new ClassBoundModelHolder<HolderRevision, HolderAlert>(first);
            for (int i = 0; i < 3; i++)
            {
                using var returned = holder.OptionalExchange(other);
                AssertNotNull(returned, $"optionalExchange returns a present argument (call {i})");
                AssertEqual(Pointer(other), Pointer(returned!), $"optionalExchange returns the object passed (call {i})");
            }
            AssertEqual((nint)4, TestLibFunctions.ClassBoundHolderCurrentRevision(holder), "Swift made the present argument current");
            AssertNull(holder.OptionalExchange(null), "optionalExchange(nil) returns nil");
            AssertEqual((nint)4, TestLibFunctions.ClassBoundHolderCurrentRevision(holder), "nil leaves current alone");
        });
    }

    public void TestOptionalResultOfAMethodWithAScalarArgument()
    {
        using var model = new HolderRevision(7);
        Run("CurrentMethod", () =>
        {
            using var holder = new ClassBoundModelHolder<HolderRevision, HolderAlert>(model);
            for (int i = 0; i < 3; i++)
            {
                using var present = holder.CurrentMethod(7);
                AssertNotNull(present, $"current(atLeast: 7) is present (call {i})");
                AssertEqual(Pointer(model), Pointer(present!), $"current(atLeast:) returns the current object (call {i})");
            }
            AssertNull(holder.CurrentMethod(8), "current(atLeast: 8) is nil");
        });
    }

    public void TestInOutValueStaysAnAddress()
    {
        using var first = new HolderRevision(10);
        using var incoming = new HolderRevision(11);
        Run("SwapIn", () =>
        {
            using var holder = new ClassBoundModelHolder<HolderRevision, HolderAlert>(first);
            var value = incoming;
            holder.SwapIn(ref value);
            AssertEqual(Pointer(first), Pointer(value), "swapIn hands back the previous current object");
            AssertEqual((nint)11, TestLibFunctions.ClassBoundHolderCurrentRevision(holder), "Swift made the incoming object current");
        });
        AssertEqual(10, first.Revision, "the swapped-out object stays alive");
        AssertEqual(11, incoming.Revision, "the swapped-in object stays alive");
    }

    public void TestObservableObjectBoundParameterIsOneReference()
    {
        using var first = new HolderRevision(21);
        using var second = new HolderRevision(22);
        Run("ObservedModelHolder", () =>
        {
            using var holder = new ObservedModelHolder<HolderRevision, HolderAlert>(first);
            using (var model = holder.Model)
                AssertEqual(Pointer(first), Pointer(model), "model returns the object init received");
            holder.Current = second;
            using (var current = holder.Current)
            {
                AssertEqual(Pointer(second), Pointer(current), "current returns the object the setter stored");
                AssertEqual(22, current.Revision, "and its revision");
            }
        });
        AssertEqual(21, first.Revision, "the model outlives the holder");
        AssertEqual(22, second.Revision, "the current value outlives the holder");
    }
}
