// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.InteropServices;
using Swift;
using Swift.Nuke;
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

        // Total content height (title + 5 buttons + result label + image)
        var totalContentHeight = titleHeight + spacing + buttonHeight + spacing + buttonHeight + spacing + buttonHeight + spacing + buttonHeight + spacing + buttonHeight + spacing + resultLabelHeight + spacing + imageHeight;

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

        var protocolTestButton = UIButton.FromType(UIButtonType.System);
        protocolTestButton.Frame = new CoreGraphics.CGRect(20, currentY, contentWidth, buttonHeight);
        protocolTestButton.SetTitle("Test Protocol Proxy", UIControlState.Normal);
        protocolTestButton.TouchUpInside += TestProtocolProxy;
        View.AddSubview(protocolTestButton);
        currentY += buttonHeight + spacing;

        var imageProcessingTestButton = UIButton.FromType(UIButtonType.System);
        imageProcessingTestButton.Frame = new CoreGraphics.CGRect(20, currentY, contentWidth, buttonHeight);
        imageProcessingTestButton.SetTitle("Test ImageProcessing Proxy", UIControlState.Normal);
        imageProcessingTestButton.TouchUpInside += TestImageProcessingProxy;
        View.AddSubview(imageProcessingTestButton);
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

        // Run tests automatically on startup
        RunAutomatedTests();
    }

    private async void RunAutomatedTests()
    {
        // Run protocol proxy tests first
        await Task.Delay(500); // Small delay for UI to settle

        // Test 1: CancellableProxy (simple, single method)
        TestProtocolProxy(null, EventArgs.Empty);
        await Task.Delay(1000);

        // Test 2: ImageProcessingProxy (complex, multiple vtable entries)
        TestImageProcessingProxy(null, EventArgs.Empty);
        await Task.Delay(1000);

        // Test 3: Async image loading
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

    private void TestProtocolProxy(object? sender, EventArgs e)
    {
        _resultLabel!.Text = "Testing Protocol Proxy...";
        _imageView!.Image = null;

        try
        {
            var results = new System.Text.StringBuilder();
            results.AppendLine("Protocol Proxy Test\n");
            results.AppendLine("Testing C# implementation of Swift protocols...\n");

            // Test 1: Create a C# implementation of ISwiftCancellable
            results.AppendLine("1. Creating MyCancellable (C# implementation)...");
            var myCancellable = new MyCancellable();
            results.AppendLine($"   Created: {myCancellable.GetType().Name}");
            Console.WriteLine($"PROTOCOL TEST: Created MyCancellable");

            // Test 2: Verify the implementation works directly
            results.AppendLine("\n2. Testing direct C# implementation...");
            myCancellable.cancel();
            results.AppendLine($"   cancel() called, CancelCount = {myCancellable.CancelCount}");
            Console.WriteLine($"PROTOCOL TEST: Direct call works, CancelCount = {myCancellable.CancelCount}");

            // Test 3: Test SwiftObjectRegistry
            results.AppendLine("\n3. Testing SwiftObjectRegistry...");
            var testHandle = new IntPtr(123456789);
            SwiftObjectRegistry.Register(testHandle, myCancellable);
            results.AppendLine($"   Registered with handle {testHandle}");

            if (SwiftObjectRegistry.TryGetProxy<MyCancellable>(testHandle, out var retrieved))
            {
                results.AppendLine($"   Retrieved proxy: {retrieved!.GetType().Name}");
                results.AppendLine($"   Same instance: {ReferenceEquals(myCancellable, retrieved)}");
                Console.WriteLine($"PROTOCOL TEST: Registry lookup succeeded");
            }
            else
            {
                results.AppendLine("   FAILED: Could not retrieve from registry");
                Console.WriteLine("PROTOCOL TEST FAILED: Registry lookup failed");
            }
            SwiftObjectRegistry.Unregister(testHandle);
            results.AppendLine("   Unregistered");

            // Test 4: Try to create a CancellableProxy
            results.AppendLine("\n4. Testing CancellableProxy creation...");
            try
            {
                var proxy = new CancellableProxy(myCancellable);
                results.AppendLine("   CancellableProxy created successfully!");
                results.AppendLine($"   Registry count: {SwiftObjectRegistry.Count}");
                Console.WriteLine($"PROTOCOL TEST: CancellableProxy created, registry count = {SwiftObjectRegistry.Count}");

                // Test 5: Call cancel() through the proxy
                results.AppendLine("\n5. Calling cancel() through proxy...");
                myCancellable.CancelCount = 0; // Reset
                proxy.cancel();
                results.AppendLine($"   cancel() called via proxy, CancelCount = {myCancellable.CancelCount}");

                if (myCancellable.CancelCount == 1)
                {
                    results.AppendLine("\n=== PROTOCOL PROXY TEST SUCCESS ===");
                    Console.WriteLine("PROTOCOL TEST SUCCESS: Full proxy pattern works!");
                }
                else
                {
                    results.AppendLine("\n=== PROTOCOL PROXY TEST PARTIAL ===");
                    Console.WriteLine("PROTOCOL TEST PARTIAL: Proxy created but callback not invoked");
                }
            }
            catch (NotImplementedException niex)
            {
                results.AppendLine($"   NotImplementedException: {niex.Message}");
                results.AppendLine("\n   NOTE: Witness table lookup failed.");
                Console.WriteLine($"=== PROTOCOL PROXY TEST FAILED: {niex.Message} ===");

                results.AppendLine("\n=== PROTOCOL PROXY TEST FAILED ===");
                results.AppendLine("Witness table lookup threw NotImplementedException.");
            }
            catch (Exception proxyEx)
            {
                results.AppendLine($"   Error: {proxyEx.GetType().Name}");
                results.AppendLine($"   {proxyEx.Message}");
                Console.WriteLine($"PROTOCOL TEST ERROR: {proxyEx}");
            }

            _resultLabel.Text = results.ToString();
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    private void TestImageProcessingProxy(object? sender, EventArgs e)
    {
        _resultLabel!.Text = "Testing ImageProcessing Proxy...";
        _imageView!.Image = null;

        try
        {
            var results = new System.Text.StringBuilder();
            results.AppendLine("ImageProcessing Proxy Test\n");
            results.AppendLine("Testing complex protocol with multiple vtable entries...\n");

            // Test 1: Create a C# implementation of ISwiftImageProcessing
            results.AppendLine("1. Creating MyImageProcessor (C# implementation)...");
            var myProcessor = new MyImageProcessor();
            results.AppendLine($"   Created: {myProcessor.GetType().Name}");
            Console.WriteLine($"IMAGE PROCESSING TEST: Created MyImageProcessor");

            // Test 2: Verify the implementation works directly
            results.AppendLine("\n2. Testing direct C# implementation...");
            var testId = myProcessor.identifier;
            results.AppendLine($"   identifier accessed, IdentifierCallCount = {myProcessor.IdentifierCallCount}");
            Console.WriteLine($"IMAGE PROCESSING TEST: Direct call works, IdentifierCallCount = {myProcessor.IdentifierCallCount}");

            // Test 3: Create an ImageProcessingProxy (triggers vtable initialization)
            results.AppendLine("\n3. Creating ImageProcessingProxy...");
            int registryCountBefore = SwiftObjectRegistry.Count;
            try
            {
                var proxy = new ImageProcessingProxy(myProcessor);
                results.AppendLine("   ImageProcessingProxy created successfully!");
                results.AppendLine($"   Registry count before: {registryCountBefore}");
                results.AppendLine($"   Registry count after: {SwiftObjectRegistry.Count}");
                Console.WriteLine($"IMAGE PROCESSING TEST: Proxy created, registry count = {SwiftObjectRegistry.Count}");

                // Test 4: Access identifier through the proxy
                results.AppendLine("\n4. Accessing identifier through proxy...");
                myProcessor.IdentifierCallCount = 0; // Reset count
                var proxyId = proxy.identifier;
                results.AppendLine($"   identifier via proxy, IdentifierCallCount = {myProcessor.IdentifierCallCount}");

                if (myProcessor.IdentifierCallCount == 1)
                {
                    results.AppendLine("\n=== IMAGE PROCESSING PROXY TEST SUCCESS ===");
                    Console.WriteLine("IMAGE PROCESSING TEST SUCCESS: Full proxy pattern works!");
                }
                else
                {
                    results.AppendLine("\n=== IMAGE PROCESSING PROXY TEST PARTIAL ===");
                    results.AppendLine("Proxy created but callback may not have been invoked correctly.");
                    Console.WriteLine("IMAGE PROCESSING TEST PARTIAL: Proxy created but callback not invoked");
                }
            }
            catch (NotImplementedException niex)
            {
                results.AppendLine($"   NotImplementedException: {niex.Message}");
                results.AppendLine("\n   NOTE: This may indicate witness table lookup issues.");
                Console.WriteLine($"IMAGE PROCESSING TEST ERROR: {niex.Message}");
            }
            catch (Exception proxyEx)
            {
                results.AppendLine($"   Error: {proxyEx.GetType().Name}");
                results.AppendLine($"   {proxyEx.Message}");
                Console.WriteLine($"IMAGE PROCESSING TEST ERROR: {proxyEx}");
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

/// <summary>
/// A C# implementation of the Swift Cancellable protocol.
/// This demonstrates implementing Swift protocols from C#.
/// </summary>
public class MyCancellable : ISwiftCancellable
{
    /// <summary>
    /// Tracks how many times cancel() was called, for testing purposes.
    /// </summary>
    public int CancelCount { get; set; }

    /// <summary>
    /// The operation was cancelled flag.
    /// </summary>
    public bool IsCancelled { get; private set; }

    /// <summary>
    /// Implements the Cancellable.cancel() protocol requirement.
    /// </summary>
    public void cancel()
    {
        CancelCount++;
        IsCancelled = true;
        Console.WriteLine($"MyCancellable.cancel() called! Count = {CancelCount}");
    }
}

/// <summary>
/// A C# implementation of the Swift ImageProcessing protocol.
/// This tests a more complex protocol with multiple methods/properties.
/// Note: The interface uses lowercase member names (identifier, process) matching Swift naming conventions.
/// </summary>
public class MyImageProcessor : ISwiftImageProcessing
{
    /// <summary>
    /// Tracks how many times identifier was accessed.
    /// </summary>
    public int IdentifierCallCount { get; set; }

    /// <summary>
    /// Tracks how many times process() was called.
    /// </summary>
    public int ProcessCallCount { get; private set; }

    /// <summary>
    /// The processor identifier (Swift naming: lowercase).
    /// </summary>
    public SwiftString identifier
    {
        get
        {
            IdentifierCallCount++;
            Console.WriteLine($"MyImageProcessor.identifier accessed! Count = {IdentifierCallCount}");
            return new SwiftString("my-test-processor");
        }
    }

    /// <summary>
    /// The hashable identifier for caching (returns empty/default for test).
    /// </summary>
    public AnyType hashableIdentifier => default;

    /// <summary>
    /// Process a UIImage (Swift naming: lowercase).
    /// Returns None for test purposes.
    /// </summary>
    public SwiftOptional<UIKit.UIImage> process(UIKit.UIImage arg0)
    {
        ProcessCallCount++;
        Console.WriteLine($"MyImageProcessor.process(UIImage) called! Count = {ProcessCallCount}");
        return SwiftOptional<UIKit.UIImage>.NewNone();
    }

    /// <summary>
    /// Process an ImageContainer with context.
    /// Returns input unchanged for test purposes.
    /// </summary>
    public ImageContainer process(ImageContainer arg0, ImageProcessingContext context)
    {
        ProcessCallCount++;
        Console.WriteLine($"MyImageProcessor.process(ImageContainer, context) called! Count = {ProcessCallCount}");
        return arg0;
    }
}
