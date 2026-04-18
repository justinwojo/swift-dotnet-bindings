// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;

// The Apple supplement package consumes KnownLibraries for hand-rolled P/Invokes on
// Foundation.Data/URL/URLRequest/etc. — promoting to internal-visible keeps these paths
// a single source of truth rather than duplicating the library path strings.
[assembly: InternalsVisibleTo("SwiftBindings.Apple")]

namespace Swift.Runtime;

internal static class KnownLibraries
{
    public const string SwiftCore = "/usr/lib/swift/libswiftCore.dylib";
    public const string SwiftFoundation = "/System/Library/Frameworks/Foundation.framework/Foundation";
    public const string SwiftDispatch = "/usr/lib/swift/libswiftDispatch.dylib";
    public const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    public const string CoreImage = "/System/Library/Frameworks/CoreImage.framework/CoreImage";
    public const string UIKit = "/System/Library/Frameworks/UIKit.framework/UIKit";
    public const string SwiftUI = "/System/Library/Frameworks/SwiftUI.framework/SwiftUI";
}
