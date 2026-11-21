using System;
using UnityEngine;

namespace DCFApixels.WhimShader
{
    /// <summary>Persistent editable data paired with its baked texture subasset.</summary>
    public sealed class WhimLutAsset : ScriptableObject
    {
        [SerializeField] WhimLutData m_Data = new WhimLutData();
        [SerializeField] Texture2D m_Texture;
        [SerializeField] Material m_OwnerMaterial;
        [SerializeField] string m_PropertyName;

        public WhimLutData Data => m_Data.Clone();
        public Texture2D Texture => m_Texture;
        public Material OwnerMaterial => m_OwnerMaterial;
        public string PropertyName => m_PropertyName;

        public void SetData(WhimLutData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            data.Validate();
            m_Data = data.Clone();
        }

        public void SetBinding(Texture2D texture, Material ownerMaterial, string propertyName)
        {
            m_Texture = texture;
            m_OwnerMaterial = ownerMaterial;
            m_PropertyName = propertyName;
        }

        /// <summary>Updates the existing texture; resolution changes require a replacement texture.</summary>
        public void Rebake()
        {
            WhimLutTexture.Update(m_Texture, m_Data);
        }
    }
}
