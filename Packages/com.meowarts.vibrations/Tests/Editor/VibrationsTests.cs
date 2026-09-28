using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Vibrations.Tests
{
    public class VibrationsTests
    {
        static readonly Transition T = new() { duration = 0.1f, overshoot = 0.3f, anticipation = 0.1f };

        [Test]
        public void EaseAnticipatesHitsTargetOvershootsAndSettles()
        {
            float min = 1f, peak = 0f;
            for (float x = 0f; x < 5f; x += 0.001f)
            {
                min = Mathf.Min(min, Tween.Ease(x, T));
                peak = Mathf.Max(peak, Tween.Ease(x, T));
            }
            Assert.AreEqual(0f, Tween.Ease(0f, T), 1e-4f);
            Assert.AreEqual(-0.1f, min, 1e-3f);
            Assert.AreEqual(1f, Tween.Ease(1f, T), 1e-3f);
            Assert.AreEqual(1f + 0.3f * 1.1f, peak, 0.01f);
            Assert.AreEqual(1f, Tween.Ease(20f, T), 1e-3f);
        }

        [Test]
        public void LoopIsSeamlessAndOneShotEndsOnLastPose()
        {
            var a = RandomAnimation();
            float len = Tween.Length(a);
            AssertSame(a, len - 1e-4f, a, 1e-4f, 1f);

            a.loop = false;
            a.poses[^1].hold = 1f;
            len = Tween.Length(a);
            var rot = new Quaternion[Tween.Bones.Length];
            Tween.Sample(a, len, rot, out var hips);
            for (int b = 0; b < rot.Length; b++)
                Assert.Less(Quaternion.Angle(rot[b], a.poses[^1].rotations[b]), 0.5f);
            Assert.Less(Vector3.Distance(hips, a.poses[^1].hipsPosition), 1e-3f);
        }

        [Test]
        public void LinearAndSmoothCurvesNeverOvershoot()
        {
            var linear = new Transition { curve = Transition.Curve.Linear, overshoot = 0.5f, anticipation = 0.3f };
            var smooth = new Transition { curve = Transition.Curve.Smooth, overshoot = 0.5f, anticipation = 0.3f };
            Assert.AreEqual(0.5f, Tween.Ease(0.5f, linear), 1e-5f);
            Assert.AreEqual(0.5f, Tween.Ease(0.5f, smooth), 1e-5f);
            Assert.Less(Tween.Ease(0.1f, smooth), 0.1f, "smooth eases in");
            for (float x = 0f; x < 3f; x += 0.01f)
            {
                Assert.That(Tween.Ease(x, linear), Is.InRange(0f, 1f));
                Assert.That(Tween.Ease(x, smooth), Is.InRange(0f, 1f));
            }

            // Looseness stretches a linear transition but keeps it linear (no spring wobble on the hands).
            var a = RandomAnimation();
            a.ApplyPreset(System.Array.FindIndex(VibrationsAnimation.Presets, p => p.name == "Linear"));
            a.looseness = 1f;
            a.loop = false; // in a loop the previous cycle's loose tail is still arriving at t = 0
            a.poses[0].hold = 0f;
            a.poses[1].hold = 5f;
            var rot = new Quaternion[Tween.Bones.Length];
            float previous = float.MaxValue;
            for (float t = 0f; t < 1.5f; t += 0.02f)
            {
                Tween.Sample(a, t, rot, out _);
                float left = Quaternion.Angle(rot[9], a.poses[1].rotations[9]);
                Assert.LessOrEqual(left, previous + 0.01f, "hand only moves toward the pose");
                previous = left;
            }
            Assert.Less(previous, 0.5f);
        }

        [Test]
        public void HumanizeLoopsSeamlesslyAndInbetweensCapJumps()
        {
            var a = RandomAnimation();
            a.humanize = true;
            a.movingHolds = 0.2f;
            a.life = 2f;
            a.inbetweens = 0.6f;
            a.transition.duration = 0.04f; // snaps way too big for one frame
            AssertSame(a, Tween.Length(a) - 1e-4f, a, 1e-4f, 1f);

            var frames = Tween.Frames(a, 30, out _);
            float max = Tween.MaxJump(a.inbetweens, 30) + 0.01f;
            for (int f = 1; f < frames.Length; f++)
            for (int b = 0; b < Tween.Bones.Length; b++)
                Assert.LessOrEqual(Quaternion.Angle(frames[f - 1][b], frames[f][b]), max, $"frame {f} bone {b}");
        }

        [Test]
        public void JointLimitsKeepNaturalPosesAndFixBrokenOnes()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Characters/default.fbx");
            if (model == null) Assert.Ignore("Needs Assets/Characters/default.fbx (Vibrations dev project).");
            var go = Object.Instantiate(model);
            try
            {
                var animator = go.GetComponent<Animator>();
                var bones = Rig.Bind(animator);
                using var handler = new HumanPoseHandler(animator.avatar, animator.transform);
                var rest = new Pose();
                Rig.Capture(bones, rest);

                Rig.ClampToLimits(handler, bones, animator.transform);
                for (int i = 0; i < bones.Length; i++)
                    if (bones[i]) Assert.Less(Quaternion.Angle(rest.rotations[i], bones[i].localRotation), 1f, $"rest bone {i} moved");

                var elbow = bones[8]; // left lower arm
                var bent = elbow.localRotation * Quaternion.Euler(0f, 0f, 150f);
                elbow.localRotation = bent;
                Rig.ClampToLimits(handler, bones, animator.transform);
                Assert.Greater(Quaternion.Angle(bent, elbow.localRotation), 10f, "impossible elbow got pulled back");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void FollowThroughTiltsChinUpWhenTheBodyDrops()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Characters/default.fbx");
            if (model == null) Assert.Ignore("Needs Assets/Characters/default.fbx (Vibrations dev project).");
            var go = Object.Instantiate(model);
            try
            {
                var animator = go.GetComponent<Animator>();
                var root = animator.transform;
                var bones = Rig.Bind(animator);
                var a = ScriptableObject.CreateInstance<VibrationsAnimation>();
                a.loop = false;
                a.humanize = true;
                a.headFollow = 1f;
                a.torsoFollow = 0f;
                a.inbetweens = 0f;
                a.life = 0f;
                var stand = new Pose { hold = 0.2f };
                Rig.Capture(bones, stand);
                var crouch = stand.Clone();
                crouch.hipsPosition = stand.hipsPosition + bones[0].parent.InverseTransformVector(Vector3.down * 0.3f); // drop 30 cm
                crouch.hold = 0.5f;
                a.poses.Add(stand);
                a.poses.Add(crouch);

                var frames = Tween.Frames(a, 30, out var hips);
                var lean = Secondary.Simulate(a, hips, bones[0], root, animator.humanScale, 30);
                int drop = 0;
                for (int f = 1; f < lean.Length; f++) if (lean[f].z < lean[drop].z) drop = f;
                Assert.Less(lean[drop].z, -2f, "head should pitch noticeably during the drop");

                Rig.Apply(bones, frames[drop], hips[drop]);
                var before = bones[5].rotation;
                Secondary.Apply(bones, root, lean[drop]);
                var tilt = bones[5].rotation * Quaternion.Inverse(before);
                Assert.Greater((tilt * root.forward).y, 0.02f, "chin up");
                Assert.AreEqual(0f, lean[drop].x, 1e-4f, "torso follow off");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void TemplatesGroundOnAnyCharacterAndRoundTrip()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Characters/default.fbx");
            if (model == null) Assert.Ignore("Needs Assets/Characters/default.fbx (Vibrations dev project).");
            var go = Object.Instantiate(model);
            go.transform.SetPositionAndRotation(new Vector3(-4f, 0f, 7f), Quaternion.Euler(0f, 45f, 0f)); // not at the origin
            try
            {
                var animator = go.GetComponent<Animator>();
                var bones = Rig.Bind(animator);
                using var handler = new HumanPoseHandler(animator.avatar, animator.transform);
                var rest = new Pose();
                Rig.Capture(bones, rest);
                var soles = new float[4];
                for (int k = 0; k < 4; k++) soles[k] = bones[Rig.SoleBones[k]].position.y;
                var rig = new TemplateRig { root = animator.transform, bones = bones, handler = handler, rest = rest, soles = soles, floorHeight = 0f, humanScale = animator.humanScale };

                Assert.GreaterOrEqual(BuiltInTemplates.All.Count, 6);
                foreach (var t in BuiltInTemplates.All)
                foreach (var tp in t.poses)
                {
                    rig.Apply(tp);
                    Assert.AreEqual(tp.lift * animator.humanScale, Rig.LowestSole(bones, soles), 0.005f, $"{t.name}/{tp.name} height");
                    var hips = animator.transform.InverseTransformPoint(bones[0].position);
                    Assert.Less(new Vector2(hips.x, hips.z).magnitude, 0.3f, $"{t.name}/{tp.name} stays over the character");
                }

                var walk = rig.CreateAnimation(System.Linq.Enumerable.First(BuiltInTemplates.All, t => t.name == "Pixar Walk"));
                Assert.AreEqual(12f, walk.stepRate, "template brings its own feel");
                // Every key pose must land on a 12 fps frame, or the rhythm would skip it.
                // (Overlap deliberately makes hands trail a hold-0 pose; check the frame grid itself.)
                walk.overlap = 0f;
                var rot = new Quaternion[Tween.Bones.Length];
                for (int i = 0; i < walk.poses.Count; i++)
                {
                    float best = float.MaxValue;
                    for (float t = 0f; t < Tween.Length(walk); t += 1f / 12f)
                    {
                        Tween.Sample(walk, t, rot, out _);
                        float worst = 0f;
                        for (int b = 0; b < rot.Length; b++) if (bones[b]) worst = Mathf.Max(worst, Quaternion.Angle(rot[b], walk.poses[i].rotations[b]));
                        best = Mathf.Min(best, worst);
                    }
                    Assert.Less(best, 6f, $"pose {walk.poses[i].name} never shows on a frame");
                }
                Assert.IsTrue(walk.loop);
                var again = rig.CreateAnimation(rig.CreateTemplate(walk));
                for (int i = 0; i < walk.poses.Count; i++)
                for (int b = 0; b < bones.Length; b++)
                    if (bones[b]) Assert.Less(Quaternion.Angle(walk.poses[i].rotations[b], again.poses[i].rotations[b]), 2f, $"pose {i} bone {b}");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void LoosenessKeepsHandsSwingingAfterTheBodySettles()
        {
            var a = RandomAnimation();
            a.loop = false;
            a.overlap = 0f;
            a.looseness = 1f;
            a.poses[0].hold = 0f;
            a.poses[1].hold = 5f; // stay on pose 2 while we measure
            float settleBody = a.transition.duration * 6f; // hips are settled, a loose hand is still swinging
            var rot = new Quaternion[Tween.Bones.Length];
            Tween.Sample(a, settleBody, rot, out _);
            float hips = Quaternion.Angle(rot[0], a.poses[1].rotations[0]);
            float hand = Quaternion.Angle(rot[9], a.poses[1].rotations[9]);
            Assert.Less(hips, 2f);
            Assert.Greater(hand, hips + 2f);
        }

        [Test]
        public void AiProtocolRoundTripsForBothProviders()
        {
            var messages = new Newtonsoft.Json.Linq.JArray { new Newtonsoft.Json.Linq.JObject { ["role"] = "user", ["content"] = "hi" } };
            var anthropic = AiAgent.BuildRequest(AiProvider.Anthropic, "claude-sonnet-5", messages);
            Assert.AreEqual(AiTools.SystemPrompt, (string)anthropic["system"]);
            Assert.AreEqual(AiTools.Definitions.Length, anthropic["tools"].Count());
            Assert.IsNotNull(anthropic["tools"][0]["input_schema"]);
            var openai = AiAgent.BuildRequest(AiProvider.OpenAI, "gpt-5", messages);
            Assert.AreEqual("function", (string)openai["tools"][0]["type"]);
            Assert.IsNotNull(openai["tools"][1]["function"]["parameters"]["properties"]["after_index"]);

            var (text, calls, assistant) = AiAgent.ParseResponse(AiProvider.Anthropic, Newtonsoft.Json.Linq.JObject.Parse(
                @"{""content"":[{""type"":""text"",""text"":""On it.""},{""type"":""tool_use"",""id"":""tu_1"",""name"":""delete_pose"",""input"":{""index"":2}}],""stop_reason"":""tool_use""}"));
            Assert.AreEqual("On it.", text);
            Assert.AreEqual("delete_pose", calls[0].name);
            Assert.AreEqual(2, (int)calls[0].args["index"]);
            var history = new Newtonsoft.Json.Linq.JArray { assistant };
            AiAgent.AppendToolResults(AiProvider.Anthropic, history, calls, new System.Collections.Generic.List<AiTools.ToolResult> { "Error: nope" });
            Assert.AreEqual("tool_result", (string)history[1]["content"][0]["type"]);
            Assert.AreEqual("tu_1", (string)history[1]["content"][0]["tool_use_id"]);
            Assert.IsTrue((bool)history[1]["content"][0]["is_error"]);

            (text, calls, assistant) = AiAgent.ParseResponse(AiProvider.OpenAI, Newtonsoft.Json.Linq.JObject.Parse(
                @"{""choices"":[{""message"":{""role"":""assistant"",""content"":null,""refusal"":null,""tool_calls"":[{""id"":""call_1"",""type"":""function"",""function"":{""name"":""set_settings"",""arguments"":""{\""looseness\"":0.6}""}}]},""finish_reason"":""tool_calls""}]}"));
            Assert.AreEqual(0.6f, (float)calls[0].args["looseness"], 1e-5f);
            Assert.IsNull(assistant["refusal"], "only role/content/tool_calls go back to OpenAI");
            history = new Newtonsoft.Json.Linq.JArray { assistant };
            AiAgent.AppendToolResults(AiProvider.OpenAI, history, calls, new System.Collections.Generic.List<AiTools.ToolResult> { "ok" });
            Assert.AreEqual("tool", (string)history[1]["role"]);
            Assert.AreEqual("call_1", (string)history[1]["tool_call_id"]);

            // Images: inside the tool result for Anthropic, in a follow-up user message for OpenAI.
            var withImage = new System.Collections.Generic.List<AiTools.ToolResult> { new() { text = "sheet", image = new byte[] { 1, 2, 3 } } };
            history = new Newtonsoft.Json.Linq.JArray();
            AiAgent.AppendToolResults(AiProvider.Anthropic, history, calls, withImage);
            Assert.AreEqual("image", (string)history[0]["content"][0]["content"][1]["type"]);
            Assert.AreEqual("AQID", (string)history[0]["content"][0]["content"][1]["source"]["data"]);
            history = new Newtonsoft.Json.Linq.JArray();
            AiAgent.AppendToolResults(AiProvider.OpenAI, history, calls, withImage);
            Assert.AreEqual("tool", (string)history[0]["role"]);
            Assert.AreEqual("user", (string)history[1]["role"]);
            StringAssert.StartsWith("data:image/png;base64,", (string)history[1]["content"][1]["image_url"]["url"]);
        }

        [Test]
        public void AiToolsEditTheAnimation()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Characters/default.fbx");
            if (model == null) Assert.Ignore("Needs Assets/Characters/default.fbx (Vibrations dev project).");
            var go = Object.Instantiate(model);
            try
            {
                var animator = go.GetComponent<Animator>();
                var bones = Rig.Bind(animator);
                using var handler = new HumanPoseHandler(animator.avatar, animator.transform);
                var rest = new Pose();
                Rig.Capture(bones, rest);
                var soles = new float[4];
                for (int k = 0; k < 4; k++) soles[k] = bones[Rig.SoleBones[k]].position.y;
                var rig = new TemplateRig { root = animator.transform, bones = bones, handler = handler, rest = rest, soles = soles, floorHeight = 0f, humanScale = animator.humanScale };
                var walk = System.Linq.Enumerable.First(BuiltInTemplates.All, t => t.name == "Walk");
                var anim = rig.CreateAnimation(walk);
                using var renderer = new PoseRenderer(animator);
                var frame = new Bounds(animator.transform.position + Vector3.up, new Vector3(2f, 2.3f, 2f));
                var tools = new AiTools(anim, rig, renderer, animator.transform, frame, 30);
                Newtonsoft.Json.Linq.JObject Args(string json) => Newtonsoft.Json.Linq.JObject.Parse(json);
                float Muscle(int pose, string name) => (float)Newtonsoft.Json.Linq.JObject.Parse(tools.Execute("get_animation", new Newtonsoft.Json.Linq.JObject()).text)["poses"][pose]["muscles"][name];

                var read = Newtonsoft.Json.Linq.JObject.Parse(tools.Execute("get_animation", new Newtonsoft.Json.Linq.JObject()).text);
                Assert.AreEqual(4, read["poses"].Count());
                Assert.IsNull(read["poses"][0]["muscles"]["Left Thumb 1 Stretched"], "no fingers over the wire");

                // In-between halfway between pose 0 and 1, then a raised knee on top.
                float a = Muscle(0, "Right Upper Leg Front-Back"), b = Muscle(1, "Right Upper Leg Front-Back");
                StringAssert.StartsWith("Added", tools.Execute("add_pose", Args(@"{""after_index"":0,""name"":""Breakdown"",""pose_a"":0,""pose_b"":1,""blend"":0.5,""muscles"":{""Left Lower Leg Stretch"":0.1}}")).text);
                Assert.AreEqual(5, anim.poses.Count);
                Assert.AreEqual("Breakdown", anim.poses[1].name);
                Assert.AreEqual((a + b) / 2f, Muscle(1, "Right Upper Leg Front-Back"), 0.05f);
                Assert.AreEqual(0.1f, Muscle(1, "Left Lower Leg Stretch"), 0.05f);

                tools.Execute("update_pose", Args(@"{""index"":1,""hold"":0.25,""transition"":{""curve"":""Smooth"",""duration"":0.3}}"));
                Assert.AreEqual(0.25f, anim.poses[1].hold, 1e-5f);
                Assert.IsTrue(anim.poses[1].customTransition);
                Assert.AreEqual(Transition.Curve.Smooth, anim.poses[1].transition.curve);

                StringAssert.StartsWith("Changed", tools.Execute("set_settings", Args(@"{""looseness"":5,""curve"":""Linear"",""humanize"":true}")).text);
                Assert.AreEqual(1f, anim.looseness, "clamped to its Range");
                Assert.AreEqual(Transition.Curve.Linear, anim.transition.curve);
                Assert.IsTrue(anim.humanize);

                StringAssert.StartsWith("Error", tools.Execute("update_pose", Args(@"{""index"":0,""muscles"":{""Left Wing Flap"":1}}")).text);
                StringAssert.StartsWith("Error", tools.Execute("delete_pose", Args(@"{""index"":42}")).text);
                StringAssert.StartsWith("Error", tools.Execute("set_settings", Args(@"{""warp"":1}")).text);

                var poses = tools.Execute("render_preview", Args(@"{""mode"":""poses""}"));
                var sheet = new Texture2D(2, 2);
                Assert.IsTrue(sheet.LoadImage(poses.image));
                Assert.AreEqual(5 * 140, sheet.width, "one column per pose");
                Assert.AreEqual(3 * 180, sheet.height, "front, side, three-quarter");
                StringAssert.Contains("1 Breakdown", poses.text);
                var motion = tools.Execute("render_preview", Args(@"{""mode"":""motion"",""frames"":16}"));
                Assert.IsTrue(sheet.LoadImage(motion.image));
                Assert.AreEqual(12 * 140, sheet.width);
                Assert.AreEqual(2 * 180, sheet.height, "16 frames wrap onto two rows");
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(Application.temporaryCachePath, "vibrations-poses.png"), poses.image);
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(Application.temporaryCachePath, "vibrations-motion.png"), motion.image);
                Debug.Log("PREVIEW " + Application.temporaryCachePath);
                Object.DestroyImmediate(sheet);
                tools.Dispose();
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void MirrorSwapsSidesExactly()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Characters/default.fbx");
            if (model == null) Assert.Ignore("Needs Assets/Characters/default.fbx (Vibrations dev project).");
            var go = Object.Instantiate(model);
            // Away from the origin and turned: HumanPose body values are world space, which broke mirroring here.
            go.transform.SetPositionAndRotation(new Vector3(5f, 0f, 3f), Quaternion.Euler(0f, 90f, 0f));
            try
            {
                var animator = go.GetComponent<Animator>();
                var bones = Rig.Bind(animator);
                using var handler = new HumanPoseHandler(animator.avatar, animator.transform);
                var rest = new Pose();
                Rig.Capture(bones, rest);
                var soles = new float[4];
                for (int k = 0; k < 4; k++) soles[k] = bones[Rig.SoleBones[k]].position.y;
                var rig = new TemplateRig { root = animator.transform, bones = bones, handler = handler, rest = rest, soles = soles, floorHeight = 0f, humanScale = animator.humanScale };
                var walk = rig.CreateAnimation(BuiltInTemplates.All.First(t => t.name == "Walk"));
                var contactR = walk.poses.First(p => p.name == "Contact R");
                var contactL = walk.poses.First(p => p.name == "Contact L");

                var root = animator.transform;
                var mirrored = contactR.Clone();
                Rig.Mirror(handler, bones, mirrored, root);
                var hips = root.InverseTransformPoint(bones[0].position);
                Assert.Less(new Vector2(hips.x, hips.z).magnitude, 0.2f, "mirrored body stays over the character");
                for (int b = 0; b < bones.Length; b++)
                    if (bones[b]) Assert.Less(Quaternion.Angle(contactL.rotations[b], mirrored.rotations[b]), 3f, $"bone {b}: mirrored Contact R should be Contact L");

                Rig.Mirror(handler, bones, mirrored, root);
                for (int b = 0; b < bones.Length; b++)
                    if (bones[b]) Assert.Less(Quaternion.Angle(contactR.rotations[b], mirrored.rotations[b]), 1f, $"bone {b}: mirroring twice is the original");
                Assert.Less(Vector3.Distance(contactR.hipsPosition, mirrored.hipsPosition), 0.01f);

                var tools = new AiTools(walk, rig, root: root);
                StringAssert.StartsWith("Inserted", tools.Execute("mirror_pose", Newtonsoft.Json.Linq.JObject.Parse(@"{""index"":0,""as_new_pose"":true,""name"":""Mirrored""}")).text);
                Assert.AreEqual(5, walk.poses.Count);
                Assert.AreEqual("Mirrored", walk.poses[1].name);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void MovingPosesKeepsTheirOrderAsABlock()
        {
            CollectionAssert.AreEqual(new[] { 0, 2, 3, 1, 4 }, VibrationsWindow.MoveOrder(5, new[] { 1 }, 3), "one pose to the right");
            CollectionAssert.AreEqual(new[] { 3, 0, 1, 2, 4 }, VibrationsWindow.MoveOrder(5, new[] { 3 }, 0), "one pose to the front");
            CollectionAssert.AreEqual(new[] { 1, 0, 2, 3, 4 }, VibrationsWindow.MoveOrder(5, new[] { 0, 2 }, 1), "a split selection gathers into a block");
            CollectionAssert.AreEqual(new[] { 2, 3, 4, 0, 1 }, VibrationsWindow.MoveOrder(5, new[] { 0, 1 }, 3), "a block to the end");
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, VibrationsWindow.MoveOrder(5, new[] { 1, 2 }, 1), "dropped where it was");
        }

        [Test]
        public void AutoTimingFollowsMotionAndResets()
        {
            var a = ScriptableObject.CreateInstance<VibrationsAnimation>();
            a.loop = false;
            a.stepRate = 12f;
            var still = new Pose { hold = 0.1f };
            for (int b = 0; b < still.rotations.Length; b++) still.rotations[b] = Quaternion.identity;
            var big = still.Clone();   // big swing from `still`
            var small = still.Clone(); // tiny adjustment from `big`
            for (int b = 0; b < still.rotations.Length; b++)
            {
                big.rotations[b] = Quaternion.Euler(0f, 0f, 60f);
                small.rotations[b] = Quaternion.Euler(0f, 0f, 63f);
            }
            a.poses.AddRange(new[] { still, big, small });
            float length = Tween.Length(a);

            Tween.AutoTime(a);
            Assert.Greater(a.poses[0].transition.duration, a.poses[1].transition.duration, "the big move snaps slower than the tiny one");
            Assert.Greater(a.poses[1].hold, a.poses[2].hold, "the pose a big move lands on holds longer");
            Assert.AreEqual(length, Tween.Length(a), 2f / 12f, "same overall length, only the rhythm changes");
            foreach (var p in a.poses)
                Assert.AreEqual(0f, Mathf.Repeat(p.hold * 12f + 0.5f, 1f) - 0.5f, 1e-3f, "holds land on the 12 fps grid");

            Tween.AutoTime(a); // running it twice keeps the original timing for Reset
            Tween.ResetTiming(a);
            Assert.IsFalse(Tween.HasSavedTiming(a));
            foreach (var p in a.poses)
            {
                Assert.AreEqual(0.1f, p.hold, 1e-6f);
                Assert.IsFalse(p.customTransition);
            }
        }

        [Test]
        public void GroundingUsesTheRealMesh()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Characters/default.fbx");
            if (model == null) Assert.Ignore("Needs Assets/Characters/default.fbx (Vibrations dev project).");
            var go = Object.Instantiate(model);
            go.transform.SetPositionAndRotation(new Vector3(2f, 0.5f, -1f), Quaternion.Euler(0f, 30f, 0f));
            try
            {
                var animator = go.GetComponent<Animator>();
                var bones = Rig.Bind(animator);
                using var renderer = new PoseRenderer(animator);
                using var handler = new HumanPoseHandler(animator.avatar, animator.transform);
                PoseRenderer.Snapshot scratch = null;
                float lowest = renderer.Lowest(ref scratch);
                Assert.GreaterOrEqual(lowest, scratch.bounds.min.y - 1e-4f, "never below the mesh bounds");
                bones[0].position += Vector3.down * 0.1f;
                Assert.AreEqual(lowest - 0.1f, renderer.Lowest(ref scratch), 1e-3f, "follows the body exactly");
                bones[0].position += Vector3.up * 0.1f;

                var rest = new Pose();
                Rig.Capture(bones, rest);
                var rig = new TemplateRig
                {
                    root = animator.transform, bones = bones, handler = handler, rest = rest, soles = new float[4],
                    floorHeight = 0.5f, humanScale = animator.humanScale, lowestPoint = () => renderer.Lowest(ref scratch),
                };
                foreach (var tp in BuiltInTemplates.All.First(t => t.name == "Pixar Walk").poses) // heel strikes and tiptoes
                {
                    rig.Apply(tp);
                    Assert.AreEqual(0.5f + tp.lift * animator.humanScale, renderer.Lowest(ref scratch), 0.002f, $"{tp.name} sits on the floor");
                }
                scratch.Dispose();
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void TwoBoneIKReachesTarget()
        {
            var a = new GameObject("a").transform;
            var b = new GameObject("b").transform;
            var c = new GameObject("c").transform;
            b.SetParent(a); c.SetParent(b);
            b.localPosition = Vector3.down; c.localPosition = Vector3.down; // straight leg
            var target = new Vector3(0.3f, -1.4f, 0.4f);
            var endLocal = c.localRotation;
            Rig.SolveTwoBone(a, b, c, target, Vector3.forward, keepEndWorldRotation: false);
            Assert.Less(Vector3.Distance(c.position, target), 1e-3f);
            Assert.Greater(b.position.z, 0f, "knee bends toward the pole");
            Assert.AreEqual(endLocal, c.localRotation, "hand follows the forearm");

            var endWorld = c.rotation;
            Rig.SolveTwoBone(a, b, c, new Vector3(-0.2f, -1.2f, 0.5f), Vector3.forward, keepEndWorldRotation: true);
            Assert.Less(Quaternion.Angle(endWorld, c.rotation), 0.01f, "foot keeps its world rotation");
            Object.DestroyImmediate(a.gameObject);
        }

        [Test]
        public void BakesHumanoidClipFromDefaultCharacter()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Characters/default.fbx");
            if (model == null) Assert.Ignore("Needs Assets/Characters/default.fbx (Vibrations dev project).");
            var go = Object.Instantiate(model);
            go.transform.SetPositionAndRotation(new Vector3(6f, 0f, -2f), Quaternion.Euler(0f, 120f, 0f)); // not at the origin
            try
            {
                var animator = go.GetComponent<Animator>();
                Assert.IsTrue(animator.isHuman, "default.fbx should import as Humanoid");
                var bones = Rig.Bind(animator);
                var a = ScriptableObject.CreateInstance<VibrationsAnimation>();
                for (int i = 0; i < 3; i++)
                {
                    var p = new Pose();
                    bones[7].localRotation = Quaternion.Euler(0f, 0f, 40f * i); // swing left arm
                    Rig.Capture(bones, p);
                    a.poses.Add(p);
                }
                var clip = ClipBaker.Bake(a, animator, 30);
                Assert.IsTrue(clip.humanMotion);
                Assert.AreEqual(Tween.Length(a), clip.length, 1f / 30f);
                Assert.IsTrue(clip.isLooping);
                foreach (var axis in new[] { "RootT.x", "RootT.z" })
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), axis));
                    Assert.Less(Mathf.Abs(curve.Evaluate(0f)), 0.2f, $"{axis} is relative to the character, not the world");
                }
            }
            finally { Object.DestroyImmediate(go); }
        }

        static VibrationsAnimation RandomAnimation()
        {
            Random.InitState(1);
            var a = ScriptableObject.CreateInstance<VibrationsAnimation>();
            a.overlap = 0.1f;
            for (int i = 0; i < 3; i++)
            {
                var p = new Pose { hold = 0.1f, hipsPosition = Random.insideUnitSphere };
                for (int b = 0; b < p.rotations.Length; b++) p.rotations[b] = Random.rotation;
                a.poses.Add(p);
            }
            return a;
        }

        static void AssertSame(VibrationsAnimation a, float t0, VibrationsAnimation b, float t1, float maxDegrees)
        {
            var r0 = new Quaternion[Tween.Bones.Length];
            var r1 = new Quaternion[Tween.Bones.Length];
            Tween.Sample(a, t0, r0, out var h0);
            Tween.Sample(b, t1, r1, out var h1);
            for (int i = 0; i < r0.Length; i++) Assert.Less(Quaternion.Angle(r0[i], r1[i]), maxDegrees, $"bone {i}");
            Assert.Less(Vector3.Distance(h0, h1), 0.01f);
        }
    }
}
