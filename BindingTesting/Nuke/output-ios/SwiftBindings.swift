import Nuke
import UIKit
import Foundation

extension Nuke.ImagePipeline {
    @_silgen_name("$s4Nuke13ImagePipelineC5image3forSo7UIImageC10Foundation3URLV_tYaKF_async")
    public func PInvoke_image_76ED7F27(callback: @escaping (UIKit.UIImage, Int64) -> Void, task: Int64, _for: UnsafeRawPointer){
        let _forValue = _for.assumingMemoryBound(to: Foundation.URL.self).pointee
        Task {
            let resultimage = try! await image(for: _forValue)
            callback(resultimage, task);
        }
    }
}
extension Nuke.ImagePipeline {
    @_silgen_name("$s4Nuke13ImagePipelineC5image3forSo7UIImageCAA0B7RequestV_tYaKF_async")
    public func PInvoke_image_702DAEAD(callback: @escaping (UIKit.UIImage, Int64) -> Void, task: Int64, _for: UnsafeRawPointer){
        let _forValue = _for.assumingMemoryBound(to: Nuke.ImageRequest.self).pointee
        Task {
            let resultimage = try! await image(for: _forValue)
            callback(resultimage, task);
        }
    }
}
