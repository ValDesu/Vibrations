using System.Linq;
using UnityEngine;

namespace Vibrations
{
    public static class Rig
    {
        public static Transform[] Bind(Animator animator) =>
            Tween.Bones.Select(animator.GetBoneTransform).ToArray();

        public static void Capture(Transform[] bones, Pose p)
        {
            if (p.rotations == null || p.rotations.Length != bones.Length) p.rotations = new Quaternion[bones.Length];
            for (int i = 0; i < bones.Length; i++)
                p.rotations[i] = bones[i] ? bones[i].localRotation : Quaternion.identity;
            p.hipsPosition = bones[0].localPosition;
        }

        public static void Apply(Transform[] bones, Quaternion[] rot, Vector3 hips)
        {
            for (int i = 0; i < bones.Length; i++)
                if (bones[i]) bones[i].localRotation = rot[i];
            bones[0].localPosition = hips;
        }

        public static readonly int[] SoleBones = { 16, 17, 20, 21 }; // feet and toes: what touches the floor

        // soles[k] = rest height of SoleBones[k] above the floor.
        public static float LowestSole(Transform[] bones, float[] soles)
        {
            float lowest = float.MaxValue;
            for (int k = 0; k < SoleBones.Length; k++)
                if (bones[SoleBones[k]]) lowest = Mathf.Min(lowest, bones[SoleBones[k]].position.y - soles[k]);
            return lowest;
        }

        // Moves the body so the lowest foot touches the floor.
        public static void Ground(Transform[] bones, float[] soles, float floorHeight)
        {
            float dy = floorHeight - LowestSole(bones, soles);
            if (Mathf.Abs(dy) > 1e-5f) bones[0].position += Vector3.up * dy;
        }

        // Every transform under root, for restoring after something poses the whole skeleton (fingers included).
        public static (Transform[] transforms, Quaternion[] rotations, Vector3[] positions) SaveAll(Transform root)
        {
            var all = root.GetComponentsInChildren<Transform>();
            var rotations = new Quaternion[all.Length];
            var positions = new Vector3[all.Length];
            for (int i = 0; i < all.Length; i++) (rotations[i], positions[i]) = (all[i].localRotation, all[i].localPosition);
            return (all, rotations, positions);
        }

        public static void RestoreAll((Transform[] transforms, Quaternion[] rotations, Vector3[] positions) saved)
        {
            for (int i = 0; i < saved.transforms.Length; i++)
                if (saved.transforms[i]) saved.transforms[i].SetLocalPositionAndRotation(saved.positions[i], saved.rotations[i]);
        }

        public static void ClampMuscles(ref HumanPose pose)
        {
            for (int m = 0; m < pose.muscles.Length; m++) pose.muscles[m] = Mathf.Clamp(pose.muscles[m], -1f, 1f);
        }

        // Pushes bones that break the avatar's muscle limits back inside them (no backward elbows, no broken wrists).
        // The Humanoid round trip isn't lossless, so every bone that was within limits is restored exactly.
        public static void ClampToLimits(HumanPoseHandler handler, Transform[] bones)
        {
            var pose = new HumanPose();
            handler.GetHumanPose(ref pose);
            var broken = new bool[HumanTrait.BoneCount];
            bool any = false;
            for (int m = 0; m < pose.muscles.Length; m++)
            {
                if (Mathf.Abs(pose.muscles[m]) <= 1f) continue;
                pose.muscles[m] = Mathf.Clamp(pose.muscles[m], -1f, 1f);
                broken[HumanTrait.BoneFromMuscle(m)] = any = true;
            }
            if (!any) return;

            var before = new Quaternion[bones.Length];
            for (int i = 0; i < bones.Length; i++) if (bones[i]) before[i] = bones[i].localRotation;
            var hips = bones[0].localPosition;
            handler.SetHumanPose(ref pose);
            for (int i = 0; i < bones.Length; i++)
                if (bones[i] && !broken[(int)Tween.Bones[i]]) bones[i].localRotation = before[i];
            bones[0].localPosition = hips;
        }

        // Analytic two-bone IK. Keeps the current bend plane (pole is only used when the limb is straight).
        // The end bone keeps its world rotation (feet stay flat) or its local one (hands follow the forearm).
        public static void SolveTwoBone(Transform a, Transform b, Transform c, Vector3 target, Vector3 pole, bool keepEndWorldRotation)
        {
            var endRotation = c.rotation;
            Vector3 pa = a.position, pb = b.position, pc = c.position;
            float lab = (pb - pa).magnitude, lbc = (pc - pb).magnitude;
            float lat = Mathf.Clamp((target - pa).magnitude, Mathf.Abs(lab - lbc) + 1e-4f, lab + lbc - 1e-4f);

            var bend = Vector3.Cross(pc - pa, pb - pa);
            if (bend.sqrMagnitude < 1e-6f * lab * lab * lab * lab) bend = Vector3.Cross(pc - pa, pole);
            float current = Vector3.Angle(pa - pb, pc - pb);
            float wanted = Mathf.Acos(Mathf.Clamp((lat * lat - lab * lab - lbc * lbc) / (-2f * lab * lbc), -1f, 1f)) * Mathf.Rad2Deg;
            b.rotation = Quaternion.AngleAxis(wanted - current, bend.normalized) * b.rotation;
            a.rotation = Quaternion.FromToRotation(c.position - pa, target - pa) * a.rotation;
            if (keepEndWorldRotation) c.rotation = endRotation;
        }
    }
}
