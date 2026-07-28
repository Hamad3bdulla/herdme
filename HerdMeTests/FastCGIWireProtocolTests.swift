import Foundation
import XCTest

@testable import HerdMe

final class FastCGIWireProtocolTests: XCTestCase {
    func testStreamsResponseAcrossSingleByteChunks() throws {
        let response = Data(
            """
            Status: 200 OK\r
            Content-Type: text/plain\r
            Content-Length: 5\r
            \r
            hello
            """.utf8
        )
        var parser = FastCGIHTTPStreamParser(headOnly: false, allowKeepAlive: true)
        var output = Data()

        for byte in response {
            try parser.consume(Data([byte])) { output.append($0) }
        }
        try parser.finish()

        XCTAssertTrue(parser.didStartResponse)
        XCTAssertTrue(parser.keepsConnectionAlive)
        XCTAssertTrue(String(decoding: output, as: UTF8.self).hasPrefix("HTTP/1.1 200 OK\r\n"))
        XCTAssertTrue(output.suffix(5).elementsEqual(Data("hello".utf8)))
    }

    func testLocationDefaultsToRedirectStatus() throws {
        var parser = FastCGIHTTPStreamParser(headOnly: false)
        var output = Data()
        try parser.consume(Data("Location: /login\nContent-Length: 0\n\n".utf8)) {
            output.append($0)
        }
        try parser.finish()

        XCTAssertTrue(String(decoding: output, as: UTF8.self).hasPrefix("HTTP/1.1 302 Found\r\n"))
    }

    func testRejectsBodyThatDoesNotMatchContentLength() throws {
        var parser = FastCGIHTTPStreamParser(headOnly: false)
        try parser.consume(Data("Content-Length: 10\n\nshort".utf8)) { _ in }

        XCTAssertThrowsError(try parser.finish()) { error in
            guard case LocalFastCGIError.invalidResponse = error else {
                return XCTFail("Expected invalidResponse, received \(error)")
            }
        }
    }
}
