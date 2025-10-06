// Assets/TextStreamVoiceChatController.cs
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// OpenAI Chat API + Whisper STT + VOICEVOX TTS 기반 음성 대화 컨트롤러
/// (OpenAI Realtime API 대비 30-50% 더 빠른 텍스트 생성)
/// </summary>
public class TextStreamVoiceChatController : MonoBehaviour
{
    [Header("Component References")]
    public WhisperSTTClient whisperClient;
    public OpenAIChatStreamClient chatClient;
    public TTSTextStreamClient ttsClient;

    [Header("UI References")]
    public Text statusText;
    public Text userText;
    public Text assistantText;
    public Button recordButton;
    public Button clearHistoryButton;

    [Header("Chat Settings")]
    public bool autoPlayResponse = true;
    public bool showConversationInUI = true;

    [Header("Visual Feedback")]
    public Color recordingColor = Color.red;
    public Color processingColor = Color.yellow;
    public Color readyColor = Color.white;
    public Color errorColor = Color.red;
    public Color connectedColor = Color.green;

    [Header("Debug")]
    public bool enableDebugLog = true;

    private string _currentUserInput = "";
    private string _currentAssistantResponse = "";

    private void Start()
    {
        InitializeComponents();
        SetupEventHandlers();
        UpdateUI("Ready - Press Space to start voice chat", connectedColor);
    }

    private void InitializeComponents()
    {
        // 컴포넌트 자동 검색
        if (whisperClient == null) whisperClient = FindObjectOfType<WhisperSTTClient>();
        if (chatClient == null) chatClient = FindObjectOfType<OpenAIChatStreamClient>();
        if (ttsClient == null) ttsClient = FindObjectOfType<TTSTextStreamClient>();

        // 컴포넌트가 없으면 자동 추가
        if (whisperClient == null) whisperClient = gameObject.AddComponent<WhisperSTTClient>();
        if (chatClient == null) chatClient = gameObject.AddComponent<OpenAIChatStreamClient>();
        if (ttsClient == null) ttsClient = gameObject.AddComponent<TTSTextStreamClient>();

        // UI 버튼 설정
        if (recordButton) recordButton.onClick.AddListener(ToggleRecording);
        if (clearHistoryButton) clearHistoryButton.onClick.AddListener(ClearConversationHistory);

        UpdateRecordButton();
    }

    private void SetupEventHandlers()
    {
        // Whisper STT 이벤트
        if (whisperClient)
        {
            whisperClient.OnRecordingStarted += OnRecordingStarted;
            whisperClient.OnRecordingStopped += OnRecordingStopped;
            whisperClient.OnTranscriptionReceived += OnTranscriptionReceived;
            whisperClient.OnError += OnWhisperError;
        }

        // OpenAI Chat 이벤트
        if (chatClient)
        {
            chatClient.OnTextDelta += OnChatTextDelta;
            chatClient.OnResponseComplete += OnChatResponseComplete;
            chatClient.OnStreamEnd += OnChatStreamEnd;
            chatClient.OnError += OnChatError;
        }

        // TTS 이벤트
        if (ttsClient)
        {
            ttsClient.OnConnected += OnTTSConnected;
            ttsClient.OnDisconnected += OnTTSDisconnected;
            ttsClient.OnError += OnTTSError;
        }
    }

    private void Update()
    {
        // Space 키로 녹음 토글
        if (Input.GetKeyDown(KeyCode.Space))
        {
            ToggleRecording();
        }
    }

    // Whisper STT 이벤트 핸들러들
    private void OnRecordingStarted()
    {
        UpdateUI("🎤 Recording... (Release Space to stop)", recordingColor);
        UpdateRecordButton();
        if (enableDebugLog) Debug.Log("[VoiceChat] Recording started");
    }

    private void OnRecordingStopped()
    {
        UpdateUI("🔄 Processing speech...", processingColor);
        UpdateRecordButton();
        if (enableDebugLog) Debug.Log("[VoiceChat] Recording stopped, transcribing...");
    }

    private void OnTranscriptionReceived(string transcript)
    {
        _currentUserInput = transcript;

        if (showConversationInUI && userText)
        {
            userText.text = $"You: {transcript}";
        }

        if (enableDebugLog) Debug.Log($"[VoiceChat] User said: {transcript}");

        UpdateUI("💭 Thinking...", processingColor);

        // OpenAI Chat API에 메시지 전송
        _ = chatClient.SendMessage(transcript);
    }

    private void OnWhisperError(string error)
    {
        UpdateUI($"STT error: {error}", errorColor);
        Debug.LogError($"[VoiceChat] Whisper error: {error}");
        Invoke(nameof(ResetToReady), 3f);
    }

    // OpenAI Chat 이벤트 핸들러들
    private void OnChatTextDelta(string textDelta)
    {
        _currentAssistantResponse += textDelta;

        // UI 업데이트
        if (showConversationInUI && assistantText)
        {
            assistantText.text = $"Assistant: {_currentAssistantResponse}";
        }

        // TTS로 텍스트 청크 전송 (실시간 스트리밍)
        if (autoPlayResponse && ttsClient && ttsClient.IsConnected)
        {
            _ = ttsClient.SendTextChunk(textDelta);
        }
    }

    private void OnChatResponseComplete(string fullResponse)
    {
        if (enableDebugLog) Debug.Log($"[VoiceChat] Assistant responded: {fullResponse}");
    }

    private void OnChatStreamEnd()
    {
        if (enableDebugLog) Debug.Log("[VoiceChat] Chat stream ended");

        // TTS 종료 신호 전송
        if (ttsClient && ttsClient.IsConnected)
        {
            _ = ttsClient.SendEndSignal();
        }

        // 응답 버퍼 초기화
        _currentAssistantResponse = "";

        UpdateUI("🔊 Playing response...", processingColor);

        // 3초 후 Ready 상태로 전환
        Invoke(nameof(ResetToReady), 3f);
    }

    private void OnChatError(string error)
    {
        UpdateUI($"Chat error: {error}", errorColor);
        Debug.LogError($"[VoiceChat] Chat error: {error}");
        Invoke(nameof(ResetToReady), 3f);
    }

    // TTS 이벤트 핸들러들
    private void OnTTSConnected()
    {
        if (enableDebugLog) Debug.Log("[VoiceChat] TTS server connected");
    }

    private void OnTTSDisconnected()
    {
        if (enableDebugLog) Debug.Log("[VoiceChat] TTS server disconnected");
        UpdateUI("TTS server disconnected", errorColor);
    }

    private void OnTTSError(string error)
    {
        Debug.LogError($"[VoiceChat] TTS error: {error}");
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
                if (whisperClient.IsRecording)
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
        UpdateRecordButton();
    }

    // 공개 메서드들
    public void ToggleRecording()
    {
        if (whisperClient.IsRecording)
        {
            _ = whisperClient.StopRecordingAndTranscribe();
        }
        else
        {
            whisperClient.StartRecording();
        }
    }

    public void ClearConversationHistory()
    {
        if (chatClient)
        {
            chatClient.ClearHistory();

            if (userText) userText.text = "";
            if (assistantText) assistantText.text = "";
            _currentUserInput = "";
            _currentAssistantResponse = "";

            UpdateUI("Conversation cleared", processingColor);
            Invoke(nameof(ResetToReady), 1f);

            if (enableDebugLog) Debug.Log("[VoiceChat] Conversation cleared");
        }
    }

    public void SetAutoPlayResponse(bool enabled)
    {
        autoPlayResponse = enabled;
        if (enableDebugLog) Debug.Log($"[VoiceChat] Auto play response: {enabled}");
    }

    public void SetSystemPrompt(string newPrompt)
    {
        if (chatClient)
        {
            chatClient.systemPrompt = newPrompt;
            chatClient.ClearHistory(); // 새 시스템 프롬프트로 히스토리 재시작
            if (enableDebugLog) Debug.Log($"[VoiceChat] System prompt updated: {newPrompt}");
        }
    }

    public void SetTemperature(float temperature)
    {
        if (chatClient)
        {
            chatClient.temperature = temperature;
            if (enableDebugLog) Debug.Log($"[VoiceChat] Temperature updated: {temperature}");
        }
    }

    // 현재 상태 정보
    public bool IsRecording() => whisperClient && whisperClient.IsRecording;
    public bool IsChatStreaming() => chatClient && chatClient.IsStreaming;

    private void OnDestroy()
    {
        // 이벤트 해제
        if (whisperClient)
        {
            whisperClient.OnRecordingStarted -= OnRecordingStarted;
            whisperClient.OnRecordingStopped -= OnRecordingStopped;
            whisperClient.OnTranscriptionReceived -= OnTranscriptionReceived;
            whisperClient.OnError -= OnWhisperError;
        }

        if (chatClient)
        {
            chatClient.OnTextDelta -= OnChatTextDelta;
            chatClient.OnResponseComplete -= OnChatResponseComplete;
            chatClient.OnStreamEnd -= OnChatStreamEnd;
            chatClient.OnError -= OnChatError;
        }

        if (ttsClient)
        {
            ttsClient.OnConnected -= OnTTSConnected;
            ttsClient.OnDisconnected -= OnTTSDisconnected;
            ttsClient.OnError -= OnTTSError;
        }
    }
}
