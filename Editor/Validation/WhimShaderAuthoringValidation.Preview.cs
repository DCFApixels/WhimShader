using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.ShaderGraph;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DCFApixels.WhimShader
{
    public static partial class WhimShaderAuthoringValidation
    {
        static Color[] ReadPNG(string file)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try { Check(texture.LoadImage(File.ReadAllBytes(file)), "PNG could not be read"); return texture.GetPixels(); }
            finally { Object.DestroyImmediate(texture); }
        }

        public static object Preview()
        {
            string path = "Assets/WhimShaderValidation/AgentPreview_" + Guid.NewGuid().ToString("N") + ".whimshader";
            var files = new List<string>(); var checks = new List<string>(); WhimShaderWindow window = null;
            JObject Render(string output, string mode = "Image", string channel = "RGBA", float time = 0)
            {
                var result = JObject.Parse(WhimShaderApi.Preview(path, "custom", output, 64, mode, channel, time));
                Check((bool)result["success"], result.ToString());
                string file = (string)result["imagePath"]; Check(Path.IsPathRooted(file) && File.Exists(file), "Missing absolute PNG");
                files.Add(file); Check(ReadPNG(file).Length == 64 * 64, "Incorrect preview size"); return result;
            }
            try
            {
                var request = JObject.Parse(@"{'op':'create','nodes':{
                  'custom':{'type':'Custom Function','expanded':false,'preview':false,
                    'function':{'code':'Color = float4(In, 0.5, 0.75, 0.2); Value = In;',
                      'ports':[{'name':'In','type':'Float','direction':'Input'},
                        {'name':'Color','type':'Float4','direction':'Output'},
                        {'name':'Value','type':'Float','direction':'Output'}]},'in':{'In':0.25}},
                  'clock':'Time'},'fragment':{'Color':'custom.Color'}}");
                request["path"] = path; Call(request);
                string before = File.ReadAllText(path), revision = (string)Inspect(path)["revision"];
                var color = Render("Color"); var colorPixel = ReadPNG((string)color["imagePath"])[32 * 64 + 32];
                Check(colorPixel.r < colorPixel.g && colorPixel.g < colorPixel.b && Math.Abs(colorPixel.a - .2f) < .01f, "Color/alpha output was lost: " + colorPixel);
                var value = Render("Value"); var valuePixel = ReadPNG((string)value["imagePath"])[32 * 64 + 32];
                Check(Math.Abs(valuePixel.r - valuePixel.g) < .005f && valuePixel.a > .99f, "Wrong selected scalar output");
                checks.Add("Closed graph: collapsed node with hidden preview renders selected scalar and RGBA outputs, preserving alpha");
                var alpha = Render("Color", channel: "A"); var alphaPixel = ReadPNG((string)alpha["imagePath"])[32 * 64 + 32];
                Check(Math.Abs(alphaPixel.r - alphaPixel.g) < .005f && Math.Abs(alphaPixel.g - alphaPixel.b) < .005f && alphaPixel.a > .99f, "Alpha display is not opaque grayscale");
                Render("Value", "Height"); Render("Color", "3D");
                checks.Add("Channel selection and Image / Height / 3D presentation render through the shared preview renderer");
                Check((string)Inspect(path)["revision"] == revision && File.ReadAllText(path) == before, "Preview modified the document");
                checks.Add("Preview leaves graph data, presentation settings and saved file unchanged");

                window = WhimShaderWindow.Open(path);
                var patch = Patch(path); patch["save"] = false; patch["nodes"] = JObject.Parse(@"{'custom':{'in':{'In':0.75}}}"); Call(patch);
                string liveRevision = (string)Inspect(path)["revision"];
                var live = Render("Color"); var livePixel = ReadPNG((string)live["imagePath"])[32 * 64 + 32];
                Check(livePixel.r > colorPixel.r + .15f && (string)live["revision"] == liveRevision && File.ReadAllText(path) == before, "Preview ignored unsaved input or saved changes");
                Check((string)Inspect(path)["revision"] == liveRevision, "Rendering changed live revision");
                checks.Add("Open graph preview uses unsaved edits without saving or changing the live revision");
                foreach (string output in new[] { "MissingOutput", "In" })
                    Check(!(bool)JObject.Parse(WhimShaderApi.Preview(path, "custom", output))["success"], "Invalid output accepted");
                Check(!(bool)JObject.Parse(WhimShaderApi.Preview(path, "custom", "Color", size: 9999))["success"], "Oversized image accepted");
                Check(!(bool)JObject.Parse(WhimShaderApi.Preview(path, "missing"))["success"], "Missing node accepted");
                checks.Add("Missing nodes, input ports, invalid outputs and oversized requests are rejected");
                var timeResult = JObject.Parse(WhimShaderApi.Preview(path, "clock", "Time", size: 32, mode: "Image", time: .25f));
                Check((bool)timeResult["success"], timeResult.ToString()); files.Add((string)timeResult["imagePath"]);
                var timePixel = ReadPNG(files.Last())[16 * 32 + 16];
                Check(Math.Abs(timePixel.r - valuePixel.r) < .01f, "Explicit animation time was not bound: " + timePixel + " / " + valuePixel);
                checks.Add("Animation time is explicit and reproducible");
                return new { success = true, checks };
            }
            finally
            {
                if (window != null) { window.DiscardChanges(); window.Close(); }
                if (File.Exists(path)) AssetDatabase.DeleteAsset(path);
                foreach (string file in files) if (File.Exists(file)) File.Delete(file);
            }
        }

        public static object CheckPreviewUI()
        {
            Check(uiWindow != null, "StartUI first");
            var node = uiWindow.Graph.GetNodes<GradientNoiseNode>().Single();
            Check(uiWindow.PreviewService.TryGetPreview(node, out _, out var displayed, out var error, out bool pending, out _) && !pending && error == null,
                "Wait for UI preview to become ready: " + error);
            string revision = WhimShaderApi.Revision(uiWindow.Graph);
            var result = JObject.Parse(WhimShaderApi.Preview(uiPath, "noise", size: displayed.width));
            Check((bool)result["success"], result.ToString());
            var expected = new Texture2D(displayed.width, displayed.height, TextureFormat.RGBA32, false, true);
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = displayed; expected.ReadPixels(new Rect(0, 0, displayed.width, displayed.height), 0, 0); expected.Apply();
                var a = expected.GetPixels(); var b = ReadPNG((string)result["imagePath"]);
                float maxError = 0;
                for (int i = 0; i < a.Length; i++)
                    maxError = Mathf.Max(maxError, Mathf.Abs(a[i].r - b[i].r), Mathf.Abs(a[i].g - b[i].g), Mathf.Abs(a[i].b - b[i].b), Mathf.Abs(a[i].a - b[i].a));
                Check(maxError <= 1f / 255f, "PNG differs from the node's UI preview: " + maxError);
                Check(WhimShaderApi.Revision(uiWindow.Graph) == revision, "PNG request modified the UI graph");
                return new { success = true, maxError, imagePath = (string)result["imagePath"], checks = new[] { "Requested PNG matches the displayed node preview pixel for pixel (8-bit tolerance)" } };
            }
            finally { RenderTexture.active = previous; Object.DestroyImmediate(expected); }
        }
    }
}
