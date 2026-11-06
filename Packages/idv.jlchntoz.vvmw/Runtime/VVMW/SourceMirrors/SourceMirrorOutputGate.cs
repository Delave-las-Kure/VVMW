using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace JLChnToZ.VRC.VVMW {
    // A dedicated VizVid output gain, never AudioSource.enabled/mute or Core.Volume.
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class SourceMirrorOutputGate : AbstractAudioController {
        public bool Held { get; private set; }
        float savedGain;
        public bool IsWired => Utilities.IsValid(audioSource) &&
            Utilities.IsValid(core) && enabled && gameObject.activeInHierarchy;
        public AudioSource Output => audioSource;
        public bool IsWiredFor(Core expectedCore) => IsWired && core == expectedCore &&
            core.OwnsExclusiveAudioOutputForUfc(this, audioSource);
        public void Hold() {
            if (!IsWired || Held) return;
            savedGain = Volume;
            Held = true;
            Volume = 0;
        }
        public void Release() {
            if (!Held) return;
            Held = false;
            Volume = savedGain;
        }
    }
}
