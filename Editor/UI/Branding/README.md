# WhimShader branding

Original orange node-and-connection artwork, authored as SVG geometry. No Unity
icon, AI image, font, or external image is used. Transparent RGBA PNGs are provided
at 16, 32 and 64 px. Geometry uses a 16 px grid with 4 px nodes and 1.5 px wires.

Use `DCFApixels.WhimShader.WhimShaderBranding.Icon` for the 32 px texture;
`Icon16` and `Icon64` provide exact-size alternatives. `IconAssetPath`,
`Icon16AssetPath`, `Icon64AssetPath` and `VectorSourceAssetPath` are public package
paths. The loader uses only `AssetDatabase.LoadAssetAtPath<Texture2D>` and caches
successful loads. It does not set window titles, asset icons, or importer state.

PNG import settings: GUI texture, sRGB, alpha transparency, clamp, bilinear,
uncompressed, no mipmaps. No SVG importer dependency is needed by the API.

To reproduce PNGs from the SVG using Python 3 with no third-party dependencies:

```text
python WhimShader~/Editor/UI/Branding/Source~/render_icon.py
```

The renderer supports only the SVG primitive subset used by this mark. Keep
`Source~` ignored by Unity. The parent owns importer/window integration.
