using UnityEngine;
using TMPro;
using System.Collections;

public class SpeechBubble : MonoBehaviour
{
    [Header("UI Components")]
    [SerializeField]
    private TextMeshProUGUI label;

    [SerializeField]
    private CanvasGroup canvasGroup;

    [Header("Animation Settings")]
    [SerializeField]
    private float animationDuration = 0.3f;

    [SerializeField]
    private AnimationCurve showCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

    [SerializeField]
    private float overshoot = 1.1f; // 띠용 효과를 위한 오버슛

    private Coroutine _currentAnimation;
    private Vector3 _originalScale;

    private void Awake()
    {
        _originalScale = transform.localScale;

        // CanvasGroup이 없으면 자동으로 추가
        if (canvasGroup == null)
        {
            canvasGroup = GetComponent<CanvasGroup>();
            if (canvasGroup == null)
            {
                canvasGroup = gameObject.AddComponent<CanvasGroup>();
            }
        }

        // TextMeshProUGUI가 할당되지 않았으면 자식에서 찾기
        if (label == null)
        {
            label = GetComponentInChildren<TextMeshProUGUI>();
        }

        // 초기 상태는 숨김
        transform.localScale = Vector3.zero;
        canvasGroup.alpha = 0f;
    }

    public void Show()
    {
        if (_currentAnimation != null)
        {
            StopCoroutine(_currentAnimation);
        }

        gameObject.SetActive(true);
        _currentAnimation = StartCoroutine(ShowAnimation());
    }

    public void Hide()
    {
        if (_currentAnimation != null)
        {
            StopCoroutine(_currentAnimation);
        }

        _currentAnimation = StartCoroutine(HideAnimation());
    }

    public void SetText(string text)
    {
        if (label != null)
        {
            label.text = text;
        }
        else
        {
            Debug.LogWarning("[SpeechBubble] Label is not assigned!");
        }
    }

    public void ShowWithText(string text)
    {
        SetText(text);
        Show();
    }

    private IEnumerator ShowAnimation()
    {
        float elapsed = 0f;

        while (elapsed < animationDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / animationDuration);

            // 커브를 적용한 값
            float curveValue = showCurve.Evaluate(t);

            // 오버슛 효과: 중간에 overshoot 크기까지 갔다가 원래 크기로
            float scale;
            if (t < 0.7f)
            {
                // 0 -> overshoot (0~70% 구간)
                scale = Mathf.Lerp(0f, overshoot, curveValue / 0.7f);
            }
            else
            {
                // overshoot -> 1.0 (70~100% 구간)
                float localT = (t - 0.7f) / 0.3f;
                scale = Mathf.Lerp(overshoot, 1f, localT);
            }

            transform.localScale = _originalScale * scale;
            canvasGroup.alpha = curveValue;

            yield return null;
        }

        // 최종 상태 보장
        transform.localScale = _originalScale;
        canvasGroup.alpha = 1f;

        _currentAnimation = null;
    }

    private IEnumerator HideAnimation()
    {
        float elapsed = 0f;

        while (elapsed < animationDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / animationDuration);

            // Show의 역순
            float curveValue = showCurve.Evaluate(1f - t);

            // 역순 오버슛: 1.0 -> overshoot -> 0
            float scale;
            if (t < 0.3f)
            {
                // 1.0 -> overshoot (0~30% 구간)
                float localT = t / 0.3f;
                scale = Mathf.Lerp(1f, overshoot, localT);
            }
            else
            {
                // overshoot -> 0 (30~100% 구간)
                float localT = (t - 0.3f) / 0.7f;
                scale = Mathf.Lerp(overshoot, 0f, localT);
            }

            transform.localScale = _originalScale * scale;
            canvasGroup.alpha = curveValue;

            yield return null;
        }

        // 최종 상태 보장
        transform.localScale = Vector3.zero;
        canvasGroup.alpha = 0f;
        gameObject.SetActive(false);

        _currentAnimation = null;
    }

    // Public getters
    public bool IsVisible => canvasGroup != null && canvasGroup.alpha > 0f;
    public TextMeshProUGUI Label => label;
}
