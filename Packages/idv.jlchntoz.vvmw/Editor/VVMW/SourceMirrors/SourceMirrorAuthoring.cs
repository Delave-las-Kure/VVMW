using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;
using UdonSharp;
using UdonSharpEditor;
using VRC.SDKBase;

namespace JLChnToZ.VRC.VVMW.Editors {
    /// <summary>Explicit edit-time composition. Never guesses screens or replaces an audio controller.</summary>
    public static class SourceMirrorAuthoring {
        const string ProgramFolder = "Packages/idv.jlchntoz.vvmw/Runtime/VVMW/SourceMirrors/";

        static SerializedProperty Property(UnityEngine.Object obj, string name) => new SerializedObject(obj).FindProperty(name);
        static T[] Array<T>(UnityEngine.Object obj, string name) where T : UnityEngine.Object {
            var property = Property(obj, name);
            return Enumerable.Range(0, property.arraySize).Select(i => property.GetArrayElementAtIndex(i).objectReferenceValue as T).ToArray();
        }
        static void Array(UnityEngine.Object obj, string name, UnityEngine.Object[] values) {
            var serialized = new SerializedObject(obj);
            var property = serialized.FindProperty(name);
            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++) property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            serialized.ApplyModifiedProperties();
        }
        static void Reference(UnityEngine.Object obj, string name, UnityEngine.Object value) {
            var serialized = new SerializedObject(obj);
            serialized.FindProperty(name).objectReferenceValue = value;
            serialized.ApplyModifiedProperties();
        }
        static T[] SceneObjects<T>() where T : Component => Resources.FindObjectsOfTypeAll<T>()
            .Where(x => x && x.gameObject.scene.IsValid() && !EditorUtility.IsPersistent(x)).ToArray();
        static AudioSource Output(AbstractAudioController gate) => Property(gate, "audioSource").objectReferenceValue as AudioSource;
        static Core Owner(AbstractAudioController gate) => Property(gate, "core").objectReferenceValue as Core;
        static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static void Editable(Core core) {
            Need(!EditorApplication.isPlayingOrWillChangePlaymode, "Install/remove only in Edit Mode after a completed Off.");
            Need(core && core.gameObject.scene.IsValid() && !EditorUtility.IsPersistent(core), "Select a scene Core.");
        }

        public static void ValidatePrograms() {
            foreach (var type in new[] { typeof(SourceMirrorController), typeof(SourceMirrorOutputGate) }) {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(ProgramFolder + type.Name + ".cs");
                var program = AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(ProgramFolder + type.Name + ".asset");
                Need(script && script.GetClass() == type && program && program.sourceCsScript == script,
                    "Restore the fork-owned Udon program and stable meta files for " + type.Name);
                Need(UdonSharpProgramAsset.GetProgramAssetForClass(type) == program, "Duplicate Udon program for " + type.Name);
            }
        }

        public static void Validate(SourceMirrorController controller) {
            Need(controller && controller.core, "Source mirrors require a Core.");
            Need(!VRCUrl.IsNullOrEmpty(controller.canonical), "Source mirrors require a canonical film URL.");
            var core = controller.core;
            Need(controller.gameObject.scene == core.gameObject.scene, "Controller and Core must belong to the same scene.");
            Need(SceneObjects<SourceMirrorController>().Count(x => x.core == core) == 1, "Only one source-mirror controller may own a Core.");
            var screens = controller.videoOnlyRenderers;
            Need(screens != null && screens.Length > 0 && screens.All(x => x && x.gameObject.scene == core.gameObject.scene) &&
                screens.Distinct().Count() == screens.Length, "Choose distinct video-only renderers in the Core scene.");
            var gates = controller.audioGates;
            Need(gates != null && gates.Length > 0 && gates.All(x => x && x.enabled && x.gameObject.activeInHierarchy) &&
                gates.Distinct().Count() == gates.Length, "Choose distinct, active output gates.");
            var outputs = gates.Select(Output).ToArray();
            Need(outputs.All(x => x && x.gameObject.scene == core.gameObject.scene) && outputs.Distinct().Count() == outputs.Length,
                "Each mirror gate must own one distinct scene AudioSource.");
            foreach (var gate in gates) {
                var output = Output(gate);
                Need(Owner(gate) == core && Array<AbstractAudioController>(core, "audioControllers").Count(x => x == gate) == 1,
                    "Register each gate exactly once with the matching Core.");
                Need(Property(gate, "preserveOutputRouteForUfc").boolValue, "The gate must retain its explicit route during preprocessing.");
                foreach (var otherCore in SceneObjects<Core>()) {
                    Need(!Array<AudioSource>(otherCore, "audioSources").Contains(output), "Direct Core audio competes with a mirror gate.");
                    Need(!Array<AbstractAudioController>(otherCore, "audioControllers").Any(x => x && x != gate && Output(x) == output),
                        "Another audio controller competes with a mirror gate.");
                    Need(otherCore == core || !Array<AbstractAudioController>(otherCore, "audioControllers").Contains(gate),
                        "The gate is registered with another Core.");
                }
                Need(!SceneObjects<AbstractAudioController>().Any(x => x != gate && Output(x) == output),
                    "Another scene audio controller targets the same AudioSource.");
            }
            foreach (var other in SceneObjects<SourceMirrorController>().Where(x => x != controller)) {
                Need(other.videoOnlyRenderers == null || !other.videoOnlyRenderers.Intersect(screens).Any(), "Two controllers share a video renderer.");
                Need(other.audioGates == null || !other.audioGates.Intersect(gates).Any(), "Two controllers share an output gate.");
            }
            ValidatePrograms();
        }

        public static SourceMirrorController Install(Core core, VRCUrl canonical, Renderer[] screens, AudioSource[] outputs) {
            Editable(core);
            ValidatePrograms();
            Need(!VRCUrl.IsNullOrEmpty(canonical), "Choose the canonical film URL.");
            Need(!SceneObjects<SourceMirrorController>().Any(x => x.core == core), "This Core already has source mirrors.");
            Need(screens != null && screens.Length > 0 && screens.All(x => x && x.gameObject.scene == core.gameObject.scene) && screens.Distinct().Count() == screens.Length,
                "Choose distinct video-only renderers in the Core scene.");
            Need(outputs != null && outputs.Length > 0 && outputs.All(x => x && x.gameObject.activeInHierarchy && x.gameObject.scene == core.gameObject.scene) && outputs.Distinct().Count() == outputs.Length,
                "Choose distinct, active video AudioSources in the Core scene.");
            var direct = Array<AudioSource>(core, "audioSources");
            var prior = Array<AbstractAudioController>(core, "audioControllers");
            foreach (var output in outputs) {
                Need(direct.Count(x => x == output) == 1, "Installer accepts outputs registered directly and exactly once with this Core.");
                Need(!SceneObjects<AbstractAudioController>().Any(x => Output(x) == output), "Preserve the existing audio controller; explicit migration is required.");
                Need(!SceneObjects<Core>().Any(x => x != core && Array<AudioSource>(x, "audioSources").Contains(output)), "Another Core owns this output.");
            }
            Need(!SceneObjects<SourceMirrorController>().Any(x => x.videoOnlyRenderers != null && x.videoOnlyRenderers.Intersect(screens).Any()), "Another controller owns a selected screen.");
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Install VizVid source mirrors");
            try {
                var go = new GameObject("Source Mirrors (optional)");
                Undo.RegisterCreatedObjectUndo(go, "Create source mirrors");
                go.transform.SetParent(core.transform, false);
                var controller = go.AddUdonSharpComponent<SourceMirrorController>();
                controller.enabled = false;
                controller.mirrorsOff = true;
                controller.core = core;
                controller.canonical = canonical;
                controller.videoOnlyRenderers = screens;
                var gates = outputs.Select(output => {
                    var gate = output.gameObject.AddUdonSharpComponent<SourceMirrorOutputGate>();
                    Undo.RegisterCreatedObjectUndo(gate, "Create mirror output gate");
                    Undo.RegisterCreatedObjectUndo(UdonSharpEditorUtility.GetBackingUdonBehaviour(gate), "Create mirror output backing");
                    Reference(gate, "audioSource", output);
                    Reference(gate, "core", core);
                    var serialized = new SerializedObject(gate);
                    serialized.FindProperty("preserveOutputRouteForUfc").boolValue = true;
                    serialized.ApplyModifiedProperties();
                    return gate;
                }).ToArray();
                controller.audioGates = gates;
                Array(core, "audioSources", direct.Except(outputs).ToArray());
                Array(core, "audioControllers", prior.Concat(gates).ToArray());
                Validate(controller);
                foreach (var gate in gates) UdonSharpEditorUtility.CopyProxyToUdon(gate);
                UdonSharpEditorUtility.CopyProxyToUdon(controller);
                UdonSharpEditorUtility.CopyProxyToUdon(core);
                Undo.CollapseUndoOperations(group);
                return controller;
            } catch {
                Undo.RevertAllDownToGroup(group);
                throw;
            }
        }

        public static void Remove(SourceMirrorController controller) {
            Need(controller, "Select the source-mirror controller.");
            var core = controller.core;
            Editable(core);
            Validate(controller);
            Need(!controller.pending && !controller.failed && controller.mirrorsOff && !controller.enabled,
                "Save a disabled Off composition before removal; never remove a running transition.");
            var gates = controller.audioGates;
            Need(gates.All(x => !x.Held && Mathf.Approximately(x.Volume, 1)), "Output hold/custom gain must be released before returning to direct audio.");
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Remove VizVid source mirrors");
            Array(core, "audioSources", Array<AudioSource>(core, "audioSources").Concat(gates.Select(Output)).ToArray());
            Array(core, "audioControllers", Array<AbstractAudioController>(core, "audioControllers").Except(gates).ToArray());
            foreach (var gate in gates) {
                var backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(gate);
                Undo.DestroyObjectImmediate(gate);
                if (backing) Undo.DestroyObjectImmediate(backing);
            }
            var controllerBacking = UdonSharpEditorUtility.GetBackingUdonBehaviour(controller);
            Undo.DestroyObjectImmediate(controller);
            if (controllerBacking) Undo.DestroyObjectImmediate(controllerBacking);
            UdonSharpEditorUtility.CopyProxyToUdon(core);
            Undo.CollapseUndoOperations(group);
        }

        [MenuItem("Tools/VizVid/Source Mirrors/Install...")]
        static void OpenInstaller() => ScriptableWizard.DisplayWizard<SourceMirrorInstallWizard>("Install optional source mirrors", "Install");
        [MenuItem("CONTEXT/SourceMirrorController/Validate composition")]
        static void ValidateMenu(MenuCommand command) { Validate((SourceMirrorController)command.context); Debug.Log(VizVidFork.Build + ": source-mirror graph valid."); }
        [MenuItem("CONTEXT/SourceMirrorController/Remove disabled composition")]
        static void RemoveMenu(MenuCommand command) => Remove((SourceMirrorController)command.context);
    }

    sealed class SourceMirrorInstallWizard : ScriptableWizard {
        public Core core;
        public string canonicalUrl;
        public Renderer[] videoOnlyRenderers;
        public AudioSource[] directVideoAudioSources;
        void OnWizardCreate() => Selection.activeObject = SourceMirrorAuthoring.Install(core, new VRCUrl(canonicalUrl), videoOnlyRenderers, directVideoAudioSources);
    }

    sealed class SourceMirrorBuildValidator : IProcessSceneWithReport {
        public int callbackOrder => -100000;
        public void OnProcessScene(Scene scene, BuildReport report) {
            foreach (var root in scene.GetRootGameObjects())
                foreach (var controller in root.GetComponentsInChildren<SourceMirrorController>(true))
                    try { SourceMirrorAuthoring.Validate(controller); }
                    catch (Exception ex) { throw new BuildFailedException("Invalid VizVid source mirrors: " + ex.Message); }
        }
    }

    sealed class SourceMirrorConfigValidator : JLChnToZ.VRC.Foundation.Editors.IPreprocessor {
        public int Priority => 100;
        public void OnPreprocess(Scene scene) {
            foreach (var root in scene.GetRootGameObjects())
                foreach (var controller in root.GetComponentsInChildren<SourceMirrorController>(true))
                    SourceMirrorAuthoring.Validate(controller);
        }
    }
}
