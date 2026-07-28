import Foundation

enum ManagedDownloadClient {
    static let defaultMaximumAttempts = 3
    static let defaultTimeout: TimeInterval = 10 * 60

    private static let maximumRetryDelayNanoseconds: UInt64 = 30_000_000_000

    static let sharedSession: URLSession = {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 60
        configuration.timeoutIntervalForResource = defaultTimeout
        configuration.waitsForConnectivity = false
        configuration.httpAdditionalHeaders = [
            "User-Agent": "HerdMe/1.0 (+https://github.com/Hamad3bdulla/herdme)"
        ]
        return URLSession(configuration: configuration)
    }()

    static func data(
        from url: URL,
        session: URLSession? = nil,
        maximumAttempts: Int = defaultMaximumAttempts,
        baseRetryDelayNanoseconds: UInt64 = 500_000_000
    ) async throws -> (Data, URLResponse) {
        let attempts = max(1, maximumAttempts)
        let activeSession = session ?? sharedSession

        for attempt in 1...attempts {
            do {
                let result = try await activeSession.data(from: url)
                guard attempt < attempts,
                    let response = result.1 as? HTTPURLResponse,
                    shouldRetry(statusCode: response.statusCode)
                else {
                    return result
                }
                try await waitBeforeRetry(
                    attempt: attempt,
                    response: response,
                    baseDelayNanoseconds: baseRetryDelayNanoseconds
                )
            } catch {
                guard attempt < attempts,
                    !Task.isCancelled,
                    isTransient(error)
                else {
                    throw error
                }
                try await waitBeforeRetry(
                    attempt: attempt,
                    response: nil,
                    baseDelayNanoseconds: baseRetryDelayNanoseconds
                )
            }
        }

        throw URLError(.unknown)
    }

    static func download(
        from url: URL,
        session: URLSession? = nil,
        maximumAttempts: Int = defaultMaximumAttempts,
        baseRetryDelayNanoseconds: UInt64 = 500_000_000
    ) async throws -> (URL, URLResponse) {
        let attempts = max(1, maximumAttempts)
        let activeSession = session ?? sharedSession

        for attempt in 1...attempts {
            do {
                let result = try await activeSession.download(from: url)
                guard attempt < attempts,
                    let response = result.1 as? HTTPURLResponse,
                    shouldRetry(statusCode: response.statusCode)
                else {
                    return result
                }
                try? FileManager.default.removeItem(at: result.0)
                try await waitBeforeRetry(
                    attempt: attempt,
                    response: response,
                    baseDelayNanoseconds: baseRetryDelayNanoseconds
                )
            } catch {
                guard attempt < attempts,
                    !Task.isCancelled,
                    isTransient(error)
                else {
                    throw error
                }
                try await waitBeforeRetry(
                    attempt: attempt,
                    response: nil,
                    baseDelayNanoseconds: baseRetryDelayNanoseconds
                )
            }
        }

        throw URLError(.unknown)
    }

    static func shouldRetry(statusCode: Int) -> Bool {
        statusCode == 408
            || statusCode == 425
            || statusCode == 429
            || 500...599 ~= statusCode
    }

    private static func isTransient(_ error: Error) -> Bool {
        if error is CancellationError { return false }
        guard let error = error as? URLError else { return false }
        switch error.code {
        case .timedOut,
            .cannotFindHost,
            .cannotConnectToHost,
            .networkConnectionLost,
            .dnsLookupFailed,
            .notConnectedToInternet,
            .resourceUnavailable,
            .internationalRoamingOff,
            .callIsActive,
            .dataNotAllowed:
            return true
        default:
            return false
        }
    }

    private static func waitBeforeRetry(
        attempt: Int,
        response: HTTPURLResponse?,
        baseDelayNanoseconds: UInt64
    ) async throws {
        let delay: UInt64
        if let retryAfter = response?.value(forHTTPHeaderField: "Retry-After"),
            let seconds = UInt64(retryAfter.trimmingCharacters(in: .whitespacesAndNewlines))
        {
            let (nanoseconds, overflow) = seconds.multipliedReportingOverflow(by: 1_000_000_000)
            delay = min(
                overflow ? maximumRetryDelayNanoseconds : nanoseconds,
                maximumRetryDelayNanoseconds
            )
        } else {
            let multiplier = UInt64(1 << min(max(attempt - 1, 0), 5))
            let (nanoseconds, overflow) = baseDelayNanoseconds.multipliedReportingOverflow(
                by: multiplier
            )
            delay = min(
                overflow ? maximumRetryDelayNanoseconds : nanoseconds,
                maximumRetryDelayNanoseconds
            )
        }
        if delay > 0 {
            try await Task<Never, Never>.sleep(nanoseconds: delay)
        }
    }
}
