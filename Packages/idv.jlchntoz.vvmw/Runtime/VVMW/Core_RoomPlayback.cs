using UnityEngine;
using VRC.SDKBase;

namespace JLChnToZ.VRC.VVMW {
    public partial class Core {
        // Consumer facts, not permissions or another playback clock.
        public bool CanControlRoomPlaybackForUfc => HasSharedPlaybackContextForUfc &&
            SharedPlaybackMatchesLocalForUfc() && (state == PLAYING || state == PAUSED) &&
            (localTransportActiveForUfc || localTransportPresentationPendingForUfc ?
                HasLocalTransportMediaIdentityForUfc() : IsReady && !isLoading && !isError);
        public bool IsRoomPlayingForUfc => CanControlRoomPlaybackForUfc && state == PLAYING;
        public float RoomPlaybackDurationForUfc => localTransportActiveForUfc || HasLocalTransportPresentationForUfc() ?
            (HasLocalTransportMediaIdentityForUfc() ? localTransportDurationForUfc : 0) : Duration;
        public bool CanSeekRoomPlaybackForUfc => CanControlRoomPlaybackForUfc &&
            RoomPlaybackDurationForUfc > 0 && RoomPlaybackDurationForUfc < float.MaxValue;
        public float RoomPlaybackTimeForUfc => CanControlRoomPlaybackForUfc ? SharedTargetTimeForUfc : Time;

        // A checkpoint must never sample an unaccepted native alignment or local error.
        public bool CanCapturePlaybackForUfc => CanCapturePlaybackPositionForUfc(Time);
        public bool CanCapturePlaybackPositionForUfc(float position) => !localTransportActiveForUfc &&
            !localTransportPresentationPendingForUfc && !isLoading && !isError &&
            (VRCUrl.IsNullOrEmpty(localUrl) || (CanControlRoomPlaybackForUfc && IsReady && !float.IsNaN(position) && !float.IsInfinity(position) && position >= 0 &&
                (IsStatic || float.IsInfinity(Duration) || Mathf.Abs(position - SharedTargetTimeForUfc) <= LocalTransportPictureToleranceForUfc)));

        void ReopenPresentationForRoomCommandForUfc() {
            if (localTransportActiveForUfc || (state != PLAYING && state != PAUSED) || !HasLocalTransportPresentationForUfc()) return;
            // The same output transaction has not acknowledged its final picture yet.
            localTransportActiveForUfc = true;
            localTransportReadyForUfc = activeHandler.IsReady;
        }
    }
}
