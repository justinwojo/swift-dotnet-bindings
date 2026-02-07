// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Foundation;
using Swift;
using Swift.CryptoSwift;
using Swift.Runtime;
using UIKit;

namespace CryptoSwiftTestApp;

public static class TestLogger
{
    private static readonly object _lock = new();
    private static readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private static readonly StringBuilder _fullLog = new();

    public static void Info(string message) => Log("INFO", message);
    public static void Pass(string message) => Log("PASS", message);
    public static void Fail(string message) => Log("FAIL", message);

    public static void Log(string prefix, string message)
    {
        var timestamp = _stopwatch.Elapsed.TotalSeconds;
        var line = $"[{timestamp:F3}s] [{prefix}] {message}";
        lock (_lock)
        {
            Console.WriteLine(line);
            _fullLog.AppendLine(line);
        }
    }

    public static string GetFullLog()
    {
        lock (_lock) { return _fullLog.ToString(); }
    }

    public static void Clear()
    {
        lock (_lock) { _fullLog.Clear(); }
    }
}

public class TestResults
{
    public int Passed { get; private set; }
    public int Failed { get; private set; }
    public List<string> FailedTests { get; } = new();

    public void Pass(string testName)
    {
        Passed++;
        TestLogger.Pass(testName);
    }

    public void Fail(string testName, string reason)
    {
        Failed++;
        FailedTests.Add($"{testName}: {reason}");
        TestLogger.Fail($"{testName}: {reason}");
    }

    public bool AllPassed => Failed == 0;
}

public class Application
{
    static void Main(string[] args)
    {
        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), ResolveBundledFramework);
        UIApplication.Main(args, null, typeof(AppDelegate));
    }

    static IntPtr ResolveBundledFramework(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName is "CryptoSwift" or "SwiftBindings")
        {
            var frameworkPath = $"@rpath/{libraryName}.framework/{libraryName}";
            if (NativeLibrary.TryLoad(frameworkPath, out var handle))
            {
                TestLogger.Info($"Resolved {libraryName} -> {frameworkPath}");
                return handle;
            }
            TestLogger.Fail($"Failed to resolve {libraryName} at {frameworkPath}");
        }
        return IntPtr.Zero;
    }
}

[Register("AppDelegate")]
public class AppDelegate : UIApplicationDelegate
{
    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
    {
        Window = new UIWindow(UIScreen.MainScreen.Bounds);
        Window.BackgroundColor = UIColor.White;
        var vc = new MainViewController();
        vc.ModalPresentationStyle = UIModalPresentationStyle.FullScreen;
        Window.RootViewController = vc;
        Window.MakeKeyAndVisible();
        return true;
    }
}

public class MainViewController : UIViewController
{
    private readonly TestResults _results = new();

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        RunAllTests();
    }

    private void RunAllTests()
    {
        TestLogger.Clear();
        TestLogger.Info("=== CRYPTOSWIFT BINDING VALIDATION SUITE ===");

        // Run tests in order: safe static → enum/property → instance (may crash)
        // Each test has its own try-catch, but SIGSEGV from Mono JIT bugs
        // will kill the process, so put risky tests last.
        TestDigestStaticSha256();   // Static — safe
        TestDigestStaticMd5();      // Static — safe
        TestDigestStaticSha1();     // Static — safe
        TestEnumTypes();            // Enum case construction — safe
        TestPropertyAccess();       // Static + instance properties — may fail
        TestSha2Instance();         // Instance init + method — may fail (non-blittable enum param)
        TestHmacSha256();           // Instance init + method — may crash
        TestChaCha20RoundTrip();    // Instance init + method — may crash
        TestRsaEncryptDecrypt();    // Instance init + method — may crash (key gen)
        TestMd5Instance();          // Instance init + method — known SIGSEGV, put last

        TestLogger.Info("=== TEST SUMMARY ===");
        TestLogger.Info($"Passed: {_results.Passed}, Failed: {_results.Failed}");

        if (_results.AllPassed)
        {
            Console.WriteLine("TEST SUCCESS");
            TestLogger.Info("=== VALIDATION PASSED ===");
        }
        else
        {
            Console.WriteLine($"TEST FAILURE: {_results.Failed} tests failed");
            TestLogger.Info("=== VALIDATION FAILED ===");
            foreach (var f in _results.FailedTests)
                TestLogger.Info($"  - {f}");
        }
    }

    // CryptoSwift Init() methods are instance methods that don't use 'self' —
    // they're Swift initializers projected as instance methods. We need a blank
    // instance just to call Init() on. The returned value is properly constructed.
    static T Uninit<T>() where T : class =>
        (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    static string ToHex(IReadOnlyList<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Count * 2);
        foreach (var b in bytes)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    // --- Test 1: Digest.Sha256 static convenience ---
    void TestDigestStaticSha256()
    {
        TestLogger.Info("Test: Digest.Sha256 static method...");
        try
        {
            // SHA-256 of "hello" = 2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824
            var input = Encoding.UTF8.GetBytes("hello");
            var hash = Digest.Sha256(input);
            var hex = ToHex(hash);
            TestLogger.Info($"  SHA256('hello') = {hex}");

            if (hex == "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824")
                _results.Pass("Digest.Sha256 static");
            else
                _results.Fail("Digest.Sha256 static", $"Wrong hash: {hex}");
        }
        catch (Exception ex)
        {
            _results.Fail("Digest.Sha256 static", ex.Message);
        }
    }

    // --- Test 2: Digest.Md5 static convenience ---
    void TestDigestStaticMd5()
    {
        TestLogger.Info("Test: Digest.Md5 static method...");
        try
        {
            // MD5 of "hello" = 5d41402abc4b2a76b9719d911017c592
            var input = Encoding.UTF8.GetBytes("hello");
            var hash = Digest.Md5(input);
            var hex = ToHex(hash);
            TestLogger.Info($"  MD5('hello') = {hex}");

            if (hex == "5d41402abc4b2a76b9719d911017c592")
                _results.Pass("Digest.Md5 static");
            else
                _results.Fail("Digest.Md5 static", $"Wrong hash: {hex}");
        }
        catch (Exception ex)
        {
            _results.Fail("Digest.Md5 static", ex.Message);
        }
    }

    // --- Test 3: Digest.Sha1 static convenience ---
    void TestDigestStaticSha1()
    {
        TestLogger.Info("Test: Digest.Sha1 static method...");
        try
        {
            // SHA-1 of "hello" = aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d
            var input = Encoding.UTF8.GetBytes("hello");
            var hash = Digest.Sha1(input);
            var hex = ToHex(hash);
            TestLogger.Info($"  SHA1('hello') = {hex}");

            if (hex == "aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d")
                _results.Pass("Digest.Sha1 static");
            else
                _results.Fail("Digest.Sha1 static", $"Wrong hash: {hex}");
        }
        catch (Exception ex)
        {
            _results.Fail("Digest.Sha1 static", ex.Message);
        }
    }

    // --- Test 4: SHA2 instance with variant ---
    void TestSha2Instance()
    {
        TestLogger.Info("Test: SHA2 instance Calculate...");
        try
        {
            var sha2 = Uninit<SHA2>().Init(SHA2.Variant.Sha256);
            var input = Encoding.UTF8.GetBytes("hello");
            var hash = sha2.Calculate(input);
            var hex = ToHex(hash);
            TestLogger.Info($"  SHA2.Calculate('hello') = {hex}");

            if (hex == "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824")
                _results.Pass("SHA2 instance Calculate");
            else
                _results.Fail("SHA2 instance Calculate", $"Wrong hash: {hex}");
        }
        catch (Exception ex)
        {
            _results.Fail("SHA2 instance Calculate", ex.Message);
        }
    }

    // --- Test 5: MD5 instance ---
    void TestMd5Instance()
    {
        TestLogger.Info("Test: MD5 instance Calculate...");
        try
        {
            var md5 = Uninit<MD5>().Init();
            var input = Encoding.UTF8.GetBytes("hello");
            var hash = md5.Calculate(input);
            var hex = ToHex(hash);
            TestLogger.Info($"  MD5.Calculate('hello') = {hex}");

            if (hex == "5d41402abc4b2a76b9719d911017c592")
                _results.Pass("MD5 instance Calculate");
            else
                _results.Fail("MD5 instance Calculate", $"Wrong hash: {hex}");
        }
        catch (Exception ex)
        {
            _results.Fail("MD5 instance Calculate", ex.Message);
        }
    }

    // --- Test 6: HMAC-SHA256 ---
    void TestHmacSha256()
    {
        TestLogger.Info("Test: HMAC-SHA256...");
        try
        {
            var key = Encoding.UTF8.GetBytes("secret-key");
            var hmac = Uninit<HMAC>().Init(key, HMAC.Variant.Sha256);
            var message = Encoding.UTF8.GetBytes("hello");
            var mac = hmac.Authenticate(message);
            var hex = ToHex(mac);
            TestLogger.Info($"  HMAC-SHA256('hello', 'secret-key') = {hex}");

            // Verify it's 32 bytes (SHA-256 output)
            if (mac.Count == 32)
                _results.Pass("HMAC-SHA256");
            else
                _results.Fail("HMAC-SHA256", $"Expected 32 bytes, got {mac.Count}");
        }
        catch (Exception ex)
        {
            _results.Fail("HMAC-SHA256", ex.Message);
        }
    }

    // --- Test 7: ChaCha20 encrypt/decrypt round-trip ---
    void TestChaCha20RoundTrip()
    {
        TestLogger.Info("Test: ChaCha20 encrypt/decrypt round-trip...");
        try
        {
            // ChaCha20 needs 32-byte key, 12-byte nonce
            var key = new byte[32];
            var iv = new byte[12];
            for (int i = 0; i < key.Length; i++) key[i] = (byte)(i + 1);
            for (int i = 0; i < iv.Length; i++) iv[i] = (byte)(i + 0x10);

            var chacha = Uninit<ChaCha20>().Init(key, iv);
            var plaintext = Encoding.UTF8.GetBytes("CryptoSwift binding test!");
            var ciphertext = chacha.Encrypt(plaintext);
            TestLogger.Info($"  Encrypted {plaintext.Length} bytes -> {ciphertext.Count} bytes");

            // Decrypt with a fresh instance (ChaCha20 is stateful)
            var chacha2 = Uninit<ChaCha20>().Init(key, iv);
            var decrypted = chacha2.Decrypt(ciphertext.ToArray());
            var decryptedText = Encoding.UTF8.GetString(decrypted.ToArray());
            TestLogger.Info($"  Decrypted: '{decryptedText}'");

            if (decryptedText == "CryptoSwift binding test!")
                _results.Pass("ChaCha20 round-trip");
            else
                _results.Fail("ChaCha20 round-trip", $"Got '{decryptedText}'");
        }
        catch (Exception ex)
        {
            _results.Fail("ChaCha20 round-trip", ex.Message);
        }
    }

    // --- Test 8: RSA encrypt/decrypt ---
    void TestRsaEncryptDecrypt()
    {
        TestLogger.Info("Test: RSA encrypt/decrypt...");
        try
        {
            // Generate a 1024-bit key (small for speed in tests)
            var rsa = Uninit<RSA>().Init((IntPtr)1024);
            TestLogger.Info($"  RSA key generated, keySize={rsa.KeySize}");

            var plaintext = Encoding.UTF8.GetBytes("test");
            var ciphertext = rsa.Encrypt(plaintext);
            TestLogger.Info($"  Encrypted {plaintext.Length} bytes -> {ciphertext.Count} bytes");

            var decrypted = rsa.Decrypt(ciphertext.ToArray());
            var decryptedText = Encoding.UTF8.GetString(decrypted.ToArray());
            TestLogger.Info($"  Decrypted: '{decryptedText}'");

            if (decryptedText == "test")
                _results.Pass("RSA encrypt/decrypt");
            else
                _results.Fail("RSA encrypt/decrypt", $"Got '{decryptedText}'");
        }
        catch (Exception ex)
        {
            _results.Fail("RSA encrypt/decrypt", ex.Message);
        }
    }

    // --- Test 9: Enum type access ---
    void TestEnumTypes()
    {
        TestLogger.Info("Test: Enum type access...");
        try
        {
            // SHA2 variants
            var sha256 = SHA2.Variant.Sha256;
            var sha512 = SHA2.Variant.Sha512;
            TestLogger.Info($"  SHA2.Variant.Sha256 created: {sha256 != null}");
            TestLogger.Info($"  SHA2.Variant.Sha512 created: {sha512 != null}");

            // HMAC variants
            var hmacSha256 = HMAC.Variant.Sha256;
            TestLogger.Info($"  HMAC.Variant.Sha256 created: {hmacSha256 != null}");

            // Padding types
            var noPadding = Padding.NoPadding;
            var pkcs7 = Padding.Pkcs7;
            TestLogger.Info($"  Padding.NoPadding created: {noPadding != null}");
            TestLogger.Info($"  Padding.Pkcs7 created: {pkcs7 != null}");

            if (sha256 != null && sha512 != null && hmacSha256 != null &&
                noPadding != null && pkcs7 != null)
                _results.Pass("Enum type access");
            else
                _results.Fail("Enum type access", "Some enum values were null");
        }
        catch (Exception ex)
        {
            _results.Fail("Enum type access", ex.Message);
        }
    }

    // --- Test 10: Property access ---
    void TestPropertyAccess()
    {
        TestLogger.Info("Test: Property access...");
        try
        {
            // AES.BlockSize (static) should be 16
            var blockSize = AES.BlockSize;
            TestLogger.Info($"  AES.BlockSize = {blockSize}");

            // SHA2 instance properties
            var sha2 = Uninit<SHA2>().Init(SHA2.Variant.Sha256);
            var digestLength = sha2.DigestLength;
            var shaBlockSize = sha2.BlockSize;
            TestLogger.Info($"  SHA2-256 DigestLength = {digestLength}");
            TestLogger.Info($"  SHA2-256 BlockSize = {shaBlockSize}");

            if ((long)blockSize == 16 && (long)digestLength == 32 && (long)shaBlockSize == 64)
                _results.Pass("Property access");
            else
                _results.Fail("Property access", $"AES.BlockSize={blockSize}, DigestLength={digestLength}, BlockSize={shaBlockSize}");
        }
        catch (Exception ex)
        {
            _results.Fail("Property access", ex.Message);
        }
    }
}
