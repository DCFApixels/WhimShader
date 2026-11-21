// Copied from WhimTex Icons/Drawn/ShaderGraphWindow.Icons.cs. Copyright (c) 2026 Mikhail.
// MIT; see ../Workspace/LICENSE.WhimTex.md.
using UnityEngine;
using UnityEngine.UIElements;

namespace DCFApixels.WhimShader
{
    internal sealed class WhimShaderNodeMessageBadge : VisualElement
    {
        readonly VisualElement icon;
        readonly HelpBox popup;
        HelpBoxMessageType level;
        internal WhimShaderNodeMessageBadge()
        {
            name = "node-message";
            AddToClassList("whimshader-shader-graph-message");
            icon = new VisualElement { name = "message-icon" }; Add(icon);
            icon.generateVisualContent += DrawIcon;
            popup = new HelpBox { name = "message-popup", pickingMode = PickingMode.Ignore };
            popup.AddToClassList("whimshader-shader-graph-message-popup");
            popup.style.display = DisplayStyle.None; Add(popup);
            icon.RegisterCallback<PointerEnterEvent>(_ => SetHovered(true));
            icon.RegisterCallback<PointerLeaveEvent>(_ => SetHovered(false));
            RegisterCallback<DetachFromPanelEvent>(_ => SetHovered(false));
        }
        void SetHovered(bool hovered)
        {
            if (hovered) parent?.BringToFront();
            popup.style.display = hovered ? DisplayStyle.Flex : DisplayStyle.None;
        }
        internal void SetMessage(string text, HelpBoxMessageType level)
        {
            if (popup.text == text && this.level == level) return;
            this.level = level;
            popup.text = text;
            icon.MarkDirtyRepaint();
            EnableInClassList("whimshader-shader-graph-message--error", level == HelpBoxMessageType.Error);
            EnableInClassList("whimshader-shader-graph-message--warning", level == HelpBoxMessageType.Warning);
            EnableInClassList("whimshader-shader-graph-message--info", level == HelpBoxMessageType.Info);
        }
        void DrawIcon(MeshGenerationContext context)
        {
            Rect rect = icon.contentRect;
            float scale = Mathf.Min(rect.width, rect.height) / 16f;
            if (scale <= 0) return;
            Vector2 Point(float x, float y) => rect.center + new Vector2(x - 8, y - 8) * scale;
            var painter = context.painter2D;
            painter.fillColor = level switch
            {
                HelpBoxMessageType.Error => new Color32(229, 81, 81, 255),
                HelpBoxMessageType.Warning => new Color32(245, 193, 65, 255),
                _ => new Color32(192, 192, 192, 255)
            };
            painter.strokeColor = new Color(0, 0, 0, .55f);
            painter.lineWidth = scale;
            painter.lineJoin = LineJoin.Round;
            painter.lineCap = LineCap.Round;
            painter.BeginPath();
            if (level == HelpBoxMessageType.Warning)
            {
                painter.MoveTo(Point(8, 1.25f));
                painter.LineTo(Point(14.75f, 14.25f));
                painter.LineTo(Point(1.25f, 14.25f));
                painter.ClosePath();
            }
            else if (level == HelpBoxMessageType.Error)
            {
                painter.MoveTo(Point(4.2f, 1.2f));
                painter.LineTo(Point(11.8f, 1.2f));
                painter.LineTo(Point(14.8f, 4.2f));
                painter.LineTo(Point(14.8f, 11.8f));
                painter.LineTo(Point(11.8f, 14.8f));
                painter.LineTo(Point(4.2f, 14.8f));
                painter.LineTo(Point(1.2f, 11.8f));
                painter.LineTo(Point(1.2f, 4.2f));
                painter.ClosePath();
            }
            else
            {
                painter.Arc(Point(8, 8), 6.8f * scale, 0, 360);
                painter.ClosePath();
            }
            painter.Fill(); painter.Stroke();
            painter.fillColor = painter.strokeColor = new Color32(43, 35, 17, 255);
            painter.lineWidth = 1.8f * scale;
            painter.BeginPath();
            float stemTop = level switch
            {
                HelpBoxMessageType.Error => 4.3f,
                HelpBoxMessageType.Warning => 5.3f,
                _ => 7.1f
            };
            bool info = level == HelpBoxMessageType.Info;
            painter.MoveTo(Point(8, stemTop));
            painter.LineTo(Point(8, info ? 11.3f : 8.8f));
            painter.Stroke();
            painter.BeginPath();
            painter.Arc(Point(8, info ? 4.5f : 11.6f), .95f * scale, 0, 360);
            painter.Fill();
        }
    }
}
