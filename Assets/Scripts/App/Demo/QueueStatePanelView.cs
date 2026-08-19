using System.Collections.Generic;
using System.Text;
using PeerPlay.Popups.Sourcing;
using PeerPlay.Popups.View;
using TMPro;
using UnityEngine;

namespace PeerPlay.Popups.App.Demo
{
    /// <summary>
    /// The queue as the public surface can describe it: the occupant, how many requests the system is
    /// holding, one line per live request and the last few terminals. Rows are pre-authored and never
    /// instantiated at runtime, so the panel itself does not allocate while a measured run is going on.
    /// </summary>
    public sealed class QueueStatePanelView : MonoBehaviour
    {
        private const float RefreshInterval = 0.25f;

        [SerializeField] private PopupSystemInstaller _installer;
        [SerializeField] private DemoDirector _director;
        [SerializeField] private PopupLayer _layer;
        [SerializeField] private TMP_Text _header;
        [SerializeField] private TMP_Text[] _rows;
        [SerializeField] private TMP_Text _log;

        private readonly StringBuilder _builder = new StringBuilder(256);

        private float _nextRefresh;

        /// <summary>Set while a measured run is going, so the panel is not part of what is measured.</summary>
        internal bool SuspendRefresh { get; set; }

        private void Update()
        {
            if (SuspendRefresh || Time.unscaledTime < _nextRefresh)
            {
                return;
            }

            _nextRefresh = Time.unscaledTime + RefreshInterval;
            Refresh();
        }

        private void Refresh()
        {
            IPopupService service = _installer.Service;

            _header.text = $"occupant: {service.CurrentKeyId ?? "-"} ({service.CurrentState})    " +
                           $"pending: {service.PendingCount}";

            IReadOnlyList<DemoRequestLedger.LiveRow> rows = _director.Ledger.Rows;

            for (int i = 0; i < _rows.Length; i++)
            {
                _rows[i].text = i < rows.Count ? Describe(rows[i]) : string.Empty;
            }

            _builder.Clear();
            IReadOnlyList<string> log = _director.Ledger.Log;

            for (int i = 0; i < log.Count; i++)
            {
                _builder.AppendLine(log[i]);
            }

            _log.text = _builder.ToString();
        }

        private string Describe(in DemoRequestLedger.LiveRow row)
        {
            PopupState state = row.Handle.State;

            _builder.Clear();
            _builder.Append('#').Append(row.Id).Append(' ').Append(row.KeyId)
                    .Append("  ").Append(state)
                    .Append("  ").Append(row.Priority).Append('/').Append(row.Sequencing);

            if (!string.IsNullOrEmpty(row.Note))
            {
                _builder.Append("  ").Append(row.Note);
            }

            if (state == PopupState.Suspended)
            {
                PopupView view = FindView(row.KeyId);

                if (view != null)
                {
                    _builder.Append("  [").Append(view.SuspendBehaviour).Append(']');
                }
            }

            // Nothing pumps on a config adoption, and while the slot is occupied the pump breaks on a Queue
            // head before it reaches the policy — so a kill switch published now is applied when this row is
            // admitted, not when it is published. Saying so is the difference between "the beat did nothing"
            // and "the beat did what it claims".
            if (state == PopupState.Pending
                && _installer.Config.Current.TryGet(row.KeyId, out PopupRule rule)
                && rule.Disabled)
            {
                _builder.Append("  ← re-evaluated on admission");
            }

            return _builder.ToString();
        }

        private PopupView FindView(string keyId)
        {
            IReadOnlyList<PopupView> views = _layer.LiveViews;

            for (int i = 0; i < views.Count; i++)
            {
                if (views[i] != null && views[i].KeyId == keyId)
                {
                    return views[i];
                }
            }

            return null;
        }
    }
}
