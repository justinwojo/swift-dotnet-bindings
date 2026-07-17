// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
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

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        Assert.True(File.Exists(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift")));
    }

    [Fact]
    public void EmitBridgeFiles_CreatesCSharpFile_InOutputDir()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("TestView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        Assert.True(File.Exists(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs")));
    }

    [Fact]
    public void EmitBridgeFiles_NoFiles_WhenNoViews()
    {
        var views = new List<TypeDecl>();

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public void BridgeOutput_InSameDirectory_AsMainBindings()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("TestView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftPath = Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift");
        var csPath = Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs");
        Assert.True(File.Exists(swiftPath));
        Assert.True(File.Exists(csPath));
    }

    [Fact]
    public void BridgeOutput_NotCreated_WhenNoSimpleViews()
    {
        // Only unsupported generic views — still creates files (with templates), not empty
        var views = new List<TypeDecl> { CreateGenericViewStruct("GenericView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        // Files created but contain only templates
        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
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

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));

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

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("ViewA", swiftContent);
        Assert.Contains("ViewB", swiftContent);
        Assert.Contains("ViewC", swiftContent);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("ViewA", csContent);
        Assert.Contains("ViewB", csContent);
        Assert.Contains("ViewC", csContent);
    }

    [Fact]
    public void EmitBridgeFiles_IncludesModuleImport()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("TestView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("import TestModule", swiftContent);
        Assert.Contains("import SwiftUI", swiftContent);
        Assert.Contains("import UIKit", swiftContent);
    }

    // SwiftUI bridge bodies use UIHostingController (UIKit-only). The emitted .swift
    // file must compile on native macOS, where there is no UIKit — empty TU is OK,
    // unconditional `import UIKit` is a hard compile error. Gate enforced via
    // `#if canImport(UIKit)` at the top and a matching `#endif` at the very end.
    // Regression: prior to this gate, TipKit's macos slice failed to build the
    // wrapper xcframework because TipView's bridge file imported UIKit unconditionally.
    [Fact]
    public void EmitBridgeFiles_FunctionalBridge_IsGatedToUIKitPlatforms()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("TestView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("#if canImport(UIKit)", swiftContent);
        Assert.EndsWith("#endif // canImport(UIKit)" + Environment.NewLine, swiftContent);
        // The UIKit import must sit INSIDE the gate.
        var ifIdx = swiftContent.IndexOf("#if canImport(UIKit)", StringComparison.Ordinal);
        var uikitIdx = swiftContent.IndexOf("import UIKit", StringComparison.Ordinal);
        var endIdx = swiftContent.IndexOf("#endif", StringComparison.Ordinal);
        Assert.True(ifIdx >= 0 && uikitIdx > ifIdx && endIdx > uikitIdx,
            "import UIKit must appear after the `#if canImport(UIKit)` gate and before `#endif`");
    }

    [Fact]
    public void EmitBridgeFiles_TemplateBridge_IsGatedToUIKitPlatforms()
    {
        // Template-only path (no functional bridges) must also be gated — TipKit's TipView
        // hit exactly this case in the 0.12.0 regression: a single template view with no
        // functional emission, but the unconditional `import UIKit` still broke macos.
        var views = new List<TypeDecl> { CreateGenericViewStruct("UnsupportedGenericView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("BRIDGE TEMPLATE", swiftContent); // confirm we're on the template path
        Assert.Contains("#if canImport(UIKit)", swiftContent);
        Assert.EndsWith("#endif // canImport(UIKit)" + Environment.NewLine, swiftContent);
    }

    // The C# .SwiftUIBridge.cs ships to every Apple TFM. Session classes call into
    // Swift @_cdecl symbols that only exist when the Swift bridge actually compiled
    // (UIKit platforms). On native macOS the Swift file is an empty translation unit,
    // so a consumer calling Session.Create would hit DllNotFoundException at runtime
    // — and unit tests can't catch that. Gate the namespace body with
    // `#if __IOS__ || __TVOS__ || __MACCATALYST__` so the session API doesn't even
    // exist for the macOS consumer to call.
    [Fact]
    public void EmitBridgeFiles_FunctionalCSharpBridge_IsGatedToUIKitTFMs()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("#if __IOS__ || __TVOS__ || __MACCATALYST__", csContent);
        Assert.Contains("#endif // __IOS__ || __TVOS__ || __MACCATALYST__", csContent);

        // Session class + NativeMethods + post-release helpers must all sit inside the gate.
        var ifIdx = csContent.IndexOf("#if __IOS__ || __TVOS__ || __MACCATALYST__", StringComparison.Ordinal);
        var endifIdx = csContent.LastIndexOf("#endif // __IOS__ || __TVOS__ || __MACCATALYST__", StringComparison.Ordinal);
        var sessionIdx = csContent.IndexOf("public sealed class TestViewSession", StringComparison.Ordinal);
        var nativeMethodsIdx = csContent.IndexOf("TestViewBridgeNativeMethods", StringComparison.Ordinal);
        var helpersIdx = csContent.IndexOf("SwiftUIBridgePostReleaseHelpers", StringComparison.Ordinal);
        Assert.True(ifIdx >= 0 && endifIdx > ifIdx,
            "TFM gate markers missing or inverted");
        Assert.InRange(sessionIdx, ifIdx, endifIdx);
        Assert.InRange(nativeMethodsIdx, ifIdx, endifIdx);
        Assert.InRange(helpersIdx, ifIdx, endifIdx);
    }

    [Fact]
    public void EmitBridgeFiles_AppliesEmissionContextCollisionRewrite()
    {
        // Bridge files share the wrapper-source rewrite pass — the bridge emitter must run
        // every Swift line through ModuleEmissionContext.QualifyForWrapperSource before
        // writing, so collision modules see consistent prefix stripping. This test proves
        // the wiring is hot by setting the colliding module to "DispatchQueue": the
        // shared SBW_onMainThread helper emits `DispatchQueue.main.sync`, which the rewrite
        // strips to `main.sync` when the helper's leading segment isn't carved out.
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };
        var ctx = new ModuleEmissionContext();
        ctx.SetCollisionContext("DispatchQueue", nestedTypesInCollidingClass: null);

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, emissionContext: ctx);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.DoesNotContain("DispatchQueue.main.sync", swiftContent);
        Assert.Contains("main.sync", swiftContent);
    }

    [Fact]
    public void EmitBridgeFiles_NoCollisionContext_LeavesBridgeContentUnchanged()
    {
        // Sanity counterpart to EmitBridgeFiles_AppliesEmissionContextCollisionRewrite —
        // when no collision context is set, the bridge writes verbatim and DispatchQueue
        // qualifications survive.
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("DispatchQueue.main.sync", swiftContent);
    }

    #endregion

    #region Functional Bridge Generation

    [Fact]
    public void EmitSimpleViewBridge_GeneratesSessionClass()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("final class SBW_TestModule_TestView_Session", swiftContent);
        Assert.Contains("UIHostingController<SBW_TestModule_TestView_Wrapper>", swiftContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_GeneratesCreateWithCdecl()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_TestModule_TestView_Create\")", swiftContent);
        Assert.Contains("public func SBW_TestModule_TestView_Create(", swiftContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_GeneratesGetViewController()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_TestModule_TestView_GetViewController\")", swiftContent);
        Assert.Contains("hostingController", swiftContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_GeneratesTypedViewControllerAccessor()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        // Typed accessor is UIKit-family only (the hosted controller is a UIHostingController),
        // so it must be guarded and absent on macOS.
        Assert.Contains("#if __IOS__ || __TVOS__ || __MACCATALYST__", csContent);
        Assert.Contains("public global::UIKit.UIViewController? ViewController =>", csContent);
        // Wraps the raw IntPtr accessor via the managed-peer lookup for an unowned (+0) pointer.
        Assert.Contains("global::ObjCRuntime.Runtime.GetNSObject<global::UIKit.UIViewController>(GetViewController());", csContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_GeneratesFreeWithHandleTracking()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_TestModule_TestView_Free\")", swiftContent);
        Assert.Contains("SBW_TestModule_TestView_liveHandles", swiftContent);
        Assert.Contains("Unmanaged<SBW_TestModule_TestView_Session>.fromOpaque(handle).release()", swiftContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_Free_DispatchesAsyncFromBackgroundCallers()
    {
        // _Free is reached from the .NET finalizer thread while the test main thread
        // is parked inside GC.WaitForPendingFinalizers(). The earlier emission routed
        // through SBW_onMainThread, whose DispatchQueue.main.sync deadlocked against
        // that parked main; the runner's hang timeout then killed the app silently.
        // The new contract: on-main callers (explicit Dispose) run inline; off-main
        // callers dispatch async so the finalizer thread can return immediately.
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        var freeStart = swiftContent.IndexOf("@_cdecl(\"SBW_TestModule_TestView_Free\")");
        Assert.True(freeStart >= 0, "Free cdecl marker missing");
        var bodyEnd = swiftContent.IndexOf("@_cdecl(", freeStart + 1);
        if (bodyEnd < 0) bodyEnd = swiftContent.Length;
        var freeBody = swiftContent.Substring(freeStart, bodyEnd - freeStart);

        Assert.Contains("Thread.isMainThread", freeBody);
        Assert.Contains("DispatchQueue.main.async(execute:", freeBody);
        Assert.DoesNotContain("SBW_onMainThread", freeBody);
        Assert.DoesNotContain("DispatchQueue.main.sync", freeBody);
    }

    [Fact]
    public void EmitSimpleViewBridge_Free_DelegatesGCHandleDisposalToSwift()
    {
        // C# Dispose must hand its lifecycle/closure GCHandles to Swift via a buffer
        // and post-release callback; Swift invokes the callback only after
        // Unmanaged.release runs. Freeing those handles in C# immediately after the
        // off-main Free returns is a UAF — the Swift session may still capture them
        // until the dispatched release block fires.
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        var freeStart = swiftContent.IndexOf("@_cdecl(\"SBW_TestModule_TestView_Free\")");
        Assert.True(freeStart >= 0, "Free cdecl marker missing");
        var bodyEnd = swiftContent.IndexOf("@_cdecl(", freeStart + 1);
        if (bodyEnd < 0) bodyEnd = swiftContent.Length;
        var freeBody = swiftContent.Substring(freeStart, bodyEnd - freeStart);

        // Swift Free takes handle + buffer + count + callback fn pointer.
        Assert.Contains("_ handleBuffer: UnsafeMutableRawPointer?", freeBody);
        Assert.Contains("_ handleCount: Int32", freeBody);
        Assert.Contains("_ postReleaseFreeFn: UnsafeMutableRawPointer?", freeBody);
        Assert.Contains("unsafeBitCast(fnPtr, to: FreeFn.self)", freeBody);
        Assert.Contains("fn(handleBuffer, handleCount)", freeBody);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));

        // Shared trampoline class lives once per bridge file.
        Assert.Contains("class SwiftUIBridgePostReleaseHelpers", csContent);
        Assert.Contains("internal static void FreeGCHandles(IntPtr buffer, int count)", csContent);
        Assert.Contains("NativeMemory.Free((void*)buffer);", csContent);

        // P/Invoke signature matches the new Swift shape.
        Assert.Contains("Free(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn)", csContent);

        // Session Dispose packs handles into a native buffer and hands off — no in-place freeing.
        var sessionStart = csContent.IndexOf("public sealed class TestViewSession");
        Assert.True(sessionStart >= 0, "Session class missing");
        var disposeStart = csContent.IndexOf("private unsafe void Dispose(bool disposing)", sessionStart);
        Assert.True(disposeStart >= 0, "Dispose method missing");
        // Method body terminator: the next "_handle = IntPtr.Zero;" line lives at the end of Dispose,
        // and the following "}" (with 8-space indent) closes the method itself.
        var disposeEnd = csContent.IndexOf("_handle = IntPtr.Zero;", disposeStart);
        Assert.True(disposeEnd >= 0, "Dispose method missing terminator");
        var disposeBody = csContent.Substring(disposeStart, disposeEnd - disposeStart);

        Assert.Contains("NativeMemory.Alloc", disposeBody);
        Assert.Contains("GCHandle.ToIntPtr", disposeBody);
        Assert.Contains("&SwiftUIBridgePostReleaseHelpers.FreeGCHandles", disposeBody);
        Assert.Contains("TestViewBridgeNativeMethods.Free(_handle, handleBuffer, totalHandles, postReleaseFreeFn);", disposeBody);
        // No more local h.Free() calls — Swift owns handle disposal now.
        Assert.DoesNotContain("h.Free()", disposeBody);
    }

    [Fact]
    public void EmitSimpleViewBridge_GeneratesOnMainThreadHelper()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("SBW_onMainThread", swiftContent);
        Assert.Contains("DispatchQueue.main.sync", swiftContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_MapsVoidClosureToFunctionPointer()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@convention(c)", swiftContent);
        Assert.Contains("UnsafeMutableRawPointer?", swiftContent);
        Assert.Contains("retryActionCallback", swiftContent);
        Assert.Contains("retryActionUserData", swiftContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_CSharp_GeneratesNativeMethods()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("TestViewBridgeNativeMethods", csContent);
        Assert.Contains("LibraryImport", csContent);
        Assert.Contains("SBW_TestModule_TestView_Create", csContent);
        Assert.Contains("SBW_TestModule_TestView_GetViewController", csContent);
        Assert.Contains("SBW_TestModule_TestView_Free", csContent);
        Assert.Contains("CallConvCdecl", csContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_CSharp_GeneratesIDisposable()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("TestViewSession : IDisposable", csContent);
        Assert.Contains("public void Dispose()", csContent);
        Assert.Contains("_disposed = true", csContent);
        Assert.Contains("IntPtr.Zero", csContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_CSharp_ThrowsObjectDisposedAfterDispose()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("ObjectDisposedException", csContent);
    }

    /// <summary>
    /// Regression: emitted SwiftUI bridge sessions follow the standard
    /// .NET <c>IDisposable</c> pattern — a public <c>Dispose()</c> that calls
    /// <c>GC.SuppressFinalize(this)</c>, a finalizer <c>~SessionName()</c>
    /// that delegates to <c>Dispose(disposing: false)</c>, and a
    /// <c>Dispose(bool disposing)</c> that frees the native handle and any
    /// <c>GCHandle</c>s. Without the finalizer, a forgotten <c>Dispose()</c>
    /// leaks the retained native pointer plus every GCHandle the session
    /// allocated for closure parameters or the lifecycle handler.
    /// </summary>
    [Fact]
    public void EmitSimpleViewBridge_CSharp_HasStandardDisposePatternWithFinalizer()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        // Public Dispose() suppresses finalizer and delegates to Dispose(true)
        Assert.Contains("public void Dispose()", csContent);
        Assert.Contains("Dispose(disposing: true)", csContent);
        Assert.Contains("GC.SuppressFinalize(this)", csContent);
        // Finalizer delegates to Dispose(false) so a forgotten Dispose() still releases native state
        Assert.Contains("~TestViewSession() => Dispose(disposing: false);", csContent);
        // Protected Dispose(bool) is the single cleanup site — `unsafe` so it can
        // take an address-of pointer to the post-release GCHandle trampoline.
        Assert.Contains("private unsafe void Dispose(bool disposing)", csContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_FallsBackToTemplate_ForUnsupportedParams()
    {
        // View with a non-primitive, non-closure parameter → template
        var views = new List<TypeDecl> { CreateViewWithUnsupportedParam("ComplexView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("BRIDGE TEMPLATE: ComplexView", swiftContent);
        // Should NOT have functional code for this view
        Assert.DoesNotContain("SBW_TestModule_ComplexView_Session", swiftContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_GeneratesSBWNamingConvention()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("MyView", "action") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("SBW_TestModule_MyView_Create", swiftContent);
        Assert.Contains("SBW_TestModule_MyView_GetViewController", swiftContent);
        Assert.Contains("SBW_TestModule_MyView_Free", swiftContent);
        Assert.Contains("SBW_TestModule_MyView_Session", swiftContent);
        Assert.Contains("SBW_TestModule_MyView_liveHandles", swiftContent);
    }

    // F3: a third-party View whose init param is spelled as a C# or Swift keyword must
    // still produce compilable output. `event` is a C# keyword (not a Swift keyword) → the
    // emitted C# factory param + every C# reference to it must be `@`-escaped. `repeat` is a
    // Swift keyword (not a C# keyword) → the emitted Swift identifiers must be backtick-escaped.
    // Pre-fix: the bridge interpolated the raw name everywhere, emitting bare `event`/`repeat`.
    [Fact]
    public void EmitSimpleViewBridge_EscapesCSharpKeywordParamName()
    {
        var views = new List<TypeDecl>
        {
            CreateViewWithPrimitiveAndStringInit("KeywordView", "event", "Swift.Int32", "repeat"),
        };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        // The C# keyword param name is `@`-escaped in the factory signature and body refs.
        Assert.Contains("@event", csContent);
        // No bare `event` identifier survives as a parameter token (would be a compile error).
        Assert.DoesNotContain("int event", csContent);
        Assert.DoesNotContain("(int)event", csContent);
        // The native-call OPERAND must also be `@`-escaped — a bare `event` argument operand
        // is invalid C#. These delimiter-anchored shapes match only an unescaped operand; the
        // correct `@event` form has `@` immediately before `event`, so none of them collide
        // with it. (Pre-fix the else-branch appended raw `param.Name`, emitting `(event, …`.)
        Assert.DoesNotContain("(event,", csContent);
        Assert.DoesNotContain(", event,", csContent);
        Assert.DoesNotContain("(event)", csContent);
        Assert.DoesNotContain(", event)", csContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_EscapesSwiftKeywordParamName()
    {
        var views = new List<TypeDecl>
        {
            CreateViewWithPrimitiveAndStringInit("KeywordView", "event", "Swift.Int32", "repeat"),
        };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // The Swift keyword identifier is backtick-escaped wherever it appears as a Swift
        // identifier token (struct field, init param, local, member access).
        Assert.Contains("`repeat`", swiftContent);
        // No bare `let repeat` / `self.repeat` survive (would be a Swift parse error).
        Assert.DoesNotContain("let repeat:", swiftContent);
        Assert.DoesNotContain("self.repeat ", swiftContent);
        // The State/View/Wrapper init CALLS must use the keyword as a BARE argument label
        // (`repeat:`), never a backtick-escaped one. Swift accepts keyword argument labels
        // bare; escaping a valid-bare keyword label emits a "keyword 'repeat' does not need
        // to be escaped in argument list" warning. The substring "repeat:" matches ONLY a
        // bare label — the escaped form `` `repeat`: `` carries a backtick immediately before
        // the colon, and DECLARATIONS (struct field / init param) all use that escaped form.
        // So a bare "repeat:" present here proves a call label was emitted unescaped.
        Assert.Contains("repeat:", swiftContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_ReportShowsGenerated()
    {
        ReportCollector.Reset();
        var moduleDecl = CreateModuleDecl();
        ReportCollector.Start(moduleDecl);

        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var report = ReportCollector.Complete()!;
        Assert.Single(report.BridgedViews);
        Assert.Equal("Generated", report.BridgedViews[0].BridgeStatus);
        Assert.Equal("Simple", report.BridgedViews[0].InitClassification);

        ReportCollector.Reset();
    }

    /// <summary>
    /// A non-generic SwiftUI View with zero public constructors in the ABI has no
    /// accessible initializer: Swift's implicit/default init for a public type is
    /// <c>internal</c>, so it cannot be constructed from the separate
    /// <c>{Module}Bridge</c> module that only sees the framework's public API.
    /// Emitting a functional <c>TypeName()</c> for such a view produces Swift that
    /// fails to compile ("cannot be constructed because it has no accessible
    /// initializers"). The view must be skipped, not bridged.
    /// </summary>
    [Fact]
    public void EmitSimpleViewBridge_SkipsView_WhenNoPublicConstructor()
    {
        ReportCollector.Reset();
        var moduleDecl = CreateModuleDecl();
        ReportCollector.Start(moduleDecl);

        var views = new List<TypeDecl>
        {
            CreateViewStructWithNoConstructor("UnconstructibleView"),
            CreateViewWithVoidClosureInit("ConstructibleView", "action"),
        };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));

        // The constructible view bridges functionally...
        Assert.Contains("SBW_TestModule_ConstructibleView_Session", swiftContent);
        // ...but the unconstructible view is skipped entirely — no functional code,
        // and not even a template stub (there is nothing the consumer could fill in).
        Assert.DoesNotContain("UnconstructibleView", swiftContent);

        var report = ReportCollector.Complete()!;
        var skipped = report.BridgedViews.FirstOrDefault(v => v.ViewName == "UnconstructibleView");
        Assert.NotNull(skipped);
        Assert.Equal("Skipped", skipped!.InitClassification);
        Assert.Equal("Skipped", skipped.BridgeStatus);

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
    public void AnalyzeView_Unsupported_GenericType_NoBridgeableCtors()
    {
        // Generic view with no constructors → no bridgeable constructor
        var view = CreateGenericViewStruct("GenericView");

        var info = SwiftUIBridgeEmitter.AnalyzeView(view, "TestModule");

        Assert.Equal(ViewInitClassification.Unsupported, info.Classification);
        Assert.Contains("No bridgeable constructor", info.UnsupportedReason);
    }

    #endregion

    #region Async Bridge Generation

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
        Assert.Equal("SBW_BlinkIDUX_BlinkIDUXView_Session", pattern.SessionClassName);
        Assert.NotNull(pattern.ResultCallback);
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
        // The view name exists but not in "OtherModule"
        var pattern = SwiftUIBridgeEmitter.GetAsyncPattern("BlinkIDUXView", "OtherModule");
        Assert.Null(pattern);
    }

    [Fact]
    public void GetAsyncPattern_BlinkIDUXView_ModelStep_DoesNotInjectSessionNumber()
    {
        // 0.11.x removed init(analyzer:uxSettings:sessionNumber:) from the model class and
        // ships only init(analyzer:uxSettings:). The dictionary entry must mirror the live API
        // — supplying sessionNumber to the new init makes swiftc reject the SwiftUIBridge.swift
        // file with "extra argument 'sessionNumber' in call". Pinning the arg list here keeps
        // the dictionary honest if the library shifts its surface again.
        var pattern = SwiftUIBridgeEmitter.GetAsyncPattern("BlinkIDUXView", "BlinkIDUX");
        Assert.NotNull(pattern);

        var modelStep = pattern!.ConstructionChain.Single(s => s.VariableName == "model");
        Assert.Equal(new[] { "analyzer", "uxSettings" }, modelStep.Args.Select(a => a.ParamLabel).ToArray());
        Assert.DoesNotContain(modelStep.Args, a => a.ParamLabel == "sessionNumber");
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
        Assert.Contains("internal static partial void Create(", csContent);
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
        // A view not in the known-async dictionary should NOT be matched by async pattern
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

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("public static unsafe TestViewSession Create(", csContent);
        Assert.Contains("Action? retryAction", csContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_CSharp_CreateFactoryHasTrampoline()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("UnmanagedCallersOnly", csContent);
        Assert.Contains("RetryActionTrampoline", csContent);
        Assert.Contains("GCHandle.FromIntPtr", csContent);
        Assert.Contains("is Action action", csContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_CSharp_CreateFactoryDisposesHandles()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("TestView", "retryAction") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("_closureHandles", csContent);
        Assert.Contains("h.IsAllocated", csContent);
        Assert.Contains("h.Free()", csContent);
    }

    [Fact]
    public void EmitSimpleViewBridge_CSharp_NoParamsFactory_HasLifecycleParams()
    {
        // View with no init params → factory has only lifecycle optional params
        var views = new List<TypeDecl> { CreateSimpleViewStruct("PlainView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("PlainViewSession Create(Action? onAppear = null, Action? onDisappear = null)", csContent);
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
        // Disposal is now transferred to Swift via the post-release trampoline —
        // the local h.Free() pattern is replaced by GCHandle.ToIntPtr + buffer
        // transfer. Asserted in detail by
        // EmitAsyncViewBridge_Free_DelegatesGCHandleDisposalToSwift below.
        Assert.Contains("GCHandle.ToIntPtr(_stateHandle)", csContent);
    }

    [Fact]
    public void EmitAsyncViewBridge_Free_DelegatesGCHandleDisposalToSwift()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("BlinkIDUXView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "Swift.BlinkIDUX", "BlinkIDUX", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.BlinkIDUX.SwiftUIBridge.swift"));
        var csContent = File.ReadAllText(Path.Combine(_tempDir, "Swift.BlinkIDUX.SwiftUIBridge.cs"));

        // Swift side: _Free signature widens to carry the handle buffer + post-release trampoline,
        // and invokes the trampoline strictly AFTER Unmanaged.release runs inside the dispatched block.
        Assert.Contains("@_cdecl(\"SBW_BlinkIDUX_BlinkIDUXView_Free\")", swiftContent);
        Assert.Contains("_ handleBuffer: UnsafeMutableRawPointer?", swiftContent);
        Assert.Contains("_ handleCount: Int32", swiftContent);
        Assert.Contains("_ postReleaseFreeFn: UnsafeMutableRawPointer?", swiftContent);
        Assert.Contains("unsafeBitCast(fnPtr, to: FreeFn.self)", swiftContent);
        // Thread-aware dispatch shape from the original finalizer-thread fix preserved.
        Assert.Contains("if Thread.isMainThread { release() }", swiftContent);
        Assert.Contains("DispatchQueue.main.async(execute: release)", swiftContent);
        // SBW_onMainThread (sync) helper must NOT wrap the new dispatch.
        Assert.DoesNotContain("SBW_onMainThread {\n        guard let handle = handle,\n              SBW_BlinkIDUX_BlinkIDUXView_liveHandles.remove", swiftContent);

        // C# side: helper class present and Dispose transfers GCHandle via NativeMemory buffer + function pointer.
        Assert.Contains("SwiftUIBridgePostReleaseHelpers", csContent);
        Assert.Contains("FreeGCHandles", csContent);
        Assert.Contains("IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn", csContent);
        Assert.Contains("private unsafe void Dispose(bool disposing)", csContent);
        Assert.Contains("NativeMemory.Alloc((nuint)sizeof(IntPtr))", csContent);
        Assert.Contains("GCHandle.ToIntPtr(_stateHandle)", csContent);
        Assert.Contains("delegate* unmanaged[Cdecl]<IntPtr, int, void> fn = &SwiftUIBridgePostReleaseHelpers.FreeGCHandles;", csContent);

        // Dispose body must NOT call _stateHandle.Free() locally — disposal is Swift-owned now.
        var disposeStart = csContent.IndexOf("private unsafe void Dispose(bool disposing)");
        Assert.True(disposeStart >= 0);
        var disposeEnd = csContent.IndexOf("_handle = IntPtr.Zero;", disposeStart);
        Assert.True(disposeEnd >= 0);
        var disposeBody = csContent.Substring(disposeStart, disposeEnd - disposeStart);
        Assert.DoesNotContain("_stateHandle.Free()", disposeBody);
    }

    #endregion

    #region BoundEnum

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
        Assert.Equal("TestModule.AlertStyle", result[0].CSharpTypeName);
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

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("_ style: Int32", swiftContent);
        // An out-of-range raw value fails creation gracefully (return nil) instead of
        // a force-unwrap SIGTRAP. The old `AlertStyle(rawValue: style)!` WAS the crash.
        Assert.Contains("guard let styleConverted = AlertStyle(rawValue: style) else { return nil }", swiftContent);
        Assert.DoesNotContain("AlertStyle(rawValue: style)!", swiftContent);
    }

    [Fact]
    public void EmitBoundEnum_Swift_GeneratesFunctionalBridge()
    {
        var typeDb = CreateEnumTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithEnumInit("EnumView", "style", "TestModule.AlertStyle") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_TestModule_EnumView_Create\")", swiftContent);
        Assert.Contains("SBW_TestModule_EnumView_Session", swiftContent);
        Assert.DoesNotContain("BRIDGE TEMPLATE", swiftContent);
    }

    [Fact]
    public void EmitBoundEnum_CSharp_UsesRawValue()
    {
        var typeDb = CreateEnumTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithEnumInit("EnumView", "style", "TestModule.AlertStyle") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("AlertStyle style", csContent);
        Assert.Contains("style.RawValue", csContent); // Enum class uses .RawValue, not (int) cast
        Assert.Contains("int style", csContent); // P/Invoke param
    }

    [Fact]
    public void EmitBoundEnum_CSharp_UsesMappedRawValueType_ForUInt8Enum()
    {
        var typeDb = CreateEnumTypeDatabaseWithRawType("UInt8");
        var views = new List<TypeDecl> { CreateViewWithEnumInit("EnumView", "style", "TestModule.AlertStyle") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("AlertStyle style", csContent);       // Factory param uses C# enum type
        Assert.Contains("style.RawValue", csContent);         // Uses .RawValue property
        Assert.Contains("byte style", csContent);             // P/Invoke param uses mapped type
    }

    [Fact]
    public void EmitBoundEnum_CSharp_GeneratesNativeMethodsAndSession()
    {
        var typeDb = CreateEnumTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithEnumInit("EnumView", "style", "TestModule.AlertStyle") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("EnumViewBridgeNativeMethods", csContent);
        Assert.Contains("EnumViewSession : IDisposable", csContent);
        Assert.Contains("LibraryImport", csContent);
    }

    #endregion

    #region OptionalWrapped

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
    public void InitAnalyzer_OptionalString_IsSupported()
    {
        var ctor = CreateConstructorWithOptionalPrimitive("title", "Swift.String");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.OptionalWrapped, result[0].Kind);
        Assert.NotNull(result[0].InnerParameter);
        Assert.Equal(BridgeParameterKind.String, result[0].InnerParameter!.Kind);
        Assert.True(result[0].HasLength);
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

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("_ countHasValue: Int32", swiftContent);
        Assert.Contains("_ countValue: Int", swiftContent);
        Assert.Contains("countHasValue != 0 ? countValue : nil", swiftContent);
    }

    [Fact]
    public void EmitOptionalBool_Swift_ConvertsViaNonZero()
    {
        var views = new List<TypeDecl> { CreateViewWithOptionalPrimitiveInit("OptView", "enabled", "Swift.Bool") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("enabledHasValue != 0 ? enabledValue != 0 : nil", swiftContent);
    }

    [Fact]
    public void EmitOptionalEnum_Swift_ConstructsFromRawValue()
    {
        var typeDb = CreateEnumTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithOptionalEnumInit("OptEnumView", "style", "TestModule.AlertStyle") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("_ styleHasValue: Int32", swiftContent);
        Assert.Contains("_ styleValue: Int32", swiftContent);
        // A present-but-out-of-range raw value fails creation gracefully (return nil)
        // instead of a force-unwrap trap; a nil Optional (HasValue == 0) stays nil.
        Assert.Contains("if styleHasValue != 0 {", swiftContent);
        Assert.Contains("guard let styleCase = AlertStyle(rawValue: styleValue) else { return nil }", swiftContent);
        Assert.DoesNotContain("AlertStyle(rawValue: styleValue)!", swiftContent);
    }

    [Fact]
    public void EmitOptionalInt_CSharp_UsesNullableFactoryParam()
    {
        var views = new List<TypeDecl> { CreateViewWithOptionalPrimitiveInit("OptView", "count", "Swift.Int") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
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

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
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

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("AlertStyle? style", csContent);       // Factory param
        Assert.Contains("byte styleValue", csContent);         // P/Invoke uses mapped type
        Assert.Contains("style?.RawValue ?? 0", csContent);    // Uses .RawValue, not (byte) cast
    }

    [Fact]
    public void EmitOptionalBool_CSharp_UsesNullableBoolFactoryParam()
    {
        var views = new List<TypeDecl> { CreateViewWithOptionalPrimitiveInit("OptView", "enabled", "Swift.Bool") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("bool? enabled", csContent); // Factory param
    }

    [Fact]
    public void EmitOptionalInt_GeneratesFunctionalBridge_NotTemplate()
    {
        var views = new List<TypeDecl> { CreateViewWithOptionalPrimitiveInit("OptView", "count", "Swift.Int") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_TestModule_OptView_Create\")", swiftContent);
        Assert.DoesNotContain("BRIDGE TEMPLATE", swiftContent);
    }

    #endregion

    #region BoundType

    [Fact]
    public void InitAnalyzer_BoundType_IsSupported_ForClassInTypeDatabase()
    {
        var typeDb = CreateClassTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithNamedType("animation", "TestModule.AnimationAsset");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.BoundType, result[0].Kind);
        Assert.Equal("animation", result[0].Name);
        Assert.Equal("UnsafeMutableRawPointer", result[0].SwiftAbiType);
        Assert.Equal("IntPtr", result[0].CSharpPInvokeType);
        Assert.Equal("AnimationAsset", result[0].BridgeTypeName);
        Assert.Equal("TestModule.AnimationAsset", result[0].CSharpTypeName);
    }

    [Fact]
    public void InitAnalyzer_BoundType_FallsBackToTemplate_WithoutTypeDatabase()
    {
        var ctor = CreateConstructorWithNamedType("animation", "TestModule.AnimationAsset");
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
    public void InitAnalyzer_MapsBoundStruct_ForNonFrozenStruct()
    {
        var typeDb = CreateStructTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithNamedType("config", "TestModule.Config");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.BoundStruct, result[0].Kind);
        Assert.Equal(StructProjectionKind.NonFrozen, result[0].StructProjection);
        Assert.Equal("UnsafeMutableRawPointer", result[0].SwiftAbiType);
        Assert.Equal("IntPtr", result[0].CSharpPInvokeType);
        Assert.Equal("Config", result[0].BridgeTypeName);
        Assert.Equal("TestModule.Config", result[0].CSharpTypeName);
    }

    [Fact]
    public void EmitBoundType_Swift_PassesUnsafeMutableRawPointerToCdecl()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithClassInit("AnimView", "animation", "TestModule.AnimationAsset") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("_ animationPtr: UnsafeMutableRawPointer", swiftContent);
        Assert.Contains("Unmanaged<AnimationAsset>.fromOpaque(animationPtr).takeUnretainedValue()", swiftContent);
    }

    [Fact]
    public void EmitBoundType_Swift_GeneratesFunctionalBridge()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithClassInit("AnimView", "animation", "TestModule.AnimationAsset") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_TestModule_AnimView_Create\")", swiftContent);
        Assert.Contains("SBW_TestModule_AnimView_Session", swiftContent);
        Assert.DoesNotContain("BRIDGE TEMPLATE", swiftContent);
    }

    [Fact]
    public void EmitBoundType_Swift_SessionStoresClassField()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithClassInit("AnimView", "animation", "TestModule.AnimationAsset") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // With state binding, BoundType goes to State as @Published
        Assert.Contains("@Published var animation: AnimationAsset", swiftContent);
        Assert.Contains("animation: state.animation", swiftContent);
    }

    [Fact]
    public void EmitBoundType_CSharp_UsesIntPtrPInvoke()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithClassInit("AnimView", "animation", "TestModule.AnimationAsset") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("IntPtr animation", csContent); // P/Invoke param
    }

    [Fact]
    public void EmitBoundType_CSharp_UsesTypedFactoryParam()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithClassInit("AnimView", "animation", "TestModule.AnimationAsset") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("AnimationAsset animation", csContent); // Factory param
        Assert.Contains("animation.Payload.DangerousGetHandle()", csContent); // Call-site
    }

    [Fact]
    public void EmitBoundType_CSharp_GeneratesNativeMethodsAndSession()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithClassInit("AnimView", "animation", "TestModule.AnimationAsset") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("AnimViewBridgeNativeMethods", csContent);
        Assert.Contains("AnimViewSession : IDisposable", csContent);
        Assert.Contains("LibraryImport", csContent);
    }

    [Fact]
    public void EmitBoundType_CSharp_UsesFullyQualifiedTypeName_ForCrossModuleSafety()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithClassInit("AnimView", "animation", "TestModule.AnimationAsset") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        // Factory param must use fully-qualified name to resolve across namespaces
        Assert.Contains("TestModule.AnimationAsset animation", csContent);
    }

    [Fact]
    public void EmitBoundEnum_CSharp_UsesFullyQualifiedTypeName_ForCrossModuleSafety()
    {
        var typeDb = CreateEnumTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithEnumInit("EnumView", "style", "TestModule.AlertStyle") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        // Factory param must use fully-qualified name to resolve across namespaces
        Assert.Contains("TestModule.AlertStyle style", csContent);
    }

    [Fact]
    public void InitAnalyzer_FallsBackToTemplate_ForNestedTypeUnderSwiftUIView()
    {
        // A nested type under a SwiftUI View that is suppressed from the main bindings, so the nested type doesn't exist in C#.
        var typeDb = new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["TestModule.ParentView.Context"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ParentView.Context"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ParentView.Context"),
                MetadataAccessor = "$s10TestModule10ParentView7ContextCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            },
        });
        var parentView = CreateSimpleViewStruct("ParentView");
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            ParentDecl = null,
            ModuleDecl = null,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { parentView },
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
        };
        var context = new BridgeContext(typeDb, moduleDecl);
        var ctor = CreateConstructorWithNamedType("context", "TestModule.ParentView.Context");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.Null(result); // Falls back to template
    }

    [Fact]
    public void InitAnalyzer_AllowsNestedType_WhenParentIsNotSwiftUIView()
    {
        // A nested type under a non-View parent should still be supported.
        var typeDb = new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["TestModule.Config.Options"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Config.Options"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Config.Options"),
                MetadataAccessor = "$s10TestModule6Config7OptionsCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            },
        });
        // Config is NOT a SwiftUI View (no View conformance)
        var configType = new StructDecl
        {
            Name = "Config",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Config"),
            MangledName = "$s10TestModule6ConfigV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(), // No View conformance
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule6ConfigVMa",
        };
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            ParentDecl = null,
            ModuleDecl = null,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { configType },
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
        };
        var context = new BridgeContext(typeDb, moduleDecl);
        var ctor = CreateConstructorWithNamedType("options", "TestModule.Config.Options");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.BoundType, result[0].Kind);
        Assert.Equal("Config.Options", result[0].BridgeTypeName);
    }

    #endregion

    #region Optional<BoundType>

    [Fact]
    public void InitAnalyzer_OptionalBoundType_IsSupported()
    {
        var typeDb = CreateClassTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithOptionalType("animation", "TestModule.AnimationAsset");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.OptionalWrapped, result[0].Kind);
        Assert.NotNull(result[0].InnerParameter);
        Assert.Equal(BridgeParameterKind.BoundType, result[0].InnerParameter!.Kind);
        Assert.Equal("AnimationAsset", result[0].InnerParameter!.BridgeTypeName);
        // Nullable pointer: no hasValue flag
        Assert.Equal("UnsafeMutableRawPointer?", result[0].SwiftAbiType);
        Assert.Equal("IntPtr", result[0].CSharpPInvokeType);
    }

    [Fact]
    public void EmitOptionalBoundType_Swift_UsesNullablePointer()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithOptionalClassInit("OptAnimView", "animation", "TestModule.AnimationAsset") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("_ animationPtr: UnsafeMutableRawPointer?", swiftContent);
        Assert.Contains("Unmanaged<AnimationAsset>.fromOpaque($0).takeUnretainedValue()", swiftContent);
    }

    [Fact]
    public void EmitOptionalBoundType_CSharp_UsesNullableFactoryParam()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithOptionalClassInit("OptAnimView", "animation", "TestModule.AnimationAsset") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("AnimationAsset? animation", csContent); // Factory param
        Assert.Contains("IntPtr animation", csContent); // P/Invoke (single IntPtr, no hasValue)
        Assert.Contains("animation?.Payload.DangerousGetHandle() ?? IntPtr.Zero", csContent);
    }

    [Fact]
    public void EmitOptionalBoundType_GeneratesFunctionalBridge_NotTemplate()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithOptionalClassInit("OptAnimView", "animation", "TestModule.AnimationAsset") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_TestModule_OptAnimView_Create\")", swiftContent);
        Assert.DoesNotContain("BRIDGE TEMPLATE", swiftContent);
    }

    #endregion

    #region BoundStruct

    [Fact]
    public void InitAnalyzer_MapsBoundStruct_ForFrozenWithMemoryStruct()
    {
        var typeDb = CreateFrozenWithMemoryStructTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithNamedType("config", "TestModule.Config");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.BoundStruct, result[0].Kind);
        Assert.Equal(StructProjectionKind.FrozenWithMemory, result[0].StructProjection);
        Assert.Equal("UnsafeMutableRawPointer", result[0].SwiftAbiType);
        Assert.Equal("IntPtr", result[0].CSharpPInvokeType);
    }

    [Fact]
    public void InitAnalyzer_FallsBackToTemplate_ForFrozenBlittableStruct()
    {
        var typeDb = CreateFrozenBlittableStructTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithNamedType("config", "TestModule.Config");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.Null(result);
    }

    [Fact]
    public void InitAnalyzer_OptionalBoundStruct_MapsCorrectly()
    {
        var typeDb = CreateStructTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithOptionalType("config", "TestModule.Config");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.OptionalWrapped, result[0].Kind);
        Assert.NotNull(result[0].InnerParameter);
        Assert.Equal(BridgeParameterKind.BoundStruct, result[0].InnerParameter!.Kind);
        Assert.Equal("Config", result[0].InnerParameter!.BridgeTypeName);
        Assert.Equal("UnsafeMutableRawPointer?", result[0].SwiftAbiType);
        Assert.Equal("IntPtr", result[0].CSharpPInvokeType);
    }

    [Fact]
    public void EmitBoundStruct_Swift_UsesPointeeNotUnmanaged()
    {
        var typeDb = CreateStructTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithClassInit("ConfigView", "config", "TestModule.Config") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains(".pointee", swiftContent);
        Assert.DoesNotContain("Unmanaged<Config>", swiftContent);
    }

    [Fact]
    public void EmitBoundStruct_Swift_PassesUnsafeMutableRawPointerToCdecl()
    {
        var typeDb = CreateStructTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithClassInit("ConfigView", "config", "TestModule.Config") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("_ configPtr: UnsafeMutableRawPointer", swiftContent);
    }

    [Fact]
    public void EmitBoundStruct_Swift_GeneratesFunctionalBridge()
    {
        var typeDb = CreateStructTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithClassInit("ConfigView", "config", "TestModule.Config") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_TestModule_ConfigView_Create\")", swiftContent);
        Assert.DoesNotContain("BRIDGE TEMPLATE", swiftContent);
    }

    [Fact]
    public void EmitBoundStruct_Swift_SessionStoresStructField()
    {
        var typeDb = CreateStructTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithClassInit("ConfigView", "config", "TestModule.Config") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // With state binding, BoundStruct goes to State as @Published
        Assert.Contains("@Published var config: Config", swiftContent);
        Assert.Contains("config: state.config", swiftContent);
    }

    [Fact]
    public void EmitOptionalBoundStruct_Swift_UsesPointeeMap()
    {
        var typeDb = CreateStructTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithOptionalClassInit("OptConfigView", "config", "TestModule.Config") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains(".pointee", swiftContent);
        Assert.Contains(".map", swiftContent);
        Assert.DoesNotContain("Unmanaged<Config>", swiftContent);
    }

    [Fact]
    public void EmitBoundStruct_CSharp_UsesIntPtrPInvoke()
    {
        var typeDb = CreateStructTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithClassInit("ConfigView", "config", "TestModule.Config") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("IntPtr config", csContent);
    }

    [Fact]
    public void EmitBoundStruct_CSharp_UsesTypedFactoryParam()
    {
        var typeDb = CreateStructTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithClassInit("ConfigView", "config", "TestModule.Config") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("Config config", csContent);
        Assert.Contains("config.Payload.DangerousGetHandle()", csContent);
    }

    [Fact]
    public void AsyncFlatParam_BoundStruct_MappedFromBridgeParam()
    {
        var bp = new BridgeParameter("config", BridgeParameterKind.BoundStruct,
            SwiftAbiType: "UnsafeMutableRawPointer", CSharpPInvokeType: "IntPtr",
            BridgeTypeName: "Config", CSharpTypeName: "TestModule.Config",
            StructProjection: StructProjectionKind.NonFrozen);

        // Use reflection to test BridgeParamToFlatParam (private)
        var method = typeof(SwiftUIBridgeEmitter).GetMethod("BridgeParamToFlatParam",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = method?.Invoke(null, new object[] { bp }) as AsyncFlatParam;

        Assert.NotNull(result);
        Assert.Equal(AsyncFlatParamKind.BoundStruct, result!.Kind);
        Assert.Equal("Config", result.BridgeTypeName);
        Assert.Equal("UnsafeMutableRawPointer", result.SwiftAbiType);
    }

    [Fact]
    public void AsyncInference_BoundStruct_IncludedInFlattenedParams()
    {
        // Build a simple async pattern with a BoundStruct flattened param.
        // Config is cross-module (OtherModule.Config) so it resolves via TypeDatabase as a leaf,
        // not as a same-module type that needs constructor resolution.
        var moduleDecl = CreateInferenceModuleDecl("TestModule");
        var asyncService = CreateClassTypeDecl("AsyncService", "TestModule");
        asyncService.Methods.Add(CreateAsyncThrowsCtor("config", "OtherModule.Config"));
        moduleDecl.Types.Add(asyncService);

        // Cross-module struct in TypeDatabase
        var typeDb = new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["OtherModule.Config"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.OtherModule", "Config"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("OtherModule.Config"),
                MetadataAccessor = "$s11OtherModule6ConfigVMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
            },
        });

        var view = CreateViewStructWithNoConstructor("AsyncConfigView");
        view.Methods.Add(CreateCtorWithNamedParam("service", "TestModule.AsyncService"));
        moduleDecl.Types.Add(view);

        var info = new ViewBridgeInfo("AsyncConfigView", "TestModule",
            ViewInitClassification.Simple, null, view.Methods.Where(m => m.IsConstructor).ToList());
        var context = new BridgeContext(TypeDatabase: typeDb, ModuleDecl: moduleDecl);

        var pattern = SwiftUIBridgeEmitter.InferAsyncPattern(info, context);

        Assert.NotNull(pattern);
        Assert.Contains(pattern!.FlattenedParams, p => p.Kind == AsyncFlatParamKind.BoundStruct && p.Name == "config");
    }

    [Fact]
    public void AsyncEmission_BoundStruct_SwiftUsesPointee()
    {
        // Full emission test: async view with BoundStruct leaf param must use .pointee, not Unmanaged.
        var moduleDecl = CreateInferenceModuleDecl("TestModule");
        var asyncService = CreateClassTypeDecl("AsyncService", "TestModule");
        asyncService.Methods.Add(CreateAsyncThrowsCtor("config", "OtherModule.Config"));
        moduleDecl.Types.Add(asyncService);

        // Cross-module struct in TypeDatabase
        var typeDb = new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["OtherModule.Config"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.OtherModule", "Config"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("OtherModule.Config"),
                MetadataAccessor = "$s11OtherModule6ConfigVMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
            },
        });

        var view = CreateViewStructWithNoConstructor("AsyncConfigView");
        view.Methods.Add(CreateCtorWithNamedParam("service", "TestModule.AsyncService"));
        moduleDecl.Types.Add(view);

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule",
            new List<TypeDecl> { view }, NullLogger.Instance, typeDb, moduleDecl);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains(".assumingMemoryBound(to: Config.self).pointee", swiftContent);
        Assert.DoesNotContain("Unmanaged<Config>", swiftContent);
    }

    #endregion

    #region TypedClosure

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
    public void InitAnalyzer_TypedClosure_StringArg_IsSupported()
    {
        var ctor = CreateConstructorWithTypedClosure("callback",
            new NamedTypeSpec("Swift.String"), TupleTypeSpec.Empty);
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.TypedClosure, result[0].Kind);
        Assert.NotNull(result[0].ClosureArguments);
        Assert.Single(result[0].ClosureArguments!);
        Assert.Equal(BridgeParameterKind.String, result[0].ClosureArguments![0].Kind);
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

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
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

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // Should generate Swift closure with typed arg
        Assert.Contains("arg0: Int", swiftContent);
        Assert.Contains("cb_callback?", swiftContent);
    }

    [Fact]
    public void EmitTypedClosure_Swift_BoolArgConvertsToInt32()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "toggle",
            new NamedTypeSpec("Swift.Bool"), TupleTypeSpec.Empty) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("arg0: Bool", swiftContent);
        Assert.Contains("arg0 ? 1 : 0", swiftContent); // Bool → Int32 conversion
    }

    [Fact]
    public void EmitTypedClosure_Swift_ReturnBoolConvertsFromInt32()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "validator",
            new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("Swift.Bool")) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("-> Bool", swiftContent);
        Assert.Contains("!= 0", swiftContent); // Int32 → Bool conversion
    }

    [Fact]
    public void EmitTypedClosure_CSharp_GeneratesTypedTrampoline()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "callback",
            new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
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

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("private static int ValidatorTrampoline", csContent); // Bool return → int
        Assert.Contains("Func<nint, bool>", csContent); // Delegate type
        Assert.Contains("result ? 1 : 0", csContent); // Bool → int conversion
    }

    [Fact]
    public void EmitTypedClosure_CSharp_GeneratesActionFactoryParam()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "callback",
            new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("Action<nint>? callback", csContent);
    }

    [Fact]
    public void EmitTypedClosure_CSharp_GeneratesFuncFactoryParam()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "validator",
            new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("Swift.Bool")) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("Func<nint, bool>? validator", csContent);
    }

    [Fact]
    public void EmitTypedClosure_CSharp_GeneratesTypedFunctionPointer()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "callback",
            new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("delegate* unmanaged[Cdecl]<nint, IntPtr, void>", csContent);
    }

    [Fact]
    public void EmitTypedClosure_CSharp_UnsafeFactory()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "callback",
            new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("unsafe CbViewSession Create(", csContent);
    }

    [Fact]
    public void EmitTypedClosure_CSharp_HasGCHandleCleanup()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "callback",
            new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("_closureHandles", csContent);
        Assert.Contains("closureHandles.Add(h)", csContent);
        Assert.Contains("h.IsAllocated", csContent);
    }

    [Fact]
    public void EmitTypedClosure_GeneratesFunctionalBridge_NotTemplate()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "callback",
            new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
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

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
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

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
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
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
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
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var report = ReportCollector.Complete()!;
        Assert.NotNull(report);
        Assert.Equal(2, report.BridgedViews.Count);
        Assert.All(report.BridgedViews, v => Assert.Equal("TemplatePending", v.BridgeStatus));

        ReportCollector.Reset();
    }

    #endregion

    #region Nullable Enable

    [Fact]
    public void EmitBridgeFiles_CSharpBridge_HasNullableEnableOnSecondLine()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("TestView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csPath = Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs");
        var lines = File.ReadAllLines(csPath);
        Assert.True(lines.Length >= 2, "C# bridge file should have at least 2 lines");
        Assert.StartsWith("// Auto-generated by SwiftBindings", lines[0]);
        Assert.Equal("#nullable enable", lines[1]);
    }

    [Fact]
    public void CleanupAutoGeneratedBridgeFiles_StillDeletesFilesWithNullableEnable()
    {
        // Write a file matching the new format (marker on line 1, #nullable enable on line 2)
        var csPath = Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs");
        File.WriteAllText(csPath, "// Auto-generated by SwiftBindings — SwiftUI Bridge\n#nullable enable\nusing System;\n");

        SwiftUIBridgeEmitter.CleanupAutoGeneratedBridgeFiles(_tempDir, "TestModule", NullLogger.Instance);

        Assert.False(File.Exists(csPath), "Auto-generated bridge file with #nullable enable should be deleted");
    }

    #endregion

    #region SwiftUIBridgeCollector Dedup

    [Fact]
    public void Collect_DuplicateViewNames_OnlyFirstIsCollected()
    {
        var ctx = new ModuleEmissionContext();

        var view1 = CreateSimpleViewStruct("DuplicateView");
        var view2 = CreateSimpleViewStruct("DuplicateView");
        view2.SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("OtherModule.DuplicateView");

        SwiftUIBridgeCollector.Collect(view1, ctx);
        SwiftUIBridgeCollector.Collect(view2, ctx);

        var collected = SwiftUIBridgeCollector.GetCollectedViews(ctx);
        Assert.Single(collected);
        Assert.Same(view1, collected[0]);
    }

    [Fact]
    public void Collect_DifferentNames_BothCollected()
    {
        var ctx = new ModuleEmissionContext();

        SwiftUIBridgeCollector.Collect(CreateSimpleViewStruct("ViewA"), ctx);
        SwiftUIBridgeCollector.Collect(CreateSimpleViewStruct("ViewB"), ctx);

        var collected = SwiftUIBridgeCollector.GetCollectedViews(ctx);
        Assert.Equal(2, collected.Count);
    }

    [Fact]
    public void FreshContext_ClearsDedupState()
    {
        var ctx1 = new ModuleEmissionContext();
        SwiftUIBridgeCollector.Collect(CreateSimpleViewStruct("ResetView"), ctx1);

        // A fresh context has independent dedup state — same name is collected again.
        var ctx2 = new ModuleEmissionContext();
        SwiftUIBridgeCollector.Collect(CreateSimpleViewStruct("ResetView"), ctx2);
        var collected = SwiftUIBridgeCollector.GetCollectedViews(ctx2);
        Assert.Single(collected);
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

    /// <summary>
    /// A View struct that bridges as a simple no-argument <c>TypeName()</c>.
    /// Models a real publicly-constructible view: it carries a public no-arg
    /// <c>init()</c> so the bridge can call <c>TypeName()</c> from the separate
    /// <c>{Module}Bridge</c> module. (A view with no public init is
    /// unconstructible and is skipped — see
    /// <c>CreateViewStructWithNoConstructor</c>.)
    /// </summary>
    private static StructDecl CreateSimpleViewStruct(string name)
    {
        var view = CreateViewStructWithNoConstructor(name);
        view.Methods.Add(CreateNoArgConstructor(name));
        return view;
    }

    /// <summary>
    /// A View struct with zero public constructors — i.e. no accessible
    /// initializer. Used as the base for views that add their own specific
    /// constructor, and to exercise the "skip unconstructible view" path.
    /// </summary>
    private static StructDecl CreateViewStructWithNoConstructor(string name)
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

    private static MethodDecl CreateNoArgConstructor(string viewName)
    {
        return new MethodDecl
        {
            Name = "init",
            MangledName = $"$s10TestModule{viewName.Length}{viewName}CACycfc",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                // Return type (index 0) — no parameters follow (public init())
                new ArgumentDecl
                {
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec($"TestModule.{viewName}"),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
            },
        };
    }

    private static StructDecl CreateViewWithVoidClosureInit(string name, string closureParamName)
    {
        var view = CreateViewStructWithNoConstructor(name);
        view.Methods.Add(CreateConstructorWithVoidClosure(closureParamName));
        return view;
    }

    private static StructDecl CreateViewWithUnsupportedParam(string name)
    {
        var view = CreateViewStructWithNoConstructor(name);
        view.Methods.Add(new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
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
            IsSynthesizedAccessor = false,
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
            IsSynthesizedAccessor = false,
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
            IsSynthesizedAccessor = false,
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
            IsSynthesizedAccessor = false,
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
            IsSynthesizedAccessor = false,
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
            IsSynthesizedAccessor = false,
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
        var view = CreateViewStructWithNoConstructor(viewName);
        view.Methods.Add(CreateConstructorWithNamedType(paramName, enumTypeName));
        return view;
    }

    private static StructDecl CreateViewWithOptionalPrimitiveInit(string viewName, string paramName, string innerTypeName)
    {
        var view = CreateViewStructWithNoConstructor(viewName);
        view.Methods.Add(CreateConstructorWithOptionalPrimitive(paramName, innerTypeName));
        return view;
    }

    private static StructDecl CreateViewWithOptionalEnumInit(string viewName, string paramName, string enumTypeName)
    {
        var view = CreateViewStructWithNoConstructor(viewName);
        view.Methods.Add(CreateConstructorWithOptionalPrimitive(paramName, enumTypeName));
        return view;
    }

    private static StructDecl CreateViewWithClassInit(string viewName, string paramName, string classTypeName)
    {
        var view = CreateViewStructWithNoConstructor(viewName);
        view.Methods.Add(CreateConstructorWithNamedType(paramName, classTypeName));
        return view;
    }

    private static StructDecl CreateViewWithOptionalClassInit(string viewName, string paramName, string classTypeName)
    {
        var view = CreateViewStructWithNoConstructor(viewName);
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
            IsSynthesizedAccessor = false,
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
        var view = CreateViewStructWithNoConstructor(viewName);
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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "AlertStyle"),
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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "LogLevel"),
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
            ["TestModule.AnimationAsset"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "AnimationAsset"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.AnimationAsset"),
                MetadataAccessor = "$s10TestModule15AnimationAssetCMa",
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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Config"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Config"),
                MetadataAccessor = "$s10TestModule6ConfigVMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
            },
        });
    }

    private static ITypeDatabase CreateFrozenWithMemoryStructTypeDatabase()
    {
        return new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["TestModule.Config"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Config"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Config"),
                MetadataAccessor = "$s10TestModule6ConfigVMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
            },
        });
    }

    private static ITypeDatabase CreateFrozenBlittableStructTypeDatabase()
    {
        return new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["TestModule.Config"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Config"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Config"),
                MetadataAccessor = "$s10TestModule6ConfigVMa",
                Flags = TypeRecordFlags.Frozen,
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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Direction"),
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
        private readonly string _asyncLibraryName;

        public string AsyncLibraryName => _asyncLibraryName;

        public BridgeTestTypeDatabase(Dictionary<string, TypeRecord> types, string asyncLibraryName = "")
        {
            _types = types;
            _asyncLibraryName = asyncLibraryName;
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) =>
            _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, out TypeRecord record)
        {
            return _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record);
        }

        public string GetLibraryPath(string moduleName) => "";

        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion

    #region Async Inference

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
        var view = CreateViewStructWithNoConstructor("TestView");
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

        var view = CreateViewStructWithNoConstructor("AsyncServiceView");
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
    public void EmitAsyncCSharpBridge_ComplexBoundEnumLeaf_UsesRawValueNotIntCast()
    {
        // An async View whose init takes an async same-module dependency plus a
        // complex (class-backed) raw-value enum leaf. A complex enum is a C# class
        // exposing a .RawValue property — so the native call arg must read .RawValue.
        // Casting the enum class to (int) is CS0030 and breaks the binding's compile.
        var moduleDecl = CreateInferenceModuleDecl("TestModule");
        var asyncService = CreateClassTypeDecl("AsyncService", "TestModule");
        asyncService.Methods.Add(CreateAsyncThrowsCtor("key", "Swift.String"));
        moduleDecl.Types.Add(asyncService);

        var view = CreateViewStructWithNoConstructor("AlertView");
        view.Methods.Add(CreateCtorWithTwoParams(
            "service", "TestModule.AsyncService",
            "style", "TestModule.AlertStyle"));
        moduleDecl.Types.Add(view);

        // AlertStyle: Frozen, no SimpleEnum flag -> a complex (class-backed) raw-value enum.
        var context = new BridgeContext(TypeDatabase: CreateEnumTypeDatabase(), ModuleDecl: moduleDecl);
        var info = new ViewBridgeInfo("AlertView", "TestModule",
            ViewInitClassification.AsyncDependency, null, view.Methods.Where(m => m.IsConstructor).ToList());

        var pattern = SwiftUIBridgeEmitter.InferAsyncPattern(info, context);
        Assert.NotNull(pattern);
        var enumParam = Assert.Single(pattern!.FlattenedParams, p => p.Kind == AsyncFlatParamKind.BoundEnum);
        Assert.False(enumParam.IsSimpleEnum);

        var sb = new System.Text.StringBuilder();
        SwiftUIBridgeEmitter.EmitAsyncCSharpBridge(sb, "TestModule", info, pattern, null);
        var cs = sb.ToString();

        Assert.Contains("style.RawValue", cs);
        Assert.DoesNotContain("(int)style", cs);
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

        var view = CreateViewStructWithNoConstructor("DeepChainView");
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

        var view = CreateViewStructWithNoConstructor("DeepView");
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

        var view = CreateViewStructWithNoConstructor("TestView");
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

        var view = CreateViewStructWithNoConstructor("TestView");
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

        var view = CreateViewStructWithNoConstructor("CycleView");
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

        var view = CreateViewStructWithNoConstructor("AsyncServiceView");
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

        var view = CreateViewStructWithNoConstructor("AsyncServiceView");
        view.Methods.Add(CreateCtorWithNamedParam("service", "TestModule.AsyncService"));
        moduleDecl.Types.Add(view);

        // TypeDatabase has AsyncService registered as a Class
        var typeDb = new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["TestModule.AsyncService"] = new TypeRecord
            {
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.AsyncService"),
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "AsyncService"),
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

        var view = CreateViewStructWithNoConstructor("DAGView");
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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "AsyncDep"),
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

    #region Data-Driven Async Emission

    [Fact]
    public void DataDrivenSwift_AsyncServiceView_EmitsCdeclCreate()
    {
        // Build: AsyncService(key: String) async throws → AsyncServiceView(service: AsyncService)
        var moduleDecl = CreateInferenceModuleDecl("TestModule");
        var asyncService = CreateClassTypeDecl("AsyncService", "TestModule");
        asyncService.Methods.Add(CreateAsyncThrowsCtor("key", "Swift.String"));
        moduleDecl.Types.Add(asyncService);

        var view = CreateViewStructWithNoConstructor("AsyncServiceView");
        view.Methods.Add(CreateCtorWithNamedParam("service", "TestModule.AsyncService"));
        moduleDecl.Types.Add(view);

        var views = new List<TypeDecl> { view };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, moduleDecl: moduleDecl);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));

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

        var view = CreateViewStructWithNoConstructor("DeepChainView");
        view.Methods.Add(CreateCtorWithNamedParam("processor", "TestModule.Processor"));
        moduleDecl.Types.Add(view);

        var views = new List<TypeDecl> { view };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, moduleDecl: moduleDecl);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));

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

        var view = CreateViewStructWithNoConstructor("AsyncServiceView");
        view.Methods.Add(CreateCtorWithNamedParam("service", "TestModule.AsyncService"));
        moduleDecl.Types.Add(view);

        var views = new List<TypeDecl> { view };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, moduleDecl: moduleDecl);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));

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

        var view = CreateViewStructWithNoConstructor("BoolAsyncView");
        view.Methods.Add(CreateCtorWithTwoParams(
            "service", "TestModule.AsyncService",
            "enabled", "Swift.Bool"));
        moduleDecl.Types.Add(view);

        var views = new List<TypeDecl> { view };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, moduleDecl: moduleDecl);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));

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

        var view = CreateViewStructWithNoConstructor("MixedAsyncView");
        view.Methods.Add(CreateCtorWithThreeParams(
            "service", "TestModule.AsyncService",
            "count", "Swift.Int32",
            "enabled", "Swift.Bool"));
        moduleDecl.Types.Add(view);

        var views = new List<TypeDecl> { view };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, moduleDecl: moduleDecl);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));

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

        var view = CreateViewStructWithNoConstructor("DeepChainView");
        view.Methods.Add(CreateCtorWithNamedParam("processor", "TestModule.Processor"));
        moduleDecl.Types.Add(view);

        var views = new List<TypeDecl> { view };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, moduleDecl: moduleDecl);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));

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

        var view = CreateViewStructWithNoConstructor("CrossModuleView");
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

        var view = CreateViewStructWithNoConstructor("CrossModuleView");
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

        var view = CreateViewStructWithNoConstructor("DirectCrossModuleView");
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

        var view = CreateViewStructWithNoConstructor("ImportTestView");
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

        var view = CreateViewStructWithNoConstructor("CrossModuleBridgeView");
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
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDatabase: typeDb, moduleDecl: moduleDecl);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));

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

        var view = CreateViewStructWithNoConstructor("CrossModuleBridgeView");
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
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDatabase: typeDb, moduleDecl: moduleDecl);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));

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
            IsSynthesizedAccessor = false,
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
            IsSynthesizedAccessor = false,
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
            IsSynthesizedAccessor = false,
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
            IsSynthesizedAccessor = false,
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

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
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

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, bridgeHintsPath: hintsPath);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
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

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, bridgeHintsPath: hintsPath);

        // All views skipped → no bridge files created (stale cleanup)
        Assert.False(File.Exists(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift")));
        Assert.False(File.Exists(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs")));
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
            SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
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

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, bridgeHintsPath: hintsPath);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("BRIDGE TEMPLATE: ForceTemplateView", swiftContent);
        // No functional @_cdecl for this view (template has it commented out)
        var nonCommentLines = swiftContent.Split('\n').Where(l => !l.TrimStart().StartsWith("//"));
        Assert.DoesNotContain(nonCommentLines, l => l.Contains("SBW_TestModule_ForceTemplateView_Create"));
    }

    [Fact]
    public void BridgeHints_PreferredInitHint_SelectsCorrectConstructor()
    {
        // Create a view with 2 constructors: [0] has void closure, [1] has Int
        var view = CreateViewStructWithNoConstructor("MultiInitView");
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

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, bridgeHintsPath: hintsPath);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // Constructor [1] has Int param → Swift ABI uses "Int" type
        Assert.Contains("count: Int", swiftContent);
        // Constructor [0] has closure — it should NOT be used
        Assert.DoesNotContain("onTapCallback", swiftContent);
    }

    [Fact]
    public void BridgeHints_PreferredInitHint_OutOfRange_FallsBackWithWarning()
    {
        var testLogger = new TestLogger();
        var view = CreateViewStructWithNoConstructor("SingleInitView");
        view.Methods.Add(CreateConstructorWithPrimitive("count", "Swift.Int"));
        var views = new List<TypeDecl> { view };
        var hintsPath = CreateBridgeHintsFile(_tempDir, """
        {
            "views": {
                "SingleInitView": { "preferredInit": 99 }
            }
        }
        """);

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            testLogger, bridgeHintsPath: hintsPath);

        // Warning about out-of-range index
        Assert.Contains(testLogger.Messages, m => m.Contains("preferredInit") && m.Contains("out of range"));
        // Falls back to constructor [0]
        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
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

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            testLogger, bridgeHintsPath: hintsPath);

        // Warning about malformed JSON
        Assert.Contains(testLogger.Messages, m => m.Contains("Malformed bridge hints"));
        // Still generates output normally
        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
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

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            testLogger, bridgeHintsPath: hintsPath);

        // Warnings about unknown keys
        Assert.Contains(testLogger.Messages, m => m.Contains("unknown key 'unknownRoot'"));
        Assert.Contains(testLogger.Messages, m => m.Contains("unknown key 'futureKey'"));
        // Still loads and generates output
        Assert.True(File.Exists(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift")));
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

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, bridgeHintsPath: hintsPath);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
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

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, bridgeHintsPath: hintsPath);

        Assert.False(File.Exists(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift")));
        Assert.False(File.Exists(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs")));
    }

    [Fact]
    public void BridgeHints_AllViewsSkipped_DeletesAutoGeneratedBridgeFiles()
    {
        // Pre-create auto-generated bridge files (contain marker)
        var staleSwift = Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift");
        var staleCs = Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs");
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

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, bridgeHintsPath: hintsPath);

        Assert.False(File.Exists(staleSwift));
        Assert.False(File.Exists(staleCs));
    }

    [Fact]
    public void BridgeHints_AllViewsSkipped_PreservesUserMaintainedBridgeFiles()
    {
        // Pre-create user-maintained bridge files (no auto-generated marker)
        var userSwift = Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift");
        var userCs = Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs");
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

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
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

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, bridgeHintsPath: hintsPath);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
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

    #region Closure String/Class Args + Optional<String/Closure>

    [Fact]
    public void InitAnalyzer_TypedClosure_StringArg_AbiHasPtrAndLen()
    {
        var ctor = CreateConstructorWithTypedClosure("callback",
            new NamedTypeSpec("Swift.String"), TupleTypeSpec.Empty);
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.NotNull(result);
        // ABI type should include ptr + Int for length
        Assert.Contains("UnsafePointer<UInt8>?", result[0].SwiftAbiType);
        Assert.Contains("Int", result[0].SwiftAbiType);
    }

    [Fact]
    public void InitAnalyzer_TypedClosure_ClassArg_IsSupported_WithTypeDatabase()
    {
        var typeDb = CreateClassTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithTypedClosure("onModel",
            new NamedTypeSpec("TestModule.AnimationAsset"), TupleTypeSpec.Empty);
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.TypedClosure, result[0].Kind);
        Assert.NotNull(result[0].ClosureArguments);
        Assert.Single(result[0].ClosureArguments!);
        Assert.Equal(BridgeParameterKind.BoundType, result[0].ClosureArguments![0].Kind);
        Assert.Equal("AnimationAsset", result[0].ClosureArguments![0].BridgeTypeName);
    }

    [Fact]
    public void InitAnalyzer_TypedClosure_ClassArg_Rejected_WithoutTypeDatabase()
    {
        var ctor = CreateConstructorWithTypedClosure("onModel",
            new NamedTypeSpec("TestModule.AnimationAsset"), TupleTypeSpec.Empty);
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.Null(result);
    }

    [Fact]
    public void InitAnalyzer_TypedClosure_EnumArg_StillRejected()
    {
        // Enums in closures are not supported — only classes via TypeDB
        var typeDb = CreateEnumTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithTypedClosure("onStyle",
            new NamedTypeSpec("TestModule.AlertStyle"), TupleTypeSpec.Empty);
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.Null(result);
    }

    [Fact]
    public void InitAnalyzer_TypedClosure_MixedStringAndPrimitive_IsSupported()
    {
        // (String, Int32) -> Void
        var argsTuple = new TupleTypeSpec(new TypeSpec[]
        {
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Swift.Int32"),
        });
        var ctor = CreateConstructorWithTypedClosure("handler", argsTuple, TupleTypeSpec.Empty);
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.NotNull(result);
        Assert.Equal(2, result[0].ClosureArguments!.Count);
        Assert.Equal(BridgeParameterKind.String, result[0].ClosureArguments![0].Kind);
        Assert.Equal(BridgeParameterKind.Primitive, result[0].ClosureArguments![1].Kind);
    }

    [Fact]
    public void InitAnalyzer_TypedClosure_StringReturn_IsSupported()
    {
        var ctor = CreateConstructorWithTypedClosure("transformer",
            new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("Swift.String"));
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.TypedClosure, result[0].Kind);
        Assert.NotNull(result[0].ClosureReturn);
        Assert.Equal(BridgeParameterKind.String, result[0].ClosureReturn!.Kind);
    }

    [Fact]
    public void InitAnalyzer_TypedClosure_StringReturn_AbiHasRetLenOutParam()
    {
        var ctor = CreateConstructorWithTypedClosure("transformer",
            new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("Swift.String"));
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.NotNull(result);
        // ABI should include retLen out-parameter and UnsafePointer return
        Assert.Contains("UnsafeMutablePointer<Int>", result[0].SwiftAbiType);
        Assert.Contains("UnsafePointer<UInt8>?", result[0].SwiftAbiType);
    }

    [Fact]
    public void InitAnalyzer_TypedClosure_VoidToString_IsSupported()
    {
        var ctor = CreateConstructorWithTypedClosure("getter",
            TupleTypeSpec.Empty, new NamedTypeSpec("Swift.String"));
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.TypedClosure, result[0].Kind);
        Assert.NotNull(result[0].ClosureReturn);
        Assert.Equal(BridgeParameterKind.String, result[0].ClosureReturn!.Kind);
        Assert.Empty(result[0].ClosureArguments!);
    }

    [Fact]
    public void InitAnalyzer_TypedClosure_ClassReturn_IsSupported_WithTypeDatabase()
    {
        var typeDb = CreateClassTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithTypedClosure("factory",
            new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("TestModule.AnimationAsset"));
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.TypedClosure, result[0].Kind);
        Assert.NotNull(result[0].ClosureReturn);
        Assert.Equal(BridgeParameterKind.BoundType, result[0].ClosureReturn!.Kind);
        Assert.Equal("AnimationAsset", result[0].ClosureReturn!.BridgeTypeName);
    }

    [Fact]
    public void InitAnalyzer_TypedClosure_ClassReturn_FallsBackToTemplate_WithoutTypeDatabase()
    {
        var ctor = CreateConstructorWithTypedClosure("factory",
            new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("TestModule.AnimationAsset"));
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        // Without TypeDatabase, class returns can't be mapped
        Assert.Null(result);
    }

    [Fact]
    public void InitAnalyzer_TypedClosure_EnumReturn_StillRejected()
    {
        // Enum returns are not supported (only primitives, String, and class)
        var typeDb = CreateEnumTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithTypedClosure("getter",
            TupleTypeSpec.Empty, new NamedTypeSpec("TestModule.AlertStyle"));
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.Null(result);
    }

    [Fact]
    public void InitAnalyzer_OptionalVoidClosure_MapsToVoidClosure()
    {
        var ctor = CreateConstructorWithOptionalClosure("callback");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.VoidClosure, result[0].Kind);
        Assert.True(result[0].HasUserData);
    }

    [Fact]
    public void InitAnalyzer_OptionalTypedClosure_MapsToTypedClosure()
    {
        var ctor = CreateConstructorWithOptionalTypedClosure("callback",
            new NamedTypeSpec("Swift.Int32"), TupleTypeSpec.Empty);
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.TypedClosure, result[0].Kind);
        Assert.NotNull(result[0].ClosureArguments);
        Assert.Single(result[0].ClosureArguments!);
    }

    [Fact]
    public void EmitStringClosureArg_Swift_ContainsUtf8Encoding()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "callback",
            new NamedTypeSpec("Swift.String"), TupleTypeSpec.Empty) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("Array(arg0.utf8)", swiftContent);
        Assert.Contains("withUnsafeBufferPointer", swiftContent);
    }

    [Fact]
    public void EmitStringClosureArg_CSharp_TrampolineDecodesUtf8()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "callback",
            new NamedTypeSpec("Swift.String"), TupleTypeSpec.Empty) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("Encoding.UTF8.GetString", csContent);
        Assert.Contains("arg0Ptr", csContent);
        Assert.Contains("arg0Len", csContent);
    }

    [Fact]
    public void EmitStringClosureArg_CSharp_FnPtrHasPtrAndLen()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "callback",
            new NamedTypeSpec("Swift.String"), TupleTypeSpec.Empty) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("delegate* unmanaged[Cdecl]<IntPtr, nint, IntPtr, void>", csContent);
    }

    [Fact]
    public void EmitStringClosureArg_CSharp_DelegateUsesString()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "callback",
            new NamedTypeSpec("Swift.String"), TupleTypeSpec.Empty) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("Action<string>", csContent);
    }

    [Fact]
    public void EmitStringClosureWithReturn_Swift_HasReturnType()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "validator",
            new NamedTypeSpec("Swift.String"), new NamedTypeSpec("Swift.Int32")) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // Closure should have return type annotation and return expression
        Assert.Contains("-> Int32 in", swiftContent);
        Assert.Contains("return cb_validator", swiftContent);
        Assert.Contains("?? 0", swiftContent);
        // Each withUnsafeBufferPointer must have `return` prefix for non-void closures
        Assert.Contains("return arg0Bytes.withUnsafeBufferPointer", swiftContent);
    }

    [Fact]
    public void EmitStringClosureWithReturn_CSharp_HasFuncDelegate()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "validator",
            new NamedTypeSpec("Swift.String"), new NamedTypeSpec("Swift.Int32")) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("Func<string, int>", csContent);
    }

    [Fact]
    public void EmitClassClosureArg_Swift_UsesUnmanagedPassRetained()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "onModel",
            new NamedTypeSpec("TestModule.AnimationAsset"), TupleTypeSpec.Empty) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("Unmanaged.passRetained(arg0).toOpaque()", swiftContent);
    }

    [Fact]
    public void EmitClassClosureArg_CSharp_UsesMarshalFromSwift()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "onModel",
            new NamedTypeSpec("TestModule.AnimationAsset"), TupleTypeSpec.Empty) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        // Direct MarshalFromSwift in the trampoline body — no NativeMemory buffer
        // needed for the class closure marshalling step. (Dispose has its own
        // unrelated NativeMemory.Alloc for the post-release GCHandle buffer.)
        var trampolineStart = csContent.IndexOf("OnModelTrampoline");
        Assert.True(trampolineStart >= 0, "Trampoline missing");
        var trampolineEnd = csContent.IndexOf("        }", trampolineStart);
        var trampolineBody = csContent.Substring(trampolineStart, trampolineEnd - trampolineStart);
        Assert.DoesNotContain("NativeMemory.Alloc", trampolineBody);
        Assert.Contains("SwiftMarshal.MarshalFromSwift", csContent);
    }

    [Fact]
    public void EmitClassClosureArg_CSharp_DelegateUsesTypedClassName()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "onModel",
            new NamedTypeSpec("TestModule.AnimationAsset"), TupleTypeSpec.Empty) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("Action<TestModule.AnimationAsset>", csContent);
    }

    #endregion

    #region Closure Non-Primitive Returns

    [Fact]
    public void EmitStringReturnClosure_Swift_HasRetLenOutParameter()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "transformer",
            new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("Swift.String")) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("UnsafeMutablePointer<Int>", swiftContent);
        Assert.Contains("UnsafePointer<UInt8>?", swiftContent);
    }

    [Fact]
    public void EmitStringReturnClosure_Swift_DecodesReturnedBuffer()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "transformer",
            new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("Swift.String")) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // Swift decodes the returned UTF-8 buffer
        Assert.Contains("var retLen: Int = 0", swiftContent);
        Assert.Contains("withUnsafeMutablePointer(to: &retLen)", swiftContent);
        Assert.Contains("retPtr?.deallocate()", swiftContent);
        Assert.Contains("UnsafeBufferPointer(start: retBuf, count: retLen)", swiftContent);
    }

    [Fact]
    public void EmitStringReturnClosure_CSharp_HasNativeMemoryAlloc()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "transformer",
            new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("Swift.String")) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("NativeMemory.Alloc", csContent);
        Assert.Contains("Encoding.UTF8.GetBytes(result", csContent);
        Assert.Contains("retLenPtr", csContent);
    }

    [Fact]
    public void EmitStringReturnClosure_CSharp_HasFuncStringDelegate()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "transformer",
            new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("Swift.String")) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("Func<int, string?>", csContent);
    }

    [Fact]
    public void EmitStringReturnClosure_CSharp_FnPtrHasRetLenParam()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "transformer",
            new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("Swift.String")) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        // fn ptr type should have extra IntPtr for retLenPtr: <int, IntPtr, IntPtr, IntPtr>
        // args: int (arg0), IntPtr (retLenPtr), IntPtr (userData), IntPtr (return)
        Assert.Contains("delegate* unmanaged[Cdecl]<int, IntPtr, IntPtr, IntPtr>", csContent);
    }

    [Fact]
    public void EmitStringReturnClosure_GeneratesFunctionalBridge()
    {
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "transformer",
            new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("Swift.String")) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_TestModule_CbView_Create\")", swiftContent);
        Assert.DoesNotContain("BRIDGE TEMPLATE", swiftContent);
    }

    [Fact]
    public void EmitClassReturnClosure_Swift_UsesUnmanagedTakeRetainedValue()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "factory",
            new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("TestModule.AnimationAsset")) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("Unmanaged<AnimationAsset>.fromOpaque(retPtr).takeRetainedValue()", swiftContent);
    }

    [Fact]
    public void EmitClassReturnClosure_Swift_HasNullGuard()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "factory",
            new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("TestModule.AnimationAsset")) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("guard let retPtr", swiftContent);
        Assert.Contains("fatalError", swiftContent);
    }

    [Fact]
    public void EmitClassReturnClosure_CSharp_UsesArcRetain()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "factory",
            new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("TestModule.AnimationAsset")) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("Arc.Retain(ptr)", csContent);
        Assert.Contains("Payload.DangerousGetHandle()", csContent);
    }

    [Fact]
    public void EmitClassReturnClosure_CSharp_HasFuncClassDelegate()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "factory",
            new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("TestModule.AnimationAsset")) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("Func<int, TestModule.AnimationAsset>", csContent);
    }

    [Fact]
    public void EmitClassReturnClosure_CSharp_HasSwiftRuntimeImport()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "factory",
            new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("TestModule.AnimationAsset")) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("using Swift.Runtime;", csContent);
    }

    [Fact]
    public void EmitClassReturnClosure_GeneratesFunctionalBridge()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "factory",
            new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("TestModule.AnimationAsset")) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_TestModule_CbView_Create\")", swiftContent);
        Assert.DoesNotContain("BRIDGE TEMPLATE", swiftContent);
    }

    [Fact]
    public void EmitClassReturnClosure_Swift_AbiHasNullableReturnPointer()
    {
        var typeDb = CreateClassTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithTypedClosureInit("CbView", "factory",
            new NamedTypeSpec("Swift.Int32"), new NamedTypeSpec("TestModule.AnimationAsset")) };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // ABI return type should be nullable to handle nil from C#
        Assert.Contains("UnsafeMutableRawPointer?)", swiftContent);
    }

    #endregion

    [Fact]
    public void EmitOptionalString_Swift_HasNilCheckBranching()
    {
        var views = new List<TypeDecl> { CreateViewWithOptionalPrimitiveInit("OptView", "title", "Swift.String") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("titlePtr == nil", swiftContent);
        // With state binding, optional string conversion uses "Converted" suffix
        Assert.Contains("titleConverted = nil", swiftContent);
        Assert.Contains("title: titleConverted", swiftContent);
    }

    [Fact]
    public void EmitOptionalString_CSharp_HasNullableStringParam()
    {
        var views = new List<TypeDecl> { CreateViewWithOptionalPrimitiveInit("OptView", "title", "Swift.String") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("string? title", csContent);
        Assert.Contains("titlePtr", csContent);
        Assert.Contains("titleLen", csContent);
    }

    [Fact]
    public void EmitOptionalString_CSharp_HasNullHandling()
    {
        var views = new List<TypeDecl> { CreateViewWithOptionalPrimitiveInit("OptView", "title", "Swift.String") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("title == null ? IntPtr.Zero", csContent);
    }

    [Fact]
    public void EmitOptionalString_GeneratesFunctionalBridge_NotTemplate()
    {
        var views = new List<TypeDecl> { CreateViewWithOptionalPrimitiveInit("OptView", "title", "Swift.String") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_TestModule_OptView_Create\")", swiftContent);
        Assert.DoesNotContain("BRIDGE TEMPLATE", swiftContent);
    }

    [Fact]
    public void EmitOptionalString_Swift_CreateParamsHavePtrAndLen()
    {
        var views = new List<TypeDecl> { CreateViewWithOptionalPrimitiveInit("OptView", "title", "Swift.String") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("_ titlePtr: UnsafePointer<UInt8>?", swiftContent);
        Assert.Contains("_ titleLen: Int", swiftContent);
    }

    [Fact]
    public void EmitOptionalString_CSharp_PInvokeHasPtrAndLen()
    {
        var views = new List<TypeDecl> { CreateViewWithOptionalPrimitiveInit("OptView", "title", "Swift.String") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("IntPtr titlePtr", csContent);
        Assert.Contains("nint titleLen", csContent);
    }

    [Fact]
    public void EmitOptionalString_CSharp_DefaultValueIsNull()
    {
        var views = new List<TypeDecl> { CreateViewWithOptionalPrimitiveInit("OptView", "title", "Swift.String") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("string? title = null", csContent);
    }

    // Helper methods for new test types
    private static MethodDecl CreateConstructorWithOptionalClosure(string paramName)
    {
        var closureSpec = new ClosureTypeSpec();
        var optionalSpec = new NamedTypeSpec("Swift.Optional", closureSpec);
        return new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
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
                    SwiftTypeSpec = optionalSpec,
                    ParentDecl = null,
                    ModuleDecl = null,
                },
            },
        };
    }

    private static MethodDecl CreateConstructorWithOptionalTypedClosure(string paramName, TypeSpec args, TypeSpec returnType)
    {
        var closureSpec = new ClosureTypeSpec(args, returnType);
        var optionalSpec = new NamedTypeSpec("Swift.Optional", closureSpec);
        return new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
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
                    SwiftTypeSpec = optionalSpec,
                    ParentDecl = null,
                    ModuleDecl = null,
                },
            },
        };
    }

    #region Generic View Support

    // --- Test Helpers ---

    /// <summary>
    /// Creates a generic view with a View constraint on τ_0_0 and two constructors:
    /// [0] has == EmptyView concrete constraint + String param ("title")
    /// [1] has : View protocol constraint + ViewBuilder closure param ("placeholder") + String param ("title")
    /// </summary>
    private static StructDecl CreateGenericViewWithViewConstraint(string name)
    {
        var view = new StructDecl
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
                new GenericArgumentDecl("τ_0_0", "Placeholder",
                    new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            new[] { "τ_0_0" },
                            SwiftTypeName.FromModuleQualifiedName("SwiftUI.View"),
                            ConformanceKind.Protocol)
                    },
                    new List<GenericParameterConformance>())
            },
        };

        // Constructor [0]: init(title:) where Placeholder == EmptyView
        view.Methods.Add(new MethodDecl
        {
            Name = "init",
            MangledName = $"$s10TestModule{name.Length}{name}V_init_concrete",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "Placeholder",
                    new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            new[] { "τ_0_0" },
                            SwiftTypeName.FromModuleQualifiedName("SwiftUI.EmptyView"),
                            ConformanceKind.ConcreteType)
                    },
                    new List<GenericParameterConformance>())
            },
            IsSynthesizedAccessor = false,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "", PrivateName = "", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec($"TestModule.{name}"),
                    ParentDecl = null, ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = "title", PrivateName = "title", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    ParentDecl = null, ModuleDecl = null,
                },
            },
        });

        // Constructor [1]: init(title:, @ViewBuilder placeholder: () -> Placeholder)
        view.Methods.Add(new MethodDecl
        {
            Name = "init",
            MangledName = $"$s10TestModule{name.Length}{name}V_init_viewbuilder",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "Placeholder",
                    new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            new[] { "τ_0_0" },
                            SwiftTypeName.FromModuleQualifiedName("SwiftUI.View"),
                            ConformanceKind.Protocol)
                    },
                    new List<GenericParameterConformance>())
            },
            IsSynthesizedAccessor = false,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "", PrivateName = "", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec($"TestModule.{name}"),
                    ParentDecl = null, ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = "title", PrivateName = "title", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    ParentDecl = null, ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = "placeholder", PrivateName = "placeholder", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, new NamedTypeSpec("τ_0_0")),
                    ParentDecl = null, ModuleDecl = null,
                },
            },
        });

        return view;
    }

    /// <summary>
    /// Creates a generic view with a non-View constraint (Swift.Identifiable) on τ_0_0.
    /// Has one constructor with a String param.
    /// </summary>
    private static StructDecl CreateGenericViewWithNonViewConstraint(string name)
    {
        var view = new StructDecl
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
                new GenericArgumentDecl("τ_0_0", "Item",
                    new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            new[] { "τ_0_0" },
                            SwiftTypeName.FromModuleQualifiedName("Swift.Identifiable"),
                            ConformanceKind.Protocol)
                    },
                    new List<GenericParameterConformance>())
            },
        };

        // One constructor with a String param
        view.Methods.Add(new MethodDecl
        {
            Name = "init",
            MangledName = $"$s10TestModule{name.Length}{name}V_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "", PrivateName = "", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec($"TestModule.{name}"),
                    ParentDecl = null, ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = "title", PrivateName = "title", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    ParentDecl = null, ModuleDecl = null,
                },
            },
        });

        return view;
    }

    /// <summary>
    /// Creates a generic view with two type params (A: View, B: View).
    /// Has one constructor with == EmptyView constraints on both params + a String param.
    /// </summary>
    private static StructDecl CreateGenericViewMultipleTypeParams(string name)
    {
        var view = new StructDecl
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
                new GenericArgumentDecl("τ_0_0", "A",
                    new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            new[] { "τ_0_0" },
                            SwiftTypeName.FromModuleQualifiedName("SwiftUI.View"),
                            ConformanceKind.Protocol)
                    },
                    new List<GenericParameterConformance>()),
                new GenericArgumentDecl("τ_0_1", "B",
                    new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            new[] { "τ_0_1" },
                            SwiftTypeName.FromModuleQualifiedName("SwiftUI.View"),
                            ConformanceKind.Protocol)
                    },
                    new List<GenericParameterConformance>()),
            },
        };

        view.Methods.Add(new MethodDecl
        {
            Name = "init",
            MangledName = $"$s10TestModule{name.Length}{name}V_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "A",
                    new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            new[] { "τ_0_0" },
                            SwiftTypeName.FromModuleQualifiedName("SwiftUI.EmptyView"),
                            ConformanceKind.ConcreteType)
                    },
                    new List<GenericParameterConformance>()),
                new GenericArgumentDecl("τ_0_1", "B",
                    new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            new[] { "τ_0_1" },
                            SwiftTypeName.FromModuleQualifiedName("SwiftUI.EmptyView"),
                            ConformanceKind.ConcreteType)
                    },
                    new List<GenericParameterConformance>()),
            },
            IsSynthesizedAccessor = false,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "", PrivateName = "", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec($"TestModule.{name}"),
                    ParentDecl = null, ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = "title", PrivateName = "title", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    ParentDecl = null, ModuleDecl = null,
                },
            },
        });

        return view;
    }

    /// <summary>
    /// Creates a generic view with only a @ViewBuilder closure param.
    /// Has : View protocol constraint (no == EmptyView), so AnalyzeGenericView defaults to EmptyView.
    /// </summary>
    private static StructDecl CreateViewBuilderOnlyGenericView(string name)
    {
        var view = new StructDecl
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
                new GenericArgumentDecl("τ_0_0", "Content",
                    new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            new[] { "τ_0_0" },
                            SwiftTypeName.FromModuleQualifiedName("SwiftUI.View"),
                            ConformanceKind.Protocol)
                    },
                    new List<GenericParameterConformance>())
            },
        };

        // Single ctor: init(@ViewBuilder content: () -> Content)
        view.Methods.Add(new MethodDecl
        {
            Name = "init",
            MangledName = $"$s10TestModule{name.Length}{name}V_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "", PrivateName = "", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec($"TestModule.{name}"),
                    ParentDecl = null, ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = "content", PrivateName = "content", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, new NamedTypeSpec("τ_0_0")),
                    ParentDecl = null, ModuleDecl = null,
                },
            },
        });

        return view;
    }

    /// <summary>
    /// Creates a generic view where the constructor has an extra method-level generic <Property>
    /// beyond the parent's <Placeholder>. This ctor should be excluded by SelectBestGenericConstructor.
    /// </summary>
    private static StructDecl CreateGenericViewWithMethodLevelGenerics(string name)
    {
        var view = new StructDecl
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
                new GenericArgumentDecl("τ_0_0", "Placeholder",
                    new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            new[] { "τ_0_0" },
                            SwiftTypeName.FromModuleQualifiedName("SwiftUI.View"),
                            ConformanceKind.Protocol)
                    },
                    new List<GenericParameterConformance>())
            },
        };

        // Only ctor has 2 generic params (parent's τ_0_0 + method-level τ_0_1)
        view.Methods.Add(new MethodDecl
        {
            Name = "init",
            MangledName = $"$s10TestModule{name.Length}{name}V_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "Placeholder",
                    new List<GenericParameterConformance>(),
                    new List<GenericParameterConformance>()),
                new GenericArgumentDecl("τ_0_1", "Property",
                    new List<GenericParameterConformance>(),
                    new List<GenericParameterConformance>()),
            },
            IsSynthesizedAccessor = false,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "", PrivateName = "", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec($"TestModule.{name}"),
                    ParentDecl = null, ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = "title", PrivateName = "title", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    ParentDecl = null, ModuleDecl = null,
                },
            },
        });

        return view;
    }

    /// <summary>
    /// Creates a generic view where ALL constructors are failable (init?).
    /// SelectBestGenericConstructor should return null.
    /// </summary>
    private static StructDecl CreateGenericViewAllFailableCtors(string name)
    {
        var view = new StructDecl
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
                new GenericArgumentDecl("τ_0_0", "Placeholder",
                    new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            new[] { "τ_0_0" },
                            SwiftTypeName.FromModuleQualifiedName("SwiftUI.View"),
                            ConformanceKind.Protocol)
                    },
                    new List<GenericParameterConformance>())
            },
        };

        // Failable constructor
        view.Methods.Add(new MethodDecl
        {
            Name = "init",
            MangledName = $"$s10TestModule{name.Length}{name}V_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            IsFailable = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "", PrivateName = "", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec($"TestModule.{name}"),
                    ParentDecl = null, ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = "title", PrivateName = "title", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    ParentDecl = null, ModuleDecl = null,
                },
            },
        });

        return view;
    }

    /// <summary>
    /// Creates a generic view with mixed constraints: τ_0_0 : SwiftUI.View, τ_0_1 : Swift.Identifiable.
    /// Not all generic params have View constraints → unsupported.
    /// </summary>
    private static StructDecl CreateGenericViewMixedConstraints(string name)
    {
        var view = new StructDecl
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
                new GenericArgumentDecl("τ_0_0", "A",
                    new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            new[] { "τ_0_0" },
                            SwiftTypeName.FromModuleQualifiedName("SwiftUI.View"),
                            ConformanceKind.Protocol)
                    },
                    new List<GenericParameterConformance>()),
                new GenericArgumentDecl("τ_0_1", "B",
                    new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            new[] { "τ_0_1" },
                            SwiftTypeName.FromModuleQualifiedName("Swift.Identifiable"),
                            ConformanceKind.Protocol)
                    },
                    new List<GenericParameterConformance>()),
            },
        };

        view.Methods.Add(new MethodDecl
        {
            Name = "init",
            MangledName = $"$s10TestModule{name.Length}{name}V_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "", PrivateName = "", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec($"TestModule.{name}"),
                    ParentDecl = null, ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = "title", PrivateName = "title", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    ParentDecl = null, ModuleDecl = null,
                },
            },
        });

        return view;
    }

    // --- Tests ---

    [Fact]
    public void AnalyzeView_GenericWithViewConstraint_ClassifiedAsSimple()
    {
        var view = CreateGenericViewWithViewConstraint("GenericPlaceholderView");

        var info = SwiftUIBridgeEmitter.AnalyzeView(view, "TestModule");

        Assert.Equal(ViewInitClassification.Simple, info.Classification);
        Assert.Null(info.UnsupportedReason);
        Assert.NotNull(info.GenericAnalysis);
        Assert.True(info.GenericAnalysis.IsBridgeable);
    }

    [Fact]
    public void AnalyzeView_GenericWithNonViewConstraint_RemainsUnsupported()
    {
        var view = CreateGenericViewWithNonViewConstraint("IdentifiableView");

        var info = SwiftUIBridgeEmitter.AnalyzeView(view, "TestModule");

        Assert.Equal(ViewInitClassification.Unsupported, info.Classification);
        Assert.Contains("no resolvable constraint", info.UnsupportedReason);
    }

    [Fact]
    public void AnalyzeGenericView_EmptyViewConcreteConstraint_MapsToEmptyView()
    {
        var view = CreateGenericViewWithViewConstraint("TestView");
        var ctor = view.Methods[0]; // ConcreteType constraint ctor

        var analysis = SwiftUIBridgeEmitter.AnalyzeGenericView(view, ctor, 0);

        Assert.True(analysis.IsBridgeable);
        Assert.Single(analysis.ConcreteTypeArgs);
        Assert.Equal("EmptyView", analysis.ConcreteTypeArgs["τ_0_0"]);
        Assert.Equal(0, analysis.SelectedConstructorIndex);
    }

    [Fact]
    public void AnalyzeGenericView_ViewProtocolConstraint_DefaultsToEmptyView()
    {
        var view = CreateViewBuilderOnlyGenericView("ContentView");
        var ctor = view.Methods[0]; // Protocol constraint ctor (no == EmptyView)

        var analysis = SwiftUIBridgeEmitter.AnalyzeGenericView(view, ctor, 0);

        Assert.True(analysis.IsBridgeable);
        Assert.Single(analysis.ConcreteTypeArgs);
        Assert.Equal("EmptyView", analysis.ConcreteTypeArgs["τ_0_0"]);
    }

    [Fact]
    public void GenericViewBridge_SwiftOutput_HasConcreteTypeArgs()
    {
        var view = CreateGenericViewWithViewConstraint("GenericPlaceholderView");
        var views = new List<TypeDecl> { view };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // With state binding (title: String is updatable), the hosted view is the Wrapper
        Assert.Contains("UIHostingController<SBW_TestModule_GenericPlaceholderView_Wrapper>", swiftContent);
    }

    [Fact]
    public void GenericViewBridge_ViewBuilderParam_SynthesizedInInitCall()
    {
        var view = CreateViewBuilderOnlyGenericView("PlaceholderOnlyView");
        var views = new List<TypeDecl> { view };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("content: { EmptyView() }", swiftContent);
    }

    [Fact]
    public void GenericViewBridge_NonClosureParamsBridgedNormally()
    {
        var view = CreateGenericViewWithViewConstraint("GenericPlaceholderView");
        var views = new List<TypeDecl> { view };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // title param is a String, should be bridged normally
        Assert.Contains("title:", swiftContent);
        // C# side should have the title parameter (String → string? in C#)
        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("string?", csContent);
        Assert.Contains("title", csContent);
    }

    [Fact]
    public void GenericView_AllViewBuilderParams_SynthesizesAll()
    {
        var view = CreateViewBuilderOnlyGenericView("PlaceholderOnlyView");
        var views = new List<TypeDecl> { view };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        // Create method should have zero user-facing parameters (only the synthesized ViewBuilder)
        // The Create @_cdecl should take no bridge params
        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("PlaceholderOnlyView(content: { EmptyView() })", swiftContent);
    }

    [Fact]
    public void BridgeHints_Placeholder_UIView_FallsBackToTemplate()
    {
        var view = CreateGenericViewWithViewConstraint("HintedView");
        var hintsPath = CreateBridgeHintsFile(_tempDir, """
        {
            "views": {
                "HintedView": { "placeholder": "uiview" }
            }
        }
        """);

        var hints = BridgeHintsLoader.Load(hintsPath, _tempDir, "TestModule", NullLogger.Instance);
        var context = new BridgeContext(Hints: hints);

        var info = SwiftUIBridgeEmitter.AnalyzeView(view, "TestModule", context);

        Assert.Equal(ViewInitClassification.Unsupported, info.Classification);
        Assert.Contains("UIView", info.UnsupportedReason);
        Assert.Contains("not yet implemented", info.UnsupportedReason);
    }

    [Fact]
    public void GenericView_MultipleTypeParams_AllView_Bridgeable()
    {
        var view = CreateGenericViewMultipleTypeParams("DualPlaceholderView");

        var info = SwiftUIBridgeEmitter.AnalyzeView(view, "TestModule");

        Assert.Equal(ViewInitClassification.Simple, info.Classification);
        Assert.NotNull(info.GenericAnalysis);
        Assert.True(info.GenericAnalysis.IsBridgeable);
        Assert.Equal(2, info.GenericAnalysis.ConcreteTypeArgs.Count);
        Assert.Equal("EmptyView", info.GenericAnalysis.ConcreteTypeArgs["τ_0_0"]);
        Assert.Equal("EmptyView", info.GenericAnalysis.ConcreteTypeArgs["τ_0_1"]);
    }

    [Fact]
    public void GenericView_MultipleTypeParams_SwiftOutput()
    {
        var view = CreateGenericViewMultipleTypeParams("DualPlaceholderView");
        var views = new List<TypeDecl> { view };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // With state binding (title: String is updatable), the hosted view is the Wrapper
        Assert.Contains("UIHostingController<SBW_TestModule_DualPlaceholderView_Wrapper>", swiftContent);
    }

    [Fact]
    public void GenericView_MixedConstraints_Unsupported()
    {
        var view = CreateGenericViewMixedConstraints("MixedView");

        var info = SwiftUIBridgeEmitter.AnalyzeView(view, "TestModule");

        Assert.Equal(ViewInitClassification.Unsupported, info.Classification);
        Assert.Contains("no resolvable constraint", info.UnsupportedReason);
    }

    [Fact]
    public void AnalyzeView_GenericWithHintSkip_StillSkipped()
    {
        var view = CreateGenericViewWithViewConstraint("SkippedGenericView");
        var hintsPath = CreateBridgeHintsFile(_tempDir, """
        {
            "views": {
                "SkippedGenericView": { "skip": true, "reason": "Not ready" }
            }
        }
        """);

        var hints = BridgeHintsLoader.Load(hintsPath, _tempDir, "TestModule", NullLogger.Instance);
        var context = new BridgeContext(Hints: hints);

        var info = SwiftUIBridgeEmitter.AnalyzeView(view, "TestModule", context);

        Assert.Equal(ViewInitClassification.Skipped, info.Classification);
        Assert.Equal("Not ready", info.UnsupportedReason);
    }

    [Fact]
    public void GenericView_PreferredInit_UsedForBothAnalysisAndEmission()
    {
        // Create a view where ctor[0] has == EmptyView (concrete), ctor[1] has : View (protocol)
        var view = CreateGenericViewWithViewConstraint("HintedInitView");
        // preferredInit=1 should select the ViewBuilder ctor instead of the concrete one
        var hintsPath = CreateBridgeHintsFile(_tempDir, """
        {
            "views": {
                "HintedInitView": { "preferredInit": 1 }
            }
        }
        """);

        var hints = BridgeHintsLoader.Load(hintsPath, _tempDir, "TestModule", NullLogger.Instance);
        var context = new BridgeContext(Hints: hints);

        var info = SwiftUIBridgeEmitter.AnalyzeView(view, "TestModule", context);

        Assert.Equal(ViewInitClassification.Simple, info.Classification);
        Assert.NotNull(info.GenericAnalysis);
        // SelectedConstructorIndex should be 1 (the ViewBuilder ctor)
        Assert.Equal(1, info.GenericAnalysis.SelectedConstructorIndex);
    }

    [Fact]
    public void GenericView_PreferredInit_OutOfRange_WarnsAndFallsBack()
    {
        var testLogger = new TestLogger();
        var view = CreateGenericViewWithViewConstraint("OutOfRangeView");
        var hintsPath = CreateBridgeHintsFile(_tempDir, """
        {
            "views": {
                "OutOfRangeView": { "preferredInit": 99 }
            }
        }
        """);

        var hints = BridgeHintsLoader.Load(hintsPath, _tempDir, "TestModule", NullLogger.Instance);
        var context = new BridgeContext(Hints: hints, Logger: testLogger);

        var info = SwiftUIBridgeEmitter.AnalyzeView(view, "TestModule", context);

        // Should warn and fall back to auto-selection
        Assert.Contains(testLogger.Messages, m => m.Contains("preferredInit") && m.Contains("out of range"));
        Assert.Equal(ViewInitClassification.Simple, info.Classification);
        // Auto-selects ctor[0] (concrete constraint, fewest params)
        Assert.Equal(0, info.GenericAnalysis!.SelectedConstructorIndex);
    }

    [Fact]
    public void GenericView_MethodLevelGenerics_CtorExcluded()
    {
        var view = CreateGenericViewWithMethodLevelGenerics("MethodGenericView");

        var info = SwiftUIBridgeEmitter.AnalyzeView(view, "TestModule");

        // Only ctor has method-level generics → no candidates → unsupported
        Assert.Equal(ViewInitClassification.Unsupported, info.Classification);
        Assert.Contains("No bridgeable constructor", info.UnsupportedReason);
    }

    [Fact]
    public void GenericView_AllFailableCtors_Unsupported()
    {
        var view = CreateGenericViewAllFailableCtors("FailableView");

        var info = SwiftUIBridgeEmitter.AnalyzeView(view, "TestModule");

        Assert.Equal(ViewInitClassification.Unsupported, info.Classification);
        Assert.Contains("No bridgeable constructor", info.UnsupportedReason);
    }

    [Fact]
    public void AnalyzeInitParameters_BackwardCompatible_NoGenericAnalysis()
    {
        // Existing 2-param overload should still work
        var ctor = CreateConstructorWithPrimitive("count", "Swift.Int");

        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("count", result[0].Name);
        Assert.Equal(BridgeParameterKind.Primitive, result[0].Kind);
    }

    [Fact]
    public void SelectBestGenericConstructor_PrefersConcrete_OverProtocol()
    {
        var view = CreateGenericViewWithViewConstraint("RankedView");
        var constructors = view.Methods.ToList();

        var selected = SwiftUIBridgeEmitter.SelectBestGenericConstructor(
            constructors, view, null, null);

        Assert.NotNull(selected);
        // ctor[0] has == EmptyView (ConcreteType) and fewer params → selected
        Assert.Equal(0, selected.Value.Index);
    }

    [Fact]
    public void GetSwiftHostedViewType_NonGeneric_ReturnsViewName()
    {
        var info = new ViewBridgeInfo("MyView", "TestModule",
            ViewInitClassification.Simple, null, new List<MethodDecl>());

        var result = SwiftUIBridgeEmitter.GetSwiftHostedViewType(info);

        Assert.Equal("MyView", result);
    }

    [Fact]
    public void GetSwiftHostedViewType_Generic_ReturnsViewNameWithTypeArgs()
    {
        var analysis = new GenericViewAnalysis(true,
            new Dictionary<string, string> { ["τ_0_0"] = "EmptyView" },
            PlaceholderStrategy.Empty, 0);
        var info = new ViewBridgeInfo("AnimationView", "TestModule",
            ViewInitClassification.Simple, null, new List<MethodDecl>(),
            GenericAnalysis: analysis);

        var result = SwiftUIBridgeEmitter.GetSwiftHostedViewType(info);

        Assert.Equal("AnimationView<EmptyView>", result);
    }

    [Fact]
    public void GetSwiftHostedViewType_ManyGenericParams_SortsNumericallyNotLexicographically()
    {
        // Regression test: lexicographic sort would place τ_0_10 before τ_0_2
        var analysis = new GenericViewAnalysis(true,
            new Dictionary<string, string>
            {
                ["τ_0_0"] = "String", ["τ_0_1"] = "Int", ["τ_0_2"] = "Double",
                ["τ_0_3"] = "Bool", ["τ_0_4"] = "String", ["τ_0_5"] = "Int",
                ["τ_0_6"] = "Double", ["τ_0_7"] = "Bool", ["τ_0_8"] = "String",
                ["τ_0_9"] = "Int", ["τ_0_10"] = "Double", ["τ_0_11"] = "Bool"
            },
            PlaceholderStrategy.Empty, 0);
        var info = new ViewBridgeInfo("BigView", "TestModule",
            ViewInitClassification.Simple, null, new List<MethodDecl>(),
            GenericAnalysis: analysis);

        var result = SwiftUIBridgeEmitter.GetSwiftHostedViewType(info);

        Assert.Equal("BigView<String, Int, Double, Bool, String, Int, Double, Bool, String, Int, Double, Bool>", result);
    }

    [Fact]
    public void BuildMergedInitArgs_NoSynthesized_ReturnsOriginal()
    {
        var viewInitArgs = new List<string> { "title: titleString" };

        var result = SwiftUIBridgeEmitter.BuildMergedInitArgs(
            null, new List<BridgeParameter>(), viewInitArgs, null);

        Assert.Equal(viewInitArgs, result);
    }

    [Fact]
    public void BuildMergedInitArgs_WithSynthesized_MergesInOrder()
    {
        // Constructor has: return, title (bridge), content (synthesized)
        var ctor = new MethodDecl
        {
            Name = "init",
            MangledName = "$s_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl { Name = "", PrivateName = "", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.TestView"),
                    ParentDecl = null, ModuleDecl = null },
                new ArgumentDecl { Name = "title", PrivateName = "title", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    ParentDecl = null, ModuleDecl = null },
                new ArgumentDecl { Name = "content", PrivateName = "content", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new ClosureTypeSpec(),
                    ParentDecl = null, ModuleDecl = null },
            },
        };

        var bridgeParams = new List<BridgeParameter>
        {
            new BridgeParameter("title", BridgeParameterKind.String, "UnsafePointer<UInt8>?", "IntPtr", HasLength: true),
        };
        var viewInitArgs = new List<string> { "title: titleString" };
        var synthesizedArgs = new List<SynthesizedInitArg>
        {
            new SynthesizedInitArg("content", "{ EmptyView() }"),
        };

        var result = SwiftUIBridgeEmitter.BuildMergedInitArgs(
            ctor, bridgeParams, viewInitArgs, synthesizedArgs);

        Assert.Equal(2, result.Count);
        Assert.Equal("title: titleString", result[0]);
        Assert.Equal("content: { EmptyView() }", result[1]);
    }

    [Fact]
    public void GenericView_MultiParam_ViewBuilderClosure_UsesCorrectConcreteType()
    {
        // View with two generic params: <Header: View, Footer: View>
        // Constructor has a @ViewBuilder closure returning Footer (τ_0_1), NOT Header (τ_0_0)
        // The synthesized closure must use the concrete type for τ_0_1, not τ_0_0
        var view = new StructDecl
        {
            Name = "TwoSlotView",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.TwoSlotView"),
            MangledName = "$s10TestModule11TwoSlotViewV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>
            {
                new TypeConformance(
                    SwiftTypeName.FromModuleQualifiedName("TestModule.TwoSlotView"),
                    SwiftTypeName.FromModuleQualifiedName("SwiftUI.View"),
                    "$TwoSlotView_SwiftUI_View_conformance")
            },
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule11TwoSlotViewVMa",
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "Header",
                    new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            new[] { "τ_0_0" },
                            SwiftTypeName.FromModuleQualifiedName("SwiftUI.View"),
                            ConformanceKind.Protocol)
                    },
                    new List<GenericParameterConformance>()),
                new GenericArgumentDecl("τ_0_1", "Footer",
                    new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            new[] { "τ_0_1" },
                            SwiftTypeName.FromModuleQualifiedName("SwiftUI.View"),
                            ConformanceKind.Protocol)
                    },
                    new List<GenericParameterConformance>()),
            },
        };

        // Constructor with: String title + @ViewBuilder () -> Footer (τ_0_1)
        // Both generic params have == EmptyView constraints
        view.Methods.Add(new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule11TwoSlotViewV_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "Header",
                    new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            new[] { "τ_0_0" },
                            SwiftTypeName.FromModuleQualifiedName("SwiftUI.EmptyView"),
                            ConformanceKind.ConcreteType)
                    },
                    new List<GenericParameterConformance>()),
                new GenericArgumentDecl("τ_0_1", "Footer",
                    new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            new[] { "τ_0_1" },
                            SwiftTypeName.FromModuleQualifiedName("SwiftUI.EmptyView"),
                            ConformanceKind.ConcreteType)
                    },
                    new List<GenericParameterConformance>()),
            },
            IsSynthesizedAccessor = false,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "", PrivateName = "", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.TwoSlotView"),
                    ParentDecl = null, ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = "title", PrivateName = "title", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    ParentDecl = null, ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    // Closure returns τ_0_1 (Footer), NOT τ_0_0 (Header)
                    Name = "footer", PrivateName = "footer", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, new NamedTypeSpec("τ_0_1")),
                    ParentDecl = null, ModuleDecl = null,
                },
            },
        });

        var views = new List<TypeDecl> { view };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // The closure must synthesize the concrete type for τ_0_1 (EmptyView), not some random first value
        Assert.Contains("footer: { EmptyView() }", swiftContent);
        // With state binding (title: String is updatable), the hosted view is the Wrapper
        Assert.Contains("UIHostingController<SBW_TestModule_TwoSlotView_Wrapper>", swiftContent);
    }

    [Fact]
    public void ParsePlaceholderStrategy_ReturnsCorrectValues()
    {
        Assert.Equal(PlaceholderStrategy.Empty, SwiftUIBridgeEmitter.ParsePlaceholderStrategy(null));
        Assert.Equal(PlaceholderStrategy.Empty, SwiftUIBridgeEmitter.ParsePlaceholderStrategy(""));
        Assert.Equal(PlaceholderStrategy.Empty, SwiftUIBridgeEmitter.ParsePlaceholderStrategy("empty"));
        Assert.Equal(PlaceholderStrategy.UIView, SwiftUIBridgeEmitter.ParsePlaceholderStrategy("uiview"));
        Assert.Equal(PlaceholderStrategy.AnyViewFromVC, SwiftUIBridgeEmitter.ParsePlaceholderStrategy("anyviewfromvc"));
        Assert.Equal(PlaceholderStrategy.Empty, SwiftUIBridgeEmitter.ParsePlaceholderStrategy("unknown"));
    }

    #endregion

    #region Two-Way State Binding

    [Fact]
    public void StateBinding_Swift_EmitsStateClass_ForUpdatableParams()
    {
        var views = new List<TypeDecl> { CreateViewWithPrimitiveAndStringInit("CounterView", "count", "Swift.Int32", "label") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("final class SBW_TestModule_CounterView_State: ObservableObject", swiftContent);
        Assert.Contains("@Published var count: Int32", swiftContent);
        Assert.Contains("@Published var label: String", swiftContent);
    }

    [Fact]
    public void StateBinding_Swift_EmitsWrapperView_WithObservedObject()
    {
        var views = new List<TypeDecl> { CreateViewWithPrimitiveAndStringInit("CounterView", "count", "Swift.Int32", "label") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("struct SBW_TestModule_CounterView_Wrapper: View", swiftContent);
        Assert.Contains("@ObservedObject var state: SBW_TestModule_CounterView_State", swiftContent);
    }

    [Fact]
    public void StateBinding_Swift_WrapperBody_UsesStateProperties()
    {
        var views = new List<TypeDecl> { CreateViewWithPrimitiveAndStringInit("CounterView", "count", "Swift.Int32", "label") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("CounterView(count: state.count, label: state.label)", swiftContent);
    }

    [Fact]
    public void StateBinding_Swift_SessionHoldsState()
    {
        var views = new List<TypeDecl> { CreateViewWithPrimitiveAndStringInit("CounterView", "count", "Swift.Int32", "label") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("let state: SBW_TestModule_CounterView_State", swiftContent);
        Assert.Contains("UIHostingController<SBW_TestModule_CounterView_Wrapper>", swiftContent);
    }

    [Fact]
    public void StateBinding_Swift_EmitsUpdateFunctions()
    {
        var views = new List<TypeDecl> { CreateViewWithPrimitiveAndStringInit("CounterView", "count", "Swift.Int32", "label") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_TestModule_CounterView_UpdateCount\")", swiftContent);
        Assert.Contains("@_cdecl(\"SBW_TestModule_CounterView_UpdateLabel\")", swiftContent);
        Assert.Contains("session.state.count = newValue", swiftContent);
    }

    [Fact]
    public void StateBinding_Swift_UpdateStringFunction_DecodesUtf8()
    {
        var views = new List<TypeDecl> { CreateViewWithPrimitiveAndStringInit("CounterView", "count", "Swift.Int32", "label") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("session.state.label = String(bytes:", swiftContent);
    }

    [Fact]
    public void StateBinding_CSharp_EmitsUpdatePInvoke()
    {
        var views = new List<TypeDecl> { CreateViewWithPrimitiveAndStringInit("CounterView", "count", "Swift.Int32", "label") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("SBW_TestModule_CounterView_UpdateCount", csContent);
        Assert.Contains("SBW_TestModule_CounterView_UpdateLabel", csContent);
    }

    [Fact]
    public void StateBinding_CSharp_EmitsUpdateMethods()
    {
        var views = new List<TypeDecl> { CreateViewWithPrimitiveAndStringInit("CounterView", "count", "Swift.Int32", "label") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("public void UpdateCount(int newValue)", csContent);
        Assert.Contains("public unsafe void UpdateLabel(string? newValue)", csContent);
    }

    [Fact]
    public void StateBinding_ClosuresExcluded_FromUpdates()
    {
        // Mixed view: title + isEnabled (updatable) + onTap (closure, NOT updatable)
        var views = new List<TypeDecl> { CreateViewWithMixedUpdatableInit("MixedView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // onTap should NOT have an Update function
        Assert.DoesNotContain("UpdateOnTap", swiftContent);
        // But title and isEnabled should
        Assert.Contains("@_cdecl(\"SBW_TestModule_MixedView_UpdateTitle\")", swiftContent);
        Assert.Contains("@_cdecl(\"SBW_TestModule_MixedView_UpdateIsEnabled\")", swiftContent);
    }

    [Fact]
    public void StateBinding_ClosureOnWrapper_NotOnState()
    {
        var views = new List<TypeDecl> { CreateViewWithMixedUpdatableInit("MixedView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // Closure is on Wrapper as let, not on State as @Published
        Assert.Contains("let onTap: () -> Void", swiftContent);
        Assert.DoesNotContain("@Published var onTap", swiftContent);
    }

    [Fact]
    public void StateBinding_AlwaysWrapper_ForClosureOnlyView()
    {
        // All views always use State+Wrapper (for lifecycle + universal modifiers)
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("ClosureOnlyView", "action") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("SBW_TestModule_ClosureOnlyView_State", swiftContent);
        Assert.Contains("SBW_TestModule_ClosureOnlyView_Wrapper", swiftContent);
        Assert.Contains("UIHostingController<SBW_TestModule_ClosureOnlyView_Wrapper>", swiftContent);
    }

    [Fact]
    public void StateBinding_AlwaysWrapper_ForParameterlessView()
    {
        // Even parameterless views use State+Wrapper
        var views = new List<TypeDecl> { CreateSimpleViewStruct("EmptyView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("SBW_TestModule_EmptyView_State", swiftContent);
        Assert.Contains("SBW_TestModule_EmptyView_Wrapper", swiftContent);
    }

    [Fact]
    public void StateBinding_Swift_BoolPrimitive_UpdateUsesConversion()
    {
        var views = new List<TypeDecl> { CreateViewWithMixedUpdatableInit("BoolView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@Published var isEnabled: Bool", swiftContent);
        Assert.Contains("!= 0", swiftContent);
    }

    [Fact]
    public void StateBinding_CSharp_BoolUpdate_UsesConversion()
    {
        var views = new List<TypeDecl> { CreateViewWithMixedUpdatableInit("BoolView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("public void UpdateIsEnabled(bool newValue)", csContent);
        Assert.Contains("? 1 : 0", csContent);
    }

    [Fact]
    public void StateBinding_BoundEnum_UpdateUsesRawValue()
    {
        var typeDb = CreateEnumTypeDatabase();
        var views = new List<TypeDecl> { CreateViewWithEnumInit("EnumUpdateView", "style", "TestModule.AlertStyle") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_TestModule_EnumUpdateView_UpdateStyle\")", swiftContent);
        // An out-of-range raw value leaves state unchanged (return) instead of trapping.
        Assert.Contains("guard let newValueConverted = AlertStyle(rawValue: newValue) else { return }", swiftContent);
        Assert.Contains("session.state.style = newValueConverted", swiftContent);
        Assert.DoesNotContain("AlertStyle(rawValue: newValue)!", swiftContent);
        Assert.Contains("@Published var style: AlertStyle", swiftContent);
    }

    [Fact]
    public void StateBinding_IsUpdatable_Property()
    {
        var primitiveParam = new BridgeParameter("count", BridgeParameterKind.Primitive, "Int32", "int");
        var stringParam = new BridgeParameter("title", BridgeParameterKind.String, "UnsafePointer<UInt8>?", "IntPtr", HasLength: true);
        var voidClosureParam = new BridgeParameter("onTap", BridgeParameterKind.VoidClosure,
            "(@convention(c) (UnsafeMutableRawPointer?) -> Void)?", "IntPtr", HasUserData: true);
        var typedClosureParam = new BridgeParameter("onValue", BridgeParameterKind.TypedClosure,
            "(@convention(c) (Int32, UnsafeMutableRawPointer?) -> Void)?", "IntPtr", HasUserData: true);
        var enumParam = new BridgeParameter("style", BridgeParameterKind.BoundEnum,
            "Int32", "int", BridgeTypeName: "AlertStyle");
        var optionalParam = new BridgeParameter("opt", BridgeParameterKind.OptionalWrapped,
            "Int32", "int", InnerParameter: primitiveParam);

        Assert.True(primitiveParam.IsUpdatable);
        Assert.True(stringParam.IsUpdatable);
        Assert.False(voidClosureParam.IsUpdatable);
        Assert.False(typedClosureParam.IsUpdatable);
        Assert.True(enumParam.IsUpdatable);
        Assert.True(optionalParam.IsUpdatable);
    }

    [Fact]
    public void StateBinding_WrapperBody_MergesSynthesizedArgs()
    {
        // Generic view with updatable + synthesized args: both should appear correctly in wrapper body
        var views = new List<TypeDecl> { CreateGenericViewWithUpdatableParam("GenericUpdatable") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("SBW_TestModule_GenericUpdatable_State", swiftContent);
        Assert.Contains("SBW_TestModule_GenericUpdatable_Wrapper", swiftContent);
        Assert.Contains("state.title", swiftContent);
    }

    [Fact]
    public void StateBinding_CSharp_NoUpdateMethods_ForClosureOnlyView()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("ActionView", "doSomething") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.DoesNotContain("public void Update", csContent);
    }

    // --- Test Helpers ---

    private static StructDecl CreateViewWithPrimitiveAndStringInit(string viewName, string primName, string primType, string strName)
    {
        var view = CreateViewStructWithNoConstructor(viewName);
        view.Methods.Add(new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
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
                    SwiftTypeSpec = new NamedTypeSpec($"TestModule.{viewName}"),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = primName,
                    PrivateName = primName,
                    IsInOut = false,
                    IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec(primType),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = strName,
                    PrivateName = strName,
                    IsInOut = false,
                    IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
            },
        });
        return view;
    }

    private static StructDecl CreateViewWithMixedUpdatableInit(string viewName)
    {
        var view = CreateViewStructWithNoConstructor(viewName);
        view.Methods.Add(new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
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
                    SwiftTypeSpec = new NamedTypeSpec($"TestModule.{viewName}"),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = "title",
                    PrivateName = "title",
                    IsInOut = false,
                    IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = "isEnabled",
                    PrivateName = "isEnabled",
                    IsInOut = false,
                    IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Bool"),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = "onTap",
                    PrivateName = "onTap",
                    IsInOut = false,
                    IsGeneric = false,
                    SwiftTypeSpec = new ClosureTypeSpec(),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
            },
        });
        return view;
    }

    private static StructDecl CreateGenericViewWithUpdatableParam(string name)
    {
        var view = new StructDecl
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
                new GenericArgumentDecl("τ_0_0", "Placeholder",
                    new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            new[] { "τ_0_0" },
                            SwiftTypeName.FromModuleQualifiedName("SwiftUICore.View"),
                            ConformanceKind.Protocol)
                    },
                    new List<GenericParameterConformance>())
            },
        };

        // Constructor: init(title: String) where Placeholder == EmptyView
        view.Methods.Add(new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "Placeholder",
                    new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            new[] { "τ_0_0" },
                            SwiftTypeName.FromModuleQualifiedName("SwiftUI.EmptyView"),
                            ConformanceKind.ConcreteType)
                    },
                    new List<GenericParameterConformance>())
            },
            IsSynthesizedAccessor = false,
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
                    SwiftTypeSpec = new NamedTypeSpec($"TestModule.{name}"),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = "title",
                    PrivateName = "title",
                    IsInOut = false,
                    IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
            },
        });

        return view;
    }

    #endregion

    #region View Modifier Chain

    [Fact]
    public void Modifier_Detection_FindsParameterlessSelfReturning()
    {
        var view = CreateViewWithModifierMethods("TestView");
        var modifiers = SwiftUIBridgeEmitter.AnalyzeModifiers(view, "TestModule");

        Assert.NotNull(modifiers);
        Assert.Contains(modifiers, m => m.MethodName == "highlighted" && m.IsParameterless);
    }

    [Fact]
    public void Modifier_Detection_FindsSingleParamSelfReturning()
    {
        var view = CreateViewWithModifierMethods("TestView");
        var modifiers = SwiftUIBridgeEmitter.AnalyzeModifiers(view, "TestModule");

        Assert.NotNull(modifiers);
        Assert.Contains(modifiers, m => m.MethodName == "animationSpeed" && !m.IsParameterless);
        Assert.Contains(modifiers, m => m.MethodName == "animationSpeed" && m.Parameter?.Kind == BridgeParameterKind.Primitive);
    }

    [Fact]
    public void Modifier_Detection_SkipsNonSelfReturning()
    {
        var view = CreateSimpleViewStruct("TestView");
        // Add void-returning method
        view.Methods.Add(new MethodDecl
        {
            Name = "doSomething",
            MangledName = "$s_doSomething",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAccessor = false,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
            ParentDecl = view,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl { Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, SwiftTypeSpec = TupleTypeSpec.Empty, ParentDecl = null, ModuleDecl = null },
            },
        });

        var modifiers = SwiftUIBridgeEmitter.AnalyzeModifiers(view, "TestModule");
        Assert.Null(modifiers);
    }

    [Fact]
    public void Modifier_Detection_SkipsStaticMethods()
    {
        var view = CreateSimpleViewStruct("TestView");
        view.Methods.Add(CreateSelfReturningMethod(view, "factory", MethodType.Static));

        var modifiers = SwiftUIBridgeEmitter.AnalyzeModifiers(view, "TestModule");
        Assert.Null(modifiers);
    }

    [Fact]
    public void Modifier_Detection_SkipsThrowingMethods()
    {
        var view = CreateSimpleViewStruct("TestView");
        var method = CreateSelfReturningMethod(view, "riskyModifier");
        method.Throws = true;
        view.Methods.Add(method);

        var modifiers = SwiftUIBridgeEmitter.AnalyzeModifiers(view, "TestModule");
        Assert.Null(modifiers);
    }

    [Fact]
    public void Modifier_Detection_SkipsMultiParamMethods()
    {
        var view = CreateSimpleViewStruct("TestView");
        view.Methods.Add(new MethodDecl
        {
            Name = "multiParam",
            MangledName = "$s_multiParam",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAccessor = false,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
            ParentDecl = view,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl { Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, SwiftTypeSpec = new NamedTypeSpec($"TestModule.{view.Name}"), ParentDecl = null, ModuleDecl = null },
                new ArgumentDecl { Name = "a", PrivateName = "a", IsInOut = false, IsGeneric = false, SwiftTypeSpec = new NamedTypeSpec("Swift.Double"), ParentDecl = null, ModuleDecl = null },
                new ArgumentDecl { Name = "b", PrivateName = "b", IsInOut = false, IsGeneric = false, SwiftTypeSpec = new NamedTypeSpec("Swift.Double"), ParentDecl = null, ModuleDecl = null },
            },
        });

        var modifiers = SwiftUIBridgeEmitter.AnalyzeModifiers(view, "TestModule");
        Assert.Null(modifiers);
    }

    [Fact]
    public void Modifier_Detection_SkipsClosureParams()
    {
        var view = CreateSimpleViewStruct("TestView");
        view.Methods.Add(new MethodDecl
        {
            Name = "withAction",
            MangledName = "$s_withAction",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAccessor = false,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
            ParentDecl = view,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl { Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, SwiftTypeSpec = new NamedTypeSpec($"TestModule.{view.Name}"), ParentDecl = null, ModuleDecl = null },
                new ArgumentDecl { Name = "action", PrivateName = "action", IsInOut = false, IsGeneric = false, SwiftTypeSpec = new ClosureTypeSpec(), ParentDecl = null, ModuleDecl = null },
            },
        });

        var modifiers = SwiftUIBridgeEmitter.AnalyzeModifiers(view, "TestModule");
        Assert.Null(modifiers);
    }

    [Fact]
    public void Modifier_Detection_SkipsOverloadedMethods()
    {
        var view = CreateSimpleViewStruct("TestView");
        // Two methods with same name but different params
        view.Methods.Add(CreateSelfReturningMethod(view, "speed"));
        view.Methods.Add(new MethodDecl
        {
            Name = "speed",
            MangledName = "$s_speed2",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAccessor = false,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
            ParentDecl = view,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl { Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, SwiftTypeSpec = new NamedTypeSpec($"TestModule.{view.Name}"), ParentDecl = null, ModuleDecl = null },
                new ArgumentDecl { Name = "value", PrivateName = "value", IsInOut = false, IsGeneric = false, SwiftTypeSpec = new NamedTypeSpec("Swift.Float"), ParentDecl = null, ModuleDecl = null },
            },
        });

        var modifiers = SwiftUIBridgeEmitter.AnalyzeModifiers(view, "TestModule");
        Assert.Null(modifiers);
    }

    [Fact]
    public void Modifier_Swift_EmitsStateVars()
    {
        var views = new List<TypeDecl> { CreateViewWithModifierMethods("ModView") };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views, NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@Published var mod_highlighted: Bool = false", swiftContent);
        Assert.Contains("@Published var mod_animationSpeed: Double? = nil", swiftContent);
    }

    [Fact]
    public void Modifier_Swift_EmitsApplyModifiers()
    {
        var views = new List<TypeDecl> { CreateViewWithModifierMethods("ModView") };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views, NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("applyModifiers(ModView(", swiftContent);
        Assert.Contains("private func applyModifiers(_ view: ModView) -> ModView", swiftContent);
        Assert.Contains("if state.mod_highlighted { result = result.highlighted() }", swiftContent);
        Assert.Contains("if let val = state.mod_animationSpeed { result = result.animationSpeed(speed: val) }", swiftContent);
    }

    [Fact]
    public void Modifier_Swift_EmitsSetFunctions()
    {
        var views = new List<TypeDecl> { CreateViewWithModifierMethods("ModView") };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views, NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_TestModule_ModView_SetHighlighted\")", swiftContent);
        Assert.Contains("@_cdecl(\"SBW_TestModule_ModView_SetAnimationSpeed\")", swiftContent);
        Assert.Contains("session.state.mod_highlighted = enabled != 0", swiftContent);
        Assert.Contains("session.state.mod_animationSpeed = hasValue != 0 ? value : nil", swiftContent);
    }

    [Fact]
    public void Modifier_CSharp_EmitsPInvoke()
    {
        var views = new List<TypeDecl> { CreateViewWithModifierMethods("ModView") };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views, NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("SBW_TestModule_ModView_SetHighlighted", csContent);
        Assert.Contains("SBW_TestModule_ModView_SetAnimationSpeed", csContent);
    }

    [Fact]
    public void Modifier_CSharp_EmitsPublicMethods()
    {
        var views = new List<TypeDecl> { CreateViewWithModifierMethods("ModView") };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views, NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("public void Highlighted(bool enabled = true)", csContent);
        Assert.Contains("public void AnimationSpeed(double? value)", csContent);
    }

    [Fact]
    public void Modifier_NoModifiers_NoExtraEmission()
    {
        // View with no self-returning methods → no modifier emission
        var views = new List<TypeDecl> { CreateSimpleViewStruct("PlainView") };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views, NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.DoesNotContain("applyModifiers", swiftContent);
        Assert.DoesNotContain("mod_", swiftContent);
    }

    [Fact]
    public void Modifier_ModifiersOnlyView_ForcesStateWrapper()
    {
        // View with no init params but with modifiers → needs State/Wrapper.
        // It still exposes a public no-arg init() (CreateSimpleViewStruct) so the
        // bridge can construct ModOnlyView() before applying the modifier.
        var view = CreateSimpleViewStruct("ModOnlyView");
        view.Methods.Add(CreateSelfReturningMethod(view, "playing"));

        var views = new List<TypeDecl> { view };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views, NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("SBW_TestModule_ModOnlyView_State", swiftContent);
        Assert.Contains("SBW_TestModule_ModOnlyView_Wrapper", swiftContent);
        Assert.Contains("@Published var mod_playing: Bool = false", swiftContent);
    }

    [Fact]
    public void Modifier_GenericView_UsesConcreteTypeInApplyModifiers()
    {
        var view = CreateGenericViewWithModifiers("GenericModView");
        var views = new List<TypeDecl> { view };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views, NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("private func applyModifiers(_ view: GenericModView<EmptyView>) -> GenericModView<EmptyView>", swiftContent);
    }

    [Fact]
    public void Modifier_GetConcreteViewType_NonGeneric()
    {
        var info = new ViewBridgeInfo("SimpleView", "TestModule", ViewInitClassification.Simple, null, new List<MethodDecl>());
        Assert.Equal("SimpleView", SwiftUIBridgeEmitter.GetConcreteViewType(info));
    }

    [Fact]
    public void Modifier_GetConcreteViewType_Generic()
    {
        var analysis = new GenericViewAnalysis(true, new Dictionary<string, string> { { "τ_0_0", "EmptyView" } },
            PlaceholderStrategy.Empty, 0);
        var info = new ViewBridgeInfo("AnimationView", "VectorAnimation", ViewInitClassification.Simple, null,
            new List<MethodDecl>(), GenericAnalysis: analysis);
        Assert.Equal("AnimationView<EmptyView>", SwiftUIBridgeEmitter.GetConcreteViewType(info));
    }

    [Fact]
    public void Modifier_BoolParam_UsesHasValuePattern()
    {
        var view = CreateSimpleViewStruct("BoolModView");
        view.Methods.Add(new MethodDecl
        {
            Name = "enabled",
            MangledName = "$s_enabled",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAccessor = false,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
            ParentDecl = view,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl { Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, SwiftTypeSpec = new NamedTypeSpec($"TestModule.{view.Name}"), ParentDecl = null, ModuleDecl = null },
                new ArgumentDecl { Name = "flag", PrivateName = "flag", IsInOut = false, IsGeneric = false, SwiftTypeSpec = new NamedTypeSpec("Swift.Bool"), ParentDecl = null, ModuleDecl = null },
            },
        });

        var views = new List<TypeDecl> { view };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views, NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@Published var mod_enabled: Bool? = nil", swiftContent);
        Assert.Contains("session.state.mod_enabled = hasValue != 0 ? (value != 0) : nil", swiftContent);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("public void Enabled(bool? value)", csContent);
    }

    [Fact]
    public void Modifier_ArgPrefixedLabel_PreservesLabel()
    {
        // A param named "argument" (not a parser-generated argN) must keep its label
        var view = CreateSimpleViewStruct("ArgLabelView");
        view.Methods.Add(new MethodDecl
        {
            Name = "configure",
            MangledName = "$s_configure",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAccessor = false,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
            ParentDecl = view,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl { Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, SwiftTypeSpec = new NamedTypeSpec($"TestModule.{view.Name}"), ParentDecl = null, ModuleDecl = null },
                new ArgumentDecl { Name = "argument", PrivateName = "argument", IsInOut = false, IsGeneric = false, SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"), ParentDecl = null, ModuleDecl = null },
            },
        });

        var views = new List<TypeDecl> { view };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views, NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // "argument" is a legitimate label, NOT a parser-generated argN — must emit with label
        Assert.Contains("result.configure(argument: val)", swiftContent);
        Assert.DoesNotContain("result.configure(val)", swiftContent);
    }

    [Fact]
    public void Modifier_KeywordMethodNameAndLabel_EmitEscapedNameAndBareLabel()
    {
        // A modifier whose Swift name is a keyword reserved in BOTH languages: the parser
        // mangles the C#-safe Name to "_class" but records OriginalSwiftName = "class". Its
        // single param's external argument label is the Swift keyword "repeat" (parser stores
        // C#-safe Name = "count", OriginalSwiftName = "repeat"). The Swift modifier call must
        // dispatch through the backtick-escaped ORIGINAL method name (`class`), never the
        // mangled "_class", and the call label must be the BARE original keyword ("repeat:")
        // — escaping a keyword argument LABEL warns, and the C#-safe internal name must not
        // leak as the label. Gates the provenance-aware modifier call path (Fix B + label).
        var view = CreateSimpleViewStruct("KeywordModView");
        view.Methods.Add(new MethodDecl
        {
            Name = "_class",
            OriginalSwiftName = "class",
            MangledName = "$s_class",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAccessor = false,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
            ParentDecl = view,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl { Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, SwiftTypeSpec = new NamedTypeSpec($"TestModule.{view.Name}"), ParentDecl = null, ModuleDecl = null },
                new ArgumentDecl { Name = "count", PrivateName = "count", OriginalSwiftName = "repeat", IsInOut = false, IsGeneric = false, SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"), ParentDecl = null, ModuleDecl = null },
            },
        });

        var views = new List<TypeDecl> { view };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views, NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // Method dispatched through the backtick-escaped original name with the bare keyword label.
        Assert.Contains("result.`class`(repeat: val)", swiftContent);
        // The mangled C#-safe method name must not reach the Swift call site.
        Assert.DoesNotContain("result._class(", swiftContent);
        // The bare keyword method name (unescaped) would not compile in Swift.
        Assert.DoesNotContain("result.class(", swiftContent);
        // The C#-safe internal param name must not leak as the call label.
        Assert.DoesNotContain("count: val", swiftContent);
        // A backtick-escaped call LABEL would emit a Swift warning — must stay bare.
        Assert.DoesNotContain("`repeat`: val", swiftContent);
    }

    // --- Test Helpers ---

    private static StructDecl CreateViewWithModifierMethods(string viewName)
    {
        var view = CreateSimpleViewStruct(viewName);
        // Add a constructor so it's functional
        view.Methods.Add(new MethodDecl
        {
            Name = "init",
            MangledName = "$s_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl { Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, SwiftTypeSpec = new NamedTypeSpec($"TestModule.{viewName}"), ParentDecl = null, ModuleDecl = null },
                new ArgumentDecl { Name = "title", PrivateName = "title", IsInOut = false, IsGeneric = false, SwiftTypeSpec = new NamedTypeSpec("Swift.String"), ParentDecl = null, ModuleDecl = null },
            },
        });

        // Parameterless modifier
        view.Methods.Add(CreateSelfReturningMethod(view, "highlighted"));

        // Single-param Double modifier
        view.Methods.Add(new MethodDecl
        {
            Name = "animationSpeed",
            MangledName = "$s_animationSpeed",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAccessor = false,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
            ParentDecl = view,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl { Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, SwiftTypeSpec = new NamedTypeSpec($"TestModule.{viewName}"), ParentDecl = null, ModuleDecl = null },
                new ArgumentDecl { Name = "speed", PrivateName = "speed", IsInOut = false, IsGeneric = false, SwiftTypeSpec = new NamedTypeSpec("Swift.Double"), ParentDecl = null, ModuleDecl = null },
            },
        });

        return view;
    }

    private static MethodDecl CreateSelfReturningMethod(StructDecl parentView, string name, MethodType methodType = MethodType.Instance)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s_{name}",
            MethodType = methodType,
            IsConstructor = false,
            IsAccessor = false,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
            ParentDecl = parentView,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl { Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, SwiftTypeSpec = new NamedTypeSpec($"TestModule.{parentView.Name}"), ParentDecl = null, ModuleDecl = null },
            },
        };
    }

    private static StructDecl CreateGenericViewWithModifiers(string name)
    {
        var view = new StructDecl
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
                new GenericArgumentDecl("τ_0_0", "Placeholder",
                    new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            new[] { "τ_0_0" },
                            SwiftTypeName.FromModuleQualifiedName("SwiftUICore.View"),
                            ConformanceKind.Protocol)
                    },
                    new List<GenericParameterConformance>())
            },
        };

        // Constructor: init(title: String) where Placeholder == EmptyView
        view.Methods.Add(new MethodDecl
        {
            Name = "init",
            MangledName = "$s_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "Placeholder",
                    new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            new[] { "τ_0_0" },
                            SwiftTypeName.FromModuleQualifiedName("SwiftUI.EmptyView"),
                            ConformanceKind.ConcreteType)
                    },
                    new List<GenericParameterConformance>())
            },
            IsSynthesizedAccessor = false,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl { Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, SwiftTypeSpec = new NamedTypeSpec($"TestModule.{name}"), ParentDecl = null, ModuleDecl = null },
                new ArgumentDecl { Name = "title", PrivateName = "title", IsInOut = false, IsGeneric = false, SwiftTypeSpec = new NamedTypeSpec("Swift.String"), ParentDecl = null, ModuleDecl = null },
            },
        });

        // Parameterless modifier
        view.Methods.Add(CreateSelfReturningMethod(view, "playing"));

        return view;
    }

    // --- Constrained Generics Helpers ---

    /// <summary>
    /// Creates a generic view with a single non-View protocol constraint on τ_0_0.
    /// Has one constructor with a generic type param and a String param.
    /// e.g., struct HashableView&lt;T: Hashable&gt;: View { init(value: T, title: String) }
    /// </summary>
    private static StructDecl CreateGenericViewWithConstraint(string name, string constraintProtocol, string sugaredName = "T")
    {
        var view = new StructDecl
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
                new GenericArgumentDecl("τ_0_0", sugaredName,
                    new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            new[] { "τ_0_0" },
                            SwiftTypeName.FromModuleQualifiedName(constraintProtocol),
                            ConformanceKind.Protocol)
                    },
                    new List<GenericParameterConformance>())
            },
        };

        // Constructor: init(value: T, title: String)
        view.Methods.Add(new MethodDecl
        {
            Name = "init",
            MangledName = $"$s10TestModule{name.Length}{name}V_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", sugaredName,
                    new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            new[] { "τ_0_0" },
                            SwiftTypeName.FromModuleQualifiedName(constraintProtocol),
                            ConformanceKind.Protocol)
                    },
                    new List<GenericParameterConformance>())
            },
            IsSynthesizedAccessor = false,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "", PrivateName = "", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec($"TestModule.{name}"),
                    ParentDecl = null, ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = "value", PrivateName = "value", IsInOut = false, IsGeneric = true,
                    SwiftTypeSpec = new NamedTypeSpec("τ_0_0"),
                    ParentDecl = null, ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = "title", PrivateName = "title", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    ParentDecl = null, ModuleDecl = null,
                },
            },
        });

        return view;
    }

    /// <summary>
    /// Creates a generic view with multiple non-View protocol constraints on τ_0_0.
    /// </summary>
    private static StructDecl CreateGenericViewWithMultipleConstraints(string name, params string[] constraintProtocols)
    {
        var conformances = constraintProtocols.Select(p =>
            new GenericParameterConformance(
                new[] { "τ_0_0" },
                SwiftTypeName.FromModuleQualifiedName(p),
                ConformanceKind.Protocol)).ToList();

        var view = new StructDecl
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
                new GenericArgumentDecl("τ_0_0", "T", conformances,
                    new List<GenericParameterConformance>())
            },
        };

        view.Methods.Add(new MethodDecl
        {
            Name = "init",
            MangledName = $"$s10TestModule{name.Length}{name}V_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "T", conformances,
                    new List<GenericParameterConformance>())
            },
            IsSynthesizedAccessor = false,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "", PrivateName = "", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec($"TestModule.{name}"),
                    ParentDecl = null, ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = "key", PrivateName = "key", IsInOut = false, IsGeneric = true,
                    SwiftTypeSpec = new NamedTypeSpec("τ_0_0"),
                    ParentDecl = null, ModuleDecl = null,
                },
            },
        });

        return view;
    }

    /// <summary>
    /// Creates a generic view with two type params: A: View, B with specified constraint.
    /// Has init(title: String) — B doesn't appear as a direct param, only in the type.
    /// </summary>
    private static StructDecl CreateGenericViewMixedViewAndNonView(string name, string bConstraint)
    {
        var view = new StructDecl
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
                new GenericArgumentDecl("τ_0_0", "A",
                    new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            new[] { "τ_0_0" },
                            SwiftTypeName.FromModuleQualifiedName("SwiftUI.View"),
                            ConformanceKind.Protocol)
                    },
                    new List<GenericParameterConformance>()),
                new GenericArgumentDecl("τ_0_1", "B",
                    new List<GenericParameterConformance>
                    {
                        new GenericParameterConformance(
                            new[] { "τ_0_1" },
                            SwiftTypeName.FromModuleQualifiedName(bConstraint),
                            ConformanceKind.Protocol)
                    },
                    new List<GenericParameterConformance>()),
            },
        };

        view.Methods.Add(new MethodDecl
        {
            Name = "init",
            MangledName = $"$s10TestModule{name.Length}{name}V_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "", PrivateName = "", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec($"TestModule.{name}"),
                    ParentDecl = null, ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = "title", PrivateName = "title", IsInOut = false, IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    ParentDecl = null, ModuleDecl = null,
                },
            },
        });

        return view;
    }

    // --- Constrained Generics Tests ---

    [Theory]
    [InlineData("Swift.Hashable", "String")]
    [InlineData("Swift.Equatable", "String")]
    [InlineData("Swift.Comparable", "String")]
    [InlineData("Swift.Sendable", "String")]
    [InlineData("Swift.Codable", "String")]
    [InlineData("Swift.Numeric", "Int")]
    [InlineData("Swift.BinaryInteger", "Int")]
    [InlineData("Swift.SignedInteger", "Int")]
    [InlineData("Swift.FloatingPoint", "Double")]
    [InlineData("Swift.BinaryFloatingPoint", "Double")]
    public void AnalyzeGenericView_NonViewConstraint_ResolvesToConcreteType(string protocol, string expectedType)
    {
        var view = CreateGenericViewWithConstraint("TestView", protocol);
        var ctor = view.Methods[0];

        var analysis = SwiftUIBridgeEmitter.AnalyzeGenericView(view, ctor, 0);

        Assert.True(analysis.IsBridgeable);
        Assert.Equal(expectedType, analysis.ConcreteTypeArgs["τ_0_0"]);
        Assert.NotNull(analysis.NonViewResolvedParams);
        Assert.Contains("τ_0_0", analysis.NonViewResolvedParams);
    }

    [Fact]
    public void AnalyzeGenericView_IdentifiableConstraint_TemplateFallback()
    {
        var view = CreateGenericViewWithConstraint("IdView", "Swift.Identifiable");
        var ctor = view.Methods[0];

        var analysis = SwiftUIBridgeEmitter.AnalyzeGenericView(view, ctor, 0);

        Assert.False(analysis.IsBridgeable);
        Assert.Contains("no resolvable constraint", analysis.UnsupportedReason);
    }

    [Fact]
    public void AnalyzeGenericView_CustomProtocolConstraint_TemplateFallback()
    {
        var view = CreateGenericViewWithConstraint("CustomView", "MyModule.MyProtocol");
        var ctor = view.Methods[0];

        var analysis = SwiftUIBridgeEmitter.AnalyzeGenericView(view, ctor, 0);

        Assert.False(analysis.IsBridgeable);
        Assert.Contains("no resolvable constraint", analysis.UnsupportedReason);
    }

    [Fact]
    public void AnalyzeGenericView_MultipleConstraints_AllSameType_Resolves()
    {
        var view = CreateGenericViewWithMultipleConstraints("MultiView", "Swift.Hashable", "Swift.Comparable");
        var ctor = view.Methods[0];

        var analysis = SwiftUIBridgeEmitter.AnalyzeGenericView(view, ctor, 0);

        Assert.True(analysis.IsBridgeable);
        Assert.Equal("String", analysis.ConcreteTypeArgs["τ_0_0"]);
    }

    [Fact]
    public void AnalyzeGenericView_MultipleConstraints_DifferentTypes_PicksSpecific()
    {
        // Hashable → String, Numeric → Int. Int satisfies both → resolves to Int
        var view = CreateGenericViewWithMultipleConstraints("NumHashView", "Swift.Hashable", "Swift.Numeric");
        var ctor = view.Methods[0];

        var analysis = SwiftUIBridgeEmitter.AnalyzeGenericView(view, ctor, 0);

        Assert.True(analysis.IsBridgeable);
        Assert.Equal("Int", analysis.ConcreteTypeArgs["τ_0_0"]);
    }

    [Fact]
    public void AnalyzeGenericView_MultipleConstraints_OneUnknown_TemplateFallback()
    {
        // Hashable is known, Identifiable is not → template fallback
        var view = CreateGenericViewWithMultipleConstraints("MixedUnknown", "Swift.Hashable", "Swift.Identifiable");
        var ctor = view.Methods[0];

        var analysis = SwiftUIBridgeEmitter.AnalyzeGenericView(view, ctor, 0);

        Assert.False(analysis.IsBridgeable);
    }

    [Fact]
    public void AnalyzeGenericView_ViewResolvedParams_NotInNonViewSet()
    {
        var view = CreateGenericViewWithViewConstraint("ViewParamView");
        var ctor = view.Methods[0]; // ConcreteType constraint

        var analysis = SwiftUIBridgeEmitter.AnalyzeGenericView(view, ctor, 0);

        Assert.True(analysis.IsBridgeable);
        Assert.Null(analysis.NonViewResolvedParams); // View-resolved params not tracked
    }

    [Fact]
    public void AnalyzeGenericView_MixedViewAndHashable_BothResolved()
    {
        var view = CreateGenericViewMixedViewAndNonView("MixedResolved", "Swift.Hashable");

        var info = SwiftUIBridgeEmitter.AnalyzeView(view, "TestModule");

        Assert.Equal(ViewInitClassification.Simple, info.Classification);
        Assert.NotNull(info.GenericAnalysis);
        Assert.True(info.GenericAnalysis.IsBridgeable);
        Assert.Equal("EmptyView", info.GenericAnalysis.ConcreteTypeArgs["τ_0_0"]);
        Assert.Equal("String", info.GenericAnalysis.ConcreteTypeArgs["τ_0_1"]);
        Assert.Contains("τ_0_1", info.GenericAnalysis.NonViewResolvedParams!);
        Assert.DoesNotContain("τ_0_0", info.GenericAnalysis.NonViewResolvedParams!);
    }

    [Fact]
    public void AnalyzeView_HashableConstraint_ClassifiedAsSimple()
    {
        var view = CreateGenericViewWithConstraint("HashableView", "Swift.Hashable");

        var info = SwiftUIBridgeEmitter.AnalyzeView(view, "TestModule");

        Assert.Equal(ViewInitClassification.Simple, info.Classification);
        Assert.Null(info.UnsupportedReason);
        Assert.NotNull(info.GenericAnalysis);
        Assert.True(info.GenericAnalysis.IsBridgeable);
    }

    [Fact]
    public void InitAnalyzer_NonViewGenericParam_BridgedAsString()
    {
        var view = CreateGenericViewWithConstraint("HashView", "Swift.Hashable");
        var ctor = view.Methods[0];
        var analysis = SwiftUIBridgeEmitter.AnalyzeGenericView(view, ctor, 0);

        var bridgeParams = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, null, analysis, out var synthesized);

        Assert.NotNull(bridgeParams);
        // "value" param should be bridged as String (not synthesized)
        Assert.Equal(2, bridgeParams.Count); // value + title
        Assert.Equal("value", bridgeParams[0].Name);
        Assert.Equal(BridgeParameterKind.String, bridgeParams[0].Kind);
        Assert.Equal("title", bridgeParams[1].Name);
        Assert.Equal(BridgeParameterKind.String, bridgeParams[1].Kind);
        Assert.Null(synthesized); // No synthesized args
    }

    [Fact]
    public void InitAnalyzer_NumericGenericParam_BridgedAsInt()
    {
        var view = CreateGenericViewWithConstraint("NumView", "Swift.Numeric");
        var ctor = view.Methods[0];
        var analysis = SwiftUIBridgeEmitter.AnalyzeGenericView(view, ctor, 0);

        var bridgeParams = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, null, analysis, out var synthesized);

        Assert.NotNull(bridgeParams);
        Assert.Equal(2, bridgeParams.Count);
        Assert.Equal("value", bridgeParams[0].Name);
        Assert.Equal(BridgeParameterKind.Primitive, bridgeParams[0].Kind);
        Assert.Equal("nint", bridgeParams[0].CSharpPInvokeType); // Swift.Int → nint
        Assert.Null(synthesized);
    }

    [Fact]
    public void InitAnalyzer_FloatingPointGenericParam_BridgedAsDouble()
    {
        var view = CreateGenericViewWithConstraint("FloatView", "Swift.FloatingPoint");
        var ctor = view.Methods[0];
        var analysis = SwiftUIBridgeEmitter.AnalyzeGenericView(view, ctor, 0);

        var bridgeParams = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, null, analysis, out var synthesized);

        Assert.NotNull(bridgeParams);
        Assert.Equal("value", bridgeParams[0].Name);
        Assert.Equal(BridgeParameterKind.Primitive, bridgeParams[0].Kind);
        Assert.Equal("double", bridgeParams[0].CSharpPInvokeType);
    }

    [Fact]
    public void GenericView_HashableParam_SwiftOutput_HasStringTypeArg()
    {
        var view = CreateGenericViewWithConstraint("HashableView", "Swift.Hashable");
        var views = new List<TypeDecl> { view };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // The view type should be specialized with <String>
        Assert.Contains("HashableView<String>", swiftContent);
        // The "value" param should be bridged as a String ptr+len, not synthesized
        Assert.Contains("valuePtr", swiftContent);
        Assert.Contains("valueLen", swiftContent);
    }

    [Fact]
    public void GenericView_HashableParam_CSharpOutput_HasStringParam()
    {
        var view = CreateGenericViewWithConstraint("HashableView", "Swift.Hashable");
        var views = new List<TypeDecl> { view };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        // C# should have both "value" and "title" as string? parameters
        Assert.Contains("string? value", csContent);
        Assert.Contains("string? title", csContent);
    }

    [Fact]
    public void GenericView_NumericParam_SwiftOutput_HasIntTypeArg()
    {
        var view = CreateGenericViewWithConstraint("NumericView", "Swift.Numeric");
        var views = new List<TypeDecl> { view };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("NumericView<Int>", swiftContent);
        // "value" bridged as Int (primitive), "title" as String
        Assert.Contains("_ value:", swiftContent);
    }

    [Fact]
    public void GenericView_MixedViewAndHashable_SwiftOutput()
    {
        var view = CreateGenericViewMixedViewAndNonView("MixedView2", "Swift.Hashable");
        var views = new List<TypeDecl> { view };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // Both type args should appear: EmptyView for View, String for Hashable
        Assert.Contains("MixedView2<EmptyView, String>", swiftContent);
    }

    [Fact]
    public void ResolveNonViewConstraint_SingleKnown_ReturnsType()
    {
        var conformances = new List<GenericParameterConformance>
        {
            new GenericParameterConformance(
                new[] { "τ_0_0" },
                SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"),
                ConformanceKind.Protocol)
        };

        var result = SwiftUIBridgeEmitter.ResolveNonViewConstraint(conformances);

        Assert.Equal("String", result);
    }

    [Fact]
    public void ResolveNonViewConstraint_UnknownProtocol_ReturnsNull()
    {
        var conformances = new List<GenericParameterConformance>
        {
            new GenericParameterConformance(
                new[] { "τ_0_0" },
                SwiftTypeName.FromModuleQualifiedName("Swift.Identifiable"),
                ConformanceKind.Protocol)
        };

        var result = SwiftUIBridgeEmitter.ResolveNonViewConstraint(conformances);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveNonViewConstraint_ConflictingTypes_FindsCommon()
    {
        // Hashable → String, Numeric → Int. Int conforms to both → Int
        var conformances = new List<GenericParameterConformance>
        {
            new GenericParameterConformance(new[] { "τ_0_0" }, SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"), ConformanceKind.Protocol),
            new GenericParameterConformance(new[] { "τ_0_0" }, SwiftTypeName.FromModuleQualifiedName("Swift.Numeric"), ConformanceKind.Protocol),
        };

        var result = SwiftUIBridgeEmitter.ResolveNonViewConstraint(conformances);

        Assert.Equal("Int", result);
    }

    [Fact]
    public void ResolveNonViewConstraint_FloatingPointAndHashable_ResolvesToDouble()
    {
        var conformances = new List<GenericParameterConformance>
        {
            new GenericParameterConformance(new[] { "τ_0_0" }, SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"), ConformanceKind.Protocol),
            new GenericParameterConformance(new[] { "τ_0_0" }, SwiftTypeName.FromModuleQualifiedName("Swift.FloatingPoint"), ConformanceKind.Protocol),
        };

        var result = SwiftUIBridgeEmitter.ResolveNonViewConstraint(conformances);

        Assert.Equal("Double", result);
    }

    [Fact]
    public void ResolveNonViewConstraint_IgnoresViewConstraints()
    {
        // View constraints are filtered out; remaining Hashable resolves normally
        var conformances = new List<GenericParameterConformance>
        {
            new GenericParameterConformance(new[] { "τ_0_0" }, SwiftTypeName.FromModuleQualifiedName("SwiftUI.View"), ConformanceKind.Protocol),
            new GenericParameterConformance(new[] { "τ_0_0" }, SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"), ConformanceKind.Protocol),
        };

        var result = SwiftUIBridgeEmitter.ResolveNonViewConstraint(conformances);

        Assert.Equal("String", result);
    }

    [Fact]
    public void ResolveNonViewConstraint_StringAndFloatingPoint_Irreconcilable()
    {
        // ExpressibleByStringLiteral → String, FloatingPoint → Double.
        // String doesn't conform to FloatingPoint → null
        var conformances = new List<GenericParameterConformance>
        {
            new GenericParameterConformance(new[] { "τ_0_0" }, SwiftTypeName.FromModuleQualifiedName("Swift.ExpressibleByStringLiteral"), ConformanceKind.Protocol),
            new GenericParameterConformance(new[] { "τ_0_0" }, SwiftTypeName.FromModuleQualifiedName("Swift.FloatingPoint"), ConformanceKind.Protocol),
        };

        var result = SwiftUIBridgeEmitter.ResolveNonViewConstraint(conformances);

        Assert.Null(result);
    }

    #endregion

    #region Lifecycle Callbacks

    [Fact]
    public void Lifecycle_Swift_StateHasLifecycleVars()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("LifeView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("var lifecycleOnAppear: (() -> Void)? = nil", swiftContent);
        Assert.Contains("var lifecycleOnDisappear: (() -> Void)? = nil", swiftContent);
    }

    [Fact]
    public void Lifecycle_Swift_HasSetLifecycleFunction()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("LifeView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_TestModule_LifeView_SetLifecycle\")", swiftContent);
        Assert.Contains("_ onAppearCb: (@convention(c) (UnsafeMutableRawPointer?) -> Void)?", swiftContent);
        Assert.Contains("_ onDisappearCb: (@convention(c) (UnsafeMutableRawPointer?) -> Void)?", swiftContent);
    }

    [Fact]
    public void Lifecycle_Swift_WrapperHasLifecycleModifiers()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("LifeView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains(".onAppear { [state] in state.lifecycleOnAppear?() }", swiftContent);
        Assert.Contains(".onDisappear { [state] in state.lifecycleOnDisappear?() }", swiftContent);
    }

    [Fact]
    public void Lifecycle_Swift_CreateSignatureUnchanged()
    {
        // Lifecycle uses SetLifecycle, not Create params — verify Create doesn't have lifecycle params
        var views = new List<TypeDecl> { CreateSimpleViewStruct("LifeView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.DoesNotContain("SBW_TestModule_LifeView_Create(onAppearCb", swiftContent);
    }

    [Fact]
    public void Lifecycle_CSharp_HasTrampolines()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("LifeView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("OnAppearTrampoline", csContent);
        Assert.Contains("OnDisappearTrampoline", csContent);
        Assert.Contains("[global::System.Runtime.InteropServices.UnmanagedCallersOnly(", csContent);
    }

    [Fact]
    public void Lifecycle_CSharp_CreateHasOptionalActionParams()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("LifeView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("Action? onAppear = null", csContent);
        Assert.Contains("Action? onDisappear = null", csContent);
    }

    [Fact]
    public void Lifecycle_CSharp_HasSetLifecyclePInvoke()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("LifeView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("SBW_TestModule_LifeView_SetLifecycle", csContent);
        Assert.Contains("SetLifecycleCallbacks", csContent);
    }

    [Fact]
    public void Lifecycle_CSharp_SessionHasLifecycleHandlesField()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("LifeView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("_lifecycleHandles", csContent);
    }

    #endregion

    #region Universal Modifiers

    [Fact]
    public void UniversalModifiers_Swift_StateHasModifierVars()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("ModView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@Published var u_frameWidth: CGFloat? = nil", swiftContent);
        Assert.Contains("@Published var u_frameHeight: CGFloat? = nil", swiftContent);
        Assert.Contains("@Published var u_padding: CGFloat? = nil", swiftContent);
        Assert.Contains("@Published var u_backgroundColor: SwiftUI.Color? = nil", swiftContent);
        Assert.Contains("@Published var u_foregroundColor: SwiftUI.Color? = nil", swiftContent);
        Assert.Contains("@Published var u_cornerRadius: CGFloat? = nil", swiftContent);
        Assert.Contains("@Published var u_opacity: Double? = nil", swiftContent);
        Assert.Contains("@Published var u_font: SwiftUI.Font? = nil", swiftContent);
    }

    [Fact]
    public void UniversalModifiers_Swift_HasApplyHelper()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("ModView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("func applyUniversalModifiers<V: View>(_ view: V) -> AnyView", swiftContent);
    }

    [Fact]
    public void UniversalModifiers_Swift_EmitsSetFunctions()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("ModView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_TestModule_ModView_SetFrame\")", swiftContent);
        Assert.Contains("@_cdecl(\"SBW_TestModule_ModView_SetPadding\")", swiftContent);
        Assert.Contains("@_cdecl(\"SBW_TestModule_ModView_SetBackground\")", swiftContent);
        Assert.Contains("@_cdecl(\"SBW_TestModule_ModView_SetForegroundColor\")", swiftContent);
        Assert.Contains("@_cdecl(\"SBW_TestModule_ModView_SetCornerRadius\")", swiftContent);
        Assert.Contains("@_cdecl(\"SBW_TestModule_ModView_SetOpacity\")", swiftContent);
        Assert.Contains("@_cdecl(\"SBW_TestModule_ModView_SetFont\")", swiftContent);
    }

    [Fact]
    public void UniversalModifiers_Swift_SetBackground_UsesRGBA()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("ModView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("SwiftUI.Color(red: r, green: g, blue: b, opacity: a)", swiftContent);
    }

    [Fact]
    public void UniversalModifiers_Swift_SetFont_UsesSystemSize()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("ModView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("SwiftUI.Font.system(size: CGFloat(size))", swiftContent);
    }

    [Fact]
    public void UniversalModifiers_CSharp_HasPInvokes()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("ModView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("SBW_TestModule_ModView_SetFrame", csContent);
        Assert.Contains("SBW_TestModule_ModView_SetPadding", csContent);
        Assert.Contains("SBW_TestModule_ModView_SetBackground", csContent);
        Assert.Contains("SBW_TestModule_ModView_SetForegroundColor", csContent);
        Assert.Contains("SBW_TestModule_ModView_SetCornerRadius", csContent);
        Assert.Contains("SBW_TestModule_ModView_SetOpacity", csContent);
        Assert.Contains("SBW_TestModule_ModView_SetFont", csContent);
    }

    [Fact]
    public void UniversalModifiers_CSharp_HasPublicMethods()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("ModView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("public void SetFrame(double? width = null, double? height = null)", csContent);
        Assert.Contains("public void SetPadding(double? value)", csContent);
        Assert.Contains("public void SetBackground(double r, double g, double b, double a = 1.0)", csContent);
        Assert.Contains("public void ClearBackground()", csContent);
        Assert.Contains("public void SetForegroundColor(double r, double g, double b, double a = 1.0)", csContent);
        Assert.Contains("public void ClearForegroundColor()", csContent);
        Assert.Contains("public void SetCornerRadius(double? value)", csContent);
        Assert.Contains("public void SetOpacity(double? value)", csContent);
        Assert.Contains("public void SetFontSize(double? size)", csContent);
    }

    #endregion

    #region Presentation Helpers

    [Fact]
    public void Presentation_Swift_EmitsPresentDismissFunctions()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("PresView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl(\"SBW_TestModule_PresView_PresentAsSheet\")", swiftContent);
        Assert.Contains("@_cdecl(\"SBW_TestModule_PresView_PushOnNav\")", swiftContent);
        Assert.Contains("@_cdecl(\"SBW_TestModule_PresView_Dismiss\")", swiftContent);
    }

    [Fact]
    public void Presentation_Swift_PresentAsSheet_UsesUIViewController()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("PresView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("Unmanaged<UIViewController>.fromOpaque(fromVC).takeUnretainedValue()", swiftContent);
        Assert.Contains("parent.present(session.hostingController, animated: true)", swiftContent);
    }

    [Fact]
    public void Presentation_Swift_PushOnNav_UsesUINavigationController()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("PresView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("Unmanaged<UINavigationController>.fromOpaque(navVC).takeUnretainedValue()", swiftContent);
        Assert.Contains("nav.pushViewController(session.hostingController, animated: true)", swiftContent);
    }

    [Fact]
    public void Presentation_Swift_Dismiss_DismissesHostingController()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("PresView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("session.hostingController.dismiss(animated: true)", swiftContent);
    }

    [Fact]
    public void Presentation_CSharp_HasPInvokes()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("PresView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("SBW_TestModule_PresView_PresentAsSheet", csContent);
        Assert.Contains("SBW_TestModule_PresView_PushOnNav", csContent);
        Assert.Contains("SBW_TestModule_PresView_Dismiss", csContent);
    }

    [Fact]
    public void Presentation_CSharp_HasPublicMethods()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("PresView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("public void PresentAsSheet(IntPtr fromViewController)", csContent);
        Assert.Contains("public void PushOnNavigationStack(IntPtr navigationController)", csContent);
        Assert.Contains("public void Dismiss()", csContent);
    }

    #endregion

    #region Always-Wrapper & Closure Handle Integration

    [Fact]
    public void AlwaysWrapper_CSharp_SessionAlwaysHasLifecycleHandles()
    {
        // All views get _lifecycleHandles (for lifecycle GCHandles)
        var views = new List<TypeDecl> { CreateSimpleViewStruct("PlainView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("_lifecycleHandles", csContent);
        Assert.Contains("GCHandle[]", csContent);
    }

    [Fact]
    public void AlwaysWrapper_CSharp_CreateCallsSetLifecycle()
    {
        // Create factory calls SetLifecycleCallbacks on the session
        var views = new List<TypeDecl> { CreateSimpleViewStruct("PlainView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("session.SetLifecycleCallbacks(onAppear, onDisappear)", csContent);
    }

    [Fact]
    public void AlwaysWrapper_Swift_WrapperBodyUsesUniversalModifiers()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("PlainView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("applyUniversalModifiers(PlainView())", swiftContent);
    }

    [Fact]
    public void UniversalModifier_Dedup_SkipsSetOpacityWhenViewHasOpacityModifier()
    {
        // View with an "opacity" modifier method (returns Self) — same name as universal SetOpacity
        var view = CreateViewWithModifierMethods("OpacityView");
        view.Methods.Add(new MethodDecl
        {
            Name = "opacity",
            MangledName = "$s_opacity",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAccessor = false,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
            ParentDecl = view,
            ModuleDecl = null,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl { Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, SwiftTypeSpec = new NamedTypeSpec("TestModule.OpacityView"), ParentDecl = null, ModuleDecl = null },
                new ArgumentDecl { Name = "level", PrivateName = "level", IsInOut = false, IsGeneric = false, SwiftTypeSpec = new NamedTypeSpec("Swift.Double"), ParentDecl = null, ModuleDecl = null },
            },
        });

        var views = new List<TypeDecl> { view };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));

        // View-specific SetOpacity should exist (from the modifier chain)
        Assert.Contains("mod_opacity", swiftContent); // view-specific state var

        // Universal SetOpacity should NOT be duplicated — only one @_cdecl SetOpacity
        var setOpacityCount = System.Text.RegularExpressions.Regex.Matches(
            swiftContent, @"@_cdecl\(""SBW_TestModule_OpacityView_SetOpacity""\)").Count;
        Assert.Equal(1, setOpacityCount);

        // Other universal modifiers should still be present
        Assert.Contains("SBW_TestModule_OpacityView_SetFrame", swiftContent);
        Assert.Contains("SBW_TestModule_OpacityView_SetPadding", swiftContent);
        Assert.Contains("SBW_TestModule_OpacityView_SetFont", swiftContent);

        // C# side: only one SetOpacity P/Invoke
        var csSetOpacityPInvokes = System.Text.RegularExpressions.Regex.Matches(
            csContent, @"internal static partial void SetOpacity").Count;
        Assert.Equal(1, csSetOpacityPInvokes);
    }

    #endregion

    #region Observable Binding

    [Fact]
    public void ObservableBinding_EmitsBindToMethod()
    {
        var views = new List<TypeDecl> { CreateViewWithPrimitiveAndStringInit("CounterView", "count", "Swift.Int32", "label") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("public void BindTo<", csContent);
        Assert.Contains("DynamicallyAccessedMembers", csContent);
        Assert.Contains("PublicProperties", csContent);
        // The namespace-qualified spelling the emitter actually writes: the bridge shares a
        // namespace with the bound module's types, so a Swift type projected as `System` would
        // otherwise capture the root identifier of this constraint.
        Assert.Contains("where T : global::System.ComponentModel.INotifyPropertyChanged", csContent);
    }

    [Fact]
    public void ObservableBinding_EmitsUnbindMethod()
    {
        var views = new List<TypeDecl> { CreateViewWithPrimitiveAndStringInit("CounterView", "count", "Swift.Int32", "label") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("public void Unbind()", csContent);
    }

    [Fact]
    public void ObservableBinding_EmitsPropertyChangedHandler()
    {
        var views = new List<TypeDecl> { CreateViewWithPrimitiveAndStringInit("CounterView", "count", "Swift.Int32", "label") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("OnBoundPropertyChanged", csContent);
        Assert.Contains("PropertyChangedEventArgs", csContent);
    }

    [Fact]
    public void ObservableBinding_EmitsPropertyDispatchers()
    {
        var views = new List<TypeDecl> { CreateViewWithPrimitiveAndStringInit("CounterView", "count", "Swift.Int32", "label") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        // Property dispatchers built in BindTo using viewModel.GetType().GetProperty (runtime type)
        Assert.Contains("viewModel.GetType().GetProperty(\"Count\")", csContent);
        Assert.Contains("viewModel.GetType().GetProperty(\"Label\")", csContent);
        Assert.Contains("UpdateCount(", csContent);
        Assert.Contains("UpdateLabel(", csContent);
    }

    [Fact]
    public void ObservableBinding_DisposeCallsUnbind()
    {
        var views = new List<TypeDecl> { CreateViewWithPrimitiveAndStringInit("CounterView", "count", "Swift.Int32", "label") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        // Dispose should call Unbind before releasing native handle
        Assert.Contains("Unbind();", csContent);
    }

    [Fact]
    public void ObservableBinding_ClosureOnlyView_NoBindTo()
    {
        var views = new List<TypeDecl> { CreateViewWithVoidClosureInit("ActionView", "doSomething") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        // Closure-only views have no updatable params → no BindTo
        Assert.DoesNotContain("BindTo", csContent);
        Assert.DoesNotContain("Unbind", csContent);
        Assert.DoesNotContain("_boundViewModel", csContent);
    }

    [Fact]
    public void ObservableBinding_MixedView_BindsOnlyUpdatableParams()
    {
        var views = new List<TypeDecl> { CreateViewWithMixedUpdatableInit("MixedView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        // BindTo should be present (view has updatable params: title, isEnabled)
        Assert.Contains("public void BindTo<", csContent);
        // Dispatchers should include updatable params only (resolved via viewModel.GetType().GetProperty)
        Assert.Contains("GetProperty(\"Title\")", csContent);
        Assert.Contains("GetProperty(\"IsEnabled\")", csContent);
        // Closure should NOT be in dispatchers
        Assert.DoesNotContain("GetProperty(\"OnTap\")", csContent);
    }

    [Fact]
    public void ObservableBinding_BindToThrowsIfAlreadyBound()
    {
        var views = new List<TypeDecl> { CreateViewWithPrimitiveAndStringInit("TestView", "count", "Swift.Int32", "label") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("Already bound to a view model", csContent);
    }

    [Fact]
    public void ObservableBinding_BindToThrowsIfDisposed()
    {
        var views = new List<TypeDecl> { CreateViewWithPrimitiveAndStringInit("TestView", "count", "Swift.Int32", "label") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("ObjectDisposedException.ThrowIf(_disposed, this)", csContent);
    }

    [Fact]
    public void ObservableBinding_BoolParam_DispatcherUsesBoolCast()
    {
        var views = new List<TypeDecl> { CreateViewWithMixedUpdatableInit("BoolView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        // Bool dispatcher should cast reflected value to bool
        Assert.Contains("GetProperty(\"IsEnabled\")", csContent);
        Assert.Contains("(bool)", csContent);
    }

    [Fact]
    public void ObservableBinding_AllPropertiesChanged_DispatchesAll()
    {
        var views = new List<TypeDecl> { CreateViewWithPrimitiveAndStringInit("CounterView", "count", "Swift.Int32", "label") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        // null or empty PropertyName should iterate all dispatchers
        Assert.Contains("string.IsNullOrEmpty(e.PropertyName)", csContent);
        Assert.Contains("foreach (var kvp in _propertyDispatchers) kvp.Value()", csContent);
    }

    [Fact]
    public void ObservableBinding_TrimSafety_DamPlusRuntimeTypeReflection()
    {
        var views = new List<TypeDecl> { CreateViewWithPrimitiveAndStringInit("CounterView", "count", "Swift.Int32", "label") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        // DAM on BindTo<T> for best-effort trim hint; viewModel.GetType() for runtime type resolution
        Assert.Contains("DynamicallyAccessedMembers", csContent);
        Assert.Contains("viewModel.GetType().GetProperty(", csContent);
        // Handler has zero reflection — dispatchers are pre-built closures
        Assert.DoesNotContain("sender.GetType()", csContent);
        // Scoped IL2075 pragma around BindTo property resolution (known .NET trimming gap)
        Assert.Contains("#pragma warning disable IL2075", csContent);
    }

    [Fact]
    public void ObservableBinding_ParameterlessView_NoBindTo()
    {
        var views = new List<TypeDecl> { CreateSimpleViewStruct("EmptyView") };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        // No updatable params → no BindTo
        Assert.DoesNotContain("BindTo", csContent);
    }

    #endregion

    #region BridgeSummary

    [Fact]
    public void BridgeSummary_Populated_ForModuleWithViews()
    {
        ReportCollector.Reset();
        var moduleDecl = CreateModuleDecl();
        ReportCollector.Start(moduleDecl);

        var views = new List<TypeDecl>
        {
            CreateSimpleViewStruct("View1"),
            CreateSimpleViewStruct("View2"),
            CreateGenericViewStruct("View3"),
        };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var report = ReportCollector.Complete()!;
        Assert.NotNull(report.BridgeSummary);
        Assert.Equal(3, report.BridgeSummary.TotalViews);
        Assert.Equal(2, report.BridgeSummary.Generated);
        Assert.Equal(1, report.BridgeSummary.Template);
        Assert.Equal(0, report.BridgeSummary.HintSkipped);
        Assert.True(report.BridgeSummary.GeneratedPercent > 60);

        ReportCollector.Reset();
    }

    [Fact]
    public void BridgeSummary_Null_ForModuleWithoutViews()
    {
        ReportCollector.Reset();
        var moduleDecl = CreateModuleDecl();
        ReportCollector.Start(moduleDecl);

        // Don't emit any bridge files — simulate a module with no views
        var report = ReportCollector.Complete()!;
        Assert.Null(report.BridgeSummary);

        ReportCollector.Reset();
    }

    [Fact]
    public void BridgeSummary_AllGenerated_ShowsHundredPercent()
    {
        ReportCollector.Reset();
        var moduleDecl = CreateModuleDecl();
        ReportCollector.Start(moduleDecl);

        var views = new List<TypeDecl>
        {
            CreateSimpleViewStruct("View1"),
            CreateSimpleViewStruct("View2"),
        };
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance);

        var report = ReportCollector.Complete()!;
        Assert.NotNull(report.BridgeSummary);
        Assert.Equal(2, report.BridgeSummary.TotalViews);
        Assert.Equal(2, report.BridgeSummary.Generated);
        Assert.Equal(0, report.BridgeSummary.Template);
        Assert.Equal(100.0, report.BridgeSummary.GeneratedPercent);

        ReportCollector.Reset();
    }

    #endregion

    #region Gate Improvements — Binding<T>, SwiftUI.Image, Array<T>

    // --- Binding<T> Tests ---

    [Fact]
    public void BindingBool_MapParameterType_ReturnsPrimitiveWithIsBinding()
    {
        var param = new ArgumentDecl
        {
            Name = "isOn",
            PrivateName = "isOn",
            IsInOut = false,
            IsGeneric = false,
            SwiftTypeSpec = new NamedTypeSpec("SwiftUI.Binding", new NamedTypeSpec("Swift.Bool")),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var result = SwiftUIBridgeEmitter.MapParameterType(param, null);

        Assert.NotNull(result);
        Assert.Equal(BridgeParameterKind.Primitive, result.Kind);
        Assert.True(result.IsBinding);
        Assert.Equal("Int32", result.SwiftAbiType);
        Assert.Equal("!= 0", result.SwiftConversion);
    }

    [Fact]
    public void BindingString_MapParameterType_ReturnsStringWithIsBinding()
    {
        var param = new ArgumentDecl
        {
            Name = "text",
            PrivateName = "text",
            IsInOut = false,
            IsGeneric = false,
            SwiftTypeSpec = new NamedTypeSpec("SwiftUICore.Binding", new NamedTypeSpec("Swift.String")),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var result = SwiftUIBridgeEmitter.MapParameterType(param, null);

        Assert.NotNull(result);
        Assert.Equal(BridgeParameterKind.String, result.Kind);
        Assert.True(result.IsBinding);
    }

    [Fact]
    public void BindingBool_Wrapper_UsesBindingProjection()
    {
        var view = CreateViewStructWithNoConstructor("ToggleView");
        view.Methods.Add(CreateConstructorWithBindingParam("isOn", "Swift.Bool"));

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule",
            new List<TypeDecl> { view }, NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // The Wrapper should use $state.isOn (Binding projection), not state.isOn
        Assert.Contains("$state.isOn", swiftContent);
    }

    [Fact]
    public void BindingInt_MapParameterType_ReturnsPrimitiveWithIsBinding()
    {
        var param = new ArgumentDecl
        {
            Name = "count",
            PrivateName = "count",
            IsInOut = false,
            IsGeneric = false,
            SwiftTypeSpec = new NamedTypeSpec("SwiftUI.Binding", new NamedTypeSpec("Swift.Int")),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var result = SwiftUIBridgeEmitter.MapParameterType(param, null);

        Assert.NotNull(result);
        Assert.Equal(BridgeParameterKind.Primitive, result.Kind);
        Assert.True(result.IsBinding);
        Assert.Equal("Int", result.SwiftAbiType);
    }

    [Fact]
    public void BindingUnsupportedType_MapParameterType_ReturnsNull()
    {
        // Binding<SomeCustomClass> is not supported — complex lifetime management
        var param = new ArgumentDecl
        {
            Name = "model",
            PrivateName = "model",
            IsInOut = false,
            IsGeneric = false,
            SwiftTypeSpec = new NamedTypeSpec("SwiftUI.Binding",
                new NamedTypeSpec("TestModule.SomeClass")),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var result = SwiftUIBridgeEmitter.MapParameterType(param, null);

        Assert.Null(result);
    }

    [Fact]
    public void BindingBoundEnum_MapParameterType_ReturnsEnumWithIsBinding()
    {
        var param = new ArgumentDecl
        {
            Name = "style",
            PrivateName = "style",
            IsInOut = false,
            IsGeneric = false,
            SwiftTypeSpec = new NamedTypeSpec("SwiftUI.Binding",
                new NamedTypeSpec("TestModule.AlertStyle")),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var context = new BridgeContext(CreateEnumTypeDatabase());
        var result = SwiftUIBridgeEmitter.MapParameterType(param, context);

        Assert.NotNull(result);
        Assert.Equal(BridgeParameterKind.BoundEnum, result.Kind);
        Assert.True(result.IsBinding);
    }

    [Fact]
    public void BindingOptionalString_MapParameterType_ReturnsOptionalWrappedWithIsBinding()
    {
        var param = new ArgumentDecl
        {
            Name = "title",
            PrivateName = "title",
            IsInOut = false,
            IsGeneric = false,
            SwiftTypeSpec = new NamedTypeSpec("SwiftUI.Binding",
                new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.String"))),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var result = SwiftUIBridgeEmitter.MapParameterType(param, null);

        Assert.NotNull(result);
        Assert.Equal(BridgeParameterKind.OptionalWrapped, result.Kind);
        Assert.True(result.IsBinding);
        Assert.Equal(BridgeParameterKind.String, result.InnerParameter!.Kind);
    }

    [Fact]
    public void BindingOptionalBool_MapParameterType_ReturnsOptionalWrappedWithIsBinding()
    {
        var param = new ArgumentDecl
        {
            Name = "isEnabled",
            PrivateName = "isEnabled",
            IsInOut = false,
            IsGeneric = false,
            SwiftTypeSpec = new NamedTypeSpec("SwiftUI.Binding",
                new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Bool"))),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var result = SwiftUIBridgeEmitter.MapParameterType(param, null);

        Assert.NotNull(result);
        Assert.Equal(BridgeParameterKind.OptionalWrapped, result.Kind);
        Assert.True(result.IsBinding);
        Assert.True(result.InnerParameter!.SwiftConversion == "!= 0");
    }

    [Fact]
    public void BindingOptionalEnum_MapParameterType_ReturnsOptionalWrappedWithIsBinding()
    {
        var param = new ArgumentDecl
        {
            Name = "style",
            PrivateName = "style",
            IsInOut = false,
            IsGeneric = false,
            SwiftTypeSpec = new NamedTypeSpec("SwiftUI.Binding",
                new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("TestModule.AlertStyle"))),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var context = new BridgeContext(CreateEnumTypeDatabase());
        var result = SwiftUIBridgeEmitter.MapParameterType(param, context);

        Assert.NotNull(result);
        Assert.Equal(BridgeParameterKind.OptionalWrapped, result.Kind);
        Assert.True(result.IsBinding);
        Assert.Equal(BridgeParameterKind.BoundEnum, result.InnerParameter!.Kind);
    }

    // --- Binding<Codable Struct> Tests ---

    [Fact]
    public void BindingCodableStruct_MapParameterType_FlagsIsBindingCodableStruct()
    {
        var module = CreateModuleDecl();
        module.Types.Add(MakeCodableStructDecl(module, "Profile"));

        var param = new ArgumentDecl
        {
            Name = "profile",
            PrivateName = "profile",
            IsInOut = false,
            IsGeneric = false,
            SwiftTypeSpec = new NamedTypeSpec("SwiftUI.Binding",
                new NamedTypeSpec("TestModule.Profile")),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var context = new BridgeContext(CreateCodableStructTypeDatabase("Profile"), module);
        var result = SwiftUIBridgeEmitter.MapParameterType(param, context);

        Assert.NotNull(result);
        Assert.True(result.IsBinding);
        Assert.True(result.IsBindingCodableStruct);
        Assert.Equal(BridgeParameterKind.BoundStruct, result.Kind);
    }

    [Fact]
    public void BindingNonCodableStruct_MapParameterType_DoesNotFlagCodable()
    {
        var module = CreateModuleDecl();
        // Add a non-Codable struct (no Encodable/Decodable conformance).
        var plain = MakeCodableStructDecl(module, "Plain");
        plain.Conformances.Clear();
        module.Types.Add(plain);

        var param = new ArgumentDecl
        {
            Name = "plain",
            PrivateName = "plain",
            IsInOut = false,
            IsGeneric = false,
            SwiftTypeSpec = new NamedTypeSpec("SwiftUI.Binding",
                new NamedTypeSpec("TestModule.Plain")),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var context = new BridgeContext(CreateCodableStructTypeDatabase("Plain"), module);
        var result = SwiftUIBridgeEmitter.MapParameterType(param, context);

        // Plain struct without Codable conformance → either returns null (Binding rejected)
        // or returns the param without IsBindingCodableStruct. Either way, MUST NOT flag Codable.
        if (result != null)
            Assert.False(result.IsBindingCodableStruct);
    }

    [Fact]
    public void BindingCodableStruct_MapParameterType_RejectsWhenAsyncLibraryNameEmpty()
    {
        var module = CreateModuleDecl();
        module.Types.Add(MakeCodableStructDecl(module, "Profile"));

        var param = new ArgumentDecl
        {
            Name = "profile",
            PrivateName = "profile",
            IsInOut = false,
            IsGeneric = false,
            SwiftTypeSpec = new NamedTypeSpec("SwiftUI.Binding",
                new NamedTypeSpec("TestModule.Profile")),
            ParentDecl = null,
            ModuleDecl = null,
        };

        // No wrapper library configured (xcframework-less mode): the bridge gate must
        // reject so it doesn't emit calls to EncodeToJson / DecodeFromJson members the
        // CodableJsonEmitter will not have produced.
        var typeDbNoWrapper = new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["TestModule.Profile"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Profile"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Profile"),
                MetadataAccessor = "$s10TestModule7ProfileVMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
            },
        });
        var context = new BridgeContext(typeDbNoWrapper, module);
        var result = SwiftUIBridgeEmitter.MapParameterType(param, context);

        if (result != null)
            Assert.False(result.IsBindingCodableStruct);
    }

    private static ITypeDatabase CreateCodableStructTypeDatabase(string simpleName)
    {
        // IsCodableStructForBinding rejects when AsyncLibraryName is empty (mirrors
        // CodableJsonEmitter's xcframework-less-mode skip), so the helper plants a
        // non-empty wrapper library name.
        return new BridgeTestTypeDatabase(
            new Dictionary<string, TypeRecord>
            {
                [$"TestModule.{simpleName}"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", simpleName),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{simpleName}"),
                    MetadataAccessor = $"$s10TestModule{simpleName.Length}{simpleName}VMa",
                    Flags = TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Struct,
                },
            },
            asyncLibraryName: "TestModuleSwiftBindings");
    }

    private static StructDecl MakeCodableStructDecl(ModuleDecl module, string name)
    {
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}");
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = swiftTypeName,
            MangledName = $"$s10TestModule{name.Length}{name}V",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>
            {
                new(swiftTypeName,
                    SwiftTypeName.FromModuleQualifiedName("Swift.Encodable"),
                    $"TestModule{name}EncodableMc"),
                new(swiftTypeName,
                    SwiftTypeName.FromModuleQualifiedName("Swift.Decodable"),
                    $"TestModule{name}DecodableMc"),
            },
            IsFrozen = false,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
            ParentDecl = module,
            ModuleDecl = module,
        };
    }

    // --- SwiftUI.Image Tests ---

    [Fact]
    public void SwiftUIImage_MapParameterType_ReturnsStringWithIsImage()
    {
        var param = new ArgumentDecl
        {
            Name = "icon",
            PrivateName = "icon",
            IsInOut = false,
            IsGeneric = false,
            SwiftTypeSpec = new NamedTypeSpec("SwiftUI.Image"),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var result = SwiftUIBridgeEmitter.MapParameterType(param, null);

        Assert.NotNull(result);
        Assert.Equal(BridgeParameterKind.String, result.Kind);
        Assert.True(result.IsSwiftUIImage);
        Assert.True(result.HasLength);
    }

    [Fact]
    public void SwiftUIImage_SwiftUICore_MapParameterType_ReturnsStringWithIsImage()
    {
        var param = new ArgumentDecl
        {
            Name = "icon",
            PrivateName = "icon",
            IsInOut = false,
            IsGeneric = false,
            SwiftTypeSpec = new NamedTypeSpec("SwiftUICore.Image"),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var result = SwiftUIBridgeEmitter.MapParameterType(param, null);

        Assert.NotNull(result);
        Assert.Equal(BridgeParameterKind.String, result.Kind);
        Assert.True(result.IsSwiftUIImage);
    }

    [Fact]
    public void SwiftUIImage_Wrapper_ConstructsImageFromSystemName()
    {
        var view = CreateViewStructWithNoConstructor("MenuView");
        view.Methods.Add(CreateConstructorWithNamedType("icon", "SwiftUI.Image"));

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule",
            new List<TypeDecl> { view }, NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // The Wrapper should construct Image(systemName:) from the stored string
        Assert.Contains("Image(systemName: state.icon)", swiftContent);
    }

    [Fact]
    public void SwiftUIImage_CSharp_FactoryAcceptsString()
    {
        var view = CreateViewStructWithNoConstructor("MenuView");
        view.Methods.Add(CreateConstructorWithNamedType("icon", "SwiftUI.Image"));

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule",
            new List<TypeDecl> { view }, NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("string? icon", csContent);
    }

    // --- Array<T> Tests ---

    [Fact]
    public void ArrayOfInt_MapParameterType_ReturnsBridgeArray()
    {
        var param = new ArgumentDecl
        {
            Name = "values",
            PrivateName = "values",
            IsInOut = false,
            IsGeneric = false,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Int")),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var result = SwiftUIBridgeEmitter.MapParameterType(param, null);

        Assert.NotNull(result);
        Assert.Equal(BridgeParameterKind.BridgeArray, result.Kind);
        Assert.NotNull(result.InnerParameter);
        Assert.Equal(BridgeParameterKind.Primitive, result.InnerParameter.Kind);
        Assert.True(result.HasLength);
        Assert.Contains("UnsafePointer<Int>?", result.SwiftAbiType);
    }

    [Fact]
    public void ArrayOfBoundEnum_MapParameterType_ReturnsBridgeArray()
    {
        var param = new ArgumentDecl
        {
            Name = "styles",
            PrivateName = "styles",
            IsInOut = false,
            IsGeneric = false,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Array",
                new NamedTypeSpec("TestModule.AlertStyle")),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var context = new BridgeContext(CreateEnumTypeDatabase());
        var result = SwiftUIBridgeEmitter.MapParameterType(param, context);

        Assert.NotNull(result);
        Assert.Equal(BridgeParameterKind.BridgeArray, result.Kind);
        Assert.NotNull(result.InnerParameter);
        Assert.Equal(BridgeParameterKind.BoundEnum, result.InnerParameter.Kind);
    }

    [Fact]
    public void ArrayOfUnsupportedType_MapParameterType_ReturnsNull()
    {
        // Array<String> is not supported (complex element serialization)
        var param = new ArgumentDecl
        {
            Name = "names",
            PrivateName = "names",
            IsInOut = false,
            IsGeneric = false,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.String")),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var result = SwiftUIBridgeEmitter.MapParameterType(param, null);

        Assert.Null(result);
    }

    [Fact]
    public void BridgeArray_IsNotUpdatable()
    {
        var arrayParam = new BridgeParameter(
            "items", BridgeParameterKind.BridgeArray,
            "UnsafePointer<Int>?", "IntPtr",
            HasLength: true,
            InnerParameter: new BridgeParameter("items_elem", BridgeParameterKind.Primitive,
                "Int", "nint"));

        Assert.False(arrayParam.IsUpdatable);
    }

    [Fact]
    public void ArrayOfInt_Swift_EmitsLetPropertyOnWrapper()
    {
        var view = CreateViewStructWithNoConstructor("ListView");
        view.Methods.Add(CreateConstructorWithArray("items", "Swift.Int"));

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule",
            new List<TypeDecl> { view }, NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // Array stored as let property on Wrapper
        Assert.Contains("let items: [Int]", swiftContent);
        // Array reconstructed from buffer pointer in Session init
        Assert.Contains("UnsafeBufferPointer(start: ptr, count: itemsCount)", swiftContent);
    }

    [Fact]
    public void ArrayOfBoundEnum_Swift_EmitsArrayReconstruction()
    {
        var view = CreateViewStructWithNoConstructor("MenuView");
        view.Methods.Add(CreateConstructorWithArray("formats", "TestModule.AlertStyle"));

        var typeDb = CreateEnumTypeDatabase();
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule",
            new List<TypeDecl> { view }, NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("let formats: [AlertStyle]", swiftContent);
        // Each element decodes via a failable init; a single out-of-range raw value fails
        // reconstruction gracefully (return nil) instead of the old `rawValue: $0)!` force-unwrap.
        Assert.Contains("guard let formatsElement = AlertStyle(rawValue: formatsRaw) else { return nil }", swiftContent);
        Assert.DoesNotContain("AlertStyle(rawValue: $0)!", swiftContent);
    }

    [Fact]
    public void ArrayOfInt_CSharp_EmitsArrayFactoryParam()
    {
        var view = CreateViewStructWithNoConstructor("ListView");
        view.Methods.Add(CreateConstructorWithArray("items", "Swift.Int"));

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule",
            new List<TypeDecl> { view }, NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("nint[] items", csContent);
        Assert.Contains("GCHandle.Alloc(items, GCHandleType.Pinned)", csContent);
    }

    [Fact]
    public void ArrayOfBoundEnum_CSharp_EmitsRawValueExtraction()
    {
        var view = CreateViewStructWithNoConstructor("MenuView");
        view.Methods.Add(CreateConstructorWithArray("formats", "TestModule.AlertStyle"));

        var typeDb = CreateEnumTypeDatabase();
        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule",
            new List<TypeDecl> { view }, NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        // Should extract raw values into an array and pin it
        Assert.Contains("formats[i].RawValue", csContent);
        Assert.Contains("GCHandle.Alloc(formatsRaw, GCHandleType.Pinned)", csContent);
    }

    [Theory]
    [InlineData("Swift.Bool")]
    [InlineData("Swift.Double")]
    [InlineData("Swift.Float")]
    [InlineData("Swift.Int32")]
    public void ArrayOfPrimitive_MapParameterType_SupportedVariants(string primitiveType)
    {
        var param = new ArgumentDecl
        {
            Name = "values",
            PrivateName = "values",
            IsInOut = false,
            IsGeneric = false,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec(primitiveType)),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var result = SwiftUIBridgeEmitter.MapParameterType(param, null);

        Assert.NotNull(result);
        Assert.Equal(BridgeParameterKind.BridgeArray, result.Kind);
    }

    // --- Combined: Binding + Image on same view ---

    [Fact]
    public void BindingAndImage_SameView_BothBridged()
    {
        var view = CreateViewStructWithNoConstructor("RichView");
        var ctor = CreateConstructorWithTwoNamedParams("isOn", "SwiftUI.Binding",
            "icon", "SwiftUI.Image");
        view.Methods.Add(ctor);

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule",
            new List<TypeDecl> { view }, NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("$state.isOn", swiftContent);
        Assert.Contains("Image(systemName: state.icon)", swiftContent);
        Assert.DoesNotContain("BRIDGE TEMPLATE", swiftContent);
    }

    // --- Unsupported inner element type → return null so view falls back to template ---

    [Fact]
    public void ArrayOfUnsupportedType_WithTypeDatabase_ReturnsNull()
    {
        // Array<UnsupportedElement> must return null so the entire view falls back to
        // template emission. The previous fall-through behavior produced a broken
        // bare "Array" / "Swift.SwiftArray" in the generated bridge — falling back
        // to the template is the correct behavior when the element type isn't
        // bridgeable.
        var typeDb = new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["Swift.Array"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftArray"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
                MetadataAccessor = "$sSaMa",
                Kind = TypeRecordKind.Struct,
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
            },
        });
        var context = new BridgeContext(TypeDatabase: typeDb);

        var param = new ArgumentDecl
        {
            Name = "actions",
            PrivateName = "actions",
            IsInOut = false,
            IsGeneric = false,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("SomeModule.SomeStruct")),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var result = SwiftUIBridgeEmitter.MapParameterType(param, context);

        Assert.Null(result);
    }

    [Fact]
    public void BindingOfUnsupportedType_WithTypeDatabase_ReturnsNull()
    {
        // Binding<UnsupportedType> returns null instead of falling through to MapDatabaseType,
        // which would strip generic parameters and emit broken bare "Binding" in Swift output.
        var typeDb = new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["SwiftUI.Binding"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("SwiftUI", "Binding"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("SwiftUI.Binding"),
                MetadataAccessor = "$s7SwiftUI7BindingVMa",
                Kind = TypeRecordKind.Struct,
                Flags = TypeRecordFlags.RequiresMemoryManagement,
            },
        });
        var context = new BridgeContext(TypeDatabase: typeDb);

        var param = new ArgumentDecl
        {
            Name = "selection",
            PrivateName = "selection",
            IsInOut = false,
            IsGeneric = false,
            SwiftTypeSpec = new NamedTypeSpec("SwiftUI.Binding", new NamedTypeSpec("CoreGraphics.CGFloat")),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var result = SwiftUIBridgeEmitter.MapParameterType(param, context);

        Assert.Null(result);
    }

    // --- Non-Raw-Value Enum as Init Param Tests ---

    [Fact]
    public void NonRawValueEnum_WithMemoryManagement_MapsToBoundStruct()
    {
        var typeDb = CreateNonRawValueEnumWithMemoryTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithNamedType("format", "TestModule.DataFormat");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.BoundStruct, result[0].Kind);
        Assert.Equal("UnsafeMutableRawPointer", result[0].SwiftAbiType);
        Assert.Equal("IntPtr", result[0].CSharpPInvokeType);
        Assert.Equal("DataFormat", result[0].BridgeTypeName);
        Assert.Equal("TestModule.DataFormat", result[0].CSharpTypeName);
        Assert.Equal(StructProjectionKind.NonFrozen, result[0].StructProjection);
    }

    [Fact]
    public void NonRawValueEnum_WithoutMemoryManagement_StillReturnsNull()
    {
        // Frozen non-raw-value enum without memory management (no SafeHandle) → unsupported
        var typeDb = CreateNonRawEnumTypeDatabase(); // Existing helper: Frozen, no memory management
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithNamedType("direction", "TestModule.Direction");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.Null(result);
    }

    [Fact]
    public void NonRawValueEnum_Wrapper_UsesAssumingMemoryBound()
    {
        var typeDb = CreateNonRawValueEnumWithMemoryTypeDatabase();
        var view = CreateViewStructWithNoConstructor("MenuView");
        view.Methods.Add(CreateConstructorWithNamedType("format", "TestModule.DataFormat"));

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule",
            new List<TypeDecl> { view }, NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("assumingMemoryBound(to: DataFormat.self).pointee", swiftContent);
    }

    [Fact]
    public void NonRawValueEnum_CSharp_UsesPayloadDangerousGetHandle()
    {
        var typeDb = CreateNonRawValueEnumWithMemoryTypeDatabase();
        var view = CreateViewStructWithNoConstructor("MenuView");
        view.Methods.Add(CreateConstructorWithNamedType("format", "TestModule.DataFormat"));

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule",
            new List<TypeDecl> { view }, NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("Payload.DangerousGetHandle()", csContent);
    }

    [Fact]
    public void NonRawValueEnum_ViewGets_FunctionalBridge()
    {
        var typeDb = CreateNonRawValueEnumWithMemoryTypeDatabase();
        var view = CreateViewStructWithNoConstructor("ActionButton");
        view.Methods.Add(CreateConstructorWithNamedType("action", "TestModule.DataFormat"));

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule",
            new List<TypeDecl> { view }, NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // Functional bridge should contain the @_cdecl create function, not a template
        Assert.Contains("@_cdecl", swiftContent);
        Assert.Contains("ActionButton(", swiftContent);
    }

    // --- BoundStruct in Closure Arg Tests ---

    [Fact]
    public void ClosureWithNonRawValueEnumArg_MapParameterType_ReturnsTypedClosure()
    {
        var typeDb = CreateNonRawValueEnumWithMemoryTypeDatabase();
        var context = new BridgeContext(typeDb);
        var param = new ArgumentDecl
        {
            Name = "onFormat",
            PrivateName = "onFormat",
            IsInOut = false,
            IsGeneric = false,
            SwiftTypeSpec = new ClosureTypeSpec(new NamedTypeSpec("TestModule.DataFormat"), null),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var result = SwiftUIBridgeEmitter.MapParameterType(param, context);

        Assert.NotNull(result);
        Assert.Equal(BridgeParameterKind.TypedClosure, result.Kind);
        Assert.NotNull(result.ClosureArguments);
        Assert.Single(result.ClosureArguments);
        Assert.Equal(BridgeParameterKind.BoundStruct, result.ClosureArguments[0].Kind);
    }

    [Fact]
    public void ClosureWithNonRawValueEnumArg_Swift_UsesAllocateAndInitializeMemory()
    {
        var typeDb = CreateNonRawValueEnumWithMemoryTypeDatabase();
        var view = CreateViewWithTypedClosureInit("MenuView", "onFormat",
            new NamedTypeSpec("TestModule.DataFormat"), TupleTypeSpec.Empty);

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule",
            new List<TypeDecl> { view }, NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // Must use allocate + initializeMemory pattern (not Unmanaged for the closure arg)
        Assert.Contains("UnsafeMutableRawPointer.allocate(byteCount: MemoryLayout<DataFormat>.size", swiftContent);
        Assert.Contains("initializeMemory(as: DataFormat.self", swiftContent);
        // The closure callback should NOT use Unmanaged for the BoundStruct arg
        // (Unmanaged.passRetained elsewhere is fine — it's used for session handle management)
        Assert.DoesNotContain("Unmanaged.passRetained(arg0)", swiftContent);
    }

    [Fact]
    public void ClosureWithNonRawValueEnumArg_CSharp_UsesMarshalFromSwift()
    {
        var typeDb = CreateNonRawValueEnumWithMemoryTypeDatabase();
        var view = CreateViewWithTypedClosureInit("MenuView", "onFormat",
            new NamedTypeSpec("TestModule.DataFormat"), TupleTypeSpec.Empty);

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule",
            new List<TypeDecl> { view }, NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("SwiftMarshal.MarshalFromSwift", csContent);
        Assert.Contains("Action<TestModule.DataFormat>", csContent);
    }

    [Fact]
    public void ClosureWithObjCBridgeableStructArg_UsesGetNSObjectAndPassUnretained()
    {
        // Typed closure (URL) -> Void where URL is an ObjC-bridgeable struct (URL → NSUrl).
        // The arg crosses the callback ABI as an ObjC OBJECT pointer, so the Swift side must
        // deliver it via passUnretained(arg as AnyObject) held alive by withExtendedLifetime,
        // and the C# trampoline must decode it via GetNSObject — NOT heap-allocate the raw URL
        // struct bytes and read them via MarshalFromSwift (which reinterprets an object pointer
        // as struct memory → type confusion / SIGSEGV). Contrast the non-ObjC BoundStruct closure
        // arg above, which correctly allocates + MarshalFromSwift.
        var typeDb = new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["TestModule.URL"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrl"),
                NativeTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrl"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.URL"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement | TypeRecordFlags.ObjCBridgeable,
                Kind = TypeRecordKind.Struct,
            },
        });
        var view = CreateViewWithTypedClosureInit("UrlClosureView", "onPick",
            new NamedTypeSpec("TestModule.URL"), TupleTypeSpec.Empty);

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule",
            new List<TypeDecl> { view }, NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        // ObjC-bridgeable struct closure arg decodes via GetNSObject + typed NSUrl delegate.
        Assert.Contains("GetNSObject", csContent);
        Assert.Contains("Action<Foundation.NSUrl>", csContent);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // Object pointer held alive across the synchronous callback: passUnretained(... as AnyObject)
        // inside withExtendedLifetime — never a raw heap-allocated URL struct.
        Assert.Contains("as AnyObject", swiftContent);
        Assert.Contains("withExtendedLifetime", swiftContent);
        Assert.Contains("passUnretained", swiftContent);
        Assert.DoesNotContain("UnsafeMutableRawPointer.allocate(byteCount: MemoryLayout<URL>", swiftContent);
    }

    [Fact]
    public void ClosureWithNonRawValueEnumArg_FullView_GetsFunctionalBridge()
    {
        // Simulate view init with: String + Image + Array<Enum> + Closure<Enum> + Optional<VoidClosure>
        var typeDb = CreateNonRawValueEnumWithMemoryTypeDatabase();
        var view = CreateViewWithTypedClosureInit("DataFormatMenu", "formatAction",
            new NamedTypeSpec("TestModule.DataFormat"), TupleTypeSpec.Empty);

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule",
            new List<TypeDecl> { view }, NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("@_cdecl", swiftContent);
        Assert.Contains("DataFormatMenu(", swiftContent);
    }

    // --- Multi-String Fixed Statement Bug Fix ---

    [Fact]
    public void DualStringInit_CSharp_FixedStatement_DoesNotRepeatBytePointerType()
    {
        // Regression: views with two string init params generated invalid C#:
        //   fixed (byte* titlePtr = titleBytes, byte* subtitlePtr = subtitleBytes)
        // Correct syntax requires the type specifier only once:
        //   fixed (byte* titlePtr = titleBytes, subtitlePtr = subtitleBytes)
        var view = CreateViewWithDualStringInit("ToolbarView", "title", "subtitle");

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule",
            new List<TypeDecl> { view }, NullLogger.Instance);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        // Verify the correct fixed statement pattern (single byte* prefix)
        Assert.Contains("fixed (byte* titlePtr = titleBytes, subtitlePtr = subtitleBytes)", csContent);
        // Ensure the invalid pattern with repeated byte* is NOT present
        Assert.DoesNotContain("byte* titlePtr = titleBytes, byte* subtitlePtr", csContent);
    }

    [Fact]
    public void DualStringInit_Swift_GeneratesBothStringParams()
    {
        var view = CreateViewWithDualStringInit("ToolbarView", "title", "subtitle");

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule",
            new List<TypeDecl> { view }, NullLogger.Instance);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("_ titlePtr: UnsafePointer<UInt8>", swiftContent);
        Assert.Contains("_ subtitlePtr: UnsafePointer<UInt8>", swiftContent);
        Assert.Contains("_ titleLen: Int", swiftContent);
        Assert.Contains("_ subtitleLen: Int", swiftContent);
    }

    // --- Finding 1: BoundStruct closure arg nil-guard ---

    [Fact]
    public void ClosureWithBoundStructArg_Swift_GuardsNilCallbackBeforeAllocation()
    {
        // When a typed closure has a BoundStruct arg, the heap allocation must be guarded
        // by a nil check on the callback pointer. Otherwise, if cb_ is nil, the allocation leaks.
        var typeDb = CreateNonRawValueEnumWithMemoryTypeDatabase();
        var view = CreateViewWithTypedClosureInit("FormatView", "onFormat",
            new NamedTypeSpec("TestModule.DataFormat"), TupleTypeSpec.Empty);

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule",
            new List<TypeDecl> { view }, NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // The guard must appear BEFORE any UnsafeMutableRawPointer.allocate calls
        var guardIndex = swiftContent.IndexOf("guard cb_onFormat != nil");
        var allocateIndex = swiftContent.IndexOf("UnsafeMutableRawPointer.allocate");
        Assert.True(guardIndex >= 0, "Expected guard cb_ != nil check in emitted Swift");
        Assert.True(allocateIndex >= 0, "Expected UnsafeMutableRawPointer.allocate in emitted Swift");
        Assert.True(guardIndex < allocateIndex,
            "guard cb_ != nil must appear BEFORE UnsafeMutableRawPointer.allocate to prevent leaks");
    }

    [Fact]
    public void ClosureWithBoundStructArgAndClassReturn_Swift_GuardEmitsFatalError()
    {
        // When a typed closure has BoundStruct args AND a BoundType (class) return,
        // the nil-guard cannot use "return 0" (invalid Swift for a class return).
        // It must emit fatalError instead.
        var typeDb = new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["TestModule.DataFormat"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "DataFormat"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DataFormat"),
                MetadataAccessor = "$s10TestModule10DataFormatOMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = null,
            },
            ["TestModule.ResultModel"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ResultModel"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ResultModel"),
                MetadataAccessor = "$s10TestModule11ResultModelCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            },
        });
        var view = CreateViewWithTypedClosureInit("TransformView", "onTransform",
            new NamedTypeSpec("TestModule.DataFormat"), new NamedTypeSpec("TestModule.ResultModel"));

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule",
            new List<TypeDecl> { view }, NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // The guard must use fatalError for class returns, not "return 0"
        Assert.Contains("guard cb_onTransform != nil", swiftContent);
        Assert.Contains("fatalError", swiftContent);
        Assert.DoesNotContain("guard cb_onTransform != nil else { return 0", swiftContent);
    }

    // --- Finding 3: Binding<SwiftUI.Image> rejection ---

    [Fact]
    public void BindingSwiftUIImage_MapParameterType_ReturnsNull()
    {
        // Binding<SwiftUI.Image> should be rejected: Image maps as Kind=String with
        // IsSwiftUIImage=true, but Binding projection is incompatible with Image reconstruction.
        var param = new ArgumentDecl
        {
            Name = "icon",
            PrivateName = "icon",
            IsInOut = false,
            IsGeneric = false,
            SwiftTypeSpec = new NamedTypeSpec("SwiftUI.Binding", new NamedTypeSpec("SwiftUI.Image")),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var result = SwiftUIBridgeEmitter.MapParameterType(param, null);

        Assert.Null(result);
    }

    [Fact]
    public void BindingSwiftUICoreImage_MapParameterType_ReturnsNull()
    {
        // Same rejection for SwiftUICore.Binding<SwiftUICore.Image>
        var param = new ArgumentDecl
        {
            Name = "icon",
            PrivateName = "icon",
            IsInOut = false,
            IsGeneric = false,
            SwiftTypeSpec = new NamedTypeSpec("SwiftUICore.Binding", new NamedTypeSpec("SwiftUICore.Image")),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var result = SwiftUIBridgeEmitter.MapParameterType(param, null);

        Assert.Null(result);
    }

    // --- Test Helpers ---

    private static StructDecl CreateViewWithDualStringInit(string viewName, string str1Name, string str2Name)
    {
        var view = CreateViewStructWithNoConstructor(viewName);
        view.Methods.Add(new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
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
                    SwiftTypeSpec = new NamedTypeSpec($"TestModule.{viewName}"),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = str1Name,
                    PrivateName = str1Name,
                    IsInOut = false,
                    IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
                new ArgumentDecl
                {
                    Name = str2Name,
                    PrivateName = str2Name,
                    IsInOut = false,
                    IsGeneric = false,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
            },
        });
        return view;
    }

    private static ITypeDatabase CreateNonRawValueEnumWithMemoryTypeDatabase()
    {
        return new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["TestModule.DataFormat"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "DataFormat"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DataFormat"),
                MetadataAccessor = "$s10TestModule10DataFormatOMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = null, // Associated values, no RawRepresentable
            },
        });
    }

    private static MethodDecl CreateConstructorWithBindingParam(string paramName, string innerType)
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
            IsSynthesizedAccessor = false,
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
                    SwiftTypeSpec = new NamedTypeSpec("SwiftUI.Binding", new NamedTypeSpec(innerType)),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
            },
        };
    }

    private static MethodDecl CreateConstructorWithArray(string paramName, string elementType)
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
            IsSynthesizedAccessor = false,
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
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec(elementType)),
                    ParentDecl = null,
                    ModuleDecl = null,
                },
            },
        };
    }

    private static MethodDecl CreateConstructorWithTwoNamedParams(
        string param1Name, string param1Type, string param2Name, string param2Type)
    {
        var args = new List<ArgumentDecl>
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
        };

        // Binding<Bool> for param1
        if (param1Type == "SwiftUI.Binding")
        {
            args.Add(new ArgumentDecl
            {
                Name = param1Name,
                PrivateName = param1Name,
                IsInOut = false,
                IsGeneric = false,
                SwiftTypeSpec = new NamedTypeSpec("SwiftUI.Binding", new NamedTypeSpec("Swift.Bool")),
                ParentDecl = null,
                ModuleDecl = null,
            });
        }

        // SwiftUI.Image for param2
        if (param2Type == "SwiftUI.Image")
        {
            args.Add(new ArgumentDecl
            {
                Name = param2Name,
                PrivateName = param2Name,
                IsInOut = false,
                IsGeneric = false,
                SwiftTypeSpec = new NamedTypeSpec("SwiftUI.Image"),
                ParentDecl = null,
                ModuleDecl = null,
            });
        }

        return new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule_init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
            ParentDecl = null,
            ModuleDecl = null,
            CSSignature = args,
        };
    }

    #endregion

    #region Optional<ExternalClass> Init Param Gate

    [Fact]
    public void MapOptionalBoundType_ReturnsOptionalWrapped_WhenClassInTypeDatabase()
    {
        var typeDb = CreateClassTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithOptionalType("device", "TestModule.AnimationAsset");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.OptionalWrapped, result[0].Kind);
        Assert.Equal("device", result[0].Name);
        Assert.NotNull(result[0].InnerParameter);
        Assert.Equal(BridgeParameterKind.BoundType, result[0].InnerParameter!.Kind);
        Assert.Equal("AnimationAsset", result[0].InnerParameter!.BridgeTypeName);
    }

    [Fact]
    public void MapOptionalBoundType_SetsObjCBridgeable_WhenRecordHasFlag()
    {
        var typeDb = CreateObjCBridgeableClassTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithOptionalType("device", "TestModule.AVCaptureDevice");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.OptionalWrapped, result[0].Kind);
        Assert.True(result[0].InnerParameter!.IsObjCBridgeable);
    }

    [Fact]
    public void MapOptionalBoundType_EmitsNullablePointerSwiftAbi()
    {
        var typeDb = CreateClassTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithOptionalType("device", "TestModule.AnimationAsset");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.NotNull(result);
        Assert.Equal("UnsafeMutableRawPointer?", result![0].SwiftAbiType);
        Assert.Equal("IntPtr", result[0].CSharpPInvokeType);
    }

    [Fact]
    public void MapBoundType_SetsObjCBridgeable_OnClassParameter()
    {
        var typeDb = CreateObjCBridgeableClassTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithNamedType("device", "TestModule.AVCaptureDevice");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.BoundType, result[0].Kind);
        Assert.True(result[0].IsObjCBridgeable);
    }

    private static ITypeDatabase CreateObjCBridgeableClassTypeDatabase()
    {
        return new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["TestModule.AVCaptureDevice"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("AVFoundation", "AVCaptureDevice"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.AVCaptureDevice"),
                MetadataAccessor = "$s10TestModule15AVCaptureDeviceCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement | TypeRecordFlags.ObjCBridgeable,
                Kind = TypeRecordKind.Class,
            },
        });
    }

    #endregion

    #region Result<T,E> Closure Param Gate

    [Fact]
    public void MapResultClosure_ReturnsResultClosure_WithTwoBoundTypes()
    {
        var typeDb = CreateResultClosureTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithResultClosure("completion",
            "TestModule.ScanResult", "TestModule.ScanError");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(BridgeParameterKind.ResultClosure, result[0].Kind);
        Assert.Equal("completion", result[0].Name);
    }

    [Fact]
    public void MapResultClosure_SetsSuccessAndErrorParams()
    {
        var typeDb = CreateResultClosureTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithResultClosure("completion",
            "TestModule.ScanResult", "TestModule.ScanError");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.NotNull(result);
        var param = result![0];
        Assert.NotNull(param.ResultSuccessParam);
        Assert.NotNull(param.ResultErrorParam);
        Assert.Equal(BridgeParameterKind.BoundType, param.ResultSuccessParam!.Kind);
        Assert.Equal(BridgeParameterKind.BoundType, param.ResultErrorParam!.Kind);
        Assert.Equal("ScanResult", param.ResultSuccessParam.BridgeTypeName);
        Assert.Equal("ScanError", param.ResultErrorParam.BridgeTypeName);
    }

    [Fact]
    public void MapResultClosure_WithPrimitiveSuccessAndClassError()
    {
        var typeDb = CreateResultClosureTypeDatabase();
        var context = new BridgeContext(typeDb);
        // (Result<Int, ScanError>) -> Void
        var ctor = CreateConstructorWithResultClosure("completion",
            "Swift.Int", "TestModule.ScanError");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.NotNull(result);
        var param = result![0];
        Assert.Equal(BridgeParameterKind.ResultClosure, param.Kind);
        Assert.Equal(BridgeParameterKind.Primitive, param.ResultSuccessParam!.Kind);
        Assert.Equal(BridgeParameterKind.BoundType, param.ResultErrorParam!.Kind);
    }

    [Fact]
    public void MapResultClosure_WithStringSuccess()
    {
        var typeDb = CreateResultClosureTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithResultClosure("completion",
            "Swift.String", "TestModule.ScanError");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.NotNull(result);
        var param = result![0];
        Assert.Equal(BridgeParameterKind.ResultClosure, param.Kind);
        Assert.Equal(BridgeParameterKind.String, param.ResultSuccessParam!.Kind);
    }

    [Fact]
    public void MapResultClosure_ReturnsNull_WhenSuccessTypeUnsupported()
    {
        // No type database — Swift.Result<UnsupportedType, ScanError> can't resolve
        var ctor = CreateConstructorWithResultClosure("completion",
            "TestModule.Unknown", "TestModule.ScanError");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor);

        Assert.Null(result);
    }

    [Fact]
    public void MapResultClosure_ReturnsNull_WhenErrorTypeUnsupported()
    {
        var typeDb = new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["TestModule.ScanResult"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ScanResult"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ScanResult"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            },
        });
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithResultClosure("completion",
            "TestModule.ScanResult", "TestModule.Unknown");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.Null(result);
    }

    [Fact]
    public void MapResultClosure_IsNotUpdatable()
    {
        var typeDb = CreateResultClosureTypeDatabase();
        var context = new BridgeContext(typeDb);
        var ctor = CreateConstructorWithResultClosure("completion",
            "TestModule.ScanResult", "TestModule.ScanError");
        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        Assert.NotNull(result);
        Assert.False(result![0].IsUpdatable);
    }

    // Regression guard for the Result-closure fall-through fix.
    //
    // The earlier null-tests above use no / partial type database, so `Swift.Result`
    // itself is unregistered. In that case the pre-fix typed-closure fall-through ALSO
    // returned null (the Result arg didn't resolve), so those tests pass even without the
    // fix — they do not exercise the regression. The bug only manifests when `Swift.Result`
    // IS resolvable: the fall-through then mapped the whole Result arg through the database
    // to the generic `Swift.SwiftResult` with its two generic args stripped, emitting an
    // uncompilable `Action<Swift.SwiftResult>` (CS0305). These two tests register
    // `Swift.Result` so the unfixed code path would produce that shape.
    private static ITypeDatabase CreateResultResolvableButSuccessUnsupportedTypeDatabase()
    {
        return new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            // Swift.Result resolves to the generic Swift.SwiftResult — the trap type.
            ["Swift.Result"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftResult"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Result"),
                MetadataAccessor = "$ss6ResultOMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            },
            ["TestModule.ScanError"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ScanError"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ScanError"),
                MetadataAccessor = "$s10TestModule9ScanErrorCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            },
            // The success type (TestModule.Unsupported) is deliberately ABSENT, so
            // MapResultClosureType cannot resolve it and returns null.
        });
    }

    [Fact]
    public void MapResultClosure_DoesNotFallThroughToTypedClosure_WhenSuccessUnsupportedButResultResolvable()
    {
        var context = new BridgeContext(CreateResultResolvableButSuccessUnsupportedTypeDatabase());
        // (Result<TestModule.Unsupported, ScanError>) -> Void — success type not bridge-supported.
        var ctor = CreateConstructorWithResultClosure("completion",
            "TestModule.Unsupported", "TestModule.ScanError");

        var result = SwiftUIBridgeEmitter.AnalyzeInitParameters(ctor, context);

        // The whole init must be unbridgeable (null). The pre-fix code fell through to
        // typed-closure handling, resolved the Result arg to BoundType SwiftResult, and
        // returned a (non-null) TypedClosure param — which then emitted Action<SwiftResult>.
        Assert.Null(result);
    }

    [Fact]
    public void EmitResultClosure_NoStrippedGenericSwiftResult_WhenSuccessUnsupported()
    {
        // Keep-alive view guarantees the bridge files are written even though the
        // Result-closure view itself degrades (so File.ReadAllText below always succeeds).
        var unsupportedView = CreateViewStructWithNoConstructor("ScannerView");
        unsupportedView.Methods.Add(CreateConstructorWithResultClosure("completion",
            "TestModule.Unsupported", "TestModule.ScanError"));
        var views = new List<TypeDecl>
        {
            CreateSimpleViewStruct("KeepAliveView"),
            unsupportedView,
        };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, CreateResultResolvableButSuccessUnsupportedTypeDatabase());

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));

        // The CS0305 regression shape: SwiftResult<,> emitted with its generics stripped.
        Assert.DoesNotContain("SwiftResult", csContent);
        Assert.DoesNotContain("SwiftResult", swiftContent);
        // And no functional Result decomposition was emitted for the unsupported view.
        Assert.DoesNotContain("CompletionSuccessTrampoline", csContent);
        Assert.DoesNotContain("CompletionErrorTrampoline", csContent);
    }

    [Fact]
    public void EmitResultClosure_SwiftContainsSwitchStatement()
    {
        var typeDb = CreateResultClosureTypeDatabase();
        var context = new BridgeContext(typeDb);
        var view = CreateViewStructWithNoConstructor("ScannerView");
        view.Methods.Add(CreateConstructorWithResultClosure("completion",
            "TestModule.ScanResult", "TestModule.ScanError"));
        var views = new List<TypeDecl> { view };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("case .success(let value):", swiftContent);
        Assert.Contains("case .failure(let error):", swiftContent);
        Assert.Contains("cb_completionSuccess?", swiftContent);
        Assert.Contains("cb_completionError?", swiftContent);
    }

    [Fact]
    public void EmitResultClosure_SwiftHasFourCallbackParams()
    {
        var typeDb = CreateResultClosureTypeDatabase();
        var context = new BridgeContext(typeDb);
        var view = CreateViewStructWithNoConstructor("ScannerView");
        view.Methods.Add(CreateConstructorWithResultClosure("completion",
            "TestModule.ScanResult", "TestModule.ScanError"));
        var views = new List<TypeDecl> { view };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("completionSuccessCallback", swiftContent);
        Assert.Contains("completionSuccessUserData", swiftContent);
        Assert.Contains("completionErrorCallback", swiftContent);
        Assert.Contains("completionErrorUserData", swiftContent);
    }

    [Fact]
    public void EmitResultClosure_CSharpHasTwoActionParams()
    {
        var typeDb = CreateResultClosureTypeDatabase();
        var view = CreateViewStructWithNoConstructor("ScannerView");
        view.Methods.Add(CreateConstructorWithResultClosure("completion",
            "TestModule.ScanResult", "TestModule.ScanError"));
        var views = new List<TypeDecl> { view };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("Action<TestModule.ScanResult>? completionSuccess = null", csContent);
        Assert.Contains("Action<TestModule.ScanError>? completionError = null", csContent);
    }

    [Fact]
    public void EmitResultClosure_CSharpHasTwoTrampolines()
    {
        var typeDb = CreateResultClosureTypeDatabase();
        var view = CreateViewStructWithNoConstructor("ScannerView");
        view.Methods.Add(CreateConstructorWithResultClosure("completion",
            "TestModule.ScanResult", "TestModule.ScanError"));
        var views = new List<TypeDecl> { view };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        Assert.Contains("CompletionSuccessTrampoline", csContent);
        Assert.Contains("CompletionErrorTrampoline", csContent);
        Assert.Contains("[global::System.Runtime.InteropServices.UnmanagedCallersOnly", csContent);
    }

    [Fact]
    public void EmitResultClosure_WrapperHasResultClosureProperty()
    {
        var typeDb = CreateResultClosureTypeDatabase();
        var view = CreateViewStructWithNoConstructor("ScannerView");
        view.Methods.Add(CreateConstructorWithResultClosure("completion",
            "TestModule.ScanResult", "TestModule.ScanError"));
        var views = new List<TypeDecl> { view };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        Assert.Contains("let completion: (Result<ScanResult, ScanError>) -> Void", swiftContent);
    }

    [Fact]
    public void EmitResultClosure_ObjCBranch_UsesGetNSObject()
    {
        // Result<ObjCClass, SwiftClass> — ObjC success branch should use GetNSObject, not MarshalFromSwift
        var typeDb = new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["TestModule.ObjCResult"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("AVFoundation", "AVCaptureDevice"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ObjCResult"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement | TypeRecordFlags.ObjCBridgeable,
                Kind = TypeRecordKind.Class,
            },
            ["TestModule.ScanError"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ScanError"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ScanError"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            },
        });
        var view = CreateViewStructWithNoConstructor("ObjCResultView");
        view.Methods.Add(CreateConstructorWithResultClosure("completion",
            "TestModule.ObjCResult", "TestModule.ScanError"));
        var views = new List<TypeDecl> { view };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        // ObjC branch uses GetNSObject, not MarshalFromSwift
        Assert.Contains("GetNSObject", csContent);
        // Non-ObjC error branch still uses MarshalFromSwift
        Assert.Contains("MarshalFromSwift", csContent);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // ObjC branch uses passUnretained (no ownership transfer)
        Assert.Contains("passUnretained", swiftContent);
        // Non-ObjC error branch uses passRetained (ownership transfer to C#)
        Assert.Contains("passRetained", swiftContent);
    }

    [Fact]
    public void EmitResultClosure_ObjCBridgeableStruct_UsesPassUnretainedAndGetNSObject()
    {
        // Result<ObjCBridgeableStruct, SwiftClass> — ObjC struct success branch (e.g., URL → NSUrl)
        // must use passUnretained on Swift side and GetNSObject on C# side, NOT heap-allocate.
        var typeDb = new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["TestModule.URL"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrl"),
                NativeTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrl"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.URL"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement | TypeRecordFlags.ObjCBridgeable,
                Kind = TypeRecordKind.Struct,
            },
            ["TestModule.ScanError"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ScanError"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ScanError"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            },
        });
        var view = CreateViewStructWithNoConstructor("UrlResultView");
        view.Methods.Add(CreateConstructorWithResultClosure("completion",
            "TestModule.URL", "TestModule.ScanError"));
        var views = new List<TypeDecl> { view };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var csContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.cs"));
        // ObjC-bridgeable struct branch uses GetNSObject, not MarshalFromSwift
        Assert.Contains("GetNSObject", csContent);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // ObjC-bridgeable struct uses passUnretained (as AnyObject), not heap-allocate
        Assert.Contains("passUnretained", swiftContent);
        Assert.Contains("as AnyObject", swiftContent);
        // Should NOT contain allocate for the success branch (only non-ObjC BoundStruct would)
        Assert.DoesNotContain("UnsafeMutableRawPointer.allocate", swiftContent);
    }

    [Fact]
    public void EmitResultClosure_BoundStructBranch_HasNilGuard()
    {
        // Result with BoundStruct error — should guard against nil callback before allocation
        var typeDb = new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["TestModule.ScanResult"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ScanResult"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ScanResult"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            },
            ["TestModule.ScanError"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ScanError"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ScanError"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = null, // Non-raw-value enum → BoundStruct
            },
        });
        var view = CreateViewStructWithNoConstructor("StructResultView");
        view.Methods.Add(CreateConstructorWithResultClosure("completion",
            "TestModule.ScanResult", "TestModule.ScanError"));
        var views = new List<TypeDecl> { view };

        SwiftUIBridgeEmitter.EmitBridgeFiles(_tempDir, "TestModule", "TestModule", views,
            NullLogger.Instance, typeDb);

        var swiftContent = File.ReadAllText(Path.Combine(_tempDir, "TestModule.SwiftUIBridge.swift"));
        // BoundStruct branch must have nil guard before allocation
        Assert.Contains("guard cb_completionError != nil else { return }", swiftContent);
    }

    private static ITypeDatabase CreateResultClosureTypeDatabase()
    {
        return new BridgeTestTypeDatabase(new Dictionary<string, TypeRecord>
        {
            ["TestModule.ScanResult"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ScanResult"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ScanResult"),
                MetadataAccessor = "$s10TestModule10ScanResultCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            },
            ["TestModule.ScanError"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ScanError"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ScanError"),
                MetadataAccessor = "$s10TestModule9ScanErrorCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            },
        });
    }

    private static MethodDecl CreateConstructorWithResultClosure(string paramName,
        string successType, string errorType)
    {
        // Build (Result<Success, Failure>) -> Void closure spec
        var resultTypeSpec = new NamedTypeSpec("Swift.Result");
        resultTypeSpec.GenericParameters.Add(new NamedTypeSpec(successType));
        resultTypeSpec.GenericParameters.Add(new NamedTypeSpec(errorType));

        var closureSpec = new ClosureTypeSpec(
            resultTypeSpec,  // argument: Result<T, E>
            TupleTypeSpec.Empty);  // return: Void

        return CreateConstructorWithClosureSpec(paramName, closureSpec);
    }

    #endregion
}
