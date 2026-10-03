using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Vibrations
{
    // A pose in Humanoid muscle space, so it fits any Humanoid character.
    [Serializable]
    public class TemplatePose
    {
        public string name = "Pose";
        public float hold = 0.1f;
        public bool customTransition;
        public Transition transition;
        public float[] muscles = new float[0];
        public Quaternion bodyRotation = Quaternion.identity; // relative to the character's rest body rotation
        [Tooltip("Height of the lowest foot above the floor, in avatar-scaled meters. 0 = standing on the floor.")]
        public float lift;
    }

    // A reusable animation: poses in muscle space plus the animation's settings (feel, humanize...).
    [CreateAssetMenu(menuName = "Vibrations/Template")]
    public class VibrationsTemplate : ScriptableObject
    {
        [TextArea] public string description;
        public List<TemplatePose> poses = new();
        [HideInInspector] public string settings; // VibrationsAnimation fields as JSON (poses excluded)

        public bool Loops => settings != null && settings.Contains("\"loop\":true");
    }

    // Converts between templates and a character in the scene. Leaves the rig posed (fingers included),
    // so wrap calls in Rig.SaveAll / Rig.RestoreAll.
    public struct TemplateRig
    {
        public Transform root; // the Animator's transform
        public Transform[] bones;
        public HumanPoseHandler handler;
        public Pose rest;
        public float[] soles;
        public float floorHeight;
        public float humanScale;
        public Func<float> lowestPoint; // exact lowest point of the mesh; null = estimate from the foot bones

        public void Apply(TemplatePose tp)
        {
            var self = this;
            Rig.AtOrigin(root, () => self.ApplyAtOrigin(tp));
            if (lowestPoint != null) bones[0].position += Vector3.up * (floorHeight - lowestPoint());
            else Rig.Ground(bones, soles, floorHeight);
            bones[0].position += Vector3.up * tp.lift * humanScale;
        }

        void ApplyAtOrigin(TemplatePose tp)
        {
            Rig.Apply(bones, rest.rotations, rest.hipsPosition);
            var hp = new HumanPose();
            handler.GetHumanPose(ref hp);
            var muscles = new float[HumanTrait.MuscleCount];
            Array.Copy(tp.muscles, muscles, Mathf.Min(tp.muscles.Length, muscles.Length));
            hp.muscles = muscles;
            var relative = tp.bodyRotation == default ? Quaternion.identity : tp.bodyRotation;
            hp.bodyRotation *= relative;
            handler.SetHumanPose(ref hp);
        }

        public Pose ToPose(TemplatePose tp)
        {
            Apply(tp);
            var p = new Pose { name = tp.name, hold = tp.hold, customTransition = tp.customTransition, transition = tp.transition,
                grounded = tp.lift <= 0f };
            Rig.Capture(bones, p);
            return p;
        }

        public TemplatePose FromPose(Pose p)
        {
            TemplatePose result = null;
            var self = this;
            Rig.AtOrigin(root, () => result = self.FromPoseAtOrigin(p));
            float lowest = lowestPoint?.Invoke() ?? Rig.LowestSole(bones, soles);
            result.lift = Mathf.Max(0f, lowest - floorHeight) / humanScale; // floor is in world space
            return result;
        }

        TemplatePose FromPoseAtOrigin(Pose p)
        {
            Rig.Apply(bones, rest.rotations, rest.hipsPosition);
            var restPose = new HumanPose();
            handler.GetHumanPose(ref restPose);
            Rig.Apply(bones, p.rotations, p.hipsPosition);
            var hp = new HumanPose();
            handler.GetHumanPose(ref hp);
            return new TemplatePose
            {
                name = p.name, hold = p.hold, customTransition = p.customTransition, transition = p.transition,
                muscles = hp.muscles,
                bodyRotation = Quaternion.Inverse(restPose.bodyRotation) * hp.bodyRotation,
            };
        }

        public VibrationsAnimation CreateAnimation(VibrationsTemplate t)
        {
            var a = ScriptableObject.CreateInstance<VibrationsAnimation>();
            if (!string.IsNullOrEmpty(t.settings)) JsonUtility.FromJsonOverwrite(t.settings, a);
            a.poses = new List<Pose>();
            foreach (var tp in t.poses) a.poses.Add(ToPose(tp));
            return a;
        }

        public VibrationsTemplate CreateTemplate(VibrationsAnimation a)
        {
            var t = ScriptableObject.CreateInstance<VibrationsTemplate>();
            var settings = Object.Instantiate(a);
            settings.poses.Clear();
            t.settings = JsonUtility.ToJson(settings);
            Object.DestroyImmediate(settings);
            foreach (var p in a.poses) t.poses.Add(FromPose(p));
            return t;
        }
    }
}
