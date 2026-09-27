using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Vibrations
{
    // Starter animations, written in Humanoid muscle values (Unity's default muscle ranges).
    // Rule of thumb: the second word of a muscle name is its + direction ("Down-Up": + up, "Up-Down": + down).
    //   Upper Leg / Arm Front-Back: + back, - forward.  Lower Leg / Forearm Stretch: +1 straight, 0 ≈ 100° bent.
    //   Arm Down-Up: + up, - down.  Spine/Chest Front-Back: + lean back, - bend forward.  Head Nod: + up.  Foot Up-Down: + toes down.
    // Feet are grounded on the floor automatically; `lift` raises airborne poses.
    public static class BuiltInTemplates
    {
        static List<VibrationsTemplate> all;

        public static IReadOnlyList<VibrationsTemplate> All => all ??= Build();

        static readonly (string, float)[] Stand =
        {
            ("Left Upper Leg Front-Back", 0.6f), ("Right Upper Leg Front-Back", 0.6f),
            ("Left Lower Leg Stretch", 0.95f), ("Right Lower Leg Stretch", 0.95f),
            ("Left Arm Down-Up", -0.75f), ("Right Arm Down-Up", -0.75f),
            ("Left Arm Front-Back", 0.3f), ("Right Arm Front-Back", 0.3f),
            ("Left Forearm Stretch", 0.75f), ("Right Forearm Stretch", 0.75f),
        };

        static List<VibrationsTemplate> Build()
        {
            // Walk: contact and passing, then the same mirrored. Grounding makes the passing pose rise on its own.
            var walkContact = new[]
            {
                ("Right Upper Leg Front-Back", 0.15f), ("Right Lower Leg Stretch", 0.9f),
                ("Left Upper Leg Front-Back", 0.95f), ("Left Lower Leg Stretch", 0.85f),
                ("Left Arm Front-Back", -0.1f), ("Right Arm Front-Back", 0.7f),
                ("Left Forearm Stretch", 0.55f), ("Spine Twist Left-Right", 0.1f),
            };
            var walkPassing = new[]
            {
                ("Right Upper Leg Front-Back", 0.6f), ("Right Lower Leg Stretch", 0.95f),
                ("Left Upper Leg Front-Back", 0.2f), ("Left Lower Leg Stretch", 0.25f),
            };

            var runPush = new[]
            {
                ("Right Upper Leg Front-Back", -0.15f), ("Right Lower Leg Stretch", 0.55f),
                ("Left Upper Leg Front-Back", 1f), ("Left Lower Leg Stretch", 0.7f),
                ("Left Arm Front-Back", -0.3f), ("Right Arm Front-Back", 0.9f),
                ("Left Forearm Stretch", 0f), ("Right Forearm Stretch", 0.1f),
                ("Left Arm Down-Up", -0.6f), ("Right Arm Down-Up", -0.6f),
                ("Spine Front-Back", -0.25f), ("Spine Twist Left-Right", 0.15f),
            };
            var runPassing = new[]
            {
                ("Right Upper Leg Front-Back", 0.45f), ("Right Lower Leg Stretch", 0.6f),
                ("Left Upper Leg Front-Back", -0.1f), ("Left Lower Leg Stretch", -0.4f),
                ("Left Forearm Stretch", 0f), ("Right Forearm Stretch", 0f),
                ("Left Arm Down-Up", -0.6f), ("Right Arm Down-Up", -0.6f),
                ("Spine Front-Back", -0.3f),
            };

            var crouch = new[]
            {
                ("Left Upper Leg Front-Back", -0.3f), ("Right Upper Leg Front-Back", -0.3f),
                ("Left Lower Leg Stretch", -0.15f), ("Right Lower Leg Stretch", -0.15f),
                ("Spine Front-Back", -0.35f), ("Chest Front-Back", -0.2f),
                ("Left Arm Front-Back", 0.65f), ("Right Arm Front-Back", 0.65f),
                ("Left Arm Down-Up", -0.85f), ("Right Arm Down-Up", -0.85f),
            };

            // Pixar walk: 4 poses per step, exaggerated. Snappy body, arms dragging and swinging through.
            var toonContact = new[]
            {
                ("Right Upper Leg Front-Back", -0.05f), ("Right Lower Leg Stretch", 0.95f), ("Right Foot Up-Down", -0.3f),
                ("Left Upper Leg Front-Back", 1f), ("Left Lower Leg Stretch", 0.9f), ("Left Foot Up-Down", 0.3f),
                ("Left Arm Front-Back", -0.45f), ("Left Forearm Stretch", 0.35f), ("Left Arm Down-Up", -0.65f),
                ("Right Arm Front-Back", 0.95f), ("Right Forearm Stretch", 0.8f),
                ("Spine Twist Left-Right", 0.2f), ("Chest Twist Left-Right", -0.15f), ("Chest Front-Back", 0.15f),
            };
            var toonDown = new[]
            {
                ("Right Upper Leg Front-Back", 0.25f), ("Right Lower Leg Stretch", 0.45f),
                ("Left Upper Leg Front-Back", 0.95f), ("Left Lower Leg Stretch", 0.35f), ("Left Foot Up-Down", 0.4f),
                ("Left Arm Front-Back", -0.6f), ("Left Forearm Stretch", 0.25f), ("Left Arm Down-Up", -0.6f),
                ("Right Arm Front-Back", 1f), ("Right Forearm Stretch", 0.7f),
                ("Spine Front-Back", -0.15f), ("Spine Left-Right", -0.12f), ("Spine Twist Left-Right", 0.25f),
                ("Chest Twist Left-Right", -0.2f), ("Head Nod Down-Up", -0.15f),
            };
            var toonPassing = new[]
            {
                ("Right Upper Leg Front-Back", 0.6f), ("Right Lower Leg Stretch", 0.9f),
                ("Left Upper Leg Front-Back", 0f), ("Left Lower Leg Stretch", 0f), ("Left Foot Up-Down", -0.2f),
                ("Left Arm Front-Back", 0.2f), ("Right Arm Front-Back", 0.4f),
                ("Left Forearm Stretch", 0.55f), ("Right Forearm Stretch", 0.55f),
                ("Spine Left-Right", -0.08f), ("Chest Front-Back", 0.1f),
            };
            var toonUp = new[]
            {
                ("Right Upper Leg Front-Back", 0.85f), ("Right Lower Leg Stretch", 1f), ("Right Foot Up-Down", 0.6f),
                ("Left Upper Leg Front-Back", -0.2f), ("Left Lower Leg Stretch", 0.55f), ("Left Foot Up-Down", -0.3f),
                ("Left Arm Front-Back", 0.75f), ("Left Forearm Stretch", 0.75f),
                ("Right Arm Front-Back", -0.2f), ("Right Forearm Stretch", 0.45f), ("Right Arm Down-Up", -0.65f),
                ("Spine Twist Left-Right", -0.15f), ("Chest Twist Left-Right", 0.1f), ("Chest Front-Back", 0.2f), ("Head Nod Down-Up", 0.12f),
            };

            // Sneak: crouched tiptoe with raised hands, choppy stop-motion timing.
            var sneakStep = new[]
            {
                ("Right Upper Leg Front-Back", -0.35f), ("Right Lower Leg Stretch", 0.2f), ("Right Foot Up-Down", 0.3f),
                ("Left Upper Leg Front-Back", 0.4f), ("Left Lower Leg Stretch", 0.1f), ("Left Foot Up-Down", 0.6f),
                ("Spine Front-Back", -0.4f), ("Chest Front-Back", -0.2f), ("Head Nod Down-Up", 0.25f),
                ("Left Arm Down-Up", -0.35f), ("Right Arm Down-Up", -0.35f), ("Left Arm Front-Back", -0.35f), ("Right Arm Front-Back", -0.1f),
                ("Left Forearm Stretch", -0.35f), ("Right Forearm Stretch", -0.3f), ("Spine Twist Left-Right", 0.15f),
            };
            var sneakHold = new[]
            {
                ("Right Upper Leg Front-Back", 0.2f), ("Right Lower Leg Stretch", 0.1f),
                ("Left Upper Leg Front-Back", -0.3f), ("Left Lower Leg Stretch", -0.3f), ("Left Foot Up-Down", 0.3f),
                ("Spine Front-Back", -0.45f), ("Chest Front-Back", -0.2f), ("Head Nod Down-Up", 0.3f), ("Head Turn Left-Right", 0.25f),
                ("Left Arm Down-Up", -0.3f), ("Right Arm Down-Up", -0.3f), ("Left Arm Front-Back", -0.2f), ("Right Arm Front-Back", -0.25f),
                ("Left Forearm Stretch", -0.4f), ("Right Forearm Stretch", -0.4f),
            };

            return new List<VibrationsTemplate>
            {
                // Rhythm on the 12 fps grid (F = one frame): hit contact, drop fast into the down, let the weight settle,
                // push up slower through passing, hang at the top, fall into the next contact.
                Template("Pixar Walk", "Toony 8-pose walk with a real rhythm: fast drops, settled downs, a hang at the top. Stop-motion feel.",
                    Feel(true, a => a.ApplyPreset(3)),
                    Snap(P("Contact R", 0f, 0f, toonContact), F, overshoot: 0.3f),
                    Snap(P("Down R", F, 0f, toonDown), 2 * F, overshoot: 0.03f),
                    Snap(P("Passing R", 0f, 0f, toonPassing), F, overshoot: 0.2f),
                    Snap(P("Up R", F, 0.03f, toonUp), F, overshoot: 0.1f),
                    Snap(P("Contact L", 0f, 0f, Mirror(toonContact)), F, overshoot: 0.3f),
                    Snap(P("Down L", F, 0f, Mirror(toonDown)), 2 * F, overshoot: 0.03f),
                    Snap(P("Passing L", 0f, 0f, Mirror(toonPassing)), F, overshoot: 0.2f),
                    Snap(P("Up L", F, 0.03f, Mirror(toonUp)), F, overshoot: 0.1f)),

                Template("Sneak", "Cartoon tiptoe: crouched, hands up, choppy stop-motion steps with big wind-ups.",
                    Feel(true, a =>
                    {
                        a.transition = T(0.18f, 0.15f, 0.25f);
                        a.stepRate = 8f;
                        a.overlap = 0.06f;
                        a.looseness = 0.3f;
                    }),
                    P("Step R", 0.12f, 0f, sneakStep),
                    P("Hold R", 0.25f, 0f, sneakHold),
                    P("Step L", 0.12f, 0f, Mirror(sneakStep)),
                    P("Hold L", 0.25f, 0f, Mirror(sneakHold))),

                Template("Idle", "Relaxed breathing loop. Humanize adds sway.",
                    Feel(true, a =>
                    {
                        a.transition = T(0.7f, 0.05f, 0.1f);
                        a.looseness = 0.2f;
                        Humanize(a, holds: 0.2f, life: 1f, head: 0.3f, torso: 0.2f);
                    }),
                    P("Exhale", 0.8f, 0f),
                    P("Inhale", 1.1f, 0f, ("Chest Front-Back", 0.15f), ("Head Nod Down-Up", 0.05f),
                        ("Left Arm Down-Up", -0.7f), ("Right Arm Down-Up", -0.7f))),

                Template("Walk", "Classic 4-pose walk: contact, passing, mirrored.",
                    Feel(true, a =>
                    {
                        a.transition = T(0.14f, 0.15f, 0.03f);
                        a.overlap = 0.06f;
                        a.looseness = 0.3f;
                        Humanize(a, holds: 0.1f, life: 0.5f, head: 0.4f, torso: 0.3f);
                    }),
                    Snap(P("Contact R", 0.04f, 0f, walkContact), 0.18f, overshoot: 0.15f),
                    Snap(P("Passing R", 0.1f, 0f, walkPassing), 0.12f, overshoot: 0.1f),
                    Snap(P("Contact L", 0.04f, 0f, Mirror(walkContact)), 0.18f, overshoot: 0.15f),
                    Snap(P("Passing L", 0.1f, 0f, Mirror(walkPassing)), 0.12f, overshoot: 0.1f)),

                Template("Run", "Fast 4-pose run with airborne passing poses.",
                    Feel(true, a =>
                    {
                        a.transition = T(0.07f, 0.35f, 0.05f);
                        a.overlap = 0.06f;
                        a.looseness = 0.5f;
                        a.armsWeight = 1.2f;
                        Humanize(a, holds: 0.05f, life: 0.3f, head: 0.6f, torso: 0.5f);
                    }),
                    Snap(P("Push R", 0.02f, 0f, runPush), 0.06f, overshoot: 0.35f),
                    Snap(P("Air R", 0.07f, 0.06f, runPassing), 0.09f, overshoot: 0.2f),
                    Snap(P("Push L", 0.02f, 0f, Mirror(runPush)), 0.06f, overshoot: 0.35f),
                    Snap(P("Air L", 0.07f, 0.06f, Mirror(runPassing)), 0.09f, overshoot: 0.2f)),

                Template("Jump", "Anticipate, launch, tuck, land, recover.",
                    Feel(false, a =>
                    {
                        a.transition = T(0.1f, 0.45f, 0.25f);
                        a.overlap = 0.08f;
                        a.looseness = 0.5f;
                        Humanize(a, holds: 0f, life: 0f, head: 0.7f, torso: 0.5f);
                    }),
                    P("Stand", 0.15f, 0f),
                    P("Crouch", 0.15f, 0f, crouch),
                    P("Launch", 0.05f, 0.15f, ("Spine Front-Back", 0.2f), ("Left Arm Down-Up", 0.6f), ("Right Arm Down-Up", 0.6f),
                        ("Left Arm Front-Back", -0.4f), ("Right Arm Front-Back", -0.4f), ("Left Lower Leg Stretch", 1f), ("Right Lower Leg Stretch", 1f)),
                    P("Tuck", 0.12f, 0.45f, ("Left Upper Leg Front-Back", -0.3f), ("Right Upper Leg Front-Back", -0.3f),
                        ("Left Lower Leg Stretch", 0f), ("Right Lower Leg Stretch", 0f), ("Left Arm Front-Back", -0.2f), ("Right Arm Front-Back", -0.2f)),
                    P("Land", 0.15f, 0f, ("Left Upper Leg Front-Back", -0.3f), ("Right Upper Leg Front-Back", -0.3f),
                        ("Left Lower Leg Stretch", -0.15f), ("Right Lower Leg Stretch", -0.15f), ("Spine Front-Back", -0.3f),
                        ("Left Arm Front-Back", -0.3f), ("Right Arm Front-Back", -0.3f)),
                    P("Recover", 0.4f, 0f)),

                Template("Sit down", "Stand, lean, sit, settle. Feet stay on the floor.",
                    Feel(false, a =>
                    {
                        a.transition = T(0.25f, 0.12f, 0.1f);
                        a.looseness = 0.3f;
                        Humanize(a, holds: 0.1f, life: 0.4f, head: 0.4f, torso: 0.2f);
                    }),
                    P("Stand", 0.2f, 0f),
                    P("Lean", 0.1f, 0f, ("Spine Front-Back", -0.45f), ("Left Upper Leg Front-Back", -0.1f), ("Right Upper Leg Front-Back", -0.1f),
                        ("Left Lower Leg Stretch", 0.55f), ("Right Lower Leg Stretch", 0.55f), ("Left Arm Front-Back", -0.3f), ("Right Arm Front-Back", -0.3f)),
                    P("Sit", 0.3f, 0f, ("Left Upper Leg Front-Back", -0.65f), ("Right Upper Leg Front-Back", -0.65f),
                        ("Left Lower Leg Stretch", 0f), ("Right Lower Leg Stretch", 0f), ("Spine Front-Back", -0.15f),
                        ("Left Arm Down-Up", -0.6f), ("Right Arm Down-Up", -0.6f), ("Left Arm Front-Back", -0.1f), ("Right Arm Front-Back", -0.1f),
                        ("Left Forearm Stretch", 0.3f), ("Right Forearm Stretch", 0.3f)),
                    P("Settle", 0.6f, 0f, ("Left Upper Leg Front-Back", -0.65f), ("Right Upper Leg Front-Back", -0.65f),
                        ("Left Lower Leg Stretch", 0f), ("Right Lower Leg Stretch", 0f), ("Spine Front-Back", 0.05f),
                        ("Left Arm Down-Up", -0.6f), ("Right Arm Down-Up", -0.6f), ("Left Arm Front-Back", -0.1f), ("Right Arm Front-Back", -0.1f),
                        ("Left Forearm Stretch", 0.3f), ("Right Forearm Stretch", 0.3f), ("Head Nod Down-Up", 0.05f))),

                Template("Wave", "Right hand waving loop.",
                    Feel(true, a =>
                    {
                        a.transition = T(0.12f, 0.5f, 0.05f);
                        a.overlap = 0.05f;
                        a.looseness = 0.6f;
                    }),
                    P("Wave out", 0.08f, 0f, ("Right Arm Down-Up", 0.35f), ("Right Arm Front-Back", 0.35f), ("Right Arm Twist In-Out", 0.9f),
                        ("Right Forearm Stretch", 0.15f), ("Right Hand In-Out", 0.7f), ("Head Tilt Left-Right", 0.15f)),
                    P("Wave in", 0.08f, 0f, ("Right Arm Down-Up", 0.3f), ("Right Arm Front-Back", 0.35f), ("Right Arm Twist In-Out", 0.9f),
                        ("Right Forearm Stretch", -0.15f), ("Right Hand In-Out", -0.7f), ("Head Tilt Left-Right", 0.15f))),
            };
        }

        static VibrationsTemplate Template(string name, string description, string settings, params TemplatePose[] poses)
        {
            var t = ScriptableObject.CreateInstance<VibrationsTemplate>();
            t.name = name;
            t.description = description;
            t.settings = settings;
            t.poses.AddRange(poses);
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        static TemplatePose P(string name, float hold, float lift, params (string muscle, float value)[] overrides)
        {
            var muscles = new float[HumanTrait.MuscleCount];
            foreach (var (m, v) in Stand) muscles[Index(m)] = v;
            foreach (var (m, v) in overrides) muscles[Index(m)] = v;
            return new TemplatePose { name = name, hold = hold, lift = lift, muscles = muscles };
        }

        const float F = 1f / 12f; // one frame at 12 fps

        // This pose's own transition out: snap time and overshoot (anticipation optional).
        static TemplatePose Snap(TemplatePose p, float duration, float overshoot, float anticipation = 0f)
        {
            p.customTransition = true;
            p.transition = T(duration, overshoot, anticipation);
            return p;
        }

        static int Index(string muscle)
        {
            int i = Array.IndexOf(HumanTrait.MuscleName, muscle);
            if (i < 0) throw new ArgumentException($"Unknown Humanoid muscle '{muscle}'");
            return i;
        }

        // Swap sides; center left/right and twist muscles flip sign.
        static (string, float)[] Mirror((string muscle, float value)[] m)
        {
            var result = new (string, float)[m.Length];
            for (int i = 0; i < m.Length; i++)
            {
                var (name, v) = m[i];
                if (name.StartsWith("Left ")) name = "Right " + name.Substring(5);
                else if (name.StartsWith("Right ")) name = "Left " + name.Substring(6);
                else if (name.Contains("Left-Right")) v = -v;
                result[i] = (name, v);
            }
            return result;
        }

        static Transition T(float duration, float overshoot, float anticipation) =>
            new() { duration = duration, overshoot = overshoot, anticipation = anticipation };

        static void Humanize(VibrationsAnimation a, float holds, float life, float head, float torso, float springiness = 0.5f)
        {
            a.humanize = true;
            a.movingHolds = holds;
            a.life = life;
            a.headFollow = head;
            a.torsoFollow = torso;
            a.springiness = springiness;
        }

        // The template's own Vibrations settings, stored as JSON like user templates.
        static string Feel(bool loop, Action<VibrationsAnimation> configure)
        {
            var a = ScriptableObject.CreateInstance<VibrationsAnimation>();
            a.loop = loop;
            a.humanize = false;
            configure(a);
            var json = JsonUtility.ToJson(a);
            Object.DestroyImmediate(a);
            return json;
        }
    }
}
