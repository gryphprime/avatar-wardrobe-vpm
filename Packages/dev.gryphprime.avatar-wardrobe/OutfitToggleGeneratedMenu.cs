using UnityEngine;

namespace OutfitToggleGenerator
{
    [AddComponentMenu("")]
    // Bookkeeping for generated controls; never a runtime avatar component.
    public sealed class OutfitToggleGeneratedMenu : MonoBehaviour, VRC.SDKBase.IEditorOnly
    {
        [HideInInspector] public string[] outfitPaths;
        [HideInInspector] public string generatedKind;
        [HideInInspector] public string ownerId;
        // Zero identifies older menu-group organizers that need relocation.
        [HideInInspector] public int menuGroupsLayoutVersion;
    }
}
