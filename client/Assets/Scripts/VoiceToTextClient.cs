// Assets/VoiceToTextClient.cs
using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

[System.Serializable]
public class SpeechRequest
{
    public SpeechConfig config;
    public SpeechAudio audio;
}

[System.Serializable]
public class SpeechAudio
{
    public string content; // Base64 encoded audio
}

[System.Serializable]
public class SpeechConfig
{
    public string encoding = "LINEAR16";
    public int sampleRateHertz = 16000;
    public string languageCode = "ko-KR";
    public bool enableAutomaticPunctuation = true;
    public string model = "latest_long";
}

[System.Serializable]
public class SpeechResponse
{
    public SpeechResult[] results;
}

[System.Serializable]
public class SpeechResult
{
    public SpeechAlternative[] alternatives;
}

[System.Serializable]
public class SpeechAlternative
{
    public string transcript;
    public float confidence;
}

public class VoiceToTextClient : MonoBehaviour
{
    [Header("Recording Settings")]
    public KeyCode recordKey = KeyCode.Space;

    public int recordingLength = 10; // seconds
    public int sampleRate = 16000; // 16kHz는 STT에 최적
    public int maxRecordingSeconds = 30; // 최대 녹음 시간 제한

    [Header("API Settings")]
    [Tooltip("Google Cloud API Key (AIza...) or Access Token (ya29...)")]
    public string googleCloudApiKey = "YOUR_API_KEY_HERE";

    [Tooltip("OpenAI API Key (sk-...)")]
    public string openAiApiKey = "YOUR_OPENAI_API_KEY_HERE";

    [Header("Service Selection")]
    public STTService sttService = STTService.OpenAI; // OpenAI Whisper가 일반적으로 더 빠름

    [Header("Debug")]
    public bool enableDebugLog = true;

    public enum STTService
    {
        GoogleCloud,
        OpenAI,
        AzureCognitive
    }

    private AudioClip _recordedClip;
    private bool _isRecording = false;
    private HttpClient _httpClient;

    // Events
    public event Action<string> OnTextRecognized;

    public event Action OnRecordingStarted;

    public event Action OnRecordingStopped;

    public event Action<string> OnError;

    private void Awake()
    {
        _httpClient = new HttpClient();
    }

    private void Start()
    {
        // 마이크 권한 확인
        if (!Microphone.IsRecording(null))
        {
            if (enableDebugLog) Debug.Log("[STT] Microphone ready");
        }

        // 사용 가능한 마이크 디바이스 출력
        if (enableDebugLog)
        {
            Debug.Log($"[STT] Available microphones: {string.Join(", ", Microphone.devices)}");
        }

        // API 키 유효성 확인
        ValidateApiKeys();
    }

    private void ValidateApiKeys()
    {
        if (enableDebugLog)
        {
            switch (sttService)
            {
                case STTService.GoogleCloud:
                    if (string.IsNullOrEmpty(googleCloudApiKey) || googleCloudApiKey == "YOUR_API_KEY_HERE")
                    {
                        Debug.LogWarning("[STT] Google Cloud API key not set!");
                    }
                    else if (googleCloudApiKey.StartsWith("AIza"))
                    {
                        Debug.Log($"[STT] Using Google Cloud API Key: {googleCloudApiKey.Substring(0, 8)}...");
                    }
                    else if (googleCloudApiKey.StartsWith("ya29."))
                    {
                        Debug.Log($"[STT] Using Google Cloud Access Token: {googleCloudApiKey.Substring(0, 8)}...");
                    }
                    else
                    {
                        Debug.LogWarning("[STT] Google Cloud API key format seems invalid. Expected: AIza... or ya29...");
                    }
                    break;

                case STTService.OpenAI:
                    if (string.IsNullOrEmpty(openAiApiKey) || openAiApiKey == "YOUR_OPENAI_API_KEY_HERE")
                    {
                        Debug.LogWarning("[STT] OpenAI API key not set!");
                    }
                    else if (openAiApiKey.StartsWith("sk-"))
                    {
                        Debug.Log($"[STT] Using OpenAI API Key: {openAiApiKey.Substring(0, 8)}...");
                    }
                    else
                    {
                        Debug.LogWarning("[STT] OpenAI API key format seems invalid. Expected: sk-...");
                    }
                    break;
            }
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
        if (_isRecording) return;

        _isRecording = true;
        OnRecordingStarted?.Invoke();

        if (enableDebugLog) Debug.Log("[STT] Recording started");

        // 마이크 녹음 시작 (16kHz, mono)
        _recordedClip = Microphone.Start(null, false, recordingLength, sampleRate);
    }

    public void StopRecording()
    {
        if (!_isRecording) return;

        _isRecording = false;
        OnRecordingStopped?.Invoke();

        if (enableDebugLog) Debug.Log("[STT] Recording stopped");

        Microphone.End(null);

        // 음성 인식 실행
        _ = ProcessRecordedAudio();
    }

    private async Task ProcessRecordedAudio()
    {
        if (_recordedClip == null)
        {
            OnError?.Invoke("No audio recorded");
            return;
        }

        try
        {
            // AudioClip을 WAV 바이트로 변환
            byte[] audioData = ConvertAudioClipToWav(_recordedClip);

            if (enableDebugLog) Debug.Log($"[STT] Audio data size: {audioData.Length} bytes");

            // 선택된 서비스로 음성 인식 실행
            string recognizedText = await RecognizeSpeech(audioData);

            if (!string.IsNullOrEmpty(recognizedText))
            {
                if (enableDebugLog) Debug.Log($"[STT] Recognized: {recognizedText}");
                OnTextRecognized?.Invoke(recognizedText);
            }
            else
            {
                OnError?.Invoke("No speech recognized");
            }
        }
        catch (Exception e)
        {
            OnError?.Invoke($"Recognition failed: {e.Message}");
            if (enableDebugLog) Debug.LogError($"[STT] Error: {e}");
        }
    }

    private async Task<string> RecognizeSpeech(byte[] audioData)
    {
        switch (sttService)
        {
            case STTService.GoogleCloud:
                return await RecognizeWithGoogleCloud(audioData);

            case STTService.OpenAI:
                return await RecognizeWithOpenAI(audioData);

            default:
                throw new NotImplementedException($"STT service {sttService} not implemented");
        }
    }

    private async Task<string> RecognizeWithGoogleCloud(byte[] audioData)
    {
        var config = new SpeechConfig();
        var request = new SpeechRequest
        {
            config = config,
            audio = new SpeechAudio
            {
                content = Convert.ToBase64String(audioData)
            }
        };

        var json = JsonConvert.SerializeObject(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // API 키 방식들 시도
        string url;
        HttpRequestMessage httpRequest;

        // 방식 1: URL 파라미터로 API 키 전달
        if (!string.IsNullOrEmpty(googleCloudApiKey) && !googleCloudApiKey.Contains(".json"))
        {
            url = $"https://speech.googleapis.com/v1/speech:recognize?key={googleCloudApiKey}";
            httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            httpRequest.Content = content;

            if (enableDebugLog) Debug.Log($"[STT] Using API key method: {url.Substring(0, Math.Min(80, url.Length))}...");
        }
        // 방식 2: Authorization 헤더 (Bearer 토큰)
        else if (!string.IsNullOrEmpty(googleCloudApiKey) && googleCloudApiKey.StartsWith("ya29."))
        {
            url = "https://speech.googleapis.com/v1/speech:recognize";
            httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            httpRequest.Headers.Add("Authorization", $"Bearer {googleCloudApiKey}");
            httpRequest.Content = content;

            if (enableDebugLog) Debug.Log("[STT] Using Bearer token method");
        }
        else
        {
            throw new Exception("Invalid API key format. Use either API key or access token.");
        }

        if (enableDebugLog) Debug.Log($"[STT] Request payload: {json.Substring(0, Math.Min(300, json.Length))}...");

        var response = await _httpClient.SendAsync(httpRequest);

        if (response.IsSuccessStatusCode)
        {
            var responseJson = await response.Content.ReadAsStringAsync();
            if (enableDebugLog) Debug.Log($"[STT] Google Cloud response: {responseJson}");

            var speechResponse = JsonConvert.DeserializeObject<SpeechResponse>(responseJson);

            if (speechResponse.results != null && speechResponse.results.Length > 0 &&
                speechResponse.results[0].alternatives != null && speechResponse.results[0].alternatives.Length > 0)
            {
                var alternative = speechResponse.results[0].alternatives[0];
                return alternative.transcript;
            }
            else
            {
                if (enableDebugLog) Debug.Log("[STT] No speech detected in audio");
                return null;
            }
        }
        else
        {
            var error = await response.Content.ReadAsStringAsync();
            if (enableDebugLog) Debug.LogError($"[STT] Response headers: {response.Headers}");
            throw new Exception($"Google Cloud STT failed ({response.StatusCode}): {error}");
        }
    }

    private async Task<string> RecognizeWithOpenAI(byte[] audioData)
    {
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent(audioData), "file", "audio.wav");
            form.Add(new StringContent("whisper-1"), "model");
            form.Add(new StringContent("ko"), "language");
            form.Add(new StringContent("json"), "response_format");
            form.Add(new StringContent("0.1"), "temperature"); // 낮은 temperature로 빠른 처리

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {openAiApiKey}");

            if (enableDebugLog) Debug.Log($"[STT] Sending {audioData.Length} bytes to OpenAI Whisper");

            var startTime = Time.realtimeSinceStartup;
            var response = await _httpClient.PostAsync("https://api.openai.com/v1/audio/transcriptions", form);
            var processingTime = Time.realtimeSinceStartup - startTime;

            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync();
                if (enableDebugLog) Debug.Log($"[STT] OpenAI response time: {processingTime:F2}s");

                var result = JsonConvert.DeserializeObject<Dictionary<string, object>>(responseJson);
                return result["text"].ToString();
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new Exception($"OpenAI Whisper failed ({response.StatusCode}): {error}");
            }
        }
    }

    private byte[] ConvertAudioClipToWav(AudioClip clip)
    {
        // AudioClip 데이터 추출
        float[] samples = new float[clip.samples * clip.channels];
        clip.GetData(samples, 0);

        // 실제로 녹음된 부분만 추출 (마이크 녹음 중 실제 길이)
        int actualSamples = clip.samples;
        if (Microphone.IsRecording(null))
        {
            actualSamples = Microphone.GetPosition(null);
        }

        // 실제 샘플만 자르기
        if (actualSamples < samples.Length)
        {
            float[] trimmedSamples = new float[actualSamples * clip.channels];
            Array.Copy(samples, trimmedSamples, trimmedSamples.Length);
            samples = trimmedSamples;
        }

        // 침묵 제거 (시작과 끝)
        samples = TrimSilence(samples, 0.01f); // 1% 임계값

        if (enableDebugLog) Debug.Log($"[STT] Audio: {clip.frequency}Hz, {clip.channels}ch, {samples.Length} samples");

        // 16-bit PCM으로 변환
        short[] intData = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            float sample = Mathf.Clamp(samples[i], -1f, 1f);
            intData[i] = (short)(sample * 32767);
        }

        // WAV 헤더 생성
        int hz = clip.frequency;
        int channels = clip.channels;
        int dataSize = intData.Length * 2;

        using (var stream = new System.IO.MemoryStream())
        using (var writer = new System.IO.BinaryWriter(stream))
        {
            // RIFF 헤더
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

            // fmt 청크
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16); // fmt chunk size
            writer.Write((ushort)1); // PCM format
            writer.Write((ushort)channels);
            writer.Write(hz);
            writer.Write(hz * channels * 2); // byte rate
            writer.Write((ushort)(channels * 2)); // block align
            writer.Write((ushort)16); // bits per sample

            // data 청크
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);

            // 오디오 데이터 작성 (리틀 엔디안)
            foreach (short sample in intData)
            {
                writer.Write(sample);
            }

            return stream.ToArray();
        }
    }

    private void OnDestroy()
    {
        _httpClient?.Dispose();

        if (Microphone.IsRecording(null))
        {
            Microphone.End(null);
        }
    }

    // 헬퍼 메서드들
    private float[] TrimSilence(float[] samples, float threshold)
    {
        int start = 0;
        int end = samples.Length - 1;

        // 시작 침묵 찾기
        for (int i = 0; i < samples.Length; i++)
        {
            if (Mathf.Abs(samples[i]) > threshold)
            {
                start = i;
                break;
            }
        }

        // 끝 침묵 찾기
        for (int i = samples.Length - 1; i >= 0; i--)
        {
            if (Mathf.Abs(samples[i]) > threshold)
            {
                end = i;
                break;
            }
        }

        // 유효한 오디오가 있는지 확인
        if (start >= end)
        {
            return new float[0]; // 모든 샘플이 침묵
        }

        // 트림된 샘플 반환
        int trimmedLength = end - start + 1;
        float[] trimmed = new float[trimmedLength];
        Array.Copy(samples, start, trimmed, 0, trimmedLength);

        if (enableDebugLog) Debug.Log($"[STT] Trimmed silence: {samples.Length} → {trimmed.Length} samples");

        return trimmed;
    }

    // 공개 메서드들
    public bool IsRecording => _isRecording;

    public void SetApiKey(STTService service, string apiKey)
    {
        switch (service)
        {
            case STTService.GoogleCloud:
                googleCloudApiKey = apiKey;
                break;

            case STTService.OpenAI:
                openAiApiKey = apiKey;
                break;
        }
    }
}