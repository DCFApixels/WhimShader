// Pure drawing extracted/adapted from WhimTexCheckerboard.cs and ShaderGraphWindow
// Preview.cs / Connections.cs / Nodes.cs. Copyright (c) 2026 Mikhail.
// MIT; see ../Workspace/LICENSE.WhimTex.md.
using UnityEditor;
using UnityEditor.ShaderGraph;
using UnityEngine;
using UnityEngine.UIElements;

namespace DCFApixels.WhimShader
{
    internal static class WhimShaderNodeDrawing
    {
        static Texture2D checker;
        static WhimShaderNodeDrawing()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Release;
            EditorApplication.quitting += Release;
        }

        static void Release()
        {
            if (checker != null) Object.DestroyImmediate(checker);
            checker = null;
        }

        internal static void DrawChecker(MeshGenerationContext context)
            => DrawChecker(context, context.visualElement.contentRect, 8);

        internal static void DrawChecker(MeshGenerationContext context, Rect rect, float cellSize)
        {
            if (!(rect.width > 0 && rect.height > 0 && cellSize > 0)) return;
            if (checker == null)
            {
                checker = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    name = "WhimShader Preview Checkerboard", hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat
                };
                Color32 first = new Color(.28f, .28f, .28f, 1), second = new Color(.42f, .42f, .42f, 1);
                checker.SetPixels32(new[] { first, second, second, first }); checker.Apply(false, false);
            }
            float period = 2 * cellSize;
            float u = rect.width / period, v = rect.height / period;
            context.AllocateTempMesh(4, 6, out var vertices, out var indices);
            vertices[0] = new Vertex { position = new Vector3(rect.xMin, rect.yMin, Vertex.nearZ), tint = Color.white, uv = Vector2.zero };
            vertices[1] = new Vertex { position = new Vector3(rect.xMax, rect.yMin, Vertex.nearZ), tint = Color.white, uv = new Vector2(u, 0) };
            vertices[2] = new Vertex { position = new Vector3(rect.xMax, rect.yMax, Vertex.nearZ), tint = Color.white, uv = new Vector2(u, v) };
            vertices[3] = new Vertex { position = new Vector3(rect.xMin, rect.yMax, Vertex.nearZ), tint = Color.white, uv = new Vector2(0, v) };
            indices[0] = 0; indices[1] = 1; indices[2] = 2; indices[3] = 0; indices[4] = 2; indices[5] = 3;
#if UNITY_6000_3_OR_NEWER
            context.DrawMesh(vertices, indices, checker, TextureOptions.SkipDynamicAtlas);
#else
            context.DrawMesh(vertices, indices, checker);
#endif
        }

        internal static void DrawChevron(MeshGenerationContext context, bool up)
        {
            var icon = context.visualElement;
            Vector2 center = icon.contentRect.center;
            var painter = context.painter2D;
            painter.lineWidth = 2; painter.lineCap = LineCap.Round; painter.lineJoin = LineJoin.Round;
            painter.strokeColor = icon.resolvedStyle.color;
            float direction = up ? -1 : 1;
            painter.BeginPath(); painter.MoveTo(center + new Vector2(-4, -2 * direction));
            painter.LineTo(center + new Vector2(0, 2 * direction));
            painter.LineTo(center + new Vector2(4, -2 * direction)); painter.Stroke();
        }

        internal static void DrawConnectorHover(MeshGenerationContext context)
        {
            var connector = context.visualElement;
            var rect = new Rect(Vector2.zero, connector.layout.size);
            if (rect.width <= 0 || rect.height <= 0) return;
            var painter = context.painter2D;
            painter.strokeColor = connector.resolvedStyle.borderLeftColor;
            painter.lineWidth = 2;
            painter.BeginPath();
            if (connector.resolvedStyle.borderTopLeftRadius > 0)
                painter.Arc(rect.center, Mathf.Min(rect.width, rect.height) * .5f, 0, 360);
            else
            {
                painter.MoveTo(rect.min); painter.LineTo(new Vector2(rect.xMax, rect.yMin));
                painter.LineTo(rect.max); painter.LineTo(new Vector2(rect.xMin, rect.yMax));
            }
            painter.ClosePath(); painter.Stroke();
        }

        internal static Color PortColor(ConcreteSlotValueType type) => type switch
        {
            ConcreteSlotValueType.Vector1 => new Color32(132, 228, 231, 255),
            ConcreteSlotValueType.Vector2 => new Color32(154, 239, 146, 255),
            ConcreteSlotValueType.Vector3 => new Color32(246, 255, 154, 255),
            ConcreteSlotValueType.Vector4 => new Color32(251, 203, 244, 255),
            ConcreteSlotValueType.Boolean or ConcreteSlotValueType.PropertyConnectionState => new Color32(148, 129, 230, 255),
            ConcreteSlotValueType.Texture2D or ConcreteSlotValueType.Texture2DArray or ConcreteSlotValueType.Texture3D or ConcreteSlotValueType.Cubemap or ConcreteSlotValueType.VirtualTexture => new Color32(255, 139, 139, 255),
            ConcreteSlotValueType.Gradient => new Color(.5f, .8f, .9f),
            _ => new Color(.85f, .65f, .55f)
        };

        internal static string PortTypeLabel(ConcreteSlotValueType type) => type switch
        {
            ConcreteSlotValueType.Vector1 => "1", ConcreteSlotValueType.Vector2 => "2",
            ConcreteSlotValueType.Vector3 => "3", ConcreteSlotValueType.Vector4 => "4",
            ConcreteSlotValueType.Boolean => "bool", ConcreteSlotValueType.PropertyConnectionState => "bool",
            ConcreteSlotValueType.Texture2D => "T2", ConcreteSlotValueType.Texture2DArray => "T2[]",
            ConcreteSlotValueType.Texture3D => "T3", ConcreteSlotValueType.Cubemap => "Cube",
            ConcreteSlotValueType.VirtualTexture => "VT", ConcreteSlotValueType.Gradient => "G",
            ConcreteSlotValueType.SamplerState => "S", ConcreteSlotValueType.Matrix2 => "2x2",
            ConcreteSlotValueType.Matrix3 => "3x3", ConcreteSlotValueType.Matrix4 => "4x4", _ => type.ToString()
        };
    }
}
