using System.Linq;
using UnityEngine;
using Live2D.Cubism.Core;

public class MouthOpenDebugPulse : MonoBehaviour
{
    private CubismParameter _p;

    private void Start()
    {
        var m = GetComponentInChildren<CubismModel>();
        _p = m?.Parameters.FirstOrDefault(x => x && (x.Id.Contains("MouthOpenY") || x.name.Contains("MouthOpenY")));
        Debug.Log($"name: {_p.gameObject.name}");
    }

    private void LateUpdate()
    {
        if (_p == null) return;
        float t01 = (Mathf.Sin(Time.time * 3f) + 1f) * 0.5f; // 0~1
        _p.Value = Mathf.Lerp(_p.MinimumValue, _p.MaximumValue, t01);
    }
}