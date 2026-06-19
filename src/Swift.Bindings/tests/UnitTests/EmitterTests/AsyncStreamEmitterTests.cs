// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for AsyncStreamEmitter — covers @MainActor, custom `actor`, and plain non-actor
/// parents. Custom actor AsyncStream properties are emitted as `Task { for await e in await
/// __self.prop { ... } }`; the `await` on property access hops to the actor's serial
/// executor. Parameterized-protocol element types stay rejected because the @_cdecl
/// wrapper can't spell iOS 16+ parameterized-protocol types.
/// </summary>
public class AsyncStreamEmitterTests
{
    [Fact]
    public void EmitSwiftWrapper_NonIsolatedParent_EmitsPlainTask()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Sensor", moduleDecl);

        var property = CreateAsyncStreamProperty("readings", classDecl, moduleDecl);
        var asyncStreamHandler = new AsyncStreamHandler(new MockTypeDatabase());

        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        AsyncStreamEmitter.EmitSwiftWrapper(swiftWriter, property, asyncStreamHandler,
            "Sensor_readings_AsyncStream", "TestModule.Sensor", isThrowing: false);

        var swift = swiftOutput.ToString();
        Assert.DoesNotContain("@MainActor", swift);
        Assert.Contains("Task {", swift);
        Assert.DoesNotContain("@MainActor in", swift);
    }

    [Fact]
    public void MemberEmissionValidator_AllowsMainActorIsolatedAsyncStream()
    {
        // @MainActor AsyncStream properties are now allowed — under -strict-concurrency=minimal,
        // nonisolated wrappers can access @MainActor members
        var typeDatabase = new MockTypeDatabase();

        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Camera", moduleDecl);
        classDecl.IsMainActorIsolated = true;

        var property = CreateAsyncStreamProperty("sampleBuffer", classDecl, moduleDecl);

        var skipReason = MemberEmissionValidator.CanEmitProperty(
            property, typeDatabase, out var skipDetails, out var projectedTypeName);

        Assert.Null(skipReason);
        Assert.Equal("nint", projectedTypeName);
    }

    [Fact]
    public void MemberEmissionValidator_AllowsPropertyLevelActorIsolatedAsyncStream()
    {
        // Per-member @MainActor on non-actor class is now allowed
        var typeDatabase = new MockTypeDatabase();

        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("DataStream", moduleDecl);

        var property = CreateAsyncStreamProperty("events", classDecl, moduleDecl);
        property.IsActorIsolated = true;

        var skipReason = MemberEmissionValidator.CanEmitProperty(
            property, typeDatabase, out var skipDetails, out var projectedTypeName);

        Assert.Null(skipReason);
        Assert.Equal("nint", projectedTypeName);
    }

    [Fact]
    public void MemberEmissionValidator_AllowsCustomActorAsyncStream()
    {
        // Custom actor AsyncStream properties are now emitted; the Swift wrapper awaits the
        // property across the actor's serial executor.
        var typeDatabase = new MockTypeDatabase();

        var moduleDecl = CreateModuleDecl("TestModule");
        var actorDecl = CreateClassDecl("DataProcessor", moduleDecl);
        actorDecl.IsActor = true;

        var property = CreateAsyncStreamProperty("results", actorDecl, moduleDecl);

        var skipReason = MemberEmissionValidator.CanEmitProperty(
            property, typeDatabase, out var skipDetails, out var projectedTypeName);

        Assert.Null(skipReason);
        Assert.Equal("nint", projectedTypeName);
    }

    [Fact]
    public void MemberEmissionValidator_AllowsNonisolatedCustomActorAsyncStream()
    {
        // `nonisolated` actor property access is synchronous from any isolation; still allowed.
        var typeDatabase = new MockTypeDatabase();

        var moduleDecl = CreateModuleDecl("TestModule");
        var actorDecl = CreateClassDecl("DataProcessor", moduleDecl);
        actorDecl.IsActor = true;

        var property = CreateAsyncStreamProperty("results", actorDecl, moduleDecl);
        property.IsNonisolated = true;

        var skipReason = MemberEmissionValidator.CanEmitProperty(
            property, typeDatabase, out var skipDetails, out var projectedTypeName);

        Assert.Null(skipReason);
        Assert.Equal("nint", projectedTypeName);
    }

    [Fact]
    public void EmitSwiftWrapper_CustomActorIsolated_EmitsAwaitOnPropertyAccess()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var actorDecl = CreateClassDecl("DataProcessor", moduleDecl);
        actorDecl.IsActor = true;

        var property = CreateAsyncStreamProperty("results", actorDecl, moduleDecl);
        var asyncStreamHandler = new AsyncStreamHandler(new MockTypeDatabase());

        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        AsyncStreamEmitter.EmitSwiftWrapper(swiftWriter, property, asyncStreamHandler,
            "DataProcessor_results_AsyncStream", "TestModule.DataProcessor", isThrowing: false);

        var swift = swiftOutput.ToString();
        Assert.Contains("Task {", swift);
        Assert.DoesNotContain("@MainActor", swift);
        // Actor-isolated property: hop through `await __self.results` to the actor's executor.
        Assert.Contains("await __self.results", swift);
    }

    [Fact]
    public void EmitSwiftWrapper_CustomActorNonisolated_OmitsAwaitOnPropertyAccess()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var actorDecl = CreateClassDecl("DataProcessor", moduleDecl);
        actorDecl.IsActor = true;

        var property = CreateAsyncStreamProperty("results", actorDecl, moduleDecl);
        property.IsNonisolated = true;
        var asyncStreamHandler = new AsyncStreamHandler(new MockTypeDatabase());

        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        AsyncStreamEmitter.EmitSwiftWrapper(swiftWriter, property, asyncStreamHandler,
            "DataProcessor_results_AsyncStream", "TestModule.DataProcessor", isThrowing: false);

        var swift = swiftOutput.ToString();
        Assert.DoesNotContain("@MainActor", swift);
        // Nonisolated property access is synchronous — no await prefix on the property access.
        Assert.DoesNotContain("await __self.results", swift);
        Assert.Contains("__self.results", swift);
    }

    [Fact]
    public void MemberEmissionValidator_AllowsNonIsolatedAsyncStream()
    {
        var typeDatabase = new MockTypeDatabase();

        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Sensor", moduleDecl);

        var property = CreateAsyncStreamProperty("readings", classDecl, moduleDecl);

        var skipReason = MemberEmissionValidator.CanEmitProperty(
            property, typeDatabase, out var skipDetails, out var projectedTypeName);

        Assert.Null(skipReason);
        Assert.Equal("nint", projectedTypeName);
    }

    [Fact]
    public void MemberEmissionValidator_AllowsStaticMainActorAsyncStream()
    {
        var typeDatabase = new MockTypeDatabase();

        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Camera", moduleDecl);
        classDecl.IsMainActorIsolated = true;

        var property = CreateAsyncStreamProperty("globalUpdates", classDecl, moduleDecl);
        property.IsStatic = true; // Static doesn't capture self

        var skipReason = MemberEmissionValidator.CanEmitProperty(
            property, typeDatabase, out var skipDetails, out var projectedTypeName);

        Assert.Null(skipReason);
        Assert.Equal("nint", projectedTypeName);
    }

    [Fact]
    public void EmitCompletionCallback_InvokesStreamComplete()
    {
        // Regression guard: the completion callback must resolve the stream from context and call
        // stream.Complete(). Complete() closes the channel writer (a no-op completion would leave it
        // open forever and hang any C# consumer iterating via `await foreach`) and frees the context
        // handle, since completion is the last Swift→C# callback for this context.
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Sensor", moduleDecl);
        var property = CreateAsyncStreamProperty("readings", classDecl, moduleDecl);
        var asyncStreamHandler = new AsyncStreamHandler(new MockTypeDatabase());

        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);

        AsyncStreamEmitter.EmitCompletionCallback(csWriter, property, asyncStreamHandler, "Sensor_readings");

        var cs = csOutput.ToString();
        Assert.Contains("stream.Complete();", cs);
        // The UCO body is guarded: a managed exception faults the channel instead of unwinding
        // across the Swift boundary.
        Assert.Contains("catch (global::System.Exception __uco_ex)", cs);
        Assert.Contains("stream.FaultChannel(__uco_ex);", cs);
        // Null-context guard so a stale/freed cookie does not NRE inside the trampoline.
        Assert.Contains("if (stream == null) return;", cs);
    }

    [Fact]
    public void EmitElementCallback_DeliversElementAndGuardsWithStreamFault()
    {
        // The element callback resolves the stream, delivers the borrowed element pointer, and is
        // wrapped in the StreamFault envelope so a marshalling failure faults the channel (the
        // consumer observes the exception) rather than unwinding across the Swift boundary or
        // silently truncating the stream. It returns a byte (1 continue / 0 stop).
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Sensor", moduleDecl);
        var property = CreateAsyncStreamProperty("readings", classDecl, moduleDecl);
        var asyncStreamHandler = new AsyncStreamHandler(new MockTypeDatabase());

        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);

        AsyncStreamEmitter.EmitElementCallback(csWriter, property, asyncStreamHandler, "Sensor_readings");

        var cs = csOutput.ToString();
        Assert.Contains("stream.DeliverElement(new IntPtr(elementPtr))", cs);
        Assert.Contains("catch (global::System.Exception __uco_ex)", cs);
        Assert.Contains("stream.FaultChannel(__uco_ex);", cs);
        // On a marshal fault the trampoline returns 0 (stop) after faulting the channel.
        Assert.Contains("return 0;", cs);
        Assert.Contains("if (stream == null) return 0;", cs);
    }

    [Fact]
    public void MemberEmissionValidator_AllowsNonisolatedAsyncStreamOnMainActorType()
    {
        var typeDatabase = new MockTypeDatabase();

        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Camera", moduleDecl);
        classDecl.IsMainActorIsolated = true;

        var property = CreateAsyncStreamProperty("statusUpdates", classDecl, moduleDecl);
        property.IsNonisolated = true; // Explicitly opts out of parent's isolation

        var skipReason = MemberEmissionValidator.CanEmitProperty(
            property, typeDatabase, out var skipDetails, out var projectedTypeName);

        Assert.Null(skipReason);
        Assert.Equal("nint", projectedTypeName);
    }

    [Fact]
    public void MemberEmissionValidator_AllowsAsyncThrowingStream()
    {
        // Inverse of the Session-2 rejection: AsyncThrowingStream is now bound. It projects to
        // IAsyncEnumerable<T> like AsyncStream, and its finish(throwing:) termination is marshalled
        // through a producer-error callback that faults the channel so the consumer's await foreach
        // rethrows. The validator no longer fails it closed.
        var typeDatabase = new MockTypeDatabase();

        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Feed", moduleDecl);

        var property = CreateAsyncThrowingStreamProperty("events", classDecl, moduleDecl);

        var skipReason = MemberEmissionValidator.CanEmitProperty(
            property, typeDatabase, out var skipDetails, out var projectedTypeName);

        Assert.Null(skipReason);
        Assert.Equal("nint", projectedTypeName);
    }

    [Fact]
    public void EmitErrorCallback_FaultsChannelWithProducerError()
    {
        // The producer-error callback (throwing streams only) marshals the Swift error message and
        // routes it to FaultChannel so the consumer's await foreach rethrows. It is null-guarded and
        // wrapped in the StreamFault envelope (constructing the bridge exception must not unwind
        // across the @convention(c) boundary, since the Swift wrapper invokes completionCallback —
        // which owns the GCHandle free — AFTER this returns).
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Feed", moduleDecl);
        var property = CreateAsyncThrowingStreamProperty("events", classDecl, moduleDecl);
        var asyncStreamHandler = new AsyncStreamHandler(new MockTypeDatabase());

        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);

        AsyncStreamEmitter.EmitErrorCallback(csWriter, property, asyncStreamHandler, "Feed_events");

        var cs = csOutput.ToString();
        Assert.Contains("_OnError(long context, byte* messagePtr)", cs);
        Assert.Contains("PtrToStringUTF8", cs);
        Assert.Contains("stream.FaultChannel(new global::Swift.Runtime.SwiftRuntimeException(__msg));", cs);
        Assert.Contains("if (stream == null) return;", cs);
        // StreamFault envelope around the body.
        Assert.Contains("catch (global::System.Exception __uco_ex)", cs);
        Assert.Contains("stream.FaultChannel(__uco_ex);", cs);
    }

    [Fact]
    public void EmitSwiftWrapper_ThrowingStream_EmitsErrorCallbackAndDoCatch()
    {
        // A throwing stream's Swift wrapper takes the extra producer-error callback, iterates with
        // `for try await`, swallows a consumer-driven CancellationError (cancel is not a producer
        // fault), and on a genuine producer throw marshals the error string back through the error
        // callback. completionCallback still fires on every path so the C# channel always completes.
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Feed", moduleDecl);
        var property = CreateAsyncThrowingStreamProperty("events", classDecl, moduleDecl);
        var asyncStreamHandler = new AsyncStreamHandler(new MockTypeDatabase());

        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        AsyncStreamEmitter.EmitSwiftWrapper(swiftWriter, property, asyncStreamHandler,
            "Feed_events_AsyncStream", "TestModule.Feed", isThrowing: true);

        var swift = swiftOutput.ToString();
        Assert.Contains("errorCallback: @convention(c) (Int64, UnsafePointer<CChar>) -> Void", swift);
        Assert.Contains("for try await element in", swift);
        Assert.Contains("catch is CancellationError {", swift);
        Assert.Contains("errorCallback(context, $0)", swift);
        Assert.Contains("completionCallback(context)", swift);
    }

    [Fact]
    public void EmitSwiftWrapper_NonThrowingStream_OmitsErrorCallbackAndDoCatch()
    {
        // A non-throwing stream takes no producer-error callback and iterates with a plain
        // `for await` (no do/catch, no `try`).
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Sensor", moduleDecl);
        var property = CreateAsyncStreamProperty("readings", classDecl, moduleDecl);
        var asyncStreamHandler = new AsyncStreamHandler(new MockTypeDatabase());

        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        AsyncStreamEmitter.EmitSwiftWrapper(swiftWriter, property, asyncStreamHandler,
            "Sensor_readings_AsyncStream", "TestModule.Sensor", isThrowing: false);

        var swift = swiftOutput.ToString();
        Assert.DoesNotContain("errorCallback", swift);
        Assert.DoesNotContain("catch is CancellationError", swift);
        Assert.Contains("for await element in", swift);
    }

    [Fact]
    public void EmitSwiftWrapper_AllStreams_RegisterProducerCancelTask()
    {
        // Every stream (throwing or not) registers its producer Task with the cancellation registry
        // so a C# Cancel()/Dispose() can task-cancel a suspended `for await` producer, not merely
        // complete the channel. Mirrors the live method emitters' registration block.
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Sensor", moduleDecl);
        var property = CreateAsyncStreamProperty("readings", classDecl, moduleDecl);
        var asyncStreamHandler = new AsyncStreamHandler(new MockTypeDatabase());

        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        AsyncStreamEmitter.EmitSwiftWrapper(swiftWriter, property, asyncStreamHandler,
            "Sensor_readings_AsyncStream", "TestModule.Sensor", isThrowing: false);

        var swift = swiftOutput.ToString();
        Assert.Contains("_ cancelKey: Int64", swift);
        Assert.Contains("_sbwRegisterTask(cancelKey, _sbwEntry)", swift);
        Assert.Contains("defer { _sbwUnregisterTask(cancelKey) }", swift);
        Assert.Contains("if _sbwAssignTask(_sbwEntry, _sbwTask) { _sbwTask.cancel() }", swift);
    }

    [Fact]
    public void EmitPInvokeDeclaration_ThrowingStream_EmitsErrorCallbackAndCancelKey()
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);

        AsyncStreamEmitter.EmitPInvokeDeclaration(csWriter, "Feed_events_AsyncStream", "libFeed",
            isStatic: false, isThrowing: true);

        var cs = csOutput.ToString();
        Assert.Contains("delegate* unmanaged[Cdecl]<long, byte*, void> errorCallback", cs);
        Assert.Contains("long cancelKey", cs);
    }

    [Fact]
    public void EmitPInvokeDeclaration_NonThrowingStream_OmitsErrorCallbackButKeepsCancelKey()
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);

        AsyncStreamEmitter.EmitPInvokeDeclaration(csWriter, "Sensor_readings_AsyncStream", "libSensor",
            isStatic: false, isThrowing: false);

        var cs = csOutput.ToString();
        Assert.DoesNotContain("errorCallback", cs);
        // Producer-cancel is wired for ALL streams, not just throwing ones.
        Assert.Contains("long cancelKey", cs);
    }

    [Fact]
    public void EmitPropertyGetter_WiresProducerCancellationAndPassesCancelKey()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Sensor", moduleDecl);
        var property = CreateAsyncStreamProperty("readings", classDecl, moduleDecl);
        var asyncStreamHandler = new AsyncStreamHandler(new MockTypeDatabase());

        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);

        AsyncStreamEmitter.EmitPropertyGetter(csWriter, property, asyncStreamHandler,
            "Sensor_readings_AsyncStream", "Sensor_readings", isThrowing: false, "Sensor");

        var cs = csOutput.ToString();
        Assert.Contains("NextCancelKey()", cs);
        Assert.Contains("SetProducerCancellation(", cs);
        Assert.Contains("SBW_CancelTask(", cs);
    }

    [Fact]
    public void EmitPropertyGetter_ThrowingStream_PassesErrorCallback()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Feed", moduleDecl);
        var property = CreateAsyncThrowingStreamProperty("events", classDecl, moduleDecl);
        var asyncStreamHandler = new AsyncStreamHandler(new MockTypeDatabase());

        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);

        AsyncStreamEmitter.EmitPropertyGetter(csWriter, property, asyncStreamHandler,
            "Feed_events_AsyncStream", "Feed_events", isThrowing: true, "Feed");

        var cs = csOutput.ToString();
        Assert.Contains("_OnError", cs);
    }

    #region Helpers

    private static ModuleDecl CreateModuleDecl(string name)
    {
        return new ModuleDecl
        {
            Name = name,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
    {
        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
    }

    private static PropertyDecl CreateAsyncStreamProperty(string name, TypeDecl parentDecl, ModuleDecl moduleDecl)
    {
        // AsyncStream<Element> type spec — use _Concurrency.AsyncStream as the handler expects
        var asyncStreamType = new NamedTypeSpec("_Concurrency.AsyncStream",
            new TypeSpec[] { new NamedTypeSpec("Swift.Int") });

        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = asyncStreamType,
            IsStatic = false,
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Accessors = new List<AccessorDecl>(),
            HasStorage = false,
        };
    }

    private static PropertyDecl CreateAsyncThrowingStreamProperty(string name, TypeDecl parentDecl, ModuleDecl moduleDecl)
    {
        // AsyncThrowingStream<Element, any Error> — the handler keys off the type name, so the
        // element type is enough to exercise the throwing-stream rejection path.
        var asyncThrowingStreamType = new NamedTypeSpec("_Concurrency.AsyncThrowingStream",
            new TypeSpec[] { new NamedTypeSpec("Swift.Int") });

        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = asyncThrowingStreamType,
            IsStatic = false,
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Accessors = new List<AccessorDecl>(),
            HasStorage = false,
        };
    }

    /// <summary>
    /// Mock type database with Swift.Int registered so AsyncStream element type resolves.
    /// </summary>
    private class MockTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types;

        public string AsyncLibraryName => null!;

        public MockTypeDatabase()
        {
            _types = new Dictionary<string, TypeRecord>
            {
                ["Swift.Int"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.NIntType,
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                }
            };
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, out TypeRecord record)
        {
            return _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record);
        }

        public string GetLibraryPath(string moduleName) => "";

        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion
}
