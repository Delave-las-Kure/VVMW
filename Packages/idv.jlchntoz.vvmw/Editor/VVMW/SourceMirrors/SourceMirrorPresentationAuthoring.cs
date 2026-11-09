using System.Linq;
using UnityEngine;
using static JLChnToZ.VRC.VVMW.Editors.SourceMirrorAuthoring;

namespace JLChnToZ.VRC.VVMW.Editors {
    internal static class SourceMirrorPresentationAuthoring {
        internal static void Choice(Core core, Renderer[] screens, SourceMirrorPresentation presentation, bool unbound) {
            if (!presentation) {
                Need(screens != null && screens.Length > 0 && screens.All(x => x && x.gameObject.scene == core.gameObject.scene) &&
                    screens.Distinct().Count() == screens.Length, "Choose distinct video-only renderers in the Core scene.");
                return;
            }
            Need(screens == null || screens.Length == 0, "Choose video-only renderers or a presentation sink, not both.");
            Need(presentation.enabled && presentation.gameObject.activeInHierarchy && presentation.gameObject.scene == core.gameObject.scene &&
                presentation.core == core && presentation.CanPresent(), "Choose a configured active presentation sink in the Core scene.");
            if (unbound) Need(!presentation.controller && !SceneObjects<SourceMirrorController>().Any(x => x.presentation == presentation),
                "Another controller owns the presentation sink.");
        }
        internal static void Validate(SourceMirrorController controller) {
            Choice(controller.core,controller.videoOnlyRenderers,controller.presentation,false);
            if (!controller.presentation) return;
            Need(controller.presentation.controller == controller && controller.presentation.IsWiredFor(controller.core,controller),
                "Presentation sink back-reference differs from its controller.");
            Need(SceneObjects<SourceMirrorController>().Count(x => x.presentation == controller.presentation) == 1,
                "Only one controller may own the presentation sink.");
        }
        internal static void Bind(SourceMirrorController controller, SourceMirrorPresentation presentation) {
            if (!presentation) return;
            SourceMirrorAudioAuthoring.Record(presentation,"Bind source mirror presentation");
            presentation.controller=controller; controller.presentation=presentation;
            SourceMirrorAudioAuthoring.Copy(presentation);
        }
        internal static void Unbind(SourceMirrorController controller) {
            if (!controller.presentation) return;
            Need(!controller.IsHoldingPresentation,"Finish the picture handoff before removing its presentation sink.");
            var presentation=controller.presentation;
            SourceMirrorAudioAuthoring.Record(presentation,"Unbind source mirror presentation");
            presentation.controller=null;
            SourceMirrorAudioAuthoring.Copy(presentation);
        }
    }
}
