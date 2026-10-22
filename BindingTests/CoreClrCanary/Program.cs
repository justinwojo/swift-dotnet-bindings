// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Swift;
using Swift.Runtime;
using Swift.Runtime.InteropServices;

internal static unsafe class Program
{
    private static readonly List<string> Passed = new();

    public static int Main()
    {
        try
        {
            Check(Environment.Version.Major == 11, "actual .NET 11 runtime");
            Check(Type.GetType("Mono.Runtime") is null, "no Mono runtime type");
            var images = Enumerable.Range(0, checked((int)Native.ImageCount()))
                .Select(index => Marshal.PtrToStringUTF8(Native.ImageName((uint)index)) ?? "").ToArray();
            var coreClrImage = images.SingleOrDefault(path => Path.GetFileName(path) == "libcoreclr.dylib");
            Check(coreClrImage is not null, "loaded libcoreclr.dylib (dyld image evidence)");
            foreach (var engine in new[] { "Mono", "CoreCLR", "NativeAOT" })
                Check(AppContext.TryGetSwitch("Swift.Runtime.Engine." + engine, out var active) && active == (engine == "CoreCLR"),
                    "package engine switch " + engine);
            Check(!SwiftRuntimeInfo.IsMonoRuntime && !SwiftRuntimeInfo.IsNativeAotRuntime, "runtime classifies actual CoreCLR");
            Check(RuntimeFeature.IsDynamicCodeSupported == (Environment.GetEnvironmentVariable("CANARY_DYNAMIC_CODE") != "false"),
                "dynamic code capability matches requested process setting");
            Check(Native.Add(19, 23) == 42, "native Swift call");
            var callbackState = GCHandle.Alloc(new int[] { 0 });
            try
            {
                Check(Native.Callback(5, GCHandle.ToIntPtr(callbackState), &Callback) == 20, "Swift invokes managed callback and returns result");
                Check(((int[])callbackState.Target!)[0] == 6, "callback context and argument");
            }
            finally { callbackState.Free(); }

            // Do the conformance lookup before any dictionary creates a warm cache.
            Check(ProtocolConformanceDescriptor.TryGet<SwiftString, ISwiftHashable>(out var conformance) && conformance.Value.IsValid,
                "cold String Hashable conformance");
            ColdArray();
            ColdOptional();
            ColdTuple();
            Dictionary();
            // Write JSON without reflection serialization, which is unavailable in a trimmed app.
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("status", "PASS");
                writer.WriteString("framework", AppContext.TargetFrameworkName);
                writer.WriteString("runtime", RuntimeInformation.FrameworkDescription);
                writer.WriteString("coreClrImage", coreClrImage);
                writer.WriteString("runtimeAssembly", typeof(SwiftString).Assembly.FullName);
                writer.WriteString("runtimeAssemblyPath", typeof(SwiftString).Assembly.Location);
                writer.WriteBoolean("dynamicCode", RuntimeFeature.IsDynamicCodeSupported);
                writer.WriteStartArray("assertions");
                foreach (var assertion in Passed) writer.WriteStringValue(assertion);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Console.Error.WriteLine("Passed before failure: " + string.Join("; ", Passed));
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Callback(int value, IntPtr context)
    {
        // Never throw across the native callback boundary; Main verifies the state.
        ((int[])GCHandle.FromIntPtr(context).Target!)[0] = value;
        return value * 3;
    }

    private static void ColdArray()
    {
        var metadata = TypeMetadata.GetTypeMetadataOrThrow<SwiftArray<SwiftString>>();
        Check(metadata.Size == (nuint)Native.ArraySize(), "cold Array<String> metadata matches Swift layout");
        var buffer = NativeMemory.Alloc(metadata.Size);
        Native.ArrayInit((IntPtr)buffer);
        try
        {
            // No factory registration or managed constructor precedes this cold dispatch.
            using var array = SwiftMarshal.MarshalFromSwiftObject<SwiftArray<SwiftString>>((IntPtr)buffer);
            Check(array.Count == 2, "cold Array<String> materializes");
            using var first = array[0];
            Check(first.ToString() == "cold — 🌍", "native Array<String> element Unicode");
            using var added = new SwiftString("managed");
            array.Append(added);
            Check(Native.ArrayCheck(array.Payload.DangerousGetHandle()) == 1, "Swift observes managed Array<String> mutation");
            GC.KeepAlive(array);
        }
        finally { Native.ArrayDestroy((IntPtr)buffer); NativeMemory.Free(buffer); }
    }

    private static void ColdOptional()
    {
        var metadata = TypeMetadata.GetTypeMetadataOrThrow<SwiftOptional<SwiftArray<int>>>();
        Check(metadata.Size == (nuint)Native.OptionalSize(), "cold Optional<Array<Int32>> metadata matches Swift layout");
        var buffer = NativeMemory.Alloc(metadata.Size);
        Native.OptionalInit((IntPtr)buffer);
        try
        {
            using var optional = SwiftMarshal.MarshalFromSwiftObject<SwiftOptional<SwiftArray<int>>>((IntPtr)buffer);
            Check(optional.HasValue, "cold Optional<Array<Int32>> materializes Some");
            using var array = optional.Some;
            Check(array.Count == 2 && array[0] == 17 && array[1] == 29, "nested generic native array elements");
        }
        finally { Native.OptionalDestroy((IntPtr)buffer); NativeMemory.Free(buffer); }
    }

    private static void ColdTuple()
    {
        var metadata = TypeMetadata.GetTypeMetadataOrThrow<(int, double, bool)>();
        Check(metadata.Size == (nuint)Native.TupleSize(), "cold tuple metadata matches Swift layout");
        var buffer = NativeMemory.Alloc(metadata.Size);
        try
        {
            Native.TupleInit((IntPtr)buffer);
            var tuple = SwiftMarshal.MarshalFromSwift<(int, double, bool)>((IntPtr)buffer);
            Check(tuple == (47, 2.5, true), "cold tuple materialization from Swift");
            var span = new Span<byte>(buffer, checked((int)metadata.Size));
            SwiftMarshal.MarshalToSwift((89, 4.25, false), ref span);
            Check(Native.TupleCheck((IntPtr)buffer) == 1, "Swift observes managed tuple marshalling");
        }
        finally { NativeMemory.Free(buffer); }
    }

    private static void Dictionary()
    {
        using var dictionary = new SwiftDictionary<SwiftString, int>();
        using var key = new SwiftString("cold-key");
        dictionary[key] = 73;
        Check(dictionary.TryGetValue(key, out var value) && value == 73, "Dictionary<String,Int32> Hashable lookup");
        Check(Native.DictionaryCheck(dictionary.Payload.DangerousGetHandle()) == 1, "Swift observes managed dictionary");
        GC.KeepAlive(dictionary);
    }

    private static void Check(bool condition, string assertion)
    {
        if (!condition) throw new InvalidOperationException(assertion);
        Passed.Add(assertion);
    }

    private static class Native
    {
        private const string Fixture = "CoreClrCanary";
        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_dyld_image_count")] internal static extern uint ImageCount();
        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_dyld_get_image_name")] internal static extern IntPtr ImageName(uint index);
        [DllImport(Fixture, EntryPoint = "coreclr_canary_add")] internal static extern int Add(int left, int right);
        [DllImport(Fixture, EntryPoint = "coreclr_canary_callback")] internal static extern int Callback(int value, IntPtr context, delegate* unmanaged[Cdecl]<int, IntPtr, int> callback);
        [DllImport(Fixture, EntryPoint = "coreclr_canary_array_size")] internal static extern nint ArraySize();
        [DllImport(Fixture, EntryPoint = "coreclr_canary_array_init")] internal static extern void ArrayInit(IntPtr output);
        [DllImport(Fixture, EntryPoint = "coreclr_canary_array_destroy")] internal static extern void ArrayDestroy(IntPtr output);
        [DllImport(Fixture, EntryPoint = "coreclr_canary_array_check")] internal static extern int ArrayCheck(IntPtr value);
        [DllImport(Fixture, EntryPoint = "coreclr_canary_dictionary_check")] internal static extern int DictionaryCheck(IntPtr value);
        [DllImport(Fixture, EntryPoint = "coreclr_canary_optional_size")] internal static extern nint OptionalSize();
        [DllImport(Fixture, EntryPoint = "coreclr_canary_optional_init")] internal static extern void OptionalInit(IntPtr output);
        [DllImport(Fixture, EntryPoint = "coreclr_canary_optional_destroy")] internal static extern void OptionalDestroy(IntPtr output);
        [DllImport(Fixture, EntryPoint = "coreclr_canary_tuple_size")] internal static extern nint TupleSize();
        [DllImport(Fixture, EntryPoint = "coreclr_canary_tuple_init")] internal static extern void TupleInit(IntPtr output);
        [DllImport(Fixture, EntryPoint = "coreclr_canary_tuple_check")] internal static extern int TupleCheck(IntPtr value);
    }
}
