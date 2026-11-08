using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common.Enums;

namespace JLChnToZ.VRC.VVMW {
    // Optional, finite transition coordinator. Core owns every playback/seek command.
    // No extra timer loop, alternate clock, predictive lead or secondary decoder.
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public partial class SourceMirrorController : UdonSharpBehaviour {
        public Core core;
        public VRCUrl canonical, requestedPhysical;
        public Renderer[] videoOnlyRenderers;
        public SourceMirrorOutputGate[] audioGates;
        public bool requestAccepted, pending, failed, picturePresented, mirrorsOff;
        public string diagnostic;
        public float transitionTimeout = 25f;
        public bool observeCoreLoads;
        public int generation, acceptedRevision, alignments, tickCount;
        bool tickQueued, held, disabling;
        bool[] rendererWasEnabled;
        float startedAt, previousRaw;
        int stableSamples, phase;

        public bool CanDetach => mirrorsOff && !held && !pending &&
            Utilities.IsValid(core) && !core.IsLocalTransportActiveForUfc && !core.IsLocalTransportPresentationPendingForUfc;

        bool Configured() {
            if (!Utilities.IsValid(core) || VRCUrl.IsNullOrEmpty(canonical) ||
                videoOnlyRenderers == null || videoOnlyRenderers.Length == 0 ||
                audioGates == null || audioGates.Length == 0) return false;
            for (int i=0;i<videoOnlyRenderers.Length;i++) {
                if (!Utilities.IsValid(videoOnlyRenderers[i])) return false;
                for (int j=0;j<i;j++) if (videoOnlyRenderers[i] == videoOnlyRenderers[j]) return false;
            }
            for (int i=0;i<audioGates.Length;i++) {
                if (!Utilities.IsValid(audioGates[i]) || !audioGates[i].IsWiredFor(core)) return false;
                for (int j=0;j<i;j++) if (audioGates[i].Output == audioGates[j].Output) return false;
            }
            return true;
        }
        bool SameFilm() => !VRCUrl.IsNullOrEmpty(core.Url) && core.Url.Equals(canonical);

        void Start() { RegisterCatalogObserver(); if (observeCoreLoads) Attach(); }
        void OnEnable() {
            RegisterCatalogObserver();
            if (pending) Queue();
            if (observeCoreLoads) Attach();
        }
        bool Register() => core.RegisterLocalTransportPresenterForUfc(this,canonical,observeCoreLoads);
        void Attach() {
            if (!enabled || mirrorsOff || !Configured()) return;
            if (!Register()) { diagnostic="Another presenter owns this transition";return; }
            if (SameFilm() && !(pending && core.IsLocalTransportActiveForUfc)) core.AdoptLocalTransportForPresenterForUfc();
        }
        public void _WatchCoreLoads() { observeCoreLoads=true; disabling=false; mirrorsOff=false; enabled=true; Attach(); }
        void DetachUnlessWatching() {
            if (!observeCoreLoads || mirrorsOff) core.RegisterLocalTransportPresenterForUfc(null);
        }
        public void _OnLocalTransportBeginForUfc() {
            if (!enabled || !Configured() || !SameFilm() || !core.IsLocalTransportActiveForUfc) return;
            Hold();generation=core.LocalTransportGenerationForUfc;
            acceptedRevision=-1;stableSamples=phase=0;previousRaw=-1;alignments=0;
            pending=true;failed=false;startedAt=Time.realtimeSinceStartup;
            AutoObserveBegin();
            diagnostic="Synchronizing current room source";Queue();
        }
        public void _OnLocalTransportAuthorityChangedForUfc() {
            if (!pending || failed || !enabled) return;
            Hold();generation=core.LocalTransportGenerationForUfc;
            acceptedRevision=-1;stableSamples=phase=0;previousRaw=-1;Queue();
        }

        void Hold() {
            if (!held) {
                rendererWasEnabled = new bool[videoOnlyRenderers.Length];
                for (int i=0;i<videoOnlyRenderers.Length;i++) rendererWasEnabled[i]=videoOnlyRenderers[i].enabled;
                for (int i=0;i<audioGates.Length;i++) audioGates[i].Hold();
                held=true;
            }
            picturePresented=false;
            for (int i=0;i<videoOnlyRenderers.Length;i++) videoOnlyRenderers[i].enabled=false;
        }
        void Show() {
            for (int i=0;i<videoOnlyRenderers.Length;i++) videoOnlyRenderers[i].enabled=rendererWasEnabled[i];
            picturePresented=true;
        }
        void Release() {
            if (!held) return;
            Show();
            for (int i=0;i<audioGates.Length;i++) audioGates[i].Release();
            held=false;
        }
        void Queue() {
            if (tickQueued || !pending || !enabled || !gameObject.activeInHierarchy) return;
            tickQueued=true;
            SendCustomEventDelayedFrames(nameof(_Tick),1,EventTiming.LateUpdate);
        }

        public void _SelectMirror() { disabling=false; Request(requestedPhysical); if(requestAccepted)StopAuto(); }
        public void _TurnOff() {
            StopAuto();
            requestAccepted=false;
            if (!Configured()) { diagnostic="Presentation wiring incomplete"; return; }
            if (!SameFilm() || core.SharedStateForUfc==0) {
                core.SetPreferredLocalTransportUrlForUfc(canonical,null);
                core.RegisterLocalTransportPresenterForUfc(null);
                disabling=false;pending=failed=false;mirrorsOff=true;Release();enabled=false;requestAccepted=true;
                return;
            }
            disabling=true;
            Request(null);
        }
        public void _TurnOn() { disabling=false; mirrorsOff=false; enabled=true; if(observeCoreLoads)Attach(); }

        void Request(VRCUrl physical) {
            requestAccepted=false;
            if (!enabled || !Configured() || !SameFilm()) { diagnostic="No matching current film or presentation wiring"; return; }
            if (!Register()) { diagnostic="Another presenter owns this transition";return; }
            Hold();
            generation=core.RequestLocalTransportUrlForUfc(canonical,physical);
            if (!core.LocalTransportRequestAcceptedForUfc) {
                if (!pending) { Release();DetachUnlessWatching(); }
                diagnostic="Local source request rejected";return;
            }
            requestAccepted=true;failed=false;mirrorsOff=false;
            if (generation==0) { pending=false;Release();Finish();return; }
            startedAt=Time.realtimeSinceStartup;
            acceptedRevision=-1;stableSamples=phase=0;previousRaw=-1;alignments=0;
            pending=true;diagnostic="Loading selected source";Queue();
        }
        bool InWindow() {
            bool prepared=core.IsLocalTransportActiveForUfc ? core.IsLocalTransportReadyForUfc : core.IsReady;
            if (!prepared || core.HasLocalTransportErrorForUfc) return false;
            float raw=core.Time, target=core.SharedTargetTimeForUfc;
            if (float.IsNaN(raw) || float.IsInfinity(raw)) return false;
            float tolerance=core.LocalTransportPictureToleranceForUfc;
            return Mathf.Abs(raw-target)<=tolerance &&
                (core.SharedStateForUfc==3 ? core.IsPaused && !core.IsPlaying : core.IsPlaying);
        }
        void Fail(string reason) {
            failed=true;pending=false;diagnostic=reason;Hold();
            core.FailLocalTransportForUfc(generation);
            AutoAfterFailure();
        }
        void Finish() {
            pending=false;failed=false;diagnostic="Ready";
            AutoAfterReady();
            if (disabling) {
                disabling=false;mirrorsOff=true;core.RegisterLocalTransportPresenterForUfc(null);enabled=false;
            }
        }
        public void _OnLocalTransportCancelledForUfc() {
            if (disabling) { Release();Finish();diagnostic="Mirrors off";return; }
            if (!held || !enabled || !gameObject.activeInHierarchy) return;
            pending=true;failed=false;phase=0;Queue();
        }
        public void _Tick() {
            tickQueued=false;
            if (!pending || !enabled || !gameObject.activeInHierarchy) return;
            if (AutoAdvanceTick()) return;
            tickCount++;
            if (!SameFilm() || (!core.IsLocalTransportActiveForUfc && !core.IsLocalTransportPresentationPendingForUfc && !core.IsLoading && !core.IsReady)) {
                if (disabling) { Release();Finish();diagnostic="Mirrors off";return; }
                pending=failed=false;Release();DetachUnlessWatching();diagnostic="Transition cancelled";return;
            }
            if (core.LocalTransportGenerationForUfc!=generation) {
                generation=core.LocalTransportGenerationForUfc;
                acceptedRevision=-1;stableSamples=phase=0;previousRaw=-1;Hold();
            }
            if (Time.realtimeSinceStartup-startedAt>=Mathf.Clamp(transitionTimeout,2f,30f)) { Fail("Timed out waiting for the current picture; no automatic retry");return; }
            if (core.HasLocalTransportErrorForUfc) { Fail("Selected source failed; retry or turn mirrors off");return; }
            int revision=core.LocalTransportRevisionForUfc;
            if (core.IsLocalTransportActiveForUfc && revision!=acceptedRevision) {
                Hold();stableSamples=phase=0;previousRaw=-1;
                if (!core.AlignLocalTransportForUfc(generation)) {Queue();return;}
                acceptedRevision=revision;alignments++;
            }
            if (core.IsLocalTransportActiveForUfc) core.ResumeLocalTransportSyncForUfc(generation);
            if (!InWindow()) {Hold();stableSamples=phase=0;previousRaw=core.Time;Queue();return;}
            if (phase==2) {
                if (core.IsLocalTransportPresentationPendingForUfc && !core.AcknowledgeLocalTransportPresentationForUfc(generation)) {Hold();stableSamples=phase=0;Queue();return;}
                Release();Finish();return;
            }
            if (phase==1) {
                if (core.IsLocalTransportActiveForUfc && !core.CompleteLocalTransportForUfc(generation)) {Queue();return;}
                Show();phase=2;Queue();return;
            }
            float raw=core.Time;
            bool advanced=core.SharedStateForUfc==3 || (previousRaw>=0 && raw>previousRaw+.002f);
            previousRaw=raw;
            if (advanced) stableSamples++;
            if (stableSamples>=3 && core.RefreshLocalTransportPictureForUfc(generation)) phase=1;
            Queue();
        }
        void OnDisable() {
            StopAuto();
            // Direct removal during an in-flight operation is deliberately fail-closed.
            // Use _TurnOff and wait for CanDetach before disabling/removing the composition.
            if (pending || failed) diagnostic="Finish canonical handback before removing the module";
        }
    }
}
