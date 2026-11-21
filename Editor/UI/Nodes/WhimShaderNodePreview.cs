// Preview hierarchy and controls copied from WhimTex ShaderGraphWindow.Preview.cs / NodeHeight.cs.
// Copyright (c) 2026 Mikhail. MIT; see ../Workspace/LICENSE.WhimTex.md.
using System;
using System.Collections.Generic;
using UnityEditor.ShaderGraph;
using UnityEngine;
using UnityEngine.UIElements;

namespace DCFApixels.WhimShader
{
    internal sealed class WhimShaderNodePreview : VisualElement, IDisposable
    {
        readonly VisualElement content;
        readonly Button expand;
        readonly PreviewLoadingIndicator loading;
        readonly WhimShaderPreviewControls controls;
        bool expanded, updating;
        internal VisualElement Surface { get; }
        internal Image Image { get; }

        internal WhimShaderNodePreview(Action<bool> changeExpanded, Action<WhimShaderNodePreviewSettings> changed)
        {
            AddToClassList("whimshader-shader-graph-preview");
            content = new VisualElement(); Add(content);
            Surface = new VisualElement(); Surface.AddToClassList("whimshader-shader-graph-node-preview");
            Surface.generateVisualContent += WhimShaderNodeDrawing.DrawChecker;
            Image = new Image { name = "whimshader-preview-image", scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            Image.AddToClassList("whimshader-shader-graph-node-preview-image"); Surface.Add(Image); content.Add(Surface);
            controls = new WhimShaderPreviewControls(changed); controls.BindSurface(Surface); Surface.Add(controls);
            var badge = HeightPreviewUI.Icon(true);
            badge.AddToClassList("whimshader-node-preview-height-badge"); Surface.Add(badge);
            var collapse = new Button(() => changeExpanded(false)) { name = "collapse-preview" };
            collapse.AddToClassList("whimshader-shader-graph-preview-collapse"); collapse.Add(Chevron(true)); Surface.Add(collapse);
            loading = new PreviewLoadingIndicator(Image); Surface.Insert(1, loading);
            expand = new Button(() => changeExpanded(true)) { name = "expand-preview" };
            expand.AddToClassList("whimshader-shader-graph-preview-expand"); expand.Add(Chevron(false)); Add(expand);
        }

        static VisualElement Chevron(bool up)
        {
            var icon = new VisualElement { pickingMode = PickingMode.Ignore };
            icon.AddToClassList("whimshader-shader-graph-preview-chevron");
            icon.generateVisualContent += context => WhimShaderNodeDrawing.DrawChevron(context, up);
            return icon;
        }

        internal void SetExpanded(bool value)
        {
            expanded = value;
            content.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
            expand.style.display = value ? DisplayStyle.None : DisplayStyle.Flex;
            EnableInClassList("whimshader-shader-graph-preview--collapsed", !value);
            if (!value) controls.CloseSettings();
            loading.SetUpdating(updating && expanded);
        }
        internal void SetState(WhimShaderNodePreviewSettings value, IEnumerable<MaterialSlot> slots) => controls.SetState(value, slots);
        internal void SetUpdating(bool value) { updating = value; loading.SetUpdating(updating && expanded); }
        internal void RefreshTint() { if (loading.IsUpdating) Image.tintColor = new Color(.62f, .62f, .62f, 1); }
        public void Dispose() { controls.Dispose(); loading.SetUpdating(false); }
    }
}
