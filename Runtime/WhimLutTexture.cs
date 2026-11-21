using System;
using UnityEngine;

namespace DCFApixels.WhimShader
{
    /// <summary>Main-thread LUT baking and material binding. The caller owns created textures.</summary>
    public static class WhimLutTexture
    {
        public static Texture2D Create(WhimLutData data, string name = "WhimShader LUT")
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            data.Validate();
            var texture = new Texture2D(data.resolution, 1, TextureFormat.RGBAFloat, false, true)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 0
            };
            try { Update(texture, data); }
            catch
            {
                DestroyOwned(texture);
                throw;
            }
            return texture;
        }

        /// <summary>Reuses a readable LUT of the same size, with no shader recompilation.</summary>
        public static void Update(Texture2D texture, WhimLutData data)
        {
            if (texture == null) throw new ArgumentNullException(nameof(texture));
            if (data == null) throw new ArgumentNullException(nameof(data));
            data.Validate();
            if (texture.width != data.resolution || texture.height != 1 ||
                texture.format != TextureFormat.RGBAFloat || !texture.isReadable || texture.mipmapCount != 1)
                throw new ArgumentException("Expected a readable, single-mip RGBAFloat LUT of the requested size.", nameof(texture));

            // The texture is linear data. Gradient colors are authored by a linear/HDR editor field.
            var pixels = texture.GetPixelData<Color>(0);
            float denominator = data.resolution - 1;
            for (int i = 0; i < data.resolution; ++i)
            {
                float t = i / denominator;
                if (data.kind == WhimLutKind.Gradient)
                    pixels[i] = data.gradient.Evaluate(t);
                else
                {
                    float value = data.curve.Evaluate(t);
                    pixels[i] = new Color(value, value, value, 1);
                }
            }
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.Apply(false, false);
        }

        public static void Bind(Material material, int propertyId, Texture2D texture)
        {
            if (material == null) throw new ArgumentNullException(nameof(material));
            if (texture == null) throw new ArgumentNullException(nameof(texture));
            if (!material.HasProperty(propertyId))
                throw new ArgumentException("Material does not have the LUT property.", nameof(propertyId));
            material.SetTexture(propertyId, texture);
        }

        public static void Bind(Material material, string propertyName, Texture2D texture)
        {
            if (string.IsNullOrEmpty(propertyName)) throw new ArgumentException("A property name is required.", nameof(propertyName));
            Bind(material, Shader.PropertyToID(propertyName), texture);
            material.SetVector(Shader.PropertyToID(propertyName + "_TexelSize"), TexelSize(texture));
        }

        public static void Bind(MaterialPropertyBlock block, int propertyId, Texture2D texture)
        {
            if (block == null) throw new ArgumentNullException(nameof(block));
            if (texture == null) throw new ArgumentNullException(nameof(texture));
            block.SetTexture(propertyId, texture);
        }

        public static void Bind(MaterialPropertyBlock block, string propertyName, Texture2D texture)
        {
            if (string.IsNullOrEmpty(propertyName)) throw new ArgumentException("A property name is required.", nameof(propertyName));
            Bind(block, Shader.PropertyToID(propertyName), texture);
            block.SetVector(Shader.PropertyToID(propertyName + "_TexelSize"), TexelSize(texture));
        }

        static Vector4 TexelSize(Texture2D texture) => new Vector4(1f / texture.width, 1f / texture.height, texture.width, texture.height);

        public static void DestroyOwned(Texture2D texture)
        {
            if (texture == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(texture);
            else UnityEngine.Object.DestroyImmediate(texture);
        }
    }
}
