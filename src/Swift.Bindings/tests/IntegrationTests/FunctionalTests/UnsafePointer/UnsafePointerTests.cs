// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Security.Cryptography;
using Swift;
using Swift.Runtime;
using Swift.UnsafePointerTests;
using Xunit;

namespace BindingsGeneration.FunctionalTests
{
    public class UnsafePointerTests : IClassFixture<UnsafePointerTests.TestFixture>
    {
        private readonly TestFixture _fixture;

        public UnsafePointerTests(TestFixture fixture)
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

        private static unsafe void ChaCha20Poly1305Encrypt(
            ReadOnlySpan<byte> key,
            ReadOnlySpan<byte> nonce,
            ReadOnlySpan<byte> plaintext,
            Span<byte> ciphertext,
            Span<byte> tag,
            ReadOnlySpan<byte> aad)
        {
            fixed (void* keyPtr = key)
            fixed (void* noncePtr = nonce)
            fixed (void* plaintextPtr = plaintext)
            fixed (byte* ciphertextPtr = ciphertext)
            fixed (byte* tagPtr = tag)
            fixed (void* aadPtr = aad)
            {
                const int Success = 1;

                // Swift pointer types (UnsafeMutableRawPointer, UnsafeMutablePointer<T>, etc.)
                // are projected to IntPtr in the generated bindings
                int result = Swift.UnsafePointerTests.Functions.AppleCryptoNative_ChaCha20Poly1305Encrypt(
                                    (nint)keyPtr, key.Length,
                                    (nint)noncePtr, nonce.Length,
                                    (nint)plaintextPtr, plaintext.Length,
                                    (nint)ciphertextPtr, ciphertext.Length,
                                    (nint)tagPtr, tag.Length,
                                    (nint)aadPtr, aad.Length);

                if (result != Success)
                {
                    Debug.Assert(result == 0);
                    Console.WriteLine("Encryption failed");
                }
            }
        }

        private static unsafe void ChaCha20Poly1305Decrypt(
            ReadOnlySpan<byte> key,
            ReadOnlySpan<byte> nonce,
            ReadOnlySpan<byte> ciphertext,
            ReadOnlySpan<byte> tag,
            Span<byte> plaintext,
            ReadOnlySpan<byte> aad)
        {
            fixed (void* keyPtr = key)
            fixed (void* noncePtr = nonce)
            fixed (void* ciphertextPtr = ciphertext)
            fixed (void* tagPtr = tag)
            fixed (byte* plaintextPtr = plaintext)
            fixed (void* aadPtr = aad)
            {
                const int Success = 1;
                const int AuthTagMismatch = -1;

                // Swift pointer types are projected to IntPtr in the generated bindings
                int result = Swift.UnsafePointerTests.Functions.AppleCryptoNative_ChaCha20Poly1305Decrypt(
                    (nint)keyPtr, key.Length,
                    (nint)noncePtr, nonce.Length,
                    (nint)ciphertextPtr, ciphertext.Length,
                    (nint)tagPtr, tag.Length,
                    (nint)plaintextPtr, plaintext.Length,
                    (nint)aadPtr, aad.Length);

                if (result != Success)
                {
                    CryptographicOperations.ZeroMemory(plaintext);

                    if (result == AuthTagMismatch)
                    {
                        throw new AuthenticationTagMismatchException();
                    }
                    else
                    {
                        Debug.Assert(result == 0);
                        throw new CryptographicException();
                    }
                }
            }
        }

        /// <summary>
        /// Verifies that UnsafePointer&lt;T&gt; (immutable) generates IntPtr, not AnyType.
        /// This was a suspected generator bug — this test ensures it stays correct.
        /// </summary>
        [Fact]
        public static void TestImmutableUnsafePointerGeneratesIntPtr()
        {
            // The generated binding for readImmutablePointerValue should accept nint (IntPtr),
            // not be skipped as AnyType. If this compiles, the type is correct.
            // We verify the method exists and has the expected signature via reflection.
            var method = typeof(Swift.UnsafePointerTests.Functions)
                .GetMethod("ReadImmutablePointerValue");
            Assert.NotNull(method);

            // Verify parameter type is nint (IntPtr), not AnyType
            var parameters = method!.GetParameters();
            Assert.Single(parameters);
            Assert.Equal(typeof(nint), parameters[0].ParameterType);

            // Verify return type is Int32
            Assert.Equal(typeof(int), method.ReturnType);
        }

        [Fact]
        public static void TestUnsafePointerCryptoKit()
        {
            byte[] key = RandomNumberGenerator.GetBytes(32); // Generate a 256-bit key
            byte[] nonce = RandomNumberGenerator.GetBytes(12); // Generate a 96-bit nonce
            byte[] plaintext = System.Text.Encoding.UTF8.GetBytes("Hello, World!");
            byte[] aad = System.Text.Encoding.UTF8.GetBytes("Additional Authenticated Data");

            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[16]; // ChaCha20Poly1305 tag size
            Console.WriteLine($"Plaintext: {BitConverter.ToString(plaintext)}");

            ChaCha20Poly1305Encrypt(
                key,
                nonce,
                plaintext,
                ciphertext,
                tag,
                aad);

            Console.WriteLine($"Ciphertext: {BitConverter.ToString(ciphertext)}");
            Console.WriteLine($"Tag: {BitConverter.ToString(tag)}");

            Array.Clear(plaintext, 0, plaintext.Length);

            ChaCha20Poly1305Decrypt(
                key,
                nonce,
                ciphertext,
                tag,
                plaintext,
                aad
            );

            string decryptedMessage = System.Text.Encoding.UTF8.GetString(plaintext);
            Assert.Equal("Hello, World!", decryptedMessage);
        }
    }
}
