import { formatTimecode } from "../services/AudioEngine.js";

/**
 * TransportBar Component
 * WinUI 3 底部毛玻璃磨砂 (Acrylic) 播放控制栏组件
 */
export class TransportBar {
  constructor(
    mountEl,
    { onTogglePlay, onPrevTrack, onNextTrack, onSeekRatio, onVolumeChange, onToggleMute }
  ) {
    this.mountEl = mountEl;
    this.onTogglePlay = onTogglePlay;
    this.onPrevTrack = onPrevTrack;
    this.onNextTrack = onNextTrack;
    this.onSeekRatio = onSeekRatio;
    this.onVolumeChange = onVolumeChange;
    this.onToggleMute = onToggleMute;

    this.isDraggingTimeline = false;
    this.render();
  }

  render() {
    this.mountEl.innerHTML = `
      <!-- 左侧：声波律动与当前歌词实时摘要 -->
      <div class="transport-now-playing">
        <div class="mini-soundwave" id="mini-soundwave" aria-hidden="true">
          <span class="wave-bar"></span>
          <span class="wave-bar"></span>
          <span class="wave-bar"></span>
          <span class="wave-bar"></span>
        </div>
        <div class="transport-status-text">
          <div class="transport-status-label">SYNCED LYRIC STAGE</div>
          <div class="transport-current-lyric-snippet" id="transport-lyric-snippet">
            点击播放或任意歌词体验丝滑居中滚动
          </div>
        </div>
      </div>

      <!-- 中央：WinUI 3 播放控制按钮组 + 进度条 -->
      <div class="transport-center-stack">
        <div class="transport-buttons">
          <button
            type="button"
            class="winui-icon-btn"
            id="btn-prev"
            title="上一首曲目 (Previous)"
            aria-label="上一首曲目"
          >
            <svg width="15" height="15" viewBox="0 0 24 24" fill="currentColor">
              <path d="M6 6h2v12H6V6zm3.5 6l8.5 6V6l-8.5 6z"/>
            </svg>
          </button>

          <button
            type="button"
            class="winui-play-fab"
            id="btn-play-pause"
            title="播放 / 暂停 (Space)"
            aria-label="播放或暂停"
          >
            <svg id="icon-play" width="16" height="16" viewBox="0 0 24 24" fill="currentColor">
              <path d="M8 5.14v14l11-7-11-7z"/>
            </svg>
            <svg id="icon-pause" width="16" height="16" viewBox="0 0 24 24" fill="currentColor" style="display:none;">
              <path d="M6 19h4V5H6v14zm8-14v14h4V5h-4z"/>
            </svg>
          </button>

          <button
            type="button"
            class="winui-icon-btn"
            id="btn-next"
            title="下一首曲目 (Next)"
            aria-label="下一首曲目"
          >
            <svg width="15" height="15" viewBox="0 0 24 24" fill="currentColor">
              <path d="M6 18l8.5-6L6 6v12zM16 6v12h2V6h-2z"/>
            </svg>
          </button>
        </div>

        <div class="timeline-row">
          <span class="time-readout" id="time-current">00:00</span>
          <div class="winui-slider-container" id="timeline-slider" role="slider" aria-label="播放进度">
            <div class="winui-slider-track">
              <div class="winui-slider-fill" id="timeline-fill"></div>
            </div>
            <div class="winui-slider-thumb" id="timeline-thumb"></div>
          </div>
          <span class="time-readout" id="time-duration">01:16</span>
        </div>
      </div>

      <!-- 右侧：北欧毛毡钢琴音量控制 -->
      <div class="transport-right-controls">
        <div class="volume-group">
          <button
            type="button"
            class="winui-icon-btn"
            id="btn-mute"
            title="静音切换"
            aria-label="静音切换"
          >
            <svg id="icon-vol-on" width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8">
              <polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5"></polygon>
              <path d="M15.54 8.46a5 5 0 0 1 0 7.07"></path>
              <path d="M19.07 4.93a10 10 0 0 1 0 14.14"></path>
            </svg>
          </button>

          <div class="winui-slider-container" id="volume-slider" role="slider" aria-label="音量调节">
            <div class="winui-slider-track">
              <div class="winui-slider-fill" id="volume-fill" style="width: 72%;"></div>
            </div>
            <div class="winui-slider-thumb" id="volume-thumb" style="left: 72%;"></div>
          </div>
        </div>
      </div>
    `;

    this._bindEvents();
  }

  _bindEvents() {
    this.mountEl.querySelector("#btn-play-pause").addEventListener("click", () => {
      this.onTogglePlay?.();
    });
    this.mountEl.querySelector("#btn-prev").addEventListener("click", () => {
      this.onPrevTrack?.();
    });
    this.mountEl.querySelector("#btn-next").addEventListener("click", () => {
      this.onNextTrack?.();
    });
    this.mountEl.querySelector("#btn-mute").addEventListener("click", () => {
      this.onToggleMute?.();
    });

    // 进度条拖拽与点击寻道
    const timelineEl = this.mountEl.querySelector("#timeline-slider");
    const calcRatio = (clientX, rect) =>
      Math.max(0, Math.min(1, (clientX - rect.left) / Math.max(1, rect.width)));

    timelineEl.addEventListener("pointerdown", (e) => {
      this.isDraggingTimeline = true;
      timelineEl.setPointerCapture(e.pointerId);
      const ratio = calcRatio(e.clientX, timelineEl.getBoundingClientRect());
      this.onSeekRatio?.(ratio);
    });

    timelineEl.addEventListener("pointermove", (e) => {
      if (!this.isDraggingTimeline) return;
      const ratio = calcRatio(e.clientX, timelineEl.getBoundingClientRect());
      this.onSeekRatio?.(ratio);
    });

    timelineEl.addEventListener("pointerup", () => {
      this.isDraggingTimeline = false;
    });

    // 音量滑块
    const volSliderEl = this.mountEl.querySelector("#volume-slider");
    let draggingVol = false;

    const handleVolPointer = (e) => {
      const ratio = calcRatio(e.clientX, volSliderEl.getBoundingClientRect());
      this.onVolumeChange?.(ratio);
    };

    volSliderEl.addEventListener("pointerdown", (e) => {
      draggingVol = true;
      volSliderEl.setPointerCapture(e.pointerId);
      handleVolPointer(e);
    });
    volSliderEl.addEventListener("pointermove", (e) => {
      if (draggingVol) handleVolPointer(e);
    });
    volSliderEl.addEventListener("pointerup", () => {
      draggingVol = false;
    });
  }

  setPlayingState(isPlaying) {
    const playIcon = this.mountEl.querySelector("#icon-play");
    const pauseIcon = this.mountEl.querySelector("#icon-pause");
    const wave = this.mountEl.querySelector("#mini-soundwave");

    playIcon.style.display = isPlaying ? "none" : "block";
    pauseIcon.style.display = isPlaying ? "block" : "none";
    wave.classList.toggle("is-playing", isPlaying);
  }

  updateProgress(currentTime, duration) {
    const safeDur = Math.max(1, duration || 1);
    const pct = Math.max(0, Math.min(100, (currentTime / safeDur) * 100));

    this.mountEl.querySelector("#time-current").textContent = formatTimecode(currentTime);
    this.mountEl.querySelector("#time-duration").textContent = formatTimecode(safeDur);
    this.mountEl.querySelector("#timeline-fill").style.width = `${pct.toFixed(2)}%`;
    this.mountEl.querySelector("#timeline-thumb").style.left = `${pct.toFixed(2)}%`;
  }

  updateVolume(volume, isMuted) {
    const pct = isMuted ? 0 : Math.round(volume * 100);
    this.mountEl.querySelector("#volume-fill").style.width = `${pct}%`;
    this.mountEl.querySelector("#volume-thumb").style.left = `${pct}%`;
  }

  updateLyricSnippet(line) {
    const snippetEl = this.mountEl.querySelector("#transport-lyric-snippet");
    if (!snippetEl || !line) return;
    snippetEl.textContent = line.isInterlude ? line.sub || "Instrumental Interlude" : line.text;
  }
}
