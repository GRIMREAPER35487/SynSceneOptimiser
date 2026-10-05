using System.Collections.Generic;
using UnityEngine;

namespace Synthos.SynSceneOptimizer
{
    [ExecuteInEditMode]
    [DisallowMultipleComponent]
    public class SynDynamicPropertyBlock : MonoBehaviour
    {
        [System.Serializable]
        public struct FloatProp
        {
            public string name;
            public float value;
        }

        [System.Serializable]
        public struct VectorProp
        {
            public string name;
            public Vector4 value;
        }

        [SerializeField]
        public List<FloatProp> floats = new List<FloatProp>();

        [SerializeField]
        public List<VectorProp> vectors = new List<VectorProp>();

        private void Awake()
        {
            Apply();
        }

        private void OnEnable()
        {
            Apply();
        }

        private void OnValidate()
        {
            Apply();
        }

        public void Apply()
        {
            Renderer rendererComponent = GetComponent<Renderer>();
            if (rendererComponent == null) return;

            MaterialPropertyBlock block = new MaterialPropertyBlock();
            rendererComponent.GetPropertyBlock(block);

            if (floats != null)
            {
                foreach (var f in floats)
                {
                    if (!string.IsNullOrEmpty(f.name))
                    {
                        block.SetFloat(f.name, f.value);
                    }
                }
            }

            if (vectors != null)
            {
                foreach (var v in vectors)
                {
                    if (!string.IsNullOrEmpty(v.name))
                    {
                        block.SetVector(v.name, v.value);
                    }
                }
            }

            rendererComponent.SetPropertyBlock(block);
        }
    }
}
