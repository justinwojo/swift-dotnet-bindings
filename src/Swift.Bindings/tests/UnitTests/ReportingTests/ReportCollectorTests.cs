// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

public class ReportCollectorTests
{
    [Fact]
    public void RecordTypeSkipped_AfterRecordTypeEmitted_IsSilentlySuppressed()
    {
        // Regression: ReportCollector.RecordTypeSkipped silently bails out when
        // the type is already in EmittedTypeKeys (RecordTypeEmitted -> ... -> RecordTypeSkipped
        // is a no-op). Handlers therefore MUST call any "skip gate" check BEFORE
        // RecordTypeEmitted, otherwise skipped types are double-counted as emitted and
        // never make it onto the SkippedItems list.
        //
        // This test pins the suppression behaviour so any future change to RecordTypeSkipped
        // is forced to also revisit handler ordering. The fix on the handler side lives in
        // ClassHandler / FrozenStructHandler / NonFrozenStructHandler / EnumHandler, where
        // TypeMetadataAccessorSkipGate.ShouldSkip now runs before RecordTypeEmitted.
        var moduleDecl = CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordTypeEmitted(classDecl);
        // Subsequent skip is suppressed because the type is already in EmittedTypeKeys.
        ReportCollector.RecordTypeSkipped(classDecl, SkipReason.UnsupportedType, "should be suppressed");

        var report = ReportCollector.Complete();
        Assert.NotNull(report);
        Assert.Equal(1, report!.EmittedTypes);
        Assert.Equal(0, report.SkippedTypes);
        Assert.Empty(report.SkippedItems);

        ReportCollector.Reset();
    }

    [Fact]
    public void RecordObjCPrefixBridge_FlowsOntoReport_SortedAndDeduped()
    {
        // F10 Stage 20: an ObjC-prefix bridge guess (a SUCCESSFUL heuristic bridge, not a
        // degradation) is recorded for observability. Distinct entries flow onto the report sorted;
        // duplicates collapse. No loud diagnostic is emitted for these — unlike SWIFTBIND025/026.
        var moduleDecl = CreateModuleDecl();

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordObjCPrefixBridge("UIKit.UIImage");
        ReportCollector.RecordObjCPrefixBridge("Foundation.NSURL");
        ReportCollector.RecordObjCPrefixBridge("UIKit.UIImage"); // duplicate collapses

        var report = ReportCollector.Complete();
        Assert.NotNull(report);
        Assert.Equal(new[] { "Foundation.NSURL", "UIKit.UIImage" }, report!.ObjCPrefixBridges);

        ReportCollector.Reset();
    }

    [Fact]
    public void RecordObjCPrefixBridge_OutsideSession_IsNoOp()
    {
        // No active report session: the record is silently dropped, mirroring RecordObjectDegradation.
        ReportCollector.Reset();
        ReportCollector.RecordObjCPrefixBridge("UIKit.UIImage");

        var moduleDecl = CreateModuleDecl();
        ReportCollector.Start(moduleDecl);
        var report = ReportCollector.Complete();
        Assert.NotNull(report);
        Assert.Empty(report!.ObjCPrefixBridges);

        ReportCollector.Reset();
    }

    [Fact]
    public void RecordTypeSkipped_BeforeRecordTypeEmitted_TakesPrecedence()
    {
        // Mirror of the test above: when ShouldSkip runs FIRST (the new handler ordering),
        // the skip is recorded correctly and the subsequent RecordTypeEmitted call is itself
        // suppressed by the skip-set. This is the path the handlers should hit with the
        // correct ordering; if a handler regresses to recording emit before checking the gate,
        // the test above will fire but this one will keep passing — the asymmetry is the
        // signal that a regression is in the handler, not in the collector.
        var moduleDecl = CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordTypeSkipped(classDecl, SkipReason.UnsupportedType, "skip wins");
        // Subsequent RecordTypeEmitted is suppressed because SkippedTypeKeys already contains the key.
        ReportCollector.RecordTypeEmitted(classDecl);

        var report = ReportCollector.Complete();
        Assert.NotNull(report);
        Assert.Equal(0, report!.EmittedTypes);
        Assert.Equal(1, report.SkippedTypes);
        // One row for the type itself, plus one per member the suppression meant was never
        // enumerated (Loader.State, Loader.Fetch, and Loader.Payload.Read under the suppressed
        // parent) — those members are counted in TotalMembers, so leaving them unrecorded would
        // make the report claim less was lost than actually was.
        Assert.Single(report.SkippedItems, i => i.Kind == BindingItemKind.Type);
        Assert.Equal(3, report.SkippedItems.Count(i => i.Reason == SkipReason.ParentTypeSuppressed));

        ReportCollector.Reset();
    }

    [Fact]
    public void StartAndComplete_ComputesTotalsAndRecordedCounts()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];
        var nestedStruct = (StructDecl)classDecl.Types[0];
        var protocolDecl = moduleDecl.Protocols[0];

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordTypeEmitted(classDecl);
        ReportCollector.RecordTypeSkipped(nestedStruct, SkipReason.UnsupportedType, "test");
        ReportCollector.RecordTypeEmitted(protocolDecl);
        ReportCollector.RecordMemberEmitted(BindingItemKind.Method, "Fetch", classDecl);
        ReportCollector.RecordMemberSkipped(BindingItemKind.Property, "State", classDecl, SkipReason.AnyTypeFallback, "test");

        var report = ReportCollector.Complete();
        Assert.NotNull(report);
        Assert.Equal("TestModule", report.ModuleName);
        Assert.Equal(3, report.TotalTypes);
        Assert.Equal(6, report.TotalMembers);
        Assert.Equal(2, report.EmittedTypes);
        Assert.Equal(1, report.SkippedTypes);
        Assert.Equal(1, report.EmittedMembers);
        // State (recorded directly) plus Payload.Read, which is only reachable through the
        // suppressed nested struct and so is accounted for against that suppression.
        Assert.Equal(2, report.SkippedMembers);
        Assert.Equal(0, report.SynthesizedMembers);
        Assert.Equal(3, report.SkippedItems.Count);

        ReportCollector.Reset();
    }

    [Fact]
    public void ReportEmitter_WritesJsonReportFile()
    {
        var moduleDecl = CreateModuleDecl();
        ReportCollector.Start(moduleDecl);
        var report = ReportCollector.Complete();
        Assert.NotNull(report);

        var outputDir = Path.Combine(Path.GetTempPath(), $"swift-bindings-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);

        try
        {
            ReportEmitter.Emit(report, outputDir, NullLogger.Instance);
            var reportPath = Path.Combine(outputDir, "binding-report.json");
            Assert.True(File.Exists(reportPath));
            var text = File.ReadAllText(reportPath);
            Assert.Contains("\"ModuleName\": \"TestModule\"", text);
        }
        finally
        {
            Directory.Delete(outputDir, true);
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void RecordMemberSkipped_PopulatesRecommendedWorkaround()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordMemberSkipped(BindingItemKind.Method, "Fetch", classDecl, SkipReason.UnsupportedExistential, "test");

        var report = ReportCollector.Complete();
        Assert.NotNull(report);
        Assert.Single(report.SkippedItems);
        Assert.NotNull(report.SkippedItems[0].RecommendedWorkaround);
        Assert.Contains("Swift wrapper", report.SkippedItems[0].RecommendedWorkaround);

        ReportCollector.Reset();
    }

    [Fact]
    public void RecordMemberWrapped_IncrementsEmittedCountAndPopulatesWrappedItems()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordMemberWrapped(
            BindingItemKind.Method, "init", "$s10TestModule6LoaderCACycfc",
            classDecl, "ExistentialBypass", "Existential parameter(s) omitted.");

        var report = ReportCollector.Complete();
        Assert.NotNull(report);
        // Simple key only — matches distinct-name counting in CalculateTotals
        Assert.Equal(1, report.EmittedMembers);
        Assert.Single(report.WrappedItems);
        Assert.Equal("init", report.WrappedItems[0].Name);
        Assert.Equal("$s10TestModule6LoaderCACycfc", report.WrappedItems[0].MangledName);
        Assert.Equal("ExistentialBypass", report.WrappedItems[0].WrapperKind);

        ReportCollector.Reset();
    }

    [Fact]
    public void RecordMemberWrapped_OverloadedInits_GetDistinctEntries()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordMemberWrapped(
            BindingItemKind.Method, "init", "$s10TestModule6LoaderCACycfc",
            classDecl, "ExistentialBypass");
        ReportCollector.RecordMemberWrapped(
            BindingItemKind.Method, "init", "$s10TestModule6LoaderCACSi_tcfc",
            classDecl, "ExistentialBypass");

        var report = ReportCollector.Complete();
        Assert.NotNull(report);
        // Both overloads share the simple key "Method:TestModule.Loader:init"
        Assert.Equal(1, report.EmittedMembers);
        Assert.Equal(2, report.WrappedItems.Count);

        ReportCollector.Reset();
    }

    [Fact]
    public void ReportEmitter_WritesJsonWithRecommendedWorkaround()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordMemberSkipped(BindingItemKind.Method, "Fetch", classDecl, SkipReason.AsyncProperty, "test");

        var report = ReportCollector.Complete();
        Assert.NotNull(report);

        var outputDir = Path.Combine(Path.GetTempPath(), $"swift-bindings-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);

        try
        {
            ReportEmitter.Emit(report, outputDir, NullLogger.Instance);
            var text = File.ReadAllText(Path.Combine(outputDir, "binding-report.json"));
            Assert.Contains("RecommendedWorkaround", text);
            Assert.Contains("async method", text);
        }
        finally
        {
            Directory.Delete(outputDir, true);
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void ReportEmitter_WritesJsonWithWrappedItems()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordMemberWrapped(
            BindingItemKind.Method, "init", "$s10TestModule6LoaderCACycfc",
            classDecl, "ExistentialBypass", "details");

        var report = ReportCollector.Complete();
        Assert.NotNull(report);

        var outputDir = Path.Combine(Path.GetTempPath(), $"swift-bindings-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);

        try
        {
            ReportEmitter.Emit(report, outputDir, NullLogger.Instance);
            var text = File.ReadAllText(Path.Combine(outputDir, "binding-report.json"));
            Assert.Contains("WrappedItems", text);
            Assert.Contains("ExistentialBypass", text);
            Assert.Contains("MangledName", text);
        }
        finally
        {
            Directory.Delete(outputDir, true);
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void RecordMemberSynthesized_IncrementsSynthesizedCountOnly()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordMemberSynthesized(BindingItemKind.Method, "get_value", classDecl);

        var report = ReportCollector.Complete();
        Assert.NotNull(report);
        Assert.Equal(1, report.SynthesizedMembers);
        Assert.Equal(0, report.EmittedMembers);
        Assert.Equal(0, report.SkippedMembers);

        ReportCollector.Reset();
    }

    [Fact]
    public void ReportEmitter_SkippedItems_ShowsReassuranceMessage()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordMemberSkipped(BindingItemKind.Method, "Fetch", classDecl, SkipReason.UnsupportedExistential, "test");

        var report = ReportCollector.Complete();
        Assert.NotNull(report);

        var outputDir = Path.Combine(Path.GetTempPath(), $"swift-bindings-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);

        try
        {
            var logger = new CapturingLogger();
            ReportEmitter.Emit(report, outputDir, logger);
            var allMessages = string.Join("\n", logger.Messages);
            Assert.Contains("excluded from C# output", allMessages);
            Assert.Contains("binding-report.json", allMessages);
        }
        finally
        {
            Directory.Delete(outputDir, true);
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void ReportEmitter_SkippedItems_ShowsDescriptionSuffix()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordMemberSkipped(BindingItemKind.Method, "Fetch", classDecl, SkipReason.UnsupportedExistential, "test");

        var report = ReportCollector.Complete();
        Assert.NotNull(report);

        var outputDir = Path.Combine(Path.GetTempPath(), $"swift-bindings-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);

        try
        {
            var logger = new CapturingLogger();
            ReportEmitter.Emit(report, outputDir, logger);
            var allMessages = string.Join("\n", logger.Messages);
            Assert.Contains("protocol-typed parameter/return not yet projected", allMessages);
        }
        finally
        {
            Directory.Delete(outputDir, true);
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void ReportEmitter_NoSkippedItems_NoReassuranceMessage()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordTypeEmitted(classDecl);

        var report = ReportCollector.Complete();
        Assert.NotNull(report);

        var outputDir = Path.Combine(Path.GetTempPath(), $"swift-bindings-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);

        try
        {
            var logger = new CapturingLogger();
            ReportEmitter.Emit(report, outputDir, logger);
            var allMessages = string.Join("\n", logger.Messages);
            Assert.DoesNotContain("excluded from C# output", allMessages);
        }
        finally
        {
            Directory.Delete(outputDir, true);
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void Complete_PopulatesPerKindMemberCounts()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordMemberEmitted(BindingItemKind.Method, "Fetch", classDecl);
        ReportCollector.RecordMemberEmitted(BindingItemKind.Method, "Load", classDecl);
        ReportCollector.RecordMemberEmitted(BindingItemKind.Property, "Name", classDecl);
        ReportCollector.RecordMemberSkipped(BindingItemKind.Property, "State", classDecl, SkipReason.AnyTypeFallback, "test");
        ReportCollector.RecordMemberSkipped(BindingItemKind.Method, "BadMethod", classDecl, SkipReason.UnsupportedSignature, "test");

        var report = ReportCollector.Complete();
        Assert.NotNull(report);

        // Emitted per-kind
        Assert.Equal(2, report.EmittedMembersByKind[BindingItemKind.Method]);
        Assert.Equal(1, report.EmittedMembersByKind[BindingItemKind.Property]);
        Assert.False(report.EmittedMembersByKind.ContainsKey(BindingItemKind.Operator));

        // Skipped per-kind
        Assert.Equal(1, report.SkippedMembersByKind[BindingItemKind.Property]);
        Assert.Equal(1, report.SkippedMembersByKind[BindingItemKind.Method]);

        ReportCollector.Reset();
    }

    [Fact]
    public void ReportEmitter_EmitsPerKindBreakdown()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordMemberEmitted(BindingItemKind.Method, "Fetch", classDecl);
        ReportCollector.RecordMemberEmitted(BindingItemKind.Property, "Name", classDecl);
        ReportCollector.RecordMemberSkipped(BindingItemKind.Method, "BadMethod", classDecl, SkipReason.UnsupportedSignature, "test");

        var report = ReportCollector.Complete();
        Assert.NotNull(report);

        var outputDir = Path.Combine(Path.GetTempPath(), $"swift-bindings-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);

        try
        {
            var logger = new CapturingLogger();
            ReportEmitter.Emit(report, outputDir, logger);
            var allMessages = string.Join("\n", logger.Messages);
            // Should include per-kind breakdown
            Assert.Contains("Method", allMessages);
            Assert.Contains("Property", allMessages);
        }
        finally
        {
            Directory.Delete(outputDir, true);
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void RecordMemberSkipped_DistinctMethodOverloads_RecordEachAsSeparateSkip()
    {
        // Previously RecordMemberSkipped dedup'd on "Kind:ContainingType:Name"
        // and refused to record any skip beyond the first whenever the same
        // triple was already in the skip-or-emit set, which collapsed all
        // overloads of foo(...) into one entry. After the identity fix, two
        // overloads with the same base name but different parameter signatures
        // must each appear in SkippedItems.
        //
        // Pre-fix behavior would have asserted Single(report.SkippedItems)
        // here — the assertion below would have failed with one entry. That's
        // the regression guard.
        var moduleDecl = TestModelFactory.CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];
        var fooInt = TestModelFactory.CreateMethod(
            "foo",
            parent: classDecl,
            args: new[] { ("_", "Swift.Int") },
            mangledName: "$s10TestModule6LoaderC3fooyySiF");
        var fooString = TestModelFactory.CreateMethod(
            "foo",
            parent: classDecl,
            args: new[] { ("_", "Swift.String") },
            mangledName: "$s10TestModule6LoaderC3fooyySSF");

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordMemberSkipped(fooInt, SkipReason.UnsupportedSignature, "Int overload skipped");
        ReportCollector.RecordMemberSkipped(fooString, SkipReason.UnsupportedExistential, "String overload skipped");

        var report = ReportCollector.Complete();
        Assert.NotNull(report);
        Assert.Equal(2, report!.SkippedMembers);
        Assert.Equal(2, report.SkippedItems.Count);
        // Both overloads share the same display name + containing type — only
        // the reason and details (driven from the per-overload identity) tell
        // them apart in the report list.
        Assert.All(report.SkippedItems, item =>
        {
            Assert.Equal("foo", item.Name);
            Assert.Equal("TestModule.Loader", item.ContainingType);
        });
        Assert.Contains(report.SkippedItems, i => i.Reason == SkipReason.UnsupportedSignature && i.Details == "Int overload skipped");
        Assert.Contains(report.SkippedItems, i => i.Reason == SkipReason.UnsupportedExistential && i.Details == "String overload skipped");

        ReportCollector.Reset();
    }

    [Fact]
    public void RecordMemberSkipped_SameMethodTwice_DedupsToOneEntry()
    {
        // The dedup contract still holds for genuinely-equal skips: calling
        // RecordMemberSkipped twice for the same MethodDecl with the same
        // identity records a single SkippedItems entry. This is the safety
        // check on the M1 identity rewrite — distinguishing overloads must
        // not also un-dedup repeated calls for the same overload.
        var moduleDecl = TestModelFactory.CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];
        var foo = TestModelFactory.CreateMethod(
            "foo",
            parent: classDecl,
            args: new[] { ("_", "Swift.Int") },
            mangledName: "$s10TestModule6LoaderC3fooyySiF");

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordMemberSkipped(foo, SkipReason.UnsupportedSignature, "first call");
        ReportCollector.RecordMemberSkipped(foo, SkipReason.UnsupportedSignature, "second call should be deduped");

        var report = ReportCollector.Complete();
        Assert.NotNull(report);
        Assert.Equal(1, report!.SkippedMembers);
        Assert.Single(report.SkippedItems);
        Assert.Equal("first call", report.SkippedItems[0].Details);

        ReportCollector.Reset();
    }

    [Fact]
    public void RecordMemberEmitted_DistinctMethodOverloads_BothCount()
    {
        // Mirror of the skip test on the emit path — overload-stable identity
        // means the per-kind emitted count tracks each overload, not the
        // distinct base-name count.
        var moduleDecl = TestModelFactory.CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];
        var fooInt = TestModelFactory.CreateMethod(
            "foo",
            parent: classDecl,
            args: new[] { ("_", "Swift.Int") },
            mangledName: "$s10TestModule6LoaderC3fooyySiF");
        var fooString = TestModelFactory.CreateMethod(
            "foo",
            parent: classDecl,
            args: new[] { ("_", "Swift.String") },
            mangledName: "$s10TestModule6LoaderC3fooyySSF");

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordMemberEmitted(fooInt);
        ReportCollector.RecordMemberEmitted(fooString);

        var report = ReportCollector.Complete();
        Assert.NotNull(report);
        Assert.Equal(2, report!.EmittedMembers);
        Assert.Equal(2, report.EmittedMembersByKind[BindingItemKind.Method]);

        ReportCollector.Reset();
    }

    [Fact]
    public void RecordMemberSkipped_PropertyGetterAndSetter_RecordedAsDistinctEntries()
    {
        // The EnumHandler.SimpleEnum migration relies on AccessorKind.Setter to
        // distinguish setter-only skips from getter-or-property-level skips on
        // the same property name. Pre-fix, the synthetic "{name}_set" base name
        // carried the distinction; the M1 identity rewrite moves it onto the
        // explicit AccessorKind field. Both per-accessor skips must produce
        // separate SkippedItem entries.
        var moduleDecl = TestModelFactory.CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];
        var prop = TestModelFactory.CreateProperty("value", parent: classDecl);

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordMemberSkipped(prop, SkipReason.UnsupportedSignature, "getter rejected", AccessorKind.Getter);
        ReportCollector.RecordMemberSkipped(prop, SkipReason.UnsupportedSignature, "setter rejected", AccessorKind.Setter);

        var report = ReportCollector.Complete();
        Assert.NotNull(report);
        Assert.Equal(2, report!.SkippedMembers);
        Assert.Equal(2, report.SkippedItems.Count);
        Assert.Contains(report.SkippedItems, i => i.Details == "getter rejected");
        Assert.Contains(report.SkippedItems, i => i.Details == "setter rejected");

        ReportCollector.Reset();
    }

    [Fact]
    public void ReportEmitter_SummaryHeader_IsBindingGenerationSummary()
    {
        var moduleDecl = CreateModuleDecl();

        ReportCollector.Start(moduleDecl);
        var report = ReportCollector.Complete();
        Assert.NotNull(report);

        var outputDir = Path.Combine(Path.GetTempPath(), $"swift-bindings-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);

        try
        {
            var logger = new CapturingLogger();
            ReportEmitter.Emit(report, outputDir, logger);
            var allMessages = string.Join("\n", logger.Messages);
            Assert.Contains("Binding Generation Summary", allMessages);
            Assert.Contains("bound", allMessages);
        }
        finally
        {
            Directory.Delete(outputDir, true);
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void RecordUnsupportedCommentDrop_FlowsOntoReport_DedupedAndSorted()
    {
        // Finding 53: every // Unsupported: comment-drop emitted by UnsupportedCommentEmitter is
        // recorded on the ambient session and surfaces on the report (the SWIFTBIND025 channel),
        // deduplicated by comment text and emitted in Ordinal order.
        var moduleDecl = CreateModuleDecl();
        ReportCollector.Start(moduleDecl);

        var writer = new CSharpWriter(new StringWriter());
        UnsupportedCommentEmitter.EmitTypeSkipped(writer, "Widget", SkipReason.UnsupportedType);
        UnsupportedCommentEmitter.EmitMemberSkipped(writer, "Fetch", BindingItemKind.Method, SkipReason.UnsupportedExistential);
        // Re-emitting the identical type-skip must not produce a second entry.
        UnsupportedCommentEmitter.EmitTypeSkipped(writer, "Widget", SkipReason.UnsupportedType);

        var report = ReportCollector.Complete();
        Assert.NotNull(report);
        Assert.Equal(2, report!.UnsupportedCommentDrops.Count);
        // Entries are the comment text minus the leading "// " (so they begin with "Unsupported:").
        Assert.All(report.UnsupportedCommentDrops, d => Assert.StartsWith("Unsupported:", d));
        Assert.Contains(report.UnsupportedCommentDrops, d => d.Contains("type 'Widget'"));
        Assert.Contains(report.UnsupportedCommentDrops, d => d.Contains("'Fetch'"));
        // Sorted Ordinal.
        var sorted = report.UnsupportedCommentDrops.OrderBy(d => d, StringComparer.Ordinal).ToList();
        Assert.Equal(sorted, report.UnsupportedCommentDrops);

        ReportCollector.Reset();
    }

    [Fact]
    public void RecordObjectDegradation_FlowsOntoReport_DedupedAndSorted()
    {
        // Finding 53: a Swift type that degraded to bare `object` (no [UnsupportedSwiftType] marker)
        // is recorded once per distinct type on the ambient session, surfacing the SWIFTBIND026 channel.
        var moduleDecl = CreateModuleDecl();
        ReportCollector.Start(moduleDecl);

        ReportCollector.RecordObjectDegradation("any Shape");
        ReportCollector.RecordObjectDegradation("any AttributeKind");
        ReportCollector.RecordObjectDegradation("any Shape"); // duplicate — dedup
        ReportCollector.RecordObjectDegradation(""); // ignored

        var report = ReportCollector.Complete();
        Assert.NotNull(report);
        Assert.Equal(new[] { "any AttributeKind", "any Shape" }, report!.ObjectDegradations);

        ReportCollector.Reset();
    }

    [Fact]
    public void RecordUnsupportedCommentDrop_SameMemberNameDifferentTypes_RecordedDistinctly()
    {
        // Finding 53 (Codex Low): the SWIFTBIND025 dedup key is the comment text. A member's simple
        // name is not unique across types, so two distinct types each dropping a same-named member for
        // the same reason must produce TWO entries — collapsing them would under-count drops in a
        // diagnostic whose whole purpose is "never silent". The qualified Type.member name keeps them apart.
        var moduleDecl = CreateModuleDecl();
        ReportCollector.Start(moduleDecl);

        var writer = new CSharpWriter(new StringWriter());
        var typeA = new BaseDecl { Name = "Alpha", ParentDecl = null, ModuleDecl = null };
        var typeB = new BaseDecl { Name = "Beta", ParentDecl = null, ModuleDecl = null };
        UnsupportedCommentEmitter.EmitMemberSkipped(writer, "configure", BindingItemKind.Method, SkipReason.UnsupportedSignature, containingDecl: typeA);
        UnsupportedCommentEmitter.EmitMemberSkipped(writer, "configure", BindingItemKind.Method, SkipReason.UnsupportedSignature, containingDecl: typeB);
        // Same type + same member emitted twice still dedups to one entry.
        UnsupportedCommentEmitter.EmitMemberSkipped(writer, "configure", BindingItemKind.Method, SkipReason.UnsupportedSignature, containingDecl: typeA);

        var report = ReportCollector.Complete();
        Assert.NotNull(report);
        Assert.Equal(2, report!.UnsupportedCommentDrops.Count);
        Assert.Contains(report.UnsupportedCommentDrops, d => d.Contains("'Alpha.configure'"));
        Assert.Contains(report.UnsupportedCommentDrops, d => d.Contains("'Beta.configure'"));

        ReportCollector.Reset();
    }

    [Fact]
    public void RecordDegradations_OutsideActiveSession_AreNoOps()
    {
        // No ambient session: both Finding 53 recorders must be silent no-ops (no throw), and a
        // subsequent clean session must not inherit anything.
        ReportCollector.RecordUnsupportedCommentDrop("Unsupported: stray");
        ReportCollector.RecordObjectDegradation("any Stray");

        var moduleDecl = CreateModuleDecl();
        ReportCollector.Start(moduleDecl);
        var report = ReportCollector.Complete();

        Assert.NotNull(report);
        Assert.Empty(report!.UnsupportedCommentDrops);
        Assert.Empty(report.ObjectDegradations);

        ReportCollector.Reset();
    }

    [Fact]
    public void Complete_RecoveredMember_StampsRecoveredByAndClassifiesRecovered()
    {
        // A skipped open-generic property whose typed surface was recovered by CSM concrete
        // specializations: RecordMemberRecovered accumulates the closed projections, and Complete()
        // joins them onto the matching skip row by (ContainingType, Name). The row keeps its skip
        // reason but carries RecoveredBy (sorted, deduped) and reclassifies to Recovered.
        var moduleDecl = TestModelFactory.CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];
        var items = TestModelFactory.CreateProperty("items", parent: classDecl);

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordMemberSkipped(items, SkipReason.AnyTypeFallback, "open-generic collection projected to AnyType");
        ReportCollector.RecordMemberRecovered(items, "Loader<Song>.Items");
        ReportCollector.RecordMemberRecovered(items, "Loader<Album>.Items");
        ReportCollector.RecordMemberRecovered(items, "Loader<Song>.Items"); // dedup

        var report = ReportCollector.Complete();
        Assert.NotNull(report);
        var row = Assert.Single(report!.SkippedItems);
        Assert.NotNull(row.RecoveredBy);
        Assert.Equal(new[] { "Loader<Album>.Items", "Loader<Song>.Items" }, row.RecoveredBy);
        // Reason is unchanged (the base member really was AnyType-skipped) but disposition softens.
        Assert.Equal(SkipReason.AnyTypeFallback, row.Reason);
        Assert.Equal(SkipDisposition.Recovered, SkipDispositionClassifier.Classify(row));

        ReportCollector.Reset();
    }

    [Fact]
    public void Complete_SkippedMemberWithNoProjection_StaysPlainSkip()
    {
        // The control: an AnyType skip with NO recorded recovery keeps RecoveredBy null and stays a
        // plain KnownLimitation. This preserves the reader-facing invariant — "a row that says skipped
        // with no annotation really is unreachable" — so only genuinely-recovered rows soften.
        var moduleDecl = TestModelFactory.CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];
        var items = TestModelFactory.CreateProperty("items", parent: classDecl);

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordMemberSkipped(items, SkipReason.AnyTypeFallback, "open-generic collection projected to AnyType");

        var report = ReportCollector.Complete();
        Assert.NotNull(report);
        var row = Assert.Single(report!.SkippedItems);
        Assert.Null(row.RecoveredBy);
        Assert.Equal(SkipDisposition.KnownLimitation, SkipDispositionClassifier.Classify(row));

        ReportCollector.Reset();
    }

    [Fact]
    public void RecordMemberRecovered_OutsideSession_IsNoOp()
    {
        // No active session: the recovery record is silently dropped, and a subsequent clean session
        // must not inherit it (mirrors the other ambient recorders).
        ReportCollector.Reset();
        var strayModule = TestModelFactory.CreateModuleDecl();
        var strayClass = (ClassDecl)strayModule.Types[0];
        ReportCollector.RecordMemberRecovered(TestModelFactory.CreateProperty("items", parent: strayClass), "Loader<Song>.Items");

        var moduleDecl = TestModelFactory.CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];
        var items = TestModelFactory.CreateProperty("items", parent: classDecl);
        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordMemberSkipped(items, SkipReason.AnyTypeFallback, "open-generic");

        var report = ReportCollector.Complete();
        Assert.NotNull(report);
        Assert.Null(Assert.Single(report!.SkippedItems).RecoveredBy);

        ReportCollector.Reset();
    }

    private static ModuleDecl CreateModuleDecl()
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl> { CreateMethod("TopLevel") },
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var classDecl = new ClassDecl
        {
            Name = "Loader",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
            MangledName = "$s10TestModule6LoaderCN",
            Properties = new List<PropertyDecl> { CreateProperty("State", moduleDecl) },
            Methods = new List<MethodDecl> { CreateMethod("Fetch", moduleDecl) },
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var nestedStruct = new StructDecl
        {
            Name = "Payload",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Loader.Payload"),
            MangledName = "$s10TestModule6LoaderV7PayloadV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl> { CreateMethod("Read", classDecl) },
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule6LoaderV7PayloadVMa",
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl
        };

        classDecl.Types.Add(nestedStruct);
        moduleDecl.Types.Add(classDecl);

        var protocolDecl = new ProtocolDecl
        {
            Name = "IThing",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.IThing"),
            MangledName = "$s10TestModule6IThingP",
            Properties = new List<PropertyDecl> { CreateProperty("Value", moduleDecl) },
            Methods = new List<MethodDecl> { CreateMethod("DoWork", moduleDecl) },
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        // ProtocolDecl : TypeDecl, so the parser's OfType<TypeDecl>() puts protocols
        // in both moduleDecl.Types and moduleDecl.Protocols.
        moduleDecl.Types.Add(protocolDecl);
        moduleDecl.Protocols.Add(protocolDecl);

        return moduleDecl;
    }

    private static MethodDecl CreateMethod(string name, BaseDecl? parent = null) => new()
    {
        Name = name,
        MangledName = $"$s4Test{name.Length}{name}yyF",
        MethodType = MethodType.Instance,
        IsConstructor = false,
        CSSignature = new List<ArgumentDecl>
        {
            new()
            {
                SwiftTypeSpec = TupleTypeSpec.Empty,
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parent,
                ModuleDecl = parent?.ModuleDecl
            }
        },
        Throws = false,
        IsAsync = false,
        GenericParameters = new List<GenericArgumentDecl>(),
        IsSynthesizedAccessor = false,
        ParentDecl = parent,
        ModuleDecl = parent?.ModuleDecl
    };

    private static PropertyDecl CreateProperty(string name, ModuleDecl moduleDecl) => new()
    {
        Name = name,
        SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
        HasStorage = false,
        IsStatic = false,
        Accessors = new List<AccessorDecl>(),
        ParentDecl = moduleDecl,
        ModuleDecl = moduleDecl
    };

    // ==================== DeclId on report rows ====================

    [Fact]
    public void RecordTypeSkipped_StampsTheTypesDeclIdOnTheRow()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordTypeSkipped(classDecl, SkipReason.UnsupportedType, "test");

        var report = ReportCollector.Complete();
        // The type's own row; its members are separately accounted for against the suppression.
        var row = Assert.Single(report!.SkippedItems, i => i.Kind == BindingItemKind.Type);
        Assert.Equal(DeclIdFactory.ForType(classDecl).Canonical, row.DeclId);

        ReportCollector.Reset();
    }

    [Fact]
    public void RecordMemberSkipped_StampsTheMembersDeclIdOnTheRow()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordMemberSkipped(
            BindingItemKind.Method, "Fetch", classDecl, SkipReason.UnsupportedExistential, "test");

        var report = ReportCollector.Complete();
        var row = Assert.Single(report!.SkippedItems);
        Assert.Equal(
            DeclIdFactory.ForMember(BindingItemKind.Method, "Fetch", classDecl).Canonical,
            row.DeclId);

        ReportCollector.Reset();
    }

    [Fact]
    public void RecordUnsupportedCommentDrop_ProjectsDescriptionsWithTheirDeclIds()
    {
        // The drop list has always carried descriptions; the detail list pairs each one with the
        // declaration it came from so a "why is this member missing?" question is answerable.
        var moduleDecl = CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];
        var declId = DeclIdFactory.ForType(classDecl);

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordUnsupportedCommentDrop("Loader: unsupported type", declId);
        ReportCollector.RecordUnsupportedCommentDrop("Loader: unsupported type", declId); // dedups
        ReportCollector.RecordUnsupportedCommentDrop("anonymous drop");                   // no owner

        var report = ReportCollector.Complete();
        Assert.Equal(2, report!.UnsupportedCommentDropDetails.Count);

        var owned = report.UnsupportedCommentDropDetails
            .Single(d => d.Description == "Loader: unsupported type");
        Assert.Equal(declId.Canonical, owned.DeclId);

        var unowned = report.UnsupportedCommentDropDetails
            .Single(d => d.Description == "anonymous drop");
        Assert.Null(unowned.DeclId);

        // The pre-existing description list is unchanged in content.
        Assert.Equal(
            report.UnsupportedCommentDropDetails.Select(d => d.Description),
            report.UnsupportedCommentDrops);

        ReportCollector.Reset();
    }

    // ==================== Members of a suppressed parent type ====================
    //
    // Whole-type suppression short-circuits before the member loop, so the members of a suppressed
    // type used to be counted in TotalMembers yet recorded as neither emitted nor skipped — silently
    // absent from every roll-up computed off the skip list, and most absent exactly where suppression
    // removes the most surface. The fixture below is that shape: a public parent type whose entire
    // member surface, including the members of the types nested inside it, is reachable only through
    // the parent's declaration.

    [Fact]
    public void SuppressedParentType_AccountsForEveryMemberCountedInTheTotals()
    {
        var moduleDecl = CreateSuppressedParentModule();
        var parent = moduleDecl.Types[0];

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordTypeSkipped(parent, SkipReason.SwiftUIView, "Type conforms to SwiftUI.View.");

        var report = ReportCollector.Complete();
        Assert.NotNull(report);

        // Nothing emitted, so the whole counted member surface has to appear on the skip side —
        // this is the arithmetic that used to be short by every unenumerated member.
        Assert.Equal(0, report!.EmittedMembers);
        Assert.Equal(report.TotalMembers, report.EmittedMembers + report.SkippedMembers);

        // Every one of them is attributed to the suppression rather than left reason-less.
        Assert.Equal(
            report.TotalMembers,
            report.SkippedItems.Count(i => i.Reason == SkipReason.ParentTypeSuppressed));

        // Including the members that only exist under the nested types, which the suppressed
        // parent's declaration is the only route to.
        Assert.Contains(
            report.SkippedItems,
            i => i.Reason == SkipReason.ParentTypeSuppressed && i.ContainingType == "ToastModule.Toast.Style");

        ReportCollector.Reset();
    }

    [Fact]
    public void SuppressedPublicParent_MembersAreStructuralLosses_NotReviewItems()
    {
        // A public type suppressed by design still cost the consumer its members, so they count as
        // lost surface — but the cause is attributed and lives on the parent's own row, so none of
        // them may land in the tier that means "nobody can explain this".
        var moduleDecl = CreateSuppressedParentModule();
        var parent = moduleDecl.Types[0];

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordTypeSkipped(parent, SkipReason.SwiftUIView, "Type conforms to SwiftUI.View.");
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        var memberRows = report!.SkippedItems.Where(i => i.Reason == SkipReason.ParentTypeSuppressed).ToList();
        Assert.NotEmpty(memberRows);
        Assert.All(memberRows, row =>
            Assert.Equal(SkipDisposition.ExpectedStructural, SkipDispositionClassifier.Classify(row)));

        var triage = SkipTriageBuilder.Build(report.SkippedItems);
        Assert.Equal(0, triage.ReviewCount);
        // Public surface that a consumer could have seen and didn't get now includes them.
        Assert.Equal(triage.Total, triage.PublicSurfaceLost);
    }

    [Fact]
    public void SuppressedNeverPublicParent_MembersAreNotCountedAsLostSurface()
    {
        // The mirror case: members of a type that was never public surface (module-internal, @_spi,
        // underscore-internal) were never visible to a consumer, so accounting for them must not
        // start reporting a public-surface loss that never existed.
        var moduleDecl = CreateSuppressedParentModule();
        var parent = moduleDecl.Types[0];

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordTypeSkipped(parent, SkipReason.ModuleInternal, "@_spi type suppressed from bindings.");
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        var memberRows = report!.SkippedItems.Where(i => i.Reason == SkipReason.ParentTypeSuppressed).ToList();
        Assert.NotEmpty(memberRows);
        Assert.All(memberRows, row =>
            Assert.Equal(SkipDisposition.ExpectedNonPublic, SkipDispositionClassifier.Classify(row)));

        var triage = SkipTriageBuilder.Build(report.SkippedItems);
        Assert.Equal(0, triage.ReviewCount);
        Assert.Equal(0, triage.PublicSurfaceLost);
    }

    [Fact]
    public void SuppressedParentType_DoesNotRestateMembersThatWereAlreadyRecorded()
    {
        // Some members of a suppressed type do get recorded on their own — the gate that fired for
        // them ran before the type-level suppression. Those keep their specific reason, and only the
        // ones nothing claimed are accounted for against the parent, so the arithmetic still closes
        // with no member counted twice.
        var moduleDecl = CreateSuppressedParentModule();
        var parent = moduleDecl.Types[0];
        var parentMethod = parent.Methods[0];

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordMemberSkipped(parentMethod, SkipReason.UnsupportedSignature, "already attributed");
        ReportCollector.RecordTypeSkipped(parent, SkipReason.SwiftUIView, "Type conforms to SwiftUI.View.");
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        Assert.Equal(report!.TotalMembers, report.EmittedMembers + report.SkippedMembers);
        Assert.Single(report.SkippedItems, i => i.Reason == SkipReason.UnsupportedSignature);
        Assert.DoesNotContain(
            report.SkippedItems,
            i => i.Reason == SkipReason.ParentTypeSuppressed && i.Name == parentMethod.Name);
    }

    [Fact]
    public void SuppressedParentType_DoesNotRestateMembersThatWereEmitted()
    {
        // A member with a real C# surface is not lost, whatever the type-level bookkeeping says.
        var moduleDecl = CreateSuppressedParentModule();
        var parent = moduleDecl.Types[0];
        var parentProperty = parent.Properties[0];

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordMemberEmitted(parentProperty);
        ReportCollector.RecordTypeSkipped(parent, SkipReason.SwiftUIView, "Type conforms to SwiftUI.View.");
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        Assert.Equal(1, report!.EmittedMembers);
        Assert.Equal(report.TotalMembers, report.EmittedMembers + report.SkippedMembers);
        Assert.DoesNotContain(
            report.SkippedItems,
            i => i.Reason == SkipReason.ParentTypeSuppressed && i.Name == parentProperty.Name);
    }

    [Fact]
    public void EmittedTypeWithUnrecordedMembers_ProducesNoParentSuppressedRows()
    {
        // Scope guard: this accounts for members lost to a SUPPRESSED type. A member that went
        // unrecorded under a type that emitted is a different question entirely, and attributing it
        // to a parent suppression that never happened would be a lie.
        var moduleDecl = CreateSuppressedParentModule();
        var parent = moduleDecl.Types[0];
        var nested = parent.Types[0];

        ReportCollector.Start(moduleDecl);
        ReportCollector.RecordTypeEmitted(parent);
        ReportCollector.RecordTypeSkipped(nested, SkipReason.UnsupportedType, "nested only");
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        Assert.DoesNotContain(
            report!.SkippedItems,
            i => i.Reason == SkipReason.ParentTypeSuppressed && i.ContainingType == "ToastModule.Toast");
        Assert.Contains(
            report.SkippedItems,
            i => i.Reason == SkipReason.ParentTypeSuppressed && i.ContainingType == "ToastModule.Toast.Style");
    }

    /// <summary>
    /// A public parent type carrying member surface of its own plus two nested types that carry
    /// theirs — the shape a SwiftUI <c>View</c> with nested configuration enums has, and the shape
    /// whole-type suppression makes wholly unreachable in one step. The module carries no top-level
    /// members so <c>TotalMembers</c> is exactly this tree's member surface.
    /// </summary>
    private static ModuleDecl CreateSuppressedParentModule()
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "ToastModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var parent = NewStruct("Toast", "ToastModule.Toast", moduleDecl, moduleDecl);
        parent.Methods.Add(CreateMethod("init", parent));
        parent.Properties.Add(CreateProperty("body", moduleDecl));
        parent.Properties[0].ParentDecl = parent;

        var style = NewStruct("Style", "ToastModule.Toast.Style", parent, moduleDecl);
        style.Methods.Add(CreateMethod("hash", style));
        style.Methods.Add(CreateMethod("equals", style));

        var display = NewStruct("Display", "ToastModule.Toast.Display", parent, moduleDecl);
        display.Methods.Add(CreateMethod("hash", display));

        parent.Types.Add(style);
        parent.Types.Add(display);
        moduleDecl.Types.Add(parent);
        return moduleDecl;
    }

    private static StructDecl NewStruct(string name, string qualified, BaseDecl parent, ModuleDecl moduleDecl) => new()
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(qualified),
        MangledName = $"$s{qualified.Length}{name}V",
        MetadataAccessor = $"$s{qualified.Length}{name}VMa",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Subscripts = new List<SubscriptDecl>(),
        GenericParameters = new List<GenericArgumentDecl>(),
        Conformances = new List<TypeConformance>(),
        IsFrozen = true,
        ParentDecl = parent,
        ModuleDecl = moduleDecl,
    };

    /// <summary>
    /// Simple ILogger that captures log messages for assertions.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
