#if NATIVE_UI_LIB
using System;
using Ryuka.Sulfur.NativeUI;
using SULFURTogether.Networking;
using UnityEngine;

namespace SULFURTogether.UI
{
    /// <summary>
    /// #26: one host-authoritative session toggle on the connect page, the same behaviour the friendly-fire and loot rows
    /// have: the host edits its own stored value, broadcasts the session settings and toasts; while connected as a
    /// client the row is dimmed and input-blocked and follows the host's synced value, with a line saying so. The four
    /// older rows keep their own code; new session toggles use this.
    /// </summary>
    internal sealed class HostSessionToggleRow
    {
        private readonly BalanceSetting _setting;
        private PerfectRandom.Sulfur.Core.OptionsScreenOption? _option;
        private SulfurTextHandle? _mirrorLine;
        private bool _lockApplied;

        public HostSessionToggleRow(BalanceSetting setting) { _setting = setting; }

        public void Build(SulfurOptionsContext ctx)
        {
            _option = ctx.AddToggle(_setting.Label, _setting.Description, _setting.Stored, OnToggled);
            _mirrorLine = ctx.AddTextRow("");
            _mirrorLine.SetVisible(false);
            _lockApplied = false;
            Tick();
        }

        public void Reset()
        {
            _option = null;
            _mirrorLine = null;
            _lockApplied = false;
        }

        private void OnToggled(bool value)
        {
            if (CoopConnection.CurrentMode == NetMode.Client) { Tick(); return; }
            bool changed = _setting.Stored != value;
            _setting.Stored = value;
            try
            {
                if (CoopConnection.CurrentMode == NetMode.Host)
                {
                    CoopConnection.Service?.BroadcastSessionSettings("ui-toggle");
                    if (changed) CoopToasts.NotifySessionSetting(_setting.Label, value);
                }
            }
            catch (Exception e) { Plugin.Log?.Warn($"[CoopUi] {_setting.LabelFallback} broadcast failed: {e.Message}"); }
        }

        /// <summary>Keep the row role-correct; called from the page tick.</summary>
        public void Tick()
        {
            bool isClient = CoopConnection.CurrentMode == NetMode.Client;
            if (_option != null)
            {
                if (isClient != _lockApplied) { ApplyLock(isClient); _lockApplied = isClient; }
                var checkbox = CoopConnectPage.FfCheckboxField?.GetValue(_option) as UnityEngine.UI.Toggle;
                bool desired = isClient ? _setting.Session : _setting.Stored;
                if (checkbox != null && checkbox.isOn != desired) checkbox.SetIsOnWithoutNotify(desired);
            }
            if (_mirrorLine == null) return;
            if (isClient)
            {
                _mirrorLine.SetText(CoopLoc.Format("connect.sessionValue", "Host's setting: {state}",
                    ("state", _setting.Session ? CoopLoc.Get("common.onUpper", "ON") : CoopLoc.Get("common.offUpper", "OFF"))));
                _mirrorLine.SetColor(CoopConnectPage.NeutralColor);
                _mirrorLine.SetVisible(true);
            }
            else _mirrorLine.SetVisible(false);
        }

        private void ApplyLock(bool locked)
        {
            if (_option == null) return;
            try { CoopConnectPage.FfIsLockedField?.SetValue(_option, locked); } catch { }
            var go = _option.gameObject;
            var cg = go.GetComponent<CanvasGroup>();
            if (cg == null) cg = go.AddComponent<CanvasGroup>();
            cg.alpha = locked ? 0.45f : 1f;
            cg.interactable = !locked;
            cg.blocksRaycasts = !locked;
        }
    }
}
#endif
