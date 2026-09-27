using System;
using UnityEngine;

namespace Vibrations
{
    // Follow-through: the spine and head react to how the hips move, so only arms and legs need posing.
    // Hips acceleration drives two damped springs (torso, and a slower head); their lag tilts the chain.
    // Drop → chin up, rise → chin down, sideways → lean into the lag, forward → head tips back.
    public static class Secondary
    {
        const float DegreesPerUnit = 300f; // tilt per unit of lag (lag is in avatar-scaled meters)
        const float MaxDegrees = 35f;

        // (index into Tween.Bones, share of the tilt, head chain?)
        static readonly (int bone, float share, bool head)[] Chain =
            { (1, 0.3f, false), (2, 0.35f, false), (3, 0.35f, false), (4, 0.4f, true), (5, 0.6f, true) };

        // Per frame: torso pitch, torso roll, head pitch, head roll (degrees, around the character's right/forward axes).
        public static Vector4[] Simulate(VibrationsAnimation a, Vector3[] hipsLocal, Transform hips, Transform root, float humanScale, int fps)
        {
            int n = hipsLocal.Length;
            var result = new Vector4[n];
            if (!a.humanize || (a.headFollow <= 0f && a.torsoFollow <= 0f) || n < 3 || hips.parent == null) return result;

            var p = new Vector3[n];
            for (int f = 0; f < n; f++) p[f] = root.InverseTransformPoint(hips.parent.TransformPoint(hipsLocal[f])) / humanScale;
            int last = n - 1; // in a loop, frame `last` repeats frame 0
            Vector3 Acceleration(int f)
            {
                int prev = a.loop ? (f == 0 ? last - 1 : f - 1) : Mathf.Max(f - 1, 0);
                int next = a.loop ? (f >= last - 1 ? (f + 1) % last : f + 1) : Mathf.Min(f + 1, last);
                return (p[next] - 2f * p[f] + p[prev]) * fps * fps;
            }

            float zeta = Mathf.Lerp(0.9f, 0.2f, a.springiness);
            var torso = Spring(Acceleration, n, fps, 2f * Mathf.PI * 3.5f, zeta, a.loop);
            var head = Spring(Acceleration, n, fps, 2f * Mathf.PI * 2.5f, zeta, a.loop);
            for (int f = 0; f < n; f++)
            {
                var (tp, tr) = Tilt(torso[f], a.torsoFollow);
                var (hp, hr) = Tilt(head[f], a.headFollow);
                result[f] = new Vector4(tp, tr, hp, hr);
            }
            return result;
        }

        public static void Apply(Transform[] bones, Transform root, Vector4 lean)
        {
            foreach (var (bone, share, head) in Chain)
            {
                var t = bones[bone];
                if (!t) continue;
                float pitch = head ? lean.z : lean.x, roll = head ? lean.w : lean.y;
                t.rotation = Quaternion.AngleAxis(pitch * share, root.right) * Quaternion.AngleAxis(roll * share, root.forward) * t.rotation;
            }
        }

        // Lag up or back → negative pitch around right = chin up. Lag right → negative roll around forward = lean right.
        static (float pitch, float roll) Tilt(Vector3 lag, float amount)
        {
            float k = DegreesPerUnit * amount;
            return (Mathf.Clamp(-k * (lag.y - lag.z), -MaxDegrees, MaxDegrees), Mathf.Clamp(-k * lag.x, -MaxDegrees, MaxDegrees));
        }

        // x'' = -w²x - 2ζw x' - a : the lag of a mass carried by the hips. Loops run twice so the loop point is settled.
        static Vector3[] Spring(Func<int, Vector3> acceleration, int n, int fps, float w, float zeta, bool loop)
        {
            const int substeps = 4;
            float dt = 1f / (fps * substeps);
            var x = Vector3.zero;
            var v = Vector3.zero;
            var output = new Vector3[n];
            int frames = loop ? n - 1 : n;
            for (int pass = loop ? 0 : 1; pass < 2; pass++)
            for (int f = 0; f < frames; f++)
            {
                var acc = acceleration(f);
                for (int s = 0; s < substeps; s++)
                {
                    v += (-w * w * x - 2f * zeta * w * v - acc) * dt;
                    x += v * dt;
                }
                output[f] = x;
            }
            if (loop) output[n - 1] = output[0];
            return output;
        }
    }
}
