// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for SwiftUIBridgeEmitter template and functional bridge generation.
/// </summary>
[Collection("ReportCollector")]
public class SwiftUIBridgeEmitterTests : IDisposable
{
    private readonly string _tempDir;

    public SwiftUIBridgeEmitterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SwiftUIBridgeTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    #region File Creation

    [Fact]
    public void EmitBridgeFiles_CreatesSwiftFile_InOutputDir()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("TestView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        Assert.True(File.Exists(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift")));
    }

    [Fact]
    public void EmitBridgeFiles_CreatesCSharpFile_InOutputDir()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("TestView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        Assert.True(File.Exists(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs")));
    }

    [Fact]
    public void EmitBridgeFiles_NoFiles_WhenNoViews()
    {
        var views = new List<TypeDecl>();

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public void BridgeOutput_InSameDirectory_AsMainBindings()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("TestView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftPath = Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift");
        var csPath = Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs");
        Assert.True(File.Exists(swiftPath));
        Assert.True(File.Exists(csPath));
    }

    [Fact]
    public void BridgeOutput_NotCreated_WhenNoSimpleViews()
    {
        // Only unsupported generic views — still creates files (with templates), not empty
        var views = new List<TypeDecl> { CreateGenericViewStruct("GenericView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        // Files created but contain only templates
        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("BRIDGE TEMPLATE", swiftContent);
        Assert.DoesNotContain("@_cdecl", swiftContent.Replace("// @_cdecl", ""));
    }

    #endregion

    #region Template Generation (Unsupported Views)

    [Fact]
    public void EmitBridgeFiles_TemplateIsCompileSafe_ForUnsupportedViews()
    {
        // Only unsupported views → all templates, no functional code
        var views = new List<TypeDecl>
        {
            CreateGenericViewStruct("View1"),
            CreateGenericViewStruct("View2"),
        };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));

        // Swift: no @_cdecl outside of comments
        var swiftLines = swiftContent.Split('\n');
        foreach (var line in swiftLines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.Contains("@_cdecl"))
                Assert.True(trimmed.StartsWith("//"), $"Found uncommented @_cdecl: {line}");
        }

        // C#: all content lines start with // (no executable code)
        var csLines = csContent.Split('\n');
        foreach (var line in csLines)
        {
            var trimmed = line.Trim();
            if (!string.IsNullOrEmpty(trimmed))
                Assert.True(trimmed.StartsWith("//"), $"Found uncommented code in C# template: {line}");
        }
    }

    [Fact]
    public void EmitBridgeFiles_ListsAllDetectedViews()
    {
        var views = new List<TypeDecl>
        {
            CreateSimpleViewStruct("ViewA"),
            CreateSimpleViewStruct("ViewB"),
            CreateSimpleViewStruct("ViewC"),
        };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("ViewA", swiftContent);
        Assert.Contains("ViewB", swiftContent);
        Assert.Contains("ViewC", swiftContent);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("ViewA", csContent);
        Assert.Contains("ViewB", csContent);
        Assert.Contains("ViewC", csContent);
    }

    [Fact]
    public void EmitBridgeFiles_IncludesModuleImport()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("TestView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("import TestModule", swiftContent);
        Assert.Contains("import SwiftUI", swiftContent);
        Assert.Contains("import UIKit", swiftContent);
    }

    #endregion

    #region Functional Bridge Generation (Phase 3)

    [Fact]
    public void EmitSimpleViewBridge_GeneratesSessionClass()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("final class SBW_TestModule_TestView_Session", swiftContent);
        Assert.Contains("UIHostingController<TestView>", swiftContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_GeneratesCreateWithCdecl()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_TestModule_TestView_Create\")", swiftContent);
        Assert.Contains("public func SBW_TestModule_TestView_Create(", swiftContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_GeneratesGetViewController()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_TestModule_TestView_GetViewController\")", swiftContent);
        Assert.Contains("hostingController", swiftContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_GeneratesFreeWithHandleTracking()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_TestModule_TestView_Free\")", swiftContent);
        Assert.Contains("SBW_TestModule_TestView_liveHandles", swiftContent);
        Assert.Contains("Unmanaged<SBW_TestModule_TestView_Session>.fromOpaque(handle).release()", swiftContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_GeneratesOnMainThreadHelper()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("SBW_onMainThread", swiftContent);
        Assert.Contains("DispatchQueue.main.sync", swiftContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_MapsVoidClosureToFunctionPointer()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@convention(c)", swiftContent);
        Assert.Contains("UnsafeMutableRawPointer?", swiftContent);
        Assert.Contains("retryActionCallback", swiftContent);
        Assert.Contains("retryActionUserData", swiftContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_CSharp_GeneratesNativeMethods()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("TestViewBridgeNativeMethods", csContent);
        Assert.Contains("DllImport", csContent);
        Assert.Contains("SBW_TestModule_TestView_Create", csContent);
        Assert.Contains("SBW_TestModule_TestView_GetViewController", csContent);
        Assert.Contains("SBW_TestModule_TestView_Free", csContent);
        Assert.Contains("CallConvCdecl", csContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_CSharp_GeneratesIDisposable()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("TestViewSession : IDisposable", csContent);
        Assert.Contains("public void Dispose()", csContent);
        Assert.Contains("_disposed = true", csContent);
        Assert.Contains("IntPtr.Zero", csContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_CSharp_ThrowsObjectDisposedAfterDispose()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("ObjectDisposedException", csContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_FallsBackToTemplate_ForUnsupportedParams()
    {
        // View with a non-primitive, non-closure parameter → template
        var views = new List<TypeDecl> { CreateViewWithUnsupportedParam("ComplexView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("BRIDGE TEMPLATE: ComplexView", swiftContent);
        // Should NOT have functional code for this view
        Assert.DoesNotContain("SBW_TestModule_ComplexView_Session", swiftContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_GeneratesSBWNamingConvention()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("MyView", "action") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("SBW_TestModule_MyView_Create", swiftContent);
        Assert.Contains("SBW_TestModule_MyView_GetViewController", swiftContent);
        Assert.Contains("SBW_TestModule_MyView_Free", swiftContent);
        Assert.Contains("SBW_TestModule_MyView_Session", swiftContent);
        Assert.Contains("SBW_TestModule_MyView_liveHandles", swiftContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_ReportShowsGenerated()
    {
        ReportCollector.Reset();
        var moduleDecl = CreateModuleDecl();
        ReportCollector.Start(moduleDecl);

        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var report = ReportCollector.Complete()!;
        Assert.Single(report.BridgedViews);
        Assert.Equal("Generated", report.BridgedViews[0].BridgeStatus);
        Assert.Equal("Simple", report.BridgedViews[0].InitClassification);

        ReportCollector.Reset();
    }

    #endregion

    #region InitAnalyzer

    [Fact]
    public void InitAnalyzer_VoidClosure_IsSupported()
    {
        var ctor = CreateConstructorWithVoidClosure("retryAction");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.VoidClosure, result[0].Kind);
        Assert.Equal("retryAction", result[0].Name);
        Assert.True(result[0].HasUserData);
    }

    [Fact]
    public void InitAnalyzer_PrimitiveParams_AreSupported()
    {
        var ctor = CreateConstructorWithPrimitive("count", "Swift.Int");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.Primitive, result[0].Kind);
        Assert.Equal("nint", result[0].CSharpPInvokeType);
    }

    [Fact]
    public void InitAnalyzer_GenericParam_ForcesTemplate()
    {
        var ctor = CreateConstructorWithGenericParam("item");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.Null(result); // Null means unsupported → fallback to template
    }

    [Fact]
    public void InitAnalyzer_ExistentialParam_ForcesTemplate()
    {
        var ctor = CreateConstructorWithExistentialParam("source");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.Null(result); // Null means unsupported → fallback to template
    }

    [Fact]
    public void InitAnalyzer_Bool_MapsThroughConversion()
    {
        var ctor = CreateConstructorWithPrimitive("enabled", "Swift.Bool");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("int", result[0].CSharpPInvokeType);
        Assert.Equal("Int32", result[0].SwiftAbiType);
        Assert.NotNull(result[0].SwiftConversion);
    }

    [Fact]
    public void InitAnalyzer_String_HasLength()
    {
        var ctor = CreateConstructorWithPrimitive("title", "Swift.String");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.String, result[0].Kind);
        Assert.True(result[0].HasLength);
    }

    #endregion

    #region Init Classification

    [Fact]
    public void AnalyzeView_Simple_NoParams()
    {
        var view = CreateSimpleViewStruct("SimpleView");

        var info = SwiftUIBridgeEmitter.AnalyzeView(view, "TestModule");

        Assert.Equal(ViewInitClassification.Simple, info.Classification);
        Assert.Null(info.UnsupportedReason);
    }

    [Fact]
    public void AnalyzeView_Unsupported_GenericType()
    {
        var view = CreateGenericViewStruct("GenericView");

        var info = SwiftUIBridgeEmitter.AnalyzeView(view, "TestModule");

        Assert.Equal(ViewInitClassification.Unsupported, info.Classification);
        Assert.Contains("Generic", info.UnsupportedReason);
    }

    #endregion

    #region Async Bridge Generation (Phase 4)

    [Fact]
    public void AnalyzeView_AsyncDependency_BlinkIDUXView()
    {
        var view = CreateSimpleViewStruct("BlinkIDUXView");

        var info = SwiftUIBridgeEmitter.AnalyzeView(view, "BlinkIDUX");

        Assert.Equal(ViewInitClassification.AsyncDependency, info.Classification);
        Assert.Null(info.UnsupportedReason);
    }

    [Fact]
    public void GetAsyncPattern_ReturnsPattern_ForBlinkIDUXView()
    {
        var pattern = SwiftUIBridgeEmitter.GetAsyncPattern("BlinkIDUXView", "BlinkIDUX");

        Assert.NotNull(pattern);
        Assert.Equal("BlinkIDUXView", pattern.ViewName);
        Assert.Equal("BlinkIDUXSession", pattern.SessionClassName);
        Assert.True(pattern.HasResultCallback);
    }

    [Fact]
    public void GetAsyncPattern_ReturnsNull_ForUnknownView()
    {
        var pattern = SwiftUIBridgeEmitter.GetAsyncPattern("SomeRandomView", "TestModule");
        Assert.Null(pattern);
    }

    [Fact]
    public void GetAsyncPattern_ReturnsNull_WhenModuleMismatch()
    {
        // BlinkIDUXView exists but not in "OtherModule"
        var pattern = SwiftUIBridgeEmitter.GetAsyncPattern("BlinkIDUXView", "OtherModule");
        Assert.Null(pattern);
    }

    [Fact]
    public void EmitAsyncViewBridge_Swift_GeneratesAsyncCreate()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("BlinkIDUXView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.BlinkIDUX", "BlinkIDUX", views,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.BlinkIDUX.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_BlinkIDUX_BlinkIDUXView_Create\")", swiftContent);
        Assert.Contains("Task { @MainActor in", swiftContent);
        Assert.Contains("guard let onReady = onReady else { return }", swiftContent);
    }

    [Fact]
    public void EmitAsyncViewBridge_Swift_GeneratesSessionWithFields()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("BlinkIDUXView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.BlinkIDUX", "BlinkIDUX", views,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.BlinkIDUX.SwiftUIBridge.swift"));
        Assert.Contains("final class SBW_BlinkIDUX_BlinkIDUXView_Session", swiftContent);
        Assert.Contains("let sdk: BlinkIDSdk", swiftContent);
        Assert.Contains("let analyzer: BlinkIDAnalyzer", swiftContent);
        Assert.Contains("let model: BlinkIDUXModel", swiftContent);
        Assert.Contains("UIHostingController<BlinkIDUXView>", swiftContent);
    }

    [Fact]
    public void EmitAsyncViewBridge_Swift_GeneratesResultMonitor()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("BlinkIDUXView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.BlinkIDUX", "BlinkIDUX", views,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.BlinkIDUX.SwiftUIBridge.swift"));
        Assert.Contains("startResultMonitor", swiftContent);
        Assert.Contains("cancelResultMonitor", swiftContent);
        Assert.Contains("resultTask?.cancel()", swiftContent);
    }

    [Fact]
    public void EmitAsyncViewBridge_Swift_GeneratesErrorCallback()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("BlinkIDUXView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.BlinkIDUX", "BlinkIDUX", views,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.BlinkIDUX.SwiftUIBridge.swift"));
        Assert.Contains("onError", swiftContent);
        Assert.Contains("catch", swiftContent);
        Assert.Contains("utf8", swiftContent);
    }

    [Fact]
    public void EmitAsyncViewBridge_Swift_GeneratesGetViewControllerAndFree()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("BlinkIDUXView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.BlinkIDUX", "BlinkIDUX", views,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.BlinkIDUX.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_BlinkIDUX_BlinkIDUXView_GetViewController\")", swiftContent);
        Assert.Contains("@_cdecl(\"SBW_BlinkIDUX_BlinkIDUXView_Free\")", swiftContent);
        Assert.Contains("SBW_BlinkIDUX_BlinkIDUXView_liveHandles", swiftContent);
    }

    [Fact]
    public void EmitAsyncViewBridge_CSharp_GeneratesNativeMethods()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("BlinkIDUXView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.BlinkIDUX", "BlinkIDUX", views,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.BlinkIDUX.SwiftUIBridge.cs"));
        Assert.Contains("BlinkIDUXViewBridgeNativeMethods", csContent);
        Assert.Contains("SBW_BlinkIDUX_BlinkIDUXView_Create", csContent);
        Assert.Contains("SBW_BlinkIDUX_BlinkIDUXView_GetViewController", csContent);
        Assert.Contains("SBW_BlinkIDUX_BlinkIDUXView_Free", csContent);
        // Async create returns void (not IntPtr)
        Assert.Contains("internal static extern void Create(", csContent);
    }

    [Fact]
    public void EmitAsyncViewBridge_CSharp_CreateHasCallbackParams()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("BlinkIDUXView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.BlinkIDUX", "BlinkIDUX", views,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.BlinkIDUX.SwiftUIBridge.cs"));
        Assert.Contains("IntPtr onReady", csContent);
        Assert.Contains("IntPtr onError", csContent);
        Assert.Contains("IntPtr onResult", csContent);
        Assert.Contains("IntPtr userData", csContent);
    }

    [Fact]
    public void EmitAsyncViewBridge_CSharp_GeneratesIDisposable()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("BlinkIDUXView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.BlinkIDUX", "BlinkIDUX", views,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.BlinkIDUX.SwiftUIBridge.cs"));
        Assert.Contains("BlinkIDUXViewSession : IDisposable", csContent);
        Assert.Contains("ObjectDisposedException", csContent);
        Assert.Contains("public void Dispose()", csContent);
    }

    [Fact]
    public void EmitAsyncViewBridge_ReportShowsGenerated()
    {
        ReportCollector.Reset();
        var moduleDecl = CreateModuleDecl();
        ReportCollector.Start(moduleDecl);

        var views = new List<TypeDecl> { CreateSimpleViewStruct("BlinkIDUXView") };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.BlinkIDUX", "BlinkIDUX", views,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var report = ReportCollector.Complete()!;
        Assert.Single(report.BridgedViews);
        Assert.Equal("Generated", report.BridgedViews[0].BridgeStatus);
        Assert.Equal("AsyncDependency", report.BridgedViews[0].InitClassification);

        ReportCollector.Reset();
    }

    [Fact]
    public void EmitAsyncViewBridge_NonBlinkIDUXView_StaysAsTemplate()
    {
        // A non-BlinkIDUXView should NOT be matched by async pattern
        var view = CreateSimpleViewStruct("SomeOtherView");
        var info = SwiftUIBridgeEmitter.AnalyzeView(view, "TestModule");

        Assert.Equal(ViewInitClassification.Simple, info.Classification);
        Assert.False(SwiftUIBridgeEmitter.HasAsyncDependency(info));
    }

    #endregion

    #region Factory Generation

    [Fact]
    public void EmitSimpleViewBridge_CSharp_GeneratesCreateFactory()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("public static unsafe TestViewSession Create(", csContent);
        Assert.Contains("Action? retryAction", csContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_CSharp_CreateFactoryHasTrampoline()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("UnmanagedCallersOnly", csContent);
        Assert.Contains("RetryActionTrampoline", csContent);
        Assert.Contains("GCHandle.FromIntPtr", csContent);
        Assert.Contains("is Action action", csContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_CSharp_CreateFactoryDisposesHandles()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("_closureHandles", csContent);
        Assert.Contains("h.IsAllocated", csContent);
        Assert.Contains("h.Free()", csContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_CSharp_NoParamsFactory_NotUnsafe()
    {
        // View with no init params → factory doesn't need unsafe
        var views = new List<TypeDecl> { CreateSimpleViewStruct("PlainView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("public static PlainViewSession Create()", csContent);
        Assert.DoesNotContain("unsafe PlainViewSession", csContent);
    }

    [Fact]
    public void EmitAsyncViewBridge_CSharp_GeneratesCreateAsyncFactory()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("BlinkIDUXView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.BlinkIDUX", "BlinkIDUX", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.BlinkIDUX.SwiftUIBridge.cs"));
        Assert.Contains("public static async Task<BlinkIDUXViewSession> CreateAsync(", csContent);
        Assert.Contains("string licenseKey", csContent);
        Assert.Contains("bool showIntroductionAlert", csContent);
    }

    [Fact]
    public void EmitAsyncViewBridge_CSharp_CreateAsyncHasTrampolines()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("BlinkIDUXView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.BlinkIDUX", "BlinkIDUX", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.BlinkIDUX.SwiftUIBridge.cs"));
        Assert.Contains("OnReadyTrampoline", csContent);
        Assert.Contains("OnErrorTrampoline", csContent);
        Assert.Contains("OnResultTrampoline", csContent);
    }

    [Fact]
    public void EmitAsyncViewBridge_CSharp_CreateAsyncHasCreateState()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("BlinkIDUXView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.BlinkIDUX", "BlinkIDUX", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.BlinkIDUX.SwiftUIBridge.cs"));
        Assert.Contains("private sealed class CreateState", csContent);
        Assert.Contains("TaskCompletionSource<BlinkIDUXViewSession>", csContent);
        Assert.Contains("Action<int>? OnResult", csContent);
    }

    [Fact]
    public void EmitAsyncViewBridge_CSharp_CreateAsyncDisposesState()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("BlinkIDUXView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.BlinkIDUX", "BlinkIDUX", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.BlinkIDUX.SwiftUIBridge.cs"));
        Assert.Contains("_stateHandle", csContent);
        Assert.Contains("_stateHandle.IsAllocated", csContent);
        Assert.Contains("_stateHandle.Free()", csContent);
    }

    #endregion

    #region BoundEnum (Phase 1A)

    [Fact]
    public void InitAnalyzer_BoundEnum_IsSupported_WithTypeDatabase()
    {
        var typeDb = CreateEnumTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithNamedType("style", "TestModule.AlertStyle");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.BoundEnum, result[0].Kind);
        Assert.Equal("style", result[0].Name);
        Assert.Equal("Int32", result[0].SwiftAbiType);
        Assert.Equal("int", result[0].CSharpPInvokeType);
        Assert.Equal("AlertStyle", result[0].BridgeTypeName);
        Assert.Equal("Swift.TestModule.AlertStyle", result[0].CSharpTypeName);
    }

    [Fact]
    public void InitAnalyzer_BoundEnum_FallsBackToTemplate_WithoutTypeDatabase()
    {
        // No TypeDatabase → unknown named type → template fallback
        var ctor = CreateConstructorWithNamedType("style", "TestModule.AlertStyle");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.Null(result);
    }

    [Fact]
    public void InitAnalyzer_BoundEnum_FallsBackToTemplate_WhenTypeNotInDatabase()
    {
        var typeDb = CreateEnumTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithNamedType("config", "TestModule.UnknownType");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.Null(result);
    }

    [Fact]
    public void InitAnalyzer_BoundEnum_FallsBackToTemplate_ForStringRawValueEnum()
    {
        // String raw-value enums can't cross ABI as integers
        var typeDb = CreateStringEnumTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithNamedType("level", "TestModule.LogLevel");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.Null(result);
    }

    [Fact]
    public void InitAnalyzer_BoundEnum_FallsBackToTemplate_ForNonRawRepresentableEnum()
    {
        // Enums without RawRepresentable conformance can't use rawValue init
        var typeDb = CreateNonRawEnumTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithNamedType("direction", "TestModule.Direction");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.Null(result);
    }

    [Fact]
    public void InitAnalyzer_BoundEnum_UsesCorrectAbiType_ForIntRawValue()
    {
        // Int raw value → nint ABI type (platform word size)
        var typeDb = CreateEnumTypeDatabaseWithRawType("Int");
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithNamedType("style", "TestModule.AlertStyle");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.NotNull(result);
        Assert.Equal("Int", result[0].SwiftAbiType);
        Assert.Equal("nint", result[0].CSharpPInvokeType);
    }

    [Fact]
    public void InitAnalyzer_BoundEnum_UsesCorrectAbiType_ForUInt8RawValue()
    {
        var typeDb = CreateEnumTypeDatabaseWithRawType("UInt8");
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithNamedType("style", "TestModule.AlertStyle");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.NotNull(result);
        Assert.Equal("UInt8", result[0].SwiftAbiType);
        Assert.Equal("byte", result[0].CSharpPInvokeType);
    }

    [Fact]
    public void EmitBoundEnum_Swift_PassesRawValueToCdecl()
    {
        var typeDb = CreateEnumTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithEnumInit("EnumView", "style", "TestModule.AlertStyle") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("_ style: Int32", swiftContent);
        Assert.Contains("AlertStyle(rawValue: style)!", swiftContent);
    }

    [Fact]
    public void EmitBoundEnum_Swift_GeneratesFunctionalBridge()
    {
        var typeDb = CreateEnumTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithEnumInit("EnumView", "style", "TestModule.AlertStyle") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_TestModule_EnumView_Create\")", swiftContent);
        Assert.Contains("SBW_TestModule_EnumView_Session", swiftContent);
        Assert.DoesNotContain("BRIDGE TEMPLATE", swiftContent);
    }

    [Fact]
    public void EmitBoundEnum_CSharp_UsesRawValue()
    {
        var typeDb = CreateEnumTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithEnumInit("EnumView", "style", "TestModule.AlertStyle") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("AlertStyle style", csContent);
        Assert.Contains("style.RawValue", csContent); // Enum class uses .RawValue, not (int) cast
        Assert.Contains("int style", csContent); // P/Invoke param
    }

    [Fact]
    public void EmitBoundEnum_CSharp_UsesMappedRawValueType_ForUInt8Enum()
    {
        var typeDb = CreateEnumTypeDatabaseWithRawType("UInt8");
        var views = new List<TypeDecl> { CreateViewWithEnumInit("EnumView", "style", "TestModule.AlertStyle") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("AlertStyle style", csContent);       // Factory param uses C# enum type
        Assert.Contains("style.RawValue", csContent);         // Uses .RawValue property
        Assert.Contains("byte style", csContent);             // P/Invoke param uses mapped type
    }

    [Fact]
    public void EmitBoundEnum_CSharp_GeneratesNativeMethodsAndSession()
    {
        var typeDb = CreateEnumTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithEnumInit("EnumView", "style", "TestModule.AlertStyle") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("EnumViewBridgeNativeMethods", csContent);
        Assert.Contains("EnumViewSession : IDisposable", csContent);
        Assert.Contains("DllImport", csContent);
    }

    #endregion

    #region OptionalWrapped (Phase 1A)

    [Fact]
    public void InitAnalyzer_OptionalPrimitive_IsSupported()
    {
        var ctor = CreateConstructorWithOptionalPrimitive("count", "Swift.Int");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.OptionalWrapped, result[0].Kind);
        Assert.Equal("count", result[0].Name);
        Assert.NotNull(result[0].InnerParameter);
        Assert.Equal(BridgeParameterKind.Primitive, result[0].InnerParameter!.Kind);
        Assert.Equal("Int", result[0].InnerParameter!.SwiftAbiType);
    }

    [Fact]
    public void InitAnalyzer_OptionalBool_IsSupported()
    {
        var ctor = CreateConstructorWithOptionalPrimitive("enabled", "Swift.Bool");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.OptionalWrapped, result[0].Kind);
        Assert.NotNull(result[0].InnerParameter);
        Assert.Equal(BridgeParameterKind.Primitive, result[0].InnerParameter!.Kind);
        Assert.NotNull(result[0].InnerParameter!.SwiftConversion);
    }

    [Fact]
    public void InitAnalyzer_OptionalEnum_IsSupported_WithTypeDatabase()
    {
        var typeDb = CreateEnumTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithOptionalType("style", "TestModule.AlertStyle");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.OptionalWrapped, result[0].Kind);
        Assert.NotNull(result[0].InnerParameter);
        Assert.Equal(BridgeParameterKind.BoundEnum, result[0].InnerParameter!.Kind);
        Assert.Equal("AlertStyle", result[0].InnerParameter!.BridgeTypeName);
    }

    [Fact]
    public void InitAnalyzer_OptionalString_IsNotSupported()
    {
        // Optional<String> is not supported in Phase 1A (reference type semantics)
        var ctor = CreateConstructorWithOptionalPrimitive("title", "Swift.String");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.Null(result); // String has its own Kind, not Primitive → blocked
    }

    [Fact]
    public void InitAnalyzer_OptionalUnknownType_FallsBackToTemplate()
    {
        var ctor = CreateConstructorWithOptionalType("config", "TestModule.UnknownType");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.Null(result);
    }

    [Fact]
    public void EmitOptionalInt_Swift_UsesHasValueAndValue()
    {
        var views = new List<TypeDecl> { CreateViewWithOptionalPrimitiveInit("OptView", "count", "Swift.Int") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("_ countHasValue: Int32", swiftContent);
        Assert.Contains("_ countValue: Int", swiftContent);
        Assert.Contains("countHasValue != 0 ? countValue : nil", swiftContent);
    }

    [Fact]
    public void EmitOptionalBool_Swift_ConvertsViaNonZero()
    {
        var views = new List<TypeDecl> { CreateViewWithOptionalPrimitiveInit("OptView", "enabled", "Swift.Bool") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("enabledHasValue != 0 ? enabledValue != 0 : nil", swiftContent);
    }

    [Fact]
    public void EmitOptionalEnum_Swift_ConstructsFromRawValue()
    {
        var typeDb = CreateEnumTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithOptionalEnumInit("OptEnumView", "style", "TestModule.AlertStyle") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("_ styleHasValue: Int32", swiftContent);
        Assert.Contains("_ styleValue: Int32", swiftContent);
        Assert.Contains("styleHasValue != 0 ? AlertStyle(rawValue: styleValue)! : nil", swiftContent);
    }

    [Fact]
    public void EmitOptionalInt_CSharp_UsesNullableFactoryParam()
    {
        var views = new List<TypeDecl> { CreateViewWithOptionalPrimitiveInit("OptView", "count", "Swift.Int") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("nint? count", csContent); // Factory param
        Assert.Contains("int countHasValue", csContent); // P/Invoke
        Assert.Contains("nint countValue", csContent); // P/Invoke
        Assert.Contains("count.HasValue ? 1 : 0", csContent); // Call arg
        Assert.Contains("count ?? 0", csContent); // Call arg value
    }

    [Fact]
    public void EmitOptionalEnum_CSharp_UsesNullableEnumFactoryParam()
    {
        var typeDb = CreateEnumTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithOptionalEnumInit("OptEnumView", "style", "TestModule.AlertStyle") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("AlertStyle? style", csContent); // Factory param
        Assert.Contains("int styleHasValue", csContent); // P/Invoke
        Assert.Contains("int styleValue", csContent); // P/Invoke
        Assert.Contains("style != null ? 1 : 0", csContent); // Reference type null check
        Assert.Contains("style?.RawValue ?? 0", csContent); // Uses .RawValue
    }

    [Fact]
    public void EmitOptionalEnum_CSharp_UsesMappedRawValueType_ForUInt8Enum()
    {
        var typeDb = CreateEnumTypeDatabaseWithRawType("UInt8");
        var views = new List<TypeDecl> { CreateViewWithOptionalEnumInit("OptEnumView", "style", "TestModule.AlertStyle") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("AlertStyle? style", csContent);       // Factory param
        Assert.Contains("byte styleValue", csContent);         // P/Invoke uses mapped type
        Assert.Contains("style?.RawValue ?? 0", csContent);    // Uses .RawValue, not (byte) cast
    }

    [Fact]
    public void EmitOptionalBool_CSharp_UsesNullableBoolFactoryParam()
    {
        var views = new List<TypeDecl> { CreateViewWithOptionalPrimitiveInit("OptView", "enabled", "Swift.Bool") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("bool? enabled", csContent); // Factory param
    }

    [Fact]
    public void EmitOptionalInt_GeneratesFunctionalBridge_NotTemplate()
    {
        var views = new List<TypeDecl> { CreateViewWithOptionalPrimitiveInit("OptView", "count", "Swift.Int") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_TestModule_OptView_Create\")", swiftContent);
        Assert.DoesNotContain("BRIDGE TEMPLATE", swiftContent);
    }

    #endregion

    #region BoundType (Phase 1B)

    [Fact]
    public void InitAnalyzer_BoundType_IsSupported_ForClassInTypeDatabase()
    {
        var typeDb = CreateClassTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithNamedType("animation", "TestModule.LottieAnimation");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.BoundType, result[0].Kind);
        Assert.Equal("animation", result[0].Name);
        Assert.Equal("UnsafeMutableRawPointer", result[0].SwiftAbiType);
        Assert.Equal("IntPtr", result[0].CSharpPInvokeType);
        Assert.Equal("LottieAnimation", result[0].BridgeTypeName);
        Assert.Equal("Swift.TestModule.LottieAnimation", result[0].CSharpTypeName);
    }

    [Fact]
    public void InitAnalyzer_BoundType_FallsBackToTemplate_WithoutTypeDatabase()
    {
        var ctor = CreateConstructorWithNamedType("animation", "TestModule.LottieAnimation");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.Null(result);
    }

    [Fact]
    public void InitAnalyzer_BoundType_FallsBackToTemplate_WhenTypeNotInDatabase()
    {
        var typeDb = CreateClassTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithNamedType("unknown", "TestModule.UnknownClass");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.Null(result);
    }

    [Fact]
    public void InitAnalyzer_BoundType_FallsBackToTemplate_ForStructInTypeDatabase()
    {
        // Non-frozen structs are deferred to v2.1
        var typeDb = CreateStructTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithNamedType("config", "TestModule.Config");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.Null(result);
    }

    [Fact]
    public void EmitBoundType_Swift_PassesUnsafeMutableRawPointerToCdecl()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithClassInit("AnimView", "animation", "TestModule.LottieAnimation") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("_ animationPtr: UnsafeMutableRawPointer", swiftContent);
        Assert.Contains("Unmanaged<LottieAnimation>.fromOpaque(animationPtr).takeUnretainedValue()", swiftContent);
    }

    [Fact]
    public void EmitBoundType_Swift_GeneratesFunctionalBridge()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithClassInit("AnimView", "animation", "TestModule.LottieAnimation") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_TestModule_AnimView_Create\")", swiftContent);
        Assert.Contains("SBW_TestModule_AnimView_Session", swiftContent);
        Assert.DoesNotContain("BRIDGE TEMPLATE", swiftContent);
    }

    [Fact]
    public void EmitBoundType_Swift_SessionStoresClassField()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithClassInit("AnimView", "animation", "TestModule.LottieAnimation") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("let animation: LottieAnimation", swiftContent);
        Assert.Contains("animation: self.animation", swiftContent);
    }

    [Fact]
    public void EmitBoundType_CSharp_UsesIntPtrPInvoke()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithClassInit("AnimView", "animation", "TestModule.LottieAnimation") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("IntPtr animation", csContent); // P/Invoke param
    }

    [Fact]
    public void EmitBoundType_CSharp_UsesTypedFactoryParam()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithClassInit("AnimView", "animation", "TestModule.LottieAnimation") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("LottieAnimation animation", csContent); // Factory param
        Assert.Contains("animation.Payload.DangerousGetHandle()", csContent); // Call-site
    }

    [Fact]
    public void EmitBoundType_CSharp_GeneratesNativeMethodsAndSession()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithClassInit("AnimView", "animation", "TestModule.LottieAnimation") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("AnimViewBridgeNativeMethods", csContent);
        Assert.Contains("AnimViewSession : IDisposable", csContent);
        Assert.Contains("DllImport", csContent);
    }

    [Fact]
    public void EmitBoundType_CSharp_UsesFullyQualifiedTypeName_ForCrossModuleSafety()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithClassInit("AnimView", "animation", "TestModule.LottieAnimation") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        // Factory param must use fully-qualified name to resolve across namespaces
        Assert.Contains("Swift.TestModule.LottieAnimation animation", csContent);
    }

    [Fact]
    public void EmitBoundEnum_CSharp_UsesFullyQualifiedTypeName_ForCrossModuleSafety()
    {
        var typeDb = CreateEnumTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithEnumInit("EnumView", "style", "TestModule.AlertStyle") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        // Factory param must use fully-qualified name to resolve across namespaces
        Assert.Contains("Swift.TestModule.AlertStyle style", csContent);
    }

    #endregion

    #region Optional<BoundType> (Phase 1B/1D)

    [Fact]
    public void InitAnalyzer_OptionalBoundType_IsSupported()
    {
        var typeDb = CreateClassTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithOptionalType("animation", "TestModule.LottieAnimation");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.OptionalWrapped, result[0].Kind);
        Assert.NotNull(result[0].InnerParameter);
        Assert.Equal(BridgeParameterKind.BoundType, result[0].InnerParameter!.Kind);
        Assert.Equal("LottieAnimation", result[0].InnerParameter!.BridgeTypeName);
        // Nullable pointer: no hasValue flag
        Assert.Equal("UnsafeMutableRawPointer?", result[0].SwiftAbiType);
        Assert.Equal("IntPtr", result[0].CSharpPInvokeType);
    }

    [Fact]
    public void EmitOptionalBoundType_Swift_UsesNullablePointer()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithOptionalClassInit("OptAnimView", "animation", "TestModule.LottieAnimation") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("_ animationPtr: UnsafeMutableRawPointer?", swiftContent);
        Assert.Contains("Unmanaged<LottieAnimation>.fromOpaque($0).takeUnretainedValue()", swiftContent);
    }

    [Fact]
    public void EmitOptionalBoundType_CSharp_UsesNullableFactoryParam()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithOptionalClassInit("OptAnimView", "animation", "TestModule.LottieAnimation") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("LottieAnimation? animation", csContent); // Factory param
        Assert.Contains("IntPtr animation", csContent); // P/Invoke (single IntPtr, no hasValue)
        Assert.Contains("animation?.Payload.DangerousGetHandle() ?? IntPtr.Zero", csContent);
    }

    [Fact]
    public void EmitOptionalBoundType_GeneratesFunctionalBridge_NotTemplate()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithOptionalClassInit("OptAnimView", "animation", "TestModule.LottieAnimation") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_TestModule_OptAnimView_Create\")", swiftContent);
        Assert.DoesNotContain("BRIDGE TEMPLATE", swiftContent);
    }

    #endregion

    #region TypedClosure (Phase 1C)

    [Fact]
    public void InitAnalyzer_TypedClosure_IntToVoid_IsSupported()
    {
        var ctor = CreateConstructorWithTypedClosure("callback",
            new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty);
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.TypedClosure, result[0].Kind);
        Assert.Equal("callback", result[0].Name);
        Assert.True(result[0].HasUserData);
        Assert.NotNull(result[0].ClosureArguments);
        Assert.Single(result[0].ClosureArguments!);
        Assert.Null(result[0].ClosureReturn);
    }

    [Fact]
    public void InitAnalyzer_TypedClosure_IntToBool_IsSupported()
    {
        var ctor = CreateConstructorWithTypedClosure("validator",
            new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("Swift.Bool"));
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.TypedClosure, result[0].Kind);
        Assert.NotNull(result[0].ClosureArguments);
        Assert.Single(result[0].ClosureArguments!);
        Assert.NotNull(result[0].ClosureReturn);
        Assert.Equal("Int32", result[0].ClosureReturn!.SwiftAbiType);
    }

    [Fact]
    public void InitAnalyzer_TypedClosure_MultipleArgs_IsSupported()
    {
        // (Int, Bool) -> Void
        var argsTuple = new TupleTypeSpec(new TypeSpec[]
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"),
        });
        var ctor = CreateConstructorWithTypedClosure("handler", argsTuple, TupleTypeSpec.Empty);
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.TypedClosure, result[0].Kind);
        Assert.Equal(2, result[0].ClosureArguments!.Count);
        Assert.Equal("Int", result[0].ClosureArguments![0].SwiftAbiType);
        Assert.Equal("Int32", result[0].ClosureArguments![1].SwiftAbiType); // Bool → Int32
    }

    [Fact]
    public void InitAnalyzer_TypedClosure_FourArgs_IsSupported()
    {
        // (Int, Bool, Double, Float) -> Void — exactly 4 params (max)
        var argsTuple = new TupleTypeSpec(new TypeSpec[]
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"),
            new NamedTypeSpec("Swift.Double"),
            new NamedTypeSpec("Swift.Float"),
        });
        var ctor = CreateConstructorWithTypedClosure("handler", argsTuple, TupleTypeSpec.Empty);
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.NotNull(result);
        Assert.Equal(4, result[0].ClosureArguments!.Count);
    }

    [Fact]
    public void InitAnalyzer_TypedClosure_FiveArgs_ForcesTemplate()
    {
        // (Int, Bool, Double, Float, Int32) -> Void — 5 params exceeds max
        var argsTuple = new TupleTypeSpec(new TypeSpec[]
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"),
            new NamedTypeSpec("Swift.Double"),
            new NamedTypeSpec("Swift.Float"),
            new NamedTypeSpec("Swift.Int32"),
        });
        var ctor = CreateConstructorWithTypedClosure("handler", argsTuple, TupleTypeSpec.Empty);
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.Null(result);
    }

    [Fact]
    public void InitAnalyzer_TypedClosure_AsyncClosure_ForcesTemplate()
    {
        var closure = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), null) { IsAsync = true };
        var ctor = CreateConstructorWithClosureSpec("callback", closure);
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.Null(result);
    }

    [Fact]
    public void InitAnalyzer_TypedClosure_ThrowingClosure_ForcesTemplate()
    {
        var closure = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), null) { Throws = true };
        var ctor = CreateConstructorWithClosureSpec("callback", closure);
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.Null(result);
    }

    [Fact]
    public void InitAnalyzer_TypedClosure_StringArg_ForcesTemplate()
    {
        // String closure args are not supported in Phase 1C
        var ctor = CreateConstructorWithTypedClosure("callback",
            new NamedTypeSpec("Swift.String"), TupleTypeSpec.Empty);
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.Null(result);
    }

    [Fact]
    public void InitAnalyzer_TypedClosure_VoidToInt_IsSupported()
    {
        // () -> Int — no args, typed return
        var ctor = CreateConstructorWithTypedClosure("getter",
            TupleTypeSpec.Empty, new NamedTypeSpec("Swift.Int"));
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.NotNull(result);
        Assert.Equal(BridgeParameterKind.TypedClosure, result[0].Kind);
        Assert.Empty(result[0].ClosureArguments!);
        Assert.NotNull(result[0].ClosureReturn);
        Assert.Equal("Int", result[0].ClosureReturn!.SwiftAbiType);
    }

    [Fact]
    public void InitAnalyzer_TypedClosure_SwiftAbiType_IncludesConventionC()
    {
        var ctor = CreateConstructorWithTypedClosure("callback",
            new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("Swift.Bool"));
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.NotNull(result);
        Assert.Contains("@convention(c)", result[0].SwiftAbiType);
        Assert.Contains("UnsafeMutableRawPointer?", result[0].SwiftAbiType);
        Assert.Contains("Int32", result[0].SwiftAbiType); // Return type (Bool → Int32)
    }

    [Fact]
    public void EmitTypedClosure_Swift_GeneratesConventionCCallback()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "callback",
            new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@convention(c)", swiftContent);
        Assert.Contains("callbackCallback", swiftContent);
        Assert.Contains("callbackUserData", swiftContent);
        Assert.Contains("@_cdecl(\"SBW_TestModule_CbView_Create\")", swiftContent);
    }

    [Fact]
    public void EmitTypedClosure_Swift_GeneratesTypedClosureWrapper()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "callback",
            new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        // Should generate Swift closure with typed arg
        Assert.Contains("arg0: Int", swiftContent);
        Assert.Contains("cb_callback?", swiftContent);
    }

    [Fact]
    public void EmitTypedClosure_Swift_BoolArgConvertsToInt32()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "toggle",
            new NamedTypeSpec("Swift.Bool"), TupleTypeSpec.Empty) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("arg0: Bool", swiftContent);
        Assert.Contains("arg0 ? 1 : 0", swiftContent); // Bool → Int32 conversion
    }

    [Fact]
    public void EmitTypedClosure_Swift_ReturnBoolConvertsFromInt32()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "validator",
            new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("Swift.Bool")) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("-> Bool", swiftContent);
        Assert.Contains("!= 0", swiftContent); // Int32 → Bool conversion
    }

    [Fact]
    public void EmitTypedClosure_CSharp_GeneratesTypedTrampoline()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "callback",
            new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("UnmanagedCallersOnly", csContent);
        Assert.Contains("CallbackTrampoline", csContent);
        Assert.Contains("nint arg0", csContent); // Int → nint in trampoline
        Assert.Contains("Action<nint>", csContent); // Delegate cast type
    }

    [Fact]
    public void EmitTypedClosure_CSharp_ReturnBoolTrampoline()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "validator",
            new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("Swift.Bool")) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("private static int ValidatorTrampoline", csContent); // Bool return → int
        Assert.Contains("Func<nint, bool>", csContent); // Delegate type
        Assert.Contains("result ? 1 : 0", csContent); // Bool → int conversion
    }

    [Fact]
    public void EmitTypedClosure_CSharp_GeneratesActionFactoryParam()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "callback",
            new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("Action<nint>? callback", csContent);
    }

    [Fact]
    public void EmitTypedClosure_CSharp_GeneratesFuncFactoryParam()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "validator",
            new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("Swift.Bool")) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("Func<nint, bool>? validator", csContent);
    }

    [Fact]
    public void EmitTypedClosure_CSharp_GeneratesTypedFunctionPointer()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "callback",
            new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("delegate* unmanaged[Cdecl]<nint, IntPtr, void>", csContent);
    }

    [Fact]
    public void EmitTypedClosure_CSharp_UnsafeFactory()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "callback",
            new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("unsafe CbViewSession Create(", csContent);
    }

    [Fact]
    public void EmitTypedClosure_CSharp_HasGCHandleCleanup()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "callback",
            new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("_closureHandles", csContent);
        Assert.Contains("closureHandles.Add(h)", csContent);
        Assert.Contains("h.IsAllocated", csContent);
    }

    [Fact]
    public void EmitTypedClosure_GeneratesFunctionalBridge_NotTemplate()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "callback",
            new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_TestModule_CbView_Create\")", swiftContent);
        Assert.DoesNotContain("BRIDGE TEMPLATE", swiftContent);
    }

    [Fact]
    public void EmitTypedClosure_MultipleArgs_Swift_AllArgsInClosure()
    {
        // (Int, Bool) -> Void
        var argsTuple = new TupleTypeSpec(new TypeSpec[]
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"),
        });
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "handler", argsTuple, TupleTypeSpec.Empty) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("arg0: Int", swiftContent);
        Assert.Contains("arg1: Bool", swiftContent);
    }

    [Fact]
    public void EmitTypedClosure_MultipleArgs_CSharp_AllArgsInTrampoline()
    {
        var argsTuple = new TupleTypeSpec(new TypeSpec[]
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"),
        });
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "handler", argsTuple, TupleTypeSpec.Empty) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("nint arg0", csContent);
        Assert.Contains("int arg1", csContent); // Bool → int
        Assert.Contains("Action<nint, bool>", csContent);
    }

    #endregion

    #region Report Integration

    [Fact]
    public void EmitBridgeFiles_ReportsViewsAsBridgedItems()
    {
        ReportCollector.Reset();
        var moduleDecl = CreateModuleDecl();
        ReportCollector.Start(moduleDecl);

        // Simple view with no constructors → Generated (functional bridge)
        var views = new List<TypeDecl> { CreateSimpleViewStruct("TestView") };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var report = ReportCollector.Complete()!;
        Assert.NotNull(report);
        Assert.Single(report.BridgedViews);
        Assert.Equal("TestView", report.BridgedViews[0].ViewName);
        Assert.Equal("TestModule", report.BridgedViews[0].ModuleName);
        Assert.Equal("Generated", report.BridgedViews[0].BridgeStatus);

        ReportCollector.Reset();
    }

    [Fact]
    public void BridgedViews_ShowTemplateStatus_ForUnsupported()
    {
        ReportCollector.Reset();
        var moduleDecl = CreateModuleDecl();
        ReportCollector.Start(moduleDecl);

        var views = new List<TypeDecl>
        {
            CreateGenericViewStruct("View1"),
            CreateGenericViewStruct("View2"),
        };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var report = ReportCollector.Complete()!;
        Assert.NotNull(report);
        Assert.Equal(2, report.BridgedViews.Count);
        Assert.All(report.BridgedViews, v => Assert.Equal("TemplatePending", v.BridgeStatus));

        ReportCollector.Reset();
    }

    #endregion

    #region Helpers

    private static ModuleDecl CreateModuleDecl()
    {
        return new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Protocols = new List<ProtocolDecl>(),
            Dependencies = new List<string>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
    }

    private static StructDecl CreateSimpleViewStruct(string name)
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}V",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>
            {
                new TypeConformance(
                    SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
                    SwiftTypeName.FromModuleQualifiedName("SwiftUI.View"),
                    $"${name}_SwiftUI_View_conformance")
            },
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
        };
    }

    private static StructDecl CreateViewWithVoidClosureInit(string name, string closureParamName)
    {
        var view = CreateSimpleViewStruct(name);
        view.Methods.Add(CreateConstructorWithVoidClosure(closureParamName));
        return view;
    }

    private static StructDecl CreateViewWithUnsupportedParam(string name)
    {
        var view = CreateSimpleViewStruct(name);
        view.Methods.Add(new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            Visibility = Visibility.Public,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                // Return type (index 0)
                new ArgumentDecl
                {
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec($"TestModule.{name}"),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
                // Unsupported param: a custom struct type
                new ArgumentDecl
                {
                    Name = "config",
                    PrivateName = "config",
                    IsInOut = false,
                    IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.SomeComplexType"),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
            },
        });
        return view;
    }

    private static StructDecl CreateGenericViewStruct(string name)
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}V",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>
            {
                new TypeConformance(
                    SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
                    SwiftTypeName.FromModuleQualifiedName("SwiftUI.View"),
                    $"${name}_SwiftUI_View_conformance")
            },
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "T",
                    new List<GenericParameterConformance>(),
                    new List<GenericParameterConformance>())
            },
        };
    }

    private static MethodDecl CreateConstructorWithVoidClosure(string paramName)
    {
        return new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            Visibility = Visibility.Public,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                // Return type (index 0)
                new ArgumentDecl
                {
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.TestView"),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
                // Void closure param
                new ArgumentDecl
                {
                    Name = paramName,
                    PrivateName = paramName,
                    IsInOut = false,
                    IsGeneric = false,
                    SwiftTypeSpec = new ClosureTypeSpec(),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
            },
        };
    }

    private static MethodDecl CreateConstructorWithPrimitive(string paramName, string typeName)
    {
        return new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            Visibility = Visibility.Public,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                // Return type (index 0)
                new ArgumentDecl
                {
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.TestView"),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = paramName,
                    PrivateName = paramName,
                    IsInOut = false,
                    IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec(typeName),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
            },
        };
    }

    private static MethodDecl CreateConstructorWithGenericParam(string paramName)
    {
        return new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            Visibility = Visibility.Public,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.TestView"),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = paramName,
                    PrivateName = paramName,
                    IsInOut = false,
                    IsGeneric = true,
                    SwiftTypeSpec = new NamedTypeSpec("τ_0_0"),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
            },
        };
    }

    private static MethodDecl CreateConstructorWithExistentialParam(string paramName)
    {
        return new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            Visibility = Visibility.Public,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.TestView"),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = paramName,
                    PrivateName = paramName,
                    IsInOut = false,
                    IsGeneric = false,
                    SwiftTypeSpec = new ProtocolListTypeSpec(),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
            },
        };
    }

    private static MethodDecl CreateConstructorWithNamedType(string paramName, string typeName)
    {
        return new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            Visibility = Visibility.Public,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.TestView"),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = paramName,
                    PrivateName = paramName,
                    IsInOut = false,
                    IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec(typeName),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
            },
        };
    }

    private static MethodDecl CreateConstructorWithOptionalPrimitive(string paramName, string innerTypeName)
    {
        return new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            Visibility = Visibility.Public,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.TestView"),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = paramName,
                    PrivateName = paramName,
                    IsInOut = false,
                    IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec(innerTypeName)),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
            },
        };
    }

    private static MethodDecl CreateConstructorWithOptionalType(string paramName, string innerTypeName)
    {
        return CreateConstructorWithOptionalPrimitive(paramName, innerTypeName);
    }

    private static StructDecl CreateViewWithEnumInit(string viewName, string paramName, string enumTypeName)
    {
        var view = CreateSimpleViewStruct(viewName);
        view.Methods.Add(CreateConstructorWithNamedType(paramName, enumTypeName));
        return view;
    }

    private static StructDecl CreateViewWithOptionalPrimitiveInit(string viewName, string paramName, string innerTypeName)
    {
        var view = CreateSimpleViewStruct(viewName);
        view.Methods.Add(CreateConstructorWithOptionalPrimitive(paramName, innerTypeName));
        return view;
    }

    private static StructDecl CreateViewWithOptionalEnumInit(string viewName, string paramName, string enumTypeName)
    {
        var view = CreateSimpleViewStruct(viewName);
        view.Methods.Add(CreateConstructorWithOptionalPrimitive(paramName, enumTypeName));
        return view;
    }

    private static StructDecl CreateViewWithClassInit(string viewName, string paramName, string classTypeName)
    {
        var view = CreateSimpleViewStruct(viewName);
        view.Methods.Add(CreateConstructorWithNamedType(paramName, classTypeName));
        return view;
    }

    private static StructDecl CreateViewWithOptionalClassInit(string viewName, string paramName, string classTypeName)
    {
        var view = CreateSimpleViewStruct(viewName);
        view.Methods.Add(CreateConstructorWithOptionalPrimitive(paramName, classTypeName));
        return view;
    }

    /// <summary>
    /// Creates a constructor with a typed closure parameter.
    /// args/returnType: NamedTypeSpec for single arg, TupleTypeSpec for multiple args, null for void.
    /// </summary>
    private static MethodDecl CreateConstructorWithTypedClosure(string paramName, TypeSpec args, TypeSpec returnType)
    {
        var closure = new ClosureTypeSpec(args, returnType);
        return CreateConstructorWithClosureSpec(paramName, closure);
    }

    private static MethodDecl CreateConstructorWithClosureSpec(string paramName, ClosureTypeSpec closureSpec)
    {
        return new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            Visibility = Visibility.Public,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.TestView"),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = paramName,
                    PrivateName = paramName,
                    IsInOut = false,
                    IsGeneric = false,
                    SwiftTypeSpec = closureSpec,
                    ParentDecl = null,
                    ModuleDecl = null,
                },
            },
        };
    }

    private static StructDecl CreateViewWithTypedClosureInit(string viewName, string paramName, TypeSpec args, TypeSpec returnType)
    {
        var view = CreateSimpleViewStruct(viewName);
        view.Methods.Add(CreateConstructorWithTypedClosure(paramName, args, returnType));
        return view;
    }

    private static ITypeDatabase CreateEnumTypeDatabase()
    {
        return CreateEnumTypeDatabaseWithRawType("Int32");
    }

    private static ITypeDatabase CreateEnumTypeDatabaseWithRawType(string rawValueType)
    {
        return new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["TestModule.AlertStyle"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "AlertStyle"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.AlertStyle"),
                MetadataAccessor = "$s10TestModule10AlertStyleOMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = rawValueType,
            },
        });
    }

    private static ITypeDatabase CreateStringEnumTypeDatabase()
    {
        return new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["TestModule.LogLevel"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "LogLevel"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.LogLevel"),
                MetadataAccessor = "$s10TestModule8LogLevelOMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = "String",
            },
        });
    }

    private static ITypeDatabase CreateClassTypeDatabase()
    {
        return new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["TestModule.LottieAnimation"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "LottieAnimation"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.LottieAnimation"),
                MetadataAccessor = "$s10TestModule15LottieAnimationCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            },
        });
    }

    private static ITypeDatabase CreateStructTypeDatabase()
    {
        return new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["TestModule.Config"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Config"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Config"),
                MetadataAccessor = "$s10TestModule6ConfigVMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct,
            },
        });
    }

    private static ITypeDatabase CreateNonRawEnumTypeDatabase()
    {
        return new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["TestModule.Direction"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Direction"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Direction"),
                MetadataAccessor = "$s10TestModule9DirectionOMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = null, // Not RawRepresentable
            },
        });
    }

    private class BridgeTestTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types;

        public string AsyncLibraryName => null!;

        public BridgeTestTypeDatabase(Dictionary<string, TypeRecord> types)
        {
            _types = types;
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) =>
            _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, out TypeRecord record)
        {
            return _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record);
        }

        public string GetLibraryPath(string moduleName) => "";
    }

    #endregion

    #region Async Inference (Phase 2A)

    [Fact]
    public void InferAsyncPattern_ReturnsNull_WhenNoModuleDecl()
    {
        var view = CreateSimpleViewStruct("TestView");
        var info = new ViewBridgeInfo("TestView", "TestModule",
            ViewInitClassification.Simple, null, view.Methods.Where(m => m.IsConstructor).ToList());
        var context = new BridgeContext(TypeDatabase: null, ModuleDecl: null);

        var result = SwiftUIBridgeEmitter.InferAsyncPattern(info, context);

        Assert.Null(result);
    }

    [Fact]
    public void InferAsyncPattern_ReturnsNull_WhenNoAsyncDeps()
    {
        // View with a constructor that takes only a primitive (leaf) — no async deps
        var moduleDecl = CreateInferenceModuleDecl("TestModule");
        var view = CreateSimpleViewStruct("TestView");
        view.Methods.Add(CreateConstructorWithPrimitive("count", "Swift.Int"));
        moduleDecl.Types.Add(view);

        var info = new ViewBridgeInfo("TestView", "TestModule",
            ViewInitClassification.Simple, null, view.Methods.Where(m => m.IsConstructor).ToList());
        var context = new BridgeContext(ModuleDecl: moduleDecl);

        var result = SwiftUIBridgeEmitter.InferAsyncPattern(info, context);

        Assert.Null(result); // No async steps → not an async view
    }

    [Fact]
    public void InferAsyncPattern_ReturnsPattern_TwoLevelChain()
    {
        // View → AsyncService(key: String) async throws
        var moduleDecl = CreateInferenceModuleDecl("TestModule");
        var asyncService = CreateClassTypeDecl("AsyncService", "TestModule");
        asyncService.Methods.Add(CreateAsyncThrowsCtor("key", "Swift.String"));
        moduleDecl.Types.Add(asyncService);

        var view = CreateSimpleViewStruct("AsyncServiceView");
        view.Methods.Add(CreateCtorWithNamedParam("service", "TestModule.AsyncService"));
        moduleDecl.Types.Add(view);

        var info = new ViewBridgeInfo("AsyncServiceView", "TestModule",
            ViewInitClassification.Simple, null, view.Methods.Where(m => m.IsConstructor).ToList());
        var context = new BridgeContext(ModuleDecl: moduleDecl);

        var result = SwiftUIBridgeEmitter.InferAsyncPattern(info, context);

        Assert.NotNull(result);
        Assert.Single(result.ConstructionChain!);
        Assert.Equal("AsyncService", result.ConstructionChain![0].SwiftTypeName);
        Assert.True(result.ConstructionChain[0].IsAsync);
        Assert.True(result.ConstructionChain[0].Throws);
        Assert.Single(result.FlattenedParams);
        Assert.Equal("key", result.FlattenedParams[0].Name);
        Assert.Equal(AsyncFlatParamKind.String, result.FlattenedParams[0].Kind);
    }

    [Fact]
    public void InferAsyncPattern_ReturnsPattern_ThreeLevelChain()
    {
        // View → Processor(service: AsyncService, mode: Int32) → AsyncService(key: String) async throws
        var moduleDecl = CreateInferenceModuleDecl("TestModule");

        var asyncService = CreateClassTypeDecl("AsyncService", "TestModule");
        asyncService.Methods.Add(CreateAsyncThrowsCtor("key", "Swift.String"));
        moduleDecl.Types.Add(asyncService);

        var processor = CreateClassTypeDecl("Processor", "TestModule");
        processor.Methods.Add(CreateCtorWithTwoParams(
            "service", "TestModule.AsyncService",
            "mode", "Swift.Int32"));
        moduleDecl.Types.Add(processor);

        var view = CreateSimpleViewStruct("DeepChainView");
        view.Methods.Add(CreateCtorWithNamedParam("processor", "TestModule.Processor"));
        moduleDecl.Types.Add(view);

        var info = new ViewBridgeInfo("DeepChainView", "TestModule",
            ViewInitClassification.Simple, null, view.Methods.Where(m => m.IsConstructor).ToList());
        var context = new BridgeContext(ModuleDecl: moduleDecl);

        var result = SwiftUIBridgeEmitter.InferAsyncPattern(info, context);

        Assert.NotNull(result);
        Assert.Equal(2, result.ConstructionChain!.Count);
        // AsyncService is built first (deeper dep), then Processor
        Assert.Equal("AsyncService", result.ConstructionChain[0].SwiftTypeName);
        Assert.Equal("Processor", result.ConstructionChain[1].SwiftTypeName);
        // Flattened params: key (from AsyncService) + mode (from Processor)
        Assert.Equal(2, result.FlattenedParams.Length);
        Assert.Equal("key", result.FlattenedParams[0].Name);
        Assert.Equal("mode", result.FlattenedParams[1].Name);
    }

    [Fact]
    public void InferAsyncPattern_ReturnsNull_FourLevelChain()
    {
        // Depth > 3 should fail
        var moduleDecl = CreateInferenceModuleDecl("TestModule");

        var level3 = CreateClassTypeDecl("Level3", "TestModule");
        level3.Methods.Add(CreateAsyncThrowsCtor("key", "Swift.String"));
        moduleDecl.Types.Add(level3);

        var level2 = CreateClassTypeDecl("Level2", "TestModule");
        level2.Methods.Add(CreateCtorWithNamedParam("dep", "TestModule.Level3"));
        moduleDecl.Types.Add(level2);

        var level1 = CreateClassTypeDecl("Level1", "TestModule");
        level1.Methods.Add(CreateCtorWithNamedParam("dep", "TestModule.Level2"));
        moduleDecl.Types.Add(level1);

        var level0 = CreateClassTypeDecl("Level0", "TestModule");
        level0.Methods.Add(CreateCtorWithNamedParam("dep", "TestModule.Level1"));
        moduleDecl.Types.Add(level0);

        var view = CreateSimpleViewStruct("DeepView");
        view.Methods.Add(CreateCtorWithNamedParam("dep", "TestModule.Level0"));
        moduleDecl.Types.Add(view);

        var info = new ViewBridgeInfo("DeepView", "TestModule",
            ViewInitClassification.Simple, null, view.Methods.Where(m => m.IsConstructor).ToList());
        var context = new BridgeContext(ModuleDecl: moduleDecl);

        var result = SwiftUIBridgeEmitter.InferAsyncPattern(info, context);

        Assert.Null(result); // Exceeds depth limit
    }

    [Fact]
    public void InferAsyncPattern_ReturnsNull_UnsupportedLeafParam()
    {
        // View → SomeType(config: SomeComplexType) where SomeComplexType is not in module
        var moduleDecl = CreateInferenceModuleDecl("TestModule");

        var someType = CreateClassTypeDecl("SomeType", "TestModule");
        someType.Methods.Add(CreateAsyncThrowsCtor("config", "OtherModule.ComplexType"));
        moduleDecl.Types.Add(someType);

        var view = CreateSimpleViewStruct("TestView");
        view.Methods.Add(CreateCtorWithNamedParam("dep", "TestModule.SomeType"));
        moduleDecl.Types.Add(view);

        var info = new ViewBridgeInfo("TestView", "TestModule",
            ViewInitClassification.Simple, null, view.Methods.Where(m => m.IsConstructor).ToList());
        var context = new BridgeContext(ModuleDecl: moduleDecl);

        var result = SwiftUIBridgeEmitter.InferAsyncPattern(info, context);

        Assert.Null(result); // Cross-module dep in chain
    }

    [Fact]
    public void InferAsyncPattern_ReturnsNull_CrossModuleType()
    {
        // View takes a cross-module type directly
        var moduleDecl = CreateInferenceModuleDecl("TestModule");

        var view = CreateSimpleViewStruct("TestView");
        view.Methods.Add(CreateCtorWithNamedParam("dep", "OtherModule.SomeClass"));
        moduleDecl.Types.Add(view);

        var info = new ViewBridgeInfo("TestView", "TestModule",
            ViewInitClassification.Simple, null, view.Methods.Where(m => m.IsConstructor).ToList());
        var context = new BridgeContext(ModuleDecl: moduleDecl);

        var result = SwiftUIBridgeEmitter.InferAsyncPattern(info, context);

        Assert.Null(result);
    }

    [Fact]
    public void InferAsyncPattern_ReturnsNull_CyclicDependency()
    {
        // CycleA(dep: CycleB) and CycleB(dep: CycleA) — infinite loop
        var moduleDecl = CreateInferenceModuleDecl("TestModule");

        var cycleA = CreateClassTypeDecl("CycleA", "TestModule");
        cycleA.Methods.Add(CreateAsyncThrowsCtor("dep", "TestModule.CycleB"));
        moduleDecl.Types.Add(cycleA);

        var cycleB = CreateClassTypeDecl("CycleB", "TestModule");
        cycleB.Methods.Add(CreateAsyncThrowsCtor("dep", "TestModule.CycleA"));
        moduleDecl.Types.Add(cycleB);

        var view = CreateSimpleViewStruct("CycleView");
        view.Methods.Add(CreateCtorWithNamedParam("dep", "TestModule.CycleA"));
        moduleDecl.Types.Add(view);

        var info = new ViewBridgeInfo("CycleView", "TestModule",
            ViewInitClassification.Simple, null, view.Methods.Where(m => m.IsConstructor).ToList());
        var context = new BridgeContext(ModuleDecl: moduleDecl);

        var result = SwiftUIBridgeEmitter.InferAsyncPattern(info, context);

        Assert.Null(result);
    }

    [Fact]
    public void SelectBestConstructor_PrefersSmallestSurface()
    {
        var moduleDecl = CreateInferenceModuleDecl("TestModule");
        var context = new BridgeContext(ModuleDecl: moduleDecl);

        var ctors = new List<MethodDecl>
        {
            CreateCtorWithTwoParams("a", "Swift.Int", "b", "Swift.Bool"),
            CreateConstructorWithPrimitive("x", "Swift.Int"),
        };

        var best = SwiftUIBridgeEmitter.SelectBestConstructor(ctors, context);

        Assert.NotNull(best);
        Assert.Equal(2, best.CSSignature.Count); // return type + 1 param
    }

    [Fact]
    public void SelectBestConstructor_PrefersShallowerAsync()
    {
        var moduleDecl = CreateInferenceModuleDecl("TestModule");

        // Add two types: one with async init, one without
        var asyncType = CreateClassTypeDecl("AsyncDep", "TestModule");
        asyncType.Methods.Add(CreateAsyncThrowsCtor("key", "Swift.String"));
        moduleDecl.Types.Add(asyncType);

        var syncType = CreateClassTypeDecl("SyncDep", "TestModule");
        syncType.Methods.Add(CreateConstructorWithPrimitive("val", "Swift.Int32"));
        moduleDecl.Types.Add(syncType);

        var context = new BridgeContext(ModuleDecl: moduleDecl);

        // Both ctors have 1 param, but one has async dep (deeper async)
        var ctors = new List<MethodDecl>
        {
            CreateCtorWithNamedParam("dep", "TestModule.AsyncDep"),  // asyncDepth=1
            CreateCtorWithNamedParam("dep", "TestModule.SyncDep"),   // asyncDepth=0
        };

        var best = SwiftUIBridgeEmitter.SelectBestConstructor(ctors, context);

        Assert.NotNull(best);
        // Should prefer SyncDep (shallower async depth)
        Assert.Equal("TestModule.SyncDep",
            (best.CSSignature[1].SwiftTypeSpec as NamedTypeSpec)?.Name);
    }

    [Fact]
    public void SelectBestConstructor_UsesAbiOrder_OnTie()
    {
        var moduleDecl = CreateInferenceModuleDecl("TestModule");
        var context = new BridgeContext(ModuleDecl: moduleDecl);

        // Two ctors with identical surface: both 1 primitive param
        var ctors = new List<MethodDecl>
        {
            CreateConstructorWithPrimitive("first", "Swift.Int"),
            CreateConstructorWithPrimitive("second", "Swift.Int"),
        };

        var best = SwiftUIBridgeEmitter.SelectBestConstructor(ctors, context);

        Assert.NotNull(best);
        Assert.Equal("first", best.CSSignature[1].Name); // ABI order: first wins
    }

    [Fact]
    public void SelectBestConstructor_SkipsGenericCtors()
    {
        var moduleDecl = CreateInferenceModuleDecl("TestModule");
        var context = new BridgeContext(ModuleDecl: moduleDecl);

        var genericCtor = CreateConstructorWithPrimitive("val", "Swift.Int");
        genericCtor.GenericParameters.Add(new GenericArgumentDecl("τ_0_0", "T",
            new List<GenericParameterConformance>(), new List<GenericParameterConformance>()));

        var normalCtor = CreateConstructorWithPrimitive("count", "Swift.Int32");

        var ctors = new List<MethodDecl> { genericCtor, normalCtor };

        var best = SwiftUIBridgeEmitter.SelectBestConstructor(ctors, context);

        Assert.NotNull(best);
        Assert.Equal("count", best.CSSignature[1].Name); // Generic ctor skipped
    }

    [Fact]
    public void SelectBestConstructor_SkipsFailableInits()
    {
        var moduleDecl = CreateInferenceModuleDecl("TestModule");
        var context = new BridgeContext(ModuleDecl: moduleDecl);

        var failableCtor = CreateConstructorWithPrimitive("val", "Swift.Int");
        failableCtor.IsFailable = true;

        var normalCtor = CreateConstructorWithPrimitive("count", "Swift.Int32");

        var ctors = new List<MethodDecl> { failableCtor, normalCtor };

        var best = SwiftUIBridgeEmitter.SelectBestConstructor(ctors, context);

        Assert.NotNull(best);
        Assert.Equal("count", best.CSSignature[1].Name); // Failable ctor skipped
    }

    [Fact]
    public void AnalyzeView_KnownPattern_TakesPrecedence_OverInference()
    {
        // BlinkIDUXView in BlinkIDUX module is a known pattern — inference shouldn't run
        var moduleDecl = CreateInferenceModuleDecl("BlinkIDUX");
        var view = CreateSimpleViewStruct("BlinkIDUXView");
        view.SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("BlinkIDUX.BlinkIDUXView");
        moduleDecl.Types.Add(view);

        var context = new BridgeContext(ModuleDecl: moduleDecl);

        var result = SwiftUIBridgeEmitter.AnalyzeView(view, "BlinkIDUX", context);

        Assert.Equal(ViewInitClassification.AsyncDependency, result.Classification);
        Assert.Null(result.InferredPattern); // Known pattern, not inferred
    }

    [Fact]
    public void AnalyzeView_InfersAsync_WhenModuleDeclAvailable()
    {
        var moduleDecl = CreateInferenceModuleDecl("TestModule");

        var asyncService = CreateClassTypeDecl("AsyncService", "TestModule");
        asyncService.Methods.Add(CreateAsyncThrowsCtor("key", "Swift.String"));
        moduleDecl.Types.Add(asyncService);

        var view = CreateSimpleViewStruct("AsyncServiceView");
        view.Methods.Add(CreateCtorWithNamedParam("service", "TestModule.AsyncService"));
        moduleDecl.Types.Add(view);

        var context = new BridgeContext(ModuleDecl: moduleDecl);

        var result = SwiftUIBridgeEmitter.AnalyzeView(view, "TestModule", context);

        Assert.Equal(ViewInitClassification.AsyncDependency, result.Classification);
        Assert.NotNull(result.InferredPattern);
        Assert.NotNull(result.InferredPattern.ConstructionChain);
    }

    [Fact]
    public void InferAsyncPattern_WorksWithTypeDatabase_ClassDependency()
    {
        // Production path: TypeDatabase has the async dependency class registered.
        // Inference should still resolve it as a chain dependency, not a leaf.
        var moduleDecl = CreateInferenceModuleDecl("TestModule");

        var asyncService = CreateClassTypeDecl("AsyncService", "TestModule");
        asyncService.Methods.Add(CreateAsyncThrowsCtor("key", "Swift.String"));
        moduleDecl.Types.Add(asyncService);

        var view = CreateSimpleViewStruct("AsyncServiceView");
        view.Methods.Add(CreateCtorWithNamedParam("service", "TestModule.AsyncService"));
        moduleDecl.Types.Add(view);

        // TypeDatabase has AsyncService registered as a Class
        var typeDb = new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["TestModule.AsyncService"] = new TypeRecord
            {
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.AsyncService"),
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "AsyncService"),
                MetadataAccessor = "$s10TestModuleAsyncServiceMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            },
        });
        var context = new BridgeContext(TypeDatabase: typeDb, ModuleDecl: moduleDecl);

        var info = new ViewBridgeInfo("AsyncServiceView", "TestModule",
            ViewInitClassification.Simple, null, view.Methods.Where(m => m.IsConstructor).ToList());

        var result = SwiftUIBridgeEmitter.InferAsyncPattern(info, context);

        Assert.NotNull(result);
        Assert.Single(result.ConstructionChain!);
        Assert.Equal("AsyncService", result.ConstructionChain![0].SwiftTypeName);
        Assert.True(result.ConstructionChain[0].IsAsync);
    }

    [Fact]
    public void InferAsyncPattern_DAG_SharedDependencyNotFalseCycle()
    {
        // DAG: View → A(dep: Shared), B(dep: Shared) — Shared appears in two branches
        // This should NOT be treated as a cycle.
        var moduleDecl = CreateInferenceModuleDecl("TestModule");

        var shared = CreateClassTypeDecl("SharedService", "TestModule");
        shared.Methods.Add(CreateAsyncThrowsCtor("key", "Swift.String"));
        moduleDecl.Types.Add(shared);

        var branchA = CreateClassTypeDecl("BranchA", "TestModule");
        branchA.Methods.Add(CreateCtorWithNamedParam("dep", "TestModule.SharedService"));
        moduleDecl.Types.Add(branchA);

        var branchB = CreateClassTypeDecl("BranchB", "TestModule");
        branchB.Methods.Add(CreateCtorWithNamedParam("dep", "TestModule.SharedService"));
        moduleDecl.Types.Add(branchB);

        var view = CreateSimpleViewStruct("DAGView");
        view.Methods.Add(CreateCtorWithTwoParams(
            "a", "TestModule.BranchA",
            "b", "TestModule.BranchB"));
        moduleDecl.Types.Add(view);

        var info = new ViewBridgeInfo("DAGView", "TestModule",
            ViewInitClassification.Simple, null, view.Methods.Where(m => m.IsConstructor).ToList());
        var context = new BridgeContext(ModuleDecl: moduleDecl);

        var result = SwiftUIBridgeEmitter.InferAsyncPattern(info, context);

        Assert.NotNull(result);
        // Chain should have: SharedService (from A's dep), BranchA, SharedService (from B's dep), BranchB
        Assert.True(result.ConstructionChain!.Count >= 2);
        // Verify both branches resolved (SharedService appears in chain for each branch)
        var sharedSteps = result.ConstructionChain.Where(s => s.SwiftTypeName == "SharedService").ToList();
        Assert.Equal(2, sharedSteps.Count);
    }

    [Fact]
    public void SelectBestConstructor_WorksWithTypeDatabase_ModuleTypePrioritized()
    {
        // When TypeDatabase has the module type registered as Class,
        // SelectBestConstructor should still count async depth correctly.
        var moduleDecl = CreateInferenceModuleDecl("TestModule");

        var asyncType = CreateClassTypeDecl("AsyncDep", "TestModule");
        asyncType.Methods.Add(CreateAsyncThrowsCtor("key", "Swift.String"));
        moduleDecl.Types.Add(asyncType);

        var typeDb = new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["TestModule.AsyncDep"] = new TypeRecord
            {
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.AsyncDep"),
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "AsyncDep"),
                MetadataAccessor = "$s10TestModuleAsyncDepMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            },
        });
        var context = new BridgeContext(TypeDatabase: typeDb, ModuleDecl: moduleDecl);

        // Two ctors: one with async module dep, one with just a primitive
        var ctors = new List<MethodDecl>
        {
            CreateCtorWithNamedParam("dep", "TestModule.AsyncDep"),   // asyncDepth=1
            CreateConstructorWithPrimitive("val", "Swift.Int"),       // asyncDepth=0
        };

        var best = SwiftUIBridgeEmitter.SelectBestConstructor(ctors, context);

        Assert.NotNull(best);
        // Should prefer the primitive ctor (shallower async depth, same param count)
        Assert.Equal("val", best.CSSignature[1].Name);
    }

    #endregion

    #region Data-Driven Async Emission (Phase 2B)

    [Fact]
    public void DataDrivenSwift_AsyncServiceView_EmitsCdeclCreate()
    {
        // Build: AsyncService(key: String) async throws → AsyncServiceView(service: AsyncService)
        var moduleDecl = CreateInferenceModuleDecl("TestModule");
        var asyncService = CreateClassTypeDecl("AsyncService", "TestModule");
        asyncService.Methods.Add(CreateAsyncThrowsCtor("key", "Swift.String"));
        moduleDecl.Types.Add(asyncService);

        var view = CreateSimpleViewStruct("AsyncServiceView");
        view.Methods.Add(CreateCtorWithNamedParam("service", "TestModule.AsyncService"));
        moduleDecl.Types.Add(view);

        var views = new List<TypeDecl> { view };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, moduleDecl: moduleDecl);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));

        // @_cdecl signature present
        Assert.Contains("@_cdecl(\"SBW_TestModule_AsyncServiceView_Create\")", swiftContent);
        // String parameter pair
        Assert.Contains("keyPtr: UnsafePointer<UInt8>?", swiftContent);
        Assert.Contains("keyLen: Int", swiftContent);
        // Callback typedefs
        Assert.Contains("ReadyFn", swiftContent);
        Assert.Contains("ErrorFn", swiftContent);
        // Chain step: let service = try await AsyncService(key: key)
        Assert.Contains("let service = try await AsyncService(key: key)", swiftContent);
        // Session init
        Assert.Contains("let session = SBW_TestModule_AsyncServiceView_Session(", swiftContent);
        Assert.Contains("service: service", swiftContent);
        // Session class with field
        Assert.Contains("let service: AsyncService", swiftContent);
        // View construction in Create scope (not session init — fixes mixed chain + leaf param issue)
        Assert.Contains("let rootView = AsyncServiceView(service: service)", swiftContent);
        // Session receives pre-built hosting controller
        Assert.Contains("hostingController: hc", swiftContent);
    }

    [Fact]
    public void DataDrivenSwift_DeepChainView_EmitsMultiStepChain()
    {
        // Build: AsyncService(key: String) async throws → Processor(service: AsyncService, mode: Int32) → DeepChainView(processor: Processor)
        var moduleDecl = CreateInferenceModuleDecl("TestModule");

        var asyncService = CreateClassTypeDecl("AsyncService", "TestModule");
        asyncService.Methods.Add(CreateAsyncThrowsCtor("key", "Swift.String"));
        moduleDecl.Types.Add(asyncService);

        var processor = CreateClassTypeDecl("Processor", "TestModule");
        processor.Methods.Add(CreateCtorWithTwoParams(
            "service", "TestModule.AsyncService",
            "mode", "Swift.Int32"));
        moduleDecl.Types.Add(processor);

        var view = CreateSimpleViewStruct("DeepChainView");
        view.Methods.Add(CreateCtorWithNamedParam("processor", "TestModule.Processor"));
        moduleDecl.Types.Add(view);

        var views = new List<TypeDecl> { view };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, moduleDecl: moduleDecl);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));

        // Two chain steps: AsyncService first, then Processor
        Assert.Contains("let service = try await AsyncService(key: key)", swiftContent);
        Assert.Contains("let processor = Processor(service: service, mode: mode)", swiftContent);
        // Session has two fields
        Assert.Contains("let service: AsyncService", swiftContent);
        Assert.Contains("let processor: Processor", swiftContent);
        // Flattened params: key + mode
        Assert.Contains("keyPtr: UnsafePointer<UInt8>?", swiftContent);
        Assert.Contains("mode: Int32", swiftContent);
    }

    [Fact]
    public void DataDrivenCSharp_AsyncServiceView_EmitsCreateAsync()
    {
        // Same setup as Swift test
        var moduleDecl = CreateInferenceModuleDecl("TestModule");
        var asyncService = CreateClassTypeDecl("AsyncService", "TestModule");
        asyncService.Methods.Add(CreateAsyncThrowsCtor("key", "Swift.String"));
        moduleDecl.Types.Add(asyncService);

        var view = CreateSimpleViewStruct("AsyncServiceView");
        view.Methods.Add(CreateCtorWithNamedParam("service", "TestModule.AsyncService"));
        moduleDecl.Types.Add(view);

        var views = new List<TypeDecl> { view };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, moduleDecl: moduleDecl);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));

        // CreateAsync factory with string parameter
        Assert.Contains("CreateAsync(string key)", csContent);
        // NativeMethods with P/Invoke
        Assert.Contains("AsyncServiceViewBridgeNativeMethods", csContent);
        Assert.Contains("EntryPoint = \"SBW_TestModule_AsyncServiceView_Create\"", csContent);
        // TaskCompletionSource
        Assert.Contains("TaskCompletionSource<AsyncServiceViewSession>", csContent);
        // OnReady + OnError trampolines
        Assert.Contains("OnReadyTrampoline", csContent);
        Assert.Contains("OnErrorTrampoline", csContent);
        // No result callback (data-driven never has it)
        Assert.DoesNotContain("OnResultTrampoline", csContent);
        // String encoding in C#
        Assert.Contains("Encoding.UTF8.GetBytes(key", csContent);
    }

    [Fact]
    public void LegacyAsync_BlinkIDUX_UnchangedByRefactor()
    {
        // Ensure the legacy BlinkIDUX path is completely unchanged
        var views = new List<TypeDecl> { CreateSimpleViewStruct("BlinkIDUXView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.BlinkIDUX", "BlinkIDUX", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.BlinkIDUX.SwiftUIBridge.swift"));

        // Legacy hard-coded content must still be present
        Assert.Contains("ScanningUXSettings(", swiftContent);
        Assert.Contains("BlinkIDSdk.createBlinkIDSdk(withSettings: sdkSettings)", swiftContent);
        Assert.Contains("BlinkIDEventStream()", swiftContent);
        Assert.Contains("BlinkIDAnalyzer(", swiftContent);
        Assert.Contains("BlinkIDUXModel(", swiftContent);
        Assert.Contains("SBW_BlinkIDUX_BlinkIDUXView_Session", swiftContent);
        Assert.Contains("ResultFn", swiftContent);
        Assert.Contains("startResultMonitor", swiftContent);
    }

    [Fact]
    public void DataDrivenSwift_BoolParam_EmitsConversion()
    {
        // View → AsyncService(key: String) async throws; View also takes enabled: Bool
        var moduleDecl = CreateInferenceModuleDecl("TestModule");
        var asyncService = CreateClassTypeDecl("AsyncService", "TestModule");
        asyncService.Methods.Add(CreateAsyncThrowsCtor("key", "Swift.String"));
        moduleDecl.Types.Add(asyncService);

        var view = CreateSimpleViewStruct("BoolAsyncView");
        view.Methods.Add(CreateCtorWithTwoParams(
            "service", "TestModule.AsyncService",
            "enabled", "Swift.Bool"));
        moduleDecl.Types.Add(view);

        var views = new List<TypeDecl> { view };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, moduleDecl: moduleDecl);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));

        // Bool parameter ABI type
        Assert.Contains("enabled: Int32", swiftContent);
        // Bool conversion before Task
        Assert.Contains("let enabledVal: Bool = enabled != 0", swiftContent);
        // View construction uses enabledVal (converted) not enabled (Int32 ABI)
        Assert.Contains("BoolAsyncView(service: service, enabled: enabledVal)", swiftContent);
    }

    [Fact]
    public void DataDrivenSwift_MixedChainAndLeaf_ViewBuiltInCreateScope()
    {
        // View with async chain step (service) + direct leaf params (count: Int32, enabled: Bool)
        // Validates P1 fix: View construction happens in Create scope, not session init,
        // so leaf params are accessible alongside chain outputs.
        var moduleDecl = CreateInferenceModuleDecl("TestModule");
        var asyncService = CreateClassTypeDecl("AsyncService", "TestModule");
        asyncService.Methods.Add(CreateAsyncThrowsCtor("key", "Swift.String"));
        moduleDecl.Types.Add(asyncService);

        var view = CreateSimpleViewStruct("MixedAsyncView");
        view.Methods.Add(CreateCtorWithThreeParams(
            "service", "TestModule.AsyncService",
            "count", "Swift.Int32",
            "enabled", "Swift.Bool"));
        moduleDecl.Types.Add(view);

        var views = new List<TypeDecl> { view };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, moduleDecl: moduleDecl);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));

        // Chain step built in Create
        Assert.Contains("let service = try await AsyncService(key: key)", swiftContent);
        // View built in Create scope (not session init) with all three params
        Assert.Contains("let rootView = MixedAsyncView(service: service, count: count, enabled: enabledVal)", swiftContent);
        // Session gets pre-built hosting controller
        Assert.Contains("hostingController: hc", swiftContent);
        // Session class only has chain step field, not leaf params
        Assert.Contains("let service: AsyncService", swiftContent);
        Assert.DoesNotContain("let count: Int32", swiftContent);
        Assert.DoesNotContain("let enabled: Bool", swiftContent);
    }

    [Fact]
    public void DataDrivenCSharp_DeepChain_HasBothParams()
    {
        // Same setup as DeepChain Swift test
        var moduleDecl = CreateInferenceModuleDecl("TestModule");
        var asyncService = CreateClassTypeDecl("AsyncService", "TestModule");
        asyncService.Methods.Add(CreateAsyncThrowsCtor("key", "Swift.String"));
        moduleDecl.Types.Add(asyncService);

        var processor = CreateClassTypeDecl("Processor", "TestModule");
        processor.Methods.Add(CreateCtorWithTwoParams(
            "service", "TestModule.AsyncService",
            "mode", "Swift.Int32"));
        moduleDecl.Types.Add(processor);

        var view = CreateSimpleViewStruct("DeepChainView");
        view.Methods.Add(CreateCtorWithNamedParam("processor", "TestModule.Processor"));
        moduleDecl.Types.Add(view);

        var views = new List<TypeDecl> { view };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, moduleDecl: moduleDecl);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));

        // P/Invoke has both flattened params
        Assert.Contains("IntPtr keyPtr", csContent);
        Assert.Contains("int mode", csContent);
        // CreateAsync factory
        Assert.Contains("CreateAsync(string key, int mode)", csContent);
    }

    #endregion

    #region Cross-Module Async Inference

    [Fact]
    public void InferAsyncPattern_CrossModuleType_WithTypeDB_ResolvesAsLeaf()
    {
        // View(service: SameModule.AsyncService) where AsyncService(sdk: OtherModule.ExternalSdk, key: String)
        // ExternalSdk is cross-module but registered in TypeDatabase as a Class → treated as leaf
        var moduleDecl = CreateInferenceModuleDecl("TestModule");

        var asyncService = CreateClassTypeDecl("AsyncService", "TestModule");
        asyncService.Methods.Add(CreateCtorWithTwoParams(
            "sdk", "OtherModule.ExternalSdk",
            "key", "Swift.String"));
        asyncService.Methods[0].IsAsync = true;
        asyncService.Methods[0].Throws = true;
        moduleDecl.Types.Add(asyncService);

        var view = CreateSimpleViewStruct("CrossModuleView");
        view.Methods.Add(CreateCtorWithNamedParam("service", "TestModule.AsyncService"));
        moduleDecl.Types.Add(view);

        // TypeDatabase has the cross-module type registered
        var typeDb = new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["OtherModule.ExternalSdk"] = new TypeRecord
            {
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("OtherModule.ExternalSdk"),
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.OtherModule", "ExternalSdk"),
                MetadataAccessor = "$s11OtherModuleExternalSdkMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            },
        });
        var context = new BridgeContext(TypeDatabase: typeDb, ModuleDecl: moduleDecl);

        var info = new ViewBridgeInfo("CrossModuleView", "TestModule",
            ViewInitClassification.Simple, null, view.Methods.Where(m => m.IsConstructor).ToList());

        var result = SwiftUIBridgeEmitter.InferAsyncPattern(info, context);

        Assert.NotNull(result);
        Assert.NotNull(result.ConstructionChain);
        Assert.Single(result.ConstructionChain!);
        Assert.Equal("AsyncService", result.ConstructionChain[0].SwiftTypeName);
        Assert.True(result.ConstructionChain[0].IsAsync);
        // ExternalSdk is a flattened leaf parameter
        Assert.Equal(2, result.FlattenedParams.Length); // sdk (BoundType) + key (String)
        var sdkParam = result.FlattenedParams.First(p => p.Name == "sdk");
        Assert.Equal(AsyncFlatParamKind.BoundType, sdkParam.Kind);
        Assert.Equal("ExternalSdk", sdkParam.BridgeTypeName);
    }

    [Fact]
    public void InferAsyncPattern_CrossModuleType_WithoutTypeDB_ReturnsNull()
    {
        // Same setup but no TypeDatabase — cross-module type unresolvable
        var moduleDecl = CreateInferenceModuleDecl("TestModule");

        var asyncService = CreateClassTypeDecl("AsyncService", "TestModule");
        asyncService.Methods.Add(CreateCtorWithTwoParams(
            "sdk", "OtherModule.ExternalSdk",
            "key", "Swift.String"));
        asyncService.Methods[0].IsAsync = true;
        asyncService.Methods[0].Throws = true;
        moduleDecl.Types.Add(asyncService);

        var view = CreateSimpleViewStruct("CrossModuleView");
        view.Methods.Add(CreateCtorWithNamedParam("service", "TestModule.AsyncService"));
        moduleDecl.Types.Add(view);

        // No TypeDatabase — cross-module type can't be resolved
        var context = new BridgeContext(ModuleDecl: moduleDecl);

        var info = new ViewBridgeInfo("CrossModuleView", "TestModule",
            ViewInitClassification.Simple, null, view.Methods.Where(m => m.IsConstructor).ToList());

        var result = SwiftUIBridgeEmitter.InferAsyncPattern(info, context);

        Assert.Null(result);
    }

    [Fact]
    public void InferAsyncPattern_CrossModule_DirectParam_WithTypeDB()
    {
        // View directly takes a cross-module type + a primitive
        // View(sdk: OtherModule.ExternalSdk, key: String) — sdk is leaf, needs async service from elsewhere
        // This alone won't be async (no async steps), so add a same-module async dep too:
        // View(service: TestModule.AsyncService, sdk: OtherModule.ExternalSdk)
        var moduleDecl = CreateInferenceModuleDecl("TestModule");

        var asyncService = CreateClassTypeDecl("AsyncService", "TestModule");
        asyncService.Methods.Add(CreateAsyncThrowsCtor("key", "Swift.String"));
        moduleDecl.Types.Add(asyncService);

        var view = CreateSimpleViewStruct("DirectCrossModuleView");
        view.Methods.Add(CreateCtorWithTwoParams(
            "service", "TestModule.AsyncService",
            "sdk", "OtherModule.ExternalSdk"));
        moduleDecl.Types.Add(view);

        var typeDb = new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["OtherModule.ExternalSdk"] = new TypeRecord
            {
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("OtherModule.ExternalSdk"),
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.OtherModule", "ExternalSdk"),
                MetadataAccessor = "$s11OtherModuleExternalSdkMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            },
        });
        var context = new BridgeContext(TypeDatabase: typeDb, ModuleDecl: moduleDecl);

        var info = new ViewBridgeInfo("DirectCrossModuleView", "TestModule",
            ViewInitClassification.Simple, null, view.Methods.Where(m => m.IsConstructor).ToList());

        var result = SwiftUIBridgeEmitter.InferAsyncPattern(info, context);

        Assert.NotNull(result);
        Assert.Single(result.ConstructionChain!);
        // Flattened params: key (String from AsyncService ctor) + sdk (BoundType direct on View)
        Assert.Equal(2, result.FlattenedParams.Length);
        var sdkParam = result.FlattenedParams.First(p => p.Name == "sdk");
        Assert.Equal(AsyncFlatParamKind.BoundType, sdkParam.Kind);
    }

    [Fact]
    public void InferAsyncPattern_CrossModule_ExtraSwiftImports_Populated()
    {
        // Cross-module type should populate ExtraSwiftImports for the Swift bridge file
        var moduleDecl = CreateInferenceModuleDecl("TestModule");

        var asyncService = CreateClassTypeDecl("AsyncService", "TestModule");
        asyncService.Methods.Add(CreateCtorWithTwoParams(
            "sdk", "OtherModule.ExternalSdk",
            "key", "Swift.String"));
        asyncService.Methods[0].IsAsync = true;
        asyncService.Methods[0].Throws = true;
        moduleDecl.Types.Add(asyncService);

        var view = CreateSimpleViewStruct("ImportTestView");
        view.Methods.Add(CreateCtorWithNamedParam("service", "TestModule.AsyncService"));
        moduleDecl.Types.Add(view);

        var typeDb = new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["OtherModule.ExternalSdk"] = new TypeRecord
            {
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("OtherModule.ExternalSdk"),
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.OtherModule", "ExternalSdk"),
                MetadataAccessor = "$s11OtherModuleExternalSdkMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            },
        });
        var context = new BridgeContext(TypeDatabase: typeDb, ModuleDecl: moduleDecl);

        var info = new ViewBridgeInfo("ImportTestView", "TestModule",
            ViewInitClassification.Simple, null, view.Methods.Where(m => m.IsConstructor).ToList());

        var result = SwiftUIBridgeEmitter.InferAsyncPattern(info, context);

        Assert.NotNull(result);
        Assert.Single(result.ExtraSwiftImports);
        Assert.Equal("OtherModule", result.ExtraSwiftImports[0]);
    }

    [Fact]
    public void DataDrivenSwift_CrossModule_EmitsBoundTypeParam()
    {
        // End-to-end: cross-module BoundType param appears in Swift bridge
        var moduleDecl = CreateInferenceModuleDecl("TestModule");

        var asyncService = CreateClassTypeDecl("AsyncService", "TestModule");
        asyncService.Methods.Add(CreateCtorWithTwoParams(
            "sdk", "OtherModule.ExternalSdk",
            "key", "Swift.String"));
        asyncService.Methods[0].IsAsync = true;
        asyncService.Methods[0].Throws = true;
        moduleDecl.Types.Add(asyncService);

        var view = CreateSimpleViewStruct("CrossModuleBridgeView");
        view.Methods.Add(CreateCtorWithNamedParam("service", "TestModule.AsyncService"));
        moduleDecl.Types.Add(view);

        var typeDb = new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["OtherModule.ExternalSdk"] = new TypeRecord
            {
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("OtherModule.ExternalSdk"),
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.OtherModule", "ExternalSdk"),
                MetadataAccessor = "$s11OtherModuleExternalSdkMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            },
        });

        var views = new List<TypeDecl> { view };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, typeDatabase: typeDb, moduleDecl: moduleDecl);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));

        // Extra import for cross-module type
        Assert.Contains("import OtherModule", swiftContent);
        // BoundType param in Create function uses Ptr suffix
        Assert.Contains("sdkPtr: UnsafeMutableRawPointer", swiftContent);
        // Unmanaged cast to typed reference
        Assert.Contains("Unmanaged<ExternalSdk>.fromOpaque(sdkPtr).takeUnretainedValue()", swiftContent);
        // P1 fix: null-pointer guard before Unmanaged cast
        Assert.Contains("sdkPtr == UnsafeMutableRawPointer(bitPattern: 0)", swiftContent);
        Assert.Contains("Null pointer passed for required object parameter", swiftContent);
        // Chain step uses the typed variable
        Assert.Contains("let service = try await AsyncService(sdk: sdk, key: key)", swiftContent);
    }

    [Fact]
    public void DataDrivenCSharp_CrossModule_EmitsBoundTypeParam()
    {
        // End-to-end: cross-module BoundType param appears in C# bridge
        var moduleDecl = CreateInferenceModuleDecl("TestModule");

        var asyncService = CreateClassTypeDecl("AsyncService", "TestModule");
        asyncService.Methods.Add(CreateCtorWithTwoParams(
            "sdk", "OtherModule.ExternalSdk",
            "key", "Swift.String"));
        asyncService.Methods[0].IsAsync = true;
        asyncService.Methods[0].Throws = true;
        moduleDecl.Types.Add(asyncService);

        var view = CreateSimpleViewStruct("CrossModuleBridgeView");
        view.Methods.Add(CreateCtorWithNamedParam("service", "TestModule.AsyncService"));
        moduleDecl.Types.Add(view);

        var typeDb = new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["OtherModule.ExternalSdk"] = new TypeRecord
            {
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("OtherModule.ExternalSdk"),
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.OtherModule", "ExternalSdk"),
                MetadataAccessor = "$s11OtherModuleExternalSdkMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            },
        });

        var views = new List<TypeDecl> { view };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, typeDatabase: typeDb, moduleDecl: moduleDecl);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));

        // P/Invoke has IntPtr for the cross-module type
        Assert.Contains("IntPtr sdk", csContent);
        // CreateAsync factory has IntPtr parameter
        Assert.Contains("CreateAsync(IntPtr sdk, string key)", csContent);
        // P1 fix: ArgumentNullException guard for BoundType IntPtr params
        Assert.Contains("ArgumentNullException(nameof(sdk))", csContent);
    }

    // --- Inference test helpers ---

    private static ModuleDecl CreateInferenceModuleDecl(string moduleName)
    {
        return new ModuleDecl
        {
            Name = moduleName,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
    }

    private static ClassDecl CreateClassTypeDecl(string name, string moduleName)
    {
        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = $"$s{moduleName}{name}",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
    }

    private static MethodDecl CreateCtorWithNamedParam(string paramName, string swiftType)
    {
        return new MethodDecl
        {
            Name = "init",
            MangledName = "$s_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            Visibility = Visibility.Public,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "", PrivateName = "", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("Self"),
                    ParentDecl = null, ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = paramName, PrivateName = paramName, IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec(swiftType),
                    ParentDecl = null, ModuleDecl = null,
                },
            },
        };
    }

    private static MethodDecl CreateAsyncThrowsCtor(string paramName, string swiftType)
    {
        return new MethodDecl
        {
            Name = "init",
            MangledName = "$s_init_async",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = true,
            IsAsync = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            Visibility = Visibility.Public,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "", PrivateName = "", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("Self"),
                    ParentDecl = null, ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = paramName, PrivateName = paramName, IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec(swiftType),
                    ParentDecl = null, ModuleDecl = null,
                },
            },
        };
    }

    private static MethodDecl CreateCtorWithTwoParams(
        string param1Name, string param1Type,
        string param2Name, string param2Type)
    {
        return new MethodDecl
        {
            Name = "init",
            MangledName = "$s_init_2params",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            Visibility = Visibility.Public,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "", PrivateName = "", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("Self"),
                    ParentDecl = null, ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = param1Name, PrivateName = param1Name, IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec(param1Type),
                    ParentDecl = null, ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = param2Name, PrivateName = param2Name, IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec(param2Type),
                    ParentDecl = null, ModuleDecl = null,
                },
            },
        };
    }

    private static MethodDecl CreateCtorWithThreeParams(
        string param1Name, string param1Type,
        string param2Name, string param2Type,
        string param3Name, string param3Type)
    {
        return new MethodDecl
        {
            Name = "init",
            MangledName = "$s_init_3params",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            Visibility = Visibility.Public,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "", PrivateName = "", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("Self"),
                    ParentDecl = null, ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = param1Name, PrivateName = param1Name, IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec(param1Type),
                    ParentDecl = null, ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = param2Name, PrivateName = param2Name, IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec(param2Type),
                    ParentDecl = null, ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = param3Name, PrivateName = param3Name, IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec(param3Type),
                    ParentDecl = null, ModuleDecl = null,
                },
            },
        };
    }

    #endregion

    #region Bridge Hints

    private class TestLogger : ILogger
    {
        public List<string> Messages { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception exception, Func<TState, Exception, string> formatter)
            => Messages.Add($"[{logLevel}] {formatter(state, exception)}");
    }

    private static string CreateBridgeHintsFile(string dir, string json, string fileName = null)
    {
        var path = Path.Combine(dir, fileName ?? "bridge-hints.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void BridgeHints_NoHintsPath_ProducesIdenticalOutput()
    {
        // No hints → same as before (null bridgeHintsPath)
        var views = new List<TypeDecl> { CreateSimpleViewStruct("TestView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("SBW_TestModule_TestView_Create", swiftContent);
    }

    [Fact]
    public void BridgeHints_SkipHint_ViewProducesNoOutput()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("SkipMe"), CreateSimpleViewStruct("KeepMe") };
        var hintsPath = CreateBridgeHintsFile(_tempDir, """
        {
            "views": {
                "SkipMe": { "skip": true, "reason": "Not needed" }
            }
        }
        """);

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, bridgeHintsPath: hintsPath);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.DoesNotContain("SkipMe", swiftContent);
        Assert.Contains("SBW_TestModule_KeepMe_Create", swiftContent);
    }

    [Fact]
    public void BridgeHints_SkipHint_GenericView_StillSkipped()
    {
        // skip overrides generic rejection
        var views = new List<TypeDecl> { CreateGenericViewStruct("GenericSkipView") };
        var hintsPath = CreateBridgeHintsFile(_tempDir, """
        {
            "views": {
                "GenericSkipView": { "skip": true }
            }
        }
        """);

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, bridgeHintsPath: hintsPath);

        // All views skipped → no bridge files created (stale cleanup)
        Assert.False(File.Exists(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift")));
        Assert.False(File.Exists(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs")));
    }

    [Fact]
    public void BridgeHints_SkipHint_RecordedInReport()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("SkipMe"), CreateSimpleViewStruct("KeepMe") };
        var hintsPath = CreateBridgeHintsFile(_tempDir, """
        {
            "views": {
                "SkipMe": { "skip": true }
            }
        }
        """);

        // Start a report session to capture bridged views
        var moduleDecl = CreateModuleDecl();
        ReportCollector.Start(moduleDecl);
        try
        {
            SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
                NullLogger.Instance, bridgeHintsPath: hintsPath);

            var report = ReportCollector.Complete();
            Assert.NotNull(report);
            var skippedView = report!.BridgedViews.FirstOrDefault(v => v.ViewName == "SkipMe");
            Assert.NotNull(skippedView);
            Assert.Equal("HintSkipped", skippedView!.BridgeStatus);
            Assert.Equal("Skipped", skippedView.InitClassification);
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void BridgeHints_ForceTemplateHint_ViewGetsTemplate()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("ForceTemplateView") };
        var hintsPath = CreateBridgeHintsFile(_tempDir, """
        {
            "views": {
                "ForceTemplateView": { "forceTemplate": true, "reason": "WIP" }
            }
        }
        """);

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, bridgeHintsPath: hintsPath);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("BRIDGE TEMPLATE: ForceTemplateView", swiftContent);
        // No functional @_cdecl for this view (template has it commented out)
        var nonCommentLines = swiftContent.Split('\n').Where(l => !l.TrimStart().StartsWith("//"));
        Assert.DoesNotContain(nonCommentLines, l => l.Contains("SBW_TestModule_ForceTemplateView_Create"));
    }

    [Fact]
    public void BridgeHints_PreferredInitHint_SelectsCorrectConstructor()
    {
        // Create a view with 2 constructors: [0] has void closure, [1] has Int
        var view = CreateSimpleViewStruct("MultiInitView");
        view.Methods.Add(CreateConstructorWithVoidClosure("onTap"));
        view.Methods.Add(CreateConstructorWithPrimitive("count", "Swift.Int"));
        var views = new List<TypeDecl> { view };
        var hintsPath = CreateBridgeHintsFile(_tempDir, """
        {
            "views": {
                "MultiInitView": { "preferredInit": 1 }
            }
        }
        """);

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, bridgeHintsPath: hintsPath);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        // Constructor [1] has Int param → Swift ABI uses "Int" type
        Assert.Contains("count: Int", swiftContent);
        // Constructor [0] has closure — it should NOT be used
        Assert.DoesNotContain("onTapCallback", swiftContent);
    }

    [Fact]
    public void BridgeHints_PreferredInitHint_OutOfRange_FallsBackWithWarning()
    {
        var testLogger = new TestLogger();
        var view = CreateSimpleViewStruct("SingleInitView");
        view.Methods.Add(CreateConstructorWithPrimitive("count", "Swift.Int"));
        var views = new List<TypeDecl> { view };
        var hintsPath = CreateBridgeHintsFile(_tempDir, """
        {
            "views": {
                "SingleInitView": { "preferredInit": 99 }
            }
        }
        """);

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            testLogger, bridgeHintsPath: hintsPath);

        // Warning about out-of-range index
        Assert.Contains(testLogger.Messages, m => m.Contains("preferredInit") && m.Contains("out of range"));
        // Falls back to constructor [0]
        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("count: Int", swiftContent);
    }

    [Fact]
    public void BridgeHints_AsyncPatternHint_ForcesAsyncClassification()
    {
        var view = CreateSimpleViewStruct("MyAsyncView");
        var hintsJson = """
        {
            "views": {
                "MyAsyncView": {
                    "asyncPattern": {
                        "dependencyChain": [
                            { "type": "SomeService", "factory": "create" }
                        ]
                    }
                }
            }
        }
        """;
        var hintsPath = CreateBridgeHintsFile(_tempDir, hintsJson);
        var hints = BridgeHintsLoader.Load(hintsPath, _tempDir, "TestModule", NullLogger.Instance);
        var context = new BridgeContext(Hints: hints);

        var info = SwiftUIBridgeEmitter.AnalyzeView(view, "TestModule", context);

        Assert.Equal(ViewInitClassification.AsyncDependency, info.Classification);
    }

    [Fact]
    public void BridgeHints_MalformedJson_FallsBackToAutoDetection()
    {
        var testLogger = new TestLogger();
        var views = new List<TypeDecl> { CreateSimpleViewStruct("TestView") };
        var hintsPath = CreateBridgeHintsFile(_tempDir, "{ invalid json!!!");

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            testLogger, bridgeHintsPath: hintsPath);

        // Warning about malformed JSON
        Assert.Contains(testLogger.Messages, m => m.Contains("Malformed bridge hints"));
        // Still generates output normally
        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("SBW_TestModule_TestView_Create", swiftContent);
    }

    [Fact]
    public void BridgeHints_UnknownKeys_IgnoredWithWarning()
    {
        var testLogger = new TestLogger();
        var views = new List<TypeDecl> { CreateSimpleViewStruct("TestView") };
        var hintsPath = CreateBridgeHintsFile(_tempDir, """
        {
            "views": {
                "TestView": { "skip": false, "futureKey": true }
            },
            "unknownRoot": 42
        }
        """);

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            testLogger, bridgeHintsPath: hintsPath);

        // Warnings about unknown keys
        Assert.Contains(testLogger.Messages, m => m.Contains("unknown key 'unknownRoot'"));
        Assert.Contains(testLogger.Messages, m => m.Contains("unknown key 'futureKey'"));
        // Still loads and generates output
        Assert.True(File.Exists(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift")));
    }

    [Fact]
    public void BridgeHints_HintsDiscovery_CliArgTakesPrecedence()
    {
        var testLogger = new TestLogger();

        // Create both CLI file and discovered file
        var cliHintsPath = CreateBridgeHintsFile(_tempDir, """
        { "views": { "TestView": { "skip": true } } }
        """, "cli-hints.json");
        CreateBridgeHintsFile(_tempDir, """
        { "views": { "TestView": { "forceTemplate": true } } }
        """);

        var hints = BridgeHintsLoader.Load(cliHintsPath, _tempDir, "TestModule", testLogger);

        Assert.NotNull(hints);
        Assert.True(hints!.Views!["TestView"].Skip);
        // Warning about ignoring discovered file
        Assert.Contains(testLogger.Messages, m => m.Contains("ignoring discovered file"));
    }

    [Fact]
    public void BridgeHints_HintsDiscovery_ModuleSpecificFile()
    {
        CreateBridgeHintsFile(_tempDir, """
        { "views": { "TestView": { "skip": true } } }
        """, "TestModule.bridge-hints.json");

        var hints = BridgeHintsLoader.Load(null, _tempDir, "TestModule", NullLogger.Instance);

        Assert.NotNull(hints);
        Assert.True(hints!.Views!["TestView"].Skip);
    }

    [Fact]
    public void BridgeHints_HintsDiscovery_GenericFile()
    {
        CreateBridgeHintsFile(_tempDir, """
        { "views": { "TestView": { "forceTemplate": true } } }
        """);

        var hints = BridgeHintsLoader.Load(null, _tempDir, "TestModule", NullLogger.Instance);

        Assert.NotNull(hints);
        Assert.True(hints!.Views!["TestView"].ForceTemplate);
    }

    [Fact]
    public void BridgeHints_ExtraSwiftImports_MergedIntoOutput()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("TestView") };
        var hintsPath = CreateBridgeHintsFile(_tempDir, """
        {
            "views": {
                "TestView": {
                    "extraSwiftImports": ["SomeFramework"]
                }
            },
            "globalSettings": {
                "extraSwiftImports": ["AnotherLib"]
            }
        }
        """);

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, bridgeHintsPath: hintsPath);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("import SomeFramework", swiftContent);
        Assert.Contains("import AnotherLib", swiftContent);
    }

    [Fact]
    public void BridgeHints_AllViewsSkipped_NoBridgeFilesWritten()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("ViewA"), CreateSimpleViewStruct("ViewB") };
        var hintsPath = CreateBridgeHintsFile(_tempDir, """
        {
            "views": {
                "ViewA": { "skip": true },
                "ViewB": { "skip": true }
            }
        }
        """);

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, bridgeHintsPath: hintsPath);

        Assert.False(File.Exists(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift")));
        Assert.False(File.Exists(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs")));
    }

    [Fact]
    public void BridgeHints_AllViewsSkipped_DeletesAutoGeneratedBridgeFiles()
    {
        // Pre-create auto-generated bridge files (contain marker)
        var staleSwift = Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift");
        var staleCs = Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs");
        File.WriteAllText(staleSwift, "// Auto-generated by SwiftBindings — SwiftUI Bridge\nimport UIKit\n");
        File.WriteAllText(staleCs, "// Auto-generated by SwiftBindings — SwiftUI Bridge\nusing System;\n");
        Assert.True(File.Exists(staleSwift));
        Assert.True(File.Exists(staleCs));

        var views = new List<TypeDecl> { CreateSimpleViewStruct("OnlyView") };
        var hintsPath = CreateBridgeHintsFile(_tempDir, """
        {
            "views": {
                "OnlyView": { "skip": true }
            }
        }
        """);

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, bridgeHintsPath: hintsPath);

        Assert.False(File.Exists(staleSwift));
        Assert.False(File.Exists(staleCs));
    }

    [Fact]
    public void BridgeHints_AllViewsSkipped_PreservesUserMaintainedBridgeFiles()
    {
        // Pre-create user-maintained bridge files (no auto-generated marker)
        var userSwift = Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift");
        var userCs = Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs");
        File.WriteAllText(userSwift, "// Hand-written bridge\nimport UIKit\n");
        File.WriteAllText(userCs, "// Hand-written bridge\nusing System;\n");

        var views = new List<TypeDecl> { CreateSimpleViewStruct("OnlyView") };
        var hintsPath = CreateBridgeHintsFile(_tempDir, """
        {
            "views": {
                "OnlyView": { "skip": true }
            }
        }
        """);

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, bridgeHintsPath: hintsPath);

        // User-maintained files should NOT be deleted
        Assert.True(File.Exists(userSwift));
        Assert.True(File.Exists(userCs));
    }

    [Fact]
    public void BridgeHints_ExtraSwiftImports_NullAndEmptyFiltered()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("TestView") };
        var hintsPath = CreateBridgeHintsFile(_tempDir, """
        {
            "views": {
                "TestView": {
                    "extraSwiftImports": ["ValidFramework", "", "  "]
                }
            },
            "globalSettings": {
                "extraSwiftImports": ["AnotherValid", ""]
            }
        }
        """);

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, bridgeHintsPath: hintsPath);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.swift"));
        Assert.Contains("import ValidFramework", swiftContent);
        Assert.Contains("import AnotherValid", swiftContent);
        // No blank imports
        var lines = swiftContent.Split('\n');
        Assert.DoesNotContain(lines, l => l.Trim() == "import" || l.Trim() == "import ");
    }

    [Fact]
    public void BridgeHints_UnknownNestedKeys_WarnedForGlobalSettingsAndAsyncPattern()
    {
        var testLogger = new TestLogger();
        var hintsPath = CreateBridgeHintsFile(_tempDir, """
        {
            "views": {
                "TestView": {
                    "asyncPattern": {
                        "dependencyChain": [],
                        "badAsyncKey": true
                    }
                }
            },
            "globalSettings": {
                "maxAsyncChainDepth": 5,
                "badGlobalKey": "oops"
            }
        }
        """);

        BridgeHintsLoader.Load(hintsPath, _tempDir, "TestModule", testLogger);

        Assert.Contains(testLogger.Messages, m => m.Contains("unknown key 'badAsyncKey'") && m.Contains("asyncPattern"));
        Assert.Contains(testLogger.Messages, m => m.Contains("unknown key 'badGlobalKey'") && m.Contains("globalSettings"));
    }

    [Fact]
    public void BridgeHints_ConflictingHints_SkipWinsOverForceTemplate()
    {
        var view = CreateSimpleViewStruct("ConflictView");
        var hintsJson = """
        {
            "views": {
                "ConflictView": { "skip": true, "forceTemplate": true }
            }
        }
        """;
        var hintsPath = CreateBridgeHintsFile(_tempDir, hintsJson);
        var hints = BridgeHintsLoader.Load(hintsPath, _tempDir, "TestModule", NullLogger.Instance);
        var context = new BridgeContext(Hints: hints);

        var info = SwiftUIBridgeEmitter.AnalyzeView(view, "TestModule", context);

        // skip is checked first → Skipped (not Unsupported/forceTemplate)
        Assert.Equal(ViewInitClassification.Skipped, info.Classification);
    }

    #endregion
}
