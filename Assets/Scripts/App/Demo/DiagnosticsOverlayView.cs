#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Text;
using PeerPlay.Popups.Sourcing;
using TMPro;
using Unity.Profiling;
using UnityEngine;

namespace PeerPlay.Popups.App.Demo
{
    /// <summary>
    /// The numbers, on screen, sampled the same way every frame so a baseline and a popup row are
    /// comparable. ProfilerRecorder rather than UnityEditor.UnityStats: the latter lives in an Editor
    /// assembly, and this component sits in the same runtime assembly as the composition root.
    ///
    /// Render counters are per-frame, so they are read in LateUpdate — after everything that could dirty a
    /// canvas has run.
    ///
    /// The whole file is compiled out of a release player rather than self-destructing in Awake: this is a
    /// diagnostic surface, and the strongest form of "it does not ship" is that the type does not exist.
    /// Its recorders are IDisposable and are released in OnDisable.
    /// </summary>
    public sealed class DiagnosticsOverlayView : MonoBehaviour
    {
        private const float RefreshInterval = 0.25f;

        [SerializeField] private PopupSystemInstaller _installer;
        [SerializeField] private TMP_Text _text;

        private readonly StringBuilder _builder = new StringBuilder(512);
        private readonly string[] _pooledKeys =
        {
            "info", "confirm", "reward", "offer_weekend", "stress",
            "info_modeless", "info_blocking", "capped_daily",
            "queue_low", "queue_normal", "queue_critical"
        };

        private ProfilerRecorder _drawCalls;
        private ProfilerRecorder _batches;
        private ProfilerRecorder _setPass;
        private ProfilerRecorder _textureMemory;
        private ProfilerRecorder _gcPerFrame;

        private float _nextRefresh;

        /// <summary>Set while a measured run is going: this overlay's own string building would be inside it.</summary>
        internal bool SuspendRefresh { get; set; }

        private void OnEnable()
        {
            _drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            _batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            _setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            _textureMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Texture Memory");
            _gcPerFrame = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
        }

        private void OnDisable()
        {
            _drawCalls.Dispose();
            _batches.Dispose();
            _setPass.Dispose();
            _textureMemory.Dispose();
            _gcPerFrame.Dispose();
        }

        private void LateUpdate()
        {
            if (SuspendRefresh || Time.unscaledTime < _nextRefresh)
            {
                return;
            }

            _nextRefresh = Time.unscaledTime + RefreshInterval;
            Render();
        }

        private void Render()
        {
            _builder.Clear();

            _builder.Append("draw ").Append(_drawCalls.LastValue)
                    .Append("  batches ").Append(_batches.LastValue)
                    .Append("  setpass ").Append(_setPass.LastValue)
                    .Append("  tex ").Append(_textureMemory.LastValue / (1024 * 1024)).Append(" MB")
                    .Append("  gc/frame ").Append(_gcPerFrame.LastValue).AppendLine("B");

            RefCountedViewSource source = _installer.RemoteSource;

            if (source == null)
            {
                _builder.AppendLine("prefabs: local source (no refcounts to hold)");
            }
            else
            {
                _builder.Append("prefabs: entries ").Append(source.EntryCount)
                        .Append("  popup_info ").Append(source.RefCountOf("popup_info"))
                        .Append("  popup_offer ").Append(source.RefCountOf("popup_offer"))
                        .Append("  popup_confirm ").Append(source.RefCountOf("popup_confirm"))
                        .Append("  popup_reward ").Append(source.RefCountOf("popup_reward"))
                        .Append("  popup_blocking ").AppendLine(source.RefCountOf("popup_blocking").ToString());
            }

            AppendPool();

            if (DemoAddressablesMode.Name.Length > 0)
            {
                _builder.Append("Addressables play mode: ").Append(DemoAddressablesMode.Name);
            }

            _text.text = _builder.ToString();
        }

        /// <summary>
        /// Live and idle per key. An idle instance still holds its prefab refcount by design, so idle &gt; 0
        /// with a refcount of the same size is the pool working, not a leak — a non-zero LIVE count with an
        /// empty queue is the leak.
        /// </summary>
        private void AppendPool()
        {
            _builder.Append("pool:");

            for (int i = 0; i < _pooledKeys.Length; i++)
            {
                string key = _pooledKeys[i];
                int live = _installer.Factory.Pool.LiveCount(key);
                int idle = _installer.Factory.Pool.IdleCount(key);

                if (live == 0 && idle == 0)
                {
                    continue;
                }

                _builder.Append(' ').Append(key).Append(' ').Append(live).Append('/').Append(idle);
            }

            _builder.AppendLine();
        }
    }
}
#endif
