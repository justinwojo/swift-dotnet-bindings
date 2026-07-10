// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins the persisted-report contract for a degraded EveryProtocol reverse-dispatch member.
/// When a <c>{Protocol}Proxy</c> conformance cannot be synthesized the generator keeps the
/// surrounding member but degrades it — a PRODUCE getter/return throws, a CONSUME setter/parameter
/// silently drops its C#-conformer wrap, or a reverse-dispatch receiver fail-fasts. Each outcome
/// used to be invisible in <c>binding-report.json</c>; <see cref="SuppressedProxyReporting"/> is
/// the single point that promotes the per-member decline to a classified
/// <see cref="SkipReason.SuppressedProxyMemberDegraded"/> skip with a greppable, site-stamped
/// <see cref="SkippedItem.Details"/>. These tests assert the row lands, carries the site token,
/// and reads back as <see cref="SkipDisposition.KnownLimitation"/> — the honest fixture that a
/// decline is now a durable diagnostic, not a log line.
/// </summary>
[Collection("ReportCollector")]
public class SuppressedProxyReportingTests
{
    [Theory]
    [InlineData(SuppressedProxyReporting.Site.ProduceThrow, "produce-throw")]
    [InlineData(SuppressedProxyReporting.Site.ConsumeDegraded, "consume-degraded")]
    [InlineData(SuppressedProxyReporting.Site.ReceiverFailFast, "receiver-failfast")]
    public void Details_StampsGreppableSiteToken(SuppressedProxyReporting.Site site, string expectedToken)
    {
        var details = SuppressedProxyReporting.Details(site, "MyProtocolProxy");
        Assert.Contains($"({expectedToken})", details);
        // The subject (proxy/protocol) is named so triage can grep by protocol too.
        Assert.Contains("MyProtocolProxy", details);
    }

    [Fact]
    public void Details_NullSubject_FallsBackToGenericPhrase()
    {
        var details = SuppressedProxyReporting.Details(SuppressedProxyReporting.Site.ProduceThrow, proxyOrProtocol: null);
        Assert.Contains("a suppressed protocol proxy", details);
    }

    [Fact]
    public void Record_Method_ProduceThrow_RecordsClassifiedDegradedSkip()
    {
        RunWithCollector(moduleDecl =>
        {
            var method = TestModelFactory.CreateMethod("materials", parent: moduleDecl);
            SuppressedProxyReporting.Record(method, SuppressedProxyReporting.Site.ProduceThrow, "IMaterialProxy");
        },
        report =>
        {
            var item = Assert.Single(report.SkippedItems);
            Assert.Equal(BindingItemKind.Method, item.Kind);
            Assert.Equal("materials", item.Name);
            Assert.Equal(SkipReason.SuppressedProxyMemberDegraded, item.Reason);
            Assert.Contains("(produce-throw)", item.Details);
            Assert.Equal(SkipDisposition.KnownLimitation, SkipDispositionClassifier.Classify(item));
        });
    }

    [Fact]
    public void Record_MethodParameter_ConsumeDegraded_RecordsClassifiedDegradedSkip()
    {
        RunWithCollector(moduleDecl =>
        {
            var method = TestModelFactory.CreateMethod("setDelegate", parent: moduleDecl);
            SuppressedProxyReporting.Record(method, SuppressedProxyReporting.Site.ConsumeDegraded, "IRoomCaptureDelegateProxy");
        },
        report =>
        {
            var item = Assert.Single(report.SkippedItems);
            Assert.Equal(SkipReason.SuppressedProxyMemberDegraded, item.Reason);
            Assert.Contains("(consume-degraded)", item.Details);
            Assert.Equal(SkipDisposition.KnownLimitation, SkipDispositionClassifier.Classify(item));
        });
    }

    /// <summary>
    /// A getter that throws (PRODUCE) and a setter that silently drops the C#-conformer wrap
    /// (CONSUME) on the SAME property are two distinct declines and must record as two rows —
    /// the get+set granularity is why the helper takes an <see cref="AccessorKind"/>.
    /// </summary>
    [Fact]
    public void Record_Property_GetterThrowAndSetterConsume_CoexistAsTwoRows()
    {
        RunWithCollector(moduleDecl =>
        {
            var property = TestModelFactory.CreateProperty("handler", parent: moduleDecl);
            SuppressedProxyReporting.Record(property, SuppressedProxyReporting.Site.ProduceThrow, "IHandlerProxy", AccessorKind.Getter);
            SuppressedProxyReporting.Record(property, SuppressedProxyReporting.Site.ConsumeDegraded, "IHandlerProxy", AccessorKind.Setter);
        },
        report =>
        {
            Assert.Equal(2, report.SkippedItems.Count);
            Assert.All(report.SkippedItems, i =>
            {
                Assert.Equal(BindingItemKind.Property, i.Kind);
                Assert.Equal("handler", i.Name);
                Assert.Equal(SkipReason.SuppressedProxyMemberDegraded, i.Reason);
                Assert.Equal(SkipDisposition.KnownLimitation, SkipDispositionClassifier.Classify(i));
            });
            Assert.Contains(report.SkippedItems, i => i.Details!.Contains("(produce-throw)"));
            Assert.Contains(report.SkippedItems, i => i.Details!.Contains("(consume-degraded)"));
        });
    }

    [Fact]
    public void Record_Subscript_ProduceThrow_RecordsClassifiedDegradedSkip()
    {
        RunWithCollector(moduleDecl =>
        {
            var subscriptDecl = TestModelFactory.CreateSubscript(
                moduleDecl, new[] { ("index", "Swift.Int") });
            SuppressedProxyReporting.Record(subscriptDecl, SuppressedProxyReporting.Site.ProduceThrow, "IElementProxy", AccessorKind.SubscriptGetter);
        },
        report =>
        {
            var item = Assert.Single(report.SkippedItems);
            Assert.Equal(BindingItemKind.Subscript, item.Kind);
            Assert.Equal(SkipReason.SuppressedProxyMemberDegraded, item.Reason);
            Assert.Contains("(produce-throw)", item.Details);
            Assert.Equal(SkipDisposition.KnownLimitation, SkipDispositionClassifier.Classify(item));
        });
    }

    [Fact]
    public void Record_DescriptorMember_RecordsClassifiedDegradedSkip()
    {
        RunWithCollector(moduleDecl =>
        {
            SuppressedProxyReporting.Record(
                BindingItemKind.Method, "Palette.TryGetSwatch", moduleDecl,
                SuppressedProxyReporting.Site.ProduceThrow, "ISwatchProxy");
        },
        report =>
        {
            var item = Assert.Single(report.SkippedItems);
            Assert.Equal(BindingItemKind.Method, item.Kind);
            Assert.Equal("Palette.TryGetSwatch", item.Name);
            Assert.Equal(SkipReason.SuppressedProxyMemberDegraded, item.Reason);
            Assert.Contains("(produce-throw)", item.Details);
            Assert.Equal(SkipDisposition.KnownLimitation, SkipDispositionClassifier.Classify(item));
        });
    }

    [Fact]
    public void RecordReceiver_FailFast_RecordsClassifiedDegradedSkip()
    {
        RunWithCollector(_ =>
        {
            SuppressedProxyReporting.RecordReceiver("IAnalyzer.analyze(with:)", "IAnalyzerProxy");
        },
        report =>
        {
            var item = Assert.Single(report.SkippedItems);
            Assert.Equal(SkipReason.SuppressedProxyMemberDegraded, item.Reason);
            Assert.Contains("(receiver-failfast)", item.Details);
            // API contract: a non-null proxy name reaches the row's Details (so triage can grep the
            // receiver-failfast rows by proxy just like the produce/consume rows) rather than falling back
            // to the generic "a suppressed protocol proxy". This asserts the RecordReceiver→Details plumbing
            // for a named subject; that the EMITTER actually passes the exception's ProxyClassName here
            // (not null) is locked separately by
            // EmitReceiverOrDegrade_SuppressedProxy_RecordsRowNamingTheProxyFromException, which drives the
            // real emitter catch path.
            Assert.Contains("IAnalyzerProxy", item.Details);
            Assert.DoesNotContain("a suppressed protocol proxy", item.Details);
            Assert.Equal(SkipDisposition.KnownLimitation, SkipDispositionClassifier.Classify(item));
        });
    }

    /// <summary>
    /// End-to-end wiring lock for the reverse-dispatch receiver path. Drives the actual emitter degrade
    /// branch (<see cref="ProtocolProxyEmitter.EmitReceiverOrDegrade"/>) with an <c>emitBody</c> that throws
    /// <see cref="SuppressedProxyReferenceException"/> — the shape a receiver whose existential references a
    /// suppressed proxy takes — and asserts the recorded row NAMES the proxy carried on the exception. This
    /// is the regression the API-level <see cref="RecordReceiver_FailFast_RecordsClassifiedDegradedSkip"/>
    /// cannot catch: reverting the emitter to pass <c>null</c> (dropping <c>ex.ProxyClassName</c>) leaves that
    /// test green but fails this one, because only this test exercises the catch → <c>ex.ProxyClassName</c> →
    /// <see cref="SuppressedProxyReporting.RecordReceiver"/> plumbing.
    /// </summary>
    [Fact]
    public void EmitReceiverOrDegrade_SuppressedProxy_RecordsRowNamingTheProxyFromException()
    {
        RunWithCollector(_ =>
        {
            var typeDatabase = new TypeDatabase();
            typeDatabase.AddModuleDatabase(new ModuleTypeDatabase("TestModule", "/fake/path"));
            var emitter = new ProtocolProxyEmitter(
                typeDatabase, NullLogger.Instance, "TestModule", new ModuleEmissionContext());

            var writer = new CSharpWriter(new StringWriter());
            emitter.EmitReceiverOrDegrade(
                writer, "void", "Receive_analyze", "IntPtr vtHandle, IntPtr selfContainer, IntPtr valuePtr",
                "IAnalyzer.analyze(with:)",
                emitBody: () => throw new SuppressedProxyReferenceException("IAnalyzerReceiverProxy"));
        },
        report =>
        {
            var item = Assert.Single(report.SkippedItems);
            Assert.Equal(SkipReason.SuppressedProxyMemberDegraded, item.Reason);
            Assert.Contains("(receiver-failfast)", item.Details);
            // The name comes off the thrown exception, threaded through EmitSuppressedProxyReceiverStub —
            // NOT the null-subject generic fallback. This is what pins the emitter wiring.
            Assert.Contains("IAnalyzerReceiverProxy", item.Details);
            Assert.DoesNotContain("a suppressed protocol proxy", item.Details);
            Assert.Equal(SkipDisposition.KnownLimitation, SkipDispositionClassifier.Classify(item));
        });
    }

    private static void RunWithCollector(System.Action<ModuleDecl> record, System.Action<BindingReport> assert)
    {
        try
        {
            var moduleDecl = TestModelFactory.CreateModuleDecl("TestModule");
            ReportCollector.Start(moduleDecl);
            record(moduleDecl);
            var report = ReportCollector.Complete();
            Assert.NotNull(report);
            assert(report!);
        }
        finally
        {
            ReportCollector.Reset();
        }
    }
}
