// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if canImport(PassKit)
import PassKit
import Foundation

// MARK: - Members typed by an Apple framework enum the registry lists as a value type
//
// `PKPaymentButtonType` is an NS_ENUM(NSInteger) that the Swift importer surfaces as a
// raw-value enum and that the platform bindings ship as a plain C# enum. It is listed
// among PassKit's registry value types so the ObjC bridge does not synthesize a
// Handle-bearing class record for it — but a value-type listing on its own supplies no
// record at all, which used to make every member below unresolvable ("Type resolution
// failed") and silently drop a payment-button configuration surface that is entirely
// bindable. The registry now additionally describes the shape, so a real raw-value enum
// record is built and these members emit and compile.
//
// Shapes exercised: a stored property (the reported failing shape), a method that both
// takes and returns the enum, and a constructor parameter. The Int sibling confirms the
// rest of the type still binds.

public final class PaymentButtonConfigurationLike {
    public let buttonType: PKPaymentButtonType

    public func alternate(to other: PKPaymentButtonType) -> PKPaymentButtonType { other }

    public let cornerRadius: Int32

    public init(buttonType: PKPaymentButtonType, cornerRadius: Int32) {
        self.buttonType = buttonType
        self.cornerRadius = cornerRadius
    }
}
#endif
