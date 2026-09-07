using UnityEngine;
namespace OutfitToggleGenerator
{
    // Editor-only status is serialized with the instance, and stripped from the avatar build.
    [AddComponentMenu("")]
    public sealed class WardrobeSetupStatus : MonoBehaviour, VRC.SDKBase.IEditorOnly
    {
        [TextArea] public string warning;
        public string sourceGuid;
    }
}
