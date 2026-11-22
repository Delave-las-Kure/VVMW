using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.UdonNetworkCalling;

namespace JLChnToZ.VRC.VVMW {
    public partial class SourceMirrorController {
        public const int AutoOff = 0, AutoWaitingForCatalog = 1, AutoLoading = 2,
            AutoReady = 3, AutoExhausted = 4, AutoBlocked = 5, AutoStopped = 6;
        bool autoEnabled, autoAdvancePending, autoAttemptClock, autoWaitingForCatalog;
        int autoState, autoAttempts, autoCycleSerial;
        string autoPlayback = "", autoFilm = "";
        VRCUrl autoCanonical, autoMobile;
        byte autoBackend;
        string[] autoIds = new string[0];
        VRCUrl[] autoUrls = new VRCUrl[0];
        bool[] autoVisited = new bool[0];
        float autoAttemptStartedAt;

        public bool AutoEnabled => autoEnabled;
        public int AutoState => autoWaitingForCatalog ? AutoWaitingForCatalog :
            autoState == AutoExhausted && pending && !failed ? AutoLoading : autoState;
        public int AutoAttempts => autoAttempts;
        public string AutoPlaybackId => autoPlayback;

        bool AutoMatchesContext() => Utilities.IsValid(sourceCatalog) && sourceCatalog.IsCurrent &&
            Utilities.IsValid(core) && core.SharedStateForUfc != 0 &&
            autoPlayback == sourceCatalog.ContextPlaybackId && autoFilm == sourceCatalog.FilmId &&
            autoBackend == sourceCatalog.ContextBackend && autoBackend == CatalogBackend() &&
            !VRCUrl.IsNullOrEmpty(autoCanonical) && !VRCUrl.IsNullOrEmpty(autoMobile) &&
            autoCanonical.Equals(sourceCatalog.ContextCanonical) && autoCanonical.Equals(core.Url) &&
            autoMobile.Equals(sourceCatalog.ContextMobileCanonical);

        void StopAuto() {
            autoCycleSerial++;
            autoEnabled = autoAdvancePending = autoAttemptClock = autoWaitingForCatalog = false;
            autoState = AutoOff;
        }

        public bool UseAuto(string expectedCatalogVersion, string expectedPlaybackId) {
            if (!CanSelectCatalogSource()) return false;
            // Resolve through the same CAS/context validation as an explicit choice.
            if (VRCUrl.IsNullOrEmpty(sourceCatalog.ResolveSource(sourceCatalog.GetSourceId(0),
                expectedCatalogVersion, expectedPlaybackId))) {
                selectionResult = sourceCatalog.Result; return false;
            }
            if (!sourceCatalog.RegisterLocalSelectionObserver(this)) {
                selectionResult = SourceMirrorCatalog.NotContextReady; return false;
            }
            if (!Configured() || !Register()) { selectionResult = SourceMirrorCatalog.TransportRejected; return false; }
            autoEnabled = true;
            BeginAutoCycle();
            selectionResult = SourceMirrorCatalog.Accepted;
            return true;
        }

        public bool RetryAuto(string expectedCatalogVersion, string expectedPlaybackId) {
            if (NetworkCalling.InNetworkCall || !autoEnabled) return false;
            return UseAuto(expectedCatalogVersion, expectedPlaybackId);
        }

        void BeginAutoCycle() {
            autoCycleSerial++; autoWaitingForCatalog = false;
            autoPlayback = sourceCatalog.ContextPlaybackId; autoFilm = sourceCatalog.FilmId;
            autoCanonical = sourceCatalog.ContextCanonical; autoMobile = sourceCatalog.ContextMobileCanonical;
            autoBackend = sourceCatalog.ContextBackend;
            autoIds = sourceCatalog.GetSourceIds(); autoUrls = sourceCatalog.GetSourceUrls();
            autoVisited = new bool[autoIds.Length]; autoAttempts = 0; autoAttemptClock = false;
            if (AdoptAutoReadySource()) return;
            autoAdvancePending = true; autoState = AutoLoading;
            pending = true; Queue();
        }

        bool AdoptAutoReadySource() {
            // Explicit Auto intent may accept an already presented ready receipt.
            // No hold/release or native request is needed just to change the mode.
            var loaded = core.LocalTransportLoadedPhysicalUrlForUfc;
            if (!pending && !failed && picturePresented && core.IsReady &&
                !core.HasLocalTransportErrorForUfc && !VRCUrl.IsNullOrEmpty(loaded)) {
                int i = FindListedEndpoint(loaded, selectedSourceId);
                if (i >= 0) {
                    VisitAutoEndpoint(i); autoAttempts = 1;
                    selectedSourceId = autoIds[i]; selectedPhysical = loaded;
                    selectedFilmId = autoFilm; selectedPlaybackId = autoPlayback;
                    selectedVersion = sourceCatalog.Version; selectedCanonical = autoCanonical;
                    selectedMobile = autoMobile; selectedBackend = autoBackend;
                    autoAdvancePending = false; autoState = AutoReady;
                    diagnostic = "Auto: current source ready"; return true;
                }
            }
            return false;
        }

        // Called by metadata/context notifications; never issues a physical command.
        void AutoContextChanged() {
            if (!autoEnabled) return;
            if (!Utilities.IsValid(core) || core.SharedStateForUfc == 0) {
                autoAdvancePending = autoAttemptClock = autoWaitingForCatalog = false; autoState = AutoStopped; return;
            }
            if (!sourceCatalog.IsCurrent) {
                // Park the cycle and its visited sources. No load can start until a matching list returns.
                autoWaitingForCatalog = true;
                return;
            }
            if (sourceCatalog.ContextPlaybackId != autoPlayback) {
                BeginAutoCycle();
                return;
            }
            bool wasWaiting = autoWaitingForCatalog;
            autoWaitingForCatalog = false;
            if (!AutoMatchesContext()) {
                autoAdvancePending = autoAttemptClock = false; autoState = AutoBlocked;
            } else if (wasWaiting) {
                if (autoAdvancePending) { pending = true; Queue(); }
                else if ((autoState == AutoLoading || autoState == AutoExhausted) && !pending && !failed && picturePresented && core.IsReady) AutoAfterReady();
            }
        }

        void AutoObserveBegin() {
            if (!autoEnabled || !AutoMatchesContext()) return;
            // Repeated Begin/backend/revision notifications don't extend this attempt.
            if (!autoAttemptClock) { autoAttemptStartedAt = startedAt; autoAttemptClock = true; }
            startedAt = autoAttemptStartedAt;
            if (autoState != AutoExhausted && autoState != AutoBlocked) autoState = AutoLoading;
        }

        void AutoAfterFailure() {
            if (!autoEnabled) return;
            autoAttemptClock = false;
            if (autoState == AutoExhausted || autoState == AutoBlocked || autoState == AutoStopped) return;
            autoAdvancePending = true;
            if (!AutoMatchesContext()) { autoWaitingForCatalog = true; return; }
            pending = true; Queue();
            diagnostic = "Auto: next source queued";
        }

        void AutoAfterReady() {
            if (!autoEnabled || disabling || autoState == AutoBlocked || autoState == AutoStopped ||
                !AutoMatchesContext()) return;
            // A local resync can present the last candidate after Auto has spent its retries.
            if (autoState == AutoExhausted && (core.HasLocalTransportErrorForUfc || string.IsNullOrEmpty(ActiveSourceId))) return;
            autoAttemptClock = false; autoAdvancePending = false; autoState = AutoReady;
        }

        bool AutoAdvanceTick() {
            if (!autoEnabled || !autoAdvancePending) return false;
            if (!AutoMatchesContext()) {
                if (core.SharedStateForUfc == 0) {
                    autoAdvancePending = autoAttemptClock = autoWaitingForCatalog = false; autoState = AutoStopped;
                } else autoWaitingForCatalog = true;
                return false;
            }
            autoAdvancePending = false;
            for (int i = 0; i < autoIds.Length; i++) {
                if (autoVisited[i]) continue;
                autoVisited[i] = true;
                // Never substitute a changed URL or replenish the cycle from metadata.
                int listed = FindListedEndpoint(autoUrls[i], autoIds[i]);
                if (listed < 0) continue;
                VisitAutoEndpoint(i);
                autoAttempts++; autoState = AutoLoading;
                autoAttemptStartedAt = Time.realtimeSinceStartup; autoAttemptClock = true;
                int cycle = autoCycleSerial;
                bool accepted = SelectCatalogSource(sourceCatalog.GetSourceId(listed), sourceCatalog.Version, autoPlayback, true);
                if (!autoEnabled || cycle != autoCycleSerial) return true;
                if (!accepted) {
                    EndAutoAttempts(AutoBlocked);
                } else {
                    startedAt = autoAttemptStartedAt;
                    if (!pending && !failed) AutoAfterReady();
                }
                return true;
            }
            EndAutoAttempts(AutoExhausted);
            return true;
        }

        void VisitAutoEndpoint(int index) {
            autoVisited[index] = true;
            // Historical lists can have several IDs for the same physical endpoint.
            for (int i = 0; i < autoUrls.Length; i++)
                if (!autoVisited[i] && sourceCatalog.SameSourceEndpoint(autoUrls[i], autoUrls[index])) autoVisited[i] = true;
        }

        void EndAutoAttempts(int state) {
            autoAttemptClock = false; autoState = state;
            // A refused or missing candidate did not start a new load. Let an
            // existing transition finish, or leave its already-ready output alone.
            pending = held && !failed;
            failed = !pending && (held || !core.IsReady || core.HasLocalTransportErrorForUfc);
            diagnostic = state == AutoBlocked ? "Auto blocked; explicit action required" : "Auto exhausted; explicit retry required";
            if (pending) Queue();
        }
    }
}
