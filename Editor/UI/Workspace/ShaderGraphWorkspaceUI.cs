using UnityEngine.UIElements;

namespace DCFApixels.WhimShader
{
    internal static class ShaderGraphWorkspaceUI
    {
        // Same finite-value check used by the original workspace's ProjectiveMatrix helper.
        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        // Preserve the original menu element structure; sizing comes from ShaderGraphWorkspace.uss.
        internal static void AddMenuDots(VisualElement target)
        {
            for (int i = 0; i < 3; i++)
            {
                var dot = new VisualElement { pickingMode = PickingMode.Ignore };
                dot.AddToClassList("whimshader-layer-menu-dot");
                target.Add(dot);
            }
        }
    }
}
