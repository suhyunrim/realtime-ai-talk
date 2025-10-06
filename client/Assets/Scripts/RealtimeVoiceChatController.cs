// Assets/RealtimeVoiceChatController.cs
using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

public enum AudioOutputMode
{
    OpenAIDirect,        // OpenAI 오디오 직접 재생
    RVCStreaming,        // OpenAI 오디오 → RVC 스트리밍 변환
    TraditionalTTS,      // 기존 TTS (VOICEVOX + RVC)
    TextStreamingTTS     // OpenAI 텍스트 → VOICEVOX + RVC 스트리밍 (가장 빠름)
}

public class RealtimeVoiceChatController : MonoBehaviour
{
    [Header("Component References")]
    public OpenAIRealtimeClient realtimeClient;
    public TTSStreamClient ttsClient;
    public RVCStreamClient rvcClient;
    public TTSTextStreamClient ttsTextStreamClient; // 새로운 텍스트 스트리밍 클라이언트

    [Header("UI References")]
    public Text statusText;
    public Text userText;
    public Text assistantText;
    public Button recordButton;
    public Button clearHistoryButton;

    [Header("Chat Settings")]
    public bool autoPlayResponse = true;
    public bool showConversationInUI = true;
    public AudioOutputMode audioOutputMode = AudioOutputMode.TextStreamingTTS; // 기본값 변경

    [Header("Visual Feedback")]
    public Color recordingColor = Color.red;
    public Color processingColor = Color.yellow;
    public Color readyColor = Color.white;
    public Color errorColor = Color.red;
    public Color connectedColor = Color.green;

    [Header("Debug")]
    public bool enableDebugLog = true;

    // Audio playback for OpenAI audio response
    private AudioSource _audioSource;
    private Queue<byte[]> _audioQueue;
    private bool _isPlayingAudio = false;

    private void Start()
    {
        InitializeComponents();
        SetupEventHandlers();
        UpdateUI("Connecting to OpenAI Realtime...", processingColor);
    }

    private void InitializeComponents()
    {
        // 컴포넌트 자동 검색
        if (realtimeClient == null) realtimeClient = FindObjectOfType<OpenAIRealtimeClient>();
        if (ttsClient == null) ttsClient = FindObjectOfType<TTSStreamClient>();
        if (rvcClient == null) rvcClient = FindObjectOfType<RVCStreamClient>();

        // 컴포넌트가 없으면 자동 추가
        if (realtimeClient == null) realtimeClient = gameObject.AddComponent<OpenAIRealtimeClient>();
        if (rvcClient == null) rvcClient = gameObject.AddComponent<RVCStreamClient>();

        // AudioSource 설정 (OpenAI 오디오용)
        _audioSource = GetComponent<AudioSource>();
        if (_audioSource == null) _audioSource = gameObject.AddComponent<AudioSource>();

        _audioQueue = new Queue<byte[]>();

        // UI 버튼 설정
        if (recordButton) recordButton.onClick.AddListener(ToggleRecording);
        if (clearHistoryButton) clearHistoryButton.onClick.AddListener(ClearConversationHistory);
    }

    private void SetupEventHandlers()
    {
        if (realtimeClient)
        {
            // 연결 이벤트
            realtimeClient.OnConnected += OnRealtimeConnected;
            realtimeClient.OnDisconnected += OnRealtimeDisconnected;
            realtimeClient.OnError += OnRealtimeError;

            // 녹음 이벤트
            realtimeClient.OnRecordingStarted += OnRecordingStarted;
            realtimeClient.OnRecordingStopped += OnRecordingStopped;

            // 응답 이벤트
            realtimeClient.OnTranscriptionReceived += OnTranscriptionReceived;
            realtimeClient.OnTextResponseReceived += OnTextResponseReceived;
            realtimeClient.OnTextDelta += OnTextDelta; // 텍스트 스트리밍
            realtimeClient.OnAudioResponseReceived += OnAudioResponseReceived;
            realtimeClient.OnResponseDone += OnResponseDone;
        }

        if (rvcClient)
        {
            // RVC 클라이언트 이벤트
            rvcClient.OnConnected += OnRVCConnected;
            rvcClient.OnDisconnected += OnRVCDisconnected;
            rvcClient.OnError += OnRVCError;
        }
    }

    // Realtime 연결 이벤트 핸들러들
    private void OnRealtimeConnected()
    {
        UpdateUI("Ready - Press Space to start voice chat", connectedColor);
        UpdateRecordButton();
        if (enableDebugLog) Debug.Log("[RealtimeChat] Connected to OpenAI Realtime API");
    }

    private void OnRealtimeDisconnected()
    {
        UpdateUI("Disconnected from OpenAI Realtime", errorColor);
        UpdateRecordButton();
        if (enableDebugLog) Debug.Log("[RealtimeChat] Disconnected from OpenAI Realtime API");
    }

    private void OnRealtimeError(string error)
    {
        UpdateUI($"Realtime error: {error}", errorColor);
        UpdateRecordButton();
        Debug.LogError($"[RealtimeChat] Error: {error}");

        // 3초 후 상태 리셋
        Invoke(nameof(ResetToReady), 3f);
    }

    // 녹음 이벤트 핸들러들
    private void OnRecordingStarted()
    {
        UpdateUI("🎤 Recording... (Release Space to stop)", recordingColor);
        UpdateRecordButton();
        if (enableDebugLog) Debug.Log("[RealtimeChat] Recording started");
    }

    private void OnRecordingStopped()
    {
        UpdateUI("🔄 Processing speech...", processingColor);
        UpdateRecordButton();
        if (enableDebugLog) Debug.Log("[RealtimeChat] Recording stopped");
    }

    // 응답 이벤트 핸들러들
    private void OnTranscriptionReceived(string transcript)
    {
        if (showConversationInUI && userText)
        {
            userText.text = $"You: {transcript}";
        }

        if (enableDebugLog) Debug.Log($"[RealtimeChat] User said: {transcript}");
    }

    private string _textBuffer = "";

    private void OnTextDelta(string textDelta)
    {
        _textBuffer += textDelta;

        // UI 업데이트
        if (showConversationInUI && assistantText)
        {
            assistantText.text = $"Assistant: {_textBuffer}";
        }

        // TextStreamingTTS 모드에서 텍스트 청크 전송
        if (audioOutputMode == AudioOutputMode.TextStreamingTTS && autoPlayResponse && ttsTextStreamClient && ttsTextStreamClient.IsConnected)
        {
            _ = ttsTextStreamClient.SendTextChunk(textDelta);
        }
    }

    private void OnTextResponseReceived(string response)
    {
        if (showConversationInUI && assistantText)
        {
            assistantText.text = $"Assistant: {response}";
        }

        if (enableDebugLog) Debug.Log($"[RealtimeChat] Assistant responded: {response}");

        // Traditional TTS 모드에서만 기존 TTS 사용
        if (audioOutputMode == AudioOutputMode.TraditionalTTS && autoPlayResponse && ttsClient)
        {
            UpdateUI("🔊 Playing response...", processingColor);
            ttsClient.Speak(response);
        }
    }

    private void OnAudioResponseReceived(byte[] audioData)
    {
        if (!autoPlayResponse) return;

        switch (audioOutputMode)
        {
            case AudioOutputMode.OpenAIDirect:
                // OpenAI 오디오 직접 재생
                _audioQueue.Enqueue(audioData);
                if (!_isPlayingAudio)
                {
                    UpdateUI("🔊 Playing response...", processingColor);
                    StartCoroutine(PlayAudioQueue());
                }
                break;

            case AudioOutputMode.RVCStreaming:
                // OpenAI 오디오를 RVC 서버로 스트리밍
                if (rvcClient && rvcClient.IsConnected)
                {
                    UpdateUI("🔊 Converting voice...", processingColor);
                    _ = rvcClient.SendAudioData(audioData);
                }
                break;

            case AudioOutputMode.TraditionalTTS:
                // 텍스트 응답만 사용, 오디오는 무시
                break;
        }
    }

    private void OnResponseDone()
    {
        if (enableDebugLog) Debug.Log("[RealtimeChat] Response done");

        // RVC 스트리밍 모드인 경우 end 신호 전송
        if (audioOutputMode == AudioOutputMode.RVCStreaming && rvcClient && rvcClient.IsConnected)
        {
            _ = rvcClient.SendEndSignal();
        }

        // TextStreamingTTS 모드인 경우 end 신호 전송
        if (audioOutputMode == AudioOutputMode.TextStreamingTTS && ttsTextStreamClient && ttsTextStreamClient.IsConnected)
        {
            _ = ttsTextStreamClient.SendEndSignal();
            _textBuffer = ""; // 버퍼 초기화
        }
    }

    // RVC 이벤트 핸들러들
    private void OnRVCConnected()
    {
        if (enableDebugLog) Debug.Log("[RealtimeChat] RVC server connected");
    }

    private void OnRVCDisconnected()
    {
        if (enableDebugLog) Debug.Log("[RealtimeChat] RVC server disconnected");
    }

    private void OnRVCError(string error)
    {
        Debug.LogError($"[RealtimeChat] RVC error: {error}");
    }

    private System.Collections.IEnumerator PlayAudioQueue()
    {
        _isPlayingAudio = true;

        while (_audioQueue.Count > 0)
        {
            var audioData = _audioQueue.Dequeue();

            // PCM16 데이터를 AudioClip으로 변환
            var audioClip = ConvertPCM16ToAudioClip(audioData, realtimeClient.sampleRate);

            if (audioClip != null)
            {
                _audioSource.clip = audioClip;
                _audioSource.Play();

                // 오디오 재생 완료까지 대기
                yield return new WaitForSeconds(audioClip.length);

                // AudioClip 정리
                DestroyImmediate(audioClip);
            }

            yield return null;
        }

        _isPlayingAudio = false;
        UpdateUI("Ready - Press Space for next question", readyColor);
        UpdateRecordButton();
    }

    private AudioClip ConvertPCM16ToAudioClip(byte[] pcm16Data, int sampleRate)
    {
        if (pcm16Data == null || pcm16Data.Length == 0) return null;

        // PCM16 바이트를 float 배열로 변환
        int sampleCount = pcm16Data.Length / 2;
        float[] samples = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            short sample = (short)(pcm16Data[i * 2] | (pcm16Data[i * 2 + 1] << 8));
            samples[i] = sample / 32768f;
        }

        // AudioClip 생성
        var clip = AudioClip.Create("RealtimeAudio", sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);

        return clip;
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
                if (!realtimeClient.IsConnected)
                {
                    buttonText.text = "Disconnected";
                    recordButton.interactable = false;
                }
                else if (realtimeClient.IsRecording)
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
        if (realtimeClient.IsConnected)
        {
            UpdateUI("Ready - Press Space for voice chat", readyColor);
        }
        else
        {
            UpdateUI("Disconnected from OpenAI Realtime", errorColor);
        }
        UpdateRecordButton();
    }

    // 공개 메서드들
    public void ToggleRecording()
    {
        if (!realtimeClient.IsConnected) return;

        if (realtimeClient.IsRecording)
        {
            realtimeClient.StopRecording();
        }
        else
        {
            realtimeClient.StartRecording();
        }
    }

    public void ClearConversationHistory()
    {
        // OpenAI Realtime API는 세션 기반이므로 새 세션으로 재시작
        if (realtimeClient && realtimeClient.IsConnected)
        {
            if (userText) userText.text = "";
            if (assistantText) assistantText.text = "";
            UpdateUI("Conversation cleared - Reconnecting...", processingColor);

            // 재연결로 새 세션 시작
            _ = RestartSession();

            if (enableDebugLog) Debug.Log("[RealtimeChat] Conversation cleared - restarting session");
        }
    }

    private async System.Threading.Tasks.Task RestartSession()
    {
        await realtimeClient.Disconnect();
        await System.Threading.Tasks.Task.Delay(1000); // 1초 대기
        // realtimeClient는 Start()에서 자동으로 재연결을 시도합니다
    }

    public void SetAutoPlayResponse(bool enabled)
    {
        autoPlayResponse = enabled;
        if (enableDebugLog) Debug.Log($"[RealtimeChat] Auto play response: {enabled}");
    }

    public void SetAudioOutputMode(AudioOutputMode mode)
    {
        audioOutputMode = mode;
        if (enableDebugLog) Debug.Log($"[RealtimeChat] Audio output mode: {mode}");
    }

    public void SetInstructions(string newInstructions)
    {
        if (realtimeClient)
        {
            realtimeClient.SetInstructions(newInstructions);
            if (enableDebugLog) Debug.Log($"[RealtimeChat] Instructions updated: {newInstructions}");
        }
    }

    public void SetVoice(string newVoice)
    {
        if (realtimeClient)
        {
            realtimeClient.SetVoice(newVoice);
            if (enableDebugLog) Debug.Log($"[RealtimeChat] Voice updated: {newVoice}");
        }
    }

    // 현재 상태 정보
    public bool IsRecording() => realtimeClient && realtimeClient.IsRecording;
    public bool IsConnected() => realtimeClient && realtimeClient.IsConnected;

    private void OnDestroy()
    {
        // 이벤트 해제
        if (realtimeClient)
        {
            realtimeClient.OnConnected -= OnRealtimeConnected;
            realtimeClient.OnDisconnected -= OnRealtimeDisconnected;
            realtimeClient.OnError -= OnRealtimeError;
            realtimeClient.OnRecordingStarted -= OnRecordingStarted;
            realtimeClient.OnRecordingStopped -= OnRecordingStopped;
            realtimeClient.OnTranscriptionReceived -= OnTranscriptionReceived;
            realtimeClient.OnTextResponseReceived -= OnTextResponseReceived;
            realtimeClient.OnTextDelta -= OnTextDelta;
            realtimeClient.OnAudioResponseReceived -= OnAudioResponseReceived;
            realtimeClient.OnResponseDone -= OnResponseDone;
        }

        if (rvcClient)
        {
            rvcClient.OnConnected -= OnRVCConnected;
            rvcClient.OnDisconnected -= OnRVCDisconnected;
            rvcClient.OnError -= OnRVCError;
        }

        // 오디오 정리
        while (_audioQueue != null && _audioQueue.Count > 0)
        {
            _audioQueue.Dequeue();
        }
    }
}