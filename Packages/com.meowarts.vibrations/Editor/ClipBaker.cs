using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Vibrations
{
    public static class ClipBaker
    {
        static readonly string[] RootCurves = { "RootT.x", "RootT.y", "RootT.z", "RootQ.x", "RootQ.y", "RootQ.z", "RootQ.w" };

        // Samples the tween on the rig and converts each frame to Humanoid muscles. Leaves the rig on the last frame.
        public static AnimationClip Bake(VibrationsAnimation a, Animator animator, int fps)
        {
            var bones = Rig.Bind(animator);
            var frames = Tween.Frames(a, fps, out var hips);
            bool limits = a.humanize && a.jointLimits;
            var lean = Secondary.Simulate(a, hips, bones[0], animator.transform, animator.humanScale, fps);

            var muscles = Enumerable.Range(0, HumanTrait.MuscleCount)
                .Where(m => !IsFinger(HumanTrait.BoneFromMuscle(m))).ToArray();
            var names = RootCurves.Concat(muscles.Select(m => HumanTrait.MuscleName[m])).ToArray();
            var keys = names.Select(_ => new Keyframe[frames.Length]).ToArray();

            var pose = new HumanPose();
            using (var handler = new HumanPoseHandler(animator.avatar, animator.transform))
            {
                for (int f = 0; f < frames.Length; f++)
                {
                    Rig.Apply(bones, frames[f], hips[f]);
                    Secondary.Apply(bones, animator.transform, lean[f]);
                    handler.GetHumanPose(ref pose);
                    if (limits) Rig.ClampMuscles(ref pose);

                    float time = f / (float)fps;
                    Vector3 p = pose.bodyPosition;
                    Quaternion q = pose.bodyRotation;
                    float[] root = { p.x, p.y, p.z, q.x, q.y, q.z, q.w };
                    for (int c = 0; c < names.Length; c++)
                        keys[c][f] = new Keyframe(time, c < root.Length ? root[c] : pose.muscles[muscles[c - root.Length]]);
                }
            }

            var mode = a.stepRate > 0f ? AnimationUtility.TangentMode.Constant : AnimationUtility.TangentMode.Linear;
            var curves = keys.Select(k =>
            {
                var curve = new AnimationCurve(k);
                for (int i = 0; i < k.Length; i++)
                {
                    AnimationUtility.SetKeyLeftTangentMode(curve, i, mode);
                    AnimationUtility.SetKeyRightTangentMode(curve, i, mode);
                }
                return curve;
            }).ToArray();

            var clip = new AnimationClip { frameRate = fps };
            AnimationUtility.SetEditorCurves(clip,
                names.Select(n => EditorCurveBinding.FloatCurve("", typeof(Animator), n)).ToArray(), curves);

            // In place: root rotation and position are baked into the pose, so the hip bob stays but nothing travels.
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = a.loop;
            settings.loopBlendOrientation = true;
            settings.loopBlendPositionY = true;
            settings.loopBlendPositionXZ = true;
            settings.keepOriginalOrientation = true;
            settings.keepOriginalPositionY = true;
            settings.keepOriginalPositionXZ = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            return clip;
        }

        static bool IsFinger(int bone) =>
            bone >= (int)HumanBodyBones.LeftThumbProximal && bone <= (int)HumanBodyBones.RightLittleDistal;
    }
}
