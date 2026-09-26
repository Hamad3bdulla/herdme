import Foundation
import XCTest

@testable import HerdMe

final class SMTPLoopbackTests: XCTestCase {
    func testLoopbackSessionCapturesMessageAndResetsEnvelope() throws {
        let port = try XCTUnwrap(LocalEnvironmentEngine.availablePort(startingAt: 35_000))
        let ready = expectation(description: "SMTP listener ready")
        let received = expectation(description: "SMTP message captured")
        let server = SMTPServer()
        defer { server.stop() }
        try server.start(
            port: port,
            onStateChange: { running, error in
                if running { ready.fulfill() }
                XCTAssertNil(error)
            },
            onMessage: { message in
                XCTAssertEqual(message.sender, "sender@fixture.test")
                XCTAssertEqual(message.recipients, ["inbox@fixture.test"])
                XCTAssertEqual(message.subject, "Loopback fixture")
                XCTAssertTrue(message.raw.contains(".dot-stuffed"))
                received.fulfill()
            }
        )
        wait(for: [ready], timeout: 5)
        XCTAssertTrue(server.isRunning)
        let script = #"""
            require 'socket'
            socket = TCPSocket.new('127.0.0.1', ARGV.fetch(0).to_i)
            def reply(socket, code)
              loop do
                line = socket.gets or abort 'SMTP connection closed'
                abort line unless line.start_with?(code)
                break if line[3] == ' '
              end
            end
            def command(socket, text, code)
              socket.write(text + "\r\n")
              reply(socket, code)
            end
            reply(socket, '220')
            command(socket, 'EHLO fixture.test', '250')
            command(socket, 'NOOP', '250')
            command(socket, 'UNKNOWN', '502')
            command(socket, 'MAIL FROM:<discarded@fixture.test>', '250')
            command(socket, 'RCPT TO:<discarded@fixture.test>', '250')
            command(socket, 'RSET', '250')
            command(socket, 'MAIL FROM:<sender@fixture.test>', '250')
            command(socket, 'RCPT TO:<inbox@fixture.test>', '250')
            command(socket, 'DATA', '354')
            socket.write("Subject: Loopback fixture\r\nContent-Type: text/plain\r\n\r\n..dot-stuffed\r\n.\r\n")
            reply(socket, '250')
            command(socket, 'QUIT', '221')
            socket.close
            """#
        let result = try ProcessRunner.run(
            URL(fileURLWithPath: "/usr/bin/ruby"), arguments: ["-e", script, String(port)], timeout: 5
        )
        XCTAssertEqual(result.status, 0, result.output)
        wait(for: [received], timeout: 5)
        server.stop()
        XCTAssertFalse(server.isRunning)
    }
}
