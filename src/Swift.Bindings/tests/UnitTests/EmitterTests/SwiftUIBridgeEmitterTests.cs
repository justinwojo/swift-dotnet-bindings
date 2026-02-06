// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for SwiftUIBridgeEmitter template and functional bridge generation.
/// </summary>
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
        Assert.Equal("AlertStyle", result[0].CSharpTypeName);
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
    public void EmitBoundEnum_CSharp_CastsEnumToInt()
    {
        var typeDb = CreateEnumTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithEnumInit("EnumView", "style", "TestModule.AlertStyle") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("AlertStyle style", csContent);
        Assert.Contains("(int)style", csContent);
        Assert.Contains("int style", csContent); // P/Invoke param
    }

    [Fact]
    public void EmitBoundEnum_CSharp_UsesMappedCastType_ForUInt8Enum()
    {
        var typeDb = CreateEnumTypeDatabaseWithRawType("UInt8");
        var views = new List<TypeDecl> { CreateViewWithEnumInit("EnumView", "style", "TestModule.AlertStyle") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("AlertStyle style", csContent);       // Factory param uses C# enum type
        Assert.Contains("(byte)style", csContent);            // Cast uses mapped type, not (int)
        Assert.Contains("byte style", csContent);             // P/Invoke param uses mapped type
        Assert.DoesNotContain("(int)style", csContent);       // No hardcoded int cast
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
        Assert.Contains("style.HasValue ? 1 : 0", csContent);
        Assert.Contains("(int)style.Value", csContent);
    }

    [Fact]
    public void EmitOptionalEnum_CSharp_UsesMappedCastType_ForUInt8Enum()
    {
        var typeDb = CreateEnumTypeDatabaseWithRawType("UInt8");
        var views = new List<TypeDecl> { CreateViewWithOptionalEnumInit("OptEnumView", "style", "TestModule.AlertStyle") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.TestModule.SwiftUIBridge.cs"));
        Assert.Contains("AlertStyle? style", csContent);       // Factory param
        Assert.Contains("byte styleValue", csContent);         // P/Invoke uses mapped type
        Assert.Contains("(byte)style.Value", csContent);       // Cast uses mapped type, not (int)
        Assert.DoesNotContain("(int)style.Value", csContent);  // No hardcoded int cast
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
        Assert.Equal("LottieAnimation", result[0].CSharpTypeName);
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
}
