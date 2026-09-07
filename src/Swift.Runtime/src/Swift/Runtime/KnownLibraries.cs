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
    /// <summary>
    /// The C runtime umbrella that carries libdispatch's exported C API and data symbols
    /// (<c>dispatch_get_global_queue</c>, <c>_dispatch_main_q</c>, …). The Swift Dispatch overlay
    /// inlines its queue accessors into callers and exports no getter to call, so the queues are
    /// reached through the C surface.
    /// </summary>
    public const string LibSystem = "/usr/lib/libSystem.B.dylib";
    public const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    public const string CoreImage = "/System/Library/Frameworks/CoreImage.framework/CoreImage";
    public const string UIKit = "/System/Library/Frameworks/UIKit.framework/UIKit";
    public const string SwiftUI = "/System/Library/Frameworks/SwiftUI.framework/SwiftUI";
}
