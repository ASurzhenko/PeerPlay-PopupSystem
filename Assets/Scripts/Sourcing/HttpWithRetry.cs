using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace PeerPlay.Popups.Sourcing
{
    /// <summary>
    /// Retry with backoff under a whole-operation deadline, and the one place that can tell a deadline
    /// from a teardown.
    /// </summary>
    public sealed class HttpWithRetry : IHttpClient
    {
        internal const int MaxAttempts = 3;

        /// <summary>
        /// The per-request timeout the caller passes must stay STRICTLY below this. Raise it to 8 and the
        /// deadline fires first every time: the per-request timeout becomes dead code, and the system
        /// behaves correctly for the wrong reason — permanently, and quietly, because both bounds resolve
        /// to the same visible outcome.
        /// </summary>
        internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(8);

        private static readonly TimeSpan[] Backoff =
        {
            TimeSpan.FromSeconds(0.5),
            TimeSpan.FromSeconds(1.5)
        };

        private readonly IHttpClient _inner;
        private readonly IDelayProvider _delays;

        public HttpWithRetry(IHttpClient inner, IDelayProvider delays)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _delays = delays ?? throw new ArgumentNullException(nameof(delays));
        }

        public UniTask<HttpResult> GetAsync(string url, int timeoutSeconds, CancellationToken ct)
        {
            return RunAsync(url, timeoutSeconds, false, ct);
        }

        public UniTask<HttpResult> GetTextureAsync(string url, int timeoutSeconds, CancellationToken ct)
        {
            return RunAsync(url, timeoutSeconds, true, ct);
        }

        /// <summary>
        /// A timeout IS retried: it is a transport-class failure, and retrying it is what makes the outer
        /// deadline reachable on the timeout path at all. A 4xx never is.
        /// </summary>
        private static bool IsRetryable(HttpFailure failure)
        {
            return failure == HttpFailure.Transport
                || failure == HttpFailure.Timeout
                || failure == HttpFailure.Http5xx;
        }

        private async UniTask<HttpResult> RunAsync(string url, int timeoutSeconds, bool texture, CancellationToken ct)
        {
            using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (_delays.CancelAfter(cts, Deadline))
            {
                try
                {
                    for (int attempt = 0; ; attempt++)
                    {
                        HttpResult result = texture
                            ? await _inner.GetTextureAsync(url, timeoutSeconds, cts.Token)
                            : await _inner.GetAsync(url, timeoutSeconds, cts.Token);

                        if (result.Ok || !IsRetryable(result.Failure) || attempt >= MaxAttempts - 1)
                        {
                            return result;
                        }

                        Debug.LogWarning($"{nameof(HttpWithRetry)}.{nameof(RunAsync)} [Retry] attempt " +
                                         $"{attempt + 1}/{MaxAttempts} failed ({result.Failure}: {result.Error}); " +
                                         $"waiting {Backoff[attempt].TotalSeconds}s — {url}");

                        await _delays.DelayAsync(Backoff[attempt], cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Cancelling the source makes the in-flight request raise exactly what a caller cancel
                    // raises, so the two are separated here or nowhere: a real teardown propagates, our own
                    // expired budget is a normal result the view answers with its placeholder copy.
                    if (ct.IsCancellationRequested)
                    {
                        throw;
                    }

                    return HttpResult.Fail(HttpFailure.Deadline, "deadline");
                }
            }
        }
    }
}
