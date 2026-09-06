using UnityEngine;

namespace OutfitToggleGenerator
{
    [AddComponentMenu("")]
    public sealed class OutfitToggleGeneratedMenu : MonoBehaviour
    {
        [HideInInspector] public string[] outfitPaths;
        [HideInInspector] public string generatedKind;
        [HideInInspector] public string ownerId;
        // Zero identifies older menu-group organizers that need relocation.
        [HideInInspector] public int menuGroupsLayoutVersion;
    }
}
