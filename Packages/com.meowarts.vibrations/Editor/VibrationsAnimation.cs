using System;
using System.Collections.Generic;
using UnityEngine;

namespace Vibrations
{
    [Serializable]
    public struct Transition
    {
        public enum Curve { Spring, Linear, Smooth }

        [Tooltip("Spring: snap with overshoot and wind-up (toon). Linear: constant speed. Smooth: ease in and out.")]
        public Curve curve;
        [Tooltip("Snap time in seconds: how long until the pose is first reached.")]
        [Range(0.02f, 0.6f)] public float duration;
        [Tooltip("How far past the target it swings. Higher = more wobble before settling.")]
        [Range(0f, 0.7f)] public float overshoot;
        [Tooltip("Wind-up: small move away from the target before snapping.")]
        [Range(0f, 0.5f)] public float anticipation;
    }

    [Serializable]
    public class Pose
    {
        public string name = "Pose";
        [Tooltip("Seconds to stay on this pose before the next transition starts.")]
        [Min(0f)] public float hold = 0.1f;
        [Tooltip("Override the animation's transition for the move out of this pose.")]
        public bool customTransition;
        public Transition transition;
        [Tooltip("After each edit, move the body so its lowest point touches the floor. Off for airborne poses.")]
        public bool grounded = true;
        public Quaternion[] rotations = new Quaternion[Tween.Bones.Length]; // local, indexed like Tween.Bones
        public Vector3 hipsPosition; // local

        // Timing before Auto Timing touched it, for Reset Timing.
        [HideInInspector] public bool hasSavedTiming;
        [HideInInspector] public float savedHold;
        [HideInInspector] public bool savedCustomTransition;
        [HideInInspector] public Transition savedTransition;

        public Pose Clone()
        {
            var p = (Pose)MemberwiseClone();
            p.rotations = (Quaternion[])rotations.Clone();
            return p;
        }
    }

    [CreateAssetMenu(menuName = "Vibrations/Animation")]
    public class VibrationsAnimation : ScriptableObject
    {
        public static readonly (string name, Transition transition, float stepRate, float overlap, float looseness)[] Presets =
        {
            ("Snappy", new Transition { duration = 0.12f, overshoot = 0.25f, anticipation = 0.05f }, 0f, 0.05f, 0.2f),
            ("Pixar", new Transition { duration = 0.1f, overshoot = 0.35f, anticipation = 0.1f }, 0f, 0.1f, 0.7f),
            ("Rubbery", new Transition { duration = 0.2f, overshoot = 0.5f, anticipation = 0.1f }, 0f, 0.1f, 0.5f),
            ("Stop-motion", new Transition { duration = 0.1f, overshoot = 0.2f, anticipation = 0f }, 12f, 0.04f, 0f),
            ("Heavy", new Transition { duration = 0.35f, overshoot = 0.08f, anticipation = 0.15f }, 0f, 0.08f, 0.1f),
            ("Linear", new Transition { curve = Transition.Curve.Linear, duration = 0.25f }, 0f, 0f, 0f),
            ("Soft", new Transition { curve = Transition.Curve.Smooth, duration = 0.3f }, 0f, 0.06f, 0.15f),
        };

        public List<Pose> poses = new();
        [HideInInspector] public Avatar avatar; // the rig the poses were captured on: they're local bone rotations, so they only fit it
        [Tooltip("Last pose tweens back to the first.")]
        public bool loop = true;
        public Transition transition = Presets[0].transition;
        [Tooltip("Choppiness: hold frames at this rate (12 = on twos at 24fps). 0 = smooth.")]
        [Range(0f, 30f)] public float stepRate;
        [Tooltip("Seconds the extremities (hands, head) trail behind the hips.")]
        [Range(0f, 0.3f)] public float overlap = Presets[0].overlap;
        [Tooltip("Arms and head drag behind and keep swinging after the body snaps. Scaled by the body-part weights.")]
        [Range(0f, 1f)] public float looseness = Presets[0].looseness;
        [Range(0f, 2f)] public float spineWeight = 1f;
        [Range(0f, 2f)] public float headWeight = 1f;
        [Range(0f, 2f)] public float armsWeight = 1f;
        [Tooltip("Keep at 0 so feet plant on time.")]
        [Range(0f, 2f)] public float legsWeight;

        [Tooltip("Smart constraints that make the motion feel more natural.")]
        public bool humanize;
        [Tooltip("Keep joints inside the avatar's natural range (Humanoid muscle limits) while posing, previewing and exporting.")]
        public bool jointLimits = true;
        [Tooltip("Instead of freezing, the body keeps creeping toward the next pose during holds.")]
        [Range(0f, 0.3f)] public float movingHolds = 0.08f;
        [Tooltip("Spread jumps that are too big for one frame over the next frames.")]
        [Range(0f, 1f)] public float inbetweens = 0.4f;
        [Tooltip("Degrees of subtle random sway on the upper body. Loops seamlessly.")]
        [Range(0f, 3f)] public float life = 0.6f;
        public int seed = 1;
        [Tooltip("Head lags behind the body's movement: drops tilt the chin up, rises tip it down.")]
        [Range(0f, 1f)] public float headFollow = 0.5f;
        [Tooltip("Spine leans against the body's movement, then snaps back.")]
        [Range(0f, 1f)] public float torsoFollow = 0.4f;
        [Tooltip("How much the follow-through overshoots when it settles.")]
        [Range(0f, 1f)] public float springiness = 0.5f;

        public Transition TransitionOut(int i) => poses[i].customTransition ? poses[i].transition : transition;

        public float Weight(Tween.Group g) => g switch
        {
            Tween.Group.Spine => spineWeight,
            Tween.Group.Head => headWeight,
            Tween.Group.Arms => armsWeight,
            _ => legsWeight,
        };

        public void ApplyPreset(int i)
        {
            (_, transition, stepRate, overlap, looseness) = Presets[i];
        }
    }
}
