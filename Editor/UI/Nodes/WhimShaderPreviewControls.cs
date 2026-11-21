// UI copied from WhimTex ShaderGraphWindow.Preview.cs / NodeHeight.cs.
// Copyright (c) 2026 Mikhail. MIT; see ../Workspace/LICENSE.WhimTex.md.
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
    internal sealed class WhimShaderPreviewControls : VisualElement, IDisposable
    {
        readonly ToolbarToggle imageModeButton, heightModeButton, threeDModeButton;
        readonly ToolbarButton heightSettingsButton;
        readonly HeightPreviewSettings heightState = new();
        HeightPreviewUI.SettingsPopup heightSettings;
        readonly DropdownField output = new(new List<string> { "Out" }, 0);
        readonly List<int> outputIds = new();
        readonly Action<WhimShaderNodePreviewSettings> changed;
        readonly HashSet<DropdownField> openMenus = new();
        VisualElement surface;
        WhimShaderNodePreviewSettings state;

        internal WhimShaderPreviewControls(Action<WhimShaderNodePreviewSettings> changed, bool hoverOnly = true)
        {
            this.changed = changed;
            name = "node-preview-controls";
            AddToClassList("whimshader-node-preview-controls");
            if (!hoverOnly) AddToClassList("whimshader-preview-controls--always-visible");
            RegisterCallback<FocusInEvent>(_ => AddToClassList("whimshader-node-preview-controls--focused"));
            RegisterCallback<FocusOutEvent>(e =>
            {
                if (e.relatedTarget is not VisualElement next || !Contains(next))
                    RemoveFromClassList("whimshader-node-preview-controls--focused");
            });
            imageModeButton = HeightPreviewUI.ModeButton(false, "node-preview-image", () => SetPreviewMode(WhimShaderPreviewMode.Flat));
            heightModeButton = HeightPreviewUI.ModeButton(true, "node-preview-height", () => SetPreviewMode(WhimShaderPreviewMode.Height));
            threeDModeButton = HeightPreviewUI.ModeButton(WhimShaderPreviewMode.ThreeD, "node-preview-3d", () => SetPreviewMode(WhimShaderPreviewMode.ThreeD));
            Add(imageModeButton); Add(heightModeButton); Add(threeDModeButton);
            heightSettingsButton = new ToolbarButton(() =>
            {
                heightSettings?.editorWindow?.Close();
                heightState.Read(state);
                heightSettings = new HeightPreviewUI.SettingsPopup(heightState, _ =>
                {
                    state = heightState.Write(state); changed?.Invoke(state);
                }, () => { heightSettings = null; RefreshOpenState(); });
                RefreshOpenState();
                UnityEditor.PopupWindow.Show(heightSettingsButton.worldBound, heightSettings);
            }) { name = "node-preview-height-settings", text = "⋮", tooltip = "Height settings" };
            Add(heightSettingsButton);
            output.name = "preview-output"; output.tooltip = "Preview output";
            foreach (var field in new[] { output })
            {
                field.AddToClassList("whimshader-shader-graph-preview-output"); Add(field);
                field.RegisterCallback<PointerDownEvent>(e =>
                {
                    if (e.button != 0) return;
                    e.StopImmediatePropagation(); ShowMenu(field);
                }, TrickleDown.TrickleDown);
                field.RegisterCallback<KeyDownEvent>(e =>
                {
                    if (e.keyCode is not (KeyCode.Space or KeyCode.Return or KeyCode.KeypadEnter or KeyCode.UpArrow or KeyCode.DownArrow)) return;
                    e.StopImmediatePropagation(); ShowMenu(field);
                }, TrickleDown.TrickleDown);
                field.RegisterCallback<NavigationSubmitEvent>(e =>
                { e.StopImmediatePropagation(); ShowMenu(field); }, TrickleDown.TrickleDown);
            }
            output.RegisterValueChangedCallback(_ =>
            {
                if (output.index < 0 || output.index >= outputIds.Count) return;
                state.outputSlotId = outputIds[output.index]; changed?.Invoke(state);
            });
            RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode is KeyCode.Backspace or KeyCode.Delete or KeyCode.Space or KeyCode.LeftArrow or KeyCode.RightArrow or KeyCode.UpArrow or KeyCode.DownArrow)
                    e.StopPropagation();
            });
            RegisterCallback<DetachFromPanelEvent>(_ => CloseSettings());
        }

        internal void BindSurface(VisualElement value) => surface = value;

        void SetPreviewMode(WhimShaderPreviewMode value)
        {
            if (state.mode == value) return;
            CloseSettings(); state.mode = value; UpdateViewControls(); changed?.Invoke(state);
        }

        void UpdateViewControls()
        {
            imageModeButton.SetValueWithoutNotify(state.mode == WhimShaderPreviewMode.Flat);
            heightModeButton.SetValueWithoutNotify(state.mode == WhimShaderPreviewMode.Height);
            threeDModeButton.SetValueWithoutNotify(state.mode == WhimShaderPreviewMode.ThreeD);
            heightSettingsButton.EnableInClassList("whimshader-hidden", state.mode != WhimShaderPreviewMode.Height);
            surface?.EnableInClassList("whimshader-node-preview--height", state.mode == WhimShaderPreviewMode.Height);
        }

        void RefreshOpenState() => EnableInClassList("whimshader-node-preview-controls--open", heightSettings != null || openMenus.Count != 0);
        internal void CloseSettings() { heightSettings?.editorWindow?.Close(); heightSettings = null; RefreshOpenState(); }

        // WhimTex's output menu: opens above the preview, follows graph zoom, and remains visible
        // while focus is in the popup. Display channels share this menu to retain its single row.
        void ShowMenu(DropdownField field)
        {
            if (field.panel == null || !field.enabledInHierarchy || field.choices.Count == 0 || openMenus.Contains(field)) return;
            var input = field.Q(className: DropdownField.inputUssClassName);
            Rect anchor = input.worldBound;
            float menuScale = Mathf.Max(.7f, anchor.height / input.resolvedStyle.height);
            var menu = new GenericDropdownMenu();
            if (field.choices.Count > 1)
            {
                foreach (string choice in field.choices)
                    menu.AddItem(choice, field.value == choice, () =>
                    {
                        if (field.panel != null && field.choices.Contains(choice)) field.value = choice;
                    });
                menu.AddSeparator(string.Empty);
            }
            string[] channels = { "RGBA", "R", "G", "B", "A" };
            int[] masks = { 15, 1, 2, 4, 8 };
            for (int i = 0; i < channels.Length; i++)
            {
                int index = i;
                menu.AddItem(channels[i], state.EffectiveMask == masks[i], () =>
                {
                    if (field.panel == null) return;
                    state.channel = (WhimShaderPreviewChannel)index; state.channelMask = -1;
                    changed?.Invoke(state);
                });
            }
            field.AddToClassList("whimshader-shader-graph-preview-output--open");
            openMenus.Add(field); RefreshOpenState();
            menu.onClose += () =>
            {
                field.RemoveFromClassList("whimshader-shader-graph-preview-output--open");
                openMenus.Remove(field); RefreshOpenState();
            };
            var popup = menu.contentContainer;
            while (!popup.ClassListContains(GenericDropdownMenu.containerOuterUssClassName)) popup = popup.parent;
            popup.name = "node-preview-output-menu";
            popup.style.minWidth = anchor.width / menuScale;
            popup.style.transformOrigin = new TransformOrigin(0, 0, 0);
            popup.style.scale = new Scale(new Vector3(menuScale, menuScale, 1));
            popup.style.opacity = 0;
            void PositionMenu(GeometryChangedEvent e)
            {
                if (e.newRect.width <= 0 || e.newRect.height <= 0 || e.newRect.size == e.oldRect.size) return;
                popup.schedule.Execute(() =>
                {
                    if (field.panel == null || popup.panel == null) return;
                    anchor = input.worldBound;
                    float right = (surface ?? this).worldBound.xMax;
                    Vector2 corner = popup.parent.WorldToLocal(new Vector2(right, anchor.yMin));
                    float availableHeight = Mathf.Max(0, corner.y);
                    float height = Mathf.Min(popup.resolvedStyle.height * menuScale, availableHeight);
                    menu.DropDown(new Rect(right - popup.resolvedStyle.width * menuScale, anchor.yMin - height, 0, 0),
                        field, DropdownMenuSizeMode.Content);
                    popup.style.maxHeight = availableHeight / menuScale;
                    popup.style.opacity = 1;
                });
            }
            popup.RegisterCallback<GeometryChangedEvent>(PositionMenu);
            field.Focus();
            menu.DropDown(new Rect((surface ?? this).worldBound.xMax, anchor.yMin, 0, 0), field, DropdownMenuSizeMode.Content);
        }

        internal void SetState(WhimShaderNodePreviewSettings value, IEnumerable<MaterialSlot> slots = null)
        {
            if (state.mode != value.mode) CloseSettings();
            state = value; heightState.Read(value); UpdateViewControls();
            string channelText = value.EffectiveMask switch { 15 => "RGBA", 1 => "R", 2 => "G", 4 => "B", 8 => "A", _ => "Custom" };
            var current = slots?.ToList() ?? new List<MaterialSlot>();
            var labels = current.Select(slot => slot.RawDisplayName()).ToList();
            for (int i = 0; i < labels.Count; ++i)
                if (current.Count(slot => slot.RawDisplayName() == current[i].RawDisplayName()) > 1)
                    labels[i] += " [" + current[i].id + "]";
            if (!outputIds.SequenceEqual(current.Select(slot => slot.id)) || !output.choices.SequenceEqual(labels))
            {
                outputIds.Clear(); outputIds.AddRange(current.Select(slot => slot.id)); output.choices = labels;
            }
            int selected = Math.Max(0, outputIds.IndexOf(value.outputSlotId));
            output.SetValueWithoutNotify(labels.Count > 1 ? labels[selected] : channelText);
            output.tooltip = "Preview output / channel: " + channelText;
            output.EnableInClassList("whimshader-hidden", labels.Count == 0);
        }

        public void Dispose() => CloseSettings();
    }
}
