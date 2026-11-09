using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UdonSharpEditor;
using VRC.SDKBase;

using static JLChnToZ.VRC.VVMW.Editors.SourceMirrorAuthoring;

namespace JLChnToZ.VRC.VVMW.Editors {
    // Retains the world's audio controller and gain input. Never adds a second AudioSource/writer.
    internal static class SourceMirrorAudioAuthoring {
        static void Record(UdonSharp.UdonSharpBehaviour behavior, string label) {
            Copy(behavior);
            var backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(behavior);
            Undo.RegisterCompleteObjectUndo(behavior, label);
            if (backing) Undo.RegisterCompleteObjectUndo(backing, label);
        }
        static void Copy(UdonSharp.UdonSharpBehaviour behavior) {
            UdonSharpEditorUtility.CopyProxyToUdon(behavior);
            // Unity Undo snapshots serialized fields, not Udon's live variable table.
            var backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(behavior);
            if (backing is ISerializationCallbackReceiver serialized) serialized.OnBeforeSerialize();
        }
        static AudioSource[] Outputs(SourceMirrorController controller) => controller.existingAudioControllers.Select(Output).ToArray();
        static AbstractAudioController[] InstalledControllers(SourceMirrorController controller) =>
            controller.audioControllersBeforeInstall.Concat(controller.existingAudioControllers
                .Where(x => !controller.audioControllersBeforeInstall.Contains(x))).ToArray();

        internal static void Validate(SourceMirrorController controller) {
            var core = controller.core;
            var routes = controller.existingAudioControllers;
            Need(controller.audioGates == null || controller.audioGates.Length == 0, "Choose direct gates or existing controllers, not both.");
            Need(controller.existingAudioRoutesCaptured && controller.audioSourcesBeforeInstall != null &&
                controller.audioControllersBeforeInstall != null && controller.preserveRoutesBeforeInstall != null &&
                controller.preserveRoutesBeforeInstall.Length == routes.Length && controller.existingAudioControllerTypes != null &&
                controller.existingAudioControllerTypes.Length == routes.Length, "Restore the explicit existing-audio installation receipt.");
            Need(routes.All(x => x && x.enabled && x.gameObject.activeInHierarchy && x.gameObject.scene == core.gameObject.scene) &&
                routes.Distinct().Count() == routes.Length, "Choose distinct active audio controllers in the Core scene.");
            var outputs = Outputs(controller);
            Need(outputs.All(x => x && x.gameObject.scene == core.gameObject.scene) && outputs.Distinct().Count() == outputs.Length,
                "Each retained controller must own one distinct AudioSource.");
            for (int i = 0; i < routes.Length; i++) {
                var route = routes[i]; var output = outputs[i];
                Need(route.GetType().FullName == controller.existingAudioControllerTypes[i] && !(route is SourceMirrorOutputGate),
                    "The explicitly qualified audio controller type changed.");
                Need(Owner(route) == core && Property(route, "preserveOutputRouteForUfc").boolValue,
                    "Retain the matching Core and explicit route through preprocessing.");
                Need(!route.IsOutputHeldForUfc || route.OutputHoldOwnerForUfc == controller, "Another local hold owns this audio controller.");
                Need(Array<AbstractAudioController>(core, "audioControllers").Count(x => x == route) == 1,
                    "Register each retained controller exactly once.");
                Need(!SceneObjects<AbstractAudioController>().Any(x => x != route && Output(x) == output),
                    "Another scene audio controller targets the retained output.");
                foreach (var otherCore in SceneObjects<Core>()) {
                    Need(!Array<AudioSource>(otherCore, "audioSources").Contains(output), "Direct audio competes with a retained controller.");
                    Need(otherCore == core || !Array<AbstractAudioController>(otherCore, "audioControllers").Contains(route),
                        "Another Core registers the retained controller.");
                }
            }
            Need(!SceneObjects<SourceMirrorController>().Any(x => x != controller && x.existingAudioControllers != null &&
                x.existingAudioControllers.Intersect(routes).Any()), "Two mirror controllers share an existing audio route.");
        }

        internal static SourceMirrorController Install(Core core, VRCUrl canonical, Renderer[] screens,
            AbstractAudioController[] routes, Type[] compatibleTypes) {
            Editable(core); ValidatePrograms();
            Need(!VRCUrl.IsNullOrEmpty(canonical), "Choose the canonical film URL.");
            Need(!SceneObjects<SourceMirrorController>().Any(x => x.core == core), "This Core already has source mirrors.");
            Need(screens != null && screens.Length > 0 && screens.All(x => x && x.gameObject.scene == core.gameObject.scene) &&
                screens.Distinct().Count() == screens.Length, "Choose distinct video-only renderers in the Core scene.");
            Need(!SceneObjects<SourceMirrorController>().Any(x => x.videoOnlyRenderers != null && x.videoOnlyRenderers.Intersect(screens).Any()),
                "Another controller owns a selected screen.");
            Need(routes != null && routes.Length > 0 && routes.All(x => x && x.enabled && x.gameObject.activeInHierarchy &&
                x.gameObject.scene == core.gameObject.scene) && routes.Distinct().Count() == routes.Length, "Choose distinct active audio controllers.");
            Need(compatibleTypes != null && compatibleTypes.Length > 0 && compatibleTypes.All(x => x != null && !x.IsAbstract &&
                typeof(AbstractAudioController).IsAssignableFrom(x) && x != typeof(SourceMirrorOutputGate)),
                "Supply an explicit allowlist of subclasses qualified to use the base volume/hold path.");
            Need(routes.All(x => compatibleTypes.Contains(x.GetType())), "Unknown audio controller; qualify its source before migration.");
            var direct = Array<AudioSource>(core, "audioSources");
            var prior = Array<AbstractAudioController>(core, "audioControllers");
            var outputs = routes.Select(Output).ToArray();
            Need(outputs.All(x => x && x.gameObject.scene == core.gameObject.scene) && outputs.Distinct().Count() == outputs.Length,
                "Each retained controller must own one distinct scene output.");
            for (int i = 0; i < routes.Length; i++) {
                var route = routes[i]; var output = outputs[i];
                Need(Owner(route) == core && !route.IsOutputHeldForUfc, "Existing controller Core/hold is unavailable.");
                Need(direct.Count(x => x == output) <= 1 && prior.Count(x => x == route) <= 1 &&
                    (direct.Contains(output) || prior.Contains(route)), "Existing route must already belong to this Core without duplicates.");
                Need(!SceneObjects<AbstractAudioController>().Any(x => x != route && Output(x) == output), "Preserve competing/unknown output controllers.");
                foreach (var otherCore in SceneObjects<Core>().Where(x => x != core))
                    Need(!Array<AudioSource>(otherCore, "audioSources").Contains(output) &&
                        !Array<AbstractAudioController>(otherCore, "audioControllers").Contains(route), "Another Core owns this audio output.");
            }
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Install source mirrors with existing audio controllers");
            try {
                Record(core, "Retain existing audio routes");
                foreach (var route in routes) Record(route, "Retain explicit audio route");
                var go = new GameObject("Source Mirrors (optional)");
                Undo.RegisterCreatedObjectUndo(go, "Create source mirrors");
                go.transform.SetParent(core.transform, false);
                var controller = go.AddUdonSharpComponent<SourceMirrorController>();
                Undo.RegisterCreatedObjectUndo(controller, "Create source mirrors controller");
                Undo.RegisterCreatedObjectUndo(UdonSharpEditorUtility.GetBackingUdonBehaviour(controller), "Create source mirrors backing");
                controller.enabled = false; controller.mirrorsOff = true;
                controller.core = core; controller.canonical = canonical; controller.videoOnlyRenderers = screens;
                controller.audioGates = new SourceMirrorOutputGate[0];
                controller.existingAudioControllers = routes.ToArray();
                controller.existingAudioControllerTypes = routes.Select(x => x.GetType().FullName).ToArray();
                controller.audioSourcesBeforeInstall = direct;
                controller.audioControllersBeforeInstall = prior;
                controller.preserveRoutesBeforeInstall = routes.Select(x => Property(x, "preserveOutputRouteForUfc").boolValue).ToArray();
                controller.existingAudioRoutesCaptured = true;
                foreach (var route in routes) {
                    var serialized = new SerializedObject(route);
                    serialized.FindProperty("preserveOutputRouteForUfc").boolValue = true;
                    serialized.ApplyModifiedProperties();
                    Copy(route);
                }
                Array(core, "audioSources", direct.Except(outputs).ToArray());
                Array(core, "audioControllers", InstalledControllers(controller));
                SourceMirrorAuthoring.Validate(controller);
                Copy(controller);
                Copy(core);
                Undo.CollapseUndoOperations(group);
                return controller;
            } catch { Undo.RevertAllDownToGroup(group); throw; }
        }

        internal static void Remove(SourceMirrorController controller) {
            var core = controller.core;
            Editable(core); SourceMirrorAuthoring.Validate(controller);
            Need(!controller.pending && !controller.failed && controller.mirrorsOff && !controller.enabled &&
                controller.existingAudioControllers.All(x => !x.IsOutputHeldForUfc), "Complete Off and release all holds before removal.");
            // Never restore an old full graph over someone else's intervening route edits.
            Need(Array<AudioSource>(core, "audioSources").SequenceEqual(controller.audioSourcesBeforeInstall.Except(Outputs(controller))) &&
                Array<AbstractAudioController>(core, "audioControllers").SequenceEqual(InstalledControllers(controller)),
                "Audio routes changed after installation; preserve them and review before removal.");
            Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Remove source mirrors and retain existing audio controllers");
            try {
                Record(core, "Restore authored audio routes");
                Array(core, "audioSources", controller.audioSourcesBeforeInstall);
                Array(core, "audioControllers", controller.audioControllersBeforeInstall);
                for (int i = 0; i < controller.existingAudioControllers.Length; i++) {
                    var route = controller.existingAudioControllers[i];
                    Record(route, "Restore authored route policy");
                    var serialized = new SerializedObject(route);
                    serialized.FindProperty("preserveOutputRouteForUfc").boolValue = controller.preserveRoutesBeforeInstall[i];
                    serialized.ApplyModifiedProperties();
                    Copy(route);
                }
                var backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(controller);
                Undo.DestroyObjectImmediate(controller);
                if (backing) Undo.DestroyObjectImmediate(backing);
                Copy(core);
                Undo.CollapseUndoOperations(group);
            } catch { Undo.RevertAllDownToGroup(group); throw; }
        }
    }
}
