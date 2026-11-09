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
        [BindUdonSharpEvent(nameof(_OnLanguageChanged))] public LanguageManager languageManager;
        public int fallbackLanguage;
        public GameObject panel;
        public Button entryButton, applyButton, reloadButton;
        public VRCUrlInputField[] mirrorInputs;
        public InputField[] nameInputs;
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
                if (!Fields() || compatibilityToggle.isOn) return true;
                for (int i = 0; i < 3; i++) if (nameInputs[i].text != originalLabels[i]) return true;
                for (int i = 0; i < 2; i++) {
                    var value = mirrorInputs[i].GetUrl(); var original = originalUrls[i + 1];
                    if (VRCUrl.IsNullOrEmpty(value) ? !VRCUrl.IsNullOrEmpty(original) :
                        VRCUrl.IsNullOrEmpty(original) || !value.Equals(original)) return true;
                }
                return false;
            }
        }

        bool bound, tickQueued;
        int role, language;
        VRCUrl primary;
        string playback = "", version = "", film = "", tag = "", request = "", feedback = "";
        string[] ids = new string[3];
        string[] originalLabels = new string[3];
        VRCUrl[] originalUrls = new VRCUrl[3];
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
            Utilities.IsValid(nameInputs[1]) && Utilities.IsValid(nameInputs[2]) && Utilities.IsValid(compatibilityToggle);

        void Start() { _OnLanguageChanged(); }
        void OnEnable() { _OnLanguageChanged(); }
        void OnDisable() {
            if (Utilities.IsValid(entryButton)) entryButton.interactable = false;
            if (Utilities.IsValid(panel)) panel.SetActive(false);
        }
        public void _Open() {
            if (!LocalAction() || !Utilities.IsValid(panel)) return;
            bool wasOpen = IsOpen; panel.SetActive(true);
            // Closing never cancels a submitted publication. Reopen keeps its
            // draft/status until the existing authority finishes the request.
            if (!wasOpen && !(Graph() && authority.PublicationPending && authority.PendingRequestId == request)) ReadDraft();
            _Refresh(); Queue();
        }
        public void _Close() { if (LocalAction() && Utilities.IsValid(panel)) panel.SetActive(false); }
        public void _Reload() {
            if (!LocalAction() || !IsOpen || (Graph() && authority.PublicationPending)) return;
            ReadDraft(); _Refresh();
        }
        void ReadDraft() {
            bound = false; feedback = request = "";
            if (Fields()) {
                foreach (var name in nameInputs) name.text = "";
                foreach (var input in mirrorInputs) input.SetUrl(VRCUrl.Empty);
                compatibilityToggle.isOn = false;
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
            for (int i = 0; i < 3; i++) {
                bool present = current && i < catalog.SourceCount;
                if (present) { ids[i] = catalog.GetSourceId(i); originalUrls[i] = catalog.GetSourceUrl(i); }
                nameInputs[i].text = present ? catalog.GetSourceLabel(i) : Tr(i == 0 ? "Primary" : i == 1 ? "Mirror1" : "Mirror2");
                originalLabels[i] = nameInputs[i].text;
                if (i > 0) mirrorInputs[i - 1].SetUrl(present ? catalog.GetSourceUrl(i) : VRCUrl.Empty);
            }
            // A removed source may already use one of the default slot IDs.
            if (ids[1] == ids[0]) ids[1] = Guid.NewGuid().ToString("N");
            if (ids[2] == ids[0] || ids[2] == ids[1]) ids[2] = Guid.NewGuid().ToString("N");
            compatibilityToggle.isOn = false; bound = true;
        }
        public void _Apply() {
            if (!LocalAction() || !IsOpen || !Fields()) return;
            if (!CanEdit) { feedback = ""; _Refresh(); return; }
            if (authority.PublicationPending) { _Refresh(); return; }
            if (!compatibilityToggle.isOn) { feedback = "ConfirmFirst"; _Refresh(); return; }
            if (!authority.IsReadyForPublication || VRCUrl.IsNullOrEmpty(primary)) { feedback = "Unavailable"; _Refresh(); return; }
            var first = mirrorInputs[0].GetUrl(); var second = mirrorInputs[1].GetUrl();
            bool hasFirst = !VRCUrl.IsNullOrEmpty(first), hasSecond = !VRCUrl.IsNullOrEmpty(second);
            int count = 1 + (hasFirst ? 1 : 0) + (hasSecond ? 1 : 0);
            var urls = new VRCUrl[count]; var sourceIds = new string[count]; var labels = new string[count];
            urls[0] = primary; sourceIds[0] = ids[0]; labels[0] = nameInputs[0].text;
            int at = 1;
            for (int i = 1; i < 3; i++) {
                if ((i == 1 && !hasFirst) || (i == 2 && !hasSecond)) continue;
                var url = i == 1 ? first : second;
                urls[at] = url; sourceIds[at] = SourceId(url); labels[at] = nameInputs[i].text; at++;
            }
            request = Guid.NewGuid().ToString("N"); feedback = "";
            bool accepted = authority.RequestPublishCurrentCatalog(role, version, playback, request,
                urls, sourceIds, labels, new[] { film, tag });
            compatibilityToggle.isOn = false;
            if (!accepted) feedback = "Rejected";
            _Refresh();
        }
        string SourceId(VRCUrl url) {
            for (int i = 0; i < originalUrls.Length; i++)
                if (!VRCUrl.IsNullOrEmpty(originalUrls[i]) && originalUrls[i].Equals(url)) return ids[i];
            return Guid.NewGuid().ToString("N");
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
                !authority.IsReadyForPublication ? "Unavailable" : "Draft";
            Put(titleText, Tr("Title")); Put(scopeText, Tr("Scope")); Put(confirmationText, Tr("Confirm"));
            Put(primaryText, Tr("PrimaryUrl") + ":\n" + (bound && !VRCUrl.IsNullOrEmpty(primary) ? primary.Get() : "—"));
            Put(applyText, Tr("Apply")); Put(reloadText, Tr("Reload")); Put(closeText, Tr("Close"));
            Put(statusText, Tr(status));
            if (nameCaptions != null) for (int i = 0; i < nameCaptions.Length; i++) Put(nameCaptions[i], Tr(i == 0 ? "PrimaryName" : i == 1 ? "Mirror1" : "Mirror2"));
            if (urlCaptions != null) for (int i = 0; i < urlCaptions.Length; i++) Put(urlCaptions[i], Tr("MirrorUrl"));
            if (Fields()) {
                for (int i = 0; i < 3; i++) nameInputs[i].interactable = editable && !pending;
                for (int i = 0; i < 2; i++) mirrorInputs[i].interactable = editable && !pending;
                compatibilityToggle.interactable = editable && !pending;
            }
            if (Utilities.IsValid(applyButton)) applyButton.interactable = editable && !pending && authority.IsReadyForPublication;
            if (Utilities.IsValid(reloadButton)) reloadButton.interactable = graph && !pending;
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
                case "Scope": return L("For everyone. This does not start or reload the video.", "Для всех. Не запускает и не перезагружает видео.", "全員に適用。動画の開始や再読み込みは行いません。");
                case "PrimaryUrl": return L("Current video · primary URL", "Текущий фильм · основной URL", "現在の動画・メインURL");
                case "PrimaryName": return L("Primary source name", "Название основного источника", "メインソースの名前");
                case "Primary": return L("Primary", "Основной", "メイン");
                case "Mirror1": return L("Mirror 1", "Зеркало 1", "ミラー1");
                case "Mirror2": return L("Mirror 2", "Зеркало 2", "ミラー2");
                case "MirrorUrl": return L("Full URL · leave empty to omit", "Полный URL · пустое поле исключает зеркало", "完全なURL・空欄は除外");
                case "Confirm": return L("Same edit, start and playback speed", "Одинаковые монтаж, начало и скорость", "同じ編集・開始位置・再生速度");
                case "Apply": return L("Apply for everyone", "Применить для всех", "全員に適用");
                case "Reload": return L("Reload list · discard edits", "Перечитать · сбросить правки", "一覧を再取得・編集を破棄");
                case "Close": return L("Close", "Закрыть", "閉じる");
                case "Draft": return L("Edit sources for this video, then confirm and apply.", "Измените источники этого фильма, подтвердите и примените.", "この動画のソースを編集し、確認して適用してください。");
                case "ViewOnly": return L("Only the current host can edit this list.", "Изменять список может только текущий ведущий.", "編集できるのは現在のホストのみです。");
                case "NoVideo": return L("Start a video using the normal player controls first.", "Сначала запустите фильм обычными кнопками плеера.", "先に通常の操作で動画を開始してください。");
                case "Changed": return L("The video, list or host changed. Reload the list before applying.", "Фильм, список или ведущий изменились. Сначала перечитайте список.", "動画・一覧・ホストが変更されました。適用前に一覧を再取得してください。");
                case "ConfirmFirst": return L("Confirm the same edit, start and speed before applying.", "Перед применением подтвердите одинаковые монтаж, начало и скорость.", "適用前に同じ編集・開始位置・速度であることを確認してください。");
                case "Waiting": return L("Publishing… waiting for confirmation.", "Публикация… ожидаем подтверждения.", "公開中…確認を待っています。");
                case "Confirmed": return L("The shared source list is published.", "Общий список источников опубликован.", "共有ソース一覧を公開しました。");
                case "Unconfirmed": return L("Publication was not confirmed. Reload the list before trying again.", "Публикация не подтверждена. Перечитайте список перед новой попыткой.", "公開を確認できませんでした。再試行前に一覧を再取得してください。");
                case "Rejected": return L("Could not publish. Check the URLs and reload the list.", "Не удалось применить. Проверьте URL и перечитайте список.", "適用できませんでした。URLを確認して一覧を再取得してください。");
                case "Unavailable": return L("Publication is unavailable. Wait or reload the list.", "Публикация пока недоступна. Подождите или перечитайте список.", "現在公開できません。しばらく待つか一覧を再取得してください。");
            }
            return key;
        }
    }
}
