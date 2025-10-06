// Assets/VoiceControlExample.cs
using UnityEngine;
using UnityEngine.UI;

public class VoiceControlExample : MonoBehaviour
{
    [Header("UI References")]
    public Text statusText;
    public Text recognizedText;
    public Button recordButton;
    public TTSStreamClient ttsClient; // 기존 TTS 클라이언트

    [Header("Voice Settings")]
    public bool autoTTS = true; // 인식된 텍스트를 자동으로 TTS로 재생

    private VoiceToTextClient _voiceClient;

    private void Start()
    {
        _voiceClient = GetComponent<VoiceToTextClient>();

        if (_voiceClient == null)
        {
            _voiceClient = gameObject.AddComponent<VoiceToTextClient>();
        }

        // 이벤트 구독
        _voiceClient.OnRecordingStarted += OnRecordingStarted;
        _voiceClient.OnRecordingStopped += OnRecordingStopped;
        _voiceClient.OnTextRecognized += OnTextRecognized;
        _voiceClient.OnError += OnError;

        // UI 초기화
        if (statusText) statusText.text = "Ready - Press Space to record";
        if (recognizedText) recognizedText.text = "";

        if (recordButton)
        {
            recordButton.onClick.AddListener(ToggleRecording);
            UpdateRecordButton();
        }
    }

    private void OnRecordingStarted()
    {
        if (statusText) statusText.text = "🎤 Recording... (Release Space to stop)";
        if (statusText) statusText.color = Color.red;
        UpdateRecordButton();
    }

    private void OnRecordingStopped()
    {
        if (statusText) statusText.text = "Processing...";
        if (statusText) statusText.color = Color.yellow;
        UpdateRecordButton();
    }

    private void OnTextRecognized(string text)
    {
        if (statusText) statusText.text = "Recognition completed!";
        if (statusText) statusText.color = Color.green;

        if (recognizedText) recognizedText.text = text;

        Debug.Log($"[Voice] Recognized: {text}");

        // 자동 TTS 재생
        if (autoTTS && ttsClient && !string.IsNullOrEmpty(text))
        {
            ttsClient.Speak(text);
        }

        // 3초 후 상태 리셋
        Invoke(nameof(ResetStatus), 3f);
    }

    private void OnError(string error)
    {
        if (statusText) statusText.text = $"Error: {error}";
        if (statusText) statusText.color = Color.red;

        Debug.LogError($"[Voice] Error: {error}");

        // 5초 후 상태 리셋
        Invoke(nameof(ResetStatus), 5f);
    }

    private void ResetStatus()
    {
        if (statusText) statusText.text = "Ready - Press Space to record";
        if (statusText) statusText.color = Color.white;
    }

    private void ToggleRecording()
    {
        if (_voiceClient.IsRecording)
        {
            _voiceClient.StopRecording();
        }
        else
        {
            _voiceClient.StartRecording();
        }
    }

    private void UpdateRecordButton()
    {
        if (recordButton)
        {
            var buttonText = recordButton.GetComponentInChildren<Text>();
            if (buttonText)
            {
                buttonText.text = _voiceClient.IsRecording ? "Stop Recording" : "Start Recording";
            }
        }
    }

    private void OnDestroy()
    {
        if (_voiceClient)
        {
            _voiceClient.OnRecordingStarted -= OnRecordingStarted;
            _voiceClient.OnRecordingStopped -= OnRecordingStopped;
            _voiceClient.OnTextRecognized -= OnTextRecognized;
            _voiceClient.OnError -= OnError;
        }
    }

    // 공개 메서드들
    public void SetAutoTTS(bool enabled)
    {
        autoTTS = enabled;
    }

    public void PlayRecognizedText()
    {
        if (ttsClient && recognizedText && !string.IsNullOrEmpty(recognizedText.text))
        {
            ttsClient.Speak(recognizedText.text);
        }
    }
}