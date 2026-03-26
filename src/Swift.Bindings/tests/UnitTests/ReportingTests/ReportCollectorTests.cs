// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

[Collection("ReportCollector")]
public class ReportCollectorTests
{
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
        Assert.Equal(1, report.SkippedMembers);
        Assert.Equal(0, report.SynthesizedMembers);
        Assert.Equal(2, report.SkippedItems.Count);

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
        Visibility = Visibility.Public,
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
