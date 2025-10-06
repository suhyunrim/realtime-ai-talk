// Assets/WhisperSTTClient.cs
using UnityEngine;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using UnityEngine.Networking;
using Newtonsoft.Json;

/// <summary>
/// OpenAI Whisper API를 사용한 음성 인식 클라이언트
/// </summary>
public class WhisperSTTClient : MonoBehaviour
{
    [Header("API Settings")]
    public string apiKey = ""; // OpenAI API Key
    public string model = "whisper-1";
    public string apiUrl = "https://api.openai.com/v1/audio/transcriptions";
    public string language = "ko"; // 한국어

    [Header("Recording Settings")]
    public int sampleRate = 16000;
    public int maxRecordSeconds = 30;

    [Header("Debug")]
    public bool enableDebugLog = true;

    // Audio recording
    private AudioClip _recordingClip;
    private string _microphoneDevice;
    private bool _isRecording = false;
    private int _recordingStartPosition;
    private float _recordingStartTime;

    // Events
    public event Action OnRecordingStarted;
    public event Action OnRecordingStopped;
    public event Action<string> OnTranscriptionReceived;
    public event Action<string> OnError;

    private void Start()
    {
        // 기본 마이크 디바이스 설정
        if (Microphone.devices.Length > 0)
        {
            _microphoneDevice = Microphone.devices[0];
            if (enableDebugLog) Debug.Log($"[WhisperSTT] Using microphone: {_microphoneDevice}");
        }
        else
        {
            Debug.LogError("[WhisperSTT] No microphone device found!");
        }
    }

    /// <summary>
    /// 녹음 시작
    /// </summary>
    public void StartRecording()
    {
        if (_isRecording)
        {
            Debug.LogWarning("[WhisperSTT] Already recording");
            return;
        }

        if (string.IsNullOrEmpty(_microphoneDevice))
        {
            Debug.LogError("[WhisperSTT] No microphone device available");
            OnError?.Invoke("No microphone device available");
            return;
        }

        _recordingClip = Microphone.Start(_microphoneDevice, false, maxRecordSeconds, sampleRate);
        _recordingStartPosition = Microphone.GetPosition(_microphoneDevice);
        _recordingStartTime = Time.time;
        _isRecording = true;

        if (enableDebugLog) Debug.Log("[WhisperSTT] Recording started");
        OnRecordingStarted?.Invoke();
    }

    /// <summary>
    /// 녹음 중지 및 전사 요청
    /// </summary>
    public async Task StopRecordingAndTranscribe()
    {
        if (!_isRecording)
        {
            Debug.LogWarning("[WhisperSTT] Not recording");
            return;
        }

        _isRecording = false;
        int currentPosition = Microphone.GetPosition(_microphoneDevice);
        Microphone.End(_microphoneDevice);

        if (enableDebugLog) Debug.Log("[WhisperSTT] Recording stopped");
        OnRecordingStopped?.Invoke();

        // 녹음된 오디오 추출
        int recordedSamples = currentPosition - _recordingStartPosition;
        if (recordedSamples < 0)
        {
            recordedSamples += _recordingClip.samples;
        }

        if (recordedSamples < sampleRate * 0.5f) // 0.5초 미만
        {
            if (enableDebugLog) Debug.Log("[WhisperSTT] Recording too short, skipping transcription");
            return;
        }

        float[] samples = new float[recordedSamples * _recordingClip.channels];
        _recordingClip.GetData(samples, _recordingStartPosition);

        // WAV 파일로 변환
        byte[] wavData = ConvertToWav(samples, sampleRate, _recordingClip.channels);

        // Whisper API 호출
        await TranscribeAudio(wavData);
    }

    private async Task TranscribeAudio(byte[] wavData)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            Debug.LogError("[WhisperSTT] API Key is not set!");
            OnError?.Invoke("API Key is not set");
            return;
        }

        if (enableDebugLog) Debug.Log($"[WhisperSTT] Sending {wavData.Length} bytes to Whisper API");

        try
        {
            // multipart/form-data 생성
            var formData = new WWWForm();
            formData.AddBinaryData("file", wavData, "audio.wav", "audio/wav");
            formData.AddField("model", model);
            formData.AddField("language", language);

            using (UnityWebRequest request = UnityWebRequest.Post(apiUrl, formData))
            {
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}");

                var operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result == UnityWebRequest.Result.Success)
                {
                    string responseText = request.downloadHandler.text;
                    var response = JsonConvert.DeserializeObject<WhisperResponse>(responseText);

                    if (!string.IsNullOrEmpty(response.text))
                    {
                        if (enableDebugLog) Debug.Log($"[WhisperSTT] Transcription: {response.text}");
                        OnTranscriptionReceived?.Invoke(response.text);
                    }
                }
                else
                {
                    string error = $"{request.error}: {request.downloadHandler.text}";
                    Debug.LogError($"[WhisperSTT] Request failed: {error}");
                    OnError?.Invoke(error);
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[WhisperSTT] Error: {e.Message}");
            OnError?.Invoke(e.Message);
        }
    }

    private byte[] ConvertToWav(float[] samples, int sampleRate, int channels)
    {
        int sampleCount = samples.Length;
        int byteCount = sampleCount * 2; // 16-bit PCM

        byte[] wav = new byte[44 + byteCount];

        // WAV 헤더
        int pos = 0;

        // RIFF 청크
        Array.Copy(Encoding.ASCII.GetBytes("RIFF"), 0, wav, pos, 4); pos += 4;
        Array.Copy(BitConverter.GetBytes(36 + byteCount), 0, wav, pos, 4); pos += 4;
        Array.Copy(Encoding.ASCII.GetBytes("WAVE"), 0, wav, pos, 4); pos += 4;

        // fmt 청크
        Array.Copy(Encoding.ASCII.GetBytes("fmt "), 0, wav, pos, 4); pos += 4;
        Array.Copy(BitConverter.GetBytes(16), 0, wav, pos, 4); pos += 4; // fmt 청크 크기
        Array.Copy(BitConverter.GetBytes((short)1), 0, wav, pos, 2); pos += 2; // PCM
        Array.Copy(BitConverter.GetBytes((short)channels), 0, wav, pos, 2); pos += 2;
        Array.Copy(BitConverter.GetBytes(sampleRate), 0, wav, pos, 4); pos += 4;
        Array.Copy(BitConverter.GetBytes(sampleRate * channels * 2), 0, wav, pos, 4); pos += 4; // Byte rate
        Array.Copy(BitConverter.GetBytes((short)(channels * 2)), 0, wav, pos, 2); pos += 2; // Block align
        Array.Copy(BitConverter.GetBytes((short)16), 0, wav, pos, 2); pos += 2; // Bits per sample

        // data 청크
        Array.Copy(Encoding.ASCII.GetBytes("data"), 0, wav, pos, 4); pos += 4;
        Array.Copy(BitConverter.GetBytes(byteCount), 0, wav, pos, 4); pos += 4;

        // PCM 데이터
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = (short)(Mathf.Clamp(samples[i], -1f, 1f) * 32767f);
            Array.Copy(BitConverter.GetBytes(sample), 0, wav, pos, 2);
            pos += 2;
        }

        return wav;
    }

    public bool IsRecording => _isRecording;

    private void OnDestroy()
    {
        if (_isRecording)
        {
            Microphone.End(_microphoneDevice);
        }
    }
}

[Serializable]
public class WhisperResponse
{
    public string text;
}
