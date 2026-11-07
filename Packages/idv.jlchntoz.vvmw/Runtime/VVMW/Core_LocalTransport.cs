using UnityEngine;
using VRC.SDKBase;
using UdonSharp;

namespace JLChnToZ.VRC.VVMW {
    public partial class Core {
        // Read-only output ownership check; no reference to optional mirror types.
        public bool OwnsExclusiveAudioOutputForUfc(AbstractAudioController owner, AudioSource output) {
            if (!Utilities.IsValid(owner) || !Utilities.IsValid(output)) return false;
            if (audioSources != null)
                for (int i = 0; i < audioSources.Length; i++)
                    if (audioSources[i] == output) return false;
            int matches = 0;
            if (audioControllers != null)
                for (int i = 0; i < audioControllers.Length; i++) {
                    var candidate = audioControllers[i];
                    if (!Utilities.IsValid(candidate)) continue;
                    if (candidate == owner) matches++;
                    else if (candidate.OutputAudioSourceForUfc == output) return false;
                }
            return matches == 1;
        }
        // Optional fork API. No module/caller means these guards remain false.
        // This retains Core's existing state/time anchor; it does not virtualize handler.Time.
        bool localTransportActiveForUfc, localTransportReadyForUfc;
        bool localTransportPresentationPendingForUfc;
        bool localTransportRouteManagedForUfc, localTransportInitialOpeningForUfc, localTransportRequestAcceptedForUfc;
        bool localTransportSeekPendingForUfc;
        float localTransportSeekTargetForUfc, localTransportSeekRawForUfc;
        int deferredErrorGenerationForUfc;
        UdonSharpBehaviour localTransportPresenterForUfc;
        VRCUrl localTransportPresenterCanonicalForUfc;
        bool localTransportPresenterWatchesLoadsForUfc;
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
        public bool IsLocalTransportPresentationPendingForUfc => localTransportPresentationPendingForUfc;
        public int LocalTransportGenerationForUfc => localTransportGenerationForUfc;
        public int LocalTransportRevisionForUfc => localTransportRevisionForUfc;
        public bool IsLocalTransportReadyForUfc => localTransportReadyForUfc &&
            Utilities.IsValid(activeHandler) && activeHandler.IsReady;
        public bool HasLocalTransportErrorForUfc => isError;
        public byte SharedStateForUfc => state;
        public bool LocalTransportRequestAcceptedForUfc => localTransportRequestAcceptedForUfc;
        public float LocalTransportPictureToleranceForUfc => state == PAUSED ? 0.08F : timeDriftDetectThreshold;

        bool CanRunLocalTransportSyncForUfc() => gameObject.activeInHierarchy && enabled &&
            HasLocalTransportIdentityForUfc() && HasLocalTransportPresenterForUfc(localUrl) &&
            state == PLAYING && localTransportReadyForUfc && activeHandler.IsReady && !isError &&
            localTransportAlignedRevisionForUfc == localTransportRevisionForUfc;

        // Reuse the existing Core cadence. A presenter may restart it after re-enabling,
        // but never creates another timer or writes the decoder itself.
        public bool ResumeLocalTransportSyncForUfc(int generation) {
            if (generation != localTransportGenerationForUfc || !CanRunLocalTransportSyncForUfc()) return false;
            if (!isResyncTime) {
                isResyncTime = true;
                SendCustomEventDelayedSeconds(nameof(_AutoSyncTime), 0.5F);
            }
            return true;
        }

        void ArmLocalTransportSeekForUfc(float target) {
            localTransportSeekPendingForUfc = state == PLAYING && HasLocalTransportPresenterForUfc(localUrl);
            localTransportSeekTargetForUfc = target;
            localTransportSeekRawForUfc = activeHandler.Time;
        }

        bool AcknowledgeLocalTransportSeekForUfc() {
            if (!localTransportSeekPendingForUfc) return true;
            float raw = activeHandler.Time;
            bool moved = raw > localTransportSeekRawForUfc + 0.002F;
            localTransportSeekRawForUfc = raw;
            if (!activeHandler.IsPlaying || !moved || float.IsNaN(raw) || float.IsInfinity(raw) ||
                raw < localTransportSeekTargetForUfc - 0.1F ||
                raw > SharedTargetTimeForUfc + timeDriftDetectThreshold) return false;
            localTransportSeekPendingForUfc = false;
            // A seek jump is not ordinary buffering progress. Start the existing raw
            // comparison from the observed post-seek position, without estimating a lead.
            lastSyncRawTime = raw;
            timeChangeRequested = false;
            isBuffering = false;
            return true;
        }

        public bool RegisterLocalTransportPresenterForUfc(UdonSharpBehaviour presenter) =>
            RegisterLocalTransportPresenterForUfc(presenter, null, false);

        public bool RegisterLocalTransportPresenterForUfc(UdonSharpBehaviour presenter, VRCUrl canonicalUrl, bool watchLoads) {
            if (Utilities.IsValid(localTransportPresenterForUfc) && Utilities.IsValid(presenter) &&
                localTransportPresenterForUfc != presenter) return false;
            localTransportPresenterForUfc = presenter;
            localTransportPresenterCanonicalForUfc = canonicalUrl;
            localTransportPresenterWatchesLoadsForUfc = watchLoads;
            return true;
        }

        bool HasLocalTransportPresenterForUfc(VRCUrl canonicalUrl) =>
            Utilities.IsValid(localTransportPresenterForUfc) && localTransportPresenterForUfc.enabled &&
            localTransportPresenterForUfc.gameObject.activeInHierarchy && !VRCUrl.IsNullOrEmpty(canonicalUrl) &&
            !VRCUrl.IsNullOrEmpty(localTransportPresenterCanonicalForUfc) && canonicalUrl.Equals(localTransportPresenterCanonicalForUfc);

        bool WatchesLocalTransportLoadForUfc(VRCUrl canonicalUrl) =>
            localTransportPresenterWatchesLoadsForUfc && HasLocalTransportPresenterForUfc(canonicalUrl);

        void NotifyLocalTransportPresenterForUfc(string eventName) {
            if (HasLocalTransportPresenterForUfc(localUrl)) localTransportPresenterForUfc.SendCustomEvent(eventName);
        }

        /// <summary>Adopt a matching load already issued by Core, without a second SDK load.</summary>
        public int AdoptLocalTransportForPresenterForUfc() {
            if (!WatchesLocalTransportLoadForUfc(localUrl) || !Utilities.IsValid(activeHandler)) return 0;
            var video = VideoTransportForUfc(activeHandler);
            if (!Utilities.IsValid(video)) return 0;
            if (!localTransportActiveForUfc && !isLoading &&
                (state != PAUSED || !video.IsReady ||
                (Mathf.Abs(video.Time - SharedTargetTimeForUfc) <= 0.1F && Utilities.IsValid(video.Texture)))) return 0;
            if (!localTransportActiveForUfc) {
                if (state == LOADING) { if (!BeginInitialTransportForUfc()) return 0; }
                else if (BeginLocalTransportForUfc() == 0) return 0;
                localTransportRouteManagedForUfc = true;
                video.AdoptLocalTransportLoadForUfc(localTransportGenerationForUfc);
                localTransportReadyForUfc = video.IsReady;
            }
            localTransportPresentationPendingForUfc = true;
            NotifyLocalTransportPresenterForUfc("_OnLocalTransportBeginForUfc");
            return localTransportGenerationForUfc;
        }

        bool SetPausedProgressForPresenterForUfc(float seconds) {
            if (state != PAUSED || !WatchesLocalTransportLoadForUfc(localUrl) ||
                !Utilities.IsValid(activeHandler) || !activeHandler.IsReady || float.IsNaN(seconds) || float.IsInfinity(seconds)) return false;
            var video = VideoTransportForUfc(activeHandler);
            if (!Utilities.IsValid(video) || BeginLocalTransportForUfc() == 0) return false;
            localTransportRouteManagedForUfc = true;
            video.AdoptLocalTransportLoadForUfc(localTransportGenerationForUfc);
            localTransportReadyForUfc = video.IsReady;
            SetSharedTimeDuringLocalTransportForUfc(seconds);
            NotifyLocalTransportPresenterForUfc("_OnLocalTransportBeginForUfc");
            return true;
        }

        public bool RefreshLocalTransportPictureForUfc(int generation) {
            if (generation != localTransportGenerationForUfc || !Utilities.IsValid(activeHandler) ||
                !activeHandler.IsReady || isError || (state != PLAYING && state != PAUSED)) return false;
            if (localTransportActiveForUfc && (localTransportSeekPendingForUfc || !HasLocalTransportIdentityForUfc() ||
                localTransportAlignedRevisionForUfc != localTransportRevisionForUfc)) return false;
            float tolerance = state == PAUSED ? 0.1F : timeDriftDetectThreshold;
            if (Mathf.Abs(activeHandler.Time - SharedTargetTimeForUfc) > tolerance) return false;
            var video = VideoTransportForUfc(activeHandler);
            return Utilities.IsValid(video) && video.RefreshLocalTransportPictureForUfc();
        }

        public bool FailLocalTransportForUfc(int generation) {
            if (generation != localTransportGenerationForUfc || (!HasLocalTransportIdentityForUfc() &&
                !HasLocalTransportPresentationForUfc())) return false;
            // Native Ready/transport handback may precede the last output acknowledgement.
            // Reacquire only private failure authority before Stop can emit callbacks.
            RetainLocalTransportFailureAuthorityForUfc();
            isError = true;
            isLoading = false;
            localTransportReadyForUfc = false;
            var video = VideoTransportForUfc(activeHandler);
            if (Utilities.IsValid(video)) video.AbortLocalTransportLoadForUfc(generation);
            else activeHandler.Pause();
            return true;
        }

        void RetainLocalTransportFailureAuthorityForUfc() {
            localTransportActiveForUfc = localTransportRouteManagedForUfc = true;
            localTransportInitialOpeningForUfc = false;
            isLocalReloading = true;
        }

        /// <summary>Final output acknowledgement; no decoder command or room-clock write.</summary>
        public bool AcknowledgeLocalTransportPresentationForUfc(int generation) {
            if (generation != localTransportGenerationForUfc || localTransportActiveForUfc ||
                !HasLocalTransportPresentationForUfc() || !activeHandler.IsReady || isError ||
                (state != PLAYING && state != PAUSED)) return false;
            float raw = activeHandler.Time, target = SharedTargetTimeForUfc;
            if (float.IsNaN(raw) || float.IsInfinity(raw) || float.IsNaN(target) || float.IsInfinity(target) ||
                (state == PAUSED && (!activeHandler.IsPaused || activeHandler.IsPlaying)) ||
                (state == PLAYING && !activeHandler.IsPlaying) ||
                Mathf.Abs(raw - target) > LocalTransportPictureToleranceForUfc) return false;
            localTransportPresentationPendingForUfc = false;
            return true;
        }

        VideoPlayerHandler VideoTransportForUfc(AbstractMediaPlayerHandler handler) {
            if (!Utilities.IsValid(handler)) return null;
            var video = handler.GetComponent<VideoPlayerHandler>();
            return video == handler ? video : null;
        }

        /// <summary>Optional authored preference; no load, owner transfer or network write.</summary>
        public bool SetPreferredLocalTransportUrlForUfc(VRCUrl canonicalUrl, VRCUrl physicalUrl) {
            if (!synced || VRCUrl.IsNullOrEmpty(canonicalUrl)) return false;
            bool found = false;
            foreach (var handler in playerHandlers) {
                var video = VideoTransportForUfc(handler);
                if (!Utilities.IsValid(video)) continue;
                video.SetPreferredTransportUrlForUfc(canonicalUrl, physicalUrl);
                found = true;
            }
            return found;
        }

        /// <summary>Atomically select a local physical URL for the current canonical film.</summary>
        public int RequestLocalTransportUrlForUfc(VRCUrl canonicalUrl, VRCUrl physicalUrl) {
            localTransportRequestAcceptedForUfc = false;
            var video = VideoTransportForUfc(activeHandler);
            if (!synced || !Utilities.IsValid(video) || VRCUrl.IsNullOrEmpty(localUrl) ||
                VRCUrl.IsNullOrEmpty(canonicalUrl) || !canonicalUrl.Equals(localUrl) || state == IDLE) return 0;
            VRCUrl wanted = VRCUrl.IsNullOrEmpty(physicalUrl) ? canonicalUrl : physicalUrl;
            if (video.IsSupported(wanted.Get()) < 0) return 0;
            SetPreferredLocalTransportUrlForUfc(canonicalUrl, physicalUrl);
            // Keep an already issued, unprepared load of this exact source. Off must
            // not stop and reopen the primary URL while its first Ready is pending.
            // Prepared/aligned and queued replacements retain their existing paths.
            if (!isError && isLoading && localTransportActiveForUfc && localTransportRouteManagedForUfc &&
                HasLocalTransportIdentityForUfc() && !localTransportReadyForUfc &&
                localTransportAlignedRevisionForUfc == -1 && !localTransportSeekPendingForUfc &&
                video.CanContinueLocalTransportLoadForUfc(localTransportGenerationForUfc, canonicalUrl, wanted)) {
                localTransportRequestAcceptedForUfc = true;
                return localTransportGenerationForUfc;
            }
            if (!localTransportActiveForUfc && !isLoading && !isError && video.IsReady &&
                !VRCUrl.IsNullOrEmpty(video.LoadedPhysicalUrlForUfc) && video.LoadedPhysicalUrlForUfc.Equals(wanted)) {
                localTransportRequestAcceptedForUfc = true;
                // Prepared does not imply that the current presentation has been accepted.
                return localTransportPresentationPendingForUfc ? localTransportGenerationForUfc : 0;
            }
            int generation = ReloadManagedTransportForUfc(video);
            localTransportRequestAcceptedForUfc = generation > 0;
            return generation;
        }

        public int RestoreCanonicalTransportForUfc() => RequestLocalTransportUrlForUfc(localUrl, null);

        bool BeginInitialTransportForUfc() {
            if (!synced || state != LOADING || !Utilities.IsValid(activeHandler) || VRCUrl.IsNullOrEmpty(localUrl)) return false;
            localTransportCanonicalForUfc = localUrl;
            localTransportHandlerForUfc = activeHandler;
            localTransportDurationForUfc = 0;
            localTransportGenerationForUfc++;
            localTransportRevisionForUfc = 0;
            localTransportAlignedRevisionForUfc = -1;
            localTransportReadyForUfc = false;
            localTransportActiveForUfc = true;
            localTransportPresentationPendingForUfc = HasLocalTransportPresenterForUfc(localUrl);
            localTransportSeekPendingForUfc = false;
            localTransportInitialOpeningForUfc = true;
            isLocalReloading = true;
            isLoading = true;
            RememberLocalTransportIntentForUfc();
            return true;
        }

        int ReloadManagedTransportForUfc(VideoPlayerHandler video) {
            if (!localTransportActiveForUfc) {
                if (state == LOADING) { if (!BeginInitialTransportForUfc()) return 0; }
                else if (BeginLocalTransportForUfc() == 0) return 0;
            } else {
                if (!HasLocalTransportIdentityForUfc()) return 0;
                localTransportGenerationForUfc++;
                localTransportReadyForUfc = false;
                localTransportAlignedRevisionForUfc = -1;
            }
            localTransportRouteManagedForUfc = true;
            localTransportPresentationPendingForUfc = HasLocalTransportPresenterForUfc(localUrl);
            isLocalReloading = true;
            isLoading = true;
            isError = false;
            loadingUrl = localUrl;
            video.PrepareLocalTransportLoadForUfc(localTransportGenerationForUfc);
            NotifyLocalTransportPresenterForUfc("_OnLocalTransportBeginForUfc");
            video.LoadUrl(localUrl, true);
            return localTransportGenerationForUfc;
        }

        bool IsMappedTransportForUfc() {
            var video = VideoTransportForUfc(activeHandler);
            return Utilities.IsValid(video) && video.HasLocalTransportUrlForUfc(localUrl);
        }

        void PrepareMappedInitialLoadForUfc() {
            if (!IsMappedTransportForUfc() && !WatchesLocalTransportLoadForUfc(localUrl)) return;
            state = LOADING;
            if (!BeginInitialTransportForUfc()) return;
            localTransportRouteManagedForUfc = true;
            var video = (VideoPlayerHandler)activeHandler;
            if (video.CanReuseLocalTransportForUfc(localUrl)) video.AdoptLocalTransportLoadForUfc(localTransportGenerationForUfc);
            else video.PrepareLocalTransportLoadForUfc(localTransportGenerationForUfc);
            NotifyLocalTransportPresenterForUfc("_OnLocalTransportBeginForUfc");
        }

        void PrepareMappedReceivedLoadForUfc(VRCUrl canonicalUrl) {
            var video = VideoTransportForUfc(activeHandler);
            if (!Utilities.IsValid(video) || (!video.HasLocalTransportUrlForUfc(canonicalUrl) && !WatchesLocalTransportLoadForUfc(canonicalUrl))) return;
            localUrl = canonicalUrl;
            if (state == LOADING) { if (!BeginInitialTransportForUfc()) return; }
            else if (BeginLocalTransportForUfc() == 0) return;
            localTransportDurationForUfc = 0;
            localTransportRouteManagedForUfc = true;
            video.PrepareLocalTransportLoadForUfc(localTransportGenerationForUfc);
            NotifyLocalTransportPresenterForUfc("_OnLocalTransportBeginForUfc");
        }

        bool FinishMappedInitialReadyForUfc() {
            if (!localTransportRouteManagedForUfc || !localTransportInitialOpeningForUfc ||
                state != LOADING || !Networking.IsOwner(gameObject)) return false;
            ((VideoPlayerHandler)activeHandler).FinishLocalTransportLoadForUfc(localTransportGenerationForUfc);
            float duration = activeHandler.Duration;
            if (duration > 0 && !float.IsInfinity(duration)) localTransportDurationForUfc = duration;
            localTransportActiveForUfc = localTransportInitialOpeningForUfc = localTransportRouteManagedForUfc = false;
            isLocalReloading = false;
            return true; // Stock first-ready establishes the room clock once, from the chosen source.
        }

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
            // The room already owns this anchor. A personal source choice must not recapture raw time.
            if (state != PLAYING && state != PAUSED) return 0;
            localTransportCanonicalForUfc = localUrl;
            localTransportHandlerForUfc = activeHandler;
            localTransportDurationForUfc = Mathf.Max(0, duration);
            localTransportGenerationForUfc++;
            localTransportRevisionForUfc = 0;
            localTransportAlignedRevisionForUfc = -1;
            localTransportReadyForUfc = false;
            localTransportActiveForUfc = true;
            localTransportPresentationPendingForUfc = HasLocalTransportPresenterForUfc(localUrl);
            localTransportSeekPendingForUfc = false;
            localTransportInitialOpeningForUfc = false;
            isLocalReloading = true;
            isLoading = true;
            RememberLocalTransportIntentForUfc();
            return localTransportGenerationForUfc;
        }

        bool HasLocalTransportMediaIdentityForUfc() =>
            Utilities.IsValid(activeHandler) && activeHandler == localTransportHandlerForUfc &&
            !VRCUrl.IsNullOrEmpty(localUrl) && localUrl.Equals(localTransportCanonicalForUfc);

        bool HasLocalTransportIdentityForUfc() => localTransportActiveForUfc && HasLocalTransportMediaIdentityForUfc();

        bool HasLocalTransportPresentationForUfc() => localTransportPresentationPendingForUfc &&
            HasLocalTransportMediaIdentityForUfc() && HasLocalTransportPresenterForUfc(localUrl);

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
        /// Initial Core-owned alignment per accepted intent revision. The existing cadence
        /// may settle remaining Playing drift after this seek has been acknowledged.
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
            float target = SharedTargetTimeForUfc;
            ArmLocalTransportSeekForUfc(target);
            Time = target;
            if (state == PLAYING && !activeHandler.IsPlaying) activeHandler.Play();
            ResumeLocalTransportSyncForUfc(generation);
            return true;
        }

        /// <summary>Release after latest target and caller's picture gate; stale tokens cannot release.</summary>
        public bool CompleteLocalTransportForUfc(int generation) {
            if (generation != localTransportGenerationForUfc || !HasLocalTransportIdentityForUfc() ||
                !localTransportReadyForUfc || !activeHandler.IsReady ||
                localTransportSeekPendingForUfc ||
                localTransportAlignedRevisionForUfc != localTransportRevisionForUfc) return false;
            float tolerance = state == PAUSED ? 0.1F : timeDriftDetectThreshold;
            if ((state == PAUSED && !activeHandler.IsPaused) ||
                (state == PLAYING && !activeHandler.IsPlaying) ||
                Mathf.Abs(activeHandler.Time - SharedTargetTimeForUfc) > tolerance) return false;
            localTransportActiveForUfc = false;
            if (localTransportRouteManagedForUfc) {
                var video = VideoTransportForUfc(activeHandler);
                if (Utilities.IsValid(video)) video.FinishLocalTransportLoadForUfc(generation);
            }
            localTransportRouteManagedForUfc = localTransportInitialOpeningForUfc = false;
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
            if (!localTransportActiveForUfc && !localTransportPresentationPendingForUfc) return;
            var video = VideoTransportForUfc(localTransportHandlerForUfc);
            if ((localTransportRouteManagedForUfc || localTransportPresentationPendingForUfc) && Utilities.IsValid(video)) video.CancelLocalTransportLoadForUfc(localTransportGenerationForUfc);
            localTransportGenerationForUfc++;
            localTransportActiveForUfc = false;
            localTransportPresentationPendingForUfc = false;
            localTransportReadyForUfc = false;
            localTransportSeekPendingForUfc = false;
            localTransportRouteManagedForUfc = localTransportInitialOpeningForUfc = false;
            localTransportAlignedRevisionForUfc = -1;
            isLocalReloading = false;
            if (Utilities.IsValid(localTransportPresenterForUfc) && localTransportPresenterForUfc.enabled &&
                localTransportPresenterForUfc.gameObject.activeInHierarchy)
                localTransportPresenterForUfc.SendCustomEvent("_OnLocalTransportCancelledForUfc");
        }

        void InvalidateLocalTransportAuthorityForUfc() {
            if (!localTransportActiveForUfc && !localTransportPresentationPendingForUfc) return;
            int oldGeneration = localTransportGenerationForUfc;
            bool prepared = (localTransportReadyForUfc || localTransportPresentationPendingForUfc) && Utilities.IsValid(activeHandler) && activeHandler.IsReady && !isError;
            // Keep publication protected during a handoff; invalidate the old caller's write token.
            localTransportGenerationForUfc++;
            localTransportAlignedRevisionForUfc = -1;
            localTransportReadyForUfc = prepared;
            var video = VideoTransportForUfc(localTransportHandlerForUfc);
            if ((localTransportRouteManagedForUfc || localTransportPresentationPendingForUfc) && Utilities.IsValid(video)) video.RetagLocalTransportLoadForUfc(oldGeneration, localTransportGenerationForUfc);
            NotifyLocalTransportPresenterForUfc("_OnLocalTransportAuthorityChangedForUfc");
        }
    }
}
