// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Emission-level coverage for the ABI floor: a member that no Swift wrapper can name — because the
/// member, or the type declaring it, is module-internal — and whose direct CallConvSwift P/Invoke
/// would carry a non-blittable value has no sound call route at all. Such a member is emitted as a
/// throwing tombstone — declaration kept, body replaced — instead of a callable that faults on
/// invocation, on every path that emits a member body (method, constructor, failable-init factory).
/// The predicate itself is covered by AbiSafetyTests; these tests pin what the emitter writes.
/// </summary>
public class AbiFloorTombstoneEmissionTests
{
    [Fact]
    public void ModuleInternalNonBlittableMethod_BodyThrowsInsteadOfCallingSwift()
    {
        var (moduleDecl, typeDb) = CreateEnvironmentWithOpaqueStruct();
        var parentDecl = CreateClassDecl("Reporter", moduleDecl);
        var method = CreateMethodWithParam("record", new NamedTypeSpec("TestModule.Opaque"), "value", parentDecl, moduleDecl);
        method.IsModuleInternal = true;

        var csOutput = EmitMethod(method, typeDb);

        Assert.Contains("throw new NotSupportedException", csOutput);
        // The body must be the throw and nothing else: no residue of the call the rollback replaced.
        Assert.False(InvokesPInvoke(csOutput));
        // The extern itself deliberately stays — it names the library's own exported Swift symbol,
        // so leaving it costs nothing and keeps the tombstone a body-only replacement.
        Assert.True(DeclaresPInvoke(csOutput));
    }

    [Fact]
    public void ModuleInternalNonBlittableMethod_ThrowMessageSelfDescribes()
    {
        var (moduleDecl, typeDb) = CreateEnvironmentWithOpaqueStruct();
        var parentDecl = CreateClassDecl("Reporter", moduleDecl);
        var method = CreateMethodWithParam("record", new NamedTypeSpec("TestModule.Opaque"), "value", parentDecl, moduleDecl);
        method.IsModuleInternal = true;

        var csOutput = EmitMethod(method, typeDb);

        // A consumer who hits this at runtime gets both halves of the cause from the message alone:
        // the Swift declaration's visibility (why no wrapper exists) and the signature problem (why
        // the direct call is unsound). Anything vaguer sends them to the generator source.
        var throwLine = csOutput.Split('\n').Single(l => l.Contains("throw new NotSupportedException"));
        Assert.Contains("internal", throwLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("blittable", throwLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModuleInternalNonBlittableMethod_KeepsDeclarationAndItsOwnMarker()
    {
        var (moduleDecl, typeDb) = CreateEnvironmentWithOpaqueStruct();
        var parentDecl = CreateClassDecl("Reporter", moduleDecl);
        var method = CreateMethodWithParam("record", new NamedTypeSpec("TestModule.Opaque"), "value", parentDecl, moduleDecl);
        method.IsModuleInternal = true;

        var csOutput = EmitMethod(method, typeDb);

        // Keeping the declaration is what separates a tombstone from a skip: a suppressed member that
        // satisfies a protocol requirement breaks its conformance (CS0535), and the pinned public
        // surface would shrink. The marker stays so the member is still flagged at the call site —
        // under the uncallable id, not the advisory one a NativeAOT consumer suppresses.
        Assert.Contains("public void Record", csOutput);
        Assert.Contains(WrapperValidation.UncallableAbiDiagnosticId, csOutput);
        Assert.DoesNotContain(WrapperValidation.DirectCallConvSwiftDiagnosticId, csOutput);
    }

    [Fact]
    public void ModuleInternalNonBlittableMethod_DeclarationMatchesItsCallableTwin()
    {
        // The floor must replace the BODY only. If it also moved the signature, a conforming type
        // would stop satisfying the interface that the callable shape declares.
        var (internalModule, internalDb) = CreateEnvironmentWithOpaqueStruct();
        var internalParent = CreateClassDecl("Reporter", internalModule);
        var internalMethod = CreateMethodWithParam("record", new NamedTypeSpec("TestModule.Opaque"), "value", internalParent, internalModule);
        internalMethod.IsModuleInternal = true;

        var (publicModule, publicDb) = CreateEnvironmentWithOpaqueStruct();
        var publicParent = CreateClassDecl("Reporter", publicModule);
        var publicMethod = CreateMethodWithParam("record", new NamedTypeSpec("TestModule.Opaque"), "value", publicParent, publicModule);

        var tombstoned = DeclarationLine(EmitMethod(internalMethod, internalDb), "public void Record");
        var callable = DeclarationLine(EmitMethod(publicMethod, publicDb), "public void Record");

        Assert.Equal(callable, tombstoned);
    }

    [Fact]
    public void TombstonedMember_NamesItsImportOrphaned_AsItsCallPlanNamesIt()
    {
        // A call-plan contradiction is left alone only on an import nothing calls, matched on the
        // identity the import's own plan records; the callable twin orphans nothing.
        var (moduleDecl, typeDb) = CreateEnvironmentWithOpaqueStruct();
        var parentDecl = CreateClassDecl("Reporter", moduleDecl);
        var method = CreateMethodWithParam("record", new NamedTypeSpec("TestModule.Opaque"), "value", parentDecl, moduleDecl);
        method.IsModuleInternal = true;
        var context = new ModuleEmissionContext();

        EmitMethod(method, typeDb, context);

        var orphan = Assert.Single(context.OrphanedImports);
        Assert.Contains(context.AbiCallPlans, plan =>
            plan.EntryPoint == orphan.EntryPoint && plan.MethodName == orphan.MethodName && plan.Owner == orphan.Owner);

        var (publicModule, publicDb) = CreateEnvironmentWithOpaqueStruct();
        var publicMethod = CreateMethodWithParam("record", new NamedTypeSpec("TestModule.Opaque"), "value", CreateClassDecl("Reporter", publicModule), publicModule);
        var liveContext = new ModuleEmissionContext();

        EmitMethod(publicMethod, publicDb, liveContext);

        Assert.NotEmpty(liveContext.AbiCallPlans);
        Assert.Empty(liveContext.OrphanedImports);
    }

    [Fact]
    public void SwiftConventionClosureCallback_NamesTheImportThatPassesIt_AsItsCallPlanNamesIt()
    {
        // A contradicted callback withdraws the member whose import hands its closure to Swift,
        // matched on the identity that import's own plan records. A module-internal member has no
        // wrapper, so its closure crosses as a Swift-convention callback; on a non-final class the
        // import binds the dispatch thunk, so its entry point is not the member's own symbol.
        var (moduleDecl, typeDb) = CreateEnvironmentWithOpaqueStruct();
        var parentDecl = CreateClassDecl("Reporter", moduleDecl);
        parentDecl.IsFinal = false;
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }), new NamedTypeSpec("Swift.Int"));
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));
        var method = CreateMethodWithParam("record", closureType, "transform", parentDecl, moduleDecl);
        method.IsModuleInternal = true;
        var context = new ModuleEmissionContext();
        context.Marshaling = new MarshalingContext(moduleDecl, typeDb, null) { EmissionContext = context };

        EmitMethod(method, typeDb, context);

        var plan = Assert.Single(context.AbiCallPlans, p => p.EntryPoint.StartsWith(method.MangledName, StringComparison.Ordinal));
        Assert.NotEqual(method.MangledName, plan.EntryPoint);
        var callback = ClosureHandler.GetCallbackFunctionName(method.Name, "transform", method.MangledName);
        Assert.Contains(callback, context.SwiftCallbackRecords.Select(r => r.CallbackName));
        Assert.Equal(SwiftPlanContradictions.ImportKey.Of(plan), Assert.Single(context.CallbackImports[callback]));
    }

    [Fact]
    public void PublicNonBlittableMethod_KeepsALiveCall()
    {
        // Same non-blittable signature on a PUBLIC declaration: a wrapper is possible for it and the
        // SB0001 prediction over-reports on shapes the emitter passes indirectly, so the floor must
        // not fire here — tombstoning on the marker's predicate would replace working members.
        var (moduleDecl, typeDb) = CreateEnvironmentWithOpaqueStruct();
        var parentDecl = CreateClassDecl("Reporter", moduleDecl);
        var method = CreateMethodWithParam("record", new NamedTypeSpec("TestModule.Opaque"), "value", parentDecl, moduleDecl);

        var csOutput = EmitMethod(method, typeDb);

        Assert.True(InvokesPInvoke(csOutput));
        Assert.DoesNotContain("throw new NotSupportedException", csOutput);
    }

    [Fact]
    public void ModuleInternalBlittableMethod_KeepsALiveCall()
    {
        // Module-internal alone is not disqualifying: an all-blittable signature reaches its exported
        // symbol through a sound direct CallConvSwift call.
        var (moduleDecl, typeDb) = CreateEnvironmentWithOpaqueStruct();
        var parentDecl = CreateClassDecl("Reporter", moduleDecl);
        var method = CreateMethodWithParam("record", new NamedTypeSpec("Swift.Int"), "value", parentDecl, moduleDecl);
        method.IsModuleInternal = true;

        var csOutput = EmitMethod(method, typeDb);

        Assert.True(InvokesPInvoke(csOutput));
        Assert.DoesNotContain("throw new NotSupportedException", csOutput);
    }

    [Fact]
    public void NonBlittableMethodOnModuleInternalParent_BodyThrowsInsteadOfCallingSwift()
    {
        // The member's own declaration is public, but a wrapper would have to name the PARENT type to
        // reconstruct `self`, and the wrapper compiles as a separate client module that cannot see an
        // internal type. So this shape is just as unwrappable as an internal member, reaches the same
        // direct CallConvSwift path, and must take the same floor — gating on the member's own
        // visibility alone would leave it emitting a live call that faults.
        var (moduleDecl, typeDb) = CreateEnvironmentWithOpaqueStruct();
        var parentDecl = CreateClassDecl("Reporter", moduleDecl);
        parentDecl.IsModuleInternal = true;
        var method = CreateMethodWithParam("record", new NamedTypeSpec("TestModule.Opaque"), "value", parentDecl, moduleDecl);

        var csOutput = EmitMethod(method, typeDb);

        Assert.Contains("throw new NotSupportedException", csOutput);
        Assert.False(InvokesPInvoke(csOutput));
        Assert.Contains(WrapperValidation.UncallableAbiDiagnosticId, csOutput);
    }

    [Fact]
    public void BlittableMethodOnModuleInternalParent_KeepsALiveCall()
    {
        // The unwrappable receiver alone is not disqualifying: a blittable signature still reaches the
        // exported symbol through a sound direct CallConvSwift call, which is exactly why the emitter
        // keeps these members rather than dropping them.
        var (moduleDecl, typeDb) = CreateEnvironmentWithOpaqueStruct();
        var parentDecl = CreateClassDecl("Reporter", moduleDecl);
        parentDecl.IsModuleInternal = true;
        var method = CreateMethodWithParam("record", new NamedTypeSpec("Swift.Int"), "value", parentDecl, moduleDecl);

        var csOutput = EmitMethod(method, typeDb);

        Assert.True(InvokesPInvoke(csOutput));
        Assert.DoesNotContain("throw new NotSupportedException", csOutput);
    }

    [Fact]
    public void ModuleInternalNonBlittableConstructor_BodyThrowsInsteadOfCallingSwift()
    {
        // Constructors emit their body through their own path, so the floor has to be applied there
        // too: an initializer that faults the process is no better than a method that does.
        var (moduleDecl, typeDb) = CreateEnvironmentWithOpaqueStruct();
        var parentDecl = CreateStructDecl("Box", moduleDecl, typeDb);
        var ctor = CreateConstructorWithParam(
            new NamedTypeSpec("TestModule.Opaque"), "value", parentDecl, moduleDecl, isFailable: false);
        ctor.IsModuleInternal = true;

        var csOutput = EmitConstructor(ctor, typeDb);

        Assert.Contains("throw new NotSupportedException", csOutput);
        Assert.False(InvokesPInvoke(csOutput, AnyPInvokePrefix));
        // The declaration survives — only the body is replaced.
        Assert.Contains("public Box(", csOutput);
    }

    [Fact]
    public void ModuleInternalNonBlittableFailableInit_FactoryBodyThrowsInsteadOfCallingSwift()
    {
        // A failable init projects to a static TryCreate factory with its own body-emission path. It
        // carries the same fatal shape and therefore the same floor; without it, the one member kind
        // whose C# surface reads as a safe "try" idiom would be the one that faults.
        var (moduleDecl, typeDb) = CreateEnvironmentWithOpaqueStruct();
        var parentDecl = CreateStructDecl("Box", moduleDecl, typeDb);
        var ctor = CreateConstructorWithParam(
            new NamedTypeSpec("TestModule.Opaque"), "value", parentDecl, moduleDecl, isFailable: true);
        ctor.IsModuleInternal = true;

        var csOutput = EmitConstructor(ctor, typeDb);

        Assert.Contains("static bool TryCreate", csOutput);
        Assert.Contains("throw new NotSupportedException", csOutput);
        Assert.False(InvokesPInvoke(csOutput, AnyPInvokePrefix));
        // The throwing factory says so on the declaration as well, so the consumer is warned at compile
        // time rather than only when the call is made.
        Assert.Contains(WrapperValidation.UncallableAbiDiagnosticId, csOutput);
    }

    [Fact]
    public void NonBlittableFailableInit_OnPublicDeclaration_KeepsALiveCall()
    {
        // Negative control for the factory path: the same signature on a wrappable declaration must
        // keep its real body, and must not pick up the uncallable marker the floor adds.
        var (moduleDecl, typeDb) = CreateEnvironmentWithOpaqueStruct();
        var parentDecl = CreateStructDecl("Box", moduleDecl, typeDb);
        var ctor = CreateConstructorWithParam(
            new NamedTypeSpec("TestModule.Opaque"), "value", parentDecl, moduleDecl, isFailable: true);

        var csOutput = EmitConstructor(ctor, typeDb);

        Assert.Contains("static bool TryCreate", csOutput);
        Assert.True(InvokesPInvoke(csOutput, AnyPInvokePrefix));
        Assert.DoesNotContain("throw new NotSupportedException", csOutput);
        Assert.DoesNotContain(WrapperValidation.UncallableAbiDiagnosticId, csOutput);
    }

    [Fact]
    public void ModuleInternalNonBlittableMethod_IsRecordedAsSkippedInTheReport()
    {
        // The surface report must not count a tombstone as a working binding — a consumer reading it
        // would otherwise see a member that only throws listed as bound.
        var (moduleDecl, typeDb) = CreateEnvironmentWithOpaqueStruct();
        var parentDecl = CreateClassDecl("Reporter", moduleDecl);
        var method = CreateMethodWithParam("record", new NamedTypeSpec("TestModule.Opaque"), "value", parentDecl, moduleDecl);
        method.IsModuleInternal = true;

        ReportCollector.Start(moduleDecl);
        try
        {
            EmitMethod(method, typeDb);
            var report = ReportCollector.Complete();

            Assert.NotNull(report);
            var skipped = report!.SkippedItems.Where(i => i.Name.Contains("record", StringComparison.OrdinalIgnoreCase)).ToList();
            var entry = Assert.Single(skipped);
            Assert.Equal(SkipReason.NonBlittableCallConvSwift, entry.Reason);
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    /// <summary>
    /// The prefix every generated extern for this member's Swift symbol carries. Matching on the
    /// prefix rather than the full hashed name keeps the assertions about whether a call was
    /// emitted, not about how the extern is named.
    /// </summary>
    private const string PInvokePrefix = "PInvoke_record";

    /// <summary>The prefix shared by every generated extern, whatever member it belongs to.</summary>
    private const string AnyPInvokePrefix = "PInvoke_";

    /// <summary>True when some line CALLS the extern, as opposed to declaring it.</summary>
    private static bool InvokesPInvoke(string csOutput, string prefix = PInvokePrefix) =>
        csOutput.Split('\n').Any(l => l.Contains(prefix) && !IsPInvokeDeclarationLine(l));

    private static bool DeclaresPInvoke(string csOutput, string prefix = PInvokePrefix) =>
        csOutput.Split('\n').Any(l => l.Contains(prefix) && IsPInvokeDeclarationLine(l));

    private static bool IsPInvokeDeclarationLine(string line) => line.Contains("partial");

    private static string DeclarationLine(string csOutput, string marker) =>
        csOutput.Split('\n').First(l => l.Contains(marker)).TrimEnd('\r');

    private static string EmitMethod(MethodDecl methodDecl, TypeDatabase typeDatabase, ModuleEmissionContext? emissionContext = null)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = new MethodEnvironment(methodDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        var context = emissionContext is null
            ? TypeHandlerContext.Empty
            : new TypeHandlerContext(null, new(), null, EmissionContext: emissionContext);
        handler.Emit(csWriter, swiftWriter, env, conductor, context);

        return csOutput.ToString();
    }

    private static string EmitConstructor(MethodDecl methodDecl, TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new ConstructorHandler(new NullLogger<ConstructorHandler>(), new HashSet<string>());
        var env = new MethodEnvironment(methodDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        var context = new TypeHandlerContext(null, new(), null, EmissionContext: new ModuleEmissionContext());
        handler.Emit(csWriter, swiftWriter, env, conductor, context);

        return csOutput.ToString();
    }

    /// <summary>
    /// A module carrying one non-frozen struct — a shape the Swift calling convention cannot carry
    /// through a direct P/Invoke — alongside the blittable Swift primitives.
    /// </summary>
    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateEnvironmentWithOpaqueStruct()
    {
        var typeDb = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 8
            });
        typeDb.AddModuleDatabase(swiftModule);

        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Opaque"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Opaque"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Opaque"),
                MetadataAccessor = "$sOpaqueMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(testModule);

        return (moduleDecl, typeDb);
    }

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
    {
        var decl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
            IsFinal = true,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(decl);
        return decl;
    }

    private static StructDecl CreateStructDecl(string name, ModuleDecl moduleDecl, TypeDatabase typeDatabase)
    {
        var decl = new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(decl);
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: decl.SwiftTypeName, record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", name),
                SwiftTypeName = decl.SwiftTypeName,
                MetadataAccessor = decl.MetadataAccessor,
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 8
            })
        });
        return decl;
    }

    private static MethodDecl CreateConstructorWithParam(
        TypeSpec paramType, string paramName, TypeDecl parentDecl, ModuleDecl moduleDecl, bool isFailable)
    {
        var ctor = new MethodDecl
        {
            Name = "init",
            MangledName = $"$s10TestModule{parentDecl.Name.Length}{parentDecl.Name}_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            IsFailable = isFailable,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec($"TestModule.{parentDecl.Name}"),
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                new ArgumentDecl
                {
                    SwiftTypeSpec = paramType,
                    Name = paramName,
                    PrivateName = paramName,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parentDecl.Methods.Add(ctor);
        return ctor;
    }

    private static MethodDecl CreateMethodWithParam(
        string name, TypeSpec paramType, string paramName, TypeDecl parentDecl, ModuleDecl moduleDecl)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                new ArgumentDecl
                {
                    SwiftTypeSpec = paramType,
                    Name = paramName,
                    PrivateName = paramName,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parentDecl.Methods.Add(method);
        return method;
    }
}
