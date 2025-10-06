# server/ws_app.py
import asyncio, json, io, os, re
from contextlib import suppress
from typing import List, AsyncGenerator
import anyio
import datetime
import time

import numpy as np
import requests
import soundfile as sf
from fastapi import FastAPI, WebSocket
from starlette.websockets import WebSocketDisconnect

# --- 기존 코드 재사용 ---
from .server import wav_bytes_to_float32  # (네 server.py에 있는 함수)
from .rvc_wrapper import RVCConverter

# ====== 설정 ======
VOICEVOX_URL = os.environ.get("VOICEVOX_URL", "http://127.0.0.1:50021")
DEFAULT_SPEAKER_ID = int(os.environ.get("VV_SPK", "2"))  # ずんだもん ノーマル
DEBUG_SAVE_AUDIO = os.environ.get("DEBUG_SAVE_AUDIO", "true").lower() == "true"

# 디버그 로그 활성화 여부
enableDebugLog = True

# RVC 준비(너의 안정 프리셋 + 속도 옵션 예시)
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

print("[INIT] Initializing RVC...")
try:
    rvc = RVCConverter(
        rvc_root=os.path.join(ROOT, "third_party", "RVC"),
        pth_path=os.path.join(ROOT, "models", "rvc", "Fern_e300_s1800.pth"),
        index_path=os.path.join(ROOT, "models", "rvc", "added_IVF75_Flat_nprobe_1_Fern_v2.index"),
        device="cuda",
        input_sr=24000,
        f0_method="rmvpe",      # 가장 빠른 f0 추출 방법
        index_rate=0.8,         # OpenAI 음성에 맞게 중간값 (원본 특성도 일부 유지)
        protect=0.75,           # 무성음 보호 감소 (더 강한 변환)
        filter_radius=5,        # 피치 평활화
        rms_mix_rate=0,      # 25% 원본 볼륨 믹싱 (자연스러움 향상)
        f0_up_key=0,           # OpenAI(156Hz) → VOICEVOX(365Hz) 맞춤: +15 반음 ≈ 365Hz
        resample_sr=24000,      # 전송용 24k로 통일
        is_half=True,           # FP16으로 메모리/속도 최적화
        bucketing=False,        # 비활성화 (품질 우선)
        bucket_ms=500,          # 0.5초 버킷
    )
    print(f"[INIT] RVC initialized successfully. Environment 'infer': {os.environ.get('infer', 'NOT_SET')}")
    print(f"[INIT] RVC target SR: {getattr(rvc, 'tgt_sr', 'UNKNOWN')}")

    # 워밍업: 더미 오디오로 첫 변환 실행 (Cold Start 제거)
    print("[INIT] Warming up RVC with dummy audio...")
    warmup_start = time.perf_counter()
    dummy_audio = np.random.randn(24000).astype(np.float32) * 0.01  # 1초, 작은 볼륨
    try:
        rvc.convert(dummy_audio, sr=24000)
        warmup_time = time.perf_counter() - warmup_start
        print(f"[INIT] RVC warmup completed in {warmup_time*1000:.1f}ms")
    except Exception as e:
        print(f"[WARN] RVC warmup failed: {e}")

except Exception as e:
    print(f"[ERROR] RVC initialization failed: {e}")
    rvc = None

app = FastAPI()
session = requests.Session()
session.post(f"{VOICEVOX_URL}/initialize_speaker",
             params={"speaker": DEFAULT_SPEAKER_ID, "skip_reinit": True}, timeout=10)

def slice_text(s: str) -> List[str]:
    """구두점 기준으로 자연스럽게 쪼개기 (VOICEVOX 품질 보장)"""
    # 구두점으로 분할하되 구두점을 앞 문장에 포함시킴
    parts = re.split(r'([。！？!?、,])', s)
    merged, buf = [], ""

    for i, part in enumerate(parts):
        buf += part

        # 구두점일 때만 분할 (자연스러운 문장 단위)
        if part in "。！？!?、,":
            if buf.strip():  # 빈 문자열이 아닐 때만 추가
                merged.append(buf.strip())
            buf = ""

    # 마지막 남은 부분
    if buf.strip():
        merged.append(buf.strip())

    # 구두점만 있는 피스들 제거
    result = []
    for piece in merged:
        # 구두점과 공백만 있는 피스는 제외
        if re.sub(r'[。！？!?、,\s]', '', piece):
            result.append(piece)

    return result

def iter_pcm16_frames(pcm_f32: np.ndarray, sr: int, frame_ms: int = 20):
    """float32 → PCM16 20ms 프레임 바이너리 생성기"""
    n = int(sr * frame_ms / 1000)
    for i in range(0, len(pcm_f32), n):
        f = pcm_f32[i:i+n]
        if f.size < n:
            f = np.pad(f, (0, n - f.size), constant_values=0)
        f = np.clip(f, -1.0, 1.0)
        yield (f * 32767.0).astype(np.int16).tobytes()

@app.get("/health")
def health():
    return {"ok": True, "tgt_sr": getattr(rvc, "tgt_sr", 24000)}

@app.websocket("/ws/tts_stream")
async def ws_tts_stream(ws: WebSocket):
    """텍스트 스트리밍 → VOICEVOX + RVC → 오디오 스트리밍 (실시간)"""
    await ws.accept()
    try:
        if enableDebugLog: print("[TTS_STREAM] WebSocket connected")

        # RVC 사용 가능성 체크
        if rvc is None:
            await ws.send_text(json.dumps({"event": "error", "detail": "RVC not initialized"}))
            return

        # 준비 완료 신호
        await ws.send_text(json.dumps({"event": "ready"}))

        tgt_sr = getattr(rvc, "tgt_sr", 24000)
        text_buffer = ""
        speaker = DEFAULT_SPEAKER_ID

        while True:
            try:
                message = await ws.receive()

                if message.get("type") == "websocket.disconnect":
                    break

                if "text" in message and message["text"]:
                    try:
                        data = json.loads(message["text"])
                        msg_type = data.get("type", "")

                        if msg_type == "text":
                            # 텍스트 청크 수신
                            text_chunk = data.get("text", "")
                            text_buffer += text_chunk

                            if enableDebugLog:
                                print(f"[TTS_STREAM] Received text chunk: '{text_chunk}' (buffer: '{text_buffer}')")

                            # slice_text로 즉시 처리 가능한 조각 추출
                            pieces = slice_text(text_buffer)

                            # 처리 가능한 조각들 즉시 처리
                            if pieces:
                                # 마지막 조각은 미완성일 수 있으므로 확인
                                # 버퍼가 구두점으로 끝나면 모든 조각 처리, 아니면 마지막 조각 보류
                                processable_pieces = pieces
                                if not re.search(r'[。！？!?、,.\n]$', text_buffer):
                                    # 버퍼가 구두점으로 끝나지 않으면 마지막 조각은 미완성
                                    if len(pieces) > 1:
                                        processable_pieces = pieces[:-1]
                                        # 마지막 조각을 버퍼에 남김
                                        text_buffer = pieces[-1]
                                    else:
                                        # 조각이 1개뿐이면 아직 처리 불가
                                        processable_pieces = []
                                else:
                                    # 구두점으로 끝나면 모두 처리하고 버퍼 초기화
                                    text_buffer = ""

                                for piece_idx, piece in enumerate(processable_pieces):
                                    piece_start = time.perf_counter()
                                    if enableDebugLog:
                                        print(f"[TTS_STREAM] Processing piece {piece_idx+1}/{len(processable_pieces)}: '{piece}'")

                                    # VOICEVOX 합성
                                    try:
                                        voicevox_start = time.perf_counter()
                                        q = session.post(f"{VOICEVOX_URL}/audio_query",
                                                       params={"text": piece, "speaker": speaker}, timeout=10).json()
                                        q["outputSamplingRate"] = int(tgt_sr)
                                        q["outputStereo"] = False
                                        wav_bytes = session.post(f"{VOICEVOX_URL}/synthesis",
                                                               params={"speaker": speaker}, json=q, timeout=30).content
                                        voicevox_time = time.perf_counter() - voicevox_start

                                        pcm, sr = wav_bytes_to_float32(wav_bytes)
                                        if pcm is not None and pcm.size > 0:
                                            # 전처리
                                            pcm = pcm - np.mean(pcm)
                                            peak = np.max(np.abs(pcm))
                                            if peak > 0.95:
                                                pcm = pcm * (0.95 / peak)

                                            # RVC 변환
                                            rvc_start = time.perf_counter()
                                            conv, out_sr = rvc.convert(pcm, sr=sr)
                                            rvc_time = time.perf_counter() - rvc_start

                                            # 후처리
                                            if conv.size > 0:
                                                fade_len = min(240, conv.size // 20)
                                                if fade_len > 0:
                                                    fade_in = np.linspace(0, 1, fade_len)
                                                    conv[:fade_len] *= fade_in
                                                    fade_out = np.linspace(1, 0, fade_len)
                                                    conv[-fade_len:] *= fade_out
                                                conv[np.abs(conv) < 0.001] = 0

                                                # 스트리밍
                                                streaming_start = time.perf_counter()
                                                for frame in iter_pcm16_frames(conv, out_sr, frame_ms=20):
                                                    if frame:
                                                        await ws.send_bytes(frame)
                                                streaming_time = time.perf_counter() - streaming_start

                                                piece_total = time.perf_counter() - piece_start
                                                print(f"[TTS_STREAM] Piece {piece_idx+1}/{len(processable_pieces)} completed - VOICEVOX: {voicevox_time*1000:.1f}ms, RVC: {rvc_time*1000:.1f}ms, Streaming: {streaming_time*1000:.1f}ms, Total: {piece_total*1000:.1f}ms")

                                    except Exception as e:
                                        print(f"[TTS_STREAM] TTS error: {e}")

                        elif msg_type == "end":
                            # 남은 버퍼를 로컬로 복사하고 즉시 초기화 (타이밍 이슈 방지)
                            final_text = text_buffer.strip()
                            text_buffer = ""  # 처리 전에 먼저 초기화하여 다음 요청과 격리

                            if final_text:
                                if enableDebugLog:
                                    print(f"[TTS_STREAM] Processing final buffer: '{final_text}'")

                                # slice_text로 더 작은 조각으로 나누기
                                final_pieces = slice_text(final_text)

                                for piece_idx, piece in enumerate(final_pieces):
                                    piece_start = time.perf_counter()
                                    if enableDebugLog:
                                        print(f"[TTS_STREAM] Processing final piece {piece_idx+1}/{len(final_pieces)}: '{piece}'")

                                    try:
                                        voicevox_start = time.perf_counter()
                                        q = session.post(f"{VOICEVOX_URL}/audio_query",
                                                       params={"text": piece, "speaker": speaker}, timeout=10).json()
                                        q["outputSamplingRate"] = int(tgt_sr)
                                        q["outputStereo"] = False
                                        wav_bytes = session.post(f"{VOICEVOX_URL}/synthesis",
                                                               params={"speaker": speaker}, json=q, timeout=30).content
                                        voicevox_time = time.perf_counter() - voicevox_start

                                        pcm, sr = wav_bytes_to_float32(wav_bytes)
                                        if pcm is not None and pcm.size > 0:
                                            pcm = pcm - np.mean(pcm)
                                            peak = np.max(np.abs(pcm))
                                            if peak > 0.95:
                                                pcm = pcm * (0.95 / peak)

                                            rvc_start = time.perf_counter()
                                            conv, out_sr = rvc.convert(pcm, sr=sr)
                                            rvc_time = time.perf_counter() - rvc_start

                                            if conv.size > 0:
                                                fade_len = min(240, conv.size // 20)
                                                if fade_len > 0:
                                                    fade_in = np.linspace(0, 1, fade_len)
                                                    conv[:fade_len] *= fade_in
                                                    fade_out = np.linspace(1, 0, fade_len)
                                                    conv[-fade_len:] *= fade_out
                                                conv[np.abs(conv) < 0.001] = 0

                                                streaming_start = time.perf_counter()
                                                for frame in iter_pcm16_frames(conv, out_sr, frame_ms=20):
                                                    if frame:
                                                        await ws.send_bytes(frame)
                                                streaming_time = time.perf_counter() - streaming_start

                                                piece_total = time.perf_counter() - piece_start
                                                print(f"[TTS_STREAM] Final piece {piece_idx+1}/{len(final_pieces)} completed - VOICEVOX: {voicevox_time*1000:.1f}ms, RVC: {rvc_time*1000:.1f}ms, Streaming: {streaming_time*1000:.1f}ms, Total: {piece_total*1000:.1f}ms")
                                    except Exception as e:
                                        print(f"[TTS_STREAM] Final TTS error: {e}")

                            # 종료 신호 전송 (버퍼는 이미 초기화됨)
                            await ws.send_text(json.dumps({"event": "end"}))
                            if enableDebugLog: print("[TTS_STREAM] Utterance completed, ready for next")

                        elif msg_type == "speaker":
                            speaker = int(data.get("speaker", DEFAULT_SPEAKER_ID))
                            if enableDebugLog:
                                print(f"[TTS_STREAM] Speaker changed to: {speaker}")

                    except Exception as e:
                        print(f"[TTS_STREAM] Message parse error: {e}")

            except Exception as e:
                print(f"[TTS_STREAM] Processing error: {e}")
                await ws.send_text(json.dumps({"event": "error", "detail": str(e)}))
                break

    except WebSocketDisconnect:
        if enableDebugLog: print("[TTS_STREAM] WebSocket disconnected by client")
    except Exception as e:
        print(f"[TTS_STREAM] Error: {e}")
        with suppress(Exception):
            await ws.send_text(json.dumps({"event": "error", "detail": str(e)}))
    finally:
        if enableDebugLog: print("[TTS_STREAM] WebSocket session ended")

@app.websocket("/ws/rvc")
async def ws_rvc(ws: WebSocket):
    """OpenAI Realtime 오디오를 받아서 RVC 변환만 하는 엔드포인트"""
    await ws.accept()
    try:
        if enableDebugLog: print("[RVC] RVC-only WebSocket connected")

        # RVC 사용 가능성 체크
        if rvc is None:
            await ws.send_text(json.dumps({"event": "error", "detail": "RVC not initialized"}))
            return

        # 준비 완료 신호
        await ws.send_text(json.dumps({"event": "ready"}))

        tgt_sr = getattr(rvc, "tgt_sr", 24000)
        audio_buffer = bytearray()

        # 디버그용 폴더 설정
        debug_dir = None
        if DEBUG_SAVE_AUDIO:
            timestamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
            debug_dir = os.path.join(ROOT, f"debug_rvc_{timestamp}")
            os.makedirs(debug_dir, exist_ok=True)
            print(f"[DEBUG] Saving RVC audio to: {debug_dir}")

        chunk_counter = 0
        is_receiving = True

        while True:
            try:
                # 바이너리 오디오 데이터 수신 (PCM16)
                message = await ws.receive()

                if message.get("type") == "websocket.disconnect":
                    break

                # 텍스트 메시지 우선 처리 (제어 명령)
                if "text" in message and message["text"]:
                    try:
                        cmd = json.loads(message["text"])
                        # OpenAI Realtime API 이벤트 처리
                        event_type = cmd.get("type", "")

                        # response.done 또는 response.output_item.done 또는 커스텀 end 신호
                        if event_type in ["end", "response.done", "response.output_item.done"]:
                            is_receiving = False
                            # 전체 버퍼를 한 번에 RVC 변환
                            if len(audio_buffer) > 0:
                                chunk_counter += 1
                                chunk_data = bytes(audio_buffer)
                                pcm_array = np.frombuffer(chunk_data, dtype=np.int16)
                                pcm_f32 = pcm_array.astype(np.float32) / 32768.0

                                if len(pcm_f32) > 0:
                                    # 입력 오디오 정보 로깅
                                    if enableDebugLog:
                                        duration_sec = len(pcm_f32) / 24000.0
                                        print(f"[RVC] Processing full utterance: {len(pcm_f32)} samples ({duration_sec:.2f}s), max={np.max(np.abs(pcm_f32)):.3f}")

                                    # 디버그: 입력 오디오 저장
                                    if debug_dir:
                                        input_file = os.path.join(debug_dir, f"full_input.wav")
                                        sf.write(input_file, pcm_f32, 24000)

                                    # 오디오 전처리 (DC 제거, 정규화, EQ)
                                    if np.max(np.abs(pcm_f32)) > 0:
                                        # DC offset 제거
                                        pcm_f32 = pcm_f32 - np.mean(pcm_f32)

                                        # 볼륨 정규화 (RVC 입력은 충분한 볼륨 필요)
                                        peak = np.max(np.abs(pcm_f32))
                                        if peak > 0.01:  # 너무 작은 신호 제외
                                            target_level = 0.7  # RVC에 적합한 입력 레벨
                                            pcm_f32 = pcm_f32 * (target_level / peak)
                                            pcm_f32 = np.clip(pcm_f32, -1.0, 1.0)

                                        # 디버그: 전처리 후 오디오 저장
                                        if debug_dir:
                                            pre_rvc_file = os.path.join(debug_dir, f"full_pre_rvc.wav")
                                            sf.write(pre_rvc_file, pcm_f32, 24000)

                                        # RVC 변환
                                        rvc_start = time.perf_counter()
                                        conv, out_sr = rvc.convert(pcm_f32, sr=24000)
                                        rvc_time = time.perf_counter() - rvc_start

                                        if enableDebugLog:
                                            print(f"[RVC] Output: {len(conv)} samples, max={np.max(np.abs(conv)):.3f}, SR={out_sr}, time={rvc_time*1000:.1f}ms")
                                            # 입력 오디오 특성 분석
                                            f0_range = "unknown"
                                            try:
                                                import librosa
                                                f0, voiced_flag, voiced_probs = librosa.pyin(pcm_f32, fmin=librosa.note_to_hz('C2'), fmax=librosa.note_to_hz('C7'), sr=24000)
                                                f0_valid = f0[~np.isnan(f0)]
                                                if len(f0_valid) > 0:
                                                    f0_mean = np.mean(f0_valid)
                                                    f0_range = f"{np.min(f0_valid):.1f}-{np.max(f0_valid):.1f}Hz (mean: {f0_mean:.1f}Hz)"
                                            except:
                                                pass
                                            print(f"[RVC] Input pitch range: {f0_range}")

                                        # 디버그: RVC 원본 결과 저장
                                        if debug_dir:
                                            rvc_raw_file = os.path.join(debug_dir, f"full_rvc_raw.wav")
                                            sf.write(rvc_raw_file, conv, out_sr)

                                        # 출력 오디오 후처리
                                        if len(conv) > 0 and np.max(np.abs(conv)) > 0:
                                            # 페이드 인/아웃 (클릭 노이즈 방지)
                                            fade_samples = min(240, len(conv) // 20)  # 5ms @ 48kHz 또는 5%
                                            if fade_samples > 0:
                                                # 페이드 인
                                                fade_in = np.linspace(0, 1, fade_samples)
                                                conv[:fade_samples] *= fade_in
                                                # 페이드 아웃
                                                fade_out = np.linspace(1, 0, fade_samples)
                                                conv[-fade_samples:] *= fade_out

                                            # 매우 작은 값들을 0으로 (노이즈 제거)
                                            conv[np.abs(conv) < 0.001] = 0

                                            # 디버그: 최종 결과 저장
                                            if debug_dir:
                                                final_file = os.path.join(debug_dir, f"full_final.wav")
                                                sf.write(final_file, conv, out_sr)

                                            # 변환된 오디오를 20ms 프레임으로 스트리밍
                                            for frame in iter_pcm16_frames(conv, out_sr, frame_ms=20):
                                                if frame:
                                                    await ws.send_bytes(frame)
                                        else:
                                            if enableDebugLog:
                                                print("[RVC] Empty or silent output from RVC")
                                    else:
                                        if enableDebugLog:
                                            print("[RVC] Input audio too quiet, skipping")

                            await ws.send_text(json.dumps({"event": "end"}))
                            break
                    except Exception as parse_error:
                        if enableDebugLog:
                            print(f"[RVC] Text message parse error: {parse_error}")

                elif "bytes" in message and message["bytes"]:
                    audio_chunk = message["bytes"]
                    audio_buffer.extend(audio_chunk)
                    # 데이터 수신 중에는 버퍼만 쌓음 (전체 발화를 한 번에 처리)

            except Exception as e:
                print(f"[RVC] Processing error: {e}")
                await ws.send_text(json.dumps({"event": "error", "detail": str(e)}))
                break

    except WebSocketDisconnect:
        pass
    except Exception as e:
        with suppress(Exception):
            await ws.send_text(json.dumps({"event": "error", "detail": str(e)}))
    finally:
        with suppress(Exception):
            await ws.close()
        if enableDebugLog: print("[RVC] RVC-only WebSocket disconnected")

async def _recv_init_json(ws, timeout: float = 5.0):
    """
    첫 메시지를 TEXT 또는 BYTES로 받아 JSON 파싱.
    실패 시 (None, "에러원인") 반환.
    """
    try:
        ev = await asyncio.wait_for(ws.receive(), timeout=timeout)
    except asyncio.TimeoutError:
        return None, "timeout"
    if ev.get("type") == "websocket.disconnect":
        return None, "disconnect"

    if "text" in ev and ev["text"] is not None:
        raw = ev["text"]
    elif "bytes" in ev and ev["bytes"] is not None:
        # Unity가 혹시 바이너리로 보냈더라도 텍스트로 간주해 시도
        raw = ev["bytes"].decode("utf-8", "ignore")
    else:
        return None, "empty"

    print("[WS init raw]", repr(raw))  # ← 실제 받은 문자열을 눈으로 확인
    try:
        return json.loads(raw), None
    except Exception as e:
        return None, f"bad-json: {e}"

@app.websocket("/ws/tts")
async def ws_tts(ws: WebSocket):
    await ws.accept()
    try:
        # 1) 쿼리 먼저
        text = (ws.query_params.get("text") or "").strip()
        spk  = int(ws.query_params.get("speaker") or DEFAULT_SPEAKER_ID)

        # 2) 쿼리에 없으면 첫 메시지를 유연하게 수신
        if not text:
            init, err = await _recv_init_json(ws, timeout=5.0)
            if err:
                await ws.send_text(json.dumps({"event": "error", "detail": err}))
                return
            text = str(init.get("text", "")).strip()
            spk  = int(init.get("speaker", spk))

        if not text:
            await ws.send_text(json.dumps({"event":"end"}))
            return

        # 3) RVC 사용 가능성 체크
        if rvc is None:
            await ws.send_text(json.dumps({"event": "error", "detail": "RVC not initialized"}))
            return

        # 4) ACK (클라 디버그용)
        print(f"[WS] Starting TTS for text='{text[:50]}...' speaker={spk}")
        await ws.send_text(json.dumps({"event": "ready", "speaker": spk}))

        # ===== 동기 처리 (스레드 제거) =====
        raw_pieces = slice_text(text)
        pieces = [p.strip() for p in raw_pieces if p and p.strip()]

        print(f"[DEBUG] Original text: '{text}'")
        print(f"[DEBUG] Raw pieces: {raw_pieces}")
        print(f"[DEBUG] Filtered pieces: {pieces}")

        if not pieces:
            await ws.send_text(json.dumps({"event": "end"}))
            return

        tgt_sr = getattr(rvc, "tgt_sr", 24000)

        # 디버그용 폴더 설정 (옵션)
        debug_dir = None
        if DEBUG_SAVE_AUDIO:
            timestamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
            debug_dir = os.path.join(ROOT, f"debug_audio_{timestamp}")
            os.makedirs(debug_dir, exist_ok=True)
            print(f"[DEBUG] Saving audio pieces to: {debug_dir}")

        for i, part in enumerate(pieces):
            try:
                piece_start = time.perf_counter()
                print(f"[WS] Processing part {i+1}/{len(pieces)}: '{part}'")

                # 너무 짧은 피스는 건너뛰기 (노이즈 방지)
                if len(part) < 2:
                    print(f"[WS] Skipping too short piece: '{part}'")
                    continue

                # VOICEVOX 합성
                voicevox_start = time.perf_counter()
                q = session.post(f"{VOICEVOX_URL}/audio_query",
                                params={"text": part, "speaker": spk}, timeout=10).json()
                q["outputSamplingRate"] = int(tgt_sr)
                q["outputStereo"] = False
                wav_bytes = session.post(f"{VOICEVOX_URL}/synthesis",
                                        params={"speaker": spk}, json=q, timeout=30).content
                voicevox_time = time.perf_counter() - voicevox_start

                pcm, sr = wav_bytes_to_float32(wav_bytes)
                if pcm is None or pcm.size == 0:
                    continue

                # 디버그: 원본 VOICEVOX 결과 저장
                if debug_dir:
                    voicevox_file = os.path.join(debug_dir, f"piece_{i+1:02d}_voicevox.wav")
                    sf.write(voicevox_file, pcm, sr)

                # RVC 변환 (오디오 정리 포함)
                rvc_start = time.perf_counter()
                print(f"[WS] Converting audio with RVC for part {i+1}/{len(pieces)}")

                # RVC 입력 전 정리
                pcm_cleaned = pcm.copy()
                if pcm_cleaned.size > 0:
                    # DC offset 제거
                    pcm_cleaned = pcm_cleaned - np.mean(pcm_cleaned)
                    # 볼륨 정규화 (클리핑 방지)
                    peak = np.max(np.abs(pcm_cleaned))
                    if peak > 0.95:
                        pcm_cleaned = pcm_cleaned * (0.95 / peak)

                # 디버그: RVC 입력 전처리 결과 저장
                if debug_dir:
                    pre_rvc_file = os.path.join(debug_dir, f"piece_{i+1:02d}_pre_rvc.wav")
                    sf.write(pre_rvc_file, pcm_cleaned, sr)

                # 피치 분석 (VOICEVOX 입력)
                if enableDebugLog:
                    f0_range = "unknown"
                    try:
                        import librosa
                        f0, voiced_flag, voiced_probs = librosa.pyin(pcm_cleaned, fmin=librosa.note_to_hz('C2'), fmax=librosa.note_to_hz('C7'), sr=sr)
                        f0_valid = f0[~np.isnan(f0)]
                        if len(f0_valid) > 0:
                            f0_mean = np.mean(f0_valid)
                            f0_range = f"{np.min(f0_valid):.1f}-{np.max(f0_valid):.1f}Hz (mean: {f0_mean:.1f}Hz)"
                    except:
                        pass
                    print(f"[TTS] VOICEVOX input pitch range: {f0_range}")

                conv, out_sr = rvc.convert(pcm_cleaned, sr=sr)

                # 디버그: RVC 원본 결과 저장
                if debug_dir:
                    rvc_raw_file = os.path.join(debug_dir, f"piece_{i+1:02d}_rvc_raw.wav")
                    sf.write(rvc_raw_file, conv, out_sr)

                # RVC 출력 후 정리 (노이즈 제거)
                if conv.size > 0:
                    # 시작/끝 페이드 (클릭음 방지)
                    fade_len = min(240, conv.size // 20)  # 5ms @ 48kHz 또는 5%
                    if fade_len > 0:
                        # 페이드 인
                        fade_in = np.linspace(0, 1, fade_len)
                        conv[:fade_len] *= fade_in
                        # 페이드 아웃
                        fade_out = np.linspace(1, 0, fade_len)
                        conv[-fade_len:] *= fade_out

                    # 매우 작은 값들을 0으로 (노이즈 제거)
                    conv[np.abs(conv) < 0.001] = 0

                # 디버그: 최종 결과 저장
                if debug_dir:
                    final_file = os.path.join(debug_dir, f"piece_{i+1:02d}_final.wav")
                    sf.write(final_file, conv, out_sr)

                rvc_time = time.perf_counter() - rvc_start
                print(f"[WS] RVC conversion completed successfully")
                if debug_dir:
                    print(f"[DEBUG] Saved debug files for piece {i+1}")

            except Exception as e:
                import traceback
                tb = traceback.format_exc()
                error_msg = f"Processing error: {e}"
                print(f"[ERROR] {error_msg}")
                print(f"[TRACEBACK] {tb}")
                await ws.send_text(json.dumps({"event": "error", "detail": error_msg}))
                break

            # 오디오 스트리밍
            streaming_start = time.perf_counter()
            sent_any = False
            frame_count = 0
            for frame in iter_pcm16_frames(conv, out_sr, frame_ms=20):
                if not frame:
                    continue
                await ws.send_bytes(frame)
                sent_any = True
                frame_count += 1

            streaming_time = time.perf_counter() - streaming_start
            piece_total_time = time.perf_counter() - piece_start

            # 타이밍 로그 출력
            print(f"[TIMING] Piece {i+1}/{len(pieces)} - VOICEVOX: {voicevox_time*1000:.1f}ms, RVC: {rvc_time*1000:.1f}ms, Streaming: {streaming_time*1000:.1f}ms, Total: {piece_total_time*1000:.1f}ms")

            if frame_count > 0:
                print(f"[WS] Sent {frame_count} audio frames for piece {i+1}/{len(pieces)}")

            if not sent_any:
                silence_samples = int(out_sr * 0.02)  # 20ms
                await ws.send_bytes(b"\x00\x00" * silence_samples)

            # 피스 사이에 짧은 침묵 추가 (노이즈 분리)
            if i < len(pieces) - 1:  # 마지막 피스가 아니면
                gap_samples = int(out_sr * 0.05)  # 50ms 침묵
                await ws.send_bytes(b"\x00\x00" * gap_samples)

        await ws.send_text(json.dumps({"event": "end"}))

    except WebSocketDisconnect:
        pass
    except Exception as e:
        with suppress(Exception):
            await ws.send_text(json.dumps({"event":"error","detail":str(e)}))
    finally:
        with suppress(Exception):
            await ws.close()