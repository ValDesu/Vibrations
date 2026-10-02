# Changelog

## [0.2.1] - 2026-10-02

- Animations remember the rig they were made on. Opening one on a character with a different rig no longer breaks it
  (rotated, floating, only the arms moving): the Animate tab offers **Make a Copy for This Character**, which converts
  the poses through Humanoid muscles into a new animation next to the original. The source character doesn't need to be
  in the scene. Animations from earlier versions record their rig the first time a pose is selected.
- Fix: posing a rig without some optional Humanoid bones (UpperChest, Toes...) threw "Undo objects may not be null".

## [0.2.0] - 2026-09-28

- Mirror poses left ↔ right, exact on any Humanoid rig (right-click a card, or Mirror in the Pose tools).
- Right-click menu on pose cards: Duplicate, Duplicate at End, Duplicate Mirrored, Duplicate Mirrored at End, Mirror, Copy/Paste Pose, Move, Delete.
- Drag and drop pose cards to reorder them: a see-through copy follows the pointer and a card-sized slot opens where they'll land.
- Multi-select pose cards (Shift-click for a range, Cmd/Ctrl-click to add or remove one), then drag or right-click to move, duplicate, mirror or delete them together.
- AI tool `mirror_pose`.
- Auto Timing: one click sets holds and snap times from how much each pose moves (same total length, on the frame grid). Reset Timing restores the original.
- Pose tools for the selection or all poses: Ground / Ground All, Center / Center All, Mirror / Mirror All, Reset to Rest, Fix All.
- Fix: mirroring, templates and clip export on a character away from the world origin (or rotated) moved the body far away.
  Unity's GetHumanPose is world-space while SetHumanPose is root-relative; all Humanoid operations now run at the origin.
- Floor = the scene's ground under the character (raycast on colliders), or the exact bottom of the mesh; **From Scene** re-detects it.
- Fix: grounding now uses the lowest point of the actual mesh. The old foot-bone estimate was off by 2–7 cm on tilted feet
  (heel strikes, tiptoes), and the floor came from the mesh bounds, which can sit well below the feet (15.6 cm on the test
  character), hiding the floor grid under the scene's ground plane.
- Fix: undoing a pose operation (mirror, ground, move...) could be overwritten by the next pose switch.

## [0.1.0] - 2026-09-27

First release.

- Pose-to-pose animation for Humanoid characters with spring tweens (snap time, overshoot, anticipation), plus Linear and Smooth curves.
- Feel presets, choppiness, overlap, looseness, per-pose transitions, timing bar.
- Posing tools: IK hands and feet, hips with planted feet, joint rotation rings, floor snapping, onion skin (also during preview).
- Humanize: joint limits, moving holds, inbetweens, life, head and torso follow-through.
- Templates: 8 built-in starters with their own feel; save and share your own.
- AI assistant (Anthropic or OpenAI) with animation tools and visual previews.
- Export to in-place Humanoid clips at 30 or 60 fps.
