// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using Swift;
using Swift.Runtime;

/// <summary>
/// Represents Swift Hashable protocol in C#.
/// </summary>
public unsafe interface IHashable : ISwiftProtocol
{
    static ProtocolDescriptor ISwiftProtocol.GetProtocolDescriptor()
    {
        return ProtocolDescriptor.LoadFromSymbol(KnownLibraries.SwiftCore, "$sSHMp");
    }
}
