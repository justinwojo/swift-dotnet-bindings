// Compiler-legality recovery fixture: the first entry point names an Objective-C type that
// Foundation deliberately makes unavailable to Swift. The sibling must remain compilable.
import Foundation

@_cdecl("SBW_NSInvocationRecovery_broken")
public func SBW_NSInvocationRecovery_broken(_ invocation: NSInvocation) {
    invocation.invoke()
}

@_cdecl("SBW_NSInvocationRecovery_healthy")
public func SBW_NSInvocationRecovery_healthy(_ value: Int32) -> Int32 {
    value + 1
}
