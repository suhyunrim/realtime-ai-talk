// Assets/OpenAIChatStreamClient.cs
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;
using Newtonsoft.Json;

/// <summary>
/// OpenAI Chat Completions API (스트리밍) 클라이언트
/// Realtime API보다 빠르고 텍스트만 생성
/// </summary>
public class OpenAIChatStreamClient : MonoBehaviour
{
    [Header("API Settings")]
    public string apiKey = ""; // OpenAI API Key
    public string model = "gpt-4o"; // gpt-4o, gpt-4-turbo, gpt-3.5-turbo
    public string apiUrl = "https://api.openai.com/v1/chat/completions";

    [Header("Chat Settings")]
    [TextArea(3, 5)]
    public string systemPrompt = "당신은 친근하고 도움이 되는 AI 어시스턴트입니다. 한국어로 자연스럽고 간결하게 대답해주세요.";
    public float temperature = 0.8f;
    public int maxTokens = 4096;

    [Header("Debug")]
    public bool enableDebugLog = true;

    // 대화 히스토리
    private List<ChatMessage> _conversationHistory = new List<ChatMessage>();
    private CancellationTokenSource _cts;
    private bool _isStreaming = false;

    // Events
    public event Action<string> OnTextDelta; // 텍스트 청크 스트리밍
    public event Action<string> OnResponseComplete; // 응답 완료 (전체 텍스트)
    public event Action OnStreamEnd; // 스트리밍 종료
    public event Action<string> OnError;

    private void Awake()
    {
        // 시스템 프롬프트 추가
        _conversationHistory.Add(new ChatMessage
        {
            role = "system",
            content = systemPrompt
        });
    }

    /// <summary>
    /// 메시지 전송 및 스트리밍 응답 수신
    /// </summary>
    public async Task SendMessage(string userMessage)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            Debug.LogError("[ChatStream] API Key is not set!");
            OnError?.Invoke("API Key is not set");
            return;
        }

        if (_isStreaming)
        {
            Debug.LogWarning("[ChatStream] Already streaming, cancelling previous request");
            _cts?.Cancel();
        }

        _isStreaming = true;
        _cts = new CancellationTokenSource();

        // 사용자 메시지 추가
        _conversationHistory.Add(new ChatMessage
        {
            role = "user",
            content = userMessage
        });

        if (enableDebugLog) Debug.Log($"[ChatStream] Sending: {userMessage}");

        try
        {
            await StreamChatCompletion(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (enableDebugLog) Debug.Log("[ChatStream] Request cancelled");
        }
        catch (Exception e)
        {
            Debug.LogError($"[ChatStream] Error: {e.Message}");
            OnError?.Invoke(e.Message);
        }
        finally
        {
            _isStreaming = false;
        }
    }

    private async Task StreamChatCompletion(CancellationToken cancellationToken)
    {
        var requestBody = new ChatCompletionRequest
        {
            model = model,
            messages = _conversationHistory.ToArray(),
            temperature = temperature,
            max_tokens = maxTokens,
            stream = true
        };

        string jsonBody = JsonConvert.SerializeObject(requestBody);
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);

        using (UnityWebRequest request = new UnityWebRequest(apiUrl, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", $"Bearer {apiKey}");

            var operation = request.SendWebRequest();

            StringBuilder fullResponse = new StringBuilder();
            StringBuilder buffer = new StringBuilder();

            while (!operation.isDone && !cancellationToken.IsCancellationRequested)
            {
                await Task.Yield();

                // SSE 스트림 파싱
                if (request.downloadHandler.data != null && request.downloadHandler.data.Length > 0)
                {
                    string chunk = Encoding.UTF8.GetString(request.downloadHandler.data);
                    buffer.Append(chunk);

                    string[] lines = buffer.ToString().Split(new[] { "\n" }, StringSplitOptions.None);

                    // 마지막 라인은 불완전할 수 있으므로 버퍼에 유지
                    for (int i = 0; i < lines.Length - 1; i++)
                    {
                        ProcessSSELine(lines[i], fullResponse);
                    }

                    buffer.Clear();
                    buffer.Append(lines[lines.Length - 1]);
                }
            }

            // 마지막 버퍼 처리
            if (buffer.Length > 0)
            {
                string[] lines = buffer.ToString().Split('\n');
                foreach (var line in lines)
                {
                    ProcessSSELine(line, fullResponse);
                }
            }

            if (request.result == UnityWebRequest.Result.Success || request.isDone)
            {
                string completeResponse = fullResponse.ToString();

                // 대화 히스토리에 추가
                _conversationHistory.Add(new ChatMessage
                {
                    role = "assistant",
                    content = completeResponse
                });

                if (enableDebugLog) Debug.Log($"[ChatStream] Complete response: {completeResponse}");
                OnResponseComplete?.Invoke(completeResponse);
                OnStreamEnd?.Invoke();
            }
            else
            {
                Debug.LogError($"[ChatStream] Request failed: {request.error}");
                OnError?.Invoke(request.error);
            }
        }
    }

    private void ProcessSSELine(string line, StringBuilder fullResponse)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (!line.StartsWith("data: ")) return;

        string data = line.Substring(6).Trim();
        if (data == "[DONE]") return;

        try
        {
            var chunk = JsonConvert.DeserializeObject<ChatCompletionChunk>(data);
            if (chunk?.choices != null && chunk.choices.Length > 0)
            {
                var delta = chunk.choices[0].delta;
                if (!string.IsNullOrEmpty(delta.content))
                {
                    fullResponse.Append(delta.content);
                    OnTextDelta?.Invoke(delta.content);
                }
            }
        }
        catch (Exception e)
        {
            if (enableDebugLog) Debug.LogWarning($"[ChatStream] Parse error: {e.Message}");
        }
    }

    /// <summary>
    /// 대화 히스토리 초기화
    /// </summary>
    public void ClearHistory()
    {
        _conversationHistory.Clear();
        _conversationHistory.Add(new ChatMessage
        {
            role = "system",
            content = systemPrompt
        });

        if (enableDebugLog) Debug.Log("[ChatStream] History cleared");
    }

    /// <summary>
    /// 스트리밍 취소
    /// </summary>
    public void CancelStream()
    {
        _cts?.Cancel();
    }

    public bool IsStreaming => _isStreaming;

    private void OnDestroy()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

// Data classes
[Serializable]
public class ChatMessage
{
    public string role;
    public string content;
}

[Serializable]
public class ChatCompletionRequest
{
    public string model;
    public ChatMessage[] messages;
    public float temperature;
    public int max_tokens;
    public bool stream;
}

[Serializable]
public class ChatCompletionChunk
{
    public string id;
    public string @object;
    public long created;
    public string model;
    public Choice[] choices;
}

[Serializable]
public class Choice
{
    public int index;
    public Delta delta;
    public string finish_reason;
}

[Serializable]
public class Delta
{
    public string role;
    public string content;
}
