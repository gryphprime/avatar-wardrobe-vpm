using UnityEngine;

namespace GryphPrime.AtelierBridge
{
    /// <summary>Serialized marker for an instance that Atelier owns.</summary>
    [DisallowMultipleComponent]
    public sealed class AtelierOwnedItem : MonoBehaviour
    {
        public string itemId;
        public string sourcePrefabGuid;
        public string ownerObjectId;
    }
}
