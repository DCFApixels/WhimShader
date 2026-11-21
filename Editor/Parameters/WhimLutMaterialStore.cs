using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DCFApixels.WhimShader
{
    [InitializeOnLoad]
    public static class WhimLutMaterialStore
    {
        static WhimLutMaterialStore() { Undo.undoRedoPerformed += RebakeAfterUndo; }

        public static WhimLutAsset Find(Texture texture)
        {
            if (texture == null) return null;
            string path = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(path)) return null;
            foreach (var item in AssetDatabase.LoadAllAssetsAtPath(path))
                if (item is WhimLutAsset asset && asset.Texture == texture) return asset;
            return null;
        }

        public static bool CanEdit(Material material)
        {
            if (material == null) return false;
            string path = AssetDatabase.GetAssetPath(material);
            return path.StartsWith("Assets/", StringComparison.Ordinal) &&
                path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) &&
                AssetDatabase.IsOpenForEdit(material);
        }

        /// <summary>Copy-on-write: never edits another material's LUT or an imported graph default.</summary>
        public static WhimLutAsset Apply(Material material, string propertyName, WhimLutData data)
        {
            if (!CanEdit(material)) throw new ArgumentException("Save the editable material under Assets first.", nameof(material));
            if (string.IsNullOrEmpty(propertyName) || !material.HasProperty(propertyName))
                throw new ArgumentException("Material has no matching LUT texture property.", nameof(propertyName));
            if (data == null) throw new ArgumentNullException(nameof(data));
            data.Validate();

            var asset = Find(material.GetTexture(propertyName));
            bool ownsAsset = asset != null && asset.OwnerMaterial == material && asset.PropertyName == propertyName &&
                AssetDatabase.IsMainAsset(asset) && AssetDatabase.IsOpenForEdit(asset);
            if (ownsAsset)
                UpdateAsset(asset, data);
            else
            {
                var texture = WhimLutTexture.Create(data, propertyName + " LUT");
                asset = ScriptableObject.CreateInstance<WhimLutAsset>();
                asset.name = material.name + " " + propertyName + " LUT";
                asset.SetData(data);
                asset.SetBinding(texture, material, propertyName);
                string materialPath = AssetDatabase.GetAssetPath(material);
                string directory = Path.GetDirectoryName(materialPath).Replace('\\', '/');
                string fileName = SafeFileName(Path.GetFileNameWithoutExtension(materialPath)) + "." +
                    SafeFileName(propertyName) + ".lut.asset";
                SaveNewAsset(asset, AssetDatabase.GenerateUniqueAssetPath(directory + "/" + fileName));
            }

            Undo.RecordObject(material, "Change LUT Parameter");
            WhimLutTexture.Bind(material, propertyName, asset.Texture);
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssetIfDirty(material);
            return asset;
        }

        public static void UpdateAsset(WhimLutAsset asset, WhimLutData data)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (!AssetDatabase.IsMainAsset(asset) || !AssetDatabase.IsOpenForEdit(asset))
                throw new InvalidOperationException("Imported graph defaults are edited through their graph properties.");
            if (data == null) throw new ArgumentNullException(nameof(data));
            data.Validate();
            if (asset.Data.kind != data.kind) throw new ArgumentException("Cannot change the kind of an existing LUT.", nameof(data));
            if (asset.Texture == null) throw new InvalidOperationException("The LUT texture subasset is missing.");
            Undo.RegisterCompleteObjectUndo(new UnityEngine.Object[] { asset, asset.Texture }, "Edit LUT Data");
            asset.SetData(data);
            Rebake(asset);
            AssetDatabase.SaveAssetIfDirty(asset);
        }

        internal static void SaveNewAsset(WhimLutAsset asset, string path)
        {
            // Callers always generate a unique path. A failed save never overwrites an existing asset.
            if (File.Exists(path) || AssetDatabase.LoadMainAssetAtPath(path) != null)
                throw new IOException("The LUT destination already exists: " + path);
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.AddObjectToAsset(asset.Texture, asset);
            EditorUtility.SetDirty(asset);
            EditorUtility.SetDirty(asset.Texture);
            AssetDatabase.SaveAssetIfDirty(asset);
        }

        internal static string SafeFileName(string name)
        {
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; ++i)
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_' && chars[i] != '-') chars[i] = '_';
            return chars.Length == 0 ? "LUT" : new string(chars);
        }

        public static string[] CollectExportAssets(IEnumerable<Material> materials)
        {
            if (materials == null) throw new ArgumentNullException(nameof(materials));
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var material in materials)
            {
                string path = AssetDatabase.GetAssetPath(material);
                if (string.IsNullOrEmpty(path)) throw new ArgumentException("Save materials before collecting export assets.", nameof(materials));
                foreach (string dependency in AssetDatabase.GetDependencies(path, true)) paths.Add(dependency);
            }
            var result = new string[paths.Count];
            paths.CopyTo(result);
            Array.Sort(result, StringComparer.Ordinal);
            return result;
        }

        static void Rebake(WhimLutAsset asset)
        {
            var data = asset.Data;
            if (asset.Texture.width != data.resolution &&
                !asset.Texture.Reinitialize(data.resolution, 1, TextureFormat.RGBAFloat, false))
                throw new InvalidOperationException("Could not resize the LUT texture.");
            asset.Rebake();
            EditorUtility.SetDirty(asset);
            EditorUtility.SetDirty(asset.Texture);
            if (asset.OwnerMaterial != null && asset.OwnerMaterial.GetTexture(asset.PropertyName) == asset.Texture)
                WhimLutTexture.Bind(asset.OwnerMaterial, asset.PropertyName, asset.Texture);
        }

        static void RebakeAfterUndo()
        {
            foreach (var asset in Resources.FindObjectsOfTypeAll<WhimLutAsset>())
                if (asset != null && asset.Texture != null && AssetDatabase.IsMainAsset(asset) && AssetDatabase.IsOpenForEdit(asset))
                    Rebake(asset);
        }
    }
}
