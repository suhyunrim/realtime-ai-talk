// Assets/RVCStreamClient.cs
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

public class RVCStreamClient : MonoBehaviour
{
    [Header("Server Settings")]
    public string serverUrl = "ws://localhost:8000/ws/rvc";

    [Header("Audio Settings")]
    public bool autoPlayAudio = true;

    [Header("Debug")]
    public bool enableDebugLog = true;

    // Internal
    private ClientWebSocket _ws;
    private CancellationTokenSource _cts;
    private bool _isConnected = false;
    private AudioSource _audioSource;
    private Queue<float> _audioBuffer;  // float 샘플 버퍼로 변경
    private bool _isPlayingAudio = false;
    private int _audioSampleRate = 24000;
    private object _bufferLock = new object();

    // Events
    public event Action OnConnected;
    public event Action OnDisconnected;
    public event Action<string> OnError;
    public event Action<byte[]> OnAudioReceived;

    private void Awake()
    {
        _audioBuffer = new Queue<float>();

        // AudioSource 설정
        _audioSource = GetComponent<AudioSource>();
        if (_audioSource == null) _audioSource = gameObject.AddComponent<AudioSource>();

        // 스트리밍 AudioClip 생성 (10초 버퍼)
        int bufferSize = _audioSampleRate * 10;
        var streamingClip = AudioClip.Create("RVCStream", bufferSize, 1, _audioSampleRate, true, OnAudioRead);
        _audioSource.clip = streamingClip;
        _audioSource.loop = true;
    }

    private void Start()
    {
        _ = ConnectToRVCServer();
    }

    private async Task ConnectToRVCServer()
    {
        try
        {
            if (enableDebugLog) Debug.Log("[RVC] Connecting to RVC server...");

            _cts = new CancellationTokenSource();
            _ws = new ClientWebSocket();

            await _ws.ConnectAsync(new Uri(serverUrl), _cts.Token);

            _isConnected = true;
            if (enableDebugLog) Debug.Log("[RVC] Connected to RVC server");

            // 메시지 수신 시작
            _ = ReceiveMessages();

            OnConnected?.Invoke();
        }
        catch (Exception e)
        {
            Debug.LogError($"[RVC] Connection failed: {e.Message}");
            OnError?.Invoke($"Connection failed: {e.Message}");
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

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    // 텍스트 메시지 (제어 메시지)
                    messageBuffer.AddRange(buffer.Take(result.Count));

                    if (result.EndOfMessage)
                    {
                        var json = Encoding.UTF8.GetString(messageBuffer.ToArray());
                        await ProcessControlMessage(json);
                        messageBuffer.Clear();
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // 바이너리 메시지 (오디오 데이터)
                    messageBuffer.AddRange(buffer.Take(result.Count));

                    if (result.EndOfMessage)
                    {
                        var audioData = messageBuffer.ToArray();
                        ProcessAudioData(audioData);
                        messageBuffer.Clear();
                    }
                }
            }
        }
        catch (Exception e)
        {
            if (!_cts.Token.IsCancellationRequested)
            {
                Debug.LogError($"[RVC] Receive error: {e.Message}");
                OnError?.Invoke($"Receive error: {e.Message}");
            }
        }

        _isConnected = false;
        OnDisconnected?.Invoke();
    }

    private async Task ProcessControlMessage(string json)
    {
        try
        {
            if (enableDebugLog) Debug.Log($"[RVC] Control message: {json}");

            var message = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
            var eventType = message.GetValueOrDefault("event")?.ToString();

            switch (eventType)
            {
                case "ready":
                    if (enableDebugLog) Debug.Log("[RVC] Server ready");
                    break;

                case "end":
                    if (enableDebugLog) Debug.Log("[RVC] Stream ended");
                    break;

                case "error":
                    var error = message.GetValueOrDefault("detail")?.ToString();
                    Debug.LogError($"[RVC] Server error: {error}");
                    OnError?.Invoke($"Server error: {error}");
                    break;
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[RVC] Control message processing error: {e.Message}");
        }
    }

    private void ProcessAudioData(byte[] audioData)
    {
        if (audioData == null || audioData.Length == 0) return;

        // PCM16을 float로 변환하여 버퍼에 추가
        int sampleCount = audioData.Length / 2;
        float[] samples = new float[sampleCount];

        float maxSample = 0f;
        for (int i = 0; i < sampleCount; i++)
        {
            short pcm = (short)(audioData[i * 2] | (audioData[i * 2 + 1] << 8));
            samples[i] = pcm / 32768.0f;
            samples[i] = Mathf.Clamp(samples[i], -1f, 1f);
            if (Mathf.Abs(samples[i]) > maxSample) maxSample = Mathf.Abs(samples[i]);
        }

        if (enableDebugLog)
        {
            Debug.Log($"[RVC] Received {audioData.Length} bytes ({sampleCount} samples), max={maxSample:F3}");
        }

        // 버퍼에 샘플 추가
        lock (_bufferLock)
        {
            foreach (var sample in samples)
            {
                _audioBuffer.Enqueue(sample);
            }
        }

        OnAudioReceived?.Invoke(audioData);

        // 재생 시작 (충분한 버퍼가 쌓이면)
        if (autoPlayAudio && !_audioSource.isPlaying && _audioBuffer.Count >= _audioSampleRate * 0.1f) // 100ms 프리버퍼
        {
            _audioSource.Play();
            if (enableDebugLog) Debug.Log("[RVC] Audio playback started");
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
                    data[i] = 0f; // 버퍼 언더런 시 무음 출력
                }
            }
        }
    }

    public async Task SendAudioData(byte[] audioData)
    {
        if (_ws?.State != WebSocketState.Open || audioData == null || audioData.Length == 0)
            return;

        try
        {
            await _ws.SendAsync(new ArraySegment<byte>(audioData), WebSocketMessageType.Binary, true, _cts.Token);

            if (enableDebugLog) Debug.Log($"[RVC] Sent {audioData.Length} bytes of audio");
        }
        catch (Exception e)
        {
            Debug.LogError($"[RVC] Audio send error: {e.Message}");
        }
    }

    public async Task SendEndSignal()
    {
        if (_ws?.State != WebSocketState.Open) return;

        try
        {
            var endMessage = JsonConvert.SerializeObject(new { type = "end" });
            var bytes = Encoding.UTF8.GetBytes(endMessage);
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);

            if (enableDebugLog) Debug.Log("[RVC] Sent end signal");
        }
        catch (Exception e)
        {
            Debug.LogError($"[RVC] End signal send error: {e.Message}");
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

            if (enableDebugLog) Debug.Log("[RVC] Disconnected");
        }
        catch (Exception e)
        {
            Debug.LogError($"[RVC] Disconnect error: {e.Message}");
        }
    }

    // Public properties
    public bool IsConnected => _isConnected;

    public void SetServerUrl(string newUrl)
    {
        serverUrl = newUrl;
        // 재연결 필요
        if (_isConnected)
        {
            _ = Disconnect();
            _ = ConnectToRVCServer();
        }
    }

    public void SetAudioSampleRate(int sampleRate)
    {
        _audioSampleRate = sampleRate;
    }

    private void OnDestroy()
    {
        _ = Disconnect();
        _cts?.Dispose();
        _ws?.Dispose();

        // 오디오 정리
        lock (_bufferLock)
        {
            _audioBuffer?.Clear();
        }
    }
}