using System.Net;

namespace HerdMe.Windows.Services;

public static class ManagedDownloadClient
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);
    public const int DefaultMaximumAttempts = 3;

    public static HttpClient Create(TimeSpan? timeout = null)
    {
        return Create(new HttpClientHandler(), timeout);
    }

    public static HttpClient Create(
        HttpMessageHandler primaryHandler,
        TimeSpan? timeout = null,
        int maximumAttempts = DefaultMaximumAttempts,
        Func<int, TimeSpan>? delayFactory = null
    )
    {
        ArgumentNullException.ThrowIfNull(primaryHandler);
        if (maximumAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));

        var client = new HttpClient(new RetryingHttpMessageHandler(
            primaryHandler,
            maximumAttempts,
            delayFactory
        ))
        {
            Timeout = timeout ?? DefaultTimeout
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HerdMe/1.0 (+https://github.com/Hamad3bdulla/herdme)");
        return client;
    }
}

internal sealed class RetryingHttpMessageHandler : DelegatingHandler
{
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);
    private readonly int maximumAttempts;
    private readonly Func<int, TimeSpan> delayFactory;

    public RetryingHttpMessageHandler(
        HttpMessageHandler innerHandler,
        int maximumAttempts,
        Func<int, TimeSpan>? delayFactory
    ) : base(innerHandler)
    {
        this.maximumAttempts = maximumAttempts;
        this.delayFactory = delayFactory ?? (attempt => TimeSpan.FromMilliseconds(
            Math.Min(30_000, 500 * Math.Pow(2, attempt - 1))
        ));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        if (!IsRetryable(request))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            using var attemptRequest = Clone(request);
            try
            {
                var response = await base.SendAsync(attemptRequest, cancellationToken);
                if (attempt == maximumAttempts || !ShouldRetry(response.StatusCode))
                {
                    response.RequestMessage = request;
                    return response;
                }

                var delay = RetryAfter(response) ?? delayFactory(attempt);
                response.Dispose();
                await DelayAsync(delay, cancellationToken);
            }
            catch (HttpRequestException) when (
                attempt < maximumAttempts && !cancellationToken.IsCancellationRequested
            )
            {
                await DelayAsync(delayFactory(attempt), cancellationToken);
            }
            catch (IOException) when (
                attempt < maximumAttempts && !cancellationToken.IsCancellationRequested
            )
            {
                await DelayAsync(delayFactory(attempt), cancellationToken);
            }
        }

        throw new InvalidOperationException("The managed download retry loop ended unexpectedly.");
    }

    private static bool IsRetryable(HttpRequestMessage request)
    {
        return request.Content is null
            && (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head);
    }

    private static bool ShouldRetry(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code is 408 or 425 or 429 || code is >= 500 and <= 599;
    }

    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clone;
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is TimeSpan delta)
        {
            return BoundDelay(delta);
        }
        if (retryAfter?.Date is DateTimeOffset date)
        {
            return BoundDelay(date - DateTimeOffset.UtcNow);
        }
        return null;
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        delay = BoundDelay(delay);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static TimeSpan BoundDelay(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return TimeSpan.Zero;
        return delay > MaximumRetryDelay ? MaximumRetryDelay : delay;
    }
}
