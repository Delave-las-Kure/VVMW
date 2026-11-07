using UdonSharp;
using VRC.SDKBase;

namespace JLChnToZ.VRC.VVMW {
    /// <summary>
    /// World-specific authorization for the package catalog arbiter.
    /// Querying the writer or a role revision never grants publication rights.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public abstract class SourceMirrorCatalogAccessPolicy : UdonSharpBehaviour {
        public abstract VRCPlayerApi GetAuthorityPlayer();
        public abstract bool IsAuthoritativeHere();
        public abstract int GetRoleRevision();
        public abstract bool CanPublishCatalog(VRCPlayerApi actor, int expectedRoleRevision);
    }
}
