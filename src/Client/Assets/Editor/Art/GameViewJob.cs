using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // What tools/art/Capture-GameViews.ps1 asks the capture to do: the shard to enter, the file it
    // writes its state to, and every view of the standard set that it chose, each with the body
    // model, the garments and paint layers its outfit names, its pose, its camera, where its picture
    // goes, and the Blender view's class picture to measure the silhouettes against. Written by the
    // script as JSON with these field names.
    [Serializable]
    public sealed class GameViewJob
    {
        public string status = string.Empty;
        public string shard = string.Empty;
        // The longest a pose waits for the cloth to sleep, in seconds of play.
        public float settleSeconds = 3f;
        public GameViewJobView[] views = new GameViewJobView[0];

        public static GameViewJob Read(string path)
        {
            var job = JsonUtility.FromJson<GameViewJob>(File.ReadAllText(path, Encoding.UTF8));
            var problem = job != null ? job.Problem() : "the file is no job";
            if (problem != null)
            {
                throw new InvalidDataException(path + ": " + problem + ".");
            }

            return job;
        }

        // Why the job cannot be run, or null when it can.
        public string Problem()
        {
            if (string.IsNullOrEmpty(status) || string.IsNullOrEmpty(shard))
            {
                return "it names no status file or no shard";
            }

            if (views == null || views.Length == 0)
            {
                return "it names no view";
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var view in views)
            {
                var problem = view != null ? view.Problem() : "a view is empty";
                if (problem != null)
                {
                    return problem;
                }

                if (!names.Add(view.name))
                {
                    return "the view " + view.name + " is named twice";
                }
            }

            return null;
        }
    }

    [Serializable]
    public sealed class GameViewJobView
    {
        // Such as Linen_Male_Walk03_Y215, with its outfit, the body model, such as Human_Male, and
        // the body's label, such as Male.
        public string name = string.Empty;
        public string outfit = string.Empty;
        public string model = string.Empty;
        public string label = string.Empty;
        // The garment textures and paint layers the outfit names, as a look names them.
        public string[] garments = new string[0];
        public string[] paints = new string[0];
        // The pose: the rest pose for an empty clip, or the frame of a clip of the animation library.
        public string pose = string.Empty;
        public string clip = string.Empty;
        public int frame;
        // The camera: its yaw and pitch in degrees and what it frames.
        public float yaw;
        public float pitch;
        public string framing = string.Empty;
        // Where the game's picture goes, and the Blender view's class picture, which may be missing.
        public string picture = string.Empty;
        public string blenderClasses = string.Empty;

        public bool IsRest
        {
            get { return string.IsNullOrEmpty(clip); }
        }

        public string Problem()
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(model) || string.IsNullOrEmpty(pose) || string.IsNullOrEmpty(picture))
            {
                return "a view lacks its name, model, pose, or picture";
            }

            if (!GameViewFraming.IsFrame(framing))
            {
                return "the view " + name + " frames '" + framing + "', not one of figure, head, waist, and feet";
            }

            if (Mathf.Abs(pitch) > 89f || frame < 0)
            {
                return "the view " + name + " has a pitch beyond 89 degrees or a negative frame";
            }

            return null;
        }

        // The look of the outfit: its garments and paint layers, in their natural colors.
        public Mmorpg.Client.Gameplay.BodyLook Look()
        {
            var look = Mmorpg.Client.Gameplay.BodyLook.Default;
            foreach (var garment in garments ?? new string[0])
            {
                look = look.WithGarment(garment, true);
            }

            foreach (var paint in paints ?? new string[0])
            {
                look = look.WithPaint(paint, true);
            }

            return look;
        }
    }

    // What the capture writes while it runs and when it ends: its state, running, done, or failed,
    // the phase it is in, the reason of a failure, how many views are done, and per phase, body, and
    // view what it measured.
    [Serializable]
    public sealed class GameViewStatus
    {
        public const string Running = "running";
        public const string Done = "done";
        public const string Failed = "failed";

        public string state = Running;
        public string phase = string.Empty;
        public string message = string.Empty;
        public int done;
        public int total;
        // What the capture signed in to, or registered and created on a first run.
        public string account = string.Empty;
        public string place = string.Empty;
        public float bodyYaw;
        public List<GameViewPhaseTime> phases = new List<GameViewPhaseTime>();
        public List<GameViewBodyResult> bodies = new List<GameViewBodyResult>();
        public List<GameViewPoseResult> poses = new List<GameViewPoseResult>();
        public List<GameViewResult> views = new List<GameViewResult>();
    }

    [Serializable]
    public sealed class GameViewPhaseTime
    {
        public string name = string.Empty;
        public float seconds;
    }

    // An outfit on a body: how long it took to load and put together, the garments it wears, those
    // it draws, and those a player's body in the look would draw that it does not, and how far its
    // pose at rest lies from the bind pose of its mesh: the largest difference of an entry of a
    // bone's bind matrix, with that bone, and of a drawn vertex, in millimeters.
    [Serializable]
    public sealed class GameViewBodyResult
    {
        public string outfit = string.Empty;
        public string label = string.Empty;
        public float buildSeconds;
        public float seconds;
        public string worn = string.Empty;
        public string drawn = string.Empty;
        public string missing = string.Empty;
        public float restFromBindPose;
        public string restFarthestBone = string.Empty;
        public float restVertexOffset;
        public float headLength;
        // The bones each pose's bonePositions name, in their order.
        public string[] bones = new string[0];
    }

    // A pose of an outfit on a body: the state and time the Animator shows, how many frames and
    // seconds its cloth took to settle, whether it slept on its target pose or came to rest, how far
    // it moved in the last frame and lies from the target pose, in millimeters, and the detail it
    // moved with.
    [Serializable]
    public sealed class GameViewPoseResult
    {
        public string outfit = string.Empty;
        public string label = string.Empty;
        public string pose = string.Empty;
        public string shown = string.Empty;
        public float normalizedTime;
        public int settleFrames;
        public float settleSeconds;
        public bool cloth;
        public bool slept;
        public bool still;
        public float clothMotion;
        public float clothFromTarget;
        public string detail = string.Empty;
        // How far the hips turned about the upright from the rest pose, in degrees.
        public float hipsTurn;
        // Where the head of every bone the body is skinned to lies, in the body's own frame and the
        // game's axes, x right, y up, z front, in meters: three numbers per bone, in the order of
        // the outfit's bones, to hold the Blender views' poses against.
        public float[] bonePositions = new float[0];
    }

    [Serializable]
    public sealed class GameViewResult
    {
        public string name = string.Empty;
        public string picture = string.Empty;
        // The silhouettes' intersection over union, or -1 without the Blender view.
        public float overlap = -1f;
        public int gamePixels;
        public int blenderPixels;
        public float distance;
        public float milliseconds;
        // Whether the world stands between the camera and the body.
        public bool blocked;
    }
}
