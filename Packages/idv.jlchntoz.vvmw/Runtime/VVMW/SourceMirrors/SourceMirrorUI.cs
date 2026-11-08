using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using VRC.SDKBase;
using VRC.SDK3.UdonNetworkCalling;
using JLChnToZ.VRC.Foundation;
using JLChnToZ.VRC.Foundation.I18N;

namespace JLChnToZ.VRC.VVMW {
    /// <summary>Optional personal controls. All media commands remain with the existing controller.</summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    [DisallowMultipleComponent]
    [AddComponentMenu("VizVid/Source Mirrors/Personal Source UI")]
    public class SourceMirrorUI : UdonSharpBehaviour {
        public SourceMirrorController mirrors;
        [BindUdonSharpEvent(nameof(_OnLanguageChanged))] public LanguageManager languageManager;
        [Tooltip("Fallback language when no LanguageManager is configured: 0 English, 1 Russian, 2 Japanese.")]
        public int fallbackLanguage;
        public GameObject panel;
        public Button entryButton, autoButton, retryButton, enableButton;
        public Button[] sourceButtons;
        public GameObject autoSelected;
        public GameObject[] sourceSelected;
        public Graphic entryText, titleText, scopeText, autoText, retryText, enableText;
        public Graphic modeText, actualText, statusText, feedbackText;
        public Graphic[] sourceTexts;

        public const int NotConfigured = 0, Off = 1, Idle = 2, NoCatalog = 3,
            Loading = 4, Ready = 5, Error = 6, Exhausted = 7, TurningOff = 8,
            Blocked = 9, Unsupported = 10, NotListed = 11;
        int viewState, language;
        bool tickQueued, bound, canChoose;
        string shownVersion = "", shownPlayback = "", actualId = "";
        string[] shownIds = new string[0], shownLabels = new string[0];
        string feedback = "", feedbackVersion = "", feedbackPlayback = "";
        int pressedAction = -1;
        string pressedId = "", pressedVersion = "", pressedPlayback = "";

        public int ViewState => viewState;
        public string ActualSourceId => actualId;
        public string DisplayedVersion => shownVersion;
        public string DisplayedPlaybackId => shownPlayback;
        public bool IsOpen => Utilities.IsValid(panel) && panel.activeInHierarchy;

        bool LocalAction() => !NetworkCalling.InNetworkCall && enabled && gameObject.activeInHierarchy;
        bool PanelAction() => LocalAction() && IsOpen;
        bool HasController() => Utilities.IsValid(mirrors) && Utilities.IsValid(mirrors.core);
        bool HasCurrentCatalog() => HasController() && Utilities.IsValid(mirrors.sourceCatalog) &&
            mirrors.sourceCatalog.IsCurrent && mirrors.sourceCatalog.ContextPlaybackId == mirrors.core.SharedPlaybackIdForUfc;

        void OnEnable() {
            if (Utilities.IsValid(entryButton)) entryButton.interactable = true;
            ReadLanguage(); Put(entryText, Tr("Sources"));
            if (IsOpen) { _Refresh(); Queue(); }
        }
        void OnDisable() {
            bound = false; pressedAction = -1;
            if (Utilities.IsValid(entryButton)) entryButton.interactable = false;
            if (Utilities.IsValid(panel)) panel.SetActive(false);
        }
        public void _Open() {
            if (!LocalAction() || !Utilities.IsValid(panel)) return;
            panel.SetActive(true); _Refresh(); Queue();
        }
        public void _Close() {
            if (!LocalAction() || !Utilities.IsValid(panel)) return;
            panel.SetActive(false); bound = false; pressedAction = -1;
        }
        void Queue() {
            if (tickQueued || !LocalAction() || !IsOpen) return;
            tickQueued = true;
            SendCustomEventDelayedSeconds(nameof(_Tick), 0.5F);
        }
        public void _Tick() {
            if (NetworkCalling.InNetworkCall) return;
            tickQueued = false;
            if (!LocalAction() || !IsOpen) return;
            _Refresh(); Queue();
        }
        public void _OnLanguageChanged() {
            if (!LocalAction()) return;
            ReadLanguage(); Put(entryText, Tr("Sources"));
            if (IsOpen) _Refresh();
        }

        // Pointer-down captures the row the viewer actually pressed. Refreshes
        // may reorder rows before release; a stale press must still use old CAS.
        public void _PressSource0() => Press(0);
        public void _PressSource1() => Press(1);
        public void _PressSource2() => Press(2);
        public void _PressAuto() => Press(3);
        public void _PressRetry() => Press(4);
        void Press(int action) {
            if (!PanelAction() || !bound || !canChoose) return;
            pressedAction = action; pressedVersion = shownVersion; pressedPlayback = shownPlayback;
            pressedId = action < shownIds.Length ? shownIds[action] : "";
        }
        string ClickVersion(int action) => pressedAction == action ? pressedVersion : shownVersion;
        string ClickPlayback(int action) => pressedAction == action ? pressedPlayback : shownPlayback;

        public void _Auto() {
            if (!PanelAction() || !bound || !canChoose || !HasController()) return;
            // A selected mode is not an implicit Retry or a refreshed budget.
            if (mirrors.AutoEnabled) { pressedAction = -1; _Refresh(); return; }
            string version = ClickVersion(3), playback = ClickPlayback(3);
            pressedAction = -1;
            Complete(mirrors.UseAuto(version, playback));
        }
        public void _Source0() => Select(0);
        public void _Source1() => Select(1);
        public void _Source2() => Select(2);
        void Select(int index) {
            if (!PanelAction() || !bound || !canChoose || !HasController() || index < 0 || index >= shownIds.Length) return;
            string id = pressedAction == index ? pressedId : shownIds[index];
            string version = ClickVersion(index), playback = ClickPlayback(index);
            pressedAction = -1;
            if (!mirrors.AutoEnabled && mirrors.SelectedSourceId == id &&
                mirrors.SelectedPlaybackId == playback && mirrors.SelectedCatalogVersion == version &&
                HasCurrentCatalog() && mirrors.sourceCatalog.Version == version &&
                mirrors.sourceCatalog.ContextPlaybackId == playback) {
                _Refresh(); return;
            }
            // Do not rebind an index to a newer catalog before validating this click.
            Complete(mirrors.SelectSource(id, version, playback));
        }
        public void _Retry() {
            if (!PanelAction() || !bound || !canChoose || !HasController() || !CanRetry()) return;
            string version = ClickVersion(4), playback = ClickPlayback(4);
            pressedAction = -1;
            bool accepted = mirrors.AutoEnabled ? mirrors.RetryAuto(version, playback) :
                string.IsNullOrEmpty(mirrors.SelectedSourceId) ? mirrors.UsePrimary(version, playback) :
                mirrors.RetrySelected(version, playback);
            Complete(accepted);
        }
        public void _Enable() {
            if (!PanelAction() || !HasController() || !mirrors.gameObject.activeInHierarchy || !mirrors.HasPresentationWiring) return;
            feedback = "";
            if (!mirrors.enabled || mirrors.mirrorsOff || mirrors.IsTurningOff) mirrors._WatchCoreLoads();
            else mirrors._TurnOff();
            _Refresh();
        }
        bool CanRetry() => viewState == NotListed || mirrors.failed || (mirrors.AutoEnabled &&
            (mirrors.AutoState == SourceMirrorController.AutoExhausted || mirrors.AutoState == SourceMirrorController.AutoBlocked));
        void Complete(bool accepted) {
            int result = mirrors.SelectionResult;
            feedback = accepted ? "" : result == SourceMirrorCatalog.TransportRejected ? "Blocked" :
                result == SourceMirrorCatalog.UnsupportedPlatform ? "Unsupported" :
                result == SourceMirrorCatalog.Disabled ? "Unavailable" : "Changed";
            if (HasCurrentCatalog()) {
                feedbackVersion = mirrors.sourceCatalog.Version;
                feedbackPlayback = mirrors.sourceCatalog.ContextPlaybackId;
            }
            _Refresh();
        }

        void ReadLanguage() {
            language = fallbackLanguage;
            if (Utilities.IsValid(languageManager)) {
                string key = languageManager.LanguageKey;
                if (!string.IsNullOrEmpty(key)) {
                    key = key.ToUpperInvariant();
                    language = key.StartsWith("RU") ? 1 : key.StartsWith("JA") || key.StartsWith("JP") ? 2 : 0;
                }
            }
        }
        public void _Refresh() {
            if (!LocalAction() || !IsOpen) return;
            ReadLanguage();
            bool controller = HasController();
            bool current = HasCurrentCatalog();
            bool off = controller && (!mirrors.enabled || !mirrors.gameObject.activeInHierarchy || mirrors.mirrorsOff);
            bool turningOff = controller && mirrors.IsTurningOff;
            bool auto = controller && mirrors.AutoEnabled;
            canChoose = controller && current && mirrors.CanChooseSourceForUI;
#if UNITY_ANDROID || UNITY_IOS
            canChoose = false;
#endif
            bound = current;
            shownVersion = current ? mirrors.sourceCatalog.Version : "";
            shownPlayback = current ? mirrors.sourceCatalog.ContextPlaybackId : "";
            int count = current ? mirrors.sourceCatalog.SourceCount : 0;
            shownIds = new string[count]; shownLabels = new string[count];
            for (int i = 0; i < count; i++) {
                shownIds[i] = mirrors.sourceCatalog.GetSourceId(i);
                shownLabels[i] = mirrors.sourceCatalog.GetSourceLabel(i);
                if (string.IsNullOrEmpty(shownLabels[i])) shownLabels[i] = i == 0 ? Tr("Primary") : Tr("Source") + " " + (i + 1);
            }
            actualId = "";
            if (current && mirrors.core.IsReady && !mirrors.pending && !mirrors.failed) {
                var loaded = mirrors.core.LocalTransportLoadedPhysicalUrlForUfc;
                for (int i = 0; i < count; i++)
                    if (!VRCUrl.IsNullOrEmpty(loaded) && loaded.Equals(mirrors.sourceCatalog.GetSourceUrl(i))) actualId = shownIds[i];
            }
            viewState = !controller || !mirrors.HasPresentationWiring ? NotConfigured : off ? Off :
                mirrors.core.SharedStateForUfc == 0 ? Idle : turningOff ? TurningOff : !current ? NoCatalog :
                auto && mirrors.AutoState == SourceMirrorController.AutoExhausted ? Exhausted :
                auto && mirrors.AutoState == SourceMirrorController.AutoBlocked ? Blocked :
                mirrors.pending ? Loading : mirrors.failed ? Error : !string.IsNullOrEmpty(actualId) ? Ready :
                mirrors.core.IsReady ? NotListed : Loading;
#if UNITY_ANDROID || UNITY_IOS
            viewState = Unsupported;
#endif
            if (!string.IsNullOrEmpty(feedback) && (feedbackVersion != shownVersion || feedbackPlayback != shownPlayback)) feedback = "";
            Put(entryText, Tr("Sources")); Put(titleText, Tr("Title")); Put(scopeText, Tr("Scope"));
            Put(autoText, Tr("Auto")); Put(retryText, Tr("Retry"));
            Put(enableText, Tr(off || turningOff ? "Enable" : "Disable"));
            Put(modeText, Tr("Mode") + ": " + Tr(off || turningOff ? "OffMode" : auto ? "Auto" : "Manual"));
            Put(actualText, Tr("Actual") + ": " + SourceLabel(actualId));
            string stateKey = viewState == NotConfigured ? "Unavailable" : viewState == Unsupported ? "Unsupported" :
                viewState == Off ? "OffStatus" : viewState == Idle ? "Idle" : viewState == NoCatalog ? "NoCatalog" :
                viewState == TurningOff ? (mirrors.failed ? "ReturnError" : "Returning") : viewState == Exhausted ? "Exhausted" :
                viewState == Blocked ? "Blocked" : viewState == Error ? "Error" : viewState == NotListed ? "NotListed" : viewState == Loading ? "Loading" :
                mirrors.core.SharedStateForUfc == 3 ? "Paused" : count == 1 ? "Single" : "Ready";
            Put(statusText, Tr(stateKey)); Put(feedbackText, string.IsNullOrEmpty(feedback) ? "" : Tr(feedback));
            if (Utilities.IsValid(feedbackText)) feedbackText.gameObject.SetActive(!string.IsNullOrEmpty(feedback));
            if (Utilities.IsValid(autoButton)) autoButton.interactable = canChoose;
            if (Utilities.IsValid(autoSelected)) autoSelected.SetActive(auto && !off && !turningOff);
            if (Utilities.IsValid(retryButton)) retryButton.interactable = canChoose && CanRetry();
            if (Utilities.IsValid(enableButton)) enableButton.interactable = controller && mirrors.gameObject.activeInHierarchy && mirrors.HasPresentationWiring;
            int rows = sourceButtons == null ? 0 : sourceButtons.Length;
            for (int i = 0; i < rows; i++) {
                bool visible = i < count;
                if (Utilities.IsValid(sourceButtons[i])) {
                    sourceButtons[i].gameObject.SetActive(visible);
                    sourceButtons[i].interactable = canChoose;
                }
                if (sourceTexts != null && i < sourceTexts.Length) Put(sourceTexts[i], visible ? shownLabels[i] : "");
                if (sourceSelected != null && i < sourceSelected.Length && Utilities.IsValid(sourceSelected[i]))
                    sourceSelected[i].SetActive(visible && !auto && !off && !turningOff && mirrors.SelectedSourceId == shownIds[i]);
            }
        }
        string SourceLabel(string id) {
            for (int i = 0; i < shownIds.Length; i++) if (shownIds[i] == id) return shownLabels[i];
            return "—";
        }
        void Put(Graphic text, string value) {
            if (!Utilities.IsValid(text)) return;
            // Rich text is disabled and checked by the authoring factory/build
            // validator; this SDK does not expose its TMP setter to Udon.
            var tmp = text.GetComponent<TextMeshProUGUI>();
            if (Utilities.IsValid(tmp)) { if (tmp.text != value) tmp.text = value; return; }
            var legacy = text.GetComponent<Text>();
            if (Utilities.IsValid(legacy) && legacy.text != value) legacy.text = value;
        }
        string Tr(string key) {
            if (Utilities.IsValid(languageManager)) {
                string translated = languageManager.GetLocale("SourceMirrors." + key);
                if (!string.IsNullOrEmpty(translated) && translated != "SourceMirrors." + key) return translated;
            }
            switch (key) {
                case "Sources": return L("Sources", "Источники", "ソース");
                case "Title": return L("Video sources", "Источники видео", "動画のソース");
                case "Scope": return L("Only for you", "Только у вас", "自分だけに適用");
                case "Auto": return L("Auto", "Авто", "自動");
                case "Manual": return L("Manual", "Вручную", "手動");
                case "OffMode": return L("Mirrors off", "Зеркала выключены", "ミラー無効");
                case "Mode": return L("Selection", "Выбор", "選択方法");
                case "Actual": return L("Playing from", "Сейчас", "再生中のソース");
                case "Primary": return L("Primary", "Основной", "メイン");
                case "Source": return L("Source", "Источник", "ソース");
                case "Retry": return L("Retry loading", "Повторить загрузку", "再読み込み");
                case "Enable": return L("Enable mirrors", "Включить зеркала", "ミラーを有効化");
                case "Disable": return L("Disable mirrors", "Отключить зеркала", "ミラーを無効化");
                case "Unavailable": return L("Sources are unavailable", "Источники недоступны", "ソースを利用できません");
                case "Unsupported": return L("Source selection is unavailable on this platform", "Выбор источника недоступен на этой платформе", "この環境ではソースを選択できません");
                case "OffStatus": return L("Mirrors are disabled", "Зеркала отключены", "ミラーは無効です");
                case "Idle": return L("No video is playing", "Видео не запущено", "動画が再生されていません");
                case "NoCatalog": return L("No source list for this video", "Для этого видео нет списка источников", "この動画のソース一覧はありません");
                case "Loading": return L("Loading and synchronizing…", "Загрузка и синхронизация…", "読み込み・同期中…");
                case "Ready": return L("Ready", "Готово", "準備完了");
                case "Paused": return L("Paused", "На паузе", "一時停止中");
                case "Single": return L("No additional sources", "Дополнительных источников нет", "他のソースはありません");
                case "Error": return L("This source could not load. Retry or choose another.", "Источник не загрузился. Повторите или выберите другой.", "読み込めませんでした。再試行するか別のソースを選択してください。");
                case "Exhausted": return L("No source loaded. Retry when you are ready.", "Ни один источник не загрузился. Можно повторить попытку.", "すべてのソースで失敗しました。再試行できます。");
                case "Blocked": return L("Could not switch sources. Retry or choose another.", "Не удалось сменить источник. Повторите или выберите другой.", "切り替えできませんでした。再試行するか別のソースを選択してください。");
                case "Returning": return L("Returning to the primary source…", "Возвращаемся к основному источнику…", "メインソースに戻しています…");
                case "ReturnError": return L("The primary source could not load. You can enable mirrors again.", "Основной источник не загрузился. Можно снова включить зеркала.", "メインを読み込めませんでした。ミラーを再度有効にできます。");
                case "Changed": return L("The source list changed. Choose again.", "Список источников изменился. Выберите ещё раз.", "ソース一覧が更新されました。再度選択してください。");
                case "NotListed": return L("This source is no longer listed. Choose another or retry.", "Источника больше нет в списке. Выберите другой или повторите.", "このソースは一覧にありません。別のソースを選ぶか再試行してください。");
            }
            return key;
        }
        string L(string en, string ru, string ja) => language == 1 ? ru : language == 2 ? ja : en;
    }
}
