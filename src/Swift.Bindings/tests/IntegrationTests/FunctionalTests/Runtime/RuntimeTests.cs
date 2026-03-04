// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;
using BindingsGeneration.Tests;
using Swift;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
using Xunit;

using Bindings = RuntimeTests;


namespace BindingsGeneration.FunctionalTests
{
    public partial class RuntimeTests : IClassFixture<RuntimeTests.TestFixture>
    {
        private readonly TestFixture _fixture;

        public RuntimeTests(TestFixture fixture)
        {
            _fixture = fixture;
        }

        public class TestFixture
        {
            static TestFixture()
            {
                InitializeResources();
            }

            private static void InitializeResources()
            {
                // Initialize
            }
        }

        [Fact]
        public void SmokeTestArray()
        {
            var array = Bindings.Functions.GetArray(3);
            Assert.Equal(3, array.Count);
            Assert.Equal(0, array[0]);
            Assert.Equal(1, array[1]);
            Assert.Equal(2, array[2]);
            Assert.Equal(3, Bindings.Functions.SumArray(array));
        }

        [Fact]
        public void TestEmptyArray()
        {
            var array = Bindings.Functions.GetArray(0);
            Assert.Empty(array);
            Assert.Equal(0, Bindings.Functions.SumArray(array));
        }

        [Fact]
        public void TestOneElementArray()
        {
            var array = Bindings.Functions.GetArray(1);
            Assert.Single(array);
            Assert.Equal(0, array[0]);
            Assert.Equal(0, Bindings.Functions.SumArray(array));
        }

        [Fact]
        public void TestBigArray()
        {
            var array = Bindings.Functions.GetArray(10000);
            Assert.Equal(10000, array.Count);
            var sum = 0;
            for (int i = 0; i < 10000; i++)
            {
                Assert.Equal(i, array[i]);
                sum += i;
            }
            Assert.Equal(sum, Bindings.Functions.SumArray(array));
        }

        [Fact]
        public void TestArrayPassThrough()
        {
            IReadOnlyList<int> array = Bindings.Functions.GetArray(3);
            Assert.Equal(3, array.Count);

            IReadOnlyList<int> arrayCopy = Bindings.Functions.PassThroughArray(array);
            Assert.Equal(3, arrayCopy.Count);

            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(array[i], arrayCopy[i]);
            }
        }

        [Fact]
        public void TestArrayPassThroughDifferentPayloads()
        {
            IReadOnlyList<int> array = Bindings.Functions.GetArray(3);
            IReadOnlyList<int> copy1 = Bindings.Functions.PassThroughArray(array);
            IReadOnlyList<int> copy2 = Bindings.Functions.PassThroughArray(array);

            for (int i = 0; i < array.Count; i++)
            {
                Assert.Equal(array[i], copy1[i]);
                Assert.Equal(array[i], copy2[i]);
            }
        }

        [Fact]
        public unsafe void TestSwiftMarshalArray()
        {
            SwiftArray<Int32> vtype = new SwiftArray<Int32>();
            vtype.Append(42);
            Assert.Equal(1, Arc.RetainCount(*(IntPtr*)vtype.Payload.DangerousGetHandle()));

            var metadata = SwiftObjectHelper<SwiftArray<Int32>>.GetTypeMetadata();
            Span<byte> payloadSpan = stackalloc byte[(int)metadata.Size];
            IntPtr payloadPtr = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(payloadSpan));

            // Marshal the object to Swift
            SwiftMarshal.MarshalToSwift(vtype, ref payloadSpan);
            Assert.Equal(2, Arc.RetainCount(*(IntPtr*)vtype.Payload.DangerousGetHandle()));

            // Marshal back from Swift
            var copy = SwiftMarshal.MarshalFromSwift<SwiftArray<Int32>>(payloadPtr);
            Assert.Equal(2, Arc.RetainCount(*(IntPtr*)copy.Payload.DangerousGetHandle()));

            // Dispose the copy and verify retain count
            copy.Payload.Dispose();
            Assert.Equal(1, Arc.RetainCount(*(IntPtr*)vtype.Payload.DangerousGetHandle()));
        }

        [Fact]
        public async Task ConcurrentArray()
        {
            for (int i = 0; i < 10; i++)
            {
                IReadOnlyList<int> array = Bindings.Functions.GetArray(100);
                var barrier = new Barrier(2);

                var readerTask = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    try
                    {
                        var sum = array.Sum();
                        Assert.Equal(4950, sum); // Sum of 0..99
                    }
                    catch (ObjectDisposedException)
                    {
                        // Expected if disposed before read completes
                    }
                });

                var disposeTask = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    if (array is IDisposable disposable)
                        disposable.Dispose();
                });

                await Task.WhenAll(readerTask, disposeTask);
            }
        }

        [Fact]
        public void SmokeTestSet()
        {
            var set = GetSet(3);
            Assert.Equal(3, set.Count);
            Assert.Equal(3, SumSet(set));
        }

        [Fact]
        public void TestEmptySet()
        {
            var set = GetSet(0);
            int count = set.Count;
            Assert.Equal(0, count);
            Assert.Equal(0, SumSet(set));
        }

        [Fact]
        public void TestOneElementSet()
        {
            var set = GetSet(1);
            int count = set.Count;
            Assert.Equal(1, count);
            Assert.Equal(0, SumSet(set));
        }

        [Fact]
        public void TestBigSet()
        {
            var set = GetSet(10000);
            Assert.Equal(10000, set.Count);
            Assert.Equal(49995000, SumSet(set));

        }

        [Fact]
        public unsafe void TestSetPassThrough()
        {
            var set = GetSet(3);
            Assert.Equal(3, set.Count);
            Assert.Equal(3, SumSet(set));

            // Check the initial count
            Assert.Equal(1, Arc.RetainCount(*(IntPtr*)set.Payload.DangerousGetHandle()));

            var setCopy = PassThroughSet(set);
            Assert.Equal(3, setCopy.Count);
            Assert.Equal(3, SumSet(setCopy));

            // Check the references are not the same
            Assert.NotEqual(set.Payload.DangerousGetHandle(), setCopy.Payload.DangerousGetHandle());
            // Check the payloads are the same
            Assert.Equal(*(IntPtr*)set.Payload.DangerousGetHandle(), *(IntPtr*)setCopy.Payload.DangerousGetHandle());

            // Check the count after the copy
            Assert.Equal(2, Arc.RetainCount(*(IntPtr*)set.Payload.DangerousGetHandle()));
            Assert.Equal(2, Arc.RetainCount(*(IntPtr*)setCopy.Payload.DangerousGetHandle()));

            setCopy.Payload.Dispose();
            // Check the count after the copy is disposed
            Assert.Equal(1, Arc.RetainCount(*(IntPtr*)set.Payload.DangerousGetHandle()));
        }

        // Helper methods needed because binding generator doesn't yet support SwiftSet<T> marshalling
        // for method parameters and return types. These manually marshal between IntPtr and SwiftSet.
        private static unsafe SwiftSet<SwiftIntMock> GetSet(int count)
        {
            IntPtr variant = PInvoke_GetSet(count);
            return SwiftMarshal.MarshalFromSwift<SwiftSet<SwiftIntMock>>(new IntPtr(&variant));
        }

        private static unsafe int SumSet(SwiftSet<SwiftIntMock> set)
        {
            IntPtr variant = *(IntPtr*)set.Payload.DangerousGetHandle();
            return PInvoke_SumSet(variant);
        }

        private static unsafe SwiftSet<SwiftIntMock> PassThroughSet(SwiftSet<SwiftIntMock> set)
        {
            IntPtr variant = *(IntPtr*)set.Payload.DangerousGetHandle();
            variant = PInvoke_PassThroughSet(variant);
            return SwiftMarshal.MarshalFromSwift<SwiftSet<SwiftIntMock>>(new IntPtr(&variant));
        }

        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [LibraryImport("Runtime/libRuntimeTests.dylib", EntryPoint = "$s12RuntimeTests8getArray5countSays5Int32VGAE_tF")]
        private static partial IntPtr PInvoke_GetSet(int count);

        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [LibraryImport("Runtime/libRuntimeTests.dylib", EntryPoint = "$s12RuntimeTests8sumArray5arrays5Int32VSayAEG_tF")]
        private static partial int PInvoke_SumSet(IntPtr set);

        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [LibraryImport("Runtime/libRuntimeTests.dylib", EntryPoint = "$s12RuntimeTests16passThroughArray5arraySays5Int32VGAF_tF")]
        private static partial IntPtr PInvoke_PassThroughSet(IntPtr set);

        [Fact]
        public unsafe void TestSwiftMarshalSet()
        {
            SwiftSet<SwiftIntMock> vtype = GetSet(1);
            Assert.Equal(1, Arc.RetainCount(*(IntPtr*)vtype.Payload.DangerousGetHandle()));

            var metadata = SwiftObjectHelper<SwiftSet<SwiftIntMock>>.GetTypeMetadata();
            Span<byte> payloadSpan = stackalloc byte[(int)metadata.Size];
            IntPtr payloadPtr = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(payloadSpan));

            // Marshal the object to Swift
            SwiftMarshal.MarshalToSwift(vtype, ref payloadSpan);
            Assert.Equal(2, Arc.RetainCount(*(IntPtr*)vtype.Payload.DangerousGetHandle()));

            // Marshal back from Swift
            var copy = SwiftMarshal.MarshalFromSwift<SwiftSet<SwiftIntMock>>(payloadPtr);
            Assert.Equal(2, Arc.RetainCount(*(IntPtr*)copy.Payload.DangerousGetHandle()));

            // Dispose the copy and verify retain count
            copy.Payload.Dispose();
            Assert.Equal(1, Arc.RetainCount(*(IntPtr*)vtype.Payload.DangerousGetHandle()));
        }

        [Fact]
        public async Task ConcurrentSet()
        {
            for (int i = 0; i < 10; i++)
            {
                SwiftSet<SwiftIntMock> resource = GetSet(1);
                var barrier = new Barrier(2);

                var getterTask = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    try
                    {
                        int resourceCount = resource.Count;
                        Assert.Equal(1, resourceCount);
                    }
                    catch (ObjectDisposedException ex)
                    {
                        Assert.IsType<ObjectDisposedException>(ex);
                    }
                });

                var disposeTask = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    resource.Payload.Dispose();
                });

                await Task.WhenAll(getterTask, disposeTask);

                Assert.True(resource.Payload.IsClosed);
            }
        }

        [Fact]
        public void SmokeTestString()
        {
            var str = Bindings.Functions.GetString(3);
            Assert.Equal(3, str.Length);
            Assert.Equal("aaa", str.ToString());
        }

        [Fact]
        public void TestEmptyString()
        {
            var str = Bindings.Functions.GetString(0);
            Assert.Equal(0, str.Length);
            Assert.Equal(string.Empty, str.ToString());
            Assert.Equal(str.Length, Bindings.Functions.VerifyString(str));
        }

        [Fact]
        public void TestOneElementString()
        {
            var str = Bindings.Functions.GetString(1);
            Assert.Equal(1, str.Length);
            Assert.Equal("a", str.ToString());
            Assert.Equal(str.Length, Bindings.Functions.VerifyString(str));
        }

        [Fact]
        public void TestBigString()
        {
            var str = Bindings.Functions.GetString(10000);
            Assert.Equal(10000, str.Length);
            Assert.Equal(new string('a', 10000), str.ToString());
            Assert.Equal(str.Length, Bindings.Functions.VerifyString(str));
        }

        [Fact]
        public void TestStringPassThrough()
        {
            string heapString = Bindings.Functions.GetString(16);
            Assert.Equal(16, heapString.Length);
            Assert.Equal(new string('a', 16), heapString);

            string strCopy = Bindings.Functions.PassThroughString(heapString);
            Assert.Equal(heapString, strCopy);
        }

        [Fact]
        public void TestSwiftMarshalString()
        {
            string input = new string('a', 16);
            string result = Bindings.Functions.PassThroughString(input);
            Assert.Equal(input, result);
        }

        [Fact]
        public async Task ConcurrentString()
        {
            for (int i = 0; i < 10; i++)
            {
                string original = Bindings.Functions.GetString(16);
                var barrier = new Barrier(2);

                var passThroughTask = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    string copy = Bindings.Functions.PassThroughString(original);
                    Assert.Equal(original, copy);
                });

                var readTask = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    Assert.Equal(16, original.Length);
                });

                await Task.WhenAll(passThroughTask, readTask);
            }
        }
    }
}
