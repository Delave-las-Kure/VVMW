using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using UdonSharp;
using UdonSharpEditor;
using JLChnToZ.VRC.Foundation.I18N;

namespace JLChnToZ.VRC.VVMW.Editors {
    /// <summary>One optional view factory; the caller supplies native VizVid theme templates and placement.</summary>
    public static class SourceMirrorUIAuthoring {
        const string ProgramPath = "Packages/idv.jlchntoz.vvmw/Runtime/VVMW/SourceMirrors/SourceMirrorUI.asset";
        public const float Width = 560, TargetHeight = 52, SourceHeight = 60, Gap = 8;

        static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        internal static RectTransform Rect(string name, Transform parent) {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false); rect.gameObject.layer = parent.gameObject.layer; return rect;
        }
        internal static void Size(RectTransform rect, float width, float height) {
            rect.sizeDelta = new Vector2(width, height);
            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.minHeight = element.preferredHeight = height;
            element.flexibleHeight = 0;
        }
        internal static Image Surface(RectTransform rect, Image template) {
            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = template.sprite; image.type = template.type;
            image.material = template.material; image.color = template.color;
            image.pixelsPerUnitMultiplier = template.pixelsPerUnitMultiplier;
            return image;
        }
        internal static Graphic Text(string name, Transform parent, Graphic template, float height, float size, string value) {
            var rect = Rect(name, parent); Size(rect, Width - 48, height);
            var tmpTemplate = template as TextMeshProUGUI;
            if (tmpTemplate) {
                var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
                text.font = tmpTemplate.font; text.fontSharedMaterial = tmpTemplate.fontSharedMaterial;
                text.color = template.color; text.fontStyle = tmpTemplate.fontStyle;
                text.fontSize = size; text.enableAutoSizing = false;
                text.richText = false; text.raycastTarget = false;
                text.alignment = TextAlignmentOptions.TopLeft;
                text.enableWordWrapping = true; text.overflowMode = TextOverflowModes.Ellipsis;
                text.text = value; return text;
            }
            var legacyTemplate = template as UnityEngine.UI.Text;
            Need(legacyTemplate && legacyTemplate.font, "Choose a Text or TMP font template.");
            var legacy = rect.gameObject.AddComponent<UnityEngine.UI.Text>();
            legacy.font = legacyTemplate.font; legacy.fontStyle = legacyTemplate.fontStyle;
            legacy.material = legacyTemplate.material; legacy.color = template.color;
            legacy.fontSize = Mathf.RoundToInt(size); legacy.resizeTextForBestFit = false;
            legacy.supportRichText = false; legacy.raycastTarget = false;
            legacy.alignment = TextAnchor.UpperLeft; legacy.horizontalOverflow = HorizontalWrapMode.Wrap;
            legacy.verticalOverflow = VerticalWrapMode.Truncate; legacy.text = value; return legacy;
        }
        internal static Button Button(string name, Transform parent, Button template, Graphic textTemplate,
            float height, string caption, out Graphic text, out GameObject selected) {
            var rect = Rect(name, parent); Size(rect, Width - 48, height);
            var image = Surface(rect, template.targetGraphic as Image);
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image; button.colors = template.colors;
            button.transition = Selectable.Transition.ColorTint;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            text = Text("Caption", rect, textTemplate, height, 18, caption);
            var label = text.rectTransform;
            label.anchorMin = Vector2.zero; label.anchorMax = Vector2.one;
            label.offsetMin = new Vector2(20, 0); label.offsetMax = new Vector2(-20, 0);
            if (text is TextMeshProUGUI tmp) tmp.alignment = TextAlignmentOptions.MidlineLeft;
            if (text is UnityEngine.UI.Text legacy) legacy.alignment = TextAnchor.MiddleLeft;
            // Tint the caption with the native Selectable states; leaving text
            // outside the target would make unavailable actions look enabled.
            button.targetGraphic = text;
            button.enabled = false; button.enabled = true;
            var mark = Rect("Selected", rect);
            mark.anchorMin = new Vector2(0, 0); mark.anchorMax = new Vector2(0, 1);
            mark.pivot = new Vector2(0, 0.5F); mark.sizeDelta = new Vector2(4, -16);
            mark.anchoredPosition = Vector2.zero;
            var marker = mark.gameObject.AddComponent<Image>();
            marker.color = template.colors.pressedColor; marker.raycastTarget = false;
            selected = mark.gameObject; selected.SetActive(false);
            return button;
        }
        static void Wire(Button button, SourceMirrorUI view, string click, string press = null) {
            var backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(view);
            Need(backing, "The source UI needs its Udon backing before wiring.");
            button.onClick = new Button.ButtonClickedEvent();
            UnityEventTools.AddStringPersistentListener(button.onClick, backing.SendCustomEvent, click);
            if (string.IsNullOrEmpty(press)) return;
            var trigger = button.gameObject.AddComponent<EventTrigger>();
            var down = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
            UnityEventTools.AddStringPersistentListener(down.callback, backing.SendCustomEvent, press);
            trigger.triggers.Add(down);
        }

        public static SourceMirrorUI Create(RectTransform parent, SourceMirrorController mirrors, UIHandler controls) {
            Need(controls && mirrors && controls.gameObject.scene == mirrors.gameObject.scene,
                "Choose the existing controls in the mirror Core scene.");
            var serialized = new SerializedObject(controls);
            Need(serialized.FindProperty("core").objectReferenceValue == mirrors.core,
                "The selected controls must reference the same Core.");
            var status = serialized.FindProperty("statusObject").objectReferenceValue as GameObject;
            Graphic text = status ? status.GetComponent<TextMeshProUGUI>() : null;
            if (!text && status) text = status.GetComponent<UnityEngine.UI.Text>();
            return Create(parent, mirrors, serialized.FindProperty("languageManager").objectReferenceValue as LanguageManager,
                serialized.FindProperty("reloadButton").objectReferenceValue as Button, text);
        }

        public static SourceMirrorUI Create(RectTransform parent, SourceMirrorController mirrors,
            LanguageManager language, Button buttonTemplate, Graphic textTemplate) {
            Need(!EditorApplication.isPlayingOrWillChangePlaymode, "Compose source controls in Edit Mode.");
            Need(parent && parent.GetComponentInParent<Canvas>(), "Choose a parent in the existing Canvas.");
            var canvas = parent.GetComponentInParent<Canvas>();
            Need(canvas.renderMode == RenderMode.WorldSpace && canvas.gameObject.layer != 5,
                "Use a world-space Canvas on Default or another world-interaction layer, not reserved UI layer5.");
            Need(mirrors && mirrors.core && parent.gameObject.scene == mirrors.gameObject.scene,
                "The source controls and mirror controller must share the Core scene.");
            Need(buttonTemplate && buttonTemplate.targetGraphic is Image &&
                ((textTemplate is TextMeshProUGUI tmpTemplate && tmpTemplate.font) ||
                (textTemplate is UnityEngine.UI.Text legacyTemplate && legacyTemplate.font)),
                "Supply an existing VizVid image-button and text theme template.");
            var program = AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(ProgramPath);
            Need(program && program.sourceCsScript && program.sourceCsScript.GetClass() == typeof(SourceMirrorUI),
                "Restore the fork-owned SourceMirrorUI program and stable meta files.");
            var host = Rect("Video Sources", parent); host.sizeDelta = new Vector2(192, TargetHeight);
            var view = host.gameObject.AddUdonSharpComponent<SourceMirrorUI>();
            view.mirrors = mirrors; view.languageManager = language;
            view.entryButton = Button("Sources", host, buttonTemplate, textTemplate, TargetHeight,
                "Sources", out view.entryText, out var entryMarker);
            entryMarker.SetActive(false);
            var entry = view.entryButton.GetComponent<RectTransform>();
            entry.anchorMin = Vector2.zero; entry.anchorMax = Vector2.one;
            entry.offsetMin = entry.offsetMax = Vector2.zero;

            var popup = Rect("Source Panel", host);
            popup.anchorMin = popup.anchorMax = new Vector2(0, 0);
            popup.pivot = new Vector2(0, 1); popup.anchoredPosition = new Vector2(0, -Gap);
            popup.sizeDelta = new Vector2(Width, 0);
            var surface = Surface(popup, buttonTemplate.targetGraphic as Image);
            surface.raycastTarget = true;
            var layout = popup.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(24, 24, 20, 20); layout.spacing = Gap;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            var fitter = popup.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var header = Rect("Header", popup); Size(header, Width - 48, 40);
            view.titleText = Text("Title", header, textTemplate, 40, 24, "Video sources");
            view.titleText.rectTransform.anchorMin = Vector2.zero; view.titleText.rectTransform.anchorMax = Vector2.one;
            view.titleText.rectTransform.offsetMin = Vector2.zero; view.titleText.rectTransform.offsetMax = new Vector2(-60, 0);
            var close = Button("Close", header, buttonTemplate, textTemplate, TargetHeight, "×", out _, out var closeMarker);
            closeMarker.SetActive(false);
            var closeRect = close.GetComponent<RectTransform>();
            closeRect.anchorMin = closeRect.anchorMax = new Vector2(1, 0.5F);
            closeRect.pivot = new Vector2(1, 0.5F); closeRect.sizeDelta = new Vector2(TargetHeight, TargetHeight);
            closeRect.anchoredPosition = Vector2.zero;
            view.scopeText = Text("Scope", popup, textTemplate, 24, 16, "Only for you");
            view.modeText = Text("Selection Mode", popup, textTemplate, 28, 18, "Selection: Manual");
            view.actualText = Text("Actual Source", popup, textTemplate, 28, 18, "Playing from: —");
            if (view.actualText is TextMeshProUGUI actualTmp) actualTmp.enableWordWrapping = false;
            view.autoButton = Button("Auto", popup, buttonTemplate, textTemplate, TargetHeight, "Auto", out view.autoText, out view.autoSelected);
            view.sourceButtons = new Button[3]; view.sourceTexts = new Graphic[3]; view.sourceSelected = new GameObject[3];
            for (int i = 0; i < 3; i++)
                view.sourceButtons[i] = Button("Source " + i, popup, buttonTemplate, textTemplate, SourceHeight,
                    "Source " + (i + 1), out view.sourceTexts[i], out view.sourceSelected[i]);
            view.statusText = Text("Status", popup, textTemplate, 56, 16, "No video is playing");
            view.feedbackText = Text("Feedback", popup, textTemplate, 48, 16, "");
            var footer = Rect("Actions", popup); Size(footer, Width - 48, TargetHeight);
            var footerLayout = footer.gameObject.AddComponent<HorizontalLayoutGroup>();
            footerLayout.spacing = Gap; footerLayout.childControlWidth = footerLayout.childControlHeight = true;
            footerLayout.childForceExpandWidth = true; footerLayout.childForceExpandHeight = false;
            view.retryButton = Button("Retry", footer, buttonTemplate, textTemplate, TargetHeight,
                "Retry loading", out view.retryText, out var retryMarker);
            retryMarker.SetActive(false);
            view.enableButton = Button("Enable Mirrors", footer, buttonTemplate, textTemplate, TargetHeight,
                "Disable mirrors", out view.enableText, out var enableMarker);
            enableMarker.SetActive(false);
            view.panel = popup.gameObject;
            Wire(view.entryButton, view, nameof(SourceMirrorUI._Open));
            Wire(close, view, nameof(SourceMirrorUI._Close));
            Wire(view.autoButton, view, nameof(SourceMirrorUI._Auto), nameof(SourceMirrorUI._PressAuto));
            Wire(view.retryButton, view, nameof(SourceMirrorUI._Retry), nameof(SourceMirrorUI._PressRetry));
            Wire(view.enableButton, view, nameof(SourceMirrorUI._Enable));
            for (int i = 0; i < 3; i++) Wire(view.sourceButtons[i], view, "_Source" + i, "_PressSource" + i);
            popup.gameObject.SetActive(false);
            UdonSharpEditorUtility.CopyProxyToUdon(view);
            return view;
        }

        public static void Validate(SourceMirrorUI view) {
            Need(view && view.mirrors && view.panel && view.entryButton && view.autoButton && view.retryButton && view.enableButton,
                "Source UI references are incomplete.");
            var canvas = view.GetComponentInParent<Canvas>();
            Need(canvas && canvas.renderMode == RenderMode.WorldSpace && canvas.gameObject.layer != 5,
                "Personal sources need a world-space Canvas outside reserved UI layer5.");
            Need(view.sourceButtons != null && view.sourceButtons.Length == 3 && view.sourceButtons.All(x => x) &&
                view.sourceTexts != null && view.sourceTexts.Length == 3 && view.sourceTexts.All(x => x) &&
                view.sourceSelected != null && view.sourceSelected.Length == 3 && view.sourceSelected.All(x => x),
                "Source UI requires three paired optional rows.");
            Need(view.gameObject.scene == view.mirrors.gameObject.scene && !view.panel.transform.IsChildOf(view.entryButton.transform),
                "Keep the entry and popup in the Core scene, outside each other's click surfaces.");
            Need(view.panel.transform.IsChildOf(view.transform), "The optional view must own its popup.");
            foreach (var text in view.GetComponentsInChildren<TextMeshProUGUI>(true)) {
                Need(!text.richText && !text.enableAutoSizing && text.font, "Source UI text must use literal labels and configured fonts.");
            }
            foreach (var text in view.GetComponentsInChildren<UnityEngine.UI.Text>(true))
                Need(!text.supportRichText && !text.resizeTextForBestFit && text.font, "Source UI Text must use literal labels and configured fonts.");
            var expected = AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(ProgramPath);
            Need(expected && UdonSharpProgramAsset.GetProgramAssetForClass(typeof(SourceMirrorUI)) == expected,
                "Source UI must use its unique fork-owned program.");
        }
    }

    sealed class SourceMirrorUIBuildValidator : IProcessSceneWithReport {
        public int callbackOrder => -99999;
        public void OnProcessScene(UnityEngine.SceneManagement.Scene scene, BuildReport report) {
            foreach (var root in scene.GetRootGameObjects())
                foreach (var view in root.GetComponentsInChildren<SourceMirrorUI>(true)) SourceMirrorUIAuthoring.Validate(view);
        }
    }

    sealed class SourceMirrorUIWizard : ScriptableWizard {
        public RectTransform parent;
        public UIHandler existingControls;
        public SourceMirrorController mirrors;
        [MenuItem("GameObject/VizVid/Source Mirrors/Personal Source Controls", false, 12)]
        static void Open() => DisplayWizard<SourceMirrorUIWizard>("Personal Source Controls", "Create");
        void OnWizardUpdate() {
            helpString = "Adds one optional personal panel. Choose its Canvas parent and the existing controls for the same Core; place the entry beside local reload.";
            isValid = parent && existingControls && mirrors;
        }
        void OnWizardCreate() {
            var view = SourceMirrorUIAuthoring.Create(parent, mirrors, existingControls);
            Undo.RegisterCreatedObjectUndo(view.gameObject, "Add personal source controls");
            Selection.activeGameObject = view.gameObject;
            EditorUtility.SetDirty(view);
        }
    }
}
