using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Vibrations
{
    // Bakes the character's skinned meshes as they are posed right now, and draws them as a ghost (Scene view)
    // or into a thumbnail texture.
    public sealed class PoseRenderer : IDisposable
    {
        public sealed class Snapshot : IDisposable
        {
            public readonly Mesh[] meshes;
            public readonly Matrix4x4[] matrices;
            public Bounds bounds;

            public Snapshot(int count)
            {
                meshes = new Mesh[count];
                matrices = new Matrix4x4[count];
                for (int i = 0; i < count; i++) meshes[i] = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            }

            public void Dispose()
            {
                foreach (var m in meshes) Object.DestroyImmediate(m);
            }
        }

        readonly SkinnedMeshRenderer[] renderers;
        readonly Material material;

        public PoseRenderer(Animator animator)
        {
            renderers = animator.GetComponentsInChildren<SkinnedMeshRenderer>();
            var shader = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>("Packages/com.meowarts.vibrations/Editor/Ghost.shader");
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        public void Dispose() => Object.DestroyImmediate(material);

        public Snapshot Bake(Snapshot reuse)
        {
            var s = reuse ?? new Snapshot(renderers.Length);
            s.bounds = default;
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (!r) continue;
                r.BakeMesh(s.meshes[i], true);
                s.matrices[i] = Matrix4x4.TRS(r.transform.position, r.transform.rotation, Vector3.one);
                var b = TransformBounds(s.meshes[i].bounds, s.matrices[i]);
                if (s.bounds.size == Vector3.zero) s.bounds = b;
                else s.bounds.Encapsulate(b);
            }
            return s;
        }

        // World height of the lowest vertex of the character as it is posed right now (exact, unlike bounds or bones).
        public float Lowest(ref Snapshot scratch)
        {
            scratch = Bake(scratch);
            float lowest = float.MaxValue;
            for (int i = 0; i < scratch.meshes.Length; i++)
            {
                if (!renderers[i]) continue;
                var m = scratch.matrices[i];
                foreach (var v in scratch.meshes[i].vertices) lowest = Mathf.Min(lowest, m.m10 * v.x + m.m11 * v.y + m.m12 * v.z + m.m13);
            }
            return lowest;
        }

        // Call during a Repaint event with the camera matrices already set (Scene view, or RenderThumbnail).
        public void Draw(Snapshot s, Color color, Vector3 toViewer)
        {
            material.SetColor("_Color", color);
            material.SetVector("_ViewDir", toViewer);
            for (int pass = 0; pass < 2; pass++)
            {
                material.SetPass(pass);
                for (int i = 0; i < s.meshes.Length; i++)
                for (int sub = 0; sub < s.meshes[i].subMeshCount; sub++)
                    Graphics.DrawMeshNow(s.meshes[i], s.matrices[i], sub);
            }
        }

        public void RenderThumbnail(Snapshot s, RenderTexture rt, Bounds frame, Quaternion view, Color color, Color background = default)
        {
            float aspect = rt.width / (float)rt.height;
            float horizontal = Mathf.Max(frame.extents.x, frame.extents.z);
            float half = Mathf.Max(frame.extents.y, horizontal / aspect);
            var eye = frame.center - view * Vector3.forward * (frame.extents.magnitude + 1f);
            var viewMatrix = Matrix4x4.Scale(new Vector3(1, 1, -1)) * Matrix4x4.TRS(eye, view, Vector3.one).inverse;
            var projection = Matrix4x4.Ortho(-half * aspect, half * aspect, -half, half, 0.01f, frame.extents.magnitude * 2f + 2f);

            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, background);
            GL.PushMatrix();
            GL.LoadProjectionMatrix(GL.GetGPUProjectionMatrix(projection, false));
            GL.modelview = viewMatrix;
            Draw(s, color, view * Vector3.back);
            GL.PopMatrix();
            RenderTexture.active = previous;
        }

        static Bounds TransformBounds(Bounds b, Matrix4x4 m)
        {
            var result = new Bounds(m.MultiplyPoint3x4(b.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) * 2 - 1, (i & 2) - 1, (i & 4) / 2f - 1));
                result.Encapsulate(m.MultiplyPoint3x4(corner));
            }
            return result;
        }
    }
}
