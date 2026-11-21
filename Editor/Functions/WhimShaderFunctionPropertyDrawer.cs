using System;
using System.Reflection;
using UnityEditor.Graphing;
using UnityEditor.ShaderGraph.Drawing;
using UnityEditor.ShaderGraph.Drawing.Inspector;
using UnityEditor.ShaderGraph.Drawing.Inspector.PropertyDrawers;
using UnityEngine;
using UnityEngine.UIElements;

namespace DCFApixels.WhimShader
{
    [SGPropertyDrawer(typeof(WhimShaderCustomFunctionNode))]
    internal sealed class WhimShaderFunctionPropertyDrawer : IPropertyDrawer, IGetNodePropertyDrawerPropertyData
    {
        Action dirty, refresh;
        public Action inspectorUpdateDelegate { get; set; }
        void IGetNodePropertyDrawerPropertyData.GetPropertyData(Action markDirty, Action updateViews)
        { dirty = markDirty; refresh = updateViews; }
        public VisualElement DrawProperty(PropertyInfo propertyInfo, object actualObject, InspectableAttribute attribute)
        {
            var sheet = new PropertySheet(PropertyDrawerUtils.CreateLabel("Custom Function", 0, FontStyle.Bold));
            PropertyDrawerUtils.AddDefaultNodeProperties(sheet, (WhimShaderCustomFunctionNode)actualObject, dirty, refresh);
            var label = new Label("Edit code and ports on the node."); label.style.whiteSpace = WhiteSpace.Normal; sheet.Add(label);
            return sheet;
        }
        void IPropertyDrawer.DisposePropertyDrawer() { }
    }
}
