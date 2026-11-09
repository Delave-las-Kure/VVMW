using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.UI;
using UdonSharp;
using UdonSharpEditor;
using VRC.SDK3.Components;
using JLChnToZ.VRC.Foundation.I18N;
using static JLChnToZ.VRC.VVMW.Editors.SourceMirrorUIAuthoring;

namespace JLChnToZ.VRC.VVMW.Editors {
    /// <summary>Explicit native-themed shared catalog view; never wires publication policy implicitly.</summary>
    public static class SourceMirrorCatalogUIAuthoring {
        public const float PanelWidth = 760;
        static void Need(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        static void Wire(Button button, SourceMirrorCatalogUI view, string method) {
            var backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(view);
            Need(backing, "Catalog UI backing is missing.");
            button.onClick = new Button.ButtonClickedEvent();
            UnityEventTools.AddStringPersistentListener(button.onClick, backing.SendCustomEvent, method);
        }
        static RectTransform Vertical(string name, Transform parent) {
            var rect = Rect(name, parent);
            var layout = rect.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = Gap; layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            return rect;
        }
        static InputField Field(string name, Transform parent, VRCUrlInputField template, bool url) {
            var rect = Rect(name, parent); Size(rect, PanelWidth - 48, TargetHeight);
            var image = Surface(rect, template.targetGraphic as Image);
            var textRect = Rect("Text", rect);
            textRect.anchorMin = Vector2.zero; textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(12, 0); textRect.offsetMax = new Vector2(-12, 0);
            var text = textRect.gameObject.AddComponent<Text>();
            text.font = template.textComponent.font; text.fontSize = 18; text.fontStyle = template.textComponent.fontStyle;
            text.color = template.textComponent.color; text.material = template.textComponent.material;
            text.supportRichText = false; text.resizeTextForBestFit = false; text.alignment = TextAnchor.MiddleLeft;
            text.horizontalOverflow = HorizontalWrapMode.Overflow; text.raycastTarget = false;
            InputField input = url ? rect.gameObject.AddComponent<VRCUrlInputField>() : rect.gameObject.AddComponent<InputField>();
            input.textComponent = text; input.targetGraphic = image; input.colors = template.colors;
            input.lineType = InputField.LineType.SingleLine;
            input.navigation = new Navigation { mode = Navigation.Mode.None };
            input.characterLimit = url ? 4096 : 128;
            return input;
        }
        public static SourceMirrorCatalogUI Create(RectTransform parent, SourceMirrorCatalogAuthority authority, UIHandler controls) {
            Need(controls && authority && authority.core, "Choose the authority and existing controls.");
            var source = new SerializedObject(controls);
            Need(source.FindProperty("core").objectReferenceValue == authority.core, "Controls and catalog must use the same Core.");
            var status = source.FindProperty("statusObject").objectReferenceValue as GameObject;
            Graphic text = status ? status.GetComponent<Graphic>() : null;
            return Create(parent, authority, source.FindProperty("languageManager").objectReferenceValue as LanguageManager,
                source.FindProperty("reloadButton").objectReferenceValue as Button, text,
                source.FindProperty("urlInput").objectReferenceValue as VRCUrlInputField);
        }
        public static SourceMirrorCatalogUI Create(RectTransform parent, SourceMirrorCatalogAuthority authority,
            LanguageManager language, Button buttonTemplate, Graphic textTemplate, VRCUrlInputField inputTemplate) {
            Need(!EditorApplication.isPlayingOrWillChangePlaymode, "Compose catalog UI in Edit Mode.");
            Need(parent && authority && authority.core && parent.gameObject.scene == authority.core.gameObject.scene,
                "Choose a scene parent in the authority Core scene.");
            var canvas = parent.GetComponentInParent<Canvas>();
            Need(canvas && canvas.renderMode == RenderMode.WorldSpace && canvas.gameObject.layer != 5,
                "Catalog UI needs a world-space Canvas outside reserved UI5.");
            Need(buttonTemplate && buttonTemplate.targetGraphic is Image && textTemplate &&
                inputTemplate && inputTemplate.targetGraphic is Image && inputTemplate.textComponent && inputTemplate.textComponent.font,
                "Supply the native VizVid button/text/URL input templates.");
            var program = UdonSharpProgramAsset.GetProgramAssetForClass(typeof(SourceMirrorCatalogUI));
            Need(program && program.sourceCsScript && program.sourceCsScript.GetClass() == typeof(SourceMirrorCatalogUI),
                "Restore the fork-owned SourceMirrorCatalogUI program and stable metas.");
            var host = Rect("Shared Video Sources", parent); host.sizeDelta = new Vector2(240, TargetHeight);
            var view = host.gameObject.AddUdonSharpComponent<SourceMirrorCatalogUI>();
            view.authority = authority; view.languageManager = language;
            view.entryButton = Button("Shared Sources", host, buttonTemplate, textTemplate, TargetHeight,
                "Shared video sources", out view.entryText, out var marker); marker.SetActive(false);
            var entry = view.entryButton.GetComponent<RectTransform>();
            entry.anchorMin = Vector2.zero; entry.anchorMax = Vector2.one; entry.offsetMin = entry.offsetMax = Vector2.zero;
            var popup = Vertical("Shared Source Panel", host);
            popup.anchorMin = popup.anchorMax = Vector2.zero; popup.pivot = new Vector2(0, 1);
            popup.anchoredPosition = new Vector2(0, -Gap); popup.sizeDelta = new Vector2(PanelWidth, 0);
            Surface(popup, buttonTemplate.targetGraphic as Image);
            popup.GetComponent<VerticalLayoutGroup>().padding = new RectOffset(24, 24, 20, 20);
            popup.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            view.titleText = Text("Title", popup, textTemplate, 32, 24, "Shared video sources");
            view.scopeText = Text("Scope", popup, textTemplate, 44, 18, "For everyone");
            view.primaryText = Text("Current Primary URL", popup, textTemplate, 72, 18, "Current video");
            view.nameInputs = new InputField[3]; view.mirrorInputs = new VRCUrlInputField[2];
            view.nameCaptions = new Graphic[3]; view.urlCaptions = new Graphic[2];
            view.nameCaptions[0] = Text("Primary Name Caption", popup, textTemplate, 24, 18, "Primary source name");
            view.nameInputs[0] = Field("Primary Name", popup, inputTemplate, false);
            for (int i = 1; i < 3; i++) {
                var row = Rect("Mirror " + i, popup); Size(row, PanelWidth - 48, 84);
                var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>(); layout.spacing = Gap;
                layout.childControlWidth = layout.childControlHeight = true;
                layout.childForceExpandWidth = false; layout.childForceExpandHeight = false;
                var names = Vertical("Name", row); var namesSize = names.gameObject.AddComponent<LayoutElement>();
                namesSize.minWidth = namesSize.preferredWidth = 200;
                var urls = Vertical("URL", row); urls.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
                view.nameCaptions[i] = Text("Name Caption", names, textTemplate, 24, 18, "Mirror " + i);
                view.nameInputs[i] = Field("Name", names, inputTemplate, false);
                view.urlCaptions[i - 1] = Text("URL Caption", urls, textTemplate, 24, 18, "Full URL · optional");
                view.mirrorInputs[i - 1] = (VRCUrlInputField)Field("URL", urls, inputTemplate, true);
            }
            var confirmation = Rect("Compatible Edit", popup); Size(confirmation, PanelWidth - 48, 64);
            Surface(confirmation, buttonTemplate.targetGraphic as Image);
            var toggle = confirmation.gameObject.AddComponent<Toggle>(); toggle.colors = buttonTemplate.colors;
            var check = Rect("Check", confirmation); check.anchorMin = check.anchorMax = new Vector2(0, .5F);
            check.sizeDelta = new Vector2(20, 20); check.anchoredPosition = new Vector2(18, 0);
            var checkImage = check.gameObject.AddComponent<Image>(); checkImage.color = buttonTemplate.colors.pressedColor;
            checkImage.raycastTarget = false; toggle.graphic = checkImage;
            view.confirmationText = Text("Caption", confirmation, textTemplate, 64, 18, "Same edit, start and speed");
            var label = view.confirmationText.rectTransform; label.anchorMin = Vector2.zero; label.anchorMax = Vector2.one;
            label.offsetMin = new Vector2(40, 10); label.offsetMax = new Vector2(-12, -8);
            toggle.targetGraphic = view.confirmationText; toggle.navigation = new Navigation { mode = Navigation.Mode.None };
            view.compatibilityToggle = toggle;
            view.statusText = Text("Status", popup, textTemplate, 64, 18, "");
            var footer = Rect("Actions", popup); Size(footer, PanelWidth - 48, TargetHeight);
            var footerLayout = footer.gameObject.AddComponent<HorizontalLayoutGroup>(); footerLayout.spacing = Gap;
            footerLayout.childControlWidth = footerLayout.childControlHeight = true;
            footerLayout.childForceExpandWidth = true; footerLayout.childForceExpandHeight = false;
            view.applyButton = Button("Apply", footer, buttonTemplate, textTemplate, TargetHeight, "Apply for everyone", out view.applyText, out marker); marker.SetActive(false);
            view.reloadButton = Button("Reload", footer, buttonTemplate, textTemplate, TargetHeight, "Reload list", out view.reloadText, out marker); marker.SetActive(false);
            var close = Button("Close", footer, buttonTemplate, textTemplate, TargetHeight, "Close", out view.closeText, out marker); marker.SetActive(false);
            view.panel = popup.gameObject;
            Wire(view.entryButton, view, nameof(SourceMirrorCatalogUI._Open)); Wire(view.applyButton, view, nameof(SourceMirrorCatalogUI._Apply));
            Wire(view.reloadButton, view, nameof(SourceMirrorCatalogUI._Reload)); Wire(close, view, nameof(SourceMirrorCatalogUI._Close));
            popup.gameObject.SetActive(false); UdonSharpEditorUtility.CopyProxyToUdon(view); return view;
        }
        public static void Validate(SourceMirrorCatalogUI view) {
            Need(view && view.authority && view.panel && view.entryButton && view.applyButton && view.reloadButton && view.compatibilityToggle,
                "Catalog UI references are incomplete.");
            SourceMirrorAuthoring.ValidateCatalogAuthority(view.authority);
            var canvas = view.GetComponentInParent<Canvas>();
            Need(canvas && canvas.renderMode == RenderMode.WorldSpace && canvas.gameObject.layer != 5 &&
                view.gameObject.scene == view.authority.core.gameObject.scene, "Catalog UI Canvas/Core scene mismatch or reserved UI5.");
            Need(view.nameInputs != null && view.nameInputs.Length == 3 && view.nameInputs.All(x => x) &&
                view.mirrorInputs != null && view.mirrorInputs.Length == 2 && view.mirrorInputs.All(x => x), "Catalog inputs missing.");
            foreach (var text in view.GetComponentsInChildren<Text>(true)) Need(!text.supportRichText, "Catalog text must be literal.");
            foreach (var text in view.GetComponentsInChildren<TMPro.TMP_Text>(true)) Need(!text.richText, "Catalog TMP text must be literal.");
            Need(view.GetComponentsInChildren<RectTransform>(true).All(x => x.gameObject.layer == canvas.gameObject.layer),
                "Catalog controls must inherit the world Canvas layer.");
        }
    }
    sealed class SourceMirrorCatalogUIBuildValidator : IProcessSceneWithReport {
        public int callbackOrder => -9100;
        public void OnProcessScene(UnityEngine.SceneManagement.Scene scene, BuildReport report) {
            foreach (var root in scene.GetRootGameObjects())
                foreach (var view in root.GetComponentsInChildren<SourceMirrorCatalogUI>(true)) SourceMirrorCatalogUIAuthoring.Validate(view);
        }
    }
    sealed class SourceMirrorCatalogUIWizard : ScriptableWizard {
        public RectTransform parent;
        public UIHandler existingControls;
        public SourceMirrorCatalogAuthority authority;
        [MenuItem("GameObject/VizVid/Source Mirrors/Shared Catalog Controls", false, 13)]
        static void Open() => DisplayWizard<SourceMirrorCatalogUIWizard>("Shared Catalog Controls", "Create");
        void OnWizardUpdate() {
            helpString = "Adds an optional catalog form for the current video. Existing authority/policy required; no media or permission wiring is replaced.";
            isValid = parent && existingControls && authority;
        }
        void OnWizardCreate() {
            var view = SourceMirrorCatalogUIAuthoring.Create(parent, authority, existingControls);
            Undo.RegisterCreatedObjectUndo(view.gameObject, "Add shared catalog controls");
            Selection.activeGameObject = view.gameObject; EditorUtility.SetDirty(view);
        }
    }
}
