# 3D BuilderCrap icons

71 original SVG assets, including the application mark. No Microsoft artwork,
icon font, downloaded icon library, or runtime package is used.

The UI artwork uses a **24 × 24 viewBox**, **1.6-unit rounded strokes**, `fill="none"`
and `stroke="currentColor"`. Keep geometry inside the canvas, including strokes.
Use a recognizable silhouette and retain the command's text label. The app mark
uses the existing blue accent, white cube edges and a small warm seam.

`catalog.json` lists every asset and its category. Appearance assets are reserved
for later paint/material tools; they do not add those features to the application.

## Desktop use

The SVGs are embedded in `Builder.dll`. `SvgIcon` reads their path geometry once
and draws vectors with WPF, inheriting the control's foreground for normal,
disabled, hover and primary-button states. The supported artwork contract is
root-level `path` and `title` elements, explicit absolute path commands, and
`none`, `currentColor` or literal brush colors. It is not an arbitrary SVG importer.

```xml
<Button local:Icon.Key="save" Content="_Save" Click="SaveClick"/>
<local:SvgIcon Key="cube" Width="24" Height="24"/>
```

`Icon.Stacked` places ribbon icons above their labels; other commands use an
inline 16-pixel icon. Content, access keys, tooltips, automation names and handlers
remain ordinary WPF controls. Icons do not intercept input or add keyboard stops.

From the repository root:

```powershell
python scripts/build_icon_gallery.py
.\scripts\Build-AppIcon.ps1
```

The gallery is a self-contained local HTML preview of every SVG at 32 and 16 px.
The PowerShell script regenerates `../BuilderCrap.ico` directly from `app-mark.svg`
at 16, 20, 24, 32, 40, 48, 64, 128 and 256 pixels for Windows. Rebuild after editing
the assets. Standalone SVGs can also be used directly by future web adapters.
