using UnityEngine;
namespace OutfitToggleGenerator
{
    // Marks a scene camera as an Avatar Wardrobe thumbnail camera. The id
    // survives the upload staging clone, so a camera placed under the avatar
    // root frames the staged copy exactly like the source. Editor-only: the
    // component is stripped from builds; cameras Wardrobe creates are also
    // tagged EditorOnly so the Camera itself never ships.
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    [AddComponentMenu("Avatar Wardrobe/Thumbnail Camera")]
    public sealed class WardrobeThumbnailCamera : MonoBehaviour, VRC.SDKBase.IEditorOnly
    {
        public const float Aspect = 4f / 3f;

        [HideInInspector] public string id = "";
        [Tooltip("Hide everything except the avatar (backdrop, props, other avatars).")]
        public bool avatarOnly;

        private void Reset()
        {
            if (string.IsNullOrEmpty(id)) id = System.Guid.NewGuid().ToString("N");
            var cam = GetComponent<Camera>();
            if (cam != null) cam.enabled = false;
        }

#if UNITY_EDITOR
        // Thumbnails render at 4:3 whatever the Game view aspect is.
        private void OnDrawGizmos()
        {
            var cam = GetComponent<Camera>();
            if (cam == null) return;
            var previous = Gizmos.matrix;
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
            Gizmos.color = UnityEditor.Selection.Contains(gameObject) ? new Color(1f, 0.85f, 0.25f, 1f) : new Color(1f, 0.85f, 0.25f, 0.35f);
            if (cam.orthographic)
            {
                float h = cam.orthographicSize, w = h * Aspect;
                Gizmos.DrawWireCube(new Vector3(0, 0, (cam.nearClipPlane + 1f) * 0.5f), new Vector3(w * 2f, h * 2f, 1f - cam.nearClipPlane));
            }
            else Gizmos.DrawFrustum(Vector3.zero, cam.fieldOfView, Mathf.Min(cam.farClipPlane, 1.2f), cam.nearClipPlane, Aspect);
            Gizmos.matrix = previous;
        }
#endif
    }
}
