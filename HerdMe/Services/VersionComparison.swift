import Foundation

enum VersionComparison {
    nonisolated static func compare(_ left: String, _ right: String) -> ComparisonResult {
        let normalizedLeft = normalize(left)
        let normalizedRight = normalize(right)
        if normalizedLeft == normalizedRight { return .orderedSame }

        if let leftVersion = SemanticVersion(normalizedLeft),
            let rightVersion = SemanticVersion(normalizedRight)
        {
            return leftVersion.compare(to: rightVersion)
        }

        return normalizedLeft.compare(
            normalizedRight,
            options: [.numeric, .caseInsensitive]
        )
    }

    nonisolated static func isNewer(_ candidate: String, than current: String) -> Bool {
        compare(candidate, current) == .orderedDescending
    }

    nonisolated static func isSemanticVersion(_ value: String) -> Bool {
        SemanticVersion(normalize(value)) != nil
    }

    nonisolated static func normalize(_ value: String) -> String {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let first = trimmed.first, first == "v" || first == "V" else {
            return trimmed
        }
        return String(trimmed.dropFirst())
    }
}

private struct SemanticVersion {
    let core: [String]
    let prerelease: [String]?

    init?(_ value: String) {
        guard !value.isEmpty else { return nil }
        let buildParts = value.split(
            separator: "+",
            maxSplits: 1,
            omittingEmptySubsequences: false
        )
        guard buildParts.count <= 2,
            buildParts.count == 1 || Self.validIdentifiers(buildParts[1], numericLeadingZeros: true)
        else {
            return nil
        }

        let precedence = buildParts[0]
        let prereleaseSeparator = precedence.firstIndex(of: "-")
        let coreText: Substring
        let prereleaseText: Substring?
        if let prereleaseSeparator {
            coreText = precedence[..<prereleaseSeparator]
            prereleaseText = precedence[precedence.index(after: prereleaseSeparator)...]
        } else {
            coreText = precedence
            prereleaseText = nil
        }

        let core = coreText.split(separator: ".", omittingEmptySubsequences: false)
        guard !core.isEmpty,
            core.allSatisfy({ Self.validNumericIdentifier($0, allowLeadingZeros: false) })
        else {
            return nil
        }
        self.core = core.map(String.init)

        if let prereleaseText {
            guard Self.validIdentifiers(prereleaseText, numericLeadingZeros: false) else {
                return nil
            }
            prerelease = prereleaseText.split(
                separator: ".",
                omittingEmptySubsequences: false
            ).map(String.init)
        } else {
            prerelease = nil
        }
    }

    func compare(to other: SemanticVersion) -> ComparisonResult {
        let coreCount = max(core.count, other.core.count)
        for index in 0..<coreCount {
            let left = index < core.count ? core[index] : "0"
            let right = index < other.core.count ? other.core[index] : "0"
            let result = Self.compareNumeric(left, right)
            if result != .orderedSame { return result }
        }

        switch (prerelease, other.prerelease) {
        case (nil, nil):
            return .orderedSame
        case (nil, _):
            return .orderedDescending
        case (_, nil):
            return .orderedAscending
        case (let left?, let right?):
            for index in 0..<min(left.count, right.count) {
                let leftIdentifier = left[index]
                let rightIdentifier = right[index]
                if leftIdentifier == rightIdentifier { continue }
                let leftIsNumeric = leftIdentifier.allSatisfy { $0.isASCII && $0.isNumber }
                let rightIsNumeric = rightIdentifier.allSatisfy { $0.isASCII && $0.isNumber }
                if leftIsNumeric, rightIsNumeric {
                    return Self.compareNumeric(leftIdentifier, rightIdentifier)
                }
                if leftIsNumeric != rightIsNumeric {
                    return leftIsNumeric ? .orderedAscending : .orderedDescending
                }
                return leftIdentifier < rightIdentifier ? .orderedAscending : .orderedDescending
            }
            if left.count == right.count { return .orderedSame }
            return left.count < right.count ? .orderedAscending : .orderedDescending
        }
    }

    private static func validIdentifiers(
        _ value: Substring,
        numericLeadingZeros: Bool
    ) -> Bool {
        let identifiers = value.split(separator: ".", omittingEmptySubsequences: false)
        return !identifiers.isEmpty
            && identifiers.allSatisfy { identifier in
                guard !identifier.isEmpty,
                    identifier.allSatisfy({ character in
                        character.isASCII && (character.isLetter || character.isNumber || character == "-")
                    })
                else {
                    return false
                }
                if identifier.allSatisfy(\.isNumber) {
                    return validNumericIdentifier(identifier, allowLeadingZeros: numericLeadingZeros)
                }
                return true
            }
    }

    private static func validNumericIdentifier(
        _ value: Substring,
        allowLeadingZeros: Bool
    ) -> Bool {
        !value.isEmpty
            && value.allSatisfy({ $0.isASCII && $0.isNumber })
            && (allowLeadingZeros || value.count == 1 || value.first != "0")
    }

    private static func compareNumeric(_ left: String, _ right: String) -> ComparisonResult {
        let normalizedLeft = left.drop(while: { $0 == "0" })
        let normalizedRight = right.drop(while: { $0 == "0" })
        let leftDigits = normalizedLeft.isEmpty ? "0" : String(normalizedLeft)
        let rightDigits = normalizedRight.isEmpty ? "0" : String(normalizedRight)
        if leftDigits.count != rightDigits.count {
            return leftDigits.count < rightDigits.count ? .orderedAscending : .orderedDescending
        }
        if leftDigits == rightDigits { return .orderedSame }
        return leftDigits < rightDigits ? .orderedAscending : .orderedDescending
    }
}
