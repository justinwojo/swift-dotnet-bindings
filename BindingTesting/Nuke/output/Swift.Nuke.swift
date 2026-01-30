extension Nuke.ImagePipeline {
    @_silgen_name("$s4Nuke13ImagePipelineC5image3forSo7NSImageC10Foundation3URLV_tYaKF_async")
    public  func PInvoke_image_35DA2955(callback: @escaping (AppKit.NSImage, Int64) -> Void, task: Int64, _for: Foundation.URL){
        Task {
            let resultimage = try! await image(
                for: _for
            )
            callback(resultimage, task);
        }
    }
}
extension Nuke.ImagePipeline {
    @_silgen_name("$s4Nuke13ImagePipelineC5image3forSo7NSImageCAA0B7RequestV_tYaKF_async")
    public  func PInvoke_image_44C70742(callback: @escaping (AppKit.NSImage, Int64) -> Void, task: Int64, _for: Nuke.ImageRequest){
        Task {
            let resultimage = try! await image(
                for: _for
            )
            callback(resultimage, task);
        }
    }
}
