# Unity 음성 인식 설정 가이드

## 1. 필수 패키지 설치

### Newtonsoft.Json 패키지 추가
1. Unity Package Manager 열기 (Window → Package Manager)
2. 상단 드롭다운에서 "Unity Registry" 선택
3. "Newtonsoft Json" 검색하여 설치

또는 `Packages/manifest.json`에 추가:
```json
{
  "dependencies": {
    "com.unity.nuget.newtonsoft-json": "3.2.1"
  }
}
```

## 2. API 키 설정

### Google Cloud Speech-to-Text
1. [Google Cloud Console](https://console.cloud.google.com/) 접속
2. 새 프로젝트 생성 또는 기존 프로젝트 선택
3. Speech-to-Text API 활성화
4. API 키 생성 (서비스 계정 권장)
5. Unity Inspector에서 `googleCloudApiKey` 필드에 입력

### OpenAI Whisper
1. [OpenAI Platform](https://platform.openai.com/) 접속
2. API 키 생성
3. Unity Inspector에서 `openAiApiKey` 필드에 입력

## 3. Unity 설정

### 마이크 권한 설정
**Android (PlayerSettings):**
```xml
<uses-permission android:name="android.permission.RECORD_AUDIO" />
```

**iOS (PlayerSettings):**
- NSMicrophoneUsageDescription 추가

### 스크립트 설정
1. 빈 GameObject에 `VoiceToTextClient` 컴포넌트 추가
2. `VoiceControlExample` 컴포넌트 추가 (UI 사용 시)
3. Inspector에서 설정:
   - STT Service 선택 (GoogleCloud 또는 OpenAI 권장)
   - API 키 입력
   - Recording Key 설정 (기본: Space)

## 4. UI 설정 (선택사항)

Canvas에 다음 UI 요소들 추가:
- Status Text: 현재 상태 표시
- Recognized Text: 인식된 텍스트 표시
- Record Button: 녹음 버튼 (터치 디바이스용)

`VoiceControlExample`의 Inspector에서 UI 요소들을 연결

## 5. 사용법

### 키보드 입력
- Space 키를 누르고 있는 동안 녹음
- 키를 떼면 음성 인식 시작

### 스크립트에서 사용
```csharp
// 녹음 시작
voiceClient.StartRecording();

// 녹음 중지 및 인식
voiceClient.StopRecording();

// 이벤트 구독
voiceClient.OnTextRecognized += (text) => {
    Debug.Log($"인식된 텍스트: {text}");
};
```

## 6. 서비스별 특징

### Google Cloud Speech-to-Text
- **장점**: 높은 정확도, 한국어 지원 우수
- **단점**: 유료 (월 60분 무료)
- **추천**: 상용 서비스

### OpenAI Whisper
- **장점**: 다양한 언어 지원, 상대적으로 저렴
- **단점**: 응답 속도가 상대적으로 느림
- **추천**: 개발/테스트용

## 7. 문제 해결

### 마이크 권한 오류
```csharp
// 런타임에서 마이크 권한 확인
if (Microphone.devices.Length == 0)
{
    Debug.LogError("마이크를 찾을 수 없습니다.");
}
```

### API 호출 오류
- API 키가 올바른지 확인
- 네트워크 연결 상태 확인
- API 할당량 확인

### 오디오 품질 개선
- `sampleRate`를 16000으로 설정 (권장)
- 조용한 환경에서 테스트
- 마이크와 입 사이 거리 조정