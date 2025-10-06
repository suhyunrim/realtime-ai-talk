# OpenAI Realtime API 통합 가이드

## 🚀 OpenAI Realtime API 특징

### 1. 기존 방식 vs Realtime API 비교

| 구분 | 기존 방식 | OpenAI Realtime API |
|------|----------|---------------------|
| **구조** | STT → Claude → TTS | 실시간 WebSocket 연결 |
| **지연시간** | 3-8초 | 0.5-2초 |
| **API 호출** | 3번 (Whisper + Claude + TTS) | 1번 (Realtime) |
| **대화 흐름** | 순차적 처리 | 실시간 스트리밍 |
| **중단 가능** | 불가 | 가능 (사용자가 말하면 AI 중단) |

### 2. Realtime API 장점

- **빠른 응답**: 실시간 음성 활동 감지 (VAD)
- **자연스러운 대화**: 중간에 끼어들기 가능
- **단일 연결**: WebSocket 하나로 모든 처리
- **멀티모달**: 음성과 텍스트 동시 지원

## 🔧 구현 아키텍처

### 기존 파이프라인
```
사용자 음성 → VoiceToTextClient → ClaudeApiClient → TTSStreamClient → 사용자
     ↓              ↓                    ↓               ↓
  Whisper API    Claude API        VOICEVOX+RVC      오디오 재생
```

### Realtime API 파이프라인
```
사용자 음성 → OpenAIRealtimeClient → 사용자
     ↓              ↓
  WebSocket      OpenAI GPT-4o + TTS
```

## 📁 새로 추가된 파일들

### 1. `OpenAIRealtimeClient.cs`
- OpenAI Realtime API WebSocket 클라이언트
- 실시간 음성 스트리밍 및 응답 처리
- VAD (Voice Activity Detection) 지원

### 2. `RealtimeVoiceChatController.cs`
- Realtime API 전용 UI 컨트롤러
- 기존 TTS와 OpenAI 오디오 선택 가능
- 실시간 대화 흐름 관리

### 3. `VoiceChatController.cs` (업데이트)
- Traditional/Realtime 모드 전환 지원
- 단일 UI로 두 방식 모두 사용 가능

## ⚙️ 설정 및 사용법

### 1. API 키 설정
```csharp
// OpenAIRealtimeClient에서
public string openaiApiKey = "sk-your-api-key-here";
```

### 2. 모드 선택
```csharp
// VoiceChatController에서
public ChatMode chatMode = ChatMode.Realtime; // 또는 Traditional
```

### 3. 오디오 출력 모드
```csharp
// RealtimeVoiceChatController에서
public AudioOutputMode audioOutputMode = AudioOutputMode.RVCStreaming;

// 사용 가능한 모드:
// - OpenAIDirect: OpenAI 오디오 직접 재생
// - RVCStreaming: OpenAI → RVC 실시간 변환 (권장)
// - TraditionalTTS: 기존 VOICEVOX + RVC 파이프라인
```

## 🎵 오디오 처리 방식

### RVCStreaming 모드 (권장) ⭐
```
OpenAI Realtime → 실시간 오디오 스트림 → RVC 서버 → 변환된 음성
```
- **최고 성능**: VOICEVOX 생략으로 2-3초 단축
- **실시간 변환**: 100ms 청크로 스트리밍 처리
- **커스터마이징**: RVC 모델로 원하는 음성 변환

### OpenAIDirect 모드
```
OpenAI Realtime → 직접 재생
```
- **가장 빠름**: 추가 변환 없음
- **제한**: OpenAI 기본 음성만 사용 가능

### TraditionalTTS 모드
```
OpenAI Realtime (텍스트만) → VOICEVOX → RVC → 재생
```
- **호환성**: 기존 TTS 파이프라인 그대로 사용
- **가장 느림**: 전체 텍스트 완성 후 처리

## 📊 성능 최적화 설정

### 1. VAD (Voice Activity Detection) 설정
```csharp
public double vadThreshold = 0.5;        // 음성 감지 임계값
public int silenceDurationMs = 500;      // 침묵 감지 시간 (ms)
```

### 2. 오디오 품질 설정
```csharp
public int sampleRate = 24000;           // 24kHz (Realtime API 권장)
public string voice = "alloy";           // alloy, echo, fable, onyx, nova, shimmer
public double temperature = 0.8;         // 응답 창의성 (0.0-1.0)
```

### 3. 세션 설정
```csharp
public string instructions = "당신은 친근하고 도움이 되는 AI 어시스턴트입니다.";
public int max_response_output_tokens = 4096;
```

## 🔍 사용 시나리오

### 시나리오 1: 빠른 질답 (Realtime 추천)
- 간단한 질문과 답변
- 실시간 대화 느낌 중요
- 지연시간 최소화 필요

### 시나리오 2: 커스텀 음성 (Traditional 추천)
- 특정 캐릭터 음성 필요
- RVC 모델로 음성 변환
- 품질이 속도보다 중요

### 시나리오 3: 하이브리드
- Realtime으로 빠른 응답
- 중요한 답변만 기존 TTS로 재생성

## 🛠️ 디버그 및 모니터링

### 1. 연결 상태 확인
```csharp
bool isConnected = realtimeClient.IsConnected;
string sessionId = realtimeClient.SessionId;
```

### 2. 이벤트 로깅
```csharp
realtimeClient.OnTranscriptionReceived += transcript =>
    Debug.Log($"[User] {transcript}");
realtimeClient.OnTextResponseReceived += response =>
    Debug.Log($"[AI] {response}");
```

### 3. 오디오 데이터 모니터링
```csharp
realtimeClient.OnAudioResponseReceived += audioData =>
    Debug.Log($"[Audio] Received {audioData.Length} bytes");
```

## ⚠️ 주의사항

### 1. API 비용
- Realtime API는 기존 방식보다 비쌀 수 있음
- 실시간 연결 유지 비용 고려 필요

### 2. 네트워크 안정성
- WebSocket 연결이 끊어지면 대화 중단
- 자동 재연결 로직 필요

### 3. 브라우저 호환성
- Unity WebGL에서는 WebSocket 제한 있을 수 있음
- 데스크톱/모바일 앱에서 최적

## 🔮 향후 확장 가능성

### 1. 도구(Tools) 연동
```csharp
// 향후 OpenAI Function Calling 지원
public RealtimeTools tools; // 날씨, 검색 등
```

### 2. 멀티모달 확장
- 이미지 입력 지원
- 화면 공유 기능

### 3. 그룹 대화
- 다중 사용자 세션
- 화자 구분 기능

## 🎯 권장 구성

### 개발/테스트 환경
```csharp
chatMode = ChatMode.Traditional;
enableDebugLog = true;
```

### 프로덕션 환경
```csharp
chatMode = ChatMode.Realtime;
useOpenAIAudio = true;
enableDebugLog = false;
```

### 하이브리드 환경
```csharp
// UI에서 사용자가 모드 선택 가능
SetChatMode(userPreference);
```