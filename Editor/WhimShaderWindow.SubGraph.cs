// Adapted from Unity Shader Graph 17.7 MaterialGraphEditWindow.ToSubGraph.
// Copyright Unity Technologies. Unity Companion License; see ThirdPartyNotices.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Graphing;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Drawing;
using UnityEditor.ShaderGraph.Internal;
using UnityEditor.ShaderGraph.Serialization;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using Edge = UnityEditor.Experimental.GraphView.Edge;
using StickyNote = UnityEditor.ShaderGraph.Drawing.StickyNote;
namespace DCFApixels.WhimShader
{
    public sealed partial class WhimShaderWindow
    {
        const string k_PrevSubGraphPathKey = "WhimShader.ConvertToSubGraph.Path";
        const string k_PrevSubGraphPathDefaultValue = "?";
        public void ToSubGraph()
        {
            var graphView = editor.graphView;

            string path;
            string sessionStateResult = SessionState.GetString(k_PrevSubGraphPathKey, k_PrevSubGraphPathDefaultValue);
            string pathToOriginSG = Path.GetDirectoryName(AssetDatabase.GUIDToAssetPath(AssetDatabase.AssetPathToGUID(assetPath)));

            if (!sessionStateResult.Equals(k_PrevSubGraphPathDefaultValue))
            {
                path = sessionStateResult;
            }
            else
            {
                path = pathToOriginSG;
            }

            path = EditorUtility.SaveFilePanelInProject("Save Sub Graph", "New Shader Sub Graph", ShaderSubGraphImporter.Extension, "", path);
            path = path.Replace(Application.dataPath, "Assets");

            // Friendly warning that the user is generating a subgraph that would overwrite the one they are currently working on.
            if (AssetDatabase.AssetPathToGUID(path) == AssetDatabase.AssetPathToGUID(assetPath))
            {
                if (!EditorUtility.DisplayDialog("Overwrite Current Subgraph", "Do you want to overwrite this Sub Graph that you are currently working on? You cannot undo this operation.", "Yes", "Cancel"))
                {
                    path = "";
                }
            }

            if (path.Length == 0)
                return;

            var nodes = graphView.selection.OfType<IShaderNodeView>().Where(x => !(x.node is PropertyNode || x.node is SubGraphOutputNode)).Select(x => x.node).Where(x => x.allowedInSubGraph).ToArray();

            // Convert To Subgraph could create recursive reference loops if the target path already exists
            // Let's check for that here
            if (!string.IsNullOrEmpty(path))
            {
                if (GraphUtil.CheckForRecursiveDependencyOnPendingSave(path, nodes.OfType<SubGraphNode>(), "Convert To SubGraph"))
                    return;
            }

            graphObject.RegisterCompleteObjectUndo("Convert To Subgraph");

            var bounds = Rect.MinMaxRect(float.PositiveInfinity, float.PositiveInfinity, float.NegativeInfinity, float.NegativeInfinity);
            foreach (var node in nodes)
            {
                var center = node.drawState.position.center;
                bounds = Rect.MinMaxRect(
                    Mathf.Min(bounds.xMin, center.x),
                    Mathf.Min(bounds.yMin, center.y),
                    Mathf.Max(bounds.xMax, center.x),
                    Mathf.Max(bounds.yMax, center.y));
            }
            var middle = bounds.center;
            bounds.center = Vector2.zero;

            // Collect graph inputs
            var graphInputs = graphView.selection.OfType<SGBlackboardField>().Select(x => x.userData as ShaderInput);
            var categories = graphView.selection.OfType<SGBlackboardCategory>().Select(x => x.userData as CategoryData);

            // Collect the property nodes and get the corresponding properties
            var propertyNodes = graphView.selection.OfType<IShaderNodeView>().Where(x => (x.node is PropertyNode)).Select(x => ((PropertyNode)x.node).property);
            var metaProperties = graphView.graph.properties.Where(x => propertyNodes.Contains(x));

            // Collect the keyword nodes and get the corresponding keywords
            var keywordNodes = graphView.selection.OfType<IShaderNodeView>().Where(x => (x.node is KeywordNode)).Select(x => ((KeywordNode)x.node).keyword);
            var dropdownNodes = graphView.selection.OfType<IShaderNodeView>().Where(x => (x.node is DropdownNode)).Select(x => ((DropdownNode)x.node).dropdown);

            var metaKeywords = graphView.graph.keywords.Where(x => keywordNodes.Contains(x));
            var metaDropdowns = graphView.graph.dropdowns.Where(x => dropdownNodes.Contains(x));

            var copyPasteGraph = new CopyPasteGraph(graphView.selection.OfType<ShaderGroup>().Select(x => x.userData),
                nodes,
                graphView.selection.OfType<Edge>().Select(x => x.userData as UnityEditor.Graphing.Edge),
                graphInputs,
                categories,
                metaProperties,
                metaKeywords,
                metaDropdowns,
                graphView.selection.OfType<StickyNote>().Select(x => x.userData),
                true,
                false);

            // why do we serialize and deserialize only to make copies of everything in the steps below?
            // is this just to clear out all non-serialized data?
            var deserialized = CopyPasteGraph.FromJson(MultiJson.Serialize(copyPasteGraph), graphView.graph);
            if (deserialized == null)
                return;

            var subGraph = new GraphData { isSubGraph = true, path = "Sub Graphs" };
            var subGraphOutputNode = new SubGraphOutputNode();
            {
                var drawState = subGraphOutputNode.drawState;
                drawState.position = new Rect(new Vector2(bounds.xMax + 200f, 0f), drawState.position.size);
                subGraphOutputNode.drawState = drawState;
            }
            subGraph.AddNode(subGraphOutputNode);
            subGraph.outputNode = subGraphOutputNode;

            var inputSlots = new List<MaterialSlot>();
            subGraphOutputNode.GetInputSlots(inputSlots);
            if (inputSlots.Count == 0)
                subGraphOutputNode.AddSlot(ConcreteSlotValueType.Vector4);

            // Always copy deserialized keyword inputs
            foreach (ShaderKeyword keyword in deserialized.metaKeywords)
            {
                var copiedInput = (ShaderKeyword)subGraph.AddCopyOfShaderInput(keyword);

                // Update the keyword nodes that depends on the copied keyword
                var dependentKeywordNodes = deserialized.GetNodes<KeywordNode>().Where(x => x.keyword == keyword);
                foreach (var node in dependentKeywordNodes)
                {
                    node.owner = graphView.graph;
                    node.keyword = copiedInput;
                }
            }

            // Always copy deserialized dropdown inputs
            foreach (ShaderDropdown dropdown in deserialized.metaDropdowns)
            {
                var copiedInput = (ShaderDropdown)subGraph.AddCopyOfShaderInput(dropdown);

                // Update the dropdown nodes that depends on the copied dropdown
                var dependentDropdownNodes = deserialized.GetNodes<DropdownNode>().Where(x => x.dropdown == dropdown);
                foreach (var node in dependentDropdownNodes)
                {
                    node.owner = graphView.graph;
                    node.dropdown = copiedInput;
                }
            }

            foreach (GroupData groupData in deserialized.groups)
            {
                subGraph.CreateGroup(groupData);
            }

            foreach (var node in deserialized.GetNodes<AbstractMaterialNode>())
            {
                var drawState = node.drawState;
                drawState.position = new Rect(drawState.position.position - middle, drawState.position.size);
                node.drawState = drawState;

                // Checking if the group guid is also being copied.
                // If not then nullify that guid
                if (node.group != null && !subGraph.groups.Contains(node.group))
                {
                    node.group = null;
                }

                subGraph.AddNode(node);
            }

            foreach (var note in deserialized.stickyNotes)
            {
                if (note.group != null && !subGraph.groups.Contains(note.group))
                {
                    note.group = null;
                }

                subGraph.AddStickyNote(note);
            }

            // figure out what needs remapping
            var externalOutputSlots = new List<UnityEditor.Graphing.Edge>();
            var externalInputSlots = new List<UnityEditor.Graphing.Edge>();
            var passthroughSlots = new List<UnityEditor.Graphing.Edge>();
            foreach (var edge in deserialized.edges)
            {
                var outputSlot = edge.outputSlot;
                var inputSlot = edge.inputSlot;

                var outputSlotExistsInSubgraph = subGraph.ContainsNode(outputSlot.node);
                var inputSlotExistsInSubgraph = subGraph.ContainsNode(inputSlot.node);

                // pasting nice internal links!
                if (outputSlotExistsInSubgraph && inputSlotExistsInSubgraph)
                {
                    subGraph.Connect(outputSlot, inputSlot);
                }
                // one edge needs to go to outside world
                else if (outputSlotExistsInSubgraph)
                {
                    externalInputSlots.Add(edge);
                }
                else if (inputSlotExistsInSubgraph)
                {
                    externalOutputSlots.Add(edge);
                }
                else
                {
                    externalInputSlots.Add(edge);
                    externalOutputSlots.Add(edge);
                    passthroughSlots.Add(edge);
                }
            }

            // Find the unique edges coming INTO the graph
            var uniqueIncomingEdges = externalOutputSlots.GroupBy(
                edge => edge.outputSlot,
                edge => edge,
                (key, edges) => new { slotRef = key, edges = edges.ToList() });

            var externalInputNeedingConnection = new List<KeyValuePair<IEdge, AbstractShaderProperty>>();

            var amountOfProps = uniqueIncomingEdges.Count();
            const int height = 40;
            const int subtractHeight = 20;
            var propPos = new Vector2(0, -((amountOfProps / 2) + height) - subtractHeight);

            var passthroughSlotRefLookup = new Dictionary<SlotReference, SlotReference>();

            var passedInProperties = new Dictionary<AbstractShaderProperty, AbstractShaderProperty>();
            foreach (var group in uniqueIncomingEdges)
            {
                var sr = group.slotRef;
                var fromNode = sr.node;
                var fromSlot = sr.slot;

                var materialGraph = graphObject.graph;
                var fromProperty = fromNode is PropertyNode fromPropertyNode
                    ? materialGraph.properties.FirstOrDefault(p => p == fromPropertyNode.property)
                    : null;

                AbstractShaderProperty prop;
                if (fromProperty != null && passedInProperties.TryGetValue(fromProperty, out prop))
                {
                }
                else
                {
                    switch (fromSlot.concreteValueType)
                    {
                        case ConcreteSlotValueType.Texture2D:
                            prop = new Texture2DShaderProperty();
                            break;
                        case ConcreteSlotValueType.Texture2DArray:
                            prop = new Texture2DArrayShaderProperty();
                            break;
                        case ConcreteSlotValueType.Texture3D:
                            prop = new Texture3DShaderProperty();
                            break;
                        case ConcreteSlotValueType.Cubemap:
                            prop = new CubemapShaderProperty();
                            break;
                        case ConcreteSlotValueType.Vector4:
                            prop = new Vector4ShaderProperty();
                            break;
                        case ConcreteSlotValueType.Vector3:
                            prop = new Vector3ShaderProperty();
                            break;
                        case ConcreteSlotValueType.Vector2:
                            prop = new Vector2ShaderProperty();
                            break;
                        case ConcreteSlotValueType.Vector1:
                            prop = new Vector1ShaderProperty();
                            break;
                        case ConcreteSlotValueType.Boolean:
                            prop = new BooleanShaderProperty();
                            break;
                        case ConcreteSlotValueType.Matrix2:
                            prop = new Matrix2ShaderProperty();
                            break;
                        case ConcreteSlotValueType.Matrix3:
                            prop = new Matrix3ShaderProperty();
                            break;
                        case ConcreteSlotValueType.Matrix4:
                            prop = new Matrix4ShaderProperty();
                            break;
                        case ConcreteSlotValueType.SamplerState:
                            prop = new SamplerStateShaderProperty();
                            break;
                        case ConcreteSlotValueType.Gradient:
                            prop = new GradientShaderProperty();
                            break;
                        case ConcreteSlotValueType.VirtualTexture:
                            prop = new VirtualTextureShaderProperty()
                            {
                                // also copy the VT settings over from the original property (if there is one)
                                value = (fromProperty as VirtualTextureShaderProperty)?.value ?? new SerializableVirtualTexture()
                            };
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }

                    // A LUT is a texture in HLSL, but its authored keys and material drawer
                    // remain part of the parameter contract when extracting a Sub Graph.
                    if (fromProperty is WhimLutShaderProperty)
                        prop = (AbstractShaderProperty)fromProperty.Copy();

                    var propName = fromProperty != null
                        ? fromProperty.displayName
                        : fromSlot.concreteValueType.ToString();
                    prop.SetDisplayNameAndSanitizeForGraph(subGraph, propName);

                    if (fromProperty?.useCustomSlotLabel ?? false)
                    {
                        prop.useCustomSlotLabel = true;
                        prop.customSlotLabel = fromProperty.customSlotLabel;
                    }

                    subGraph.AddGraphInput(prop);
                    if (fromProperty != null)
                    {
                        passedInProperties.Add(fromProperty, prop);
                    }
                }

                var propNode = new PropertyNode();
                {
                    var drawState = propNode.drawState;
                    drawState.position = new Rect(new Vector2(bounds.xMin - 300f, 0f) + propPos,
                        drawState.position.size);
                    propPos += new Vector2(0, height);
                    propNode.drawState = drawState;
                }
                subGraph.AddNode(propNode);
                propNode.property = prop;


                Vector2 avg = Vector2.zero;
                foreach (var edge in group.edges)
                {
                    if (passthroughSlots.Contains(edge) && !passthroughSlotRefLookup.ContainsKey(sr))
                    {
                        passthroughSlotRefLookup.Add(sr, new SlotReference(propNode, PropertyNode.OutputSlotId));
                    }
                    else
                    {
                        subGraph.Connect(
                            new SlotReference(propNode, PropertyNode.OutputSlotId),
                            edge.inputSlot);

                        int i;
                        var inputs = edge.inputSlot.node.GetInputSlots<MaterialSlot>().ToList();

                        for (i = 0; i < inputs.Count; ++i)
                        {
                            if (inputs[i].slotReference.slotId == edge.inputSlot.slotId)
                            {
                                break;
                            }
                        }
                        avg += new Vector2(edge.inputSlot.node.drawState.position.xMin, edge.inputSlot.node.drawState.position.center.y + 30f * i);
                    }
                    //we collapse input properties so dont add edges that are already being added
                    if (!externalInputNeedingConnection.Any(x => x.Key.outputSlot.slot == edge.outputSlot.slot && x.Value == prop))
                    {
                        externalInputNeedingConnection.Add(new KeyValuePair<IEdge, AbstractShaderProperty>(edge, prop));
                    }
                }
                avg /= group.edges.Count;
                var pos = avg - new Vector2(150f, 0f);
                propNode.drawState = new DrawState()
                {
                    position = new Rect(pos, propNode.drawState.position.size),
                    expanded = propNode.drawState.expanded
                };
            }

            var uniqueOutgoingEdges = externalInputSlots.GroupBy(
                edge => edge.outputSlot,
                edge => edge,
                (key, edges) => new { slot = key, edges = edges.ToList() });

            var externalOutputsNeedingConnection = new List<KeyValuePair<IEdge, IEdge>>();
            foreach (var group in uniqueOutgoingEdges)
            {
                var outputNode = subGraph.outputNode as SubGraphOutputNode;

                AbstractMaterialNode node = group.edges[0].outputSlot.node;
                MaterialSlot slot = node.FindSlot<MaterialSlot>(group.edges[0].outputSlot.slotId);
                var slotId = outputNode.AddSlot(slot.concreteValueType);

                var inputSlotRef = new SlotReference(outputNode, slotId);

                foreach (var edge in group.edges)
                {
                    var newEdge = subGraph.Connect(passthroughSlotRefLookup.TryGetValue(edge.outputSlot, out SlotReference remap) ? remap : edge.outputSlot, inputSlotRef);
                    externalOutputsNeedingConnection.Add(new KeyValuePair<IEdge, IEdge>(edge, newEdge));
                }
            }

            if (WhimShaderParameterLayoutNode.Find(subGraph) == null)
                subGraph.AddNode(new WhimShaderParameterLayoutNode { layout = WhimShaderParameterLayout.FromGraph(subGraph) });
            if (FileUtilities.WriteShaderGraphToDisk(path, subGraph) != null)
                AssetDatabase.ImportAsset(path);

            // Store path for next time
            if (!pathToOriginSG.Equals(Path.GetDirectoryName(path)))
            {
                SessionState.SetString(k_PrevSubGraphPathKey, Path.GetDirectoryName(path));
            }
            else
            {
                // Or continue to make it so that next time it will open up in the converted-from SG's directory
                SessionState.EraseString(k_PrevSubGraphPathKey);
            }

            var loadedSubGraph = AssetDatabase.LoadAssetAtPath(path, typeof(SubGraphAsset)) as SubGraphAsset;
            if (loadedSubGraph == null)
                return;

            var subGraphNode = new SubGraphNode();
            var ds = subGraphNode.drawState;
            ds.position = new Rect(middle - new Vector2(100f, 150f), Vector2.zero);
            subGraphNode.drawState = ds;

            // Add the subgraph into the group if the nodes was all in the same group group
            var firstNode = copyPasteGraph.GetNodes<AbstractMaterialNode>().FirstOrDefault();
            if (firstNode != null && copyPasteGraph.GetNodes<AbstractMaterialNode>().All(x => x.group == firstNode.group))
            {
                subGraphNode.group = firstNode.group;
            }

            subGraphNode.asset = loadedSubGraph;
            graphObject.graph.AddNode(subGraphNode);

            foreach (var edgeMap in externalInputNeedingConnection)
            {
                graphObject.graph.Connect(edgeMap.Key.outputSlot, new SlotReference(subGraphNode, edgeMap.Value.guid.GetHashCode()));
            }

            foreach (var edgeMap in externalOutputsNeedingConnection)
            {
                graphObject.graph.Connect(new SlotReference(subGraphNode, edgeMap.Value.inputSlot.slotId), edgeMap.Key.inputSlot);
            }

            graphObject.graph.RemoveElements(
                graphView.selection.OfType<IShaderNodeView>().Select(x => x.node).Where(x => !(x is PropertyNode || x is SubGraphOutputNode) && x.allowedInSubGraph).ToArray(),
                new IEdge[] { },
                new GroupData[] { },
                graphView.selection.OfType<StickyNote>().Select(x => x.userData).ToArray());

            List<GraphElement> moved = new List<GraphElement>();
            foreach (var nodeView in graphView.selection.OfType<IShaderNodeView>())
            {
                var node = nodeView.node;
                if (graphView.graph.removedNodes.Contains(node) || node is SubGraphOutputNode)
                {
                    continue;
                }

                var edges = graphView.graph.GetEdges(node);
                int numEdges = edges.Count();
                if (numEdges == 0)
                {
                    graphView.graph.RemoveNode(node);
                }
                else if (numEdges == 1 && edges.First().inputSlot.node != node) //its an output edge
                {
                    var edge = edges.First();
                    int i;
                    var inputs = edge.inputSlot.node.GetInputSlots<MaterialSlot>().ToList();
                    for (i = 0; i < inputs.Count; ++i)
                    {
                        if (inputs[i].slotReference.slotId == edge.inputSlot.slotId)
                        {
                            break;
                        }
                    }
                    node.drawState = new DrawState()
                    {
                        position = new Rect(new Vector2(edge.inputSlot.node.drawState.position.xMin, edge.inputSlot.node.drawState.position.center.y) - new Vector2(150f, -30f * i), node.drawState.position.size),
                        expanded = node.drawState.expanded
                    };
                    (nodeView as GraphElement).SetPosition(node.drawState.position);
                }
            }
            graphObject.graph.ValidateGraph();
        }

    }
}
