using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Vibrations
{
    // The animation's timing drawn to scale: a block per hold, the snap curve per transition.
    // Click to select a pose, drag an edge to change a hold or a snap time.
    class TimingBar : VisualElement
    {
        const float EdgeGrab = 5f;
        readonly Func<VibrationsAnimation> getAnimation;
        readonly Func<int> getSelected;
        readonly Func<float> getPlayhead; // < 0 when not previewing
        readonly Action<int> select;
        readonly Action changed;

        int dragPose = -1;
        bool dragTransition;
        float dragStartX, dragStartValue, dragPixelsPerSecond;

        public TimingBar(Func<VibrationsAnimation> getAnimation, Func<int> getSelected, Func<float> getPlayhead, Action<int> select, Action changed)
        {
            this.getAnimation = getAnimation;
            this.getSelected = getSelected;
            this.getPlayhead = getPlayhead;
            this.select = select;
            this.changed = changed;
            AddToClassList("vb-timing");
            tooltip = "Timing: blocks are holds, curves are snaps. Drag an edge to change it.";
            generateVisualContent += Draw;
            RegisterCallback<PointerDownEvent>(OnDown);
            RegisterCallback<PointerMoveEvent>(OnMove);
            RegisterCallback<PointerUpEvent>(OnUp);
        }

        float PixelsPerSecond(VibrationsAnimation a) => contentRect.width / Mathf.Max(Tween.Length(a), 0.01f);

        int Segments(VibrationsAnimation a) => a.loop ? a.poses.Count : a.poses.Count - 1;

        // Edge under x: (pose, true = end of its transition / false = end of its hold), or pose -1.
        (int pose, bool transition) EdgeAt(VibrationsAnimation a, float x)
        {
            float pps = PixelsPerSecond(a), t = 0f;
            for (int i = 0; i < a.poses.Count; i++)
            {
                t += a.poses[i].hold;
                if (Mathf.Abs(t * pps - x) < EdgeGrab) return (i, false);
                if (i >= Segments(a)) break;
                t += a.TransitionOut(i).duration;
                if (Mathf.Abs(t * pps - x) < EdgeGrab) return (i, true);
            }
            return (-1, false);
        }

        int PoseAt(VibrationsAnimation a, float x) => Tween.PoseAt(a, x / PixelsPerSecond(a));

        void OnDown(PointerDownEvent e)
        {
            var a = getAnimation();
            if (a == null || a.poses.Count == 0 || e.button != 0) return;
            var (pose, transition) = EdgeAt(a, e.localPosition.x);
            if (pose < 0)
            {
                select(PoseAt(a, e.localPosition.x));
                return;
            }
            Undo.RecordObject(a, "Change timing");
            dragPose = pose;
            dragTransition = transition;
            dragStartX = e.localPosition.x;
            dragPixelsPerSecond = PixelsPerSecond(a); // fixed during the drag so the bar doesn't rescale under the cursor
            dragStartValue = transition ? a.TransitionOut(pose).duration : a.poses[pose].hold;
            this.CapturePointer(e.pointerId);
            e.StopPropagation();
        }

        void OnMove(PointerMoveEvent e)
        {
            var a = getAnimation();
            if (a == null || a.poses.Count == 0) return;
            if (dragPose < 0 || !this.HasPointerCapture(e.pointerId))
            {
                EnableInClassList("vb-timing--resize", EdgeAt(a, e.localPosition.x).pose >= 0);
                return;
            }
            float value = dragStartValue + (e.localPosition.x - dragStartX) / dragPixelsPerSecond;
            var p = a.poses[dragPose];
            if (dragTransition)
            {
                if (!p.customTransition) // dragging a snap makes it this pose's own transition
                {
                    p.customTransition = true;
                    p.transition = a.transition;
                }
                p.transition.duration = Mathf.Clamp(value, 0.02f, 1f);
            }
            else p.hold = Mathf.Clamp(value, 0f, 2f);
            EditorUtility.SetDirty(a);
            changed();
            MarkDirtyRepaint();
        }

        void OnUp(PointerUpEvent e)
        {
            if (!this.HasPointerCapture(e.pointerId)) return;
            this.ReleasePointer(e.pointerId);
            dragPose = -1;
        }

        void Draw(MeshGenerationContext ctx)
        {
            var a = getAnimation();
            var r = contentRect;
            if (a == null || a.poses.Count == 0 || r.width < 10f) return;
            var p = ctx.painter2D;
            bool dark = EditorGUIUtility.isProSkin;
            var holdColor = dark ? new Color(0.36f, 0.36f, 0.36f) : new Color(0.66f, 0.66f, 0.66f);
            var selectedColor = dark ? new Color(0.17f, 0.36f, 0.53f) : new Color(0.23f, 0.45f, 0.69f);
            var curveColor = dark ? new Color(0.62f, 0.62f, 0.62f) : new Color(0.35f, 0.35f, 0.35f);
            float pps = PixelsPerSecond(a), x = r.x, mid = r.y + r.height * 0.5f;
            int selected = getSelected();

            for (int i = 0; i < a.poses.Count; i++)
            {
                float w = a.poses[i].hold * pps;
                p.fillColor = i == selected ? selectedColor : holdColor;
                p.BeginPath();
                p.MoveTo(new Vector2(x, r.y + 3f));
                p.LineTo(new Vector2(x + Mathf.Max(w, 2f), r.y + 3f));
                p.LineTo(new Vector2(x + Mathf.Max(w, 2f), r.yMax - 3f));
                p.LineTo(new Vector2(x, r.yMax - 3f));
                p.ClosePath();
                p.Fill();
                x += w;
                if (i >= Segments(a)) break;

                var tr = a.TransitionOut(i);
                float tw = tr.duration * pps;
                p.strokeColor = a.poses[i].customTransition ? selectedColor : curveColor;
                p.lineWidth = 1.5f;
                p.BeginPath();
                for (int k = 0; k <= 16; k++)
                {
                    float u = k / 16f, y = mid + (0.5f - Tween.Ease(u, tr)) * (r.height - 12f);
                    var pt = new Vector2(x + u * tw, Mathf.Clamp(y, r.y + 1f, r.yMax - 1f));
                    if (k == 0) p.MoveTo(pt);
                    else p.LineTo(pt);
                }
                p.Stroke();
                x += tw;
            }

            float playhead = getPlayhead();
            if (playhead >= 0f)
            {
                p.strokeColor = dark ? Color.white : Color.black;
                p.lineWidth = 1.5f;
                p.BeginPath();
                p.MoveTo(new Vector2(r.x + playhead * pps, r.y));
                p.LineTo(new Vector2(r.x + playhead * pps, r.yMax));
                p.Stroke();
            }
        }
    }
}
