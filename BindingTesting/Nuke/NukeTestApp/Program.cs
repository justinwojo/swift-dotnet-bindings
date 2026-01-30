// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.InteropServices;
using Swift.Runtime;
using UIKit;

namespace NukeTestApp;

public class Application
{
    static void Main(string[] args)
    {
        // Register resolver for bundled frameworks BEFORE any Swift types are accessed
        // Swift.Nuke types are compiled into this assembly, so register on executing assembly
        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), ResolveBundledFramework);

        UIApplication.Main(args, null, typeof(AppDelegate));
    }

    static IntPtr ResolveBundledFramework(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // Map bundled framework names to @rpath locations
        if (libraryName == "Nuke" || libraryName == "SwiftBindings")
        {
            var frameworkPath = $"@rpath/{libraryName}.framework/{libraryName}";
            if (NativeLibrary.TryLoad(frameworkPath, out var handle))
            {
                Console.WriteLine($"Resolved {libraryName} -> {frameworkPath}");
                return handle;
            }
            Console.WriteLine($"Failed to resolve {libraryName} at {frameworkPath}");
        }

        // Fall back to default resolution
        return IntPtr.Zero;
    }
}

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
    private UILabel? _resultLabel;
    private UIImageView? _imageView;

    public override bool PrefersStatusBarHidden() => false;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();

        // Ensure view fills the entire screen
        var screenBounds = UIScreen.MainScreen.Bounds;
        View!.Frame = screenBounds;
        View.BackgroundColor = UIColor.White;
        View.ClipsToBounds = false;

        // Extend content under status bar
        EdgesForExtendedLayout = UIRectEdge.All;
        ExtendedLayoutIncludesOpaqueBars = true;
        var screenWidth = screenBounds.Width;
        var screenHeight = screenBounds.Height;

        // Safe area estimates for modern iPhones (notch + home indicator)
        var safeTop = 60.0;    // Status bar + notch area
        var safeBottom = 34.0; // Home indicator area

        // Content dimensions
        var contentWidth = screenWidth - 40;
        var titleHeight = 30.0;
        var buttonHeight = 44.0;
        var spacing = 10.0;
        var resultLabelHeight = 180.0;
        var imageHeight = 250.0;

        // Total content height (title + 3 buttons + result label + image)
        var totalContentHeight = titleHeight + spacing + buttonHeight + spacing + buttonHeight + spacing + buttonHeight + spacing + resultLabelHeight + spacing + imageHeight;

        // Center content vertically in safe area
        var safeHeight = screenHeight - safeTop - safeBottom;
        var contentTop = safeTop + (safeHeight - totalContentHeight) / 2;
        var currentY = contentTop;

        var label = new UILabel
        {
            Text = "Nuke Binding Test",
            TextAlignment = UITextAlignment.Center,
            Frame = new CoreGraphics.CGRect(20, currentY, contentWidth, titleHeight)
        };
        View.AddSubview(label);
        currentY += titleHeight + spacing;

        var testButton = UIButton.FromType(UIButtonType.System);
        testButton.Frame = new CoreGraphics.CGRect(20, currentY, contentWidth, buttonHeight);
        testButton.SetTitle("Test Nuke Binding", UIControlState.Normal);
        testButton.TouchUpInside += TestNukeBinding;
        View.AddSubview(testButton);
        currentY += buttonHeight + spacing;

        var loadImageButton = UIButton.FromType(UIButtonType.System);
        loadImageButton.Frame = new CoreGraphics.CGRect(20, currentY, contentWidth, buttonHeight);
        loadImageButton.SetTitle("Load Image with Nuke", UIControlState.Normal);
        loadImageButton.TouchUpInside += LoadImageWithNuke;
        View.AddSubview(loadImageButton);
        currentY += buttonHeight + spacing;

        var stressTestButton = UIButton.FromType(UIButtonType.System);
        stressTestButton.Frame = new CoreGraphics.CGRect(20, currentY, contentWidth, buttonHeight);
        stressTestButton.SetTitle("Memory Stress Test", UIControlState.Normal);
        stressTestButton.TouchUpInside += RunMemoryStressTest;
        View.AddSubview(stressTestButton);
        currentY += buttonHeight + spacing;

        _resultLabel = new UILabel
        {
            Tag = 100,
            Text = "Running tests automatically...",
            TextAlignment = UITextAlignment.Center,
            Lines = 0,
            Font = UIFont.SystemFontOfSize(12),
            Frame = new CoreGraphics.CGRect(20, currentY, contentWidth, resultLabelHeight)
        };
        View.AddSubview(_resultLabel);
        currentY += resultLabelHeight + spacing;

        _imageView = new UIImageView
        {
            Frame = new CoreGraphics.CGRect(20, currentY, contentWidth, imageHeight),
            ContentMode = UIViewContentMode.ScaleAspectFit,
            BackgroundColor = UIColor.LightGray
        };
        View.AddSubview(_imageView);

        // Run image loading test automatically on startup
        LoadImageWithNuke(null, EventArgs.Empty);
    }

    private void TestNukeBinding(object? sender, EventArgs e)
    {
        try
        {
            var results = new System.Text.StringBuilder();
            results.AppendLine("Testing Nuke bindings...\n");

            // Test 1: Try a struct type first (ImageProcessingContext)
            results.AppendLine("1. Struct (ImageProcessingContext)...");
            try
            {
                var metadata = Swift.Runtime.SwiftObjectHelper<Swift.Nuke.ImageProcessingContext>.GetTypeMetadata();
                results.AppendLine($"   Size: {metadata.Size}");
            }
            catch (Exception ex)
            {
                results.AppendLine($"   Failed: {ex.Message}");
            }

            // Test 2: Try a class type (ImagePipeline)
            results.AppendLine("\n2. Class (ImagePipeline)...");
            try
            {
                var metadata = Swift.Runtime.SwiftObjectHelper<Swift.Nuke.ImagePipeline>.GetTypeMetadata();
                results.AppendLine($"   Size: {metadata.Size}");
            }
            catch (Exception ex)
            {
                results.AppendLine($"   Failed: {ex.Message}");
            }

            // Test 3: Try getting ImagePipeline.Shared
            results.AppendLine("\n3. ImagePipeline.Shared...");
            try
            {
                var pipeline = Swift.Nuke.ImagePipeline.Shared;
                results.AppendLine($"   Got pipeline!");
            }
            catch (Exception ex)
            {
                results.AppendLine($"   Failed: {ex.Message}");
            }

            // Test 4: Create a SwiftString
            results.AppendLine("\n4. Creating SwiftString...");
            try
            {
                var str = new Swift.SwiftString("https://example.com");
                results.AppendLine($"   Success!");
            }
            catch (Exception ex)
            {
                results.AppendLine($"   Failed: {ex.Message}");
            }

            _resultLabel!.Text = results.ToString();
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    private async void LoadImageWithNuke(object? sender, EventArgs e)
    {
        _resultLabel!.Text = "Loading image...";
        _imageView!.Image = null;

        try
        {
            var results = new System.Text.StringBuilder();
            results.AppendLine("Loading image with Nuke...\n");

            // Get the shared pipeline
            var pipeline = Swift.Nuke.ImagePipeline.Shared;
            results.AppendLine("1. Got ImagePipeline.Shared");
            _resultLabel.Text = results.ToString();

            // Create an ImageRequest with a URL string
            // With type conversions, ImageRequest constructor now accepts string directly
            var request = new Swift.Nuke.ImageRequest("https://picsum.photos/400/300");
            results.AppendLine("2. Created ImageRequest (using string directly)");
            _resultLabel.Text = results.ToString();

            // DIAGNOSTIC: Verify ImageRequest is actually initialized by accessing a property
            // If this crashes, the constructor didn't properly initialize the object
            Console.WriteLine("DIAGNOSTIC: About to access request.Description property...");
            try
            {
                var desc = request.Description;
                Console.WriteLine("DIAGNOSTIC: Got description object, calling ToString()...");
                var descStr = desc.ToString();
                Console.WriteLine($"DIAGNOSTIC: SUCCESS! description length={descStr.Length}");
                Console.WriteLine($"DIAGNOSTIC: {descStr.Substring(0, Math.Min(100, descStr.Length))}...");
                results.AppendLine($"   DIAGNOSTIC: description length={descStr.Length}");
                results.AppendLine($"   {descStr.Substring(0, Math.Min(80, descStr.Length))}...");
            }
            catch (Exception diagEx)
            {
                Console.WriteLine($"DIAGNOSTIC FAILED: {diagEx.GetType().Name}: {diagEx.Message}");
                results.AppendLine($"   DIAGNOSTIC FAILED: {diagEx.GetType().Name}");
                results.AppendLine($"   {diagEx.Message}");
            }
            _resultLabel.Text = results.ToString();

            // Call the async image method with ImageRequest
            results.AppendLine("3. Calling pipeline.Image()...");
            _resultLabel.Text = results.ToString();

            // DIAGNOSTIC: Print pointer info before async call
            Console.WriteLine($"DIAGNOSTIC: About to call pipeline.Image()");
            Console.WriteLine($"DIAGNOSTIC: request.Payload.DangerousGetHandle() = 0x{request.Payload.DangerousGetHandle():X}");
            Console.WriteLine($"DIAGNOSTIC: pipeline.Payload.DangerousGetHandle() = 0x{pipeline.Payload.DangerousGetHandle():X}");

            // Get and print the metadata size
            var metadata = Swift.Runtime.SwiftObjectHelper<Swift.Nuke.ImageRequest>.GetTypeMetadata();
            Console.WriteLine($"DIAGNOSTIC: ImageRequest metadata.Size = {metadata.Size}");
            results.AppendLine($"   ImageRequest size = {metadata.Size}");

            // Dump first N bytes of ImageRequest memory (based on actual size)
            var dumpSize = Math.Min(64, (int)metadata.Size);
            unsafe
            {
                byte* ptr = (byte*)request.Payload.DangerousGetHandle();
                Console.Write($"DIAGNOSTIC: ImageRequest bytes ({dumpSize}): ");
                for (int i = 0; i < dumpSize; i++)
                {
                    Console.Write($"{ptr[i]:X2} ");
                }
                Console.WriteLine();
            }

            // With ObjC type remapping, the method returns UIKit.UIImage directly
            UIKit.UIImage image = await pipeline.Image(request);
            results.AppendLine($"4. Got image!");
            results.AppendLine($"   Size: {image.Size.Width}x{image.Size.Height}");
            results.AppendLine($"   Scale: {image.CurrentScale}");
            Console.WriteLine($"=== TEST SUCCESS: Image loaded, size: {image.Size.Width}x{image.Size.Height} ===");

            // No conversion needed - it's already a UIKit.UIImage
            if (image != null)
            {
                _imageView.Image = image;
                results.AppendLine("5. Displayed image!");
            }
            else
            {
                results.AppendLine("5. Failed to get native image");
            }

            _resultLabel.Text = results.ToString();
        }
        catch (Exception ex)
        {
            var msg = new System.Text.StringBuilder();
            msg.AppendLine($"Error loading image:");
            msg.AppendLine($"{ex.GetType().Name}: {ex.Message}");

            var inner = ex.InnerException;
            while (inner != null)
            {
                msg.AppendLine($"\nInner: {inner.GetType().Name}");
                msg.AppendLine($"{inner.Message}");
                inner = inner.InnerException;
            }

            Console.WriteLine("=== IMAGE LOAD ERROR ===");
            Console.WriteLine(msg.ToString());
            Console.WriteLine($"Stack trace: {ex}");
            Console.WriteLine("========================");

            _resultLabel!.Text = msg.ToString();
        }
    }

    private void RunMemoryStressTest(object? sender, EventArgs e)
    {
        _resultLabel!.Text = "Running memory stress test...";
        _imageView!.Image = null;

        try
        {
            var results = new System.Text.StringBuilder();
            results.AppendLine("Memory Stress Test\n");

            // Get the shared pipeline for reference counting test
            var pipeline = Swift.Nuke.ImagePipeline.Shared;
            var pipelinePtr = pipeline.Payload.DangerousGetHandle();
            long initialRetainCount = Arc.RetainCount(pipelinePtr);

            results.AppendLine($"Pipeline initial retain count: {initialRetainCount}");
            Console.WriteLine($"STRESS TEST: Starting, pipeline retain count: {initialRetainCount}");

            const int iterations = 50;
            int successCount = 0;
            int errorCount = 0;

            // Stress test: rapidly create and dispose ImageRequest objects
            results.AppendLine($"\nCreating/disposing {iterations} ImageRequest objects...");
            _resultLabel.Text = results.ToString();

            for (int i = 0; i < iterations; i++)
            {
                try
                {
                    var request = new Swift.Nuke.ImageRequest($"https://example.com/image{i}.jpg");
                    // Verify the request is valid by accessing a property
                    var _ = request.Description;
                    // Explicitly dispose the payload (ImageRequest doesn't implement IDisposable directly)
                    request.Payload.Dispose();
                    successCount++;
                }
                catch (Exception ex)
                {
                    errorCount++;
                    Console.WriteLine($"STRESS TEST: Error at iteration {i}: {ex.Message}");
                }
            }

            results.AppendLine($"Created: {successCount}, Errors: {errorCount}");

            // Force GC to clean up
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            results.AppendLine("\nGC completed");

            // Check pipeline retain count after stress test
            long finalRetainCount = Arc.RetainCount(pipelinePtr);
            results.AppendLine($"Pipeline final retain count: {finalRetainCount}");

            if (finalRetainCount != initialRetainCount)
            {
                results.AppendLine($"\nWARNING: Retain count drift detected!");
                results.AppendLine($"  Initial: {initialRetainCount}");
                results.AppendLine($"  Final: {finalRetainCount}");
                results.AppendLine($"  Drift: {finalRetainCount - initialRetainCount}");
                Console.WriteLine($"STRESS TEST WARNING: Retain count drift: {initialRetainCount} -> {finalRetainCount}");
            }
            else
            {
                results.AppendLine("\nPASS: No retain count drift detected");
                Console.WriteLine("STRESS TEST PASS: No retain count drift detected");
            }

            // Test SwiftString allocation stress
            results.AppendLine("\nTesting SwiftString allocation...");
            int stringSuccessCount = 0;
            for (int i = 0; i < iterations; i++)
            {
                try
                {
                    using var str = new Swift.SwiftString($"test string {i}");
                    stringSuccessCount++;
                }
                catch
                {
                    // Ignore individual errors
                }
            }
            results.AppendLine($"SwiftString: {stringSuccessCount}/{iterations} successful");

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            results.AppendLine("\n=== STRESS TEST COMPLETE ===");
            Console.WriteLine("=== STRESS TEST COMPLETE ===");

            // Mark as success if no major issues
            if (errorCount == 0 && finalRetainCount == initialRetainCount)
            {
                Console.WriteLine("TEST SUCCESS");
            }

            _resultLabel.Text = results.ToString();
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    private void LogError(Exception ex)
    {
        var msg = new System.Text.StringBuilder();
        msg.AppendLine($"Error: {ex.GetType().Name}");
        msg.AppendLine($"{ex.Message}");

        var inner = ex.InnerException;
        while (inner != null)
        {
            msg.AppendLine($"\nInner: {inner.GetType().Name}");
            msg.AppendLine($"{inner.Message}");
            inner = inner.InnerException;
        }

        Console.WriteLine("=== NUKE BINDING TEST ERROR ===");
        Console.WriteLine(msg.ToString());
        Console.WriteLine($"Stack trace: {ex}");
        Console.WriteLine("================================");

        _resultLabel!.Text = msg.ToString();
    }
}
