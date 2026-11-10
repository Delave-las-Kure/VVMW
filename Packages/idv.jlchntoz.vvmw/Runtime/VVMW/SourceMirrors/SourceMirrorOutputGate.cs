using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace JLChnToZ.VRC.VVMW {
    // A dedicated VizVid output gain, never AudioSource.enabled/mute or Core.Volume.
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class SourceMirrorOutputGate : AbstractAudioController {
        public bool Held => OutputHoldOwnerForUfc == this;
        public bool IsWired => Utilities.IsValid(audioSource) &&
            Utilities.IsValid(core) && enabled && gameObject.activeInHierarchy;
        public AudioSource Output => audioSource;
        public bool IsWiredFor(Core expectedCore) => IsWired && core == expectedCore &&
            core.OwnsExclusiveAudioOutputForUfc(this, audioSource) && CanHoldOutputForUfc(this);
        public void Hold() {
            if (!IsWired || Held) return;
            HoldOutputForUfc(this);
        }
        public void Release() {
            if (!Held) return;
            ReleaseOutputForUfc(this);
        }
    }
}
