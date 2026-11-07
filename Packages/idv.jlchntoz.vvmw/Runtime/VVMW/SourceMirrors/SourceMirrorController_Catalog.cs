using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.UdonNetworkCalling;

namespace JLChnToZ.VRC.VVMW {
    public partial class SourceMirrorController {
        public SourceMirrorCatalog sourceCatalog;
        string selectedSourceId = "", selectedFilmId = "", selectedPlaybackId = "", selectedVersion = "";
        VRCUrl selectedPhysical;
        VRCUrl selectedCanonical, selectedMobile;
        byte selectedBackend;
        int selectionResult = SourceMirrorCatalog.NotContextReady;

        public string SelectedSourceId => selectedSourceId;
        public string SelectedCatalogVersion => selectedVersion;
        public string SelectedPlaybackId => selectedPlaybackId;
        public int SelectionResult => selectionResult;

        // Computed from an accepted selection AND the actual ready load receipt.
        // No Finish hook: generation==0 may complete inside Request, and Finish
        // also serves Off cancellation. Neither can falsely confirm a catalog ID.
        public string ActiveSourceId {
            get {
                if (!enabled || !gameObject.activeInHierarchy || mirrorsOff || disabling || pending || failed ||
                    !picturePresented || !ReceiptMatchesContext() || !core.IsReady || core.SharedStateForUfc == 0)
                    return "";
                var loaded = core.LocalTransportLoadedPhysicalUrlForUfc;
                return !VRCUrl.IsNullOrEmpty(loaded) && loaded.Equals(selectedPhysical) ? selectedSourceId : "";
            }
        }

        bool ReceiptMatchesContext() => Utilities.IsValid(core) && Utilities.IsValid(sourceCatalog) &&
            !string.IsNullOrEmpty(selectedSourceId) && !VRCUrl.IsNullOrEmpty(selectedPhysical) &&
            SelectedFilmMatchesContext() && sourceCatalog.ContextPlaybackId == selectedPlaybackId &&
            sourceCatalog.FilmId == selectedFilmId && sourceCatalog.ContextBackend == core.ActivePlayer &&
            !VRCUrl.IsNullOrEmpty(sourceCatalog.ContextCanonical) && !VRCUrl.IsNullOrEmpty(core.Url) &&
            sourceCatalog.ContextCanonical.Equals(core.Url) && SameFilm();

        bool SelectedFilmMatchesContext() => Utilities.IsValid(sourceCatalog) && sourceCatalog.HasLocalContext &&
            sourceCatalog.FilmId == selectedFilmId && sourceCatalog.ContextBackend == selectedBackend &&
            !VRCUrl.IsNullOrEmpty(selectedCanonical) && !VRCUrl.IsNullOrEmpty(selectedMobile) &&
            selectedCanonical.Equals(sourceCatalog.ContextCanonical) &&
            selectedMobile.Equals(sourceCatalog.ContextMobileCanonical);

        // Metadata-only invalidation. Never cancels/restarts a physical load.
        public void CatalogContextChanged(SourceMirrorCatalog changedCatalog) {
            if (NetworkCalling.InNetworkCall || changedCatalog != sourceCatalog || SelectedFilmMatchesContext()) return;
            selectedSourceId = selectedFilmId = selectedPlaybackId = selectedVersion = "";
            selectedPhysical = selectedCanonical = selectedMobile = null; selectedBackend = 0;
        }

        bool CanSelectCatalogSource() {
            if (NetworkCalling.InNetworkCall) { selectionResult = SourceMirrorCatalog.LocalOnly; return false; }
            if (!enabled || !gameObject.activeInHierarchy || mirrorsOff || disabling) {
                selectionResult = SourceMirrorCatalog.Disabled; return false;
            }
#if UNITY_ANDROID || UNITY_IOS
            selectionResult = SourceMirrorCatalog.UnsupportedPlatform; return false;
#else
            if (!Utilities.IsValid(core) || !Utilities.IsValid(sourceCatalog) || !sourceCatalog.IsCurrent ||
                core.SharedStateForUfc == 0 || !SameFilm() || sourceCatalog.ContextBackend != core.ActivePlayer ||
                VRCUrl.IsNullOrEmpty(sourceCatalog.ContextCanonical) ||
                !sourceCatalog.ContextCanonical.Equals(core.Url)) {
                selectionResult = SourceMirrorCatalog.NotContextReady; return false;
            }
            return true;
#endif
        }

        public bool SelectSource(string sourceId, string expectedCatalogVersion, string expectedPlaybackId) {
            if (!CanSelectCatalogSource()) return false;
            var catalog = sourceCatalog;
            var requestedCore = core;
            VRCUrl physical = catalog.ResolveSource(sourceId, expectedCatalogVersion, expectedPlaybackId);
            if (VRCUrl.IsNullOrEmpty(physical)) { selectionResult = catalog.Result; return false; }
            if (!catalog.RegisterLocalSelectionObserver(this)) {
                selectionResult = SourceMirrorCatalog.NotContextReady; return false;
            }
            string requestedFilm = catalog.FilmId;
            VRCUrl requestedCanonical = catalog.ContextCanonical;
            VRCUrl requestedMobile = catalog.ContextMobileCanonical;
            byte requestedBackend = catalog.ContextBackend;
            // Existing Request owns every physical command and output transition.
            // Keep the old intent/receipt if the replacement is rejected.
            Request(physical);
            if (!requestAccepted) { selectionResult = SourceMirrorCatalog.TransportRejected; return false; }
            if (sourceCatalog != catalog || core != requestedCore ||
                catalog.ContextPlaybackId != expectedPlaybackId || catalog.FilmId != requestedFilm ||
                catalog.ContextBackend != requestedBackend ||
                !requestedCanonical.Equals(catalog.ContextCanonical) ||
                !requestedMobile.Equals(catalog.ContextMobileCanonical)) {
                selectionResult = SourceMirrorCatalog.PlaybackChanged; return false;
            }
            selectedSourceId = sourceId;
            selectedFilmId = requestedFilm;
            selectedPlaybackId = expectedPlaybackId;
            selectedVersion = expectedCatalogVersion;
            selectedPhysical = physical;
            selectedCanonical = requestedCanonical;
            selectedMobile = requestedMobile;
            selectedBackend = requestedBackend;
            requestedPhysical = physical;
            selectionResult = SourceMirrorCatalog.Accepted;
            return true;
        }

        public bool UsePrimary(string expectedCatalogVersion, string expectedPlaybackId) {
            if (!CanSelectCatalogSource()) return false;
            return SelectSource(sourceCatalog.GetSourceId(0), expectedCatalogVersion, expectedPlaybackId);
        }

        public bool RetrySelected(string expectedCatalogVersion, string expectedPlaybackId) {
            if (!CanSelectCatalogSource()) return false;
            if (string.IsNullOrEmpty(selectedSourceId) || !SelectedFilmMatchesContext()) {
                selectionResult = SourceMirrorCatalog.Unavailable; return false;
            }
            bool accepted = SelectSource(selectedSourceId, expectedCatalogVersion, expectedPlaybackId);
            if (!accepted && selectionResult == SourceMirrorCatalog.UnknownSource)
                selectionResult = SourceMirrorCatalog.Unavailable;
            return accepted;
        }
    }
}
