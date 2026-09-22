# Avatar Editor Review Implementation

## 1. Vector control rebinding — fixed

Standalone Vector2 bindings capture sticks; button bindings and composite parts retain button capture. Prompts describe the required input. Capture waits for buttons to release and sticks to return to neutral before restoring navigation, including conflict confirmation. Suppressed capture events still update device state so release checks can observe the captured stick.

Loading saved bindings removes known incompatible overrides from standalone Vector2 bindings and saves the repaired overrides, recovering face-button assignments persisted by the previous remapper.

## 2. First-person tattoo coordinates — fixed

First-person presentation caches regions that retain the full-avatar region's anatomical center and dimensions. Extracted surface points are renormalized into those bounds. The destination region's frame maps the source anatomical axes into the first-person skeleton's bone space, including companion skeletons with different bone roll.

Surface resolution uses a tighter distance limit in first person, based on half the projector's proportional depth, to hide placements on removed geometry instead of relocating them toward the crop boundary. Source dimensions also preserve tattoo size. This reconstruction uses existing region data; avatar assets do not need regeneration.

## 3. Reset View controller navigation — fixed

Normal editor navigation includes both columns through the editor root. Reset View is reachable alongside the other controls. The discard dialog retains its own navigation scope, and placement mode retains disabled navigation.

## 4. Preview placement state on reopening — fixed

Opening explicitly resets preview placement alongside the controller state, restoring head look before showing the avatar after a forced close.

## Findings left unchanged

None. All four findings describe credible defects in the current code.

## Visual validation

- Rebind Orbit and Move to another stick. Face buttons should be ignored; navigation should resume only after releasing controls, including when accepting or cancelling a conflict. Restart with an old incompatible override and confirm the default stick binding returns.
- Place tattoos on the upper arm, forearm, and near the first-person crop edge. Compare their anatomical position and size between the preview, third person, and first person. Tattoos on removed geometry should disappear instead of shifting toward the elbow. Repeat with a companion representation if one is configured.
- Using only a controller, reach Reset View after rotating and zooming. Confirm placement mode and the discard dialog retain their intended navigation restrictions.
- Force the editor closed during placement through damage, station range, or a lobby transition. Reopen it and confirm pointer-driven head look works immediately.
