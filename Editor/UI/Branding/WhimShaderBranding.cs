using UnityEditor;
using UnityEngine;

namespace DCFApixels.WhimShader
{
    /// <summary>Original WhimShader artwork. Loading does not change importer or window state.</summary>
    public static class WhimShaderBranding
    {
        const string Root = "Packages/com.dcfapixels.whimshader/Editor/UI/Branding/";
        public const string IconAssetPath = Root + "WhimShaderIcon32.png";
        public const string Icon16AssetPath = Root + "WhimShaderIcon16.png";
        public const string Icon64AssetPath = Root + "WhimShaderIcon64.png";
        public const string VectorSourceAssetPath = Root + "WhimShaderIcon.svg";

        static Texture2D icon, icon16, icon64;

        /// <summary>32 px texture, suitable for a 16-point editor title at 2x density.</summary>
        public static Texture2D Icon => Load(ref icon, IconAssetPath);
        public static Texture2D Icon16 => Load(ref icon16, Icon16AssetPath);
        public static Texture2D Icon64 => Load(ref icon64, Icon64AssetPath);

        static Texture2D Load(ref Texture2D cached, string path)
        {
            if (cached == null) cached = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            return cached;
        }
    }
}
