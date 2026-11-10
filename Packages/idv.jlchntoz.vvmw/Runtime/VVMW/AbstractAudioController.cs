using UnityEngine;
using UdonSharp;
using VRC.SDKBase;
using JLChnToZ.VRC.Foundation;

namespace JLChnToZ.VRC.VVMW {
    [RequireComponent(typeof(AudioSource))]
    public abstract class AbstractAudioController : UdonSharpBehaviour {
        [SerializeField, Resolve("."), HideInInspector] protected AudioSource audioSource;
        [SerializeField, HideInInspector] internal protected Core core;
        [SerializeField, Range(0, 1), FieldChangeCallback(nameof(Volume))] protected float volume = 1;
        float internalVolume = 1;
        UdonSharpBehaviour outputHoldOwnerForUfc;
        [SerializeField, HideInInspector] internal bool preserveOutputRouteForUfc;

        public AudioSource OutputAudioSourceForUfc => audioSource;
        public Core AudioOutputCoreForUfc => core;
        public UdonSharpBehaviour OutputHoldOwnerForUfc => outputHoldOwnerForUfc;
        public bool IsOutputHeldForUfc => Utilities.IsValid(outputHoldOwnerForUfc);

        // One local hold owner; independent of Volume, Core fades/mute and subclasses' gain.
        public bool CanHoldOutputForUfc(UdonSharpBehaviour owner) => Utilities.IsValid(owner) &&
            (!Utilities.IsValid(outputHoldOwnerForUfc) || outputHoldOwnerForUfc == owner);

        public bool HoldOutputForUfc(UdonSharpBehaviour owner) {
            if (!CanHoldOutputForUfc(owner)) return false;
            outputHoldOwnerForUfc = owner;
            UpdateVolume();
            return true;
        }

        public bool ReleaseOutputForUfc(UdonSharpBehaviour owner) {
            if (!Utilities.IsValid(owner)) return false;
            if (Utilities.IsValid(outputHoldOwnerForUfc) && outputHoldOwnerForUfc != owner) return false;
            outputHoldOwnerForUfc = null;
            UpdateVolume();
            return true;
        }

        public float Volume {
            get => volume;
            set {
                volume = Mathf.Clamp01(value);
                UpdateVolume();
            }
        }

        protected virtual void OnEnable() {
            internalVolume = core.Volume;
            internalVolume *= internalVolume;
            UpdateVolume();
        }

#if COMPILER_UDONSHARP
        public
#else
        internal
#endif
        void InternalSetVolume(float value) {
            internalVolume = value;
            UpdateVolume();
        }

#if COMPILER_UDONSHARP
        public
#else
        internal
#endif
        void InternalSetPitch(float value) => audioSource.pitch = value;

        void UpdateVolume() => audioSource.volume = IsOutputHeldForUfc ? 0 : internalVolume * volume;
    }
}
