using UnityEngine;
using static UnityEngine.HumanBodyBones;

namespace Vibrations
{
    // Pose-to-pose sampling. Each transition is a damped spring from pose i to i+1; transitions are
    // composed additively (P0 * D0^e0 * D1^e1 ...) so a spring still settling never pops when the
    // next one starts.
    public static class Tween
    {
        public enum Group { Spine, Head, Arms, Legs }

        // Fingers, eyes and jaw are left out for now.
        public static readonly HumanBodyBones[] Bones =
        {
            Hips, Spine, Chest, UpperChest, Neck, Head,
            LeftShoulder, LeftUpperArm, LeftLowerArm, LeftHand,
            RightShoulder, RightUpperArm, RightLowerArm, RightHand,
            LeftUpperLeg, LeftLowerLeg, LeftFoot, LeftToes,
            RightUpperLeg, RightLowerLeg, RightFoot, RightToes,
        };

        static readonly Group[] Groups =
        {
            Group.Spine, Group.Spine, Group.Spine, Group.Spine, Group.Head, Group.Head,
            Group.Arms, Group.Arms, Group.Arms, Group.Arms,
            Group.Arms, Group.Arms, Group.Arms, Group.Arms,
            Group.Legs, Group.Legs, Group.Legs, Group.Legs,
            Group.Legs, Group.Legs, Group.Legs, Group.Legs,
        };

        // Normalized distance from the hips: 0 leads, 1 trails by the full overlap.
        static readonly float[] Depth =
        {
            0f, 0.2f, 0.3f, 0.4f, 0.55f, 0.7f,
            0.45f, 0.6f, 0.8f, 1f,
            0.45f, 0.6f, 0.8f, 1f,
            0.3f, 0.6f, 0.8f, 1f,
            0.3f, 0.6f, 0.8f, 1f,
        };

        const float AnticipationShare = 0.35f; // part of the duration spent winding up

        // x = time since transition start / duration. 0 at x<=0, first reaches 1 at x=1,
        // overshoots to 1 + overshoot*(1+anticipation), then settles on 1.
        public static float Ease(float x, Transition tr)
        {
            if (x <= 0f) return 0f;
            if (tr.curve == Transition.Curve.Linear) return Mathf.Min(x, 1f);
            if (tr.curve == Transition.Curve.Smooth) return Mathf.SmoothStep(0f, 1f, x);
            float a = tr.anticipation, p = a > 0f ? AnticipationShare : 0f;
            if (x < p) return -a * (1f - Mathf.Cos(Mathf.PI * x / p)) * 0.5f;

            // Underdamped spring step response, tuned so overshoot is exact and the first crossing is at u=1.
            float u = (x - p) / (1f - p);
            float k = -Mathf.Log(Mathf.Clamp(tr.overshoot, 0.001f, 0.9f)) / Mathf.PI; // damping / frequency
            float w = Mathf.PI - Mathf.Atan(1f / k);
            float spring = 1f - Mathf.Exp(-k * w * u) * (Mathf.Cos(w * u) + k * Mathf.Sin(w * u));
            return -a + (1f + a) * spring;
        }

        public static float Length(VibrationsAnimation a)
        {
            float len = 0f;
            for (int i = 0; i < a.poses.Count; i++)
            {
                len += a.poses[i].hold;
                if (a.loop || i < a.poses.Count - 1) len += a.TransitionOut(i).duration;
            }
            return len;
        }

        // The pose whose hold or outgoing transition contains time t.
        public static int PoseAt(VibrationsAnimation a, float t)
        {
            int segs = a.loop ? a.poses.Count : a.poses.Count - 1;
            float end = 0f;
            for (int i = 0; i < a.poses.Count; i++)
            {
                end += a.poses[i].hold + (i < segs ? a.TransitionOut(i).duration : 0f);
                if (t < end) return i;
            }
            return a.poses.Count - 1;
        }

        public static void Sample(VibrationsAnimation a, float t, Quaternion[] rot, out Vector3 hips)
        {
            var poses = a.poses;
            int n = poses.Count, segs = a.loop ? n : n - 1;
            float len = Length(a);
            float drift = a.humanize ? a.movingHolds : 0f;
            hips = poses[0].hipsPosition;
            if (a.stepRate > 0f) t = Mathf.Floor(t * a.stepRate + 1e-4f) / a.stepRate;

            var start = new float[Mathf.Max(segs, 0)];
            var ease = new Transition[start.Length];
            float key = 0f;
            for (int s = 0; s < segs; s++)
            {
                ease[s] = a.TransitionOut(s);
                start[s] = key + poses[s].hold;
                key = start[s] + ease[s].duration;
            }

            // In a loop the previous cycle is still settling at the start, so evaluate it too.
            int firstCycle = a.loop && len > 0f ? -1 : 0;
            for (int b = 0; b < Bones.Length; b++)
            {
                float tau = t - a.overlap * a.Weight(Groups[b]) * Depth[b];
                float loose = Mathf.Clamp01(a.looseness * a.Weight(Groups[b]) * Depth[b]);
                tau = firstCycle < 0 ? Mathf.Repeat(tau, len) : Mathf.Max(tau, 0f);
                var r = poses[0].rotations[b];
                for (int c = firstCycle; c <= 0; c++)
                for (int s = 0; s < segs; s++)
                {
                    float local = tau - start[s] - c * len;
                    var tr = loose > 0f ? Loosen(ease[s], loose) : ease[s];
                    float e = Ease(local / Mathf.Max(tr.duration, 1e-4f), tr);
                    if (drift > 0f) // moving hold: creep toward the next pose during the hold, snap covers the rest
                    {
                        float hold = poses[s].hold;
                        float holdProgress = hold > 1e-4f ? Mathf.SmoothStep(0f, 1f, (local + hold) / hold) : local >= 0f ? 1f : 0f;
                        e = drift * holdProgress + (1f - drift) * e;
                    }
                    if (e == 0f) continue;
                    var from = poses[s].rotations[b];
                    var to = poses[(s + 1) % n].rotations[b];
                    r *= Pow(Quaternion.Inverse(from) * to, e);
                    if (b == 0) hips += (poses[(s + 1) % n].hipsPosition - poses[s].hipsPosition) * e;
                }
                if (a.humanize && a.life > 0f && b != 0 && Groups[b] != Group.Legs) r *= Life(b, t, len, a.life, a.seed);
                rot[b] = r.normalized;
            }
        }

        // Every frame of the clip, with the stateful Humanize passes applied. Last frame = end (or first, in a loop).
        public static Quaternion[][] Frames(VibrationsAnimation a, int fps, out Vector3[] hips)
        {
            float len = Length(a);
            int count = Mathf.Max(1, Mathf.RoundToInt(len * fps)); // timeline stretched a hair to land on whole frames
            var frames = new Quaternion[count + 1][];
            hips = new Vector3[count + 1];
            for (int f = 0; f <= count; f++)
            {
                frames[f] = new Quaternion[Bones.Length];
                Sample(a, f * len / count, frames[f], out hips[f]);
            }
            if (a.humanize && a.inbetweens > 0f) LimitJumps(frames, a.loop, MaxJump(a.inbetweens, fps));
            return frames;
        }

        public static float MaxJump(float inbetweens, int fps) => Mathf.Lerp(180f, 12f, inbetweens) * 30f / fps;

        // Auto-inbetweens: no bone rotates more than maxDegrees in one frame; big jumps spread over the next frames.
        static void LimitJumps(Quaternion[][] frames, bool loop, float maxDegrees)
        {
            var state = (Quaternion[])frames[0].Clone();
            int last = frames.Length - 1;
            if (loop) // warm up on one cycle so the loop point is already in its steady state
                for (int f = 0; f < last; f++)
                for (int b = 0; b < state.Length; b++)
                    state[b] = Quaternion.RotateTowards(state[b], frames[f][b], maxDegrees);
            for (int f = 0; f <= last; f++)
            for (int b = 0; b < state.Length; b++)
                frames[f][b] = state[b] = Quaternion.RotateTowards(state[b], frames[f][b], maxDegrees);
            if (loop) frames[last] = (Quaternion[])frames[0].Clone();
        }

        // Subtle sway. Sum of sines with whole cycles per loop, so it repeats seamlessly.
        static Quaternion Life(int bone, float t, float len, float degrees, int seed)
        {
            float period = len > 0f ? len : 1f;
            int baseCycles = Mathf.Max(1, Mathf.RoundToInt(period * 0.7f));
            var angles = Vector3.zero;
            for (int axis = 0; axis < 3; axis++)
            for (int k = 1; k <= 3; k++)
            {
                float phase = Hash(seed, bone, axis, k) * 2f * Mathf.PI;
                angles[axis] += Mathf.Sin(2f * Mathf.PI * baseCycles * k * t / period + phase) / k;
            }
            return Quaternion.Euler(angles * (degrees / 1.83f)); // 1.83 = 1 + 1/2 + 1/3
        }

        static float Hash(int seed, int bone, int axis, int k) =>
            Mathf.Repeat(Mathf.Sin(seed * 12.9898f + bone * 78.233f + axis * 37.719f + k * 4.581f) * 43758.5453f, 1f);

        // Follow-through: loose bones arrive later and swing further before settling.
        static Transition Loosen(Transition tr, float loose)
        {
            tr.duration *= 1f + 1.5f * loose;
            tr.overshoot = Mathf.Lerp(tr.overshoot, 0.7f, loose);
            return tr;
        }

        static Quaternion Pow(Quaternion d, float e)
        {
            if (d.w < 0f) d = new Quaternion(-d.x, -d.y, -d.z, -d.w); // shortest path
            return Quaternion.SlerpUnclamped(Quaternion.identity, d, e);
        }
    }
}
