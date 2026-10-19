import { formatTimecode } from "../services/AudioEngine.js";

/**
 * NavigationPane Component
 * WinUI 3 NavigationView 左侧导航栏与北欧精选专辑曲目切换
 */
export class NavigationPane {
  constructor(mountEl, { tracks, activeTrackId, onSelectTrack }) {
    this.mountEl = mountEl;
    this.tracks = tracks;
    this.activeTrackId = activeTrackId;
    this.onSelectTrack = onSelectTrack;
    this.render();
  }

  render() {
    this.mountEl.innerHTML = `
      <div>
        <div class="nav-section-title">Nordic Repertoire · 曲目集</div>
        <div class="nav-track-list" role="listbox" aria-label="北欧精选曲目">
          ${this.tracks
            .map((track, idx) => {
              const isSelected = track.id === this.activeTrackId;
              return `
                <button
                  type="button"
                  class="nav-track-item ${isSelected ? "is-selected" : ""}"
                  data-track-id="${track.id}"
                  role="option"
                  aria-selected="${isSelected}"
                >
                  <div
                    class="nav-track-swatch"
                    style="background: linear-gradient(135deg, ${track.palette.bgPrimary}, ${track.palette.bgSecondary});"
                  >
                    0${idx + 1}
                  </div>
                  <div class="nav-track-meta">
                    <div class="nav-track-title">${track.title}</div>
                    <div class="nav-track-artist">${track.artist}</div>
                  </div>
                  <span class="nav-track-duration">${formatTimecode(track.duration)}</span>
                </button>
              `;
            })
            .join("")}
        </div>
      </div>

      <div class="nav-acoustic-card">
        <div class="acoustic-header">
          <span>Scandinavian Motion</span>
          <span style="color: var(--nordic-sage); font-family: var(--font-mono);">1.03×</span>
        </div>
        <p class="acoustic-desc">
          点击右侧任意一行歌词即可丝滑跳转至对应时刻；支持滚轮自由浏览与自动居中回归。
        </p>
      </div>
    `;

    this.mountEl.querySelectorAll(".nav-track-item").forEach((btn) => {
      btn.addEventListener("click", () => {
        const id = btn.getAttribute("data-track-id");
        if (id && id !== this.activeTrackId) {
          this.setActiveTrack(id);
          this.onSelectTrack?.(id);
        }
      });
    });
  }

  setActiveTrack(trackId) {
    this.activeTrackId = trackId;
    this.mountEl.querySelectorAll(".nav-track-item").forEach((btn) => {
      const selected = btn.getAttribute("data-track-id") === trackId;
      btn.classList.toggle("is-selected", selected);
      btn.setAttribute("aria-selected", String(selected));
    });
  }
}
