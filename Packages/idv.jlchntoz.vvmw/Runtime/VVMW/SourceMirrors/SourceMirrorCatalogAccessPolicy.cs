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
        // Read-only UI capability on the requesting client. Existing policies
        // fail closed until they explicitly provide this local query. This
        // never replaces the arbiter-side CanPublishCatalog authorization.
        public virtual bool CanEditCatalogLocally(VRCPlayerApi actor, int expectedRoleRevision) => false;
        public abstract bool CanPublishCatalog(VRCPlayerApi actor, int expectedRoleRevision);
    }
}
