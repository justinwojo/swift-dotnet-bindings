// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if canImport(Vision)
import Vision
import Foundation

// MARK: - Members typed by an Apple framework NS_STRING_ENUM
//
// A framework enum spelled NS_STRING_ENUM is not an ObjC class and not an integer enum: Swift
// imports it as a String-backed RawRepresentable struct, while the .NET projection is a C# enum
// plus a `{Enum}Extensions.GetConstant`/`GetValue` pair over NSString constants. So the boundary
// has to convert in BOTH directions rather than pass a handle: treating the type as an ObjC class
// emits `.Handle` on a C# enum (no such member), and treating it as a plain value type would ship
// the C# enum's ordinal across an NSString ABI — which compiles and is wrong.
//
// The array member is the shape that carries the most risk, because the conversion belongs to each
// ELEMENT inside the container projection, not to the container. The Swift side compares the values
// it receives rather than echoing them, so a carrier that round-trips opaquely without ever being a
// real symbology fails the comparison instead of passing by accident.

public final class BarcodeSymbologySelectionLike {
    public let preferred: VNBarcodeSymbology

    /// Settable, so the accessor direction is exercised inbound as well as outbound — a getter and
    /// a setter reach the boundary through different emission paths than a method does.
    public var fallback: VNBarcodeSymbology

    public init(preferred: VNBarcodeSymbology, fallback: VNBarcodeSymbology) {
        self.preferred = preferred
        self.fallback = fallback
    }

    /// Scalar in, scalar out.
    public func alternate(to other: VNBarcodeSymbology) -> VNBarcodeSymbology {
        other
    }

    /// Surfaces the underlying NSString, so a constant that arrives as the *wrong* symbology reads
    /// as a wrong string rather than as a handle that merely survived the trip.
    public func rawValue(of symbology: VNBarcodeSymbology) -> String {
        symbology.rawValue
    }

    /// Array parameter and array return in one call.
    public func filterSymbologies(
        _ symbologies: [VNBarcodeSymbology],
        matching wanted: VNBarcodeSymbology
    ) -> [VNBarcodeSymbology] {
        symbologies.filter { $0 == wanted }
    }

    /// Array parameter with a scalar return, so the inbound direction is still observed if the
    /// array RETURN plan were the broken one.
    public func countSymbologies(
        _ symbologies: [VNBarcodeSymbology],
        matching wanted: VNBarcodeSymbology
    ) -> Int32 {
        Int32(symbologies.filter { $0 == wanted }.count)
    }

    /// Optional return. The nullable-pointer ABI reads the carrier back only after testing the
    /// pointer against nil, so the conversion has to sit inside that guarded branch.
    public func firstSymbology(in symbologies: [VNBarcodeSymbology]) -> VNBarcodeSymbology? {
        symbologies.first
    }

    /// Optional parameter. This one has its own emission path: the wrapper's parameter
    /// marshalling used to bypass the projection entirely and take `.Handle` off the inner, which
    /// a C# enum does not have and — being a value type — cannot even be `?.`-chained through.
    public func rawValue(ofOptional symbology: VNBarcodeSymbology?) -> String {
        symbology?.rawValue ?? ""
    }

    /// Settable COLLECTION properties. An accessor setter is a separate emission path from a method
    /// parameter: it builds the ObjC collection itself instead of going through the parameter marshal
    /// plan, so it can miss the per-element conversion the parameter path applies and hand a C# enum
    /// to `NSArray.FromNSObjects` / `NSSet`, which take NSObjects.
    public var accepted: [VNBarcodeSymbology] = []

    public var acceptedUnique: Set<VNBarcodeSymbology> = []

    /// Reads the settable array back on the SWIFT side, so a setter that shipped something other than
    /// real symbology constants is observable rather than merely round-tripping within C#.
    public func acceptedRawValues() -> [String] {
        accepted.map { $0.rawValue }
    }

    /// Same observation for the settable set, decided by the constant's own hash on the Swift side.
    public func acceptedUniqueCount(matching wanted: VNBarcodeSymbology) -> Int32 {
        Int32(acceptedUnique.filter { $0 == wanted }.count)
    }

    /// Dictionary value and Set element, whose conversions belong to the element rather than the
    /// container, exactly as the array case does.
    public func echoLabels(_ map: [String: VNBarcodeSymbology]) -> [String: VNBarcodeSymbology] {
        map
    }

    public func echoUnique(_ symbologies: Set<VNBarcodeSymbology>) -> Set<VNBarcodeSymbology> {
        symbologies
    }

    /// Deliberately unbindable: a closure carrying the typed enum. The member's public signature
    /// would project the enum while the callback thunk renders the NSString carrier, and the two
    /// are unrelated generic instantiations — so the delegate cast inside the thunk would throw at
    /// the first callback with nothing failing at compile time. The binding drops this member; its
    /// presence here pins that it fails closed rather than emitting the throwing shape.
    public func visitSymbologies(_ body: (VNBarcodeSymbology) -> Void) {
        body(preferred)
    }
}
#endif

#if canImport(ImageIO)
import ImageIO

// MARK: - Members typed by a framework enum whose module carries no ObjC-bridging flags
//
// CGImagePropertyOrientation is an ordinary integer enum, but ImageIO is neither an auto-bridge nor
// an optional-fallback module, so a record built only for bridging modules never covers it and every
// member typed by one is skipped as an unprojected Apple type. It is also UInt32-backed while the
// boundary carries a signed word, which is the second thing under test here: the raw value has to be
// reconstructed by bit pattern, not by a checked conversion that traps on the high cases.

public final class ImageOrientationSelectionLike {
    public let orientation: CGImagePropertyOrientation

    public init(orientation: CGImagePropertyOrientation) {
        self.orientation = orientation
    }

    public func alternate(to other: CGImagePropertyOrientation) -> CGImagePropertyOrientation {
        other
    }

    public func rawValue(of orientation: CGImagePropertyOrientation) -> UInt32 {
        orientation.rawValue
    }
}
#endif
