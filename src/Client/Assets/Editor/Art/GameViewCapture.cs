using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Mmorpg.Client.App;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Worlds;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Mmorpg.Client.Editor
{
    // The capture of the game views from the game itself: in Play Mode, logged in and standing in
    // Development World 2, it puts one body at a time on open ground beside the player, built and
    // dressed as a player's body is, holds it in each pose of the standard set, lets its cloth
    // settle, and draws it with a camera of its own that renders as the player's camera does, from
    // each camera of the view, framed by the rules of the Blender views, into a 512 by 768 picture
    // of the World. A second picture of the body alone against a clear background gives its
    // silhouette, which is held against the Blender view's to measure how far the two overlap.
    // tools/art/Capture-GameViews.ps1 enters Play Mode and starts it with a job file; nothing of it
    // runs unless it is started. See docs/ART_PIPELINE.md, Capturing The Views From The Game.
    public static class GameViewCapture
    {
        // The body stands this far from the player, east and north, on open ground of the town.
        public static readonly Vector3 PlaceOffset = new Vector3(8f, 0f, 6f);

        // The body faces so that the World's sun falls on it from where Blender's sun falls in the
        // views from this yaw: 30 degrees beside that camera.
        public const float LitYaw = 215f;
        public const float SunSide = 30f;

        // A Blender class picture's figure is every pixel that is not clear; the game's is every
        // pixel at least half covered.
        public const byte BlenderAlpha = 1;
        public const byte GameAlpha = 128;

        private const float ContentSeconds = 60f;
        private const float LookSeconds = 60f;
        private const float AnimatorSeconds = 10f;
        private const int SettleMinimumFrames = 3;
        // Cloth whose particles all stayed within this distance for this long, in seconds of play,
        // has come to rest.
        private const float StillMeters = 0.0005f;
        private const float StillSeconds = 0.5f;

        private static Run current;

        // Starts the capture of the job's views in the running Play Mode: "started", "running"
        // while one runs, or the reason it cannot start.
        public static string Start(string jobPath)
        {
            if (!EditorApplication.isPlaying)
            {
                return "error: the Editor is not in Play Mode";
            }

            if (current != null && !current.Ended)
            {
                return "running";
            }

            GameViewJob job;
            try
            {
                job = GameViewJob.Read(jobPath);
            }
            catch (Exception exception)
            {
                return "error: " + exception.Message;
            }

            var bootstrap = Object.FindAnyObjectByType<ClientBootstrap>();
            if (bootstrap == null)
            {
                return "error: the client bootstrap does not run";
            }

            current = new Run(job, bootstrap);
            bootstrap.StartCoroutine(current.Execute());
            return "started";
        }

        // Draws the World from the eye toward the target with the capture's camera, without the
        // players, into a PNG at the path, as for a picture of a check of one's own once the views
        // are captured in the same Play Mode: "written", or the reason it is not.
        public static string Picture(Vector3 eye, Vector3 target, string path)
        {
            if (current == null || !current.HasStage)
            {
                return "error: no capture runs in this Play Mode";
            }

            try
            {
                current.Picture(eye, target, path);
                return "written";
            }
            catch (Exception exception)
            {
                return "error: " + exception.Message;
            }
        }

        private sealed class Run
        {
            private readonly GameViewJob job;
            private readonly ClientBootstrap bootstrap;
            private readonly GameViewStatus status = new GameViewStatus();
            private readonly AccountEntryKeys keys = AccountEntryKeys.Take();
            private readonly Stopwatch phaseWatch = Stopwatch.StartNew();
            private readonly List<Renderer> hidden = new List<Renderer>();
            private WorldSceneContext context;
            private GameObject stage;
            private Camera lookCamera;
            private Camera maskCamera;
            private RenderTexture lookTexture;
            private RenderTexture maskTexture;
            private Texture2D lookPixels;
            private Texture2D maskPixels;
            private Texture2D blenderPixels;
            private int captureLayer = -1;
            private Vector3 place;
            private Quaternion facing = Quaternion.identity;

            public Run(GameViewJob job, ClientBootstrap bootstrap)
            {
                this.job = job;
                this.bootstrap = bootstrap;
                status.total = job.views.Length;
                EditorApplication.playModeStateChanged += OnPlayModeChanged;
            }

            public bool Ended { get; private set; }

            public bool HasStage
            {
                get { return stage != null && lookCamera != null; }
            }

            // Runs the capture, and writes why it failed when a step throws.
            public IEnumerator Execute()
            {
                var steps = new Stack<IEnumerator>();
                steps.Push(Capture());
                while (steps.Count > 0)
                {
                    object yielded;
                    bool moved;
                    try
                    {
                        moved = steps.Peek().MoveNext();
                        yielded = moved ? steps.Peek().Current : null;
                    }
                    catch (Exception exception)
                    {
                        Fail(exception);
                        // The steps that wait run their finally blocks, so the body goes.
                        while (steps.Count > 0)
                        {
                            try
                            {
                                (steps.Pop() as IDisposable)?.Dispose();
                            }
                            catch (Exception cleanup)
                            {
                                Debug.LogException(cleanup);
                            }
                        }

                        yield break;
                    }

                    if (!moved)
                    {
                        steps.Pop();
                    }
                    else if (yielded is IEnumerator nested)
                    {
                        steps.Push(nested);
                    }
                    else
                    {
                        yield return yielded;
                    }
                }
            }

            private IEnumerator Capture()
            {
                Phase("content");
                var deadline = Time.realtimeSinceStartup + ContentSeconds;
                while (!bootstrap.IsContentReady)
                {
                    Expect(deadline, "The client did not resolve its content within " + ContentSeconds + " s.");
                    yield return null;
                }

                yield return GameViewLogin.Enter(bootstrap, job.shard, Phase, value => status.account = value, value => context = value);
                Phase("stage");
                BuildStage();
                var models = ModelsOf(context);
                foreach (var group in Groups(job.views, view => view.outfit + " on " + view.label))
                {
                    yield return Outfit(group, models);
                }

                Phase("done");
                Finish(GameViewStatus.Done, string.Empty);
            }

            // One outfit on one body: its content loaded, the body put in and dressed, then each pose
            // held, its cloth settled, and its views drawn.
            private IEnumerator Outfit(List<GameViewJobView> views, PlayerBodyModels models)
            {
                var first = views[0];
                var watch = Stopwatch.StartNew();
                Phase(first.outfit + " on " + first.label);
                var model = ModelNamed(models, first.model);
                var look = first.Look();
                var meshes = context.BodyMeshes;
                var needs = PlayerBodyContent.NeedsOf(model.Body.Catalog, look);
                var deadline = Time.realtimeSinceStartup + LookSeconds;
                while (!meshes.Content.Want(needs))
                {
                    Expect(deadline, "The content of " + first.outfit + " on " + first.label + " did not load within " + LookSeconds + " s.");
                    yield return null;
                }

                var body = GameViewBody.Create(model, context.gameObject.scene, place, facing, look, meshes, context.BodyMaterials);
                try
                {
                    var result = new GameViewBodyResult
                    {
                        outfit = first.outfit,
                        label = first.label,
                        buildSeconds = (float)watch.Elapsed.TotalSeconds,
                        headLength = body.HeadLength,
                    };
                    // The body draws what a player's body in the look draws: the garments that no
                    // garment of a higher layer hides, whose content the look loads.
                    var drawn = Names(PlayerBodyMeshes.Drawn(body.Body.Worn));
                    var missing = new List<string>();
                    foreach (var garment in Names(PlayerBodyMeshes.Drawn(PlayerBodyAppearance.WornBy(model.Body.Catalog, look))))
                    {
                        if (!drawn.Contains(garment))
                        {
                            missing.Add(garment);
                        }
                    }

                    result.worn = string.Join("+", body.WornNames());
                    result.drawn = string.Join("+", drawn);
                    result.missing = string.Join("+", missing);
                    status.bodies.Add(result);
                    yield return null;
                    body.ShowRest();
                    result.restFromBindPose = body.FromBindPose(out result.restFarthestBone);
                    result.restVertexOffset = body.FromBindVertices() * 1000f;
                    result.bones = body.BoneNames();
                    foreach (var pose in Groups(views, view => view.pose))
                    {
                        yield return Pose(body, pose);
                    }

                    result.seconds = (float)watch.Elapsed.TotalSeconds;
                }
                finally
                {
                    body.Dispose();
                }

                WriteStatus();
            }

            // One pose of the body: held, its cloth settled, and drawn from every camera of its views.
            private IEnumerator Pose(GameViewBody body, List<GameViewJobView> views)
            {
                var first = views[0];
                var pose = new GameViewPoseResult { outfit = first.outfit, label = first.label, pose = first.pose };
                var watch = Stopwatch.StartNew();
                if (first.IsRest)
                {
                    body.ShowRest();
                    pose.shown = "rest";
                }
                else
                {
                    var deadline = Time.realtimeSinceStartup + AnimatorSeconds;
                    while (!body.PrepareClip())
                    {
                        Expect(deadline, "The Animator of " + body.Body.name + " did not start within " + AnimatorSeconds + " s.");
                        yield return null;
                    }

                    pose.shown = body.ShowClip(first.clip, first.frame, out pose.normalizedTime);
                }

                yield return null;
                var solver = body.Solver;
                pose.cloth = solver != null && solver.IsActive;
                if (pose.cloth)
                {
                    solver.RestartSprings();
                }

                // The cloth settles until its springs sleep on the target pose, or until it comes to
                // rest beside it, where gravity holds it as the body leans, or for the job's longest
                // time.
                var frames = 0;
                var drift = 0f;
                var fromTarget = 0f;
                var started = Time.time;
                var reference = body.ClothParticles(out fromTarget);
                var referenceTime = Time.time;
                var still = false;
                while (frames < SettleMinimumFrames
                    || (pose.cloth && !solver.Sleeps && !still && Time.time - started < job.settleSeconds))
                {
                    yield return null;
                    frames++;
                    var now = body.ClothParticles(out fromTarget);
                    drift = now.Count == reference.Count ? 0f : float.PositiveInfinity;
                    for (var index = 0; index < now.Count && index < reference.Count; index++)
                    {
                        drift = Mathf.Max(drift, Vector3.Distance(now[index], reference[index]));
                    }

                    if (drift > StillMeters)
                    {
                        reference = now;
                        referenceTime = Time.time;
                    }

                    still = Time.time - referenceTime >= StillSeconds;
                }

                pose.settleFrames = frames;
                pose.settleSeconds = (float)watch.Elapsed.TotalSeconds;
                pose.slept = !pose.cloth || solver.Sleeps;
                pose.still = !pose.cloth || solver.Sleeps || still;
                pose.clothMotion = float.IsInfinity(drift) ? -1f : drift * 1000f;
                pose.clothFromTarget = fromTarget * 1000f;
                pose.detail = solver != null ? solver.CurrentDetail.ToString() : string.Empty;
                pose.hipsTurn = body.HipsTurn();
                pose.bonePositions = body.BonePositions();
                status.poses.Add(pose);
                var figure = body.Figure();
                foreach (var view in views)
                {
                    Shoot(body, view, figure);
                }
            }

            // Draws one view: the camera placed by the view's framing, the World's picture, and the
            // body's silhouette against the Blender view's.
            private void Shoot(GameViewBody body, GameViewJobView view, List<Vector3> figure)
            {
                var watch = Stopwatch.StartNew();
                GameViewFraming.Placement placement;
                switch (view.framing)
                {
                    case GameViewFraming.Figure:
                        placement = GameViewFraming.Aim(figure, view.yaw, view.pitch);
                        break;
                    case GameViewFraming.Feet:
                        var feet = GameViewFraming.FeetPoints(figure, body.LeftFootHeight, body.RightFootHeight);
                        if (feet.Count == 0)
                        {
                            throw new InvalidOperationException("The view " + view.name + " finds nothing drawn at the feet.");
                        }

                        placement = GameViewFraming.Aim(feet, view.yaw, view.pitch);
                        break;
                    case GameViewFraming.Head:
                        placement = GameViewFraming.Close(body.HeadPoint(), view.yaw, view.pitch);
                        break;
                    default:
                        placement = GameViewFraming.Close(body.HipsPoint(), view.yaw, view.pitch);
                        break;
                }

                var root = body.Body.transform;
                var eye = root.TransformPoint(placement.Camera);
                var target = root.TransformPoint(placement.Centre);
                Aim(lookCamera, eye, target);
                var result = new GameViewResult { name = view.name, picture = view.picture, distance = placement.Distance };
                result.blocked = Physics.Linecast(eye, target, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                Draw(lookCamera, lookTexture, lookPixels);
                WritePng(view.picture, lookPixels.EncodeToPNG());

                Aim(maskCamera, eye, target);
                body.UseLayer(captureLayer);
                try
                {
                    Draw(maskCamera, maskTexture, maskPixels);
                }
                finally
                {
                    body.UseOwnLayers();
                }

                var game = GameViewFraming.Silhouette(maskPixels.GetPixels32(), GameAlpha);
                result.gamePixels = Count(game);
                var blender = BlenderSilhouette(view.blenderClasses);
                if (blender != null)
                {
                    var overlap = GameViewFraming.Compare(game, blender);
                    result.overlap = overlap.Ratio;
                    result.blenderPixels = overlap.Second;
                }

                result.milliseconds = (float)watch.Elapsed.TotalMilliseconds;
                status.views.Add(result);
                status.done = status.views.Count;
                WriteStatus();
            }

            public void Picture(Vector3 eye, Vector3 target, string path)
            {
                Aim(lookCamera, eye, target);
                Draw(lookCamera, lookTexture, lookPixels);
                WritePng(path, lookPixels.EncodeToPNG());
            }

            // The stage of the capture in the World scene: the place and facing of its bodies, its
            // camera, which renders as the player's camera does and is a view of the cloth
            // scheduler, and the camera of the silhouettes, which draws one layer on a clear ground.
            private void BuildStage()
            {
                var player = context.LocalPlayer;
                var playerCamera = player.PlayerCamera != null ? player.PlayerCamera.Camera : null;
                if (playerCamera == null)
                {
                    throw new InvalidOperationException("The local player has no camera.");
                }

                place = player.transform.position + PlaceOffset;
                if (Physics.Raycast(place + Vector3.up * 3f, Vector3.down, out var ground, 10f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    place.y = ground.point.y;
                }

                var yaw = FacingYaw();
                facing = Quaternion.Euler(0f, yaw, 0f);
                status.place = place.ToString("F2");
                status.bodyYaw = yaw;
                captureLayer = FreeLayer();

                stage = new GameObject("GameViewCapture");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(stage, context.gameObject.scene);
                var msaa = Mathf.Max(1, QualitySettings.antiAliasing);
                lookTexture = NewTexture("GameViewLook", msaa);
                maskTexture = NewTexture("GameViewSilhouette", 1);
                lookPixels = new Texture2D(GameViewFraming.Width, GameViewFraming.Height, TextureFormat.RGB24, false);
                maskPixels = new Texture2D(GameViewFraming.Width, GameViewFraming.Height, TextureFormat.RGBA32, false);
                blenderPixels = new Texture2D(2, 2, TextureFormat.RGBA32, false);

                var lookObject = new GameObject("Camera");
                lookObject.transform.SetParent(stage.transform, false);
                lookCamera = lookObject.AddComponent<Camera>();
                CameraRendering.CopyFrom(lookCamera, playerCamera);
                lookCamera.fieldOfView = GameViewFraming.FieldOfView;
                lookCamera.targetTexture = lookTexture;
                lookCamera.enabled = true;
                ClothChainScheduler.AddView(lookCamera);

                var maskObject = new GameObject("SilhouetteCamera");
                maskObject.transform.SetParent(stage.transform, false);
                maskCamera = maskObject.AddComponent<Camera>();
                maskCamera.enabled = false;
                maskCamera.fieldOfView = GameViewFraming.FieldOfView;
                maskCamera.nearClipPlane = lookCamera.nearClipPlane;
                maskCamera.farClipPlane = lookCamera.farClipPlane;
                maskCamera.cullingMask = 1 << captureLayer;
                maskCamera.clearFlags = CameraClearFlags.SolidColor;
                maskCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
                maskCamera.allowHDR = false;
                maskCamera.allowMSAA = false;
                maskCamera.targetTexture = maskTexture;
                Aim(lookCamera, place + facing * new Vector3(0f, 1f, 3f), place + Vector3.up);
            }

            // The yaw of the bodies: the World's sun falls on them from where Blender's sun falls in
            // the views from LitYaw.
            private static float FacingYaw()
            {
                var sun = RenderSettings.sun;
                if (sun == null)
                {
                    foreach (var light in Object.FindObjectsByType<Light>())
                    {
                        if (light.type == LightType.Directional && light.isActiveAndEnabled && (sun == null || light.intensity > sun.intensity))
                        {
                            sun = light;
                        }
                    }
                }

                if (sun == null)
                {
                    return 0f;
                }

                var toward = -sun.transform.forward;
                var sunYaw = Mathf.Atan2(toward.x, toward.z) * Mathf.Rad2Deg;
                return Mathf.Repeat(sunYaw + (LitYaw - SunSide) - 180f, 360f);
            }

            // A layer no object of the project uses, for the silhouettes.
            private static int FreeLayer()
            {
                for (var layer = 31; layer > 5; layer--)
                {
                    if (string.IsNullOrEmpty(LayerMask.LayerToName(layer)))
                    {
                        return layer;
                    }
                }

                throw new InvalidOperationException("Every layer has a name; the capture needs a free one for the silhouettes.");
            }

            private static RenderTexture NewTexture(string name, int samples)
            {
                var texture = new RenderTexture(GameViewFraming.Width, GameViewFraming.Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    name = name,
                    antiAliasing = samples,
                    useMipMap = false,
                };
                texture.Create();
                return texture;
            }

            private static void Aim(Camera camera, Vector3 eye, Vector3 target)
            {
                camera.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(target - eye, Vector3.up));
            }

            // Renders the camera into the texture now, without the players and with every shader
            // compiled, so nothing shows a stand-in, and reads its pixels.
            private void Draw(Camera camera, RenderTexture target, Texture2D into)
            {
                HidePlayers();
                var async = ShaderUtil.allowAsyncCompilation;
                ShaderUtil.allowAsyncCompilation = false;
                var active = RenderTexture.active;
                try
                {
                    camera.targetTexture = target;
                    camera.Render();
                    RenderTexture.active = target;
                    into.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
                }
                finally
                {
                    RenderTexture.active = active;
                    ShaderUtil.allowAsyncCompilation = async;
                    ShowPlayers();
                }
            }

            // The players stay out of the pictures: the local player and everyone in view.
            private void HidePlayers()
            {
                hidden.Clear();
                if (context == null)
                {
                    return;
                }

                AddVisible(context.LocalPlayer != null ? context.LocalPlayer.gameObject : null);
                foreach (var remote in context.RemotePlayers)
                {
                    AddVisible(remote != null ? remote.gameObject : null);
                }

                foreach (var renderer in hidden)
                {
                    renderer.forceRenderingOff = true;
                }
            }

            private void AddVisible(GameObject player)
            {
                if (player == null)
                {
                    return;
                }

                foreach (var renderer in player.GetComponentsInChildren<Renderer>(true))
                {
                    if (!renderer.forceRenderingOff)
                    {
                        hidden.Add(renderer);
                    }
                }
            }

            private void ShowPlayers()
            {
                foreach (var renderer in hidden)
                {
                    if (renderer != null)
                    {
                        renderer.forceRenderingOff = false;
                    }
                }

                hidden.Clear();
            }

            // The silhouette of the Blender view's class picture, or null without one of the
            // picture's size.
            private bool[] BlenderSilhouette(string path)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path) || !blenderPixels.LoadImage(File.ReadAllBytes(path)))
                {
                    return null;
                }

                if (blenderPixels.width != GameViewFraming.Width || blenderPixels.height != GameViewFraming.Height)
                {
                    return null;
                }

                return GameViewFraming.Silhouette(blenderPixels.GetPixels32(), BlenderAlpha);
            }

            private static int Count(bool[] mask)
            {
                var count = 0;
                foreach (var set in mask)
                {
                    count += set ? 1 : 0;
                }

                return count;
            }

            // Writes a file under a temporary name and moves it into place, so a reader finds the
            // whole old or the whole new file.
            private static void WritePng(string path, byte[] bytes)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temporary = path + ".tmp";
                File.WriteAllBytes(temporary, bytes);
                Replace(temporary, path);
            }

            private static void Replace(string temporary, string path)
            {
                if (File.Exists(path))
                {
                    File.Replace(temporary, path, null);
                }
                else
                {
                    File.Move(temporary, path);
                }
            }

            private void WriteStatus()
            {
                try
                {
                    var temporary = job.status + ".tmp";
                    File.WriteAllText(temporary, JsonUtility.ToJson(status, true), new UTF8Encoding(false));
                    Replace(temporary, job.status);
                }
                catch (IOException)
                {
                    // The script reads it now; the next write comes with the next view.
                }
            }

            private void Phase(string name)
            {
                if (!string.IsNullOrEmpty(status.phase))
                {
                    status.phases.Add(new GameViewPhaseTime { name = status.phase, seconds = (float)phaseWatch.Elapsed.TotalSeconds });
                }

                status.phase = name;
                phaseWatch.Restart();
                WriteStatus();
            }

            private void Fail(Exception exception)
            {
                Debug.LogException(exception);
                Finish(GameViewStatus.Failed, exception.Message);
            }

            private void Finish(string state, string message)
            {
                if (Ended)
                {
                    return;
                }

                Ended = true;
                status.state = state;
                status.message = message;
                if (keys.Restore())
                {
                    status.message += (status.message.Length > 0 ? " " : string.Empty) + "The account entry keys changed and were put back.";
                }

                WriteStatus();
            }

            // Leaving Play Mode ends a capture that still runs; the keys go back either way.
            private void OnPlayModeChanged(PlayModeStateChange change)
            {
                if (change != PlayModeStateChange.ExitingPlayMode)
                {
                    return;
                }

                EditorApplication.playModeStateChanged -= OnPlayModeChanged;
                if (!Ended)
                {
                    Finish(GameViewStatus.Failed, "Play Mode ended during the phase " + status.phase + ".");
                }

                if (lookCamera != null)
                {
                    ClothChainScheduler.RemoveView(lookCamera);
                    lookCamera.targetTexture = null;
                }

                if (maskCamera != null)
                {
                    maskCamera.targetTexture = null;
                }

                foreach (var texture in new Object[] { lookTexture, maskTexture, lookPixels, maskPixels, blenderPixels })
                {
                    if (texture != null)
                    {
                        Object.Destroy(texture);
                    }
                }
            }

            private static void Expect(float deadline, string message)
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    throw new TimeoutException(message);
                }
            }

            private static PlayerBodyModels ModelsOf(WorldSceneContext world)
            {
                var slot = world.LocalPlayer.GetComponentInChildren<PlayerBodySlot>(true);
                if (slot == null || slot.Models == null)
                {
                    throw new InvalidOperationException("The local player has no body slot with models.");
                }

                return slot.Models;
            }

            private static PlayerBodyModels.Model ModelNamed(PlayerBodyModels models, string name)
            {
                foreach (var model in models.Models)
                {
                    if (model != null && model.Body != null && string.Equals(model.Name, name, StringComparison.Ordinal))
                    {
                        return model;
                    }
                }

                throw new InvalidOperationException("The player prefabs show no body model " + name + ".");
            }

            private static List<string> Names(IEnumerable<BodyAppearanceCatalog.Garment> garments)
            {
                var names = new List<string>();
                foreach (var garment in garments)
                {
                    names.Add(garment.Name);
                }

                return names;
            }

            // The views in groups of the same key, in the order the first of each comes.
            private static List<List<GameViewJobView>> Groups(IEnumerable<GameViewJobView> views, Func<GameViewJobView, string> key)
            {
                var groups = new List<List<GameViewJobView>>();
                var byKey = new Dictionary<string, List<GameViewJobView>>(StringComparer.Ordinal);
                foreach (var view in views)
                {
                    if (!byKey.TryGetValue(key(view), out var group))
                    {
                        group = new List<GameViewJobView>();
                        byKey[key(view)] = group;
                        groups.Add(group);
                    }

                    group.Add(view);
                }

                return groups;
            }
        }
    }
}
