using System.Linq;
using UnityEngine;
using Live2D.Cubism.Core;
using Live2D.Cubism.Framework;
using Live2D.Cubism.Framework.MouthMovement;

public class CubismMouthDoctor : MonoBehaviour
{
    private void Start()
    {
        var upd = GetComponent<CubismUpdateController>();
        var mouth = GetComponent<CubismMouthController>();
        var model = GetComponentInChildren<CubismModel>(true);

        Debug.Log($"[MouthDoctor] UpdateController={(upd && upd.enabled)} MouthCtrl={(mouth && mouth.enabled)}");

        // 1) 파라미터 존재/바인딩 확인
        var p = model?.Parameters.FirstOrDefault(x => x && (x.Id.Contains("MouthOpenY") || x.name.Contains("MouthOpenY")));
        var hasTag = p && p.GetComponent<CubismMouthParameter>() != null;
        Debug.Log($"[MouthDoctor] ParamMouthOpenY={(p ? p.name : "NULL")}  HasMouthParameter={hasTag}");

        // 2) 컨트롤러가 업데이트 체인 인터페이스로 인식되는지
        bool mouthUpdatable = mouth is ICubismUpdatable;
        Debug.Log($"[MouthDoctor] MouthCtrl Is ICubismUpdatable? {mouthUpdatable}");

        // 3) 어셈블리(버전) 단서
        Debug.Log($"[MouthDoctor] Assemblies: Mouth={mouth?.GetType().Assembly.FullName}, UpdateCtrl={upd?.GetType().Assembly.FullName}");
        Debug.Log($"[MouthDoctor] Interface={typeof(ICubismUpdatable).Assembly.FullName}");
    }
}