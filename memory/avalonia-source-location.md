---
name: avalonia-source-location
description: Where the local Avalonia source checkout lives, for verifying Fluent template part names / resource keys
metadata:
  type: reference
---

The Avalonia source (matching the pinned 12.0.5 stack) is checked out locally at
`C:\Users\darcy\source\repos\_Experiments\Avalonia`.

Use it to verify Fluent control-theme part names and resource keys before writing `/template/`
style overrides — the NuGet package ships only compiled DLLs. Useful files:
`src/Avalonia.Themes.Fluent/Controls/*.xaml` (per-control templates) and
`src/Avalonia.Themes.Fluent/Accents/FluentControlResources.xaml` (resource aliases).

Verified keys used in the Sprocket shell: RadioButton ellipses are `OuterEllipse` /
`CheckOuterEllipse` (20px) / `CheckGlyph` (8px) with row height from `RadioButtonMinHeight` (32);
slider thumb from `SliderHorizontalThumbWidth`/`Height` (20) + `SliderThumbCornerRadius` (10);
input placeholder from `TextControlPlaceholderForeground`.
