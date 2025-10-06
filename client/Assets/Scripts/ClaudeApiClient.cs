// Assets/ClaudeApiClient.cs
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

[System.Serializable]
public class ClaudeRequest
{
    public string model = "claude-sonnet-4-5-20250929";
    public int max_tokens = 1000;
    public ClaudeMessage[] messages;
    public string system;
}

[System.Serializable]
public class ClaudeMessage
{
    public string role;
    public string content;

    public ClaudeMessage(string role, string content)
    {
        this.role = role;
        this.content = content;
    }
}

[System.Serializable]
public class ClaudeResponse
{
    public string id;
    public string type;
    public string role;
    public ClaudeContent[] content;
    public string model;
    public string stop_reason;
    public ClaudeUsage usage;
}

[System.Serializable]
public class ClaudeContent
{
    public string type;
    public string text;
}

[System.Serializable]
public class ClaudeUsage
{
    public int input_tokens;
    public int output_tokens;
}

public class ClaudeApiClient : MonoBehaviour
{
    [Header("API Settings")]
    [Tooltip("Claude API Key (sk-ant-...)")]
    public string claudeApiKey = "YOUR_CLAUDE_API_KEY_HERE";

    [Header("Chat Settings")]
    [TextArea(3, 5)]
    public string systemPrompt = "당신은 친근하고 도움이 되는 AI 어시스턴트입니다. 한국어로 자연스럽고 간결하게 대답해주세요.";

    public int maxTokens = 1000;

    [Header("Debug")]
    public bool enableDebugLog = true;

    private HttpClient _httpClient;
    private List<ClaudeMessage> _conversationHistory;

    // Events
    public event Action<string> OnResponseReceived;

    public event Action<string> OnError;

    public event Action OnRequestStarted;

    public event Action OnRequestCompleted;

    private void Awake()
    {
        _httpClient = new HttpClient();
        _conversationHistory = new List<ClaudeMessage>();

        // API 키 헤더 설정
        _httpClient.DefaultRequestHeaders.Clear();
        if (!string.IsNullOrEmpty(claudeApiKey) && claudeApiKey != "YOUR_CLAUDE_API_KEY_HERE")
        {
            _httpClient.DefaultRequestHeaders.Add("x-api-key", claudeApiKey);
            _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        }
    }

    private void Start()
    {
        ValidateApiKey();
    }

    private void ValidateApiKey()
    {
        if (enableDebugLog)
        {
            if (string.IsNullOrEmpty(claudeApiKey) || claudeApiKey == "YOUR_CLAUDE_API_KEY_HERE")
            {
                Debug.LogWarning("[Claude] API key not set!");
            }
            else if (claudeApiKey.StartsWith("sk-ant-"))
            {
                Debug.Log($"[Claude] Using Claude API Key: {claudeApiKey.Substring(0, 12)}...");
            }
            else
            {
                Debug.LogWarning("[Claude] Claude API key format seems invalid. Expected: sk-ant-...");
            }
        }
    }

    public async Task<string> SendMessage(string userMessage, bool addToHistory = true)
    {
        if (string.IsNullOrEmpty(userMessage))
        {
            OnError?.Invoke("Empty message");
            return null;
        }

        try
        {
            OnRequestStarted?.Invoke();

            if (enableDebugLog) Debug.Log($"[Claude] Sending message: {userMessage}");

            // 대화 기록에 사용자 메시지 추가
            if (addToHistory)
            {
                _conversationHistory.Add(new ClaudeMessage("user", userMessage));
            }

            // 요청 생성
            var request = new ClaudeRequest
            {
                max_tokens = maxTokens,
                messages = _conversationHistory.ToArray(),
                system = systemPrompt
            };

            var json = JsonConvert.SerializeObject(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            if (enableDebugLog) Debug.Log($"[Claude] Request: {json.Substring(0, Math.Min(300, json.Length))}...");

            var response = await _httpClient.PostAsync("https://api.anthropic.com/v1/messages", content);

            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync();
                if (enableDebugLog) Debug.Log($"[Claude] Response: {responseJson}");

                var claudeResponse = JsonConvert.DeserializeObject<ClaudeResponse>(responseJson);

                if (claudeResponse.content != null && claudeResponse.content.Length > 0)
                {
                    string assistantMessage = claudeResponse.content[0].text;

                    // 대화 기록에 어시스턴트 응답 추가
                    if (addToHistory)
                    {
                        _conversationHistory.Add(new ClaudeMessage("assistant", assistantMessage));
                    }

                    if (enableDebugLog) Debug.Log($"[Claude] Assistant response: {assistantMessage}");

                    OnResponseReceived?.Invoke(assistantMessage);
                    OnRequestCompleted?.Invoke();

                    return assistantMessage;
                }
                else
                {
                    throw new Exception("No content in Claude response");
                }
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new Exception($"Claude API failed ({response.StatusCode}): {error}");
            }
        }
        catch (Exception e)
        {
            string errorMessage = $"Claude API error: {e.Message}";
            if (enableDebugLog) Debug.LogError($"[Claude] {errorMessage}");
            OnError?.Invoke(errorMessage);
            OnRequestCompleted?.Invoke();
            return null;
        }
    }

    public void ClearConversationHistory()
    {
        _conversationHistory.Clear();
        if (enableDebugLog) Debug.Log("[Claude] Conversation history cleared");
    }

    public void SetSystemPrompt(string newSystemPrompt)
    {
        systemPrompt = newSystemPrompt;
        if (enableDebugLog) Debug.Log($"[Claude] System prompt updated: {newSystemPrompt}");
    }

    public void SetApiKey(string newApiKey)
    {
        claudeApiKey = newApiKey;
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("x-api-key", claudeApiKey);
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        if (enableDebugLog) Debug.Log("[Claude] API key updated");
    }

    // 대화 기록 관리
    public int GetConversationLength()
    {
        return _conversationHistory.Count;
    }

    public void TrimConversationHistory(int maxMessages = 20)
    {
        if (_conversationHistory.Count > maxMessages)
        {
            int toRemove = _conversationHistory.Count - maxMessages;
            _conversationHistory.RemoveRange(0, toRemove);

            if (enableDebugLog) Debug.Log($"[Claude] Trimmed {toRemove} messages from history");
        }
    }

    public List<ClaudeMessage> GetConversationHistory()
    {
        return new List<ClaudeMessage>(_conversationHistory);
    }

    private void OnDestroy()
    {
        _httpClient?.Dispose();
    }

    // 공개 유틸리티 메서드들
    public bool IsApiKeyValid()
    {
        return !string.IsNullOrEmpty(claudeApiKey) &&
               claudeApiKey != "YOUR_CLAUDE_API_KEY_HERE" &&
               claudeApiKey.StartsWith("sk-ant-");
    }
}