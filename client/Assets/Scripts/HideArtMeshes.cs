using Live2D.Cubism.Rendering;
using UnityEngine;
using static UnityEngine.GraphicsBuffer;

public class HideArtMeshes : MonoBehaviour
{
    [SerializeField]
    private CubismRenderer[] targets;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    private void Start()
    {
        foreach (var target in targets)
        {
            target.enabled = false;
        }
    }

    private void LateUpdate()
    {
        foreach (var cr in targets)
        {
            if (!cr)
                continue;

            var r = cr.GetComponent<Renderer>();
            if (!r) continue;

            r.enabled = false;              // 렌더러 끄기
            r.forceRenderingOff = true;     // 확실히 끄기(권장)

            var mpb = new MaterialPropertyBlock();
            r.GetPropertyBlock(mpb);
            if (r.sharedMaterial)
            {
                if (r.sharedMaterial.HasProperty("_MultiplyColor"))
                {
                    var v = mpb.GetVector("_MultiplyColor"); v.w = 0f;
                    mpb.SetVector("_MultiplyColor", v);
                }
                if (r.sharedMaterial.HasProperty("_Color"))
                {
                    var c = mpb.GetColor("_Color"); c.a = 0f;
                    mpb.SetColor("_Color", c);
                }
            }
            r.SetPropertyBlock(mpb);
        }
    }
}