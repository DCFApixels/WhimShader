// Header, channel controls, checkerboard and height settings copied from WhimTex
// LayerPreviewPanel.cs / LayerPreviewPanel.Height.cs / ShaderGraphWindow.Preview.cs.
// MIT; see ../Workspace/LICENSE.WhimTex.md. Only graph binding and 3D mode differ.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.ShaderGraph;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace DCFApixels.WhimShader
{
    internal sealed class WhimShaderFinalPreviewPanel : ShaderGraphFloatingPanel, IDisposable
    {
        internal readonly Image Image;
        internal readonly VisualElement Surface;
        internal readonly VisualElement HeaderControls = new() { name = "layer-preview-controls" };
        readonly Label message;
        readonly PreviewLoadingIndicator loading;
        readonly Action savePlacement;
        readonly Action<WhimShaderNodePreviewSettings> changed;
        readonly Button[] channelButtons = new Button[4];
        readonly ToolbarToggle imageModeButton, heightModeButton, meshModeButton;
        readonly ToolbarButton heightSettingsButton;
        readonly DropdownField output;
        readonly List<int> outputIds = new();
        readonly HeightPreviewSettings heightState = new();
        HeightPreviewUI.SettingsPopup heightSettings;
        WhimShaderNodePreviewSettings state;

        internal WhimShaderFinalPreviewPanel(ShaderGraphPanelPlacement placement, bool subGraph,
            Action<WhimShaderNodePreviewSettings> changed, Action savePlacement)
            : base(placement, new Vector2(200, 100))
        {
            this.savePlacement = savePlacement; this.changed = changed;
            name = "shader-layer-preview";
            AddToClassList("whimshader-shader-graph-layer-preview");
            AddToClassList("whimshader-final-preview");
            var content = new VisualElement { name = "layer-preview" };
            content.AddToClassList("whimshader-output-preview-footer");
            var header = new UnityEditor.UIElements.Toolbar { name = "layer-preview-header" };
            header.Add(new Label(subGraph ? "Sub Graph Preview" : "Shader Preview") { pickingMode = PickingMode.Ignore });
            header.Add(new ToolbarSpacer { flex = true, pickingMode = PickingMode.Ignore });
            HeaderControls.AddToClassList("whimshader-layer-preview-controls");
            imageModeButton = HeightPreviewUI.ModeButton(false, "layer-preview-image", () => SetMode(WhimShaderPreviewMode.Flat));
            heightModeButton = HeightPreviewUI.ModeButton(true, "layer-preview-height", () => SetMode(WhimShaderPreviewMode.Height));
            meshModeButton = HeightPreviewUI.ModeButton(WhimShaderPreviewMode.ThreeD, "layer-preview-3d", () => SetMode(WhimShaderPreviewMode.ThreeD));
            HeaderControls.Add(imageModeButton); HeaderControls.Add(heightModeButton); HeaderControls.Add(meshModeButton);
            heightSettingsButton = new ToolbarButton(() =>
            {
                heightState.Read(state);
                heightSettings = new HeightPreviewUI.SettingsPopup(heightState,
                    _ => { state = heightState.Write(state); changed?.Invoke(state); }, () => heightSettings = null);
                UnityEditor.PopupWindow.Show(heightSettingsButton.worldBound, heightSettings);
            }) { name = "layer-preview-height-settings", text = "⋮", tooltip = "Height settings" };
            HeaderControls.Add(heightSettingsButton);
            if (subGraph)
            {
                output = new DropdownField { name = "layer-preview-output" };
                output.AddToClassList("whimshader-shader-graph-preview-output");
                output.RegisterValueChangedCallback(_ =>
                {
                    if (output.index < 0 || output.index >= outputIds.Count) return;
                    state.outputSlotId = outputIds[output.index]; changed?.Invoke(state);
                });
                HeaderControls.Add(output);
            }
            var channels = new VisualElement { name = "layer-preview-channels" };
            channels.AddToClassList("whimshader-layer-preview-channels");
            string[] labels = { "R", "G", "B", "A" };
            for (int i = 0; i < labels.Length; i++)
            {
                int bit = 1 << i;
                var button = new Button(() => ToggleChannel(bit))
                { name = "layer-preview-channel-" + labels[i].ToLowerInvariant(), text = labels[i] };
                button.AddToClassList("whimshader-channel-button");
                button.AddToClassList("whimshader-layer-preview-channel");
                channelButtons[i] = button; channels.Add(button);
            }
            channels.AddManipulator(new ChannelDragManipulator(channelButtons, () => state.EffectiveMask, ToggleChannel));
            HeaderControls.Add(channels); header.Add(HeaderControls); content.Add(header);
            Surface = new VisualElement { name = "layer-preview-surface" };
            Surface.AddToClassList("whimshader-output-preview-surface");
            Image = new Image { name = "final-preview-image", scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            Image.AddToClassList("whimshader-output-preview-image"); Surface.Add(Image);
            loading = new PreviewLoadingIndicator(Image); Surface.Add(loading);
            message = new Label { pickingMode = PickingMode.Ignore };
            message.AddToClassList("whimshader-preview-message"); Surface.Add(message);
            Surface.generateVisualContent += DrawCheckerboard;
            Surface.RegisterCallback<GeometryChangedEvent>(_ => Surface.MarkDirtyRepaint());
            content.Add(Surface); Add(content);
            AddDragHandle(this, HeaderControls); AddResizeHandles();
            PlacementChanged += savePlacement;
            UpdateModeButtons();
        }

        void SetMode(WhimShaderPreviewMode mode)
        {
            if (state.mode != mode)
            {
                state.mode = mode; heightSettings?.editorWindow?.Close(); heightSettings = null;
                changed?.Invoke(state);
            }
            UpdateModeButtons();
        }
        void ToggleChannel(int bit)
        {
            state.channelMask = (state.EffectiveMask ^ bit) & 15;
            UpdateModeButtons(); changed?.Invoke(state);
        }
        void UpdateModeButtons()
        {
            imageModeButton.SetValueWithoutNotify(state.mode == WhimShaderPreviewMode.Flat);
            heightModeButton.SetValueWithoutNotify(state.mode == WhimShaderPreviewMode.Height);
            meshModeButton.SetValueWithoutNotify(state.mode == WhimShaderPreviewMode.ThreeD);
            heightSettingsButton.style.display = state.mode == WhimShaderPreviewMode.Height ? DisplayStyle.Flex : DisplayStyle.None;
            for (int i = 0; i < channelButtons.Length; i++)
                channelButtons[i].EnableInClassList("whimshader-channel-button--enabled", (state.EffectiveMask & (1 << i)) != 0);
        }
        internal void SetState(WhimShaderNodePreviewSettings value, IEnumerable<MaterialSlot> slots)
        {
            if (state.mode != value.mode) { heightSettings?.editorWindow?.Close(); heightSettings = null; }
            state = value; UpdateModeButtons();
            if (output == null) return;
            var current = slots?.ToList() ?? new List<MaterialSlot>();
            var labels = current.Select(slot => slot.RawDisplayName()).ToList();
            for (int i = 0; i < labels.Count; i++)
                if (current.Count(slot => slot.RawDisplayName() == current[i].RawDisplayName()) > 1) labels[i] += " [" + current[i].id + "]";
            if (!outputIds.SequenceEqual(current.Select(slot => slot.id)) || !output.choices.SequenceEqual(labels))
            { outputIds.Clear(); outputIds.AddRange(current.Select(slot => slot.id)); output.choices = labels; }
            int selected = Math.Max(0, outputIds.IndexOf(state.outputSlotId));
            output.SetValueWithoutNotify(labels.Count == 0 ? string.Empty : labels[selected]);
            output.style.display = labels.Count > 1 ? DisplayStyle.Flex : DisplayStyle.None;
        }
        internal void SetUpdating(bool pending) => loading.SetUpdating(pending);
        internal void SetMessage(string text, string detail = null)
        {
            message.text = text; message.tooltip = detail;
            message.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }
        void DrawCheckerboard(MeshGenerationContext context)
        {
            Texture texture = Image.image; Rect area = Surface.contentRect;
            if (texture == null || area.width <= 0 || area.height <= 0) return;
            float scale = Mathf.Min(area.width / texture.width, area.height / texture.height);
            Vector2 size = new Vector2(texture.width, texture.height) * scale;
            WhimShaderNodeDrawing.DrawChecker(context, new Rect(area.center - size * .5f, size), 16);
        }
        public void Dispose()
        {
            heightSettings?.editorWindow?.Close(); heightSettings = null;
            loading.SetUpdating(false); Image.image = null;
            PlacementChanged -= savePlacement; Surface.generateVisualContent -= DrawCheckerboard;
            RemoveFromHierarchy();
        }
    }
}
