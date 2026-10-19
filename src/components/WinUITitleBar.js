/**
 * WinUITitleBar Component
 * 实现 Windows 11 / WinUI 3 极简自定义标题栏与动效模式快捷切换
 */
export class WinUITitleBar {
  constructor(mountEl, { onToggleSyllableMode, onToggleTheme }) {
    this.mountEl = mountEl;
    this.onToggleSyllableMode = onToggleSyllableMode;
    this.onToggleTheme = onToggleTheme;
    this.syllableMode = true;
    this.theme = "daylight";
    this.render();
  }

  render() {
    this.mountEl.innerHTML = `
      <div class="titlebar-brand">
        <span class="brand-dot" aria-hidden="true"></span>
        <span>KLANG · NORDIC AUDIO ATELIER</span>
        <span class="titlebar-tag">WinUI 3 Mica · Scandinavian Edition</span>
      </div>

      <div class="titlebar-actions">
        <button
          type="button"
          class="winui-chip-btn is-active"
          id="btn-syllable-toggle"
          title="切换逐字/音节柔光擦除与整行极简高亮"
        >
          <span class="status-indicator"></span>
          <span id="label-syllable-mode">逐音节柔光推进</span>
        </button>

        <button
          type="button"
          class="winui-chip-btn"
          id="btn-theme-toggle"
          title="在晨雾白与峡湾炭灰之间切换北欧自然色调"
        >
          <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8">
            <circle cx="12" cy="12" r="4"></circle>
            <path d="M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M6.34 17.66l-1.41 1.41M19.07 4.93l-1.41 1.41"></path>
          </svg>
          <span id="label-theme-mode">晨雾白 · Mist Light</span>
        </button>

        <div class="window-caption-controls" aria-hidden="true">
          <button type="button" class="caption-btn" tabindex="-1" title="Minimize">
            <svg width="10" height="10" viewBox="0 0 10 10"><path d="M1 5h8" stroke="currentColor" stroke-width="1"/></svg>
          </button>
          <button type="button" class="caption-btn" tabindex="-1" title="Maximize">
            <svg width="10" height="10" viewBox="0 0 10 10"><rect x="1.5" y="1.5" width="7" height="7" rx="1" fill="none" stroke="currentColor" stroke-width="1"/></svg>
          </button>
          <button type="button" class="caption-btn" tabindex="-1" title="Close">
            <svg width="10" height="10" viewBox="0 0 10 10"><path d="M2 2l6 6M8 2L2 8" stroke="currentColor" stroke-width="1"/></svg>
          </button>
        </div>
      </div>
    `;

    const sylBtn = this.mountEl.querySelector("#btn-syllable-toggle");
    const sylLabel = this.mountEl.querySelector("#label-syllable-mode");
    sylBtn.addEventListener("click", () => {
      this.syllableMode = !this.syllableMode;
      sylBtn.classList.toggle("is-active", this.syllableMode);
      sylLabel.textContent = this.syllableMode ? "逐音节柔光推进" : "整行极简高亮";
      this.onToggleSyllableMode?.(this.syllableMode);
    });

    const themeBtn = this.mountEl.querySelector("#btn-theme-toggle");
    const themeLabel = this.mountEl.querySelector("#label-theme-mode");
    themeBtn.addEventListener("click", () => {
      this.theme = this.theme === "daylight" ? "dusk" : "daylight";
      themeLabel.textContent =
        this.theme === "daylight" ? "晨雾白 · Mist Light" : "峡湾夜 · Slate Dusk";
      this.onToggleTheme?.(this.theme);
    });
  }
}
