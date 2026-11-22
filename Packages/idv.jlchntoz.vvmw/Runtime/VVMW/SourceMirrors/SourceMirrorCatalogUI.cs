using System;
using System.Text.RegularExpressions;
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
            authority.catalog.Version != version || authority.policy.GetRoleRevision() != role ||
            RecoveryContextChanged());
        public bool CanEdit => bound && !IsDraftStale && Authorized();
        public string DraftPlaybackId => playback;
        public string DraftCatalogVersion => version;
        public int DraftRoleRevision => role;
        public bool HasLocalEdits {
            get {
                if (!bound) return false;
                if (!Fields()) return true;
                if (RepeatedSubmissionMatchesRoom()) return !SubmittedFieldsUnchanged();
                // The player explicitly submitted a revision, possibly undoing every change to the
                // opening list. Keep it until its rows arrive or the player confirms discarding it.
                if (revisedRetry && IsDraftStale && SubmittedFieldsUnchanged() &&
                    (!Graph() || !authority.catalog.IsCurrent || !RowsMatchCatalog(publishedIds, publishedLabels, publishedUrls))) return true;
                // A later edit may put a field back to the old catalog value.
                // The accepted list now contains the submitted value instead.
                if ((editedAfterConfirmation || HasMatchingConfirmation()) && !SubmittedFieldsUnchanged()) return true;
                string primaryName = nameInputs[0].text;
                if (primaryName != originalLabels[0] && (primaryName.Length > 128 || SourceName(0, primary) != originalLabels[0])) return true;
                for (int i = 0; i < 2; i++) {
                    var value = mirrorInputs[i].GetUrl();
                    if (!SameDraftUrl(value, originalUrls[i + 1])) return true;
                    bool empty = VRCUrl.IsNullOrEmpty(value);
                    // Removing a saved URL counts above. Once both lists omit the row,
                    // only an open input can contain an unfinished mirror name.
                    if (empty && !expandedMirrors[i]) continue;
                    string name = nameInputs[i + 1].text;
                    if (name == originalLabels[i + 1]) continue;
                    if (empty ? name.Trim().Length > 0 :
                        name.Length > 128 || SourceName(i + 1, value) != originalLabels[i + 1]) return true;
                }
                return false;
            }
        }

        bool bound, tickQueued, discardArmed, closeAfterSave, recoveredDraft;
        bool boundAsEditor, boundToCurrentCatalog, editedAfterConfirmation, repeatedSubmission, revisedRetry;
        // PointerDown belongs to the form shown when the gesture started.
        const int SaveAction = 2, ReloadAction = 3, AddAction = 4;
        const int NoPress = -1, IgnoreRelease = -2;
        int formRevision;
        int[] pressedRevision = { NoPress, NoPress, NoPress, NoPress, NoPress };
        int[] lastClickFrame = { -1, -1, -1, -1, -1 }, lastSubmitFrame = { -1, -1, -1, -1, -1 };
        bool[] clickHadPress = new bool[AddAction + 1], clickRejected = new bool[AddAction + 1];
        string[] discardLabels;
        VRCUrl[] discardMirrors;
        string validationFeedback = "";
        string[] validationLabels;
        VRCUrl[] validationMirrors;
        const int LocalCopy = 1, OwnerCopy = 2;
        int recoveryCopy;
        bool reviewArmed;
        int role, language;
        VRCUrl primary, mobile;
        byte backend;
        string playback = "", version = "", film = "", tag = "", request = "", feedback = "";
        string publicationBase = "", recoveryBase = "";
        string[] ids = new string[3];
        string[] originalLabels = new string[3];
        // A draft slot can be new even when the saved catalog once occupied that position.
        // Null marks a published name; otherwise this is the local automatic placeholder.
        string[] automaticLabels = new string[3];
        VRCUrl[] originalUrls = new VRCUrl[3];
        // The writer acknowledges this form's request even while its panel is closed.
        // The authority writes the outcome, status and accepted version in slots 0..2.
        // The remaining values describe the local form rows of that particular submission.
        const int FirstMirrorGeneration = 3, FirstAutomaticName = 5, FormReceiptSize = 8;
        int[] mirrorGenerations = new int[2];
        string[] confirmedRequest = new string[FormReceiptSize];
        string[] deliveredDraft = new string[FormReceiptSize];
        bool[] submittedAutomatic = new bool[3];
        bool languageRefreshPending;
        string[] submittedLabels = new string[0];
        VRCUrl[] submittedMirrors = new VRCUrl[0];
        // Exact, compacted rows sent to the writer, including generated IDs and default names.
        string[] publishedIds, publishedLabels;
        VRCUrl[] publishedUrls;
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
        string RecoveryBase() => Authorized() ? authority.RecoveryBaseVersion : "";
        bool RecoveryContextChanged() => RecoveryBase() != recoveryBase ||
            (!string.IsNullOrEmpty(recoveryBase) && (recoveryCopy != 0) != authority.HasRecoveryConflict);
        bool ReadyToSubmit() => recoveryCopy == 0 ? authority.IsReadyForPublication : authority.CanPublishReviewedRecovery;
        bool Fields() => mirrorInputs != null && mirrorInputs.Length == 2 &&
            Utilities.IsValid(mirrorInputs[0]) && Utilities.IsValid(mirrorInputs[1]) &&
            nameInputs != null && nameInputs.Length == 3 && Utilities.IsValid(nameInputs[0]) &&
            Utilities.IsValid(nameInputs[1]) && Utilities.IsValid(nameInputs[2]);

        void Start() { _OnLanguageChanged(); }
        void OnEnable() { _OnLanguageChanged(); }
        void OnDisable() {
            formRevision++;
            closeAfterSave = reviewArmed = false;
            if (Utilities.IsValid(entryButton)) entryButton.interactable = false;
            if (Utilities.IsValid(panel)) panel.SetActive(false);
        }
        void UpdateDraftBeforeComparison() {
            // A click can arrive before the next tick. A discard confirmation only covers
            // the fields that were visible when the user first pressed Reload.
            if (discardArmed && !FieldsUnchanged(discardLabels, discardMirrors)) discardArmed = false;
            if (!discardArmed && feedback == "DiscardFirst") feedback = "";
            // Only the verified delivery of our own earlier Save can supply a fresh write base
            // for the explicitly revised retry. Concurrent edits still require a manual refresh.
            if (revisedRetry && bound && Graph() && Fields() && authority.catalog.IsCurrent && PreviousSaveIsCurrent() &&
                !authority.PublicationPending && !authority.RequiresPublicationRetry && Authorized() &&
                role == authority.policy.GetRoleRevision() && authority.catalog.FilmId == film &&
                authority.catalog.CompatibilityTag == tag && !discardArmed &&
                authority.MatchesCoreContext(authority.catalog.ContextPlaybackId, primary, mobile, backend))
                ReadDraft(false, true);
        }

        public void _Open() {
            if (!LocalAction() || !Utilities.IsValid(panel)) return;
            bool wasOpen = IsOpen; panel.SetActive(true);
            if (!wasOpen) {
                formRevision++; closeAfterSave = reviewArmed = false;
                // The receipt may have arrived while the form was closed. Compare the retained
                // inputs with the delivered version before deciding whether they can be replaced.
                UpdateDraftBeforeComparison();
                if (!(Graph() && authority.PublicationPending && authority.PendingRequestId == request) &&
                    (!bound || (!HasLocalEdits && (string.IsNullOrEmpty(request) || (IsDraftStale && !KeepsSubmittedFields()))))) ReadDraft();
            }
            _Refresh(); Queue();
        }
        public void _Close() {
            if (!LocalAction() || !Utilities.IsValid(panel)) return;
            formRevision++;
            panel.SetActive(false); discardArmed = closeAfterSave = reviewArmed = false;
            if (feedback == "DiscardFirst") feedback = "";
        }
        public void _PressReload() => PressAction(ReloadAction);
        public void _SubmitReload() => SubmitAction(ReloadAction);
        public void _Reload() => ClickAction(ReloadAction);
        void ReloadDraft() {
            if (!LocalAction() || !IsOpen || (Graph() && authority.PublicationPending)) return;
            reviewArmed = false;
            UpdateDraftBeforeComparison();
            if (HasLocalEdits && !discardArmed) {
                if (!Fields()) return;
                discardLabels = DraftLabels(); discardMirrors = DraftMirrors();
                discardArmed = true; feedback = "DiscardFirst"; _Refresh(); return;
            }
            ReadDraft(recoveryCopy == LocalCopy && !IsDraftStale); _Refresh();
        }
        void ReadDraft(bool ownerCopy = false, bool keepFields = false) {
            formRevision++;
            validationFeedback = "";
            bound = false; feedback = request = ""; discardArmed = closeAfterSave = reviewArmed = editedAfterConfirmation = false;
            repeatedSubmission = revisedRetry = false;
            var delivered = deliveredDraft;
            if (Utilities.IsValid(authority)) authority.ReleaseFormDraft(this, delivered);
            deliveredDraft = new string[FormReceiptSize];
            recoveryCopy = 0;
            submittedLabels = new string[0]; submittedMirrors = new VRCUrl[0];
            if (!keepFields) expandedMirrors = new bool[2];
            if (!keepFields && Fields()) {
                // End the old native edit session before replacing its film. Escape
                // otherwise restores the text captured when that previous edit began.
                foreach (var name in nameInputs) {
                    name.DeactivateInputField();
                    name.text = "";
                }
                foreach (var input in mirrorInputs) {
                    input.DeactivateInputField();
                    input.SetUrl(VRCUrl.Empty);
                }
            }
            if (!Graph() || !Fields() || !authority.core.HasSharedPlaybackContextForUfc) return;
            var core = authority.core; var catalog = authority.catalog;
            playback = core.SharedPlaybackIdForUfc; version = catalog.Version;
            role = authority.policy.GetRoleRevision(); primary = core.CanonicalPcUrlForUfc;
            mobile = core.EffectiveMobileUrlForUfc; backend = core.ConfiguredPlayerTypeForUfc;
            bool current = catalog.IsCurrent && catalog.ContextPlaybackId == playback;
            boundAsEditor = Authorized();
            boundToCurrentCatalog = current;
            // A new launch can have the same media while its previous catalog is
            // awaiting publication. The writer requires the original film and row IDs.
            recoveryBase = RecoveryBase();
            bool useRecovery = !string.IsNullOrEmpty(recoveryBase);
            if (useRecovery && authority.HasRecoveryConflict) recoveryCopy = ownerCopy ? OwnerCopy : LocalCopy;
            bool useSnapshot = useRecovery || current || (Authorized() && catalog.HasAcceptedSnapshot &&
                authority.MatchesCoreContext(playback, catalog.SnapshotCanonical, catalog.SnapshotMobileCanonical, catalog.SnapshotBackend));
            recoveredDraft = useSnapshot && !current;
            // Keep version for detecting changes during editing. A different
            // movie's version is not the base for this movie's first list.
            publicationBase = useRecovery ? recoveryBase : useSnapshot ? version : "";
            film = useRecovery ? authority.RecoveryFilmId : useSnapshot ? catalog.FilmId : Guid.NewGuid().ToString("N");
            bool useWriterRows = useRecovery && recoveryCopy != LocalCopy;
            tag = useWriterRows ? authority.RecoveryCompatibilityTag : useSnapshot ? catalog.CompatibilityTag : "same-edit-start-speed";
            var savedIds = recoveryCopy == LocalCopy ? authority.GetLocalRecoverySourceIds() :
                useWriterRows ? authority.GetRecoverySourceIds() : useSnapshot ? catalog.GetSourceIds() : null;
            var savedLabels = useWriterRows ? authority.GetRecoverySourceLabels() : useSnapshot ? catalog.GetSourceLabels() : null;
            var savedUrls = useWriterRows ? authority.GetRecoverySourceUrls() : useSnapshot ? catalog.GetSourceUrls() : null;
            // Only saved rows have mirror IDs. SourceId allocates one for a new URL at submission.
            ids = new string[3];
            ids[0] = "primary";
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
                int savedRow = i;
                if (keepFields && i > 0) {
                    // A submit skips empty slots, but the retained inputs are still in their original slots.
                    if (delivered[FirstMirrorGeneration + i - 1] == null) savedRow = -1;
                    else if (i == 2 && delivered[FirstMirrorGeneration] == null) savedRow = 1;
                }
                bool present = useSnapshot && savedRow >= 0 && savedRow < savedIds.Length;
                if (present) { ids[i] = savedIds[savedRow]; originalUrls[i] = savedUrls[savedRow]; }
                originalLabels[i] = present ? savedLabels[savedRow] : Tr(i == 0 ? "Primary" : i == 1 ? "Mirror1" : "Mirror2");
                if (keepFields) {
                    if (present && (i == 0 || delivered[FirstMirrorGeneration + i - 1] == mirrorGenerations[i - 1].ToString())) {
                        // An automatic name may have followed the language while the receipt was in flight.
                        if (automaticLabels[i] != null && nameInputs[i].text == automaticLabels[i] &&
                            delivered[FirstAutomaticName + i] == originalLabels[i]) nameInputs[i].text = originalLabels[i];
                        automaticLabels[i] = null;
                    }
                    continue;
                }
                automaticLabels[i] = present ? null : originalLabels[i];
                nameInputs[i].text = originalLabels[i];
                if (i > 0) mirrorInputs[i - 1].SetUrl(present ? savedUrls[i] : VRCUrl.Empty);
            }
            if (!keepFields) {
                for (int i = 0; i < 2; i++) expandedMirrors[i] = !VRCUrl.IsNullOrEmpty(mirrorInputs[i].GetUrl());
                if (Utilities.IsValid(fieldsScroll)) fieldsScroll.verticalNormalizedPosition = 1;
            }
            bound = true;
        }
        // Cinema's revision also changes when the host updates someone else's permissions.
        // A clean form can renew only its role token. Preserve fields and scroll position.
        bool CanRenewCleanEditorRole(bool addingRow) {
            if (!bound || !boundAsEditor || !Graph()) return false;
            int latestRole = authority.policy.GetRoleRevision();
            if (latestRole <= role || (!addingRow && latestRole != role + 1) || !Authorized() ||
                authority.PublicationPending || KeepsSubmittedFields() || discardArmed || reviewArmed ||
                authority.catalog.Version != version ||
                (authority.catalog.IsCurrent && authority.catalog.ContextPlaybackId == playback) != boundToCurrentCatalog ||
                !authority.MatchesCoreContext(playback, primary, mobile, backend) ||
                RecoveryContextChanged() ||
                HasLocalEdits) return false;
            return true;
        }
        void RenewCleanEditorRole(bool addingRow = false) {
            if (!CanRenewCleanEditorRole(addingRow)) return;
            role = authority.policy.GetRoleRevision();
            formRevision++;
        }
        public void _PressApply() => PressAction(SaveAction);
        public void _Apply() => ClickAction(SaveAction);
        public void _SubmitApply() => SubmitAction(SaveAction);
        public void _PressRemoveMirror0() => PressAction(0);
        public void _PressRemoveMirror1() => PressAction(1);
        public void _RemoveMirror0() => ClickAction(0);
        public void _RemoveMirror1() => ClickAction(1);
        public void _SubmitRemoveMirror0() => SubmitAction(0);
        public void _SubmitRemoveMirror1() => SubmitAction(1);
        void PressAction(int action) {
            if (!LocalAction() || !IsOpen) return;
            RenewCleanEditorRole(action == AddAction);
            lastClickFrame[action] = lastSubmitFrame[action] = -1;
            if (action == ReloadAction) {
                // A stale or read-only draft can still be refreshed or discarded.
                pressedRevision[action] = Graph() && authority.PublicationPending ? IgnoreRelease : formRevision;
                return;
            }
            if (!CanEdit || authority.PublicationPending) {
                pressedRevision[action] = IgnoreRelease;
                return;
            }
            bool available = action == SaveAction ? ReadyToSubmit() :
                action == AddAction ? FirstFreeMirrorSlot() >= 0 : expandedMirrors[action];
            pressedRevision[action] = available ? formRevision : IgnoreRelease;
        }
        void ClickAction(int action) {
            if (!LocalAction() || !IsOpen || lastSubmitFrame[action] == Time.frameCount) return;
            RenewCleanEditorRole(action == AddAction);
            lastClickFrame[action] = Time.frameCount;
            clickHadPress[action] = pressedRevision[action] != NoPress;
            clickRejected[action] = clickHadPress[action] && pressedRevision[action] != formRevision;
            pressedRevision[action] = NoPress;
            if (!clickRejected[action]) RunAction(action);
        }
        // Button.OnSubmit also invokes onClick. Support either component order,
        // even if a previous pointer gesture ended outside this button.
        void SubmitAction(int action) {
            if (!LocalAction() || lastSubmitFrame[action] == Time.frameCount) return;
            bool clicked = lastClickFrame[action] == Time.frameCount;
            if (!IsOpen && !clicked) return;
            lastSubmitFrame[action] = Time.frameCount;
            // Enter can deliver onClick first, even closing the form. A mouse press
            // seen by either callback still has its own release to consume later.
            if (pressedRevision[action] != NoPress || (clicked && clickHadPress[action]))
                pressedRevision[action] = IgnoreRelease;
            if (!IsOpen || (clicked && !clickRejected[action])) return;
            RenewCleanEditorRole(action == AddAction);
            RunAction(action);
        }
        void RunAction(int action) {
            if (action == ReloadAction) ReloadDraft();
            else if (action == SaveAction) SaveDraft();
            else if (action == AddAction) AddMirror();
            else RemoveMirror(action);
        }
        void SaveDraft() {
            if (!LocalAction() || !IsOpen || !Fields()) return;
            if (!CanEdit) { feedback = ""; _Refresh(); return; }
            if (authority.PublicationPending) { _Refresh(); return; }
            RefreshDraftLanguage();
            discardArmed = false;
            string invalid = ValidateDraft();
            if (!string.IsNullOrEmpty(invalid)) { SetValidationFeedback(invalid); _Refresh(); return; }
            validationFeedback = "";
            if (authority.FirstCatalogLaunchHint != 0) { _Refresh(); return; }
            if (!ReadyToSubmit() || VRCUrl.IsNullOrEmpty(primary)) { feedback = "Unavailable"; _Refresh(); return; }
            if (recoveryCopy != 0 && (!reviewArmed || !SubmittedFieldsUnchanged())) {
                CaptureSubmittedFields(DraftMirrors());
                reviewArmed = true; request = feedback = ""; closeAfterSave = false;
                _Refresh(); return;
            }
            var mirrors = DraftMirrors();
            int count = 1;
            foreach (var mirror in mirrors)
                if (!VRCUrl.IsNullOrEmpty(mirror)) count++;
            // Keep the source IDs assigned by the first submission of this draft.
            bool repeat = !string.IsNullOrEmpty(request) && publishedIds != null && publishedIds.Length == count && SubmittedFieldsUnchanged();
            var urls = new VRCUrl[count];
            var sourceIds = repeat ? publishedIds : new string[count];
            var labels = new string[count];
            urls[0] = primary;
            sourceIds[0] = ids[0];
            labels[0] = SourceName(0, primary);
            int row = 1;
            for (int slot = 0; slot < mirrors.Length; slot++) {
                var url = mirrors[slot];
                if (VRCUrl.IsNullOrEmpty(url)) continue;
                urls[row] = url;
                labels[row] = SourceName(slot + 1, url);
                if (!repeat) sourceIds[row] = SourceId(url);
                row++;
            }
            // Compare the compacted rows that would actually be published. Reviewed
            // recovery and outstanding receipts still require an explicit write.
            if (boundToCurrentCatalog && string.IsNullOrEmpty(recoveryBase) && !KeepsSubmittedFields() &&
                !authority.RequiresPublicationRetry && authority.catalog.IsCurrent && authority.catalog.ContextPlaybackId == playback &&
                primary.Equals(authority.core.CanonicalPcUrlForUfc) && RowsMatchCatalog(sourceIds, labels, urls)) {
                // Save completed, even when the same published rows occupied different input slots.
                ReadDraft();
                ReturnToSelection(); return;
            }
            if (KeepsSubmittedFields() || repeatedSubmission || revisedRetry) revisedRetry = revisedRetry || !repeat;
            repeatedSubmission = repeat;
            CaptureSubmittedFields(mirrors);
            publishedIds = sourceIds;
            publishedLabels = labels;
            publishedUrls = urls;
            SendDraft();
        }
        void SendDraft() {
            reviewArmed = false;
            request = Guid.NewGuid().ToString("N"); feedback = ""; discardArmed = false; closeAfterSave = true;
            confirmedRequest = new string[FormReceiptSize];
            for (int slot = 0; slot < submittedMirrors.Length; slot++)
                if (!VRCUrl.IsNullOrEmpty(submittedMirrors[slot]))
                    confirmedRequest[FirstMirrorGeneration + slot] = mirrorGenerations[slot].ToString();
            for (int i = 0; i < 3; i++)
                if (submittedAutomatic[i]) confirmedRequest[FirstAutomaticName + i] = submittedLabels[i];
            bool accepted = authority.RequestPublishCurrentCatalog(role, publicationBase, playback, request,
                publishedUrls, publishedIds, publishedLabels, new[] { film, tag }, recoveryCopy != 0);
            if (accepted) authority.TrackDraftConfirmation(request, confirmedRequest, this, deliveredDraft);
            else {
                // The authority did not admit this call, so a late receipt cannot arrive.
                confirmedRequest[0] = "-" + request;
                closeAfterSave = false;
                if (authority.PublicationStatus == SourceMirrorCatalog.PayloadTooLarge && authority.PendingRequestId != request) {
                    // Envelope validation ran before the authority admitted this request.
                    request = "";
                    SetValidationFeedback("TooLarge");
                } else feedback = SaveFailure(authority.PublicationStatus);
            }
            _Refresh();
        }
        void ReturnToSelection() {
            _Close();
            if (Utilities.IsValid(sourceSelection)) sourceSelection._Open();
        }
        string SourceId(VRCUrl url) {
            for (int i = 1; i < originalUrls.Length; i++)
                if (!VRCUrl.IsNullOrEmpty(originalUrls[i]) && originalUrls[i].Equals(url)) return ids[i];
            for (int i = 1; i < originalUrls.Length; i++)
                if (authority.catalog.SameSourceEndpoint(originalUrls[i], url)) return ids[i];
            return Guid.NewGuid().ToString("N");
        }
        public void _PressAddMirror() => PressAction(AddAction);
        public void _SubmitAddMirror() => SubmitAction(AddAction);
        public void _AddMirror() => ClickAction(AddAction);
        int FirstFreeMirrorSlot() {
            if (!Fields()) return -1;
            for (int i = 0; i < expandedMirrors.Length; i++)
                if (!expandedMirrors[i] && VRCUrl.IsNullOrEmpty(mirrorInputs[i].GetUrl())) return i;
            return -1;
        }
        void AddMirror() {
            if (!CanEdit || authority.PublicationPending) return;
            int i = FirstFreeMirrorSlot();
            if (i < 0) return;
            mirrorGenerations[i]++;
            string defaultName = Tr(i == 0 ? "Mirror1" : "Mirror2");
            automaticLabels[i + 1] = defaultName;
            if (VRCUrl.IsNullOrEmpty(originalUrls[i + 1])) originalLabels[i + 1] = defaultName;
            nameInputs[i + 1].text = defaultName;
            expandedMirrors[i] = true;
            _Refresh();
        }
        void RemoveMirror(int index) {
            if (!LocalAction() || !IsOpen || !CanEdit || authority.PublicationPending || !Fields()) return;
            mirrorInputs[index].SetUrl(VRCUrl.Empty); expandedMirrors[index] = false;
            automaticLabels[index + 1] = VRCUrl.IsNullOrEmpty(originalUrls[index + 1]) ? originalLabels[index + 1] : null;
            nameInputs[index + 1].text = originalLabels[index + 1];
            if (!string.IsNullOrEmpty(validationFeedback) && validationFeedback == feedback) SetValidationFeedback(ValidateDraft());
            discardArmed = false; _Refresh();
        }
        string ValidateDraft() {
            if (nameInputs[0].text.Length > 128) return "TooLarge";
            for (int i = 0; i < 2; i++) {
                var url = mirrorInputs[i].GetUrl();
                string name = nameInputs[i + 1].text;
                if (VRCUrl.IsNullOrEmpty(url)) {
                    // Older flat forms have no remove action, so clearing their URL
                    // is still the way to remove an existing mirror.
                    bool hasRemove = removeMirrorButtons != null && i < removeMirrorButtons.Length && Utilities.IsValid(removeMirrorButtons[i]);
                    string placeholder = automaticLabels[i + 1];
                    bool incomplete = placeholder == null ? hasRemove : name != placeholder && name.Trim().Length > 0;
                    if (expandedMirrors[i] && incomplete) {
                        if (hasRemove) return i == 0 ? "Missing1" : "Missing2";
                        return i == 0 ? "Invalid1" : "Invalid2";
                    }
                    continue;
                }
                string value = url.Get();
                if (name.Length > 128 || value.Length > SourceMirrorCatalog.DraftBudget) return "TooLarge";
                if (!ValidMediaUrl(value)) return i == 0 ? "Invalid1" : "Invalid2";
                if (authority.catalog.SameSourceEndpoint(url, primary) || (i == 1 && authority.catalog.SameSourceEndpoint(url, mirrorInputs[0].GetUrl())))
                    return i == 0 ? "Duplicate1" : "Duplicate2";
            }
            return "";
        }
        char[] authorityDelimiters = new[] { '/', '?', '#' };
        string UrlAuthority(string value) {
            int begin = value.IndexOf("://") + 3;
            int end = value.IndexOfAny(authorityDelimiters, begin);
            return end < 0 ? value.Substring(begin) : value.Substring(begin, end - begin);
        }
        bool ValidMediaUrl(string value) {
            int schemeEnd = value.IndexOf("://");
            if (schemeEnd <= 0) return false;
            // HTTP video and the real-time protocols accepted by VideoPlayerHandler.
            switch (value.Substring(0, schemeEnd).ToLowerInvariant()) {
                case "http": case "https": case "rtmp": case "rtmps":
                case "rtsp": case "rtspt": case "rtspu": case "rtsps": break;
                default: return false;
            }
            if (Regex.IsMatch(value, @"[\s\p{Cc}\\<>""{}|^\x60]|%(?![0-9a-fA-F]{2})")) return false;
            string address = UrlAuthority(value);
            int credentials = address.IndexOf('@');
            if (credentials != address.LastIndexOf('@')) return false;
            if (credentials >= 0) {
                if (Regex.IsMatch(address.Substring(0, credentials), @"[\[\]]")) return false;
                address = address.Substring(credentials + 1);
            }
            int port;
            if (address.StartsWith("[")) {
                int close = address.IndexOf(']');
                if (close < 2 || !ValidIpv6(address.Substring(1, close - 1))) return false;
                if (close + 1 == address.Length) return true;
                port = close + 1;
                if (address[port] != ':') return false;
            } else {
                port = address.IndexOf(':');
                string host = authority.catalog.NormalizeDnsSeparators(port < 0 ? address : address.Substring(0, port));
                // DNS labels, including international names and a final root dot.
                const string label = @"[\p{L}\p{Nd}_](?:[\p{L}\p{M}\p{Nd}_-]*[\p{L}\p{M}\p{Nd}_])?";
                if (!authority.catalog.ValidMirrorDnsHost(host) || !Regex.IsMatch(host, @"\A" + label + @"(?:\." + label + @")*\.?\z")) return false;
                if (port < 0) return true;
            }
            string digits = address.Substring(port + 1);
            return Regex.IsMatch(digits, @"\A[0-9]+\z") && int.TryParse(digits, out int number) && number <= 65535;
        }
        bool ValidIpv6(string literal) {
            int zone = literal.IndexOf("%25");
            if (zone >= 0) {
                if (!Regex.IsMatch(literal.Substring(zone + 3), @"\A[A-Za-z0-9._~-]+\z")) return false;
                literal = literal.Substring(0, zone);
            }
            int compressed = literal.IndexOf("::");
            if (compressed != literal.LastIndexOf("::") ||
                (literal.StartsWith(":") && !literal.StartsWith("::")) ||
                (literal.EndsWith(":") && !literal.EndsWith("::"))) return false;
            var groups = literal.Split(':');
            int words = 0;
            for (int i = 0; i < groups.Length; i++) {
                var group = groups[i];
                if (group.Length == 0) continue;
                if (i == groups.Length - 1 && group.IndexOf('.') >= 0) {
                    if (!authority.catalog.ValidIpv4(group)) return false;
                    words += 2;
                } else {
                    if (!Regex.IsMatch(group, @"\A[0-9a-fA-F]{1,4}\z")) return false;
                    words++;
                }
            }
            return compressed < 0 ? words == 8 : words < 8;
        }
        string Host(VRCUrl url) {
            if (VRCUrl.IsNullOrEmpty(url)) return "—";
            string value = url.Get(); int begin = value.IndexOf("://");
            if (begin < 0) return value;
            value = UrlAuthority(value);
            int credentials = value.LastIndexOf('@');
            return credentials >= 0 ? value.Substring(credentials + 1) : value;
        }
        string SourceName(int index, VRCUrl url) {
            string name = nameInputs[index].text;
            if (name.Trim().Length > 0) return name;
            if (index == 0) return Tr("Primary");
            string host = Host(url);
            return host.Length <= 128 ? host : Tr(index == 1 ? "Mirror1" : "Mirror2");
        }
        string ReadOnlySummary() {
            string result = SourceName(0, primary) + " · " + Host(primary);
            for (int i = 0; i < 2; i++) if (!VRCUrl.IsNullOrEmpty(mirrorInputs[i].GetUrl()))
                result += "\n\n" + SourceName(i + 1, mirrorInputs[i].GetUrl()) + " · " + Host(mirrorInputs[i].GetUrl());
            return result;
        }
        string[] DraftLabels() => new[] { nameInputs[0].text, nameInputs[1].text, nameInputs[2].text };
        VRCUrl[] DraftMirrors() => new[] { mirrorInputs[0].GetUrl(), mirrorInputs[1].GetUrl() };
        void CaptureSubmittedFields(VRCUrl[] mirrors) {
            submittedLabels = DraftLabels();
            for (int i = 0; i < 3; i++)
                submittedAutomatic[i] = automaticLabels[i] != null && submittedLabels[i] == automaticLabels[i];
            submittedMirrors = mirrors;
        }
        void SetValidationFeedback(string message) {
            validationFeedback = feedback = message;
            if (string.IsNullOrEmpty(message)) return;
            validationLabels = DraftLabels();
            validationMirrors = DraftMirrors();
        }
        bool SubmittedFieldsUnchanged() => FieldsUnchanged(submittedLabels, submittedMirrors, true);
        // A timeout or failed send can still be confirmed. Only the writer's final refusal releases its fields.
        bool KeepsSubmittedFields() => !string.IsNullOrEmpty(request) && confirmedRequest[0] != "-" + request;
        bool HasCurrentConfirmation() => !authority.PublicationPending && authority.PendingRequestId == request &&
            authority.PublicationStatus == SourceMirrorCatalogAuthority.Confirmed &&
            authority.PendingCommittedVersion == authority.catalog.Version;
        bool HasMatchingConfirmation() {
            if (string.IsNullOrEmpty(request) || !Graph() || !authority.catalog.IsCurrent ||
                (confirmedRequest[0] != request && !HasCurrentConfirmation())) return false;
            string currentPlayback = authority.catalog.ContextPlaybackId;
            if (!authority.MatchesCoreContext(currentPlayback, primary, mobile, backend)) return false;
            // The writer already accepted these inputs. On the original launch the form
            // can read the latest list even if the room subsequently changed editor.
            // Across replays, require the same published list before adopting a new launch.
            return currentPlayback == playback || RowsMatchCatalog(publishedIds, publishedLabels, publishedUrls);
        }
        bool PreviousSaveIsCurrent() => !string.IsNullOrEmpty(deliveredDraft[0]) && deliveredDraft[0] != request &&
            authority.IsCurrentPublicationOrReplay(deliveredDraft[2]);
        bool RepeatedSubmissionMatchesRoom() {
            if (!repeatedSubmission || string.IsNullOrEmpty(request) || !Graph() || authority.PublicationPending ||
                authority.RequiresPublicationRetry || !authority.catalog.IsCurrent || authority.catalog.Version == version ||
                authority.policy.GetRoleRevision() != role) return false;
            string currentPlayback = authority.catalog.ContextPlaybackId;
            return (currentPlayback == playback || PreviousSaveIsCurrent()) && Authorized() &&
                authority.MatchesCoreContext(currentPlayback, primary, mobile, backend) &&
                RowsMatchCatalog(publishedIds, publishedLabels, publishedUrls);
        }
        bool RowsMatchCatalog(string[] sourceIds, string[] labels, VRCUrl[] urls) {
            var catalog = authority.catalog;
            if (sourceIds == null || catalog.FilmId != film || catalog.CompatibilityTag != tag ||
                catalog.SourceCount != sourceIds.Length) return false;
            for (int i = 0; i < sourceIds.Length; i++)
                if (catalog.GetSourceId(i) != sourceIds[i] || catalog.GetSourceLabel(i) != labels[i] ||
                    !urls[i].Equals(catalog.GetSourceUrl(i))) return false;
            return true;
        }
        bool SameDraftUrl(VRCUrl current, VRCUrl saved) => VRCUrl.IsNullOrEmpty(current) ?
            VRCUrl.IsNullOrEmpty(saved) : !VRCUrl.IsNullOrEmpty(saved) && current.Equals(saved);

        bool FieldsUnchanged(string[] names, VRCUrl[] urls, bool submitted = false) {
            if (!Fields() || names.Length != 3 || urls.Length != 2) return false;
            for (int i = 0; i < 3; i++)
                if (nameInputs[i].text != names[i] && !(submitted && submittedAutomatic[i] &&
                    automaticLabels[i] != null && nameInputs[i].text == automaticLabels[i])) return false;
            for (int i = 0; i < 2; i++)
                if (!SameDraftUrl(mirrorInputs[i].GetUrl(), urls[i])) return false;
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
            languageRefreshPending = true;
            RefreshDraftLanguage();
            Put(entryText, Tr("Title")); if (IsOpen) _Refresh();
            if (Utilities.IsValid(entryButton)) entryButton.interactable = Utilities.IsValid(panel);
        }
        void RefreshDraftLanguage() {
            if (!languageRefreshPending || !bound || !Fields()) return;
            bool preserveSubmitted = KeepsSubmittedFields();
            languageRefreshPending = false;
            for (int i = 0; i < 3; i++) {
                if (automaticLabels[i] == null || nameInputs[i].text != automaticLabels[i]) continue;
                string localized = Tr(i == 0 ? "Primary" : i == 1 ? "Mirror1" : "Mirror2");
                if (automaticLabels[i] == localized) continue;
                // An unused slot was never sent. Its placeholder can follow the language
                // immediately, even when a different row is waiting for a late result.
                if (preserveSubmitted && (i == 0 || submittedMirrors.Length == 2 &&
                    !VRCUrl.IsNullOrEmpty(submittedMirrors[i - 1]))) {
                    languageRefreshPending = true;
                    continue;
                }
                automaticLabels[i] = localized;
                nameInputs[i].text = localized;
                if (VRCUrl.IsNullOrEmpty(originalUrls[i])) originalLabels[i] = automaticLabels[i];
            }
        }
        void RefreshDraftContext() {
            RefreshDraftLanguage();
            UpdateDraftBeforeComparison();
            RenewCleanEditorRole();
            // An empty idle view has no draft/CAS capability to preserve. Bind
            // the first arriving movie without requiring a close/reopen.
            if (!bound && Graph() && Fields() && authority.core.HasSharedPlaybackContextForUfc &&
                !authority.PublicationPending) ReadDraft();
            // A different movie resets the form, including its old unsaved rows
            // (10 Nov user request). Same-movie replay still protects edits and
            // pending receipts. Authority retains pending work; this never submits.
            if (bound && Graph() && Fields() && authority.core.HasSharedPlaybackContextForUfc &&
                authority.core.SharedPlaybackIdForUfc != playback) {
                bool otherVideo = !authority.MatchesCoreContext(authority.core.SharedPlaybackIdForUfc, primary, mobile, backend);
                if (otherVideo || (!authority.PublicationPending && !KeepsSubmittedFields() && !HasLocalEdits)) ReadDraft();
                else if (authority.CanCarryFirstCatalogDraft(playback, role) && !authority.PublicationPending && boundAsEditor && Authorized() &&
                    role == authority.policy.GetRoleRevision() && !authority.catalog.HasAcceptedSnapshot &&
                    authority.catalog.Version == version && string.IsNullOrEmpty(RecoveryBase()) &&
                    authority.catalog.ContextPlaybackId == authority.core.SharedPlaybackIdForUfc &&
                    !string.IsNullOrEmpty(authority.core.SharedPlaybackMarkerForUfc)) {
                    // The advised relaunch only changed the launch ID of this still-unpublished film.
                    // Retain its inputs, and require a fresh Save gesture in the new context.
                    playback = authority.core.SharedPlaybackIdForUfc; formRevision++;
                    request = feedback = validationFeedback = "";
                    discardArmed = reviewArmed = closeAfterSave = false;
                }
            }
            // An untouched editor may have opened before the replacement writer's recovery list arrived.
            if (bound && !boundToCurrentCatalog && !recoveredDraft && string.IsNullOrEmpty(recoveryBase) &&
                Graph() && Fields() && boundAsEditor &&
                !authority.PublicationPending && string.IsNullOrEmpty(request) && !discardArmed && !reviewArmed &&
                !expandedMirrors[0] && !expandedMirrors[1] && !string.IsNullOrEmpty(RecoveryBase()) &&
                authority.MatchesCoreContext(playback, primary, mobile, backend) && !HasLocalEdits) ReadDraft();
        }

        public void _Refresh() {
            if (!LocalAction() || !IsOpen) return;
            RefreshDraftContext();
            // An acknowledged, unchanged submission can follow the latest list.
            // Keep any edits made since that submission arrived at the writer.
            bool matchingConfirmation = bound && HasMatchingConfirmation();
            // A late copy of the first save can satisfy the draft even when the retry was rejected.
            // Only an actual confirmation may complete the pending navigation.
            bool canReadRoom = matchingConfirmation || (bound && RepeatedSubmissionMatchesRoom());
            if (canReadRoom && !SubmittedFieldsUnchanged()) {
                editedAfterConfirmation = true;
                closeAfterSave = false;
            } else if (canReadRoom) {
                float position = Utilities.IsValid(fieldsScroll) ? fieldsScroll.verticalNormalizedPosition : 1;
                bool returnToSelection = matchingConfirmation && closeAfterSave && playback == authority.core.SharedPlaybackIdForUfc && role == authority.policy.GetRoleRevision();
                bool latest = HasCurrentConfirmation();
                ReadDraft();
                if (latest) feedback = "Confirmed";
                if (Utilities.IsValid(fieldsScroll)) fieldsScroll.verticalNormalizedPosition = position;
                if (returnToSelection) {
                    ReturnToSelection();
                    return;
                }
            }
            if (feedback == "Confirmed" && HasLocalEdits) feedback = "";
            // A clean read-only view follows the accepted list. An editor's
            // fixed draft, local changes and potentially late receipts stay on the form.
            if (Graph() && Fields() && !Authorized() && !authority.PublicationPending &&
                (bound ? IsDraftStale : authority.core.HasSharedPlaybackContextForUfc) &&
                !HasLocalEdits &&
                (!KeepsSubmittedFields() || (authority.PendingRequestId == request &&
                    authority.PublicationStatus == SourceMirrorCatalogAuthority.Confirmed))) ReadDraft();
            RefreshFormControls();
        }

        string DraftStatus(bool graph, bool editable, bool pending, bool choosingCopy) {
            if (!graph || !Fields()) return "Unavailable";
            if (!bound || !authority.core.HasSharedPlaybackContextForUfc) return "NoVideo";
            if (discardArmed && !pending) return "DiscardFirst";
            if (editedAfterConfirmation) return "NewerDraft";
            if (authority.core.SharedPlaybackIdForUfc != playback || authority.policy.GetRoleRevision() != role)
                return "Changed";

            // Another form can change the authority's status while this form still has its own receipt.
            bool ownRequest = !string.IsNullOrEmpty(request) && authority.PendingRequestId == request;
            int state = 0;
            bool hasReceiptStatus = !string.IsNullOrEmpty(request) && confirmedRequest[0] != request &&
                int.TryParse(confirmedRequest[1], out state);
            if (!hasReceiptStatus && ownRequest) state = authority.PublicationStatus;
            if (ownRequest && (pending || state == SourceMirrorCatalogAuthority.CommittedLocal)) return "Waiting";
            if (editable && !pending && !string.IsNullOrEmpty(feedback)) return feedback;
            if ((ownRequest || hasReceiptStatus) && state != SourceMirrorCatalogAuthority.Confirmed) return SaveFailure(state);

            if (!Authorized()) return "ViewOnly";
            if (IsDraftStale) return "Changed";
            if (pending) return "Waiting";
            if (!string.IsNullOrEmpty(feedback)) return feedback;
            if (!ReadyToSubmit()) return "Unavailable";
            if (reviewArmed) return "ConfirmCopy";
            if (choosingCopy) return "ReviewCopies";
            return HasLocalEdits ? "Unsaved" : "Draft";
        }

        void RefreshFormControls() {
            bool graph = Graph(), editable = CanEdit && Fields();
            bool pending = graph && authority.PublicationPending;
            if (editable && !pending && !string.IsNullOrEmpty(validationFeedback) && validationFeedback == feedback &&
                !FieldsUnchanged(validationLabels, validationMirrors)) SetValidationFeedback(ValidateDraft());
            bool choosingCopy = recoveryCopy != 0 && editable;
            if (reviewArmed && (!choosingCopy || pending || !ReadyToSubmit() || !SubmittedFieldsUnchanged())) reviewArmed = false;
            string status = DraftStatus(graph, editable, pending, choosingCopy);
            int launchHint = editable && !pending ? authority.FirstCatalogLaunchHint : 0;
            if (bound && graph && authority.core.SharedPlaybackIdForUfc == playback) {
                if (launchHint != 0 && (status == "WriterNotReady" || status == "Unavailable" || status == "Draft" || status == "Unsaved"))
                    status = launchHint == SourceMirrorCatalogAuthority.RelaunchForFirstCatalog ? "RestartFirstVideo" : "WaitFirstCatalog";
            }
            string scope = choosingCopy ? (recoveryCopy == LocalCopy ? "ShowingLocal" : "ShowingOwner") :
                bound && recoveredDraft && Authorized() ? "Recovery" : "Scope";
            Put(titleText, Tr("Title")); Put(scopeText, Tr(scope)); Put(confirmationText, Tr("Confirm"));
            Put(primaryText, Tr(IsDraftStale ? "DraftPrimaryUrl" : "PrimaryUrl") + ": " + (bound && !VRCUrl.IsNullOrEmpty(primary) ? primary.Get() : "—"));
            Put(applyText, Tr(reviewArmed ? "PublishCopy" : "Apply"));
            Put(reloadText, Tr(discardArmed ? "Discard" : choosingCopy ? (recoveryCopy == LocalCopy ? "ShowOwner" : "ShowLocal") : "Reload"));
            Put(closeText, Tr("Close"));
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
            RefreshMirrorRows(editable && !pending);
            if (nameCaptions != null) for (int i = 0; i < nameCaptions.Length; i++) Put(nameCaptions[i], (i == 0 ? Tr("PrimaryName") : Tr(i == 1 ? "Mirror1" : "Mirror2") + " · " + Tr("Name")));
            if (urlCaptions != null) for (int i = 0; i < urlCaptions.Length; i++) Put(urlCaptions[i], Tr("MirrorUrl"));
            if (Fields()) {
                for (int i = 0; i < 3; i++) nameInputs[i].interactable = editable && !pending;
                for (int i = 0; i < 2; i++) mirrorInputs[i].interactable = editable && !pending;
            }
            if (Utilities.IsValid(applyButton)) {
                applyButton.gameObject.SetActive(bound && graph && Authorized());
                applyButton.interactable = editable && !pending && launchHint == 0 && ReadyToSubmit();
            }
            if (Utilities.IsValid(reloadButton)) {
                reloadButton.gameObject.SetActive(bound && graph && (Authorized() || HasLocalEdits || !string.IsNullOrEmpty(request)));
                reloadButton.interactable = graph && !pending;
            }
        }
        void RefreshMirrorRows(bool canEdit) {
            bool availableSlot = false;
            for (int i = 0; i < 2; i++) {
                bool visible = expandedMirrors[i] || (Fields() && !VRCUrl.IsNullOrEmpty(mirrorInputs[i].GetUrl()));
                if (!visible) availableSlot = true;
                if (mirrorRows != null && i < mirrorRows.Length && Utilities.IsValid(mirrorRows[i])) mirrorRows[i].SetActive(visible);
                if (removeMirrorButtons != null && i < removeMirrorButtons.Length && Utilities.IsValid(removeMirrorButtons[i]))
                    removeMirrorButtons[i].interactable = canEdit;
            }
            if (Utilities.IsValid(addMirrorButton)) {
                bool canAdd = availableSlot && (canEdit || CanRenewCleanEditorRole(true));
                addMirrorButton.interactable = canAdd;
                addMirrorButton.gameObject.SetActive(canAdd);
            }
            Put(addMirrorText, Tr("AddMirror"));
        }
        void Put(Graphic target, string value) {
            if (!Utilities.IsValid(target)) return;
            var tmp = target.GetComponent<TextMeshProUGUI>(); if (Utilities.IsValid(tmp)) { tmp.text = value; return; }
            var text = target.GetComponent<Text>(); if (Utilities.IsValid(text)) text.text = value;
        }
        string L(string en, string ru, string ja) => language == 1 ? ru : language == 2 ? ja : en;
        string SaveFailure(int code) {
            switch (code) {
                case SourceMirrorCatalogAuthority.NotLeader: return "ViewOnly";
                case SourceMirrorCatalogAuthority.RoleChanged:
                case SourceMirrorCatalog.PlaybackChanged: return "Changed";
                case SourceMirrorCatalog.CatalogChanged:
                case SourceMirrorCatalog.VersionConflict:
                case SourceMirrorCatalogAuthority.Conflict: return "RefreshList";
                case SourceMirrorCatalogAuthority.NotAuthorityReady: return "WriterNotReady";
                case SourceMirrorCatalogAuthority.SerializationFailed: return "SendFailed";
                case SourceMirrorCatalogAuthority.Unconfirmed: return "Unconfirmed";
                case SourceMirrorCatalog.PayloadTooLarge: return "TooLarge";
                default: return "Rejected";
            }
        }
        string Tr(string key) {
            switch (key) {
                case "Title": return L("Shared video sources", "Общие источники видео", "共有動画ソース");
                case "Scope": return L("Available to everyone. Each viewer chooses their own source.", "Список доступен всем. Источник каждый выбирает у себя.", "全員に共有されます。再生ソースは各自で選びます。");
                case "ShowingLocal": return L("Your copy. The room owner's list is different.", "Ваша копия. У владельца комнаты другой список.", "自分の一覧です。ルーム所有者の一覧とは異なります。");
                case "ShowingOwner": return L("Room owner's copy. Your list is different.", "Копия владельца комнаты. Ваш список отличается.", "ルーム所有者の一覧です。自分の一覧とは異なります。");
                case "ReviewCopies": return L("Compare both lists and save the one you want to share.", "Сравните обе копии и сохраните нужную.", "両方の一覧を確認し、共有したい方を保存してください。");
                case "ConfirmCopy": return L("Publish this copy to replace the room list.", "«Опубликовать копию» заменит общий список источниками на экране.", "「この一覧を共有」でルームの一覧を置き換えます。");
                case "PublishCopy": return L("Publish this copy", "Опубликовать копию", "この一覧を共有");
                case "ShowLocal": return L("Show my copy", "К моей копии", "自分の一覧を見る");
                case "ShowOwner": return L("Show owner's copy", "К копии владельца", "所有者の一覧を見る");
                case "Recovery": return L("Last known list. Save to share these sources again.", "Последняя известная копия списка. Сохраните её, чтобы снова открыть источники зрителям.", "前回の一覧です。保存すると、これらのソースが再び全員に共有されます。");
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
                case "Name": return L("name", "название", "名前");
                case "Invalid1": return L("Mirror 1: enter a full video URL.", "Зеркало 1: укажите полный адрес видео.", "ミラー1：動画の完全なURLを入力してください。");
                case "Invalid2": return L("Mirror 2: enter a full video URL.", "Зеркало 2: укажите полный адрес видео.", "ミラー2：動画の完全なURLを入力してください。");
                case "Missing1": return L("Mirror 1: enter a URL or remove the row with ×.", "Зеркало 1: введите ссылку или удалите строку крестиком.", "ミラー1：URLを入力するか、×で行を削除してください。");
                case "Missing2": return L("Mirror 2: enter a URL or remove the row with ×.", "Зеркало 2: введите ссылку или удалите строку крестиком.", "ミラー2：URLを入力するか、×で行を削除してください。");
                case "Duplicate1": return L("Mirror 1 repeats the primary URL.", "Зеркало 1 повторяет основной адрес.", "ミラー1はメインURLと重複しています。");
                case "Duplicate2": return L("Mirror 2 repeats another source URL.", "Зеркало 2 повторяет другой источник.", "ミラー2は別のソースと重複しています。");
                case "Close": return L("Close", "Закрыть", "閉じる");
                case "Unsaved": return L("Unsaved changes", "Есть несохранённые изменения", "未保存の変更があります。");
                case "NewerDraft": return L("Your newer edits are unsaved. Refresh the list to review the saved version.", "Новые правки ещё не сохранены. Нажмите «Обновить список», чтобы перечитать сохранённую версию.", "その後の変更は未保存です。「一覧を更新」で保存済みの一覧を確認できます。");
                case "Draft": return L("Mirrors must match the edit, start and speed.", "Зеркала: тот же монтаж, начало и скорость.", "ミラーは同じ編集・開始位置・速度の動画を指定してください。");
                case "ViewOnly": return L("Only the current host can edit this list.", "Изменять список может только текущий ведущий.", "編集できるのは現在のホストのみです。");
                case "NoVideo": return L("Start a video using the normal player controls first.", "Сначала запустите фильм обычными кнопками плеера.", "先に通常の操作で動画を開始してください。");
                case "Changed": return L("The video, list or host changed. Reload the list before applying.", "Фильм, список или ведущий изменились. Сначала перечитайте список.", "動画・一覧・ホストが変更されました。適用前に一覧を再取得してください。");
                case "Waiting": return L("Saving… waiting for confirmation.", "Сохраняем… ждём подтверждения.", "保存中…確認を待っています。");
                case "Confirmed": return L("Sources updated.", "Источники обновлены.", "ソースを更新しました。");
                case "Unconfirmed": return L("Save was not confirmed. Your draft is kept; refresh to check the list.", "Сохранение не подтверждено. Черновик сохранён; обновите список для проверки.", "保存を確認できません。下書きは保持されています。一覧を更新してください。");
                case "RefreshList": return L("The shared list changed. Refresh it before saving.", "Общий список изменился. Нажмите «Обновить список».", "共有一覧が変更されました。一覧を更新してください。");
                case "WaitFirstCatalog": return L("Waiting for the initial catalog data. Your draft is kept.", "Дождитесь начальных данных каталога. Черновик сохранён.", "一覧の初期データを待っています。下書きは保持されます。");
                case "RestartFirstVideo": return L("Restart the room video from its original URL, then save the sources. Your draft is kept.", "Заново запустите фильм по исходной ссылке, затем сохраните источники. Черновик сохранён.", "元のURLから動画を再生し直して、配信元を保存してください。下書きは保持されます。");
                case "WriterNotReady": return L("Catalog unavailable. If this persists, restart the room video from its URL.", "Каталог пока недоступен. Если ошибка повторится, заново запустите основной URL для зала.", "一覧はまだ利用できません。改善しない場合はメインURLから動画を再生し直してください。");
                case "SendFailed": return L("Could not send the list. Refresh to check what was saved.", "Не удалось отправить список. Нажмите «Обновить список» для проверки.", "一覧を送信できませんでした。一覧を更新して保存内容を確認してください。");
                case "TooLarge": return L("List too large. Shorten the names or URLs and save again.", "Список слишком большой. Сократите названия или ссылки и сохраните ещё раз.", "一覧が大きすぎます。名前やURLを短くして再度保存してください。");
                case "Rejected": return L("Could not save the source list. Your draft is kept.", "Не удалось сохранить список источников. Черновик сохранён.", "ソース一覧を保存できませんでした。下書きは保持されています。");
                case "Unavailable": return L("Saving is unavailable. Your draft is kept.", "Сохранение пока недоступно. Черновик сохранён.", "現在保存できません。下書きは保持されています。");
            }
            return key;
        }
    }
}
