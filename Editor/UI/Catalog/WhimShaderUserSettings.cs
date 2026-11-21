using System;
using UnityEditor;
namespace DCFApixels.WhimShader
{
    internal static class WhimShaderUserSettings
    {
        internal static event Action CatalogDisplayChanged;
        internal static bool GraphCatalogThumbnails
        {
            get => EditorPrefs.GetBool("WhimShader.CatalogThumbnails", true);
            set { if (value == GraphCatalogThumbnails) return; EditorPrefs.SetBool("WhimShader.CatalogThumbnails", value); CatalogDisplayChanged?.Invoke(); }
        }
    }
}
