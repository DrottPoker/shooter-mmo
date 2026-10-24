using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Gameplay;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // MMORPG > Tools > Art Pipeline > Wardrobe: a body of the appearance catalogs dressed in the
    // garments ticked on the left, with the paint layers picked there, playing Idle, Walk, Jog, or
    // Death on a moving ground with the cloth solver running, for reviewing garments in motion
    // before approving them. Drag in the view to turn the camera around the body, and scroll to
    // come closer. The window's layout is a temporary tool interface; WardrobePreview does the work.
    public sealed class WardrobeWindow : EditorWindow
    {
        public const string MenuPath = "MMORPG/Tools/Art Pipeline/Wardrobe";

        private const float SidebarWidth = 220f;

        private readonly HashSet<string> chosen = new HashSet<string>();
        private readonly HashSet<string> chosenPaints = new HashSet<string>();
        private ArtAssetManifest[] bodies = new ArtAssetManifest[0];
        private int bodyIndex;
        private WardrobePreview preview;
        private Camera previewCamera;
        private RenderTexture texture;
        private bool paused;
        private float yaw = 200f;
        private float pitch = 10f;
        private float distance = 3.2f;
        private double lastTime;
        private string problem;
        private Vector2 scroll;

        [MenuItem(MenuPath)]
        public static void Open()
        {
            GetWindow<WardrobeWindow>("Wardrobe").Show();
        }

        private void OnEnable()
        {
            bodies = WardrobePreview.Bodies();
            lastTime = EditorApplication.timeSinceStartup;
            EditorApplication.update += Tick;
            Rebuild();
        }

        private void OnDisable()
        {
            EditorApplication.update -= Tick;
            Release();
        }

        private void Rebuild()
        {
            Release();
            problem = null;
            if (bodies.Length == 0)
            {
                problem = "No body has an appearance catalog with garments; export a body and a garment first.";
                return;
            }

            bodyIndex = Mathf.Clamp(bodyIndex, 0, bodies.Length - 1);
            try
            {
                preview = new WardrobePreview(bodies[bodyIndex]);
                chosen.IntersectWith(preview.Catalog.Garments.Select(garment => garment.Name));
                chosenPaints.IntersectWith(preview.Catalog.Paints.Select(paint => paint.Name));
                preview.Wear(chosen, chosenPaints);
            }
            catch (System.Exception error)
            {
                problem = error.Message;
                Release();
                return;
            }

            var cameraObject = new GameObject("WardrobeCamera") { hideFlags = HideFlags.HideAndDontSave };
            previewCamera = cameraObject.AddComponent<Camera>();
            previewCamera.enabled = false;
            previewCamera.clearFlags = CameraClearFlags.SolidColor;
            previewCamera.backgroundColor = new Color(0.55f, 0.6f, 0.68f);
            previewCamera.fieldOfView = 30f;
            previewCamera.nearClipPlane = 0.05f;
            previewCamera.farClipPlane = 100f;
        }

        private void Release()
        {
            preview?.Dispose();
            preview = null;
            if (previewCamera != null)
            {
                DestroyImmediate(previewCamera.gameObject);
                previewCamera = null;
            }

            if (texture != null)
            {
                texture.Release();
                DestroyImmediate(texture);
                texture = null;
            }
        }

        private void Tick()
        {
            var now = EditorApplication.timeSinceStartup;
            var deltaTime = Mathf.Min((float)(now - lastTime), 0.1f);
            lastTime = now;
            if (preview == null || paused)
            {
                return;
            }

            AimCamera();
            preview.Step(deltaTime, previewCamera.transform.position);
            Repaint();
        }

        private void AimCamera()
        {
            var focus = preview.Focus;
            var rotation = Quaternion.Euler(pitch, yaw, 0f);
            previewCamera.transform.position = focus + rotation * (Vector3.back * distance);
            previewCamera.transform.LookAt(focus);
        }

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (var side = new EditorGUILayout.ScrollViewScope(scroll, GUILayout.Width(SidebarWidth)))
                {
                    scroll = side.scrollPosition;
                    DrawSidebar();
                }

                var area = GUILayoutUtility.GetRect(10f, 10f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
                DrawView(area);
            }
        }

        private void DrawSidebar()
        {
            if (bodies.Length > 0)
            {
                var names = bodies.Select(body => body.Name).ToArray();
                var picked = EditorGUILayout.Popup("Body", bodyIndex, names);
                if (picked != bodyIndex)
                {
                    bodyIndex = picked;
                    Rebuild();
                }
            }

            if (GUILayout.Button("Reload catalogs"))
            {
                bodies = WardrobePreview.Bodies();
                Rebuild();
            }

            if (problem != null)
            {
                EditorGUILayout.HelpBox(problem, MessageType.Warning);
            }

            if (preview == null)
            {
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Garments", EditorStyles.boldLabel);
            foreach (var garment in preview.Catalog.Garments)
            {
                var worn = chosen.Contains(garment.Name);
                var wear = EditorGUILayout.ToggleLeft(garment.Name + "  (" + garment.Slot + ", " + garment.Layer + ")", worn);
                if (wear != worn)
                {
                    if (wear)
                    {
                        // A body wears one garment per visual slot.
                        foreach (var other in preview.Catalog.OthersInSlot(garment))
                        {
                            chosen.Remove(other.Name);
                        }

                        chosen.Add(garment.Name);
                    }
                    else
                    {
                        chosen.Remove(garment.Name);
                    }

                    preview.Wear(chosen, chosenPaints);
                }
            }

            // One choice per paint category and slot, by its rule: underwear is always worn.
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Paint", EditorStyles.boldLabel);
            foreach (var group in preview.Catalog.Paints.Where(paint => paint != null).GroupBy(paint => (paint.Category, paint.Slot)))
            {
                var required = AppearanceRules.For(group.Key.Category).Required;
                var choices = (required ? new string[0] : new[] { "none" }).Concat(group.Select(paint => paint.Name)).ToArray();
                var current = System.Array.FindIndex(choices, name => chosenPaints.Contains(name));
                if (current < 0)
                {
                    current = 0;
                }

                var label = group.Key.Category + (group.Key.Slot.Length > 0 ? " (" + group.Key.Slot + ")" : string.Empty);
                var picked = EditorGUILayout.Popup(label, current, choices);
                if (picked != current)
                {
                    foreach (var paint in group)
                    {
                        chosenPaints.Remove(paint.Name);
                    }

                    if (required || picked > 0)
                    {
                        chosenPaints.Add(choices[picked]);
                    }

                    preview.Wear(chosen, chosenPaints);
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Motion", EditorStyles.boldLabel);
            foreach (WardrobePreview.Motion motion in System.Enum.GetValues(typeof(WardrobePreview.Motion)))
            {
                if (GUILayout.Toggle(preview.Playing == motion, motion.ToString(), EditorStyles.miniButton) && preview.Playing != motion)
                {
                    preview.Play(motion);
                }
            }

            paused = EditorGUILayout.ToggleLeft("Paused", paused);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Speed", preview.Speed.ToString("0.0") + " m/s");
            EditorGUILayout.LabelField("Drawn", string.Join(", ", preview.Worn.Select(garment => garment.Name)));
            EditorGUILayout.LabelField("Painted", string.Join(", ", preview.Painted.Select(paint => paint.Name)));
            EditorGUILayout.LabelField("Cloth", preview.Solver.IsActive ? preview.Solver.CurrentDetail.ToString() : "none");
        }

        private void DrawView(Rect area)
        {
            if (preview == null || area.width < 2f || area.height < 2f)
            {
                return;
            }

            var current = Event.current;
            if (current.type == EventType.MouseDrag && area.Contains(current.mousePosition))
            {
                yaw += current.delta.x * 0.5f;
                pitch = Mathf.Clamp(pitch + current.delta.y * 0.3f, -20f, 80f);
                current.Use();
            }
            else if (current.type == EventType.ScrollWheel && area.Contains(current.mousePosition))
            {
                distance = Mathf.Clamp(distance * (1f + current.delta.y * 0.05f), 0.6f, 12f);
                current.Use();
            }

            if (current.type != EventType.Repaint)
            {
                return;
            }

            var width = Mathf.Max(1, (int)area.width);
            var height = Mathf.Max(1, (int)area.height);
            if (texture == null || texture.width != width || texture.height != height)
            {
                if (texture != null)
                {
                    texture.Release();
                    DestroyImmediate(texture);
                }

                texture = new RenderTexture(width, height, 24) { hideFlags = HideFlags.HideAndDontSave, antiAliasing = 4 };
            }

            previewCamera.aspect = area.width / area.height;
            AimCamera();
            preview.Render(previewCamera, texture);
            GUI.DrawTexture(area, texture, ScaleMode.StretchToFill, false);
        }
    }
}
