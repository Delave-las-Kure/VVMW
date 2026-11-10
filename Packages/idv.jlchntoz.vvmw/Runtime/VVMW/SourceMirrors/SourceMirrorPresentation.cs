using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace JLChnToZ.VRC.VVMW {
    // Optional output integration. The controller remains the only picture-ready gate.
    public abstract class SourceMirrorPresentation : UdonSharpBehaviour {
        public Core core;
        [HideInInspector] public SourceMirrorController controller;

        public bool IsWiredFor(Core expectedCore, SourceMirrorController owner) =>
            enabled && gameObject.activeInHierarchy && Utilities.IsValid(core) && core == expectedCore &&
            Utilities.IsValid(owner) && controller == owner && owner.presentation == this && CanPresent();

        public abstract bool CanPresent();
        public abstract void Hold();
        public abstract void Present();
        public abstract void End();
        public abstract void Error();
    }
}
