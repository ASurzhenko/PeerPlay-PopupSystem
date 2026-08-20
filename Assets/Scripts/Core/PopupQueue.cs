using System.Collections.Generic;

namespace PeerPlay.Popups
{
    /// <summary>
    /// The bookkeeping half of the queue: four priority bands, the live map that is the single source of
    /// truth for "does this request exist", an O(1) duplicate index, and the suspend stack. The pump
    /// that drives them lives in <see cref="PopupService"/>.
    ///
    /// The bands hold <b>ids</b>, never request references. Requests are pooled, so a band holding a
    /// reference could hand the next Show a request that is already queued elsewhere — one object in two
    /// slots, opening twice. With ids, a recycled request carries a new id, the stale entry is a
    /// dictionary miss, and that aliasing cannot be expressed at all. Cancelling a pending request
    /// therefore touches no band: the id is purged by the next scan.
    /// </summary>
    internal sealed class PopupQueue
    {
        private const int BandCount = 4;

        private readonly Queue<ulong>[] _bands = new Queue<ulong>[BandCount];
        private readonly Dictionary<ulong, PopupRequest> _live = new Dictionary<ulong, PopupRequest>(16);
        private readonly Dictionary<string, int> _liveByKey = new Dictionary<string, int>(16);
        private readonly List<PopupRequest> _suspendStack = new List<PopupRequest>(4);

        private int _pendingCount;

        internal PopupQueue()
        {
            for (int i = 0; i < BandCount; i++)
            {
                _bands[i] = new Queue<ulong>(8);
            }
        }

        /// <summary>Accepted requests that have not yet reached a terminal.</summary>
        internal int PendingCount => _pendingCount;

        internal int LiveCount => _live.Count;

        internal int LiveKeyCount => _liveByKey.Count;

        internal int SuspendedCount => _suspendStack.Count;

        internal IReadOnlyList<PopupRequest> SuspendStack => _suspendStack;

        internal Dictionary<ulong, PopupRequest>.ValueCollection LiveRequests => _live.Values;

        internal bool AllBandsEmpty
        {
            get
            {
                for (int i = 0; i < BandCount; i++)
                {
                    if (_bands[i].Count > 0)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        internal bool IsKeyLive(string keyId)
        {
            return _liveByKey.TryGetValue(keyId, out int count) && count > 0;
        }

        internal bool TryGet(ulong id, out PopupRequest request)
        {
            return _live.TryGetValue(id, out request);
        }

        internal void Register(PopupRequest request)
        {
            _live[request.Id] = request;
            _liveByKey.TryGetValue(request.KeyId, out int count);
            _liveByKey[request.KeyId] = count + 1;
            _pendingCount++;
            request.IsRegistered = true;
        }

        internal void Unregister(PopupRequest request)
        {
            if (!request.IsRegistered)
            {
                return;
            }

            request.IsRegistered = false;
            _live.Remove(request.Id);
            _pendingCount--;

            if (_liveByKey.TryGetValue(request.KeyId, out int count))
            {
                if (count <= 1)
                {
                    _liveByKey.Remove(request.KeyId);
                }
                else
                {
                    _liveByKey[request.KeyId] = count - 1;
                }
            }
        }

        internal void Enqueue(PopupRequest request)
        {
            _bands[(int)request.Priority].Enqueue(request.Id);
        }

        internal PopupRequest PeekHead()
        {
            for (int band = BandCount - 1; band >= 0; band--)
            {
                Queue<ulong> queue = _bands[band];
                PurgeDead(queue);

                if (queue.Count > 0)
                {
                    return _live[queue.Peek()];
                }
            }

            return null;
        }

        internal PopupRequest DequeueHighest()
        {
            for (int band = BandCount - 1; band >= 0; band--)
            {
                Queue<ulong> queue = _bands[band];
                PurgeDead(queue);

                if (queue.Count > 0)
                {
                    return _live[queue.Dequeue()];
                }
            }

            return null;
        }

        internal void PushSuspended(PopupRequest request)
        {
            _suspendStack.Add(request);
        }

        internal PopupRequest PopSuspended()
        {
            int last = _suspendStack.Count - 1;
            if (last < 0)
            {
                return null;
            }

            PopupRequest request = _suspendStack[last];
            _suspendStack.RemoveAt(last);
            return request;
        }

        internal void RemoveSuspended(PopupRequest request)
        {
            for (int i = _suspendStack.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_suspendStack[i], request))
                {
                    _suspendStack.RemoveAt(i);
                }
            }
        }

        internal void ClearSuspended()
        {
            _suspendStack.Clear();
        }

        internal void ClearBands()
        {
            for (int i = 0; i < BandCount; i++)
            {
                _bands[i].Clear();
            }
        }

        internal void CopyLiveIds(List<ulong> buffer)
        {
            foreach (KeyValuePair<ulong, PopupRequest> entry in _live)
            {
                buffer.Add(entry.Key);
            }
        }

        private void PurgeDead(Queue<ulong> queue)
        {
            while (queue.Count > 0 && !_live.ContainsKey(queue.Peek()))
            {
                queue.Dequeue();
            }
        }
    }
}
