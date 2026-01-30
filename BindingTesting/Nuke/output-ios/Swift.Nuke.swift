import Nuke
import Foundation
import UIKit

extension Nuke.ImagePipeline {
    @_silgen_name("$s4Nuke13ImagePipelineC5image3forSo7UIImageC10Foundation3URLV_tYaKF_async")
    public  func PInvoke_image_188230F9(callback: @escaping @convention(c) (UIKit.UIImage, Int64) -> Void, task: Int64, _for: UnsafeRawPointer){
        // Read non-frozen parameters via .pointee (bitwise copy)
        // C# created copies using InitializeWithCopy (owns a proper reference)
        let _forValue = _for.assumingMemoryBound(to: Foundation.URL.self).pointee
        // self is safe - C# called Arc.Retain before invoking this method

        Task {
            let resultimage = try! await image(
                for: _forValue
            )
            callback(resultimage, task)
        }
    }
}
extension Nuke.ImagePipeline {
    @_silgen_name("$s4Nuke13ImagePipelineC5image3forSo7UIImageCAA0B7RequestV_tYaKF_async")
    public  func PInvoke_image_3BF0CA99(callback: @escaping @convention(c) (UIKit.UIImage, Int64) -> Void, task: Int64, _for: UnsafeRawPointer){
        // Read non-frozen parameters via .pointee (bitwise copy)
        // C# created copies using InitializeWithCopy (owns a proper reference)
        let _forValue = _for.assumingMemoryBound(to: Nuke.ImageRequest.self).pointee
        // self is safe - C# called Arc.Retain before invoking this method

        Task {
            let resultimage = try! await image(
                for: _forValue
            )
            callback(resultimage, task)
        }
    }
}
