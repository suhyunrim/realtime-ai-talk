// Assets/VoiceChatController.cs
using UnityEngine;
using UnityEngine.UI;
using System.Threading.Tasks;

public enum ChatMode
{
    Traditional, // STT → Claude → TTS
    Realtime     // OpenAI Realtime API
}

public class VoiceChatController : MonoBehaviour
{
    [Header("Mode Selection")]
    public ChatMode chatMode = ChatMode.Traditional;

    [Header("Component References")]
    public VoiceToTextClient sttClient;
    public ClaudeApiClient claudeClient;
    public TTSStreamClient ttsClient;
    public RealtimeVoiceChatController realtimeController;

    [Header("UI References")]
    public Text statusText;
    public Text userText;
    public Text assistantText;
    public Button recordButton;
    public Button clearHistoryButton;

    [Header("Chat Settings")]
    public bool autoPlayResponse = true;
    public bool showConversationInUI = true;
    public int maxConversationLength = 20;

    [Header("Visual Feedback")]
    public Color recordingColor = Color.red;
    public Color processingColor = Color.yellow;
    public Color readyColor = Color.white;
    public Color errorColor = Color.red;

    [Header("Debug")]
    public bool enableDebugLog = true;

    private bool _isProcessing = false;
    private bool _isRealtimeMode = false;

    private void Start()
    {
        InitializeComponents();
        SetupEventHandlers();
        SwitchChatMode();
    }

    private void InitializeComponents()
    {
        // 컴포넌트 자동 검색
        if (sttClient == null) sttClient = FindObjectOfType<VoiceToTextClient>();
        if (claudeClient == null) claudeClient = FindObjectOfType<ClaudeApiClient>();
        if (ttsClient == null) ttsClient = FindObjectOfType<TTSStreamClient>();
        if (realtimeController == null) realtimeController = FindObjectOfType<RealtimeVoiceChatController>();

        // 컴포넌트가 없으면 자동 추가
        if (sttClient == null) sttClient = gameObject.AddComponent<VoiceToTextClient>();
        if (claudeClient == null) claudeClient = gameObject.AddComponent<ClaudeApiClient>();
        if (realtimeController == null) realtimeController = gameObject.AddComponent<RealtimeVoiceChatController>();

        // UI 버튼 설정
        if (recordButton) recordButton.onClick.AddListener(ToggleRecording);
        if (clearHistoryButton) clearHistoryButton.onClick.AddListener(ClearConversationHistory);
    }

    private void SetupEventHandlers()
    {
        // STT 이벤트
        if (sttClient)
        {
            sttClient.OnRecordingStarted += OnRecordingStarted;
            sttClient.OnRecordingStopped += OnRecordingStopped;
            sttClient.OnTextRecognized += OnTextRecognized;
            sttClient.OnError += OnSTTError;
        }

        // Claude API 이벤트
        if (claudeClient)
        {
            claudeClient.OnRequestStarted += OnClaudeRequestStarted;
            claudeClient.OnResponseReceived += OnClaudeResponseReceived;
            claudeClient.OnRequestCompleted += OnClaudeRequestCompleted;
            claudeClient.OnError += OnClaudeError;
        }
    }

    // STT 이벤트 핸들러들
    private void OnRecordingStarted()
    {
        _isProcessing = true;
        UpdateUI("🎤 Recording... (Release Space to stop)", recordingColor);
        UpdateRecordButton();
    }

    private void OnRecordingStopped()
    {
        UpdateUI("🔄 Processing speech...", processingColor);
        UpdateRecordButton();
    }

    private async void OnTextRecognized(string recognizedText)
    {
        if (string.IsNullOrEmpty(recognizedText))
        {
            UpdateUI("No speech detected. Try again.", errorColor);
            _isProcessing = false;
            UpdateRecordButton();
            return;
        }

        if (showConversationInUI && userText)
        {
            userText.text = $"You: {recognizedText}";
        }

        Debug.Log($"[VoiceChat] User said: {recognizedText}");

        // Claude API에 메시지 전송
        if (claudeClient && claudeClient.IsApiKeyValid())
        {
            UpdateUI("🤖 Claude is thinking...", processingColor);
            await claudeClient.SendMessage(recognizedText);
        }
        else
        {
            OnClaudeError("Claude API key not configured");
        }
    }

    private void OnSTTError(string error)
    {
        UpdateUI($"Speech recognition error: {error}", errorColor);
        _isProcessing = false;
        UpdateRecordButton();
        Debug.LogError($"[VoiceChat] STT Error: {error}");
    }

    // Claude API 이벤트 핸들러들
    private void OnClaudeRequestStarted()
    {
        UpdateUI("🤖 Claude is thinking...", processingColor);
    }

    private void OnClaudeResponseReceived(string response)
    {
        if (showConversationInUI && assistantText)
        {
            assistantText.text = $"Claude: {response}";
        }

        Debug.Log($"[VoiceChat] Claude responded: {response}");

        // 자동 TTS 재생
        if (autoPlayResponse && ttsClient)
        {
            UpdateUI("🔊 Playing response...", processingColor);
            ttsClient.Speak(response);
        }
    }

    private void OnClaudeRequestCompleted()
    {
        if (!autoPlayResponse)
        {
            UpdateUI("Ready - Press Space for next question", readyColor);
            _isProcessing = false;
            UpdateRecordButton();
        }
        // autoPlayResponse가 true면 TTS 완료 후에 상태 업데이트
    }

    private void OnClaudeError(string error)
    {
        UpdateUI($"Claude API error: {error}", errorColor);
        _isProcessing = false;
        UpdateRecordButton();
        Debug.LogError($"[VoiceChat] Claude Error: {error}");

        // 3초 후 상태 리셋
        Invoke(nameof(ResetToReady), 3f);
    }

    // TTS 완료 감지 (TTSStreamClient에서 이벤트가 있다면)
    private void OnTTSCompleted()
    {
        UpdateUI("Ready - Press Space for next question", readyColor);
        _isProcessing = false;
        UpdateRecordButton();
    }

    // UI 업데이트 메서드들
    private void UpdateUI(string message, Color? color = null)
    {
        if (statusText)
        {
            statusText.text = message;
            if (color.HasValue) statusText.color = color.Value;
        }
    }

    private void UpdateRecordButton()
    {
        if (recordButton)
        {
            var buttonText = recordButton.GetComponentInChildren<Text>();
            if (buttonText)
            {
                if (_isProcessing)
                {
                    buttonText.text = "Processing...";
                    recordButton.interactable = false;
                }
                else if (sttClient && sttClient.IsRecording)
                {
                    buttonText.text = "Stop Recording";
                    recordButton.interactable = true;
                }
                else
                {
                    buttonText.text = "Start Recording";
                    recordButton.interactable = true;
                }
            }
        }
    }

    private void ResetToReady()
    {
        UpdateUI("Ready - Press Space for voice chat", readyColor);
        _isProcessing = false;
        UpdateRecordButton();
    }

    // 모드 전환
    private void SwitchChatMode()
    {
        _isRealtimeMode = (chatMode == ChatMode.Realtime);

        if (_isRealtimeMode)
        {
            // Realtime 모드 활성화
            if (realtimeController) realtimeController.enabled = true;
            UpdateUI("Realtime Mode - Connecting...", processingColor);
        }
        else
        {
            // Traditional 모드 활성화
            if (realtimeController) realtimeController.enabled = false;
            UpdateUI("Traditional Mode - Ready", readyColor);
        }

        if (enableDebugLog) Debug.Log($"[VoiceChat] Switched to {chatMode} mode");
    }

    // 공개 메서드들
    public void ToggleRecording()
    {
        if (_isRealtimeMode)
        {
            // Realtime 모드
            if (realtimeController)
            {
                realtimeController.ToggleRecording();
            }
        }
        else
        {
            // Traditional 모드
            if (_isProcessing && !sttClient.IsRecording) return;

            if (sttClient.IsRecording)
            {
                sttClient.StopRecording();
            }
            else
            {
                sttClient.StartRecording();
            }
        }
    }

    public void ClearConversationHistory()
    {
        if (_isRealtimeMode)
        {
            // Realtime 모드
            if (realtimeController)
            {
                realtimeController.ClearConversationHistory();
            }
        }
        else
        {
            // Traditional 모드
            if (claudeClient)
            {
                claudeClient.ClearConversationHistory();
                if (userText) userText.text = "";
                if (assistantText) assistantText.text = "";
                UpdateUI("Conversation history cleared", readyColor);
                Debug.Log("[VoiceChat] Conversation history cleared");

                // 2초 후 상태 리셋
                Invoke(nameof(ResetToReady), 2f);
            }
        }
    }

    public void SetAutoPlayResponse(bool enabled)
    {
        autoPlayResponse = enabled;

        if (_isRealtimeMode && realtimeController)
        {
            realtimeController.SetAutoPlayResponse(enabled);
        }

        Debug.Log($"[VoiceChat] Auto play response: {enabled}");
    }

    public void SetChatMode(ChatMode newMode)
    {
        if (chatMode != newMode)
        {
            chatMode = newMode;
            SwitchChatMode();
        }
    }

    public void PlayLastResponse()
    {
        if (assistantText && ttsClient && !string.IsNullOrEmpty(assistantText.text))
        {
            string response = assistantText.text.Replace("Claude: ", "");
            ttsClient.Speak(response);
        }
    }

    // 현재 상태 정보
    public bool IsRecording()
    {
        if (_isRealtimeMode)
        {
            return realtimeController && realtimeController.IsRecording();
        }
        else
        {
            return sttClient && sttClient.IsRecording;
        }
    }

    // 대화 기록 관리
    private void Update()
    {
        // 대화 기록이 너무 길어지면 자동으로 정리
        if (claudeClient && claudeClient.GetConversationLength() > maxConversationLength)
        {
            claudeClient.TrimConversationHistory(maxConversationLength);
        }
    }

    private void OnDestroy()
    {
        // STT 이벤트 해제
        if (sttClient)
        {
            sttClient.OnRecordingStarted -= OnRecordingStarted;
            sttClient.OnRecordingStopped -= OnRecordingStopped;
            sttClient.OnTextRecognized -= OnTextRecognized;
            sttClient.OnError -= OnSTTError;
        }

        // Claude API 이벤트 해제
        if (claudeClient)
        {
            claudeClient.OnRequestStarted -= OnClaudeRequestStarted;
            claudeClient.OnResponseReceived -= OnClaudeResponseReceived;
            claudeClient.OnRequestCompleted -= OnClaudeRequestCompleted;
            claudeClient.OnError -= OnClaudeError;
        }
    }
}