using System.Collections.Generic;

namespace PeerPlay.Popups.App.Demo
{
    /// <summary>
    /// What the panel draws, and deliberately NOT a copy of the queue: the core's own introspection is
    /// internal to it, so this is built from the public surface alone — the ledger a game developer could
    /// write.
    ///
    /// Two structures, because a request can terminate without ever having been one:
    ///
    /// * <b>Live rows</b> exist only for a submit that came back with a non-zero handle id. They are keyed
    ///   by that id — a stable key, never a list position.
    /// * <b>The terminal log</b> is fed exclusively by IPopupService.RequestCompleted. A request rejected
    ///   before registration (a duplicate, a refusal, a disposed service) returns default(PopupHandle) and
    ///   raises its terminal synchronously inside Show, before the caller holds anything to record. That
    ///   terminal is still real, and the event is where it is observable.
    /// </summary>
    internal sealed class DemoRequestLedger
    {
        internal const int LogCapacity = 6;

        internal struct LiveRow
        {
            internal ulong Id;
            internal string KeyId;
            internal PopupPriority Priority;
            internal PopupSequencing Sequencing;
            internal PopupHandle Handle;
            internal string Note;
        }

        private readonly List<LiveRow> _rows = new List<LiveRow>(8);
        private readonly List<string> _log = new List<string>(LogCapacity);

        internal IReadOnlyList<LiveRow> Rows => _rows;

        /// <summary>Newest last.</summary>
        internal IReadOnlyList<string> Log => _log;

        internal void Track(in PopupHandle handle, string keyId, PopupPriority priority,
                            PopupSequencing sequencing, string note)
        {
            if (handle.Id == 0UL)
            {
                // Rejected before registration. Its terminal already went through the event.
                return;
            }

            _rows.Add(new LiveRow
            {
                Id = handle.Id,
                KeyId = keyId,
                Priority = priority,
                Sequencing = sequencing,
                Handle = handle,
                Note = note
            });
        }

        internal void OnCompleted(in PopupCompletion completion)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Id == completion.Id)
                {
                    _rows.RemoveAt(i);
                    break;
                }
            }

            PopupResult result = completion.Result;
            string reason = result.Reason ?? result.Action;

            Append(reason == null
                ? $"#{completion.Id} {completion.KeyId} → {result.Outcome}"
                : $"#{completion.Id} {completion.KeyId} → {result.Outcome} ({reason})");
        }

        internal void Note(string line)
        {
            Append(line);
        }

        internal void Clear()
        {
            _rows.Clear();
            _log.Clear();
        }

        private void Append(string line)
        {
            _log.Add(line);

            if (_log.Count > LogCapacity)
            {
                _log.RemoveAt(0);
            }
        }
    }
}
