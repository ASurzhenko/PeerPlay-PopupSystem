using System;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using PeerPlay.Popups.Sourcing;
using PeerPlay.Popups.View;
using TMPro;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.UI;

namespace PeerPlay.Popups.App.Demo
{
    /// <summary>
    /// Numbers instead of adjectives. Everyone claims "no leaks, minimal GC, no draw-call spikes"; this
    /// runs the cycles in front of the reviewer and prints what they cost.
    ///
    /// Three things decide whether the numbers mean anything:
    ///
    /// * <b>The first open of a key is never representative</b> — it instantiates, resolves the Addressables
    ///   handle, warms the TMP atlas and compiles the shader variant. Three warm-up cycles per key are run
    ///   and discarded.
    /// * <b>The stress key is Modeless AND instant.</b> For a Modal popup both ends of the transition are a
    ///   WhenAll with the layer's backdrop fade (0.2 s), so an "instant" transition on a modal key still
    ///   costs 0.2 s per end and still allocates a tween and a token source. Two rows measuring the same
    ///   thing would answer nothing; the modeless key skips the backdrop call entirely.
    /// * <b>The measurement must not allocate.</b> Samples go into pre-sized arrays, the panel and the
    ///   diagnostics overlay stop refreshing for the duration, and the table is formatted after the last
    ///   cycle.
    ///
    /// The allocation instrument is the profiler's "GC Allocated In Frame" counter, accumulated over the
    /// frames a cycle spans, minus what those frames cost while idle.
    /// <c>GC.GetAllocatedBytesForCurrentThread</c> compiles and is the obvious choice, but it returns a
    /// constant <b>0</b> on this editor's Mono runtime — the method exists and is not implemented, which a
    /// check for its presence cannot tell you.
    ///
    /// The idle subtraction is what makes the rows comparable rather than a measure of how long each cycle
    /// keeps the app alive: a modal cycle spans about twenty-four frames of backdrop and transition and an
    /// instant modeless one spans two, so the raw totals differ by the frame count before they differ by
    /// anything about popups. The published number is still whole-main-thread, not a claim that every byte
    /// belongs to the popup.
    /// </summary>
    public sealed class StressRunner : MonoBehaviour
    {
        private const int WarmUpCycles = 3;
        private const int ModelessInstantCycles = 50;
        private const int MixedModalCycles = 51;
        private const int FadeCycles = 20;

        /// <summary>Per cycle. A cycle that cannot finish inside this aborts the run and says which one.</summary>
        private const float CycleCeilingSeconds = 5f;

        [SerializeField] private PopupSystemInstaller _installer;
        [SerializeField] private QueueStatePanelView _panel;

        // The overlay type is compiled out of a release player, so the field that points at it has to be
        // too — otherwise this file stops compiling in the build where the overlay does not exist.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        [SerializeField] private DiagnosticsOverlayView _overlay;
#endif

        [SerializeField] private TMP_Text _output;
        [SerializeField] private Button _runButton;

        private readonly long[] _modelessSamples = new long[ModelessInstantCycles];
        private readonly long[] _mixedSamples = new long[MixedModalCycles];
        private readonly long[] _fadeSamples = new long[FadeCycles];
        private readonly StringBuilder _builder = new StringBuilder(1024);

        private ProfilerRecorder _gcInFrame;

        /// <summary>
        /// What an idle frame costs before any popup is involved. A modal cycle spans ~24 frames of backdrop
        /// and transition, and without this the number it publishes is mostly the editor's own per-frame
        /// churn — a table that says a fade costs ten times an instant open when what differs is how long
        /// each one keeps the app alive.
        /// </summary>
        private long _idlePerFrame;

        private bool _running;
        private string _abortReason;

        private IPopupService Service => _installer.Service;

        private void Start()
        {
            if (_runButton != null)
            {
                _runButton.onClick.AddListener(Run);
            }
        }

        private void OnDestroy()
        {
            if (_runButton != null)
            {
                _runButton.onClick.RemoveListener(Run);
            }
        }

        private void Run()
        {
            if (_running)
            {
                return;
            }

            RunAsync(this.GetCancellationTokenOnDestroy()).Forget();
        }

        private async UniTaskVoid RunAsync(CancellationToken ct)
        {
            _running = true;
            _abortReason = null;
            _panel.SuspendRefresh = true;
            SetOverlayRefresh(false);

            ProfilerRecorder drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            _gcInFrame = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");

            try
            {
                // Three frames, not one: a recorder started this frame has no last value yet, and a draw-call
                // baseline read before anything has rendered is a zero that flatters the after-run number.
                await UniTask.Yield(PlayerLoopTiming.Update, ct);
                await UniTask.Yield(PlayerLoopTiming.Update, ct);
                await UniTask.Yield(PlayerLoopTiming.Update, ct);
                long drawCallsBaseline = drawCalls.LastValue;

                Report("measuring the idle frame…");
                await MeasureIdleFrameAsync(ct);

                Report("warming up…");
                await WarmUpAsync(ct);

                Report($"modeless + instant ×{ModelessInstantCycles}…");
                await RunPhaseAsync(DemoKeys.Stress, new InfoData("stress"), _modelessSamples, ct);

                Report($"mixed modal ×{MixedModalCycles}…");
                await RunMixedModalAsync(ct);

                Report($"modal + fade ×{FadeCycles}…");
                await RunPhaseAsync(PopupKeys.Info, new InfoData("fade"), _fadeSamples, ct);

                await UniTask.Yield(PlayerLoopTiming.Update, ct);
                Publish(drawCallsBaseline, drawCalls.LastValue);
            }
            catch (OperationCanceledException)
            {
                // Play mode ended mid-run.
            }
            finally
            {
                drawCalls.Dispose();
                _gcInFrame.Dispose();
                _panel.SuspendRefresh = false;
                SetOverlayRefresh(true);
                _running = false;
            }
        }

        private void SetOverlayRefresh(bool enabled)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_overlay != null)
            {
                _overlay.SuspendRefresh = !enabled;
            }
#endif
        }

        private async UniTask MeasureIdleFrameAsync(CancellationToken ct)
        {
            const int frames = 30;
            long total = 0L;

            for (int i = 0; i < frames; i++)
            {
                await UniTask.Yield(PlayerLoopTiming.Update, ct);
                total += _gcInFrame.LastValue;
            }

            _idlePerFrame = total / frames;
        }

        private async UniTask WarmUpAsync(CancellationToken ct)
        {
            for (int i = 0; i < WarmUpCycles; i++)
            {
                await CycleAsync(DemoKeys.Stress, new InfoData("warm-up"), ct);
                await CycleAsync(PopupKeys.Info, new InfoData("warm-up"), ct);
                await CycleAsync(PopupKeys.Confirm, new ConfirmData("warm-up"), ct);
                await CycleAsync(PopupKeys.Reward, new RewardData(1, "coins"), ct);

                if (_abortReason != null)
                {
                    return;
                }
            }
        }

        private async UniTask RunPhaseAsync<TData>(PopupKey<TData> key, TData data, long[] samples,
                                                   CancellationToken ct)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = await CycleAsync(key, data, ct);

                if (_abortReason != null)
                {
                    return;
                }
            }
        }

        private async UniTask RunMixedModalAsync(CancellationToken ct)
        {
            for (int i = 0; i < MixedModalCycles; i++)
            {
                int which = i % 3;

                _mixedSamples[i] = which == 0
                    ? await CycleAsync(PopupKeys.Info, new InfoData("mixed"), ct)
                    : which == 1
                        ? await CycleAsync(PopupKeys.Confirm, new ConfirmData("mixed"), ct)
                        : await CycleAsync(PopupKeys.Reward, new RewardData(10, "coins"), ct);

                if (_abortReason != null)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Terminal-driven, not state-polled to completion: the cycle waits for the popup to own the slot,
        /// closes it, and then awaits the request's own outcome. Any outcome other than Completed aborts the
        /// whole run and names itself — a run that silently skipped half its cycles would still print a
        /// plausible table.
        /// </summary>
        private async UniTask<long> CycleAsync<TData>(PopupKey<TData> key, TData data, CancellationToken ct)
        {
            long allocated = 0L;
            int frames = 0;

            UniTask<PopupResult> pending = Service.ShowAsync(key, data, default, ct);
            float ceiling = Time.realtimeSinceStartup + CycleCeilingSeconds;

            while (Service.CurrentKeyId != key.Id || Service.CurrentState != PopupState.Active)
            {
                if (Time.realtimeSinceStartup > ceiling)
                {
                    _abortReason = $"'{key.Id}' did not reach Active within {CycleCeilingSeconds:0}s";
                    Service.ForceCloseAll();
                    await pending;
                    return 0L;
                }

                await UniTask.Yield(PlayerLoopTiming.Update, ct);
                allocated += _gcInFrame.LastValue;
                frames++;
            }

            Service.CloseTop("stress");
            PopupResult result = await pending;

            // One more frame, because the terminal and the release happen in the frame this one closes.
            await UniTask.Yield(PlayerLoopTiming.Update, ct);
            allocated += _gcInFrame.LastValue;
            frames++;

            if (result.Outcome != PopupOutcome.Completed)
            {
                _abortReason = $"'{key.Id}' terminated {result.Outcome} ({result.Reason ?? "no reason"})";
                return 0L;
            }

            // Above idle: what the cycle cost beyond what those frames would have cost anyway.
            return allocated - frames * _idlePerFrame;
        }

        private void Publish(long drawCallsBaseline, long drawCallsAfter)
        {
            _builder.Clear();

            if (_abortReason != null)
            {
                _builder.Append("RUN ABORTED — ").AppendLine(_abortReason);
            }
            else
            {
                _builder.AppendLine($"warm-up: {WarmUpCycles} cycles per key, discarded");
                _builder.AppendLine("bytes = main-thread GC alloc over the cycle's frames, minus what those " +
                                    $"frames cost idle ({_idlePerFrame} B/frame)");
                AppendRow("modeless + instant (no backdrop tween)", _modelessSamples);
                AppendRow("mixed modal (info/confirm/reward, fade+scale)", _mixedSamples);
                AppendRow("modal + fade (transition + backdrop tween)", _fadeSamples);
            }

            _builder.AppendLine($"draw calls: baseline {drawCallsBaseline} → after {drawCallsAfter}");
            _builder.AppendLine($"pending after the run: {Service.PendingCount}");

            RefCountedViewSource source = _installer.RemoteSource;

            if (source != null)
            {
                _builder.AppendLine($"prefab entries: {source.EntryCount}  " +
                                    $"popup_info {source.RefCountOf("popup_info")}  " +
                                    $"popup_confirm {source.RefCountOf("popup_confirm")}  " +
                                    $"popup_reward {source.RefCountOf("popup_reward")}");
            }

            _builder.AppendLine($"pool live/idle — info {_installer.Factory.Pool.LiveCount("info")}/" +
                                $"{_installer.Factory.Pool.IdleCount("info")}  " +
                                $"stress {_installer.Factory.Pool.LiveCount("stress")}/" +
                                $"{_installer.Factory.Pool.IdleCount("stress")}");

            string table = _builder.ToString();
            _output.text = table;
            GUIUtility.systemCopyBuffer = table;
        }

        private void AppendRow(string label, long[] samples)
        {
            long total = 0L;
            long max = 0L;

            for (int i = 0; i < samples.Length; i++)
            {
                total += samples[i];

                if (samples[i] > max)
                {
                    max = samples[i];
                }
            }

            _builder.AppendLine($"{label} ×{samples.Length}: mean {total / samples.Length} B  max {max} B");
        }

        private void Report(string line)
        {
            if (_output != null)
            {
                _output.text = line;
            }
        }
    }
}
