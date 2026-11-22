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
        public GameObject actionsRoot;
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
        bool tickQueued, bound, canChoose, shownEnable;
        string shownVersion = "", shownPlayback = "", actualId = "";
        string[] shownIds = new string[0], shownLabels = new string[0];
        string feedback = "", feedbackVersion = "", feedbackPlayback = "";
        string feedbackSourceId = "";
        int feedbackResult;
        bool feedbackAuto, feedbackOff;
        int pressedAction = -1, canceledPresses;
        const int AutoAction = 3, RetryAction = 4, EnableAction = 5;
        bool clickWasCanceled, clickUsedPress, pressedEnable;
        string pressedId = "", pressedVersion = "", pressedPlayback = "";
        int lastClickFrame = -1, lastSubmitFrame = -1, lastClickAction = -1, lastSubmitAction = -1;
        bool clickedOldPress;
        int shownRetryRevision, pressedRetryRevision, clickShownRetryRevision;
        string clickShownVersion = "", clickShownPlayback = "";

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
            feedback = ""; bound = false; CancelPress(pressedAction);
            if (Utilities.IsValid(entryButton)) entryButton.interactable = false;
            if (Utilities.IsValid(panel)) panel.SetActive(false);
        }
        public void _Open() {
            if (!LocalAction() || !Utilities.IsValid(panel)) return;
            if (!IsOpen) feedback = "";
            panel.SetActive(true); _Refresh(); Queue();
        }
        public void _Close() {
            if (!LocalAction() || !Utilities.IsValid(panel)) return;
            panel.SetActive(false); feedback = ""; bound = false; CancelPress(pressedAction);
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
        public void _PressAuto() => Press(AutoAction);
        public void _PressRetry() => Press(RetryAction);
        public void _PressEnable() => Press(EnableAction);
        void CancelPress(int action) {
            if (action < 0) return;
            canceledPresses |= 1 << action;
            if (pressedAction == action) pressedAction = -1;
        }
        void Press(int action) {
            if (!PanelAction()) return;
            // uGUI can deliver a click after the button becomes interactable again.
            // The other remote may have started returning to primary since our last refresh.
            if (action != EnableAction && (!bound || !canChoose || !HasController() || !mirrors.CanOfferSourceForUI)) {
                CancelPress(action);
                return;
            }
            if (pressedAction != action) CancelPress(pressedAction);
            canceledPresses &= ~(1 << action);
            lastClickFrame = lastSubmitFrame = -1;
            pressedAction = action; pressedVersion = shownVersion; pressedPlayback = shownPlayback;
            pressedId = action < shownIds.Length ? shownIds[action] : "";
            pressedRetryRevision = shownRetryRevision;
            pressedEnable = shownEnable;
        }
        public void _Enable() => Activate(EnableAction, false);
        public void _SubmitEnable() => Activate(EnableAction, true);
        public void _Auto() => Activate(AutoAction, false);
        public void _Retry() => Activate(RetryAction, false);
        public void _Source0() => Activate(0, false);
        public void _Source1() => Activate(1, false);
        public void _Source2() => Activate(2, false);
        public void _SubmitAuto() => Activate(AutoAction, true);
        public void _SubmitRetry() => Activate(RetryAction, true);
        public void _SubmitSource0() => Activate(0, true);
        public void _SubmitSource1() => Activate(1, true);
        public void _SubmitSource2() => Activate(2, true);

        void Activate(int action, bool submit) {
            if (!PanelAction() || !HasController() || (action != EnableAction && (!bound || !canChoose))) return;
            bool row = action < AutoAction;
            if ((row && action >= shownIds.Length) || (action == RetryAction && !CanRetry())) return;
            // Button.OnSubmit emits onClick too. Whichever Unity component runs
            // first, a keyboard activation acts once on the rendered snapshot.
            int frame = Time.frameCount;
            if (lastSubmitFrame == frame && lastSubmitAction == action) return;
            bool usePress = !submit && pressedAction == action;
            bool acceptedGesture = submit ? AcceptSubmit(action, frame) : AcceptClick(action, frame, usePress);
            if (!acceptedGesture) return;
            string version = usePress ? pressedVersion : shownVersion;
            string playback = usePress ? pressedPlayback : shownPlayback;
            string id = row ? (usePress ? pressedId : shownIds[action]) : "";
            if (usePress) pressedAction = -1;
            if (action == EnableAction) { SetMirrorsEnabled(usePress ? pressedEnable : shownEnable); return; }
            if (row) { Select(action, id, version, playback); return; }
            if (action == AutoAction) {
                // A selected mode is not an implicit Retry or a refreshed budget.
                if (mirrors.AutoEnabled && mirrors.enabled && !mirrors.mirrorsOff) { ShowFeedback(""); return; }
                Complete(mirrors.ChooseSourceFromUI("", true, version, playback), "ChooseAgain");
                return;
            }
            int retryRevision = usePress ? pressedRetryRevision : shownRetryRevision;
            // Both remotes drive the same local controller. Recheck the target and current
            // button state before a held pointer or a delayed keyboard activation can reload it.
            _Refresh();
            if (!canChoose || !CanRetry() || retryRevision != shownRetryRevision) return;
            bool accepted = mirrors.AutoEnabled ? mirrors.RetryAuto(version, playback) :
                RetryUsesPrimary() ? mirrors.UsePrimary(version, playback) : mirrors.RetrySelected(version, playback);
            Complete(accepted, "Blocked");
        }
        bool AcceptSubmit(int action, int frame) {
            lastSubmitFrame = frame;
            lastSubmitAction = action;
            // Enter supersedes a held press on this button only.
            if (pressedAction == action) CancelPress(action);
            if (lastClickFrame != frame || lastClickAction != action) return true;

            // Button.OnSubmit already sent onClick. Its physical release can still arrive later.
            if (clickWasCanceled || clickUsedPress) canceledPresses |= 1 << action;
            return clickedOldPress && clickShownVersion == shownVersion && clickShownPlayback == shownPlayback &&
                (action != RetryAction || clickShownRetryRevision == shownRetryRevision);
        }

        bool AcceptClick(int action, int frame, bool usePress) {
            lastClickFrame = frame;
            lastClickAction = action;
            clickUsedPress = usePress;
            clickShownVersion = shownVersion;
            clickShownPlayback = shownPlayback;
            clickShownRetryRevision = shownRetryRevision;
            clickWasCanceled = (canceledPresses & (1 << action)) != 0;
            if (clickWasCanceled) {
                canceledPresses &= ~(1 << action);
                clickedOldPress = true;
                return false;
            }
            clickedOldPress = usePress && (pressedVersion != shownVersion || pressedPlayback != shownPlayback ||
                (action == RetryAction && pressedRetryRevision != shownRetryRevision) ||
                (action == EnableAction && pressedEnable != shownEnable));
            return true;
        }

        void Select(int index, string id, string version, string playback) {
            if (mirrors.enabled && !mirrors.mirrorsOff && !mirrors.AutoEnabled && mirrors.SelectedSourceId == id &&
                mirrors.SelectedPlaybackId == playback &&
                HasCurrentCatalog() && mirrors.sourceCatalog.Version == version &&
                mirrors.sourceCatalog.ContextPlaybackId == playback &&
                ((mirrors.ActiveSourceId == id && mirrors.sourceCatalog.SameSourceEndpoint(
                    mirrors.sourceCatalog.GetSourceUrl(index), mirrors.core.LocalTransportLoadedPhysicalUrlForUfc)) ||
                    ((mirrors.pending || mirrors.failed) && mirrors.sourceCatalog.SameSourceEndpoint(
                    mirrors.LastAcceptedPhysicalUrl, mirrors.sourceCatalog.GetSourceUrl(index))))) {
                ShowFeedback(""); return;
            }
            // Off/On retains the old selection receipt but hands playback to
            // primary. That old ID alone must not suppress an explicit reapply.
            // Do not rebind an index to a newer catalog before validating this click.
            Complete(mirrors.ChooseSourceFromUI(id, false, version, playback), "ChooseAgain");
        }
        void SetMirrorsEnabled(bool enable) {
            if (!mirrors.gameObject.activeInHierarchy || !mirrors.HasPresentationWiring) return;
            feedback = "";
            bool currentlyOff = !mirrors.enabled || mirrors.mirrorsOff || mirrors.IsTurningOff;
            if (enable != shownEnable || enable != currentlyOff) { _Refresh(); return; }
            if (enable) mirrors._WatchCoreLoads();
            else {
                mirrors._TurnOff();
                if (!mirrors.requestAccepted) { ShowFeedback("OffRejected"); return; }
            }
            _Refresh();
        }
        bool CanRetry() => viewState == NotListed || viewState == Error || viewState == Exhausted || viewState == Blocked;
        bool RetryUsesPrimary() {
            if (mirrors.AutoEnabled) return false;
            if (mirrors.failed && VRCUrl.IsNullOrEmpty(mirrors.LastAcceptedPhysicalUrl)) return true;
            if (mirrors.SelectedPlaybackId == shownPlayback) {
                for (int i = 0; i < shownIds.Length; i++)
                    if (shownIds[i] == mirrors.SelectedSourceId) return false;
            }
            return true;
        }
        void Complete(bool accepted, string rejected) {
            int result = mirrors.SelectionResult;
            ShowFeedback(accepted ? "" : result == SourceMirrorCatalog.TransportRejected ? rejected :
                result == SourceMirrorCatalog.UnsupportedPlatform ? "Unsupported" :
                result == SourceMirrorCatalog.Disabled ? "Unavailable" : "Changed");
        }
        void ShowFeedback(string key) {
            feedback = key;
            if (HasCurrentCatalog()) {
                feedbackVersion = mirrors.sourceCatalog.Version;
                feedbackPlayback = mirrors.sourceCatalog.ContextPlaybackId;
                feedbackSourceId = mirrors.SelectedSourceId;
                feedbackResult = mirrors.SelectionResult;
                feedbackAuto = mirrors.AutoEnabled;
                feedbackOff = !mirrors.enabled || mirrors.mirrorsOff || mirrors.IsTurningOff;
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
            canChoose = controller && current && mirrors.CanOfferSourceForUI;
#if UNITY_ANDROID || UNITY_IOS
            canChoose = false;
#endif
            shownRetryRevision = controller ? mirrors.RetryRevision : 0;
            bound = current;
            shownVersion = current ? mirrors.sourceCatalog.Version : "";
            shownPlayback = current ? mirrors.sourceCatalog.ContextPlaybackId : "";
            int count = current ? mirrors.sourceCatalog.SourceCount : 0;
            bool hasMirrors = count > 1;
            shownIds = new string[count]; shownLabels = new string[count];
            for (int i = 0; i < count; i++) {
                shownIds[i] = mirrors.sourceCatalog.GetSourceId(i);
                shownLabels[i] = mirrors.sourceCatalog.GetSourceLabel(i);
                if (string.IsNullOrEmpty(shownLabels[i])) shownLabels[i] = i == 0 ? Tr("Primary") : Tr("Source") + " " + (i + 1);
            }
            actualId = "";
            if (current && mirrors.core.IsReady && !mirrors.pending && !mirrors.failed) {
                int loaded = mirrors.FindListedEndpoint(mirrors.core.LocalTransportLoadedPhysicalUrlForUfc, mirrors.SelectedSourceId);
                if (loaded >= 0) actualId = shownIds[loaded];
            }
            viewState = !controller || !mirrors.HasPresentationWiring ? NotConfigured :
                mirrors.core.SharedStateForUfc == 0 ? Idle : turningOff ? TurningOff : !current ? NoCatalog : off ? Off :
                auto && mirrors.AutoState == SourceMirrorController.AutoExhausted ? Exhausted :
                auto && mirrors.AutoState == SourceMirrorController.AutoBlocked ? Blocked :
                mirrors.pending ? Loading : mirrors.failed ? Error : !string.IsNullOrEmpty(actualId) ? Ready :
                mirrors.core.IsReady ? NotListed : Loading;
#if UNITY_ANDROID || UNITY_IOS
            viewState = Unsupported;
#endif
            // A video-page sync can finish a refused retry without changing the last selection result.
            if (!string.IsNullOrEmpty(feedback) && (!current || (feedback == "Blocked" && viewState == Ready) ||
                feedbackVersion != shownVersion ||
                feedbackPlayback != shownPlayback || feedbackSourceId != mirrors.SelectedSourceId || feedbackResult != mirrors.SelectionResult ||
                feedbackAuto != auto || feedbackOff != (off || turningOff))) feedback = "";
            Put(entryText, Tr("Sources")); Put(titleText, Tr("Title")); Put(scopeText, Tr("Scope"));
            string recoveryAction = Tr(canChoose && RetryUsesPrimary() ? "LoadPrimary" : "Retry");
            Put(autoText, Tr("Auto")); Put(retryText, recoveryAction);
            shownEnable = off || turningOff;
            Put(enableText, Tr(shownEnable ? "Enable" : "Disable"));
            Put(modeText, Tr("Mode") + ": " + Tr(off || turningOff ? "OffMode" : auto ? "Auto" : "Manual"));
            string intendedId = controller && mirrors.SelectedPlaybackId == shownPlayback ? mirrors.SelectedSourceId : "";
            string target = SourceLabel(intendedId);
            if (current && count > 0 && (mirrors.pending || mirrors.failed) &&
                (turningOff || !string.IsNullOrEmpty(intendedId))) {
                // Returning control to Core requests the primary with a null transport override.
                // The saved manual choice can survive that operation or a catalog replacement.
                var route = mirrors.LastAcceptedPhysicalUrl;
                int index = VRCUrl.IsNullOrEmpty(route) ? 0 : mirrors.FindListedEndpoint(route, intendedId);
                target = index >= 0 ? shownLabels[index] : "";
            }
            bool hasTarget = !string.IsNullOrEmpty(target);
            string actual = string.IsNullOrEmpty(actualId) ? target : SourceLabel(actualId);
            Put(actualText, Tr(string.IsNullOrEmpty(actualId) && hasTarget ? "Target" : "Actual") + ": " +
                (string.IsNullOrEmpty(actual) ? "—" : actual));
            if (Utilities.IsValid(actualText)) actualText.gameObject.SetActive(current &&
                (hasTarget || !string.IsNullOrEmpty(actualId)));
            if (Utilities.IsValid(modeText)) modeText.gameObject.SetActive(current && !off && !turningOff && viewState != Idle);
            string status = TrSourceMessage(StatusKey(hasTarget));
            if (hasTarget && (viewState == Loading || viewState == Error)) status = status.Replace("{0}", target);
            if (canChoose && (viewState == NotListed || (viewState == Error && !hasTarget)))
                status += " " + Tr("ChooseOrRecover").Replace("{0}", recoveryAction);
            Put(statusText, status); Put(feedbackText, string.IsNullOrEmpty(feedback) ? "" : TrSourceMessage(feedback));
            if (Utilities.IsValid(feedbackText)) feedbackText.gameObject.SetActive(!string.IsNullOrEmpty(feedback));
            if (Utilities.IsValid(autoButton)) {
                autoButton.interactable = canChoose;
                autoButton.gameObject.SetActive(hasMirrors && viewState != Idle);
            }
            if (Utilities.IsValid(autoSelected)) autoSelected.SetActive(auto && !off && !turningOff);
            if (Utilities.IsValid(retryButton)) {
                bool retryAvailable = canChoose && CanRetry();
                retryButton.interactable = retryAvailable;
                retryButton.gameObject.SetActive(retryAvailable && !off);
            }
            if (Utilities.IsValid(enableButton)) {
                enableButton.interactable = controller && mirrors.gameObject.activeInHierarchy && mirrors.HasPresentationWiring;
                enableButton.gameObject.SetActive(!(off && canChoose));
            }
            if (Utilities.IsValid(actionsRoot)) actionsRoot.SetActive(
                (Utilities.IsValid(enableButton) && enableButton.gameObject.activeSelf) ||
                (Utilities.IsValid(retryButton) && retryButton.gameObject.activeSelf));
            int rows = sourceButtons == null ? 0 : sourceButtons.Length;
            for (int i = 0; i < rows; i++) {
                bool visible = i < count && viewState != Idle;
                if (Utilities.IsValid(sourceButtons[i])) {
                    sourceButtons[i].gameObject.SetActive(visible);
                    sourceButtons[i].interactable = canChoose;
                }
                if (sourceTexts != null && i < sourceTexts.Length) Put(sourceTexts[i], visible ? shownLabels[i] : "");
                if (sourceSelected != null && i < sourceSelected.Length && Utilities.IsValid(sourceSelected[i]))
                    sourceSelected[i].SetActive(visible && !auto && !off && !turningOff && mirrors.SelectedSourceId == shownIds[i]);
            }
        }
        string StatusKey(bool hasTarget) {
            switch (viewState) {
                case NotConfigured: return "Unavailable";
                case Unsupported: return "Unsupported";
                case Off: return shownIds.Length > 1 ? "OffStatus" : "OffPrimary";
                case Idle: return "Idle";
                case NoCatalog: return "NoCatalog";
                case TurningOff: return mirrors.failed ? "ReturnError" : "Returning";
                case Exhausted: return "Exhausted";
                case Blocked: return "Blocked";
                case Error: return hasTarget ? "ErrorTarget" : "Error";
                case NotListed: return "NotListed";
                case Loading: return hasTarget ? "LoadingTarget" : "Loading";
                default:
                    if (mirrors.core.SharedStateForUfc == 3) return "Paused";
                    return shownIds.Length == 1 ? "Single" : "Ready";
            }
        }

        string SourceLabel(string id) {
            for (int i = 0; i < shownIds.Length; i++) if (shownIds[i] == id) return shownLabels[i];
            return "";
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
        string TrSourceMessage(string key) {
            if (shownIds.Length == 1) {
                if (key == "ErrorTarget") return Tr("PrimaryLoadError");
                if (key == "ChooseAgain") return Tr("PrimarySelectionError");
                if (key == "Blocked") return Tr("PrimaryRetryError");
            }
            return Tr(key);
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
                case "Target": return L("Selected source", "Выбран источник", "選択中のソース");
                case "Primary": return L("Primary", "Основной", "メイン");
                case "Source": return L("Source", "Источник", "ソース");
                case "Retry": return L("Retry loading", "Повторить загрузку", "再読み込み");
                case "LoadPrimary": return L("Load primary", "Загрузить основной", "メインを読み込む");
                case "Enable": return L("Enable mirrors", "Включить зеркала", "ミラーを有効化");
                case "Disable": return L("Disable mirrors", "Отключить зеркала", "ミラーを無効化");
                case "Unavailable": return L("Sources are unavailable", "Источники недоступны", "ソースを利用できません");
                case "Unsupported": return L("Source selection is unavailable on this platform", "Выбор источника недоступен на этой платформе", "この環境ではソースを選択できません");
                case "OffPrimary": return L("Select the primary source.", "Выберите основной источник.", "メインソースを選んでください。");
                case "OffStatus": return L("Choose a source or Auto to enable mirrors only for you.", "Выберите источник или Авто — зеркала включатся только у вас.", "ソースか自動を選ぶと、自分だけのミラーが有効になります。");
                case "Idle": return L("Choose a video in the player first.", "Сначала выберите видео в плеере.", "先にプレーヤーで動画を選んでください。");
                case "NoCatalog": return L("No source list for this video", "Для этого видео нет списка источников", "この動画のソース一覧はありません");
                case "Loading": return L("Loading and synchronizing…", "Загрузка и синхронизация…", "読み込み・同期中…");
                case "LoadingTarget": return L("Switching to {0}…", "Переключаем на {0}…", "{0}に切り替え中…");
                case "PrimaryLoadError": return L("{0} could not load. Try loading it again.", "Не удалось загрузить «{0}». Повторите загрузку.", "「{0}」を読み込めませんでした。もう一度読み込んでください。");
                case "PrimarySelectionError": return L("Could not select the primary source. Select it again.", "Не удалось выбрать основной источник. Выберите его ещё раз.", "メインソースを選択できませんでした。もう一度選んでください。");
                case "PrimaryRetryError": return L("Could not start loading the primary source. Try again.", "Не удалось начать загрузку основного источника. Попробуйте ещё раз.", "メインソースの読み込みを開始できませんでした。もう一度お試しください。");
                case "ErrorTarget": return L("{0} could not load. Retry or choose another source.", "{0} не загрузился. Повторите или выберите другой источник.", "{0}を読み込めませんでした。再試行するか別のソースを選んでください。");
                case "Ready": return L("Ready", "Готово", "準備完了");
                case "Paused": return L("Paused", "На паузе", "一時停止中");
                case "Single": return L("No additional sources", "Дополнительных источников нет", "他のソースはありません");
                case "Error": return L("This source could not load.", "Источник не загрузился.", "このソースを読み込めませんでした。");
                case "Exhausted": return L("No source loaded. Retry when you are ready.", "Ни один источник не загрузился. Можно повторить попытку.", "すべてのソースで失敗しました。再試行できます。");
                case "Blocked": return L("Could not switch sources. Retry or choose another.", "Не удалось сменить источник. Повторите или выберите другой.", "切り替えできませんでした。再試行するか別のソースを選択してください。");
                case "ChooseAgain": return L("Could not switch sources. Select your choice again.", "Не удалось переключиться. Выберите нужный источник или «Авто» ещё раз.", "切り替えできませんでした。使いたいソースまたは「自動」をもう一度選んでください。");
                case "OffRejected": return L("Could not load the primary source. Try turning mirrors off again.", "Не удалось перейти на основной источник. Попробуйте снова отключить зеркала.", "メインに戻れませんでした。もう一度ミラーを無効にしてください。");
                case "Returning": return L("Returning to the primary source…", "Возвращаемся к основному источнику…", "メインソースに戻しています…");
                case "ReturnError": return L("The primary source could not load. You can enable mirrors again.", "Основной источник не загрузился. Можно снова включить зеркала.", "メインを読み込めませんでした。ミラーを再度有効にできます。");
                case "Changed": return L("The source list changed. Choose again.", "Список источников изменился. Выберите ещё раз.", "ソース一覧が更新されました。再度選択してください。");
                case "NotListed": return L("This source is no longer listed.", "Источника больше нет в списке.", "このソースは一覧にありません。");
                case "ChooseOrRecover": return L("Choose a source or press “{0}”.", "Выберите источник или нажмите «{0}».", "ソースを選ぶか「{0}」を押してください。");
            }
            return key;
        }
        string L(string en, string ru, string ja) => language == 1 ? ru : language == 2 ? ja : en;
    }
}
