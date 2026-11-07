using System;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace JLChnToZ.VRC.VVMW {
    public partial class Core {
        // One immutable logical-launch header, serialized with Core's existing snapshot.
        // The UUID supplies probabilistic uniqueness, never ordering or another clock.
        [UdonSynced] string sharedPlaybackIdForUfc;
        [UdonSynced] VRCUrl sharedPlaybackPcUrlForUfc, sharedPlaybackMobileUrlForUfc;
        [UdonSynced] byte sharedPlaybackPlayerTypeForUfc;
        [UdonSynced] string sharedPlaybackMarkerForUfc;

        bool acceptedSharedPlaybackForUfc;
        string acceptedSharedPlaybackIdForUfc;
        VRCUrl acceptedSharedPlaybackPcUrlForUfc, acceptedSharedPlaybackMobileUrlForUfc;
        byte acceptedSharedPlaybackPlayerTypeForUfc;
        string acceptedSharedPlaybackMarkerForUfc;
        // Retain the last accepted header across Stop/EOF for collision/immutability checks.
        string lastSharedPlaybackIdForUfc;
        VRCUrl lastSharedPlaybackPcUrlForUfc, lastSharedPlaybackMobileUrlForUfc;
        byte lastSharedPlaybackPlayerTypeForUfc;
        string lastSharedPlaybackMarkerForUfc;
        string nextSharedPlaybackMarkerForUfc = "";
        UdonSharpBehaviour sharedPlaybackObserverForUfc;

        public bool HasSharedPlaybackContextForUfc => synced && acceptedSharedPlaybackForUfc;
        public string SharedPlaybackIdForUfc => HasSharedPlaybackContextForUfc ? acceptedSharedPlaybackIdForUfc : "";
        public VRCUrl CanonicalPcUrlForUfc => HasSharedPlaybackContextForUfc ? acceptedSharedPlaybackPcUrlForUfc : null;
        public VRCUrl EffectiveMobileUrlForUfc => HasSharedPlaybackContextForUfc ? acceptedSharedPlaybackMobileUrlForUfc : null;
        public byte ConfiguredPlayerTypeForUfc => HasSharedPlaybackContextForUfc ? acceptedSharedPlaybackPlayerTypeForUfc : (byte)0;
        public string SharedPlaybackMarkerForUfc => HasSharedPlaybackContextForUfc ? acceptedSharedPlaybackMarkerForUfc : "";

        // A single optional observer owns its registration; unrelated components cannot replace it.
        // Registration has no callback. The caller may read the already coherent public header.
        public bool RegisterSharedPlaybackObserverForUfc(UdonSharpBehaviour observer) {
            if (!Utilities.IsValid(observer) || observer == this) return false;
            if (Utilities.IsValid(sharedPlaybackObserverForUfc) && sharedPlaybackObserverForUfc != observer) return false;
            if (sharedPlaybackObserverForUfc != observer) nextSharedPlaybackMarkerForUfc = "";
            sharedPlaybackObserverForUfc = observer;
            return true;
        }

        public bool UnregisterSharedPlaybackObserverForUfc(UdonSharpBehaviour observer) {
            if (!Utilities.IsValid(observer) || sharedPlaybackObserverForUfc != observer) return false;
            sharedPlaybackObserverForUfc = null;
            nextSharedPlaybackMarkerForUfc = "";
            return true;
        }

        // This is local launch metadata. It does not acquire ownership, load, or serialize.
        // The registered observer prepares a bounded one-shot challenge for the next launch.
        public bool SetNextSharedPlaybackMarkerForUfc(UdonSharpBehaviour observer, string marker) {
            if (!Utilities.IsValid(observer) || sharedPlaybackObserverForUfc != observer ||
                marker == null || marker.Length > 128) return false;
            nextSharedPlaybackMarkerForUfc = marker;
            return true;
        }

        void NotifySharedPlaybackForUfc() {
            var observer = sharedPlaybackObserverForUfc;
            if (Utilities.IsValid(observer) && observer.enabled && observer.gameObject.activeInHierarchy &&
                sharedPlaybackObserverForUfc == observer)
                observer.SendCustomEvent("_OnSharedPlaybackContextForUfc");
        }

        bool SharedPlaybackUrlEqualsForUfc(VRCUrl a, VRCUrl b) =>
            a == b || (!VRCUrl.IsNullOrEmpty(a) && !VRCUrl.IsNullOrEmpty(b) && a.Equals(b));

        bool SharedPlaybackHeaderMatchesLastForUfc() =>
            sharedPlaybackPlayerTypeForUfc == lastSharedPlaybackPlayerTypeForUfc &&
            (sharedPlaybackMarkerForUfc ?? "") == lastSharedPlaybackMarkerForUfc &&
            SharedPlaybackUrlEqualsForUfc(sharedPlaybackPcUrlForUfc, lastSharedPlaybackPcUrlForUfc) &&
            SharedPlaybackUrlEqualsForUfc(sharedPlaybackMobileUrlForUfc, lastSharedPlaybackMobileUrlForUfc);

        bool SharedPlaybackSnapshotIsCoherentForUfc() {
            if (!synced || state == IDLE || string.IsNullOrEmpty(sharedPlaybackIdForUfc) ||
                VRCUrl.IsNullOrEmpty(sharedPlaybackPcUrlForUfc) || VRCUrl.IsNullOrEmpty(sharedPlaybackMobileUrlForUfc) ||
                sharedPlaybackPlayerTypeForUfc == 0 || sharedPlaybackPlayerTypeForUfc != activePlayer ||
                (sharedPlaybackMarkerForUfc != null && sharedPlaybackMarkerForUfc.Length > 128) ||
                !SharedPlaybackUrlEqualsForUfc(sharedPlaybackPcUrlForUfc, pcUrl) ||
                !SharedPlaybackUrlEqualsForUfc(sharedPlaybackMobileUrlForUfc,
                    VRCUrl.IsNullOrEmpty(questUrl) ? pcUrl : questUrl)) return false;
            return sharedPlaybackIdForUfc != lastSharedPlaybackIdForUfc || SharedPlaybackHeaderMatchesLastForUfc();
        }

        bool SharedPlaybackMatchesLocalForUfc() {
            if (!Utilities.IsValid(activeHandler) || localActivePlayer != sharedPlaybackPlayerTypeForUfc) return false;
#if UNITY_ANDROID || UNITY_IOS
            return SharedPlaybackUrlEqualsForUfc(sharedPlaybackMobileUrlForUfc, localUrl) &&
                SharedPlaybackUrlEqualsForUfc(sharedPlaybackPcUrlForUfc, VRCUrl.IsNullOrEmpty(altUrl) ? localUrl : altUrl);
#else
            return SharedPlaybackUrlEqualsForUfc(sharedPlaybackPcUrlForUfc, localUrl) &&
                SharedPlaybackUrlEqualsForUfc(sharedPlaybackMobileUrlForUfc, VRCUrl.IsNullOrEmpty(altUrl) ? localUrl : altUrl);
#endif
        }

        void SetAcceptedSharedPlaybackForUfc(bool valid) {
            bool changed = acceptedSharedPlaybackForUfc != valid ||
                (valid && (acceptedSharedPlaybackIdForUfc != sharedPlaybackIdForUfc ||
                acceptedSharedPlaybackPlayerTypeForUfc != sharedPlaybackPlayerTypeForUfc ||
                acceptedSharedPlaybackMarkerForUfc != (sharedPlaybackMarkerForUfc ?? "") ||
                !SharedPlaybackUrlEqualsForUfc(acceptedSharedPlaybackPcUrlForUfc, sharedPlaybackPcUrlForUfc) ||
                !SharedPlaybackUrlEqualsForUfc(acceptedSharedPlaybackMobileUrlForUfc, sharedPlaybackMobileUrlForUfc)));
            acceptedSharedPlaybackForUfc = valid;
            acceptedSharedPlaybackIdForUfc = valid ? sharedPlaybackIdForUfc : "";
            acceptedSharedPlaybackPcUrlForUfc = valid ? sharedPlaybackPcUrlForUfc : null;
            acceptedSharedPlaybackMobileUrlForUfc = valid ? sharedPlaybackMobileUrlForUfc : null;
            acceptedSharedPlaybackPlayerTypeForUfc = valid ? sharedPlaybackPlayerTypeForUfc : (byte)0;
            acceptedSharedPlaybackMarkerForUfc = valid ? (sharedPlaybackMarkerForUfc ?? "") : "";
            if (valid) {
                lastSharedPlaybackIdForUfc = sharedPlaybackIdForUfc;
                lastSharedPlaybackPcUrlForUfc = sharedPlaybackPcUrlForUfc;
                lastSharedPlaybackMobileUrlForUfc = sharedPlaybackMobileUrlForUfc;
                lastSharedPlaybackPlayerTypeForUfc = sharedPlaybackPlayerTypeForUfc;
                lastSharedPlaybackMarkerForUfc = sharedPlaybackMarkerForUfc ?? "";
            }
            if (changed) NotifySharedPlaybackForUfc();
        }

        void BeginSharedPlaybackForUfc(VRCUrl canonicalPc, VRCUrl mobile, byte configuredPlayer) {
            // Rejected/default-less requests and unusable backends do not mint a launch.
            if (!synced || VRCUrl.IsNullOrEmpty(canonicalPc) || VRCUrl.IsNullOrEmpty(localUrl) ||
                !Utilities.IsValid(activeHandler) || configuredPlayer == 0 || configuredPlayer != localActivePlayer) {
                ClearSharedPlaybackForUfc();
                return;
            }
            string next = Guid.NewGuid().ToString("N");
            if (next == sharedPlaybackIdForUfc || next == lastSharedPlaybackIdForUfc)
                next = Guid.NewGuid().ToString("N");
            if (next == sharedPlaybackIdForUfc || next == lastSharedPlaybackIdForUfc) {
                ClearSharedPlaybackForUfc();
                return;
            }
            sharedPlaybackIdForUfc = next;
            sharedPlaybackPcUrlForUfc = canonicalPc;
            sharedPlaybackMobileUrlForUfc = VRCUrl.IsNullOrEmpty(mobile) ? canonicalPc : mobile;
            sharedPlaybackPlayerTypeForUfc = configuredPlayer;
            sharedPlaybackMarkerForUfc = nextSharedPlaybackMarkerForUfc;
            nextSharedPlaybackMarkerForUfc = "";
            SetAcceptedSharedPlaybackForUfc(SharedPlaybackMatchesLocalForUfc());
        }

        bool IsNewSharedPlaybackSnapshotForUfc() => SharedPlaybackSnapshotIsCoherentForUfc() &&
            lastSharedPlaybackIdForUfc != sharedPlaybackIdForUfc;

        void AcceptSharedPlaybackSnapshotForUfc() =>
            SetAcceptedSharedPlaybackForUfc(SharedPlaybackSnapshotIsCoherentForUfc() && SharedPlaybackMatchesLocalForUfc());

        void ClearSharedPlaybackForUfc() {
            sharedPlaybackIdForUfc = "";
            sharedPlaybackPcUrlForUfc = null;
            sharedPlaybackMobileUrlForUfc = null;
            sharedPlaybackPlayerTypeForUfc = 0;
            sharedPlaybackMarkerForUfc = "";
            SetAcceptedSharedPlaybackForUfc(false);
        }

        void ValidateSharedPlaybackSnapshotForUfc() {
            if (!SharedPlaybackSnapshotIsCoherentForUfc()) ClearSharedPlaybackForUfc();
        }
    }
}
