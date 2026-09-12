using UnityEngine;

namespace GryphPrime.AtelierBridge
{
    /// <summary>Original material assignment retained while an Atelier appearance is active.</summary>
    [System.Serializable]
    public sealed class AtelierOwnedMaterial
    {
        public string rendererPath;
        public int slot;
        public string originalGuid;
        public long originalLocalId;
        public string originalPath;
        public string generatedPath;
        public string[] originalColorProperties = new string[0];
        public float[] originalColorValues = new float[0];
    }

    /// <summary>Original static blendshape value retained while an Atelier appearance is active.</summary>
    [System.Serializable]
    public sealed class AtelierOwnedBlendShape
    {
        public string rendererPath;
        public int index;
        public string name;
        public float originalValue;
    }

    /// <summary>Serialized marker for an instance that Atelier owns.</summary>
    [DisallowMultipleComponent]
    public sealed class AtelierOwnedItem : MonoBehaviour
    {
        public string itemId;
        public string assetId;
        public string sourcePrefabGuid;
        public string ownerObjectId;
        public System.Collections.Generic.List<AtelierOwnedMaterial> originalMaterials = new System.Collections.Generic.List<AtelierOwnedMaterial>();
        public System.Collections.Generic.List<AtelierOwnedBlendShape> originalBlendShapes = new System.Collections.Generic.List<AtelierOwnedBlendShape>();
    }
}
