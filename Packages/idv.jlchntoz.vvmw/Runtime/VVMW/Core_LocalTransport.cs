using UnityEngine;
using VRC.SDKBase;

namespace JLChnToZ.VRC.VVMW {
    public partial class Core {
        // Optional fork API. No module/caller means these guards remain false.
        // This retains Core's existing state/time anchor; it does not virtualize handler.Time.
        bool localTransportActiveForUfc, localTransportReadyForUfc;
        int localTransportGenerationForUfc, localTransportRevisionForUfc;
        int localTransportAlignedRevisionForUfc = -1;
        VRCUrl localTransportCanonicalForUfc;
        AbstractMediaPlayerHandler localTransportHandlerForUfc;
        float localTransportDurationForUfc;
        byte localTransportObservedStateForUfc;
        int localTransportObservedTimeForUfc, localTransportObservedRangeStartForUfc, localTransportObservedRangeEndForUfc;
        float localTransportObservedSpeedForUfc;
        bool localTransportObservedLoopForUfc;

        public bool IsLocalTransportActiveForUfc => localTransportActiveForUfc;
        public int LocalTransportGenerationForUfc => localTransportGenerationForUfc;
        public byte SharedStateForUfc => state;

        /// <summary>Existing Core target, even while local duration is unavailable. SDK Time stays real.</summary>
        public float SharedTargetTimeForUfc {
            get {
                float target;
                switch (state) {
                    case PLAYING:
                        target = (float)((Networking.CalculateServerDeltaTime(Networking.GetServerTimeInSeconds(), time * 0.001) + syncOffset + syncLatency - PerformerLatency) * actualSpeed);
                        break;
                    case PAUSED: target = time * 0.001F; break;
                    default: return 0;
                }
                float duration = localTransportActiveForUfc ? localTransportDurationForUfc : Duration;
                if (duration > 0 && !float.IsInfinity(duration))
                    target = loop ? Mathf.Repeat(target, duration) : Mathf.Clamp(target, 0, duration);
                else target = Mathf.Max(0, target);
                return IsRangeLooping ? CalculateLoopTime(target) : target;
            }
        }

        /// <summary>
        /// Acquire local load authority without publishing local Loading or a decoder-zero clock.
        /// Returns zero if no Playing/Paused room identity exists. Does not start a URL load.
        /// </summary>
        public int BeginLocalTransportForUfc() {
            if (localTransportActiveForUfc || !synced || !Utilities.IsValid(activeHandler) ||
                VRCUrl.IsNullOrEmpty(localUrl)) return 0;
            float duration = activeHandler.Duration;
            if (activeHandler.IsStatic || float.IsInfinity(duration)) return 0;
            if (Networking.IsOwner(gameObject) && activeHandler.IsReady && duration > 0 && !isLoading && !isLocalReloading) {
                // Same publication as stock, before replacing the prepared transport.
                state = activeHandler.IsPlaying ? PLAYING : PAUSED;
                time = (int)(CalcSyncTime(out actualSpeed) * 1000);
            }
            if (state != PLAYING && state != PAUSED) return 0;
            localTransportCanonicalForUfc = localUrl;
            localTransportHandlerForUfc = activeHandler;
            localTransportDurationForUfc = Mathf.Max(0, duration);
            localTransportGenerationForUfc++;
            localTransportRevisionForUfc = 0;
            localTransportAlignedRevisionForUfc = -1;
            localTransportReadyForUfc = false;
            localTransportActiveForUfc = true;
            isLocalReloading = true;
            isLoading = true;
            RememberLocalTransportIntentForUfc();
            return localTransportGenerationForUfc;
        }

        bool HasLocalTransportIdentityForUfc() => localTransportActiveForUfc &&
            Utilities.IsValid(activeHandler) && activeHandler == localTransportHandlerForUfc &&
            !VRCUrl.IsNullOrEmpty(localUrl) && localUrl.Equals(localTransportCanonicalForUfc);

        // A non-advancing signature only, to distinguish a changed command from a duplicate packet.
        void RememberLocalTransportIntentForUfc() {
            localTransportObservedStateForUfc = state;
            localTransportObservedTimeForUfc = time;
            localTransportObservedSpeedForUfc = actualSpeed;
            localTransportObservedLoopForUfc = loop;
            localTransportObservedRangeStartForUfc = localRangeLoopStartMs;
            localTransportObservedRangeEndForUfc = localRangeLoopEndMs;
        }

        void AcceptReceivedLocalTransportIntentForUfc() {
            if (localTransportObservedStateForUfc == state && localTransportObservedTimeForUfc == time &&
                localTransportObservedSpeedForUfc == actualSpeed && localTransportObservedLoopForUfc == loop &&
                localTransportObservedRangeStartForUfc == localRangeLoopStartMs &&
                localTransportObservedRangeEndForUfc == localRangeLoopEndMs) return;
            localTransportRevisionForUfc++;
            RememberLocalTransportIntentForUfc();
        }

        void SetLocalTransportIntentForUfc(byte nextState, float target) {
            state = nextState;
            if (nextState == PAUSED) time = Mathf.RoundToInt(target * 1000);
            else {
                float rate = actualSpeed > 0 ? actualSpeed : 1;
                double elapsed = target / rate - syncOffset - syncLatency + PerformerLatency;
                long anchor = Networking.GetServerTimeInMilliseconds() - (long)(elapsed * 1000);
                if (anchor > int.MaxValue) anchor -= uint.MaxValue;
                time = (int)anchor;
            }
            localTransportRevisionForUfc++;
            RememberLocalTransportIntentForUfc();
        }

        /// <summary>Latest room seek while a local decoder is unavailable; no native write.</summary>
        public bool SetSharedTimeDuringLocalTransportForUfc(float seconds) {
            if (!HasLocalTransportIdentityForUfc() || float.IsNaN(seconds) || float.IsInfinity(seconds)) return false;
            float target = Mathf.Max(0, seconds);
            if (localTransportDurationForUfc > 0) target = Mathf.Min(target, localTransportDurationForUfc);
            if (IsRangeLooping) target = CalculateLoopTime(target);
            SetLocalTransportIntentForUfc(state, target);
            RequestSync();
            return true;
        }

        /// <summary>
        /// One Core-owned alignment per accepted intent revision. No retry loop or predictive lead.
        /// Caller must keep picture/audio guarded until presentation has actually settled.
        /// </summary>
        public bool AlignLocalTransportForUfc(int generation) {
            if (generation != localTransportGenerationForUfc || !HasLocalTransportIdentityForUfc() ||
                !localTransportReadyForUfc || !activeHandler.IsReady ||
                localTransportAlignedRevisionForUfc == localTransportRevisionForUfc) return false;
            if (state != PLAYING && state != PAUSED) return false;
            float duration = activeHandler.Duration;
            if (duration <= 0 || float.IsInfinity(duration)) return false;
            localTransportDurationForUfc = duration;
            localTransportAlignedRevisionForUfc = localTransportRevisionForUfc;
            if (state == PAUSED) activeHandler.Pause();
            Time = SharedTargetTimeForUfc;
            if (state == PLAYING && !activeHandler.IsPlaying) activeHandler.Play();
            return true;
        }

        /// <summary>Release after latest target and caller's picture gate; stale tokens cannot release.</summary>
        public bool CompleteLocalTransportForUfc(int generation) {
            if (generation != localTransportGenerationForUfc || !HasLocalTransportIdentityForUfc() ||
                !localTransportReadyForUfc || !activeHandler.IsReady ||
                localTransportAlignedRevisionForUfc != localTransportRevisionForUfc) return false;
            float tolerance = state == PAUSED ? 0.1F : timeDriftDetectThreshold;
            if ((state == PAUSED && !activeHandler.IsPaused) ||
                (state == PLAYING && !activeHandler.IsPlaying) ||
                Mathf.Abs(activeHandler.Time - SharedTargetTimeForUfc) > tolerance) return false;
            localTransportActiveForUfc = false;
            isLocalReloading = false;
            isLoading = false;
            isError = false;
            // Reuse stock cadence, without a forced owner reanchor at handback.
            if (!isResyncTime) {
                isResyncTime = true;
                SendCustomEventDelayedSeconds(nameof(_AutoSyncTime), 0.5F);
            }
            StartCheckRangeLoop();
            return true;
        }

        void CancelLocalTransportForUfc() {
            if (!localTransportActiveForUfc) return;
            localTransportGenerationForUfc++;
            localTransportActiveForUfc = false;
            localTransportReadyForUfc = false;
            localTransportAlignedRevisionForUfc = -1;
            isLocalReloading = false;
        }

        void InvalidateLocalTransportAuthorityForUfc() {
            if (!localTransportActiveForUfc) return;
            // Keep publication protected during a handoff; invalidate the old caller's write token.
            localTransportGenerationForUfc++;
            localTransportAlignedRevisionForUfc = -1;
            localTransportReadyForUfc = false;
        }
    }
}
