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
        /// The whole-operation budget. The per-request timeout must stay STRICTLY below it, or the deadline
        /// fires first every time: the per-request timeout becomes dead code and the system behaves
        /// correctly for the wrong reason — permanently, and quietly, because both bounds resolve to the
        /// same visible outcome. <see cref="MaxRequestTimeoutSeconds"/> is that ordering, enforced.
        /// </summary>
        internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(8);

        /// <summary>Derived from the deadline, so raising one moves the other instead of silently crossing it.</summary>
        internal static readonly int MaxRequestTimeoutSeconds = (int)Deadline.TotalSeconds - 1;

        private static readonly TimeSpan[] Backoff =
        {
            TimeSpan.FromSeconds(0.5),
            TimeSpan.FromSeconds(1.5)
        };

        /// <summary>
        /// Indexed by <c>attempt</c>, so the table has to hold one entry per gap between attempts. Raising
        /// <see cref="MaxAttempts"/> without extending it is an index error at the third failure, and
        /// nothing in the code says so — which is what the test asserting this equality is for.
        /// </summary>
        internal static int BackoffCount => Backoff.Length;

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

        /// <summary>Below this the request has no per-request bound at all — see <see cref="ClampTimeout"/>.</summary>
        internal const int MinRequestTimeoutSeconds = 1;

        /// <summary>
        /// Holds the per-request timeout inside the range where it can actually fire, and says so when it
        /// has to move it. Both ends matter, and they fail the same way — the inner bound stops existing:
        /// at or above the deadline the deadline always wins first, and <b>zero or less is UnityWebRequest's
        /// "no timeout"</b>, which is the hole this whole clamp exists to close rather than one to leave at
        /// the other end of the range.
        /// </summary>
        internal static int ClampTimeout(int timeoutSeconds, string url)
        {
            if (timeoutSeconds >= MinRequestTimeoutSeconds && timeoutSeconds <= MaxRequestTimeoutSeconds)
            {
                return timeoutSeconds;
            }

            int clamped = timeoutSeconds < MinRequestTimeoutSeconds
                ? MinRequestTimeoutSeconds
                : MaxRequestTimeoutSeconds;

            Debug.LogWarning($"{nameof(HttpWithRetry)}.{nameof(ClampTimeout)} per-request timeout " +
                             $"{timeoutSeconds}s is outside [{MinRequestTimeoutSeconds}, " +
                             $"{MaxRequestTimeoutSeconds}]s, where it can fire before the " +
                             $"{Deadline.TotalSeconds}s deadline; clamped to {clamped}s — {url}");
            return clamped;
        }

        private async UniTask<HttpResult> RunAsync(string url, int rawTimeoutSeconds, bool texture, CancellationToken ct)
        {
            int timeoutSeconds = ClampTimeout(rawTimeoutSeconds, url);

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
