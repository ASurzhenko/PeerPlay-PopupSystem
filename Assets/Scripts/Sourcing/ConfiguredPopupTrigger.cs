using System;

namespace PeerPlay.Popups.Sourcing
{
    /// <summary>
    /// Config-driven priority and sequencing as a call-site decorator instead of a core feature. The core
    /// takes both from ShowOptions at the call site by design, so game code that wants the config to decide
    /// calls this, and game code that decides for itself calls IPopupService directly. Zero core edits, and
    /// both call sites are visible side by side.
    /// </summary>
    public sealed class ConfiguredPopupTrigger
    {
        private readonly IPopupService _service;
        private readonly RemotePopupConfigService _config;

        public ConfiguredPopupTrigger(IPopupService service, RemotePopupConfigService config)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public PopupHandle Show<TData>(in PopupKey<TData> key, in TData data)
        {
            if (!_config.Current.TryGet(key.Id, out PopupRule rule))
            {
                return _service.Show(key, data);
            }

            return _service.Show(key, data, new ShowOptions(rule.Priority, rule.Sequencing));
        }
    }
}
