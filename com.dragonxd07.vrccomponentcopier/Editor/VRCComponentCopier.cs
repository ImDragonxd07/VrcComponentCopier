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

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace VRCComponentCopierTool
{
    public class VRCComponentCopierWindow : EditorWindow
    {
        private enum Phase { Select, Review }

        private class ComponentInfo
        {
            public Component sourceComponent;
            public Type type;
            public int typeIndex;
            public bool include;
        }

        private class NodeInfo
        {
            public string path;
            public Transform sourceTransform;
            public Transform targetTransform; // null => missing on target
            public int depth;
            public bool includeObject;
            public bool foldout = true;
            public List<ComponentInfo> components = new List<ComponentInfo>();
        }

        private class RemapEntry
        {
            public Component targetComponent;
            public string componentLabel;
            public string propertyPath;
            public string displayLabel;
            public UnityEngine.Object sourceValue;
            public UnityEngine.Object proposedValue;
            public bool apply;
        }

        private static readonly HashSet<string> ExcludedTypeFullNames = new HashSet<string>
        {
            "VRC.Core.PipelineManager",
        };

        private Phase phase = Phase.Select;

        private GameObject sourceAvatar;
        private GameObject targetAvatar;

        private Dictionary<string, Transform> sourcePathMap = new Dictionary<string, Transform>();
        private Dictionary<string, Transform> targetPathMap = new Dictionary<string, Transform>();
        private List<NodeInfo> nodeList = new List<NodeInfo>();
        private List<RemapEntry> pendingRemaps = new List<RemapEntry>();

        private Vector2 scrollPos;
        private bool hasScanned;
        private string searchFilter = "";
        private Dictionary<string, bool> groupFoldouts = new Dictionary<string, bool>();

        [MenuItem("Tools/VRC Component Copier")]
        private static void ShowWindow()
        {
            var win = GetWindow<VRCComponentCopierWindow>("VRC Component Copier");
            win.minSize = new Vector2(480, 400);
        }

        private void OnGUI()
        {
            switch (phase)
            {
                case Phase.Select:
                    DrawSelectPhase();
                    break;
                case Phase.Review:
                    DrawReviewPhase();
                    break;
            }
        }

        // ---------------------------------------------------------------
        // PHASE 1: SELECT
        // ---------------------------------------------------------------

        private void DrawSelectPhase()
        {
            EditorGUILayout.LabelField("VRC Avatar Component Copier", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            EditorGUI.BeginChangeCheck();
            sourceAvatar = (GameObject)EditorGUILayout.ObjectField("Source Avatar", sourceAvatar, typeof(GameObject), true);
            targetAvatar = (GameObject)EditorGUILayout.ObjectField("Target Avatar", targetAvatar, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck())
            {
                hasScanned = false;
            }

            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(sourceAvatar == null || targetAvatar == null))
            {
                if (GUILayout.Button("Scan", GUILayout.Height(28)))
                {
                    Rescan();
                }
            }

            if (!hasScanned)
            {
                EditorGUILayout.HelpBox("Assign both avatars and click Scan.", MessageType.Info);
                return;
            }

            if (nodeList.Count == 0)
            {
                EditorGUILayout.HelpBox("No missing objects or components found. Target already has everything Source has.", MessageType.Info);
                return;
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select All")) SetAll(true);
                if (GUILayout.Button("Select None")) SetAll(false);
                if (GUILayout.Button("Missing Objects Only")) SelectMissingObjectsOnly();
                if (GUILayout.Button("Expand All", GUILayout.Width(80))) SetAllGroupsExpanded(true);
                if (GUILayout.Button("Collapse All", GUILayout.Width(85))) SetAllGroupsExpanded(false);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Filter", GUILayout.Width(40));
                searchFilter = EditorGUILayout.TextField(searchFilter);
                if (GUILayout.Button("\u00d7", GUILayout.Width(22))) searchFilter = "";
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                DrawLegendSwatch(new Color(1f, 0.55f, 0.35f, 0.9f), "Missing object");
                GUILayout.Space(12);
                DrawLegendSwatch(new Color(0.35f, 0.65f, 1f, 0.9f), "Existing object, missing component(s)");
            }

            EditorGUILayout.Space();
            scrollPos = EditorGUILayout.BeginScrollView(scrollPos, GUILayout.ExpandHeight(true));

            foreach (var group in GetGroupedNodes())
            {
                DrawGroup(group.name, group.nodes);
            }

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();
            int totalObjects = nodeList.Count(n => n.targetTransform == null && n.includeObject);
            int totalComponents = nodeList
                .Where(n => n_IsNodeApplicable(n))
                .SelectMany(n => n.components)
                .Count(c => c.include);
            EditorGUILayout.LabelField($"Selected: {totalObjects} object(s), {totalComponents} component(s)");

            using (new EditorGUI.DisabledScope(totalObjects == 0 && totalComponents == 0))
            {
                if (GUILayout.Button("Copy Selected \u2192", GUILayout.Height(32)))
                {
                    ApplySelectedCopies();
                }
            }
        }

        // helper local-ish method (kept as instance method for C# 7 compat)
        private bool n_IsNodeApplicable(NodeInfo n)
        {
            return n.targetTransform != null || n.includeObject;
        }

        private struct NodeGroup
        {
            public string name;
            public List<NodeInfo> nodes;
        }

        // Groups the flat node list by top-level branch (e.g. "Armature", "Hat", "(Root)")
        // so a big rig doesn't render as one giant indented wall of text.
        private List<NodeGroup> GetGroupedNodes()
        {
            var result = new List<NodeGroup>();
            var indexByGroup = new Dictionary<string, int>();

            foreach (var node in nodeList)
            {
                if (!string.IsNullOrEmpty(searchFilter) &&
                    node.path.IndexOf(searchFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                string g = string.IsNullOrEmpty(node.path) ? "(Root)" : node.path.Split('/')[0];
                if (!indexByGroup.TryGetValue(g, out int idx))
                {
                    idx = result.Count;
                    indexByGroup[g] = idx;
                    result.Add(new NodeGroup { name = g, nodes = new List<NodeInfo>() });
                }
                result[idx].nodes.Add(node);
            }

            return result;
        }

        private void SetAllGroupsExpanded(bool expanded)
        {
            var keys = groupFoldouts.Keys.ToList();
            foreach (var k in keys) groupFoldouts[k] = expanded;
            // also cover groups not yet seen this pass
            foreach (var g in GetGroupedNodes()) groupFoldouts[g.name] = expanded;
        }

        private void DrawLegendSwatch(Color color, string label)
        {
            Rect r = GUILayoutUtility.GetRect(12, 12, GUILayout.Width(12));
            EditorGUI.DrawRect(r, color);
            GUILayout.Space(2);
            EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
        }

        private void DrawGroup(string groupName, List<NodeInfo> nodes)
        {
            if (!groupFoldouts.TryGetValue(groupName, out bool expanded)) expanded = true;

            int missingObjCount = nodes.Count(n => n.targetTransform == null);
            int missingCompCount = nodes.Sum(n => n.components.Count);
            string header = $"{groupName}    \u2014    {missingObjCount} missing object(s), {missingCompCount} missing component(s)";

            expanded = EditorGUILayout.Foldout(expanded, header, true, EditorStyles.foldoutHeader);
            groupFoldouts[groupName] = expanded;

            if (!expanded) return;

            using (new EditorGUILayout.VerticalScope())
            {
                foreach (var node in nodes)
                {
                    DrawNode(node);
                }
            }
            EditorGUILayout.Space();
        }

        private void DrawNode(NodeInfo node)
        {
            bool isMissingObject = node.targetTransform == null;

            string shortName;
            string parentContext;
            if (string.IsNullOrEmpty(node.path))
            {
                shortName = "(Avatar Root)";
                parentContext = null;
            }
            else
            {
                int idx = node.path.LastIndexOf('/');
                if (idx < 0) { shortName = node.path; parentContext = null; }
                else { shortName = node.path.Substring(idx + 1); parentContext = node.path.Substring(0, idx); }
            }

            Color prevColor = GUI.backgroundColor;
            GUI.backgroundColor = isMissingObject
                ? new Color(1f, 0.55f, 0.35f, 0.35f)   // warm tint: missing object
                : new Color(0.35f, 0.65f, 1f, 0.28f);  // cool tint: existing object, missing component(s)

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUI.backgroundColor = prevColor;
                int indent = Mathf.Min(node.depth, 6) * 10;

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(indent);
                    using (new EditorGUILayout.VerticalScope())
                    {
                        if (!string.IsNullOrEmpty(parentContext))
                        {
                            EditorGUILayout.LabelField(parentContext, EditorStyles.miniLabel);
                        }

                        if (isMissingObject)
                        {
                            node.includeObject = EditorGUILayout.ToggleLeft(
                                new GUIContent("  " + shortName + "   [missing]", "Missing on target \u2014 will be created"),
                                node.includeObject, EditorStyles.boldLabel);
                        }
                        else
                        {
                            EditorGUILayout.LabelField(
                                new GUIContent("  " + shortName, "Exists on target \u2014 missing some components"),
                                EditorStyles.boldLabel);
                        }
                    }
                }

                if (node.components.Count > 0)
                {
                    bool enabledScope = !isMissingObject || node.includeObject;
                    using (new EditorGUI.DisabledScope(!enabledScope))
                    {
                        foreach (var comp in node.components)
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                GUILayout.Space(indent + 20);
                                string compLabel = comp.type.Name + (comp.typeIndex > 0 ? $" (#{comp.typeIndex + 1})" : "");
                                comp.include = EditorGUILayout.ToggleLeft(compLabel, comp.include);
                            }
                        }
                    }
                }
                else if (isMissingObject)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(indent + 20);
                        EditorGUILayout.LabelField("(structural only \u2014 no extra components)", EditorStyles.miniLabel);
                    }
                }
            }

            GUI.backgroundColor = prevColor;
        }

        private void SetAll(bool value)
        {
            foreach (var node in nodeList)
            {
                if (node.targetTransform == null) node.includeObject = value;
                foreach (var c in node.components) c.include = value;
            }
        }

        private void SelectMissingObjectsOnly()
        {
            foreach (var node in nodeList)
            {
                bool missing = node.targetTransform == null;
                if (missing) node.includeObject = true;
                foreach (var c in node.components) c.include = missing;
            }
        }

        // ---------------------------------------------------------------
        // SCAN
        // ---------------------------------------------------------------

        private void Rescan()
        {
            nodeList.Clear();
            pendingRemaps.Clear();
            hasScanned = true;

            if (sourceAvatar == null || targetAvatar == null) return;

            sourcePathMap = new Dictionary<string, Transform>();
            targetPathMap = new Dictionary<string, Transform>();
            var srcOrder = new List<string>();
            var tgtOrder = new List<string>();

            BuildMap(sourceAvatar.transform, sourcePathMap, srcOrder);
            BuildMap(targetAvatar.transform, targetPathMap, tgtOrder);

            foreach (var path in srcOrder)
            {
                Transform srcT = sourcePathMap[path];
                targetPathMap.TryGetValue(path, out Transform tgtT);

                var node = new NodeInfo
                {
                    path = path,
                    sourceTransform = srcT,
                    targetTransform = tgtT,
                    depth = string.IsNullOrEmpty(path) ? 0 : path.Count(c => c == '/') + 1,
                    includeObject = tgtT == null
                };

                node.components = BuildMissingComponentList(srcT, tgtT);

                if (tgtT == null || node.components.Count > 0)
                {
                    nodeList.Add(node);
                }
            }
        }

        private static List<ComponentInfo> BuildMissingComponentList(Transform srcT, Transform tgtT)
        {
            var result = new List<ComponentInfo>();

            List<Component> srcComps = srcT.GetComponents<Component>()
                .Where(c => c != null && !(c is Transform) && !IsExcluded(c))
                .ToList();

            List<Component> tgtComps = tgtT != null
                ? tgtT.GetComponents<Component>().Where(c => c != null && !(c is Transform)).ToList()
                : new List<Component>();

            var tgtCountByType = tgtComps.GroupBy(c => c.GetType()).ToDictionary(g => g.Key, g => g.Count());
            var srcSeenByType = new Dictionary<Type, int>();

            foreach (var sc in srcComps)
            {
                Type ty = sc.GetType();
                int idx = srcSeenByType.TryGetValue(ty, out var v) ? v : 0;
                srcSeenByType[ty] = idx + 1;

                int tgtCount = tgtCountByType.TryGetValue(ty, out var tc) ? tc : 0;
                if (idx >= tgtCount)
                {
                    result.Add(new ComponentInfo
                    {
                        sourceComponent = sc,
                        type = ty,
                        typeIndex = idx,
                        include = true
                    });
                }
            }

            return result;
        }

        private static bool IsExcluded(Component c)
        {
            return ExcludedTypeFullNames.Contains(c.GetType().FullName);
        }

        private static void BuildMap(Transform root, Dictionary<string, Transform> map, List<string> orderedPaths)
        {
            void Recurse(Transform t, string path)
            {
                map[path] = t;
                orderedPaths.Add(path);
                for (int i = 0; i < t.childCount; i++)
                {
                    Transform c = t.GetChild(i);
                    string childPath = string.IsNullOrEmpty(path) ? c.name : path + "/" + c.name;
                    Recurse(c, childPath);
                }
            }
            Recurse(root, "");
        }

        private static string GetRelativePath(Transform t, Transform root)
        {
            if (t == root) return "";
            var stack = new List<string>();
            Transform cur = t;
            while (cur != null && cur != root)
            {
                stack.Add(cur.name);
                cur = cur.parent;
            }
            if (cur != root) return null; // not under root
            stack.Reverse();
            return string.Join("/", stack);
        }

        private static string GetParentPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int idx = path.LastIndexOf('/');
            return idx < 0 ? "" : path.Substring(0, idx);
        }

        // ---------------------------------------------------------------
        // APPLY COPIES
        // ---------------------------------------------------------------

        private void ApplySelectedCopies()
        {
            Undo.SetCurrentGroupName("VRC Component Copier - Copy Selected");
            int group = Undo.GetCurrentGroup();

            var copyResults = new List<(Component newComp, Component srcComp)>();
            var sortedNodes = nodeList.OrderBy(n => n.depth).ToList();
            var warnings = new List<string>();

            foreach (var node in sortedNodes)
            {
                Transform targetT = node.targetTransform;

                if (targetT == null)
                {
                    if (!node.includeObject) continue;

                    string parentPath = GetParentPath(node.path);
                    if (parentPath == null || !targetPathMap.TryGetValue(parentPath, out Transform parentT))
                    {
                        warnings.Add($"Skipped '{node.path}' \u2014 parent object was not created.");
                        continue;
                    }

                    GameObject go = new GameObject(node.sourceTransform.name);
                    Undo.RegisterCreatedObjectUndo(go, "Create Missing Object");
                    go.transform.SetParent(parentT, false);
                    go.transform.localPosition = node.sourceTransform.localPosition;
                    go.transform.localRotation = node.sourceTransform.localRotation;
                    go.transform.localScale = node.sourceTransform.localScale;
                    go.layer = node.sourceTransform.gameObject.layer;
                    TrySetTag(go, node.sourceTransform.gameObject.tag);
                    go.SetActive(node.sourceTransform.gameObject.activeSelf);

                    targetT = go.transform;
                    targetPathMap[node.path] = targetT;
                }

                foreach (var comp in node.components)
                {
                    if (!comp.include) continue;

                    Component newComp = targetT.gameObject.AddComponent(comp.type);
                    Undo.RegisterCreatedObjectUndo(newComp, "Add Component");

                    ComponentUtility.CopyComponent(comp.sourceComponent);
                    ComponentUtility.PasteComponentValues(newComp);

                    copyResults.Add((newComp, comp.sourceComponent));
                }
            }

            Undo.CollapseUndoOperations(group);

            if (warnings.Count > 0)
            {
                Debug.LogWarning("[VRC Component Copier]\n" + string.Join("\n", warnings));
            }

            pendingRemaps = BuildRemapEntries(copyResults);

            if (pendingRemaps.Count == 0)
            {
                EditorUtility.DisplayDialog("VRC Component Copier",
                    "Copy complete. No object references needed remapping.", "OK");
                Rescan();
            }
            else
            {
                phase = Phase.Review;
            }
        }

        private static void TrySetTag(GameObject go, string tag)
        {
            try { go.tag = tag; }
            catch { /* tag doesn't exist in target project, leave as Untagged */ }
        }

        // ---------------------------------------------------------------
        // REFERENCE SCAN
        // ---------------------------------------------------------------

        private List<RemapEntry> BuildRemapEntries(List<(Component newComp, Component srcComp)> results)
        {
            var list = new List<RemapEntry>();
            if (sourceAvatar == null) return list;
            Transform srcRoot = sourceAvatar.transform;

            foreach (var (newComp, srcComp) in results)
            {
                if (newComp == null) continue;

                SerializedObject soNew = new SerializedObject(newComp);
                SerializedProperty iterator = soNew.GetIterator();

                while (iterator.NextVisible(true))
                {
                    if (iterator.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (iterator.name == "m_Script") continue;

                    UnityEngine.Object refObj = iterator.objectReferenceValue;
                    if (refObj == null) continue;

                    Transform refT = ExtractTransform(refObj);
                    if (refT == null) continue;
                    if (refT != srcRoot && !refT.IsChildOf(srcRoot)) continue;

                    string relPath = GetRelativePath(refT, srcRoot);
                    if (relPath == null) continue;

                    UnityEngine.Object proposed = null;
                    if (targetPathMap.TryGetValue(relPath, out Transform matchT) && matchT != null)
                    {
                        if (refObj is GameObject) proposed = matchT.gameObject;
                        else if (refObj is Transform) proposed = matchT;
                        else proposed = matchT.GetComponent(refObj.GetType());
                    }

                    list.Add(new RemapEntry
                    {
                        targetComponent = newComp,
                        componentLabel = $"{newComp.gameObject.name} / {newComp.GetType().Name}",
                        propertyPath = iterator.propertyPath,
                        displayLabel = ObjectNames.NicifyVariableName(iterator.name),
                        sourceValue = refObj,
                        proposedValue = proposed,
                        apply = proposed != null
                    });
                }
            }

            return list;
        }

        private static Transform ExtractTransform(UnityEngine.Object obj)
        {
            if (obj is Transform t) return t;
            if (obj is GameObject go) return go.transform;
            if (obj is Component c) return c.transform;
            return null;
        }

        // ---------------------------------------------------------------
        // PHASE 2: REVIEW REFERENCES
        // ---------------------------------------------------------------

        private void DrawReviewPhase()
        {
            EditorGUILayout.LabelField("Review Reference Remaps", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "These fields on newly-copied components still point at objects on the SOURCE avatar. " +
                "Review / fix the proposed replacement on the TARGET avatar for each, then Apply.",
                MessageType.Info);

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Accept All Matches")) SetAllRemaps(true);
                if (GUILayout.Button("Clear All")) SetAllRemaps(false);
            }

            EditorGUILayout.Space();
            scrollPos = EditorGUILayout.BeginScrollView(scrollPos, GUILayout.ExpandHeight(true));

            string lastComponentLabel = null;
            foreach (var r in pendingRemaps)
            {
                if (r.componentLabel != lastComponentLabel)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField(r.componentLabel, EditorStyles.boldLabel);
                    lastComponentLabel = r.componentLabel;
                }

                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    r.apply = EditorGUILayout.Toggle(r.apply, GUILayout.Width(18));

                    using (new EditorGUILayout.VerticalScope())
                    {
                        string srcName = r.sourceValue != null ? r.sourceValue.name : "(none)";
                        EditorGUILayout.LabelField($"{r.displayLabel}  (was: {srcName})", EditorStyles.miniBoldLabel);
                        r.proposedValue = EditorGUILayout.ObjectField(r.proposedValue, typeof(UnityEngine.Object), true);
                        if (r.proposedValue == null)
                        {
                            EditorGUILayout.HelpBox("No automatic match found on target \u2014 assign manually or leave unchecked to skip.", MessageType.None);
                        }
                    }
                }
            }

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Skip / Finish Without Remapping", GUILayout.Height(28)))
                {
                    phase = Phase.Select;
                    Rescan();
                }
                if (GUILayout.Button("Apply Remaps", GUILayout.Height(28)))
                {
                    ApplyRemaps();
                }
            }
        }

        private void SetAllRemaps(bool value)
        {
            foreach (var r in pendingRemaps)
            {
                if (value && r.proposedValue == null) continue; // nothing to accept
                r.apply = value;
            }
        }

        private void ApplyRemaps()
        {
            Undo.SetCurrentGroupName("VRC Component Copier - Apply Remaps");
            int group = Undo.GetCurrentGroup();

            var byComponent = pendingRemaps.Where(r => r.apply).GroupBy(r => r.targetComponent);

            foreach (var grp in byComponent)
            {
                Component comp = grp.Key;
                if (comp == null) continue;

                Undo.RecordObject(comp, "Remap Reference");
                SerializedObject so = new SerializedObject(comp);

                foreach (var r in grp)
                {
                    SerializedProperty p = so.FindProperty(r.propertyPath);
                    if (p != null)
                    {
                        p.objectReferenceValue = r.proposedValue;
                    }
                }

                so.ApplyModifiedProperties();
            }

            Undo.CollapseUndoOperations(group);

            EditorUtility.DisplayDialog("VRC Component Copier", "Remaps applied.", "OK");

            phase = Phase.Select;
            Rescan();
        }
    }
}