using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Mmorpg.Client.Worlds
{
    // The settings a camera renders with, which the render pipeline keeps partly beside the Camera.
    public static class CameraRendering
    {
        // Makes the camera render as the other does: the Camera's settings and the pipeline's, such
        // as the renderer, post-processing, and anti-aliasing, so it draws through the same passes
        // into targets of the same formats; without the other's camera stack. The camera also takes
        // the other's place and layer, as Camera.CopyFrom does.
        public static void CopyFrom(Camera camera, Camera other)
        {
            camera.CopyFrom(other);
            var from = other.GetUniversalAdditionalCameraData();
            var to = camera.GetUniversalAdditionalCameraData();
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(from), to);
            to.cameraStack?.Clear();
        }
    }
}
