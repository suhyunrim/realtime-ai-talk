// Assets/TTSTextStreamClient.cs
using UnityEngine;
using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Newtonsoft.Json;
using System.Collections.Generic;
using UnityEngine.UIElements;

/// <summary>
/// 텍스트 스트리밍 → 서버 TTS (VOICEVOX + RVC) → 오디오 스트리밍
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class TTSTextStreamClient : MonoBehaviour
{
    [Header("Server")]
    public string serverUrl = "ws://127.0.0.1:8000/ws/tts_stream";

    public int speaker = 2; // VOICEVOX speaker ID

    [Header("Audio")]
    public int sampleRate = 24000;

    [Header("Debug")]
    public bool enableDebugLog = false;

    // Internal
    private ClientWebSocket _ws;

    private CancellationTokenSource _cts;
    private bool _isConnected = false;
    private AudioSource _audioSource;
    private System.Collections.Generic.Queue<float> _audioBuffer;
    private object _bufferLock = new object();
    private bool _streamEnded = false;
    private bool _audioFinishedEventFired = false;

    // Events
    public event Action OnConnected;

    public event Action OnDisconnected;

    public event Action<string> OnError;

    public event Action OnAudioFinished; // 오디오 재생이 완전히 끝났을 때

    private void Awake()
    {
        _audioBuffer = new System.Collections.Generic.Queue<float>();

        _audioSource = GetComponent<AudioSource>();
        if (_audioSource == null) _audioSource = gameObject.AddComponent<AudioSource>();

        // 스트리밍 AudioClip 생성
        int bufferSize = sampleRate * 10;
        var streamingClip = AudioClip.Create("TTSTextStream", bufferSize, 1, sampleRate, true, OnAudioRead);
        _audioSource.clip = streamingClip;
        _audioSource.loop = true;
    }

    private void Start()
    {
        _ = ConnectToServer();
    }

    private async Task ConnectToServer()
    {
        try
        {
            if (enableDebugLog) Debug.Log("[TTSTextStream] Connecting...");

            _cts = new CancellationTokenSource();
            _ws = new ClientWebSocket();

            await _ws.ConnectAsync(new Uri(serverUrl), _cts.Token);

            _isConnected = true;
            if (enableDebugLog) Debug.Log("[TTSTextStream] Connected");

            // 메시지 수신 시작
            _ = ReceiveMessages();

            OnConnected?.Invoke();
        }
        catch (Exception e)
        {
            Debug.LogError($"[TTSTextStream] Connection failed: {e.Message}");
            OnError?.Invoke($"Connection failed: {e.Message}");
        }
    }

    private async Task ReceiveMessages()
    {
        var buffer = new byte[8192];

        try
        {
            while (_ws?.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // PCM16 오디오 데이터
                    int bytes = result.Count;
                    int samples = bytes / 2;

                    float[] floatSamples = new float[samples];
                    for (int i = 0; i < samples; i++)
                    {
                        short pcm = (short)(buffer[i * 2] | (buffer[i * 2 + 1] << 8));
                        floatSamples[i] = pcm / 32768.0f;
                    }

                    // 버퍼에 추가
                    lock (_bufferLock)
                    {
                        foreach (var sample in floatSamples)
                        {
                            _audioBuffer.Enqueue(sample);
                        }
                    }

                    // 재생 시작 (충분한 버퍼)
                    if (!_audioSource.isPlaying && _audioBuffer.Count >= sampleRate * 0.1f)
                    {
                        _audioSource.Play();
                        if (enableDebugLog) Debug.Log("[TTSTextStream] Playback started");
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    if (enableDebugLog) Debug.Log($"[TTSTextStream] Message: {message}");

                    var data = JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, object>>(message);
                    var eventType = data.GetValueOrDefault("event")?.ToString();

                    if (eventType == "ready")
                    {
                        if (enableDebugLog) Debug.Log("[TTSTextStream] Server ready");
                    }
                    else if (eventType == "end")
                    {
                        if (enableDebugLog) Debug.Log("[TTSTextStream] Stream ended");
                        _streamEnded = true;
                    }
                    else if (eventType == "error")
                    {
                        var error = data.GetValueOrDefault("detail")?.ToString();
                        Debug.LogError($"[TTSTextStream] Server error: {error}");
                        OnError?.Invoke($"Server error: {error}");
                    }
                }
            }
        }
        catch (Exception e)
        {
            if (!_cts.Token.IsCancellationRequested)
            {
                Debug.LogError($"[TTSTextStream] Receive error: {e.Message}");
                OnError?.Invoke($"Receive error: {e.Message}");
            }
        }

        _isConnected = false;
        OnDisconnected?.Invoke();
    }

    /// <summary>
    /// 텍스트 청크 전송 (스트리밍)
    /// </summary>
    public async Task SendTextChunk(string textChunk)
    {
        if (_ws?.State != WebSocketState.Open || string.IsNullOrEmpty(textChunk))
            return;

        try
        {
            // 새로운 스트림 시작 - 플래그 리셋
            if (_streamEnded)
            {
                _streamEnded = false;
                _audioFinishedEventFired = false;
            }

            var message = JsonConvert.SerializeObject(new { type = "text", text = textChunk });
            var bytes = Encoding.UTF8.GetBytes(message);
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);

            if (enableDebugLog) Debug.Log($"[TTSTextStream] Sent text: '{textChunk}'");
        }
        catch (Exception e)
        {
            Debug.LogError($"[TTSTextStream] Send error: {e.Message}");
        }
    }

    /// <summary>
    /// 종료 신호 전송
    /// </summary>
    public async Task SendEndSignal()
    {
        if (_ws?.State != WebSocketState.Open) return;

        try
        {
            var message = JsonConvert.SerializeObject(new { type = "end" });
            var bytes = Encoding.UTF8.GetBytes(message);
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);

            if (enableDebugLog) Debug.Log("[TTSTextStream] Sent end signal");
        }
        catch (Exception e)
        {
            Debug.LogError($"[TTSTextStream] End signal error: {e.Message}");
        }
    }

    /// <summary>
    /// Speaker 변경
    /// </summary>
    public async Task SetSpeaker(int newSpeaker)
    {
        if (_ws?.State != WebSocketState.Open) return;

        try
        {
            speaker = newSpeaker;
            var message = JsonConvert.SerializeObject(new { type = "speaker", speaker = newSpeaker });
            var bytes = Encoding.UTF8.GetBytes(message);
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);

            if (enableDebugLog) Debug.Log($"[TTSTextStream] Speaker set to: {newSpeaker}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[TTSTextStream] Speaker change error: {e.Message}");
        }
    }

    private void OnAudioRead(float[] data)
    {
        lock (_bufferLock)
        {
            for (int i = 0; i < data.Length; i++)
            {
                if (_audioBuffer.Count > 0)
                {
                    data[i] = _audioBuffer.Dequeue();
                }
                else
                {
                    data[i] = 0f;
                }
            }
        }
    }

    private void Update()
    {
        // 스트림이 끝났고, 버퍼가 비어있으면 오디오 재생 완료
        if (_streamEnded && !_audioFinishedEventFired)
        {
            int bufferCount;
            lock (_bufferLock)
            {
                bufferCount = _audioBuffer.Count;
            }

            // 버퍼가 비어있으면 재생 완료로 간주
            if (bufferCount == 0)
            {
                _audioFinishedEventFired = true;
                if (enableDebugLog) Debug.Log("[TTSTextStream] Audio playback finished");
                OnAudioFinished?.Invoke();
            }
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

            if (enableDebugLog) Debug.Log("[TTSTextStream] Disconnected");
        }
        catch (Exception e)
        {
            Debug.LogError($"[TTSTextStream] Disconnect error: {e.Message}");
        }
    }

    public bool IsConnected => _isConnected;

    private void OnDestroy()
    {
        _ = Disconnect();
        _cts?.Dispose();
        _ws?.Dispose();

        lock (_bufferLock)
        {
            _audioBuffer?.Clear();
        }
    }
}