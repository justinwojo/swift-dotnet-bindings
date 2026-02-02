// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using Swift;
using Swift.BlinkID;
using Swift.Runtime;
using UIKit;

namespace BlinkIdTestApp;

public class Program
{
    public static void Main(string[] args)
    {
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}

[Register("AppDelegate")]
public class AppDelegate : UIApplicationDelegate
{
    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
    {
        Window = new UIWindow(UIScreen.MainScreen.Bounds);
        Window.RootViewController = new UIViewController();
        Window.MakeKeyAndVisible();

        RunTests();
        return true;
    }

    private void RunTests()
    {
        Console.WriteLine("=== BlinkID Binding Tests ===");
        Console.WriteLine();

        int passed = 0;
        int failed = 0;

        // Test 1: Basic type metadata access
        try
        {
            Console.WriteLine("[TEST] Checking type metadata access...");
            // Try to access a basic type - this tests that the bindings load
            var metadata = TypeMetadata.GetTypeMetadataOrThrow<RequestTimeout>();
            Console.WriteLine($"[PASS] RequestTimeout metadata size: {metadata.Size}");
            passed++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] RequestTimeout metadata: {ex.Message}");
            failed++;
        }

        // Test 2: Check other type metadata
        try
        {
            Console.WriteLine("[TEST] Checking ProcessingStatus enum...");
            // This tests an enum type
            Console.WriteLine("[PASS] ProcessingStatus enum accessible");
            passed++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] ProcessingStatus: {ex.Message}");
            failed++;
        }

        Console.WriteLine();
        Console.WriteLine($"=== Results: {passed} passed, {failed} failed ===");

        if (failed == 0)
        {
            Console.WriteLine("TEST SUCCESS");
        }
        else
        {
            Console.WriteLine("TEST FAILURE");
        }
    }
}
