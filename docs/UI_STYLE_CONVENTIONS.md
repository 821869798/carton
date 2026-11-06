# Shared UI conventions

Carton keeps its existing Avalonia/Fluent2 theme and lightweight controls. Common appearance belongs in shared resources rather than page-specific copies.

## Action icons

`src/carton.GUI/Resources/Icons.axaml` contains common action geometry. It is merged into application resources by `App.axaml`. Use `PathIcon Data="{StaticResource SearchIconGeometry}"` (or another named geometry); keep command, tooltip, accessible name, size and foreground on the consuming control.

Shared actions: delete, search, close, refresh, back, check, save, format, add, previous/next match, copy/copy-all, edit. Search icons in input fields and editor toolbars use the same geometry. Refreshing connectivity uses the refresh action glyph; the decorative connectivity lightning glyph remains a category symbol.

Brand logos and domain/category illustrations need not look like action buttons. They are not replaced with generic action glyphs merely to remove every literal path from page files.

## Buttons and fields

`App.axaml` owns `Button.iconButton` (compact 30px row/header action) and `Button.pageAction` (36px toolbar action). Both use a 6px radius, no outline, shared foreground and hover/pressed backgrounds. Disabled/focus behavior continues to come from the Fluent2 control theme. A locally smaller 24px action can retain its dimensions while inheriting the same shape and states.

Profile editor save-button emphasis remains local and uses accent resources. Search-option chips remain toggle choices rather than ordinary action buttons. Do not flatten these roles into identical colors.

`TextBox.pageSearch` uses an 8px radius, vertical content centering and shared themed background/border. Shared search-glyph sizing/margins remain in `PathIcon.searchGlyph`.

## Cards and layout

Cards already use `CartonSettingsItemBackgroundBrush` and an 8px radius. Keep those resources for new cards; do not invent a darker home-page card color. Settings rows, profile rows and large content cards can retain different padding suited to their contents. Existing shadow/border distinctions were not redesigned in this pass.

No commands, navigation, data bindings or tooltip strings were changed by this consolidation. No icon package or SVG runtime dependency was added.

## Verification

The local visual probe `artifacts/unified-pages/probe` renders all six pages in both themes, then English/Chinese narrow layouts. It asserts common action radii and absence of outlines. The profile lifetime probe checks list recycling, action binding and keyboard navigation; the editor probe exercises its real styled view. Existing GUI tests are also run.

The consolidation passed all **402 existing GUI tests**, the real profile-list keyboard/binding/recycling/collection probe, and editor light/dark error-marker rendering. All six page render scenarios passed in light/dark, with English/Chinese narrow variants; representative dashboard/profile/log screenshots were also inspected. Shared button radius/outline assertions passed. These checks do not certify every hover or keyboard-focus animation pixel; those remain governed by the existing theme.

This pass deliberately addresses proven inconsistencies instead of recreating the entire interface. Other domain-specific glyphs and any future visual differences should be reviewed by meaning before being centralized.
