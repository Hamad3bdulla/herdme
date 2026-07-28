import Foundation

private func localizedPHPSerializationError(_ key: String) -> String {
    #if canImport(Darwin)
        return Bundle.main.localizedString(forKey: key, value: key, table: nil)
    #else
        return key
    #endif
}

indirect enum PHPSerializedValue: Sendable {
    case null
    case bool(Bool)
    case integer(Int64)
    case double(Double)
    case string(String)
    case array([(PHPSerializedValue, PHPSerializedValue)])
    case object(name: String, properties: [(PHPSerializedValue, PHPSerializedValue)])
    case reference(Int)

    func rendered(depth: Int = 0) -> String {
        guard depth < 16 else { return "..." }
        switch self {
        case .null: return "null"
        case .bool(let value): return value ? "true" : "false"
        case .integer(let value): return String(value)
        case .double(let value): return String(value)
        case .string(let value): return "\"" + value + "\""
        case .reference(let identifier): return "reference(" + String(identifier) + ")"
        case .array(let values):
            guard !values.isEmpty else { return "[]" }
            let indentation = String(repeating: "  ", count: depth + 1)
            let closing = String(repeating: "  ", count: depth)
            return "[\n"
                + values.map { key, value in
                    indentation + key.shortKey() + ": " + value.rendered(depth: depth + 1)
                }.joined(separator: ",\n") + "\n" + closing + "]"
        case .object(let name, let properties):
            let indentation = String(repeating: "  ", count: depth + 1)
            let closing = String(repeating: "  ", count: depth)
            return name + " {\n"
                + properties.map { key, value in
                    indentation + key.shortKey() + ": " + value.rendered(depth: depth + 1)
                }.joined(separator: ",\n") + "\n" + closing + "}"
        }
    }

    func firstString(forKeysContaining keys: [String]) -> String? {
        switch self {
        case .array(let values), .object(_, let values):
            for (key, value) in values {
                let normalized = key.shortKey().lowercased()
                if keys.contains(where: normalized.contains), case .string(let result) = value {
                    return result
                }
                if let nested = value.firstString(forKeysContaining: keys) { return nested }
            }
            return nil
        default:
            return nil
        }
    }

    private func shortKey() -> String {
        switch self {
        case .string(let value):
            return value.split(separator: "\0").last.map(String.init) ?? value
        case .integer(let value):
            return String(value)
        default:
            return rendered()
        }
    }
}

enum PHPSerializationError: LocalizedError {
    case malformed
    case resourceLimit
    case unsupported(Character)

    var errorDescription: String? {
        switch self {
        case .malformed: localizedPHPSerializationError("Malformed PHP serialized value.")
        case .resourceLimit:
            localizedPHPSerializationError("The PHP serialized value exceeds HerdMe's safety limits.")
        case .unsupported(let type):
            String.localizedStringWithFormat(
                localizedPHPSerializationError("Unsupported PHP serialized type: %@"),
                String(type)
            )
        }
    }
}

struct PHPSerializationParser {
    private static let maximumInputBytes = 4 * 1_024 * 1_024
    private static let maximumDepth = 32
    private static let maximumCollectionItems = 10_000

    private let bytes: [UInt8]
    private let exceedsInputLimit: Bool
    private var index = 0
    private var remainingCollectionItems = Self.maximumCollectionItems

    init(data: Data) {
        exceedsInputLimit = data.count > Self.maximumInputBytes
        bytes = exceedsInputLimit ? [] : Array(data)
    }

    mutating func parse() throws -> PHPSerializedValue {
        guard !exceedsInputLimit else { throw PHPSerializationError.resourceLimit }
        let value = try parseValue(depth: 0)
        guard index == bytes.count else { throw PHPSerializationError.malformed }
        return value
    }

    private mutating func parseValue(depth: Int) throws -> PHPSerializedValue {
        guard depth <= Self.maximumDepth else { throw PHPSerializationError.resourceLimit }
        guard index < bytes.count else { throw PHPSerializationError.malformed }
        let marker = Character(UnicodeScalar(bytes[index]))
        index += 1
        switch marker {
        case "N":
            try expect(";")
            return .null
        case "b":
            try expect(":")
            switch try readNumber(until: ";") {
            case "0": return .bool(false)
            case "1": return .bool(true)
            default: throw PHPSerializationError.malformed
            }
        case "i":
            try expect(":")
            guard let value = Int64(try readNumber(until: ";")) else { throw PHPSerializationError.malformed }
            return .integer(value)
        case "d":
            try expect(":")
            guard let value = Double(try readNumber(until: ";")) else { throw PHPSerializationError.malformed }
            return .double(value)
        case "s":
            return .string(try readString())
        case "a":
            try expect(":")
            guard let count = Int(try readNumber(until: ":")), count >= 0 else {
                throw PHPSerializationError.malformed
            }
            try reserveCollectionItems(count)
            try expect("{")
            var values: [(PHPSerializedValue, PHPSerializedValue)] = []
            values.reserveCapacity(count)
            for _ in 0..<count {
                values.append((try parseValue(depth: depth + 1), try parseValue(depth: depth + 1)))
            }
            try expect("}")
            return .array(values)
        case "O":
            try expect(":")
            guard let nameLength = Int(try readNumber(until: ":")) else { throw PHPSerializationError.malformed }
            try expect("\"")
            let name = try readStringBytes(length: nameLength)
            try expect("\"")
            try expect(":")
            guard let count = Int(try readNumber(until: ":")), count >= 0 else {
                throw PHPSerializationError.malformed
            }
            try reserveCollectionItems(count)
            try expect("{")
            var properties: [(PHPSerializedValue, PHPSerializedValue)] = []
            properties.reserveCapacity(count)
            for _ in 0..<count {
                properties.append((try parseValue(depth: depth + 1), try parseValue(depth: depth + 1)))
            }
            try expect("}")
            return .object(name: name, properties: properties)
        case "R", "r":
            try expect(":")
            guard let identifier = Int(try readNumber(until: ";")) else { throw PHPSerializationError.malformed }
            return .reference(identifier)
        default:
            throw PHPSerializationError.unsupported(marker)
        }
    }

    private mutating func readString() throws -> String {
        try expect(":")
        guard let length = Int(try readNumber(until: ":")) else { throw PHPSerializationError.malformed }
        try expect("\"")
        let value = try readStringBytes(length: length)
        try expect("\"")
        try expect(";")
        return value
    }

    private mutating func readStringBytes(length: Int) throws -> String {
        guard length >= 0, length <= bytes.count - index else { throw PHPSerializationError.malformed }
        let value = String(decoding: bytes[index..<(index + length)], as: UTF8.self)
        index += length
        return value
    }

    private mutating func readNumber(until delimiter: Character) throws -> String {
        guard let delimiterByte = String(delimiter).utf8.first else {
            throw PHPSerializationError.malformed
        }
        let start = index
        while index < bytes.count, bytes[index] != delimiterByte { index += 1 }
        guard index < bytes.count else { throw PHPSerializationError.malformed }
        let value = String(decoding: bytes[start..<index], as: UTF8.self)
        index += 1
        return value
    }

    private mutating func expect(_ character: Character) throws {
        guard let expected = String(character).utf8.first,
            index < bytes.count, bytes[index] == expected
        else {
            throw PHPSerializationError.malformed
        }
        index += 1
    }

    private mutating func reserveCollectionItems(_ count: Int) throws {
        guard count <= remainingCollectionItems else { throw PHPSerializationError.resourceLimit }
        remainingCollectionItems -= count
    }
}
