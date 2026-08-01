# VrcComponentCopier
Accidently deleted some components from my avatar so I had AI write a simple script to copy missing components while skipping existing ones

// VRCComponentCopier.cs
// Unity Editor tool for copying missing GameObjects/Components from one VRChat
// avatar hierarchy to another, matched by relative hierarchy PATH (not name),
// with a manual review step for remapping object references on copied components.
//
// INSTALL:
//   Drop this file anywhere inside an "Editor" folder in your Assets
//   (e.g. Assets/Editor/VRCComponentCopier.cs).
//
// USE:
//   Tools > VRC Component Copier
//
// WORKFLOW:
//   1. Assign Source Avatar and Target Avatar (both must be in the same open scene).
//   2. Click "Scan". A list of missing objects / missing components is built,
//      matched by hierarchy path (e.g. Armature/Hips/Spine/Chest) so the
//      avatar root names never matter.
//   3. Check the objects/components you want copied. Missing objects are
//      pre-checked; use "Select All" / "Select None" to bulk-toggle.
//   4. Click "Copy Selected". Objects are created (transform, layer, tag,
//      active state preserved) and components are added with their values
//      copied over.
//   5. You'll land on the "Review References" screen. Any field on a copied
//      component that pointed at something inside the SOURCE avatar (bones,
//      other components, child objects - e.g. SkinnedMeshRenderer.bones,
//      PhysBone root transforms, Constraint sources) is listed here with a
//      proposed replacement found automatically by matching hierarchy path
//      on the TARGET avatar. Confirm, fix, or clear each one, then
//      "Apply Remaps".
//
// NOTES / LIMITATIONS:
//   - Both avatars must exist in the currently open scene (this only reads
//     scene objects, not prefab assets on disk).
//   - Objects are matched by their path from the avatar root. If two avatars'
//     rigs/hierarchies are structured very differently, fewer things will
//     auto-match — you can still fix each reference manually in the review step.
//   - Sibling objects that share the same name under the same parent will
//     collapse to the same path; rename duplicates for a clean diff.
//   - VRC.Core.PipelineManager is intentionally never copied — it stores the
//     avatar's unique Blueprint ID and copying it can corrupt both avatars'
//     upload identity.
//   - Everything is wrapped in a single Undo group, so Ctrl+Z undoes a full
//     "Copy Selected" or "Apply Remaps" operation.
//   - Remember to save the scene afterwards.
