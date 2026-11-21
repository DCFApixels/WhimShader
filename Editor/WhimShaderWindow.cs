using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.Experimental.GraphView;
using UnityEditor.Graphing;
using UnityEditor.Graphing.Util;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Drawing;
using UnityEditor.ShaderGraph.Serialization;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;
using Toolbar = UnityEditor.UIElements.Toolbar;

namespace DCFApixels.WhimShader
{
    public sealed partial class WhimShaderWindow : EditorWindow
    {
        internal const string PackagePath = "Packages/com.dcfapixels.whimshader";
        [SerializeField] string assetPath;
        [SerializeField] GraphObject graphObject;
        [SerializeField] ShaderGraphWorkspaceLayout layout;
        [SerializeField] bool sourceExpanded;
        GraphEditorView editor;
        MessageManager messages;
        ShaderGraphPanelWorkspace workspace;
        WhimShaderParametersPanel parameterPanel;
        TextField source;
        Label status;
        double nextUpdate;
        bool rebuilding;
        internal GraphData Graph => graphObject?.graph;

        [MenuItem("Window/WhimShader")]
        public static void OpenWindow() => GetWindow<WhimShaderWindow>("WhimShader").Show();

        [MenuItem("Assets/Create/WhimShader/Shader Graph", priority = 208)]
        public static void CreateGraphAsset()
        {
            string path = EditorUtility.SaveFilePanelInProject("Create Shader Graph", "New Shader", "whimshader", "Choose a graph file.");
            if (string.IsNullOrEmpty(path)) return;
            var graph = WhimShaderURP.CreateGraph();
            try { WriteAtomic(path, MultiJson.Serialize(graph)); }
            finally { DestroyImmediate(graph.owner); }
            AssetDatabase.ImportAsset(path);
            Open(path);
        }

        [MenuItem("Assets/Create/WhimShader/Sub Graph", priority = 209)]
        public static void CreateSubGraphAsset()
        {
            string path = EditorUtility.SaveFilePanelInProject("Create Sub Graph", "New Sub Graph", "shadersubgraph", "Choose a subgraph file.");
            if (string.IsNullOrEmpty(path)) return;
            var graph = new GraphData { isSubGraph = true, messageManager = new MessageManager() };
            var owner = CreateInstance<GraphObject>(); owner.hideFlags = HideFlags.HideAndDontSave; owner.graph = graph;
            var output = new SubGraphOutputNode();
            graph.AddNode(output);
            graph.outputNode = output;
            output.AddSlot(new Vector4MaterialSlot(0, "Out", "Out", SlotType.Input, Vector4.zero));
            graph.AddNode(new WhimShaderParameterLayoutNode());
            try { WriteAtomic(path, MultiJson.Serialize(graph)); }
            finally { DestroyImmediate(owner); }
            AssetDatabase.ImportAsset(path);
            Open(path);
        }

        [OnOpenAsset(-1)]
        static bool OnOpenAsset(EntityId instanceID, int line)
        {
            string path = AssetDatabase.GetAssetPath(EditorUtility.EntityIdToObject(instanceID));
            if (!IsWhimGraph(path)) return false;
            Open(path); return true;
        }

        static bool IsWhimGraph(string path) => path.EndsWith(".whimshader", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".shadersubgraph", StringComparison.OrdinalIgnoreCase) && File.Exists(path) &&
            File.ReadAllText(path).Contains("DCFApixels.WhimShader.WhimShaderParameterLayoutNode");

        public static WhimShaderWindow Open(string path)
        {
            foreach (var existing in Resources.FindObjectsOfTypeAll<WhimShaderWindow>())
                if (existing.assetPath == path) { existing.Show(); existing.Focus(); return existing; }
            var window = CreateInstance<WhimShaderWindow>();
            window.assetPath = path;
            window.titleContent = new GUIContent("WhimShader — " + Path.GetFileNameWithoutExtension(path), WhimShaderBranding.Icon);
            window.minSize = new Vector2(680, 380);
            window.Show();
            window.LoadGraph();
            window.CreateGUI();
            return window;
        }

        void OnEnable()
        {
            titleContent = new GUIContent(string.IsNullOrEmpty(assetPath) ? "WhimShader" : "WhimShader — " + Path.GetFileNameWithoutExtension(assetPath), WhimShaderBranding.Icon);
            Undo.undoRedoPerformed += OnUndoRedo;
            if (Graph == null && !string.IsNullOrEmpty(assetPath)) LoadGraph();
            saveChangesMessage = "Save changes to the shader graph?";
        }

        void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
            DisposeView();
        }

        void OnDestroy()
        {
            if (graphObject != null) DestroyImmediate(graphObject);
        }

        void LoadGraph()
        {
            if (string.IsNullOrEmpty(assetPath) || !File.Exists(assetPath)) return;
            DisposeView();
            if (graphObject != null) DestroyImmediate(graphObject);
            messages = new MessageManager();
            graphObject = CreateInstance<GraphObject>();
            graphObject.hideFlags = HideFlags.HideAndDontSave;
            graphObject.graph = new GraphData
            {
                assetGuid = AssetDatabase.AssetPathToGUID(assetPath),
                isSubGraph = assetPath.EndsWith(".shadersubgraph", StringComparison.OrdinalIgnoreCase),
                messageManager = messages
            };
            MultiJson.Deserialize(Graph, File.ReadAllText(assetPath));
            Graph.OnEnable(); Graph.ValidateGraph();
        }

        void DisposeView()
        {
            DisposePreviews();
            parameterPanel?.Dispose(); parameterPanel = null;
            catalogProvider?.Dispose(); catalogProvider = null;
            workspace?.Dispose(); workspace = null;
            editor?.Dispose(); editor = null;
            if (Graph != null) WhimLutGraphProperties.ReleasePreview(Graph);
        }

        public void CreateGUI()
        {
            if (rebuilding) return;
            rebuilding = true;
            try
            {
                DisposeView(); rootVisualElement.Clear();
                if (Graph != null)
                {
                    var upgraded = WhimShaderFunctions.UpgradeLegacy(Graph);
                    if (!ReferenceEquals(upgraded, Graph)) { Graph.OnDisable(); graphObject.graph = upgraded; }
                }
                rootVisualElement.AddToClassList("whimshader-shader-graph");
                rootVisualElement.AddToClassList("whimshader-theme");
                rootVisualElement.AddToClassList("whimshader-native");
                rootVisualElement.EnableInClassList("whimshader-shader-graph--light", !EditorGUIUtility.isProSkin);
                foreach (string file in new[] { "Workspace/ShaderGraphWorkspace.uss", "WhimShaderWindow.uss", "WhimShaderNative.uss", "WhimShaderHost.uss" })
                {
                    var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(PackagePath + "/Editor/UI/" + file);
                    if (sheet != null) rootVisualElement.styleSheets.Add(sheet);
                }
                if (Graph == null)
                {
                    rootVisualElement.Add(new Button(CreateGraphAsset) { text = "Create Shader Graph" });
                    rootVisualElement.Add(new Button(() =>
                    {
                        string path = EditorUtility.OpenFilePanel("Open Shader Graph", "Assets", "whimshader");
                        if (!string.IsNullOrEmpty(path)) Open(FileUtil.GetProjectRelativePath(path));
                    }) { text = "Open Shader Graph" });
                    return;
                }
                messages = Graph.messageManager ?? new MessageManager();
                Graph.messageManager = messages;
                var toolbar = new Toolbar();
                toolbar.Add(new ToolbarButton(SaveGraph) { text = "Save" });
                toolbar.Add(new ToolbarButton(Compile) { text = "Compile" });
                toolbar.Add(new ToolbarButton(ExportShader) { text = "Export ShaderLab" });
                toolbar.Add(new ToolbarButton(CreateMaterial) { text = "Create Material" });
                toolbar.Add(new ToolbarButton(FrameGraph) { text = "Frame All" });
                toolbar.Add(new ToolbarButton(() => editor?.graphView.FrameSelection()) { text = "Frame Selection" });
                rootVisualElement.Add(toolbar);
                var body = new VisualElement { name = "whimshader-body" };
                body.style.flexGrow = 1; body.style.overflow = Overflow.Hidden;
                rootVisualElement.Add(body);
                editor = new GraphEditorView(this, Graph, messages, Path.GetFileNameWithoutExtension(assetPath));
                editor.style.flexGrow = 1;
                editor.saveRequested = SaveGraph;
                editor.saveAsRequested = SaveAs;
                editor.convertToSubgraphRequested = ToSubGraph;
                editor.isCheckedOut = () => true;
                editor.showInProjectRequested = () => EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(assetPath));
                var nativeToolbar = editor.Children().FirstOrDefault();
                if (nativeToolbar is IMGUIContainer) nativeToolbar.RemoveFromHierarchy();
                body.Add(editor);
                editor.graphView.RegisterCallback<MouseDownEvent>(evt =>
                {
                    if (evt.button != 0 || evt.clickCount != 2 || evt.target is not VisualElement target) return;
                    var view = target.GetFirstAncestorOfType<MaterialNodeView>();
                    if (view?.node is not SubGraphNode subgraph || subgraph.asset == null) return;
                    string path = AssetDatabase.GetAssetPath(subgraph.asset);
                    if (!IsWhimGraph(path)) return;
                    evt.StopImmediatePropagation(); Open(path);
                }, TrickleDown.TrickleDown);
                var colorMode = new PopupField<string>(editor.colorManager.providerNames.ToList(), editor.colorManager.activeIndex);
                colorMode.RegisterValueChangedCallback(evt => editor.UserViewSettingsChangeCheck(colorMode.index));
                toolbar.Add(colorMode);
                editor.blackboardController.blackboard.RemoveFromHierarchy();
                parameterPanel = new WhimShaderParametersPanel(Graph, editor.graphView, OnSettingsChanged);
                DockContent(editor.inspectorView);
                parameterPanel.SetSelectedNodeInspector(editor.inspectorView);
                var inspector = parameterPanel.Inspector;
                var catalog = BuildCatalog();
                var settings = WhimShaderURP.BuildSettings(Graph, OnSettingsChanged);
                if (layout == null)
                {
                    string saved = EditorPrefs.GetString("WhimShader.Workspace", "");
                    layout = string.IsNullOrEmpty(saved) ? new ShaderGraphWorkspaceLayout() : JsonUtility.FromJson<ShaderGraphWorkspaceLayout>(saved);
                    layout ??= new ShaderGraphWorkspaceLayout();
                    layout.Validate();
                }
                workspace = new ShaderGraphPanelWorkspace(body, new Dictionary<ShaderGraphPanelKind, VisualElement>
                {
                    [ShaderGraphPanelKind.Parameters] = parameterPanel,
                    [ShaderGraphPanelKind.Nodes] = catalog,
                    [ShaderGraphPanelKind.Inspector] = inspector,
                    [ShaderGraphPanelKind.ShaderSettings] = settings
                }, layout, () => EditorPrefs.SetString("WhimShader.Workspace", JsonUtility.ToJson(layout)));
                var code = new Foldout { text = "Generated HLSL", value = sourceExpanded };
                code.AddToClassList("whimshader-generated-source");
                status = new Label("Ready"); status.AddToClassList("whimshader-compile-status");
                code.Q<Toggle>().Add(status);
                source = new TextField { multiline = true, isReadOnly = true };
                source.style.height = 200;
                code.Add(source);
                code.RegisterValueChangedCallback(evt => { sourceExpanded = evt.newValue; if (evt.newValue) GenerateSource(); });
                rootVisualElement.Add(code);
                editor.viewSettings.isBlackboardVisible = false;
                editor.viewSettings.isInspectorVisible = true;
                editor.viewSettings.isPreviewVisible = true;
                editor.previewManager.UpdateMasterPreview(ModificationScope.Topological);
                InitializePreviews();
                HideMetadataNodes();
                Graph.ClearChanges();
                editor.schedule.Execute(FrameGraph).StartingIn(100);
            }
            finally { rebuilding = false; }
        }

        static void DockContent(VisualElement element)
        {
            element.RemoveFromHierarchy();
            element.style.position = Position.Relative;
            element.style.left = element.style.top = element.style.right = element.style.bottom = StyleKeyword.Auto;
            element.style.width = element.style.height = StyleKeyword.Auto;
            element.style.minWidth = 0; element.style.minHeight = 0;
            element.style.flexGrow = 1;
            element.AddToClassList("whimshader-docked-native");
        }

        void FrameGraph()
        {
            if (editor == null) return;
            var view = editor.graphView;
            Rect available = view.contentRect;
            foreach (var panel in rootVisualElement.Query<ShaderGraphFloatingPanel>().ToList())
            {
                if (!panel.visible || panel.resolvedStyle.display == DisplayStyle.None) continue;
                Rect bounds = view.WorldToLocal(panel.worldBound);
                if (bounds.width < view.contentRect.width * .5f)
                {
                    if (bounds.xMin <= 2) available.xMin = Mathf.Max(available.xMin, bounds.xMax);
                    if (bounds.xMax >= view.contentRect.width - 2) available.xMax = Mathf.Min(available.xMax, bounds.xMin);
                }
            }
            available.xMin += 24; available.xMax -= 24; available.yMin += 24; available.yMax -= 24;
            if (available.width < 160 || available.height < 160) { view.FrameAll(); return; }
            Rect? total = null;
            foreach (var node in view.nodes.ToList())
            {
                if (node.userData is WhimShaderParameterLayoutNode or WhimShaderNodePresentationMetadata ||
                    node.resolvedStyle.display == DisplayStyle.None || node.GetFirstAncestorOfType<ContextView>() != null) continue;
                Rect bounds = node.GetPosition();
                if (node.Query<PortInputView>().ToList().Any(input => input.visible)) bounds.xMin -= 236;
                if (total.HasValue)
                {
                    Rect previous = total.Value;
                    total = Rect.MinMaxRect(Mathf.Min(previous.xMin, bounds.xMin), Mathf.Min(previous.yMin, bounds.yMin),
                        Mathf.Max(previous.xMax, bounds.xMax), Mathf.Max(previous.yMax, bounds.yMax));
                }
                else total = bounds;
            }
            if (!total.HasValue) return;
            Rect rectangle = total.Value;
            float scale = Mathf.Clamp(Mathf.Min(available.width / Mathf.Max(1, rectangle.width),
                available.height / Mathf.Max(1, rectangle.height)), view.minScale, Mathf.Min(1, view.maxScale));
            view.UpdateViewTransform(available.center - rectangle.center * scale, Vector3.one * scale);
        }

        void OnSettingsChanged()
        {
            nodePalette?.Invalidate();
            Graph.ValidateGraph();
            graphObject.isDirty = true;
            foreach (var node in Graph.GetNodes<AbstractMaterialNode>()) node.Dirty(ModificationScope.Graph);
            editor?.previewManager.UpdateMasterPreview(ModificationScope.Topological);
        }

        void OnUndoRedo() { nextUpdate = 0; }

        void Update()
        {
            if (editor == null || Graph == null || rebuilding || EditorApplication.timeSinceStartup < nextUpdate) return;
            nextUpdate = EditorApplication.timeSinceStartup + .05;
            bool undo = graphObject.wasUndoRedoPerformed;
            if (undo)
            {
                editor.HandleGraphChanges(true); Graph.ClearChanges();
                graphObject.HandleUndoRedo();
                editor.inspectorView.RefreshInspectables();
                parameterPanel?.Refresh();
            }
            if (graphObject.isDirty || undo)
            {
                hasUnsavedChanges = true;
                graphObject.isDirty = false;
                editor.previewManager.UpdateMasterPreview(ModificationScope.Topological);
                if (status != null) status.text = "Modified";
            }
            editor.HandleGraphChanges(undo);
            nodePalette?.RefreshIfNeeded();
            UpdatePreviews();
            HideMetadataNodes();
            Graph.ClearChanges();
        }

        void HideMetadataNodes()
        {
            foreach (var view in editor.graphView.graphElements)
                if (view.userData is WhimShaderParameterLayoutNode or WhimShaderNodePresentationMetadata)
                    view.style.display = DisplayStyle.None;
        }

        string GenerateSource()
        {
            Graph.ValidateGraph();
            if (Graph.isSubGraph) return "// Sub Graph functions are generated by their parent graph.";
            var generator = new Generator(Graph, Graph.outputNode, GenerationMode.ForReals,
                "WhimShader/" + Path.GetFileNameWithoutExtension(assetPath), humanReadable: true);
            string text = generator.generatedShader;
            source?.SetValueWithoutNotify(text);
            return text;
        }

        void Compile()
        {
            try
            {
                GenerateSource(); SaveGraph();
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(assetPath);
                bool invalid = messages.HasSeverity(_ => true, UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error);
                var diagnostics = shader == null ? Array.Empty<UnityEditor.ShaderMessage>() : ShaderUtil.GetShaderMessages(shader);
                invalid |= diagnostics.Any(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error);
                if (status != null) status.text = invalid ? "Compilation failed — inspect node diagnostics" : "Compiled successfully";
            }
            catch (Exception error) { if (status != null) status.text = error.Message; Debug.LogException(error); }
        }

        public override void SaveChanges() { SaveGraph(); base.SaveChanges(); }
        public override void DiscardChanges() { LoadGraph(); CreateGUI(); base.DiscardChanges(); }

        void SaveGraph()
        {
            if (Graph == null || string.IsNullOrEmpty(assetPath)) return;
            WriteAtomic(assetPath, MultiJson.Serialize(Graph));
            AssetDatabase.ImportAsset(assetPath);
            graphObject.isDirty = false; hasUnsavedChanges = false;
        }

        void SaveAs()
        {
            string extension = Graph.isSubGraph ? "shadersubgraph" : "whimshader";
            string path = EditorUtility.SaveFilePanelInProject("Save Graph As", Path.GetFileNameWithoutExtension(assetPath), extension, "Choose a graph file.");
            if (string.IsNullOrEmpty(path)) return;
            WriteAtomic(path, MultiJson.Serialize(Graph)); AssetDatabase.ImportAsset(path); Open(path);
        }

        void ExportShader()
        {
            if (Graph.isSubGraph) return;
            string path = EditorUtility.SaveFilePanelInProject("Export ShaderLab", Path.GetFileNameWithoutExtension(assetPath), "shader", "Choose a ShaderLab file.");
            if (string.IsNullOrEmpty(path)) return;
            Graph.ValidateGraph();
            var generator = new Generator(Graph, Graph.outputNode, GenerationMode.ForReals,
                "WhimShader/" + Path.GetFileNameWithoutExtension(path), humanReadable: true);
            WriteAtomic(path, generator.generatedShader); AssetDatabase.ImportAsset(path);
            var shaderImporter = AssetImporter.GetAtPath(path) as ShaderImporter;
            if (shaderImporter != null)
            {
                // Includes texture constants and promoted subgraph resources as well as
                // Texture2D/3D/array/cubemap properties, exactly as the native generator collected them.
                var textures = generator.allGeneratedShaders.First().assignedTextures
                    .GroupBy(t => t.name).ToDictionary(g => g.Key,
                        g => EditorUtility.EntityIdToObject(g.Last().textureId) as Texture);
                foreach (var lut in WhimLutGraphProperties.ExportDefaults(Graph, Path.GetDirectoryName(path).Replace('\\', '/')))
                    textures[lut.PropertyName] = lut.Texture;
                shaderImporter.SetDefaultTextures(textures.Keys.ToArray(), textures.Values.ToArray());
                shaderImporter.SaveAndReimport();
            }
            WhimShaderMaterialMetadata.ExportMetadata(Graph, path);
        }

        void CreateMaterial()
        {
            if (Graph.isSubGraph) return;
            SaveGraph();
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(assetPath);
            if (shader == null) throw new InvalidOperationException("The graph has no compiled shader.");
            string path = AssetDatabase.GenerateUniqueAssetPath(Path.ChangeExtension(assetPath, ".mat"));
            var material = new Material(shader);
            foreach (var target in Graph.activeTargets) target.ProcessPreviewMaterial(material);
            AssetDatabase.CreateAsset(material, path);
            Selection.activeObject = material;
        }

        internal static void WriteAtomic(string path, string contents)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, contents, new System.Text.UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
