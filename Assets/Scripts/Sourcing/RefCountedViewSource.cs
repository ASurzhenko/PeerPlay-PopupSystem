using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using PeerPlay.Popups.View;
using UnityEngine;

namespace PeerPlay.Popups.Sourcing
{
    /// <summary>
    /// The refcount and the coalescing, with the Addressables call behind <see cref="IPrefabLoader"/>.
    /// Two callers asking for the same asset at the same time produce one load; the handle is released
    /// when the last user of that asset lets go.
    /// </summary>
    public sealed class RefCountedViewSource : IPopupViewSource
    {
        private sealed class Entry
        {
            // PLAIN, never AutoReset: coalescing means several awaiters, and the plain source is the one
            // that keeps a secondary continuation list. Re-awaiting a UniTask itself throws
            // "Already continuation registered", which is exactly the case this exists for.
            internal UniTaskCompletionSource<GameObject> Source;
            internal CancellationTokenSource Cts;
            internal int RefCount;
            internal bool Loaded;
        }

        private readonly IPrefabLoader _loader;
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(8);

        public RefCountedViewSource(IPrefabLoader loader)
        {
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        }

        internal int EntryCount => _entries.Count;

        internal int RefCountOf(string assetId)
        {
            return _entries.TryGetValue(assetId, out Entry entry) ? entry.RefCount : 0;
        }

        public async UniTask<GameObject> AcquirePrefabAsync(string assetId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(assetId))
            {
                throw new PopupLoadException("an empty assetId cannot be acquired");
            }

            bool started = false;

            if (!_entries.TryGetValue(assetId, out Entry entry))
            {
                entry = new Entry
                {
                    Source = new UniTaskCompletionSource<GameObject>(),
                    Cts = new CancellationTokenSource(),
                    RefCount = 1
                };

                _entries[assetId] = entry;
                started = true;
            }
            else
            {
                entry.RefCount++;
            }

            if (started)
            {
                // Deliberately not the caller's token: the load belongs to the entry, so the first caller
                // walking away does not cancel it for everyone still waiting.
                RunLoadAsync(assetId, entry).Forget();
            }

            try
            {
                return await entry.Source.Task.AttachExternalCancellation(ct);
            }
            catch
            {
                // Covers both a caller cancel and a failed load; either way this acquirer is gone and must
                // not leave the count — or the handle — pinned.
                DecrementAndMaybeRelease(assetId, entry);
                throw;
            }
        }

        public void ReleasePrefab(string assetId)
        {
            if (string.IsNullOrEmpty(assetId) || !_entries.TryGetValue(assetId, out Entry entry))
            {
                return;
            }

            DecrementAndMaybeRelease(assetId, entry);
        }

        private async UniTaskVoid RunLoadAsync(string assetId, Entry entry)
        {
            try
            {
                GameObject prefab = await _loader.LoadAsync(assetId, entry.Cts.Token);

                if (prefab == null)
                {
                    Fail(assetId, entry, new PopupLoadException($"'{assetId}' loaded no prefab"));
                    return;
                }

                entry.Loaded = true;
                entry.Source.TrySetResult(prefab);

                // Everyone who wanted it left while it was in flight. Nothing will release it later, so
                // release it now rather than pin the handle for the session.
                if (entry.RefCount <= 0)
                {
                    _entries.Remove(assetId);
                    DisposeEntry(entry);
                    _loader.Release(assetId);
                }
            }
            catch (OperationCanceledException)
            {
                _entries.Remove(assetId);
                DisposeEntry(entry);
                entry.Source.TrySetCanceled();
            }
            catch (Exception e)
            {
                // Faults every coalesced waiter with the same reason, so each request terminates LoadFailed
                // with something a log can act on and the queue keeps moving.
                Fail(assetId, entry, new PopupLoadException($"'{assetId}' failed to load: {e.Message}"));
            }
        }

        private void Fail(string assetId, Entry entry, Exception error)
        {
            // Removed BEFORE the awaiters resume: they resume synchronously and their catch would otherwise
            // find a dead entry still in the table.
            _entries.Remove(assetId);
            DisposeEntry(entry);
            entry.Source.TrySetException(error);
        }

        private void DecrementAndMaybeRelease(string assetId, Entry entry)
        {
            entry.RefCount--;

            if (entry.RefCount > 0)
            {
                return;
            }

            if (!_entries.TryGetValue(assetId, out Entry current) || !ReferenceEquals(current, entry))
            {
                // Already retired by the load's own failure path.
                return;
            }

            _entries.Remove(assetId);

            if (entry.Loaded)
            {
                _loader.Release(assetId);
            }
            else
            {
                // Still in flight and nobody is waiting any more. Cancelling here is what stops a load
                // whose result no one will ever release.
                entry.Cts.Cancel();
            }

            DisposeEntry(entry);
        }

        private static void DisposeEntry(Entry entry)
        {
            if (entry.Cts == null)
            {
                return;
            }

            entry.Cts.Dispose();
            entry.Cts = null;
        }
    }
}
