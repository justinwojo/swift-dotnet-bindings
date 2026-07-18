// Wrapper-shaped fixture: the wrapper imports a module that was never supplied. This is a global
// failure of the inputs, not of any declaration — attribution must classify it to InputConfiguration
// and never charge it to the entry point below.
import CompletelyFictionalDependency

@_cdecl("SBW_Feature_run")
public func SBW_Feature_run(_ handle: UnsafeMutableRawPointer) {
}
