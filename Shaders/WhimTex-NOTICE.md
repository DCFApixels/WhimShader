# WhimTex preview source notice

Source: the installed `Packages/com.dcfapixels.whimtex/src` snapshot, 2026-11-21.
Copyright (c) 2026 Mikhail. These copied sources are distributed under MIT below.

- `Editor/HeightPreviewRenderer.cs` -> `Editor/URP/HeightPreviewRenderer.cs`:
  namespace/package paths changed; WhimTex-only profiling wrappers replaced by
  their underlying Unity RenderTexture allocation calls. Rendering math unchanged.
- `Shaders/HeightPreview.compute` -> `Shaders/WhimHeightPreview.compute`: byte-for-byte copy.
- `Shaders/HeightPreviewDisplay.shader` -> `Shaders/WhimHeightPreviewDisplay.shader`:
  only the ShaderLab shader name changes.
- `Shaders/HdrColor.cginc` and its `Shaders/HueGradient.cginc` include: byte-for-byte copies.
  Height/channel display uses SpriteEncode; the unused hue helper needs no LUT binding.
- `Shaders/DisplayChannels.shader` -> channel-display pass in `Shaders/WhimPreview.shader`:
  same masking/exposure/clamping/color math; optional encodeGamma gate follows the
  node versus final-preview convention. A separate raw-copy pass preserves actual
  shader RGBA without display conversion and can fit a square Height image.

No runtime reference to the WhimTex package, document model, VM or profiling code
is introduced. UnityCG.cginc is supplied by the installed Unity Editor.

# MIT License

Copyright (c) 2026 Mikhail

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
