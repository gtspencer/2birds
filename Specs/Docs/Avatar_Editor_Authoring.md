# Avatar editor authoring

## Hats and icons

Open **Two Birds > Hat Setup**. Select a source prefab, set its library name and default availability, and edit its fit position, rotation, and scale. **Process and capture icon** registers the hat and creates presentation content, converted material copies, and a transparent icon in stable-ID folders. Selecting a registered source loads its existing identity and settings. **Recapture icon** refreshes only its icon. Source-pack assets stay intact.

Hats use two nested fits: the avatar's **Head Fit**, then the hat definition's **Fit**. Both offsets apply independently. Positions use unscaled avatar-local units and inherit avatar scale once. Avatar settings expose **Icon**, **Unlocked By Default**, and **Recapture library icon**. **Save and Process Avatar** preserves these authored values and regenerates full-body and first-person tattoo regions.

## Tattoo artwork

1. Import supplied artwork with alpha from input and Clamp wrap mode. Carry shape and detail entirely in alpha; RGB is replaced by the chosen ink. Use a transparent background.
2. Create **Two Birds > Cosmetics > Tattoo**. Its nonzero 64-bit ID is assigned once; preserve it when renaming or replacing artwork.
3. Assign Artwork, Display Name, and a Sprite Icon. Artwork dimensions determine Aspect Ratio. Use Sprite import or a separate thumbnail asset.
4. Register it in `Assets/Game/Settings/Cosmetics/TattooCatalog.asset`. All registered designs are available. An empty catalog supports avatar and hat editing.

The catalog owns one shared decal material. Runtime instances clone it, assign Artwork and Ink, and set draw order from the equipped list. Do not create per-player material assets. Ink alpha is fixed; newer tattoos cover older ones.

## Body regions

Avatar Processor classifies triangles by summed skin influences. Unmapped geometry needs an explicit **Custom Regions** bone path and a unique uint key greater than the humanoid bone count. Preserve custom keys through reprocessing. Share a key across avatars only when the parts have the same meaning. Unmapped tails, ears, and accessories are excluded from placement.

Process after changing skinning, custom mappings, or First Person Source. First-person regions come from the actual processed arm meshes. Transfer changes the current draft and removes tattoos with no compatible destination surface. Failed first-person matches only hide that projector.

## Permanent grants

```csharp
SessionController.Instance.Unlocks.UnlockAvatar(avatarSettings.Id);
SessionController.Instance.Unlocks.UnlockHat(hatDefinition.Id);
```

Grants persist separately from appearance, refresh open libraries, and tolerate repeats. Default availability is an authoring setting. Remote rendering does not depend on the observer's unlocks.

## Persistent assignments

- `SessionRoot.prefab`: avatar registry, hat catalog, tattoo catalog, and AvatarEditor controller prefab.
- `AvatarEditor.prefab`: controller, panel/UIDocument, UXML and panel settings, local AvatarPresentation, preview camera, and lights.
- `AvatarPreview` layer: preview camera includes it; gameplay and scene cameras exclude it.
- `PC_Renderer.asset`: retain Forward+ and SSAO; Decal Renderer Feature uses Screen Space with Use Rendering Layers disabled.
- Tattoo catalog: `Assets/Game/Art/Cosmetics/Tattoos/Tattoo.mat` and its Decal Shader Graph with Angle Fade enabled.

Preview and local player cameras use renderer index zero, the PC renderer. The preview RenderTexture is owned and resized at runtime.

## World station

Add **AvatarEditorStation** to a fixed object with a non-trigger collider on a layer queried by PlayerInteraction. Put the component on the collider or an ancestor. Assign Surface to that collider and optionally provide a Tooltip Anchor. No Rigidbody, damage component, or NetworkObject is required.

The remappable Player/Interact action opens the editor independently for each client. The world continues. Damage, downing, or external movement beyond PickupRange saves and closes. Focus loss, Steam overlay, and controller disconnection retain the draft.

Appearance is stored in `AvatarAppearance.v1`. Apply and explicit forced interruptions save; Cancel discards. Rigid bone-attached projectors can distort near joints and spill onto nearby geometry.
