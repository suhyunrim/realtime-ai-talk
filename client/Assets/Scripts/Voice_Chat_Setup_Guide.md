# Unity 음성 대화 시스템 설정 가이드

## 🎯 시스템 개요

음성 → 텍스트 → Claude AI → 텍스트 → 음성의 완전한 대화 시스템

## 📋 필요한 API 키들

### 1. Google Cloud Speech-to-Text API 키
1. [Google Cloud Console](https://console.cloud.google.com/) 접속
2. 새 프로젝트 생성 또는 기존 프로젝트 선택
3. Speech-to-Text API 활성화
4. 사용자 인증 정보 → API 키 생성
5. 형식: `AIza...`로 시작

### 2. Claude API 키
1. [Anthropic Console](https://console.anthropic.com/) 접속
2. API 키 생성
3. 형식: `sk-ant-...`로 시작

### 3. OpenAI API 키 (선택사항)
1. [OpenAI Platform](https://platform.openai.com/) 접속
2. API 키 생성
3. 형식: `sk-...`로 시작

## 🔧 Unity 설정

### 1. 패키지 설치
Unity Package Manager에서 설치:
- **Newtonsoft.Json** (필수)

### 2. 컴포넌트 설정

#### 기본 설정 (자동 구성)
1. 빈 GameObject 생성 (예: "VoiceChatSystem")
2. `VoiceChatController` 스크립트 추가
3. Inspector에서 API 키들 입력:
   - Google Cloud API Key
   - Claude API Key
   - (선택) OpenAI API Key

#### 고급 설정 (수동 구성)
각 컴포넌트를 개별적으로 설정하려면:

**VoiceToTextClient 설정:**
```
- Recording Key: Space (기본)
- Sample Rate: 16000
- STT Service: GoogleCloud 또는 OpenAI
- API 키 입력
```

**ClaudeApiClient 설정:**
```
- Claude API Key: sk-ant-...
- System Prompt: AI 성격 설정
- Max Tokens: 1000 (기본)
```

**TTSStreamClient 설정:**
```
- Server URL: ws://127.0.0.1:8000/ws/tts
- Speaker: 2 (기본)
- Sample Rate: 24000
```

### 3. UI 설정 (선택사항)

Canvas에 다음 UI 요소들 추가:

```
Canvas
├── StatusText (Text) - 현재 상태 표시
├── UserText (Text) - 사용자 음성 인식 결과
├── AssistantText (Text) - Claude 응답
├── RecordButton (Button) - 녹음 버튼
└── ClearHistoryButton (Button) - 대화 기록 삭제
```

`VoiceChatController`의 Inspector에서 UI 요소들을 연결

## 🚀 사용법

### 키보드 사용
1. **Space 키**를 누르고 말하기
2. 키를 떼면 음성 인식 시작
3. Claude가 응답하면 자동으로 TTS로 재생

### 버튼 사용
1. **Start Recording** 버튼 클릭
2. 말하기
3. **Stop Recording** 버튼 클릭

### 스크립트에서 사용
```csharp
// 컴포넌트 참조
VoiceChatController chatController = FindObjectOfType<VoiceChatController>();

// 설정 변경
chatController.SetAutoPlayResponse(false); // 자동 재생 끄기
chatController.ClearConversationHistory(); // 대화 기록 삭제
chatController.PlayLastResponse(); // 마지막 응답 다시 재생
```

## 🎛️ 시스템 프롬프트 예시

Claude의 성격을 설정할 수 있습니다:

```
친근한 도우미:
"당신은 친근하고 도움이 되는 AI 어시스턴트입니다. 한국어로 자연스럽고 간결하게 대답해주세요."

전문 상담사:
"당신은 전문적이고 신뢰할 수 있는 상담사입니다. 사용자의 고민을 들어주고 건설적인 조언을 해주세요."

게임 동료:
"당신은 유쾌하고 장난기 많은 게임 동료입니다. 재미있게 대화하며 게임 팁도 알려주세요."
```

## 🔍 디버깅

### 로그 확인
Unity Console에서 다음 태그들을 확인:
- `[STT]` - 음성 인식 관련
- `[Claude]` - Claude API 관련
- `[TTS]` - 음성 합성 관련
- `[VoiceChat]` - 전체 시스템 관련

### 일반적인 문제들

**마이크 권한 오류:**
- Android: RECORD_AUDIO 권한 확인
- iOS: NSMicrophoneUsageDescription 확인

**API 키 오류:**
- 키 형식 확인 (AIza..., sk-ant-...)
- API 활성화 상태 확인
- 네트워크 연결 확인

**TTS 서버 연결 실패:**
- 서버가 실행 중인지 확인
- URL이 올바른지 확인 (ws://127.0.0.1:8000/ws/tts)

## 🔗 완전한 대화 플로우

1. **사용자**: Space 키 누르고 "안녕하세요" 말하기
2. **STT**: 음성을 "안녕하세요" 텍스트로 변환
3. **Claude**: "안녕하세요! 무엇을 도와드릴까요?" 응답 생성
4. **TTS**: Claude 응답을 음성으로 변환하여 재생
5. **사용자**: 다시 Space 키로 후속 질문

## 💡 고급 기능

- **대화 기록 유지**: 이전 대화 내용을 기억
- **자동 기록 정리**: 너무 긴 대화는 자동으로 정리
- **다중 STT 서비스**: Google Cloud / OpenAI 선택 가능
- **실시간 상태 표시**: 녹음/처리/재생 상태 표시