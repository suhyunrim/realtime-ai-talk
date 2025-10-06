// Assets/TTSStreamClient.cs
using UnityEngine;
using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;

[RequireComponent(typeof(AudioSource))]
public class TTSStreamClient : MonoBehaviour
{
    [Header("Server")]
    public string WsUrl = "ws://127.0.0.1:8000/ws/tts";

    public int Speaker = 2;

    [Header("Audio")]
    public int SampleRate = 24000; // 서버와 동일

    public int Channels = 1;
    public int PrebufferMs = 120;  // 시작 전에 모을 버퍼(언더런 방지)

    [Header("Debug")]
    public bool EnableDebugLog = false;

    private ClientWebSocket _ws;
    private CancellationTokenSource _cts;
    private AudioSource _src;

    // 간단 원형 버퍼 (락 최소화)
    private float[] _ring;

    private int _rWrite = 0, _rRead = 0;
    private volatile int _ringCount = 0;
    private object _ringLock = new object();
    private int _prebufferSamples;
    private bool _isConnected = false;

    private void Awake()
    {
        _prebufferSamples = (SampleRate * PrebufferMs) / 1000;
        _ring = new float[SampleRate * 10]; // 10초 버퍼

        _src = GetComponent<AudioSource>();
        _src.clip = AudioClip.Create("RemoteAudio", SampleRate * 10, Channels, SampleRate, true, OnAudioRead);
        _src.loop = true;
    }

    private async void Start()
    {
        await ConnectWebSocket();
    }

    public void TestSpeak()
    {
        Speak("それでは他の言葉でテストしてみよう", 2);
    }

    private async Task ConnectWebSocket()
    {
        try
        {
            _cts = new CancellationTokenSource();
            _ws = new ClientWebSocket();

            if (EnableDebugLog) Debug.Log($"[TTS] Connecting to: {WsUrl}");
            await _ws.ConnectAsync(new Uri(WsUrl), _cts.Token);
            _isConnected = true;
            if (EnableDebugLog) Debug.Log("[TTS] WebSocket connected and ready");

            // 수신 루프 시작
            _ = Task.Run(() => ReceiverLoop(_cts.Token));
        }
        catch (Exception e)
        {
            Debug.LogError($"[TTS] Connection failed: {e.Message}");
            _isConnected = false;
        }
    }

    public async void Speak(string text, int speaker = 2)
    {
        if (EnableDebugLog) Debug.Log($"[TTS] Speak: {text} (speaker: {speaker})");

        // 연결되어 있지 않으면 재연결
        if (!_isConnected || _ws == null || _ws.State != WebSocketState.Open)
        {
            await ConnectWebSocket();
        }

        if (!_isConnected)
        {
            Debug.LogError("[TTS] Cannot speak: WebSocket not connected");
            return;
        }

        // 버퍼 초기화 및 재생 시작
        _rRead = _rWrite = _ringCount = 0;
        _src.Play();

        // JSON 메시지로 텍스트 전송
        var message = $"{{\"text\":\"{Escape(text)}\",\"speaker\":{speaker}}}";
        var bytes = Encoding.UTF8.GetBytes(message);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);

        if (EnableDebugLog) Debug.Log($"[TTS] Sent text message: {message}");
    }

    public void StopAudio()
    {
        _src.Stop();
        _rRead = _rWrite = _ringCount = 0;
    }

    private async Task DisconnectWebSocket()
    {
        try
        {
            _isConnected = false;
            if (_ws != null && _ws.State == WebSocketState.Open)
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "stop", CancellationToken.None);
            }
        }
        catch { /* ignore */ }
        if (_cts != null) _cts.Cancel();
        _ws = null;
    }

    private async Task ReceiverLoop(CancellationToken ct)
    {
        var buf = new byte[4096]; // 20ms frame: 960 bytes @ 24k, mono, 16-bit
        int totalBytesReceived = 0;
        try
        {
            while (!ct.IsCancellationRequested && _ws != null && _ws.State == WebSocketState.Open)
            {
                var res = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                if (res.MessageType == WebSocketMessageType.Close) break;

                if (res.MessageType == WebSocketMessageType.Binary)
                {
                    // PCM16 → float
                    int bytes = res.Count;
                    int samples = bytes / 2;
                    totalBytesReceived += bytes;

                    if (EnableDebugLog && totalBytesReceived % 50000 < bytes) // Log every ~50KB (reduce frequency)
                        Debug.Log($"[TTS] Audio data: {totalBytesReceived} bytes total");

                    PushPcm16(buf, samples);
                }
                else if (res.MessageType == WebSocketMessageType.Text)
                {
                    string message = Encoding.UTF8.GetString(buf, 0, res.Count);
                    if (EnableDebugLog) Debug.Log($"[TTS] Text message: {message}");

                    // {"event":"end"} 수신 시 그대로 종료 가능
                    if (message.Contains("\"event\":\"end\""))
                    {
                        if (EnableDebugLog) Debug.Log("[TTS] Received end event");
                        break;
                    }
                }
            }
        }
        catch (Exception e)
        {
            if (EnableDebugLog) Debug.LogError($"[TTS] ReceiverLoop error: {e.Message}");
        }
        finally
        {
            if (EnableDebugLog) Debug.Log($"[TTS] ReceiverLoop ended. Total bytes received: {totalBytesReceived}");
        }
    }

    private void PushPcm16(byte[] bytes, int samples)
    {
        // LE 16-bit → float [-1,1]
        lock (_ringLock)
        {
            int maxSamples = Mathf.Min(samples, bytes.Length / 2);
            for (int i = 0; i < maxSamples; i++)
            {
                int idx = i * 2;
                if (idx + 1 >= bytes.Length) break;

                short s = (short)(bytes[idx] | (bytes[idx + 1] << 8));
                float v = s / 32767f;
                v = Mathf.Clamp(v, -1f, 1f);

                _ring[_rWrite] = v;
                _rWrite = (_rWrite + 1) % _ring.Length;
                if (_ringCount < _ring.Length) _ringCount++;
                else _rRead = (_rRead + 1) % _ring.Length; // overwrite oldest
            }
        }
    }

    private void OnAudioRead(float[] data)
    {
        int need = data.Length;

        lock (_ringLock)
        {
            // 프리버퍼 조건을 완화: 최소 일부 데이터만 있으면 재생 시작
            bool hasMinData = _ringCount >= Mathf.Min(480, _prebufferSamples); // 20ms @ 24kHz = 480 samples

            for (int i = 0; i < need; i++)
            {
                if (_ringCount > 0 && (hasMinData || _ringCount > need - i))
                {
                    data[i] = _ring[_rRead];
                    _rRead = (_rRead + 1) % _ring.Length;
                    _ringCount--;
                }
                else
                {
                    data[i] = 0f;
                }
            }
        }
    }

    private void OnDisable()
    {
        StopAudio();
        _ = DisconnectWebSocket();
    }

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}