using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Vibrations
{
    // The tools an AI model can call to read and edit the current animation. Poses go over the wire as named
    // Humanoid muscle values (like templates), so the model works in a readable, rig-independent space.
    // Leaves the rig posed: wrap a run in Rig.SaveAll / Rig.RestoreAll.
    public sealed class AiTools
    {
        public const string SystemPrompt =
@"You are the animation assistant inside Vibrations, a Unity tool for pose-to-pose toon animation on Humanoid characters.
The animation is a list of key poses. Between poses the tool tweens with the transition settings (Spring = snap with overshoot and wind-up, Linear, Smooth), plus overlap (extremities trail), looseness (extremities drag and keep swinging), choppiness (stepRate: hold frames like stop-motion, 12 = on twos) and Humanize options.

Poses are Humanoid muscle values in [-1, 1]. Rule: the second word of a muscle name is its + direction.
- Upper Leg Front-Back / Arm Front-Back: + back, - forward. Lower Leg Stretch / Forearm Stretch: +1 straight, 0 = about 100 degrees bent, -1 fully bent.
- Arm Down-Up: + up, - down. Spine/Chest/UpperChest Front-Back: + lean back, - bend forward. Head/Neck Nod Down-Up: + up. Foot Up-Down: + toes down.
- 0 is NOT a T-pose: all-zero bends knees and elbows. Standing straight is about: Upper Leg Front-Back 0.6, Lower Leg Stretch 0.95, Arm Down-Up -0.75, Arm Front-Back 0.3, Forearm Stretch 0.75.
- Feet are grounded on the floor automatically. Use lift (meters, avatar-scaled) only for airborne poses.
- To mirror a pose, swap Left and Right muscle names and negate center Left-Right and Twist muscles.

Animation craft: vary timing (key poses held longer, breakdowns short; a walk drops fast into the down pose and pushes up slower), keep arcs, favour clear silhouettes. With stepRate 12, prefer holds and snap times in multiples of 1/12 s so every pose lands on a frame.

Always call get_animation first. After editing poses, call render_preview (mode poses) to check silhouettes, balance and that feet touch the floor; use mode motion to judge spacing and timing. Fix what looks wrong before replying, but don't render more than needed: each image costs tokens.
Make purposeful changes with the tools, then reply with one or two short sentences about what you changed.";

        public sealed class ToolResult
        {
            public string text;
            public byte[] image; // PNG, or null
            public bool IsError => text.StartsWith("Error");
            public static implicit operator ToolResult(string text) => new() { text = text };
        }

        public static readonly (string name, string description, string schema)[] Definitions =
        {
            ("get_animation", "Read the animation: settings and every pose (index, name, hold, lift, transition, muscles).",
                @"{""type"":""object"",""properties"":{}}"),
            ("add_pose", "Insert a new pose. Starts from pose_a (or a blend of pose_a and pose_b), then applies muscle overrides. Use it to fill in-betweens and breakdowns.",
                @"{""type"":""object"",""properties"":{
                    ""after_index"":{""type"":""integer"",""description"":""Insert after this pose index; -1 inserts first.""},
                    ""name"":{""type"":""string""},
                    ""hold"":{""type"":""number"",""description"":""Seconds to hold the pose.""},
                    ""pose_a"":{""type"":""integer"",""description"":""Base pose index. Defaults to after_index.""},
                    ""pose_b"":{""type"":""integer"",""description"":""Optional second pose to blend toward.""},
                    ""blend"":{""type"":""number"",""description"":""0 = pose_a, 1 = pose_b. Default 0.5.""},
                    ""muscles"":{""type"":""object"",""additionalProperties"":{""type"":""number""},""description"":""Muscle name -> value overrides.""},
                    ""lift"":{""type"":""number""},
                    ""transition"":" + TransitionSchema + @"},
                ""required"":[""after_index""]}"),
            ("update_pose", "Edit an existing pose: muscle overrides, name, hold, lift, and its own transition (or custom_transition false to use the animation's).",
                @"{""type"":""object"",""properties"":{
                    ""index"":{""type"":""integer""},
                    ""name"":{""type"":""string""},
                    ""hold"":{""type"":""number""},
                    ""muscles"":{""type"":""object"",""additionalProperties"":{""type"":""number""}},
                    ""lift"":{""type"":""number""},
                    ""transition"":" + TransitionSchema + @",
                    ""custom_transition"":{""type"":""boolean""}},
                ""required"":[""index""]}"),
            ("delete_pose", "Remove a pose.",
                @"{""type"":""object"",""properties"":{""index"":{""type"":""integer""}},""required"":[""index""]}"),
            ("render_preview", "Render the animation to an image to check it visually. mode 'poses': one column per key pose (left to right = pose 0, 1, ...), rows = front, side and three-quarter views. mode 'motion': the final tweened animation (with overlap, looseness, Humanize) sampled into evenly spaced frames. Orange line = floor.",
                @"{""type"":""object"",""properties"":{
                    ""mode"":{""type"":""string"",""enum"":[""poses"",""motion""]},
                    ""frames"":{""type"":""integer"",""description"":""motion only: 4-24, default 12.""},
                    ""view"":{""type"":""string"",""enum"":[""side"",""front"",""three_quarter""],""description"":""motion only, default side.""}},
                ""required"":[""mode""]}"),
            ("set_settings", "Change the animation's feel. Only the fields you pass change.",
                @"{""type"":""object"",""properties"":{
                    ""loop"":{""type"":""boolean""},
                    ""curve"":{""type"":""string"",""enum"":[""Spring"",""Linear"",""Smooth""]},
                    ""duration"":{""type"":""number"",""description"":""Snap time, seconds (0.02-0.6).""},
                    ""overshoot"":{""type"":""number"",""description"":""0-0.7, Spring only.""},
                    ""anticipation"":{""type"":""number"",""description"":""0-0.5, Spring only.""},
                    ""stepRate"":{""type"":""number"",""description"":""Choppiness in fps, 0 = smooth, 12 = on twos.""},
                    ""overlap"":{""type"":""number"",""description"":""Seconds extremities trail (0-0.3).""},
                    ""looseness"":{""type"":""number"",""description"":""0-1, extremities drag and keep swinging.""},
                    ""spineWeight"":{""type"":""number""},""headWeight"":{""type"":""number""},""armsWeight"":{""type"":""number""},""legsWeight"":{""type"":""number""},
                    ""humanize"":{""type"":""boolean""},""jointLimits"":{""type"":""boolean""},
                    ""movingHolds"":{""type"":""number""},""inbetweens"":{""type"":""number""},""life"":{""type"":""number""},
                    ""headFollow"":{""type"":""number""},""torsoFollow"":{""type"":""number""},""springiness"":{""type"":""number""}}}"),
        };

        const string TransitionSchema =
            @"{""type"":""object"",""description"":""This pose's own transition out."",""properties"":{
                ""curve"":{""type"":""string"",""enum"":[""Spring"",""Linear"",""Smooth""]},
                ""duration"":{""type"":""number""},""overshoot"":{""type"":""number""},""anticipation"":{""type"":""number""}}}";

        // Body muscles only: no eyes, jaw or fingers.
        static readonly int[] BodyMuscles = Enumerable.Range(0, HumanTrait.MuscleCount).Where(m => m < 15 || (m > 20 && m < 55)).ToArray();

        readonly VibrationsAnimation anim;
        readonly TemplateRig rig;
        readonly PoseRenderer renderer; // null = no previews
        readonly Transform root;
        readonly Bounds frame;
        readonly int fps;
        PoseRenderer.Snapshot snapshot;

        public AiTools(VibrationsAnimation anim, TemplateRig rig, PoseRenderer renderer = null, Transform root = null, Bounds frame = default, int fps = 30)
        {
            this.anim = anim;
            this.rig = rig;
            this.renderer = renderer;
            this.root = root;
            this.frame = frame;
            this.fps = fps;
        }

        public void Dispose() => snapshot?.Dispose();

        public ToolResult Execute(string name, JObject args)
        {
            try
            {
                return name switch
                {
                    "get_animation" => GetAnimation(),
                    "add_pose" => AddPose(args),
                    "update_pose" => UpdatePose(args),
                    "delete_pose" => DeletePose(args),
                    "set_settings" => SetSettings(args),
                    "render_preview" => RenderPreview(args),
                    _ => $"Error: unknown tool {name}",
                };
            }
            catch (Exception e)
            {
                return "Error: " + e.Message;
            }
        }

        string GetAnimation()
        {
            var poses = new JArray();
            for (int i = 0; i < anim.poses.Count; i++)
            {
                var p = anim.poses[i];
                var tp = rig.FromPose(p);
                var muscles = new JObject();
                foreach (var m in BodyMuscles) muscles[HumanTrait.MuscleName[m]] = Math.Round(tp.muscles[m], 2);
                poses.Add(new JObject
                {
                    ["index"] = i, ["name"] = p.name, ["hold"] = Math.Round(p.hold, 3), ["lift"] = Math.Round(tp.lift, 3),
                    ["transition"] = p.customTransition ? TransitionJson(p.transition) : null,
                    ["muscles"] = muscles,
                });
            }
            var settings = JObject.Parse(JsonUtility.ToJson(anim));
            settings.Remove("poses");
            settings.Remove("seed");
            settings["transition"] = TransitionJson(anim.transition);
            return new JObject { ["settings"] = settings, ["length_seconds"] = Math.Round(Tween.Length(anim), 3), ["poses"] = poses }
                .ToString(Newtonsoft.Json.Formatting.None);
        }

        string AddPose(JObject args)
        {
            int after = Mathf.Clamp(args.Value<int?>("after_index") ?? anim.poses.Count - 1, -1, anim.poses.Count - 1);
            int a = PoseIndex(args.Value<int?>("pose_a") ?? Mathf.Max(after, 0));
            var tp = rig.FromPose(anim.poses[a]);
            if (args["pose_b"] != null)
            {
                var b = rig.FromPose(anim.poses[PoseIndex(args.Value<int>("pose_b"))]);
                float t = args.Value<float?>("blend") ?? 0.5f;
                for (int m = 0; m < tp.muscles.Length; m++) tp.muscles[m] = Mathf.LerpUnclamped(tp.muscles[m], b.muscles[m], t);
                tp.lift = Mathf.LerpUnclamped(tp.lift, b.lift, t);
                tp.bodyRotation = Quaternion.SlerpUnclamped(tp.bodyRotation, b.bodyRotation, t);
            }
            tp.name = args.Value<string>("name") ?? "AI pose";
            tp.hold = Mathf.Max(0f, args.Value<float?>("hold") ?? 0.05f);
            tp.customTransition = false;
            ApplyEdits(tp, args);
            Undo.RecordObject(anim, "AI add pose");
            anim.poses.Insert(after + 1, rig.ToPose(tp));
            EditorUtility.SetDirty(anim);
            return $"Added pose {after + 1} '{tp.name}'. The animation now has {anim.poses.Count} poses.";
        }

        string UpdatePose(JObject args)
        {
            int i = PoseIndex(args.Value<int>("index"));
            var tp = rig.FromPose(anim.poses[i]);
            if (args["name"] != null) tp.name = args.Value<string>("name");
            if (args["hold"] != null) tp.hold = Mathf.Max(0f, args.Value<float>("hold"));
            if (args["custom_transition"] != null) tp.customTransition = args.Value<bool>("custom_transition");
            ApplyEdits(tp, args);
            Undo.RecordObject(anim, "AI edit pose");
            anim.poses[i] = rig.ToPose(tp);
            EditorUtility.SetDirty(anim);
            return $"Updated pose {i} '{tp.name}'.";
        }

        string DeletePose(JObject args)
        {
            if (anim.poses.Count <= 1) return "Error: an animation needs at least one pose.";
            int i = PoseIndex(args.Value<int>("index"));
            Undo.RecordObject(anim, "AI delete pose");
            anim.poses.RemoveAt(i);
            EditorUtility.SetDirty(anim);
            return $"Deleted pose {i}. The animation now has {anim.poses.Count} poses.";
        }

        string SetSettings(JObject args)
        {
            Undo.RecordObject(anim, "AI settings");
            var changed = new System.Collections.Generic.List<string>();
            object transition = anim.transition; // boxed so reflection can write into the struct
            foreach (var (key, value) in args)
            {
                if (SetField(transition, key, value) || SetField(anim, key, value)) changed.Add(key);
                else return $"Error: unknown setting {key}";
            }
            anim.transition = (Transition)transition;
            EditorUtility.SetDirty(anim);
            return changed.Count == 0 ? "Nothing changed." : "Changed " + string.Join(", ", changed) + ".";
        }

        void ApplyEdits(TemplatePose tp, JObject args)
        {
            if (args["muscles"] is JObject muscles)
                foreach (var (name, value) in muscles)
                {
                    int m = Array.IndexOf(HumanTrait.MuscleName, name);
                    if (m < 0 || !BodyMuscles.Contains(m)) throw new ArgumentException($"unknown muscle '{name}'");
                    tp.muscles[m] = Mathf.Clamp(value.Value<float>(), -1f, 1f);
                }
            if (args["lift"] != null) tp.lift = Mathf.Max(0f, args.Value<float>("lift"));
            if (args["transition"] is JObject tr)
            {
                object boxed = tp.customTransition ? tp.transition : anim.transition;
                foreach (var (key, value) in tr) SetField(boxed, key, value);
                tp.transition = (Transition)boxed;
                tp.customTransition = true;
            }
        }

        // --- render_preview ---

        const int CellWidth = 140, CellHeight = 180, MaxColumns = 12;
        static readonly Color Background = new(0.17f, 0.17f, 0.17f, 1f);
        static readonly Color Figure = new(0.86f, 0.85f, 0.83f, 1f);
        static readonly Color FloorLine = new(1f, 0.5f, 0.3f, 1f);

        ToolResult RenderPreview(JObject args)
        {
            if (renderer == null) return "Error: previews are not available right now.";
            bool motion = args.Value<string>("mode") == "motion";
            var views = new (string name, Quaternion rotation)[]
            {
                ("front", Quaternion.LookRotation(-root.forward)),
                ("side", Quaternion.LookRotation(root.right)), // character faces left
                ("three_quarter", Quaternion.LookRotation(-(Quaternion.AngleAxis(-40f, Vector3.up) * root.forward))),
            };

            if (!motion)
            {
                int count = Mathf.Min(anim.poses.Count, MaxColumns);
                var sheet = NewSheet(count, views.Length);
                for (int i = 0; i < count; i++)
                {
                    var p = anim.poses[i];
                    Rig.Apply(rig.bones, p.rotations, p.hipsPosition);
                    for (int v = 0; v < views.Length; v++) DrawCell(sheet, i, v, views.Length, views[v].rotation);
                }
                var names = string.Join(", ", anim.poses.GetRange(0, count).ConvertAll(p => $"{anim.poses.IndexOf(p)} {p.name}"));
                return Finish(sheet, $"Key poses, one column each (left to right): {names}. Rows: front, side (facing left), three-quarter. " +
                                     "Orange line = floor." + (anim.poses.Count > count ? $" Only the first {count} poses are shown." : ""));
            }

            int frames = Mathf.Clamp(args.Value<int?>("frames") ?? 12, 4, 24);
            var view = views[Mathf.Max(0, Array.FindIndex(views, x => x.name == (args.Value<string>("view") ?? "side")))];
            var all = Tween.Frames(anim, fps, out var hips);
            var lean = Secondary.Simulate(anim, hips, rig.bones[0], root, rig.humanScale, fps);
            int columns = Mathf.Min(frames, MaxColumns), rows = (frames + columns - 1) / columns;
            var motionSheet = NewSheet(columns, rows);
            float length = Tween.Length(anim);
            for (int i = 0; i < frames; i++)
            {
                int f = Mathf.RoundToInt(i / (float)frames * (all.Length - 1)); // loops: the last frame repeats the first, so stop short
                if (!anim.loop) f = Mathf.RoundToInt(i / (float)(frames - 1) * (all.Length - 1));
                Rig.Apply(rig.bones, all[f], hips[f]);
                Secondary.Apply(rig.bones, root, lean[f]);
                if (anim.humanize && anim.jointLimits) Rig.ClampToLimits(rig.handler, rig.bones);
                DrawCell(motionSheet, i % columns, i / columns, rows, view.rotation);
            }
            return Finish(motionSheet, $"{frames} frames of the final animation ({length:0.00}s, {(anim.loop ? "loop" : "once")}), {view.name} view, " +
                                       "left to right then top to bottom, evenly spaced in time. Orange line = floor.");
        }

        static Texture2D NewSheet(int columns, int rows)
        {
            var sheet = new Texture2D(columns * CellWidth, rows * CellHeight, TextureFormat.RGBA32, false);
            var pixels = new Color32[sheet.width * sheet.height];
            Array.Fill(pixels, (Color32)Background);
            sheet.SetPixels32(pixels);
            return sheet;
        }

        void DrawCell(Texture2D sheet, int column, int row, int rows, Quaternion view)
        {
            var rt = RenderTexture.GetTemporary(CellWidth, CellHeight, 24);
            snapshot = renderer.Bake(snapshot);
            renderer.RenderThumbnail(snapshot, rt, frame, view, Figure, Background);
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            int x = column * CellWidth, y = (rows - 1 - row) * CellHeight;
            sheet.ReadPixels(new Rect(0, 0, CellWidth, CellHeight), x, y);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);

            int floorY = y + Mathf.RoundToInt((rig.floorHeight - (frame.center.y - frame.extents.y)) / frame.size.y * CellHeight);
            if (floorY > y && floorY < y + CellHeight)
                for (int i = x + 4; i < x + CellWidth - 4; i++)
                {
                    sheet.SetPixel(i, floorY, FloorLine);
                    sheet.SetPixel(i, floorY - 1, FloorLine); // 2 px so it survives downscaling
                }
            for (int i = y; i < y + CellHeight; i++) sheet.SetPixel(x, i, Color.black); // cell separator
        }

        static ToolResult Finish(Texture2D sheet, string text)
        {
            sheet.Apply();
            var png = sheet.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(sheet);
            return new ToolResult { text = text, image = png };
        }

        int PoseIndex(int i)
        {
            if (i < 0 || i >= anim.poses.Count) throw new ArgumentException($"no pose {i} (the animation has {anim.poses.Count})");
            return i;
        }

        // Sets a public float/bool/enum field by name, clamped to its [Range] if it has one.
        static bool SetField(object target, string name, JToken value)
        {
            var field = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field == null) return false;
            if (field.FieldType == typeof(bool)) field.SetValue(target, value.Value<bool>());
            else if (field.FieldType.IsEnum) field.SetValue(target, Enum.Parse(field.FieldType, value.Value<string>(), true));
            else if (field.FieldType == typeof(float))
            {
                float v = value.Value<float>();
                if (field.GetCustomAttribute<RangeAttribute>() is { } range) v = Mathf.Clamp(v, range.min, range.max);
                field.SetValue(target, v);
            }
            else return false;
            return true;
        }

        static JObject TransitionJson(Transition t) => new()
        {
            ["curve"] = t.curve.ToString(), ["duration"] = Math.Round(t.duration, 3),
            ["overshoot"] = Math.Round(t.overshoot, 3), ["anticipation"] = Math.Round(t.anticipation, 3),
        };
    }
}
