using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Stops drawing world canvases the local player is too far away from, and draws them again when they come
    /// back. One manager handles every canvas and checks a few of them per frame. It switches the Canvas
    /// component (and the canvas's UI collider), never the GameObject, so scripts on the canvas keep running.
    /// Nothing is synced: each player culls for themselves.
    /// Set up from Window > Synthos > UI Audit ("Add Distance Culling").
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class SynCanvasDistanceCuller : UdonSharpBehaviour
    {
        [Tooltip("Canvases this manager hides when the player is far away.")]
        public Canvas[] canvases;

        [Tooltip("Each canvas's rectangle in its own local space (xMin, yMin, xMax, yMax), filled in by the setup tool.")]
        public Vector4[] localRects;

        [Tooltip("Distance in metres from the nearest edge of each canvas at which it is hidden.")]
        public float[] distances;

        [Tooltip("The UI collider of each canvas (VRC UI Shape), hidden together with the canvas so the laser pointer ignores it. May be empty.")]
        public Collider[] colliders;

        [Tooltip("A canvas shows again at its distance and hides at its distance plus this margin, so it doesn't flicker at the boundary.")]
        public float hysteresis = 2f;

        [Tooltip("How many canvases are checked each frame.")]
        public int canvasesPerFrame = 4;

        private VRCPlayerApi localPlayer;
        private bool[] managed;
        private bool[] colliderManaged;
        private bool[] shown;
        private int count;
        private int next;

        private void Start()
        {
            localPlayer = Networking.LocalPlayer;
            count = canvases == null ? 0 : canvases.Length;
            if (localRects == null || localRects.Length < count) count = localRects == null ? 0 : localRects.Length;
            if (distances == null || distances.Length < count) count = distances == null ? 0 : distances.Length;

            managed = new bool[count];
            colliderManaged = new bool[count];
            shown = new bool[count];
            for (int i = 0; i < count; i++)
            {
                // Canvases that start hidden are left to whatever shows them
                Canvas canvas = canvases[i];
                managed[i] = canvas != null && canvas.enabled;
                shown[i] = managed[i];

                Collider uiCollider = colliders != null && i < colliders.Length ? colliders[i] : null;
                colliderManaged[i] = managed[i] && uiCollider != null && uiCollider.enabled;
            }
        }

        private void Update()
        {
            if (count == 0 || !Utilities.IsValid(localPlayer)) return;

            Vector3 head = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
            int checks = canvasesPerFrame < count ? canvasesPerFrame : count;
            if (checks < 1) checks = 1;

            for (int k = 0; k < checks; k++)
            {
                int i = next;
                next = (next + 1) % count;
                if (!managed[i]) continue;

                Canvas canvas = canvases[i];
                if (canvas == null)
                {
                    managed[i] = false;
                    continue;
                }

                // Distance to the nearest point of the canvas rectangle, so standing next to one end of a long
                // board counts as close. Measured live, so moving canvases work too.
                Transform t = canvas.transform;
                Vector3 local = t.InverseTransformPoint(head);
                Vector4 rect = localRects[i];
                Vector3 nearest = t.TransformPoint(new Vector3(
                    Mathf.Clamp(local.x, rect.x, rect.z),
                    Mathf.Clamp(local.y, rect.y, rect.w),
                    0f));
                float distance = Vector3.Distance(head, nearest);

                bool show = shown[i] ? distance <= distances[i] + hysteresis : distance <= distances[i];
                if (show == shown[i]) continue;

                shown[i] = show;
                canvas.enabled = show;
                if (colliderManaged[i])
                {
                    Collider uiCollider = colliders[i];
                    if (uiCollider != null) uiCollider.enabled = show;
                }
            }
        }
    }
}
