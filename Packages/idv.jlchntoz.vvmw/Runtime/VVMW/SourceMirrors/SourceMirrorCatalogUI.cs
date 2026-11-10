using System;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using VRC.SDKBase;
using VRC.SDK3.Components;
using VRC.SDK3.UdonNetworkCalling;
using JLChnToZ.VRC.Foundation;
using JLChnToZ.VRC.Foundation.I18N;

namespace JLChnToZ.VRC.VVMW {
    /// <summary>Optional local draft view. The existing authority owns publication and permissions.</summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    [DisallowMultipleComponent]
    [AddComponentMenu("VizVid/Source Mirrors/Shared Catalog UI")]
    public class SourceMirrorCatalogUI : UdonSharpBehaviour {
        public SourceMirrorCatalogAuthority authority;
        // Optional previous layer, opened only after this view confirms its Save.
        public SourceMirrorUI sourceSelection;
        [BindUdonSharpEvent(nameof(_OnLanguageChanged))] public LanguageManager languageManager;
        public int fallbackLanguage;
        public GameObject panel;
        public Button entryButton, applyButton, reloadButton;
        public Button addMirrorButton;
        public Button[] removeMirrorButtons;
        public GameObject fieldsRoot, readOnlyRoot, compatibilityRow, scrollRoot;
        public GameObject[] mirrorRows;
        public ScrollRect fieldsScroll;
        public Graphic addMirrorText, readOnlyText;
        public VRCUrlInputField[] mirrorInputs;
        public InputField[] nameInputs;
        // Retained for existing serialized views. Saving is the only submit action.
        public Toggle compatibilityToggle;
        public Graphic entryText, titleText, scopeText, primaryText, confirmationText;
        public Graphic applyText, reloadText, closeText, statusText;
        public Graphic[] nameCaptions, urlCaptions;

        public bool IsOpen => Utilities.IsValid(panel) && panel.activeSelf;
        public bool HasDraft => bound;
        public bool IsDraftStale => bound && (!Graph() ||
            !authority.core.HasSharedPlaybackContextForUfc || authority.core.SharedPlaybackIdForUfc != playback ||
            authority.catalog.Version != version || authority.policy.GetRoleRevision() != role);
        public bool CanEdit => bound && !IsDraftStale && Authorized();
        public string DraftPlaybackId => playback;
        public string DraftCatalogVersion => version;
        public int DraftRoleRevision => role;
        public bool HasLocalEdits {
            get {
                if (!bound) return false;
                if (!Fields()) return true;
                for (int i = 0; i < 3; i++) if (nameInputs[i].text != originalLabels[i]) return true;
                for (int i = 0; i < 2; i++) {
                    var value = mirrorInputs[i].GetUrl(); var original = originalUrls[i + 1];
                    if (VRCUrl.IsNullOrEmpty(value) ? !VRCUrl.IsNullOrEmpty(original) :
                        VRCUrl.IsNullOrEmpty(original) || !value.Equals(original)) return true;
                }
                return false;
            }
        }

        bool bound, tickQueued, discardArmed, closeAfterSave;
        int role, language;
        VRCUrl primary;
        string playback = "", version = "", film = "", tag = "", request = "", feedback = "";
        string[] ids = new string[3];
        string[] originalLabels = new string[3];
        VRCUrl[] originalUrls = new VRCUrl[3];
        string[] submittedLabels = new string[0];
        VRCUrl[] submittedMirrors = new VRCUrl[0];
        bool[] expandedMirrors = new bool[2];
        // Legacy query used by older authoring integrations.
        public bool RequiresCompatibilityConfirmation => false;
        bool LocalAction() => !NetworkCalling.InNetworkCall && enabled && gameObject.activeInHierarchy;
        bool Graph() => Utilities.IsValid(authority) && authority.enabled && authority.gameObject.activeInHierarchy &&
            Utilities.IsValid(authority.core) && Utilities.IsValid(authority.catalog) && authority.catalog.enabled &&
            authority.catalog.gameObject.activeInHierarchy && authority.catalog.authority == authority &&
            Utilities.IsValid(authority.policy) && authority.policy.enabled && authority.policy.gameObject.activeInHierarchy;
        bool Authorized() => Graph() && Utilities.IsValid(Networking.LocalPlayer) &&
            authority.policy.CanEditCatalogLocally(Networking.LocalPlayer, authority.policy.GetRoleRevision());
        bool Fields() => mirrorInputs != null && mirrorInputs.Length == 2 &&
            Utilities.IsValid(mirrorInputs[0]) && Utilities.IsValid(mirrorInputs[1]) &&
            nameInputs != null && nameInputs.Length == 3 && Utilities.IsValid(nameInputs[0]) &&
            Utilities.IsValid(nameInputs[1]) && Utilities.IsValid(nameInputs[2]);

        void Start() { _OnLanguageChanged(); }
        void OnEnable() { _OnLanguageChanged(); }
        void OnDisable() {
            closeAfterSave = false;
            if (Utilities.IsValid(entryButton)) entryButton.interactable = false;
            if (Utilities.IsValid(panel)) panel.SetActive(false);
        }
        public void _Open() {
            if (!LocalAction() || !Utilities.IsValid(panel)) return;
            bool wasOpen = IsOpen; panel.SetActive(true);
            if (!wasOpen) closeAfterSave = false;
            // Closing never cancels a submitted publication. Reopen keeps its
            // draft/status until the existing authority finishes the request.
            if (!wasOpen && !(Graph() && authority.PublicationPending && authority.PendingRequestId == request) &&
                (!bound || (!HasLocalEdits && string.IsNullOrEmpty(feedback) && string.IsNullOrEmpty(request)))) ReadDraft();
            _Refresh(); Queue();
        }
        public void _Close() {
            if (!LocalAction() || !Utilities.IsValid(panel)) return;
            panel.SetActive(false); discardArmed = closeAfterSave = false;
            if (feedback == "DiscardFirst") feedback = "";
        }
        public void _Reload() {
            if (!LocalAction() || !IsOpen || (Graph() && authority.PublicationPending)) return;
            if (HasLocalEdits && !discardArmed) {
                discardArmed = true; feedback = "DiscardFirst"; _Refresh(); return;
            }
            ReadDraft(); _Refresh();
        }
        void ReadDraft() {
            bound = false; feedback = request = ""; discardArmed = closeAfterSave = false;
            submittedLabels = new string[0]; submittedMirrors = new VRCUrl[0];
            expandedMirrors = new bool[2];
            if (Fields()) {
                foreach (var name in nameInputs) name.text = "";
                foreach (var input in mirrorInputs) input.SetUrl(VRCUrl.Empty);
            }
            if (!Graph() || !Fields() || !authority.core.HasSharedPlaybackContextForUfc) return;
            var core = authority.core; var catalog = authority.catalog;
            playback = core.SharedPlaybackIdForUfc; version = catalog.Version;
            role = authority.policy.GetRoleRevision(); primary = core.CanonicalPcUrlForUfc;
            bool current = catalog.IsCurrent && catalog.ContextPlaybackId == playback;
            film = current ? catalog.FilmId : Guid.NewGuid().ToString("N");
            tag = current ? catalog.CompatibilityTag : "same-edit-start-speed";
            ids = new[] { "primary", "mirror1", "mirror2" };
            originalUrls = new VRCUrl[3];
            // VRCUrlInputField initializes on first activation in the Udon VM.
            // Activating a previously hidden row after SetUrl loses that value.
            // Finish its native lifecycle before filling the captured draft;
            // _Refresh restores the actual editor/view-only presentation below.
            if (Utilities.IsValid(scrollRoot)) scrollRoot.SetActive(true);
            if (Utilities.IsValid(fieldsRoot)) fieldsRoot.SetActive(true);
            if (mirrorRows != null) foreach (var row in mirrorRows)
                if (Utilities.IsValid(row)) row.SetActive(true);
            for (int i = 0; i < 3; i++) {
                bool present = current && i < catalog.SourceCount;
                if (present) { ids[i] = catalog.GetSourceId(i); originalUrls[i] = catalog.GetSourceUrl(i); }
                nameInputs[i].text = present ? catalog.GetSourceLabel(i) : Tr(i == 0 ? "Primary" : i == 1 ? "Mirror1" : "Mirror2");
                originalLabels[i] = nameInputs[i].text;
                if (i > 0) mirrorInputs[i - 1].SetUrl(present ? catalog.GetSourceUrl(i) : VRCUrl.Empty);
            }
            for (int i = 0; i < 2; i++) expandedMirrors[i] = !VRCUrl.IsNullOrEmpty(mirrorInputs[i].GetUrl());
            if (Utilities.IsValid(fieldsScroll)) fieldsScroll.verticalNormalizedPosition = 1;
            // A removed source may already use one of the default slot IDs.
            if (ids[1] == ids[0]) ids[1] = Guid.NewGuid().ToString("N");
            if (ids[2] == ids[0] || ids[2] == ids[1]) ids[2] = Guid.NewGuid().ToString("N");
            bound = true;
        }
        public void _Apply() {
            if (!LocalAction() || !IsOpen || !Fields()) return;
            if (!CanEdit) { feedback = ""; _Refresh(); return; }
            if (authority.PublicationPending) { _Refresh(); return; }
            string invalid = ValidateDraftUrls();
            if (!string.IsNullOrEmpty(invalid)) { feedback = invalid; _Refresh(); return; }
            if (!authority.IsReadyForPublication || VRCUrl.IsNullOrEmpty(primary)) { feedback = "Unavailable"; _Refresh(); return; }
            // Only a draft read from this accepted catalog may leave without a
            // publication. A new list or an unresolved receipt still uses Save.
            if (!HasLocalEdits && string.IsNullOrEmpty(request) && authority.catalog.IsCurrent &&
                authority.catalog.ContextPlaybackId == playback && !VRCUrl.IsNullOrEmpty(originalUrls[0]) &&
                originalUrls[0].Equals(primary) && primary.Equals(authority.core.CanonicalPcUrlForUfc)) {
                ReturnToSelection(); return;
            }
            var first = mirrorInputs[0].GetUrl(); var second = mirrorInputs[1].GetUrl();
            bool hasFirst = !VRCUrl.IsNullOrEmpty(first), hasSecond = !VRCUrl.IsNullOrEmpty(second);
            int count = 1 + (hasFirst ? 1 : 0) + (hasSecond ? 1 : 0);
            var urls = new VRCUrl[count]; var sourceIds = new string[count]; var labels = new string[count];
            urls[0] = primary; sourceIds[0] = ids[0]; labels[0] = SourceName(0, primary);
            int at = 1;
            for (int i = 1; i < 3; i++) {
                if ((i == 1 && !hasFirst) || (i == 2 && !hasSecond)) continue;
                var url = i == 1 ? first : second;
                urls[at] = url; sourceIds[at] = SourceId(url); labels[at] = SourceName(i, url); at++;
            }
            submittedLabels = new[] { nameInputs[0].text, nameInputs[1].text, nameInputs[2].text };
            submittedMirrors = new[] { first, second };
            request = Guid.NewGuid().ToString("N"); feedback = ""; discardArmed = false; closeAfterSave = true;
            bool accepted = authority.RequestPublishCurrentCatalog(role, version, playback, request,
                urls, sourceIds, labels, new[] { film, tag });
            if (!accepted) { feedback = "Rejected"; closeAfterSave = false; }
            _Refresh();
        }
        void ReturnToSelection() {
            _Close();
            if (Utilities.IsValid(sourceSelection)) sourceSelection._Open();
        }
        string SourceId(VRCUrl url) {
            for (int i = 0; i < originalUrls.Length; i++)
                if (!VRCUrl.IsNullOrEmpty(originalUrls[i]) && originalUrls[i].Equals(url)) return ids[i];
            return Guid.NewGuid().ToString("N");
        }
        public void _AddMirror() {
            if (!LocalAction() || !IsOpen || !CanEdit || authority.PublicationPending || !Fields()) return;
            for (int i = 0; i < 2; i++) if (!expandedMirrors[i] && VRCUrl.IsNullOrEmpty(mirrorInputs[i].GetUrl())) {
                expandedMirrors[i] = true; _Refresh(); return;
            }
        }
        public void _RemoveMirror0() => RemoveMirror(0);
        public void _RemoveMirror1() => RemoveMirror(1);
        void RemoveMirror(int index) {
            if (!LocalAction() || !IsOpen || !CanEdit || authority.PublicationPending || !Fields()) return;
            mirrorInputs[index].SetUrl(VRCUrl.Empty); expandedMirrors[index] = false;
            discardArmed = false; _Refresh();
        }
        string ValidateDraftUrls() {
            for (int i = 0; i < 2; i++) {
                var url = mirrorInputs[i].GetUrl();
                if (VRCUrl.IsNullOrEmpty(url)) continue;
                string value = url.Get();
                if (value != value.Trim() || value.IndexOf("://") < 1 || value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0)
                    return i == 0 ? "Invalid1" : "Invalid2";
                if (url.Equals(primary) || (i == 1 && !VRCUrl.IsNullOrEmpty(mirrorInputs[0].GetUrl()) && url.Equals(mirrorInputs[0].GetUrl())))
                    return i == 0 ? "Duplicate1" : "Duplicate2";
            }
            return "";
        }
        string Host(VRCUrl url) {
            if (VRCUrl.IsNullOrEmpty(url)) return "—";
            string value = url.Get(); int begin = value.IndexOf("://");
            if (begin < 0) return value;
            value = value.Substring(begin + 3);
            int end = value.IndexOf('/'); if (end >= 0) value = value.Substring(0, end);
            int credentials = value.LastIndexOf('@');
            return credentials >= 0 ? value.Substring(credentials + 1) : value;
        }
        string SourceName(int index, VRCUrl url) => string.IsNullOrEmpty(nameInputs[index].text.Trim()) ?
            (index == 0 ? Tr("Primary") : Host(url)) : nameInputs[index].text;
        string ReadOnlySummary() {
            string result = SourceName(0, primary) + " · " + Host(primary);
            for (int i = 0; i < 2; i++) if (!VRCUrl.IsNullOrEmpty(mirrorInputs[i].GetUrl()))
                result += "\n\n" + SourceName(i + 1, mirrorInputs[i].GetUrl()) + " · " + Host(mirrorInputs[i].GetUrl());
            return result;
        }
        bool SubmittedFieldsUnchanged() {
            if (!Fields() || submittedLabels.Length != 3 || submittedMirrors.Length != 2) return false;
            for (int i = 0; i < 3; i++) if (nameInputs[i].text != submittedLabels[i]) return false;
            for (int i = 0; i < 2; i++) {
                var url = mirrorInputs[i].GetUrl(); var sent = submittedMirrors[i];
                if (VRCUrl.IsNullOrEmpty(url) ? !VRCUrl.IsNullOrEmpty(sent) :
                    VRCUrl.IsNullOrEmpty(sent) || !url.Equals(sent)) return false;
            }
            return true;
        }
        void Queue() {
            if (tickQueued || !LocalAction() || !IsOpen) return;
            tickQueued = true; SendCustomEventDelayedSeconds(nameof(_Tick), 0.5F);
        }
        public void _Tick() {
            if (NetworkCalling.InNetworkCall) return;
            tickQueued = false;
            if (!LocalAction() || !IsOpen) return;
            _Refresh(); Queue();
        }
        public void _OnLanguageChanged() {
            if (!LocalAction()) return;
            string key = Utilities.IsValid(languageManager) ? languageManager.LanguageKey : "";
            language = key == "RU" || key == "ru" ? 1 : key == "JA" || key == "ja" ? 2 :
                string.IsNullOrEmpty(key) ? fallbackLanguage : 0;
            Put(entryText, Tr("Title")); if (IsOpen) _Refresh();
            if (Utilities.IsValid(entryButton)) entryButton.interactable = Utilities.IsValid(panel);
        }
        public void _Refresh() {
            if (!LocalAction() || !IsOpen) return;
            // An empty idle view has no draft/CAS capability to preserve. Bind
            // the first arriving movie without requiring a close/reopen.
            if (!bound && Graph() && Fields() && authority.core.HasSharedPlaybackContextForUfc &&
                !authority.PublicationPending) ReadDraft();
            // A different movie resets the form, including its old unsaved rows
            // (10 Nov user request). Same-movie replay still protects edits and
            // pending receipts. Authority retains pending work; this never submits.
            if (bound && Graph() && Fields() && authority.core.HasSharedPlaybackContextForUfc &&
                authority.core.SharedPlaybackIdForUfc != playback) {
                bool otherVideo = !VRCUrl.IsNullOrEmpty(authority.core.CanonicalPcUrlForUfc) &&
                    !authority.core.CanonicalPcUrlForUfc.Equals(primary);
                if (otherVideo || (!authority.PublicationPending && !HasLocalEdits)) ReadDraft();
            }
            // Only this unchanged submission's exact accepted snapshot may advance
            // the editor base. A foreign snapshot, local commit or role change cannot.
            if (bound && Graph() && !authority.PublicationPending &&
                !string.IsNullOrEmpty(request) && authority.PendingRequestId == request &&
                authority.PublicationStatus == SourceMirrorCatalogAuthority.Confirmed &&
                authority.PendingCommittedVersion == authority.catalog.Version &&
                authority.catalog.IsCurrent && authority.catalog.ContextPlaybackId == playback &&
                authority.core.HasSharedPlaybackContextForUfc && authority.core.SharedPlaybackIdForUfc == playback &&
                authority.policy.GetRoleRevision() == role && SubmittedFieldsUnchanged()) {
                float position = Utilities.IsValid(fieldsScroll) ? fieldsScroll.verticalNormalizedPosition : 1;
                bool returnToSelection = closeAfterSave;
                ReadDraft(); feedback = "Confirmed";
                if (Utilities.IsValid(fieldsScroll)) fieldsScroll.verticalNormalizedPosition = position;
                if (returnToSelection) {
                    ReturnToSelection();
                    return;
                }
            }
            if (feedback == "Confirmed" && HasLocalEdits) feedback = "";
            // A clean read-only view follows the accepted list. An editor's
            // fixed draft, local changes and outstanding/error receipts do not.
            if (Graph() && Fields() && !Authorized() && !authority.PublicationPending &&
                (bound ? IsDraftStale : authority.core.HasSharedPlaybackContextForUfc) &&
                string.IsNullOrEmpty(feedback) && !HasLocalEdits &&
                (string.IsNullOrEmpty(request) || (authority.PendingRequestId == request &&
                    authority.PublicationStatus == SourceMirrorCatalogAuthority.Confirmed))) ReadDraft();
            bool graph = Graph(), editable = CanEdit && Fields();
            bool pending = graph && authority.PublicationPending;
            bool ownRequest = graph && !string.IsNullOrEmpty(request) && authority.PendingRequestId == request;
            int state = ownRequest ? authority.PublicationStatus : 0;
            bool contextChanged = bound && graph && (authority.core.SharedPlaybackIdForUfc != playback || authority.policy.GetRoleRevision() != role);
            // Confirmation belongs to the submitted request. Do not silently
            // rebind the draft when its matching snapshot changes the version.
            string status = !graph || !Fields() ? "Unavailable" : !bound || !authority.core.HasSharedPlaybackContextForUfc ? "NoVideo" :
                contextChanged ? "Changed" : ownRequest && (pending || state == SourceMirrorCatalogAuthority.CommittedLocal) ? "Waiting" :
                ownRequest && state == SourceMirrorCatalogAuthority.Confirmed && authority.PendingCommittedVersion == authority.catalog.Version ? "Confirmed" :
                ownRequest && state == SourceMirrorCatalogAuthority.Unconfirmed ? "Unconfirmed" :
                ownRequest && state != SourceMirrorCatalogAuthority.Confirmed ? "Rejected" :
                !bound ? "NoVideo" : !Authorized() ? "ViewOnly" : IsDraftStale ? "Changed" :
                pending ? "Waiting" : !string.IsNullOrEmpty(feedback) ? feedback :
                !authority.IsReadyForPublication ? "Unavailable" : HasLocalEdits ? "Unsaved" : "Draft";
            Put(titleText, Tr("Title")); Put(scopeText, Tr("Scope")); Put(confirmationText, Tr("Confirm"));
            Put(primaryText, Tr(IsDraftStale ? "DraftPrimaryUrl" : "PrimaryUrl") + ": " + (bound && !VRCUrl.IsNullOrEmpty(primary) ? primary.Get() : "—"));
            Put(applyText, Tr("Apply")); Put(reloadText, Tr(discardArmed ? "Discard" : "Reload")); Put(closeText, Tr("Close"));
            Put(statusText, Tr(status));
            bool body = bound && graph && (authority.core.HasSharedPlaybackContextForUfc || HasLocalEdits);
            bool showFields = body && (Authorized() || HasLocalEdits);
            if (Utilities.IsValid(scrollRoot)) scrollRoot.SetActive(body);
            if (Utilities.IsValid(fieldsRoot)) fieldsRoot.SetActive(showFields);
            if (Utilities.IsValid(readOnlyRoot)) readOnlyRoot.SetActive(body && !showFields);
            Put(readOnlyText, body ? ReadOnlySummary() : "");
            if (Utilities.IsValid(compatibilityRow)) compatibilityRow.SetActive(false);
            if (Utilities.IsValid(compatibilityToggle)) {
                compatibilityToggle.interactable = false;
                compatibilityToggle.gameObject.SetActive(false);
            }
            bool availableSlot = false;
            for (int i = 0; i < 2; i++) {
                bool visible = expandedMirrors[i] || (Fields() && !VRCUrl.IsNullOrEmpty(mirrorInputs[i].GetUrl()));
                if (!visible) availableSlot = true;
                if (mirrorRows != null && i < mirrorRows.Length && Utilities.IsValid(mirrorRows[i])) mirrorRows[i].SetActive(visible);
                if (removeMirrorButtons != null && i < removeMirrorButtons.Length && Utilities.IsValid(removeMirrorButtons[i]))
                    removeMirrorButtons[i].interactable = editable && !pending;
            }
            if (Utilities.IsValid(addMirrorButton)) {
                addMirrorButton.gameObject.SetActive(editable);
                addMirrorButton.interactable = !pending && availableSlot;
            }
            Put(addMirrorText, Tr(availableSlot ? "AddMirror" : "MirrorLimit"));
            if (nameCaptions != null) for (int i = 0; i < nameCaptions.Length; i++) Put(nameCaptions[i], (i == 0 ? Tr("PrimaryName") : Tr(i == 1 ? "Mirror1" : "Mirror2") + " · " + Tr("Name")));
            if (urlCaptions != null) for (int i = 0; i < urlCaptions.Length; i++) Put(urlCaptions[i], Tr("MirrorUrl"));
            if (Fields()) {
                for (int i = 0; i < 3; i++) nameInputs[i].interactable = editable && !pending;
                for (int i = 0; i < 2; i++) mirrorInputs[i].interactable = editable && !pending;
            }
            if (Utilities.IsValid(applyButton)) {
                applyButton.gameObject.SetActive(bound && graph && Authorized());
                applyButton.interactable = editable && !pending && authority.IsReadyForPublication;
            }
            if (Utilities.IsValid(reloadButton)) {
                reloadButton.gameObject.SetActive(bound && graph && (Authorized() || HasLocalEdits || !string.IsNullOrEmpty(request)));
                reloadButton.interactable = graph && !pending;
            }
        }
        void Put(Graphic target, string value) {
            if (!Utilities.IsValid(target)) return;
            var tmp = target.GetComponent<TextMeshProUGUI>(); if (Utilities.IsValid(tmp)) { tmp.text = value; return; }
            var text = target.GetComponent<Text>(); if (Utilities.IsValid(text)) text.text = value;
        }
        string L(string en, string ru, string ja) => language == 1 ? ru : language == 2 ? ja : en;
        string Tr(string key) {
            switch (key) {
                case "Title": return L("Shared video sources", "Общие источники видео", "共有動画ソース");
                case "Scope": return L("Available to everyone. Each viewer chooses their own source.", "Список доступен всем. Источник каждый выбирает у себя.", "全員に共有されます。再生ソースは各自で選びます。");
                case "PrimaryUrl": return L("URL", "URL", "URL");
                case "PrimaryName": return L("Primary source", "Основной источник", "メインソース");
                case "Primary": return L("Primary", "Основной", "メイン");
                case "Mirror1": return L("Mirror 1", "Зеркало 1", "ミラー1");
                case "Mirror2": return L("Mirror 2", "Зеркало 2", "ミラー2");
                case "MirrorUrl": return L("Video URL", "Ссылка на видео", "動画URL");
                case "Confirm": return L("Same film, edit, start and speed", "Тот же фильм, монтаж, начало и скорость", "同じ動画・編集・開始位置・速度です");
                case "Apply": return L("Save sources", "Сохранить источники", "ソースを保存");
                case "Reload": return L("Refresh list", "Обновить список", "一覧を更新");
                case "Discard": return L("Discard changes", "Сбросить правки", "変更を破棄");
                case "DiscardFirst": return L("Your draft is kept. Press Discard changes to replace it.", "Черновик сохранён. Для сброса нажмите «Сбросить правки».", "下書きは保持されています。「変更を破棄」で再取得します。");
                case "DraftPrimaryUrl": return L("Draft for this primary URL", "Черновик для основного URL", "下書きのメインURL");
                case "AddMirror": return L("Add mirror", "Добавить зеркало", "ミラーを追加");
                case "MirrorLimit": return L("2 mirrors added", "Добавлено 2 зеркала", "ミラーは2件まで");
                case "Name": return L("name", "название", "名前");
                case "Invalid1": return L("Mirror 1: enter a full video URL.", "Зеркало 1: укажите полный адрес видео.", "ミラー1：動画の完全なURLを入力してください。");
                case "Invalid2": return L("Mirror 2: enter a full video URL.", "Зеркало 2: укажите полный адрес видео.", "ミラー2：動画の完全なURLを入力してください。");
                case "Duplicate1": return L("Mirror 1 repeats the primary URL.", "Зеркало 1 повторяет основной адрес.", "ミラー1はメインURLと重複しています。");
                case "Duplicate2": return L("Mirror 2 repeats another source URL.", "Зеркало 2 повторяет другой источник.", "ミラー2は別のソースと重複しています。");
                case "Close": return L("Close", "Закрыть", "閉じる");
                case "Unsaved": return L("Unsaved changes", "Есть несохранённые изменения", "未保存の変更があります。");
                case "Draft": return L("Mirrors must match the edit, start and speed.", "Зеркала: тот же монтаж, начало и скорость.", "ミラーは同じ編集・開始位置・速度の動画を指定してください。");
                case "ViewOnly": return L("Only the current host can edit this list.", "Изменять список может только текущий ведущий.", "編集できるのは現在のホストのみです。");
                case "NoVideo": return L("Start a video using the normal player controls first.", "Сначала запустите фильм обычными кнопками плеера.", "先に通常の操作で動画を開始してください。");
                case "Changed": return L("The video, list or host changed. Reload the list before applying.", "Фильм, список или ведущий изменились. Сначала перечитайте список.", "動画・一覧・ホストが変更されました。適用前に一覧を再取得してください。");
                case "Waiting": return L("Saving… waiting for confirmation.", "Сохраняем… ждём подтверждения.", "保存中…確認を待っています。");
                case "Confirmed": return L("Sources updated.", "Источники обновлены.", "ソースを更新しました。");
                case "Unconfirmed": return L("Save was not confirmed. Your draft is kept; refresh to check the list.", "Сохранение не подтверждено. Черновик сохранён; обновите список для проверки.", "保存を確認できません。下書きは保持されています。一覧を更新してください。");
                case "Rejected": return L("Could not save. Your draft is kept; check the list and your access.", "Не удалось сохранить. Черновик сохранён; проверьте список и права.", "保存できません。下書きは保持されています。一覧と権限を確認してください。");
                case "Unavailable": return L("Saving is unavailable. Your draft is kept.", "Сохранение пока недоступно. Черновик сохранён.", "現在保存できません。下書きは保持されています。");
            }
            return key;
        }
    }
}
