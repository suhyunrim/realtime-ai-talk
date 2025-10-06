using UnityEngine;

[CreateAssetMenu(fileName = "Config", menuName = "Config/Settings", order = 1)]
public class Config : ScriptableObject
{
    [Header("API Settings")]
    [Tooltip("API Key (sk-...)")]
    [SerializeField]
    private string apiKey = "";

    [Header("Server Settings")]
    [Tooltip("Server URL")]
    [SerializeField]
    private string serverUrl = "wss://api.openai.com/v1/realtime?model=gpt-4o-realtime-preview-2024-10-01";

    [Header("Audio Settings")]
    [Tooltip("Sample Rate (Hz)")]
    [SerializeField]
    private int sampleRate = 24000;

    [Header("Other Settings")]
    // 추가 설정들을 여기에 추가할 수 있습니다

    public string ApiKey => apiKey;
    public string ServerUrl => serverUrl;
    public int SampleRate => sampleRate;

    public bool IsValid()
    {
        return !string.IsNullOrEmpty(apiKey) && apiKey.StartsWith("sk-");
    }
}
