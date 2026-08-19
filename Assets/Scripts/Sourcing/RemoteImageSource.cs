using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using PeerPlay.Popups.View;
using UnityEngine;

namespace PeerPlay.Popups.Sourcing
{
    /// <summary>
    /// Remote popup art, fetched at display time, coalesced per URL, bounded in flight and bounded in
    /// memory.
    ///
    /// Per-URL coalescing deduplicates and bounds nothing across different URLs, so the concurrency bound
    /// is a SemaphoreSlim(2) and not a claim about the dedupe.
    /// </summary>
    public sealed class RemoteImageSource : IRemoteImageSource, IDisposable
    {
        internal const int TimeoutSeconds = 5;
        private const int MaxConcurrentFetches = 2;

        private sealed class Entry
        {
            internal UniTaskCompletionSource<Sprite> Source;
            internal CancellationTokenSource Cts;
            internal int Waiters;
        }

        private readonly IHttpClient _http;
        private readonly SpriteLruCache _cache;
        private readonly SemaphoreSlim _concurrency = new SemaphoreSlim(MaxConcurrentFetches);
        private readonly Dictionary<string, Entry> _inFlight = new Dictionary<string, Entry>(4);

        private bool _disposed;

        public RemoteImageSource(IHttpClient http, SpriteLruCache cache)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        internal int InFlightCount => _inFlight.Count;

        public async UniTask<Sprite> LoadAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url) || _disposed)
            {
                return null;
            }

            ct.ThrowIfCancellationRequested();

            if (_cache.TryTake(url, out Sprite cached))
            {
                return cached;
            }

            bool started = false;

            if (!_inFlight.TryGetValue(url, out Entry entry))
            {
                entry = new Entry
                {
                    Source = new UniTaskCompletionSource<Sprite>(),
                    Cts = new CancellationTokenSource(),
                    Waiters = 1
                };

                _inFlight[url] = entry;
                started = true;
            }
            else
            {
                entry.Waiters++;
            }

            if (started)
            {
                // Not the caller's token: one of several coalesced callers walking away must not abort the
                // fetch the others are still waiting on. The entry's own source is cancelled when the LAST
                // waiter leaves, which is what makes the request itself cancellable.
                FetchAsync(url, entry).Forget();
            }

            try
            {
                return await entry.Source.Task.AttachExternalCancellation(ct);
            }
            catch (OperationCanceledException)
            {
                LeaveWaiter(url, entry);
                throw;
            }
        }

        public void ReturnLease(Sprite sprite)
        {
            _cache.Release(sprite);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (KeyValuePair<string, Entry> pair in _inFlight)
            {
                pair.Value.Cts?.Cancel();
                pair.Value.Cts?.Dispose();
                pair.Value.Source.TrySetCanceled();
            }

            _inFlight.Clear();
            _cache.Clear();
            _concurrency.Dispose();
        }

        private async UniTaskVoid FetchAsync(string url, Entry entry)
        {
            try
            {
                await _concurrency.WaitAsync(entry.Cts.Token);
            }
            catch (OperationCanceledException)
            {
                Retire(url, entry);
                entry.Source.TrySetCanceled();
                return;
            }

            try
            {
                HttpResult result = await _http.GetTextureAsync(url, TimeoutSeconds, entry.Cts.Token);

                if (!result.Ok || result.Texture == null)
                {
                    // Transport, HTTP and deadline all arrive here, and all three are a normal answer the
                    // view renders as its placeholder copy — not an exception, and not a stalled popup.
                    Debug.LogWarning($"{nameof(RemoteImageSource)}.{nameof(FetchAsync)} [Content] {url} " +
                                     $"failed ({result.Failure}: {result.Error})");
                    Retire(url, entry);
                    entry.Source.TrySetResult(null);
                    return;
                }

                Texture2D texture = result.Texture;
                Sprite sprite = Sprite.Create(texture,
                                              new Rect(0f, 0f, texture.width, texture.height),
                                              new Vector2(0.5f, 0.5f));

                int leases = Mathf.Max(1, entry.Waiters);
                _cache.Add(url, sprite, texture, leases);

                Retire(url, entry);
                entry.Source.TrySetResult(sprite);
            }
            catch (OperationCanceledException)
            {
                Retire(url, entry);
                entry.Source.TrySetCanceled();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Retire(url, entry);
                entry.Source.TrySetResult(null);
            }
            finally
            {
                if (!_disposed)
                {
                    _concurrency.Release();
                }
            }
        }

        /// <summary>The last waiter leaving cancels the fetch; anyone else leaving only stops counting.</summary>
        private void LeaveWaiter(string url, Entry entry)
        {
            entry.Waiters--;

            if (entry.Waiters > 0)
            {
                return;
            }

            if (_inFlight.TryGetValue(url, out Entry current) && ReferenceEquals(current, entry))
            {
                entry.Cts?.Cancel();
            }
        }

        private void Retire(string url, Entry entry)
        {
            if (_inFlight.TryGetValue(url, out Entry current) && ReferenceEquals(current, entry))
            {
                _inFlight.Remove(url);
            }

            entry.Cts?.Dispose();
            entry.Cts = null;
        }
    }
}
