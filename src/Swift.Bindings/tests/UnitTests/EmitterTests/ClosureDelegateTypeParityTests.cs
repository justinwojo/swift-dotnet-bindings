// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins the single computation behind a closure's C# delegate type.
///
/// <para>A closure parameter is bridged in two halves. The public method signature declares the type
/// a consumer's lambda binds to and under which the delegate is stored in a GCHandle; the
/// <c>[UnmanagedCallersOnly]</c> trampoline recovers it with
/// <c>SwiftClosureMarshaller.GetDelegateFrom(Boxed)Context&lt;T&gt;</c>. That recovery is an
/// unchecked cast of <c>GCHandle.Target</c>, so when the two halves disagree nothing catches it:
/// the C# compiles, the Swift compiles, and the FIRST callback throws <c>InvalidCastException</c>
/// inside the trampoline, where it becomes a <c>FailFastUnhandledClosureException</c> — a process
/// abort on any invocation.</para>
///
/// <para>They used to be two computations. The public signature came from
/// <see cref="TypeProjectionFactory"/> composing each sub-projection's <c>PublicType</c>; the
/// trampoline read <see cref="ClosureHandler.GetCSharpDelegateType"/>, a separate TypeSpec
/// translator. The two agreed on ordinary shapes and diverged on container-shaped ones — a
/// <c>Result</c> failure arm resolved to the raw existential carrier on one side and to the
/// well-known <c>Swift.Error</c> mapping on the other; an array or dictionary resolved to the
/// idiomatic collection interface on one side and the Swift container carrier on the other.</para>
///
/// <para>The projection now RESOLVES the handler's string instead of re-deriving one, so the two
/// sites are the same string by construction. These tests assert that property over the shapes where
/// the translators actually disagreed, plus an ordinary shape as a control: a future change that
/// reintroduces an independent composition breaks them regardless of which layer it lives in.</para>
/// </summary>
public class ClosureDelegateTypeParityTests
{
    private const string PayloadClass = "TestModule.ParityPayload";
    private const string PointStruct = "TestModule.ParityPoint";

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        RegisterPrimitive(swiftModule, "Swift.Int32", "System", "Int32");
        RegisterPrimitive(swiftModule, "Swift.Double", "System", "Double");
        RegisterPrimitive(swiftModule, "Swift.String", "Swift", "SwiftString");
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName(PayloadClass),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ParityPayload"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(PayloadClass),
                MetadataAccessor = "$s10TestModule13ParityPayloadCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName(PointStruct),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ParityPoint"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(PointStruct),
                MetadataAccessor = "$s10TestModule11ParityPointVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(testModule);

        return typeDatabase;
    }

    private static void RegisterPrimitive(ModuleTypeDatabase module, string swiftName, string ns, string name)
        => module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName(swiftName),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(ns, name),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(swiftName),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });

    /// <summary>
    /// The assertion every case below shares: whatever the projection publishes as the parameter's
    /// C# type must be the exact string the trampoline will cast back to.
    /// </summary>
    private static void AssertPublicTypeMatchesCastTarget(ClosureTypeSpec closure)
    {
        var typeDatabase = CreateTypeDatabase();
        var projection = new TypeProjectionFactory().Project(
            closure,
            new ProjectionContext { TypeDatabase = typeDatabase, IsParameter = true });

        Assert.NotNull(projection);

        var castTarget = new ClosureHandler(typeDatabase).GetCSharpDelegateType(closure);

        // Non-empty is load-bearing: an empty expected string would make the equality vacuous, and a
        // handler that stopped resolving would then "pass" against a projection that also produced
        // nothing.
        Assert.False(string.IsNullOrWhiteSpace(castTarget));
        Assert.Equal(castTarget, projection!.PublicType);
    }

    private static ClosureTypeSpec Closure(TypeSpec? arguments, TypeSpec? returnType)
        => new ClosureTypeSpec(arguments, returnType);

    private static NamedTypeSpec Named(string name, params TypeSpec[] generics)
        => generics.Length == 0 ? new NamedTypeSpec(name) : new NamedTypeSpec(name, generics);

    // ─── The reported divergences ───

    /// <summary>
    /// `(Result&lt;ParityPayload?, any Error&gt;) -> Void`. The failure arm is where the two
    /// translators split: one spelled it as the raw existential carrier, the other as the well-known
    /// `Swift.Error` mapping, so the stored `Action&lt;SwiftResult&lt;…, ExistentialContainer1&gt;&gt;`
    /// was recovered as `Action&lt;SwiftResult&lt;…, AnyError&gt;&gt;`.
    /// </summary>
    [Fact]
    public void ResultOfOptionalClass_PublicTypeMatchesCastTarget()
        => AssertPublicTypeMatchesCastTarget(Closure(
            Named("Swift.Result", Named("Swift.Optional", Named(PayloadClass)), Named("Swift.Error")),
            null));

    /// <summary>
    /// The non-optional success arm of the same shape. Only the failure arm carries the divergence,
    /// so this pins that the fix did not narrow to the Optional-wrapped case.
    /// </summary>
    [Fact]
    public void ResultOfClass_PublicTypeMatchesCastTarget()
        => AssertPublicTypeMatchesCastTarget(Closure(
            Named("Swift.Result", Named(PayloadClass), Named("Swift.Error")),
            null));

    /// <summary>
    /// `([Double]) -> Void`. The array argument resolved to the idiomatic collection interface on the
    /// public side and to the Swift array carrier in the trampoline.
    /// </summary>
    [Fact]
    public void ArrayArgument_PublicTypeMatchesCastTarget()
        => AssertPublicTypeMatchesCastTarget(Closure(
            Named("Swift.Array", Named("Swift.Double")),
            null));

    /// <summary>The struct-element variant of the same argument shape.</summary>
    [Fact]
    public void StructArrayArgument_PublicTypeMatchesCastTarget()
        => AssertPublicTypeMatchesCastTarget(Closure(
            Named("Swift.Array", Named(PointStruct)),
            null));

    /// <summary>
    /// `(Double) -> [Double]`. Return position rather than argument position — a separate arm of the
    /// same divergence, and the one an emitter patch used to re-sync per-case.
    /// </summary>
    [Fact]
    public void ArrayReturn_PublicTypeMatchesCastTarget()
        => AssertPublicTypeMatchesCastTarget(Closure(
            Named("Swift.Double"),
            Named("Swift.Array", Named("Swift.Double"))));

    /// <summary>
    /// `([String: Int32]) -> Void`. The dictionary counterpart, whose divergence was worse than a
    /// mismatch: the public side named `IDictionary`, an interface `SwiftDictionary` does not even
    /// implement.
    /// </summary>
    [Fact]
    public void DictionaryArgument_PublicTypeMatchesCastTarget()
        => AssertPublicTypeMatchesCastTarget(Closure(
            Named("Swift.Dictionary", Named("Swift.String"), Named("Swift.Int32")),
            null));

    // ─── Controls ───

    /// <summary>
    /// A shape the two translators always agreed on. Without it, a change that made BOTH sides
    /// produce the same wrong thing everywhere would still pass the cases above.
    /// </summary>
    [Fact]
    public void BlittableArgumentAndReturn_PublicTypeMatchesCastTarget()
        => AssertPublicTypeMatchesCastTarget(Closure(
            Named("Swift.Int32"),
            Named("Swift.Double")));

    /// <summary>A no-argument, no-return closure — the degenerate `Action`.</summary>
    [Fact]
    public void VoidClosure_PublicTypeMatchesCastTarget()
        => AssertPublicTypeMatchesCastTarget(Closure(null, null));

    // ─── inout closure arguments are declined ───

    /// <summary>
    /// An `inout` parameter in the closure's own signature has no write-back channel: both closure
    /// ABIs marshal by value, and C#'s <c>Action</c>/<c>Func</c> cannot express `ref` at all. Emitting
    /// it produces a member that compiles on both sides and silently discards every mutation the
    /// consumer makes, so the closure is refused and the member becomes a tombstone instead.
    /// </summary>
    [Fact]
    public void InOutClosureArgument_IsNotSupported()
    {
        var handler = new ClosureHandler(CreateTypeDatabase());
        var inoutArg = Named("Swift.Dictionary", Named("Swift.String"), Named("Swift.Int32"));
        inoutArg.IsInOut = true;

        Assert.False(handler.IsSupportedClosure(Closure(inoutArg, null)));
    }

    /// <summary>
    /// A blittable `inout` is refused for the same reason. The mutation-dropping is a property of the
    /// closure boundary, not of the argument being container-shaped.
    /// </summary>
    [Fact]
    public void InOutBlittableClosureArgument_IsNotSupported()
    {
        var handler = new ClosureHandler(CreateTypeDatabase());
        var inoutArg = Named("Swift.Int32");
        inoutArg.IsInOut = true;

        Assert.False(handler.IsSupportedClosure(Closure(inoutArg, Named("Swift.Int32"))));
    }

    /// <summary>
    /// The control for both gates above: the identical closure WITHOUT `inout` is supported, so the
    /// refusal is attributable to the `inout` marker rather than to the argument type or to a
    /// predicate that rejects everything.
    /// </summary>
    [Fact]
    public void ByValueBlittableClosureArgument_IsSupported()
    {
        var handler = new ClosureHandler(CreateTypeDatabase());

        Assert.True(handler.IsSupportedClosure(Closure(Named("Swift.Int32"), Named("Swift.Int32"))));
    }
}
