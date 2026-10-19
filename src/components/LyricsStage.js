import { formatTimecode } from "../services/AudioEngine.js";

/**
 * LyricsStage Component
 * 核心北欧风格滚动歌词舞台：
 * - 惯性丝滑居中滚动 (Custom rAF easeOutQuart interpolator，零生硬瞬移)
 * - 边缘羽化 (CSS mask-image 顶底线性渐隐)
 * - Active 行微量放大 (scale 1.03) + 透明度 0.35 -> 1.0 平滑过渡
 * - 逐字/音节柔和透明度擦除与字重微变 (Soft Syllable Wipe)
 * - 点击任意一行歌词立即丝滑跳转到对应时刻
 */
export class LyricsStage {
  constructor(mountEl, { onSeekToTime, onActiveLineChange }) {
    this.mountEl = mountEl;
    this.onSeekToTime = onSeekToTime;
    this.onActiveLineChange = onActiveLineChange;

    this.lyrics = [];
    this.activeIndex = -1;
    this.syllableMode = true;

    // 惯性缓出滚动引擎状态
    this._scrollRaf = null;
    this._scrollStartPos = 0;
    this._scrollTargetPos = 0;
    this._scrollStartTime = 0;
    this._scrollDuration = 620; // ms，温柔从容的北欧缓出时长

    // 用户手动滚轮浏览防抖状态
    this._userScrolling = false;
    this._userScrollTimer = null;

    this._initDOM();
  }

  _initDOM() {
    this.mountEl.innerHTML = `
      <div class="lyrics-stage-wrapper">
        <div class="lyrics-scroll-viewport" id="lyrics-viewport">
          <div class="lyrics-spacer" aria-hidden="true"></div>
          <ul
            class="lyrics-list"
            id="lyrics-list"
            data-syllable-mode="true"
            role="region"
            aria-label="同步滚动歌词"
          ></ul>
          <div class="lyrics-spacer" aria-hidden="true"></div>
        </div>

        <button
          type="button"
          class="manual-scroll-pill"
          id="manual-scroll-pill"
          title="点击立即回到当前播放歌词居中位置"
        >
          <span style="width:6px;height:6px;border-radius:50%;background:var(--nordic-sage);"></span>
          <span>自由浏览中 · 点击回归居中跟随</span>
        </button>
      </div>
    `;

    this.viewportEl = this.mountEl.querySelector("#lyrics-viewport");
    this.listEl = this.mountEl.querySelector("#lyrics-list");
    this.manualPillEl = this.mountEl.querySelector("#manual-scroll-pill");

    // 监听用户手动滚轮或触控板滑动：暂停自动居中 2.6 秒，避免与用户抢夺滚动权
    this.viewportEl.addEventListener(
      "wheel",
      () => {
        this._pauseAutoCenterTemporarily();
      },
      { passive: true }
    );

    this.manualPillEl.addEventListener("click", () => {
      this._clearManualScrollState();
      if (this.activeIndex >= 0) {
        this.centerLineSmoothly(this.activeIndex, 520);
      }
    });

    window.addEventListener("resize", () => {
      if (this.activeIndex >= 0 && !this._userScrolling) {
        this.centerLineSmoothly(this.activeIndex, 320);
      }
    });
  }

  _pauseAutoCenterTemporarily() {
    if (this._scrollRaf) {
      cancelAnimationFrame(this._scrollRaf);
      this._scrollRaf = null;
    }
    this._userScrolling = true;
    this.manualPillEl.classList.add("is-visible");

    if (this._userScrollTimer) {
      clearTimeout(this._userScrollTimer);
    }
    this._userScrollTimer = setTimeout(() => {
      this._clearManualScrollState();
      if (this.activeIndex >= 0) {
        this.centerLineSmoothly(this.activeIndex, 680);
      }
    }, 2600);
  }

  _clearManualScrollState() {
    this._userScrolling = false;
    this.manualPillEl.classList.remove("is-visible");
    if (this._userScrollTimer) {
      clearTimeout(this._userScrollTimer);
      this._userScrollTimer = null;
    }
  }

  setSyllableMode(enabled) {
    this.syllableMode = Boolean(enabled);
    this.listEl.setAttribute("data-syllable-mode", String(this.syllableMode));
  }

  loadLyrics(lyrics = []) {
    this.lyrics = lyrics;
    this.activeIndex = -1;
    this._clearManualScrollState();

    this.listEl.innerHTML = lyrics
      .map((line, idx) => {
        const primaryContent = line.isInterlude
          ? `<div class="interlude-dots" aria-label="器乐间奏">
               <span class="interlude-dot" data-dot="0"></span>
               <span class="interlude-dot" data-dot="1"></span>
               <span class="interlude-dot" data-dot="2"></span>
             </div>`
          : line.syllables
              .map(
                (syl, sIdx) =>
                  `<span class="syllable-token" data-syl-idx="${sIdx}">${syl.text}</span>`
              )
              .join("");

        return `
          <li
            class="lyric-line-item"
            data-line-index="${idx}"
            data-time="${line.time}"
            tabindex="0"
            role="button"
            aria-label="跳转到 ${formatTimecode(line.time)}：${line.text}"
          >
            <div class="lyric-primary-text">${primaryContent}</div>
            ${line.sub ? `<div class="lyric-sub-translation">${line.sub}</div>` : ""}
            <span class="lyric-seek-badge">
              <svg width="9" height="9" viewBox="0 0 12 12" fill="currentColor">
                <path d="M3 2l7 4-7 4V2z"/>
              </svg>
              <span>${formatTimecode(line.time)}</span>
            </span>
          </li>
        `;
      })
      .join("");

    this.lineElements = Array.from(this.listEl.querySelectorAll(".lyric-line-item"));

    // 绑定点击或键盘回车跳转事件
    this.lineElements.forEach((el, idx) => {
      const triggerSeek = () => {
        const targetLine = this.lyrics[idx];
        if (!targetLine) return;
        this._clearManualScrollState();
        this.onSeekToTime?.(targetLine.time, idx);
        this.centerLineSmoothly(idx, 520);
      };

      el.addEventListener("click", triggerSeek);
      el.addEventListener("keydown", (e) => {
        if (e.key === "Enter" || e.key === " ") {
          e.preventDefault();
          triggerSeek();
        }
      });
    });

    // 初始定位到第一行居中
    requestAnimationFrame(() => {
      this.syncTime(0, "init");
      this.centerLineSmoothly(0, 0);
    });
  }

  /**
   * 根据当前播放时间同步高亮行与逐字/音节柔和擦除进度
   */
  syncTime(currentTime, source = "raf") {
    if (!this.lyrics.length) return;

    // 1. 查找当前时刻对应的歌词行索引
    let foundIndex = 0;
    for (let i = 0; i < this.lyrics.length; i++) {
      if (currentTime >= this.lyrics[i].time) {
        foundIndex = i;
      } else {
        break;
      }
    }

    // 2. 如果当前激活行发生变化，更新类名并触发惯性丝滑居中滚动
    if (foundIndex !== this.activeIndex) {
      this.activeIndex = foundIndex;

      this.lineElements.forEach((el, idx) => {
        const isCurrent = idx === foundIndex;
        const isNear = Math.abs(idx - foundIndex) === 1;
        el.classList.toggle("is-active", isCurrent);
        el.classList.toggle("is-near", isNear);

        if (!isCurrent) {
          // 重置非激活行的音节进度变量
          el.querySelectorAll(".syllable-token").forEach((token) => {
            token.style.setProperty("--syl-pct", "0%");
            token.classList.remove("is-singing", "is-sung");
          });
        }
      });

      const activeLine = this.lyrics[foundIndex];
      this.onActiveLineChange?.(activeLine);

      if (!this._userScrolling || source === "seek" || source === "init") {
        this.centerLineSmoothly(foundIndex, source === "init" ? 0 : 620);
      }
    }

    // 3. 更新当前激活行内部的逐音节柔光推进 / 间奏呼吸点
    const currentLine = this.lyrics[this.activeIndex];
    const currentEl = this.lineElements[this.activeIndex];
    if (!currentLine || !currentEl) return;

    if (currentLine.isInterlude) {
      const span = Math.max(1, currentLine.endTime - currentLine.time);
      const ratio = Math.max(0, Math.min(1, (currentTime - currentLine.time) / span));
      const dots = currentEl.querySelectorAll(".interlude-dot");
      dots.forEach((dot, dIdx) => {
        const threshold = (dIdx + 0.25) / dots.length;
        dot.classList.toggle("is-lit", ratio >= threshold);
      });
      return;
    }

    const tokenEls = currentEl.querySelectorAll(".syllable-token");
    currentLine.syllables.forEach((syl, sIdx) => {
      const tokenEl = tokenEls[sIdx];
      if (!tokenEl) return;

      if (currentTime <= syl.start) {
        tokenEl.style.setProperty("--syl-pct", "0%");
        tokenEl.classList.remove("is-singing", "is-sung");
      } else if (currentTime >= syl.end) {
        tokenEl.style.setProperty("--syl-pct", "115%");
        tokenEl.classList.remove("is-singing");
        tokenEl.classList.add("is-sung");
      } else {
        const pct = ((currentTime - syl.start) / Math.max(0.08, syl.end - syl.start)) * 100;
        tokenEl.style.setProperty("--syl-pct", `${pct.toFixed(1)}%`);
        tokenEl.classList.add("is-singing");
        tokenEl.classList.remove("is-sung");
      }
    });
  }

  /**
   * 惯性丝滑居中滚动 (Ease-Out Quart 缓动曲线)
   * 确保当前歌词始终温柔地停留在视口垂直中心，禁止任何生硬瞬移
   */
  centerLineSmoothly(lineIndex, duration = 620) {
    const targetEl = this.lineElements?.[lineIndex];
    if (!targetEl || !this.viewportEl) return;

    const viewportHeight = this.viewportEl.clientHeight;
    const lineCenter = targetEl.offsetTop + targetEl.offsetHeight / 2;
    const desiredScrollTop = Math.max(0, lineCenter - viewportHeight / 2);

    if (duration <= 0) {
      if (this._scrollRaf) cancelAnimationFrame(this._scrollRaf);
      this.viewportEl.scrollTop = desiredScrollTop;
      return;
    }

    if (this._scrollRaf) {
      cancelAnimationFrame(this._scrollRaf);
      this._scrollRaf = null;
    }

    this._scrollStartPos = this.viewportEl.scrollTop;
    this._scrollTargetPos = desiredScrollTop;
    this._scrollStartTime = performance.now();
    this._scrollDuration = duration;

    const delta = this._scrollTargetPos - this._scrollStartPos;
    if (Math.abs(delta) < 0.5) return;

    // Ease-Out Quart: 1 - (1 - t)^4，初段轻盈响应，末段如羽毛落定般柔和阻尼
    const easeOutNordic = (t) => 1 - Math.pow(1 - t, 4);

    const stepScroll = (now) => {
      const elapsed = now - this._scrollStartTime;
      const progress = Math.min(1, elapsed / this._scrollDuration);
      const eased = easeOutNordic(progress);

      this.viewportEl.scrollTop = this._scrollStartPos + delta * eased;

      if (progress < 1) {
        this._scrollRaf = requestAnimationFrame(stepScroll);
      } else {
        this._scrollRaf = null;
      }
    };

    this._scrollRaf = requestAnimationFrame(stepScroll);
  }
}
