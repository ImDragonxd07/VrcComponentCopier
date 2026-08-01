Accidently deleted some components from my avatar so I had AI write a simple script to copy missing components while skipping existing ones

# VRC Component Copier
 
Unity Editor tool that copies missing GameObjects and Components from one
VRChat avatar to another, matched by hierarchy **path** — so the avatar
names/roots don't matter.
 
## Install

[![Add to VCC](https://img.shields.io/badge/Add%20to%20VCC-blue?style=for-the-badge)](vcc://vpm/addRepo?url=https:/ImDragonxd07/vpm-repo/refs/heads/main/index.json)
[![Add to BVCC](https://img.shields.io/badge/Add%20to%20BVCC-blue?style=for-the-badge)](bvcc://addrepo?url=https://ImDragonxd07/vpm-repo/refs/heads/main/index.json)

Put the package folder here:
 
```
Packages/com.dragonxd07.vrccomponentcopier/
├── package.json
└── Editor/
    ├── VRCComponentCopier.cs
    └── VRCComponentCopier.Editor.asmdef
```
 
## Use
 
1. Open **Tools > VRC Component Copier**.
2. Assign **Source Avatar** and **Target Avatar** (both must be in the open scene) → **Scan**.
3. Check the objects/components you want. Missing objects are pre-checked.
   Use the filter box, group foldouts, or Select All / None to manage a long list.
4. Click **Copy Selected**. Objects are created and components are added with
   their values copied over.
5. On the **Review References** screen, confirm or fix the proposed
   replacement for any field that pointed at something on the source avatar
   (bones, PhysBone roots, Constraint sources, etc.), then **Apply Remaps**.
6. Save your scene.
## Notes
 
- Both avatars must be in the currently open scene (not prefab assets on disk).
- Objects are matched by relative path from the avatar root — sibling objects
  with duplicate names under the same parent will collapse to one path.
- `VRC.Core.PipelineManager` is never copied (it holds each avatar's unique
  upload ID).
- Everything runs in one Undo group — Ctrl+Z reverses a full Copy or Apply Remaps step.
 
