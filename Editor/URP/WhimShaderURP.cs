using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Graphing;
using UnityEditor.Graphing.Util;
using UnityEditor.Rendering.Universal.ShaderGraph;
using UnityEditor.ShaderGraph;
using UnityEngine;
using UnityEngine.UIElements;

namespace DCFApixels.WhimShader
{
    public static class WhimShaderURP
    {
        public const string SupportedVersion = "17.7.0";

        internal static void RequireVersion()
        {
            foreach (var assembly in new[] { typeof(UniversalTarget).Assembly, typeof(GraphData).Assembly })
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(assembly);
                if (package == null || package.version != SupportedVersion)
                    throw new InvalidOperationException("WhimShader v1 requires Shader Graph and URP " +
                        SupportedVersion + "; found " + (package?.name ?? assembly.GetName().Name) + " " + package?.version + ".");
            }
        }

        internal static GraphData CreateGraph()
        {
            RequireVersion();
            var target = new UniversalTarget { customEditorGUI = WhimShaderMaterialGUI.CustomEditorName };
            if (!target.TrySetActiveSubTarget(typeof(WhimSurfaceSubTarget)))
                throw new InvalidOperationException("Whim Surface subtarget was not discovered in the URP Editor assembly.");
            var graph = new GraphData { messageManager = new MessageManager() };
            var owner = ScriptableObject.CreateInstance<GraphObject>();
            owner.hideFlags = HideFlags.HideAndDontSave;
            owner.graph = graph;
            graph.AddContexts();
            graph.vertexContext.position = Vector2.zero;
            graph.fragmentContext.position = new Vector2(0, 300);
            graph.InitializeOutputs(new Target[] { target }, WhimSurfaceSubTarget.InitialBlocks);
            var pbr = new UnityPBRNode();
            var drawState = pbr.drawState;
            drawState.position = new Rect(-420, 100, 0, 0);
            pbr.drawState = drawState;
            graph.AddNode(pbr);
            var color = graph.fragmentContext.blocks.Select(b => b.value)
                .First(b => b.descriptor == WhimSurfaceBlocks.Color);
            graph.Connect(pbr.GetSlotReference(UnityPBRNode.ColorSlot), color.GetSlotReference(0));
            graph.ValidateGraph();
            return graph;
        }

        internal static VisualElement BuildSettings(GraphData graph, Action changed)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            var target = graph.activeTargets.OfType<UniversalTarget>().FirstOrDefault();
            if (target?.activeSubTarget is not WhimSurfaceSubTarget subTarget)
                return new HelpBox("This graph does not use the Whim Surface target.", HelpBoxMessageType.Info);
            var root = new VisualElement();
            root.AddToClassList("whimshader-urp-settings");
            Rebuild();
            return root;

            void Rebuild()
            {
                root.Clear();
                var context = new TargetPropertyGUIContext(graph.ValidateGraph);
                subTarget.GetPropertiesGUI(ref context, Change, Undo);
                root.Add(context);
            }
            void Undo(string label)
            {
                if (graph.owner == null)
                    throw new InvalidOperationException("Assign a GraphObject owner before editing WhimShader settings.");
                graph.owner.RegisterCompleteObjectUndo(label);
            }
            void Change()
            {
                target.customEditorGUI = WhimShaderMaterialGUI.CustomEditorName;
                graph.UpdateActiveBlocks(graph.GetActiveBlocksForAllActiveTargets());
                graph.ValidateGraph();
                foreach (var node in graph.GetNodes<AbstractMaterialNode>()) node.Dirty(ModificationScope.Graph);
                changed?.Invoke();
                // Native fields conditionally appear when surface options change.
                root.schedule.Execute(Rebuild);
            }
        }
    }
}
