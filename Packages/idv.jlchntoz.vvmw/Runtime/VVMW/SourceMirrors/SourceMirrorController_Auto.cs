using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.UdonNetworkCalling;

namespace JLChnToZ.VRC.VVMW {
    public partial class SourceMirrorController {
        public const int AutoOff = 0, AutoWaitingForCatalog = 1, AutoLoading = 2,
            AutoReady = 3, AutoExhausted = 4, AutoBlocked = 5, AutoStopped = 6;
        bool autoEnabled, autoAdvancePending, autoAttemptClock;
        int autoState, autoAttempts, autoCycleSerial;
        string autoPlayback = "", autoFilm = "";
        VRCUrl autoCanonical, autoMobile;
        byte autoBackend;
        string[] autoIds = new string[0];
        VRCUrl[] autoUrls = new VRCUrl[0];
        bool[] autoVisited = new bool[0];
        float autoAttemptStartedAt;

        public bool AutoEnabled => autoEnabled;
        public int AutoState => autoState;
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
            autoEnabled = autoAdvancePending = autoAttemptClock = false;
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
            autoCycleSerial++;
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
                for (int i = 0; i < autoUrls.Length; i++) if (loaded.Equals(autoUrls[i])) {
                    autoVisited[i] = true; autoAttempts = 1;
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
                autoAdvancePending = autoAttemptClock = false; autoState = AutoStopped; return;
            }
            if (sourceCatalog.ContextPlaybackId != autoPlayback) {
                autoAdvancePending = autoAttemptClock = false; autoState = AutoWaitingForCatalog;
                if (sourceCatalog.IsCurrent) {
                    // Snapshot now; select later on the presenter's existing tick.
                    BeginAutoCycle();
                }
            } else if (!AutoMatchesContext()) {
                autoAdvancePending = autoAttemptClock = false;
                autoState = sourceCatalog.IsCurrent ? AutoBlocked : AutoWaitingForCatalog;
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
            if (!autoEnabled || autoState == AutoExhausted || autoState == AutoBlocked ||
                autoState == AutoStopped) return;
            autoAttemptClock = false;
            if (!AutoMatchesContext()) { autoState = AutoWaitingForCatalog; return; }
            autoAdvancePending = true; pending = true; Queue();
            diagnostic = "Auto: next source queued";
        }

        void AutoAfterReady() {
            if (!autoEnabled || disabling || autoState == AutoExhausted || autoState == AutoBlocked ||
                !AutoMatchesContext()) return;
            autoAttemptClock = false; autoAdvancePending = false; autoState = AutoReady;
        }

        bool AutoAdvanceTick() {
            if (!autoEnabled || !autoAdvancePending) return false;
            if (!AutoMatchesContext()) {
                autoAdvancePending = false;
                autoState = core.SharedStateForUfc == 0 ? AutoStopped : AutoWaitingForCatalog;
                return false;
            }
            autoAdvancePending = false;
            for (int i = 0; i < autoIds.Length; i++) {
                if (autoVisited[i]) continue;
                autoVisited[i] = true;
                var url = sourceCatalog.ResolveSource(autoIds[i], sourceCatalog.Version, autoPlayback);
                // Never substitute a changed URL or replenish the cycle from metadata.
                if (VRCUrl.IsNullOrEmpty(url) || !url.Equals(autoUrls[i])) continue;
                autoAttempts++; autoState = AutoLoading;
                autoAttemptStartedAt = Time.realtimeSinceStartup; autoAttemptClock = true;
                int cycle = autoCycleSerial;
                bool accepted = SelectCatalogSource(autoIds[i], sourceCatalog.Version, autoPlayback, true);
                if (!autoEnabled || cycle != autoCycleSerial) return true;
                if (!accepted) {
                    autoAttemptClock = false; autoState = AutoBlocked;
                    pending = false; failed = true; diagnostic = "Auto blocked; explicit action required";
                } else {
                    startedAt = autoAttemptStartedAt;
                    if (!pending && !failed) AutoAfterReady();
                }
                return true;
            }
            autoAttemptClock = false; autoState = AutoExhausted;
            pending = false; failed = true; diagnostic = "Auto exhausted; explicit retry required";
            return true;
        }
    }
}
