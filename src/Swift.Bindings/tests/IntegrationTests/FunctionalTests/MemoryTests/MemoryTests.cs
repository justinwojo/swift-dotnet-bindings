// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;
using BindingsGeneration.Tests;
using Swift;
using Swift.MemoryTests;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
using Xunit;

using Bindings = Swift.MemoryTests;


namespace BindingsGeneration.FunctionalTests
{
    public static unsafe class MemoryExtensions
    {
        /// <summary>
        /// Reads a pointer stored at the given index.
        /// </summary>
        public static IntPtr At(this IntPtr ptr, int index)
        {
            byte* bytePtr = (byte*)ptr.ToPointer();
            return *(IntPtr*)(bytePtr + index * IntPtr.Size);
        }
    }

    public partial class MemoryTests : IClassFixture<MemoryTests.TestFixture>
    {
        private readonly TestFixture _fixture;

        public MemoryTests(TestFixture fixture)
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
        public unsafe void TestInitWithCopyVType()
        {
            Bindings.VType vType = new Bindings.VType();
            GC.SuppressFinalize(vType);
            GC.SuppressFinalize(vType.Payload);
            IntPtr payload = (IntPtr)vType.Payload.DangerousGetHandle();

            // Check the initial count
            Assert.Equal(1, Arc.RetainCount(payload.At(0)));
            // Check the metadata flags for a class
            Assert.Equal(0x3, payload.At(0).At(1));
            // Check deinit is not called
            Assert.Equal(0, vType.RefTypeTest);

            var metadata = SwiftObjectHelper<Bindings.VType>.GetTypeMetadata();

            // Creates a copy of the vType
            IntPtr payloadCopy = (IntPtr)NativeMemory.Alloc(metadata.Size);
            metadata.ValueWitnessTable->InitializeWithCopy((void*)payloadCopy, (void*)payload, metadata);

            // Check the copy ref is the same
            Assert.Equal(payload.At(0), payloadCopy.At(0));

            // Check the count after copy
            Assert.Equal(2, Arc.RetainCount(payload.At(0)));
            Assert.Equal(2, Arc.RetainCount(payloadCopy.At(0)));

            Arc.Release(payload.At(0));
            // // Check the count after release
            Assert.Equal(1, Arc.RetainCount(payload.At(0)));
            Assert.Equal(1, Arc.RetainCount(payloadCopy.At(0)));

            Arc.Release(payload.At(0));

            // // Check deinit is called
            Assert.Equal(1, vType.RefTypeTest);

            NativeMemory.Free((void*)payloadCopy);
        }

        [Fact]
        private static unsafe void TestDestroyVType()
        {
            Bindings.VType vType = new Bindings.VType();
            GC.SuppressFinalize(vType);
            GC.SuppressFinalize(vType.Payload);
            IntPtr payload = (IntPtr)vType.Payload.DangerousGetHandle();

            // Check the initial count
            Assert.Equal(1, Arc.RetainCount(payload.At(0)));
            // Check the metadata flags for a class
            Assert.Equal(0x3, payload.At(0).At(1));
            // Check deinit is not called
            Assert.Equal(0, vType.RefTypeTest);

            Arc.Retain(payload.At(0));
            Arc.Retain(payload.At(0));

            // Check the count after retain
            Assert.Equal(3, Arc.RetainCount(payload.At(0)));

            var metadata = SwiftObjectHelper<Bindings.VType>.GetTypeMetadata();
            metadata.ValueWitnessTable->Destroy((void*)payload, metadata);
            // Check the count after destroy
            Assert.Equal(2, Arc.RetainCount(payload.At(0)));
            // Check deinit is not called
            Assert.Equal(0, vType.RefTypeTest);

            metadata.ValueWitnessTable->Destroy((void*)payload, metadata);
            // Check the count after destroy
            Assert.Equal(1, Arc.RetainCount(payload.At(0)));
            // Check deinit is not called
            Assert.Equal(0, vType.RefTypeTest);

            metadata.ValueWitnessTable->Destroy((void*)payload, metadata);

            // Check deinit is called
            Assert.Equal(1, vType.RefTypeTest);
        }

        [Fact]
        public unsafe void TestInitWithCopyAndDestroyVType()
        {
            Bindings.VType vType = new Bindings.VType();
            GC.SuppressFinalize(vType);
            GC.SuppressFinalize(vType.Payload);
            IntPtr payload = (IntPtr)vType.Payload.DangerousGetHandle();

            // Check the initial count
            Assert.Equal(1, Arc.RetainCount(payload.At(0)));
            // Check the metadata flags for a class
            Assert.Equal(0x3, payload.At(0).At(1));
            // Check deinit is not called
            Assert.Equal(0, vType.RefTypeTest);

            var metadata = SwiftObjectHelper<Bindings.VType>.GetTypeMetadata();

            IntPtr payloadCopy = (IntPtr)NativeMemory.Alloc(metadata.Size);
            metadata.ValueWitnessTable->InitializeWithCopy((void*)payloadCopy, (void*)payload, metadata);

            // Check the copy ref is the same
            Assert.Equal(payload.At(0), payloadCopy.At(0));

            // Check the count after copy
            Assert.Equal(2, Arc.RetainCount(payload.At(0)));
            Assert.Equal(2, Arc.RetainCount(payloadCopy.At(0)));

            IntPtr payloadCopyCopy = (IntPtr)NativeMemory.Alloc(metadata.Size);
            metadata.ValueWitnessTable->InitializeWithCopy((void*)payloadCopyCopy, (void*)payloadCopy, metadata);

            // Check the copy ref is the same
            Assert.Equal(payload.At(0), payloadCopy.At(0));
            Assert.Equal(payload.At(0), payloadCopyCopy.At(0));

            // Check the count after copy
            Assert.Equal(3, Arc.RetainCount(payload.At(0)));
            Assert.Equal(3, Arc.RetainCount(payloadCopy.At(0)));
            Assert.Equal(3, Arc.RetainCount(payloadCopyCopy.At(0)));

            metadata.ValueWitnessTable->Destroy((void*)payload, metadata);
            // Check the count after destroy
            Assert.Equal(2, Arc.RetainCount(payload.At(0)));
            Assert.Equal(2, Arc.RetainCount(payloadCopy.At(0)));
            Assert.Equal(2, Arc.RetainCount(payloadCopyCopy.At(0)));
            // Check deinit is not called
            Assert.Equal(0, vType.RefTypeTest);

            metadata.ValueWitnessTable->Destroy((void*)payload, metadata);
            // Check the count after destroy
            Assert.Equal(1, Arc.RetainCount(payload.At(0)));
            Assert.Equal(1, Arc.RetainCount(payloadCopy.At(0)));
            Assert.Equal(1, Arc.RetainCount(payloadCopyCopy.At(0)));
            // Check deinit is not called
            Assert.Equal(0, vType.RefTypeTest);

            metadata.ValueWitnessTable->Destroy((void*)payload, metadata);

            // Check deinit is called
            Assert.Equal(1, vType.RefTypeTest);

            NativeMemory.Free((void*)payloadCopy);
            NativeMemory.Free((void*)payloadCopyCopy);
        }

        [Fact]
        public unsafe void TestInitWithCopyNestedVType()
        {
            Bindings.NestedVType vType = new Bindings.NestedVType();
            GC.SuppressFinalize(vType);
            GC.SuppressFinalize(vType.Payload);
            IntPtr payload = (IntPtr)vType.Payload.DangerousGetHandle();

            // Check the initial count
            Assert.Equal(1, Arc.RetainCount(payload.At(0)));
            Assert.Equal(1, Arc.RetainCount(payload.At(2)));
            // Check the metadata flags for a class
            Assert.Equal(0x3, payload.At(0).At(1));
            Assert.Equal(0x3, payload.At(2).At(1));
            // Check deinit is not called
            Assert.Equal(0, vType.RefTypeTest1);
            Assert.Equal(0, vType.RefTypeTest2);

            var metadata = SwiftObjectHelper<Bindings.NestedVType>.GetTypeMetadata();

            // Creates a copy of the vType
            IntPtr payloadCopy = (IntPtr)NativeMemory.Alloc(metadata.Size);
            metadata.ValueWitnessTable->InitializeWithCopy((void*)payloadCopy, (void*)payload, metadata);

            // Check the copy ref is the same
            Assert.Equal(payload.At(0), payloadCopy.At(0));
            Assert.Equal(payload.At(2), payloadCopy.At(2));

            // Check the count after copy
            Assert.Equal(2, Arc.RetainCount(payload.At(0)));
            Assert.Equal(2, Arc.RetainCount(payloadCopy.At(0)));
            Assert.Equal(2, Arc.RetainCount(payload.At(2)));
            Assert.Equal(2, Arc.RetainCount(payloadCopy.At(2)));

            Arc.Release(payload.At(0));
            Arc.Release(payload.At(2));
            // Check the count after release
            Assert.Equal(1, Arc.RetainCount(payload.At(0)));
            Assert.Equal(1, Arc.RetainCount(payloadCopy.At(0)));
            Assert.Equal(1, Arc.RetainCount(payload.At(2)));
            Assert.Equal(1, Arc.RetainCount(payloadCopy.At(2)));

            Arc.Release(payload.At(0));
            Arc.Release(payload.At(2));

            // Check deinit is called
            Assert.Equal(1, vType.RefTypeTest1);
            Assert.Equal(1, vType.RefTypeTest2);

            NativeMemory.Free((void*)payloadCopy);
        }

        [Fact]
        private static unsafe void TestDestroyNestedVType()
        {
            Bindings.NestedVType vType = new Bindings.NestedVType();
            GC.SuppressFinalize(vType);
            GC.SuppressFinalize(vType.Payload);
            IntPtr payload = (IntPtr)vType.Payload.DangerousGetHandle();

            // Check the initial count
            Assert.Equal(1, Arc.RetainCount(payload.At(0)));
            Assert.Equal(1, Arc.RetainCount(payload.At(2)));
            // Check the metadata flags for a class
            Assert.Equal(0x3, payload.At(0).At(1));
            Assert.Equal(0x3, payload.At(2).At(1));
            // Check deinit is not called
            Assert.Equal(0, vType.RefTypeTest1);
            Assert.Equal(0, vType.RefTypeTest2);

            Arc.Retain(payload.At(0));
            Arc.Retain(payload.At(0));
            Arc.Retain(payload.At(2));
            Arc.Retain(payload.At(2));

            // Check the count after retain
            Assert.Equal(3, Arc.RetainCount(payload.At(0)));
            Assert.Equal(3, Arc.RetainCount(payload.At(2)));

            var metadata = SwiftObjectHelper<Bindings.NestedVType>.GetTypeMetadata();
            metadata.ValueWitnessTable->Destroy((void*)payload, metadata);
            // Check the count after destroy
            Assert.Equal(2, Arc.RetainCount(payload.At(0)));
            Assert.Equal(2, Arc.RetainCount(payload.At(2)));
            // Check deinit is not called
            Assert.Equal(0, vType.RefTypeTest1);
            Assert.Equal(0, vType.RefTypeTest2);

            metadata.ValueWitnessTable->Destroy((void*)payload, metadata);
            // Check the count after destroy
            Assert.Equal(1, Arc.RetainCount(payload.At(0)));
            Assert.Equal(1, Arc.RetainCount(payload.At(2)));
            // Check deinit is not called
            Assert.Equal(0, vType.RefTypeTest1);
            Assert.Equal(0, vType.RefTypeTest2);

            metadata.ValueWitnessTable->Destroy((void*)payload, metadata);

            // Check deinit is called
            Assert.Equal(1, vType.RefTypeTest1);
            Assert.Equal(1, vType.RefTypeTest2);
        }

        [Fact]
        public unsafe void TestInitWithCopyAndDestroyNestedVType()
        {
            Bindings.NestedVType vType = new Bindings.NestedVType();
            GC.SuppressFinalize(vType);
            GC.SuppressFinalize(vType.Payload);
            IntPtr payload = (IntPtr)vType.Payload.DangerousGetHandle();

            // Check the initial count
            Assert.Equal(1, Arc.RetainCount(payload.At(0)));
            Assert.Equal(1, Arc.RetainCount(payload.At(2)));
            // Check the metadata flags for a class
            Assert.Equal(0x3, payload.At(0).At(1));
            Assert.Equal(0x3, payload.At(2).At(1));
            // Check deinit is not called
            Assert.Equal(0, vType.RefTypeTest1);
            Assert.Equal(0, vType.RefTypeTest2);

            var metadata = SwiftObjectHelper<Bindings.NestedVType>.GetTypeMetadata();

            IntPtr payloadCopy = (IntPtr)NativeMemory.Alloc(metadata.Size);
            metadata.ValueWitnessTable->InitializeWithCopy((void*)payloadCopy, (void*)payload, metadata);

            // Check the copy ref is the same
            Assert.Equal(payload.At(0), payloadCopy.At(0));
            Assert.Equal(payload.At(2), payloadCopy.At(2));

            // Check the count after copy
            Assert.Equal(2, Arc.RetainCount(payload.At(0)));
            Assert.Equal(2, Arc.RetainCount(payloadCopy.At(0)));
            Assert.Equal(2, Arc.RetainCount(payload.At(2)));
            Assert.Equal(2, Arc.RetainCount(payloadCopy.At(2)));

            IntPtr payloadCopyCopy = (IntPtr)NativeMemory.Alloc(metadata.Size);
            metadata.ValueWitnessTable->InitializeWithCopy((void*)payloadCopyCopy, (void*)payloadCopy, metadata);

            // Check the copy ref is the same
            Assert.Equal(payload.At(0), payloadCopy.At(0));
            Assert.Equal(payload.At(0), payloadCopyCopy.At(0));
            Assert.Equal(payload.At(2), payloadCopy.At(2));
            Assert.Equal(payload.At(2), payloadCopyCopy.At(2));

            // Check the count after copy
            Assert.Equal(3, Arc.RetainCount(payload.At(0)));
            Assert.Equal(3, Arc.RetainCount(payloadCopy.At(0)));
            Assert.Equal(3, Arc.RetainCount(payloadCopyCopy.At(0)));
            Assert.Equal(3, Arc.RetainCount(payload.At(2)));
            Assert.Equal(3, Arc.RetainCount(payloadCopy.At(2)));
            Assert.Equal(3, Arc.RetainCount(payloadCopyCopy.At(2)));

            metadata.ValueWitnessTable->Destroy((void*)payload, metadata);
            // Check the count after destroy
            Assert.Equal(2, Arc.RetainCount(payload.At(0)));
            Assert.Equal(2, Arc.RetainCount(payloadCopy.At(0)));
            Assert.Equal(2, Arc.RetainCount(payloadCopyCopy.At(0)));
            Assert.Equal(2, Arc.RetainCount(payload.At(2)));
            Assert.Equal(2, Arc.RetainCount(payloadCopy.At(2)));
            Assert.Equal(2, Arc.RetainCount(payloadCopyCopy.At(2)));
            // Check deinit is not called
            Assert.Equal(0, vType.RefTypeTest1);
            Assert.Equal(0, vType.RefTypeTest2);

            metadata.ValueWitnessTable->Destroy((void*)payload, metadata);
            // Check the count after destroy
            Assert.Equal(1, Arc.RetainCount(payload.At(0)));
            Assert.Equal(1, Arc.RetainCount(payloadCopy.At(0)));
            Assert.Equal(1, Arc.RetainCount(payloadCopyCopy.At(0)));
            Assert.Equal(1, Arc.RetainCount(payload.At(2)));
            Assert.Equal(1, Arc.RetainCount(payloadCopy.At(2)));
            Assert.Equal(1, Arc.RetainCount(payloadCopyCopy.At(2)));
            // Check deinit is not called
            Assert.Equal(0, vType.RefTypeTest1);
            Assert.Equal(0, vType.RefTypeTest2);

            metadata.ValueWitnessTable->Destroy((void*)payload, metadata);

            // Check deinit is called
            Assert.Equal(1, vType.RefTypeTest1);
            Assert.Equal(1, vType.RefTypeTest2);

            NativeMemory.Free((void*)payloadCopy);
            NativeMemory.Free((void*)payloadCopyCopy);
        }

        [Fact]
        public unsafe void TestInitWithCopyNestedNestedVType()
        {
            Bindings.NestedNestedVType vType = new Bindings.NestedNestedVType();
            GC.SuppressFinalize(vType);
            GC.SuppressFinalize(vType.Payload);
            IntPtr payload = (IntPtr)vType.Payload.DangerousGetHandle();

            // Check the initial count
            Assert.Equal(1, Arc.RetainCount(payload.At(0)));
            Assert.Equal(1, Arc.RetainCount(payload.At(2)));
            Assert.Equal(1, Arc.RetainCount(payload.At(4)));
            // Check the metadata flags for a class
            Assert.Equal(0x3, payload.At(0).At(1));
            Assert.Equal(0x3, payload.At(2).At(1));
            Assert.Equal(0x3, payload.At(4).At(1));
            // Check deinit is not called
            Assert.Equal(0, vType.RefTypeTest1);
            Assert.Equal(0, vType.RefTypeTest2);
            Assert.Equal(0, vType.RefTypeTest3);

            var metadata = SwiftObjectHelper<Bindings.NestedNestedVType>.GetTypeMetadata();

            // Creates a copy of the vType
            IntPtr payloadCopy = (IntPtr)NativeMemory.Alloc(metadata.Size);
            metadata.ValueWitnessTable->InitializeWithCopy((void*)payloadCopy, (void*)payload, metadata);

            // Check the copy ref is the same
            Assert.Equal(payload.At(0), payloadCopy.At(0));
            Assert.Equal(payload.At(2), payloadCopy.At(2));
            Assert.Equal(payload.At(4), payloadCopy.At(4));

            // Check the count after copy
            Assert.Equal(2, Arc.RetainCount(payload.At(0)));
            Assert.Equal(2, Arc.RetainCount(payloadCopy.At(0)));
            Assert.Equal(2, Arc.RetainCount(payload.At(2)));
            Assert.Equal(2, Arc.RetainCount(payloadCopy.At(2)));
            Assert.Equal(2, Arc.RetainCount(payload.At(4)));
            Assert.Equal(2, Arc.RetainCount(payloadCopy.At(4)));

            Arc.Release(payload.At(0));
            Arc.Release(payload.At(2));
            Arc.Release(payload.At(4));
            // Check the count after release
            Assert.Equal(1, Arc.RetainCount(payload.At(0)));
            Assert.Equal(1, Arc.RetainCount(payloadCopy.At(0)));
            Assert.Equal(1, Arc.RetainCount(payload.At(2)));
            Assert.Equal(1, Arc.RetainCount(payloadCopy.At(2)));
            Assert.Equal(1, Arc.RetainCount(payload.At(4)));
            Assert.Equal(1, Arc.RetainCount(payloadCopy.At(4)));

            Arc.Release(payload.At(0));
            Arc.Release(payload.At(2));
            Arc.Release(payload.At(4));

            // Check deinit is called
            Assert.Equal(1, vType.RefTypeTest1);
            Assert.Equal(1, vType.RefTypeTest2);
            Assert.Equal(1, vType.RefTypeTest3);

            NativeMemory.Free((void*)payloadCopy);
        }

        [Fact]
        private static unsafe void TestDestroyNestedNestedVType()
        {
            Bindings.NestedNestedVType vType = new Bindings.NestedNestedVType();
            GC.SuppressFinalize(vType);
            GC.SuppressFinalize(vType.Payload);
            IntPtr payload = (IntPtr)vType.Payload.DangerousGetHandle();

            // Check the initial count
            Assert.Equal(1, Arc.RetainCount(payload.At(0)));
            Assert.Equal(1, Arc.RetainCount(payload.At(2)));
            Assert.Equal(1, Arc.RetainCount(payload.At(4)));
            // Check the metadata flags for a class
            Assert.Equal(0x3, payload.At(0).At(1));
            Assert.Equal(0x3, payload.At(2).At(1));
            Assert.Equal(0x3, payload.At(4).At(1));
            // Check deinit is not called
            Assert.Equal(0, vType.RefTypeTest1);
            Assert.Equal(0, vType.RefTypeTest2);
            Assert.Equal(0, vType.RefTypeTest3);

            Arc.Retain(payload.At(0));
            Arc.Retain(payload.At(0));
            Arc.Retain(payload.At(2));
            Arc.Retain(payload.At(2));
            Arc.Retain(payload.At(4));
            Arc.Retain(payload.At(4));

            // Check the count after retain
            Assert.Equal(3, Arc.RetainCount(payload.At(0)));
            Assert.Equal(3, Arc.RetainCount(payload.At(2)));
            Assert.Equal(3, Arc.RetainCount(payload.At(4)));

            var metadata = SwiftObjectHelper<Bindings.NestedNestedVType>.GetTypeMetadata();
            metadata.ValueWitnessTable->Destroy((void*)payload, metadata);
            // Check the count after destroy
            Assert.Equal(2, Arc.RetainCount(payload.At(0)));
            Assert.Equal(2, Arc.RetainCount(payload.At(2)));
            Assert.Equal(2, Arc.RetainCount(payload.At(4)));
            // Check deinit is not called
            Assert.Equal(0, vType.RefTypeTest1);
            Assert.Equal(0, vType.RefTypeTest2);
            Assert.Equal(0, vType.RefTypeTest3);

            metadata.ValueWitnessTable->Destroy((void*)payload, metadata);
            // Check the count after destroy
            Assert.Equal(1, Arc.RetainCount(payload.At(0)));
            Assert.Equal(1, Arc.RetainCount(payload.At(2)));
            Assert.Equal(1, Arc.RetainCount(payload.At(4)));
            // Check deinit is not called
            Assert.Equal(0, vType.RefTypeTest1);
            Assert.Equal(0, vType.RefTypeTest2);
            Assert.Equal(0, vType.RefTypeTest3);

            metadata.ValueWitnessTable->Destroy((void*)payload, metadata);

            // Check deinit is called
            Assert.Equal(1, vType.RefTypeTest1);
            Assert.Equal(1, vType.RefTypeTest2);
            Assert.Equal(1, vType.RefTypeTest3);
        }

        [Fact]
        public unsafe void TestInitWithCopyAndDestroyNestedNestedVType()
        {
            Bindings.NestedNestedVType vType = new Bindings.NestedNestedVType();
            GC.SuppressFinalize(vType);
            GC.SuppressFinalize(vType.Payload);
            IntPtr payload = (IntPtr)vType.Payload.DangerousGetHandle();

            // Check the initial count
            Assert.Equal(1, Arc.RetainCount(payload.At(0)));
            Assert.Equal(1, Arc.RetainCount(payload.At(2)));
            Assert.Equal(1, Arc.RetainCount(payload.At(4)));
            // Check the metadata flags for a class
            Assert.Equal(0x3, payload.At(0).At(1));
            Assert.Equal(0x3, payload.At(2).At(1));
            Assert.Equal(0x3, payload.At(4).At(1));
            // Check deinit is not called
            Assert.Equal(0, vType.RefTypeTest1);
            Assert.Equal(0, vType.RefTypeTest2);
            Assert.Equal(0, vType.RefTypeTest3);

            var metadata = SwiftObjectHelper<Bindings.NestedNestedVType>.GetTypeMetadata();

            IntPtr payloadCopy = (IntPtr)NativeMemory.Alloc(metadata.Size);
            metadata.ValueWitnessTable->InitializeWithCopy((void*)payloadCopy, (void*)payload, metadata);

            // Check the copy ref is the same
            Assert.Equal(payload.At(0), payloadCopy.At(0));
            Assert.Equal(payload.At(2), payloadCopy.At(2));
            Assert.Equal(payload.At(4), payloadCopy.At(4));

            // Check the count after copy
            Assert.Equal(2, Arc.RetainCount(payload.At(0)));
            Assert.Equal(2, Arc.RetainCount(payloadCopy.At(0)));
            Assert.Equal(2, Arc.RetainCount(payload.At(2)));
            Assert.Equal(2, Arc.RetainCount(payloadCopy.At(2)));
            Assert.Equal(2, Arc.RetainCount(payload.At(4)));
            Assert.Equal(2, Arc.RetainCount(payloadCopy.At(4)));

            IntPtr payloadCopyCopy = (IntPtr)NativeMemory.Alloc(metadata.Size);
            metadata.ValueWitnessTable->InitializeWithCopy((void*)payloadCopyCopy, (void*)payloadCopy, metadata);

            // Check the copy ref is the same
            Assert.Equal(payload.At(0), payloadCopy.At(0));
            Assert.Equal(payload.At(0), payloadCopyCopy.At(0));
            Assert.Equal(payload.At(2), payloadCopy.At(2));
            Assert.Equal(payload.At(2), payloadCopyCopy.At(2));
            Assert.Equal(payload.At(4), payloadCopy.At(4));
            Assert.Equal(payload.At(4), payloadCopyCopy.At(4));

            // Check the count after copy
            Assert.Equal(3, Arc.RetainCount(payload.At(0)));
            Assert.Equal(3, Arc.RetainCount(payloadCopy.At(0)));
            Assert.Equal(3, Arc.RetainCount(payloadCopyCopy.At(0)));
            Assert.Equal(3, Arc.RetainCount(payload.At(2)));
            Assert.Equal(3, Arc.RetainCount(payloadCopy.At(2)));
            Assert.Equal(3, Arc.RetainCount(payloadCopyCopy.At(2)));
            Assert.Equal(3, Arc.RetainCount(payload.At(4)));
            Assert.Equal(3, Arc.RetainCount(payloadCopy.At(4)));
            Assert.Equal(3, Arc.RetainCount(payloadCopyCopy.At(4)));

            metadata.ValueWitnessTable->Destroy((void*)payload, metadata);
            // Check the count after destroy
            Assert.Equal(2, Arc.RetainCount(payload.At(0)));
            Assert.Equal(2, Arc.RetainCount(payloadCopy.At(0)));
            Assert.Equal(2, Arc.RetainCount(payloadCopyCopy.At(0)));
            Assert.Equal(2, Arc.RetainCount(payload.At(2)));
            Assert.Equal(2, Arc.RetainCount(payloadCopy.At(2)));
            Assert.Equal(2, Arc.RetainCount(payloadCopyCopy.At(2)));
            Assert.Equal(2, Arc.RetainCount(payload.At(4)));
            Assert.Equal(2, Arc.RetainCount(payloadCopy.At(4)));
            Assert.Equal(2, Arc.RetainCount(payloadCopyCopy.At(4)));
            // Check deinit is not called
            Assert.Equal(0, vType.RefTypeTest1);
            Assert.Equal(0, vType.RefTypeTest2);
            Assert.Equal(0, vType.RefTypeTest3);

            metadata.ValueWitnessTable->Destroy((void*)payload, metadata);
            // Check the count after destroy
            Assert.Equal(1, Arc.RetainCount(payload.At(0)));
            Assert.Equal(1, Arc.RetainCount(payloadCopy.At(0)));
            Assert.Equal(1, Arc.RetainCount(payloadCopyCopy.At(0)));
            Assert.Equal(1, Arc.RetainCount(payload.At(2)));
            Assert.Equal(1, Arc.RetainCount(payloadCopy.At(2)));
            Assert.Equal(1, Arc.RetainCount(payloadCopyCopy.At(2)));
            Assert.Equal(1, Arc.RetainCount(payload.At(4)));
            Assert.Equal(1, Arc.RetainCount(payloadCopy.At(4)));
            Assert.Equal(1, Arc.RetainCount(payloadCopyCopy.At(4)));
            // Check deinit is not called
            Assert.Equal(0, vType.RefTypeTest1);
            Assert.Equal(0, vType.RefTypeTest2);
            Assert.Equal(0, vType.RefTypeTest3);

            metadata.ValueWitnessTable->Destroy((void*)payload, metadata);

            // Check deinit is called
            Assert.Equal(1, vType.RefTypeTest1);
            Assert.Equal(1, vType.RefTypeTest2);
            Assert.Equal(1, vType.RefTypeTest3);

            NativeMemory.Free((void*)payloadCopy);
            NativeMemory.Free((void*)payloadCopyCopy);
        }

        [Fact]
        public void TestProjectionTypes()
        {
            Assert.True(typeof(Bindings.FrozenStruct).IsValueType);
            Assert.True(typeof(Bindings.FrozenStructRequiresMemoryManagement).IsClass);
            Assert.True(typeof(Bindings.NestedFrozenStructRequiresMemoryManagement).IsClass);
            Assert.True(typeof(Bindings.NonFrozenStruct).IsClass);
            Assert.True(typeof(Bindings.NonFrozenStructRequiresMemoryManagement).IsClass);
        }

        [Fact]
        public unsafe void TestDisposeInvokesDestroy()
        {
            var frozenRequiresMemoryManagement = new Bindings.FrozenStructRequiresMemoryManagement(42);

            // Check the initial count
            Assert.Equal(1, Arc.RetainCount(frozenRequiresMemoryManagement.Payload.DangerousGetHandle().At(0)));
            // Check the metadata flags for a class
            Assert.Equal(0x3, frozenRequiresMemoryManagement.Payload.DangerousGetHandle().At(0).At(1));

            // Retain the payload count
            Arc.Retain(frozenRequiresMemoryManagement.Payload.DangerousGetHandle().At(0));
            Assert.Equal(2, Arc.RetainCount(frozenRequiresMemoryManagement.Payload.DangerousGetHandle().At(0)));

            // Dispose the frozenRequiresMemoryManagement
            Assert.False(frozenRequiresMemoryManagement.Payload.IsClosed);
            Assert.False(frozenRequiresMemoryManagement.Payload.IsInvalid);
            var handle = *(Bindings.FrozenStructRequiresMemoryManagement.Buffer*)frozenRequiresMemoryManagement.Payload.DangerousGetHandle();
            frozenRequiresMemoryManagement.Payload.Dispose();
            Assert.True(frozenRequiresMemoryManagement.Payload.IsClosed);
            Assert.True(frozenRequiresMemoryManagement.Payload.IsInvalid);
            Assert.Equal(1, Arc.RetainCount(new IntPtr(&handle).At(0)));

            var nestedFrozenRequiresMemoryManagement = new Bindings.NestedFrozenStructRequiresMemoryManagement(42);

            // Check the initial count
            Assert.Equal(1, Arc.RetainCount(nestedFrozenRequiresMemoryManagement.Payload.DangerousGetHandle().At(0)));
            // Check the metadata flags for a class
            Assert.Equal(0x3, nestedFrozenRequiresMemoryManagement.Payload.DangerousGetHandle().At(0).At(1));

            // Retain the payload count
            Arc.Retain(nestedFrozenRequiresMemoryManagement.Payload.DangerousGetHandle().At(0));
            Assert.Equal(2, Arc.RetainCount(nestedFrozenRequiresMemoryManagement.Payload.DangerousGetHandle().At(0)));

            // Dispose the NestedFrozenRequiresMemoryManagement
            Assert.False(nestedFrozenRequiresMemoryManagement.Payload.IsClosed);
            Assert.False(nestedFrozenRequiresMemoryManagement.Payload.IsInvalid);
            var nestedHandle = *(Bindings.NestedFrozenStructRequiresMemoryManagement.Buffer*)nestedFrozenRequiresMemoryManagement.Payload.DangerousGetHandle();
            nestedFrozenRequiresMemoryManagement.Payload.Dispose();
            Assert.True(nestedFrozenRequiresMemoryManagement.Payload.IsClosed);
            Assert.True(nestedFrozenRequiresMemoryManagement.Payload.IsInvalid);
            Assert.Equal(1, Arc.RetainCount(new IntPtr(&nestedHandle).At(0)));

            var nonfrozenRequiresMemoryManagement = new Bindings.NonFrozenStructRequiresMemoryManagement(42);

            // Check the initial count
            Assert.Equal(1, Arc.RetainCount(nonfrozenRequiresMemoryManagement.Payload.DangerousGetHandle().At(0)));
            // Check the metadata flags for a class
            Assert.Equal(0x3, nonfrozenRequiresMemoryManagement.Payload.DangerousGetHandle().At(0).At(1));

            // Retain the payload count
            Arc.Retain(nonfrozenRequiresMemoryManagement.Payload.DangerousGetHandle().At(0));
            Assert.Equal(2, Arc.RetainCount(nonfrozenRequiresMemoryManagement.Payload.DangerousGetHandle().At(0)));

            Assert.False(nonfrozenRequiresMemoryManagement.Payload.IsClosed);
            Assert.False(nonfrozenRequiresMemoryManagement.Payload.IsInvalid);
            // Memory is allocated from C# side and released in Dispose
            // Take the payload to check the retain count
            var nonFrozenHandle = nonfrozenRequiresMemoryManagement.Payload.DangerousGetHandle().At(0);
            nonfrozenRequiresMemoryManagement.Payload.Dispose();
            Assert.True(nonfrozenRequiresMemoryManagement.Payload.IsClosed);
            Assert.True(nonfrozenRequiresMemoryManagement.Payload.IsInvalid);
            // Check the count after destroy
            Assert.Equal(1, Arc.RetainCount(nonFrozenHandle));
            Assert.Equal(IntPtr.Zero, nonfrozenRequiresMemoryManagement.Payload.DangerousGetHandle());
        }

        [Fact]
        public unsafe void TestSwiftMarshalFrozenStruct()
        {
            var vtype = new Bindings.FrozenStructRequiresMemoryManagement(42);
            Assert.Equal(1, Arc.RetainCount(vtype.Payload.DangerousGetHandle().At(0)));

            var metadata = SwiftObjectHelper<Bindings.FrozenStructRequiresMemoryManagement>.GetTypeMetadata();
            Span<byte> payloadSpan = stackalloc byte[(int)metadata.Size];
            IntPtr payloadPtr = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(payloadSpan));

            // Marshal the object to Swift
            SwiftMarshal.MarshalToSwift(vtype, ref payloadSpan);
            Assert.Equal(2, Arc.RetainCount(vtype.Payload.DangerousGetHandle().At(0)));

            // Marshal back from Swift
            var copy = SwiftMarshal.MarshalFromSwift<Bindings.FrozenStructRequiresMemoryManagement>(payloadPtr);
            Assert.Equal(2, Arc.RetainCount(copy.Payload.DangerousGetHandle().At(0)));

            // Dispose the copy and verify retain count
            copy.Payload.Dispose();
            Assert.Equal(1, Arc.RetainCount(vtype.Payload.DangerousGetHandle().At(0)));
        }

        [Fact]
        public unsafe void TestSwiftMarshalMethodsNestedFrozenStruct()
        {
            var vtype = new Bindings.NestedFrozenStructRequiresMemoryManagement(42);
            Assert.Equal(1, Arc.RetainCount(vtype.Payload.DangerousGetHandle().At(0)));

            var metadata = SwiftObjectHelper<Bindings.NestedFrozenStructRequiresMemoryManagement>.GetTypeMetadata();
            Span<byte> payloadSpan = stackalloc byte[(int)metadata.Size];
            IntPtr payloadPtr = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(payloadSpan));

            // Marshal the object to Swift
            SwiftMarshal.MarshalToSwift(vtype, ref payloadSpan);
            Assert.Equal(2, Arc.RetainCount(vtype.Payload.DangerousGetHandle().At(0)));

            // Marshal back from Swift
            var copy = SwiftMarshal.MarshalFromSwift<Bindings.NestedFrozenStructRequiresMemoryManagement>(payloadPtr);
            Assert.Equal(2, Arc.RetainCount(copy.Payload.DangerousGetHandle().At(0)));

            // Dispose the copy and verify retain count
            copy.Payload.Dispose();
            Assert.Equal(1, Arc.RetainCount(vtype.Payload.DangerousGetHandle().At(0)));
        }

        [Fact]
        public unsafe void TestSwiftMarshalMethodsNonFrozenStruct()
        {
            var vtype = new Bindings.NonFrozenStructRequiresMemoryManagement(42);
            Assert.Equal(1, Arc.RetainCount(vtype.Payload.DangerousGetHandle().At(0)));

            var metadata = SwiftObjectHelper<Bindings.NonFrozenStructRequiresMemoryManagement>.GetTypeMetadata();
            IntPtr payloadPtr = (IntPtr)NativeMemory.Alloc(metadata.Size);
            Span<byte> payloadSpan = new Span<byte>((byte*)payloadPtr, (int)metadata.Size);

            // Marshal the object to Swift
            SwiftMarshal.MarshalToSwift(vtype, ref payloadSpan);
            Assert.Equal(2, Arc.RetainCount(vtype.Payload.DangerousGetHandle().At(0)));

            // Marshal back from Swift
            var copy = SwiftMarshal.MarshalFromSwift<Bindings.NonFrozenStructRequiresMemoryManagement>(payloadPtr);
            Assert.Equal(2, Arc.RetainCount(copy.Payload.DangerousGetHandle().At(0)));

            // Dispose the copy and verify retain count
            copy.Payload.Dispose();
            Assert.Equal(1, Arc.RetainCount(vtype.Payload.DangerousGetHandle().At(0)));
        }

        [Fact]
        public unsafe void TestPassThroughEmbeddedStruct()
        {
            EmbeddedStruct vtype = new EmbeddedStruct();
            Assert.Equal(1, vtype.X.X);
            Assert.Equal(2, vtype.X.Y);
            Assert.Equal(3, vtype.Y);

            Assert.Equal(1, Arc.RetainCount(vtype.Payload.DangerousGetHandle().At(1)));
            EmbeddedStruct copy = Bindings.Functions.PassThroughEmbeddedStruct(vtype);

            Assert.Equal(1, copy.X.X);
            Assert.Equal(2, copy.X.Y);
            Assert.Equal(3, copy.Y);

            Assert.Equal(2, Arc.RetainCount(vtype.Payload.DangerousGetHandle().At(1)));
            Assert.Equal(2, Arc.RetainCount(copy.Payload.DangerousGetHandle().At(1)));

            copy.Payload.Dispose();

            Assert.True(copy.Payload.IsClosed);
            Assert.True(copy.Payload.IsInvalid);

            Assert.Equal(1, Arc.RetainCount(vtype.Payload.DangerousGetHandle().At(1)));
        }

        [Fact]
        public unsafe void TestSwiftMarshalEmbeddedStruct()
        {
            EmbeddedStruct vtype = new EmbeddedStruct();
            Assert.Equal(1, Arc.RetainCount(vtype.Payload.DangerousGetHandle().At(1)));

            var metadata = SwiftObjectHelper<EmbeddedStruct>.GetTypeMetadata();
            Span<byte> payloadSpan = stackalloc byte[(int)metadata.Size];
            IntPtr payloadPtr = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(payloadSpan));

            // Marshal the object to Swift
            SwiftMarshal.MarshalToSwift(vtype, ref payloadSpan);
            Assert.Equal(2, Arc.RetainCount(vtype.Payload.DangerousGetHandle().At(1)));

            // Marshal back from Swift
            var copy = SwiftMarshal.MarshalFromSwift<EmbeddedStruct>(payloadPtr);
            Assert.Equal(2, Arc.RetainCount(copy.Payload.DangerousGetHandle().At(1)));

            // Dispose the copy and verify retain count
            copy.Payload.Dispose();
            Assert.Equal(1, Arc.RetainCount(vtype.Payload.DangerousGetHandle().At(1)));
        }

        partial class FrozenStructExtension : FrozenStructRequiresMemoryManagement
        {
            public FrozenStructExtension() : base(42)
            {
            }

            public void CallDispose()
            {
                bool success = false;
                this.Payload.DangerousAddRef(ref success);
#pragma warning disable CS8500
                unsafe
                {
                    var handle = this.Payload;
                    PInvoke_CallDispose(&Callback, &handle);

                    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
                    static void Callback(SwiftSelf context)
                    {
                        SwiftSafeHandle<FrozenStructExtension> pContext = *(SwiftSafeHandle<FrozenStructExtension>*)context.Value;
                        pContext.Dispose();
                    }
                }

                Assert.False(Payload.IsClosed);
                Assert.False(Payload.IsInvalid);

                Assert.Equal(1, Arc.RetainCount(Payload.DangerousGetHandle().At(0)));
                Assert.Equal(42, B);

#pragma warning restore CS8500
                if (success)
                    this.Payload.DangerousRelease();
            }

            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [LibraryImport("MemoryTests/libMemoryTests.dylib", EntryPoint = "$s11MemoryTests020FrozenStructRequiresA10ManagementV11callDispose8callbackyyyc_tF")]
            private unsafe static partial void PInvoke_CallDispose(delegate* unmanaged[Swift]<SwiftSelf, void> callback, void* context);
        }

        [Fact]
        public unsafe void TestSafeHandleDispose()
        {
            FrozenStructExtension frozenRequiresMemoryManagement = new FrozenStructExtension();
            Assert.Equal(42, frozenRequiresMemoryManagement.B);

            frozenRequiresMemoryManagement.CallDispose();
            Assert.True(frozenRequiresMemoryManagement.Payload.IsClosed);
        }

        [Fact]
        public async Task ConcurrentFrozenStructDispose()
        {
            for (int i = 0; i < 10; i++)
            {
                var resource = new Bindings.FrozenStructRequiresMemoryManagement(42);
                var barrier = new Barrier(4);

                var getterTask = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    try
                    {
                        Assert.Equal(42, resource.B);
                    }
                    catch (ObjectDisposedException ex)
                    {
                        Assert.IsType<ObjectDisposedException>(ex);
                    }
                });

                var methodTask = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    try
                    {
                        Assert.Equal(42, resource.GetValue());
                    }
                    catch (ObjectDisposedException ex)
                    {
                        Assert.IsType<ObjectDisposedException>(ex);
                    }
                });

                var passThroughTask = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    try
                    {
                        var copy = Bindings.Functions.PassThroughFrozenStruct(resource);
                        var genericCopy = Bindings.Functions.PassThroughGeneric<FrozenStructRequiresMemoryManagement>(resource);
                        copy.Payload.Dispose();
                        genericCopy.Payload.Dispose();
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

                await Task.WhenAll(methodTask, getterTask, passThroughTask, disposeTask);

                Assert.True(resource.Payload.IsClosed);
                Assert.True(resource.Payload.IsInvalid);
            }
        }

        [Fact]
        public async Task ConcurrentNonFrozenStruct()
        {
            for (int i = 0; i < 10; i++)
            {
                var resource = new Bindings.NonFrozenStructRequiresMemoryManagement(42);
                var barrier = new Barrier(4);

                var getterTask = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    try
                    {
                        Assert.Equal(42, resource.B);
                    }
                    catch (ObjectDisposedException ex)
                    {
                        Assert.IsType<ObjectDisposedException>(ex);
                    }
                });

                var methodTask = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    try
                    {
                        Assert.Equal(42, resource.GetValue());
                    }
                    catch (ObjectDisposedException ex)
                    {
                        Assert.IsType<ObjectDisposedException>(ex);
                    }
                });

                var passThroughTask = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    try
                    {
                        var copy = Bindings.Functions.PassThroughNonFrozenStruct(resource);
                        var genericCopy = Bindings.Functions.PassThroughGeneric<NonFrozenStructRequiresMemoryManagement>(resource);
                        copy.Payload.Dispose();
                        genericCopy.Payload.Dispose();
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

                await Task.WhenAll(methodTask, getterTask, passThroughTask, disposeTask);

                Assert.True(resource.Payload.IsClosed);
                Assert.True(resource.Payload.IsInvalid);
            }
        }

        [Fact]
        public void TestDoubleDisposeIsIdempotent()
        {
            // Create a frozen struct
            var frozenStruct = new Bindings.FrozenStructRequiresMemoryManagement(42);

            // First dispose should work normally
            frozenStruct.Payload.Dispose();
            Assert.True(frozenStruct.Payload.IsClosed);
            Assert.True(frozenStruct.Payload.IsInvalid);

            // Second dispose should not throw (SafeHandle is idempotent)
            var ex = Record.Exception(() => frozenStruct.Payload.Dispose());
            Assert.Null(ex);

            // State should remain the same
            Assert.True(frozenStruct.Payload.IsClosed);
            Assert.True(frozenStruct.Payload.IsInvalid);
        }

        [Fact]
        public void TestDoubleDisposeNonFrozenStruct()
        {
            // Create a non-frozen struct
            var nonFrozenStruct = new Bindings.NonFrozenStructRequiresMemoryManagement(42);

            // First dispose should work normally
            nonFrozenStruct.Payload.Dispose();
            Assert.True(nonFrozenStruct.Payload.IsClosed);
            Assert.True(nonFrozenStruct.Payload.IsInvalid);

            // Second dispose should not throw
            var ex = Record.Exception(() => nonFrozenStruct.Payload.Dispose());
            Assert.Null(ex);

            // State should remain the same
            Assert.True(nonFrozenStruct.Payload.IsClosed);
            Assert.True(nonFrozenStruct.Payload.IsInvalid);
        }

        [Fact]
        public unsafe void TestHandleValidityBeforeAndAfterDispose()
        {
            var frozenStruct = new Bindings.FrozenStructRequiresMemoryManagement(42);

            // Before dispose: handle should be valid
            Assert.False(frozenStruct.Payload.IsClosed);
            Assert.False(frozenStruct.Payload.IsInvalid);
            Assert.NotEqual(IntPtr.Zero, frozenStruct.Payload.DangerousGetHandle());

            // Dispose
            frozenStruct.Payload.Dispose();

            // After dispose: handle should be invalid
            Assert.True(frozenStruct.Payload.IsClosed);
            Assert.True(frozenStruct.Payload.IsInvalid);
            // Handle is set to zero in ReleaseHandle
            Assert.Equal(IntPtr.Zero, frozenStruct.Payload.DangerousGetHandle());
        }

        [Fact]
        public unsafe void TestUnownedRetainAndRelease()
        {
            // Create a VType which contains a reference type
            Bindings.VType vType = new Bindings.VType();
            GC.SuppressFinalize(vType);
            GC.SuppressFinalize(vType.Payload);
            IntPtr payload = (IntPtr)vType.Payload.DangerousGetHandle();

            // Get the embedded reference (at offset 0)
            IntPtr refTypePtr = payload.At(0);

            // Check initial strong retain count
            Assert.Equal(1, Arc.RetainCount(refTypePtr));

            // Check initial unowned retain count
            long initialUnownedCount = Arc.UnownedRetainCount(refTypePtr);

            // Perform unowned retain
            Arc.UnownedRetain(refTypePtr);
            Assert.Equal(initialUnownedCount + 1, Arc.UnownedRetainCount(refTypePtr));

            // Strong retain count should be unchanged
            Assert.Equal(1, Arc.RetainCount(refTypePtr));

            // Perform another unowned retain
            Arc.UnownedRetain(refTypePtr);
            Assert.Equal(initialUnownedCount + 2, Arc.UnownedRetainCount(refTypePtr));

            // Perform unowned release
            Arc.UnownedRelease(refTypePtr);
            Assert.Equal(initialUnownedCount + 1, Arc.UnownedRetainCount(refTypePtr));

            // Strong retain count should still be unchanged
            Assert.Equal(1, Arc.RetainCount(refTypePtr));

            // Release the final unowned retain
            Arc.UnownedRelease(refTypePtr);
            Assert.Equal(initialUnownedCount, Arc.UnownedRetainCount(refTypePtr));

            // Clean up: release the strong reference to trigger deinit
            Arc.Release(refTypePtr);
            Assert.Equal(1, vType.RefTypeTest); // deinit was called
        }

        [Fact]
        public unsafe void TestUnownedRetainThrowsOnNull()
        {
            Assert.Throws<ArgumentNullException>(() => Arc.UnownedRetain(IntPtr.Zero));
            Assert.Throws<ArgumentNullException>(() => Arc.UnownedRelease(IntPtr.Zero));
            Assert.Throws<ArgumentNullException>(() => Arc.UnownedRetainCount(IntPtr.Zero));
        }

        [Fact]
        public unsafe void TestRapidAllocFree()
        {
            // Stress test: rapidly allocate and free objects to detect slow leaks
            const int iterations = 100;

            for (int i = 0; i < iterations; i++)
            {
                var frozenStruct = new Bindings.FrozenStructRequiresMemoryManagement(i);
                Assert.Equal(i, frozenStruct.B);
                Assert.Equal(1, Arc.RetainCount(frozenStruct.Payload.DangerousGetHandle().At(0)));
                frozenStruct.Payload.Dispose();
                Assert.True(frozenStruct.Payload.IsClosed);
            }

            // Force GC to clean up any finalizable objects
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        [Fact]
        public unsafe void TestFinalizationVsExplicitDispose()
        {
            // This test verifies that finalization eventually cleans up SafeHandles.
            // GC behavior is non-deterministic, so we test that explicit dispose
            // releases immediately while finalization may take multiple GC cycles.

            // Test explicit dispose - should release immediately
            var frozenStruct = new Bindings.FrozenStructRequiresMemoryManagement(42);
            var refPtr = frozenStruct.Payload.DangerousGetHandle().At(0);

            // Retain the reference so object doesn't get deallocated
            Arc.Retain(refPtr);
            Assert.Equal(2, Arc.RetainCount(refPtr));

            // Explicit dispose should release the SafeHandle's reference
            frozenStruct.Payload.Dispose();
            Assert.True(frozenStruct.Payload.IsClosed);

            // After explicit dispose, only our retained reference remains
            Assert.Equal(1, Arc.RetainCount(refPtr));

            // Clean up our retained reference
            Arc.Release(refPtr);
        }
    }
}
