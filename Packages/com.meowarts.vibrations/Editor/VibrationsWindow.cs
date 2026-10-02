using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Vibrations
{
    // The rig in the scene is the working copy of the selected pose: edits are made on the rig directly
    // (native undo) and committed into the asset when leaving the pose (select, play, export, close).
    public class VibrationsWindow : EditorWindow
    {
        const string PackagePath = "Packages/com.meowarts.vibrations/Editor/";

        // Indices into Tween.Bones.
        static readonly (int a, int b, int c, bool leg)[] Limbs = { (7, 8, 9, false), (11, 12, 13, false), (14, 15, 16, true), (18, 19, 20, true) };
        static readonly int[] Joints = { 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 14, 15, 17, 18, 19, 21 }; // clickable dots (not hips/hands/feet)

        static readonly Color Accent = new(1f, 0.48f, 0.35f);
        static readonly Color Cool = new(0.36f, 0.78f, 1f);
        static readonly Color GhostColor = new(0.45f, 0.7f, 1f, 0.3f);
        static readonly Color ThumbColor = new(0.86f, 0.85f, 0.83f, 1f);
        static readonly Color[] AxisColors = { new(1f, 0.35f, 0.35f), new(0.45f, 0.9f, 0.4f), new(0.35f, 0.6f, 1f) };

        [SerializeField] Animator animator;
        [SerializeField] VibrationsAnimation anim;
        [SerializeField] int selected = -1;
        [SerializeField] Pose rest;
        [SerializeField] Animator restOwner;
        [SerializeField] float[] soles = new float[4]; // rest height of each sole bone above the floor
        [SerializeField] Vector3 restCenter, restSize; // character bounds at rest, relative to the root (thumbnail framing)
        [SerializeField] bool onion = true, onionInPreview, floor = true, keepGrounded = true;
        [SerializeField] float floorHeight;
        [SerializeField] int calibration;
        [SerializeField] Vector3 restHips; // hips at rest, in the character's space
        const int Calibration = 3; // bump to re-measure floor/soles on existing setups
        [SerializeField] int tab;

        Transform[] bones;
        // The selected pose when Global Edit started: what's changed since is spread to the others. NonSerialized: a domain
        // reload would bring it back as an empty Pose (never null), turn the mode on and write that blank pose on cancel.
        [NonSerialized] Pose baseline;
        PoseRenderer.Snapshot baselineGhost;
        Button globalButton;
        VisualElement globalBar;
        bool GlobalEditing => baseline != null;
        static readonly Color GlobalGhostColor = new(1f, 0.55f, 0.15f, 0.35f);
        Transform[] undoBones; // bones minus the optional ones this rig lacks (Undo rejects nulls)
        SerializedObject so;
        bool playing, previewing;
        double playStart;
        float previewTime;
        int activeBone = -1;
        int previewGhost = -1; // pose shown as the ghost while previewing

        HumanPoseHandler humanHandler;
        Quaternion[][] frames; // preview cache: the same frames the export bakes
        Vector3[] frameHips;
        Vector4[] frameLean;
        int framesVersion = -1, framesFps;

        PoseRenderer poseRenderer;
        PoseRenderer.Snapshot ghost, scratch;
        readonly List<RenderTexture> thumbs = new();
        readonly List<Image> cardImages = new();
        readonly List<VisualElement> cards = new();
        readonly List<VisualElement> chips = new(); // transition chip after each card (null for a one-shot's last pose)
        readonly SortedSet<int> picked = new(); // multi-selection (Shift / Cmd-Ctrl click), includes `selected` when used
        VisualElement addCard, dropSlot, dragGhost;
        List<int> dragSet; // poses being dragged
        Vector2 dragGrab;  // pointer offset inside the grabbed card
        bool thumbsDirty = true;
        Pose lastRendered;
        string signature;

        VisualElement bound, main, empty, strip, transitionBox, humanizeBox, noSelection, poseBox;
        readonly List<(VisualElement hint, VisualElement content)> gated = new(); // tabs that need an animation
        TabView tabView;
        VisualElement builtInGrid, userGrid, templatesContent;
        Label templatesHint;
        Button saveTemplateButton, convertButton;
        TimingBar timingBar;
        Button resetTimingButton;
        TextField aiPrompt;
        Button aiSend;
        Label aiStatus;
        VisualElement aiLog;
        CancellationTokenSource aiCancel;
        VisualElement aiKeyWarning, aiContent;
        Label aiHint;
        readonly List<Texture2D> aiImages = new();
        bool aiBusy;
        readonly Dictionary<VibrationsTemplate, RenderTexture> templateThumbs = new();
        bool templatesDirty = true;
        Label emptyText, timeLabel, poseTitle;
        ObjectField characterField, animationField;
        Button playButton;
        Slider scrub;
        readonly List<Button> presetChips = new();
        readonly List<CurveView> curves = new();
        readonly List<VisualElement> poseFields = new();

        bool Ready => bones != null && anim != null && so != null && !Foreign;
        bool Foreign => anim != null && anim.avatar != null && animator != null && anim.avatar != animator.avatar;
        bool HasSelection => Ready && selected >= 0 && selected < anim.poses.Count;

        [MenuItem("Window/Vibrations")]
        static void Open() => GetWindow<VibrationsWindow>(); // title and icon are set in OnEnable

        // --- Lifecycle ---

        void OnEnable()
        {
            titleContent = new GUIContent("Vibrations", AssetDatabase.LoadAssetAtPath<Texture2D>(PackagePath + "VibrationsIcon.png"));
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.update += Tick;
            Undo.undoRedoPerformed += OnUndo;
            Undo.undoRedoEvent += OnUndoRedo;
            if (animator == null && Selection.activeGameObject)
                animator = Selection.activeGameObject.GetComponentInParent<Animator>();
            so = anim != null ? new SerializedObject(anim) : null;
            Bind(animator);
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.update -= Tick;
            Undo.undoRedoPerformed -= OnUndo;
            Undo.undoRedoEvent -= OnUndoRedo;
            aiCancel?.Cancel();
            foreach (var t in aiImages) DestroyImmediate(t);
            aiImages.Clear();
            CancelGlobalEdit();
            Stop();
            Commit();
            ReleaseRendering();
        }

        void OnDestroy() => RestoreRest();

        // Undoing a pose operation restores the data; show it on the rig, or the next commit would overwrite the undo.
        void OnUndoRedo(in UndoRedoInfo info)
        {
            if (!previewing && PoseEdits.Contains(info.undoName)) ApplySelected();
        }

        void OnUndo()
        {
            thumbsDirty = true;
            frames = null;
        }

        void Bind(Animator a)
        {
            ReleaseRendering();
            animator = a;
            bones = a != null && a.isHuman && a.avatar != null ? Rig.Bind(a) : null;
            activeBone = -1;
            if (bones == null) return;
            undoBones = bones.Where(b => b).ToArray();
            poseRenderer = new PoseRenderer(a);
            humanHandler = new HumanPoseHandler(a.avatar, a.transform);

            var working = new Pose();
            Rig.Capture(bones, working);
            bool newCharacter = restOwner != a;
            if (newCharacter)
            {
                rest = working.Clone();
                restOwner = a;
            }
            Rig.Apply(bones, rest.rotations, rest.hipsPosition);
            scratch = poseRenderer.Bake(scratch);
            restCenter = scratch.bounds.center - a.transform.position;
            restSize = scratch.bounds.size;
            restHips = a.transform.InverseTransformPoint(bones[0].position);
            if (newCharacter || calibration != Calibration)
            {
                // Soles = foot/toe bone heights above the true bottom of the mesh at rest (used for foot snapping).
                // The floor is the scene's ground under the character, or that mesh bottom if there's none.
                float bottom = LowestPoint();
                for (int k = 0; k < Rig.SoleBones.Length; k++)
                    soles[k] = bones[Rig.SoleBones[k]] ? bones[Rig.SoleBones[k]].position.y - bottom : 0f;
                floorHeight = SceneGround() ?? bottom;
                calibration = Calibration;
            }
            Rig.Apply(bones, working.rotations, working.hipsPosition);

            thumbsDirty = true;
            ApplySelected();
        }

        void ReleaseRendering()
        {
            poseRenderer?.Dispose();
            humanHandler?.Dispose();
            humanHandler = null;
            ghost?.Dispose();
            scratch?.Dispose();
            baselineGhost?.Dispose();
            baselineGhost = null;
            poseRenderer = null;
            ghost = scratch = null;
            foreach (var rt in thumbs) if (rt) { rt.Release(); DestroyImmediate(rt); }
            thumbs.Clear();
            foreach (var rt in templateThumbs.Values) if (rt) { rt.Release(); DestroyImmediate(rt); }
            templateThumbs.Clear();
            templatesDirty = true;
            thumbsDirty = true;
        }

        void SetAnimation(VibrationsAnimation a)
        {
            CancelGlobalEdit();
            Stop();
            Commit();
            anim = a;
            so = a != null ? new SerializedObject(a) : null;
            selected = a != null && a.poses.Count > 0 ? 0 : -1;
            signature = null;
            thumbsDirty = true;
            frames = null;
            if (bound != null)
            {
                if (so != null) bound.Bind(so);
                else bound.Unbind();
                animationField.SetValueWithoutNotify(a);
            }
            ApplySelected();
            Refresh();
        }

        // --- Pose editing ---

        void Commit()
        {
            if (previewing || !HasSelection) return;
            var captured = anim.poses[selected].Clone();
            Rig.Capture(bones, captured);
            Undo.RecordObject(anim, "Edit pose");
            anim.poses[selected] = captured;
            if (anim.avatar == null) anim.avatar = animator.avatar;
            EditorUtility.SetDirty(anim);
            so.Update();
        }

        void Select(int i)
        {
            if (i != selected) CancelGlobalEdit();
            Stop();
            if (picked.Count > 0)
            {
                picked.Clear();
                signature = null;
            }
            if (i == selected)
            {
                Refresh();
                return;
            }
            Commit();
            selected = i;
            activeBone = -1;
            thumbsDirty = true; // the ghost follows the selection
            ApplySelected();
            Refresh();
        }

        void ApplySelected()
        {
            if (!HasSelection) return;
            var p = anim.poses[selected];
            Rig.Apply(bones, p.rotations, p.hipsPosition);
            SceneView.RepaintAll();
        }

        static readonly HashSet<string> PoseEdits = new() { "Vibrations AI" }; // undo names that change pose data

        void EditPoses(string undoName, Action edit)
        {
            CancelGlobalEdit();
            PoseEdits.Add(undoName);
            Stop();
            Commit();
            Undo.RecordObject(anim, undoName);
            edit();
            EditorUtility.SetDirty(anim);
            so.Update();
            thumbsDirty = true;
            ApplySelected();
            Refresh();
        }

        void AddPose(int after, bool atEnd = false) => EditPoses("Add pose", () =>
        {
            var p = after >= 0 ? anim.poses[after].Clone() : new Pose();
            if (after < 0) Rig.Capture(bones, p);
            p.name = $"Pose {anim.poses.Count + 1}";
            selected = atEnd ? anim.poses.Count : after + 1;
            anim.poses.Insert(selected, p);
        });

        // Moves the poses (in order) as a block to `slot`, counted among the poses that stay.
        void MovePoses(List<int> targets, int slot)
        {
            slot = Mathf.Clamp(slot, 0, anim.poses.Count - targets.Count);
            var order = MoveOrder(anim.poses.Count, targets, slot);
            if (order.SequenceEqual(Enumerable.Range(0, anim.poses.Count))) return; // dropped where it was
            EditPoses("Move poses", () =>
            {
                var current = HasSelection ? anim.poses[selected] : null;
                var reordered = order.Select(k => anim.poses[k]).ToList();
                anim.poses.Clear();
                anim.poses.AddRange(reordered);
                if (current != null) selected = anim.poses.IndexOf(current);
                Pick(slot, targets.Count, keepSelected: true);
            });
        }

        // New order of pose indices after moving `targets` (in order) to `slot` among the poses that stay.
        public static List<int> MoveOrder(int count, IList<int> targets, int slot)
        {
            var remaining = Enumerable.Range(0, count).Where(k => !targets.Contains(k)).ToList();
            return remaining.Take(slot).Concat(targets).Concat(remaining.Skip(slot)).ToList();
        }

        void DuplicatePoses(List<int> targets, bool mirrored, bool atEnd) => EditPoses(mirrored ? "Duplicate mirrored" : "Duplicate", () =>
        {
            var copies = targets.Select(k => anim.poses[k].Clone()).ToList();
            if (mirrored)
            {
                var saved = Rig.SaveAll(animator.transform);
                foreach (var p in copies)
                {
                    Rig.Mirror(humanHandler, bones, p, animator.transform);
                    p.name = MirroredName(p.name);
                }
                Rig.RestoreAll(saved);
            }
            int at = atEnd ? anim.poses.Count : targets[^1] + 1;
            anim.poses.InsertRange(at, copies);
            Pick(at, copies.Count, keepSelected: false);
        });

        void MirrorPoses(List<int> targets) => EditPoses("Mirror pose", () =>
        {
            var saved = Rig.SaveAll(animator.transform);
            foreach (var k in targets)
            {
                var p = anim.poses[k].Clone();
                Rig.Mirror(humanHandler, bones, p, animator.transform);
                anim.poses[k] = p;
            }
            Rig.RestoreAll(saved);
        });

        void DeletePoses(List<int> targets) => EditPoses("Delete pose", () =>
        {
            foreach (var k in targets.OrderByDescending(k => k)) anim.poses.RemoveAt(k);
            selected = Mathf.Clamp(targets[0], 0, anim.poses.Count - 1);
            picked.Clear();
        });

        // Multi-select `count` poses from `start`; the first one becomes the edited pose unless keepSelected.
        void Pick(int start, int count, bool keepSelected)
        {
            picked.Clear();
            if (count > 1) for (int k = 0; k < count; k++) picked.Add(start + k);
            if (!keepSelected) selected = start;
        }

        // "Contact R" -> "Contact L", "Left kick" -> "Right kick", anything else gets " (mirror)".
        static string MirroredName(string name)
        {
            var words = name.Split(' ');
            bool swapped = false;
            for (int w = 0; w < words.Length; w++)
            {
                var swap = words[w] switch { "R" => "L", "L" => "R", "Right" => "Left", "Left" => "Right", "right" => "left", "left" => "right", _ => null };
                if (swap != null) (words[w], swapped) = (swap, true);
            }
            return swapped ? string.Join(" ", words) : name + " (mirror)";
        }

        static Pose clipboard; // copied pose, shared by every Vibrations window

        void PastePose(int i) => EditPoses("Paste pose", () =>
        {
            var p = clipboard.Clone();
            p.name = anim.poses[i].name;
            p.hold = anim.poses[i].hold;
            p.customTransition = anim.poses[i].customTransition;
            p.transition = anim.poses[i].transition;
            anim.poses[i] = p;
        });

        void RestoreRest()
        {
            if (bones != null && restOwner == animator) Rig.Apply(bones, rest.rotations, rest.hipsPosition);
        }

        // --- Floor ---

        // Moves the body so its lowest actual point (the mesh, not a bone estimate) touches the floor.
        void Ground()
        {
            if (poseRenderer == null) Rig.Ground(bones, soles, floorHeight);
            else bones[0].position += Vector3.up * (floorHeight - LowestPoint());
        }

        float LowestPoint() => poseRenderer.Lowest(ref scratch);

        // Height of the scene's ground (anything with a collider) under the character, ignoring the character itself.
        float? SceneGround()
        {
            Physics.SyncTransforms();
            var root = animator.transform;
            var origin = bones[0].position;
            float? best = null;
            foreach (var hit in Physics.RaycastAll(origin, Vector3.down, 10f * animator.humanScale, ~0, QueryTriggerInteraction.Ignore))
                if (!hit.transform.IsChildOf(root) && (best == null || hit.point.y > best)) best = hit.point.y;
            return best;
        }

        // Hips back over the character's origin on X/Z (height untouched).
        void Center()
        {
            var root = animator.transform;
            var local = root.InverseTransformPoint(bones[0].position);
            local.x = restHips.x;
            local.z = restHips.z;
            bones[0].position = root.TransformPoint(local);
        }

        // Runs a rig operation (ground, center...) on each target pose and stores the result.
        void EditEach(List<int> targets, string undoName, Action operation) => EditPoses(undoName, () =>
        {
            foreach (var k in targets)
            {
                var p = anim.poses[k].Clone();
                Rig.Apply(bones, p.rotations, p.hipsPosition);
                operation();
                Rig.Capture(bones, p);
                anim.poses[k] = p;
            }
        });

        void EditTiming(string undoName, Action<VibrationsAnimation> change)
        {
            Commit();
            Undo.RecordObject(anim, undoName);
            change(anim);
            EditorUtility.SetDirty(anim);
            so.Update();
            signature = null;
            Refresh();
        }

        // Global Edit: the selected pose is snapshotted (orange ghost), and on Apply the joint rotations changed since
        // are added to the other poses, or to the Shift/Cmd-clicked ones: widen the arms once, every pose gets wider arms.
        void ToggleGlobalEdit()
        {
            if (GlobalEditing) { CancelGlobalEdit(); return; }
            if (!HasSelection) return;
            Stop();
            Commit();
            baseline = anim.poses[selected].Clone();
            baselineGhost = poseRenderer.Bake(baselineGhost);
            activeBone = -1;
            Refresh();
            SceneView.RepaintAll();
        }

        // Leaves Global Edit with the selected pose back as it was. Anything that changes pose, animation or character calls it.
        void CancelGlobalEdit()
        {
            if (!GlobalEditing) return;
            var p = baseline;
            baseline = null;
            if (HasSelection)
            {
                Rig.Apply(bones, p.rotations, p.hipsPosition);
                Commit();
            }
            Refresh();
            SceneView.RepaintAll();
        }

        void ApplyGlobalEdit()
        {
            if (!HasSelection || !GlobalEditing) return;
            var current = new Pose();
            Rig.Capture(bones, current);
            var deltas = new Quaternion?[bones.Length];
            for (int b = 0; b < bones.Length; b++)
                if (bones[b] && Quaternion.Angle(current.rotations[b], baseline.rotations[b]) > 0.01f)
                    deltas[b] = current.rotations[b] * Quaternion.Inverse(baseline.rotations[b]); // in the parent's space
            if (deltas.All(d => d == null))
            {
                ShowNotification(new GUIContent("Rotate joints on this pose first"));
                return;
            }
            baseline = null;
            var targets = picked.Count > 1 ? new List<int>(picked) : AllPoses();
            targets.Remove(selected);
            EditEach(targets, "Global edit", () =>
            {
                for (int b = 0; b < bones.Length; b++)
                    if (deltas[b] is { } d) bones[b].localRotation = d * bones[b].localRotation;
                if (anim.humanize && anim.jointLimits) Rig.ClampToLimits(humanHandler, bones, animator.transform);
                if (floor && keepGrounded) Ground();
            });
        }

        List<int> AllPoses() => Enumerable.Range(0, anim.poses.Count).ToList();

        List<int> SelectedPoses() => HasSelection ? Targets(selected) : new List<int>();

        float SnapToFloor(float y, float sole)
        {
            float min = floorHeight + sole;
            return y < min + 0.02f * animator.humanScale ? min : y;
        }

        // --- Preview ---

        void TogglePlay()
        {
            CancelGlobalEdit();
            if (playing) { Stop(); return; }
            if (!previewing) Commit();
            previewing = playing = true;
            playStart = EditorApplication.timeSinceStartup - previewTime;
        }

        void Scrub(float t)
        {
            CancelGlobalEdit();
            if (!previewing) Commit();
            previewing = true;
            playing = false;
            previewTime = t;
            ApplyPreviewTime();
        }

        void Stop()
        {
            if (!previewing) return;
            previewing = playing = false;
            previewTime = 0f;
            previewGhost = -1;
            thumbsDirty = true; // re-bakes the editing ghost (previous pose)
            scrub?.SetValueWithoutNotify(0f);
            ApplySelected();
        }

        void Tick()
        {
            if (!playing) return;
            float len = Ready && anim.poses.Count > 0 ? Tween.Length(anim) : 0f;
            if (len <= 0f) { Stop(); return; }
            float t = (float)(EditorApplication.timeSinceStartup - playStart);
            previewTime = anim.loop ? Mathf.Repeat(t, len) : Mathf.Min(Mathf.Repeat(t, len + 0.5f), len); // one-shots replay after a short pause
            ApplyPreviewTime();
        }

        void ApplyPreviewTime()
        {
            int fps = VibrationsSettings.instance.frameRate, version = EditorUtility.GetDirtyCount(anim);
            if (frames == null || version != framesVersion || fps != framesFps)
            {
                frames = Tween.Frames(anim, fps, out frameHips);
                frameLean = Secondary.Simulate(anim, frameHips, bones[0], animator.transform, animator.humanScale, fps);
                (framesVersion, framesFps) = (version, fps);
            }
            float len = Tween.Length(anim);
            int f = len > 0f ? Mathf.Clamp(Mathf.FloorToInt(previewTime / len * (frames.Length - 1)), 0, frames.Length - 1) : 0;
            if (onion && onionInPreview && poseRenderer != null)
            {
                int key = Tween.PoseAt(anim, previewTime);
                if (key != previewGhost) // bake the key pose once, when the playhead enters it
                {
                    var p = anim.poses[key];
                    Rig.Apply(bones, p.rotations, p.hipsPosition);
                    ghost = poseRenderer.Bake(ghost);
                    previewGhost = key;
                }
            }
            Rig.Apply(bones, frames[f], frameHips[f]);
            Secondary.Apply(bones, animator.transform, frameLean[f]);
            if (anim.humanize && anim.jointLimits) Rig.ClampToLimits(humanHandler, bones, animator.transform);
            scrub?.SetValueWithoutNotify(previewTime);
            timingBar?.MarkDirtyRepaint();
            if (timeLabel != null) timeLabel.text = $"{previewTime:0.00} / {len:0.00}s";
            SceneView.RepaintAll();
        }

        // --- Export / create ---

        void Export()
        {
            Stop();
            Commit();
            var clip = ClipBaker.Bake(anim, animator, VibrationsSettings.instance.frameRate);
            ApplySelected();
            var path = Path.ChangeExtension(AssetDatabase.GetAssetPath(anim), ".anim");
            var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(clip, existing); // keep the GUID so Animator references survive
                clip = existing;
            }
            else AssetDatabase.CreateAsset(clip, path);
            AssetDatabase.SaveAssets();
            EditorGUIUtility.PingObject(clip);
            ShowNotification(new GUIContent($"Exported {Path.GetFileName(path)}"));
        }

        void CreateAnimation()
        {
            if (bones == null) return;
            var settings = VibrationsSettings.instance;
            Directory.CreateDirectory(settings.exportFolder);
            AssetDatabase.Refresh();
            var path = EditorUtility.SaveFilePanelInProject("New Vibrations animation", "Walk", "asset", "", settings.exportFolder);
            if (string.IsNullOrEmpty(path)) return;
            Commit();
            var a = CreateInstance<VibrationsAnimation>();
            a.avatar = animator.avatar;
            a.ApplyPreset(settings.defaultPreset);
            var p = new Pose { name = "Pose 1" };
            Rig.Capture(bones, p);
            a.poses.Add(p);
            AssetDatabase.CreateAsset(a, path);
            SetAnimation(a);
        }

        // --- Thumbnails & onion skin ---

        void RefreshRendering()
        {
            if (poseRenderer == null || previewing || !Ready) return;
            if (thumbsDirty) RebuildThumbnails();
            else if (HasSelection && RigChanged()) RenderSelectedThumbnail();
        }

        void RebuildThumbnails()
        {
            thumbsDirty = false;
            var working = new Pose();
            Rig.Capture(bones, working);
            while (thumbs.Count < anim.poses.Count)
                thumbs.Add(new RenderTexture(176, 232, 24) { hideFlags = HideFlags.HideAndDontSave });

            for (int i = 0; i < anim.poses.Count; i++)
            {
                var p = i == selected ? working : anim.poses[i];
                Rig.Apply(bones, p.rotations, p.hipsPosition);
                scratch = poseRenderer.Bake(scratch);
                poseRenderer.RenderThumbnail(scratch, thumbs[i], ThumbnailFrame(), ThumbnailView(), ThumbColor);
            }

            int g = GhostIndex();
            if (g >= 0)
            {
                Rig.Apply(bones, anim.poses[g].rotations, anim.poses[g].hipsPosition);
                ghost = poseRenderer.Bake(ghost);
            }
            else { ghost?.Dispose(); ghost = null; }

            Rig.Apply(bones, working.rotations, working.hipsPosition);
            lastRendered = working;
            for (int i = 0; i < cardImages.Count && i < thumbs.Count; i++)
            {
                cardImages[i].image = thumbs[i];
                cardImages[i].MarkDirtyRepaint();
            }
            SceneView.RepaintAll();
        }

        void RenderSelectedThumbnail()
        {
            Rig.Capture(bones, lastRendered ??= new Pose());
            if (selected >= thumbs.Count) return;
            scratch = poseRenderer.Bake(scratch);
            poseRenderer.RenderThumbnail(scratch, thumbs[selected], ThumbnailFrame(), ThumbnailView(), ThumbColor);
            if (selected < cardImages.Count) cardImages[selected].MarkDirtyRepaint();
        }

        bool RigChanged()
        {
            if (lastRendered == null) return true;
            if (bones[0].localPosition != lastRendered.hipsPosition) return true;
            for (int i = 0; i < bones.Length; i++)
                if (bones[i] && bones[i].localRotation != lastRendered.rotations[i]) return true;
            return false;
        }

        int GhostIndex()
        {
            if (!HasSelection || anim.poses.Count < 2) return -1;
            if (selected > 0) return selected - 1;
            return anim.loop ? anim.poses.Count - 1 : -1;
        }

        Bounds ThumbnailFrame() =>
            new(animator.transform.position + restCenter, Vector3.Scale(restSize, new Vector3(1.3f, 1.12f, 1.3f)));

        Quaternion ThumbnailView() // three-quarter view from the character's front-right
        {
            var root = animator.transform;
            return Quaternion.LookRotation(-(Quaternion.AngleAxis(-40f, Vector3.up) * root.forward), Vector3.up);
        }

        // --- UI ---

        void CreateGUI()
        {
            var root = rootVisualElement;
            root.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(PackagePath + "Vibrations.uss"));
            tabView = new TabView().Cls("vb-tabview");
            root.Add(tabView);
            bound = tabView;

            // Animate: character, animation, transport, poses and the selected pose.
            var animate = AddTab(tabView, "Animate");
            var setup = Add(animate, "vb-section");
            characterField = new ObjectField("Character") { objectType = typeof(Animator), allowSceneObjects = true, value = animator };
            characterField.RegisterValueChangedCallback(e =>
            {
                CancelGlobalEdit(); Stop(); Commit(); RestoreRest();
                Bind((Animator)e.newValue);
                signature = null;
                Refresh();
            });
            setup.Add(characterField);
            var animRow = Add(setup, "vb-row");
            animationField = new ObjectField("Animation") { objectType = typeof(VibrationsAnimation), allowSceneObjects = false, value = anim };
            animationField.style.flexGrow = 1;
            animationField.RegisterValueChangedCallback(e => SetAnimation((VibrationsAnimation)e.newValue));
            animRow.Add(animationField);
            animRow.Add(MakeButton("New", CreateAnimation, "vb-btn-inline"));

            empty = Add(animate, "vb-empty");
            emptyText = new Label().Cls("vb-empty-text");
            empty.Add(emptyText);
            Step(empty, "1", "Pick a Humanoid character from the scene");
            Step(empty, "2", "Create an animation with New");
            Step(empty, "3", "Pose, add poses, play, export");
            convertButton = MakeButton("Make a Copy for This Character", ConvertAnimation, "vb-btn-row");
            empty.Add(convertButton);

            main = Add(animate);
            BuildTransport(main);
            BuildStrip(main);
            BuildPosePanel(main);

            BuildTemplates(AddTab(tabView, "Templates"));
            foreach (var (name, build) in new (string, Action<VisualElement>)[] { ("Feel", BuildFeel), ("Humanize", BuildHumanize), ("Scene", BuildScene) })
            {
                var page = AddTab(tabView, name);
                var hint = new Label("Pick a character and an animation in the Animate tab.").Cls("vb-caption", "vb-pad");
                page.Add(hint);
                var content = Add(page);
                build(content);
                gated.Add((hint, content));
            }
            // AI: only a warning until an API key is set.
            var aiPage = AddTab(tabView, "AI");
            aiKeyWarning = Add(aiPage, "vb-pad");
            aiKeyWarning.Add(new HelpBox("Add an Anthropic or OpenAI API key in the Settings tab to use the AI assistant.", HelpBoxMessageType.Warning));
            int settingsTab = tabView.Query<Tab>().ToList().Count; // Settings is added next
            aiKeyWarning.Add(MakeButton("Open Settings", () => tabView.selectedTabIndex = settingsTab, "vb-btn-row"));
            aiHint = new Label("Pick a character and an animation in the Animate tab.").Cls("vb-caption", "vb-pad");
            aiPage.Add(aiHint);
            aiContent = Add(aiPage);
            BuildAi(aiContent);

            BuildSettings(AddTab(tabView, "Settings"));
            BuildSupport(AddTab(tabView, "Support"));

            tabView.selectedTabIndex = tab;
            tabView.activeTabChanged += (_, _) =>
            {
                tab = tabView.selectedTabIndex;
                templatesDirty = true; // user templates may have changed
            };
            if (so != null) bound.Bind(so);
            root.schedule.Execute(Refresh).Every(150);
            Refresh();
        }

        static VisualElement AddTab(TabView view, string label)
        {
            var tab = new Tab(label);
            var scroll = new ScrollView().Cls("vb-page");
            tab.Add(scroll);
            view.Add(tab);
            return scroll.contentContainer;
        }

        void BuildTransport(VisualElement parent)
        {
            var bar = Add(parent, "vb-transport");
            playButton = new Button(TogglePlay) { tooltip = "Play / stop (back to the selected pose)" }.Cls("vb-play");
            playButton.Add(new Image { image = EditorGUIUtility.IconContent("PlayButton").image });
            bar.Add(playButton);
            var middle = Add(bar, "vb-transport-middle");
            scrub = new Slider(0f, 1f).Cls("vb-scrub");
            scrub.RegisterValueChangedCallback(e => Scrub(e.newValue));
            middle.Add(scrub);
            var info = Add(middle, "vb-row");
            timeLabel = new Label().Cls("vb-caption", "vb-time");
            info.Add(timeLabel);
            info.Add(new VisualElement { style = { flexGrow = 1 } });
            info.Add(new Toggle("Loop") { bindingPath = nameof(VibrationsAnimation.loop) }.Cls("vb-loop"));
            bar.Add(MakeButton("Export Clip", Export, "vb-export"));
        }

        void BuildStrip(VisualElement parent)
        {
            var section = Add(parent, "vb-section");
            var head = Add(section, "vb-row");
            head.Add(new Label("POSES").Cls("vb-section-title"));
            head.Add(new VisualElement { style = { flexGrow = 1 } });
            globalButton = new Button(ToggleGlobalEdit) { tooltip = "Global Edit: rotate joints on this pose and add the same change to every pose" }.Cls("vb-tool");
            globalButton.Add(new Image { image = EditorGUIUtility.IconContent("CustomTool").image });
            head.Add(globalButton);
            globalBar = Add(section, "vb-global-bar");
            globalBar.Add(new Label("Global Edit: rotate joints on this pose, then apply the change to every pose (or the Shift/Cmd-clicked ones). ").Cls("vb-caption", "vb-global-text"));
            var globalRow = Add(globalBar, "vb-row");
            globalRow.Add(MakeButton("Apply to All", ApplyGlobalEdit, "vb-btn-row", "vb-global-apply"));
            globalRow.Add(MakeButton("Cancel", CancelGlobalEdit, "vb-btn-row"));
            var scroller = new ScrollView(ScrollViewMode.Horizontal) .Cls("vb-strip-scroll");
            section.Add(scroller);
            strip = Add(scroller.contentContainer, "vb-strip");
            strip.RegisterCallback<PointerMoveEvent>(OnStripPointerMove);
            strip.RegisterCallback<PointerUpEvent>(OnStripPointerUp);
            strip.RegisterCallback<PointerCaptureOutEvent>(e => { if (e.target == strip) EndDrag(); }); // focus lost mid-drag: cancel
            timingBar = new TimingBar(() => anim, () => selected, () => previewing ? previewTime : -1f, Select, () => so.Update());
            section.Add(timingBar);
            var timingRow = Add(section, "vb-row");
            timingRow.Add(MakeButton("Auto Timing", () => EditTiming("Auto timing", Tween.AutoTime), "vb-btn-row"));
            resetTimingButton = MakeButton("Reset Timing", () => EditTiming("Reset timing", Tween.ResetTiming), "vb-btn-row");
            timingRow.Add(resetTimingButton);
            timingRow.Add(Caption("Auto sets holds and snaps from how much each pose moves; the total length stays the same.").Cls("vb-timing-caption"));
        }

        void RebuildStrip()
        {
            strip.Clear();
            cardImages.Clear();
            cards.Clear();
            chips.Clear();
            int n = anim.poses.Count;
            picked.RemoveWhere(k => k >= n);
            for (int i = 0; i < n; i++)
            {
                strip.Add(Card(i));
                chips.Add(i < n - 1 || anim.loop ? TransitionChip(i, last: i == n - 1) : null);
                if (chips[i] != null) strip.Add(chips[i]);
            }
            addCard = MakeButton("", () => AddPose(selected), "vb-card", "vb-card--add");
            addCard.Add(new Label("+").Cls("vb-add-plus"));
            addCard.Add(new Label("Add pose").Cls("vb-card-meta"));
            addCard.tooltip = "Copy the selected pose as a new pose after it";
            strip.Add(addCard);
        }

        VisualElement Card(int i)
        {
            var p = anim.poses[i];
            var card = new VisualElement().Cls("vb-card");
            if (i == selected) card.AddToClassList("vb-card--selected");
            else if (picked.Contains(i)) card.AddToClassList("vb-card--picked");
            var image = new Image { image = i < thumbs.Count ? thumbs[i] : null, scaleMode = ScaleMode.ScaleToFit }.Cls("vb-thumb");
            cardImages.Add(image);
            card.Add(image);
            card.Add(new Label((i + 1).ToString()).Cls("vb-card-index"));
            card.Add(new Label(p.name).Cls("vb-card-name"));
            card.Add(new Label($"hold {p.hold:0.00}s").Cls("vb-card-meta"));
            DragToReorder(card, i);
            card.AddManipulator(new ContextualMenuManipulator(e =>
            {
                var targets = Targets(i);
                string many = targets.Count > 1 ? $" {targets.Count} Poses" : "";
                DropdownMenuAction.Status When(bool enabled) => enabled ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled;
                e.menu.AppendAction("Duplicate" + many, _ => DuplicatePoses(targets, mirrored: false, atEnd: false));
                e.menu.AppendAction("Duplicate" + many + " at End", _ => DuplicatePoses(targets, mirrored: false, atEnd: true));
                e.menu.AppendAction("Duplicate" + many + " Mirrored", _ => DuplicatePoses(targets, mirrored: true, atEnd: false));
                e.menu.AppendAction("Duplicate" + many + " Mirrored at End", _ => DuplicatePoses(targets, mirrored: true, atEnd: true));
                e.menu.AppendAction("Mirror" + many + " (L ↔ R)", _ => MirrorPoses(targets));
                e.menu.AppendSeparator();
                e.menu.AppendAction("Copy Pose", _ => { Commit(); clipboard = anim.poses[i].Clone(); }, When(targets.Count == 1));
                e.menu.AppendAction("Paste Pose", _ => PastePose(i), When(clipboard != null && targets.Count == 1));
                e.menu.AppendSeparator();
                e.menu.AppendAction("Move" + many + " Left", _ => MovePoses(targets, targets[0] - 1), When(targets[0] > 0));
                e.menu.AppendAction("Move" + many + " Right", _ => MovePoses(targets, targets[0] + 1), When(targets[0] + targets.Count < anim.poses.Count));
                e.menu.AppendSeparator();
                e.menu.AppendAction("Delete" + many, _ => DeletePoses(targets), When(anim.poses.Count > targets.Count));
            }));
            card.tooltip = "Click to edit, Shift-click to select a range, Cmd/Ctrl-click to add or remove one. Drag to reorder, right-click for more.";
            cards.Add(card);
            return card;
        }

        // The poses an action applies to: the multi-selection if the card is part of it, else just that card.
        List<int> Targets(int i) => picked.Count > 1 && picked.Contains(i) ? new List<int>(picked) : new List<int> { i };

        void Click(int i, bool shift, bool toggle)
        {
            if (shift && HasSelection) // range from the pose being edited, which stays the one on the rig
            {
                picked.Clear();
                for (int k = Mathf.Min(selected, i); k <= Mathf.Max(selected, i); k++) picked.Add(k);
            }
            else if (toggle && HasSelection && i != selected)
            {
                picked.Add(selected);
                if (!picked.Remove(i)) picked.Add(i);
            }
            else
            {
                Select(i);
                return;
            }
            signature = null;
            Refresh();
        }

        // Click selects; dragging past a few pixels hands the pointer to the strip, which runs the drag.
        void DragToReorder(VisualElement card, int i)
        {
            float startX = 0f;
            card.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                startX = e.position.x;
                dragGrab = (Vector2)e.position - card.worldBound.position;
                card.CapturePointer(e.pointerId);
            });
            card.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (card.HasPointerCapture(e.pointerId) && Mathf.Abs(e.position.x - startX) > 6f) BeginDrag(card, i, e.pointerId, e.position);
            });
            card.RegisterCallback<PointerUpEvent>(e =>
            {
                if (!card.HasPointerCapture(e.pointerId)) return;
                card.ReleasePointer(e.pointerId);
                Click(i, e.shiftKey, e.actionKey);
            });
        }

        void BeginDrag(VisualElement card, int i, int pointerId, Vector2 position)
        {
            dragSet = Targets(i);
            var size = new Vector2(card.resolvedStyle.width, card.resolvedStyle.height);
            strip.CapturePointer(pointerId);

            // Each card moves with its transition chip. The dragged ones step out and a slot of exactly their width
            // (a box per pose, a gap per chip) opens where they'll land, so nothing else collapses or shifts.
            dropSlot = new VisualElement { pickingMode = PickingMode.Ignore }.Cls("vb-drop-group");
            foreach (var k in dragSet)
            {
                var box = new VisualElement { pickingMode = PickingMode.Ignore }.Cls("vb-drop-slot");
                box.style.width = size.x;
                box.style.height = size.y;
                dropSlot.Add(box);
                cards[k].style.display = DisplayStyle.None;
                if (chips[k] == null) continue;
                var gap = new VisualElement { pickingMode = PickingMode.Ignore };
                gap.style.width = chips[k].resolvedStyle.width + chips[k].resolvedStyle.marginLeft + chips[k].resolvedStyle.marginRight;
                dropSlot.Add(gap);
                chips[k].style.display = DisplayStyle.None;
            }
            strip.Insert(strip.IndexOf(cards[dragSet[0]]), dropSlot);

            dragGhost = new VisualElement { pickingMode = PickingMode.Ignore }.Cls("vb-card", "vb-card--selected", "vb-drag-ghost");
            dragGhost.style.width = size.x;
            dragGhost.style.height = size.y;
            dragGhost.Add(new Image { image = i < thumbs.Count ? thumbs[i] : null, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore }.Cls("vb-thumb"));
            dragGhost.Add(new Label(dragSet.Count > 1 ? $"{dragSet.Count} poses" : anim.poses[i].name) { pickingMode = PickingMode.Ignore }.Cls("vb-card-name"));
            rootVisualElement.Add(dragGhost);
            MoveGhost(position);
        }

        void OnStripPointerMove(PointerMoveEvent e)
        {
            if (dragSet == null || !strip.HasPointerCapture(e.pointerId)) return;
            MoveGhost(e.position);
            int slot = SlotIndex(e.position.x);
            var visible = cards.Where((_, k) => !dragSet.Contains(k)).ToList();
            var before = slot < visible.Count ? visible[slot] : addCard;
            dropSlot.RemoveFromHierarchy();
            strip.Insert(strip.IndexOf(before), dropSlot);
        }

        void OnStripPointerUp(PointerUpEvent e)
        {
            if (dragSet == null || !strip.HasPointerCapture(e.pointerId)) return;
            var moved = dragSet;
            int slot = SlotIndex(e.position.x);
            strip.ReleasePointer(e.pointerId);
            EndDrag();
            MovePoses(moved, slot);
        }

        void MoveGhost(Vector2 pointer)
        {
            var local = rootVisualElement.WorldToLocal(pointer - dragGrab);
            dragGhost.style.left = local.x;
            dragGhost.style.top = local.y;
        }

        // Where the dragged poses go, counted among the poses that stay.
        int SlotIndex(float x)
        {
            int slot = 0;
            for (int k = 0; k < cards.Count; k++)
                if (!dragSet.Contains(k) && cards[k].worldBound.center.x < x) slot++;
            return slot;
        }

        void EndDrag()
        {
            if (dragSet == null) return;
            dragGhost?.RemoveFromHierarchy();
            dropSlot?.RemoveFromHierarchy();
            dragGhost = dropSlot = null;
            dragSet = null;
            signature = null; // rebuild the strip: shows cards and chips again
            Refresh();
        }

        VisualElement TransitionChip(int i, bool last)
        {
            var chip = new VisualElement().Cls("vb-link");
            if (anim.poses[i].customTransition) chip.AddToClassList("vb-link--custom");
            chip.Add(new Label(last ? "↺" : "→") .Cls("vb-link-arrow"));
            chip.Add(new Label($"{anim.TransitionOut(i).duration:0.00}s") .Cls("vb-link-time"));
            chip.tooltip = anim.poses[i].customTransition ? "Custom transition. Click to edit." : "Uses the animation's Feel. Click to customize.";
            chip.RegisterCallback<ClickEvent>(_ => Select(i));
            return chip;
        }

        void BuildPosePanel(VisualElement parent)
        {
            noSelection = new Label("Select a pose card above to edit it.").Cls("vb-hint", "vb-pad");
            parent.Add(noSelection);
            poseBox = Add(parent);
            var section = Section(poseBox, "POSE");
            poseTitle = section.Q<Label>(className: "vb-section-title");
            var name = new TextField("Name") .Cls("vb-field");
            section.Add(name);
            var hold = SliderRow(section, "Hold", 0f, 1f, "", "Seconds to stay on this pose before moving on.");
            var custom = new Toggle("Custom transition") { tooltip = "Override the Feel for the move out of this pose." }.Cls("vb-toggle");
            section.Add(custom);
            transitionBox = Add(section, "vb-indent");
            curves.Add(TransitionControls(transitionBox, "", () => HasSelection ? anim.TransitionOut(selected) : default));
            var tools = Section(poseBox, "TOOLS");
            var one = Add(tools, "vb-row");
            one.Add(new Label("Selected").Cls("vb-tools-label"));
            one.Add(MakeButton("Ground", () => EditEach(SelectedPoses(), "Ground pose", Ground), "vb-btn-row"));
            one.Add(MakeButton("Center", () => EditEach(SelectedPoses(), "Center pose", Center), "vb-btn-row"));
            one.Add(MakeButton("Mirror", () => MirrorPoses(SelectedPoses()), "vb-btn-row"));
            one.Add(MakeButton("Reset to Rest", () => EditEach(SelectedPoses(), "Reset pose", RestoreRest), "vb-btn-row"));
            var all = Add(tools, "vb-row");
            all.Add(new Label("All poses").Cls("vb-tools-label"));
            all.Add(MakeButton("Ground All", () => EditEach(AllPoses(), "Ground all poses", Ground), "vb-btn-row"));
            all.Add(MakeButton("Center All", () => EditEach(AllPoses(), "Center all poses", Center), "vb-btn-row"));
            all.Add(MakeButton("Mirror All", () => MirrorPoses(AllPoses()), "vb-btn-row"));
            all.Add(MakeButton("Fix All", () => EditEach(AllPoses(), "Fix all poses", () => { Center(); Ground(); }), "vb-btn-row"));
            tools.Add(Caption("Selected = the pose you're editing, or every pose you Shift/Cmd-clicked. " +
                              "Ground puts the lowest foot on the floor, Center puts the hips over the character, Fix does both."));
            tools.Add(new Label("In the Scene view: drag ● hands and feet, ■ hips (feet stay planted). Click any joint to rotate it with the rings.")
                .Cls("vb-hint"));
            poseFields.Add(name);
            poseFields.Add(hold);
            poseFields.Add(custom);
            name.userData = "name";
            hold.userData = "hold";
            custom.userData = "customTransition";
        }

        void RebindPosePanel()
        {
            if (!HasSelection) return;
            string prefix = $"poses.Array.data[{selected}].";
            foreach (var field in poseFields) ((IBindable)field).bindingPath = prefix + field.userData;
            foreach (var field in transitionBox.Query<BindableElement>().ToList())
                if (field.userData is string rel) field.bindingPath = prefix + "transition." + rel;
            poseBox.Bind(so);
        }

        void BuildFeel(VisualElement parent)
        {
            var presets = Section(parent, "PRESETS");
            var chips = Add(presets, "vb-chips");
            for (int i = 0; i < VibrationsAnimation.Presets.Length; i++)
            {
                int preset = i;
                var chip = MakeButton(VibrationsAnimation.Presets[i].name, () =>
                {
                    Undo.RecordObject(anim, "Apply preset");
                    anim.ApplyPreset(preset);
                    EditorUtility.SetDirty(anim);
                    so.Update();
                }, "vb-chip");
                presetChips.Add(chip);
                chips.Add(chip);
            }
            var transition = Section(parent, "TRANSITION");
            transition.Add(Caption("How every pose snaps to the next. A pose can override this in the Pose tab."));
            curves.Add(TransitionControls(transition, "transition.", () => anim != null ? anim.transition : default));
            var section = Section(parent, "TIMING");
            SliderRow(section, "Choppiness", 0f, 30f, nameof(VibrationsAnimation.stepRate),
                "Hold frames at this rate, like hand-drawn animation (12 = on twos). 0 = smooth.");
            SliderRow(section, "Overlap", 0f, 0.3f, nameof(VibrationsAnimation.overlap),
                "Seconds the hands and head trail behind the hips.");
            SliderRow(section, "Looseness", 0f, 1f, nameof(VibrationsAnimation.looseness),
                "Arms and head drag behind and keep swinging after the body snaps.");
            section.Add(Caption("Snappy body + high looseness = cartoony follow-through, like the arms of a Pixar walk."));
            var weights = new Foldout { text = "Body parts (overlap and looseness)", value = false }.Cls("vb-foldout");
            section.Add(weights);
            SliderRow(weights, "Spine", 0f, 2f, nameof(VibrationsAnimation.spineWeight), "");
            SliderRow(weights, "Head", 0f, 2f, nameof(VibrationsAnimation.headWeight), "");
            SliderRow(weights, "Arms", 0f, 2f, nameof(VibrationsAnimation.armsWeight), "");
            SliderRow(weights, "Legs", 0f, 2f, nameof(VibrationsAnimation.legsWeight), "Keep at 0 so feet plant on time.");
        }

        void BuildHumanize(VisualElement parent)
        {
            var section = Section(parent, "HUMANIZE");
            section.Add(Caption("Smart constraints that make poses and motion feel less robotic. Applied to preview and export."));
            section.Add(new Toggle("Humanize") { bindingPath = nameof(VibrationsAnimation.humanize) }.Cls("vb-toggle", "vb-toggle--big"));
            humanizeBox = Add(section, "vb-group");

            humanizeBox.Add(new Toggle("Joint limits") { bindingPath = nameof(VibrationsAnimation.jointLimits) }.Cls("vb-toggle"));
            humanizeBox.Add(Caption("Keeps every joint inside its natural range while you pose: no backward elbows or broken wrists."));
            SliderRow(humanizeBox, "Moving holds", 0f, 0.3f, nameof(VibrationsAnimation.movingHolds), "");
            humanizeBox.Add(Caption("Nobody freezes: during holds the body keeps creeping toward the next pose."));
            SliderRow(humanizeBox, "Inbetweens", 0f, 1f, nameof(VibrationsAnimation.inbetweens), "");
            humanizeBox.Add(Caption("Adds inbetween frames when two frames jump too far apart. Higher = smoother, less snappy."));
            var lifeRow = Add(humanizeBox, "vb-row");
            var life = SliderRow(lifeRow, "Life", 0f, 3f, nameof(VibrationsAnimation.life), "");
            life.style.flexGrow = 1;
            lifeRow.Add(MakeButton("Reroll", () =>
            {
                Undo.RecordObject(anim, "Reroll life");
                anim.seed = UnityEngine.Random.Range(1, 100000);
                EditorUtility.SetDirty(anim);
            }, "vb-btn-inline"));
            humanizeBox.Add(Caption("Subtle random sway on the spine, head and arms, in degrees. Loops seamlessly."));

            humanizeBox.Add(new Label("Follow-through").Cls("vb-section-title", "vb-subtitle"));
            humanizeBox.Add(Caption("Animates the spine and head from the hips' movement, so you only pose arms and legs. " +
                                    "When the body drops, the chin tilts up and the chest lags behind; sideways moves lean the torso."));
            SliderRow(humanizeBox, "Head follow", 0f, 1f, nameof(VibrationsAnimation.headFollow), "");
            SliderRow(humanizeBox, "Torso follow", 0f, 1f, nameof(VibrationsAnimation.torsoFollow), "");
            SliderRow(humanizeBox, "Springiness", 0f, 1f, nameof(VibrationsAnimation.springiness), "");
            humanizeBox.Add(Caption("Springiness: how much it overshoots and snaps back when the movement stops."));
        }

        static Label Caption(string text) => new Label(text).Cls("vb-caption");

        CurveView TransitionControls(VisualElement parent, string prefix, Func<Transition> source)
        {
            var curve = new CurveView(source);
            parent.Add(curve);
            var shape = new EnumField("Curve", Transition.Curve.Spring) { bindingPath = prefix + "curve" }.Cls("vb-field");
            shape.userData = "curve";
            parent.Add(shape);
            SliderRow(parent, "Snap time", 0.02f, 0.6f, prefix + "duration", "Seconds to reach the next pose. Short = snappy.").userData = "duration";
            var overshoot = SliderRow(parent, "Overshoot", 0f, 0.7f, prefix + "overshoot", "How far past the pose it swings, and how much it wobbles.");
            var anticipation = SliderRow(parent, "Anticipation", 0f, 0.5f, prefix + "anticipation", "Wind-up away from the pose before snapping.");
            overshoot.userData = "overshoot";
            anticipation.userData = "anticipation";
            parent.schedule.Execute(() => // overshoot and wind-up only exist on springs
            {
                bool spring = source().curve == Transition.Curve.Spring;
                overshoot.SetEnabled(spring);
                anticipation.SetEnabled(spring);
            }).Every(200);
            return curve;
        }

        void BuildScene(VisualElement parent)
        {
            var ghostSection = Section(parent, "ONION SKIN");
            ghostSection.Add(WindowToggle("Show previous pose", "Blue ghost of the previous pose while editing.", () => onion, v => onion = v));
            ghostSection.Add(WindowToggle("Show during preview", "While playing or scrubbing, the ghost shows the key pose the character is moving away from.",
                () => onionInPreview, v => { onionInPreview = v; previewGhost = -1; }).Cls("vb-indent"));
            var section = Section(parent, "FLOOR");
            section.Add(WindowToggle("Floor", "Show the floor, snap feet to it.", () => floor, v => floor = v));
            var floorRow = Add(section, "vb-row", "vb-indent");
            var height = new FloatField("Height") { value = floorHeight }.Cls("vb-field");
            height.style.flexGrow = 1;
            height.RegisterValueChangedCallback(e => { floorHeight = e.newValue; SceneView.RepaintAll(); });
            height.schedule.Execute(() => height.SetValueWithoutNotify(floorHeight)).Every(300);
            floorRow.Add(height);
            floorRow.Add(MakeButton("From Feet", () => { floorHeight = LowestPoint(); SceneView.RepaintAll(); }, "vb-btn-inline"));
            floorRow.Add(MakeButton("From Scene", () =>
            {
                var ground = SceneGround();
                if (ground != null) floorHeight = ground.Value;
                else ShowNotification(new GUIContent("No ground collider under the character"));
                SceneView.RepaintAll();
            }, "vb-btn-inline"));
            section.Add(WindowToggle("Keep grounded", "After each edit, move the body so the lowest foot touches the floor. Turn off for jumps.",
                () => keepGrounded, v => keepGrounded = v));
        }

        void BuildSettings(VisualElement parent)
        {
            var foldout = Section(parent, "PROJECT SETTINGS");
            foldout.Add(Caption("Shared by everyone on this project."));
            var s = VibrationsSettings.instance;
            var fps = new DropdownField("Clip frame rate", new List<string> { "30 fps", "60 fps" }, s.frameRate == 60 ? 1 : 0) .Cls("vb-field");
            fps.RegisterValueChangedCallback(_ => { s.frameRate = fps.index == 1 ? 60 : 30; s.SaveSettings(); });
            var names = new List<string>();
            foreach (var p in VibrationsAnimation.Presets) names.Add(p.name);
            var preset = new DropdownField("Default feel", names, s.defaultPreset) .Cls("vb-field");
            preset.RegisterValueChangedCallback(_ => { s.defaultPreset = preset.index; s.SaveSettings(); });
            var folder = new TextField("Export folder") { value = s.exportFolder }.Cls("vb-field");
            folder.RegisterValueChangedCallback(e => { s.exportFolder = e.newValue; s.SaveSettings(); });
            foldout.Add(fps);
            foldout.Add(preset);
            foldout.Add(folder);
            var templates = new TextField("Templates folder") { value = s.templatesFolder }.Cls("vb-field");
            templates.RegisterValueChangedCallback(e => { s.templatesFolder = e.newValue; s.SaveSettings(); templatesDirty = true; });
            foldout.Add(templates);

            var ai = Section(parent, "AI ASSISTANT");
            ai.Add(Caption("Saved on this computer only (Unity EditorPrefs), never in the project. " +
                           "Prompts send the animation's poses and settings to the provider you pick."));
            var provider = new EnumField("Provider", AiAgent.Provider).Cls("vb-field");
            var model = new TextField("Model") { value = AiAgent.GetModel(AiAgent.Provider) }.Cls("vb-field");
            var key = new TextField("API key") { isPasswordField = true, value = AiAgent.GetKey(AiAgent.Provider) }.Cls("vb-field");
            provider.RegisterValueChangedCallback(e =>
            {
                AiAgent.Provider = (AiProvider)e.newValue;
                model.SetValueWithoutNotify(AiAgent.GetModel(AiAgent.Provider));
                key.SetValueWithoutNotify(AiAgent.GetKey(AiAgent.Provider));
            });
            model.RegisterValueChangedCallback(e => AiAgent.SetModel(AiAgent.Provider, e.newValue));
            key.RegisterValueChangedCallback(e => AiAgent.SetKey(AiAgent.Provider, e.newValue));
            ai.Add(provider);
            ai.Add(model);
            ai.Add(key);
        }

        // --- Templates ---

        TemplateRig TemplateContext() => new()
        {
            root = animator.transform, bones = bones, handler = humanHandler, rest = rest, soles = soles, floorHeight = floorHeight, humanScale = animator.humanScale,
            lowestPoint = poseRenderer != null ? LowestPoint : null,
        };

        void BuildTemplates(VisualElement parent)
        {
            templatesHint = new Label("Pick a Humanoid character in the Animate tab.").Cls("vb-caption", "vb-pad");
            parent.Add(templatesHint);
            templatesContent = Add(parent);
            var starters = Section(templatesContent, "STARTERS");
            starters.Add(Caption("Click a template to create a new animation from it. Templates work on any Humanoid character. " +
                                 "Right-click to replace the poses of the current animation instead."));
            builtInGrid = Add(starters, "vb-grid");
            var mine = Section(templatesContent, "MY TEMPLATES");
            mine.Add(Caption("Saved in the project, so the whole team can use them."));
            saveTemplateButton = MakeButton("Save Current Animation as Template", SaveTemplate, "vb-btn-row");
            mine.Add(saveTemplateButton);
            userGrid = Add(mine, "vb-grid");
        }

        void RebuildTemplates()
        {
            templatesDirty = false;
            builtInGrid.Clear();
            userGrid.Clear();
            var saved = Rig.SaveAll(animator.transform);
            foreach (var t in BuiltInTemplates.All) builtInGrid.Add(TemplateCard(t, false));
            foreach (var guid in AssetDatabase.FindAssets("t:" + nameof(VibrationsTemplate)))
            {
                var t = AssetDatabase.LoadAssetAtPath<VibrationsTemplate>(AssetDatabase.GUIDToAssetPath(guid));
                if (t != null && t.poses.Count > 0) userGrid.Add(TemplateCard(t, true));
            }
            if (userGrid.childCount == 0) userGrid.Add(Caption("No templates yet."));
            Rig.RestoreAll(saved);
        }

        VisualElement TemplateCard(VibrationsTemplate t, bool user)
        {
            var card = new VisualElement { tooltip = t.description }.Cls("vb-card", "vb-template");
            if (!templateThumbs.TryGetValue(t, out var rt) && poseRenderer != null)
            {
                rt = new RenderTexture(176, 232, 24) { hideFlags = HideFlags.HideAndDontSave };
                TemplateContext().Apply(t.poses[t.poses.Count / 2]);
                scratch = poseRenderer.Bake(scratch);
                poseRenderer.RenderThumbnail(scratch, rt, ThumbnailFrame(), ThumbnailView(), ThumbColor);
                templateThumbs[t] = rt;
            }
            card.Add(new Image { image = rt, scaleMode = ScaleMode.ScaleToFit }.Cls("vb-thumb"));
            card.Add(new Label(t.name).Cls("vb-card-name"));
            card.Add(new Label($"{t.poses.Count} poses · {(t.Loops ? "loop" : "once")}").Cls("vb-card-meta"));
            card.RegisterCallback<ClickEvent>(_ => CreateFromTemplate(t));
            card.AddManipulator(new ContextualMenuManipulator(e =>
            {
                e.menu.AppendAction("New Animation from Template", _ => CreateFromTemplate(t));
                e.menu.AppendAction("Replace Poses in Current Animation", _ => ReplacePoses(t),
                    Ready ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
                if (!user) return;
                e.menu.AppendSeparator();
                e.menu.AppendAction("Show in Project", _ => EditorGUIUtility.PingObject(t));
                e.menu.AppendAction("Delete Template", _ =>
                {
                    if (!EditorUtility.DisplayDialog("Delete template", $"Delete '{t.name}'? This can't be undone.", "Delete", "Cancel")) return;
                    AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(t));
                    templatesDirty = true;
                });
            }));
            return card;
        }

        void CreateFromTemplate(VibrationsTemplate t)
        {
            if (bones == null) return;
            var folder = VibrationsSettings.instance.exportFolder;
            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();
            var path = EditorUtility.SaveFilePanelInProject("New animation from template", t.name, "asset", "", folder);
            if (string.IsNullOrEmpty(path)) return;
            Stop();
            Commit();
            var saved = Rig.SaveAll(animator.transform);
            var a = TemplateContext().CreateAnimation(t);
            Rig.RestoreAll(saved);
            a.avatar = animator.avatar;
            AssetDatabase.CreateAsset(a, path);
            SetAnimation(a);
            tabView.selectedTabIndex = 0;
        }

        void ReplacePoses(VibrationsTemplate t) => EditPoses("Apply template", () =>
        {
            var saved = Rig.SaveAll(animator.transform);
            var a = TemplateContext().CreateAnimation(t);
            Rig.RestoreAll(saved);
            anim.poses = a.poses;
            DestroyImmediate(a);
            selected = 0;
            tabView.selectedTabIndex = 0;
        });

        void SaveTemplate()
        {
            if (!Ready || anim.poses.Count == 0) return;
            Stop();
            Commit();
            var folder = VibrationsSettings.instance.templatesFolder;
            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();
            var path = EditorUtility.SaveFilePanelInProject("Save as template", anim.name, "asset", "Save this animation as a reusable template.", folder);
            if (string.IsNullOrEmpty(path)) return;
            var saved = Rig.SaveAll(animator.transform);
            var t = TemplateContext().CreateTemplate(anim);
            Rig.RestoreAll(saved);
            t.description = $"Saved from {anim.name}.";
            AssetDatabase.CreateAsset(t, path);
            AssetDatabase.SaveAssets();
            templatesDirty = true;
            ApplySelected();
            ShowNotification(new GUIContent($"Saved template {t.name}"));
        }

        // Copies an animation made on another rig onto this character, through muscle space like a template.
        // The source rig comes from the model its avatar was imported with, so it doesn't need to be in the scene.
        void ConvertAnimation()
        {
            var source = anim;
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GetAssetPath(source.avatar));
            if (model == null)
            {
                EditorUtility.DisplayDialog("Make a copy", $"Can't find the model that {source.avatar.name} comes from. " +
                    "Open the animation on its original character and use Save Current Animation as Template instead.", "OK");
                return;
            }
            var path = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(Path.GetDirectoryName(AssetDatabase.GetAssetPath(source)),
                $"{source.name} ({animator.name}).asset"));

            var instance = (GameObject)Instantiate(model);
            instance.hideFlags = HideFlags.HideAndDontSave;
            VibrationsTemplate template = null;
            try
            {
                var a = instance.GetComponentInChildren<Animator>() ?? instance.AddComponent<Animator>();
                a.avatar = source.avatar;
                var rest = new Pose();
                var sourceBones = Rig.Bind(a);
                Rig.Capture(sourceBones, rest);
                using var renderer = new PoseRenderer(a);
                using var handler = new HumanPoseHandler(a.avatar, a.transform);
                PoseRenderer.Snapshot snapshot = null;
                float Lowest() => renderer.Lowest(ref snapshot);
                template = new TemplateRig
                {
                    root = a.transform, bones = sourceBones, handler = handler, rest = rest, soles = new float[Rig.SoleBones.Length],
                    floorHeight = Lowest(), humanScale = a.humanScale > 0f ? a.humanScale : 1f, lowestPoint = Lowest,
                }.CreateTemplate(source);
                snapshot?.Dispose();
            }
            finally { DestroyImmediate(instance); }

            var saved = Rig.SaveAll(animator.transform);
            var copy = TemplateContext().CreateAnimation(template);
            Rig.RestoreAll(saved);
            DestroyImmediate(template);
            copy.avatar = animator.avatar;
            AssetDatabase.CreateAsset(copy, path);
            AssetDatabase.SaveAssets();
            SetAnimation(copy);
            ShowNotification(new GUIContent($"Created {Path.GetFileName(path)}"));
        }

        // --- AI assistant ---

        static readonly (string label, string prompt)[] QuickPrompts =
        {
            ("Fill in-betweens", "Add breakdown poses between my key poses so the motion reads better: arcs, weight shift, follow-through. Keep my key poses as they are."),
            ("Fix timing", "Improve the timing: vary the holds and snap times so the motion has a rhythm instead of being even."),
            ("Snappier", "Make it snappier and more cartoony without changing the poses."),
            ("Softer", "Make it softer and more natural, closer to realistic animation."),
            ("Pixar feel", "Give it a Pixar-like feel: snappy body, arms that drag and swing through, uneven timing with a clear rhythm."),
        };

        void BuildAi(VisualElement parent)
        {
            var section = Section(parent, "PROMPT");
            section.Add(Caption("Describe what you want. The AI reads the poses and settings, then edits them with Vibrations' tools: " +
                                "fill in-betweens, tune the feel, fix timing. The whole run is one undo step."));
            var chips = Add(section, "vb-chips");
            foreach (var (label, prompt) in QuickPrompts) chips.Add(MakeButton(label, () => aiPrompt.value = prompt, "vb-chip"));
            aiPrompt = new TextField { multiline = true }.Cls("vb-prompt");
            section.Add(aiPrompt);
            var row = Add(section, "vb-row");
            aiSend = MakeButton("Send", SendPrompt, "vb-btn-row");
            row.Add(aiSend);
            aiStatus = new Label().Cls("vb-caption", "vb-ai-status");
            row.Add(aiStatus);
            aiLog = Add(Section(parent, "CONVERSATION"), "vb-ai-log");
        }

        async void SendPrompt()
        {
            if (aiBusy)
            {
                aiCancel?.Cancel();
                return;
            }
            var provider = AiAgent.Provider;
            var key = AiAgent.GetKey(provider);
            if (string.IsNullOrEmpty(key))
            {
                aiStatus.text = $"Add your {provider} API key in the Settings tab.";
                return;
            }
            var prompt = aiPrompt.value?.Trim();
            if (string.IsNullOrEmpty(prompt) || !Ready) return;

            Stop();
            Commit();
            AddLog("user", prompt);
            aiPrompt.value = "";
            aiBusy = true;
            aiCancel = new CancellationTokenSource();
            aiSend.text = "Cancel";
            aiStatus.text = $"Working with {AiAgent.GetModel(provider)}…";
            SetLocked(true);
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Vibrations AI");
            var saved = Rig.SaveAll(animator.transform);
            var tools = new AiTools(anim, TemplateContext(), poseRenderer, animator.transform, ThumbnailFrame(), VibrationsSettings.instance.frameRate);
            try
            {
                await AiAgent.RunAsync(provider, key, AiAgent.GetModel(provider), prompt, tools, AddLog, AddImage, aiCancel.Token);
            }
            catch (OperationCanceledException) { AddLog("error", "Cancelled."); }
            catch (Exception e) { AddLog("error", e.Message); }
            finally
            {
                Undo.CollapseUndoOperations(group);
                Rig.RestoreAll(saved);
                tools.Dispose();
            }
            if (this == null) return; // window closed while the AI was working
            aiBusy = false;
            aiSend.text = "Send";
            aiStatus.text = "";
            SetLocked(false);
            selected = Mathf.Clamp(selected, 0, anim.poses.Count - 1);
            so.Update();
            thumbsDirty = true;
            frames = null;
            signature = null;
            ApplySelected();
            Refresh();
        }

        void AddLog(string role, string text)
        {
            if (aiLog == null) return;
            var prefix = role switch { "user" => "You: ", "tool" => "• ", "error" => "⚠ ", _ => "" };
            aiLog.Add(new Label(prefix + text).Cls("vb-ai-msg", "vb-ai-" + role));
        }

        void AddImage(byte[] png)
        {
            if (aiLog == null) return;
            var texture = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
            texture.LoadImage(png);
            aiImages.Add(texture);
            var image = new Image { image = texture, scaleMode = ScaleMode.ScaleToFit }.Cls("vb-ai-image");
            image.style.height = Mathf.Min(texture.height, 220);
            aiLog.Add(image);
        }

        void SetLocked(bool locked)
        {
            main.SetEnabled(!locked);
            templatesContent.SetEnabled(!locked);
            foreach (var (_, content) in gated) content.SetEnabled(!locked);
        }

        void BuildSupport(VisualElement parent)
        {
            var section = Section(parent, "SUPPORT MEOWARTS");
            section.Add(Caption("Vibrations is made by Meowarts. If it helps you, check out our game and follow us."));
            section.Add(MakeButton("La Pizza Del Gatto on Steam", () => Application.OpenURL("https://store.steampowered.com/app/4022740/La_Pizza_Del_Gatto/"), "vb-btn-row", "vb-link-button"));
            section.Add(MakeButton("@meowartsgames on Instagram", () => Application.OpenURL("https://www.instagram.com/meowartsgames/"), "vb-btn-row", "vb-link-button"));
        }

        void Refresh()
        {
            if (bound == null) return;
            bool ready = Ready;
            main.style.display = ready ? DisplayStyle.Flex : DisplayStyle.None;
            empty.style.display = ready ? DisplayStyle.None : DisplayStyle.Flex;
            foreach (var (hint, content) in gated)
            {
                hint.style.display = ready ? DisplayStyle.None : DisplayStyle.Flex;
                content.style.display = ready ? DisplayStyle.Flex : DisplayStyle.None;
            }
            emptyText.text = animator == null ? "Pick a character to start."
                : bones == null ? "This character needs a Humanoid avatar: model import settings → Rig → Humanoid."
                : Foreign ? $"This animation was made for {anim.avatar.name}. Poses don't carry over between rigs as they are, but a copy can be converted for {animator.name}."
                : "Create or pick an animation.";
            convertButton.style.display = Foreign && bones != null ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (var step in empty.Query(className: "vb-step").ToList()) step.style.display = Foreign ? DisplayStyle.None : DisplayStyle.Flex;
            characterField.SetValueWithoutNotify(animator);
            templatesHint.style.display = bones == null ? DisplayStyle.Flex : DisplayStyle.None;
            templatesContent.style.display = bones == null ? DisplayStyle.None : DisplayStyle.Flex;
            saveTemplateButton?.SetEnabled(ready);
            bool hasKey = !string.IsNullOrEmpty(AiAgent.GetKey(AiAgent.Provider));
            aiKeyWarning.style.display = hasKey ? DisplayStyle.None : DisplayStyle.Flex;
            aiHint.style.display = hasKey && !ready ? DisplayStyle.Flex : DisplayStyle.None;
            aiContent.style.display = hasKey && ready ? DisplayStyle.Flex : DisplayStyle.None;
            if (templatesDirty && bones != null && poseRenderer != null && !previewing) RebuildTemplates();
            if (!ready) return;

            so.UpdateIfRequiredOrScript();
            var sig = Signature();
            if (sig != signature)
            {
                signature = sig;
                thumbsDirty = true;
                RebuildStrip();
                RebindPosePanel();
            }
            RefreshRendering();

            float len = Tween.Length(anim);
            scrub.highValue = Mathf.Max(len, 0.01f);
            playButton.EnableInClassList("vb-play--on", playing);
            globalButton.EnableInClassList("vb-tool--on", GlobalEditing);
            globalBar.style.display = GlobalEditing ? DisplayStyle.Flex : DisplayStyle.None;
            if (!previewing) timeLabel.text = $"{len:0.00}s · {anim.poses.Count} poses";
            noSelection.style.display = HasSelection ? DisplayStyle.None : DisplayStyle.Flex;
            poseBox.style.display = HasSelection ? DisplayStyle.Flex : DisplayStyle.None;
            humanizeBox.SetEnabled(anim.humanize);
            if (HasSelection)
            {
                poseTitle.text = $"POSE {selected + 1} · {anim.poses[selected].name.ToUpperInvariant()}";
                transitionBox.style.display = anim.poses[selected].customTransition ? DisplayStyle.Flex : DisplayStyle.None;
            }
            for (int i = 0; i < presetChips.Count; i++)
            {
                var (_, tr, step, overlap, loose) = VibrationsAnimation.Presets[i];
                bool on = tr.Equals(anim.transition) && step == anim.stepRate && overlap == anim.overlap && loose == anim.looseness;
                presetChips[i].EnableInClassList("vb-chip--on", on);
            }
            foreach (var c in curves) c.MarkDirtyRepaint();
            timingBar.MarkDirtyRepaint();
            resetTimingButton.SetEnabled(Tween.HasSavedTiming(anim));
        }

        string Signature()
        {
            var sb = new StringBuilder().Append(selected).Append(anim.loop).Append(string.Join(",", picked));
            foreach (var p in anim.poses)
                sb.Append('|').Append(RuntimeHelpers.GetHashCode(p)).Append(p.name).Append(p.hold).Append(p.customTransition).Append(p.transition.duration);
            sb.Append(anim.transition.duration);
            return sb.ToString();
        }

        // --- UI helpers ---

        static VisualElement Add(VisualElement parent, params string[] classes)
        {
            var e = new VisualElement();
            foreach (var c in classes) e.AddToClassList(c);
            parent.Add(e);
            return e;
        }

        static VisualElement Section(VisualElement parent, string title)
        {
            var s = Add(parent, "vb-section");
            s.Add(new Label(title) .Cls("vb-section-title"));
            return s;
        }

        static Button MakeButton(string text, Action onClick, params string[] classes)
        {
            var b = new Button(onClick) { text = text };
            foreach (var c in classes) b.AddToClassList(c);
            return b;
        }

        static Slider SliderRow(VisualElement parent, string label, float min, float max, string path, string tooltip)
        {
            var s = new Slider(label, min, max) { bindingPath = path, showInputField = true, tooltip = tooltip }.Cls("vb-slider");
            parent.Add(s);
            return s;
        }

        static void Step(VisualElement parent, string number, string text)
        {
            var row = Add(parent, "vb-row", "vb-step");
            row.Add(new Label(number) .Cls("vb-step-number"));
            row.Add(new Label(text) .Cls("vb-step-text"));
        }

        static Toggle WindowToggle(string label, string tooltip, Func<bool> get, Action<bool> set)
        {
            var t = new Toggle(label) { value = get(), tooltip = tooltip }.Cls("vb-toggle");
            t.RegisterValueChangedCallback(e => { set(e.newValue); SceneView.RepaintAll(); });
            return t;
        }

        // --- Scene view ---

        void OnSceneGUI(SceneView view)
        {
            if (bones == null || animator == null || aiBusy) return;
            if (floor) DrawFloor();
            if (GlobalEditing && baselineGhost != null && Event.current.type == EventType.Repaint)
            {
                GL.Clear(true, false, Color.clear); // ghost draws over the character it overlaps
                poseRenderer.Draw(baselineGhost, GlobalGhostColor, -view.camera.transform.forward);
            }
            else if (onion && ghost != null && (previewing ? onionInPreview : HasSelection) && Event.current.type == EventType.Repaint)
            {
                GL.Clear(true, false, Color.clear);
                poseRenderer.Draw(ghost, GhostColor, -view.camera.transform.forward);
            }
            if (previewing || !HasSelection) return;

            var root = animator.transform;
            bool changed = false;

            foreach (var (a, b, c, leg) in Limbs)
            {
                if (!bones[a] || !bones[b] || !bones[c]) continue;
                var pos = bones[c].position;
                int id = GUIUtility.GetControlID(FocusType.Passive);
                Handles.color = activeBone == c ? Color.white : leg ? Cool : Accent;
                EditorGUI.BeginChangeCheck();
                var target = Handles.FreeMoveHandle(id, pos, HandleUtility.GetHandleSize(pos) * 0.08f, Vector3.zero, Handles.SphereHandleCap);
                if (GUIUtility.hotControl == id) activeBone = c;
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObjects(undoBones, "Move limb");
                    if (leg && floor) target.y = SnapToFloor(target.y, soles[c == 16 ? 0 : 2]);
                    Rig.SolveTwoBone(bones[a], bones[b], bones[c], target, leg ? root.forward : -root.forward, keepEndWorldRotation: leg);
                    changed = true;
                }
            }

            // Hips: move the body, re-solve legs so feet stay planted.
            var hips = bones[0];
            int hipsId = GUIUtility.GetControlID(FocusType.Passive);
            Handles.color = activeBone == 0 ? Color.white : Color.yellow;
            EditorGUI.BeginChangeCheck();
            var hipsTarget = Handles.FreeMoveHandle(hipsId, hips.position, HandleUtility.GetHandleSize(hips.position) * 0.1f, Vector3.zero, Handles.CubeHandleCap);
            if (GUIUtility.hotControl == hipsId) activeBone = 0;
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObjects(undoBones, "Move hips");
                var feet = new (Vector3 pos, Quaternion rot)[Limbs.Length];
                for (int i = 0; i < Limbs.Length; i++)
                    if (bones[Limbs[i].c]) feet[i] = (bones[Limbs[i].c].position, bones[Limbs[i].c].rotation);
                hips.position = hipsTarget;
                for (int i = 0; i < Limbs.Length; i++)
                {
                    var (a, b, c, leg) = Limbs[i];
                    if (!leg || !bones[a] || !bones[b] || !bones[c]) continue;
                    bones[c].rotation = feet[i].rot;
                    Rig.SolveTwoBone(bones[a], bones[b], bones[c], feet[i].pos, root.forward, keepEndWorldRotation: true);
                }
                changed = true;
            }

            foreach (var i in Joints)
            {
                if (!bones[i]) continue;
                var pos = bones[i].position;
                float size = HandleUtility.GetHandleSize(pos) * 0.035f;
                Handles.color = activeBone == i ? Accent : new Color(1f, 1f, 1f, 0.8f);
                if (Handles.Button(pos, Quaternion.identity, size, size * 1.6f, Handles.DotHandleCap)) activeBone = i;
            }

            // Rotation rings on the active bone (local axes, front half only).
            if (activeBone >= 0 && bones[activeBone])
            {
                var t = bones[activeBone];
                float radius = HandleUtility.GetHandleSize(t.position) * 0.45f;
                for (int k = 0; k < 3; k++)
                {
                    var axis = t.rotation * (k == 0 ? Vector3.right : k == 1 ? Vector3.up : Vector3.forward);
                    Handles.color = AxisColors[k];
                    EditorGUI.BeginChangeCheck();
                    var r = Handles.Disc(t.rotation, t.position, axis, radius, true, 0f);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObjects(undoBones, "Rotate bone");
                        t.rotation = r;
                        changed = true;
                    }
                }
            }

            if (changed && anim.humanize && anim.jointLimits) Rig.ClampToLimits(humanHandler, bones, animator.transform);
            if (changed && floor && keepGrounded) Ground();
        }

        void DrawFloor()
        {
            var center = animator.transform.position;
            center.y = floorHeight + 0.001f * animator.humanScale; // a hair above, so a ground plane at the same height doesn't hide it
            float extent = 1.5f * animator.humanScale, step = 0.25f * animator.humanScale;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;
            Handles.color = new Color(1f, 1f, 1f, 0.05f);
            Handles.DrawSolidDisc(center, Vector3.up, extent);
            Handles.color = new Color(1f, 1f, 1f, 0.12f);
            for (float d = -extent; d <= extent + 1e-4f; d += step)
            {
                float half = Mathf.Sqrt(Mathf.Max(extent * extent - d * d, 0f)); // grid clipped to the disc
                Handles.DrawLine(center + new Vector3(d, 0, -half), center + new Vector3(d, 0, half));
                Handles.DrawLine(center + new Vector3(-half, 0, d), center + new Vector3(half, 0, d));
            }
            // Contact marks under feet that touch the floor.
            Handles.color = Accent;
            for (int k = 0; k < Rig.SoleBones.Length; k += 2)
            {
                var foot = bones[Rig.SoleBones[k]];
                if (foot && Mathf.Abs(foot.position.y - soles[k] - floorHeight) < 0.01f * animator.humanScale)
                    Handles.DrawWireDisc(new Vector3(foot.position.x, floorHeight, foot.position.z), Vector3.up, 0.08f * animator.humanScale, 2f);
            }
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
        }
    }

    // Plot of the transition ease: where the move starts (A), where it lands (B), the wind-up dip and the overshoot.
    class CurveView : VisualElement
    {
        const float XMax = 3f, YMin = -0.6f, YMax = 1.8f;
        readonly Func<Transition> source;

        public CurveView(Func<Transition> source)
        {
            this.source = source;
            AddToClassList("vb-curve");
            Add(new Label("B") { style = { top = Length.Percent((YMax - 1f) / (YMax - YMin) * 100f - 7f) } }.Cls("vb-curve-label"));
            Add(new Label("A") { style = { top = Length.Percent(YMax / (YMax - YMin) * 100f - 7f) } }.Cls("vb-curve-label"));
            generateVisualContent += Draw;
        }

        void Draw(MeshGenerationContext ctx)
        {
            var r = contentRect;
            if (r.width < 10f || r.height < 10f) return;
            var tr = source();
            var p = ctx.painter2D;
            Vector2 Point(float x, float y) => new(r.x + 18f + x / XMax * (r.width - 26f), r.y + (YMax - y) / (YMax - YMin) * r.height);

            var stroke = resolvedStyle.color; // theme highlight, set in Vibrations.uss
            p.lineWidth = 1f;
            p.strokeColor = new Color(stroke.r, stroke.g, stroke.b, 0.25f);
            foreach (var y in new[] { 0f, 1f })
            {
                p.BeginPath();
                p.MoveTo(Point(0f, y));
                p.LineTo(Point(XMax, y));
                p.Stroke();
            }

            p.lineWidth = 2f;
            p.lineJoin = LineJoin.Round;
            p.lineCap = LineCap.Round;
            p.strokeColor = stroke;
            p.BeginPath();
            for (int i = 0; i <= 160; i++)
            {
                float x = i / 160f * XMax;
                var pt = Point(x, Mathf.Clamp(Tween.Ease(x, tr), YMin, YMax));
                if (i == 0) p.MoveTo(pt);
                else p.LineTo(pt);
            }
            p.Stroke();
        }
    }

    static class ElementExtensions
    {
        public static T Cls<T>(this T e, params string[] classes) where T : VisualElement
        {
            foreach (var c in classes) e.AddToClassList(c);
            return e;
        }
    }
}
