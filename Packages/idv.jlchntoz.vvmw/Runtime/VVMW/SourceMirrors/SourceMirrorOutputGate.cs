using UdonSharp;
using UnityEngine;

namespace JLChnToZ.VRC.VVMW {
    // A dedicated VizVid output gain, never AudioSource.enabled/mute or Core.Volume.
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class SourceMirrorOutputGate : AbstractAudioController {
        public bool Held { get; private set; }
        float savedGain;
        public bool IsWired => VRC.SDKBase.Utilities.IsValid(audioSource) &&
            VRC.SDKBase.Utilities.IsValid(core) && enabled && gameObject.activeInHierarchy;
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
