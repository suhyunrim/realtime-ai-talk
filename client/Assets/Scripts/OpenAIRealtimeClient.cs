// Assets/OpenAIRealtimeClient.cs
using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.UIElements;
using TMPro;

[System.Serializable]
public class RealtimeEvent
{
    public string type;
    public string event_id;

    [JsonExtensionData]
    public Dictionary<string, object> additionalData = new Dictionary<string, object>();
}

[System.Serializable]
public class RealtimeSessionUpdate
{
    public string type = "session.update";
    public RealtimeSession session;
}

[System.Serializable]
public class RealtimeSession
{
    public string[] modalities = { "text", "audio" };
    public string instructions = "당신은 친근하고 도움이 되는 AI 어시스턴트입니다. 한국어로 자연스럽고 간결하게 대답해주세요.";
    public string voice = "shimmer";
    public string input_audio_format = "pcm16";
    public string output_audio_format = "pcm16";
    public RealtimeInputAudioTranscription input_audio_transcription;
    public RealtimeTurnDetection turn_detection;
    public RealtimeTool[] tools = new RealtimeTool[0]; // 빈 배열
    public int max_response_output_tokens = 4096;
    public double temperature = 0.8;
}

[System.Serializable]
public class RealtimeInputAudioTranscription
{
    public string model = "whisper-1";
}

[System.Serializable]
public class RealtimeTurnDetection
{
    public string type = "server_vad";
    public double threshold = 0.5;
    public int prefix_padding_ms = 300;
    public int silence_duration_ms = 200;
}

[System.Serializable]
public class RealtimeTool
{
    public string type;
    public string name;
    public string description;
    public object parameters;
}

[System.Serializable]
public class RealtimeInputAudioBufferAppend
{
    public string type = "input_audio_buffer.append";
    public string audio; // Base64 encoded PCM16 audio
}

[System.Serializable]
public class RealtimeInputAudioBufferCommit
{
    public string type = "input_audio_buffer.commit";
}

[System.Serializable]
public class RealtimeResponseCreate
{
    public string type = "response.create";
    public RealtimeResponse response;
}

[System.Serializable]
public class RealtimeResponse
{
    public string[] modalities = { "text" };
    public string instructions;
}

public class OpenAIRealtimeClient : MonoBehaviour
{
    [Header("Recording Settings")]
    public KeyCode recordKey = KeyCode.Space;

    public int sampleRate = 24000; // OpenAI Realtime API supports 24kHz
    public int maxRecordingSeconds = 30;

    [Header("Session Settings")]
    [TextArea(3, 5)]
    public string instructions = "당신은 친근하고 도움이 되는 AI 어시스턴트입니다. 한국어로 자연스럽고 간결하게 대답해주세요.";

    public string voice = "alloy"; // alloy, echo, fable, onyx, nova, shimmer
    public double temperature = 0.8;
    public double vadThreshold = 0.5;
    public int silenceDurationMs = 500;

    [Header("Debug")]
    public bool enableDebugLog = true;

    // Internal
    private Config _config;
    private ClientWebSocket _ws;

    private CancellationTokenSource _cts;
    private AudioClip _recordedClip;
    private bool _isRecording = false;
    private bool _isConnected = false;
    private string _sessionId;
    private Queue<byte[]> _audioBuffer;
    private Coroutine _audioSendCoroutine;

    [SerializeField]
    private TextMeshProUGUI mySpeechLabel;

    [SerializeField]
    private TextMeshProUGUI fernSpeechLabel;

    // Events
    public event Action<string> OnTranscriptionReceived;

    public event Action<string> OnTextResponseReceived;

    public event Action<string> OnTextDelta; // 텍스트 스트리밍 델타

    public event Action<byte[]> OnAudioResponseReceived;

    public event Action OnResponseDone; // 응답 완료 이벤트

    public event Action OnRecordingStarted;

    public event Action OnRecordingStopped;

    public event Action<string> OnError;

    public event Action OnConnected;

    public event Action OnDisconnected;

    private void Awake()
    {
        _audioBuffer = new Queue<byte[]>();

        // Load config from Resources folder
        _config = Resources.Load<Config>("Config");
        if (_config == null)
        {
            Debug.LogError("[Realtime] Config file not found in Resources folder! Please create a Config asset in Resources/Config.asset");
        }
    }

    private void Start()
    {
        ValidateApiKey();
        _ = ConnectToRealtime();
    }

    private void ValidateApiKey()
    {
        if (enableDebugLog)
        {
            if (_config == null)
            {
                Debug.LogError("[Realtime] Config not found!");
            }
            else if (!_config.IsValid())
            {
                Debug.LogWarning("[Realtime] API key not set or invalid!");
            }
            else
            {
                Debug.Log($"[Realtime] Using API Key: {_config.ApiKey.Substring(0, 8)}...");
            }
        }
    }

    private async Task ConnectToRealtime()
    {
        try
        {
            if (enableDebugLog) Debug.Log("[Realtime] Connecting to OpenAI Realtime API...");

            _cts = new CancellationTokenSource();
            _ws = new ClientWebSocket();

            // 헤더 설정
            _ws.Options.SetRequestHeader("Authorization", $"Bearer {_config.ApiKey}");
            _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

            // WebSocket 연결
            var uri = new Uri("wss://api.openai.com/v1/realtime?model=gpt-4o-realtime-preview-2024-10-01");
            await _ws.ConnectAsync(uri, _cts.Token);

            _isConnected = true;
            if (enableDebugLog) Debug.Log("[Realtime] Connected to OpenAI Realtime API");

            // 세션 설정
            await SetupSession();

            // 메시지 수신 시작
            _ = ReceiveMessages();

            OnConnected?.Invoke();
        }
        catch (Exception e)
        {
            Debug.LogError($"[Realtime] Connection failed: {e.Message}");
            OnError?.Invoke($"Connection failed: {e.Message}");
        }
    }

    private async Task SetupSession()
    {
        var sessionUpdate = new RealtimeSessionUpdate
        {
            session = new RealtimeSession
            {
                instructions = instructions,
                voice = voice,
                input_audio_format = "pcm16",
                output_audio_format = "pcm16",
                input_audio_transcription = new RealtimeInputAudioTranscription(),
                turn_detection = new RealtimeTurnDetection
                {
                    threshold = vadThreshold,
                    silence_duration_ms = silenceDurationMs
                },
                tools = new RealtimeTool[0], // 빈 배열
                temperature = temperature
            }
        };

        await SendMessage(sessionUpdate);
        if (enableDebugLog) Debug.Log($"[Realtime] Session configured - voice: {voice}");
    }

    private async Task SendMessage(object message)
    {
        if (_ws?.State != WebSocketState.Open) return;

        try
        {
            var json = JsonConvert.SerializeObject(message);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);

            if (enableDebugLog) Debug.Log($"[Realtime] Sent: {json.Substring(0, Math.Min(200, json.Length))}...");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Realtime] Send error: {e.Message}");
        }
    }

    private async Task ReceiveMessages()
    {
        var buffer = new byte[8192];
        var messageBuffer = new List<byte>();

        try
        {
            while (_ws?.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                messageBuffer.AddRange(buffer.Take(result.Count));

                if (result.EndOfMessage)
                {
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var json = Encoding.UTF8.GetString(messageBuffer.ToArray());
                        await ProcessMessage(json);
                    }

                    messageBuffer.Clear();
                }
            }
        }
        catch (Exception e)
        {
            if (!_cts.Token.IsCancellationRequested)
            {
                Debug.LogError($"[Realtime] Receive error: {e.Message}");
                OnError?.Invoke($"Receive error: {e.Message}");
            }
        }

        _isConnected = false;
        OnDisconnected?.Invoke();
    }

    private async Task ProcessMessage(string json)
    {
        try
        {
            //if (enableDebugLog) Debug.Log($"[Realtime] Received: {json.Substring(0, Math.Min(300, json.Length))}...");

            var eventData = JObject.Parse(json);
            var eventType = eventData["type"]?.ToString();

            switch (eventType)
            {
                case "session.created":
                    _sessionId = eventData["session"]?["id"]?.ToString();
                    if (enableDebugLog) Debug.Log($"[Realtime] Session created: {_sessionId}");
                    break;

                case "session.updated":
                    if (enableDebugLog) Debug.Log("[Realtime] Session updated");
                    break;

                case "input_audio_buffer.speech_started":
                    if (enableDebugLog) Debug.Log("[Realtime] Speech started detected");
                    break;

                case "input_audio_buffer.speech_stopped":
                    if (enableDebugLog) Debug.Log("[Realtime] Speech stopped detected");
                    break;

                case "input_audio_buffer.committed":
                    if (enableDebugLog) Debug.Log("[Realtime] Audio buffer committed");
                    // 오디오 입력 완료 후 응답 생성 요청
                    _ = RequestResponse();
                    break;

                case "conversation.item.input_audio_transcription.completed":
                    var transcript = eventData["transcript"]?.ToString();
                    if (!string.IsNullOrEmpty(transcript))
                    {
                        if (enableDebugLog) Debug.Log($"[Realtime] Transcription: {transcript}");
                        mySpeechLabel.text = transcript;
                        OnTranscriptionReceived?.Invoke(transcript);
                    }
                    break;

                case "response.created":
                    if (enableDebugLog) Debug.Log("[Realtime] Response creation started");
                    break;

                case "response.output_item.added":
                    if (enableDebugLog) Debug.Log("[Realtime] Response item added");
                    break;

                case "response.content_part.added":
                    var contentType = eventData["part"]?["type"]?.ToString();
                    if (enableDebugLog) Debug.Log($"[Realtime] Content part added: {contentType}");
                    break;

                case "response.content_part.done":
                    var part = eventData["part"];
                    var partType = part?["type"]?.ToString();

                    if (partType == "text")
                    {
                        var text = part["text"]?.ToString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            if (enableDebugLog) Debug.Log($"[Realtime] Text response: {text}");
                            fernSpeechLabel.text = text.Replace("。", "。\n");
                            OnTextResponseReceived?.Invoke(text);
                        }
                    }
                    else if (partType == "audio")
                    {
                        var audio = part["transcript"]?.ToString();
                        if (!string.IsNullOrEmpty(audio))
                        {
                            if (enableDebugLog) Debug.Log($"[Realtime] Audio transcript: {audio}");
                            OnTextResponseReceived?.Invoke(audio);
                        }
                    }
                    break;

                case "response.audio_transcript.delta":
                    // 오디오의 실시간 자막 (텍스트 스트리밍)
                    var transcriptDelta = eventData["delta"]?.ToString();
                    if (!string.IsNullOrEmpty(transcriptDelta))
                    {
                        OnTextDelta?.Invoke(transcriptDelta);
                    }
                    break;

                case "response.text.delta":
                    var textDelta = eventData["delta"]?.ToString();
                    if (!string.IsNullOrEmpty(textDelta))
                    {
                        OnTextDelta?.Invoke(textDelta);
                    }
                    break;

                case "response.audio.delta":
                    var audioData = eventData["delta"]?.ToString();
                    if (!string.IsNullOrEmpty(audioData))
                    {
                        try
                        {
                            var audioBytes = Convert.FromBase64String(audioData);
                            OnAudioResponseReceived?.Invoke(audioBytes);
                        }
                        catch (Exception e)
                        {
                            Debug.LogError($"[Realtime] Audio decode error: {e.Message}");
                        }
                    }
                    break;

                case "response.text.done":
                    if (enableDebugLog) Debug.Log("[Realtime] Text response completed");
                    break;

                case "response.audio.done":
                    if (enableDebugLog) Debug.Log("[Realtime] Audio response completed");
                    break;

                case "response.done":
                    if (enableDebugLog) Debug.Log("[Realtime] Response completed");
                    OnResponseDone?.Invoke();
                    break;

                case "error":
                    var error = eventData["error"];
                    var errorMsg = $"OpenAI error: {error?["message"]} (Code: {error?["code"]})";
                    Debug.LogError($"[Realtime] {errorMsg}");
                    OnError?.Invoke(errorMsg);
                    break;

                default:
                    if (enableDebugLog) Debug.Log($"[Realtime] Unhandled event: {eventType}");
                    break;
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[Realtime] Message processing error: {e.Message}");
        }
    }

    private void Update()
    {
        if (Input.GetKeyDown(recordKey))
        {
            StartRecording();
        }
        else if (Input.GetKeyUp(recordKey))
        {
            StopRecording();
        }
    }

    public void StartRecording()
    {
        if (_isRecording || !_isConnected) return;

        _isRecording = true;
        OnRecordingStarted?.Invoke();

        if (enableDebugLog) Debug.Log("[Realtime] Recording started");

        // 마이크 녹음 시작
        _recordedClip = Microphone.Start(null, true, maxRecordingSeconds, sampleRate);

        mySpeechLabel.text = "...";

        // 오디오 전송 코루틴 시작
        _audioSendCoroutine = StartCoroutine(SendAudioStream());
    }

    public void StopRecording()
    {
        if (!_isRecording) return;

        _isRecording = false;
        OnRecordingStopped?.Invoke();

        if (enableDebugLog) Debug.Log("[Realtime] Recording stopped");

        Microphone.End(null);

        if (_audioSendCoroutine != null)
        {
            StopCoroutine(_audioSendCoroutine);
            _audioSendCoroutine = null;
        }

        // 마지막 오디오 버퍼 커밋
        _ = CommitAudioBuffer();
    }

    private IEnumerator SendAudioStream()
    {
        int lastPosition = 0;

        while (_isRecording && _recordedClip != null)
        {
            int currentPosition = Microphone.GetPosition(null);

            if (currentPosition != lastPosition)
            {
                // 새로운 오디오 데이터 추출
                int sampleCount = currentPosition - lastPosition;
                if (sampleCount < 0) // 순환 버퍼 처리
                {
                    sampleCount = _recordedClip.samples + sampleCount;
                }

                if (sampleCount > 0)
                {
                    float[] samples = new float[sampleCount];
                    _recordedClip.GetData(samples, lastPosition);

                    // PCM16으로 변환
                    byte[] pcmData = ConvertToPCM16(samples);

                    // 오디오 데이터 전송
                    _ = SendAudioData(pcmData);
                }

                lastPosition = currentPosition;
            }

            yield return new WaitForSeconds(0.1f); // 100ms 간격
        }
    }

    private byte[] ConvertToPCM16(float[] samples)
    {
        short[] intData = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            float sample = Mathf.Clamp(samples[i], -1f, 1f);
            intData[i] = (short)(sample * 32767);
        }

        byte[] bytes = new byte[intData.Length * 2];
        for (int i = 0; i < intData.Length; i++)
        {
            bytes[i * 2] = (byte)(intData[i] & 0xFF);
            bytes[i * 2 + 1] = (byte)((intData[i] >> 8) & 0xFF);
        }

        return bytes;
    }

    private async Task SendAudioData(byte[] audioData)
    {
        if (_ws?.State != WebSocketState.Open || audioData == null || audioData.Length == 0)
            return;

        try
        {
            var audioMessage = new RealtimeInputAudioBufferAppend
            {
                audio = Convert.ToBase64String(audioData)
            };

            await SendMessage(audioMessage);
        }
        catch (Exception e)
        {
            Debug.LogError($"[Realtime] Audio send error: {e.Message}");
        }
    }

    private async Task CommitAudioBuffer()
    {
        if (_ws?.State != WebSocketState.Open) return;

        try
        {
            var commitMessage = new RealtimeInputAudioBufferCommit();
            await SendMessage(commitMessage);

            if (enableDebugLog) Debug.Log("[Realtime] Audio buffer committed");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Realtime] Audio commit error: {e.Message}");
        }
    }

    private async Task RequestResponse()
    {
        if (_ws?.State != WebSocketState.Open) return;

        try
        {
            var responseMessage = new RealtimeResponseCreate
            {
                response = new RealtimeResponse()
            };

            await SendMessage(responseMessage);
            if (enableDebugLog) Debug.Log("[Realtime] Response generation requested");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Realtime] Response request error: {e.Message}");
        }
    }

    // Public properties
    public bool IsRecording => _isRecording;

    public bool IsConnected => _isConnected;
    public string SessionId => _sessionId;

    // Public methods
    public void SetInstructions(string newInstructions)
    {
        instructions = newInstructions;
        if (_isConnected)
        {
            _ = SetupSession(); // 세션 재설정
        }
    }

    public void SetVoice(string newVoice)
    {
        voice = newVoice;
        if (_isConnected)
        {
            _ = SetupSession(); // 세션 재설정
        }
    }

    public void SetConfig(Config newConfig)
    {
        _config = newConfig;
        // 재연결 필요
        if (_isConnected)
        {
            _ = Disconnect();
            _ = ConnectToRealtime();
        }
    }

    public async Task Disconnect()
    {
        try
        {
            _isConnected = false;
            _cts?.Cancel();

            if (_ws?.State == WebSocketState.Open)
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting", CancellationToken.None);
            }

            if (enableDebugLog) Debug.Log("[Realtime] Disconnected");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Realtime] Disconnect error: {e.Message}");
        }
    }

    private void OnDestroy()
    {
        if (Microphone.IsRecording(null))
        {
            Microphone.End(null);
        }

        _ = Disconnect();
        _cts?.Dispose();
        _ws?.Dispose();
    }

    public bool IsApiKeyValid()
    {
        return _config != null && _config.IsValid();
    }
}