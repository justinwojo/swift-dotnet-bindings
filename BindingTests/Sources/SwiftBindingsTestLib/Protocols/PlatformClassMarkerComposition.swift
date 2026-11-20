// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// A platform framework's Objective-C class composed only with a marker protocol
// (`any AVAsset & Sendable`) is the class itself at runtime, exactly like a module's
// own class composed that way (ClassMarkerComposition.swift). It is the entry point of
// media-analysis session APIs (`init(asset: any AVAsset & Sendable) async throws`). The
// platform class has no record in the module's own type database, so it must be recognized
// from the platform binding; these members cover parameter, async throwing initializer,
// return, optional and array positions.

import AVFoundation

public enum PlatformAssetMarkerError: Error {
    case notAURLAsset
}

private func platformAssetName(_ asset: AVAsset) -> String {
    if let urlAsset = asset as? AVURLAsset {
        return urlAsset.url.lastPathComponent
    }
    return "<\(type(of: asset))>"
}

/// Session-style API whose entry points take the platform class composition.
public final class PlatformAssetMarkerSession {
    public let assetName: String

    public init(asset: any AVAsset & Sendable) async throws {
        await Task.yield()
        guard let urlAsset = asset as? AVURLAsset else {
            throw PlatformAssetMarkerError.notAURLAsset
        }
        assetName = urlAsset.url.lastPathComponent
    }

    public init(syncAsset: any AVAsset & Sendable) {
        assetName = platformAssetName(syncAsset)
    }

    public func describe(_ asset: any AVAsset & Sendable) -> String {
        return platformAssetName(asset)
    }

    public func echo(_ asset: any AVAsset & Sendable) -> any AVAsset & Sendable {
        return asset
    }

    public func optionalDescribe(_ asset: (any AVAsset & Sendable)?) -> String {
        return asset.map(platformAssetName) ?? "<nil>"
    }

    public func names(_ assets: [any AVAsset & Sendable]) -> String {
        return assets.map(platformAssetName).joined(separator: ",")
    }

    public func isSameObject(_ asset: any AVAsset & Sendable, _ other: AVAsset) -> Bool {
        return asset === other
    }
}
