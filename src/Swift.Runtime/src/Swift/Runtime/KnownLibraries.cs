// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift.Runtime;

internal static class KnownLibraries
{
    public const string SwiftCore = "/usr/lib/swift/libswiftCore.dylib";
    public const string SwiftFoundation = "/System/Library/Frameworks/Foundation.framework/Foundation";
    public const string SwiftDispatch = "/usr/lib/swift/libswiftDispatch.dylib";
    public const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    public const string CoreImage = "/System/Library/Frameworks/CoreImage.framework/CoreImage";
}
