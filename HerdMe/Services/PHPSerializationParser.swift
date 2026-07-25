import Foundation

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
        case let .bool(value): return value ? "true" : "false"
        case let .integer(value): return String(value)
        case let .double(value): return String(value)
        case let .string(value): return "\"" + value + "\""
        case let .reference(identifier): return "reference(" + String(identifier) + ")"
        case let .array(values):
            guard !values.isEmpty else { return "[]" }
            let indentation = String(repeating: "  ", count: depth + 1)
            let closing = String(repeating: "  ", count: depth)
            return "[\n" + values.map { key, value in
                indentation + key.shortKey() + ": " + value.rendered(depth: depth + 1)
            }.joined(separator: ",\n") + "\n" + closing + "]"
        case let .object(name, properties):
            let indentation = String(repeating: "  ", count: depth + 1)
            let closing = String(repeating: "  ", count: depth)
            return name + " {\n" + properties.map { key, value in
                indentation + key.shortKey() + ": " + value.rendered(depth: depth + 1)
            }.joined(separator: ",\n") + "\n" + closing + "}"
        }
    }

    func firstString(forKeysContaining keys: [String]) -> String? {
        switch self {
        case let .array(values), let .object(_, values):
            for (key, value) in values {
                let normalized = key.shortKey().lowercased()
                if keys.contains(where: normalized.contains), case let .string(result) = value {
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
        case let .string(value):
            return value.split(separator: "\0").last.map(String.init) ?? value
        case let .integer(value):
            return String(value)
        default:
            return rendered()
        }
    }
}

enum PHPSerializationError: LocalizedError {
    case malformed
    case unsupported(Character)

    var errorDescription: String? {
        switch self {
        case .malformed: "Malformed PHP serialized value."
        case let .unsupported(type): "Unsupported PHP serialized type: " + String(type)
        }
    }
}

struct PHPSerializationParser {
    private let bytes: [UInt8]
    private var index = 0

    init(data: Data) {
        bytes = Array(data)
    }

    mutating func parse() throws -> PHPSerializedValue {
        guard index < bytes.count else { throw PHPSerializationError.malformed }
        let marker = Character(UnicodeScalar(bytes[index]))
        index += 1
        switch marker {
        case "N":
            try expect(";")
            return .null
        case "b":
            try expect(":")
            let value = try readNumber(until: ";")
            return .bool(value == "1")
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
            guard let count = Int(try readNumber(until: ":")) else { throw PHPSerializationError.malformed }
            try expect("{")
            var values: [(PHPSerializedValue, PHPSerializedValue)] = []
            for _ in 0..<count {
                values.append((try parse(), try parse()))
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
            guard let count = Int(try readNumber(until: ":")) else { throw PHPSerializationError.malformed }
            try expect("{")
            var properties: [(PHPSerializedValue, PHPSerializedValue)] = []
            for _ in 0..<count {
                properties.append((try parse(), try parse()))
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
        guard length >= 0, index + length <= bytes.count else { throw PHPSerializationError.malformed }
        let value = String(decoding: bytes[index..<(index + length)], as: UTF8.self)
        index += length
        return value
    }

    private mutating func readNumber(until delimiter: Character) throws -> String {
        let delimiterByte = UInt8(String(delimiter).utf8.first!)
        let start = index
        while index < bytes.count, bytes[index] != delimiterByte { index += 1 }
        guard index < bytes.count else { throw PHPSerializationError.malformed }
        let value = String(decoding: bytes[start..<index], as: UTF8.self)
        index += 1
        return value
    }

    private mutating func expect(_ character: Character) throws {
        guard let expected = String(character).utf8.first,
              index < bytes.count, bytes[index] == expected else {
            throw PHPSerializationError.malformed
        }
        index += 1
    }
}
