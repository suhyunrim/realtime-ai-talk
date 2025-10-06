# server/rvc_wrapper.py
from contextlib import nullcontext
import os, sys, logging
from typing import Tuple, Set
import numpy as np
import librosa
import torch
import importlib
import importlib.util
from contextlib import suppress

import torch
try:
    import torchaudio
    _HAS_TA = True
except Exception:
    _HAS_TA = False

log = logging.getLogger("rvc_wrapper")

def _prime_rvc_sys_path(rvc_root: str, set_env: bool = True) -> str:
    """
    RVC 최신 구조(infer/...)를 import 가능하게 만들고,
    pipeline이 참조하는 환경변수(infer, rmvpe_root 등)를 보강한다.

    Returns: rvc_root
    Raises:  FileNotFoundError, ImportError (스모크 테스트 실패 시)
    """
    r = os.path.abspath(rvc_root)
    infer_dir = os.path.join(r, "infer")
    lib_dir   = os.path.join(infer_dir, "lib")

    if not os.path.isdir(infer_dir):
        raise FileNotFoundError(f"[RVC] infer 디렉터리를 찾을 수 없습니다: {infer_dir}")

    # 1) 모든 관련 모듈 제거 (완전한 재로딩)
    modules_to_remove = []
    for module_name in sys.modules.keys():
        if module_name.startswith('infer') or 'rmvpe' in module_name.lower():
            modules_to_remove.append(module_name)

    for module_name in modules_to_remove:
        del sys.modules[module_name]
        print(f"[RVC] Removed module: {module_name}")

    # 2) sys.path 완전히 정리하고 다시 설정
    paths_to_remove = []
    for p in sys.path:
        if 'RVC' in p or 'infer' in p:
            paths_to_remove.append(p)

    for p in paths_to_remove:
        sys.path.remove(p)

    # 3) 올바른 순서로 sys.path에 추가
    sys.path.insert(0, r)  # RVC root가 최우선
    if os.path.isdir(infer_dir):
        sys.path.insert(1, infer_dir)
    if os.path.isdir(lib_dir):
        sys.path.insert(2, lib_dir)

    print(f"[RVC] sys.path updated: {[p for p in sys.path[:5] if 'RVC' in p]}")

    # 4) 환경변수 설정 (강제로 모든 변형 설정)
    if set_env:
        env_vars = {
            "infer": r,
            "INFER": r,
            "rmvpe_root": os.path.join(r, "assets", "rmvpe"),
            "RMVPE_ROOT": os.path.join(r, "assets", "rmvpe"),
            "fcpe_root": os.path.join(r, "assets", "fcpe"),
            "FCPE_ROOT": os.path.join(r, "assets", "fcpe"),
            "hubert_path": os.path.join(r, "assets", "hubert", "hubert_base.pt"),
            "HUBERT_PATH": os.path.join(r, "assets", "hubert", "hubert_base.pt"),
        }

        for k, v in env_vars.items():
            os.environ[k] = v.replace("\\", "/")

        print(f"[RVC] Set environment variables: {list(env_vars.keys())}")

    # 5) importlib 캐시 무효화
    importlib.invalidate_caches()

    # 6) 스모크 테스트
    try:
        # 직접 sys.path를 통해 임포트 시도
        spec = importlib.util.find_spec("infer.modules.vc.pipeline")
        if spec is None:
            raise ImportError("Cannot find infer.modules.vc.pipeline")

        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        print("[RVC] Pipeline module loaded successfully")

    except Exception as e:
        raise ImportError(
            f"[RVC] pipeline import 실패. infer_dir={infer_dir}\n"
            f"sys.path RVC paths: {[p for p in sys.path if 'RVC' in p]}\n"
            f"Error: {e}"
        ) from e

    return r

def snr_segmental(y: np.ndarray, sr: int, win_ms: int = 20, thr_ratio: float = 0.15) -> float:
    """
    - 20ms 프레임, 50% 겹침
    - 프레임 RMS의 중앙값(median)의 thr_ratio배 미만을 '무음'으로 간주
    - 무음 프레임 RMS^2 평균 = noise_power
    유음 프레임 RMS^2 평균 = signal_power
    """
    y = np.asarray(y, dtype=np.float32)
    if y.size == 0: return float("nan")

    # DC 제거 + 과피크만 누름
    y = y - float(np.mean(y))
    peak = float(np.max(np.abs(y)))
    if peak > 1.0: y = y / peak

    n = int(sr * win_ms / 1000)
    hop = n // 2
    if n < 1: return float("nan")

    # 프레임 RMS
    frames = []
    for i in range(0, len(y) - n + 1, hop):
        f = y[i:i+n]
        rms2 = float(np.mean(f*f)) + 1e-12
        frames.append(rms2)
    frames = np.array(frames, dtype=np.float64)
    if frames.size < 4: return float("nan")

    med = np.median(frames)
    noise_mask = frames < (med * thr_ratio)
    if not np.any(noise_mask) or np.all(noise_mask):
        # 전구간이 말하거나 전구간이 무음이면 SNR 의미 없음
        return float("nan")

    noise_power  = float(np.mean(frames[noise_mask]))
    signal_power = float(np.mean(frames[~noise_mask]))
    return 10.0 * np.log10(signal_power / noise_power)

class RVCConverter:
    """
    RVC(WebUI) 최신 구조 전용 보이스 변환기.
    - 입력/출력: float32 mono PCM
    - 내부 파이프라인은 16kHz 기준으로 동작 → convert()에서 자동 리샘플
    """
    def __init__(
        self,
        rvc_root: str,              # 예: E:/works/realtime_tts/third_party/RVC
        pth_path: str,              # 예: ../models/rvc/speaker.pth
        index_path: str = "",       # 예: ../models/rvc/speaker.index (없으면 "")
        device: str = "cuda",
        input_sr: int = 24000,
        f0_method: str = "rmvpe",   # "rmvpe" | "crepe" | "harvest" | "dio"
        index_rate: float = 0.5,
        protect: float = 0.33,
        filter_radius: int = 3,
        rms_mix_rate: float = 0.25,
        f0_up_key: int = 0,
        resample_sr: int = 0,
        is_half: bool = False,
        bucketing: bool = True,
        bucket_ms: int = 500,
    ):
        self.device       = torch.device(device if torch.cuda.is_available() else "cpu")
        self.input_sr     = int(input_sr)
        self.f0_method    = f0_method
        self.index_path   = index_path or ""
        self.index_rate   = float(index_rate)
        self.protect      = float(protect)
        self.filter_radius= int(filter_radius)
        self.rms_mix_rate = float(rms_mix_rate)
        self.f0_up_key    = int(f0_up_key)
        self.resample_sr  = int(resample_sr)
        self.is_half      = bool(is_half)
        self.bucketing = bool(bucketing)
        self.bucket_ms = int(bucket_ms)
        self.bucket_samples = int(16000 * (self.bucket_ms / 1000.0)) if (self.bucketing and self.bucket_ms > 0) else 0
        self._warmed_buckets: Set[int] = set()

        # 1) sys.path 준비
        self.rvc_root = _prime_rvc_sys_path(rvc_root, set_env=True)

        # 환경변수 백업 (멀티스레딩 안전성을 위해) - 대소문자 모두 포함
        self._env_backup = {
            "infer": self.rvc_root,
            "INFER": self.rvc_root,
            "rmvpe_root": os.path.join(self.rvc_root, "assets", "rmvpe"),
            "RMVPE_ROOT": os.path.join(self.rvc_root, "assets", "rmvpe"),
            "hubert_path": os.path.join(self.rvc_root, "assets", "hubert", "hubert_base.pt"),
            "HUBERT_PATH": os.path.join(self.rvc_root, "assets", "hubert", "hubert_base.pt"),
        }

        rmvpe_dir = os.path.join(self.rvc_root, "assets", "rmvpe")
        os.makedirs(rmvpe_dir, exist_ok=True)  # 폴더 보장
        os.environ.setdefault("rmvpe_root", rmvpe_dir)  # ← 없으면 세팅

        # (선택) 파일 존재 체크까지
        rmvpe_pt = os.path.join(rmvpe_dir, "rmvpe.pt")
        if not os.path.isfile(rmvpe_pt):
            raise FileNotFoundError(f"rmvpe.pt missing at {rmvpe_pt}")

        # 2) 파이프라인 임포트 (일반 임포트 → 실패 시 파일 경로로 로드)
        try:
            from infer.modules.vc.pipeline import Pipeline as RVC_Pipeline  # 정상 경로
        except ModuleNotFoundError:
            # fallback: 파일 경로로 직접 로딩(이름 충돌 완전 회피)
            pipeline_py = os.path.join(self.rvc_root, "infer", "modules", "vc", "pipeline.py")
            if not os.path.isfile(pipeline_py):
                raise ModuleNotFoundError(
                    f"RVC 최신 구조를 찾을 수 없습니다. '{pipeline_py}'가 존재하는지 확인하세요."
                )
            spec = importlib.util.spec_from_file_location("rvc_pipeline", pipeline_py)
            if not spec or not spec.loader:
                raise ImportError(f"pipeline.py를 로드할 수 없습니다: {pipeline_py}")
            mod = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(mod)
            RVC_Pipeline = mod.Pipeline

        # 3) 모델 클래스 임포트(최신 경로)
        try:
            from infer.lib.infer_pack.models import (
                SynthesizerTrnMs256NSFsid, SynthesizerTrnMs256NSFsid_nono,
                SynthesizerTrnMs768NSFsid, SynthesizerTrnMs768NSFsid_nono
            )
        except Exception as e:
            raise ImportError(
                "infer.lib.infer_pack.models 임포트 실패. RVC 레포가 최신 구조인지 확인하세요."
            ) from e

        # 4) .pth 로드 및 네트워크 구성
        cpt = torch.load(pth_path, map_location="cpu", weights_only=False)
        self.tgt_sr  = cpt["config"][-1]
        n_spk        = cpt["weight"]["emb_g.weight"].shape[0]
        cpt["config"][-3] = n_spk
        self.if_f0   = cpt.get("f0", 1)
        self.version = cpt.get("version", "v2")

        Synth = (
            SynthesizerTrnMs256NSFsid if (self.version == "v1" and self.if_f0)
            else SynthesizerTrnMs256NSFsid_nono if (self.version == "v1")
            else SynthesizerTrnMs768NSFsid if self.if_f0
            else SynthesizerTrnMs768NSFsid_nono
        )

        net_g = Synth(*cpt["config"], is_half=self.is_half)
        _ = net_g.load_state_dict(cpt["weight"], strict=False)
        net_g.eval().to(self.device)
        self.net_g = net_g.half() if (self.is_half and self.device.type == "cuda") else net_g.float()

        # 5) HuBERT 로드
        from torch.serialization import add_safe_globals, safe_globals
        from fairseq.data.dictionary import Dictionary as FairseqDictionary
        from fairseq import checkpoint_utils

        # ① 전역 허용 목록에 추가
        add_safe_globals([FairseqDictionary])

        hubert_path = os.path.join(self.rvc_root, "assets", "hubert", "hubert_base.pt")
        models, _, _ = checkpoint_utils.load_model_ensemble_and_task([hubert_path], suffix="", strict=False)
        self.hubert = models[0].to(self.device).eval().float()

        # 6) 최신 파이프라인 인스턴스
        import multiprocessing

        class Config:
            def __init__(self, device_str: str, is_half: bool):
                self.device   = device_str          # 예: "cuda:0" 또는 "cpu"
                self.is_half  = bool(is_half)
                self.n_cpu    = multiprocessing.cpu_count()
                self.gpu_name = None
                self.gpu_mem  = None  # 단위: GB(대략치)

                # GPU 정보
                if device_str.startswith("cuda") and torch.cuda.is_available():
                    i = int(device_str.split(":")[1]) if ":" in device_str else torch.cuda.current_device()
                    self.gpu_name = torch.cuda.get_device_name(i)
                    self.gpu_mem  = int(torch.cuda.get_device_properties(i).total_memory / 1024 / 1024 / 1024 + 0.4)

                x_pad, x_query, x_center, x_max = 1, 6, 38, 41
                self.x_pad, self.x_query, self.x_center, self.x_max = x_pad, x_query, x_center, x_max

        # (B) device 문자열 준비
        if self.device.type == "cuda":
            # 현재 디바이스 인덱스 사용(대부분 0)
            dev_idx = torch.cuda.current_device()
            dev_str = f"cuda:{dev_idx}"
        elif self.device.type == "mps":
            dev_str = "mps"
        else:
            dev_str = "cpu"

        cfg = Config(dev_str, self.is_half)

        # (C) 파이프라인 생성
        self.pipeline = RVC_Pipeline(self.tgt_sr, cfg)

        # (D) 최종 환경변수 확인 및 강제 설정
        for k, v in self._env_backup.items():
            os.environ[k] = v
        print(f"[RVC] Final environment check - all vars set: {list(self._env_backup.keys())}")

    def _bucket_len(self, n: int) -> int:
        """입력 샘플 길이 n을 버킷 경계(올림)로 정규화"""
        if not self.bucketing or self.bucket_samples <= 0:
            return n
        bs = self.bucket_samples
        return ((n + bs - 1) // bs) * bs

    def warm_bucket(self, nb_samples: int) -> None:
        """
        지정 길이(16k 기준 nb_samples) 버킷을 선워밍.
        서버 startup에서 대표 버킷(0.5/1.0/1.5/2.0s 등)만 호출해 두는 것을 권장.
        """
        if (not self.bucketing) or (nb_samples <= 0) or (nb_samples in self._warmed_buckets):
            return

        # 환경변수 강력하게 보장 (파이프라인 내부 KeyError 방지)
        for k, v in self._env_backup.items():
            os.environ[k] = v

        # 추가: import 경로 확보
        importlib.invalidate_caches()

        print(f"[RVC] Warming bucket {nb_samples} samples")

        # 16kHz 기준의 무음 입력으로 파이프라인 1회 워밍
        x = np.zeros(nb_samples, dtype=np.float32)

        use_amp = (self.device.type == "cuda" and self.is_half)
        amp_ctx = (torch.autocast(device_type="cuda", dtype=torch.float16, enabled=use_amp)
                if self.device.type == "cuda" else nullcontext())

        # 출력 SR은 실제 추론과 동일한 규칙(아래 convert와 동일)로 맞춤
        out_sr = self.resample_sr if (self.resample_sr and self.resample_sr >= 16000) else self.tgt_sr

        with torch.inference_mode():
            with amp_ctx:
                _ = self.pipeline.pipeline(
                    self.hubert, self.net_g, 0, x, None, [0, 0, 0],
                    int(self.f0_up_key), self.f0_method,
                    (self.index_path or ""), float(self.index_rate), int(self.if_f0),
                    int(self.filter_radius), int(self.tgt_sr), int(self.resample_sr),
                    float(self.rms_mix_rate), str(self.version), float(self.protect),
                    f0_file=None,
                )
        self._warmed_buckets.add(nb_samples)

    def convert(self, pcm_float32_mono: np.ndarray, sr: int) -> tuple[np.ndarray, int]:
        """
        입력: float32 mono PCM @ sr
        출력: (float32 mono PCM, output_sr)
        - 내부 파이프라인은 16kHz 기준
        - bucketing 활성 시: 16k 기준 버킷 길이로 패딩 → 추론 → 출력은 원래 길이에 맞춰 크롭
        """
        # 0) 환경변수 및 import 경로 강력하게 보장
        for k, v in self._env_backup.items():
            os.environ[k] = v  # 매번 확실하게 설정

        # import 캐시 무효화
        importlib.invalidate_caches()

        # 추가: 현재 작업 디렉토리도 RVC root로 설정 (일부 라이브러리에서 상대 경로 사용 시)
        original_cwd = os.getcwd()
        try:
            os.chdir(self.rvc_root)

            # 1) 가드
            if pcm_float32_mono is None or pcm_float32_mono.size == 0:
                return np.zeros(0, dtype=np.float32), (self.resample_sr or self.tgt_sr)

            # 1) 모노/float32/연속 메모리
            x = pcm_float32_mono if pcm_float32_mono.ndim == 1 else pcm_float32_mono.mean(axis=-1)
            if x.dtype != np.float32:
                x = x.astype(np.float32, copy=False)
            x = np.ascontiguousarray(x)

            # 2) 내부 SR(16k)로 1회 리샘플
            if sr != 16000:
                if _HAS_TA and self.device.type == "cuda":
                    x_t = torch.from_numpy(x).to(self.device)
                    # (BWE 튜닝: 필터폭 6, rolloff 0.85 가 빠르고 자연스러움)
                    x_t = torchaudio.functional.resample(x_t, sr, 16000,
                                                        lowpass_filter_width=6, rolloff=0.85)
                    x = x_t.detach().cpu().numpy().astype(np.float32, copy=False)
                else:
                    # fallback
                    x = librosa.resample(x, orig_sr=sr, target_sr=16000, res_type="kaiser_fast").astype(np.float32, copy=False)

            # 3) 최소 전처리: DC 제거 + 과피크만 누름
            if x.size:
                x = x - float(np.mean(x))
                peak = float(np.max(np.abs(x)))
                if peak > 1.0:
                    x = x / peak
                x = np.nan_to_num(x, nan=0.0, posinf=0.0, neginf=0.0).astype(np.float32, copy=False)

            # --- Bucketing: 패딩 길이 결정 ---
            n_in = int(x.shape[0])
            nb = self._bucket_len(n_in)  # bucketing=False면 n_in 그대로

            # (선택) 아직 안 워밍된 버킷이면 워밍 1회
            if self.bucketing and self.bucket_samples > 0 and nb not in self._warmed_buckets:
                # 주의: 여기서 워밍하면 '해당 버킷의 첫 호출'이 1회 더 수행되므로
                # 서버 startup에서 미리 warm_bucket(...)을 호출해 두는 걸 권장
                self.warm_bucket(nb)

            # 패딩 적용
            if nb != n_in:
                x = np.pad(x, (0, nb - n_in), mode="constant")

            # 출력 sr 미리 계산(크롭 길이 계산에 사용)
            out_sr = self.resample_sr if (self.resample_sr and self.resample_sr >= 16000) else self.tgt_sr
            exp_len = int(round(n_in * (out_sr / 16000.0)))  # 기대 출력 길이

            # 4) 추론 (dtype 일관)
            use_amp = (self.device.type == "cuda" and self.is_half)
            amp_ctx = (torch.autocast(device_type="cuda", dtype=torch.float16, enabled=use_amp)
                    if self.device.type == "cuda" else nullcontext())

            with torch.inference_mode():
                with amp_ctx:
                    audio_opt = self.pipeline.pipeline(
                        self.hubert, self.net_g, 0, x, None, [0, 0, 0],
                        int(self.f0_up_key), self.f0_method,
                        (self.index_path or ""), float(self.index_rate), int(self.if_f0),
                        int(self.filter_radius), int(self.tgt_sr), int(self.resample_sr),
                        float(self.rms_mix_rate), str(self.version), float(self.protect),
                        f0_file=None,
                    )

            # 5) 출력 정리 + 크롭(원래 길이에 맞춤)
            y = np.asarray(audio_opt, dtype=np.float32)
            if y.size:
                # 기대 길이로 크롭/패드 (드물게 샘플 1~2개 차이가 날 수 있음)
                if y.size > exp_len:
                    y = y[:exp_len]
                elif y.size < exp_len:
                    y = np.pad(y, (0, exp_len - y.size), mode="constant")

                peak = float(np.max(np.abs(y)))
                if peak > 0.99:
                    y = (0.99 / peak) * y
                y = np.nan_to_num(y, nan=0.0, posinf=0.0, neginf=0.0).astype(np.float32, copy=False)

        finally:
            # 작업 디렉토리 복원
            os.chdir(original_cwd)

        return y, out_sr