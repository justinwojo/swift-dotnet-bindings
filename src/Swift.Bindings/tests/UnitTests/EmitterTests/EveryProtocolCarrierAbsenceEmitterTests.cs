// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Full-pipeline regression net for the empty-<c>suitableProtocols</c> × full-proxy-eligible hole
/// (the StripeCore SWIFTBIND108 report). When an ENTIRE module's suitable-protocol set is empty, the
/// Swift side never runs <c>EmitEveryProtocolClass</c>, so no <c>@_cdecl("SBW_CreateEveryProtocol")</c>
/// carrier is defined; the C# side must therefore NOT emit a full reverse-dispatch proxy, whose
/// <c>NativeMethods</c> would name that never-defined factory as a P/Invoke <c>EntryPoint</c> — a
/// dangling wrapper symbol that <see cref="WrapperSymbolIntegrityGate"/> hard-fails (SWIFTBIND108) at
/// generation time and that would otherwise throw <c>EntryPointNotFoundException</c> at runtime.
///
/// <para>
/// This cannot be reproduced in BindingTests: both of that harness's Swift modules already contain
/// suitable protocols, so neither ever presents an empty carrier-less module. The hole is a
/// module-global emission decision, so this full-pipeline unit test (real <c>ModuleHandler.Marshal</c>
/// + <c>Emit</c>, reconciled through the real integrity gate) is the durable gate. The policy-level
/// decision itself is pinned separately by <see cref="ProtocolProxyEmissionPolicyTests"/>.
/// </para>
/// </summary>
public class EveryProtocolCarrierAbsenceEmitterTests : IDisposable
{
    private readonly string _dir;

    public EveryProtocolCarrierAbsenceEmitterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sbw-carrier-absence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void EmptySuitableModule_FullProxyEligible_NoDanglingCarrierSymbol()
    {
        // A module whose ONLY protocol is dropped from the suitable set (here: a member references a
        // module-internal type not in the DB — a non-forward-safe drop cause, so it is NOT admitted as
        // a read-only proxy either) never emits the EveryProtocol carrier. A full proxy for it would
        // dangle on SBW_CreateEveryProtocol. `ping()` keeps the protocol proxy-eligible (an
        // implementable member) so the suppression is decided by the carrier fact, not by the
        // no-implementable-member vtable guard.
        var protocolDecl = BuildDroppedNonReadOnlyProtocol("UnknownFieldsDecodable");
        var (csOutput, swiftOutput, ctx) = EmitModule("TestModule", protocolDecl);

        // We really did reproduce the empty-suitable shape: no carrier was emitted.
        Assert.False(ctx.WasEveryProtocolCarrierEmitted,
            "Test setup invalid: the carrier WAS emitted, so this is not the empty-suitable-module shape.");
        Assert.DoesNotContain("@_cdecl(\"SBW_CreateEveryProtocol\")", swiftOutput);

        // The real gate, over the real emitted text, must find no dangling wrapper symbol.
        WriteSource("TestModule.cs", csOutput);
        WriteSource("TestModule.swift", swiftOutput);
        var logger = new CapturingLogger();
        Assert.False(WrapperSymbolIntegrityGate.HasViolations(_dir, logger),
            "A full proxy calling the never-emitted SBW_CreateEveryProtocol carrier leaked through — SWIFTBIND108.");
        Assert.DoesNotContain(logger.Messages, m => m.Contains("SWIFTBIND108"));

        // And no full-proxy P/Invoke names the carrier factory as its entry point.
        Assert.DoesNotContain("EntryPoint = \"SBW_CreateEveryProtocol\"", csOutput);
    }

    [Fact]
    public void SuitableModule_CarrierEmitted_ProxyKeepsCarrierSymbol()
    {
        // Discrimination guard: the SAME protocol WITHOUT the internal-type member is suitable, so the
        // carrier IS emitted and a full proxy legitimately references it. This pins the suppression to
        // the carrier-absence fact — the fix must not start suppressing carrier-present proxies.
        var protocolDecl = BuildProxyEligibleProtocol("UnknownFieldsDecodable");
        var (csOutput, swiftOutput, ctx) = EmitModule("TestModule", protocolDecl);

        Assert.True(ctx.WasEveryProtocolCarrierEmitted,
            "Test setup invalid: a suitable single-protocol module should emit the EveryProtocol carrier.");
        Assert.Contains("@_cdecl(\"SBW_CreateEveryProtocol\")", swiftOutput);

        // Carrier present ⇒ every referenced wrapper symbol is defined ⇒ gate is silent.
        WriteSource("TestModule.cs", csOutput);
        WriteSource("TestModule.swift", swiftOutput);
        var logger = new CapturingLogger();
        Assert.False(WrapperSymbolIntegrityGate.HasViolations(_dir, logger));

        // Discrimination: the full proxy legitimately NAMES the carrier factory as its P/Invoke
        // entry point (the exact mirror of the carrier-absent test's DoesNotContain). Without this a
        // regression that suppressed ALL full proxies would still pass — the WasEveryProtocolCarrierEmitted
        // flag and a silent gate are both satisfied by emitting no full proxy at all.
        Assert.Contains("EntryPoint = \"SBW_CreateEveryProtocol\"", csOutput);
    }

    // A non-class, non-Self protocol that is full-proxy-eligible (has an implementable `ping()`
    // requirement) but dropped from the suitable set because `probe()` returns a module-internal type
    // absent from the DB (HasMembersReferencingInternalTypes). Internal-type reach is NOT a
    // forward-safe reverse-impossible reason, so the read-only admission does not rescue it — exactly
    // the carrier-less full-proxy cell.
    private static ProtocolDecl BuildDroppedNonReadOnlyProtocol(string name)
    {
        var protocol = BuildProxyEligibleProtocol(name);
        protocol.Methods.Add(new MethodDecl
        {
            Name = "probe",
            MangledName = "$sprobe",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            // CSSignature[0] is the return slot — a module-internal type not in the DB.
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = "",
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.HiddenReturn"),
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        });
        return protocol;
    }

    // The proxy-eligible baseline: one implementable void `ping()` requirement, no Self/associated
    // types, no class binding — suitable on its own, so it emits the carrier.
    private static ProtocolDecl BuildProxyEligibleProtocol(string name)
    {
        var protocol = new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}P",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            HasSelfRequirement = false,
            IsClassBound = false,
            ParentDecl = null,
            ModuleDecl = null
        };

        protocol.Methods.Add(new MethodDecl
        {
            Name = "ping",
            MangledName = "$sping",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = "",
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        });

        return protocol;
    }

    // Runs the real ModuleHandler.Marshal + Emit pipeline over a single-protocol module and returns
    // both emitted texts and the populated context. The protocol is added to BOTH Types and Protocols
    // — the parser maintains those as independent lists, and ProtocolHandler (which emits/suppresses
    // the C# proxy) only walks Types, so a Protocols-only entry would never exercise the proxy path.
    private static (string csOutput, string swiftOutput, ModuleEmissionContext ctx) EmitModule(
        string moduleName, ProtocolDecl protocolDecl)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = moduleName,
            Dependencies = new List<string>(),
            Types = new List<TypeDecl> { protocolDecl },
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl> { protocolDecl },
            ParentDecl = null,
            ModuleDecl = null
        };

        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        typeDatabase.AddModuleDatabase(new ModuleTypeDatabase(moduleName, "/fake/path"));

        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var handler = new ModuleHandler(new NullLogger<ModuleHandler>());
        var env = handler.Marshal(moduleDecl, typeDatabase);

        var conductor = new Conductor(new NullLoggerFactory());
        var emissionCtx = new ModuleEmissionContext();
        var context = new TypeHandlerContext(null, new(), null, EmissionContext: emissionCtx);
        handler.Emit(csWriter, swiftWriter, env, conductor, context);

        return (csStringWriter.ToString(), swiftStringWriter.ToString(), emissionCtx);
    }

    private void WriteSource(string fileName, string contents)
        => File.WriteAllText(Path.Combine(_dir, fileName), contents);

    // Minimal ILogger that captures formatted messages so a test can assert SWIFTBIND108 presence.
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
