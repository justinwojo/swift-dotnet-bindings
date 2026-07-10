// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Linq;
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
